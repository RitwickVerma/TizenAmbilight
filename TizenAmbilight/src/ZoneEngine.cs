using System;
using System.Text;
using System.Collections.Generic;

namespace TizenAmbilight
{
    /// <summary>Installed screen edges; omitted configuration keys preserve the full perimeter.</summary>
    [Flags]
    public enum ScreenEdges
    {
        None = 0, Top = 1, Right = 2, Bottom = 4, Left = 8,
        All = Top | Right | Bottom | Left
    }

    /// <summary>One LED zone: a half-open rectangle [X0,X1) x [Y0,Y1) of the captured frame,
    /// plus which edge it belongs to and its position along that edge.</summary>
    public sealed class Zone
    {
        public string Edge;   // "top" | "right" | "bottom" | "left"
        public int Slot;      // index along the edge, in WALK ORDER
        public int X0, Y0, X1, Y1;

        public override string ToString()
        {
            return Edge + "[" + Slot + "] " + X0 + "," + Y0 + "-" + X1 + "," + Y1;
        }
    }

    /// <summary>Per-zone RGB, stored as three byte arrays so an EMA update is a tight loop.</summary>
    public sealed class ZoneColors
    {
        public readonly int Count;
        public readonly byte[] R, G, B;

        public ZoneColors(int count)
        {
            Count = count;
            R = new byte[count];
            G = new byte[count];
            B = new byte[count];
        }

        public void CopyFrom(ZoneColors o)
        {
            Array.Copy(o.R, R, Count);
            Array.Copy(o.G, G, Count);
            Array.Copy(o.B, B, Count);
        }

        /// <summary>Exponential moving average, applied per channel:
        /// smoothed = alpha*raw + (1-alpha)*smoothed. alpha = 1 means "no smoothing".</summary>
        public void SmoothTowards(ZoneColors raw, double alpha)
        {
            double inv = 1.0 - alpha;
            for (int i = 0; i < Count; i++)
            {
                R[i] = (byte)(alpha * raw.R[i] + inv * R[i] + 0.5);
                G[i] = (byte)(alpha * raw.G[i] + inv * G[i] + 0.5);
                B[i] = (byte)(alpha * raw.B[i] + inv * B[i] + 0.5);
            }
        }
    }

    /// <summary>
    /// Perimeter zone layout and zone-colour extraction from a REAL captured frame.
    ///
    /// Zone counts for the default 73-zone strip, in WALK ORDER (clockwise, starting at the
    /// top-left corner), are exactly the requested split:
    ///
    ///     top 23  ->  right 13  ->  bottom 24  ->  left 13      (23+13+24+13 = 73)
    ///
    /// For other zone totals the same proportions are derived from the frame's edge lengths
    /// (left/right scale with height, top/bottom with width) and any rounding remainder is
    /// given to the bottom edge — which is what makes 73 come out as 13/23/13/24.
    /// Partial layouts exclude disabled edges from the weights and place any remainder on the
    /// last enabled edge in left/top/right/bottom allocation order. Walk order stays clockwise.
    ///
    /// Every zone reads a BORDER BAND: a strip `bandFrac` of the frame dimension thick,
    /// running inward from the edge (default 8% — inside the requested 6-10% range). The image
    /// CENTRE is never sampled. Corner regions are intentionally shared between the two edges
    /// that meet there, which matches where the physical strip LEDs actually sit.
    /// </summary>
    public sealed class ZoneLayout
    {
        public readonly int Total;
        public readonly int LeftCount, TopCount, RightCount, BottomCount;
        public readonly int BandX, BandY;
        public readonly double BandFrac;
        public readonly Zone[] Zones;
        public readonly ScreenEdges Edges;
        public readonly int[] Previous, Next;
        readonly int[] _component, _position;
        readonly List<int[]> _runs = new List<int[]>();

        public ZoneLayout(int width, int height, int total, double bandFrac)
            : this(width, height, total, bandFrac, ScreenEdges.All) { }

