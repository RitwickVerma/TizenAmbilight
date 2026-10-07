using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TizenAmbilight
{
    /// <summary>
    /// Decode options for <see cref="JpegDecoder"/>. Full quality is the default.
    ///
    /// The ambilight only ever reads a BORDER RING of the frame (ZoneEngine never samples the
    /// centre), so the inverse DCT — by far the dominant cost of a baseline JPEG decode — can be
    /// skipped for the 8x8 blocks that lie entirely OUTSIDE that ring. When
    /// <see cref="SkipInteriorIdct"/> is set, those interior blocks are filled with their DC
    /// value (a 64-pixel constant) and every block that intersects the ring is reconstructed
    /// exactly as a full decode would. The entropy stream is still parsed in full — you cannot
    /// skip Huffman-coded block data — so this is a pure IDCT saving, and it does not change a
    /// single pixel the zone engine reads.
    /// </summary>
    public sealed class JpegOptions
    {
        /// <summary>Skip the IDCT for blocks entirely outside the border ring.</summary>
        public bool SkipInteriorIdct;

        /// <summary>Border ring thickness in PIXELS of the full-resolution frame (the same
        /// numbers ZoneLayout computes from bandFrac). The ring is
        /// {x &lt; BandX} ∪ {x ≥ W-BandX} ∪ {y &lt; BandY} ∪ {y ≥ H-BandY}.
        /// BandX = BandY = 0 makes every block "interior", i.e. a whole-frame DC-only decode.</summary>
        public int BandX;
        public int BandY;

        public static JpegOptions Full { get { return new JpegOptions(); } }

        public static JpegOptions ForBorder(int bandX, int bandY)
        {
            return new JpegOptions
            {
                SkipInteriorIdct = true,
                BandX = Math.Max(0, bandX),
                BandY = Math.Max(0, bandY),
            };
        }
    }

    /// <summary>Header facts and block counts for a decode, for the stats report.</summary>
    public sealed class JpegFrameInfo
    {
        public int Width, Height;
        public int Components;
        public int MaxH, MaxV;
        public int RestartInterval;
        public int Precision;
        public bool Grayscale;
        public int BlocksDecoded;      // blocks that got a real IDCT
        public int BlocksSkipped;      // blocks that only got their DC value
        public string SofMarker = "";
        public string Sampling = "";
        public string Mode = "";
        public string Error;

        public string Describe()
        {
            if (Error != null) return "jpeg_error:" + Error;
            return "JPEG " + SofMarker + " " + Width + "x" + Height + " " + Components + "comp " +
                   "sampling=" + Sampling + " restart=" + RestartInterval + " " + Mode +
                   " (idct " + BlocksDecoded + ", dc-only " + BlocksSkipped + " of " +
                   (BlocksDecoded + BlocksSkipped) + " blocks)";
        }
    }

    /// <summary>
    /// Minimal BASELINE SEQUENTIAL JPEG decoder (ITU-T T.81 / JFIF, SOF0, 8-bit Huffman).
    ///
    /// Deliberately scoped, because this has to be provably correct, not merely plausible:
    ///
    ///   SUPPORTED  SOF0 baseline sequential DCT; 8-bit precision; 1 (grayscale) or 3 (YCbCr)
    ///              components; sampling factors 1..4 with triangular ("fancy", libjpeg-
    ///              compatible) upsampling for 2:1 and nearest-neighbour for anything else;
    ///              DRI restart markers; 8- and 16-bit DQT; the entropy stream is fully parsed so
    ///              malformed data fails loudly instead of silently.
    ///   REFUSED    SOF1..SOF15 (progressive, extended, lossless), 12-bit precision, arithmetic
    ///              coding, 4-component CMYK/YCCK, non-interleaved scans.
    ///
    /// No NuGet dependency. The output is the SAME 8-bit RGBA layout the PNG decoder produces
    /// (PngImage: row-major, width*height*4, R,G,B,A with A = 255), so ZoneEngine.Extract
    /// consumes a JPEG frame and a PNG frame through identical code.
    ///
    /// Accuracy: float inverse DCT over the exact cosine basis, YCbCr→RGB through the same
    /// integer lookup tables libjpeg uses, and libjpeg-compatible chroma upsampling. The maximum
    /// per-channel error against Pillow's own decode is MEASURED by decoder-selftest
    /// (gen_jpeg_testdata.py + SelfTestMain.cs) rather than asserted here.
    /// </summary>
    public static class JpegDecoder
    {
        // zigzag position -> NATURAL (row*8+col) index. Standard T.81 table.
        static readonly int[] ZigZag =
        {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };

        // A[u][x] = 0.5 * C(u) * cos((2x+1) * u * pi / 16), C(0) = 1/sqrt(2) else 1. Two passes
        // with this basis carry the full 1/4 of the 2-D IDCT.
        static readonly double[][] A = BuildBasis();

        // libjpeg's integer YCbCr -> RGB tables (jdcolor.c / jdmerge.c): matching the reference
        // decoder's fixed-point rounding instead of drifting from it.
        static readonly int[] CrR = BuildTable(1.40200);
        static readonly int[] CbG = BuildTable(-0.344136);
        static readonly int[] CrG = BuildTable(-0.714136);
        static readonly int[] CbB = BuildTable(1.77200);

        [ThreadStatic] static JpegFrameInfo _info;

        /// <summary>Header facts + block counts for the most recent decode on this thread.</summary>
        public static JpegFrameInfo LastInfo { get { return _info; } }

        static double[][] BuildBasis()
        {
            var t = new double[8][];
            for (int u = 0; u < 8; u++)
            {
                t[u] = new double[8];
                double c = (u == 0) ? (1.0 / Math.Sqrt(2.0)) : 1.0;
                for (int x = 0; x < 8; x++)
                    t[u][x] = 0.5 * c * Math.Cos((2.0 * x + 1.0) * u * Math.PI / 16.0);
            }
            return t;
        }

        static int[] BuildTable(double scale)
        {
            var t = new int[256];
            for (int i = 0; i < 256; i++) t[i] = (int)Math.Round(scale * (i - 128));
            return t;
        }

        // ================================================================ entry points

        public static PngImage DecodeFile(string path)
        {
            return Decode(File.ReadAllBytes(path), null);
        }

        public static PngImage DecodeFile(string path, JpegOptions opt)
        {
            return Decode(File.ReadAllBytes(path), opt);
        }

        public static PngImage Decode(byte[] data)
        {
            return Decode(data, null);
        }

        /// <summary>Decode a baseline JPEG to RGBA. Throws InvalidDataException for anything this
        /// decoder deliberately does not support, so the caller can fall back rather than ship a
        /// wrong frame.</summary>
        public static PngImage Decode(byte[] data, JpegOptions opt)
        {
            var info = new JpegFrameInfo();
            _info = info;
            try
            {
                if (data == null) throw new InvalidDataException("jpeg: null buffer");
                if (data.Length < 4) throw new InvalidDataException("jpeg: too short (" + data.Length + " bytes)");
                if (data[0] != 0xFF || data[1] != 0xD8) throw new InvalidDataException("jpeg: no SOI marker");

                if (opt == null) opt = JpegOptions.Full;

                var tables = new Tables();
                var comps = new List<Component>();
                int width = 0, height = 0, restartInterval = 0, precision = 0;
                bool haveSof = false;

                int pos = 2;
                while (pos + 1 < data.Length)
                {
                    if (data[pos] != 0xFF) { pos++; continue; }
                    int m = pos + 1;
                    while (m < data.Length && data[m] == 0xFF) m++;      // legal fill bytes
                    if (m >= data.Length) break;
                    byte marker = data[m];
                    pos = m + 1;

                    if (marker == 0xD9) break;                            // EOI
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;

                    if (pos + 1 >= data.Length) break;
                    int segLen = (data[pos] << 8) | data[pos + 1];
                    if (segLen < 2) throw new InvalidDataException("jpeg: bad segment length " + segLen);
                    int segData = pos + 2;
                    int segEnd = pos + segLen;
                    if (segEnd > data.Length) segEnd = data.Length;

                    if (marker == 0xDB)
                    {
                        ReadDqt(data, segData, segEnd, tables);
                    }
                    else if (marker == 0xC4)
                    {
                        ReadDht(data, segData, segEnd, tables);
                    }
                    else if (marker >= 0xC0 && marker <= 0xCF &&
                             marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        if (marker != 0xC0)
                            throw new InvalidDataException("jpeg: " + SofName(marker) +
                                " is not baseline sequential (SOF0); this decoder is SOF0 only");
                        ReadSof0(data, segData, segEnd, comps, out width, out height,
                                 out precision, out info.MaxH, out info.MaxV);
                        haveSof = true;
                        info.SofMarker = "SOF0";
                    }
                    else if (marker == 0xCC)
                    {
                        throw new InvalidDataException("jpeg: arithmetic coding (DAC) is not supported");
                    }
                    else if (marker == 0xDD)
                    {
                        if (segData + 1 < segEnd)
                            restartInterval = (data[segData] << 8) | data[segData + 1];
                    }
                    else if (marker == 0xDA)
                    {
                        if (!haveSof) throw new InvalidDataException("jpeg: SOS before SOF");

                        int ns = data[segData];
                        if (ns < 1 || ns > 4) throw new InvalidDataException("jpeg: SOS with " + ns + " components");
                        var scan = new ScanComponent[ns];
                        int p = segData + 1;
                        for (int i = 0; i < ns; i++)
                        {
                            if (p + 1 >= segEnd) throw new InvalidDataException("jpeg: truncated SOS");
                            int cs = data[p++];
                            int selectors = data[p++];
                            scan[i] = new ScanComponent
                            {
                                Comp = FindComponent(comps, cs),
                                DcTable = selectors >> 4,
                                AcTable = selectors & 15,
                            };
                        }
                        if (p + 2 < segEnd)
                        {
                            int ss = data[p], se = data[p + 1], ahal = data[p + 2];
                            if (ss != 0 || se != 63)
                                throw new InvalidDataException("jpeg: non-sequential spectral selection (Ss=" +
                                                               ss + ", Se=" + se + ") is not baseline");
                            if (ahal != 0)
                                throw new InvalidDataException("jpeg: successive approximation (AhAl=" +
                                                               ahal + ") is not baseline");
                        }
                        if (ns != comps.Count)
                            throw new InvalidDataException("jpeg: scan has " + ns + " components, frame has " +
                                                           comps.Count + " (non-interleaved scans unsupported)");

                        PngImage img = ScanToRgba(data, segEnd, comps, scan, tables, width, height,
                                                  info.MaxH, info.MaxV, restartInterval, opt, info);
                        info.Width = width;
                        info.Height = height;
                        info.Components = comps.Count;
                        info.Precision = precision;
                        info.RestartInterval = restartInterval;
                        info.Grayscale = comps.Count == 1;
                        info.Mode = opt.SkipInteriorIdct
                            ? ("border ring " + opt.BandX + "x" + opt.BandY + " px, interior DC-only")
                            : "full";
                        var sb = new StringBuilder();
                        for (int i = 0; i < comps.Count; i++)
                        {
                            if (i > 0) sb.Append(' ');
                            sb.Append(comps[i].Id).Append(':')
                              .Append(comps[i].H).Append('x').Append(comps[i].V);
                        }
                        info.Sampling = sb.ToString();
                        return img;
                    }

                    pos = segEnd;
                }
                throw new InvalidDataException("jpeg: no SOS/scan found");
            }
            catch (Exception e)
            {
                info.Error = e.GetType().Name + ": " + e.Message;
                if (e is InvalidDataException) throw;
                throw new InvalidDataException("jpeg: " + e.GetType().Name + ": " + e.Message);
            }
        }

        static string SofName(byte m)
        {
            switch (m)
            {
                case 0xC0: return "SOF0";
                case 0xC1: return "SOF1";
                case 0xC2: return "SOF2";
                case 0xC3: return "SOF3";
                case 0xC5: return "SOF5";
                case 0xC6: return "SOF6";
                case 0xC7: return "SOF7";
                case 0xC9: return "SOF9";
                case 0xCA: return "SOF10";
                case 0xCB: return "SOF11";
                case 0xCD: return "SOF13";
                case 0xCE: return "SOF14";
                case 0xCF: return "SOF15";
                default: return "marker 0x" + m.ToString("x2");
            }
        }

        // ================================================================ tables

        sealed class Huffman
        {
            public readonly int[] MinCode = new int[17];
            public readonly int[] MaxCode = new int[17];
            public readonly int[] ValPtr = new int[17];
            public readonly byte[] Values = new byte[256];
            public int Count;

            /// <summary>Canonical code assignment (T.81 Annex C).</summary>
            public static Huffman Build(byte[] counts, int countOff, byte[] symbols, int symOff, int symCount)
            {
                var h = new Huffman();
                int code = 0, k = 0;
                for (int l = 1; l <= 16; l++)
                {
                    int n = counts[countOff + l - 1];
                    if (n > 0)
                    {
                        h.ValPtr[l] = k;
                        h.MinCode[l] = code;
                        code += n;
                        k += n;
                        h.MaxCode[l] = code - 1;
                    }
                    else
                    {
                        h.MaxCode[l] = -1;
                    }
                    code <<= 1;
                }
                if (k > 256) throw new InvalidDataException("jpeg: Huffman table with " + k + " symbols");
                if (symCount < k)
                    throw new InvalidDataException("jpeg: Huffman table truncated (" + symCount + " of " + k + ")");
                for (int i = 0; i < k; i++) h.Values[i] = symbols[symOff + i];
                h.Count = k;
                return h;
            }
        }

        sealed class Component
        {
            public int Idx;
            public int Id;
            public int H, V;              // sampling factors
            public int Tq;                // quant table id
            public int BlocksW, BlocksH;
            public int PlaneW, PlaneH;
            public byte[] Plane;
            public int Pred;
        }

        sealed class ScanComponent
        {
            public Component Comp;
            public int DcTable, AcTable;
        }

        sealed class Tables
        {
            public readonly int[][] Quant = new int[4][];        // natural order
            public readonly Huffman[] Dc = new Huffman[4];
            public readonly Huffman[] Ac = new Huffman[4];
        }

        static void ReadDqt(byte[] d, int p, int end, Tables t)
        {
            while (p < end)
            {
                int pq = d[p] >> 4, tq = d[p] & 15;
                p++;
                if (tq > 3) throw new InvalidDataException("jpeg: DQT id " + tq);
                var q = new int[64];
                if (pq == 0)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        if (p >= end) throw new InvalidDataException("jpeg: truncated DQT");
                        q[ZigZag[i]] = d[p++];
                    }
                }
                else if (pq == 1)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        if (p + 1 >= end) throw new InvalidDataException("jpeg: truncated 16-bit DQT");
                        q[ZigZag[i]] = (d[p] << 8) | d[p + 1];
                        p += 2;
                    }
                }
                else throw new InvalidDataException("jpeg: DQT precision " + pq);
                t.Quant[tq] = q;
            }
        }

        static void ReadDht(byte[] d, int p, int end, Tables t)
        {
            while (p < end)
            {
                int tc = d[p] >> 4, th = d[p] & 15;
                p++;
                if (th > 3) throw new InvalidDataException("jpeg: DHT id " + th);
                if (p + 16 > end) throw new InvalidDataException("jpeg: truncated DHT");
                var counts = new byte[16];
                int total = 0;
                for (int i = 0; i < 16; i++) { counts[i] = d[p + i]; total += counts[i]; }
                p += 16;
                if (total > 256) throw new InvalidDataException("jpeg: DHT symbol count " + total);
                if (p + total > end) throw new InvalidDataException("jpeg: truncated DHT symbols");
                Huffman h = Huffman.Build(counts, 0, d, p, total);
                p += total;
                if (tc == 0) t.Dc[th] = h;
                else if (tc == 1) t.Ac[th] = h;
                else throw new InvalidDataException("jpeg: DHT type " + tc);
            }
        }

        static void ReadSof0(byte[] d, int p, int end, List<Component> comps,
                             out int width, out int height, out int precision,
                             out int maxH, out int maxV)
        {
            if (p + 6 > end) throw new InvalidDataException("jpeg: truncated SOF0");
            precision = d[p];
            height = (d[p + 1] << 8) | d[p + 2];
            width = (d[p + 3] << 8) | d[p + 4];
            int nf = d[p + 5];
            p += 6;
            if (precision != 8) throw new InvalidDataException("jpeg: precision " + precision + " (want 8)");
            if (width <= 0 || height <= 0)
                throw new InvalidDataException("jpeg: bad dimensions " + width + "x" + height);
            if (width > 16384 || height > 16384)
                throw new InvalidDataException("jpeg: implausible dimensions " + width + "x" + height);
            if (nf != 1 && nf != 3)
                throw new InvalidDataException("jpeg: " + nf + " components unsupported (want 1 grayscale or 3 YCbCr)");
            if (p + nf * 3 > end) throw new InvalidDataException("jpeg: truncated SOF0 component list");

            maxH = 1; maxV = 1;
            comps.Clear();
            for (int i = 0; i < nf; i++)
            {
                var c = new Component
                {
                    Idx = i,
                    Id = d[p],
                    H = d[p + 1] >> 4,
                    V = d[p + 1] & 15,
                    Tq = d[p + 2],
                };
                p += 3;
                if (c.H < 1 || c.H > 4 || c.V < 1 || c.V > 4)
                    throw new InvalidDataException("jpeg: sampling factor " + c.H + "x" + c.V + " unsupported");
                // The quant-table id indexes Tables.Quant[4]; read it back here the same way
                // ReadDqt/ReadDht range-check their ids, instead of trusting the header.
                if (c.Tq > 3)
                    throw new InvalidDataException("jpeg: component quant table id " + c.Tq + " (want 0..3)");
                if (c.H > maxH) maxH = c.H;
                if (c.V > maxV) maxV = c.V;
                comps.Add(c);
            }
        }

        static Component FindComponent(List<Component> comps, int id)
        {
            foreach (Component c in comps) if (c.Id == id) return c;
            throw new InvalidDataException("jpeg: SOS references unknown component id " + id);
        }

        // ================================================================ bit reader

        sealed class BitReader
        {
            readonly byte[] _d;
            int _pos, _end;
            int _bits, _buf;

            public BitReader(byte[] d, int start, int end) { _d = d; _pos = start; _end = end; }

            /// <summary>Next entropy byte, undoing 0xFF00 stuffing. Returns -1 at the end of the
            /// scan or at a real (non-stuffed) marker.</summary>
            int NextByte()
            {
                if (_pos >= _end) return -1;
                byte b = _d[_pos++];
                if (b != 0xFF) return b;
                while (_pos < _end && _d[_pos] == 0xFF) _pos++;      // fill bytes
                if (_pos >= _end) return -1;
                byte n = _d[_pos];
                if (n == 0x00) { _pos++; return 0xFF; }
                return -1;                                          // a real marker: scan is over
            }

            public int ReadBit()
            {
                if (_bits == 0)
                {
                    int v = NextByte();
                    if (v < 0) throw new InvalidDataException("jpeg: entropy stream ended early");
                    _buf = v;
                    _bits = 8;
                }
                _bits--;
                return (_buf >> _bits) & 1;
            }

            public int ReadBits(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++) v = (v << 1) | ReadBit();
                return v;
            }

            public int DecodeHuff(Huffman h)
            {
                int code = ReadBit();
                int l = 1;
                while (l <= 16 && code > h.MaxCode[l]) { code = (code << 1) | ReadBit(); l++; }
                if (l > 16) throw new InvalidDataException("jpeg: invalid Huffman code");
                int idx = h.ValPtr[l] + code - h.MinCode[l];
                if (idx < 0 || idx >= h.Count) throw new InvalidDataException("jpeg: Huffman symbol out of range");
                return h.Values[idx] & 0xFF;
            }

            /// <summary>Drop partial bits and step over bytes until the next RSTn marker. Restart
            /// markers only appear at MCU boundaries, which is where this is called.</summary>
            public bool Restart()
            {
                _bits = 0; _buf = 0;
                while (_pos + 1 < _end)
                {
                    if (_d[_pos] == 0xFF && _d[_pos + 1] >= 0xD0 && _d[_pos + 1] <= 0xD7)
                    {
                        _pos += 2;
                        return true;
                    }
                    _pos++;
                }
                return false;
            }
        }

        // ================================================================ the scan

        static PngImage ScanToRgba(byte[] data, int scanStart, List<Component> comps,
                                   ScanComponent[] scan, Tables tables, int width, int height,
                                   int maxH, int maxV, int restartInterval, JpegOptions opt,
                                   JpegFrameInfo info)
        {
            foreach (Component c in comps)
            {
                c.BlocksW = (width * c.H + maxH * 8 - 1) / (maxH * 8);
                c.BlocksH = (height * c.V + maxV * 8 - 1) / (maxV * 8);
                c.PlaneW = c.BlocksW * 8;
                c.PlaneH = c.BlocksH * 8;
                c.Plane = new byte[c.PlaneW * c.PlaneH];
                c.Pred = 0;
            }

            int mcusW = (width + 8 * maxH - 1) / (8 * maxH);
            int mcusH = (height + 8 * maxV - 1) / (8 * maxV);

            // The border ring, expressed in each component's own plane coordinates. The ring the
            // zone engine samples is {x < BandX} ∪ {x ≥ width-BandX} ∪ {y < BandY} ∪ {y ≥ height-BandY}
            // — NOT a symmetric inset, because the plane is zero-padded out to a whole block. Each
            // edge is therefore scaled from its OWN side of the full-resolution frame, plus 2 px of
            // slack so the triangular chroma upsampling's support stays on fully-decoded ground.
            int n = comps.Count;
            var ringXLo = new int[n];
            var ringXHi = new int[n];
            var ringYLo = new int[n];
            var ringYHi = new int[n];
            for (int i = 0; i < n; i++)
            {
                Component c = comps[i];
                int loX = CeilDiv(opt.BandX * c.H, maxH) + 2;
                int loY = CeilDiv(opt.BandY * c.V, maxV) + 2;
                int hiX = (width - opt.BandX) * c.H / maxH - 2;
                int hiY = (height - opt.BandY) * c.V / maxV - 2;

                if (loX > c.PlaneW) loX = c.PlaneW;
                if (loY > c.PlaneH) loY = c.PlaneH;
                if (hiX < 0) hiX = 0;
                if (hiY < 0) hiY = 0;

                ringXLo[i] = loX;
                ringXHi[i] = hiX < loX ? loX : hiX;   // overlapping bands => nothing is interior
                ringYLo[i] = loY;
                ringYHi[i] = hiY < loY ? loY : hiY;
            }

            var reader = new BitReader(data, scanStart, data.Length);
            var coef = new int[64];
            var blk = new byte[64];
            int sinceRestart = 0;
            int full = 0, skipped = 0;

            for (int my = 0; my < mcusH; my++)
            {
                for (int mx = 0; mx < mcusW; mx++)
                {
                    if (restartInterval > 0 && sinceRestart == restartInterval)
                    {
                        reader.Restart();
                        foreach (Component c in comps) c.Pred = 0;
                        sinceRestart = 0;
                    }

                    foreach (ScanComponent s in scan)
                    {
                        Component c = s.Comp;
                        for (int v = 0; v < c.V; v++)
                        {
                            for (int h = 0; h < c.H; h++)
                            {
                                int bx = mx * c.H + h;
                                int by = my * c.V + v;
                                bool store = bx < c.BlocksW && by < c.BlocksH;

                                bool idct = true;
                                if (opt.SkipInteriorIdct && store)
                                {
                                    // The zone engine never samples the interior rectangle
                                    // [xLo, xHi) x [yLo, yHi) of the component plane, so a block
                                    // may be DC-filled only if it lies ENTIRELY inside it. Anything
                                    // that straddles the boundary gets a full IDCT.
                                    int x0 = bx * 8, x1 = x0 + 8, y0 = by * 8, y1 = y0 + 8;
                                    bool interior = x0 >= ringXLo[c.Idx] && x1 <= ringXHi[c.Idx] &&
                                                    y0 >= ringYLo[c.Idx] && y1 <= ringYHi[c.Idx];
                                    idct = !interior;
                                }

                                bool got = DecodeBlock(reader, tables, c, s, coef, blk, idct);
                                if (got && store)
                                {
                                    WriteBlock(c, bx, by, blk);
                                    if (idct) full++; else skipped++;
                                }
                                else if (got && !store)
                                {
                                    // block exists in the padded grid only; bits consumed
                                }
                            }
                        }
                    }
                    sinceRestart++;
                }
            }

            info.BlocksDecoded = full;
            info.BlocksSkipped = skipped;
            return Render(comps, width, height, maxH, maxV);
        }

        static int CeilDiv(int a, int b)
        {
            if (a <= 0) return 0;
            return (a + b - 1) / b;
        }

        /// <summary>Decode one 8x8 block's coefficients. When <paramref name="idct"/> is false the
        /// AC symbols are still parsed (block alignment depends on them) but discarded, and the
        /// block becomes a flat DC value — a legitimate approximation the caller opts into, and
        /// only ever used for blocks the zone engine never samples.</summary>
        static bool DecodeBlock(BitReader r, Tables tables, Component c, ScanComponent s,
                                int[] coef, byte[] blk, bool idct)
        {
            for (int i = 0; i < 64; i++) coef[i] = 0;

            Huffman dc = tables.Dc[s.DcTable];
            Huffman ac = tables.Ac[s.AcTable];
            if (dc == null) throw new InvalidDataException("jpeg: scan uses a DC Huffman table that was never defined");
            if (ac == null) throw new InvalidDataException("jpeg: scan uses an AC Huffman table that was never defined");
            int[] q = tables.Quant[c.Tq];
            if (q == null) throw new InvalidDataException("jpeg: component uses a DQT that was never defined");

            int t = r.DecodeHuff(dc);
            if (t > 15) throw new InvalidDataException("jpeg: DC category " + t);
            if (t > 0) c.Pred += Extend(r.ReadBits(t), t);

            int k = 1;
            while (k < 64)
            {
                int rs = r.DecodeHuff(ac);
                int run = rs >> 4, size = rs & 15;
                if (size == 0)
                {
                    if (run == 15) { k += 16; continue; }   // ZRL
                    break;                                   // EOB
                }
                k += run;
                if (k > 63) break;
                int v = Extend(r.ReadBits(size), size);
                if (idct) coef[ZigZag[k]] = v;
                k++;
            }

            if (idct)
            {
                coef[0] = c.Pred;
                Idct(coef, q, blk);
            }
            else
            {
                int val = Clamp(c.Pred * q[0] / 8 + 128);
                for (int i = 0; i < 64; i++) blk[i] = (byte)val;
            }
            return true;
        }

        static void WriteBlock(Component c, int bx, int by, byte[] blk)
        {
            int row = by * 8;
            int col = bx * 8;
            int o = 0;
            for (int y = 0; y < 8; y++)
            {
                int p = (row + y) * c.PlaneW + col;
                for (int x = 0; x < 8; x++) c.Plane[p + x] = blk[o++];
            }
        }

        /// <summary>Two-pass float IDCT over the exact cosine basis, then level shift + clamp.</summary>
        static void Idct(int[] coef, int[] q, byte[] blk)
        {
            Span<double> dq = stackalloc double[64];
            for (int i = 0; i < 64; i++) dq[i] = coef[i] * (double)q[i];

            // pass 1: vertical (u -> y). tmp[y*8 + v]
            Span<double> tmp = stackalloc double[64];
            for (int y = 0; y < 8; y++)
            {
                for (int v = 0; v < 8; v++)
                {
                    double sum = 0;
                    for (int u = 0; u < 8; u++) sum += A[u][y] * dq[u * 8 + v];
                    tmp[y * 8 + v] = sum;
                }
            }

            // pass 2: horizontal (v -> x)
            for (int y = 0; y < 8; y++)
            {
                int o = y * 8;
                for (int x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (int v = 0; v < 8; v++) sum += A[v][x] * tmp[o + v];
                    blk[o + x] = (byte)Clamp((int)Math.Round(sum) + 128);
                }
            }
        }

        /// <summary>Receive and extend a JPEG magnitude category (T.81 F.2.2.1).</summary>
        static int Extend(int v, int size)
        {
            int vt = 1 << (size - 1);
            if (v < vt) v = v - (1 << size) + 1;
            return v;
        }

        static int Clamp(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }

        // ================================================================ upsampling + colour

        static PngImage Render(List<Component> comps, int width, int height, int maxH, int maxV)
        {
            var outIm = new PngImage { Width = width, Height = height, Rgba = new byte[width * height * 4] };
            byte[] rgba = outIm.Rgba;
            int n = comps.Count;

            if (n == 1)
            {
                Component y = comps[0];
                byte[] p = UpsamplePlane(y, width, height, maxH, maxV);
                for (int i = 0; i < width * height; i++)
                {
                    byte g = p[i];
                    int o = i * 4;
                    rgba[o] = g; rgba[o + 1] = g; rgba[o + 2] = g; rgba[o + 3] = 255;
                }
                return outIm;
            }

            byte[] py = UpsamplePlane(comps[0], width, height, maxH, maxV);
            byte[] pcb = UpsamplePlane(comps[1], width, height, maxH, maxV);
            byte[] pcr = UpsamplePlane(comps[2], width, height, maxH, maxV);

            for (int i = 0; i < width * height; i++)
            {
                int yy = py[i], cb = pcb[i], cr = pcr[i];
                int o = i * 4;
                rgba[o] = (byte)Clamp(yy + CrR[cr]);
                rgba[o + 1] = (byte)Clamp(yy + CbG[cb] + CrG[cr]);
                rgba[o + 2] = (byte)Clamp(yy + CbB[cb]);
                rgba[o + 3] = 255;
            }
            return outIm;
        }

        static byte[] UpsamplePlane(Component c, int width, int height, int maxH, int maxV)
        {
            int hf = Math.Max(1, maxH / c.H);
            int vf = Math.Max(1, maxV / c.V);

            // The one combination Pillow/libjpeg actually emits for a colour JPEG is 2:2
            // (4:2:0). It gets libjpeg's own h2v2_fancy_upsample arithmetic, which combines the
            // two rows FIRST and rounds once — that single-rounding order is what keeps the
            // decoded pixels on top of the reference decoder's rather than a few LSBs away.
            if (hf == 2 && vf == 2)
                return H2V2Fancy(c.Plane, c.PlaneW, c.PlaneH, width, height);

            // horizontal pass -> stride `width`, `c.PlaneH` rows
            byte[] h = new byte[width * c.PlaneH];
            if (hf == 1)
            {
                for (int y = 0; y < c.PlaneH; y++)
                    Buffer.BlockCopy(c.Plane, y * c.PlaneW, h, y * width, Math.Min(width, c.PlaneW));
            }
            else if (hf == 2)
            {
                for (int y = 0; y < c.PlaneH; y++)
                    Fancy2(c.Plane, y * c.PlaneW, c.PlaneW, h, y * width, width);
            }
            else
            {
                for (int y = 0; y < c.PlaneH; y++)
                {
                    int src = y * c.PlaneW, dst = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int sx = x * c.PlaneW / width;
                        if (sx >= c.PlaneW) sx = c.PlaneW - 1;
                        h[dst + x] = c.Plane[src + sx];
                    }
                }
            }

            // vertical pass
            byte[] o = new byte[width * height];
            if (vf == 1)
            {
                for (int y = 0; y < height; y++)
                    Buffer.BlockCopy(h, Math.Min(y, c.PlaneH - 1) * width, o, y * width, width);
            }
            else if (vf == 2)
            {
                // 4:4:0 style (horizontal full, vertical halved) is not emitted by any encoder
                // Pillow/libjpeg produce with subsampling 0/1/2, so this path is a best-effort
                // triangular interpolation with the same edge clamp as H2V2Fancy.
                int maxM = (height - 1) >> 1;
                if (maxM > c.PlaneH - 1) maxM = c.PlaneH - 1;
                for (int y = 0; y < height; y++)
                {
                    int m = y >> 1;
                    if (m > maxM) m = maxM;
                    int r0 = m;
                    int dst = y * width;
                    if ((y & 1) == 0)
                    {
                        int r1 = m - 1 >= 0 ? m - 1 : r0;
                        int a = r0 * width, b = r1 * width;
                        for (int x = 0; x < width; x++) o[dst + x] = (byte)((3 * h[a + x] + h[b + x] + 1) >> 2);
                    }
                    else
                    {
                        int r1 = m + 1 <= maxM ? m + 1 : r0;
                        int a = r0 * width, b = r1 * width;
                        for (int x = 0; x < width; x++) o[dst + x] = (byte)((3 * h[a + x] + h[b + x] + 2) >> 2);
                    }
                }
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    int sy = y * c.PlaneH / height;
                    if (sy >= c.PlaneH) sy = c.PlaneH - 1;
                    Buffer.BlockCopy(h, sy * width, o, y * width, width);
                }
            }
            return o;
        }

        /// <summary>Vertical-then-horizontal 2:2 triangular upsampling with libjpeg's exact
        /// integer arithmetic (jdsample.c h2v2_fancy_upsample): the two source rows are summed
        /// into a column value first (3:1 in favour of the nearer row) and rounded exactly once,
        /// in the horizontal step. Row/column pairings and edge clamping reproduce libjpeg's
        /// context-row behaviour, so the very first output row is a pure copy of source row 0
        /// and the last output row a copy of the last source row.</summary>
        static byte[] H2V2Fancy(byte[] src, int sw, int sh, int outW, int outH)
        {
            var o = new byte[outW * outH];
            if (sw <= 0 || sh <= 0) return o;
            var colsum = new int[sw];

            // The last output row pairs with the row BELOW it, and libjpeg's context row there is
            // a duplicate of the last row the group actually needed — not of the (padded) last row
            // of the plane. Getting this wrong is invisible except on the final image row, which
            // is why it is clamped to the highest row the requested output can use.
            int maxM = (outH - 1) >> 1;
            if (maxM > sh - 1) maxM = sh - 1;

            for (int m = 0; m <= maxM; m++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    int y = 2 * m + pass;
                    if (y >= outH) break;

                    int other = pass == 0 ? m - 1 : m + 1;
                    if (other < 0) other = 0;
                    if (other > maxM) other = maxM;

                    int a = m * sw, b = other * sw;
                    for (int k = 0; k < sw; k++) colsum[k] = 3 * src[a + k] + src[b + k];

                    int d = y * outW;
                    for (int x = 0; x < outW; x++)
                    {
                        if (x == 0)
                        {
                            o[d + x] = (byte)((colsum[0] * 4 + 8) >> 4);
                            continue;
                        }
                        int k = x >> 1;
                        if (k >= sw) k = sw - 1;
                        if ((x & 1) == 0)
                        {
                            int prev = k - 1 >= 0 ? colsum[k - 1] : colsum[k];
                            o[d + x] = (byte)((colsum[k] * 3 + prev + 8) >> 4);
                        }
                        else if (k + 1 >= sw)
                        {
                            o[d + x] = (byte)((colsum[k] * 4 + 7) >> 4);
                        }
                        else
                        {
                            o[d + x] = (byte)((colsum[k] * 3 + colsum[k + 1] + 7) >> 4);
                        }
                    }
                }
            }
            return o;
        }

        /// <summary>Horizontal 2:1 triangular upsampling, byte-for-byte the same rule libjpeg's
        /// h2v1_fancy_upsample uses: out[0]=s[0]; out[2k]=(3*s[k]+s[k-1]+1)&gt;&gt;2;
        /// out[2k+1]=(3*s[k]+s[k+1]+2)&gt;&gt;2; last output = s[last].</summary>
        static void Fancy2(byte[] src, int srcOff, int sw, byte[] dst, int dstOff, int outW)
        {
            if (outW <= 0 || sw <= 0) return;
            for (int x = 0; x < outW; x++)
            {
                int k = x >> 1;
                if (k >= sw) k = sw - 1;
                int a = src[srcOff + k];
                if ((x & 1) == 0)
                {
                    if (x == 0) { dst[dstOff + x] = (byte)a; continue; }
                    int b = k - 1 >= 0 ? src[srcOff + k - 1] : a;
                    dst[dstOff + x] = (byte)((3 * a + b + 1) >> 2);
                }
                else
                {
                    int b = k + 1 < sw ? src[srcOff + k + 1] : a;
                    dst[dstOff + x] = (byte)((3 * a + b + 2) >> 2);
                }
            }
        }
    }
}
