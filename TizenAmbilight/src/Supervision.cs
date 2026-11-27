using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace TizenAmbilight
{
    static partial class Ambilight
    {
        // ================================================================ supervision
        //
        // Restart-on-death is the PLATFORM's job: the manifest sets auto-restart="true", so the TV's
        // own launcher brings the app back after an abnormal exit (measured: Exit(3) and FailFast
        // both relaunched in ~22-25 s). Nothing off the TV is involved. This file only makes that
        // safe: exactly one process, no restart storm, and no pause that outlives its controller.

        /// <summary>The app's OWN data dir. The sandbox does not let the app write /home/owner/ambilight
        /// (measured: every write there failed silently), so state lives where the platform says.</summary>
        static string _stateDir;
        static string StateDir
        {
            get
            {
                if (_stateDir != null) return _stateDir;
                string d = null;
                try { d = Tizen.Applications.Application.Current?.DirectoryInfo?.Data; } catch { }
                _stateDir = string.IsNullOrEmpty(d) ? "/home/owner/apps_rw/" + AppId + "/data" : d;
                return _stateDir;
            }
        }

        /// <summary>Guard state for telemetry, so a guard that is off is visible, never silent.</summary>
        static string _lockState = "unset", _stormState = "unset";
        static string SupervisionReport() { return "lock=" + _lockState + " starts=" + _stormState + " dir=" + StateDir; }

        /// <summary>Held for the life of the process. The kernel drops the lock when the process
        /// dies however it dies, so a crash can never leave a stale lock behind.</summary>
        static FileStream _instanceLock;

        /// <summary>Exclusive lock on StateDir/instance.lock. A second process waits up to 30 s
        /// (a redeploy's old process may still be dying), then gives up.</summary>
        static bool AcquireInstanceLock()
        {
            string path = Path.Combine(StateDir, "instance.lock");
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    Directory.CreateDirectory(StateDir);
                    _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    _lockState = "held";
                    return true;
                }
                catch (IOException) { Thread.Sleep(1000); }
                catch (Exception e) { _lockState = "UNAVAILABLE:" + e.GetType().Name; L("instance lock unavailable (" + e.GetType().Name + "), running unguarded"); return true; }
            }
            return false;
        }

        const int StormWindowSec = 600;    // look back this far ...
        const int StormMaxStarts = 5;      // ... and allow this many starts in it
        const int StormCooldownSec = 600;  // otherwise hold black this long, then try normally

        /// <summary>Record this start; if the app is crash-looping, stay ALIVE but idle (strip held
        /// black, no capture calls) for StormCooldownSec. Alive means the platform has nothing to
        /// restart, which ends the loop; the timed exit from cooldown means it can never stick.</summary>
        static void CrashStormGuard(Config cfg)
        {
            var starts = new List<long>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string path = Path.Combine(StateDir, "starts.txt");
            try
            {
                if (File.Exists(path))
                    foreach (string ln in File.ReadAllLines(path))
                    {
                        long t;
                        if (long.TryParse(ln.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out t) &&
                            now - t >= 0 && now - t < StormWindowSec) starts.Add(t);
                    }
                starts.Add(now);
                var lines = new List<string>();
                foreach (long t in starts) lines.Add(t.ToString(CultureInfo.InvariantCulture));
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception e) { _stormState = "UNAVAILABLE:" + e.GetType().Name; L("start history unavailable (" + e.GetType().Name + "): storm guard off"); return; }

            _stormState = starts.Count + "/" + StormMaxStarts + "in" + StormWindowSec + "s";
            if (starts.Count <= StormMaxStarts) return;
            _stormState += ":COOLDOWN";
            L("CRASH STORM: " + starts.Count + " starts in " + StormWindowSec + " s - holding black, no capture, for " +
              StormCooldownSec + " s");
            try { Post(cfg, BuildReport(cfg, "", null, "", null, "cooldown", new Stats())); } catch { }
            var until = DateTime.UtcNow.AddSeconds(StormCooldownSec);
            while (DateTime.UtcNow < until && !_stopping) { HoldBlackout(cfg, "crash-storm cooldown: holding black"); Thread.Sleep(1000); }
            L("cooldown over: starting normally");
        }

        /// <summary>Last successful control fetch. A pause exists only to protect a redeploy that the
        /// controller is running; if the controller has been gone this long, the pause is orphaned.</summary>
        static DateTime _ctrlOkUtc = DateTime.UtcNow;
        const int PauseOrphanSec = 120;
        static bool PauseOrphaned() { return (DateTime.UtcNow - _ctrlOkUtc).TotalSeconds > PauseOrphanSec; }

        /// <summary>The k=v tokens of the applied body, i.e. everything overriding the baked defaults.</summary>
        static string ControlOverrides(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var o = new List<string>();
            foreach (string t in body.Split(new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (t.IndexOf('=') > 0) o.Add(t.Trim().ToLowerInvariant());
            return string.Join(",", o);
        }
    }
}
