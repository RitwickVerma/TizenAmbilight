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
            w["bars"] = "enabled=" + cfg.Bars + " rowTest=" + cfg.BarRows + " rows=" + st.BarTopNow + ".." + st.BarBottomNow +
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
            if (!cfg.HasTelemetry) return;
            string json = Json.ToJson(report);
            string err = Json.Post(cfg.Endpoint + "/probe", json, 10);
            L("report " + report["phase"] + " -> " + (err == null ? "OK" : err) + " (" + json.Length + "B)");
        }
    }
}