        public ZoneLayout(int width, int height, int total, double bandFrac, ScreenEdges edges)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException("width/height");
            Edges = edges & ScreenEdges.All;
            if (total < 4) throw new ArgumentOutOfRangeException(nameof(total));
            Total = Edges == ScreenEdges.None ? 0 : total;
            BandFrac = bandFrac;

            BandX = Math.Max(1, (int)Math.Round(width * bandFrac));
            BandY = Math.Max(1, (int)Math.Round(height * bandFrac));
            if (BandX > width) BandX = width;
            if (BandY > height) BandY = height;

            // Keep the established all-edge rounding exactly; partial layouts redistribute only
            // across enabled edges, with at least one zone on each enabled edge.
            int[] counts = new int[4];                     // left, top, right, bottom
            int[] weights = { height, width, height, width };
            ScreenEdges[] flags = { ScreenEdges.Left, ScreenEdges.Top, ScreenEdges.Right, ScreenEdges.Bottom };
            int perimeter = 0, remainderEdge = -1;
            for (int i = 0; i < 4; i++)
                if ((Edges & flags[i]) != 0) { perimeter += weights[i]; remainderEdge = i; }
            int sum = 0;
            for (int i = 0; i < 4; i++)
            {
                if ((Edges & flags[i]) == 0) continue;
                counts[i] = Math.Max(1, (int)Math.Round(total * (weights[i] / (double)perimeter)));
                sum += counts[i];
            }
            while (sum < Total) { counts[remainderEdge]++; sum++; }
            while (sum > Total && counts[remainderEdge] > 1) { counts[remainderEdge]--; sum--; }
            while (sum > Total)
            {
                int big = 0;
                for (int i = 1; i < 4; i++) if (counts[i] > counts[big]) big = i;
                counts[big]--; sum--;
            }

            LeftCount = counts[0];
            TopCount = counts[1];
            RightCount = counts[2];
            BottomCount = counts[3];

            // ---- build the zones in WALK ORDER: top (L->R), right (T->B), bottom (R->L), left (B->T) ----
            var zones = new Zone[TopCount + RightCount + BottomCount + LeftCount];
            int k = 0;

            // top edge: y in [0, BandY), x split into TopCount segments, left to right
            for (int i = 0; i < TopCount; i++)
            {
                int x0 = (int)((long)width * i / TopCount);
                int x1 = (int)((long)width * (i + 1) / TopCount);
                zones[k++] = new Zone { Edge = "top", Slot = i, X0 = x0, Y0 = 0, X1 = x1, Y1 = BandY };
            }

            // right edge: x in [width-BandX, width), y split into RightCount segments, top to bottom
            for (int i = 0; i < RightCount; i++)
            {
                int y0 = (int)((long)height * i / RightCount);
                int y1 = (int)((long)height * (i + 1) / RightCount);
                zones[k++] = new Zone { Edge = "right", Slot = i, X0 = width - BandX, Y0 = y0, X1 = width, Y1 = y1 };
            }

            // bottom edge: y in [height-BandY, height), x split into BottomCount segments, RIGHT to LEFT
            for (int i = 0; i < BottomCount; i++)
            {
                int x1 = width - (int)((long)width * i / BottomCount);
                int x0 = width - (int)((long)width * (i + 1) / BottomCount);
                zones[k++] = new Zone { Edge = "bottom", Slot = i, X0 = x0, Y0 = height - BandY, X1 = x1, Y1 = height };
            }

            // left edge: x in [0, BandX), y split into LeftCount segments, BOTTOM to TOP
            for (int i = 0; i < LeftCount; i++)
            {
                int y1 = height - (int)((long)height * i / LeftCount);
                int y0 = height - (int)((long)height * (i + 1) / LeftCount);
                zones[k++] = new Zone { Edge = "left", Slot = i, X0 = 0, Y0 = y0, X1 = BandX, Y1 = y1 };
            }

            Zones = zones;
            Previous = new int[Total]; Next = new int[Total];
            _component = new int[Total]; _position = new int[Total];
            BuildTopology();

