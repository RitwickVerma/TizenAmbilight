using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TizenAmbilight
{
    /// <summary>
    /// HARDWARE PIXEL SAMPLER, used when the video plane is withheld (DRM, rc=-4).
    ///
    /// libvideoenhance.so exports a small RGB-measure API ("ppi_ve_*"). It is the interface Samsung's own
    /// Tizen.TV.System.ContentAnalysis wraps for the Philips Hue Sync app, and it is documented nowhere
    /// else. It measures the mean colour of a small block INSIDE the TV's video processor; it never hands
    /// out a frame, so this backend reads a few spot colours, not pixels:
    ///
    ///     int ppi_ve_get_rgb_measure_condition(Condition* out)
    ///     int ppi_ve_set_rgb_measure_position(int slot, int x, int y)
    ///     int ppi_ve_get_rgb_measure_pixel(int slot, Color* out)    // mean R,G,B of a block, 10-bit
    ///
    /// Struct layouts follow the HyperTizen project's working Capturer.cs:
    ///   Condition = 6 x int32 { ScreenCapturePoints (the slot count), PixelDensityX, PixelDensityY (block
    ///               w/h), SleepMS (set->get settle time), Width, Height }
    ///   Color     = int32 R, G, B (0..1023), + a 4th int32 (lowInputLagOnOff in VEPPIRgbMeasure_t)
    /// HyperTizen pads both to 64 bytes because some firmwares write past the documented size through the
    /// out pointer. Here both are 256-byte zeroed native buffers, which gives the same protection and lets
    /// the 4th int be read for telemetry.
    ///
    /// SLOT SAFETY: the library reports how many slots exist (BlockCount, 2 on the reference TV). Every
    /// native call goes through SetPos/GetPix, which refuse any slot index at or above that reported
    /// count. Nothing here ever addresses a slot the TV did not announce.
    ///
    /// ARCHITECTURE (rolling / partial, as Samsung's own HwCaptureControl does): one sampler thread owns
    /// every native call. It cycles continuously over `pz` measured positions spread evenly round the
    /// perimeter, `Slots` at a time: arm the slots, wait the settle time, read them back. The render
    /// loop never waits for it: it takes a snapshot of the latest values, interpolates the pz measured
    /// zones onto all zones round the ring, and runs the normal EMA / tone map / WLED path at targetFps.
    /// That render rate is interpolation, NOT measurement: each measured zone is refreshed far more
    /// slowly (see docs/how-it-works.md, "DRM fallback"). One-shot tasks (bar probe, thumbnail grid,
    /// freshness test) run on the same thread between batches.
    ///
    /// THREADING: the sampler thread runs Step() while holding _gate. Park() clears _wantRun and then
    /// takes _gate, so once it returns true no native call is in flight and none can start. Data shared
    /// with the render loop (measured values, active picture rectangle, geometry, diagnostics) is guarded
    /// by _lock; plain counters use Interlocked because a 64-bit read is not atomic on 32-bit ARM.
    ///
    /// dlopen is RTLD_LAZY, like every other private library in this app.
    /// </summary>
    public static class PpiCapture
    {
        public const string BackendName = "ppi";
        const string LibPath = "/usr/lib/libvideoenhance.so";
        const string SymCond = "ppi_ve_get_rgb_measure_condition";
        const string SymPos = "ppi_ve_set_rgb_measure_position";
        const string SymPix = "ppi_ve_get_rgb_measure_pixel";

        const int RTLD_LAZY = 0x00001;
        const int BufBytes = 256;
        const int MaxSlots = 16;             // sanity bound on the slot count the library reports
        const int MinSettleMs = 1, MaxSettleMs = 100;   // clamp on the settle time the library reports

        [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        static extern IntPtr dlopen(string filename, int flags);
        [DllImport("libdl.so.2", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
        static extern IntPtr dlsym(IntPtr handle, string symbol);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CondFn(IntPtr cond);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PosFn(int slot, int x, int y);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PixFn(int slot, IntPtr color);

        static IntPtr _lib = IntPtr.Zero;
        static CondFn _cond;
        static PosFn _pos;
        static PixFn _pix;
        static IntPtr _condBuf = IntPtr.Zero;
        static IntPtr _colBuf = IntPtr.Zero;
        static readonly byte[] _zero = new byte[BufBytes];

        // ---- condition as reported by the library (written once in Init, before the sampler starts) ----
        public static int Slots, BlockW, BlockH, SleepMs, SleepMsReported, FullW, FullH;   // SleepMs = reported, clamped
        public static int CondRc = int.MinValue, CondExtra6, CondExtra7;
        public static volatile string InitError;          // null when ready
        static volatile bool _geomReady;
        public static bool Ready { get { return _cond != null && _geomReady; } }

        // ---- zone geometry in the panel's measure space (replaced as a set, under _lock) ----
        static int _zones;
        static int[] _px, _py;                   // block top-left per zone (full-ring layout)
        static double[] _fx, _fy;                // zone centre as a fraction of the frame
        static ZoneLayout _geomLayout;

        // ---- settle time between arming a slot and reading it. Default -1 = the TV-reported SleepMS (clamped),
        // which is what Samsung's own client waits. 0..200 ms overrides it (config ppiSettleMs, token psleep).
        // On the reference TV (reports 20 ms) reads were measured fresh down to ~15-17 ms (one 60 Hz frame) and
        // stale below that, so a lower value buys rate only where YOU have checked freshness (token pfresh). ----
        public static volatile int SleepOverrideMs = -1;
        public static int EffectiveSleepMs { get { int o = SleepOverrideMs; return o >= 0 ? o : SleepMs; } }

        // ---- counters (telemetry). Written by the sampler thread with Interlocked, read with Interlocked.Read ----
        public static long Samples, Cycles, PosFails, PixFails, PixInvalid, ThreadErrors;
        public static volatile int LastPosRc, LastPixRc;
        public static volatile string LastError = "";
        static long _winSamples0, _winCycles0;   // render thread only (Describe)
        static DateTime _winT0 = DateTime.UtcNow;
        public static double Sleep1Ms = -1;      // measured cost of Thread.Sleep(1) on this runtime (set in Init)
        static volatile int _lagMin = int.MaxValue, _lagMax = int.MinValue;

        static string Fail(string e) { InitError = e; return e; }

        public static string Init(int zoneCount, double bandFrac)
        {
            if (_cond != null && _geomReady && _zones == zoneCount) return null;
            try
            {
                if (_cond == null)
                {
                    // A TV without the library (or with a different export set) ends up here: report why and
                    // stay on the video plane. Nothing below makes a ppi_ve call until every symbol resolved.
                    if (_lib == IntPtr.Zero) _lib = dlopen(LibPath, RTLD_LAZY);
                    if (_lib == IntPtr.Zero) _lib = dlopen("libvideoenhance.so", RTLD_LAZY);
                    if (_lib == IntPtr.Zero) return Fail("dlopen libvideoenhance.so failed (no hardware measure API on this TV)");
                    IntPtr fc = dlsym(_lib, SymCond), fp = dlsym(_lib, SymPos), fx = dlsym(_lib, SymPix);
                    if (fc == IntPtr.Zero || fp == IntPtr.Zero || fx == IntPtr.Zero)
                        return Fail("dlsym failed: cond=" + (fc != IntPtr.Zero) + " pos=" + (fp != IntPtr.Zero) +
                                    " pix=" + (fx != IntPtr.Zero));
                    if (_condBuf == IntPtr.Zero) _condBuf = Marshal.AllocHGlobal(BufBytes);   // once: a retry must not leak
                    if (_colBuf == IntPtr.Zero) _colBuf = Marshal.AllocHGlobal(BufBytes);
                    Marshal.Copy(_zero, 0, _condBuf, BufBytes);
                    Marshal.Copy(_zero, 0, _colBuf, BufBytes);
                    var cond = (CondFn)Marshal.GetDelegateForFunctionPointer(fc, typeof(CondFn));
                    var pos = (PosFn)Marshal.GetDelegateForFunctionPointer(fp, typeof(PosFn));
                    var pix = (PixFn)Marshal.GetDelegateForFunctionPointer(fx, typeof(PixFn));

                    CondRc = cond(_condBuf);
                    Slots = Marshal.ReadInt32(_condBuf, 0);
                    BlockW = Marshal.ReadInt32(_condBuf, 4);
                    BlockH = Marshal.ReadInt32(_condBuf, 8);
                    SleepMsReported = Marshal.ReadInt32(_condBuf, 12);
                    FullW = Marshal.ReadInt32(_condBuf, 16);
                    FullH = Marshal.ReadInt32(_condBuf, 20);
                    CondExtra6 = Marshal.ReadInt32(_condBuf, 24);
                    CondExtra7 = Marshal.ReadInt32(_condBuf, 28);
                    if (CondRc < 0) return Fail("ppi_ve_get_rgb_measure_condition rc=" + CondRc);
                    if (Slots <= 0 || FullW <= 0 || FullH <= 0 || BlockW <= 0 || BlockH <= 0)
                        return Fail("implausible condition: slots=" + Slots + " full=" + FullW + "x" + FullH +
                                    " block=" + BlockW + "x" + BlockH);
                    // Everything the sampler does is derived from what the TV reported, clamped to a sane range:
                    // the slot count (never addressed beyond it) and the settle time (never below 1 ms).
                    if (Slots > MaxSlots) Slots = MaxSlots;
                    SleepMs = Math.Max(MinSettleMs, Math.Min(MaxSettleMs, SleepMsReported));
                    _cond = cond; _pos = pos; _pix = pix;

                    // How coarse is Thread.Sleep here? HyperTizen measured ~20 ms; it decides how Wait() may
                    // idle without burning a core.
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 10; i++) Thread.Sleep(1);
                    Sleep1Ms = sw.Elapsed.TotalMilliseconds / 10.0;
                }

                // Zone centres come from the SAME ZoneLayout class the video plane uses (clockwise from the
                // top-left), at the plane's aspect, scaled into the panel's measure space - so zone i here is
                // zone i downstream, and the LED mapping needs no special case.
                var layout = new ZoneLayout(VideoPlaneCapture.PlaneW, VideoPlaneCapture.PlaneH, zoneCount, bandFrac);
                int n = layout.Zones.Length;
                var px = new int[n]; var py = new int[n];
                var fx2 = new double[n]; var fy2 = new double[n];
                for (int i = 0; i < n; i++)
                {
                    Zone z = layout.Zones[i];
                    fx2[i] = ((z.X0 + z.X1) * 0.5) / VideoPlaneCapture.PlaneW;
                    fy2[i] = ((z.Y0 + z.Y1) * 0.5) / VideoPlaneCapture.PlaneH;
                }
                lock (_lock)
                {
                    _geomLayout = layout; _zones = n; _px = px; _py = py; _fx = fx2; _fy = fy2;
                    Place(); _rebuild = true;
                }
                _geomReady = true;
                InitError = null;
                return null;
            }
            catch (Exception e)
            {
                return Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        public static int ZoneTotal { get { return _zones; } }

        // ================================================================ native calls (slot-guarded)

        /// <summary>The ONLY way this class arms a slot. A slot the library did not report is refused.</summary>
        static int SetPos(int slot, int x, int y)
        {
            if ((uint)slot >= (uint)Slots) return -1;
            int rc = _pos(slot, x, y);
            LastPosRc = rc;
            if (rc < 0) Interlocked.Increment(ref PosFails);
            return rc;
        }

        /// <summary>The ONLY way this class reads a slot. Returns 0 = ok, 1 = call failed or slot refused,
        /// 2 = the library returned an out-of-range value.</summary>
        static int ReadPix(int slot, out int r, out int g, out int b, out int lag)
        {
            r = g = b = lag = 0;
            if ((uint)slot >= (uint)Slots) return 1;
            Marshal.Copy(_zero, 0, _colBuf, BufBytes);
            int rc = _pix(slot, _colBuf);
            LastPixRc = rc;
            if (rc < 0) { Interlocked.Increment(ref PixFails); return 1; }
            r = Marshal.ReadInt32(_colBuf, 0); g = Marshal.ReadInt32(_colBuf, 4); b = Marshal.ReadInt32(_colBuf, 8);
            lag = Marshal.ReadInt32(_colBuf, 12);
            if (r < 0 || g < 0 || b < 0 || r > 1023 || g > 1023 || b > 1023) { Interlocked.Increment(ref PixInvalid); return 2; }
            return 0;
        }

        // ================================================================ timing
        /// <summary>Exact short wait on a stopwatch. A bare Thread.Sleep(d) is only as fine as the runtime's
        /// timer (HyperTizen saw ~20 ms), which would put a floor under every delay under test. Idle with
        /// Sleep(1) while more than ~2.5 ms remain IF this runtime's Sleep(1) really is ~1 ms, otherwise
        /// yield with Sleep(0).</summary>
        static void Wait(int ms)
        {
            if (ms <= 0) return;
            long t0 = Stopwatch.GetTimestamp();
            long target = (long)ms * Stopwatch.Frequency / 1000;
            long coarse = (long)(2.5 * Stopwatch.Frequency / 1000);
            bool fineSleep = Sleep1Ms > 0 && Sleep1Ms < 2.0;
            while (true)
            {
                long rem = target - (Stopwatch.GetTimestamp() - t0);
                if (rem <= 0) break;
                if (fineSleep && rem > coarse) Thread.Sleep(1); else Thread.Sleep(0);
            }
        }

        // ================================================================ the sampler thread
        static readonly object _lock = new object();   // shared data (values, geometry, rectangle, diagnostics)
        static readonly object _gate = new object();   // held by the sampler thread for the duration of a Step
        static volatile Thread _thr;
        static volatile bool _wantRun, _exit;
        public static bool Running { get { return _wantRun; } }

        // pz: number of positions measured round the perimeter (control token pz)
        static volatile int _pzReq = 24;
        public static int PzRequested { get { return _pzReq; } set { _pzReq = Math.Max(4, value); } }
        static volatile int _pz;
        static volatile bool _rebuild = true;
        // sampler-owned except where noted; _mz/_val/_have/_pz are replaced/updated under _lock
        static int[] _mz;                         // zone index of each measured position
        static int[] _mpx, _mpy;
        static int[] _val;                        // pz*3 raw 10-bit
        static bool[] _have;
        static int _cur;
        // render-thread scratch for Snapshot (reused, grown only if pz grows)
        static int[] _snapZ, _snapV;

        /// <summary>Start (or resume) the sampler thread. Idempotent; a no-op after Shutdown.</summary>
        public static void Start()
        {
            if (!Ready || _exit) return;
            lock (_lock)
            {
                if (_thr == null)
                {
                    var t = new Thread(Run) { IsBackground = true, Name = "ppi-sampler" };
                    _thr = t;
                    t.Start();
                }
            }
            _wantRun = true;
        }

        /// <summary>Stop making native calls and WAIT until the sampler is outside Step (or 2 s). Safe to
        /// call when never started. The deploy rule depends on this: after 'paused' is reported no ppi
        /// call may be in flight, and none can start (the thread re-checks _wantRun inside _gate).</summary>
        public static bool Park()
        {
            _wantRun = false;
            if (_thr == null) return true;
            if (!Monitor.TryEnter(_gate, 2000)) return false;
            Monitor.Exit(_gate);
            return true;
        }

        public static void Shutdown() { _exit = true; Park(); }

        static void Run()
        {
            while (!_exit)
            {
                bool ran = false, failed = false;
                lock (_gate)
                {
                    if (_wantRun && !_exit)
                    {
                        ran = true;
                        try { Step(); }
                        catch (Exception e)
                        {
                            Interlocked.Increment(ref ThreadErrors);
                            LastError = e.GetType().Name + ": " + e.Message;
                            failed = true;
                        }
                    }
                }
                if (failed) Thread.Sleep(50);        // a persistent fault must not spin
                else if (!ran) Thread.Sleep(10);
            }
        }

        static bool Abort() { return !_wantRun || _exit; }

        static int EffectivePz() { return Math.Max(4, Math.Min(_zones, _pzReq)); }

        static void RebuildMeasured()
        {
            int pz = EffectivePz();
            lock (_lock)
            {
                bool resize = (_mz == null || _pz != pz);
                if (resize)
                {
                    _mz = new int[pz]; _mpx = new int[pz]; _mpy = new int[pz];
                    _val = new int[pz * 3]; _have = new bool[pz];
                    _cur = 0;
                }
                _pz = pz;
                for (int k = 0; k < pz; k++)
                {
                    int z = (int)Math.Round(k * (double)_zones / pz);
                    if (z >= _zones) z = _zones - 1;
                    _mz[k] = z; _mpx[k] = _px[z]; _mpy[k] = _py[z];
                }
                _rebuild = false;
            }
        }

        static void Step()
        {
            if (_rebuild || _pz != EffectivePz()) RebuildMeasured();

            // one-shot tasks, between batches
            if (Interlocked.Exchange(ref GridRequest, 0) == 1) { GridSweep(32, 18); return; }
            int fr = Interlocked.Exchange(ref FreshRequest, 0);
            if (fr > 0) { FreshnessTest(fr); return; }
            if (ProbeDue()) { ProbeBars(); return; }

            int pz = _pz;
            int armed = Math.Min(Slots, pz);
            int i0 = _cur;
            for (int j = 0; j < armed; j++)
            {
                int m = (i0 + j) % pz;
                SetPos(j, _mpx[m], _mpy[m]);
            }
            Wait(EffectiveSleepMs);
            for (int k = 0; k < armed; k++)
            {
                int rr, gg, bb, lag;
                if (ReadPix(k, out rr, out gg, out bb, out lag) != 0) continue;
                if (lag < _lagMin) _lagMin = lag;
                if (lag > _lagMax) _lagMax = lag;
                lock (_lock)
                {
                    int m = (i0 + k) % pz;
                    _val[m * 3] = rr; _val[m * 3 + 1] = gg; _val[m * 3 + 2] = bb;
                    _have[m] = true;
                    Samples++;       // under _lock: Describe reads it under _lock too
                }
            }
            Interlocked.Increment(ref Cycles);
            _cur = (i0 + armed) % pz;
        }

        /// <summary>Latest measurements interpolated linearly round the ring onto every zone (8-bit =
        /// 10-bit >> 2). Returns the number of measured positions that have data, 0 if none yet. Render
        /// thread only (it reuses scratch buffers, so a steady-state call allocates nothing).</summary>
        public static int Snapshot(byte[] r8, byte[] g8, byte[] b8)
        {
            int n = 0;
            int zc;
            lock (_lock)
            {
                if (_mz == null) return 0;
                int pz = _pz;
                if (_snapZ == null || _snapZ.Length < pz) { _snapZ = new int[pz]; _snapV = new int[pz * 3]; }
                for (int k = 0; k < pz; k++)
                {
                    if (!_have[k]) continue;
                    _snapZ[n] = _mz[k];
                    _snapV[n * 3] = _val[k * 3]; _snapV[n * 3 + 1] = _val[k * 3 + 1]; _snapV[n * 3 + 2] = _val[k * 3 + 2];
                    n++;
                }
                zc = Math.Min(_zones, r8.Length);
            }
            if (n == 0) return 0;
            int[] zs = _snapZ, vs = _snapV;
            for (int m = 0; m < n; m++)
            {
                bool last = (m + 1 == n);
                int a = zs[m];
                int b = last ? zs[0] + zc : zs[m + 1];                   // wrap to the first measured zone
                int bm = last ? 0 : m + 1;
                int span = b - a;
                if (span <= 0) span = zc;                               // single measured position: flat ring
                int end = (n == 1) ? a + zc : b;
                for (int z = a; z < end; z++)
                {
                    double t = (double)(z - a) / span;
                    int zi = z % zc;
                    r8[zi] = To8(vs[m * 3] + (vs[bm * 3] - vs[m * 3]) * t);
                    g8[zi] = To8(vs[m * 3 + 1] + (vs[bm * 3 + 1] - vs[m * 3 + 1]) * t);
                    b8[zi] = To8(vs[m * 3 + 2] + (vs[bm * 3 + 2] - vs[m * 3 + 2]) * t);
                }
            }
            return n;
        }

        static byte To8(double v10) { int v = (int)((v10 + 0.5) / 4.0); return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }

        // ================================================================ active picture area
        //
        // MEASURED on a DRM stream (32x18 grid): unlike the video plane - which the library scales to FILL
        // the request, so a 2.39:1 film arrives stretched with no bars - the measure block sees the
        // COMPOSED picture, black bars included (2 of 18 grid rows solid 0,0,0 top and bottom). Sampling
        // the zone bands blindly would put every top/bottom zone in a bar. So the sampler finds the active
        // rectangle and maps the same zone layout affinely into it, which reproduces what the plane gives
        // for free (edges map to picture edges).
        //
        // Detection is deliberately conservative: a bar is EXACT digital black (10-bit max channel <= 6),
        // a bar must span >= 2 probe cells (6%), a change needs 2 consecutive agreeing probes (the larger bar
        // of the two wins, because subtitles inside a bar only make it look smaller), and a probe whose
        // centre is also black (fade / black frame) is discarded as uninformative.
        public static volatile bool BarsEnabled = true;        // control token ppibars
        // Active rectangle, probe stats and notes: written by the sampler under _lock, read by Describe under _lock.
        public static double ActL, ActT, ActR = 1.0, ActB = 1.0;
        public static long Probes, ProbesDiscarded, Adoptions;
        public static double LastProbeMs;
        public static string ProbeNote = "none yet";
        static DateTime _lastProbeUtc = DateTime.MinValue;
        static double _cL, _cT, _cR = 1.0, _cB = 1.0;
        static int _candCount;
        const int ProbeN = 8;
        const double ProbeDepth = 0.25;
        const int BlackMax10 = 6;
        const int ProbeIntervalS = 12;

        /// <summary>Block top-left per zone for the current active rectangle. Caller holds _lock.</summary>
        static void Place()
        {
            for (int i = 0; i < _zones; i++)
            {
                double fx = ActL + _fx[i] * (ActR - ActL);
                double fy = ActT + _fy[i] * (ActB - ActT);
                int x, y; Clamp(fx, fy, out x, out y);
                _px[i] = x; _py[i] = y;
            }
        }

        static bool ProbeDue()
        {
            if (!BarsEnabled || !Ready) return false;
            return (DateTime.UtcNow - _lastProbeUtc).TotalSeconds >= ProbeIntervalS;
        }

        static bool Near(double a, double b) { return Math.Abs(a - b) <= 0.035; }

        static void SetProbe(double ms, string note, bool probed, bool discarded)
        {
            lock (_lock)
            {
                LastProbeMs = ms; ProbeNote = note;
                if (probed) Probes++;
                if (discarded) ProbesDiscarded++;
            }
        }

        static void ProbeBars()
        {
            var sw = Stopwatch.StartNew();
            _lastProbeUtc = DateTime.UtcNow;
            double[] along = { 0.3, 0.5, 0.7 };        // lines crossing the top/bottom probes
            double[] across = { 0.35, 0.5, 0.65 };     // lines crossing the left/right probes
            int per = ProbeN * 3;
            int n = per * 4 + 3;
            var xs = new double[n]; var ys = new double[n];
            int q = 0;
            for (int side = 0; side < 4; side++)
                for (int k = 0; k < ProbeN; k++)
                {
                    double d = (k + 0.5) * ProbeDepth / ProbeN;
                    for (int l = 0; l < 3; l++)
                    {
                        if (side == 0) { xs[q] = along[l]; ys[q] = d; }                 // top
                        else if (side == 1) { xs[q] = along[l]; ys[q] = 1.0 - d; }       // bottom
                        else if (side == 2) { xs[q] = d; ys[q] = across[l]; }            // left
                        else { xs[q] = 1.0 - d; ys[q] = across[l]; }                     // right
                        q++;
                    }
                }
            xs[q] = 0.5; ys[q] = 0.5; q++;
            xs[q] = 0.4; ys[q] = 0.4; q++;
            xs[q] = 0.6; ys[q] = 0.6; q++;

            var mx = new int[n];
            if (!MeasureMax(xs, ys, n, mx)) { SetProbe(sw.Elapsed.TotalMilliseconds, "aborted", false, false); return; }
            bool centreLit = mx[n - 1] > BlackMax10 || mx[n - 2] > BlackMax10 || mx[n - 3] > BlackMax10;
            double ms = sw.Elapsed.TotalMilliseconds;
            if (!centreLit) { SetProbe(ms, "discarded: centre black", true, true); return; }

            double[] edge = new double[4];
            for (int side = 0; side < 4; side++)
            {
                int first = -1;
                for (int k = 0; k < ProbeN && first < 0; k++)
                {
                    bool lit = false;
                    for (int l = 0; l < 3; l++) if (mx[side * per + k * 3 + l] > BlackMax10) lit = true;
                    if (lit) first = k;
                }
                if (first < 0) { SetProbe(ms, "discarded: a side is black to depth", true, true); return; }
                if (first < 2) first = 0;                            // < 2 cells (6%) is not believed as a bar
                edge[side] = first * ProbeDepth / ProbeN;
            }
            double nT = edge[0], nB = 1.0 - edge[1], nL = edge[2], nR = 1.0 - edge[3];
            if (nB - nT < 0.4 || nR - nL < 0.4) { SetProbe(ms, "discarded: active area implausible", true, true); return; }
            string note = "probe L" + nL.ToString("0.000") + " T" + nT.ToString("0.000") + " R" + nR.ToString("0.000") + " B" + nB.ToString("0.000");

            if (Near(nL, ActL) && Near(nT, ActT) && Near(nR, ActR) && Near(nB, ActB)) { SetProbe(ms, note, true, false); _candCount = 0; return; }
            if (_candCount > 0 && Near(nL, _cL) && Near(nT, _cT) && Near(nR, _cR) && Near(nB, _cB))
            {
                lock (_lock)
                {
                    ActL = Math.Max(_cL, nL); ActT = Math.Max(_cT, nT); ActR = Math.Min(_cR, nR); ActB = Math.Min(_cB, nB);
                    Place();
                    _rebuild = true;
                    Adoptions++;
                    Probes++;
                    LastProbeMs = ms; ProbeNote = note + " ADOPTED";
                }
                _candCount = 0;
            }
            else
            {
                SetProbe(ms, note, true, false);
                _cL = nL; _cT = nT; _cR = nR; _cB = nB; _candCount = 1;
                // confirm quickly instead of waiting a whole interval
                _lastProbeUtc = DateTime.UtcNow.AddSeconds(-(ProbeIntervalS - 1));
            }
        }

        /// <summary>Max channel (10-bit) at n normalised points, arm/settle/read like the rolling batches.
        /// Always waits the TV-reported SleepMs (a probe must not be fooled by a stale slot).</summary>
        static bool MeasureMax(double[] xs, double[] ys, int n, int[] outMax)
        {
            int slots = Slots;
            for (int i = 0; i < n; i += slots)
            {
                if (Abort()) return false;
                int armed = Math.Min(slots, n - i);
                for (int j = 0; j < armed; j++)
                {
                    int x, y; Clamp(xs[i + j], ys[i + j], out x, out y);
                    SetPos(j, x, y);
                }
                Wait(SleepMs);
                for (int k = 0; k < armed; k++)
                {
                    int rr, gg, bb, lag;
                    // a failed read must not look like a bar
                    outMax[i + k] = ReadPix(k, out rr, out gg, out bb, out lag) == 0 ? Math.Max(rr, Math.Max(gg, bb)) : 1023;
                }
            }
            return true;
        }

        /// <summary>Normalised frame position -> block top-left in measure space, clamped exactly as
        /// HyperTizen does so the block never leaves the panel.</summary>
        static void Clamp(double fx, double fy, out int x, out int y)
        {
            x = (int)(fx * FullW) - BlockW / 2;
            y = (int)(fy * FullH) - BlockH / 2;
            x = Math.Max(0, Math.Min(FullW - BlockW - 1, x));
            y = Math.Max(0, Math.Min(FullH - BlockH - 1, y));
        }

        // ================================================================ diagnostics (one-shot tasks)
        // Both read the picture only, through the same slot-guarded calls as the sampler (slots below the
        // reported count). Results go to telemetry; they change nothing on the strip.

        // ---- thumbnail: a cols x rows grid of the WHOLE picture, so a human can see the measurement is the
        // real picture and where the bars are. Control token ppigrid=1. ----
        public static int GridRequest;
        public static string[] GridRows;         // replaced as a whole, under _lock together with GridMs/GridSweeps
        public static long GridSweeps;
        public static double GridMs;

        static void GridSweep(int cols, int rows)
        {
            var sw = Stopwatch.StartNew();
            int slots = Slots;
            int total = cols * rows;
            var rgb = new int[total * 3];
            for (int i = 0; i < total; i += slots)
            {
                if (Abort()) return;
                int armed = Math.Min(slots, total - i);
                for (int j = 0; j < armed; j++)
                {
                    int c = (i + j) % cols, r = (i + j) / cols, x, y;
                    Clamp((c + 0.5) / cols, (r + 0.5) / rows, out x, out y);
                    SetPos(j, x, y);
                }
                Wait(SleepMs);
                for (int k = 0; k < armed; k++)
                {
                    int rr, gg, bb, lag;
                    if (ReadPix(k, out rr, out gg, out bb, out lag) != 0) continue;
                    int z = i + k;
                    rgb[z * 3] = rr >> 2; rgb[z * 3 + 1] = gg >> 2; rgb[z * 3 + 2] = bb >> 2;
                }
            }
            var outRows = new string[rows];
            for (int r = 0; r < rows; r++)
            {
                var sb = new StringBuilder();
                for (int c = 0; c < cols; c++)
                {
                    int z = r * cols + c;
                    for (int ch = 0; ch < 3; ch++)
                        sb.Append(Math.Min(255, Math.Max(0, rgb[z * 3 + ch])).ToString("x2"));
                }
                outRows[r] = sb.ToString();
            }
            lock (_lock) { GridRows = outRows; GridSweeps++; GridMs = sw.Elapsed.TotalMilliseconds; }
        }

        // ---- FRESHNESS test (control token pfresh=1 coarse | 2 fine) --------------------------------
        // Question: at settle time d, does a slot return the value of the position it was JUST set to, or
        // still the one before it (stale)? Method: find two positions A (darkest) and B (brightest) with
        // large contrast, then run the REAL access pattern - arm slot0=A and slot1=B, wait d, read both;
        // then swap (slot0=B, slot1=A), wait d, read both - and count reads that carry the class of the
        // position they were set to. A stale read carries the class of the PREVIOUS position, i.e. the
        // opposite one, so stale reads show up as wrong-class reads. d=20 (the TV's own value) is run first
        // and last as a control: its score is the noise floor from the picture moving.
        public static int FreshRequest;
        public static volatile List<object> FreshResults = new List<object>();   // replaced as a whole, never mutated after publishing
        public static volatile string FreshNote = "not run";
        public static long FreshRuns;
        static readonly int[] FreshDelaysCoarse = { 20, 16, 10, 5, 0, 20 };
        static readonly int[] FreshDelaysFine = { 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 20 };
        const int FreshIters = 80;

        /// <summary>Pick two stable, high-contrast measure positions (darkest A, brightest B) by reading a
        /// candidate set twice at a long settle time. Returns null on success, else the reason.</summary>
        static string PickAB(out int ax, out int ay, out int bx, out int by, out int va, out int vb)
        {
            ax = ay = bx = by = va = vb = 0;
            // candidate points: top/bottom bar strips + a 4x3 grid through the picture
            var cx = new List<double>(); var cy = new List<double>();
            foreach (double x in new[] { 0.3, 0.5, 0.7 }) { cx.Add(x); cy.Add(0.03); cx.Add(x); cy.Add(0.97); }
            foreach (double y in new[] { 0.3, 0.5, 0.7 })
                foreach (double x in new[] { 0.2, 0.4, 0.6, 0.8 }) { cx.Add(x); cy.Add(y); }
            int nc = cx.Count;
            var v1 = new int[nc]; var v2 = new int[nc];
            for (int pass = 0; pass < 2; pass++)
            {
                var dst = pass == 0 ? v1 : v2;
                for (int i = 0; i < nc; i++)
                {
                    if (Abort()) return "aborted";
                    int x, y; Clamp(cx[i], cy[i], out x, out y);
                    SetPos(0, x, y); Wait(30);
                    int r, g, b, lag;
                    dst[i] = ReadPix(0, out r, out g, out b, out lag) == 0 ? Math.Max(r, Math.Max(g, b)) : -1;
                }
            }
            int ia = -1, ib = -1;
            for (int i = 0; i < nc; i++)
            {
                if (v1[i] < 0 || v2[i] < 0 || Math.Abs(v1[i] - v2[i]) > 40) continue;   // unstable: moving picture
                int v = (v1[i] + v2[i]) / 2;
                if (ia < 0 || v < (v1[ia] + v2[ia]) / 2) ia = i;
                if (ib < 0 || v > (v1[ib] + v2[ib]) / 2) ib = i;
            }
            if (ia < 0 || ib < 0) return "no stable candidate points";
            va = (v1[ia] + v2[ia]) / 2; vb = (v1[ib] + v2[ib]) / 2;
            if (vb - va < 150) return "insufficient contrast A=" + va + " B=" + vb + " (need >=150, 10-bit)";
            Clamp(cx[ia], cy[ia], out ax, out ay); Clamp(cx[ib], cy[ib], out bx, out by);
            return null;
        }

        /// <summary>Tri-state class of a reading against the dark reference: 0 dark (<= ref+25), 1 lit
        /// (>= ref+80), -1 ambiguous. The picture moves during a test, so B can fade; ambiguous reads are
        /// COUNTED SEPARATELY instead of being scored as stale.</summary>
        static int Cls(int v, int darkRef) { return v <= darkRef + 25 ? 0 : v >= darkRef + 80 ? 1 : -1; }

        static void FreshnessTest(int mode)
        {
            int[] delays = mode == 2 ? FreshDelaysFine : FreshDelaysCoarse;
            var res = new List<object>();
            Interlocked.Increment(ref FreshRuns);
            int ax, ay, bx, by, va, vb;
            string why = PickAB(out ax, out ay, out bx, out by, out va, out vb);
            if (why != null) { FreshNote = why; FreshResults = res; return; }
            int mid = (va + vb) / 2;
            int used = Math.Min(2, Slots);     // slots 0 and 1 only, and only if the TV reported them
            FreshNote = "A=(" + ax + "," + ay + ")~" + va + " B=(" + bx + "," + by + ")~" + vb + " mid=" + mid +
                        " iters=" + FreshIters + " slots=" + Slots;

            foreach (int d in delays)
            {
                long reads = 0, ok = 0, bad = 0, failed = 0, amb = 0;
                long sumDark = 0, nDark = 0, sumBright = 0, nBright = 0;
                var lagCount = new Dictionary<int, long>();
                var sw = Stopwatch.StartNew();
                bool aborted = false;
                for (int it = 0; it < FreshIters && !aborted; it++)
                {
                    for (int phase = 0; phase < 2; phase++)
                    {
                        if (Abort()) { aborted = true; break; }
                        // phase 0: slot0=A slot1=B ; phase 1: slot0=B slot1=A
                        SetPos(0, phase == 0 ? ax : bx, phase == 0 ? ay : by);
                        if (used > 1) SetPos(1, phase == 0 ? bx : ax, phase == 0 ? by : ay);
                        Wait(d);
                        for (int s = 0; s < used; s++)
                        {
                            int r, g, b, lag;
                            if (ReadPix(s, out r, out g, out b, out lag) != 0) { failed++; continue; }
                            reads++;
                            int v = Math.Max(r, Math.Max(g, b));
                            long lc; lagCount.TryGetValue(lag, out lc); lagCount[lag] = lc + 1;
                            bool expectDark = (phase == 0) == (s == 0);       // slot0 dark in phase0, slot1 dark in phase1
                            if (expectDark) { sumDark += v; nDark++; } else { sumBright += v; nBright++; }
                            int cls = Cls(v, va);
                            if (cls < 0) amb++;
                            else if ((cls == 0) == expectDark) ok++; else bad++;
                        }
                    }
                }
                sw.Stop();
                double sec = sw.Elapsed.TotalSeconds;
                var lagStr = new StringBuilder();
                foreach (var kv in lagCount) lagStr.Append(kv.Key).Append(':').Append(kv.Value).Append(' ');
                long cycles = (reads + failed) / Math.Max(1, used);
                res.Add(new Dictionary<string, object>
                {
                    { "settle_ms", (long)d },
                    { "reads", reads },
                    { "read_failures", failed },
                    { "samples_per_s", sec > 0 ? Math.Round((reads + failed) / sec, 1) : 0.0 },
                    { "cycle_ms", cycles > 0 ? Math.Round(sec * 1000.0 / cycles, 2) : 0.0 },
                    { "fresh_pct", ok + bad > 0 ? Math.Round(100.0 * ok / (ok + bad), 1) : 0.0 },
                    { "stale_or_wrong_pct", ok + bad > 0 ? Math.Round(100.0 * bad / (ok + bad), 1) : 0.0 },
                    { "ambiguous_reads", amb },
                    { "mean_read_when_set_dark", nDark > 0 ? Math.Round(sumDark / (double)nDark, 0) : 0.0 },
                    { "mean_read_when_set_bright", nBright > 0 ? Math.Round(sumBright / (double)nBright, 0) : 0.0 },
                    { "lowInputLagOnOff_counts", lagStr.ToString().Trim() },
                    { "aborted", aborted },
                });
                if (aborted) break;
            }
            FreshResults = res;
        }

        // ================================================================ telemetry
        /// <summary>Telemetry block. Called from the render thread only (it advances the rate window).</summary>
        public static Dictionary<string, object> Describe()
        {
            DateTime now = DateTime.UtcNow;
            double win = (now - _winT0).TotalSeconds;
            long samples, cycles;
            lock (_lock) { samples = Samples; }
            cycles = Interlocked.Read(ref Cycles);
            long ds = samples - _winSamples0, dc = cycles - _winCycles0;
            _winSamples0 = samples; _winCycles0 = cycles; _winT0 = now;
            double sps = win > 0 ? ds / win : 0.0;
            int pz = _pz;
            int lagMin = _lagMin, lagMax = _lagMax;
            var d = new Dictionary<string, object>
            {
                { "ready", Ready },
                { "init_error", InitError ?? "" },
                { "sampler_running", Running },
                { "thread_errors", Interlocked.Read(ref ThreadErrors) },
                { "thread_last_error", LastError },
                { "cond_rc", (long)CondRc },
                { "cond_slots", (long)Slots },
                { "cond_block", BlockW + "x" + BlockH },
                { "cond_sleep_ms", (long)SleepMsReported },
                { "cond_full", FullW + "x" + FullH },
                { "cond_extra", CondExtra6 + "," + CondExtra7 },
                { "thread_sleep1_ms", Math.Round(Sleep1Ms, 2) },
                { "settle_used_ms", (long)EffectiveSleepMs },
                { "pz", (long)pz },
                { "samples_total", samples },
                { "samples_per_s_window", Math.Round(sps, 1) },
                { "cycles_per_s_window", Math.Round(win > 0 ? dc / win : 0.0, 1) },
                { "per_zone_refresh_hz", Math.Round(pz > 0 ? sps / pz : 0.0, 2) },
                { "last_pos_rc", (long)LastPosRc },
                { "last_pix_rc", (long)LastPixRc },
                { "pos_fail", Interlocked.Read(ref PosFails) },
                { "pix_fail", Interlocked.Read(ref PixFails) },
                { "pix_invalid", Interlocked.Read(ref PixInvalid) },
                { "lowInputLagOnOff_seen", (lagMin == int.MaxValue ? "none" : lagMin + ".." + lagMax) },
                { "bars_enabled", BarsEnabled },
                { "fresh_note", FreshNote },
            };
            List<object> fr = FreshResults;
            if (fr != null && fr.Count > 0) d["fresh_test"] = fr;
            lock (_lock)
            {
                d["active_rect_LTRB"] = ActL.ToString("0.000") + "," + ActT.ToString("0.000") + "," + ActR.ToString("0.000") + "," + ActB.ToString("0.000");
                d["bar_probes"] = Probes;
                d["bar_probes_discarded"] = ProbesDiscarded;
                d["bar_adoptions"] = Adoptions;
                d["bar_probe_ms"] = LastProbeMs;
                d["bar_probe_note"] = ProbeNote;
                if (GridRows != null)
                {
                    d["grid_32x18_hex"] = new List<object>(GridRows);
                    d["grid_ms"] = GridMs;
                    d["grid_sweeps"] = GridSweeps;
                }
                if (_mz != null && _pz > 0)
                {
                    var sample = new List<object>();
                    for (int s = 0; s < 6; s++)
                    {
                        int k = (int)((long)s * _pz / 6);
                        Zone zn = _geomLayout.Zones[_mz[k]];
                        sample.Add("m" + k + "(z" + _mz[k] + " " + zn.Edge + "[" + zn.Slot + "])=" +
                                   _val[k * 3] + "," + _val[k * 3 + 1] + "," + _val[k * 3 + 2] + " @" + _mpx[k] + "," + _mpy[k]);
                    }
                    d["raw10_sample"] = sample;
                }
            }
            return d;
        }
    }
}
