// TTVR Workshop Plus - engine-independent core (OBJ parsing, vertex sharing, texture atlas).
// Compiles for Unity 2017.3's Mono (.NET 3.5 profile) and for modern .NET (tests).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TTVRPlus
{
    public struct Corner { public int v, t, n; }

    public class ObjModel
    {
        public List<float> V = new List<float>();     // xyz
        public List<float> VT = new List<float>();    // uv
        public List<float> VN = new List<float>();    // xyz
        public List<Corner> Corners = new List<Corner>();   // 3 per triangle
        public List<int> TriMaterial = new List<int>();     // index into MaterialNames, -1 = none
        public List<string> MaterialNames = new List<string>();
        public int VertexCount { get { return V.Count / 3; } }
        public int TriangleCount { get { return TriMaterial.Count; } }
    }

    public static class ObjParser
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly char[] Ws = { ' ', '\t' };

        static float F(string s) { return float.Parse(s, NumberStyles.Float, Inv); }

        public static ObjModel Parse(string path)
        {
            var m = new ObjModel();
            var matIndex = new Dictionary<string, int>();
            int cur = -1;
            var face = new List<Corner>(8);
            int lineNo = 0;
            using (var r = new StreamReader(path))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    lineNo++;
                    int n = line.Length, p = 0;
                    while (p < n && (line[p] == ' ' || line[p] == '\t')) p++;
                    if (n - p < 2) continue;
                    char c0 = line[p];
                    if (c0 != 'v' && c0 != 'f' && c0 != 'u') continue;
                    int ks = p; while (p < n && line[p] != ' ' && line[p] != '\t') p++;
                    int kl = p - ks;
                    try
                    {
                        if (kl == 1 && c0 == 'v') { m.V.Add(Num(line, ref p)); m.V.Add(Num(line, ref p)); m.V.Add(Num(line, ref p)); }
                        else if (kl == 2 && c0 == 'v' && line[ks + 1] == 't') { m.VT.Add(Num(line, ref p)); m.VT.Add(HasMore(line, p) ? Num(line, ref p) : 0f); }
                        else if (kl == 2 && c0 == 'v' && line[ks + 1] == 'n') { m.VN.Add(Num(line, ref p)); m.VN.Add(Num(line, ref p)); m.VN.Add(Num(line, ref p)); }
                        else if (kl == 6 && string.CompareOrdinal(line, ks, "usemtl", 0, 6) == 0)
                        {
                            string name = line.Substring(p).Trim();
                            if (!matIndex.TryGetValue(name, out cur))
                            {
                                cur = m.MaterialNames.Count; matIndex[name] = cur; m.MaterialNames.Add(name);
                            }
                        }
                        else if (kl == 1 && c0 == 'f')
                        {
                            face.Clear();
                            int vc = m.V.Count / 3, tc = m.VT.Count / 2, nc = m.VN.Count / 3;
                            while (HasMore(line, p))
                            {
                                while (line[p] == ' ' || line[p] == '\t') p++;
                                var k = new Corner();
                                k.v = Idx(Int(line, ref p), vc); k.t = -1; k.n = -1;
                                if (p < n && line[p] == '/')
                                {
                                    p++;
                                    if (p < n && line[p] != '/' && line[p] != ' ' && line[p] != '\t') k.t = Idx(Int(line, ref p), tc);
                                    if (p < n && line[p] == '/')
                                    {
                                        p++;
                                        if (p < n && line[p] != ' ' && line[p] != '\t') k.n = Idx(Int(line, ref p), nc);
                                    }
                                }
                                while (p < n && line[p] != ' ' && line[p] != '\t') p++;     // anything else in this corner
                                if (k.v < 0 || k.v >= vc) throw new Exception("vertex index out of range");
                                if (k.t >= tc) k.t = -1;
                                if (k.n >= nc) k.n = -1;
                                face.Add(k);
                            }
                            for (int i = 1; i + 1 < face.Count; i++)   // same fan as Workshop.exe
                            {
                                m.Corners.Add(face[0]); m.Corners.Add(face[i]); m.Corners.Add(face[i + 1]);
                                m.TriMaterial.Add(cur);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        throw new Exception("OBJ line " + lineNo + ": " + e.Message);
                    }
                }
            }
            if (m.TriangleCount == 0) throw new Exception("no faces found in the OBJ file.");
            return m;
        }

        static bool HasMore(string s, int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t')) p++;
            return p < s.Length;
        }

        static int Int(string s, ref int p)
        {
            int n = s.Length; bool neg = false;
            if (p < n && (s[p] == '-' || s[p] == '+')) { neg = s[p] == '-'; p++; }
            int start = p; long v = 0;
            while (p < n && s[p] >= '0' && s[p] <= '9') { v = v * 10 + (s[p] - '0'); p++; }
            if (p == start) throw new FormatException("expected a number");
            return (int)(neg ? -v : v);
        }

        static int Idx(int i, int count) { return i > 0 ? i - 1 : count + i; }

        static readonly double[] Pow10 = { 1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22 };

        // next number on the line; plain decimals are parsed directly (float.Parse is very slow in the tool's old Mono runtime),
        // anything unusual (inf, nan, very long numbers) goes through float.Parse
        static float Num(string s, ref int p)
        {
            int n = s.Length;
            while (p < n && (s[p] == ' ' || s[p] == '\t')) p++;
            int start = p;
            bool neg = false;
            if (p < n && (s[p] == '-' || s[p] == '+')) { neg = s[p] == '-'; p++; }
            long mant = 0; int digits = 0, exp10 = 0; bool any = false;
            while (p < n && s[p] >= '0' && s[p] <= '9')
            {
                any = true;
                if (digits < 18) { mant = mant * 10 + (s[p] - '0'); if (mant != 0) digits++; } else exp10++;
                p++;
            }
            if (p < n && s[p] == '.')
            {
                p++;
                while (p < n && s[p] >= '0' && s[p] <= '9')
                {
                    any = true;
                    if (digits < 18) { mant = mant * 10 + (s[p] - '0'); if (mant != 0) digits++; exp10--; }
                    p++;
                }
            }
            if (any && p < n && (s[p] == 'e' || s[p] == 'E'))
            {
                int q = p + 1; bool eneg = false;
                if (q < n && (s[q] == '-' || s[q] == '+')) { eneg = s[q] == '-'; q++; }
                int e = 0, es = q;
                while (q < n && s[q] >= '0' && s[q] <= '9') { if (e < 10000) e = e * 10 + (s[q] - '0'); q++; }
                if (q > es) { exp10 += eneg ? -e : e; p = q; }
            }
            bool endOk = p >= n || s[p] == ' ' || s[p] == '\t' || s[p] == '\r';
            if (!any || !endOk || digits >= 18 || exp10 < -22 || exp10 > 22)
            {   // fallback: the whole token through float.Parse
                p = start; while (p < n && s[p] != ' ' && s[p] != '\t') p++;
                if (p == start) throw new FormatException("expected a number");
                return F(s.Substring(start, p - start));
            }
            double v = exp10 >= 0 ? mant * Pow10[exp10] : mant / Pow10[-exp10];
            return (float)(neg ? -v : v);
        }

    }

    // ------------------------------------------------------------------ atlas
    public class MaterialSource
    {
        public string Name;
        public byte R = 204, G = 204, B = 204;   // diffuse colour
        public int TextureSlot = -1;             // index into texture list, -1 = colour only
    }

    public class TextureSource
    {
        public string Name;                       // for notes
        public int Width, Height;
        public byte[] Rgba;                       // bottom-up rows, like Unity GetPixels32
    }

    public class UvRect
    {
        public float U0, V0, U1, V1;
        public bool Tile;                         // keep original UVs (single-texture items)
        public float SU0 = 0f, SV0 = 0f, SU1 = 1f, SV1 = 1f;   // part of the texture's 0..1 UV space placed in this rect (UV cropping)
        public bool Swatch { get { return U0 == U1 && V0 == V1; } }
    }

    public class AtlasResult
    {
        public int Size;
        public byte[] Rgba;                       // null when the single texture is used unchanged
        public UvRect[] Rects;                    // per material
        public string Layout;                     // e.g. "a.png 300px, b.png 120x90px" (By area) for the note
    }

    public enum AtlasMode { Equal = 0, ByArea = 1 }

    public class AtlasOptions
    {
        public AtlasMode Mode = AtlasMode.Equal;
        public bool Crop;
        public double[] Weights;                  // per texture slot: 3D surface area using it
        public float[][] Windows;                 // per texture slot: used UV range {u0, v0, u1, v1} (0..1)
    }

    /// surface area and used UV range per texture slot, measured the same way the mesh builder maps UVs
    public static class AtlasStats
    {
        public static AtlasOptions Measure(ObjModel m, IList<MaterialSource> mats, int slots)
        {
            var o = new AtlasOptions { Weights = new double[slots], Windows = new float[slots][] };
            var lo = new float[slots * 2]; var hi = new float[slots * 2];
            for (int s = 0; s < slots; s++) { lo[s * 2] = lo[s * 2 + 1] = float.MaxValue; hi[s * 2] = hi[s * 2 + 1] = float.MinValue; }
            int nt = m.TriMaterial.Count;
            bool hasUv = m.VT.Count > 0;
            for (int t = 0; t < nt; t++)
            {
                int mi = m.TriMaterial[t];
                if (mi < 0 || mi >= mats.Count) continue;
                int s = mats[mi].TextureSlot;
                if (s < 0 || s >= slots) continue;
                var a = m.Corners[t * 3]; var b = m.Corners[t * 3 + 1]; var cc = m.Corners[t * 3 + 2];
                double ax = m.V[b.v * 3] - m.V[a.v * 3], ay = m.V[b.v * 3 + 1] - m.V[a.v * 3 + 1], az = m.V[b.v * 3 + 2] - m.V[a.v * 3 + 2];
                double bx = m.V[cc.v * 3] - m.V[a.v * 3], by = m.V[cc.v * 3 + 1] - m.V[a.v * 3 + 1], bz = m.V[cc.v * 3 + 2] - m.V[a.v * 3 + 2];
                double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
                o.Weights[s] += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (!hasUv) continue;
                float mu = float.MaxValue, mv = float.MaxValue;
                for (int c = 0; c < 3; c++) { var k = m.Corners[t * 3 + c]; if (k.t < 0) continue; mu = Math.Min(mu, m.VT[k.t * 2]); mv = Math.Min(mv, m.VT[k.t * 2 + 1]); }
                if (mu == float.MaxValue) continue;
                int ou = (int)Math.Floor(mu + 1e-5f), ov = (int)Math.Floor(mv + 1e-5f);
                for (int c = 0; c < 3; c++)
                {
                    var k = m.Corners[t * 3 + c]; if (k.t < 0) continue;
                    float u = Math.Min(1f, Math.Max(0f, m.VT[k.t * 2] - ou)), v = Math.Min(1f, Math.Max(0f, m.VT[k.t * 2 + 1] - ov));
                    lo[s * 2] = Math.Min(lo[s * 2], u); lo[s * 2 + 1] = Math.Min(lo[s * 2 + 1], v);
                    hi[s * 2] = Math.Max(hi[s * 2], u); hi[s * 2 + 1] = Math.Max(hi[s * 2 + 1], v);
                }
            }
            for (int s = 0; s < slots; s++)
                o.Windows[s] = lo[s * 2] == float.MaxValue ? new[] { 0f, 0f, 1f, 1f } : new[] { lo[s * 2], lo[s * 2 + 1], hi[s * 2], hi[s * 2 + 1] };
            return o;
        }
    }

    public static class Atlas
    {

        public static AtlasResult Build(IList<MaterialSource> materials, IList<TextureSource> textures, int maxSize, int pad)
        {
            return Build(materials, textures, maxSize, pad, null);
        }

        // materials: per material; textures: unique textures (already loaded, any size)
        public static AtlasResult Build(IList<MaterialSource> materials, IList<TextureSource> textures, int maxSize, int pad, AtlasOptions opt)
        {
            opt = opt ?? new AtlasOptions();
            var res = new AtlasResult { Rects = new UvRect[materials.Count] };
            var colorMats = new List<int>();
            for (int i = 0; i < materials.Count; i++) if (materials[i].TextureSlot < 0) colorMats.Add(i);

            // 1) one texture, no colour-only materials: use it as-is, keep tiling UVs (cropping of a single texture is done by the caller)
            if (textures.Count == 1 && colorMats.Count == 0)
            {
                for (int i = 0; i < materials.Count; i++) res.Rects[i] = new UvRect { U0 = 0, V0 = 0, U1 = 1, V1 = 1, Tile = true };
                res.Size = textures[0].Width; res.Rgba = null;
                return res;
            }
            int swPerRow = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, colorMats.Count)));
            // 2) colours only: swatch grid
            if (textures.Count == 0)
            {
                int s = 128;
                while (s / swPerRow < 8 && s < maxSize) s *= 2;
                res.Size = s; res.Rgba = Fill(s, 255, 255, 255);
                PlaceSwatches(res, materials, colorMats, 0, 0, s / swPerRow, swPerRow);
                return res;
            }

            // optional: keep only the part of each texture its UVs use
            var src = new TextureSource[textures.Count];
            var win = new float[textures.Count][];
            for (int t = 0; t < textures.Count; t++)
            {
                src[t] = textures[t]; win[t] = new[] { 0f, 0f, 1f, 1f };
                if (opt.Crop && opt.Windows != null && t < opt.Windows.Length && opt.Windows[t] != null)
                    src[t] = CropToWindow(textures[t], opt.Windows[t], out win[t]);
            }

            int S = maxSize;
            res.Size = S; res.Rgba = Fill(S, 255, 255, 255);
            var slotRect = new UvRect[textures.Count];
            int colX = -1, colY = -1, colCell = 0;
            if (opt.Mode == AtlasMode.ByArea)
            {
                int[] x, y, w, h; int colSize = colorMats.Count > 0 ? Math.Min(64, Math.Max(16, swPerRow * 4)) : 0;
                if (!PackByArea(src, opt.Weights, colSize, S, pad, out x, out y, out w, out h))
                    throw new Exception("too many textures (" + textures.Count + ") to fit in one " + S + "px texture.");
                var parts = new List<string>();
                for (int t = 0; t < textures.Count; t++)
                {
                    var scaled = Resize(src[t], w[t], h[t]);
                    Blit(res.Rgba, S, scaled, w[t], h[t], x[t] + pad, y[t] + pad, pad);
                    slotRect[t] = new UvRect { U0 = (x[t] + pad) / (float)S, V0 = (y[t] + pad) / (float)S,
                                               U1 = (x[t] + pad + w[t]) / (float)S, V1 = (y[t] + pad + h[t]) / (float)S };
                    parts.Add((textures[t].Name ?? "texture " + (t + 1)) + " " + (w[t] == h[t] ? w[t] + "px" : w[t] + "x" + h[t] + "px"));
                }
                res.Layout = string.Join(", ", parts.ToArray());
                if (colSize > 0) { int c = textures.Count; colX = x[c] + pad; colY = y[c] + pad; colCell = colSize / swPerRow; }
            }
            else
            {   // 3) Equal: grid of equal cells, colours share one cell
                int slots = textures.Count + (colorMats.Count > 0 ? 1 : 0);
                int k = (int)Math.Ceiling(Math.Sqrt(slots));
                int cell = S / k, inner = cell - 2 * pad;
                if (inner < 4) throw new Exception("too many textures (" + textures.Count + ") to fit in one " + S + "px texture.");
                for (int t = 0; t < textures.Count; t++)
                {
                    int cx = (t % k) * cell, cy = (t / k) * cell;            // cy measured from the bottom
                    var scaled = Resize(src[t], inner, inner);
                    Blit(res.Rgba, S, scaled, inner, inner, cx + pad, cy + pad, pad);
                    slotRect[t] = new UvRect { U0 = (cx + pad) / (float)S, V0 = (cy + pad) / (float)S,
                                               U1 = (cx + cell - pad) / (float)S, V1 = (cy + cell - pad) / (float)S };
                }
                if (colorMats.Count > 0) { int t = textures.Count; colX = (t % k) * cell; colY = (t / k) * cell; colCell = cell / swPerRow; }
            }
            for (int t = 0; t < textures.Count; t++)
            {
                slotRect[t].SU0 = win[t][0]; slotRect[t].SV0 = win[t][1]; slotRect[t].SU1 = win[t][2]; slotRect[t].SV1 = win[t][3];
            }
            for (int i = 0; i < materials.Count; i++)
                if (materials[i].TextureSlot >= 0) res.Rects[i] = slotRect[materials[i].TextureSlot];
            if (colorMats.Count > 0) PlaceSwatches(res, materials, colorMats, colX, colY, Math.Max(1, colCell), swPerRow);
            return res;
        }

        /// pixel box of the used UV window plus a small margin; false when that's nearly the whole texture (not worth cropping)
        public static bool CropPixels(int W, int H, float[] w, out int x0, out int y0, out int x1, out int y1)
        {
            int mx = Math.Max(2, W / 128), my = Math.Max(2, H / 128);
            x0 = Math.Max(0, (int)Math.Floor(w[0] * W) - mx); x1 = Math.Min(W, (int)Math.Ceiling(w[2] * W) + mx);
            y0 = Math.Max(0, (int)Math.Floor(w[1] * H) - my); y1 = Math.Min(H, (int)Math.Ceiling(w[3] * H) + my);
            if (x1 - x0 < 1 || y1 - y0 < 1) return false;
            return (x1 - x0) * (long)(y1 - y0) <= 0.92 * W * (long)H;
        }

        /// the used part of a texture (plus a small margin), and the UV window it covers; the whole texture when that's nearly all of it
        public static TextureSource CropToWindow(TextureSource t, float[] w, out float[] used)
        {
            used = new[] { 0f, 0f, 1f, 1f };
            if (w == null || t.Rgba == null) return t;
            int W = t.Width, H = t.Height, x0, y0, x1, y1;
            if (!CropPixels(W, H, w, out x0, out y0, out x1, out y1)) return t;
            int cw = x1 - x0, ch = y1 - y0;
            var px = new byte[cw * ch * 4];
            for (int y = 0; y < ch; y++) Buffer.BlockCopy(t.Rgba, ((y + y0) * W + x0) * 4, px, y * cw * 4, cw * 4);
            used = new[] { x0 / (float)W, y0 / (float)H, x1 / (float)W, y1 / (float)H };
            return new TextureSource { Name = t.Name, Width = cw, Height = ch, Rgba = px };
        }

        // By area: each texture gets space in proportion to the surface it covers, keeps its shape,
        // never more pixels than it has and never less than 16px a side; the largest layout that fits wins.
        static bool PackByArea(TextureSource[] src, double[] weights, int colSize, int S, int pad, out int[] x, out int[] y, out int[] w, out int[] h)
        {
            int n = src.Length, total = n + (colSize > 0 ? 1 : 0);
            x = new int[total]; y = new int[total]; w = new int[total]; h = new int[total];
            var share = new double[n]; double sum = 0;
            for (int i = 0; i < n; i++) { share[i] = weights != null && i < weights.Length ? Math.Max(0, weights[i]) : 0; sum += share[i]; }
            for (int i = 0; i < n; i++) share[i] = sum > 0 ? Math.Max(share[i] / sum, 0.002) : 1.0 / n;
            if (colSize > 0) { w[n] = h[n] = colSize; }

            double lo = 0.01, hi = 3.0; bool any = false;
            int[] bx = null, by = null, bw = null, bh = null;
            for (int iter = 0; iter < 28; iter++)
            {
                double k = iter == 0 ? lo : (lo + hi) * 0.5;
                for (int i = 0; i < n; i++)
                {
                    double aspect = Math.Min(4.0, Math.Max(0.25, src[i].Width / (double)Math.Max(1, src[i].Height)));
                    double area = k * share[i] * S * S;
                    double hh = Math.Sqrt(area / aspect), ww = hh * aspect;
                    double fit = Math.Min(1.0, Math.Min(src[i].Width / ww, src[i].Height / hh));   // no upscaling past the source
                    ww *= fit; hh *= fit;
                    w[i] = Math.Max(16, Math.Min(S - 2 * pad, (int)Math.Floor(ww)));
                    h[i] = Math.Max(16, Math.Min(S - 2 * pad, (int)Math.Floor(hh)));
                }
                bool ok = Shelf(w, h, S, pad, x, y);
                if (iter == 0) { if (!ok) return false; }
                else if (ok) lo = k; else hi = k;
                if (ok) { any = true; bx = (int[])x.Clone(); by = (int[])y.Clone(); bw = (int[])w.Clone(); bh = (int[])h.Clone(); }
            }
            if (!any) return false;
            x = bx; y = by; w = bw; h = bh;

            // then grow textures one at a time into the space left over (least-served first), keeping each one's shape
            var tx = new int[total]; var ty = new int[total];
            int[] gw = w, gh = h;
            for (int round = 0; round < 60; round++)
            {
                bool grew = false;
                var order = new List<int>(); for (int i = 0; i < n; i++) order.Add(i);
                order.Sort((p, q) => ((gw[p] * (double)gh[p]) / share[p]).CompareTo((gw[q] * (double)gh[q]) / share[q]));
                foreach (int i in order)
                {
                    int ow = w[i], oh = h[i];
                    double f = 1.04;
                    int nw = Math.Max(ow + 1, (int)(ow * f)), nh = Math.Max(oh + 1, (int)(oh * f));
                    if (nw > src[i].Width || nh > src[i].Height || nw > S - 2 * pad || nh > S - 2 * pad) continue;
                    w[i] = nw; h[i] = nh;
                    if (Shelf(w, h, S, pad, tx, ty)) { Array.Copy(tx, x, total); Array.Copy(ty, y, total); grew = true; }
                    else { w[i] = ow; h[i] = oh; }
                }
                if (!grew) break;
            }
            return true;
        }

        // skyline (bottom-left) packing, biggest first; positions are the padded boxes' corners, y from the bottom
        static bool Shelf(int[] w, int[] h, int S, int pad, int[] x, int[] y)
        {
            int n = w.Length;
            var order = new List<int>(); for (int i = 0; i < n; i++) order.Add(i);
            order.Sort((a, b) => { int c = Math.Max(h[b], w[b]).CompareTo(Math.Max(h[a], w[a])); return c != 0 ? c : (h[b] * w[b]).CompareTo(h[a] * w[a]); });
            var sx = new List<int> { 0 }; var sy = new List<int> { 0 }; var sw = new List<int> { S };   // skyline segments
            foreach (int i in order)
            {
                int pw = w[i] + 2 * pad, ph = h[i] + 2 * pad;
                int bestY = int.MaxValue, bestX = 0, bestSeg = -1;
                for (int s = 0; s < sx.Count; s++)
                {
                    int left = sx[s]; if (left + pw > S) break;
                    int top = 0, covered = 0, k = s;
                    while (covered < pw && k < sx.Count) { top = Math.Max(top, sy[k]); covered = sx[k] + sw[k] - left; k++; }
                    if (covered < pw || top + ph > S) continue;
                    if (top < bestY || (top == bestY && left < bestX)) { bestY = top; bestX = left; bestSeg = s; }
                }
                if (bestSeg < 0) return false;
                x[i] = bestX; y[i] = bestY;
                // raise the skyline under the new box
                int nx = bestX, ne = bestX + pw, ny = bestY + ph;
                var nsx = new List<int>(); var nsy = new List<int>(); var nsw = new List<int>();
                for (int s = 0; s < sx.Count; s++)
                {
                    int s0 = sx[s], s1 = sx[s] + sw[s];
                    if (s1 <= nx || s0 >= ne) { nsx.Add(s0); nsy.Add(sy[s]); nsw.Add(sw[s]); continue; }
                    if (s0 < nx) { nsx.Add(s0); nsy.Add(sy[s]); nsw.Add(nx - s0); }
                    if (s0 <= nx || nsx.Count == 0 || nsx[nsx.Count - 1] + nsw[nsw.Count - 1] <= nx) { if (!(nsx.Count > 0 && nsx[nsx.Count - 1] == nx && nsy[nsy.Count - 1] == ny)) { nsx.Add(nx); nsy.Add(ny); nsw.Add(pw); } }
                    if (s1 > ne) { nsx.Add(ne); nsy.Add(sy[s]); nsw.Add(s1 - ne); }
                }
                // merge neighbours at the same height
                sx = new List<int>(); sy = new List<int>(); sw = new List<int>();
                for (int s = 0; s < nsx.Count; s++)
                {
                    if (sx.Count > 0 && sy[sy.Count - 1] == nsy[s] && sx[sx.Count - 1] + sw[sw.Count - 1] == nsx[s]) sw[sw.Count - 1] += nsw[s];
                    else { sx.Add(nsx[s]); sy.Add(nsy[s]); sw.Add(nsw[s]); }
                }
            }
            return true;
        }


        static void PlaceSwatches(AtlasResult res, IList<MaterialSource> mats, List<int> idx, int ox, int oy, int cell, int perRow)
        {
            int S = res.Size;
            for (int j = 0; j < idx.Count; j++)
            {
                var m = mats[idx[j]];
                int x0 = ox + (j % perRow) * cell, y0 = oy + (j / perRow) * cell;
                for (int y = y0; y < y0 + cell; y++)
                    for (int x = x0; x < x0 + cell; x++)
                    {
                        int o = (y * S + x) * 4; res.Rgba[o] = m.R; res.Rgba[o + 1] = m.G; res.Rgba[o + 2] = m.B; res.Rgba[o + 3] = 255;
                    }
                float u = (x0 + cell * 0.5f) / S, v = (y0 + cell * 0.5f) / S;
                res.Rects[idx[j]] = new UvRect { U0 = u, V0 = v, U1 = u, V1 = v };
            }
        }

        static byte[] Fill(int s, byte r, byte g, byte b)
        {
            var a = new byte[s * s * 4];
            for (int i = 0; i < a.Length; i += 4) { a[i] = r; a[i + 1] = g; a[i + 2] = b; a[i + 3] = 255; }
            return a;
        }

        // copy src (w x h) into dst at (x0,y0) and extend its edges by 'pad' pixels to stop mipmap bleeding
        static void Blit(byte[] dst, int S, byte[] src, int w, int h, int x0, int y0, int pad)
        {
            for (int y = -pad; y < h + pad; y++)
            {
                int sy = Math.Min(h - 1, Math.Max(0, y));
                for (int x = -pad; x < w + pad; x++)
                {
                    int sx = Math.Min(w - 1, Math.Max(0, x));
                    int si = (sy * w + sx) * 4, di = ((y0 + y) * S + (x0 + x)) * 4;
                    dst[di] = src[si]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si + 2]; dst[di + 3] = 255;
                }
            }
        }

        // bilinear resize (area-averaged when shrinking a lot)
        public static byte[] Resize(TextureSource t, int w, int h)
        {
            var src = t.Rgba; int sw = t.Width, sh = t.Height;
            // pre-shrink by 2x box filter while the source is >2x the target
            while (sw >= w * 2 && sh >= h * 2)
            {
                int nw = sw / 2, nh = sh / 2; var d = new byte[nw * nh * 4];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                        for (int c = 0; c < 4; c++)
                        {
                            int a = ((2 * y) * sw + 2 * x) * 4 + c, b = a + sw * 4;
                            d[(y * nw + x) * 4 + c] = (byte)((src[a] + src[a + 4] + src[b] + src[b + 4] + 2) / 4);
                        }
                src = d; sw = nw; sh = nh;
            }
            var o = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                float fy = Math.Max(0f, (y + 0.5f) * sh / h - 0.5f); int y0 = Math.Min((int)fy, sh - 1), y1 = Math.Min(y0 + 1, sh - 1); float ty = fy - y0;
                for (int x = 0; x < w; x++)
                {
                    float fx = Math.Max(0f, (x + 0.5f) * sw / w - 0.5f); int x0 = Math.Min((int)fx, sw - 1), x1 = Math.Min(x0 + 1, sw - 1); float tx = fx - x0;
                    for (int c = 0; c < 4; c++)
                    {
                        float a = src[(y0 * sw + x0) * 4 + c] * (1 - tx) + src[(y0 * sw + x1) * 4 + c] * tx;
                        float b = src[(y1 * sw + x0) * 4 + c] * (1 - tx) + src[(y1 * sw + x1) * 4 + c] * tx;
                        o[(y * w + x) * 4 + c] = (byte)Math.Min(255f, Math.Max(0f, a * (1 - ty) + b * ty + 0.5f));
                    }
                }
            }
            return o;
        }
    }

    // ------------------------------------------------------------------ mesh
    public enum Shading { Custom = 0, Smooth = 1, Flat = 2, AutoSmooth = 3 }

    public class BuiltMesh
    {
        public float[] Positions;   // xyz
        public float[] Uvs;         // uv
        public float[] Normals;     // xyz (always filled)
        public int[] Triangles;
        public int VertexCount;
        public int ClampedUvs;
        public Shading Used;        // Custom falls back to Smooth when the OBJ has no normals
    }

    public static class MeshBuilder
    {
        struct Key : IEquatable<Key>
        {
            public int v, t, n, mat, ou, ov;   // n = OBJ normal index (Custom) or quantized face normal id (Flat)
            public bool Equals(Key o) { return v == o.v && t == o.t && n == o.n && mat == o.mat && ou == o.ou && ov == o.ov; }
            public override bool Equals(object o) { return o is Key && Equals((Key)o); }
            public override int GetHashCode()
            {
                unchecked { return ((((v * 397 ^ t) * 397 ^ n) * 397 ^ mat) * 397 ^ ou) * 397 ^ ov; }
            }
        }

        static void FaceNormal(ObjModel m, int t, out double x, out double y, out double z)
        {
            int a = m.Corners[t * 3].v * 3, b = m.Corners[t * 3 + 1].v * 3, c = m.Corners[t * 3 + 2].v * 3;
            double ux = m.V[b] - m.V[a], uy = m.V[b + 1] - m.V[a + 1], uz = m.V[b + 2] - m.V[a + 2];
            double vx = m.V[c] - m.V[a], vy = m.V[c + 1] - m.V[a + 1], vz = m.V[c + 2] - m.V[a + 2];
            // same handedness as Unity's RecalculateNormals on the raw OBJ data (verified against Workshop.exe output)
            x = uy * vz - uz * vy; y = uz * vx - ux * vz; z = ux * vy - uy * vx;
        }

        static void Normalize(ref double x, ref double y, ref double z)
        {
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l > 1e-20) { x /= l; y /= l; z /= l; } else { x = 0; y = 1; z = 0; }
        }

        // rects: per material index; noMaterialRect used for faces with no usemtl
        public static BuiltMesh Build(ObjModel m, UvRect[] rects, UvRect noMaterialRect, Shading shading, float autoAngle = 30f)
        {
            bool hasUv = m.VT.Count > 0;
            bool allNormals = m.VN.Count > 0;
            for (int i = 0; i < m.Corners.Count && allNormals; i++) if (m.Corners[i].n < 0) allNormals = false;
            Shading mode = shading == Shading.Custom && !allNormals ? Shading.Smooth : shading;

            // per-triangle face normals; per-position smooth normals (area weighted, ignores UV seams)
            int nt = m.TriangleCount;
            var fn = new double[nt * 3];
            for (int t = 0; t < nt; t++) { double x, y, z; FaceNormal(m, t, out x, out y, out z); fn[t * 3] = x; fn[t * 3 + 1] = y; fn[t * 3 + 2] = z; }
            double[] smooth = null; int[] weld = null;
            if (mode == Shading.Smooth || mode == Shading.AutoSmooth)
            {   // weld by position, not OBJ index, so duplicated vertices (UV seams, poles) don't leave shading seams
                int nv = m.V.Count / 3; weld = new int[nv];
                var byPos = new Dictionary<long, int>();
                for (int i = 0; i < nv; i++)
                {
                    long h = ((long)BitConverter.DoubleToInt64Bits(m.V[i * 3] + 0.0) * 73856093L) ^ ((long)BitConverter.DoubleToInt64Bits(m.V[i * 3 + 1] + 0.0) * 19349663L) ^ ((long)BitConverter.DoubleToInt64Bits(m.V[i * 3 + 2] + 0.0) * 83492791L);
                    int j; weld[i] = i;
                    // resolve hash collisions by exact comparison along a short probe chain
                    while (byPos.TryGetValue(h, out j))
                    {
                        if (m.V[j * 3] == m.V[i * 3] && m.V[j * 3 + 1] == m.V[i * 3 + 1] && m.V[j * 3 + 2] == m.V[i * 3 + 2]) { weld[i] = j; break; }
                        h++;
                    }
                    if (weld[i] == i) byPos[h] = i;
                }
                smooth = new double[m.V.Count];
                for (int t = 0; t < nt; t++)
                    for (int c = 0; c < 3; c++)
                    {
                        int v = weld[m.Corners[t * 3 + c].v] * 3;
                        smooth[v] += fn[t * 3]; smooth[v + 1] += fn[t * 3 + 1]; smooth[v + 2] += fn[t * 3 + 2];
                    }
            }
            // Auto smooth: each corner averages only the faces around its position that are within the angle of its own face
            double[] cornerN = null;
            if (mode == Shading.AutoSmooth)
            {
                var unit = new double[nt * 3]; var degenerate = new bool[nt];
                for (int t = 0; t < nt; t++)
                {
                    double x = fn[t * 3], y = fn[t * 3 + 1], z = fn[t * 3 + 2], l = Math.Sqrt(x * x + y * y + z * z);
                    degenerate[t] = l < 1e-20;
                    if (!degenerate[t]) { unit[t * 3] = x / l; unit[t * 3 + 1] = y / l; unit[t * 3 + 2] = z / l; }
                }
                int nv = m.V.Count / 3;
                var start = new int[nv + 1];
                for (int i = 0; i < m.Corners.Count; i++) start[weld[m.Corners[i].v] + 1]++;
                for (int i = 0; i < nv; i++) start[i + 1] += start[i];
                var fill = (int[])start.Clone(); var faces = new int[m.Corners.Count];
                for (int i = 0; i < m.Corners.Count; i++) faces[fill[weld[m.Corners[i].v]]++] = i / 3;
                double cosLimit = Math.Cos(Math.Max(0.0, Math.Min(180.0, autoAngle)) * Math.PI / 180.0) - 1e-9;
                cornerN = new double[m.Corners.Count * 3];
                for (int i = 0; i < m.Corners.Count; i++)
                {
                    int t = i / 3, p = weld[m.Corners[i].v];
                    double sx = 0, sy = 0, sz = 0;
                    for (int j = start[p]; j < start[p + 1]; j++)
                    {
                        int o = faces[j];
                        bool use = degenerate[t] || (!degenerate[o] &&
                            unit[t * 3] * unit[o * 3] + unit[t * 3 + 1] * unit[o * 3 + 1] + unit[t * 3 + 2] * unit[o * 3 + 2] >= cosLimit);
                        if (use) { sx += fn[o * 3]; sy += fn[o * 3 + 1]; sz += fn[o * 3 + 2]; }
                    }
                    Normalize(ref sx, ref sy, ref sz);
                    cornerN[i * 3] = sx; cornerN[i * 3 + 1] = sy; cornerN[i * 3 + 2] = sz;
                }
            }
            Dictionary<long, int> flatIds = (mode == Shading.Flat || mode == Shading.AutoSmooth) ? new Dictionary<long, int>() : null;

            var map = new Dictionary<Key, int>(m.Corners.Count);
            var pos = new List<float>(); var uv = new List<float>(); var nrm = new List<float>();
            var tris = new int[m.Corners.Count];
            int clamped = 0;
            var rectIds = new Dictionary<UvRect, int>();      // materials sharing a texture share vertices
            for (int t = 0; t < nt; t++)
            {
                int mi = m.TriMaterial[t];
                UvRect r = mi >= 0 ? rects[mi] : noMaterialRect;
                int ou = 0, ov = 0;
                if (hasUv && !r.Tile && !r.Swatch)
                {   // move each triangle into the 0..1 tile it sits in, so atlas regions don't bleed
                    float mu = float.MaxValue, mv = float.MaxValue;
                    for (int c = 0; c < 3; c++)
                    {
                        var k = m.Corners[t * 3 + c]; if (k.t < 0) continue;
                        mu = Math.Min(mu, m.VT[k.t * 2]); mv = Math.Min(mv, m.VT[k.t * 2 + 1]);
                    }
                    if (mu != float.MaxValue) { ou = (int)Math.Floor(mu + 1e-5f); ov = (int)Math.Floor(mv + 1e-5f); }
                }
                double fx = fn[t * 3], fy = fn[t * 3 + 1], fz = fn[t * 3 + 2];
                Normalize(ref fx, ref fy, ref fz);
                int flatId = 0;
                if (mode == Shading.Flat)
                {   // faces with the same direction (e.g. the two halves of a quad) can still share vertices
                    long q = ((long)Math.Round(fx * 10000) + 20000) * 40001L * 40001L + ((long)Math.Round(fy * 10000) + 20000) * 40001L + ((long)Math.Round(fz * 10000) + 20000);
                    if (!flatIds.TryGetValue(q, out flatId)) { flatId = flatIds.Count; flatIds[q] = flatId; }
                }
                for (int c = 0; c < 3; c++)
                {
                    var k = m.Corners[t * 3 + c];
                    int nkey = mode == Shading.Custom ? k.n : mode == Shading.Flat ? flatId : -1;
                    if (mode == Shading.AutoSmooth)
                    {   // corners with the same resulting normal share a vertex
                        int ci = (t * 3 + c) * 3;
                        long q = ((long)Math.Round(cornerN[ci] * 10000) + 20000) * 40001L * 40001L + ((long)Math.Round(cornerN[ci + 1] * 10000) + 20000) * 40001L + ((long)Math.Round(cornerN[ci + 2] * 10000) + 20000);
                        if (!flatIds.TryGetValue(q, out nkey)) { nkey = flatIds.Count; flatIds[q] = nkey; }
                    }
                    int rid; if (r.Tile) rid = 0; else if (!rectIds.TryGetValue(r, out rid)) { rid = rectIds.Count + 1; rectIds[r] = rid; }
                    var key = new Key { v = k.v, t = r.Swatch ? -1 : k.t, n = nkey, mat = rid, ou = ou, ov = ov };
                    int idx;
                    if (!map.TryGetValue(key, out idx))
                    {
                        idx = pos.Count / 3; map[key] = idx;
                        pos.Add(m.V[k.v * 3]); pos.Add(m.V[k.v * 3 + 1]); pos.Add(m.V[k.v * 3 + 2]);
                        float u, v;
                        if (r.Swatch) { u = r.U0; v = r.V0; }
                        else if (hasUv && k.t >= 0)
                        {
                            u = m.VT[k.t * 2] - ou; v = m.VT[k.t * 2 + 1] - ov;
                            if (!r.Tile)
                            {
                                if (u < -1e-4f || u > 1.0001f || v < -1e-4f || v > 1.0001f) clamped++;
                                u = Math.Min(1f, Math.Max(0f, u)); v = Math.Min(1f, Math.Max(0f, v));
                                if (r.SU1 > r.SU0 && r.SV1 > r.SV0 && (r.SU0 != 0f || r.SV0 != 0f || r.SU1 != 1f || r.SV1 != 1f))
                                {   // cropped texture: map the used window onto the whole rect
                                    u = Math.Min(1f, Math.Max(0f, (u - r.SU0) / (r.SU1 - r.SU0)));
                                    v = Math.Min(1f, Math.Max(0f, (v - r.SV0) / (r.SV1 - r.SV0)));
                                }
                                u = r.U0 + u * (r.U1 - r.U0); v = r.V0 + v * (r.V1 - r.V0);
                            }
                        }
                        else { u = (r.U0 + r.U1) * 0.5f; v = (r.V0 + r.V1) * 0.5f; }
                        uv.Add(u); uv.Add(v);
                        double nx, ny, nz;
                        if (mode == Shading.Custom) { nx = m.VN[k.n * 3]; ny = m.VN[k.n * 3 + 1]; nz = m.VN[k.n * 3 + 2]; }
                        else if (mode == Shading.Smooth) { int wv = weld[k.v] * 3; nx = smooth[wv]; ny = smooth[wv + 1]; nz = smooth[wv + 2]; }
                        else if (mode == Shading.AutoSmooth) { int ci = (t * 3 + c) * 3; nx = cornerN[ci]; ny = cornerN[ci + 1]; nz = cornerN[ci + 2]; }
                        else { nx = fx; ny = fy; nz = fz; }
                        Normalize(ref nx, ref ny, ref nz);
                        nrm.Add((float)nx); nrm.Add((float)ny); nrm.Add((float)nz);
                    }
                    tris[t * 3 + c] = idx;
                }
            }
            return new BuiltMesh
            {
                Positions = pos.ToArray(), Uvs = uv.ToArray(), Normals = nrm.ToArray(),
                Triangles = tris, VertexCount = pos.Count / 3, ClampedUvs = clamped, Used = mode
            };
        }
    }


    public static class ImageInfo
    {
        /// Reads width/height from a PNG or JPEG header without decoding. Returns false if unknown.
        public static bool TryGetSize(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                using (var f = File.OpenRead(path))
                {
                    var hdr = new byte[24]; if (f.Read(hdr, 0, 24) < 24) return false;
                    if (hdr[0] == 0x89 && hdr[1] == 0x50 && hdr[2] == 0x4E && hdr[3] == 0x47)
                    {
                        w = (hdr[16] << 24) | (hdr[17] << 16) | (hdr[18] << 8) | hdr[19];
                        h = (hdr[20] << 24) | (hdr[21] << 16) | (hdr[22] << 8) | hdr[23];
                        return true;
                    }
                    if (hdr[0] == 'D' && hdr[1] == 'D' && hdr[2] == 'S' && hdr[3] == ' ')
                    {
                        h = hdr[12] | hdr[13] << 8 | hdr[14] << 16 | hdr[15] << 24;
                        w = hdr[16] | hdr[17] << 8 | hdr[18] << 16 | hdr[19] << 24;
                        return w > 0 && h > 0;
                    }
                    if (path.ToLowerInvariant().EndsWith(".tga"))
                    {
                        w = hdr[12] | hdr[13] << 8; h = hdr[14] | hdr[15] << 8;
                        return w > 0 && h > 0;
                    }
                    if (hdr[0] == 0xFF && hdr[1] == 0xD8)
                    {
                        f.Position = 2;
                        while (true)
                        {
                            int b = f.ReadByte(); if (b < 0) return false;
                            if (b != 0xFF) continue;
                            int marker = f.ReadByte(); while (marker == 0xFF) marker = f.ReadByte();
                            if (marker < 0) return false;
                            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                            int len = (f.ReadByte() << 8) | f.ReadByte();
                            if (len < 2) return false;
                            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                            {
                                f.ReadByte(); h = (f.ReadByte() << 8) | f.ReadByte(); w = (f.ReadByte() << 8) | f.ReadByte();
                                return w > 0 && h > 0;
                            }
                            f.Position += len - 2;
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
