using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Tizen.Applications;

namespace TizenAmbilight
{
    static partial class Ambilight
    {
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
                    if (cfg.HasControl && swCtrl.ElapsedMilliseconds >= ControlPollMs)
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
                    if (stats != null && cfg.HasTelemetry)
                    {
                        string err = Json.Post(cfg.Endpoint + "/probe", stats, 10);
                        if (err != null) L("stats POST failed (poster): " + err);
                    }
                    if (dy != null && cfg.HasTelemetry)
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
    }
}
