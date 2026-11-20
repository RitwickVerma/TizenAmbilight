using System;
using System.Collections.Generic;

namespace TizenAmbilight
{
    static partial class Ambilight
    {
        // ================================================================ ppi fallback glue
        //
        // The video plane is withheld for DRM content (rc=-4). The panel still has to draw the picture, so
        // the video processor's own RGB-measure block (libvideoenhance ppi_ve_*) is the only on-TV source
        // left. Selection:
        //   backend=2        force the sampler (diagnostics, and the way to test under DRM on demand)
        //   backend=0 / none the plane; AUTOMATICALLY hands over to the sampler after PpiEnterStreak
        //                    consecutive rc=-4, and back on the first rc=0.

        /// <summary>Consecutive rc=-4 before the sampler takes over. rc=-1 (the normal post-start
        /// "not ready" burst) neither counts nor resets, so a -4/-1 mix still qualifies.</summary>
        const int PpiEnterStreak = 3;

        static bool _ppiAuto;
        static int _drmStreak;
        static ZoneColors _ppiRaw;
        static DateTime _ppiInitTryUtc = DateTime.MinValue;
        static DateTime _lastPpiFrameUtc = DateTime.MinValue;
        static int[] _ppiLuma;      // scratch for PpiMedianCode (render loop only)

        static DateTime _lastPlaneProbeUtc = DateTime.MinValue;
        /// <summary>Stand-in result for a plane probe that was deliberately not made (shared, read-only).</summary>
        static readonly VideoPlaneResult PlaneAssumedLocked = new VideoPlaneResult { Rc = -4 };
        /// <summary>While the sampler carries the strip, ask the (DRM-locked) plane only this often.</summary>
        const int PlaneProbeWhilePpiMs = 500;

        /// <summary>Init the sampler on first need; after a failure retry at most every 30 s.</summary>
        static bool EnsurePpi(Config cfg)
        {
            if (PpiCapture.Ready && PpiCapture.ZoneTotal == cfg.ZoneCount) return true;
            if (_ppiInitTryUtc != DateTime.MinValue && PpiCapture.InitError != null &&
                (DateTime.UtcNow - _ppiInitTryUtc).TotalSeconds < 30) return false;
            _ppiInitTryUtc = DateTime.UtcNow;
            string err = PpiCapture.Init(cfg.ZoneCount, cfg.BandFrac, cfg.Edges);
            if (err != null) { L("ppi sampler NOT ready: " + err); return false; }
            L("ppi sampler ready: cond rc=" + PpiCapture.CondRc + " slots=" + PpiCapture.Slots +
              " block=" + PpiCapture.BlockW + "x" + PpiCapture.BlockH + " settle=" + PpiCapture.SleepMs +
              "ms full=" + PpiCapture.FullW + "x" + PpiCapture.FullH + " zones=" + PpiCapture.ZoneTotal);
            return true;
        }

        /// <summary>Content signature for the stale fade, quantised to 5 bits per channel so the 10-bit
        /// measurement noise of a static picture does not read as motion.</summary>
        static void NoteContentPpi(ZoneColors z, Stats st)
        {
            int sig = 17;
            for (int i = 0; i < z.Count; i++)
                sig = unchecked(sig * 31 + (z.R[i] >> 3) * 1024 + (z.G[i] >> 3) * 32 + (z.B[i] >> 3));
            if (sig == st.ContentSig) { st.ContentUnchanged++; }
            else
            {
                st.ContentSig = sig;
                st.ContentUnchanged = 0;
                st.LastContentChangeUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Median zone luma mapped onto the limited-range code scale SceneGammaTarget expects
        /// (16 = black, 235 = white).</summary>
        static int PpiMedianCode(ZoneColors z)
        {
            if (_ppiLuma == null || _ppiLuma.Length != z.Count) _ppiLuma = new int[z.Count];
            var l = _ppiLuma;
            for (int i = 0; i < z.Count; i++)
                l[i] = (int)(0.2627 * z.R[i] + 0.6780 * z.G[i] + 0.0593 * z.B[i]);
            Array.Sort(l);
            int y = l[l.Length / 2];
            return 16 + (int)Math.Round(y * 219.0 / 255.0);
        }
    }
}
