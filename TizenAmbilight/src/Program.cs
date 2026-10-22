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
    /// TizenAmbilight: a real-frame ambilight for Samsung Tizen TVs, as one headless service app.
    ///
    /// Each frame: read the TV's decoded VIDEO plane into our own buffers
    /// (<see cref="VideoPlaneCapture"/>), average a border band into N perimeter zones straight from
    /// the YUV planes (<see cref="ZoneLayout"/>), smooth them over time, tone-map, and send the colours
    /// to a WLED strip as one DRGB UDP datagram (<see cref="WledOutput"/>). The legacy
    /// screenshot-to-PNG path (<see cref="CaptureApi"/>) remains as a non-default fallback backend.
    ///
    /// Settings come from config.json bundled in the TPK (<see cref="ConfigFile"/>); live tuning from
    /// an optional control URL (<see cref="Ambilight.ApplyControl"/>). Nothing is sent anywhere until
    /// an endpoint is configured.
    ///
    /// Power-state aware: the loop asks the TV's own local API (port 8001) whether the panel is on and
    /// makes no capture calls in standby. A probe that cannot answer is treated as "on", so a working
    /// screen is never skipped (<see cref="PowerStateProbe"/>).
    ///
    /// Everything is instrumented rather than guessed at: timings, per-status counters and a log tail
    /// are POSTed to an optional collector every `statsIntervalSec`.
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

    static partial class Ambilight
    {
        public const string AppId = "dev.ritwickverma.TizenAmbilight";
        // Bump this whenever behaviour changes. It sat at "v3" through the HDR decode, the BT.2020->709
        // gamut map and the adaptive tone curve, which made build identification useless in telemetry.
        public const string Version = "v5";

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

        // ================================================================ entry

        public static void Run()
        {
            Config cfg = ReadConfig();
            _cfg = cfg;
            CaptureFileName = cfg.CompressType == CaptureApi.CompressJpeg ? "amb.jpg" : "amb.png";

            L("=== " + AppId + " " + Version + " starting ===");
            L(RecordedMeasurement);
            L("config: " + ConfigFile.Source + " (" + ConfigFile.Count + " keys)");
            if (!cfg.HasWled)
                L("NOTE: no wledIp configured - colours are computed but nothing is sent. " +
                  "Copy config.example.json to config.json, set wledIp, rebuild.");
            L("control channel: " + (cfg.HasControl ? cfg.ControlUrl : "(none)") +
              "   telemetry: " + (cfg.HasTelemetry ? cfg.Endpoint : "(none)"));
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
            bool usePlane = cfg.CaptureBackend == VideoPlaneCapture.BackendName;
            string vErr = null;
            if (usePlane)
            {
                vErr = VideoPlaneCapture.Init();
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

            // Gate on the SELECTED backend only. This used to test the legacy file backend's library
            // even when the video plane was selected, so a firmware without libdisplay-capture-api
            // would idle forever although the default backend was ready.
            string backendErr = usePlane ? vErr : initErr;
            if (backendErr != null)
            {
                Post(cfg, BuildReport(cfg, selfTest, backendErr, captureDir, discovery, "no_library", stats));
                L("cannot run the main loop without the capture library; idling and retrying every 60 s");
                while (true)
                {
                    Thread.Sleep(60000);
                    string again = usePlane ? VideoPlaneCapture.Init() : CaptureApi.Init();
                    if (again == null) { L("capture library became available"); MainLoop(cfg, selfTest, captureDir, discovery, stats); return; }
                }
            }

            // ---- (3) the one-off startup POST: decoder verdict + directory discovery ----
            Post(cfg, BuildReport(cfg, selfTest, null, captureDir, discovery, "started", stats));

            // ---- (4) the real loop ----
            MainLoop(cfg, selfTest, captureDir, discovery, stats);
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
                            if (cfg.BarRows)
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
                            }
                            else
                            {
                                barTop = 0; barBottom = -1;
                                st.BarTopNow = 0; st.BarBottomNow = 0;
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

                    // ---- 5. output (only when enabled AND a strip is configured) ----
                    if (CanSend(cfg))
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
    }
}
