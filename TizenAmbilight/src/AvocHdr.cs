using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TizenAmbilight
{
    /// <summary>
    /// The TV's own record of the current content's dynamic range, from the AV output control service
    /// (avocd) through <c>/usr/lib/libavoc.so</c>. Found 2026-11-16 by scanning the S90C firmware
    /// for exported strings; the signature is copied from Samsung's wrapper in Tizen.TV.System.dll
    /// (<c>Interop.Avocd.avoc_get_hdr_type(out AvocHDRTypee)</c>). Measured: rc 0 and under 1 ms per call
    /// from this app, value 1 while the Jellyfin app played an HDR10 file and 0 within one poll of switching
    /// to an SDR file.
    ///
    /// The call is a synchronous request to a system service, so it runs on its own thread, never in the
    /// render loop. If the library or symbol is missing (other models), or the call fails, the poller stops
    /// and <see cref="SignalDetect"/> falls back to guessing from the picture.
    /// </summary>
    public static class AvocHdr
    {
        [DllImport("/usr/lib/libavoc.so", CallingConvention = CallingConvention.Cdecl)]
        static extern int avoc_get_hdr_type(out int type);

        // Signatures read from libavoc's disassembly (no Samsung wrapper exists): both send their int
        // arguments to avocd and write one int. Measured 2026-11-16: black level 1 ("Low", limited range,
        // plane black at Y=16) in the Jellyfin app and 2 ("Normal", full range, black at 0) on the PC's HDMI
        // input; pc_mode 0 and 1 respectively.
        [DllImport("/usr/lib/libavoc.so", CallingConvention = CallingConvention.Cdecl)]
        static extern int avoc_get_hdmi_black_level(int a, out int v, int b);
        [DllImport("/usr/lib/libavoc.so", CallingConvention = CallingConvention.Cdecl)]
        static extern int avoc_get_pc_mode(int unused, out int v);

        public static volatile int LastBlackLevel = -1, LastPcMode = -1;

        /// <summary>AvocHDRTypee: 0 SDR, 1 HDR10, 2 HLG, 3 HDR10+, 4 reserved, 5 CUVA, 6 FreeSync, 7 HDR10+ gaming.</summary>
        public static readonly string[] Names = { "sdr", "hdr10", "hlg", "hdr10+", "reserved", "cuva", "freesync", "hdr10+gaming" };

        public static volatile int LastRc = int.MinValue;
        public static volatile int LastType = -1;
        public static long Polls, Failures, MaxMs;
        public static string Status = "not started";

        public static void Start(int intervalMs = 500)
        {
            var t = new Thread(() => Loop(intervalMs)) { IsBackground = true, Name = "avoc-hdr" };
            t.Start();
        }

        static void Loop(int intervalMs)
        {
            int consecutiveFail = 0;
            while (true)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int type;
                    int rc = avoc_get_hdr_type(out type);
                    long ms = sw.ElapsedMilliseconds;
                    Polls++;
                    if (ms > MaxMs) MaxMs = ms;
                    LastRc = rc;
                    if (rc == 0 && type >= 0 && type < 64)
                    {
                        consecutiveFail = 0;
                        if (type != LastType)
                            Ambilight.L("avoc: content " + Name(LastType) + " -> " + Name(type));
                        LastType = type;
                        SignalDetect.ExternalType = type;
                        Status = "ok";
                    }
                    else
                    {
                        Failures++;
                        if (++consecutiveFail >= 3) { SignalDetect.ExternalType = -1; Status = "rc " + rc; }
                    }
                }
                catch (Exception e)
                {
                    // DllNotFound / EntryPointNotFound: this firmware has no AVOC; picture detection only.
                    SignalDetect.ExternalType = -1;
                    Status = "unavailable: " + e.GetType().Name;
                    Ambilight.L("avoc: " + Status + " " + e.Message);
                    return;
                }
                PollRange();
                Thread.Sleep(consecutiveFail > 0 ? 2000 : intervalMs);
            }
        }

        /// <summary>Black level 1 = limited, 2 = full; anything else (or a failed call) leaves the range to
        /// the picture guess. Failures here never stop the HDR poll.</summary>
        static void PollRange()
        {
            try
            {
                int v, pc;
                int rc = avoc_get_hdmi_black_level(0, out v, 0);
                int bl = rc == 0 ? v : -1;
                if (bl != LastBlackLevel) Ambilight.L("avoc: black level " + LastBlackLevel + " -> " + bl);
                LastBlackLevel = bl;
                SignalDetect.ExternalLimited = bl == 1 ? 1 : bl == 2 ? 0 : -1;
                LastPcMode = avoc_get_pc_mode(0, out pc) == 0 ? pc : -1;
            }
            catch (Exception) { SignalDetect.ExternalLimited = -1; }
        }

        public static string Name(int t) { return t >= 0 && t < Names.Length ? Names[t] : t.ToString(); }
    }
}
