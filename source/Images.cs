// TTVR Workshop Plus - DDS / TGA decoding (Unity 2017's LoadImage only reads PNG and JPG).
// Output is RGBA32 with bottom-up rows, like Texture2D.GetPixels32.
using System;

namespace TTVRPlus
{
    public enum ImageKind { Unknown, Png, Jpg, Dds, Tga }

    public static class ImageDecoder
    {
        public static ImageKind Detect(byte[] d, string path)
        {
            if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return ImageKind.Png;
            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ImageKind.Jpg;
            if (d.Length >= 128 && d[0] == 'D' && d[1] == 'D' && d[2] == 'S' && d[3] == ' ') return ImageKind.Dds;
            string p = (path ?? "").ToLowerInvariant();
            if (p.EndsWith(".tga") && d.Length >= 18) return ImageKind.Tga;
            return ImageKind.Unknown;
        }

        public static bool TryDecode(byte[] d, string path, out int w, out int h, out byte[] rgba, out string error)
        {
            w = h = 0; rgba = null; error = null;
            try
            {
                switch (Detect(d, path))
                {
                    case ImageKind.Dds: return Dds(d, out w, out h, out rgba, out error);
                    case ImageKind.Tga: return Tga(d, out w, out h, out rgba, out error);
                    default: error = "not a DDS or TGA image"; return false;
                }
            }
            catch (Exception e) { error = "damaged or unsupported image (" + e.Message + ")"; return false; }
        }

        static uint U32(byte[] d, int o) { return (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24); }
        static int U16(byte[] d, int o) { return d[o] | d[o + 1] << 8; }

        // ------------------------------------------------------------------ DDS
        enum Fmt { None, BC1, BC2, BC3, BC4, BC5, BC7, RGBA8, BGRA8, BGRX8, Masked }

        static bool Dds(byte[] d, out int w, out int h, out byte[] rgba, out string error)
        {
            rgba = null; error = null;
            h = (int)U32(d, 12); w = (int)U32(d, 16);
            uint pfFlags = U32(d, 80), fourCC = U32(d, 84), bits = U32(d, 88);
            uint rMask = U32(d, 92), gMask = U32(d, 96), bMask = U32(d, 100), aMask = U32(d, 104);
            int off = 128;
            Fmt f = Fmt.None;
            string cc = "" + (char)(fourCC & 0xFF) + (char)((fourCC >> 8) & 0xFF) + (char)((fourCC >> 16) & 0xFF) + (char)(fourCC >> 24);
            if ((pfFlags & 0x4) != 0)
            {
                switch (cc)
                {
                    case "DXT1": f = Fmt.BC1; break;
                    case "DXT2": case "DXT3": f = Fmt.BC2; break;
                    case "DXT4": case "DXT5": f = Fmt.BC3; break;
                    case "ATI1": case "BC4U": case "BC4S": f = Fmt.BC4; break;
                    case "ATI2": case "BC5U": case "BC5S": f = Fmt.BC5; break;
                    case "DX10":
                        uint dxgi = U32(d, 128); off = 148;
                        switch (dxgi)
                        {
                            case 70: case 71: case 72: f = Fmt.BC1; break;
                            case 73: case 74: case 75: f = Fmt.BC2; break;
                            case 76: case 77: case 78: f = Fmt.BC3; break;
                            case 79: case 80: case 81: f = Fmt.BC4; break;
                            case 82: case 83: case 84: f = Fmt.BC5; break;
                            case 97: case 98: case 99: f = Fmt.BC7; break;
                            case 27: case 28: case 29: f = Fmt.RGBA8; break;
                            case 87: case 90: case 91: f = Fmt.BGRA8; break;
                            case 88: case 92: case 93: f = Fmt.BGRX8; break;
                            default: error = "DDS format " + DxgiName(dxgi) + " isn't supported; save it as PNG"; return false;
                        }
                        break;
                    default: error = "DDS compression '" + cc + "' isn't supported; save it as PNG"; return false;
                }
            }
            else if ((pfFlags & 0x40) != 0 || (pfFlags & 0x20000) != 0) f = Fmt.Masked;   // RGB or luminance
            else { error = "unknown DDS pixel format; save it as PNG"; return false; }
            if (w <= 0 || h <= 0 || w > 16384 || h > 16384) { error = "bad DDS size"; return false; }

            var top = new byte[w * h * 4];   // top-down while decoding
            switch (f)
            {
                case Fmt.BC1: Blocks(d, off, w, h, 8, top, Bc1Block); break;
                case Fmt.BC2: Blocks(d, off, w, h, 16, top, Bc2Block); break;
                case Fmt.BC3: Blocks(d, off, w, h, 16, top, Bc3Block); break;
                case Fmt.BC4: Blocks(d, off, w, h, 8, top, Bc4Block); break;
                case Fmt.BC5: Blocks(d, off, w, h, 16, top, Bc5Block); break;
                case Fmt.BC7: Blocks(d, off, w, h, 16, top, Bc7.Decode); break;
                case Fmt.RGBA8: Raw(d, off, w, h, 4, top, 0, 1, 2, 3); break;
                case Fmt.BGRA8: Raw(d, off, w, h, 4, top, 2, 1, 0, 3); break;
                case Fmt.BGRX8: Raw(d, off, w, h, 4, top, 2, 1, 0, -1); break;
                case Fmt.Masked:
                    if (bits != 8 && bits != 16 && bits != 24 && bits != 32) { error = bits + "-bit DDS isn't supported; save it as PNG"; return false; }
                    if ((pfFlags & 0x20000) != 0 && rMask != 0) { gMask = bMask = rMask; }      // luminance
                    if ((pfFlags & 0x1) == 0) aMask = 0;
                    Masked(d, off, w, h, (int)bits / 8, top, rMask, gMask, bMask, aMask);
                    break;
            }
            rgba = FlipRows(top, w, h);
            return true;
        }

