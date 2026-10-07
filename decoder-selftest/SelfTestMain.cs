using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TizenAmbilight;

namespace DecoderSelfTest
{
    /// <summary>
    /// Runs the SAME decoder the TV app uses and byte-compares its output against PNGs produced
    /// by a completely independent implementation (a hand-written Python/zlib encoder that also
    /// emits the expected decoded bytes). Exits non-zero if anything fails.
    /// </summary>
    static class SelfTestMain
    {
        static int _fail;
        static int _pass;

        static void Check(bool ok, string what, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("  PASS  " + what + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
            else { _fail++; Console.WriteLine("  FAIL  " + what + "  [" + detail + "]"); }
        }

        static int Main(string[] args)
        {
            string dataDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "testdata");
            if (!Directory.Exists(dataDir))
                dataDir = Path.Combine(Directory.GetCurrentDirectory(), "testdata");
            dataDir = Path.GetFullPath(dataDir);

            Console.WriteLine("== TizenAmbilight PNG decoder self-test ==");
            Console.WriteLine("testdata: " + dataDir);
            Console.WriteLine();

            // ============================================================ 1. PNG decode vs reference
            Console.WriteLine("[1] PNG decode vs independent reference encoder");
            string manifestPath = Path.Combine(dataDir, "manifest.json");
            Check(File.Exists(manifestPath), "manifest.json present", manifestPath);
            string manifest = File.ReadAllText(manifestPath);

            var names = new List<string>();
            int mp = 0;
            while (true)
            {
                int a = manifest.IndexOf("\"name\"", mp, StringComparison.Ordinal);
                if (a < 0) break;
                int q1 = manifest.IndexOf('"', manifest.IndexOf(':', a) + 1);
                int q2 = manifest.IndexOf('"', q1 + 1);
                names.Add(manifest.Substring(q1 + 1, q2 - q1 - 1));
                mp = q2 + 1;
            }
            Check(names.Count >= 5, "manifest lists test cases", names.Count + " cases");

            foreach (string name in names)
            {
                string pngPath = Path.Combine(dataDir, name + ".png");
                string rgbaPath = Path.Combine(dataDir, name + ".rgba");
                if (!File.Exists(pngPath) || !File.Exists(rgbaPath))
                {
                    Check(false, name, "missing png or rgba");
                    continue;
                }

                byte[] expected = File.ReadAllBytes(rgbaPath);
                var sw = Stopwatch.StartNew();
                PngImage img;
                try { img = PngDecoder.DecodeFile(pngPath); }
                catch (Exception e) { Check(false, name, "decode threw " + e.GetType().Name + ": " + e.Message); continue; }
                sw.Stop();

                if (img.Rgba.Length != expected.Length)
                {
                    Check(false, name, "decoded " + img.Rgba.Length + " bytes, expected " + expected.Length);
                    continue;
                }

                int firstDiff = -1;
                for (int i = 0; i < expected.Length; i++)
                    if (img.Rgba[i] != expected[i]) { firstDiff = i; break; }

                bool ok = firstDiff < 0;
                string detail = img.Width + "x" + img.Height + " " + expected.Length + " bytes, " +
                                sw.Elapsed.TotalMilliseconds.ToString("0.00") + " ms";
                if (!ok) detail += ", FIRST DIFF at byte " + firstDiff + ": got 0x" +
                                   img.Rgba[firstDiff].ToString("x2") + " want 0x" + expected[firstDiff].ToString("x2");
                Check(ok, name, detail);
            }

            // ============================================================ 2. embedded in-app self-test
            Console.WriteLine();
            Console.WriteLine("[2] embedded in-app self-test (the exact string the TV reports at startup)");
            string verdict = PngSelfTest.Run();
            Check(verdict.StartsWith("PASS"), "PngSelfTest.Run()", verdict);

            // ============================================================ 3. negative cases
            Console.WriteLine();
            Console.WriteLine("[3] decoder must reject what it does not support");
            {
                byte[] good = File.ReadAllBytes(Path.Combine(dataDir, names[0] + ".png"));

                byte[] badSig = (byte[])good.Clone();
                badSig[1] = 0x00;
                Check(Throws(() => PngDecoder.Decode(badSig)), "corrupt signature rejected", "signature byte 1 zeroed");

                byte[] trunc = new byte[good.Length / 2];
                Array.Copy(good, trunc, trunc.Length);
                Check(Throws(() => PngDecoder.Decode(trunc)), "truncated file rejected", trunc.Length + " of " + good.Length + " bytes");

                Check(Throws(() => PngDecoder.Decode(new byte[] { 1, 2, 3 })), "runt buffer rejected", "3 bytes");

                // bit depth 16 -> must be refused (colour type 6, bit depth 16 is legal PNG, unsupported here)
                byte[] depth16 = (byte[])good.Clone();
                depth16[24] = 16;   // IHDR: 8 sig + 4 len + 4 type + 4 w + 4 h + bitdepth at offset 24
                Check(Throws(() => PngDecoder.Decode(depth16)), "bit depth 16 rejected", "IHDR bit depth patched to 16");
            }

            // ============================================================ 4. zone layout
            Console.WriteLine();
            Console.WriteLine("[4] zone layout (73 zones) and zone-colour extraction");
            var layout = new ZoneLayout(960, 540, 73, 0.08);
            Check(layout.Total == 73, "zone count is 73", layout.Total.ToString());
            Check(layout.LeftCount == 13 && layout.TopCount == 23 &&
                  layout.RightCount == 13 && layout.BottomCount == 24,
                  "split is left 13 / top 23 / right 13 / bottom 24",
                  layout.LeftCount + "/" + layout.TopCount + "/" + layout.RightCount + "/" + layout.BottomCount);
            Check(layout.Zones.Length == 73, "zone array length 73", layout.Zones.Length.ToString());
            Check(layout.Zones[0].Edge == "top" && layout.Zones[22].Edge == "top" &&
                  layout.Zones[23].Edge == "right" && layout.Zones[35].Edge == "right" &&
                  layout.Zones[36].Edge == "bottom" && layout.Zones[59].Edge == "bottom" &&
                  layout.Zones[60].Edge == "left" && layout.Zones[72].Edge == "left",
                  "walk order is top -> right -> bottom -> left",
                  layout.Zones[0].Edge + "->" + layout.Zones[23].Edge + "->" +
                  layout.Zones[36].Edge + "->" + layout.Zones[60].Edge);
            Check(layout.BandX == 77 && layout.BandY == 43, "border band is 8% of each dimension (rounded)",
                  layout.BandX + "x" + layout.BandY + " px = " +
                  (100.0 * layout.BandX / 960).ToString("0.0") + "% x " +
                  (100.0 * layout.BandY / 540).ToString("0.0") + "%");
            Check(layout.BandX >= 0.06 * 960 && layout.BandX <= 0.10 * 960 &&
                  layout.BandY >= 0.06 * 540 && layout.BandY <= 0.10 * 540,
                  "band sits inside the requested 6-10% range", "inward from the edge");

            // left band pure red, everything else black: every LEFT zone must read exactly red
            {
                var img = SolidImage(100, 100, 0, 0, 0);
                for (int y = 0; y < 100; y++)
                    for (int x = 0; x < new ZoneLayout(100, 100, 73, 0.08).BandX; x++)
                        SetPixel(img, x, y, 255, 0, 0);

                var l2 = new ZoneLayout(100, 100, 73, 0.08);
                ZoneColors c = l2.Extract(img);
                bool allRed = true;
                for (int i = 0; i < l2.Total; i++)
                {
                    if (l2.Zones[i].Edge != "left") continue;
                    if (c.R[i] != 255 || c.G[i] != 0 || c.B[i] != 0) { allRed = false; break; }
                }
                Check(allRed, "every left-edge zone reads the left border band exactly (255,0,0)",
                      "left zones = " + l2.LeftCount);

                // and the image CENTRE must never be sampled: make the centre white, re-extract,
                // and confirm no zone changes.
                for (int y = 30; y < 70; y++)
                    for (int x = 30; x < 70; x++)
                        SetPixel(img, x, y, 255, 255, 255);
                ZoneColors c2 = l2.Extract(img);
                bool unchanged = true;
                for (int i = 0; i < l2.Total; i++)
                    if (c.R[i] != c2.R[i] || c.G[i] != c2.G[i] || c.B[i] != c2.B[i]) { unchanged = false; break; }
                Check(unchanged, "centre pixels are never sampled (centre painted white, no zone changed)", "");
            }

            // ============================================================ 5. smoothing + DRGB
            Console.WriteLine();
            Console.WriteLine("[5] temporal smoothing and the DRGB packet builder");
            {
                var a = new ZoneColors(3);
                a.R[0] = 100; a.G[0] = 200; a.B[0] = 0;
                a.R[1] = 0; a.G[1] = 0; a.B[1] = 0;
                a.R[2] = 255; a.G[2] = 255; a.B[2] = 255;

                var raw = new ZoneColors(3);
                raw.R[0] = 200; raw.G[0] = 100; raw.B[0] = 100;
                raw.R[1] = 50; raw.G[1] = 50; raw.B[1] = 50;
                raw.R[2] = 0; raw.G[2] = 0; raw.B[2] = 0;

                a.SmoothTowards(raw, 0.5);
                // 0.5*200 + 0.5*100 = 150 ; 0.5*100+0.5*200 = 150 ; 0.5*100+0.5*0 = 50
                Check(a.R[0] == 150 && a.G[0] == 150 && a.B[0] == 50, "EMA alpha=0.5 arithmetic",
                      "(" + a.R[0] + "," + a.G[0] + "," + a.B[0] + ")");
                Check(a.R[1] == 25 && a.R[2] == 128, "EMA on the other channels",
                      "R1=" + a.R[1] + " R2=" + a.R[2]);

                var cols = new ZoneColors(73);
                for (int i = 0; i < 73; i++) { cols.R[i] = (byte)i; cols.G[i] = (byte)(255 - i); cols.B[i] = 7; }
                byte[] p = WledOutput.BuildDrgb(cols, 1);
                Check(p.Length == 221, "DRGB payload is 2 + 73*3 = 221 bytes", p.Length.ToString());
                Check(p[0] == 0x02, "DRGB protocol byte is 0x02", "0x" + p[0].ToString("x2"));
                Check(p[1] == 1, "DRGB timeout byte", p[1].ToString());
                Check(p[2] == 0 && p[3] == 255 && p[4] == 7, "LED 0 RGB at offsets 2,3,4",
                      p[2] + "," + p[3] + "," + p[4]);
                Check(p[2 + 72 * 3] == 72 && p[2 + 72 * 3 + 1] == 183, "LED 72 RGB at the end",
                      p[2 + 72 * 3] + "," + p[2 + 72 * 3 + 1] + "," + p[2 + 72 * 3 + 2]);
            }

            // ============================================================ 6. json + delta
            Console.WriteLine();
            Console.WriteLine("[6] JSON writer and smoothed-vs-raw delta");
            {
                var o = new Dictionary<string, object>
                {
                    { "s", "a\"b\n" }, { "n", 42L }, { "d", 0.5 }, { "b", true },
                    { "list", new List<object> { 1L, "x" } },
                    { "nested", new Dictionary<string, object> { { "k", null } } },
                };
                string j = Json.ToJson(o);
                Check(j.Contains("\"s\":\"a\\\"b\\n\"") && j.Contains("\"n\":42") &&
                      j.Contains("\"b\":true") && j.Contains("\"list\":[1,\"x\"]") &&
                      j.Contains("\"k\":null"), "JSON escapes and shapes look right", j);

                var x = new ZoneColors(2); x.R[0] = 10; x.G[0] = 20; x.B[0] = 30;
                var y = new ZoneColors(2); y.R[0] = 20; y.G[0] = 20; y.B[0] = 30;
                double d = Colors.MeanDelta(x, y);
                Check(Math.Abs(d - (10.0 / 6.0)) < 1e-9, "mean delta over all channels",
                      d.ToString("0.0000"));
            }

            // ============================================================ 7. JPEG decoder (v3)
            // v3's only new risk is the baseline JPEG decoder. It is proved here against
            // libjpeg-turbo by way of Pillow: gen_jpeg_testdata.py encodes real JPEGs and stores
            // PILLOW'S OWN decode as the reference, so the C# decoder is compared to a foreign
            // implementation's pixels, never to a hand-written expectation. Every case reports
            // its maximum per-channel error; only a re-encode could hide a bug, and nothing here
            // re-encodes.
            Console.WriteLine();
            Console.WriteLine("[7] baseline JPEG decoder vs Pillow (libjpeg-turbo)");
            {
                string jmPath = Path.Combine(dataDir, "jpeg_manifest.json");
                Check(File.Exists(jmPath), "jpeg_manifest.json present", jmPath);
                List<Dictionary<string, string>> jcases = ParseCases(File.ReadAllText(jmPath));
                Check(jcases.Count >= 10, "jpeg manifest lists cases", jcases.Count + " cases");

                // Gate values. MEASURED worst case across all 12 cases is 3 (see the per-case
                // lines above); the gate is one LSB above that so a different libjpeg-turbo
                // rounding build cannot fail the suite, while a genuine decoder bug (wrong zigzag,
                // wrong dequant, wrong DC prediction, wrong edge handling, wrong upsampling) is
                // orders of magnitude too big to slip through.
                const int MaxErrGate = 4;          // per-channel maximum
                const double MeanErrGate = 0.5;    // per-channel mean
                int worstMax = 0;
                string worstCase = "";
                long fullMs = 0;

                foreach (Dictionary<string, string> jc in jcases)
                {
                    string nm = jc["name"];
                    string jpg = Path.Combine(dataDir, jc["file"]);
                    string rpath = Path.Combine(dataDir, jc["reference"]);
                    if (!File.Exists(jpg) || !File.Exists(rpath))
                    {
                        Check(false, nm, "missing jpg or reference rgba");
                        continue;
                    }
                    byte[] expected = File.ReadAllBytes(rpath);
                    int ew = int.Parse(jc["width"]), eh = int.Parse(jc["height"]);
                    if (expected.Length != ew * eh * 4)
                    {
                        Check(false, nm, "reference is " + expected.Length + " bytes, expected " + (ew * eh * 4));
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    PngImage img;
                    try { img = JpegDecoder.DecodeFile(jpg); }
                    catch (Exception e)
                    {
                        Check(false, nm, "decode threw " + e.GetType().Name + ": " + e.Message);
                        continue;
                    }
                    sw.Stop();
                    fullMs += (long)sw.Elapsed.TotalMilliseconds;

                    string desc = JpegDecoder.LastInfo != null ? JpegDecoder.LastInfo.Describe() : "";

                    if (img.Width != ew || img.Height != eh)
                    {
                        Check(false, nm, "decoded " + img.Width + "x" + img.Height + ", expected " + ew + "x" + eh);
                        continue;
                    }

                    int maxErr = 0, firstBad = -1;
                    long sum = 0;
                    long chan = (long)ew * eh * 3;
                    for (int i = 0; i < expected.Length; i++)
                    {
                        if ((i & 3) == 3)
                        {
                            if (img.Rgba[i] != 255) { maxErr = 256; firstBad = i; break; }
                            continue;
                        }
                        int d = Math.Abs(img.Rgba[i] - expected[i]);
                        if (d > maxErr) maxErr = d;
                        if (d > 0 && firstBad < 0) firstBad = i;
                        sum += d;
                    }
                    double mean = chan > 0 ? (double)sum / chan : 0.0;
                    if (maxErr > worstMax) { worstMax = maxErr; worstCase = nm; }

                    // Optional forensic dump: JPEG_DUMP=1 writes our decode next to Pillow's so a
                    // disagreement can be localised in a second tool instead of guessed at.
                    if (Environment.GetEnvironmentVariable("JPEG_DUMP") == "1")
                        File.WriteAllBytes(Path.Combine(dataDir, nm + ".ours.rgba"), img.Rgba);

                    bool ok = maxErr <= MaxErrGate && mean <= MeanErrGate;
                    string detail = "max=" + maxErr + " mean=" + mean.ToString("0.0000") +
                                    (maxErr >= 0 && firstBad >= 0
                                        ? " first_diff@" + firstBad + " got " + img.Rgba[firstBad] +
                                          " want " + expected[firstBad]
                                        : " (bit-exact)") +
                                    "  " + sw.Elapsed.TotalMilliseconds.ToString("0.00") + "ms  " + desc;
                    Check(ok, nm, detail);
                }

                Check(worstMax <= MaxErrGate, "worst-case per-channel error across ALL jpeg cases",
                      "max=" + worstMax + " on " + worstCase + " (gate " + MaxErrGate + ")");
                Console.WriteLine("      max per-channel error vs Pillow: " + worstMax +
                                  " (worst case " + worstCase + "); total full-decode time for all cases " +
                                  fullMs + " ms");

                // ---- the fast path: zone colours must be BIT-IDENTICAL to the full decode ----
                // JpegOptions.ForBorder skips the IDCT only for blocks that lie entirely outside
                // the border ring ZoneEngine samples, so this is not a tolerance test — it must
                // come out exactly equal, on every case, or the optimisation is not shippable.
                // The DC-only whole-frame variant is reported separately because it IS an
                // approximation and is NOT the mode that ships.
                Console.WriteLine();
                Console.WriteLine("[8] JPEG fast path (skipped interior IDCT) vs full decode, by zone colour");
                {
                    int fastCases = 0, fastBad = 0, worstDcMax = 0;
                    double worstDcMean = 0;
                    long ringFastMs = 0, fullDecodeMs = 0, blocksFull = 0, blocksSkipped = 0;
                    int oddExact = 0;

                    foreach (Dictionary<string, string> jc in jcases)
                    {
                        string jpg = Path.Combine(dataDir, jc["file"]);
                        if (!File.Exists(jpg)) continue;

                        var fsw = Stopwatch.StartNew();
                        PngImage full = JpegDecoder.DecodeFile(jpg);
                        fsw.Stop();
                        fullDecodeMs += (long)fsw.Elapsed.TotalMilliseconds;
                        var jlayout = new ZoneLayout(full.Width, full.Height, 73, 0.08);
                        ZoneColors zFull = jlayout.Extract(full);

                        var opt = JpegOptions.ForBorder(jlayout.BandX, jlayout.BandY);
                        double best = double.MaxValue;
                        PngImage fast = null;
                        for (int rep = 0; rep < 3; rep++)
                        {
                            var sw = Stopwatch.StartNew();
                            PngImage f = JpegDecoder.DecodeFile(jpg, opt);
                            sw.Stop();
                            if (sw.Elapsed.TotalMilliseconds < best) { best = sw.Elapsed.TotalMilliseconds; fast = f; }
                        }
                        JpegFrameInfo fi = JpegDecoder.LastInfo;
                        ZoneColors zFast = jlayout.Extract(fast);

                        int dmax = 0;
                        for (int i = 0; i < zFull.Count; i++)
                        {
                            dmax = Math.Max(dmax, Math.Abs(zFull.R[i] - zFast.R[i]));
                            dmax = Math.Max(dmax, Math.Abs(zFull.G[i] - zFast.G[i]));
                            dmax = Math.Max(dmax, Math.Abs(zFull.B[i] - zFast.B[i]));
                        }
                        fastCases++;
                        if (dmax == 0) oddExact++; else fastBad++;
                        if (fi != null) { blocksFull += fi.BlocksDecoded; blocksSkipped += fi.BlocksSkipped; }
                        ringFastMs += (long)best;

                        // whole-frame DC-only: an approximation, measured rather than gated
                        PngImage dcIm = JpegDecoder.DecodeFile(jpg, JpegOptions.ForBorder(0, 0));
                        ZoneColors zDc = jlayout.Extract(dcIm);
                        int dmaxDc = 0;
                        long sDc = 0;
                        for (int i = 0; i < zFull.Count; i++)
                        {
                            int dr = Math.Abs(zFull.R[i] - zDc.R[i]);
                            int dg = Math.Abs(zFull.G[i] - zDc.G[i]);
                            int db = Math.Abs(zFull.B[i] - zDc.B[i]);
                            dmaxDc = Math.Max(dmaxDc, Math.Max(dr, Math.Max(dg, db)));
                            sDc += dr + dg + db;
                        }
                        double meanDc = (double)sDc / (zFull.Count * 3);
                        if (dmaxDc > worstDcMax) { worstDcMax = dmaxDc; worstDcMean = meanDc; }

                        Console.WriteLine("      " + jc["name"].PadRight(28) +
                                          " ring " + jlayout.BandX + "x" + jlayout.BandY +
                                          ": zone delta max=" + dmax +
                                          "  fast=" + best.ToString("0.0") + "ms" +
                                          "  (idct " + (fi != null ? fi.BlocksDecoded : -1) +
                                          ", dc-only " + (fi != null ? fi.BlocksSkipped : -1) + ")");
                    }

                    Check(fastCases > 0 && fastBad == 0,
                          "border-ring fast path: zone colours identical to a full decode on EVERY case",
                          oddExact + "/" + fastCases + " cases exact (max zone delta 0 across 73 zones)");
                    Check(blocksSkipped > 0,
                          "fast path really skips interior IDCT work (not a silent full decode)",
                          blocksFull + " full-idct / " + blocksSkipped + " dc-only blocks total");
                    Console.WriteLine("      whole-frame DC-only (worst case, NOT the shipped mode): " +
                                      "max zone delta " + worstDcMax + ", mean " + worstDcMean.ToString("0.00"));
                    Console.WriteLine("      totals: " + blocksFull + " blocks full-IDCT vs " +
                                      blocksSkipped + " DC-only for the ring (" +
                                      (100.0 * blocksSkipped / Math.Max(1.0, blocksFull + blocksSkipped)).ToString("0.0") +
                                      "% of blocks skipped); ring fast-decode " + ringFastMs +
                                      " ms vs full decode " + fullDecodeMs + " ms over " + fastCases + " cases");
                }

                // ---- must-refuse cases ----
                Console.WriteLine();
                Console.WriteLine("[9] JPEG decoder refuses what it does not implement");
                {
                    string cmyk = Path.Combine(dataDir, "jpeg_cmyk_32x32.jpg");
                    Check(File.Exists(cmyk), "4-component CMYK test file present", cmyk);
                    Check(Throws(() => JpegDecoder.DecodeFile(cmyk)),
                          "4-component CMYK JPEG refused (not silently mis-decoded)",
                          "jpeg_cmyk_32x32.jpg");

                    string prog = Path.Combine(dataDir, "probe_progressive_300x200.jpg");
                    Check(File.Exists(prog), "progressive test file present", prog);
                    Check(Throws(() => JpegDecoder.DecodeFile(prog)),
                          "progressive (SOF2) JPEG refused", "probe_progressive_300x200.jpg");

                    string png = Path.Combine(dataDir, "probe_real.png");
                    Check(Throws(() => JpegDecoder.DecodeFile(png)),
                          "a PNG handed to the JPEG decoder is refused", "probe_real.png");

                    Check(Throws(() => JpegDecoder.Decode(new byte[] { 0xFF, 0xD8, 0x00 })),
                          "truncated JPEG refused", "3 bytes");

                    byte[] full = File.ReadAllBytes(Path.Combine(dataDir, "jpeg_q90_480x270.jpg"));
                    byte[] cut = new byte[full.Length / 3];
                    Array.Copy(full, cut, cut.Length);
                    Check(Throws(() => JpegDecoder.Decode(cut)),
                          "JPEG truncated to a third refused", cut.Length + " of " + full.Length + " bytes");

                    byte[] noSof = (byte[])full.Clone();
                    // corrupt the SOF0 precision byte so the frame is no longer 8-bit baseline
                    for (int i = 2; i < noSof.Length - 4; i++)
                    {
                        if (noSof[i] == 0xFF && noSof[i + 1] == 0xC0)
                        {
                            noSof[i + 4] = 12;   // precision 12
                            break;
                        }
                    }
                    Check(Throws(() => JpegDecoder.Decode(noSof)),
                          "12-bit precision (SOF0 precision=12) refused", "precision patched");
                }
            }

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("PASSED " + _pass + "   FAILED " + _fail);
            Console.WriteLine("==================================================");
            return _fail == 0 ? 0 : 1;
        }

        static bool Throws(Action a)
        {
            try { a(); return false; }
            catch { return true; }
        }

        /// <summary>Pull the {name, file, reference, width, height, bytes} records out of a test
        /// manifest. The generator writes one flat object per case with those keys first, followed
        /// by nested objects, so a first-hit scan over each {...} body is exact and does not need a
        /// JSON library in the harness.</summary>
        static List<Dictionary<string, string>> ParseCases(string json)
        {
            var list = new List<Dictionary<string, string>>();
            if (json == null) return list;
            int i = json.IndexOf("\"cases\"", StringComparison.Ordinal);
            if (i < 0) i = 0;
            string[] keys = { "name", "file", "reference", "width", "height", "bytes" };
            while (true)
            {
                int a = json.IndexOf('{', i);
                if (a < 0) break;
                int b = json.IndexOf('}', a);
                if (b < 0) break;
                string body = json.Substring(a + 1, b - a - 1);
                var d = new Dictionary<string, string>();
                foreach (string key in keys)
                {
                    int k = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                    if (k < 0) continue;
                    int c = body.IndexOf(':', k);
                    if (c < 0) continue;
                    c++;
                    while (c < body.Length && body[c] == ' ') c++;
                    if (c < body.Length && body[c] == '"')
                    {
                        int e = body.IndexOf('"', c + 1);
                        if (e < 0) continue;
                        d[key] = body.Substring(c + 1, e - c - 1);
                    }
                    else
                    {
                        int e = c;
                        while (e < body.Length && (char.IsDigit(body[e]) || body[e] == '-')) e++;
                        d[key] = body.Substring(c, e - c);
                    }
                }
                if (d.ContainsKey("name")) list.Add(d);
                i = b + 1;
                if (i >= json.Length) break;
            }
            return list;
        }

        static PngImage SolidImage(int w, int h, byte r, byte g, byte b)
        {
            var img = new PngImage { Width = w, Height = h, Rgba = new byte[w * h * 4] };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    SetPixel(img, x, y, r, g, b);
            return img;
        }

        static void SetPixel(PngImage img, int x, int y, byte r, byte g, byte b)
        {
            int o = (y * img.Width + x) * 4;
            img.Rgba[o] = r; img.Rgba[o + 1] = g; img.Rgba[o + 2] = b; img.Rgba[o + 3] = 255;
        }
    }
}
