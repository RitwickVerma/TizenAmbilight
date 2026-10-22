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

        /// <summary>Every tunable the app reads, resolved once at startup by <see cref="ReadConfig"/>
        /// and then adjusted live by the control channel (<see cref="ApplyControl"/>).</summary>
        sealed class Config
        {
            public bool OutputEnabled;          // master switch for UDP output; gates every send
            public int ZoneCount;
            public double Alpha;
            // ---- output brightness shaping ----
            public double Gain;
            public double Gamma;
            public double Black;        // blacks at/below this go to zero (kills the "milky" haze)
            // ---- adaptive temporal + spatial smoothing ----
            public double AlphaCut;     // mean per-channel frame delta above which we react FAST
            public double AlphaFast;    // alpha used for those frames (scene cuts)
            public double Spatial;      // perimeter neighbour blend, 0 = off
            // ---- letterbox / pillarbox handling ----
            public bool Bars;           // master switch for black-bar detection
            public bool BarRows;        // also test ROWS (letterbox); see the ReadConfig note
            public int BarThresh;       // a row/column is bar if >=90% of it is below this
            public int BarHold;         // frames a new bar size must persist before being adopted
            // ---- ambience ----
            public double Punch;        // brief brightness/saturation overshoot on a cut, 0 = off
            public int PunchFrames;     // how many frames the punch decays over
            public double Floor;        // minimum zone level; 0 = true blacks
            // ---- stale content: a frozen picture must not hold the strip lit forever ----
            public double StaleMs;      // fade after the picture has not moved this long (0 = never)
            public double StaleLevel;   // dim factor to fade to (0.35 = 35%)
            // ---- colour handling ----
            public double Saturation;   // chroma expansion about neutral, applied before the matrix
            public bool Hdr;            // decode the plane as PQ-encoded HDR
            public bool Gamut;          // convert BT.2020 primaries -> BT.709 for the strip
            public int ZoneSample;      // pixel stride when averaging zones (1 = every pixel)
            public bool ZoneHexAll;     // report every zone in zone_sample_hex (audit tool)
            // ---- adaptive tone curve (see SceneGammaTarget) ----
            public bool AdaptiveGamma;          // drive gamma from the scene's brightness
            public double GammaMin, GammaMax;   // clamp range for the adapted gamma
            public double AdaptTauS;            // EMA time constant of the adaptation
            public int TargetFps;               // cap on the capture rate; 0 = uncapped
            public double WhiteR, WhiteG, WhiteB;   // per-channel white balance
            public double BandFrac;
            // ---- endpoints. Empty = that feature is off; nothing is sent anywhere by default. ----
            public string WledIp;
            public int WledPort;
            public int WledTimeout;
            public string Endpoint;     // telemetry collector base URL
            public int StatsIntervalSec;
            public string PowerUrl;     // the TV's own local API (loopback)
            public string ControlUrl;   // live control channel; GET returns the token body
            // ---- legacy file backend request ----
            public int RequestWidth;
            public int RequestHeight;
            // "video_plane" : secvideo_api_capture_video_main -> the VIDEO plane, in memory (no file)
            // "file"        : the legacy dc_request_capture_to_file_sync PNG-file path
            public string CaptureBackend;
            // ---- zone -> LED mapping (see WledOutput.BuildDrgbMapped) ----
            public int MapOffset;
            public bool MapReverse;
            public int LedCount;        // physical LEDs to drive; LEDs past ZoneCount are sent black
            public bool TestPattern;    // send the 4-edge colour test instead of the ambilight
            public int CompressType;
            public int JpegQuality;

            public bool HasWled { get { return !string.IsNullOrEmpty(WledIp); } }
            public bool HasTelemetry { get { return !string.IsNullOrEmpty(Endpoint); } }
            public bool HasControl { get { return !string.IsNullOrEmpty(ControlUrl); } }

            public Dictionary<string, object> ToJson()
            {
                return new Dictionary<string, object>
                {
                    { "config_source", ConfigFile.Source },
                    { "output_enabled", OutputEnabled },
                    { "zone_count", (long)ZoneCount },
                    { "led_count", (long)LedCount },
                    { "smoothing_alpha", Alpha },
                    { "border_band_frac", BandFrac },
                    { "wled_ip", WledIp },
                    { "wled_port", (long)WledPort },
                    { "wled_timeout_s", (long)WledTimeout },
                    { "probe_endpoint", Endpoint },
                    { "control_url", ControlUrl },
                    { "stats_interval_s", (long)StatsIntervalSec },
                    { "power_state_url", PowerUrl },
                    { "capture_backend", CaptureBackend },
                    { "request_width", (long)RequestWidth },
                    { "request_height", (long)RequestHeight },
                    { "compress_type", (long)CompressType },
                    { "jpeg_quality", (long)JpegQuality },
                };
            }
        }

        // ---- setting lookup: Tizen Preference (if the app ever set one) > config.json > default ----

        static bool PrefBool(string key, bool dflt)
        {
            try
            {
                if (Preference.Contains(key))
                {
                    object v = Preference.Get<bool>(key);
                    return (v is bool) && (bool)v;
                }
            }
            catch { }
            double d;
            return ConfigFile.TryGetDouble(key, out d) ? d != 0 : dflt;
        }

        static int PrefInt(string key, int dflt)
        {
            try { if (Preference.Contains(key)) return Preference.Get<int>(key); }
            catch { }
            double d;
            if (ConfigFile.TryGetDouble(key, out d) && d >= int.MinValue && d <= int.MaxValue)
                return (int)Math.Round(d);
            return dflt;
        }

        static double PrefDouble(string key, double dflt)
        {
            try
            {
                if (Preference.Contains(key))
                {
                    object v = Preference.Get<double>(key);
                    if (v != null) return Convert.ToDouble(v);
                }
            }
            catch { }
            double d;
            return ConfigFile.TryGetDouble(key, out d) ? d : dflt;
        }

        static string PrefString(string key, string dflt)
        {
            try
            {
                if (Preference.Contains(key))
                {
                    string v = Preference.Get<string>(key);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
            string s;
            return ConfigFile.TryGetString(key, out s) ? s.Trim() : dflt;
        }

        /// <summary>Find and load config.json. The TPK puts it in <c>res/</c>; the app's assembly runs
        /// from <c>bin/</c>, so the sibling path is the fallback if the framework directory API is
        /// unavailable.</summary>
        static void LoadConfigFile()
        {
            string resDir = null;
            try { resDir = Application.Current?.DirectoryInfo?.Resource; } catch { }
            string binDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            ConfigFile.Load(
                string.IsNullOrEmpty(resDir) ? null : System.IO.Path.Combine(resDir, "config.json"),
                System.IO.Path.Combine(binDir, "..", "res", "config.json"));
        }

        static Config ReadConfig()
        {
            LoadConfigFile();
            var c = new Config();
            // Gates every UDP send. With no wledIp configured there is nowhere to send anyway, so
            // defaulting to true only means "drive the strip once one is configured".
            c.OutputEnabled = PrefBool("outputEnabled", true);

            // ---- strip size. Set ledCount to the strip's LED count; zones follow it unless set. ----
            // zoneCount < ledCount leaves the extra LEDs black (e.g. LEDs hidden behind a stand).
            int leds = PrefInt("ledCount", 0), zones = PrefInt("zoneCount", 0);
            if (leds <= 0 && zones <= 0) { leds = DefaultZoneCount; zones = DefaultZoneCount; }
            else if (zones <= 0) zones = leds;
            else if (leds <= 0) leds = zones;
            c.ZoneCount = Math.Max(4, Math.Min(WledOutput.MaxDrgbLeds, zones));
            c.LedCount = Math.Max(4, Math.Min(WledOutput.MaxDrgbLeds, leds));

            c.Alpha = PrefDouble("alpha", 0.25);
            if (c.Alpha <= 0.0) c.Alpha = 1.0;
            if (c.Alpha > 1.0) c.Alpha = 1.0;
            c.Gain = PrefDouble("gain", 1.0);
            if (c.Gain < 0.1) c.Gain = 0.1;
            if (c.Gain > 8.0) c.Gain = 8.0;
            // Brightness belongs in GAMMA, not gain: gain multiplies the top of the range and clips it
            // (gain 1.25 flattened everything above a zone average of ~200 to 255), whereas gamma lifts
            // the mid-tones and cannot clip.
            // 1.5 because the plane is PRE-TONE-MAP and the TV brightens it substantially for display,
            // so a faithful mapping reads darker than the screen. Measured on a real film: 96% of its
            // pixels sat at PQ codes 16-31 (0.09-0.28 nits), where gamma 1.2 left the LEDs at 0.2%.
            // This is the manual curve; with adaptiveGamma on it is only the fallback.
            c.Gamma = PrefDouble("gamma", 1.5);
            if (c.Gamma < 1.0) c.Gamma = 1.0;
            if (c.Gamma > 4.0) c.Gamma = 4.0;
            // ADAPTIVE TONE CURVE. One static gamma cannot suit both a film sitting at 0.09-0.28 nits
            // and a bright game. The surround-gamma rule says a darker viewing condition wants a
            // HIGHER gamma; taking the scene itself as the "surround" gives a content-adaptive curve,
            // the same idea as HDR10+ / SMPTE ST 2094 dynamic metadata and madVR's dynamic target nits.
            c.ZoneHexAll = PrefInt("zoneHexAll", 0) != 0;
            c.AdaptiveGamma = PrefInt("adaptiveGamma", 1) != 0;
            c.GammaMin = Math.Max(0.5, Math.Min(3.0, PrefDouble("gammaMin", 1.15)));
            c.GammaMax = Math.Max(c.GammaMin, Math.Min(4.0, PrefDouble("gammaMax", 1.6)));
            // ~2 s: slow enough that the strip never visibly pumps between scenes, which is the failure
            // mode of any content-adaptive tone curve. Game engines use tau ~1.1 s for tonemapping.
            c.AdaptTauS = Math.Max(0.2, Math.Min(60.0, PrefDouble("adaptTau", 2.0)));
            // Black crush. MUST STAY BELOW `floor` when a floor is used: the floor is applied BEFORE
            // this tone map, so a black level above it maps the floored value to 0 and silently turns
            // the floor off. 12 because the content's black level is PQ code 16 (0.087 nits), which the
            // transfer maps to ~10: 12 keeps code 16 at exactly 0 while letting code 24 reach 17. At 20
            // it deleted most of a dark film (66% of its pixels sat at codes 16-23). Raise it only if
            // blacks look lifted; lower it only if a genuinely black frame glows.
            c.Black = PrefDouble("black", 12);
            if (c.Black < 0) c.Black = 0;
            if (c.Black > 200) c.Black = 200;
            c.AlphaCut = PrefDouble("alphaCut", 18);
            if (c.AlphaCut < 0) c.AlphaCut = 0;
            c.AlphaFast = PrefDouble("alphaFast", 0.75);
            if (c.AlphaFast < 0.02) c.AlphaFast = 0.02;
            if (c.AlphaFast > 1.0) c.AlphaFast = 1.0;
            if (c.AlphaFast < c.Alpha) c.AlphaFast = c.Alpha;
            // 0.50 because the blur is applied once per frame to an output copy. It used to be applied
            // in place to the EMA state, so it compounded frame over frame and a smaller number gave
            // a far wider halo than it suggested.
            c.Spatial = PrefDouble("spatial", 0.50);
            if (c.Spatial < 0) c.Spatial = 0;
            if (c.Spatial > 1.0) c.Spatial = 1.0;
            // Bar detection. Columns (pillarbox) detected real 4:3 content on hardware. Rows
            // (letterbox) default OFF: the video plane is scaled to fill the requested box, so on the
            // reference TV it carried no letterbox rows and the row test only produced false
            // positives (observed cropping 25% of the picture). `barRows=1` re-enables it for a
            // device whose plane does carry them.
            c.Bars = PrefInt("bars", 1) != 0;
            c.BarRows = PrefInt("barRows", 0) != 0;
            // 18, not 24. A genuine bar reads the PQ black level (16, measured); dark PICTURE reads
            // 21-26, so 24 cropped real picture whenever a scene went dark. The detector also
            // requires flatness, so this only has to clear the black level.
            c.BarThresh = PrefInt("barThresh", 18);
            if (c.BarThresh < 17) c.BarThresh = 17;   // bars read exactly 16 and the test is `< thr`,
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
            // Ambient floor, default 0 = true blacks. A floor and true blacks are mutually exclusive:
            // the floor lifts exactly the zones that should be off. Must exceed `black` to have any
            // effect (see the Black note above).
            c.Floor = PrefDouble("floor", 0);
            if (c.Floor < 0) c.Floor = 0;
            if (c.Floor > 255) c.Floor = 255;
            c.StaleMs = PrefDouble("staleMs", 6000);
            if (c.StaleMs < 0) c.StaleMs = 0;
            c.StaleLevel = PrefDouble("staleLevel", 0.35);
            if (c.StaleLevel < 0) c.StaleLevel = 0;
            if (c.StaleLevel > 1.0) c.StaleLevel = 1.0;
            // 1.0 = no chroma exaggeration. Scaling chroma preserves hue and cannot create colour in
            // a grey zone; it compensates for per-zone U/V averaging, which is a vector mean and pulls
            // mixed-hue content toward neutral (measured mean output saturation 0.17 at 1.0 vs 0.31 at
            // 1.8). Raise toward ~1.4 for a deliberately more vivid look; above ~1.5 the soft gamut
            // map starts pulling chroma back.
            c.Saturation = PrefDouble("saturation", 1.0);
            if (c.Saturation < 0.0) c.Saturation = 0.0;
            if (c.Saturation > 4.0) c.Saturation = 4.0;
            // The plane carries PQ-encoded HDR on the reference TV (luma ceiling 148 on two unrelated
            // contents = the PQ code for 203 nits). Set 0 only if your device delivers SDR.
            c.Hdr = PrefInt("hdr", 1) != 0;
            // HDR reference white in nits: what maps to full scale. 203 is the BT.2408 diffuse white.
            VideoPlaneCapture.HdrPeakNits = Math.Max(100.0, Math.Min(4000.0, PrefDouble("peak", 203.0)));
            c.Gamut = PrefInt("gamut", 1) != 0;
            // Stride 3 scans ~9x fewer pixels per zone at 1.4% mean error against every pixel. Zone
            // extraction, not capture, was the frame-rate ceiling.
            c.ZoneSample = Math.Max(1, Math.Min(8, PrefInt("zoneSample", 3)));
            // RESOURCE CAP. Uncapped the loop runs at ~55 fps, but ambient light reads as continuous
            // at ~25. Capping removes whole capture calls (about two thirds of a frame) instead of
            // shaving cheap arithmetic.
            c.TargetFps = Math.Max(0, Math.Min(120, PrefInt("targetFps", 25)));
            c.WhiteR = Math.Max(0.1, Math.Min(4.0, PrefDouble("whiteR", 1.0)));
            c.WhiteG = Math.Max(0.1, Math.Min(4.0, PrefDouble("whiteG", 1.0)));
            c.WhiteB = Math.Max(0.1, Math.Min(4.0, PrefDouble("whiteB", 1.0)));
            c.BandFrac = PrefDouble("bandFrac", 0.08);
            if (c.BandFrac <= 0.0) c.BandFrac = 0.08;
            if (c.BandFrac > 0.5) c.BandFrac = 0.5;

            // ---- endpoints: all empty by default, so a fresh install sends nothing anywhere ----
            c.WledIp = PrefString("wledIp", "");
            c.WledPort = PrefInt("wledPort", WledOutput.DefaultPort);
            if (c.WledPort < 1 || c.WledPort > 65535) c.WledPort = WledOutput.DefaultPort;
            c.WledTimeout = PrefInt("wledTimeout", WledOutput.DefaultTimeoutSeconds);
            if (c.WledTimeout < 1) c.WledTimeout = 1;
            if (c.WledTimeout > 255) c.WledTimeout = 255;
            c.Endpoint = PrefString("probeEndpoint", "").TrimEnd('/');
            c.StatsIntervalSec = PrefInt("statsIntervalSec", 5);
            if (c.StatsIntervalSec < 1) c.StatsIntervalSec = 1;
            c.PowerUrl = PrefString("powerStateUrl", PowerStateProbe.DefaultUrl);
            c.ControlUrl = PrefString("controlUrl", "");

            // ---- mapping: which LED shows zone 0, and which way the ring runs ----
            c.MapOffset = PrefInt("mapOffset", 0);
            c.MapOffset = ((c.MapOffset % c.ZoneCount) + c.ZoneCount) % c.ZoneCount;
            c.MapReverse = PrefInt("mapReverse", 0) != 0;

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

        /// <summary>Zones (and LEDs) when neither ledCount nor zoneCount is configured.</summary>
        const int DefaultZoneCount = 73;
    }
}
