using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TizenAmbilight
{
    /// <summary>Outcome of one secvideo_api_capture_video_main call. The planes arrive in OUR
    /// buffers — there is no file and no encoder anywhere on this path.</summary>
    public sealed class VideoPlaneResult
    {
        public int Rc;                 // 0 == success for this API (measured on hardware, v15-v17)
        public int RetWidth;           // param+0x08, written by the library
        public int RetHeight;          // param+0x0c
        public int YPitch;             // param+0x2c — 960 on this TV
        public int CPitch;             // param+0x30 — 960 on this TV
        public int YStride;            // row stride to use for the Y plane (pitch, validated)
        public int CStride;            // row stride to use for the chroma plane
        public int Interlaced;         // param+0x28 — 0 on this TV
        public double CaptureMs;
        // The planes, handed over raw. There is deliberately NO RGBA conversion here any more:
        // converting 518,400 pixels to RGB only to average them down to 73 zone colours was the
        // single biggest cost in the pipeline (~115 ms/frame) and it averaged in gamma-encoded
        // space, which is not the same as averaging light. The zone engine now averages Y/U/V
        // per zone and runs the colour matrix 73 times instead of 518,400.
        public byte[] Y;
        public byte[] C;
        public int W, H;
        public string Error;

        public bool HasFrame { get { return Error == null && Rc == 0 && Y != null && C != null; } }

        public string Status
        {
            get
            {
                if (Error != null) return "vplane_error:" + Error;
                if (Rc != 0) return "vplane rc=" + Rc + " (not ready)";
                if (Y == null) return "vplane rc=0 but no planes";
                return "ok " + RetWidth + "x" + RetHeight + " YUV planes in memory (" +
                       CaptureMs.ToString("0.0") + " ms)";
            }
        }
    }

    /// <summary>
    /// THE VIDEO PLANE, in memory. The only native entry point on this path:
    ///
    ///     int secvideo_api_capture_video_main(int req_w, int req_h, secvideo_capture_param*)
    ///     from libvideo-capture.so.0.1.0  (3 args — verified from the disassembly, not assumed)
    ///
    /// Why this and not the alternatives (all measured on QN77S84FAEXZA, Tizen 9, 2026-09-11):
    ///   * EPScreenCapture::capture() -> getScreenPostYUV is the SCREEN/UI plane. It goes DARK over
    ///     video (mean 108.5 idle -> 24.7 during playback). Wrong plane for an ambilight.
    ///   * dc_request_capture_to_file_sync works but writes a PNG file: ~126 ms/frame end to end
    ///     (encode + write + decode) at 480x270.
    ///   * HyperTizen samples 16 points off the video plane at ~4-12 fps.
    ///   THIS call returns the real video plane, 960x540, into caller-owned buffers, with no file.
    ///   During playback the frame renders as film content (verified: a title sequence with readable
    ///   place-name text). Measured 60/60 successes in 988 ms on one run — 16.5 ms/frame — though the
    ///   per-call cost is variable and pacing-sensitive, so treat that as best-observed, not typical.
    ///
    /// secvideo_capture_param layout — PROVEN from Samsung's own decompiled
    /// Tizen.TV.System.ContentAnalysis.dll, then confirmed on device by reading back the fields the
    /// library fills in (ret_y_pitch = 960, ret_c_pitch = 960, ret_interlaced = 0):
    ///   +0x00 u32 uYSize      +0x04 u32 uCSize    +0x08 int ret_width   +0x0c int ret_height
    ///   +0x10 ptr pYAddr      +0x14 ptr pCAddr    +0x18 int no_lock_no_copy
    ///   +0x1c u32 color_format                    +0x20 u32 protected_on
    ///   +0x24 u32 capt_3dmod  +0x28 u32 ret_interlaced  +0x2c u32 ret_y_pitch
    ///   +0x30 u32 ret_c_pitch +0x34 u64 timestamp_nsec (ARM32 alignment of this tail is NOT trusted;
    ///                                                     the struct is zeroed and only the four
    ///                                                     fields the partner code itself sets are written)
    ///
    /// Chroma layout — settled on device: NV12 INTERLEAVED, pitch 960, non-interlaced. Confirmed two
    /// independent ways: autocorrelation of the exfiltrated plane (lag1 = -0.95, lag2 = +0.98 =>
    /// period-2 byte alternation) and the library's own ret_c_pitch = 960 (960 bytes/row x 270 rows
    /// = 259200 = exactly the plane we allocate).
    ///
    /// SAFETY: the only native calls are dlopen/dlsym and this one function. Buffers are ours.
    /// No file is written, no /dev node is opened here, and no lock is taken (lockGlobal was measured
    /// to be actively harmful: 0/15 successes at ~1016 ms per call).
    /// </summary>
    public static class VideoPlaneCapture
    {
        public const string BackendName = "video_plane";
        public const string LibName = "libvideo-capture.so.0.1.0";
        public const string Symbol = "secvideo_api_capture_video_main";

        // 960x540 is the size the partner (Samsung's own Tizen.TV.System.ContentAnalysis) requests,
        // and the size that came back filled on hardware.
        /// <summary>Maximum plane size - the size the buffers are allocated at, once, at init.
        /// Never shrink this: the param's Y/C size fields advertise these, and a smaller request
        /// still writes into a buffer at least this large.</summary>
        public const int PlaneW = 960;
        public const int PlaneH = 540;
        public const int YBytes = PlaneW * PlaneH;      // 518400
        public const int CBytes = YBytes / 2;           // 259200, NV12 interleaved

        const int StructBytes = 256;

        const int OffYSize = 0x00;
        const int OffCSize = 0x04;
        const int OffRetWidth = 0x08;
        const int OffRetHeight = 0x0c;
        const int OffYAddr = 0x10;
        const int OffCAddr = 0x14;
        const int OffInterlaced = 0x28;
        const int OffYPitch = 0x2c;
        const int OffCPitch = 0x30;

        const int RTLD_LAZY = 0x00001;
        const int RTLD_NOW = 0x00002;

        [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
        static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        static extern IntPtr dlerror();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int CaptureVideoMainFn(int reqW, int reqH, IntPtr param);

        static IntPtr _lib = IntPtr.Zero;
        static IntPtr _fn = IntPtr.Zero;
        static IntPtr _param = IntPtr.Zero;
        static IntPtr _bufY = IntPtr.Zero;
        static IntPtr _bufC = IntPtr.Zero;
        static CaptureVideoMainFn _call;

        /// <summary>Requested capture size. Configurable at runtime (control token `cw`/`ch`) because
        /// the hardware scaler does the downsampling for us - we only ever consume 73 zone averages,
        /// so requesting the full 960x540 may be wasted work in the capture path.</summary>
        public static int ReqW = PlaneW;
        public static int ReqH = PlaneH;

        /// <summary>Clamp a requested size so it fits the allocated buffers: W*H <= PlaneW*PlaneH.
        /// Returns false if the pair is unusable.</summary>
        public static bool SetRequestSize(int w, int h)
        {
            if (w < 64 || h < 64) return false;
            if ((long)w * h > (long)PlaneW * PlaneH) return false;
            ReqW = w; ReqH = h;
            return true;
        }

        static readonly byte[] _y = new byte[YBytes];
        static readonly byte[] _c = new byte[CBytes];

        public static string ResolvedPath = "";

        // ---- color_format: the field we never set, and the suspected chroma gate ----
        // secvideo_capture_param has color_format at +0x1c and we have sent 0 in every call ever
        // made. On the first capture the app now sweeps 0..5, measures the chroma SPREAD each value
        // produces, and keeps the best. If every value is flat the field is not the gate and the app
        // says so in its stats/status rather than pretending colour is coming.
        const int OffColorFormat = 0x1c;
        public static int ChosenColorFormat = -1;
        public static double ChosenChromaSpread = -1;
        public static string ColorFormatVerdict = "not probed";
        static bool _cfProbed;
        public static readonly List<object> ColorFormatProbe = new List<object>();
        public static IntPtr FunctionPointer { get { return _fn; } }
        public static bool Ready { get { return _call != null; } }

        /// <summary>Idempotent. dlopen + dlsym + allocate the param struct and both planes ONCE.
        /// Returns null on success, or the error text.</summary>
        public static string Init()
        {
            if (_call != null) return null;

            try
            {
                IntPtr err0 = dlerror();
                _lib = dlopen(LibName, RTLD_NOW);
                if (_lib == IntPtr.Zero)
                {
                    _lib = dlopen(LibName, RTLD_LAZY);
                    if (_lib == IntPtr.Zero)
                    {
                        _lib = dlopen("/usr/lib/" + LibName, RTLD_LAZY);
                        if (_lib == IntPtr.Zero) return "dlopen " + LibName + " failed";
                    }
                }
                ResolvedPath = LibName;

                IntPtr e1 = dlerror();
                _fn = dlsym(_lib, Symbol);
                if (_fn == IntPtr.Zero) return "dlsym " + Symbol + " failed";

                _param = Marshal.AllocHGlobal(StructBytes);
                _bufY = Marshal.AllocHGlobal(YBytes);
                _bufC = Marshal.AllocHGlobal(CBytes);
                if (_param == IntPtr.Zero || _bufY == IntPtr.Zero || _bufC == IntPtr.Zero)
                    return "allocation failed";

                for (int i = 0; i < StructBytes; i++) Marshal.WriteByte(_param, i, 0);

                Marshal.WriteInt32(_param, OffYSize, YBytes);
                Marshal.WriteInt32(_param, OffCSize, CBytes);
                Marshal.WriteInt32(_param, OffYAddr, _bufY.ToInt32());
                Marshal.WriteInt32(_param, OffCAddr, _bufC.ToInt32());

                _call = (CaptureVideoMainFn)Marshal.GetDelegateForFunctionPointer(
                            _fn, typeof(CaptureVideoMainFn));
                return null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>Measure the chroma spread one color_format value produces. Returns -1 if the
        /// call did not succeed. Settled on hardware: every value 0..5 produced byte-identical chroma,
        /// so color_format is NOT the gate on this device.</summary>
        static double ChromaSpreadFor(int cf)
        {
            Marshal.WriteInt32(_param, OffColorFormat, cf);
            for (int i = 0; i < CBytes; i++) Marshal.WriteByte(_bufC, i, 0);   // a no-write must be visible
            int rc = _call(ReqW, ReqH, _param);
            var rec = new Dictionary<string, object> { { "color_format", (long)cf }, { "rc", (long)rc } };
            if (rc != 0) { ColorFormatProbe.Add(rec); return -1; }

            Marshal.Copy(_bufC, _c, 0, CBytes);
            long us = 0, vs = 0, us2 = 0, vs2 = 0; long n = 0;
            int umin = 255, umax = 0, vmin = 255, vmax = 0;
            for (int i = 0; i + 1 < CBytes; i += 2)
            {
                byte u = _c[i], v = _c[i + 1];
                us += u; us2 += (long)u * u; vs += v; vs2 += (long)v * v; n++;
                if (u < umin) umin = u; if (u > umax) umax = u;
                if (v < vmin) vmin = v; if (v > vmax) vmax = v;
            }
            double um = (double)us / n, vm = (double)vs / n;
            double ust = Math.Sqrt(Math.Max(0, (double)us2 / n - um * um));
            double vst = Math.Sqrt(Math.Max(0, (double)vs2 / n - vm * vm));
            rec["U_mean"] = Math.Round(um, 2); rec["U_stdev"] = Math.Round(ust, 3);
            rec["U_range"] = umin + "-" + umax;
            rec["V_mean"] = Math.Round(vm, 2); rec["V_stdev"] = Math.Round(vst, 3);
            rec["V_range"] = vmin + "-" + vmax;
            rec["chroma_spread"] = Math.Round(ust + vst, 3);
            ColorFormatProbe.Add(rec);
            return ust + vst;
        }

        static void ProbeColorFormatOnce()
        {
            _cfProbed = true;
            if (_call == null) { ColorFormatVerdict = "no call"; return; }
            double best = -1; int bestCf = 0; int ok = 0;
            for (int cf = 0; cf <= 5; cf++)
            {
                double sp = ChromaSpreadFor(cf);
                if (sp >= 0) { ok++; if (sp > best) { best = sp; bestCf = cf; } }
            }
            if (ok == 0)
            {
                ChosenColorFormat = 0; ChosenChromaSpread = -1;
                ColorFormatVerdict = "every color_format failed - capture is wedged (reboot needed)";
            }
            else if (best > 8.0)
            {
                ChosenColorFormat = bestCf; ChosenChromaSpread = best;
                ColorFormatVerdict = "color_format=" + bestCf + " gives real chroma (spread " +
                                     Math.Round(best, 2) + ") - USING IT";
            }
            else
            {
                ChosenColorFormat = 0; ChosenChromaSpread = best;
                ColorFormatVerdict = "flat for EVERY value (best " + Math.Round(best, 2) +
                                     " <= 8): color_format is not the gate, the plane carries no usable chroma";
            }
            Marshal.WriteInt32(_param, OffColorFormat, ChosenColorFormat);
        }

        /// <summary>One frame. NO file, NO encoder, NO decoder: the library writes the video plane
        /// straight into buffers this process owns and the planes are handed over RAW (the RGBA
        /// conversion this once did was removed - it cost ~115 ms/frame to produce 73 numbers).
        /// Rc is 0 on success; -1 is "not ready" and is NOT an error.
        ///
        /// NOTE: the returned Y/C reference these class's STATIC buffers, so a caller must consume
        /// them before the next Capture(). The render loop does (it reads them in the same
        /// iteration). Holding a result across a capture would observe mutating data.</summary>
        public static VideoPlaneResult Capture()
        {
            if (!_cfProbed) ProbeColorFormatOnce();
            var r = new VideoPlaneResult();
            if (_call == null) { r.Rc = -1; r.Error = "not initialised"; return r; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                r.Rc = _call(ReqW, ReqH, _param);
            }
            catch (Exception e)
            {
                r.Rc = -1;
                r.Error = "native: " + e.GetType().Name + ": " + e.Message;
                return r;
            }

            try
            {
                r.RetWidth = Marshal.ReadInt32(_param, OffRetWidth);
                r.RetHeight = Marshal.ReadInt32(_param, OffRetHeight);
                r.Interlaced = Marshal.ReadInt32(_param, OffInterlaced);
                r.YPitch = Marshal.ReadInt32(_param, OffYPitch);
                r.CPitch = Marshal.ReadInt32(_param, OffCPitch);
            }
            catch { /* param readback only: a failure here leaves the fields 0 and the app still runs */ }

            if (r.Rc != 0) { sw.Stop(); r.CaptureMs = sw.Elapsed.TotalMilliseconds; return r; }

            try
            {
                Marshal.Copy(_bufY, _y, 0, YBytes);
                Marshal.Copy(_bufC, _c, 0, CBytes);
                // Report what the library ACTUALLY produced, falling back to the request. The zone
                // layout is built from these, so a size change is picked up automatically.
                int aw = Marshal.ReadInt32(_param, OffRetWidth);
                int ah = Marshal.ReadInt32(_param, OffRetHeight);
                r.W = (aw > 0 ? aw : ReqW);
                r.H = (ah > 0 ? ah : ReqH);
                // ABI SANITY CLAMP. If a firmware upgrade changes the param layout these fields could
                // come back as anything. Clamp rather than trust: an out-of-range width would otherwise
                // shear every zone read. Extraction also bounds-checks against the buffer, so a bad
                // value degrades rather than corrupting memory.
                bool abiOdd = false;
                if (r.W < 64 || r.W > PlaneW) { abiOdd = true; r.W = ReqW; }
                if (r.H < 64 || r.H > PlaneH) { abiOdd = true; r.H = ReqH; }
                if (abiOdd && !_abiWarned)
                {
                    _abiWarned = true;
                    r.Error = "ABI: unexpected returned dims " + aw + "x" + ah + " - using " + r.W + "x" + r.H;
                }
                // Stride: the reported pitch when it is sane, else assume pitch == width. Only
                // reported before - if the pitch ever differs, reading rows at `width` would shear
                // the picture.
                int yp = Marshal.ReadInt32(_param, OffYPitch);
                int cp = Marshal.ReadInt32(_param, OffCPitch);
                r.YStride = (yp >= r.W && yp <= PlaneW) ? yp : r.W;
                r.CStride = (cp >= r.W && cp <= PlaneW) ? cp : r.W;
                r.Y = _y; r.C = _c;
            }
            catch (Exception e)
            {
                r.Error = "plane copy: " + e.GetType().Name + ": " + e.Message;
            }

            sw.Stop();
            r.CaptureMs = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        static int[] _hist;

        /// <summary>Median luma of the frame, as a scene-brightness statistic.
        ///
        /// Shape follows the established cheap auto-exposure form: ONE O(samples) pass of histogram
        /// increments (here 64 bins, which Unreal uses and Narkowicz notes is enough to cover a wide
        /// range) followed by a 64-entry scan - NO SORT, no second full-frame pass. Samples are taken
        /// with a stride, so ~3.6k of the 518k pixels are read (~0.02 ms), against a 6.4 ms zone budget.
        ///
        /// The MEDIAN rather than the mean: the published weakness of a mean statistic is that small
        /// bright areas (specular highlights, a lamp in frame) bias it badly, and the established fix is
        /// percentile filtering (Unreal Low/High Percent, Unity HDRP discarding outliers by percentile).
        /// The median is the most robust single percentile and is what a scene-brightness estimate wants.</summary>
        public static int MedianLuma(byte[] y, int w, int h)
        {
            if (y == null || w <= 0 || h <= 0) return 0;
            if (_hist == null) _hist = new int[64];
            for (int i = 0; i < 64; i++) _hist[i] = 0;

            int n = 0;
            for (int row = 0; row < h; row += 12)
            {
                int b = row * w;
                for (int x = 0; x < w; x += 12)
                {
                    int idx = b + x;
                    if (idx >= y.Length) break;
                    _hist[y[idx] >> 2]++;          // 256 codes -> 64 bins
                    n++;
                }
            }
            if (n == 0) return 0;
            int half = n / 2, acc = 0;
            for (int i = 0; i < 64; i++)
            {
                acc += _hist[i];
                if (acc >= half) return (i << 2) + 2;    // bin centre
            }
            return 255;
        }

        /// <summary>Absolute luminance (nits) of a PQ code, for reporting the scene's real brightness.</summary>
        public static double PqNits(int code)
        {
            const double m1 = 2610.0 / 16384.0;
            const double m2 = 2523.0 / 4096.0 * 128.0;
            const double c1 = 3424.0 / 4096.0;
            const double c2 = 2413.0 / 4096.0 * 32.0;
            const double c3 = 2392.0 / 4096.0 * 32.0;
            double N = (code < 0 ? 0 : (code > 255 ? 255 : code)) / 255.0;
            double Nm = Math.Pow(N, 1.0 / m2);
            double num = Nm - c1; if (num < 0.0) num = 0.0;
            double den = c2 - c3 * Nm;
            return den > 1e-9 ? 10000.0 * Math.Pow(num / den, 1.0 / m1) : 0.0;
        }

        /// <summary>PQ (SMPTE ST 2084) -> SDR, using the BT.1886 display gamma 2.4 and referencing
        /// 203 nits as diffuse white (the HDR convention for "graphics white").
        ///
        /// WHY THIS EXISTS: the captured plane carries PQ-encoded HDR, not SDR. Measured on two
        /// unrelated contents the plane's luma topped out at 146-148, and 148.1/255 is exactly the PQ
        /// code for 203 nits - a transfer-function ceiling, not scene content. Decoding PQ as BT.601
        /// limited-range threw away the entire upper half of the signal (code 148 came out as 154
        /// instead of 255), which is exactly what "washed out, nothing like the screen" looks like.</summary>
        /// <summary>Reference luminance mapped to full scale. 203 nits is the HDR convention for
        /// diffuse white, which is why it is the default. Content varies ABOVE this (highlights run
        /// 203-4000 nits), and anything over the reference CLIPS - which does not merely lose detail,
        /// it pins all three channels to 255 and a bright coloured zone renders as WHITE. Raise it for
        /// very bright films (at the cost of a dimmer picture; raise `gamma` to compensate), lower it
        /// for dim ones. Live-tunable so the choice is per-content rather than a guess.</summary>
        public static double HdrPeakNits = 203.0;

        static bool _abiWarned;

        static byte[] _pqTable;
        static double _pqTablePeak = -1.0;

        static byte[] PqToSdr()
        {
            if (_pqTable != null && _pqTablePeak == HdrPeakNits) return _pqTable;
            _pqTable = BuildPqToSdrTable();
            _pqTablePeak = HdrPeakNits;
            return _pqTable;
        }

        static byte[] BuildPqToSdrTable()
        {
            const double m1 = 2610.0 / 16384.0;
            const double m2 = 2523.0 / 4096.0 * 128.0;
            const double c1 = 3424.0 / 4096.0;
            const double c2 = 2413.0 / 4096.0 * 32.0;
            const double c3 = 2392.0 / 4096.0 * 32.0;
            double refNits = HdrPeakNits <= 0.0 ? 203.0 : HdrPeakNits;
            var t = new byte[256];
            for (int code = 0; code < 256; code++)
            {
                double N = code / 255.0;
                double Nm = Math.Pow(N, 1.0 / m2);
                double num = Nm - c1; if (num < 0.0) num = 0.0;
                double den = c2 - c3 * Nm;
                double nits = den > 1e-9 ? 10000.0 * Math.Pow(num / den, 1.0 / m1) : 0.0;
                double x = nits / refNits;
                // Above the 203-nit reference we clip. Further highlight tone-mapping is taste and
                // belongs downstream, not inside the transfer function.
                double outp = x <= 0.0 ? 0.0 : 255.0 * Math.Pow(x, 1.0 / 2.4);
                if (outp > 255.0) outp = 255.0;
                t[code] = (byte)(outp + 0.5);
            }
            return t;
        }

        static int Clamp255(double x)
        {
            if (x <= 0.0) return 0;
            if (x >= 255.0) return 255;
            return (int)(x + 0.5);
        }

        /// <summary>BT.601 limited-range YUV -> RGB for ONE already-averaged colour. Integer maths,
        /// no toolkit. Chroma is EXPANDED about neutral by <paramref name="saturation"/> first —
        /// doing it here (in chroma space, once per zone) is what fights the desaturation that
        /// per-channel averaging causes across a mixed zone. Per-channel white balance multiplies
        /// the result: that is the only place the strip's own colour calibration can be corrected.
        /// </summary>
        public static void YuvToRgb(int Y, int U, int V, double saturation,
                                    double kr, double kg, double kb, bool hdr, bool gamut709,
                                    out byte rOut, out byte gOut, out byte bOut)
        {
            // Clamp to >= 0 rather than forcing 1.0: `sat=0` is a legal control value (the parser
            // accepts 0..4) and means "no chroma", but forcing it to 1.0 made the setting silently do
            // nothing. Negative would INVERT the chroma axes, which is never intended.
            if (saturation < 0) saturation = 0;
            double u = 128.0 + (U - 128.0) * saturation;
            double v = 128.0 + (V - 128.0) * saturation;

            int R, G, B;
            if (hdr)
            {
                // HDR path: BT.2020 non-constant-luminance, FULL range, matrixed in the PQ CODE domain.
                double cb = u - 128.0, cr = v - 128.0;
                int R0 = Clamp255(Y + 1.4746 * cr);
                int G0 = Clamp255(Y - 0.164553 * cb - 0.571353 * cr);
                int B0 = Clamp255(Y + 1.8814 * cb);

                // TONE-MAP THE LUMINANCE, SCALE RGB BY THE SAME FACTOR. NOT per channel.
                //
                // A convex transfer applied independently to R, G and B WIDENS the gaps between them,
                // which INVENTS chroma. Measured on real content: +18.4% mean saturation versus this
                // method (0.455 vs 0.384), and the error is worst exactly where it matters - the most
                // saturated zones. A warm orange [131,80,40] became [113,85,56]: red pushed, blue
                // suppressed, i.e. garish instead of authentic. This is the classic "per-channel tone
                // mapping oversaturates" artifact.
                //
                // Scaling every channel by one factor derived from the LUMA keeps hue and chromaticity
                // intact while still applying the whole PQ->SDR range compression, so the picture keeps
                // its contrast and punch without the invented colour.
                double L0 = 0.2627 * R0 + 0.6780 * G0 + 0.0593 * B0;   // BT.2020 luma coefficients
                byte[] T = PqToSdr();
                if (L0 <= 0.5)
                {
                    int nn = T[Clamp255(L0)];      // neutral: nothing to preserve
                    R = nn; G = nn; B = nn;
                }
                else
                {
                    double k = T[Clamp255(L0)] / L0;
                    R = Clamp255(R0 * k);
                    G = Clamp255(G0 * k);
                    B = Clamp255(B0 * k);
                }

                if (gamut709) ApplyGamut709(ref R, ref G, ref B);
            }
            else
            {
                int yy = Y - 16; if (yy < 0) yy = 0;
                int uu = (int)Math.Round(u) - 128;
                int vv = (int)Math.Round(v) - 128;

                R = (298 * yy + 409 * vv + 128) >> 8;
                G = (298 * yy - 100 * uu - 208 * vv + 128) >> 8;
                B = (298 * yy + 516 * uu + 128) >> 8;
            }

            R = (int)Math.Round(R * kr);
            G = (int)Math.Round(G * kg);
            B = (int)Math.Round(B * kb);

            if (R < 0) R = 0; else if (R > 255) R = 255;
            if (G < 0) G = 0; else if (G > 255) G = 255;
            if (B < 0) B = 0; else if (B > 255) B = 255;
            rOut = (byte)R; gOut = (byte)G; bOut = (byte)B;
        }

        /// <summary>BT.2020 primaries -> BT.709 primaries, in LINEAR light.
        ///
        /// HDR content is mastered in BT.2020, whose primaries are WIDER; feeding those triples to a
        /// 709/sRGB strip renders the same numbers as a LESS saturated colour. Measured +39% chroma
        /// spread on a real frame once converted. The matrix belongs in LINEAR light, hence the
        /// decode/re-encode around it.
        ///
        /// Extracted as its own method so the HDR path can decode per PIXEL (unbiased) and then apply
        /// the gamut to the zone mean, rather than being forced to do everything per pixel.</summary>
        public static void ApplyGamut709(ref int R, ref int G, ref int B)
        {
            double rl = Math.Pow(R / 255.0, 2.4);
            double gl = Math.Pow(G / 255.0, 2.4);
            double bl = Math.Pow(B / 255.0, 2.4);
            double r7 = 1.6605 * rl - 0.5876 * gl - 0.0728 * bl;
            double g7 = -0.1246 * rl + 1.1329 * gl - 0.0083 * bl;
            double b7 = -0.0182 * rl - 0.1006 * gl + 1.1187 * bl;

            // SOFT GAMUT MAP. Widening into 709 pushes saturated BT.2020 colours OUTSIDE the 709 gamut,
            // i.e. negative linear channels. Clamping those to zero CRUSHES them (measured 11-34% of
            // zones losing a channel to black = lost shadow detail). Desaturate toward the colour's own
            // luminance by the SMALLEST amount that brings it back in gamut: hue survives, nothing is
            // crushed.
            double lum = 0.2126 * r7 + 0.7152 * g7 + 0.0722 * b7;
            double t = 0.0;
            double dc;
            dc = r7; if (dc < 0.0) { double need = (lum - dc) > 1e-9 ? (-dc) / (lum - dc) : 1.0; if (need > t) t = need; }
            dc = g7; if (dc < 0.0) { double need = (lum - dc) > 1e-9 ? (-dc) / (lum - dc) : 1.0; if (need > t) t = need; }
            dc = b7; if (dc < 0.0) { double need = (lum - dc) > 1e-9 ? (-dc) / (lum - dc) : 1.0; if (need > t) t = need; }
            dc = r7; if (dc > 1.0) { double need = (lum - dc) < -1e-9 ? (dc - 1.0) / (dc - lum) : 1.0; if (need > t) t = need; }
            dc = g7; if (dc > 1.0) { double need = (lum - dc) < -1e-9 ? (dc - 1.0) / (dc - lum) : 1.0; if (need > t) t = need; }
            dc = b7; if (dc > 1.0) { double need = (lum - dc) < -1e-9 ? (dc - 1.0) / (dc - lum) : 1.0; if (need > t) t = need; }
            if (t > 1.0) t = 1.0;
            r7 = r7 + (lum - r7) * t;
            g7 = g7 + (lum - g7) * t;
            b7 = b7 + (lum - b7) * t;
            if (r7 < 0.0) r7 = 0.0; else if (r7 > 1.0) r7 = 1.0;
            if (g7 < 0.0) g7 = 0.0; else if (g7 > 1.0) g7 = 1.0;
            if (b7 < 0.0) b7 = 0.0; else if (b7 > 1.0) b7 = 1.0;
            R = Clamp255(255.0 * Math.Pow(r7, 1.0 / 2.4));
            G = Clamp255(255.0 * Math.Pow(g7, 1.0 / 2.4));
            B = Clamp255(255.0 * Math.Pow(b7, 1.0 / 2.4));
        }

        /// <summary>Find the ACTIVE picture area vertically, i.e. strip off letterbox bars.
        ///
        /// Why this matters more than any tuning knob: on a 2.39:1 film in a 16:9 frame the bars are
        /// ~69 px tall while our border bands are only 43 px deep - so the top 23 and bottom 24 zones
        /// sample PURE BLACK, 47 of 73 zones, and no amount of gain or saturation can recover them.
        ///
        /// A row counts as bar if >=90% of its sampled pixels are below `thr`. That "nearly all dark"
        /// test is deliberate: a merely dark scene has variation, bars are uniform near-zero. Looking
        /// for uniformity rather than darkness is what stops a night scene being mistaken for bars.
        ///
        /// Bars are capped at a quarter of the height each, so a fully black screen cannot be
        /// misread as "entirely bar" and collapse the picture area to nothing.
        /// </summary>
        public const int MinBarPx = 12;

        /// <summary>Column test for PILLARBOX bars (left/right), the horizontal mirror of RowIsBar.
        ///
        /// Stricter than the row test on purpose: it requires EVERY sampled pixel to be dark, not 90%
        /// of them. The looser row version is what produced false positives that cropped real picture
        /// on dark rows, so the column test demands uniformity AND darkness. A genuinely black bar is
        /// flat at the black level (measured: a whole bar column reads a constant code 16), whereas
        /// even a dark night scene carries some structure that exceeds the threshold.</summary>
        static bool ColIsBar(byte[] y, int col, int w, int h, int step, int thr)
        {
            int dark = 0, cnt = 0;
            for (int row = 0; row < h; row += step)   // NB: not `y` - that is the plane parameter
            {
                int idx = row * w + col;
                if (idx < 0 || idx >= y.Length) return false;
                if (y[idx] < thr) dark++;
                cnt++;
            }
            return cnt > 0 && dark == cnt;          // ALL sampled pixels dark
        }

        /// <summary>Find the active picture area HORIZONTALLY (strip pillarbox bars). Mirrors
        /// DetectActiveArea with the same guards: bars capped at a quarter of the width each, slivers
        /// below MinBarPx ignored, and never collapse the picture below a third of the frame.</summary>
        public static void DetectActiveAreaX(byte[] y, int w, int h, int thr, out int xLeft, out int xRight)
        {
            int step = 8;
            int maxBar = w / 4;
            xLeft = 0; xRight = w;

            int l = 0;
            while (l < maxBar && ColIsBar(y, l, w, h, step, thr)) l++;
            xLeft = (l >= maxBar) ? 0 : l;

            int r = w - 1, run = 0;
            while (run < maxBar && r > 0 && ColIsBar(y, r, w, h, step, thr)) { r--; run++; }
            xRight = (run >= maxBar) ? w : (r + 1);

            if (xRight - xLeft < w / 3) { xLeft = 0; xRight = w; }   // never collapse the picture
            if (xLeft < MinBarPx) xLeft = 0;
            if (w - xRight < MinBarPx) xRight = w;
        }

        public static void DetectActiveArea(byte[] y, int w, int h, int thr, out int yTop, out int yBottom)
        {
            int step = 8;
            int maxBar = h / 4;
            yTop = 0; yBottom = h;

            int t = 0;
            while (t < maxBar && RowIsBar(y, t, w, step, thr)) t++;
            yTop = (t >= maxBar) ? 0 : t;

            int b = h - 1;
            int run = 0;
            while (run < maxBar && b > 0 && RowIsBar(y, b, w, step, thr)) { b--; run++; }
            yBottom = (run >= maxBar) ? h : (b + 1);

            if (yBottom - yTop < h / 3) { yTop = 0; yBottom = h; }   // sanity: never collapse the picture

            // Tiny "bars" are noise, not letterboxing. A 2-3 px sliver usually means a dark row at the
            // very top/bottom of the picture, and cropping to it only shifts the sampling window for no
            // benefit - which shows up as the strip twitching.
            if (yTop < MinBarPx) yTop = 0;
            if (h - yBottom < MinBarPx) yBottom = h;
        }

        static bool RowIsBar(byte[] y, int row, int w, int step, int thr)
        {
            int baseIdx = row * w;
            if (baseIdx < 0 || baseIdx + w > y.Length) return false;
            // EVERY sampled pixel must be below the threshold - not "90% of them".
            //
            // The 90% rule is what made this crop REAL PICTURE: on a dark film (row means 21-26) a
            // scene would satisfy it while genuinely containing picture, the crop would follow the
            // scene's brightness (measured drifting 50 -> 71 rows in 7 s) and the ambience geometry
            // lurched with it. A real letterbox bar is FLAT at the black level, so demanding flatness
            // costs nothing for true bars and rejects dark-but-structured picture.
            int dark = 0, cnt = 0;
            for (int x = 0; x < w; x += step)
            {
                if (y[baseIdx + x] < thr) dark++;
                cnt++;
            }
            return cnt > 0 && dark == cnt;
        }

        public static void Shutdown()
        {
            foreach (IntPtr p in new IntPtr[] { _param, _bufY, _bufC })
                if (p != IntPtr.Zero) { try { Marshal.FreeHGlobal(p); } catch { } }
            _param = _bufY = _bufC = IntPtr.Zero;
            _call = null;
        }
    }
}