        static string DxgiName(uint f)
        {
            switch (f) { case 95: case 96: return "BC6H (HDR)"; case 10: return "RGBA16F"; case 2: return "RGBA32F"; default: return "#" + f; }
        }

        delegate void BlockFn(byte[] src, int o, byte[] px);   // px = 16 RGBA texels

        static void Blocks(byte[] d, int off, int w, int h, int bsize, byte[] dst, BlockFn fn)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            var px = new byte[64];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int o = off + (by * bw + bx) * bsize;
                    if (o + bsize > d.Length) throw new Exception("DDS file is truncated");
                    fn(d, o, px);
                    for (int y = 0; y < 4; y++)
                    {
                        int yy = by * 4 + y; if (yy >= h) break;
                        for (int x = 0; x < 4; x++)
                        {
                            int xx = bx * 4 + x; if (xx >= w) break;
                            Buffer.BlockCopy(px, (y * 4 + x) * 4, dst, (yy * w + xx) * 4, 4);
                        }
                    }
                }
        }

        static void Raw(byte[] d, int off, int w, int h, int bpp, byte[] dst, int ri, int gi, int bi, int ai)
        {
            if (off + w * h * bpp > d.Length) throw new Exception("DDS file is truncated");
            for (int i = 0; i < w * h; i++)
            {
                int s = off + i * bpp;
                dst[i * 4] = d[s + ri]; dst[i * 4 + 1] = d[s + gi]; dst[i * 4 + 2] = d[s + bi];
                dst[i * 4 + 3] = ai < 0 ? (byte)255 : d[s + ai];
            }
        }

        static byte MaskVal(uint v, uint mask)
        {
            if (mask == 0) return 255;
            int shift = 0; while (((mask >> shift) & 1) == 0) shift++;
            uint m = mask >> shift; uint x = (v & mask) >> shift;
            return (byte)((x * 255 + m / 2) / m);
        }

        static void Masked(byte[] d, int off, int w, int h, int bpp, byte[] dst, uint r, uint g, uint b, uint a)
        {
            if (off + w * h * bpp > d.Length) throw new Exception("DDS file is truncated");
            for (int i = 0; i < w * h; i++)
            {
                int s = off + i * bpp; uint v = 0;
                for (int k = 0; k < bpp; k++) v |= (uint)d[s + k] << (8 * k);
                dst[i * 4] = MaskVal(v, r); dst[i * 4 + 1] = MaskVal(v, g); dst[i * 4 + 2] = MaskVal(v, b);
                dst[i * 4 + 3] = a == 0 ? (byte)255 : MaskVal(v, a);
            }
        }

        static void Rgb565(int c, out int r, out int g, out int b)
        {
            r = (c >> 11) & 31; g = (c >> 5) & 63; b = c & 31;
            r = (r << 3) | (r >> 2); g = (g << 2) | (g >> 4); b = (b << 3) | (b >> 2);
        }

        static void ColorBlock(byte[] s, int o, byte[] px, bool forceFour)
        {
            int c0 = U16(s, o), c1 = U16(s, o + 2);
            int r0, g0, b0, r1, g1, b1; Rgb565(c0, out r0, out g0, out b0); Rgb565(c1, out r1, out g1, out b1);
            var pal = new int[16];
            pal[0] = r0; pal[1] = g0; pal[2] = b0; pal[3] = 255;
            pal[4] = r1; pal[5] = g1; pal[6] = b1; pal[7] = 255;
            if (c0 > c1 || forceFour)
            {
                pal[8] = (2 * r0 + r1) / 3; pal[9] = (2 * g0 + g1) / 3; pal[10] = (2 * b0 + b1) / 3; pal[11] = 255;
                pal[12] = (r0 + 2 * r1) / 3; pal[13] = (g0 + 2 * g1) / 3; pal[14] = (b0 + 2 * b1) / 3; pal[15] = 255;
            }
            else
            {
                pal[8] = (r0 + r1) / 2; pal[9] = (g0 + g1) / 2; pal[10] = (b0 + b1) / 2; pal[11] = 255;
                pal[12] = 0; pal[13] = 0; pal[14] = 0; pal[15] = 0;
            }
            uint idx = U32(s, o + 4);
            for (int i = 0; i < 16; i++)
            {
                int k = (int)((idx >> (2 * i)) & 3) * 4;
                px[i * 4] = (byte)pal[k]; px[i * 4 + 1] = (byte)pal[k + 1]; px[i * 4 + 2] = (byte)pal[k + 2]; px[i * 4 + 3] = (byte)pal[k + 3];
            }
        }

        static void AlphaBlock(byte[] s, int o, byte[] px, int channel)   // BC3 alpha / BC4 / BC5 channel
        {
            int a0 = s[o], a1 = s[o + 1];
            var pal = new int[8]; pal[0] = a0; pal[1] = a1;
            if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            else { for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5; pal[6] = 0; pal[7] = 255; }
            ulong bitsv = 0; for (int i = 0; i < 6; i++) bitsv |= (ulong)s[o + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++) px[i * 4 + channel] = (byte)pal[(int)((bitsv >> (3 * i)) & 7)];
        }

        static void Bc1Block(byte[] s, int o, byte[] px) { ColorBlock(s, o, px, false); }

        static void Bc2Block(byte[] s, int o, byte[] px)
        {
            ColorBlock(s, o + 8, px, true);
            for (int i = 0; i < 16; i++) { int a = (s[o + i / 2] >> (4 * (i & 1))) & 15; px[i * 4 + 3] = (byte)(a * 17); }
        }

        static void Bc3Block(byte[] s, int o, byte[] px) { ColorBlock(s, o + 8, px, true); AlphaBlock(s, o, px, 3); }

        static void Bc4Block(byte[] s, int o, byte[] px)
        {
            AlphaBlock(s, o, px, 0);
            for (int i = 0; i < 16; i++) { px[i * 4 + 1] = px[i * 4 + 2] = px[i * 4]; px[i * 4 + 3] = 255; }
        }

        static void Bc5Block(byte[] s, int o, byte[] px)
        {
            AlphaBlock(s, o, px, 0); AlphaBlock(s, o + 8, px, 1);
            for (int i = 0; i < 16; i++)
            {   // two-channel (usually a normal map): rebuild blue so it looks like one
                double x = px[i * 4] / 127.5 - 1, y = px[i * 4 + 1] / 127.5 - 1, z = Math.Sqrt(Math.Max(0, 1 - x * x - y * y));
                px[i * 4 + 2] = (byte)Math.Round((z + 1) * 127.5); px[i * 4 + 3] = 255;
            }
        }

        static byte[] FlipRows(byte[] top, int w, int h)
        {
            var o = new byte[top.Length]; int row = w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(top, y * row, o, (h - 1 - y) * row, row);
            return o;
        }

        // ------------------------------------------------------------------ TGA
        static bool Tga(byte[] d, out int w, out int h, out byte[] rgba, out string error)
        {
            rgba = null; error = null;
            int idLen = d[0], cmapType = d[1], type = d[2];
            int cmapLen = U16(d, 5), cmapBits = d[7];
            w = U16(d, 12); h = U16(d, 14); int bpp = d[16], desc = d[17];
            if (cmapType != 0 || (type != 2 && type != 3 && type != 10 && type != 11))
            { error = "this TGA type (colour-mapped or unusual) isn't supported; save it as PNG"; return false; }
            bool gray = type == 3 || type == 11, rle = type >= 9;
            int bytes = bpp / 8;
            if (gray ? bytes != 1 && bytes != 2 : bytes != 3 && bytes != 4) { error = bpp + "-bit TGA isn't supported; save it as PNG"; return false; }
            int p = 18 + idLen + (cmapType != 0 ? cmapLen * ((cmapBits + 7) / 8) : 0);
            var buf = new byte[w * h * 4]; int n = 0, total = w * h;
            var pix = new byte[4];
            while (n < total)
            {
                int count = 1; bool repeat = false;
                if (rle) { int hdr = d[p++]; count = (hdr & 0x7F) + 1; repeat = (hdr & 0x80) != 0; }
                for (int i = 0; i < count && n < total; i++)
                {
                    if (!repeat || i == 0)
                    {
                        if (gray) { pix[0] = pix[1] = pix[2] = d[p]; pix[3] = bytes == 2 ? d[p + 1] : (byte)255; }
                        else { pix[2] = d[p]; pix[1] = d[p + 1]; pix[0] = d[p + 2]; pix[3] = bytes == 4 ? d[p + 3] : (byte)255; }
                        p += bytes;
                    }
                    if (!rle) { count = total; }
                    Buffer.BlockCopy(pix, 0, buf, n * 4, 4); n++;
                }
            }
            bool topDown = (desc & 0x20) != 0, rightToLeft = (desc & 0x10) != 0;
            if (rightToLeft)
                for (int y = 0; y < h; y++) for (int x = 0; x < w / 2; x++)
                        for (int c = 0; c < 4; c++) { int a = (y * w + x) * 4 + c, b = (y * w + (w - 1 - x)) * 4 + c; byte t = buf[a]; buf[a] = buf[b]; buf[b] = t; }
            rgba = topDown ? FlipRows(buf, w, h) : buf;     // TGA's default origin is already bottom-left
            return true;
        }
    }

    // ------------------------------------------------------------------ BC7 (per the D3D11 / Khronos BPTC spec)
    static class Bc7
    {
        static readonly int[][] W = { null, null, new[] { 0, 21, 43, 64 }, new[] { 0, 9, 18, 27, 37, 46, 55, 64 },
                                      new[] { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 } };

        // partition tables (2 and 3 subsets), 64 shapes x 16 texels
        static readonly byte[] Part2 = BuildP2();
        static readonly byte[] Part3 = BuildP3();
        static readonly byte[] Anchor2 = { 15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15, 2, 8, 2, 2, 8, 8,15, 2, 8, 2, 2, 8, 8, 2, 2,
                                           15,15, 6, 8, 2, 8,15,15, 2, 8, 2, 2, 2,15,15, 6, 6, 2, 6, 8,15,15, 2, 2,15,15,15,15,15, 2, 2,15 };
        static readonly byte[] Anchor3a = { 3, 3,15,15, 8, 3,15,15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8,15, 3, 3, 6,10, 5, 8, 8, 6, 8, 5,15,15,
                                            8,15, 3, 5, 6,10, 8,15,15, 3,15, 5,15,15,15,15, 3,15, 5, 5, 5, 8, 5,10, 5,10, 8,13,15,12, 3, 3 };
        static readonly byte[] Anchor3b = { 15, 8, 8, 3,15,15, 3, 8,15,15,15,15,15,15,15, 8,15, 8,15, 3,15, 8,15, 8, 3,15, 6,10,15,15,10, 8,
                                            15, 3,15,10,10, 8, 9,10, 6,15, 8,15, 3, 6, 6, 8,15, 3,15,15,15,15,15,15,15,15,15,15, 3,15,15, 8 };

        static byte[] BuildP2()
        {
            // canonical BC7 2-subset partition table
            uint[] t = { 0xCCCC,0x8888,0xEEEE,0xECC8,0xC880,0xFEEC,0xFEC8,0xEC80,0xC800,0xFFEC,0xFE80,0xE800,0xFFE8,0xFF00,0xFFF0,0xF000,
                         0xF710,0x008E,0x7100,0x08CE,0x008C,0x7310,0x3100,0x8CCE,0x088C,0x3110,0x6666,0x366C,0x17E8,0x0FF0,0x718E,0x399C,
                         0xAAAA,0xF0F0,0x5A5A,0x33CC,0x3C3C,0x55AA,0x9696,0xA55A,0x73CE,0x13C8,0x324C,0x3BDC,0x6996,0xC33C,0x9966,0x0660,
                         0x0272,0x04E4,0x4E40,0x2720,0xC936,0x936C,0x39C6,0x639C,0x9336,0x9CC6,0x817E,0xE718,0xCCF0,0x0FCC,0x7744,0xEE22 };
            var p = new byte[64 * 16];
            for (int s = 0; s < 64; s++) for (int i = 0; i < 16; i++) p[s * 16 + i] = (byte)((t[s] >> i) & 1);
            return p;
        }

        static byte[] BuildP3()
        {
            string[] t = {
                "0011001102212222","0001001122112221","0000200122112211","0222002200110111","0000000011221122","0011001100220022","0022002211111111","0011001122112211",
                "0000000011112222","0000111111112222","0000111122222222","0012001200120012","0112011201120112","0122012201220122","0011011211221222","0011200122002220",
                "0001001101121122","0111001120012200","0000112211221122","0022002200221111","0111011102220222","0001000122212221","0000001101220122","0000110022102210",
                "0122012200110000","0012001211222222","0110122112210110","0000011012211221","0022110211020022","0110011020022222","0011012201220011","0000200022112221",
                "0000000211221222","0222002200120011","0011001200220222","0120012001200120","0000111122220000","0120120120120120","0120201212010120","0011220011220011",
                "0011112222000011","0101010122222222","0000000021212121","0022112200221122","0022001100220011","0220122102201221","0101222222220101","0000212121212121",
                "0101010101012222","0222011102220111","0002111200021112","0000211221122112","0222011101110222","0002111211120002","0110011001102222","0000000021122112",
                "0110011022222222","0022001100110022","0022112211220022","0000000000002112","0002000100020001","0222122202221222","0101222222222222","0111201122012220" };
            var p = new byte[64 * 16];
            for (int s = 0; s < 64; s++) for (int i = 0; i < 16; i++) p[s * 16 + i] = (byte)(t[s][i] - '0');
            return p;
        }

        struct Bits
        {
            public byte[] d; public int o, pos;
            public int Get(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++, pos++) v |= ((d[o + (pos >> 3)] >> (pos & 7)) & 1) << i;
                return v;
            }
        }

        static readonly int[] NS = { 3, 2, 3, 2, 1, 1, 1, 2 }, PB = { 4, 6, 6, 6, 0, 0, 0, 6 }, RB = { 0, 0, 0, 0, 2, 2, 0, 0 },
                              ISB = { 0, 0, 0, 0, 1, 0, 0, 0 }, CB = { 4, 6, 5, 7, 5, 7, 7, 5 }, AB = { 0, 0, 0, 0, 6, 8, 7, 5 },
                              EPB = { 1, 0, 0, 1, 0, 0, 1, 1 }, SPB = { 0, 1, 0, 0, 0, 0, 0, 0 }, IB = { 3, 3, 2, 2, 2, 2, 4, 2 }, IB2 = { 0, 0, 0, 0, 3, 2, 0, 0 };

        static int Interp(int e0, int e1, int idx, int bits) { int w = W[bits][idx]; return (e0 * (64 - w) + e1 * w + 32) >> 6; }

        public static void Decode(byte[] s, int o, byte[] px)
        {
            int mode = 0; while (mode < 8 && ((s[o] >> mode) & 1) == 0) mode++;
            if (mode == 8) { for (int i = 0; i < 64; i++) px[i] = 0; return; }
            var b = new Bits { d = s, o = o, pos = mode + 1 };
            int ns = NS[mode];
            int part = b.Get(PB[mode]), rot = b.Get(RB[mode]), isel = b.Get(ISB[mode]);
            int cb = CB[mode], ab = AB[mode];
            var ep = new int[6, 4];   // [endpoint][rgba]
            for (int c = 0; c < 3; c++) for (int e = 0; e < ns * 2; e++) ep[e, c] = b.Get(cb);
            for (int e = 0; e < ns * 2; e++) ep[e, 3] = ab > 0 ? b.Get(ab) : 255;
            int pbits = 0;
            var pb = new int[6];
            if (EPB[mode] == 1) { for (int e = 0; e < ns * 2; e++) pb[e] = b.Get(1); pbits = 1; }
            else if (SPB[mode] == 1) { for (int sI = 0; sI < ns; sI++) { int v = b.Get(1); pb[sI * 2] = pb[sI * 2 + 1] = v; } pbits = 1; }
            for (int e = 0; e < ns * 2; e++)
                for (int c = 0; c < 4; c++)
                {
                    int bitsC = c < 3 ? cb : ab; if (c == 3 && ab == 0) continue;
                    int v = ep[e, c];
                    if (pbits == 1) { v = (v << 1) | pb[e]; bitsC++; }
                    v <<= 8 - bitsC; v |= v >> bitsC;
                    ep[e, c] = v;
                }
            byte[] ptab = ns == 2 ? Part2 : ns == 3 ? Part3 : null;
            int ib = IB[mode], ib2 = IB2[mode];
            var idx = new int[16]; var idx2 = new int[16];
            for (int i = 0; i < 16; i++)
            {
                int sub = ptab != null ? ptab[part * 16 + i] : 0;
                bool anchor = i == 0 || (ns == 2 && sub == 1 && i == Anchor2[part]) ||
                              (ns == 3 && ((sub == 1 && i == Anchor3a[part]) || (sub == 2 && i == Anchor3b[part])));
                idx[i] = b.Get(anchor ? ib - 1 : ib);
            }
            if (ib2 > 0) for (int i = 0; i < 16; i++) idx2[i] = b.Get(i == 0 ? ib2 - 1 : ib2);
            for (int i = 0; i < 16; i++)
            {
                int sub = ptab != null ? ptab[part * 16 + i] : 0;
                int e0 = sub * 2, e1 = sub * 2 + 1;
                int r, g, bl, a;
                if (ib2 > 0)
                {
                    int ci = isel == 0 ? idx[i] : idx2[i], ai = isel == 0 ? idx2[i] : idx[i];
                    int cbits = isel == 0 ? ib : ib2, abits = isel == 0 ? ib2 : ib;
                    r = Interp(ep[e0, 0], ep[e1, 0], ci, cbits); g = Interp(ep[e0, 1], ep[e1, 1], ci, cbits);
                    bl = Interp(ep[e0, 2], ep[e1, 2], ci, cbits); a = Interp(ep[e0, 3], ep[e1, 3], ai, abits);
                }
                else
                {
                    r = Interp(ep[e0, 0], ep[e1, 0], idx[i], ib); g = Interp(ep[e0, 1], ep[e1, 1], idx[i], ib);
                    bl = Interp(ep[e0, 2], ep[e1, 2], idx[i], ib);
                    a = ab > 0 ? Interp(ep[e0, 3], ep[e1, 3], idx[i], ib) : 255;
                }
                int t;
                switch (rot) { case 1: t = a; a = r; r = t; break; case 2: t = a; a = g; g = t; break; case 3: t = a; a = bl; bl = t; break; }
                px[i * 4] = (byte)r; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)bl; px[i * 4 + 3] = (byte)a;
            }
        }
    }
}
