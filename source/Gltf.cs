// TTVR Workshop Plus - glTF 2.0 (.gltf / .glb) reader. Produces the same ObjModel the OBJ path uses,
// plus per-material colour/texture info. Engine-independent (.NET 3.5 compatible).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TTVRPlus
{
    public class ModelMaterial
    {
        public string Name;
        public byte R = 255, G = 255, B = 255;
        public string TexturePath;      // external image file, or
        public byte[] TextureData;      // image embedded in the file
        public string TextureName;      // for messages
        public string TextureKey;       // same image -> same key (for de-duplication)
    }

    // ------------------------------------------------------------------ minimal JSON
    public static class GltfJson
    {
        public static object Parse(string s) { int i = 0; var v = Value(s, ref i); return v; }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new Exception("unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++;
                Ws(s, ref i); if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); string k = Str(s, ref i); Ws(s, ref i);
                    if (s[i] != ':') throw new Exception("bad JSON at " + i); i++;
                    d[k] = Value(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new Exception("bad JSON at " + i);
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++;
                Ws(s, ref i); if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new Exception("bad JSON at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new Exception("bad JSON at " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new Exception("bad JSON string at " + i);
            i++; var b = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { b.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': b.Append('\n'); break; case 't': b.Append('\t'); break; case 'r': b.Append('\r'); break;
                    case 'b': b.Append('\b'); break; case 'f': b.Append('\f'); break;
                    case 'u': b.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: b.Append(e); break;
                }
            }
            i++; return b.ToString();
        }
    }

    // ------------------------------------------------------------------ glTF
    public static class GltfReader
    {
        public static bool IsGltf(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            return p.EndsWith(".gltf") || p.EndsWith(".glb");
        }

        class Ctx
        {
            public Dictionary<string, object> J;
            public List<byte[]> Buffers = new List<byte[]>();
            public string Dir;
            public ObjModel M;
            public List<ModelMaterial> Mats;
            public int[] MatIndex;       // glTF material -> MaterialNames index
            public int Skipped;
            public Dictionary<int, byte[]> Images = new Dictionary<int, byte[]>();
        }

        // helpers for loosely-typed JSON
        static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
        static List<object> L(object o) { return o as List<object>; }
        static object G(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        static int I(object o, int def) { return o is double ? (int)(double)o : def; }
        static double D(object o, double def) { return o is double ? (double)o : def; }
        static string S(object o) { return o as string; }

        public static ObjModel Load(string path, out List<ModelMaterial> materials, out string warning)
        {
            warning = null;
            byte[] file = File.ReadAllBytes(path);
            var c = new Ctx { Dir = Path.GetDirectoryName(path), M = new ObjModel() };
            byte[] glbBin = null;
            string json;
            if (file.Length >= 12 && file[0] == 'g' && file[1] == 'l' && file[2] == 'T' && file[3] == 'F')
            {
                int version = BitConverter.ToInt32(file, 4);
                if (version != 2) throw new Exception("glTF version " + version + " isn't supported (only glTF 2.0)");
                int p = 12; json = null;
                while (p + 8 <= file.Length)
                {
                    int len = BitConverter.ToInt32(file, p); uint type = BitConverter.ToUInt32(file, p + 4); p += 8;
                    if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(file, p, len);
                    else if (type == 0x004E4942) { glbBin = new byte[len]; Buffer.BlockCopy(file, p, glbBin, 0, len); }
                    p += len;
                }
                if (json == null) throw new Exception("GLB file has no JSON chunk");
            }
            else json = Encoding.UTF8.GetString(file);
            if (json.Length > 0 && json[0] == '﻿') json = json.Substring(1);
            c.J = O(GltfJson.Parse(json));
            if (c.J == null) throw new Exception("not a glTF file");

            var asset = O(G(c.J, "asset"));
            string ver = S(G(asset, "version"));
            if (ver != null && !ver.StartsWith("2")) throw new Exception("glTF version " + ver + " isn't supported (only glTF 2.0)");
            var req = L(G(c.J, "extensionsRequired"));
            if (req != null)
                foreach (var e in req)
                {
                    string n = S(e);
                    if (n == "KHR_draco_mesh_compression" || n == "EXT_meshopt_compression" || n == "KHR_mesh_quantization")
                        throw new Exception("the file uses " + n + "; export it again without mesh compression");
                }

            // buffers
            var buffers = L(G(c.J, "buffers")) ?? new List<object>();
            for (int b = 0; b < buffers.Count; b++)
            {
                string uri = S(G(O(buffers[b]), "uri"));
                if (uri == null) { c.Buffers.Add(glbBin ?? new byte[0]); continue; }
                c.Buffers.Add(LoadUri(uri, c.Dir));
            }

            // materials
            var mats = L(G(c.J, "materials")) ?? new List<object>();
            c.Mats = new List<ModelMaterial>();
            c.MatIndex = new int[mats.Count];
            for (int i = 0; i < mats.Count; i++) c.MatIndex[i] = -1;

            // scene graph
            var nodes = L(G(c.J, "nodes")) ?? new List<object>();
            var scenes = L(G(c.J, "scenes"));
            var roots = new List<int>();
            if (scenes != null && scenes.Count > 0)
            {
                var sc = O(scenes[Math.Max(0, Math.Min(scenes.Count - 1, I(G(c.J, "scene"), 0)))]);
                foreach (var n in L(G(sc, "nodes")) ?? new List<object>()) roots.Add(I(n, 0));
            }
            else
            {   // no scene: every node that isn't somebody's child
                var isChild = new bool[nodes.Count];
                foreach (var n in nodes) foreach (var ch in L(G(O(n), "children")) ?? new List<object>()) { int k = I(ch, -1); if (k >= 0 && k < isChild.Length) isChild[k] = true; }
                for (int k = 0; k < nodes.Count; k++) if (!isChild[k]) roots.Add(k);
                if (nodes.Count == 0 && L(G(c.J, "meshes")) != null) for (int m = 0; m < L(G(c.J, "meshes")).Count; m++) AddMesh(c, m, Mat4.Identity());
            }
            var visiting = new HashSet<int>();
            foreach (int r in roots) Walk(c, nodes, r, Mat4.Identity(), visiting);

            if (c.M.TriangleCount == 0) throw new Exception("no triangle meshes found in the glTF file");
            if (c.Skipped > 0) warning = c.Skipped + " non-triangle primitive(s) (points/lines) were skipped";
            materials = c.Mats;
            return c.M;
        }

        static byte[] LoadUri(string uri, string dir)
        {
            if (uri.StartsWith("data:"))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || uri.Substring(0, comma).IndexOf(";base64") < 0) throw new Exception("unsupported data URI");
                return Convert.FromBase64String(uri.Substring(comma + 1));
            }
            string p = Path.Combine(dir, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) throw new Exception("missing file referenced by the glTF: " + uri);
            return File.ReadAllBytes(p);
        }

        static void Walk(Ctx c, List<object> nodes, int idx, Mat4 parent, HashSet<int> visiting)
        {
            if (idx < 0 || idx >= nodes.Count || visiting.Contains(idx)) return;
            visiting.Add(idx);
            var n = O(nodes[idx]);
            Mat4 local = Mat4.Identity();
            var m = L(G(n, "matrix"));
            if (m != null && m.Count == 16) { var a = new double[16]; for (int i = 0; i < 16; i++) a[i] = D(m[i], 0); local = Mat4.FromColumnMajor(a); }
            else
            {
                var t = L(G(n, "translation")); var r = L(G(n, "rotation")); var s = L(G(n, "scale"));
                local = Mat4.TRS(t != null ? new[] { D(t[0], 0), D(t[1], 0), D(t[2], 0) } : new double[] { 0, 0, 0 },
                                 r != null ? new[] { D(r[0], 0), D(r[1], 0), D(r[2], 0), D(r[3], 1) } : new double[] { 0, 0, 0, 1 },
                                 s != null ? new[] { D(s[0], 1), D(s[1], 1), D(s[2], 1) } : new double[] { 1, 1, 1 });
            }
            Mat4 world = parent * local;
            int mesh = I(G(n, "mesh"), -1);
            if (mesh >= 0) AddMesh(c, mesh, world);
            foreach (var ch in L(G(n, "children")) ?? new List<object>()) Walk(c, nodes, I(ch, -1), world, visiting);
            visiting.Remove(idx);
        }

        static void AddMesh(Ctx c, int meshIndex, Mat4 world)
        {
            var meshes = L(G(c.J, "meshes"));
            if (meshes == null || meshIndex >= meshes.Count) return;
            var prims = L(G(O(meshes[meshIndex]), "primitives")) ?? new List<object>();
            double[] nm = world.NormalMatrix();
            bool flip = world.Det3() < 0;
            foreach (var po in prims)
            {
                var p = O(po);
                int mode = I(G(p, "mode"), 4);
                if (mode != 4 && mode != 5 && mode != 6) { c.Skipped++; continue; }
                var attr = O(G(p, "attributes"));
                int posAcc = I(G(attr, "POSITION"), -1);
                if (posAcc < 0) continue;
                float[] pos = ReadAccessor(c, posAcc, 3);
                int nAcc = I(G(attr, "NORMAL"), -1), tAcc = I(G(attr, "TEXCOORD_0"), -1);
                float[] nrm = nAcc >= 0 ? ReadAccessor(c, nAcc, 3) : null;
                float[] uv = tAcc >= 0 ? ReadAccessor(c, tAcc, 2) : null;
                int vc = pos.Length / 3;
                int vBase = c.M.V.Count / 3, tBase = c.M.VT.Count / 2, nBase = c.M.VN.Count / 3;
                for (int i = 0; i < vc; i++)
                {
                    double[] q = world.TransformPoint(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                    c.M.V.Add((float)q[0]); c.M.V.Add((float)q[1]); c.M.V.Add((float)q[2]);
                    if (nrm != null)
                    {
                        double x = nrm[i * 3], y = nrm[i * 3 + 1], z = nrm[i * 3 + 2];
                        double nx = nm[0] * x + nm[1] * y + nm[2] * z, ny = nm[3] * x + nm[4] * y + nm[5] * z, nz = nm[6] * x + nm[7] * y + nm[8] * z;
                        double l = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (l > 1e-20) { nx /= l; ny /= l; nz /= l; }
                        c.M.VN.Add((float)nx); c.M.VN.Add((float)ny); c.M.VN.Add((float)nz);
                    }
                    if (uv != null) { c.M.VT.Add(uv[i * 2]); c.M.VT.Add(1f - uv[i * 2 + 1]); }   // glTF UVs start top-left
                }
                // indices -> triangle list
                int[] idx;
                int iAcc = I(G(p, "indices"), -1);
                if (iAcc >= 0) { float[] f = ReadAccessor(c, iAcc, 1); idx = new int[f.Length]; for (int i = 0; i < f.Length; i++) idx[i] = (int)f[i]; }
                else { idx = new int[vc]; for (int i = 0; i < vc; i++) idx[i] = i; }
                var tris = new List<int>();
                if (mode == 4) tris.AddRange(idx);
                else if (mode == 5) for (int i = 0; i + 2 < idx.Length; i++) { if ((i & 1) == 0) { tris.Add(idx[i]); tris.Add(idx[i + 1]); tris.Add(idx[i + 2]); } else { tris.Add(idx[i + 1]); tris.Add(idx[i]); tris.Add(idx[i + 2]); } }
                else for (int i = 1; i + 1 < idx.Length; i++) { tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]); }

                int mat = MaterialSlot(c, I(G(p, "material"), -1));
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], d = tris[t + 2];
                    if (a >= vc || b >= vc || d >= vc) continue;
                    if (flip) { int tmp = b; b = d; d = tmp; }
                    foreach (int k in new[] { a, b, d })
                        c.M.Corners.Add(new Corner { v = vBase + k, t = uv != null ? tBase + k : -1, n = nrm != null ? nBase + k : -1 });
                    c.M.TriMaterial.Add(mat);
                }
            }
        }

        static int MaterialSlot(Ctx c, int gltfMat)
        {
            if (gltfMat < 0 || gltfMat >= c.MatIndex.Length) return -1;
            if (c.MatIndex[gltfMat] >= 0) return c.MatIndex[gltfMat];
            var m = O(L(G(c.J, "materials"))[gltfMat]);
            var mm = new ModelMaterial { Name = S(G(m, "name")) ?? ("material_" + gltfMat) };
            var pbr = O(G(m, "pbrMetallicRoughness"));
            var f = L(G(pbr, "baseColorFactor"));
            if (f != null && f.Count >= 3)
            {   // linear factor -> sRGB colour
                mm.R = ToSrgb(D(f[0], 1)); mm.G = ToSrgb(D(f[1], 1)); mm.B = ToSrgb(D(f[2], 1));
            }
            var tex = O(G(pbr, "baseColorTexture"));
            if (tex == null)   // KHR_materials_pbrSpecularGlossiness fallback
                tex = O(G(O(G(O(G(m, "extensions")), "KHR_materials_pbrSpecularGlossiness")), "diffuseTexture"));
            if (tex != null) LoadImage(c, I(G(tex, "index"), -1), mm);
            // keep the material name unique (it's used as a key)
            string baseName = mm.Name; int n = 2;
            while (c.M.MaterialNames.Contains(mm.Name)) mm.Name = baseName + " (" + (n++) + ")";
            int slot = c.M.MaterialNames.Count;
            c.M.MaterialNames.Add(mm.Name); c.Mats.Add(mm);
            c.MatIndex[gltfMat] = slot;
            return slot;
        }

        static byte ToSrgb(double v)
        {
            v = Math.Max(0, Math.Min(1, v));
            double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return (byte)Math.Round(s * 255);
        }

        static void LoadImage(Ctx c, int texIndex, ModelMaterial mm)
        {
            var textures = L(G(c.J, "textures")); var images = L(G(c.J, "images"));
            if (texIndex < 0 || textures == null || texIndex >= textures.Count || images == null) return;
            var t = O(textures[texIndex]);
            int src = I(G(t, "source"), -1);
            if (src < 0)
            {   // texture only available through an extension (e.g. KTX2 / WebP)
                var ext = O(G(t, "extensions"));
                if (ext != null) foreach (var kv in ext) { int s2 = I(G(O(kv.Value), "source"), -1); if (s2 >= 0) { src = s2; break; } }
            }
            if (src < 0 || src >= images.Count) return;
            var img = O(images[src]);
            mm.TextureKey = "gltf-image:" + src;
            string uri = S(G(img, "uri"));
            mm.TextureName = S(G(img, "name")) ?? (uri != null && !uri.StartsWith("data:") ? Path.GetFileName(Uri.UnescapeDataString(uri)) : "embedded image " + src);
            if (uri != null && !uri.StartsWith("data:"))
            {
                string p = Path.Combine(c.Dir, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) mm.TexturePath = p;
                else mm.TextureName += " (file not found)";
                return;
            }
            byte[] cached;
            if (c.Images.TryGetValue(src, out cached)) { mm.TextureData = cached; return; }
            try
            {
                if (uri != null) mm.TextureData = LoadUri(uri, c.Dir);
                else
                {
                    int bv = I(G(img, "bufferView"), -1);
                    if (bv >= 0) mm.TextureData = ReadBufferView(c, bv);
                }
            }
            catch (Exception) { mm.TextureData = null; }
            c.Images[src] = mm.TextureData;
        }

        static byte[] ReadBufferView(Ctx c, int bv)
        {
            var v = O(L(G(c.J, "bufferViews"))[bv]);
            byte[] buf = c.Buffers[I(G(v, "buffer"), 0)];
            int off = I(G(v, "byteOffset"), 0), len = I(G(v, "byteLength"), 0);
            var r = new byte[len]; Buffer.BlockCopy(buf, off, r, 0, len); return r;
        }

        // returns accessor data as floats (components per element = comps); integer types are converted, normalized ones scaled
        static float[] ReadAccessor(Ctx c, int ai, int comps)
        {
            var a = O(L(G(c.J, "accessors"))[ai]);
            int count = I(G(a, "count"), 0), ct = I(G(a, "componentType"), 5126);
            bool norm = G(a, "normalized") is bool && (bool)G(a, "normalized");
            string type = S(G(a, "type")) ?? "SCALAR";
            int n = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" ? 4 : type == "MAT4" ? 16 : 1;
            int csize = ct == 5126 || ct == 5125 ? 4 : ct == 5122 || ct == 5123 ? 2 : 1;
            var res = new float[count * comps];
            int bvI = I(G(a, "bufferView"), -1);
            if (bvI >= 0)
            {
                var v = O(L(G(c.J, "bufferViews"))[bvI]);
                byte[] buf = c.Buffers[I(G(v, "buffer"), 0)];
                int stride = I(G(v, "byteStride"), 0); if (stride == 0) stride = n * csize;
                int start = I(G(v, "byteOffset"), 0) + I(G(a, "byteOffset"), 0);
                for (int e = 0; e < count; e++)
                    for (int k = 0; k < comps && k < n; k++)
                        res[e * comps + k] = Comp(buf, start + e * stride + k * csize, ct, norm);
            }
            var sparse = O(G(a, "sparse"));
            if (sparse != null)
            {
                int sc = I(G(sparse, "count"), 0);
                var si = O(G(sparse, "indices")); var sv = O(G(sparse, "values"));
                var ib = O(L(G(c.J, "bufferViews"))[I(G(si, "bufferView"), 0)]); var vb = O(L(G(c.J, "bufferViews"))[I(G(sv, "bufferView"), 0)]);
                byte[] ibuf = c.Buffers[I(G(ib, "buffer"), 0)], vbuf = c.Buffers[I(G(vb, "buffer"), 0)];
                int ict = I(G(si, "componentType"), 5125), isz = ict == 5125 ? 4 : ict == 5123 ? 2 : 1;
                int ioff = I(G(ib, "byteOffset"), 0) + I(G(si, "byteOffset"), 0), voff = I(G(vb, "byteOffset"), 0) + I(G(sv, "byteOffset"), 0);
                for (int s = 0; s < sc; s++)
                {
                    int target = (int)Comp(ibuf, ioff + s * isz, ict, false);
                    if (target < 0 || target >= count) continue;
                    for (int k = 0; k < comps && k < n; k++) res[target * comps + k] = Comp(vbuf, voff + (s * n + k) * csize, ct, norm);
                }
            }
            return res;
        }

        static float Comp(byte[] b, int o, int ct, bool norm)
        {
            switch (ct)
            {
                case 5126: return BitConverter.ToSingle(b, o);
                case 5125: return (float)BitConverter.ToUInt32(b, o);
                case 5123: { int v = BitConverter.ToUInt16(b, o); return norm ? v / 65535f : v; }
                case 5122: { int v = BitConverter.ToInt16(b, o); return norm ? Math.Max(v / 32767f, -1f) : v; }
                case 5121: { int v = b[o]; return norm ? v / 255f : v; }
                case 5120: { int v = (sbyte)b[o]; return norm ? Math.Max(v / 127f, -1f) : v; }
            }
            throw new Exception("unsupported accessor component type " + ct);
        }
    }

    // ------------------------------------------------------------------ 4x4 (row-major storage)
    public struct Mat4
    {
        public double[] m;
        public static Mat4 Identity() { return new Mat4 { m = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } }; }
        public static Mat4 FromColumnMajor(double[] a)
        {
            var r = new double[16];
            for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) r[row * 4 + col] = a[col * 4 + row];
            return new Mat4 { m = r };
        }
        public static Mat4 TRS(double[] t, double[] q, double[] s)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double l = Math.Sqrt(x * x + y * y + z * z + w * w); if (l > 0) { x /= l; y /= l; z /= l; w /= l; }
            double[] r = {
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y) };
            return new Mat4 { m = new double[] {
                r[0] * s[0], r[1] * s[1], r[2] * s[2], t[0],
                r[3] * s[0], r[4] * s[1], r[5] * s[2], t[1],
                r[6] * s[0], r[7] * s[1], r[8] * s[2], t[2],
                0, 0, 0, 1 } };
        }
        public static Mat4 operator *(Mat4 a, Mat4 b)
        {
            var r = new double[16];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) { double s = 0; for (int k = 0; k < 4; k++) s += a.m[i * 4 + k] * b.m[k * 4 + j]; r[i * 4 + j] = s; }
            return new Mat4 { m = r };
        }
        public double[] TransformPoint(double x, double y, double z)
        {
            return new[] { m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11] };
        }
        public double Det3()
        {
            return m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
        }
        // inverse-transpose of the upper 3x3 (for normals), row-major 3x3
        public double[] NormalMatrix()
        {
            double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double D_ = -(b * i - c * h), E = a * i - c * g, F = -(a * h - b * g);
            double G_ = b * f - c * e, H = -(a * f - c * d), I_ = a * e - b * d;
            double det = a * A + b * B + c * C; if (Math.Abs(det) < 1e-30) det = 1;
            // cofactor matrix / det = inverse-transpose
            return new[] { A / det, B / det, C / det, D_ / det, E / det, F / det, G_ / det, H / det, I_ / det };
        }
    }
}
