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
    }
}
