using System;
using System.IO;
using System.IO.Compression;

namespace TizenAmbilight
{
    /// <summary>A decoded 8-bit RGBA image. Rgba is row-major, width*height*4 bytes,
    /// channel order R,G,B,A — exactly the PNG byte order, so no conversion is done.</summary>
    public sealed class PngImage
    {
        public int Width;
        public int Height;
        public byte[] Rgba;
    }

    /// <summary>
    /// Minimal PNG decoder: 8-bit RGBA only (IHDR colour type 6, bit depth 8, no interlace),
    /// which is exactly what dc_request_capture_to_file_sync writes with compress_type=0.
    ///
    /// No dependency on System.Drawing, SkiaSharp or any NuGet package — only
    /// System.IO.Compression.DeflateStream, which ships with the .NET runtime.
    ///
    /// Implementation:
    ///   1. validate the 8-byte signature
    ///   2. walk the chunk list: read IHDR (width/height/bit depth/colour type/interlace),
    ///      concatenate every IDAT payload, stop at IEND; every other chunk is skipped
    ///   3. drop the 2-byte zlib header, inflate the remaining deflate stream
    ///   4. undo the per-scanline filters (None / Sub / Up / Average / Paeth) with bpp = 4
    ///
    /// The CRC of each chunk is not verified — the zlib Adler-32 trailer already makes a
    /// corrupted stream fail, and the CRC would only add cost to a per-frame hot path.
    /// </summary>
    public static class PngDecoder
    {
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Bytes per pixel for the only format we accept (RGBA, bit depth 8).</summary>
        public const int BytesPerPixel = 4;

        public static PngImage DecodeFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Decode(bytes);
        }

