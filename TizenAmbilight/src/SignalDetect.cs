using System;

namespace TizenAmbilight
{
    /// <summary>
    /// Guesses, from the picture itself, how the video plane is encoded: PQ (HDR) or gamma (SDR), and
    /// full or limited range. Used where AvocHdr is unavailable; the TV's own flags win when present. The
    /// plane differs by source. Measured on the S90C 2026-11-16:
    ///   * PC HDMI input, KDE HDR on: BT.2020 PQ, FULL range (black 0, 203-nit white at 148).
    ///   * Jellyfin app, SDR file: BT.709 gamma, LIMITED range (black exactly 16, pure green 172/42/27).
    /// Decoding SDR as PQ made mid-tones far too bright and pushed 709 colours through a 2020->709 matrix
    /// (pure green came out #00ff91).
    ///
    /// RANGE. Normally decided by the TV (<c>ExternalLimited</c>, its effective HDMI black level via
    /// <c>AvocHdr</c>); the rest of this paragraph is the fallback.
    /// Full-range sources put real black at 0; limited-range video never goes far below 16.
    /// A frame with a noticeable share of pixels at or below 8 is full range. A frame whose darkest
    /// pixels pile up at 16 with nothing below 11 is limited. Limited needs more frames in a row
    /// because a full-range picture can contain dark grey with no true black.
    ///
    /// TRANSFER. Normally decided by the TV itself (<c>ExternalType</c>, from <c>AvocHdr</c>); the rest of
    /// this paragraph is the fallback for firmware without AVOC.
    /// PQ puts 203-nit white at about 0.58 of the range and a 1000-nit highlight at 0.75, so
    /// HDR10 content almost never fills 0.5% of the frame above 0.80 (about 1500 nits). SDR white sits
    /// at 1.0, so any bright SDR scene does. That gives positive evidence for SDR only; HDR is assumed
    /// again after <see cref="HdrReturnMs"/> without such evidence. A dark SDR scene mistaken for HDR
    /// costs little: below about 0.45 of the range the PQ and gamma decodes land within a few percent.
    /// </summary>
    public static class SignalDetect
    {
        /// <summary>0 = always SDR, 1 = always HDR (PQ), 2 = detect (config/token <c>hdr</c>).</summary>
        public static int HdrMode = 2;
        /// <summary>0 = detect, 1 = always full, 2 = always limited (config/token <c>range</c>).</summary>
        public static int RangeMode = 0;

        /// <summary>Detected state; read <see cref="UseHdr"/> / <see cref="UseLimited"/> for the
        /// effective values after the forced modes.</summary>
        public static bool DetHdr = true, DetLimited = false;
        /// <summary>The TV's own content type from <c>AvocHdr</c> (0 SDR, 1 HDR10, 2 HLG, 3 HDR10+, ...), or -1
        /// when unknown. In mode 2 it wins over the picture guess; only the range is still guessed.</summary>
        public static volatile int ExternalType = -1;
        public static bool UseHdr
        {
            get
            {
                if (HdrMode != 2) return HdrMode == 1;
                int t = ExternalType;
                // HLG (2) is gamma-like by design and is closer to the SDR path than to a PQ decode.
                if (t >= 0) return t != 0 && t != 2;
                return DetHdr;
            }
        }
        /// <summary>The TV's effective HDMI black level from <c>AvocHdr</c>: 1 = limited ("Low"), 0 = full
        /// ("Normal"), -1 = unknown. In range mode 0 it wins over the picture guess.</summary>
        public static volatile int ExternalLimited = -1;
        public static bool UseLimited
        {
            get
            {
                if (RangeMode != 0) return RangeMode == 2;
                int x = ExternalLimited;
                return x >= 0 ? x == 1 : DetLimited;
            }
        }

        public const double SdrLevel = 0.80;     // normalised p99.5 above this = SDR evidence
        public const int SdrFrames = 3;
        public const double FullBlackFrac = 0.002;
        public const int FullFrames = 2;
        public const double LimBlackFrac = 0.01;
        public const int LimFrames = 10;
        public static long HdrReturnMs = 20000;

        // telemetry
        public static double LastP995, LastLowFrac, LastBlack16Frac;
        public static int Switches;

        static int _sdrEv, _fullEv, _limEv;
        static long _lastSdrMs;
        static readonly int[] _hist = new int[256];

        public static void Reset()
        {
            DetHdr = true; DetLimited = false; _sdrEv = _fullEv = _limEv = 0; _lastSdrMs = 0;
            Switches = 0; LastP995 = LastLowFrac = LastBlack16Frac = 0; ExternalType = -1; ExternalLimited = -1;
        }

        /// <summary>Histogram a sparse grid (every 8th pixel each way: ~8k samples, well under 1 ms) of
        /// the active picture and update the detected state.</summary>
        public static void Observe(byte[] y, int stride, int top, int bottom, int left, int right, long nowMs)
        {
            if (y == null || bottom <= top || right <= left) return;
            Array.Clear(_hist, 0, 256);
            int n = 0;
            for (int row = top + 4; row < bottom; row += 8)
            {
                int o = row * stride;
                for (int x = left + 4; x < right; x += 8)
                {
                    int i = o + x;
                    if (i >= y.Length) break;
                    _hist[y[i]]++; n++;
                }
            }
            if (n < 64) return;
            ObserveHistogram(_hist, n, nowMs);
        }

        /// <summary>The decision on a ready histogram (split out for the host test harness).</summary>
        public static void ObserveHistogram(int[] hist, int n, long nowMs)
        {
            int low = 0, below11 = 0, b16 = 0;
            for (int v = 0; v <= 8; v++) low += hist[v];
            for (int v = 0; v <= 10; v++) below11 += hist[v];
            for (int v = 14; v <= 18; v++) b16 += hist[v];
            int target = n - n / 200, acc = 0, p995 = 255;
            for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= target) { p995 = v; break; } }
            LastLowFrac = (double)low / n; LastBlack16Frac = (double)b16 / n;

            bool wasHdr = DetHdr, wasLim = DetLimited;

            if (LastLowFrac >= FullBlackFrac) { _limEv = 0; if (++_fullEv >= FullFrames) DetLimited = false; }
            else
            {
                _fullEv = 0;
                if (below11 == 0 && LastBlack16Frac >= LimBlackFrac) { if (++_limEv >= LimFrames) DetLimited = true; }
                else _limEv = 0;
            }

            double norm = DetLimited ? (p995 - 16) / 219.0 : p995 / 255.0;
            LastP995 = norm;
            if (norm >= SdrLevel)
            {
                _lastSdrMs = nowMs;
                if (++_sdrEv >= SdrFrames) DetHdr = false;
            }
            else
            {
                _sdrEv = 0;
                if (!DetHdr && nowMs - _lastSdrMs >= HdrReturnMs) DetHdr = true;
            }

            if (wasHdr != DetHdr || wasLim != DetLimited) Switches++;
        }

        public static string Describe()
        {
            return (UseHdr ? "pq" : "sdr") + "/" + (UseLimited ? "limited" : "full") +
                   " (from " + (HdrMode != 2 ? "config" : ExternalType >= 0 ? "tv type " + ExternalType : "picture") +
                   "/" + (RangeMode != 0 ? "config" : ExternalLimited >= 0 ? "tv black level" : "picture") +
                   ", hdr=" + HdrMode + " range=" + RangeMode + " det=" + (DetHdr ? "pq" : "sdr") + "/" +
                   (DetLimited ? "limited" : "full") + ")";
        }
    }
}
