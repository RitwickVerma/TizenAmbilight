using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Tizen.Applications;

namespace TizenAmbilight
{
    /// <summary>
    /// TizenAmbilight — a real-frame ambilight for Samsung Tizen TVs.
    ///
    /// One loop, one native call per frame: dc_request_capture_to_file_sync() writes a PNG of the
    /// screen, we decode it ourselves (PngDecoder), average a border band into N perimeter zones
    /// (ZoneEngine), smooth those over time with an EMA, and — ONLY when the `outputEnabled`
    /// preference is explicitly true — push the colours to a WLED strip as one DRGB UDP datagram.
    /// `outputEnabled` defaults to TRUE on this device - set deliberately on explicit request so the
    /// strip is driven out of the box. It still gates every send, so a preference of false makes the
    /// app compute and report colours without emitting a packet. A stock install on a NEW device
    /// would therefore drive the strip: change the default below before reusing this app elsewhere.
    ///
    /// The loop is instrumented rather than guessed at: capture/decode/zone/smooth/total
    /// milliseconds, the achieved and window fps, and per-status counters are POSTed to a local
    /// collector every `statsIntervalSec`. What the earlier measurement builds established about
    /// the capture API is recorded as a constant (<see cref="RecordedMeasurement"/>) and logged
    /// once at startup — it is not re-derived at runtime.
    ///
    /// A JPEG decoder exists (compressType=1 selects it) but is deliberately NOT yet wired into
    /// the main loop: the capture-call saving is real, but end to end it is much smaller than the
    /// per-frame overhead the loop still carries, and that has to be measured first.
    ///
    /// POWER-STATE AWARENESS: before every capture the loop asks the TV's own local REST API
    /// (port 8001) whether the panel is on. While it is in standby there is nothing to capture, so
    /// CaptureApi.Capture is not called at all and the loop sleeps long instead — the earlier
    /// measurement recorded ~10.8k wasted capture calls at ~1/s sustained with the panel off, into
    /// an API known to burst then stop. A probe that cannot answer is treated as "panel on", so a
    /// working screen is never skipped. See <see cref="PowerStateProbe"/>.
    ///
    /// SAFETY: outputEnabled defaults to TRUE on this device (see above). Only dc_request_capture_to_file_sync is ever
    /// called and no /dev node is touched by this project.
    /// </summary>
    class App : ServiceApplication
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            Task.Run(() => Ambilight.Run());
        }

        /// <summary>Framework terminate callback. Blanks the strip so it is never left latched on
        /// a stale frame. This is the real registered terminate callback, but note it only fires on
        /// a framework-initiated terminate: a hard kill relies on the DRGB timeout byte instead,
        /// which is why every packet (including the blackout) carries a short timeout.</summary>
        protected override void OnTerminate()
        {
            Ambilight.OnShutdown();
            base.OnTerminate();
        }

        static void Main(string[] args)
        {
            // GRACEFUL TERMINATION. An unhandled exception on any thread otherwise kills the process
            // outright: no blackout, no log, and the strip stays latched on its last frame until the
            // DRGB timeout byte expires. Catch it, blank the strip, flush what we know, then exit.
            AppDomain.CurrentDomain.UnhandledException += (s2, e2) =>
            {
                try
                {
                    Ambilight.L("FATAL unhandled exception: " + e2.ExceptionObject);
                    Ambilight.OnShutdown();          // blanks the strip and sets the stopping flag
                    Ambilight.FlushLog();
                }
                catch { /* nothing left to do */ }
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s2, e2) =>
            {
                try { Ambilight.L("unobserved task exception: " + e2.Exception); e2.SetObserved(); } catch { }
            };
            new App().Run(args);
        }
    }

    static class Ambilight
    {
        public const string AppId = "io.gh.hermes.TizenAmbilight";
        // Bump this whenever behaviour changes. It sat at "v3" through the HDR decode, the BT.2020->709
        // gamut map and the adaptive tone curve, which made build identification useless in telemetry.
        public const string Version = "v4";

        // ---- failure handling ----
        const int FailSleepMs = 200;                     // pause after a frame-less iteration
        /// <summary>Blank the strip after this long with NO usable frame.
        ///
        /// TIME-based, not count-based. The old rule ("10 consecutive fails") interacted with the
        /// backoff ladder {200,500,1000,2000...} to need ~15.7 s before it blanked - the strip sat on
        /// a stale colour through the whole ramp. Elapsed time is the honest signal and cannot be
        /// stretched by backoff.
        ///
        /// 700 ms is safe: normal "not ready" rejections are replaced by a good frame within ~100 ms
        /// at 10-45 fps, so this only triggers when the picture genuinely stops.</summary>
        const int BlankAfterNoFrameMs = 700;

        // ---- power-state awareness ----
        // The panel's real state is read from the TV's OWN local REST API (port 8001) rather than
        // inferred from capture failures, because a frame-less capture is also what a transient API
        // hiccup looks like. When the panel is off there is nothing to capture, so the loop must not
        // call the capture API at all: the live measurement showed ~10.8k failed capture calls,
        // ~1/s sustained, while the panel was in standby, and the API is known to burst then stop.
        const int ControlPollMs = 250;        // control endpoint cadence (worker thread)
        const int IoWorkerTickMs = 50;        // worker loop granularity
        const int PowerPollCacheMs = 1000;    // re-probe the local API at most once per 1 s
                                              // (was 5000: the stale 5 s cache WAS the "~5 s stuck" delay)
        const int PowerPollTimeoutSec = 2;    // short: a stalled probe must not stall the loop
        const int OffSleepMs = 500;           // panel off: short first sleep so RESUME is fast ...
        const int OffSleepIdleMs = 3000;      // ... then back off to this once standby looks settled

        // ---- progressive backoff for the ON-but-failing case ----
        // When the panel reports ON but captures keep failing, hammering a fixed 200 ms only makes
        // the burst worse. Step the pause up and reset it on the first usable frame:
        //     200 -> 500 -> 1000 -> 2000 ms, capped at the last entry.
        static readonly int[] FailBackoffLadderMs = { FailSleepMs, 500, 1000, 2000 };

        /// <summary>The sleep the loop should use right now for a frame-less iteration (ON path).</summary>
        static int BackoffSleep(Stats st)
        {
            long i = st.BackoffStep;
            if (i < 0) i = 0;
            if (i >= FailBackoffLadderMs.Length) i = FailBackoffLadderMs.Length - 1;
            return FailBackoffLadderMs[(int)i];
        }

        /// <summary>Step one rung down the ladder after a frame-less iteration (capped).</summary>
        static void BackoffAdvance(Stats st)
        {
            if (st.BackoffStep < FailBackoffLadderMs.Length - 1) st.BackoffStep++;
        }

        /// <summary>
        /// Already MEASURED on the target TV (QN77S84FAEXZA, Tizen 9) by the earlier measurement
        /// builds, and recorded here so the number survives without re-running the sweeps that
        /// produced it. These are RECORDED readings, not live values: nothing at runtime derives
        /// or refreshes them.
        /// </summary>
        public const string RecordedMeasurement =
            "recorded: 640x360 PNG capture ~89ms, JPEG ~12ms; PNG end-to-end ~8.9fps";

        /// <summary>Set once at startup: the capture file name (and extension) follows
        /// compress_type, so a JPEG request never writes a file called ".png".</summary>
        static string CaptureFileName = "amb.png";

        static Config _cfg;   // set once at startup; read by the shutdown hook

        static readonly DateTime ProcessStartUtc = DateTime.UtcNow;
        // GUARDED BY _logLock. The I/O worker calls L() too (stats POST failures), so this list has
        // two writers and two readers on different threads. List<T> is NOT thread-safe: a concurrent
        // Add during the stats-payload copy throws "Collection was modified", and Add racing
        // RemoveRange can corrupt the internal array. All three accessors below take the lock.
        static readonly List<string> Log = new List<string>();
        static readonly object _logLock = new object();

        public static void L(string s)
        {
            string line = (long)(DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds + "ms " + s;
            lock (_logLock)
            {
                Log.Add(line);
                if (Log.Count > 800) Log.RemoveRange(0, Log.Count - 800);
            }
            // Console is out of the lock: it can block on a slow tty and we must not hold the log lock
            // while it does, or the worker thread stalls behind it.
            try { Console.WriteLine("[amb] " + line); } catch { /* logging must never throw */ }
        }

        // ================================================================ config

        sealed class Config
        {
            public bool OutputEnabled;          // default TRUE on this device: enabled on explicit
                                                // user request 2026-09-11. Only a deliberate change.
            public int ZoneCount;
            public double Alpha;
            // ---- output brightness shaping (WLED itself is already at bri=255) ----
            public double Gain;
            public double Gamma;
            public double Black;        // blacks at/below this go to zero (kills the "milky" haze)
            // ---- adaptive temporal + spatial smoothing ----
            public double AlphaCut;     // mean per-channel frame delta above which we react FAST
            public double AlphaFast;    // alpha used for those frames (scene cuts)
            public double Spatial;      // perimeter neighbour blend, 0 = off
            // ---- letterbox (black bar) handling ----
            public bool Bars;           // slide top/bottom bands inside the active picture area
            public int BarThresh;       // a row is bar if >=90% of it is below this
            public int BarHold;         // frames a new bar size must persist before being adopted
            // ---- ambience ----
            public double Punch;        // brief brightness/saturation overshoot on a cut, 0 = off
            public int PunchFrames;     // how many frames the punch decays over
            public double Floor;        // minimum zone level; 0 = true blacks
            // ---- stale content: a frozen picture must not hold the strip lit forever ----
            public double StaleMs;      // fade after the picture has not moved this long (0 = never)
            public double StaleLevel;   // dim factor to fade to (0.35 = 35%)
            // ---- colour handling (added with the YUV rework) ----
            public double Saturation;   // chroma expansion about neutral, applied before the matrix
            public bool Hdr;                    // decode the plane as PQ-encoded HDR
        public bool Gamut;                  // convert BT.2020 primaries -> BT.709 for the strip
        public int ZoneSample;              // pixel stride when averaging zones (1 = every pixel)
        // ---- adaptive tone curve (see SceneGammaTarget) ----
        public bool ZoneHexAll;             // report every zone in zone_sample_hex (audit tool)
        public bool AdaptiveGamma;          // drive gamma from the scene's brightness
        public double GammaMin, GammaMax;   // clamp range for the adapted gamma
        public double AdaptTauS;            // EMA time constant of the adaptation
        public int TargetFps;               // cap on the capture rate; 0 = uncapped
            public double WhiteR, WhiteG, WhiteB;   // per-channel white balance, applied after it
            public double BandFrac;
            public string WledIp;
            public int WledPort;
            public int WledTimeout;
            public string Endpoint;
            public int StatsIntervalSec;
            // ---- power-state probe (local API on the TV itself) ----
            public string PowerUrl;
            // ---- the main loop's capture request is steerable without a rebuild ----
            public int RequestWidth;
            public int RequestHeight;
            // ---- which capture backend the main loop uses ----
            // "video_plane" : secvideo_api_capture_video_main -> the VIDEO plane, in memory (no file)
            // "file"        : the original dc_request_capture_to_file_sync PNG-file path
            public string CaptureBackend;
            public string ControlUrl;   // GET here; body containing "pause" stops capture calls
            // ---- live-steerable mapping (see WledOutput.BuildDrgbMapped) ----
            public int MapOffset;
            public bool MapReverse;
            public int LedCount;        // physical LEDs to drive (75 on this strip)
            public bool TestPattern;    // send the 4-edge colour test instead of the ambilight
            public int CompressType;
            public int JpegQuality;

            public Dictionary<string, object> ToJson()
            {
                return new Dictionary<string, object>
                {
                    { "output_enabled", OutputEnabled },
                    { "zone_count", (long)ZoneCount },
                    { "smoothing_alpha", Alpha },
                    { "border_band_frac", BandFrac },
                    { "wled_ip", WledIp },
                    { "wled_port", (long)WledPort },
                    { "wled_timeout_s", (long)WledTimeout },
                    { "probe_endpoint", Endpoint },
                    { "stats_interval_s", (long)StatsIntervalSec },
                    { "power_state_url", PowerUrl },
                    { "request_width", (long)RequestWidth },
                    { "request_height", (long)RequestHeight },
                    { "compress_type", (long)CompressType },
                    { "jpeg_quality", (long)JpegQuality },
                };
            }
        }

        static bool PrefBool(string key, bool dflt)
        {
            try
            {
                if (!Preference.Contains(key)) return dflt;
                // Only an explicit true enables anything; anything else stays at the default.
                object v = Preference.Get<bool>(key);
                return (v is bool) && (bool)v;
            }
            catch { return dflt; }
        }

        static int PrefInt(string key, int dflt)
        {
            try { return Preference.Contains(key) ? Preference.Get<int>(key) : dflt; }
            catch { return dflt; }
        }

        static double PrefDouble(string key, double dflt)
        {
            try
            {
                if (!Preference.Contains(key)) return dflt;
                object v = Preference.Get<double>(key);
                return v == null ? dflt : Convert.ToDouble(v);
            }
            catch { return dflt; }
        }

        static string PrefString(string key, string dflt)
        {
            try
            {
                if (!Preference.Contains(key)) return dflt;
                string v = Preference.Get<string>(key);
                return string.IsNullOrEmpty(v) ? dflt : v;
            }
            catch { return dflt; }
        }

        // ================================================================ capture control channel
        //
        // WHY THIS EXISTS. Measured today, 3 deploys out of 3: running `tizen install` over this app
        // SIGKILLs it while it is mid-capture, and that leaves the TV's scaler wedged - every
        // subsequent capture returns rc=-2 until the TV is rebooted. The app's own runtime is NOT the
        // problem: it captured 7,369 frames continuously at 19 fps with zero failures immediately
        // before the first such kill.
        //
        // So the app polls a tiny control endpoint and, when told to, STOPS MAKING CAPTURE CALLS
        // while staying alive. A redeploy after that has no capture in flight to abandon.
        static bool _capturePaused;
        static bool _pauseLogged;

        /// <summary>Fetch the control body. Returns null on any failure (fail-safe: keep running).
        /// Format: comma-separated tokens, e.g. "run,map=23,rev=1,test=1".
        ///   pause / run     - stop or resume making capture calls
        ///   map=N           - ring rotation offset
        ///   rev=0|1         - run the ring the other way
        ///   test=0|1        - send the 4-edge colour test instead of ambilight colours
        /// The app applies these LIVE, so the mapping can be dialled in by eye with no rebuild.</summary>
        // ================================================================ background I/O
        // The render loop must NEVER block on the network. Each iteration could previously stall on a
        // control GET (every 15 frames), a power GET (every 1 s) and a stats POST (every 5 s), all
        // synchronous on the frame thread. At 40 fps one 100 ms call costs an entire frame slot - that
        // is what made the frame rate UNEVEN rather than merely low.
        //
        // One worker thread now owns ALL network I/O: control poll, power poll, stats POST. The render
        // loop reads volatile fields and drops a payload into a ONE-SLOT mailbox. One slot means a slow
        // endpoint can never build a backlog: the newest snapshot replaces the stale one.
        static volatile string _ctrlBody;
        static string _appliedCtrl;
        static int _dumpRequested;      // control token: dump the next captured frame for inspection
        static volatile bool _powerOff;
        static volatile bool _powerKnown;
        static volatile string _powerRaw = "";
        // The firmware the app is running against. The capture ABI (param offsets, plane layout) was
        // reverse-engineered against a specific build and does NOT come with a version in the API, so
        // the only defence is to record what it was validated on and make a change LOUD.
        /// <summary>Read the Tizen platform version REFLECTIVELY.
        ///
        /// Why reflection rather than `Tizen.System.SystemInfo.Version`: a compile-time reference to a
        /// named property is itself an API-change hazard - if an OS upgrade renames or drops it, the
        /// app stops BUILDING. Reflecting cannot break the build, works if the property is merely
        /// renamed, and degrades to "not available" instead of failing.
        ///
        /// The TV's own REST API is useless here: it returns the literal string "Unknown" for
        /// firmwareVersion (verified). This is the only version signal available.</summary>
        public static string ReadPlatformInfo()
        {
            // 1. Kernel + distro files FIRST: plain text, no assembly loading, and a kernel or Tizen
            //    release bump is exactly the OS upgrade we want to notice.
            try
            {
                if (System.IO.File.Exists("/proc/version"))
                {
                    string line = System.IO.File.ReadAllText("/proc/version").Trim();
                    if (line.Length > 0) return "kernel=" + (line.Length > 80 ? line.Substring(0, 80) : line);
                }
            }
            catch { }
            try
            {
                if (System.IO.File.Exists("/etc/tizen-release"))
                {
                    string line = System.IO.File.ReadAllText("/etc/tizen-release").Trim();
                    if (line.Length > 0) return "tizen-release=" + line;
                }
            }
            catch { }

            // 2. Then the Tizen .NET API. GetAssemblies() returns only ALREADY-LOADED assemblies, and
            //    nothing has touched Tizen.System in this process - which is exactly why the first
            //    attempt returned nothing. Load it explicitly before reflecting.
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (dir != null && System.IO.Directory.Exists(dir))
                {
                    foreach (string f in System.IO.Directory.GetFiles(dir, "Tizen*.dll"))
                    {
                        try { System.Reflection.Assembly.LoadFrom(f); } catch { }
                    }
                }
            }
            catch { }
            foreach (string nm in new string[] { "Tizen.System", "Tizen" })
            {
                try { System.Reflection.Assembly.Load(nm); } catch { }
            }

            try
            {
                string[] wanted = { "Version", "PlatformVersion", "Build", "Model", "PlatformName" };
                foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType("Tizen.System.SystemInfo", false); } catch { }
                    if (t == null) continue;
                    var parts = new List<string>();
                    foreach (string name in wanted)
                    {
                        System.Reflection.PropertyInfo pi = null;
                        try
                        {
                            pi = t.GetProperty(name, System.Reflection.BindingFlags.Public |
                                                     System.Reflection.BindingFlags.Static);
                        }
                        catch { }
                        if (pi == null) continue;
                        object v = null;
                        try { v = pi.GetValue(null, null); } catch { }
                        if (v != null) parts.Add(name + "=" + v);
                    }
                    if (parts.Count > 0) return string.Join(" ", parts.ToArray());
                }
            }
            catch { }
            return null;
        }

        static readonly string PlatformStampPath = "/home/owner/ambilight/platform.txt";

        /// <summary>Compare the platform version with the previous run's and shout if it changed. An OS
        /// upgrade can alter the capture ABI silently, so a change is the single most valuable thing to
        /// surface. Never throws, and the version is reported either way even if the file cannot be
        /// written.</summary>
        public static string CheckPlatformChange()
        {
            string now = ReadPlatformInfo();
            if (now == null) return "unavailable";
            string prev = null;
            try
            {
                if (System.IO.File.Exists(PlatformStampPath))
                    prev = System.IO.File.ReadAllText(PlatformStampPath).Trim();
            }
            catch { }
            if (prev != null && prev.Length > 0 && prev != now)
            {
                L("!!! PLATFORM CHANGED: was [" + prev + "] now [" + now + "] - the capture ABI was " +
                  "validated on " + ValidatedFirmware + ". Re-verify the plane layout and param offsets " +
                  "before trusting the picture.");
            }
            else if (prev == null)
            {
                L("platform (first run): " + now);
            }
            try
            {
                string dir = System.IO.Path.GetDirectoryName(PlatformStampPath);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(PlatformStampPath, now);
            }
            catch { }
            return now;
        }

        /// <summary>The build the capture ABI (param offsets, plane layout) was validated against.
        /// Recorded so a firmware change is visible in the log and telemetry rather than silent.</summary>
        static readonly string ValidatedFirmware = "TIZEN-TRUNK2025-PontusM-HOTFIX-RELEASE_20260714.1";
        static volatile string _firmware = "";
        static volatile string _platform = "";
        /// <summary>Adaptive-tone-curve state. Written and read ONLY by the render thread (the telemetry
        /// builder runs on it too), so no synchronisation is needed - but they must be fields rather
        /// than loop locals because the stats payload is assembled in a separate method.</summary>
        static double _adaptGamma;
        static int _lastMedianCode;
        static volatile string _modelName = "";
        static volatile string _powerErr;
        static long _powerPolls;
        static long _powerFails;        // probes that could not answer (assumed ON)
        static readonly object _postLock = new object();
        static string _pendingPost;
        static string _pendingDumpY;
        static string _pendingDumpC;
        static System.Net.Http.HttpClient _http;

        // Double-checked with a lock. The plain check-then-assign could construct two clients
        // concurrently; the loser is never disposed and a caller may hold a client that is about to
        // be replaced. Only one thread calls this today, so it was latent rather than live.
        static readonly object _httpLock = new object();

        static System.Net.Http.HttpClient Http()
        {
            if (_http != null) return _http;
            lock (_httpLock)
            {
                if (_http != null) return _http;
                try
                {
                    var c = new System.Net.Http.HttpClient();
                    c.Timeout = TimeSpan.FromSeconds(5);
                    _http = c;
                }
                catch { }
                return _http;
            }
        }

        static string HttpGet(string url)
        {
            try { var c = Http(); return c == null ? null : c.GetStringAsync(url).GetAwaiter().GetResult(); }
            catch { return null; }
        }

        static void IoWorker(Config cfg)
        {
            var swCtrl = Stopwatch.StartNew();
            var swPower = Stopwatch.StartNew();
            bool tvInfoRead = false;
            while (true)
            {
                try
                {
                    // ONCE at startup: record the TV firmware. The capture ABI was reverse-engineered
                    // against one build and the API exposes no version for it, so a firmware upgrade can
                    // change the param layout or the plane format SILENTLY. Recording the validated
                    // build turns that into a one-line diff in the telemetry instead of a mystery.
                    if (!tvInfoRead)
                    {
                        tvInfoRead = true;
                        try
                        {
                            string root = HttpGet("http://127.0.0.1:8001/api/v2/");
                            if (root != null)
                            {
                                _firmware = PowerStateProbe.ExtractStringValue(root, "firmwareVersion") ?? "";
                                _modelName = PowerStateProbe.ExtractStringValue(root, "modelName") ?? "";
                                // The version signal that actually works. Cross-boot comparison means an
                                // OS upgrade announces itself instead of silently changing the ABI.
                                _platform = CheckPlatformChange() ?? "unavailable";
                                L("TV: model=" + _modelName + " firmware=" + _firmware +
                                  " | capture ABI validated against firmware " + ValidatedFirmware +
                                  " - if this differs, re-verify the plane layout and param offsets");
                            }
                        }
                        catch (Exception e3) { L("tv info read failed: " + e3.GetType().Name); }
                    }
                    if (swCtrl.ElapsedMilliseconds >= ControlPollMs)
                    {
                        swCtrl.Restart();
                        string b = HttpGet(cfg.ControlUrl);
                        if (b != null) _ctrlBody = b;
                    }
                    if (swPower.ElapsedMilliseconds >= PowerPollCacheMs)
                    {
                        swPower.Restart();
                        PowerStateProbe.Result pr = PowerStateProbe.Query(cfg.PowerUrl, PowerPollTimeoutSec);
                        Interlocked.Increment(ref _powerPolls);
                        _powerRaw = pr.Raw ?? "";
                        if (pr.State == PowerStateProbe.Power.On)
                        { _powerOff = false; _powerKnown = true; _powerErr = null; }
                        else if (pr.State == PowerStateProbe.Power.Standby)
                        { _powerOff = true; _powerKnown = true; _powerErr = null; }
                        else
                        {
                            // GRACEFUL FALLBACK preserved: an unanswerable probe means "unknown", and
                            // unknown is treated as ON so a working screen is never skipped.
                            _powerOff = false; _powerKnown = true; _powerErr = pr.Error ?? "unknown";
                            Interlocked.Increment(ref _powerFails);
                        }
                    }
                }
                catch (Exception e)
                {
                    // Never silent: a throw here means a real defect (bad URL, disposed client,
                    // serialisation bug) and the worker would otherwise loop forever with no signal.
                    // Rate-limited so a persistent fault cannot flood the log.
                    if (IoErrGateOpen()) L("io worker error: " + e.GetType().Name + ": " + e.Message);
                }
                Thread.Sleep(IoWorkerTickMs);
            }
        }

        // Two threads write this rate-limiter (IoWorker AND IoPoster). A DateTime is 8 bytes, so on
        // 32-bit ARM that is a torn write; keep it as ticks and mutate with Interlocked.
        static long _lastIoErrTicks;
        static bool IoErrGateOpen()
        {
            long now = DateTime.UtcNow.Ticks;
            long prev = Interlocked.Read(ref _lastIoErrTicks);
            if (prev != 0 && (now - prev) < TimeSpan.TicksPerSecond * 5) return false;
            return Interlocked.CompareExchange(ref _lastIoErrTicks, now, prev) == prev;
        }

        /// <summary>Separate POSTER thread. A stats POST has a 10 s timeout; while it shared a thread
        /// with the control poll, a slow or dead collector stretched each worker iteration to ~17 s,
        /// so `pause` - the ONLY protection against the redeploy SIGKILL that wedges the TV scaler -
        /// could be delayed ~10 s. Control/power cadence is now independent of collector health.</summary>
        static void IoPoster(Config cfg)
        {
            while (true)
            {
                try
                {
                    string stats = null, dy = null, dc = null;
                    lock (_postLock)
                    {
                        if (_pendingPost != null) { stats = _pendingPost; _pendingPost = null; }
                        if (_pendingDumpY != null)
                        { dy = _pendingDumpY; _pendingDumpY = null; dc = _pendingDumpC; _pendingDumpC = null; }
                    }
                    if (stats != null)
                    {
                        string err = Json.Post(cfg.Endpoint + "/probe", stats, 10);
                        if (err != null) L("stats POST failed (poster): " + err);
                    }
                    if (dy != null)
                    {
                        string e1 = Json.Post(cfg.Endpoint + "/file", dy, 15);
                        L("frame dump Y " + (e1 == null ? "sent" : "FAILED: " + e1));
                        if (dc != null)
                        {
                            string e2 = Json.Post(cfg.Endpoint + "/file", dc, 15);
                            L("frame dump C " + (e2 == null ? "sent" : "FAILED: " + e2));
                        }
                    }
                }
                catch (Exception e)
                {
                    if (IoErrGateOpen()) L("io poster error: " + e.GetType().Name + ": " + e.Message);
                }
                Thread.Sleep(IoWorkerTickMs);
            }
        }

        static void ApplyControl(string body, Config cfg, Stats st)
        {
            if (body == null) return;
            foreach (string rawTok in body.Split(new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string tok = rawTok.Trim().ToLowerInvariant();
                if (tok.Length == 0) continue;
                if (tok == "pause") { _capturePaused = true; continue; }
                if (tok == "run") { _capturePaused = false; _pauseLogged = false; continue; }
                int eq = tok.IndexOf('=');
                if (eq <= 0) continue;
                string k = tok.Substring(0, eq), v = tok.Substring(eq + 1);
                double dv;
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) continue;
                // Reject NaN/Infinity. .NET parses "nan"/"infinity" under NumberStyles.Float, and
                // (int)Math.Round(NaN) is an unspecified cast that yields int.MinValue - which then
                // wraps through the modulo into a LEGAL-LOOKING but wrong mapOffset. A malformed token
                // must be ignored, not latched as permanent config.
                if (double.IsNaN(dv) || double.IsInfinity(dv)) continue;
                int iv = (int)Math.Round(dv);
                switch (k)
                {
                    case "map":  if (iv >= -4096 && iv <= 4096)
                                     cfg.MapOffset = ((iv % cfg.ZoneCount) + cfg.ZoneCount) % cfg.ZoneCount;
                                 break;
                    case "rev":  if (dv == 0 || dv == 1) cfg.MapReverse = dv != 0; break;
                    case "test": if (dv == 0 || dv == 1) cfg.TestPattern = dv != 0; break;
                    case "leds": if (iv >= 4 && iv <= 512) cfg.LedCount = iv; break;
                    // ---- tuning knobs, live. Lower alpha = MORE smoothing (longer EMA time constant).
                    case "alpha": if (dv >= 0.02 && dv <= 1.0) cfg.Alpha = dv; break;
                    case "gain":  if (dv >= 0.1 && dv <= 8.0) cfg.Gain = dv; break;
                    case "gamma": if (dv >= 1.0 && dv <= 4.0) cfg.Gamma = dv; break;
                    case "sat":   if (dv >= 0.0 && dv <= 4.0) cfg.Saturation = dv; break;
                    case "hdr":  if (dv == 0 || dv == 1) cfg.Hdr = dv != 0; break;
                    // Capture request size, live. The hardware scaler downsamples to whatever we ask
                    // for, and we only consume 73 zone averages, so a smaller request can cut the
                    // per-frame capture cost. Applied on the next capture; the zone layout rebuilds
                    // itself when the returned dims change.
                    case "cw":   if (iv >= 64 && iv <= 960) VideoPlaneCapture.SetRequestSize(iv, VideoPlaneCapture.ReqH); break;
                    case "ch":   if (iv >= 64 && iv <= 540) VideoPlaneCapture.SetRequestSize(VideoPlaneCapture.ReqW, iv); break;
                    case "gamut": if (dv == 0 || dv == 1) cfg.Gamut = dv != 0; break;
                    case "zs":   if (iv >= 1 && iv <= 8) cfg.ZoneSample = iv; break;
                    case "fps":  if (iv >= 0 && iv <= 120) cfg.TargetFps = iv; break;
                    case "zhex": if (dv == 0 || dv == 1) cfg.ZoneHexAll = dv != 0; break;
                    case "ag":   if (dv == 0 || dv == 1) cfg.AdaptiveGamma = dv != 0; break;
                    case "gmin": if (dv >= 0.5 && dv <= 3.0) cfg.GammaMin = dv; break;
                    case "gmax": if (dv >= 0.5 && dv <= 4.0) cfg.GammaMax = dv; break;
                    case "atau": if (dv >= 0.2 && dv <= 60.0) cfg.AdaptTauS = dv; break;
                    // Backend switch. The `file` path (dc_request_capture_to_file_sync) had NOT been
                    // exercised since the YUV/HDR rework, yet that rework edited it - a whole branch of
                    // shipped, unrun code. This makes it reachable without a redeploy.
                    // NOTE: the parser REQUIRES a numeric value (double.TryParse -> continue), so every
                    // token takes a NUMBER. A string-valued token is silently dropped before it reaches
                    // this switch. backend=1 selects the file path, anything else the video plane.
                    case "backend": cfg.CaptureBackend = (iv == 1) ? "file" : VideoPlaneCapture.BackendName;
                                    L("backend switched to " + cfg.CaptureBackend); break;
                    // Exercises the shutdown path on demand (see SelfTestShutdown).
                    case "shutdown": if (iv == 1) SelfTestShutdown(); break;
                    // HDR reference luminance in nits: what maps to full scale. Content varies above
                    // it and anything higher clips to WHITE, so this is the per-content knob.
                    case "peak": if (dv >= 100.0 && dv <= 4000.0) VideoPlaneCapture.HdrPeakNits = dv; break;
                    case "black": if (dv >= 0.0 && dv <= 200.0) cfg.Black = dv; break;
                    case "afast": if (dv >= 0.02 && dv <= 1.0) cfg.AlphaFast = dv; break;
                    case "acut":  if (dv >= 0.0 && dv <= 200.0) cfg.AlphaCut = dv; break;
                    case "spatial": if (dv >= 0.0 && dv <= 1.0) cfg.Spatial = dv; break;
                    case "bars":  if (dv == 0 || dv == 1) cfg.Bars = dv != 0; break;
                    case "bthr":  if (dv >= 4 && dv <= 80) cfg.BarThresh = (int)Math.Round(dv); break;
                    case "punch": if (dv >= 0.0 && dv <= 1.0) cfg.Punch = dv; break;
                    case "floor": if (dv >= 0.0 && dv <= 255.0) cfg.Floor = dv; break;
                    case "stale": if (dv >= 0.0 && dv <= 600000.0) cfg.StaleMs = dv; break;
                    case "stalevel": if (dv >= 0.0 && dv <= 1.0) cfg.StaleLevel = dv; break;
                    case "dump":  Interlocked.Exchange(ref _dumpRequested, dv != 0 ? 1 : 0); break;
                }
                st.LastMapping = "map=" + cfg.MapOffset + " rev=" + (cfg.MapReverse ? 1 : 0) +
                                 " leds=" + cfg.LedCount + " test=" + (cfg.TestPattern ? 1 : 0) +
                                 " alpha=" + cfg.Alpha.ToString("0.###", CultureInfo.InvariantCulture) +
                                 " gain=" + cfg.Gain.ToString("0.###", CultureInfo.InvariantCulture) +
                                 " gamma=" + cfg.Gamma.ToString("0.###", CultureInfo.InvariantCulture) +
                                 " sat=" + cfg.Saturation.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        static Config ReadConfig()
        {
            var c = new Config();
            // ---- Default TRUE on this device, on explicit request 2026-09-11. Change back to false
            //      before reusing this app on a strip that another ambilight already drives. ----
            c.OutputEnabled = PrefBool("outputEnabled", true);   // ENABLED 2026-09-11 on explicit request
            c.ZoneCount = PrefInt("zoneCount", 73);
            if (c.ZoneCount < 4) c.ZoneCount = 4;
            if (c.ZoneCount > 512) c.ZoneCount = 512;
            c.Alpha = PrefDouble("alpha", 0.25);
            if (c.Alpha <= 0.0) c.Alpha = 1.0;
            if (c.Alpha > 1.0) c.Alpha = 1.0;
            c.Gain = PrefDouble("gain", 1.0);
            if (c.Gain < 0.1) c.Gain = 0.1;
            if (c.Gain > 8.0) c.Gain = 8.0;
            // 1.2 = a mild perceptual lift, chosen by the user for punch. Brightness belongs HERE and
            // not in Gain: gain multiplies the top of the range and CLIPS it (gain 1.25 flattened
            // everything above zone-avg ~200 to 255 = lost highlight detail), whereas gamma lifts the
            // mid-tones and provably cannot clip. Set 1.0 for a strictly faithful transfer.
            // 1.5, not 1.2. The plane is PRE-TONE-MAP, and the TV brightens it substantially for
            // display, so a faithful linear-ish mapping always read darker than the screen. Measured on
            // a real film: 96% of its pixels sit at PQ codes 16-31 (0.09-0.28 nits), where gamma 1.2 put
            // the LED output at 0.2% - effectively off. Gamma raises those mid/low codes WITHOUT
            // touching hue or saturation, and cannot clip (the transfer already saturates by code 160).
            // This is the brightness dial; the user tunes it by eye.
            c.Gamma = PrefDouble("gamma", 1.5);
            // ADAPTIVE TONE CURVE. A single static gamma cannot suit both a film whose picture sits at
            // 0.09-0.28 nits (where 1.5 is right) and a bright game (where 1.5 over-brightens). The
            // surround-gamma literature says a darker viewing condition wants a HIGHER gamma, and that
            // principle applies directly if "surround" is taken to be the scene itself. Standard
            // practice in HDR is likewise to adapt the tone curve to the scene's AVERAGE brightness
            // (HDR10+ / SMPTE ST 2094 DMCVT), shipped as madVR's "dynamic target nits".
            c.ZoneHexAll = PrefInt("zoneHexAll", 0) != 0;
            c.AdaptiveGamma = PrefInt("adaptiveGamma", 1) != 0;
            c.GammaMin = Math.Max(0.5, Math.Min(3.0, PrefDouble("gammaMin", 1.15)));
            c.GammaMax = Math.Max(c.GammaMin, Math.Min(4.0, PrefDouble("gammaMax", 1.6)));
            // ~2 s: slow enough that the strip never visibly pumps between scenes, which is the failure
            // mode of any content-adaptive tone curve. Game engines use tau ~1.1 s for tonemapping.
            c.AdaptTauS = Math.Max(0.2, Math.Min(60.0, PrefDouble("adaptTau", 2.0)));
            if (c.Gamma < 1.0) c.Gamma = 1.0;
            if (c.Gamma > 4.0) c.Gamma = 4.0;
            // A mild crush for contrast. MUST STAY BELOW `floor`: the floor is applied BEFORE this
            // tone map, so a black level above it maps the floored value to 0 and silently switches
            // the ambient floor off (floor 10 with black 20 = no floor at all).
            // 12, not 20. The content's black level is PQ code 16 (0.087 nits) and the PQ->SDR transfer
            // maps that to ~10, so the crush only needs to remove ~10-12 to reach true zero. At 20 it was
            // also deleting the film's entire picture: 66% of its pixels sit at codes 16-23, which mapped
            // to LED 0. 12 keeps code 16 at exactly 0 (true black preserved) while letting code 24 reach
            // 17 and code 31 reach 30. Raise this only if blacks look lifted; lower it only if a genuinely
            // black frame glows.
            c.Black = PrefDouble("black", 12);
            if (c.Black < 0) c.Black = 0;
            if (c.Black > 200) c.Black = 200;
            c.AlphaCut = PrefDouble("alphaCut", 18);
            if (c.AlphaCut < 0) c.AlphaCut = 0;
            c.AlphaFast = PrefDouble("alphaFast", 0.75);
            if (c.AlphaFast < 0.02) c.AlphaFast = 0.02;
            if (c.AlphaFast > 1.0) c.AlphaFast = 1.0;
            if (c.AlphaFast < c.Alpha) c.AlphaFast = c.Alpha;
            // Raised from 0.25 to 0.50 because the blur no longer COMPOUNDS. It used to be applied
            // in place to the EMA state, so every frame re-blurred an already-blurred value and the
            // effective halo was far wider than the number suggested. Applying it once per frame on
            // an output copy (correct, and no drift) needs a bigger value for the same look.
            c.Spatial = PrefDouble("spatial", 0.50);
            if (c.Spatial < 0) c.Spatial = 0;
            if (c.Spatial > 1.0) c.Spatial = 1.0;
            c.Bars = PrefInt("bars", 1) != 0;   // ON: plane DOES carry bars (measured) - pillarbox 4:3 content
            // 18, not 24. A genuine letterbox/pillarbox bar reads the PQ black level (16, measured);
            // dark PICTURE in a film reads 21-26. 24 sat in the middle of that and cropped real
            // picture whenever a scene went dark, with adopted re-firing hundreds of times as the
            // scene brightness moved. 18 separates the two cleanly. The detector also requires FLATNESS
            // now, so this value only has to clear the black level, not every dark pixel.
            c.BarThresh = PrefInt("barThresh", 18);
            if (c.BarThresh < 17) c.BarThresh = 17;   // bars read exactly 16, and the test is `< thr`,
                                                      // so anything <= 16 matches nothing at all
            if (c.BarThresh > 80) c.BarThresh = 80;
            c.BarHold = PrefInt("barHold", 8);
            if (c.BarHold < 1) c.BarHold = 1;
            if (c.BarHold > 90) c.BarHold = 90;
            c.Punch = PrefDouble("punch", 0.15);
            if (c.Punch < 0) c.Punch = 0;
            if (c.Punch > 1.0) c.Punch = 1.0;
            c.PunchFrames = PrefInt("punchFrames", 5);
            if (c.PunchFrames < 0) c.PunchFrames = 0;
            if (c.PunchFrames > 60) c.PunchFrames = 60;
            // Must exceed `black` or the tone map erases it (see the Black note above).
            // PERMANENTLY ZERO - "no floor ever" (user decision, 2026-09-11). Do NOT restore this to a
            // non-zero default: it was originally requested as a "never fully dark" feature and was then
            // explicitly removed once true blacks mattered. A floor and true blacks are mutually
            // exclusive - the floor lifts exactly the zones that should be off.
            //
            // This is NOT a lost setting or a bug. A future session seeing 0 here has found the intended
            // value, not a regression. The ambient glow is deliberately gone.
            c.Floor = PrefDouble("floor", 0);
            if (c.Floor < 0) c.Floor = 0;
            if (c.Floor > 255) c.Floor = 255;
            c.StaleMs = PrefDouble("staleMs", 6000);
            if (c.StaleMs < 0) c.StaleMs = 0;
            c.StaleLevel = PrefDouble("staleLevel", 0.35);
            if (c.StaleLevel < 0) c.StaleLevel = 0;
            if (c.StaleLevel > 1.0) c.StaleLevel = 1.0;
            // 1.8 is a COMPENSATION, not an exaggeration, and it is not the same kind of thing as a
            // tint: scaling chroma magnitude preserves HUE and cannot create colour in a grey zone
            // (a grey zone stays grey at any saturation). What it corrects is a real dilution - per
            // zone U/V averaging is a vector mean, so mixed-hue content averages toward neutral.
            // Measured on a real frame, mean output saturation over 73 zones: 0.17 at 1.0 vs 0.31 at
            // 1.8. Set 1.0 only if you want the raw averaged value rather than the perceived colour.
            // 1.0 = AUTHENTIC, chosen by the user: no chroma exaggeration at all. The old per-channel
            // transfer INVENTED +19-35% saturation with a hue shift (a blue cast in the shadows); the
            // luma-preserving decode removed that, and the gamut conversion supplies the honest colour.
            // Raise toward 1.4 only if you want it deliberately more vivid - scaling chroma preserves
            // hue, so it stays natural-looking; above ~1.5 the soft gamut map starts pulling chroma back.
            c.Saturation = PrefDouble("saturation", 1.0);
            // HDR: the plane carries PQ-encoded HDR on this device (verified 2026-09-11 - luma ceiling
            // 148 on two unrelated contents = the PQ code for 203 nits). Set 0 only for SDR sources.
            c.Hdr = PrefInt("hdr", 1) != 0;
            c.Gamut = PrefInt("gamut", 1) != 0;
            // 3 scans ~9x fewer pixels per zone; the zone mean is unaffected at this sample count.
            // This loop was the frame-rate ceiling, so it is worth tuning live.
            c.ZoneSample = Math.Max(1, Math.Min(8, PrefInt("zoneSample", 3)));
            // RESOURCE CAP. The loop otherwise runs as fast as the TV allows (~55 fps measured), but
            // ambient light reads as continuous at ~25. Capping the rate is the largest saving
            // available at zero perceptible cost: it removes whole CAPTURE calls, which are about
            // two thirds of a frame, instead of shaving cheap arithmetic.
            c.TargetFps = Math.Max(0, Math.Min(120, PrefInt("targetFps", 25)));
            if (c.Saturation < 0.0) c.Saturation = 0.0;
            if (c.Saturation > 4.0) c.Saturation = 4.0;
            c.WhiteR = PrefDouble("whiteR", 1.0);
            c.WhiteG = PrefDouble("whiteG", 1.0);
            c.WhiteB = PrefDouble("whiteB", 1.0);
            if (c.WhiteR < 0.1) c.WhiteR = 0.1; if (c.WhiteR > 4.0) c.WhiteR = 4.0;
            if (c.WhiteG < 0.1) c.WhiteG = 0.1; if (c.WhiteG > 4.0) c.WhiteG = 4.0;
            if (c.WhiteB < 0.1) c.WhiteB = 0.1; if (c.WhiteB > 4.0) c.WhiteB = 4.0;
            c.BandFrac = PrefDouble("bandFrac", 0.08);
            if (c.BandFrac <= 0.0) c.BandFrac = 0.08;
            if (c.BandFrac > 0.5) c.BandFrac = 0.5;
            c.WledIp = PrefString("wledIp", "192.168.86.18");
            c.WledPort = PrefInt("wledPort", WledOutput.DefaultPort);
            c.WledTimeout = PrefInt("wledTimeout", WledOutput.DefaultTimeoutSeconds);
            if (c.WledTimeout < 1) c.WledTimeout = 1;
            if (c.WledTimeout > 255) c.WledTimeout = 255;
            c.Endpoint = PrefString("probeEndpoint", "http://192.168.86.101:8791").TrimEnd('/');
            c.StatsIntervalSec = PrefInt("statsIntervalSec", 5);
            if (c.StatsIntervalSec < 1) c.StatsIntervalSec = 1;
            // The local-API endpoint is steerable the same way the stats endpoint is, so the port
            // can be changed without a rebuild if a device exposes it elsewhere.
            c.PowerUrl = PrefString("powerStateUrl", PowerStateProbe.DefaultUrl);

            // ---- the request the main loop uses ----
            c.ControlUrl = PrefString("controlUrl", "http://192.168.86.101:8792/control");
            c.MapOffset = PrefInt("mapOffset", 58);   // dialled in on hardware 2026-09-11
            if (c.MapOffset < 0) c.MapOffset = 0;
            c.MapReverse = PrefInt("mapReverse", 0) != 0;
            c.LedCount = PrefInt("ledCount", 75);   // payload length: 73 zones + 2 forced black
            if (c.LedCount < 4) c.LedCount = 4;
            if (c.LedCount > 512) c.LedCount = 512;
            c.CaptureBackend = PrefString("captureBackend", VideoPlaneCapture.BackendName);
            if (c.CaptureBackend != VideoPlaneCapture.BackendName && c.CaptureBackend != "file")
                c.CaptureBackend = VideoPlaneCapture.BackendName;
            c.RequestWidth = PrefInt("requestWidth", CaptureApi.RequestWidth);
            if (c.RequestWidth < 16) c.RequestWidth = 16;
            if (c.RequestWidth > 3840) c.RequestWidth = 3840;
            c.RequestHeight = PrefInt("requestHeight", CaptureApi.RequestHeight);
            if (c.RequestHeight < 16) c.RequestHeight = 16;
            if (c.RequestHeight > 2160) c.RequestHeight = 2160;
            c.CompressType = PrefInt("compressType", CaptureApi.CompressPng);
            if (c.CompressType != CaptureApi.CompressPng && c.CompressType != CaptureApi.CompressJpeg)
                c.CompressType = CaptureApi.CompressPng;
            c.JpegQuality = PrefInt("jpegQuality", CaptureApi.JpegQuality);
            if (c.JpegQuality < 1) c.JpegQuality = 1;
            if (c.JpegQuality > 100) c.JpegQuality = 100;
            return c;
        }

        // ================================================================ run statistics

        sealed class Stats
        {
            public long CaptureOk, CaptureFail, DecodeFail, Frames;
            public long JpegFramesSkipped;      // captured a non-PNG format we cannot decode yet
            public long VideoPlaneFrames;       // frames delivered by the in-memory video-plane backend
            public string LastMapping = "";
            public double LastAlpha;
            public double LastPunch = 1.0;
            public long BarsAdopted;
            // ---- content staleness (is the picture still moving?) ----
            public int ContentSig;
            public long ContentUnchanged;
            public DateTime LastContentChangeUtc = DateTime.MinValue;
            public int BarTopNow, BarBottomNow;
            public int BarLeftNow, BarRightNow;
            public long WindowFrames;
            public long LoopExceptions;         // unexpected throws caught by the loop guard
            public long ConsecutiveFails;       // reset by any usable frame; drives the blackout
            public double SumCapture, SumDecode, SumTotal, SumZones, SumSmooth;
            public long SendOk, SendFail;
            // ---- power-state telemetry (new; the existing counters above are untouched) ----
            public string PowerState = "unknown";   // last observed: on / standby / unknown->assume_on
            public string PowerRaw = "";            // the raw PowerState token from the local API
            public long PowerPolls;                 // probes actually issued to the local API
            public long PowerProbeFail;             // probes that could not answer (assume-ON fallback)
            public long PowerSkipped;               // iterations where the capture call was SKIPPED
            public long BackoffStep;                // index into FailBackoffLadderMs; reset on success
            public string LastSendErr;
            public string LastStatus = "";
            public string LastDims = "";
            public string LastRetPath = "";
            public long LastFileBytes = -1;   // size of the file the FILE backend wrote; -1 in the video-plane
            /// <summary>Bytes of the frame that actually fed the strip. In the video-plane backend there is
            /// no file, and this was being reported as `last_file_bytes` - a memory size under a file name.</summary>
            public long FrameBytes = -1;
            public double LastDelta;
            public readonly Stopwatch Window = Stopwatch.StartNew();
            public readonly DateTime RunStart = DateTime.UtcNow;
        }

        // ================================================================ entry

        public static void Run()
        {
            Config cfg = ReadConfig();
            _cfg = cfg;
            CaptureFileName = cfg.CompressType == CaptureApi.CompressJpeg ? "amb.jpg" : "amb.png";

            L("=== " + AppId + " " + Version + " starting ===");
            L(RecordedMeasurement);
            L("capture control channel: " + cfg.ControlUrl + " (body containing 'pause' stops capture calls)");
            L("brightness shaping: gain=" + cfg.Gain + " gamma=" + cfg.Gamma +
              " saturation=" + cfg.Saturation +
              " white=" + cfg.WhiteR + "/" + cfg.WhiteG + "/" + cfg.WhiteB);
            L("outputEnabled=" + cfg.OutputEnabled + ", zones=" + cfg.ZoneCount +
              ", alpha=" + cfg.Alpha + ", bandFrac=" + cfg.BandFrac);
            L("main-loop capture request " + cfg.RequestWidth + "x" + cfg.RequestHeight +
              " compress_type=" + cfg.CompressType +
              (cfg.CompressType == CaptureApi.CompressJpeg ? " (JPEG q" + cfg.JpegQuality + ")" : " (PNG)"));
            L("power-state probe: GET " + cfg.PowerUrl + " cached " + PowerPollCacheMs +
              " ms (timeout " + PowerPollTimeoutSec + " s); panel standby => capture SKIPPED, sleep " +
              OffSleepMs + " ms; probe failure => ASSUME ON (fallback)");

            // ---- (0) PNG decoder self-check, run on the device ----
            string selfTest = PngSelfTest.Run();
            L("png decoder self-test: " + selfTest);

            // ---- (1) resolve the capture library ----
            if (cfg.CaptureBackend == VideoPlaneCapture.BackendName)
            {
                string vErr = VideoPlaneCapture.Init();
                if (vErr != null) L("VIDEO-PLANE backend NOT ready: " + vErr +
                                    " (set preference captureBackend=file to fall back)");
                else L("video-plane backend ready: " + VideoPlaneCapture.ResolvedPath + " @ 0x" +
                       VideoPlaneCapture.FunctionPointer.ToInt64().ToString("x") +
                       " -> " + VideoPlaneCapture.PlaneW + "x" + VideoPlaneCapture.PlaneH +
                       " NV12 in memory, NO file");
            }
            string initErr = CaptureApi.Init();
            if (initErr != null) L("capture library NOT ready: " + initErr);
            else L("capture library ready: " + CaptureApi.ResolvedPath + " @ 0x" +
                   CaptureApi.FunctionPointer.ToInt64().ToString("x"));

            // ---- (2) writable directory discovery ----
            string captureDir = null;
            Dictionary<string, object> discovery = null;
            var dirChecks = new List<object>();
            try
            {
                List<string> cands = CaptureApi.CandidateDirs(out discovery);
                L("dir candidates: " + cands.Count);
                foreach (string d in cands)
                {
                    string detail;
                    bool ok = CaptureApi.WriteTest(d, out detail);
                    dirChecks.Add(new Dictionary<string, object>
                    {
                        { "dir", d }, { "writable", ok }, { "detail", detail }
                    });
                    L("  dir " + d + " -> writable=" + ok + " (" + detail + ")");
                    if (ok) { captureDir = d; break; }
                }
            }
            catch (Exception e)
            {
                L("dir discovery threw " + e.GetType().Name + ": " + e.Message);
            }
            if (captureDir == null)
            {
                captureDir = "/tmp";
                L("no candidate passed the write test; falling back to " + captureDir);
            }
            L("CAPTURE DIR = " + captureDir + "   file = " + CaptureFileName +
              "  (this app's OWN data dir; the probe has its own separate id)");
            if (discovery == null) discovery = new Dictionary<string, object>();
            discovery["write_tests"] = dirChecks;
            discovery["chosen_capture_dir"] = captureDir;

            var stats = new Stats();

            if (initErr != null)
            {
                Post(cfg, BuildReport(cfg, selfTest, initErr, captureDir, discovery, "no_library", stats));
                L("cannot run the main loop without the capture library; idling and retrying every 60 s");
                while (true)
                {
                    Thread.Sleep(60000);
                    string again = CaptureApi.Init();
                    if (again == null) { L("capture library became available"); MainLoop(cfg, selfTest, captureDir, discovery, stats); return; }
                }
            }

            // ---- (3) the one-off startup POST: decoder verdict + directory discovery ----
            Post(cfg, BuildReport(cfg, selfTest, null, captureDir, discovery, "started", stats));

            // ---- (4) the real loop ----
            MainLoop(cfg, selfTest, captureDir, discovery, stats);
        }

        // ================================================================ shutdown / blackout

        /// <summary>Called from the framework terminate callback. Blanks the strip so it is never
        /// left showing a stale frame. Does nothing unless output is enabled. Never throws.</summary>
        /// <summary>Set by OnShutdown (framework thread) and polled by the render loop. Without it the
        /// loop could send one more colour packet AFTER the shutdown blackout - last packet wins, so
        /// the strip would be left latched on a colour, which is the exact opposite of this path's
        /// purpose.</summary>
        static volatile bool _stopping;

        /// <summary>Persist the log tail. The fatal handler calls this so the last lines survive a
        /// crash - they are usually the only evidence of what went wrong.</summary>
        /// <summary>Exercise the shutdown path on demand. `OnTerminate` has NEVER been observed firing
        /// on this device - the app is always SIGKILLed by `tizen install` - so the blackout, the log
        /// flush and the buffer release were all written-but-unexercised. This runs the identical code
        /// path so it can be watched.</summary>
        public static void SelfTestShutdown()
        {
            L("SELFTEST: invoking the shutdown path deliberately");
            try { OnShutdown(); }
            catch (Exception e) { L("selftest shutdown threw: " + e.GetType().Name + ": " + e.Message); }
            try { FlushLog(); } catch { }
            // SHIP the shutdown evidence. FlushLog writes to the TV's own disk, which is unreachable -
            // `sdb shell` is blocked on this firmware - and the app exits BEFORE the next 5 s stats post,
            // so the shutdown lines were being written and never seen. A bounded synchronous post (3 s
            // timeout) makes them visible without risking a hang on a dead collector.
            try
            {
                List<string> tail;
                lock (_logLock) { int take = Log.Count < 12 ? Log.Count : 12;
                                  tail = Log.GetRange(Log.Count - take, take); }
                var rep = new Dictionary<string, object>
                {
                    { "app", AppId }, { "version", Version }, { "phase", "shutdown" },
                    { "ts_utc", DateTime.UtcNow.ToString("o") },
                    { "reason", "SelfTestShutdown / normal terminate" },
                    { "uptime_s", (DateTime.UtcNow - ProcessStartUtc).TotalSeconds },
                    { "log", new List<object>(tail) },
                };
                string err = Json.Post(_cfg != null ? _cfg.Endpoint + "/probe" : "", Json.ToJson(rep), 3);
                Console.WriteLine("[amb] shutdown report -> " + (err ?? "OK"));
            }
            catch { }
            Thread.Sleep(300);                      // let the blackout datagram leave
            L("SELFTEST: shutdown path complete, exiting");
            Environment.Exit(0);
        }

        public static void FlushLog()
        {
            try
            {
                string dir = "/home/owner/ambilight";
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                List<string> snap;
                lock (_logLock) { snap = new List<string>(Log); }
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "last.log"), snap.ToArray());
            }
            catch { }
        }

        public static void OnShutdown()
        {
            _stopping = true;
            // Give the render loop a moment to observe the flag and stop sending, so our blackout is
            // genuinely the last datagram. The loop's iteration is tens of ms; 200 ms is ample.
            try { Thread.Sleep(200); } catch { }
            Config c = _cfg;
            if (c == null || !c.OutputEnabled) return;
            try
            {
                string err = WledOutput.SendBlackout(c.WledIp, c.WledPort, c.LedCount);
                L("shutdown blackout (" + c.LedCount + " LEDs) -> " + (err == null ? "OK" : err));
                VideoPlaneCapture.Shutdown();      // free the plane buffers on a clean exit
            }
            catch (Exception e)
            {
                L("shutdown blackout threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>A run of consecutive frame-less iterations means the strip would sit latched on
        /// the last good frame, so blank it. Fires ONCE per streak — at exactly
        /// <see cref="BlankAfterNoFrameMs"/> of no usable frame —
        /// and only when output is enabled. Never throws.</summary>
        static DateTime _lastGoodFrameUtc = DateTime.MinValue;
        static bool _blankedForStall;
        /// <summary>Last time a stall blackout was sent, so it can be re-sent before WLED's live-mode
        /// timeout expires and it reverts to its own default colour.</summary>
        static DateTime _lastBlackoutUtc = DateTime.MinValue;
        /// <summary>Must stay WELL UNDER the blackout packet's timeout byte (WledOutput
        /// .BlackoutTimeoutSeconds) or WLED un-latches between sends and flashes its own colour.</summary>
        const int BlackoutHoldMs = 1000;

        /// <summary>Mark that a usable frame just arrived. Called from BOTH capture backends: it
        /// previously ran only on the video-plane path, so with captureBackend=file MaybeBlackout()
        /// short-circuited on DateTime.MinValue forever and the strip was NEVER blanked after a stall.</summary>
        static void NoteGoodFrame()
        {
            _lastGoodFrameUtc = DateTime.UtcNow;
            _blankedForStall = false;
            _lastBlackoutUtc = DateTime.MinValue;   // frames are back: no need to hold
        }

        /// <summary>Cheap content signature over whatever buffer the backend produced, so the stale
        /// fade works on both backends (it too was video-plane-only, leaving a frozen picture holding
        /// the strip lit forever on the file path).</summary>
        static void NoteContent(byte[] buf, Stats st)
        {
            if (buf == null || buf.Length == 0) return;
            int sig = 17;
            for (int i = 0; i < buf.Length; i += 157) sig = unchecked(sig * 31 + buf[i]);
            if (sig == st.ContentSig) { st.ContentUnchanged++; }
            else
            {
                st.ContentSig = sig;
                st.ContentUnchanged = 0;
                st.LastContentChangeUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Send a stall/standby blackout, rate-limited to BlackoutHoldMs.
        ///
        /// WLED does not stay on our black by itself: the datagram carries a timeout byte, after which
        /// WLED leaves live mode and reverts to its OWN segment colour (default WLED ORANGE). Anything
        /// that blanks ONCE and then stops therefore turns orange a few seconds later. This is the one
        /// place that knows how to keep the strip latched to black, and BOTH the stall path and the
        /// standby path must go through it for as long as the blank state lasts.</summary>
        /// <summary>Target gamma for a given scene brightness.
        ///
        /// Anchored on the surround-gamma rule that a darker viewing condition calls for a higher gamma
        /// - the Philips patent states "a dark surround requires a gamma of about 1.5", and the
        /// (secondhand) De Marsh figures run 1.0 bright / 1.2 dim / 1.5 dark. Our "surround" is the
        /// scene itself, so a dark scene takes the top of the range and a bright one the bottom.
        ///
        /// Interpolation is linear in PQ code, which is roughly logarithmic in nits (PQ is itself a
        /// log-ish encoding), so the adaptation is perceptually even rather than linear in luminance.
        /// 16 is the black level and 120 is a comfortably bright scene on this panel.</summary>
        static double SceneGammaTarget(int medianCode, Config cfg)
        {
            double t = (medianCode - 16.0) / 104.0;
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            double g = cfg.GammaMax - (cfg.GammaMax - cfg.GammaMin) * t;
            if (g < cfg.GammaMin) g = cfg.GammaMin;
            if (g > cfg.GammaMax) g = cfg.GammaMax;
            return g;
        }

        static void HoldBlackout(Config cfg, string why)
        {
            if (!cfg.OutputEnabled) return;
            if (_lastBlackoutUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _lastBlackoutUtc).TotalMilliseconds < BlackoutHoldMs) return;

            bool firstBlank = !_blankedForStall;
            _lastBlackoutUtc = DateTime.UtcNow;
            _blankedForStall = true;
            string err = WledOutput.SendBlackout(cfg.WledIp, cfg.WledPort, cfg.LedCount);
            if (firstBlank)
            {
                if (err == null) L(why);
                else L("blackout send failed: " + err);
            }
        }

        /// <summary>Blank once the picture has been gone for BlankAfterNoFrameMs. Idempotent: it fires
        /// exactly once per stall and re-arms automatically when frames return.</summary>
        static void MaybeBlackout(Config cfg, Stats st)
        {
            if (!cfg.OutputEnabled) return;
            if (_lastGoodFrameUtc == DateTime.MinValue) return;
            double ageMs = (DateTime.UtcNow - _lastGoodFrameUtc).TotalMilliseconds;
            if (ageMs < BlankAfterNoFrameMs) return;

            // HOLD THE BLACK, do not fire once and stop.
            //
            // The blackout datagram carries a TIMEOUT byte: WLED un-latches that many seconds after the
            // last packet we send, leaves live mode, and reverts to its OWN segment colour - which is
            // the default WLED ORANGE (255,160,0). Sending a single blackout therefore produced a strip
            // that went black for about a second and then glowed orange for the entire stall. Observed
            // live: 46 s of rc=-1 (playback stopped, back to a menu) with the whole ring orange, and
            // WLED reporting live=False with no source IP - i.e. it was not us driving it at all.
            //
            // Re-sending inside the timeout keeps WLED latched to OUR black for as long as the stall
            // lasts. Cheap: one small UDP packet per second while stalled, and none at all while frames
            // are flowing.
            if (_lastBlackoutUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _lastBlackoutUtc).TotalMilliseconds < BlackoutHoldMs) return;

            HoldBlackout(cfg, "strip blanked: no usable frame for " + (long)ageMs + " ms");
        }

        /// <summary>Blank the strip the instant the panel is first seen going to standby. The screen
        /// is already dark, so a frame left latched on the strip is exactly the stale-light case
        /// <see cref="WledOutput.SendBlackout"/> exists to prevent — this is a THIRD blackout path
        /// alongside the consecutive-failure blackout and <see cref="OnShutdown"/>. It fires once per
        /// on->off transition (the loop guards it), only when output is enabled, and never throws.</summary>
        static void BlackoutForStandby(Config cfg)
        {
            if (!cfg.OutputEnabled) return;
            try
            {
                string err = WledOutput.SendBlackout(cfg.WledIp, cfg.WledPort, cfg.LedCount);
                if (err == null) L("panel on->standby: strip blanked (" + cfg.ZoneCount + " LEDs)");
                else L("panel on->standby blackout failed: " + err);
            }
            catch (Exception e)
            {
                L("panel on->standby blackout threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ================================================================ main loop

        static void MainLoop(Config cfg, string selfTest, string captureDir,
                             Dictionary<string, object> discovery, Stats st)
        {
            ZoneLayout layout = null;
            ZoneColors smoothed = null;

            // ---- power-state gate state: cache + on->off transition detection ----
            bool powerIsOff = false;    // last decision: the panel is in standby, skip captures
            bool powerWasOff = false;   // drives the one-shot blank on the on->off transition

            L("=== main loop start (output " + (cfg.OutputEnabled ? "ENABLED" : "DISABLED — statistics only") +
              ", request " + cfg.RequestWidth + "x" + cfg.RequestHeight + " compress_type=" + cfg.CompressType + ") ===");

            // Network I/O on its own thread from here on, started before the loop so the first power
            // decision lands almost immediately.
            var ioThread = new Thread(() => IoWorker(cfg));
            ioThread.IsBackground = true;
            ioThread.Start();
            var postThread = new Thread(() => IoPoster(cfg));
            postThread.IsBackground = true;
            postThread.Start();

            // ---- letterbox / punch state, persisted across loop iterations ----
            int barTop = 0, barBottom = -1;
            int barLeft = 0, barRight = -1;   // pillarbox, same convention (-1 = full width)          // currently ADOPTED active area (-1 = full frame)
            int candTop = -1, candBottom = -1, candCount = 0;
            int candL = -1, candR = -1, candCountX = 0;      // pillarbox candidates
            int punchLeft = 0;
            int standbyEpisode = 0;     // iterations in the CURRENT standby episode
            DateTime lastFrameUtc = DateTime.UtcNow;
            DateTime lastMeterUtc = DateTime.UtcNow;
            bool gammaInit = false;
            int lastW = -1, lastH = -1;
            ZoneColors shaped = null;   // per-frame output copy; keeps the EMA state uncontaminated
            double staleDim = 1.0;      // ramped dim factor for frozen content

            while (true)
            {
                try
                {
                    // ---- 0. power-state gate (top of loop) ----
                    // The decision is CACHED for PowerPollCacheMs so the loop is not HTTP-polling the
                    // local API every iteration. The probe sits OUTSIDE the frame's Stopwatch region
                    // (fsw starts immediately before capture, exactly as before), so its cost cannot
                    // contaminate mean_total_frame_ms and the fps stays comparable.
                    // Latest power decision from the worker thread - no network call here at all.
                    if (_powerKnown)
                    {
                        st.PowerPolls = Interlocked.Read(ref _powerPolls);
                        st.PowerProbeFail = Interlocked.Read(ref _powerFails);
                        st.PowerRaw = _powerRaw ?? "";
                        if (_powerOff) { powerIsOff = true; st.PowerState = "standby"; }
                        else if (_powerErr != null) { powerIsOff = false; st.PowerState = "unknown->assume_on"; }
                        else { powerIsOff = false; st.PowerState = "on"; }
                    }
                    else
                    {
                        powerIsOff = false;            // no answer yet: assume ON, never skip a screen
                        st.PowerState = "unknown->assume_on";
                    }

                    if (powerIsOff)
                    {
                        // Panel standby: there is nothing to capture, and hammering the capture API
                        // while it is off burns the very call budget the ON path needs. Skip
                        // CaptureApi.Capture ENTIRELY and sleep long.
                        st.PowerSkipped++;
                        // HOLD the black for the WHOLE standby, not just the on->off transition. The
                        // old code blanked once here and then slept in a loop without re-sending, so
                        // once WLED's timeout lapsed it flashed its own default ORANGE and stayed that
                        // way for the entire standby - a strip glowing orange while the TV is off.
                        // Rate-limited internally, so calling it every iteration is safe.
                        if (!powerWasOff) powerWasOff = true;
                        HoldBlackout(cfg, "panel on->standby: strip blanked, holding black (" +
                                          cfg.LedCount + " LEDs)");
                        // ADAPTIVE standby sleep. The first ~20 iterations use the SHORT sleep so a
                        // panel that is merely transitioning (source switch, video closing) resumes
                        // capture within ~0.5 s instead of the old flat 4 s. Only once standby looks
                        // genuinely settled do we back off and stop polling hard.
                        // Per-EPISODE counter. This used to test st.PowerSkipped, which is incremented
                        // for the life of the process and never reset - so after the first standby
                        // episode passed 20 iterations, EVERY later brief off slept the long 3 s and
                        // the documented "resume within ~0.5 s" never applied again.
                        standbyEpisode++;
                        int standbySleep = (standbyEpisode <= 20) ? OffSleepMs : OffSleepIdleMs;
                        if (st.PowerSkipped == 1 || st.PowerSkipped % 10 == 0)
                            L("panel standby: capture SKIPPED (" + st.PowerSkipped +
                              " total), sleeping " + standbySleep + " ms");
                        MaybePost(cfg, captureDir, layout, smoothed, st);
                        Thread.Sleep(standbySleep);
                        continue;
                    }
                    powerWasOff = false;   // panel is on (or assumed on): captures run as before
                    standbyEpisode = 0;    // leave standby: the next episode gets the fast sleep again

                    if (_stopping)
                    {
                        L("stopping: render loop exiting so the shutdown blackout stands");
                        return;
                    }

                    // ---- control channel, BEFORE the backend branch ----
                    // Hoisted out of the video-plane branch: it used to live inside it, so with
                    // captureBackend=file the worker still fetched the body but nothing parsed it -
                    // `pause` was inert and every live tuning token was a silent no-op. That matters
                    // most for pause, which is the only protection against the redeploy SIGKILL that
                    // wedges the TV scaler.
                    // Applied ONLY WHEN IT CHANGES: ApplyControl parses strings and rebuilds
                    // LastMapping with several invariant ToString calls, so running it per frame
                    // allocated fresh strings for nothing.
                    {
                        string ctrl = _ctrlBody;
                        if (ctrl != null && !string.Equals(ctrl, _appliedCtrl, StringComparison.Ordinal))
                        {
                            _appliedCtrl = ctrl;
                            ApplyControl(ctrl, cfg, st);
                        }
                        if (_capturePaused)
                        {
                            if (!_pauseLogged)
                            {
                                L("CAPTURE PAUSED by control channel - no native calls are being made; " +
                                  "safe to redeploy. Clear the control body and restart to resume.");
                                _pauseLogged = true;
                            }
                            st.LastStatus = "paused by control channel (no capture calls)";
                            MaybePost(cfg, captureDir, layout, smoothed, st);
                            Thread.Sleep(1000);
                            continue;
                        }
                    }

                    var fsw = Stopwatch.StartNew();

                    // ---- 0. VIDEO-PLANE backend: in memory, no file, no encoder, no decoder ----
                    PngImage img = null;
                    ZoneColors raw = null;
                    double capMs = 0, decMs = 0, zMs = 0;
                    string dims = null;
                    if (cfg.CaptureBackend == VideoPlaneCapture.BackendName)
                    {
                        // checked every 30 frames, BEFORE the capture call, so a redeploy sees the
                        // app holding no capture in flight
                        lastFrameUtc = DateTime.UtcNow;
                        VideoPlaneResult vr = VideoPlaneCapture.Capture();
                        st.LastStatus = vr.Status;
                        capMs = vr.CaptureMs;
                        if (!vr.HasFrame)
                        {
                            st.CaptureFail++;
                            st.ConsecutiveFails++;
                            st.LastRetPath = "(in-memory video plane)";
                            if (st.CaptureFail % 25 == 1) L("no video-plane frame: " + vr.Status);
                            MaybePost(cfg, captureDir, layout, smoothed, st);
                            MaybeBlackout(cfg, st);
                            int vSleepMs = BackoffSleep(st);
                            BackoffAdvance(st);
                            Thread.Sleep(vSleepMs);
                            continue;
                        }
                        st.CaptureOk++;
                        st.ConsecutiveFails = 0;
                        st.BackoffStep = 0;
                        NoteGoodFrame();
                        NoteContent(vr.Y, st);

                        // ---- adaptive tone curve ----------------------------------------------
                        // One strided histogram pass (~3.6k of 518k pixels, no sort) then an EMA toward
                        // the gamma that suits this scene's brightness.
                        _lastMedianCode = VideoPlaneCapture.MedianLuma(vr.Y, vr.W, vr.H);
                        double wantGamma = SceneGammaTarget(_lastMedianCode, cfg);
                        DateTime nowM = DateTime.UtcNow;
                        if (!gammaInit)
                        {
                            _adaptGamma = wantGamma;     // first frame: adopt immediately, no glide
                            gammaInit = true;
                        }
                        else
                        {
                            double dts = (nowM - lastMeterUtc).TotalSeconds;
                            if (dts <= 0.0 || dts > 2.0) dts = 1.0 / Math.Max(1, cfg.TargetFps > 0 ? cfg.TargetFps : 25);
                            double a = 1.0 - Math.Exp(-dts / Math.Max(0.05, cfg.AdaptTauS));
                            _adaptGamma += (wantGamma - _adaptGamma) * a;
                        }
                        lastMeterUtc = nowM;
                        st.LastRetPath = "(in-memory video plane)";
                        // ---- one-shot frame dump: lets us actually LOOK at what feeds the strip.
                        // Answers things no statistic can: is the picture bar-free in OUR plane (i.e.
                        // are the bars added downstream), what is the real geometry, is the image sane.
                        if (Interlocked.Exchange(ref _dumpRequested, 0) == 1)
                        {
                            try
                            {
                                // Build the payloads here (pure CPU) and let the POSTER thread send
                                // them. These used to be two synchronous 30 s-timeout POSTs INLINE on
                                // the frame thread, so a slow collector froze the strip for up to 60 s
                                // and corrupted that sample's frame timing.
                                var d = new Dictionary<string, object>
                                {
                                    { "name", "appframe_Y" }, { "part", (long)1 }, { "parts", (long)1 },
                                    { "width", (long)vr.W }, { "height", (long)vr.H },
                                    { "data", Convert.ToBase64String(vr.Y) },
                                };
                                var d2 = new Dictionary<string, object>
                                {
                                    { "name", "appframe_C" }, { "part", (long)1 }, { "parts", (long)1 },
                                    { "data", Convert.ToBase64String(vr.C) },
                                };
                                lock (_postLock) { _pendingDumpY = Json.ToJson(d); _pendingDumpC = Json.ToJson(d2); }
                                L("frame dump queued for the poster (Y " + vr.Y.Length + "B, C " + vr.C.Length + "B)");
                            }
                            catch (Exception de) { L("frame dump exception: " + de.GetType().Name + ": " + de.Message); }
                        }

                        // No file exists in this backend. Report the frame size under its own key and keep
                        // last_file_bytes honest (-1 = not applicable) instead of putting a memory size there.
                        st.FrameBytes = (long)(vr.Y.Length + vr.C.Length);
                        st.LastFileBytes = -1;
                        // Build the string only when the dimensions ACTUALLY change (it was allocated
                        // every frame purely to test for a change that almost never happens).
                        // dims must RETAIN its previous value: the layout test below compares it against
                        // st.LastDims, so clearing it would make that comparison fail on every frame and
                        // rebuild the whole zone layout - 73 objects - on every frame.
                        if (vr.W != lastW || vr.H != lastH) { lastW = vr.W; lastH = vr.H; dims = vr.W + "x" + vr.H; }
                        st.VideoPlaneFrames++;

                        // Zone colours straight from the planes. No RGBA frame is ever materialised:
                        // the YUV average and the colour matrix now happen once per ZONE, not once
                        // per pixel.
                        if (layout == null || layout.Total != cfg.ZoneCount || st.LastDims != dims)
                        {
                            layout = new ZoneLayout(vr.W, vr.H, cfg.ZoneCount, cfg.BandFrac);
                            smoothed = null;
                            st.LastDims = dims;
                            L("zone layout: " + layout.Describe());
                        }
                        // ---- letterbox detection, with hysteresis ----
                        if (cfg.Bars)
                        {
                            // ---- VERTICAL (letterbox) ----
                            int dTop, dBottom;
                            VideoPlaneCapture.DetectActiveArea(vr.Y, vr.W, vr.H, cfg.BarThresh,
                                                               out dTop, out dBottom);
                            if (Math.Abs(dTop - candTop) <= 2 && Math.Abs(dBottom - candBottom) <= 2)
                            {
                                candCount++;
                            }
                            else
                            {
                                candTop = dTop; candBottom = dBottom; candCount = 1;
                            }
                            // Only adopt once the same area has held for BarHold frames, or the bars
                            // "breathe" on every cut to a bright or dark shot.
                            // ASYMMETRIC hysteresis: cropping is adopted quickly (a film starting is a
                            // real, persistent event) but GIVING THE CROP BACK needs a much longer run,
                            // or a bright cut inside a letterboxed film reads as "bars gone" and the
                            // strip twitches. Measured on hardware as adopted=7 flip-flops.
                            bool reducingBars = (candTop < barTop) || (candBottom > barBottom);
                            int needHold = reducingBars ? cfg.BarHold * 6 : cfg.BarHold;
                            if (candCount >= needHold &&
                                (candTop != barTop || candBottom != barBottom))
                            {
                                barTop = candTop; barBottom = candBottom;
                                st.BarTopNow = barTop; st.BarBottomNow = barBottom;
                                st.BarsAdopted++;
                                L("letterbox: active rows " + barTop + ".." + barBottom +
                                  " (" + (barBottom - barTop) + " of " + vr.H + " px)" +
                                  (barTop > 0 || barBottom < vr.H ? " - bars" : " - full frame"));
                            }

                            // ---- HORIZONTAL (pillarbox) ----
                            // This did not exist. Only ROWS were scanned, so a pillarboxed frame was
                            // invisible to the detector: the left/right bands sampled pure black and
                            // the top/bottom bands averaged across ~20% dead pixels. Measured on a real
                            // 4:3-in-16:9 frame: 94 black columns each side.
                            int dL, dR;
                            VideoPlaneCapture.DetectActiveAreaX(vr.Y, vr.W, vr.H, cfg.BarThresh,
                                                                out dL, out dR);
                            if (Math.Abs(dL - candL) <= 2 && Math.Abs(dR - candR) <= 2)
                            {
                                candCountX++;
                            }
                            else
                            {
                                candL = dL; candR = dR; candCountX = 1;
                            }
                            bool reducingX = (candL < barLeft) || (candR > barRight);
                            int needHoldX = reducingX ? cfg.BarHold * 6 : cfg.BarHold;
                            if (candCountX >= needHoldX && (candL != barLeft || candR != barRight))
                            {
                                barLeft = candL; barRight = candR;
                                st.BarLeftNow = barLeft; st.BarRightNow = barRight;
                                st.BarsAdopted++;
                                L("pillarbox: active cols " + barLeft + ".." + barRight +
                                  " (" + (barRight - barLeft) + " of " + vr.W + " px)" +
                                  (barLeft > 0 || barRight < vr.W ? " - bars" : " - full width"));
                            }
                        }
                        else
                        {
                            barTop = 0; barBottom = -1;
                            barLeft = 0; barRight = -1;
                            st.BarTopNow = 0; st.BarBottomNow = 0;
                            st.BarLeftNow = 0; st.BarRightNow = 0;
                        }

                        var zswv = Stopwatch.StartNew();
                        raw = layout.ExtractYuv(vr.Y, vr.C, vr.W, vr.H,
                                                cfg.Saturation, cfg.WhiteR, cfg.WhiteG, cfg.WhiteB, cfg.Hdr, cfg.Gamut,
                                                vr.YStride, vr.CStride, cfg.ZoneSample,
                                                barTop, barBottom < 0 ? vr.H : barBottom,
                                                barLeft, barRight < 0 ? vr.W : barRight);
                        zswv.Stop();
                        zMs = zswv.Elapsed.TotalMilliseconds;

                        if (st.VideoPlaneFrames == 1)
                            L("video-plane frame 1: " + dims + " ret " + vr.RetWidth + "x" + vr.RetHeight +
                              " pitches Y=" + vr.YPitch + " C=" + vr.CPitch + " strides " + vr.YStride + "/" + vr.CStride +
                              " request=" + VideoPlaneCapture.ReqW + "x" + VideoPlaneCapture.ReqH +
                              " interlaced=" + vr.Interlaced +
                              " capture=" + capMs.ToString("0.0") + "ms zones=" + zMs.ToString("0.0") +
                              "ms sat=" + cfg.Saturation +
                              " wb=" + cfg.WhiteR + "/" + cfg.WhiteG + "/" + cfg.WhiteB);
                        goto smoothStage;
                    }

                    // ---- 1. capture ----
                    var csw = Stopwatch.StartNew();
                    CaptureResult res = CaptureApi.Capture(captureDir, CaptureFileName,
                                                           cfg.RequestWidth, cfg.RequestHeight,
                                                           cfg.CompressType, cfg.JpegQuality);
                    csw.Stop();
                    capMs = csw.Elapsed.TotalMilliseconds;

                    st.LastStatus = res.Status;
                    if (!res.HasFrame)
                    {
                        // rc=1 with an empty ret_path is the documented screen-off / standby case.
                        // Skip the frame; it is not fatal.
                        st.CaptureFail++;
                        st.ConsecutiveFails++;
                        st.LastRetPath = res.RetPath ?? "";
                        if (st.CaptureFail % 25 == 1) L("no frame: " + res.Status);
                        MaybePost(cfg, captureDir, layout, smoothed, st);
                        MaybeBlackout(cfg, st);
                        int failSleepMs = BackoffSleep(st);
                        BackoffAdvance(st);
                        Thread.Sleep(failSleepMs);
                        continue;
                    }

                    st.CaptureOk++;
                    st.ConsecutiveFails = 0;
                    st.BackoffStep = 0;   // first usable frame: reset the ON-but-failing ladder
                    NoteGoodFrame();      // so the stall blackout arms on this backend too
                    st.LastRetPath = res.RetPath;
                    st.LastFileBytes = res.FileBytes;
                    st.FrameBytes = res.FileBytes;

                    // ---- 1b. a non-PNG capture has no decoder wired into this loop. It is reported
                    //          rather than spammed as a decode failure; compressType=0 restores the
                    //          full PNG pipeline.
                    if (cfg.CompressType != CaptureApi.CompressPng)
                    {
                        st.JpegFramesSkipped++;
                        st.ConsecutiveFails++;
                        if (st.JpegFramesSkipped == 1 || st.JpegFramesSkipped % 25 == 0)
                            L("compressType=" + cfg.CompressType + ": no decoder wired into the loop for this " +
                              "format — " + st.JpegFramesSkipped + " frame(s) skipped. " +
                              "Set compressType=0 to run the PNG pipeline.");
                        MaybePost(cfg, captureDir, layout, smoothed, st);
                        MaybeBlackout(cfg, st);
                        int failSleepMs = BackoffSleep(st);
                        BackoffAdvance(st);
                        Thread.Sleep(failSleepMs);
                        continue;
                    }

                    // ---- 2. decode the PNG ourselves ----
                    var dsw = Stopwatch.StartNew();
                    try
                    {
                        img = PngDecoder.DecodeFile(res.RetPath);
                    }
                    catch (Exception e)
                    {
                        st.DecodeFail++;
                        st.ConsecutiveFails++;
                        st.LastStatus = "decode_fail: " + e.GetType().Name + ": " + e.Message;
                        if (st.DecodeFail % 10 == 1) L(st.LastStatus);
                        MaybePost(cfg, captureDir, layout, smoothed, st);
                        MaybeBlackout(cfg, st);
                        int failSleepMs = BackoffSleep(st);
                        BackoffAdvance(st);
                        Thread.Sleep(failSleepMs);
                        continue;
                    }
                    dsw.Stop();
                    decMs = dsw.Elapsed.TotalMilliseconds;

                    // Feed the stale fade on this backend too: the signature was video-plane-only,
                    // so a frozen picture held the strip lit forever when captureBackend=file.
                    NoteContent(img.Rgba, st);

                    // ---- 3. zone colours from the real image ----
                    dims = img.Width + "x" + img.Height;
                    if (layout == null || layout.Total != cfg.ZoneCount || st.LastDims != dims)
                    {
                        layout = new ZoneLayout(img.Width, img.Height, cfg.ZoneCount, cfg.BandFrac);
                        smoothed = null;
                        st.LastDims = dims;
                        L("zone layout: " + layout.Describe());
                    }

                    var zsw = Stopwatch.StartNew();
                    raw = layout.Extract(img);
                    zsw.Stop();
                    zMs = zsw.Elapsed.TotalMilliseconds;

                smoothStage:
                    // ---- 4. temporal smoothing ----
                    var ssw = Stopwatch.StartNew();

                    // ADAPTIVE TEMPORAL SMOOTHING. A fixed alpha forces a choice between smooth and
                    // reactive. Measuring how far the new frame is from the running average lets us
                    // have both: small changes (motion, grain) get the slow alpha and stay silky,
                    // while a large change (scene cut, flash) snaps toward the new colour instead of
                    // smearing through it. Delta is taken BEFORE smoothing, against the old average.
                    double frameDelta = (smoothed == null) ? 0.0 : Colors.MeanDelta(smoothed, raw);
                    double useAlpha = (frameDelta > cfg.AlphaCut) ? cfg.AlphaFast : cfg.Alpha;

                    if (smoothed == null)
                    {
                        smoothed = new ZoneColors(raw.Count);
                        smoothed.CopyFrom(raw);
                    }
                    else
                    {
                        smoothed.SmoothTowards(raw, useAlpha);
                    }

                    // SPATIAL and FLOOR are OUTPUT shaping and are applied to a COPY, leaving the EMA
                    // state pure. Mutating `smoothed` in place polluted it three ways:
                    //  1. the spatial blur COMPOUNDED - each frame blurred an already-blurred state, so
                    //     the effective blur was far stronger than cfg.Spatial and drifted over time;
                    //  2. the floor lifted the state, so the NEXT frame's frameDelta measured
                    //     |floor - raw|; whenever floor > cfg.AlphaCut that latched AlphaFast forever
                    //     AND re-filled punchLeft every frame, so punch never decayed;
                    //  3. neither could ever be released while the floor stayed high.
                    if (shaped == null || shaped.Count != smoothed.Count)
                        shaped = new ZoneColors(smoothed.Count);
                    shaped.CopyFrom(smoothed);
                    // blend along the perimeter so adjacent zones merge into one halo instead of
                    // reading as discrete patches (zone order is already the ring, so it wraps)
                    Colors.SpatialRing(shaped, cfg.Spatial);
                    ssw.Stop();
                    double sMs = ssw.Elapsed.TotalMilliseconds;

                    st.LastDelta = frameDelta;
                    st.LastAlpha = useAlpha;

                    // ---- CUT PUNCH: a brief overshoot so hard cuts land with impact ----
                    if (cfg.Punch > 0 && cfg.PunchFrames > 0 && frameDelta > cfg.AlphaCut)
                        punchLeft = cfg.PunchFrames;
                    double punchGain = 1.0;
                    if (punchLeft > 0 && cfg.PunchFrames > 0)   // PunchFrames>0 is guaranteed today
                    {                                            // because no token sets it, but the
                        double k = (double)punchLeft / cfg.PunchFrames;   // division is cheap to guard
                        punchGain = 1.0 + cfg.Punch * k;
                        punchLeft--;
                    }
                    st.LastPunch = punchGain;

                    // ---- STALE FADE ----
                    // A closed/paused video leaves the last decoded frame sitting in the plane, so we
                    // capture it happily forever and the strip holds one colour. That is the "lights
                    // stuck then go black" feel. Ramp DOWN slowly once the picture has been frozen for
                    // StaleMs, but ramp back UP fast, so resuming is instant and cannot feel laggy.
                    double dimTarget = 1.0;
                    if (cfg.StaleMs > 0 && st.LastContentChangeUtc != DateTime.MinValue)
                    {
                        double staleAge = (DateTime.UtcNow - st.LastContentChangeUtc).TotalMilliseconds;
                        if (staleAge > cfg.StaleMs) dimTarget = cfg.StaleLevel;
                    }
                    double dimRamp = (dimTarget > staleDim) ? 0.35 : 0.06;   // snap back, ease down
                    staleDim += (dimTarget - staleDim) * dimRamp;

                    // ---- AMBIENT FLOOR: never let the room go fully dark ----
                    if (cfg.Floor > 0) Colors.ApplyFloor(shaped, cfg.Floor);

                    // ---- 5. output. Enabled by default on THIS device (a deliberate choice on
                    // explicit request); the preference still gates it. ----
                    if (cfg.OutputEnabled)
                    {
                        ZoneColors toSend = shaped;
                        if (cfg.TestPattern && layout != null)
                        {
                            // 4-edge colour test, built in OUR zone order so the mapping is visible:
                            // top=red, right=green, bottom=blue, left=yellow
                            var tp = new ZoneColors(layout.Total);
                            int k0 = 0;
                            for (int e = 0; e < 4; e++)
                            {
                                int cnt = (e == 0) ? layout.TopCount : (e == 1) ? layout.RightCount
                                        : (e == 2) ? layout.BottomCount : layout.LeftCount;
                                byte r = (byte)(e == 0 ? 255 : (e == 2 ? 0 : (e == 3 ? 255 : 0)));
                                byte g = (byte)(e == 1 ? 255 : (e == 3 ? 255 : 0));
                                byte b = (byte)(e == 2 ? 255 : 0);
                                for (int q = 0; q < cnt && k0 < layout.Total; q++, k0++)
                                { tp.R[k0] = r; tp.G[k0] = g; tp.B[k0] = b; }
                            }
                            toSend = tp;
                        }
                        // WQUANTISED so the WLED LUT - cached on the exact gamma - is rebuilt only when
                        // the value actually moves rather than on every frame of the glide.
                        double effGamma = cfg.AdaptiveGamma
                            ? Math.Max(cfg.GammaMin, Math.Min(cfg.GammaMax,
                                        Math.Round(_adaptGamma / 0.05) * 0.05))
                            : cfg.Gamma;

                        byte[] payload = WledOutput.BuildDrgbMapped(toSend, (byte)cfg.WledTimeout,
                                                                    cfg.Gain * punchGain * staleDim,
                                                                    effGamma, cfg.Black,
                                                                    cfg.MapOffset, cfg.MapReverse, cfg.LedCount);
                        string err = WledOutput.Send(cfg.WledIp, cfg.WledPort, payload);
                        if (err == null) { st.SendOk++; st.LastSendErr = null; }
                        else { st.SendFail++; st.LastSendErr = err; if (st.SendFail % 20 == 1) L("wled send: " + err); }
                    }

                    fsw.Stop();
                    double totalMs = fsw.Elapsed.TotalMilliseconds;

                    // ---- accumulate ----
                    st.Frames++;
                    st.WindowFrames++;
                    st.SumCapture += capMs;
                    st.SumDecode += decMs;
                    st.SumTotal += totalMs;
                    st.SumZones += zMs;
                    st.SumSmooth += sMs;

                    if (st.Frames == 1)
                        L("first frame: " + dims + " capture=" + capMs.ToString("0.0") +
                          " decode=" + decMs.ToString("0.0") + " zones=" + zMs.ToString("0.0") +
                          " smooth=" + sMs.ToString("0.0") + " total=" + totalMs.ToString("0.0") + " ms -> " +
                          (1000.0 / Math.Max(0.001, totalMs)).ToString("0.0") + " fps");

                    // ---- 6. stats POST every ~statsIntervalSec ----
                    // Report `shaped` (what was actually sent), not the raw EMA state: the sample
                    // otherwise omitted the stale-fade dim, the ambient floor and the spatial blur,
                    // so it could disagree with the strip while looking authoritative.
                    MaybePost(cfg, captureDir, layout, shaped ?? smoothed, st);

                    // ---- RATE CAP --------------------------------------------------------------
                    // Skip whole frames rather than shave work: the capture call dominates a frame,
                    // so capping the rate is what actually saves power on the TV.
                    if (cfg.TargetFps > 0)
                    {
                        int targetMs = 1000 / cfg.TargetFps;
                        // A frozen picture (menu, pause, loading) needs even fewer updates. The
                        // content signature already says it is static, so back off further - the
                        // sleep is capped so a scene change is still noticed within ~200 ms.
                        if (st.ContentUnchanged > 10) targetMs *= 3;
                        int elapsedMs = (int)(DateTime.UtcNow - lastFrameUtc).TotalMilliseconds;
                        int wait = targetMs - elapsedMs;
                        if (wait > 200) wait = 200;
                        if (wait > 0) Thread.Sleep(wait);
                    }
                }
                catch (Exception e)
                {
                    // The loop owns both the telemetry and the strip, so an unexpected throw must
                    // take down neither: log the failure, count it, blank the strip if the run of
                    // failures has gone on long enough, and carry on with the next iteration.
                    st.LoopExceptions++;
                    st.ConsecutiveFails++;
                    L("loop exception #" + st.LoopExceptions + ": " + e.GetType().Name + ": " + e.Message);
                    MaybeBlackout(cfg, st);
                    Thread.Sleep(FailSleepMs);
                }
            }
        }

        static void MaybePost(Config cfg, string captureDir,
                              ZoneLayout layout, ZoneColors smoothed, Stats st)
        {
            if (st.Window.Elapsed.TotalSeconds < cfg.StatsIntervalSec) return;

            double windowSec = st.Window.Elapsed.TotalSeconds;
            // WINDOWED, not lifetime. These used to divide lifetime sums by the lifetime frame count,
            // so they read a flat ~11 ms while the real frame time swung from ~98 to ~13 ms - a 4.5x
            // change the telemetry could not show. Now they describe the window that just ended.
            double n = st.WindowFrames > 0 ? st.WindowFrames : 0;
            double runSec = (DateTime.UtcNow - st.RunStart).TotalSeconds;

            var w = new Dictionary<string, object>
            {
                { "app", AppId },
                { "version", Version },
                { "ts_utc", DateTime.UtcNow.ToString("o") },
                { "phase", "stats" },
                { "window_sec", windowSec },
                { "frames_this_window", st.WindowFrames },
                { "capture_ok", st.CaptureOk },
                { "capture_fail", st.CaptureFail },
                { "decode_fail", st.DecodeFail },
                { "frames_skipped_no_decoder", st.JpegFramesSkipped },
                { "frames_decoded_total", st.Frames },
                { "loop_exceptions", st.LoopExceptions },
                { "consecutive_fail", st.ConsecutiveFails },
                { "mean_capture_ms", n > 0 ? st.SumCapture / n : 0.0 },
                { "mean_decode_ms", n > 0 ? st.SumDecode / n : 0.0 },
                { "mean_total_frame_ms", n > 0 ? st.SumTotal / n : 0.0 },
                { "mean_zone_ms", n > 0 ? st.SumZones / n : 0.0 },
                { "mean_smooth_ms", n > 0 ? st.SumSmooth / n : 0.0 },
                { "achieved_fps", runSec > 0 ? st.Frames / runSec : 0.0 },
                { "window_fps", windowSec > 0 ? st.WindowFrames / windowSec : 0.0 },
                { "last_dims", st.LastDims ?? "" },
                { "last_status", st.LastStatus ?? "" },
                { "last_ret_path", st.LastRetPath ?? "" },
                { "frame_bytes", st.FrameBytes },
                { "last_file_bytes", st.LastFileBytes },
                { "requested_file_name", CaptureFileName },
                { "requested_width", (long)cfg.RequestWidth },
                { "requested_height", (long)cfg.RequestHeight },
                { "compress_type", (long)cfg.CompressType },
                { "capture_dir_in_use", captureDir },
                { "smoothed_vs_raw_mean_delta", st.LastDelta },
                { "uptime_s", runSec },
                { "output_enabled", cfg.OutputEnabled },
                { "wled_ip", cfg.WledIp },
                { "wled_port", (long)cfg.WledPort },
                { "wled_sent_ok", st.SendOk },
                { "wled_sent_fail", st.SendFail },
                // ---- power-state awareness ----
                { "power_state", st.PowerState ?? "unknown" },
                { "power_raw", st.PowerRaw ?? "" },
                { "power_polls", st.PowerPolls },
                { "power_capture_skipped", st.PowerSkipped },
                { "power_probe_fail", st.PowerProbeFail },
                { "capture_backoff_ms", (long)BackoffSleep(st) },
                { "off_sleep_ms", (long)OffSleepMs },
                { "power_poll_cache_ms", (long)PowerPollCacheMs },
                { "power_probe_url", cfg.PowerUrl },
            };

            if (layout != null)
            {
                w["zone_layout"] = layout.Describe();
                w["zone_count"] = (long)layout.Total;
                w["zone_band_px"] = layout.BandX + "x" + layout.BandY;
            }

            if (smoothed != null && smoothed.Count > 0 && layout != null)
            {
                // 12 by default (a compact ring sample). `zhex=1` reports every zone, which is the only
                // way to verify per-edge geometry from telemetry - e.g. whether the zones on one edge
                // really resolve independently or all report the same average.
                int want = cfg.ZoneHexAll ? smoothed.Count : 12;
                if (want < 1) want = 1;
                var sample = new List<object>();
                var idxs = new List<object>();
                for (int i = 0; i < want; i++)
                {
                    int idx = (int)((long)i * smoothed.Count / want);
                    if (idx >= smoothed.Count) idx = smoothed.Count - 1;
                    Zone zn = layout.Zones[idx];
                    sample.Add(zn.Edge + "[" + zn.Slot + "]=" +
                               Colors.Hex(smoothed.R[idx], smoothed.G[idx], smoothed.B[idx]));
                    idxs.Add((long)idx);
                }
                w["zone_sample_hex"] = sample;
                w["zone_sample_indices"] = idxs;
                // how many of the sampled zones actually carry colour?
                int coloured = 0;
                for (int i = 0; i < idxs.Count; i++)
                {
                    int zz = (int)(long)idxs[i];
                    int mx = Math.Max(smoothed.R[zz], Math.Max(smoothed.G[zz], smoothed.B[zz]));
                    int mn = Math.Min(smoothed.R[zz], Math.Min(smoothed.G[zz], smoothed.B[zz]));
                    if (mx - mn > 8) coloured++;
                }
                w["zones_with_colour"] = (long)coloured;
                w["zones_sampled"] = (long)idxs.Count;
                w["color_format_chosen"] = (long)VideoPlaneCapture.ChosenColorFormat;
                w["color_format_verdict"] = VideoPlaneCapture.ColorFormatVerdict;
                w["chroma_spread_found"] = VideoPlaneCapture.ChosenChromaSpread;
                w["color_format_probe"] = VideoPlaneCapture.ColorFormatProbe;
            }

            // ---- Everything below depends only on CONFIG and STATS, never on the zone sample, so it
            // must be emitted on EVERY post. These were accidentally nested inside the
            // `if (smoothed != null && ...)` guard above - visible as the indentation jump - which
            // silently dropped them from any post made before the first successful frame, or on any
            // path where the zone buffers are still null (notably the standby and paused paths).
            w["mapping"] = "mapOffset=" + cfg.MapOffset + " reverse=" + cfg.MapReverse +
                           " ledCount=" + cfg.LedCount + " testPattern=" + cfg.TestPattern;
            // Report the decode switches. Without this they had to be INFERRED from zone values
            // after a reboot (max channel > 160 => HDR path active), which is silly for a flag.
            // Ship the last few log lines with every stats post. Without this the log is only carried
            // by startup reports, which means crash evidence, the platform-change shout and anything
            // else logged mid-run is INVISIBLE from the collector - several safety features of this app
            // were unverifiable for exactly that reason.
            try
            {
                List<string> tail;
                lock (_logLock)
                {
                    int take = Log.Count < 6 ? Log.Count : 6;
                    tail = Log.GetRange(Log.Count - take, take);
                }
                w["log_tail"] = new List<object>(tail);
            }
            catch { }

            w["tv"] = "model=" + (_modelName ?? "") + " platform=" + (_platform ?? "unavailable") +
                      " captureApi=" + (VideoPlaneCapture.Ready ? "ok" : "UNAVAILABLE") +
                      " plane=" + VideoPlaneCapture.ReqW + "x" + VideoPlaneCapture.ReqH;
            w["adaptive"] = "enabled=" + cfg.AdaptiveGamma + " medianCode=" + _lastMedianCode +
                            " sceneNits=" + Math.Round(VideoPlaneCapture.PqNits(_lastMedianCode), 3) +
                            " gammaEff=" + Math.Round(cfg.AdaptiveGamma ? _adaptGamma : cfg.Gamma, 3) +
                            " range=" + cfg.GammaMin + ".." + cfg.GammaMax + " tau=" + cfg.AdaptTauS;
            w["decode"] = "hdr=" + cfg.Hdr + " gamut=" + cfg.Gamut + " zoneSample=" + cfg.ZoneSample +
                          " peak=" + VideoPlaneCapture.HdrPeakNits + " targetFps=" + cfg.TargetFps;
            w["tuning"] = "alpha=" + cfg.Alpha + " gain=" + cfg.Gain + " gamma=" + cfg.Gamma +
                          " saturation=" + cfg.Saturation + " white=" + cfg.WhiteR + "/" + cfg.WhiteG +
                          "/" + cfg.WhiteB;
            // Report the EFFECTIVE gamma - with the adaptive curve on, cfg.Gamma is only the manual
            // fallback and quoting it here made the telemetry disagree with what is actually applied.
            w["tone"] = "black=" + cfg.Black + " gamma=" +
                        Math.Round(cfg.AdaptiveGamma ? _adaptGamma : cfg.Gamma, 3) +
                        " gain=" + cfg.Gain;
            double staleMs = (st.LastContentChangeUtc == DateTime.MinValue)
                ? -1.0 : (DateTime.UtcNow - st.LastContentChangeUtc).TotalMilliseconds;
            w["content"] = "unchangedFrames=" + st.ContentUnchanged +
                           " msSinceChange=" + Math.Round(staleMs, 0) +
                           " noFrameMs=" + (_lastGoodFrameUtc == DateTime.MinValue ? -1
                                            : (long)(DateTime.UtcNow - _lastGoodFrameUtc).TotalMilliseconds);
            w["io"] = "worker=separate-thread controlPollMs=" + ControlPollMs + " httpShared=True";
            w["reactive"] = "blankAfterNoFrameMs=" + BlankAfterNoFrameMs +
                            " powerPollCacheMs=" + PowerPollCacheMs +
                            " offSleepMs=" + OffSleepMs + " powerPolls=" + st.PowerPolls;
            w["bars"] = "enabled=" + cfg.Bars + " rows=" + st.BarTopNow + ".." + st.BarBottomNow +
                        " cols=" + st.BarLeftNow + ".." + st.BarRightNow +
                        " adopted=" + st.BarsAdopted + " thresh=" + cfg.BarThresh + " hold=" + cfg.BarHold;
            w["ambience"] = "punch=" + cfg.Punch + " punchFrames=" + cfg.PunchFrames +
                            " lastPunch=" + Math.Round(st.LastPunch, 3) + " floor=" + cfg.Floor +
                            " staleMs=" + cfg.StaleMs + " staleLevel=" + cfg.StaleLevel;
            w["smoothing"] = "alphaSlow=" + cfg.Alpha + " alphaFast=" + cfg.AlphaFast +
                             " cutAt=" + cfg.AlphaCut + " spatial=" + cfg.Spatial +
                             " lastAlpha=" + Math.Round(st.LastAlpha, 3) +
                             " lastDelta=" + Math.Round(st.LastDelta, 2);
            w["mapping_live"] = st.LastMapping;

            if (st.LastSendErr != null) w["wled_last_error"] = st.LastSendErr;
            w["output_note"] = cfg.OutputEnabled
                ? "outputEnabled is TRUE \u2014 DRGB datagrams are being sent to " + cfg.WledIp + ":" + cfg.WledPort +
                  " for " + cfg.LedCount + " LEDs (" + cfg.ZoneCount + " zones)."
                : "outputEnabled is FALSE \u2014 no UDP packet has been sent.";

            // Build here (pure CPU), POST on the worker. One slot: a newer snapshot replaces an
            // unsent older one instead of queueing behind it.
            string json = Json.ToJson(w);
            lock (_postLock) { _pendingPost = json; }

            st.WindowFrames = 0;
            st.SumCapture = 0; st.SumZones = 0; st.SumTotal = 0; st.SumSmooth = 0; st.SumDecode = 0;
            st.Window.Restart();
        }

        // ================================================================ one-off reports

        static Dictionary<string, object> BuildReport(Config cfg, string selfTest, string libInitError,
                                                      string captureDir, Dictionary<string, object> discovery,
                                                      string phase, Stats st)
        {
            var r = new Dictionary<string, object>
            {
                { "app", AppId },
                { "version", Version },
                { "ts_utc", DateTime.UtcNow.ToString("o") },
                { "phase", phase },
                { "uptime_s", (DateTime.UtcNow - ProcessStartUtc).TotalSeconds },
                { "config", cfg.ToJson() },
                { "png_decoder_self_test", selfTest },
                { "capture_symbol", CaptureApi.Symbol },
                { "capture_library", CaptureApi.LibName },
                { "capture_library_resolved_path", CaptureApi.ResolvedPath },
                { "capture_library_error", libInitError ?? "" },
                { "capture_dir", captureDir ?? "" },
                { "capture_file", CaptureFileName },
                { "output_enabled", cfg.OutputEnabled },
                { "capture_ok", st != null ? st.CaptureOk : 0 },
                { "capture_fail", st != null ? st.CaptureFail : 0 },
            };
            if (discovery != null) r["dir_discovery"] = discovery;
            if (st != null) r["loop_exceptions"] = st.LoopExceptions;
            lock (_logLock) { r["log"] = new List<object>(Log); }
            return r;
        }

        static void Post(Config cfg, Dictionary<string, object> report)
        {
            string json = Json.ToJson(report);
            string err = Json.Post(cfg.Endpoint + "/probe", json, 10);
            L("report " + report["phase"] + " -> " + (err == null ? "OK" : err) + " (" + json.Length + "B)");
        }
    }
}
