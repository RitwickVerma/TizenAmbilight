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
    }
}