            // Defensive clamp. For a frame whose dimension is smaller than the number of zones
            // along that edge, the rounding above can produce a zero-width zone pushed one pixel
            // past the edge (e.g. x0 == x1 == width). That is not reachable at the capture sizes
            // this app uses, but a zone that points outside the frame would make Extract read out
            // of bounds, so pin every rectangle inside the image instead of trusting the maths.
            for (int i = 0; i < zones.Length; i++)
            {
                Zone z = zones[i];
                if (z.X0 < 0) z.X0 = 0;
                if (z.Y0 < 0) z.Y0 = 0;
                if (z.X1 > width) z.X1 = width;
                if (z.Y1 > height) z.Y1 = height;
                if (z.X1 <= z.X0) z.X0 = Math.Max(0, z.X1 - 1);
                if (z.Y1 <= z.Y0) z.Y0 = Math.Max(0, z.Y1 - 1);
            }
        }

        /// <summary>Missing edges break the halo into open runs. A missing neighbour points at
        /// the zone itself, so blur and measurement interpolation cannot bridge an unlit gap.</summary>
        void BuildTopology()
        {
            int n = Total;
            if (n == 0) return;
            string[] order = { "top", "right", "bottom", "left" };
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                int q = (i + 1) % n;
                int edge = Array.IndexOf(order, Zones[i].Edge);
                bool joined = Zones[i].Edge == Zones[q].Edge
                    ? q != 0 : order[(edge + 1) % 4] == Zones[q].Edge;
                Next[i] = joined ? q : i;
                Previous[q] = joined ? i : q;
                if (!joined) start = q;
            }
            int visited = 0;
            while (visited < n)
            {
                var run = new List<int>();
                int z = start;
                while (true)
                {
                    _component[z] = _runs.Count; _position[z] = run.Count;
                    run.Add(z); visited++;
                    if (Next[z] == z || Next[z] == start) break;
                    z = Next[z];
                }
                _runs.Add(run.ToArray());
                start = (z + 1) % n;
            }
        }

        /// <summary>Retain the original ring sampling for full strips. For open runs include both
        /// endpoints, so a bottom-only strip does not extrapolate its last LEDs from a distant spot.</summary>
        public int[] SampleZones(int requested)
        {
            int count = Math.Min(Total, Math.Max(4, requested));
            if (count == 0) return new int[0];
            var samples = new List<int>();
            if (Edges == ScreenEdges.All)
            {
                for (int k = 0; k < count; k++) samples.Add((int)Math.Round(k * (double)Total / count));
            }
            else
            {
                int[] counts = new int[_runs.Count];
                int assigned = 0;
                for (int i = 0; i < counts.Length; i++) { counts[i] = Math.Min(2, _runs[i].Length); assigned += counts[i]; }
                while (assigned < count)
                {
                    int best = -1;
                    double widest = -1;
                    for (int i = 0; i < counts.Length; i++)
                    {
                        if (counts[i] >= _runs[i].Length) continue;
                        double gap = (_runs[i].Length - 1.0) / Math.Max(1, counts[i] - 1);
                        if (gap > widest) { best = i; widest = gap; }
                    }
                    counts[best]++; assigned++;
                }
                for (int i = 0; i < counts.Length; i++)
                    for (int k = 0; k < counts[i]; k++)
                        samples.Add(_runs[i][counts[i] == 1 ? 0 : (int)Math.Round(k * (_runs[i].Length - 1.0) / (counts[i] - 1))]);
                samples.Sort();
            }
            return samples.ToArray();
        }

        /// <summary>Interpolate raw 10-bit PPI measurements within each connected run. An endpoint
        /// holds its nearest sample; a run without any fresh sample stays black during warm-up.</summary>
        public void InterpolateSamples(int[] zones, int[] rgb10, int count, byte[] r, byte[] g, byte[] b)
        {
            bool closed = Edges == ScreenEdges.All;
            for (int z = 0; z < Total; z++)
            {
                int before = -1, after = -1, db = int.MaxValue, da = int.MaxValue;
                for (int k = 0; k < count; k++)
                {
                    int s = zones[k];
                    if (_component[s] != _component[z]) continue;
                    int delta = _position[z] - _position[s];
                    int back = closed ? (delta + Total) % Total : delta;
                    int forward = closed ? (-delta + Total) % Total : -delta;
                    if (back >= 0 && back < db) { before = k; db = back; }
                    if (forward >= 0 && forward < da) { after = k; da = forward; }
                }
                if (before < 0) before = after;
                if (after < 0) after = before;
                if (before < 0) { r[z] = g[z] = b[z] = 0; continue; }
                double t = before == after ? 0 : (double)db / ((double)db + da);
                r[z] = To8(rgb10[before * 3] + (rgb10[after * 3] - rgb10[before * 3]) * t);
                g[z] = To8(rgb10[before * 3 + 1] + (rgb10[after * 3 + 1] - rgb10[before * 3 + 1]) * t);
                b[z] = To8(rgb10[before * 3 + 2] + (rgb10[after * 3 + 2] - rgb10[before * 3 + 2]) * t);
            }
        }

        static byte To8(double value)
        {
            int v = (int)((value + 0.5) / 4.0);
            return (byte)Math.Max(0, Math.Min(255, v));
        }

        public string Describe()
        {
            return Total + " zones in clockwise edge order (" + Edges + "): top " + TopCount + " (L->R), right " +
                   RightCount + " (T->B), bottom " + BottomCount + " (R->L), left " + LeftCount +
                   " (B->T); border band " + BandX + "x" + BandY + " px (" +
                   (BandFrac * 100.0).ToString("0.#") + "% of each dimension, inward from the edge)";
        }

        /// <summary>Average every pixel of every zone in one pass over the zones. The centre of
        /// the frame is never touched. Returns a fresh raw ZoneColors (not smoothed).</summary>
        public ZoneColors Extract(PngImage img)
        {
            var outCols = new ZoneColors(Zones.Length);
            byte[] px = img.Rgba;
            int stride = img.Width * 4;

            for (int z = 0; z < Zones.Length; z++)
            {
                Zone zn = Zones[z];
                // Never trust the layout to be inside the frame (see the clamp in the constructor).
                int x0 = Math.Max(0, Math.Min(zn.X0, img.Width));
                int x1 = Math.Max(x0, Math.Min(zn.X1, img.Width));
                int y0 = Math.Max(0, Math.Min(zn.Y0, img.Height));
                int y1 = Math.Max(y0, Math.Min(zn.Y1, img.Height));
                long sr = 0, sg = 0, sb = 0, n = 0;

                for (int y = y0; y < y1; y++)
                {
                    int row = y * stride;
                    for (int x = x0; x < x1; x++)
                    {
                        int o = row + x * 4;
                        sr += px[o];
                        sg += px[o + 1];
                        sb += px[o + 2];
                        n++;
                    }
                }

                if (n == 0) { outCols.R[z] = 0; outCols.G[z] = 0; outCols.B[z] = 0; continue; }
                outCols.R[z] = (byte)((sr + n / 2) / n);
                outCols.G[z] = (byte)((sg + n / 2) / n);
                outCols.B[z] = (byte)((sb + n / 2) / n);
            }
            return outCols;
        }

        /// <summary>Average each zone in YUV and run the colour matrix ONCE PER ZONE.
        ///
        /// Three things this fixes, all of which the RGB path got wrong:
        ///
        ///  1. COST. The old path converted all 518,400 pixels to RGBA, then averaged 73 rectangles
        ///     out of that 2 MB buffer. This reads only the border band (~150k luma + ~38k chroma
        ///     samples) and runs the matrix 73 times instead of 518,400. The conversion was the
        ///     dominant cost in the whole frame (~115 ms) and it was producing 73 numbers.
        ///
        ///  2. CORRECTNESS. Averaging R, G and B independently over a mixed zone drags the result
        ///     toward grey — bright red plus bright green averages to olive. Averaging Y, U and V
        ///     separately keeps luma and chroma distinct. Chroma is then EXPANDED about neutral by
        ///     `saturation` (done in YuvToRgb, i.e. in chroma space, before the matrix) to undo the
        ///     flattening that any average still causes.
        ///
        ///  3. CALIBRATION. Per-channel white balance is applied after the matrix — the only place it
        ///     can correct what the strip actually emits, since nothing in the chain otherwise knows
        ///     the strip's colour behaviour.
        ///
        /// Chroma is 4:2:0: one U/V pair per 2x2 luma block, interleaved, stride = frame width (NV12,
        /// pitch 960, verified on device from the library's own ret_c_pitch).
        /// </summary>
        static ZoneColors _yuvScratch;

        public ZoneColors ExtractYuv(byte[] y, byte[] c, int width, int height,
                                     double saturation, double kr, double kg, double kb, bool hdr, bool gamut709,
                                     int yStride = 0, int cStride = 0, int sample = 1,
                                     int activeTop = 0, int activeBottom = -1,
                                     int activeLeft = 0, int activeRight = -1)
        {
            // SUBSAMPLING. Each zone resolves to a single average, and a zone is 1,700-3,200 px -
            // far more samples than an average needs. Stepping by `sample` in both axes cuts the work
            // by sample^2, which matters because this loop measured 62 ms/frame - more than the
            // native capture call it follows (53 ms) - so it, not the capture, was the frame-rate
            // ceiling. At sample=3 a zone still averages ~300 pixels, ample for a stable mean.
            if (sample < 1) sample = 1;
            if (yStride <= 0) yStride = width;      // caller did not report a pitch: assume tight
            if (cStride <= 0) cStride = width;
            if (activeBottom < 0) activeBottom = height;
            if (activeRight < 0) activeRight = width;
            if (activeTop < 0) activeTop = 0;
            if (activeBottom > height) activeBottom = height;
            if (activeTop > activeBottom) activeTop = activeBottom;
            // Reused across frames. Safe because the caller consumes the result within the same
            // iteration (smoothing reads it and nothing retains it). The previous frame's object was
            // garbage the moment this was called anyway - it just cost a GC pass to become so.
            if (_yuvScratch == null || _yuvScratch.Count != Zones.Length)
                _yuvScratch = new ZoneColors(Zones.Length);
            ZoneColors outCols = _yuvScratch;

            for (int z = 0; z < Zones.Length; z++)
            {
                Zone zn = Zones[z];
                int x0 = Math.Max(0, Math.Min(zn.X0, width));
                int x1 = Math.Max(x0, Math.Min(zn.X1, width));

                // PILLARBOX: mirror of the vertical band logic below. The left/right bands keep their
                // thickness but slide INSIDE the active picture so they sample the image rather than
                // the black bar, and the top/bottom bands stop spanning the bars - which is what
                // polluted ~20% of every top/bottom zone with black on a pillarboxed frame.
                int bandW = x1 - x0;
                if (zn.Edge == "left")
                {
                    x0 = activeLeft;
                    x1 = Math.Min(activeRight, activeLeft + bandW);
                }
                else if (zn.Edge == "right")
                {
                    x1 = activeRight;
                    x0 = Math.Max(activeLeft, activeRight - bandW);
                }
                else
                {
                    if (x0 < activeLeft) x0 = activeLeft;
                    if (x1 > activeRight) x1 = activeRight;
                }
                if (x1 <= x0) x0 = Math.Max(0, x1 - 1);

                // Letterbox handling: the TOP and BOTTOM bands keep their thickness but are slid
                // INSIDE the active picture area, so they sample the film instead of the black bars.
                // Left/right bands simply span the active height (the bars run the full width).
                int y0, y1;
                int bandH = zn.Y1 - zn.Y0;
                if (zn.Edge == "top")
                {
                    y0 = activeTop;
                    y1 = Math.Min(activeBottom, activeTop + bandH);
                }
                else if (zn.Edge == "bottom")
                {
                    y1 = activeBottom;
                    y0 = Math.Max(activeTop, activeBottom - bandH);
                }
                else
                {
                    // LEFT / RIGHT: keep the zone's OWN vertical segment, REMAPPED into the active
                    // picture area.
                    //
                    // This used to be `y0 = activeTop; y1 = activeBottom;` - the full active height for
                    // every side zone. Since all the zones on an edge also share one X range, that made
                    // all 13 of them the SAME RECTANGLE, so they returned the same average by
                    // construction. Measured on device: 11 of 13 zones byte-identical on each side, i.e.
                    // 22 of 73 LEDs duplicating a neighbour as a flat block down the side of the image,
                    // while top and bottom resolved 23/23 and 24/24. The layout has always computed a
                    // distinct segment per zone (zn.Y0..zn.Y1); this branch was discarding it.
                    //
                    // The remap is scale-preserving: the zone's position as a FRACTION of the frame is
                    // reproduced inside the active area. With no bars the active area IS the frame, so
                    // this is the identity and matches what the layout intended. With letterbox it slides
                    // the segment inside the picture - which is what the full-height version was reaching
                    // for, except it achieved it by throwing the segmentation away.
                    int activeH = activeBottom - activeTop;
                    if (activeH <= 0) activeH = height;
                    y0 = activeTop + (int)Math.Round((double)zn.Y0 / height * activeH);
                    y1 = activeTop + (int)Math.Round((double)zn.Y1 / height * activeH);
                    if (y1 <= y0) y1 = y0 + 1;      // never collapse a side zone to zero rows
                }
                y0 = Math.Max(0, Math.Min(y0, height));
                y1 = Math.Max(y0, Math.Min(y1, height));

                // NOTE: do NOT decode per pixel here. Decode-then-average is the mathematically exact
                // order for a convex transfer like PQ, and it was implemented and measured: it cost
                // 8.7x the zone time (8.4 ms -> 72.8 ms, frame rate ~25 -> 6 fps) because it needs one
                // YuvToRgb call - 9 params plus 3 out params - per sampled pixel (~20k/frame). The bias
                // it removes is ~1% on dark content and ~7% on bright. A 4x frame-rate loss for that is
                // a bad trade, so the zone-mean decode stays and the residual under-estimate is
                // ACCEPTED, documented, and measured rather than silently approximated.
                long sy = 0, n = 0;
                for (int row = y0; row < y1; row += sample)
                {
                    int yRow = row * yStride;
                    for (int x = x0; x < x1; x += sample)
                    {
                        int yi = yRow + x;
                        if (yi < 0) continue;
                        if (yi >= y.Length) break;      // row ran past the buffer: stop scanning this row
                        sy += y[yi]; n++;
                    }
                }

                long su = 0, sv = 0, nc = 0;
                for (int cy = (y0 >> 1); cy <= ((y1 - 1) >> 1); cy += sample)
                {
                    int cRow = cy * cStride;
                    for (int cx = (x0 >> 1); cx <= ((x1 - 1) >> 1); cx += sample)
                    {
                        int ci = cRow + (cx << 1);
                        if (ci + 1 < c.Length) { su += c[ci]; sv += c[ci + 1]; nc++; }
                    }
                }

                if (n == 0) { outCols.R[z] = 0; outCols.G[z] = 0; outCols.B[z] = 0; continue; }

                int Ym = (int)((sy + n / 2) / n);
                int Um = nc > 0 ? (int)((su + nc / 2) / nc) : 128;
                int Vm = nc > 0 ? (int)((sv + nc / 2) / nc) : 128;

                byte rr, gg, bb;
                VideoPlaneCapture.YuvToRgb(Ym, Um, Vm, saturation, kr, kg, kb, hdr, gamut709, out rr, out gg, out bb);
                outCols.R[z] = rr; outCols.G[z] = gg; outCols.B[z] = bb;
            }
            return outCols;
        }
    }

    public static class Colors
    {
        public static string Hex(byte r, byte g, byte b)
        {
            return "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2");
        }

        /// <summary>Guarantee a minimum level so the room never goes pitch black.
        ///
        /// Only zones BELOW the floor are lifted, and they are SCALED (not offset) so the hue is
        /// preserved - the wall keeps the scene's colour instead of washing to grey. A truly black
        /// zone has no hue to preserve and becomes a neutral floor. Leave at 0 for true blacks.
        /// </summary>
        public static void ApplyFloor(ZoneColors c, double floor)
        {
            if (c == null || floor <= 0) return;
            if (floor > 255) floor = 255;
            for (int i = 0; i < c.Count; i++)
            {
                int mx = Math.Max(c.R[i], Math.Max(c.G[i], c.B[i]));
                if (mx >= floor) continue;
                if (mx == 0) { c.R[i] = c.G[i] = c.B[i] = (byte)floor; continue; }
                double s = floor / mx;
                c.R[i] = (byte)Math.Min(255.0, c.R[i] * s + 0.5);
                c.G[i] = (byte)Math.Min(255.0, c.G[i] * s + 0.5);
                c.B[i] = (byte)Math.Min(255.0, c.B[i] * s + 0.5);
            }
        }

        /// <summary>Blend each zone with its two NEIGHBOURS ALONG THE PERIMETER.
        ///
        /// Without a layout this preserves the original circular blur. A supplied layout uses
        /// physical neighbours and repeats an endpoint at each missing edge. This softens the hard boundaries
        /// between adjacent zones, which is what makes the glow read as one continuous halo rather
        /// than as 73 discrete patches. amount 0 = off, ~0.25 = gentle, 0.5 = very soft.
        /// </summary>
        static byte[] _sr, _sg, _sb;    // scratch for SpatialRing; render thread only

        public static void SpatialRing(ZoneColors c, double amount, ZoneLayout layout = null)
        {
            if (c == null || c.Count < 3 || amount <= 0.0) return;
            if (amount > 1.0) amount = 1.0;
            int n = c.Count;
            // Reusable scratch: this ran 3 allocations per frame (~75/s at 25 fps) purely to hold an
            // intermediate that is consumed two lines later. Grown once, then reused forever.
            if (_sr == null || _sr.Length < n)
            {
                _sr = new byte[n]; _sg = new byte[n]; _sb = new byte[n];
            }
            byte[] r = _sr, g = _sg, b = _sb;
            double k = 1.0 - amount, m = amount * 0.5;   // loop-invariant: was recomputed per zone
            for (int i = 0; i < n; i++)
            {
                int p = layout == null ? (i - 1 + n) % n : layout.Previous[i];
                int q = layout == null ? (i + 1) % n : layout.Next[i];
                r[i] = (byte)(c.R[i] * k + (c.R[p] + c.R[q]) * m + 0.5);
                g[i] = (byte)(c.G[i] * k + (c.G[p] + c.G[q]) * m + 0.5);
                b[i] = (byte)(c.B[i] * k + (c.B[p] + c.B[q]) * m + 0.5);
            }
            for (int i = 0; i < n; i++) { c.R[i] = r[i]; c.G[i] = g[i]; c.B[i] = b[i]; }
        }

        /// <summary>Mean absolute per-channel difference between two colour sets — the
        /// smoothed-vs-raw delta reported in the stats.</summary>
        public static double MeanDelta(ZoneColors a, ZoneColors b)
        {
            if (a == null || b == null || a.Count == 0) return 0.0;
            long sum = 0;
            int cnt = Math.Min(a.Count, b.Count);
            for (int i = 0; i < cnt; i++)
            {
                sum += Math.Abs(a.R[i] - b.R[i]);
                sum += Math.Abs(a.G[i] - b.G[i]);
                sum += Math.Abs(a.B[i] - b.B[i]);
            }
            return cnt == 0 ? 0.0 : (double)sum / (cnt * 3);
        }
    }
}