        public static PngImage Decode(byte[] d)
        {
            if (d == null) throw new InvalidDataException("png: null buffer");
            if (d.Length < 8 + 25) throw new InvalidDataException("png: too short (" + d.Length + " bytes)");

            for (int i = 0; i < Signature.Length; i++)
                if (d[i] != Signature[i])
                    throw new InvalidDataException("png: bad signature byte " + i +
                        " (0x" + d[i].ToString("x2") + ")");

            int pos = 8;
            int width = 0, height = 0, bitDepth = 0, colourType = 0, interlace = 0;
            bool haveIhdr = false;

            using (var idat = new MemoryStream())
            {
                while (pos + 8 <= d.Length)
                {
                    long len = ReadBE32(d, pos);
                    pos += 4;
                    if (len < 0 || len > int.MaxValue) throw new InvalidDataException("png: bad chunk length " + len);

                    int typeOff = pos;
                    if (typeOff + 4 > d.Length) throw new InvalidDataException("png: truncated chunk type");
                    string type = "" + (char)d[typeOff] + (char)d[typeOff + 1] +
                                  (char)d[typeOff + 2] + (char)d[typeOff + 3];
                    pos += 4;

                    int dataOff = pos;
                    int n = (int)len;
                    // Overflow-safe form: `n` can be int.MaxValue here, and `dataOff + n` would
                    // wrap to a negative number and slip through the old guard.
                    if (n > d.Length - dataOff)
                        throw new InvalidDataException("png: chunk '" + type + "' overruns file");
                    pos += n;

                    if (pos + 4 > d.Length)
                    {
                        if (type == "IEND") break;
                        throw new InvalidDataException("png: chunk '" + type + "' truncated before CRC");
                    }
                    pos += 4; // CRC, not verified — see class comment

                    if (type == "IHDR")
                    {
                        if (n < 13) throw new InvalidDataException("png: IHDR shorter than 13 bytes");
                        width = (int)ReadBE32(d, dataOff);
                        height = (int)ReadBE32(d, dataOff + 4);
                        bitDepth = d[dataOff + 8];
                        colourType = d[dataOff + 9];
                        int compMethod = d[dataOff + 10];
                        int filterMethod = d[dataOff + 11];
                        interlace = d[dataOff + 12];
                        haveIhdr = true;

                        if (bitDepth != 8)
                            throw new InvalidDataException("png: bit depth " + bitDepth + " unsupported (want 8)");
                        if (colourType != 6)
                            throw new InvalidDataException("png: colour type " + colourType + " unsupported (want 6 = RGBA)");
                        if (compMethod != 0)
                            throw new InvalidDataException("png: compression method " + compMethod + " unsupported");
                        if (filterMethod != 0)
                            throw new InvalidDataException("png: filter method " + filterMethod + " unsupported");
                        if (interlace != 0)
                            throw new InvalidDataException("png: interlace " + interlace + " unsupported (want 0)");
                    }
                    else if (type == "IDAT")
                    {
                        idat.Write(d, dataOff, n);
                    }
                    else if (type == "IEND")
                    {
                        break;
                    }
                    // PLTE / tRNS / gAMA / sRGB / pHYs / tEXt ... all irrelevant for colour type 6
                }

                if (!haveIhdr) throw new InvalidDataException("png: no IHDR chunk");
                if (width <= 0 || height <= 0)
                    throw new InvalidDataException("png: bad dimensions " + width + "x" + height);
                if (width > 16384 || height > 16384)
                    throw new InvalidDataException("png: implausible dimensions " + width + "x" + height);

                long expected = (long)height * (1L + (long)width * BytesPerPixel);

                byte[] z = idat.ToArray();
                if (z.Length < 2) throw new InvalidDataException("png: IDAT stream empty");

                // zlib: 2-byte header (CMF+FLG). Drop it, inflate the raw deflate stream.
                // The trailing 4-byte Adler-32 is simply left unread.
                byte[] filtered;
                using (var ms = new MemoryStream(z, 2, z.Length - 2))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    filtered = ReadFully(ds, expected);
                }

                if (filtered.Length < expected)
                    throw new InvalidDataException("png: inflated " + filtered.Length +
                        " bytes, expected " + expected + " (truncated deflate stream)");

                byte[] rgba = Unfilter(filtered, width, height, BytesPerPixel);
                return new PngImage { Width = width, Height = height, Rgba = rgba };
            }
        }

        static byte[] ReadFully(Stream s, long n)
        {
            byte[] buf = new byte[n];
            int off = 0;
            while (off < buf.Length)
            {
                int r = s.Read(buf, off, buf.Length - off);
                if (r <= 0) break;
                off += r;
            }
            if (off == buf.Length) return buf;
            byte[] t = new byte[off];
            Array.Copy(buf, t, off);
            return t;
        }

        /// <summary>Undo the PNG scanline filters in place into a fresh RGBA buffer.
        /// Each row on the wire is 1 filter-type byte followed by stride payload bytes.</summary>
        static byte[] Unfilter(byte[] raw, int w, int h, int bpp)
        {
            int stride = w * bpp;
            byte[] outBytes = new byte[checked(stride * h)];
            byte[] prev = new byte[stride];
            byte[] cur = new byte[stride];
            int src = 0;

            for (int y = 0; y < h; y++)
            {
                if (src >= raw.Length)
                    throw new InvalidDataException("png: ran out of filtered data on row " + y);
                int ft = raw[src++];
                if (src + stride > raw.Length)
                    throw new InvalidDataException("png: row " + y + " truncated");
                Buffer.BlockCopy(raw, src, cur, 0, stride);
                src += stride;

                switch (ft)
                {
                    case 0: // None
                        break;
                    case 1: // Sub
                        for (int i = bpp; i < stride; i++)
                            cur[i] = (byte)(cur[i] + cur[i - bpp]);
                        break;
                    case 2: // Up
                        for (int i = 0; i < stride; i++)
                            cur[i] = (byte)(cur[i] + prev[i]);
                        break;
                    case 3: // Average
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? cur[i - bpp] : 0;
                            int b = prev[i];
                            cur[i] = (byte)(cur[i] + ((a + b) >> 1));
                        }
                        break;
                    case 4: // Paeth
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? cur[i - bpp] : 0;
                            int b = prev[i];
                            int c = i >= bpp ? prev[i - bpp] : 0;
                            cur[i] = (byte)(cur[i] + Paeth(a, b, c));
                        }
                        break;
                    default:
                        throw new InvalidDataException("png: invalid filter type " + ft + " on row " + y);
                }

                Buffer.BlockCopy(cur, 0, outBytes, y * stride, stride);
                Buffer.BlockCopy(cur, 0, prev, 0, stride);
            }
            return outBytes;
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = p > a ? p - a : a - p;
            int pb = p > b ? p - b : b - p;
            int pc = p > c ? p - c : c - p;
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        static long ReadBE32(byte[] b, int off)
        {
            return ((long)b[off] << 24) | ((long)b[off + 1] << 16) |
                   ((long)b[off + 2] << 8) | (long)b[off + 3];
        }
    }
}
