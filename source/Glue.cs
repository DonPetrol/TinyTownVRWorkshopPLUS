// TTVR Workshop Plus - main Unity side: start-up (window, list loading), model loading and building
// (Workshop.LoadObject), textures, the editor's extra controls (shading, atlas, crop, size, flip, reimport,
// export) and messages. Other features live in their own files (list, thumbnails, publish, help...).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Steamworks;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;
using UnityEngine.UI;

namespace TTVRPlus
{
    public static class WorkshopPlus
    {
        public const string Version = "1.0";
        public const int VertexLimit = 65000;     // same constant the game uses; 16-bit Unity meshes

        static TextureSource ToSource(Texture2D t)
        {
            var px = t.GetPixels32();
            var b = new byte[px.Length * 4];
            for (int i = 0; i < px.Length; i++) { b[i * 4] = px[i].r; b[i * 4 + 1] = px[i].g; b[i * 4 + 2] = px[i].b; b[i * 4 + 3] = 255; }
            return new TextureSource { Width = t.width, Height = t.height, Rgba = b };
        }

        static Texture2D ToTexture(byte[] rgba, int size)
        {
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], 255);
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static void SetMainTexture(Workshop w, Texture tex)
        {
            if (w.defaultMaterial.mainTexture != null && w.defaultMaterial.mainTexture != w.defaultTexture)
                UnityEngine.Object.Destroy(w.defaultMaterial.mainTexture);
            w.defaultMaterial.mainTexture = tex;
        }

        // ---------------------------------------------------------------- texture loading (replaces Workshop.LoadTexture)
        static List<string> currentNotes;      // notes for the model being loaded, if any

        // notes about a texture that couldn't be used are always shown; size/atlas notes only when importing
        static readonly HashSet<string> alwaysNotes = new HashSet<string>();
        static int importFrame = -1;

        /// called at the start of the + (new), model Browse, texture Browse and texture Clear buttons
        public static void MarkImport() { importFrame = Time.frameCount; }
        static bool Importing { get { return importFrame == Time.frameCount; } }

        static void TexNote(string msg, bool always)
        {
            if (always) alwaysNotes.Add(msg);
            TexNote(msg);
        }

        static void TexNote(string msg)
        {
            if (currentNotes != null) { if (!currentNotes.Contains(msg)) currentNotes.Add(msg); }
            else Debug.Log("[WorkshopPlus] " + msg);
        }

        static GameObject reimportButton;

        /// re-read the model (and textures) from the same path; the current model stays if that fails
        static void Reimport(Workshop w)
        {
            var item = w.activeItem;
            if (item == null || string.IsNullOrEmpty(item.sourceFile)) { SetMessage(w, "Unable to reimport: this item has no model file."); return; }
            string path = item.sourceFile, name = Path.GetFileName(path);
            if (!File.Exists(path)) { SetMessage(w, "Unable to reimport: " + path + " can't be found. The current model is unchanged."); return; }
            ObjModel model; List<ModelMaterial> mats = null; string warn = null;
            try
            {
                if (GltfReader.IsGltf(path)) model = GltfReader.Load(path, out mats, out warn);
                else model = ObjParser.Parse(path);
            }
            catch (Exception e)
            {
                Debug.Log("[WorkshopPlus] reimport: " + e);
                SetMessage(w, "Unable to reimport " + name + ": " + e.Message + ". The current model is unchanged.");
                return;
            }
            // forget everything cached for it, then rebuild from the fresh copy
            string key = FileKey(path);
            string prefix = key.Substring(0, key.IndexOf('|')) + "|";
            modelCache.RemoveAll(c => c.Key.StartsWith(prefix));
            modelCache.Add(new ParsedModel { Key = key, Model = model, Mats = mats, Warn = warn });
            decodedCache.Clear(); decodedOrder.Clear(); decodedBytes = 0;
            atlasCache.Clear(); atlasOrder.Clear();
            FileCache.Forget(path);
            MarkImport();
            w.ActivateDetailView();
            var t = w.errorMessageText != null ? w.errorMessageText.text : "";
            if (string.IsNullOrEmpty(t)) SetMessage(w, "Note: reimported " + name + ".");
            else if (t.StartsWith("Note:")) SetMessage(w, "Note: reimported " + name + "; " + t.Substring(6));
        }

        static int texMax = 512;          // the item's Texture size setting while it's being built
        static int SupportedSize(int v) { return v < 128 ? 128 : v < 256 ? 256 : 512; }   // same rule as Workshop.exe

        // Same contract as the original: a readable square texture of 128, 256 or 512, or null if the file can't be read.
        // Unlike Unity's LoadImage it never hands back Unity's red "?" placeholder for files it can't decode.
        // ---------------------------------------------------------------- caches (so changing shading/atlas options doesn't re-read files)
        class Decoded { public int W, H; public byte[] Px; public int SqSize; public byte[] Sq; }
        static readonly Dictionary<string, Decoded> decodedCache = new Dictionary<string, Decoded>();
        static readonly List<string> decodedOrder = new List<string>();
        static long decodedBytes;

        static string FileKey(string path)
        {
            try { var fi = new FileInfo(path); return Path.GetFullPath(path).ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length; }
            catch (Exception) { return null; }
        }

        /// decoded RGBA for a file or embedded image, decoded once and kept while it's in use (about 200MB at most)
        static Decoded GetDecoded(string key, Func<byte[]> read, string pathForDetect, out string err)
        {
            err = null;
            Decoded d;
            if (key != null && decodedCache.TryGetValue(key, out d)) { decodedOrder.Remove(key); decodedOrder.Add(key); return d; }
            byte[] data = read();
            int iw, ih; byte[] px;
            if (!DecodeImage(data, pathForDetect, out iw, out ih, out px, out err)) return null;
            d = new Decoded { W = iw, H = ih, Px = px };
            if (key != null)
            {
                decodedCache[key] = d; decodedOrder.Add(key); decodedBytes += px.Length;
                while (decodedBytes > 200L * 1024 * 1024 && decodedOrder.Count > 1)
                {
                    string old = decodedOrder[0]; decodedOrder.RemoveAt(0);
                    Decoded o; if (decodedCache.TryGetValue(old, out o)) { decodedBytes -= o.Px.Length + (o.Sq != null ? o.Sq.Length : 0); decodedCache.Remove(old); }
                }
            }
            return d;
        }

        /// decodes the not-yet-cached DDS and TGA files in parallel (PNG and JPG have to go through Unity on the main thread)
        static void Prefetch(IList<string> paths)
        {
            var todo = new List<string>(); var keys = new List<string>();
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p) || p.StartsWith("embedded:") || todo.Contains(p) || !File.Exists(p)) continue;
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext != ".dds" && ext != ".tga") continue;
                string key = FileKey(p);
                if (key == null || decodedCache.ContainsKey(key)) continue;
                todo.Add(p); keys.Add(key);
            }
            if (todo.Count == 0) return;
            var sw = Stopwatch.StartNew();
            var results = new Decoded[todo.Count];
            int next = -1, size = texMax;
            int threads = Math.Max(1, Math.Min(Environment.ProcessorCount, todo.Count));
            var workers = new System.Threading.Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                workers[t] = new System.Threading.Thread(() =>
                {
                    int i;
                    while ((i = System.Threading.Interlocked.Increment(ref next)) < todo.Count)
                    {
                        try
                        {
                            byte[] data = File.ReadAllBytes(todo[i]);
                            int w, h; byte[] px; string err;
                            if (!ImageDecoder.TryDecode(data, todo[i], out w, out h, out px, out err)) continue;   // reported later by the normal path
                            var d = new Decoded { W = w, H = h, Px = px };
                            int s = SupportedSize(Math.Min(Math.Max(w, h), size));            // the shrunk copy too
                            d.Sq = (w == s && h == s) ? px : Atlas.Resize(new TextureSource { Width = w, Height = h, Rgba = px }, s, s);
                            d.SqSize = s;
                            results[i] = d;
                        }
                        catch (Exception) { }
                    }
                });
                workers[t].IsBackground = true;
                workers[t].Start();
            }
            foreach (var t in workers) t.Join();
            int n = 0;
            for (int i = 0; i < todo.Count; i++)
            {
                if (results[i] == null) continue;
                decodedCache[keys[i]] = results[i]; decodedOrder.Add(keys[i]); decodedBytes += results[i].Px.Length; n++;
            }
            Debug.Log("[WorkshopPlus] decoded " + n + " texture" + (n == 1 ? "" : "s") + " on " + threads + " threads in " + sw.ElapsedMilliseconds + " ms");
        }

        // parsed models, so rebuilding the same file skips parsing
        class ParsedModel { public string Key; public ObjModel Model; public List<ModelMaterial> Mats; public string Warn; }
        static readonly List<ParsedModel> modelCache = new List<ParsedModel>();

        // finished atlases (the pixel work), keyed by everything that goes into them
        static readonly Dictionary<string, AtlasResult> atlasCache = new Dictionary<string, AtlasResult>();
        static readonly List<string> atlasOrder = new List<string>();

        public static Texture2D LoadTexture(Workshop w, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string name = Path.GetFileName(path);
            string err;
            Decoded d;
            try { d = GetDecoded(FileKey(path), () => File.ReadAllBytes(path), path, out err); }
            catch (Exception e) { TexNote("could not read " + name + " (" + e.Message + ")", true); return null; }
            if (d == null) { TexNote(name + err, true); return null; }
            return TextureFromDecoded(d, name);
        }

        static Texture2D TextureFromDecoded(Decoded d, string name)
        {
            int iw = d.W, ih = d.H;
            if (Math.Max(iw, ih) > 512) TexNote(name + " is " + iw + "x" + ih + "px; the game's maximum is 512px, so it was reduced" + (texMax < 512 ? " (to " + texMax + "px, the Texture size setting)" : ""));
            else if (Math.Max(iw, ih) > texMax) TexNote(name + " was reduced to " + texMax + "px (the Texture size setting)");
            if (iw != ih) TexNote(name + " isn't square, so it was stretched");
            int size = SupportedSize(Math.Min(Math.Max(iw, ih), texMax));
            if (d.Sq == null || d.SqSize != size)
            {
                d.Sq = (iw == size && ih == size) ? d.Px : Atlas.Resize(new TextureSource { Width = iw, Height = ih, Rgba = d.Px }, size, size);
                d.SqSize = size;
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            var cols = new Color32[size * size];
            byte[] sq = d.Sq;
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(sq[i * 4], sq[i * 4 + 1], sq[i * 4 + 2], sq[i * 4 + 3]);
            tex.SetPixels32(cols); tex.Apply();
            return tex;
        }


        /// PNG/JPG (through Unity), DDS or TGA -> RGBA pixels, rows in the same order for every format
        public static bool DecodeImage(byte[] data, string path, out int iw, out int ih, out byte[] px, out string err)
        {
            iw = ih = 0; px = null; err = null;
            switch (ImageDecoder.Detect(data, path))
            {
                case ImageKind.Png:
                case ImageKind.Jpg:
                    {
                        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!t.LoadImage(data) || t.width <= 8 && t.height <= 8 && data.Length > 2048)
                        {   // the 8x8 check catches Unity's "?" placeholder
                            UnityEngine.Object.Destroy(t); err = " couldn't be decoded"; return false;
                        }
                        var c = t.GetPixels32(); iw = t.width; ih = t.height; UnityEngine.Object.Destroy(t);
                        px = new byte[c.Length * 4];
                        for (int i = 0; i < c.Length; i++) { px[i * 4] = c[i].r; px[i * 4 + 1] = c[i].g; px[i * 4 + 2] = c[i].b; px[i * 4 + 3] = c[i].a; }
                        return true;
                    }
                case ImageKind.Dds:
                case ImageKind.Tga:
                    string derr;
                    if (!ImageDecoder.TryDecode(data, path, out iw, out ih, out px, out derr)) { err = ": " + derr; return false; }
                    return true;
                default:
                    err = " isn't a supported image (use PNG, JPG, DDS or TGA)";
                    return false;
            }
        }

        // embedded glTF images seen while loading the current model (for UV cropping of a single embedded texture)
        static Dictionary<string, ModelMaterial> lastEmbedded = new Dictionary<string, ModelMaterial>();
        static string EmbeddedKey(string modelPath, ModelMaterial em)
        {
            string k = FileKey(modelPath);
            return k == null ? null : k + "|" + em.TextureKey + "|" + (em.TextureData != null ? em.TextureData.Length : 0);
        }

        static ModelMaterial embeddedFor(string key) { ModelMaterial m; return key != null && lastEmbedded.TryGetValue(key, out m) ? m : null; }

        /// a texture at its full resolution (file or embedded image)
        static TextureSource LoadFullSource(string modelPath, string path, ModelMaterial embedded)
        {
            try
            {
                string derr;
                var d = GetDecoded(embedded != null ? EmbeddedKey(modelPath, embedded) : FileKey(path),
                                   () => embedded != null ? embedded.TextureData : File.ReadAllBytes(path), embedded != null ? null : path, out derr);
                if (d == null) return null;
                return new TextureSource { Name = embedded != null ? embedded.TextureName : Path.GetFileName(path), Width = d.W, Height = d.H, Rgba = d.Px };
            }
            catch (Exception) { return null; }
        }

        static string ResolveTexture(string texPath, string objPath)
        {
            if (string.IsNullOrEmpty(texPath)) return null;
            if (File.Exists(texPath)) return texPath;
            string alt = Path.Combine(Path.GetDirectoryName(objPath), Path.GetFileName(texPath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(alt) ? alt : null;
        }

        // Signature matches Workshop.LoadObject(string path, string textureFile, out string err)
        public static GameObject LoadObject(Workshop w, string path, string textureFile, out string err)
        {
            err = string.Empty;
            ShowFlatWarning(null);
            try
            {
                if (!string.IsNullOrEmpty(path) && !File.Exists(path))
                {
                    string moved = Relocate(w, path, ref textureFile);
                    if (moved == null)
                    {
                        err = "model file not found: " + path + ". Open the item again to locate it.";
                        return null;
                    }
                    path = moved;
                    MarkImport();                     // re-pointing the model counts as re-adding it
                }
                return LoadObjectImpl(w, path, textureFile, out err);
            }
            catch (Exception e)
            {
                Debug.Log("[WorkshopPlus] " + e);
                err = e.Message;
                return null;
            }
        }

        // the texture picked with Browse, if any (one texture, as in the original tool)
        static string[] SplitTextures(string textureFile)
        {
            return string.IsNullOrEmpty(textureFile) ? new string[0] : new[] { textureFile };
        }


        static GameObject LoadObjectImpl(Workshop w, string path, string textureFile, out string err)
        {
            err = string.Empty;
            w.SetErrorMessage(string.Empty);
            var notes = new List<string>();
            ObjModel model;
            List<ModelMaterial> modelMats = null;
            var psw = System.Diagnostics.Stopwatch.StartNew();
            string mkey = FileKey(path);
            var cached = mkey == null ? null : modelCache.Find(c => c.Key == mkey);
            if (cached != null) { model = cached.Model; modelMats = cached.Mats; if (cached.Warn != null) notes.Add(cached.Warn); }
            else
            {
                string warn = null;
                if (GltfReader.IsGltf(path)) model = GltfReader.Load(path, out modelMats, out warn);
                else model = ObjParser.Parse(path);
                if (warn != null) notes.Add(warn);
                if (mkey != null)
                {
                    modelCache.RemoveAll(c => c.Key.StartsWith(mkey.Substring(0, mkey.IndexOf('|')) + "|"));   // older versions of this file
                    modelCache.Add(new ParsedModel { Key = mkey, Model = model, Mats = modelMats, Warn = warn });
                    while (modelCache.Count > 3) modelCache.RemoveAt(0);
                }
            }
            Debug.Log("[WorkshopPlus] " + (cached != null ? "model from cache" : "parsed model in " + psw.ElapsedMilliseconds + " ms"));
            currentNotes = notes;
            try { return BuildObject(w, path, textureFile, model, modelMats, notes, out err); }
            finally { currentNotes = null; }
        }

        static GameObject BuildObject(Workshop w, string path, string textureFile, ObjModel model, List<ModelMaterial> modelMats, List<string> notes, out string err)
        {
            err = string.Empty;

            var setItem = w.activeItem;
            texMax = ItemSettings.GetTexSize(setItem);
            try { return BuildObjectWith(w, path, textureFile, model, modelMats, notes, setItem, out err); }
            finally { texMax = 512; }
        }

        static GameObject BuildObjectWith(Workshop w, string path, string textureFile, ObjModel model, List<ModelMaterial> modelMats, List<string> notes, WorkshopItem setItem, out string err)
        {
            err = string.Empty;
            var mats = new List<MaterialSource>();
            var textures = new List<TextureSource>();
            var atlasKeyParts = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Texture2D singleTexture = null; string singleTexturePath = null; bool assignSingle = false;
            string[] userTex = SplitTextures(textureFile);

            Texture2D chosen = null;
            if (userTex.Length == 1)
            {
                Prefetch(userTex);
                chosen = LoadTexture(w, userTex[0]);
                if (chosen == null)
                {   // never show the placeholder: fall back to the model's own materials
                    notes.Add("the chosen texture " + Path.GetFileName(userTex[0]) + " couldn't be used, so the model's own materials are shown (press Clear to remove it)");
                    userTex = new string[0];
                }
            }
            if (userTex.Length == 1)
            {
                // One texture chosen in the UI: same behaviour as the original tool (one texture, original UVs).
                Texture2D t = chosen;

                foreach (var n in model.MaterialNames) mats.Add(new MaterialSource { Name = n, TextureSlot = 0 });
                textures.Add(new TextureSource { Width = 1, Height = 1 });
                singleTexture = t; singleTexturePath = userTex[0]; assignSingle = true;
            }
            else
            {
                var descs = new Dictionary<string, MaterialDesc>();
                if (modelMats == null)
                    foreach (var d in MTLLoader.LoadMaterialDescriptions(path))
                        if (d != null && d.name != null) descs[d.name] = d;
                var embedded = new Dictionary<string, ModelMaterial>();     // glTF images stored inside the file
                lastEmbedded = embedded;

                // texture file per material: from the textures chosen in the UI, or from the MTL file
                var texForMat = new string[model.MaterialNames.Count];
                var mtlTex = new List<string>();
                for (int i = 0; i < model.MaterialNames.Count; i++)
                {
                    if (modelMats != null)
                    {
                        var mm = modelMats[i];
                        if (mm.TexturePath != null) mtlTex.Add(mm.TexturePath);
                        else if (mm.TextureData != null) { string key = "embedded:" + mm.TextureKey; embedded[key] = mm; mtlTex.Add(key); }
                        else
                        {
                            if (mm.TextureName != null) notes.Add("couldn't load the texture " + mm.TextureName);
                            mtlTex.Add(null);
                        }
                        continue;
                    }
                    MaterialDesc d; mtlTex.Add(descs.TryGetValue(model.MaterialNames[i], out d) ? d.diffuseTexture : null);
                }
                {
                    for (int i = 0; i < texForMat.Length; i++)
                    {
                        texForMat[i] = mtlTex[i] != null && mtlTex[i].StartsWith("embedded:") ? mtlTex[i] : ResolveTexture(mtlTex[i], path);
                        if (texForMat[i] == null && !string.IsNullOrEmpty(mtlTex[i]))
                            Debug.Log("[WorkshopPlus] texture not found: " + mtlTex[i]);
                    }
                }

                Prefetch(texForMat);                 // decode DDS/TGA files on all cores at once
                var slotByPath = new Dictionary<string, int>();
                var loaded = new List<Texture2D>(); var loadedPaths = new List<string>();
                var loadedData = new List<byte[]>(); var loadedNames = new List<string>(); var loadedKeys = new List<string>();
                for (int i = 0; i < model.MaterialNames.Count; i++)
                {
                    string n = model.MaterialNames[i];
                    var ms = new MaterialSource { Name = n };
                    MaterialDesc d;
                    if (modelMats != null) { ms.R = modelMats[i].R; ms.G = modelMats[i].G; ms.B = modelMats[i].B; }
                    else if (descs.TryGetValue(n, out d))
                    {
                        Color c = d.diffuseColor;
                        ms.R = (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
                        ms.G = (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
                        ms.B = (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
                    }
                    string tp = texForMat[i];
                    if (tp != null)
                    {
                        int slot;
                        if (!slotByPath.TryGetValue(tp, out slot))
                        {
                            ModelMaterial em;
                            Texture2D t;
                            if (embedded.TryGetValue(tp, out em))
                            {
                                string derr; var ed = em.TextureData;
                                var dd = GetDecoded(EmbeddedKey(path, em), () => ed, null, out derr);
                                if (dd == null) TexNote((em.TextureName ?? "embedded texture") + derr, true);
                                t = dd != null ? TextureFromDecoded(dd, em.TextureName ?? "embedded texture") : null;
                            }
                            else t = LoadTexture(w, tp);
                            if (t != null)
                            {
                                slot = loaded.Count; slotByPath[tp] = slot; loaded.Add(t); loadedPaths.Add(tp);
                                loadedData.Add(em != null ? em.TextureData : null);
                                loadedKeys.Add(em != null ? EmbeddedKey(path, em) : FileKey(tp));
                                loadedNames.Add(em != null ? (em.TextureName ?? "embedded texture") : Path.GetFileName(tp));
                            }
                            else { slot = -1; notes.Add("could not read " + (em != null ? em.TextureName : Path.GetFileName(tp))); }
                        }
                        ms.TextureSlot = slot;
                    }
                    mats.Add(ms);
                }
                bool anyColorOnly = false;
                foreach (var ms in mats) if (ms.TextureSlot < 0) anyColorOnly = true;
                bool facesWithoutMaterial = model.TriMaterial.Contains(-1);
                if (loaded.Count == 1 && !anyColorOnly && !facesWithoutMaterial)
                {
                    singleTexture = loaded[0]; singleTexturePath = loadedPaths[0];
                    // MTL/glTF texture file: remember it like the original tool did (embedded images have no file to remember)
                    assignSingle = userTex.Length == 0 && !singleTexturePath.StartsWith("embedded:");
                    textures.Add(new TextureSource { Width = singleTexture.width, Height = singleTexture.height });
                }
                else
                {   // atlas: work from the full-resolution images, so cropping and By area sizes keep as much detail as possible
                    for (int i = 0; i < loaded.Count; i++)
                    {
                        TextureSource full = null;
                        try
                        {
                            string derr; byte[] raw = loadedData[i]; string lp = loadedPaths[i];
                            bool emb = lp.StartsWith("embedded:");
                            var dd = GetDecoded(loadedKeys[i], () => raw ?? File.ReadAllBytes(lp), emb ? null : lp, out derr);
                            if (dd != null) full = new TextureSource { Width = dd.W, Height = dd.H, Rgba = dd.Px };
                        }
                        catch (Exception) { }
                        atlasKeyParts.Add(loadedKeys[i] ?? loadedPaths[i]);
                        if (full == null) full = ToSource(loaded[i]);
                        full.Name = loadedNames[i];
                        textures.Add(full);
                        UnityEngine.Object.Destroy(loaded[i]);
                    }
                }
            }

            // faces before any usemtl get their own grey material
            int noMat = -1;
            if (model.TriMaterial.Contains(-1) && !(singleTexturePath != null))
            {
                noMat = mats.Count; mats.Add(new MaterialSource { Name = "(none)" });
            }

            AtlasMode atlasMode = ItemSettings.GetAtlas(setItem);
            bool crop = ItemSettings.GetCrop(setItem);
            ShowAtlas(atlasMode, crop, texMax);
            AtlasOptions opts = AtlasStats.Measure(model, mats, textures.Count);
            opts.Mode = atlasMode; opts.Crop = crop;

            // UV cropping with a single texture: replace it with just the part the UVs use (tiling is lost)
            float[] singleWindow = null;
            if (crop && singleTexture != null && textures.Count == 1 && opts.Windows[0] != null)
            {
                TextureSource full = LoadFullSource(path, singleTexturePath, embeddedFor(singleTexturePath));
                if (full != null)
                {
                    float[] used;
                    var part = Atlas.CropToWindow(full, opts.Windows[0], out used);
                    if (part != full)
                    {
                        int size = SupportedSize(Math.Min(Math.Max(part.Width, part.Height), texMax));
                        var sq = Atlas.Resize(part, size, size);
                        UnityEngine.Object.Destroy(singleTexture);
                        singleTexture = ToTexture(sq, size);
                        singleWindow = used;
                        notes.Add("UV cropping uses " + Mathf.RoundToInt(100f * (used[2] - used[0]) * (used[3] - used[1])) + "% of " + Path.GetFileName(singleTexturePath) + ", now " + size + "px");
                    }
                }
            }

            long tTextures = sw.ElapsedMilliseconds;
            AtlasResult atlas = null;
            string atlasKey = null;
            if (atlasKeyParts.Count == textures.Count && textures.Count > 0 && singleTexture == null)
            {
                var kb = new System.Text.StringBuilder();
                kb.Append((int)opts.Mode).Append('|').Append(opts.Crop ? 1 : 0).Append('|').Append(texMax);
                foreach (var p in atlasKeyParts) kb.Append('|').Append(p);
                foreach (var ms in mats) kb.Append('|').Append(ms.TextureSlot).Append(',').Append(ms.R).Append(',').Append(ms.G).Append(',').Append(ms.B);
                if (opts.Mode == AtlasMode.ByArea || opts.Crop)
                    for (int i = 0; i < textures.Count; i++)
                        kb.Append('|').Append(opts.Weights[i].ToString("R")).Append(',').Append(string.Join(",", Array.ConvertAll(opts.Windows[i], f => f.ToString("R"))));
                atlasKey = kb.ToString();
                atlasCache.TryGetValue(atlasKey, out atlas);
            }
            if (atlas == null)
            {
                atlas = Atlas.Build(mats, textures, texMax, 4, opts);
                if (atlasKey != null && atlas.Rgba != null)
                {
                    atlasCache[atlasKey] = atlas; atlasOrder.Add(atlasKey);
                    while (atlasOrder.Count > 6) { atlasCache.Remove(atlasOrder[0]); atlasOrder.RemoveAt(0); }
                }
            }
            long tAtlas = sw.ElapsedMilliseconds;
            if (singleWindow != null)
            {
                var cr = new UvRect { U0 = 0, V0 = 0, U1 = 1, V1 = 1, SU0 = singleWindow[0], SV0 = singleWindow[1], SU1 = singleWindow[2], SV1 = singleWindow[3] };
                for (int i = 0; i < atlas.Rects.Length; i++) if (atlas.Rects[i] != null && atlas.Rects[i].Tile) atlas.Rects[i] = cr;
            }
            UvRect noRect = noMat >= 0 ? atlas.Rects[noMat]
                          : singleWindow != null ? atlas.Rects.Length > 0 ? atlas.Rects[0] : new UvRect { U0 = 0, V0 = 0, U1 = 1, V1 = 1 }
                          : new UvRect { U0 = 0, V0 = 0, U1 = 1, V1 = 1, Tile = true };
            lastFlatModel = lastModel; lastFlatRects = lastRects; lastFlatNoRect = lastNoRect;     // what the Flat check (if any) was built from
            lastModel = model; lastRects = atlas.Rects; lastNoRect = noRect; lastPath = path; lastFlatCount = -1;
            Shading shading = ItemSettings.Get(setItem);
            float angle = ItemSettings.GetAngle(setItem);
            ShowShading(shading, angle);
            BuiltMesh b = shading == Shading.Flat && lastFlatMesh != null && lastFlatModel == model && SameRects(lastFlatRects, atlas.Rects) && SameRect(lastFlatNoRect, noRect)
                        ? lastFlatMesh : MeshBuilder.Build(model, atlas.Rects, noRect, shading, angle);
            lastFlatMesh = null;
            long tMesh = sw.ElapsedMilliseconds;
            VertexCounter.SetExact(path, (int)shading, angle, textureFile, crop, b.VertexCount);

            if (b.VertexCount > VertexLimit)
            {
                string hint = shading == Shading.Flat ? " Flat shading needs extra vertices; try Auto-Smooth or Smooth."
                            : shading == Shading.AutoSmooth ? " Try a larger Auto-Smooth angle." : "";
                if (singleTexture != null) UnityEngine.Object.Destroy(singleTexture);
                err = "the model has " + b.VertexCount.ToString("N0") + " vertices; the game's limit is "
                      + VertexLimit.ToString("N0") + ". Reduce it in Blender (Decimate modifier) or split it into parts." + hint;
                return null;
            }

            var verts = new Vector3[b.VertexCount]; var uvs = new Vector2[b.VertexCount];
            for (int i = 0; i < b.VertexCount; i++)
            {
                verts[i] = new Vector3(b.Positions[i * 3], b.Positions[i * 3 + 1], b.Positions[i * 3 + 2]);
                uvs[i] = new Vector2(b.Uvs[i * 2], b.Uvs[i * 2 + 1]);
            }
            var mesh = new Mesh();
            mesh.name = Path.GetFileNameWithoutExtension(path);
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = b.Triangles;
            var nrm = new Vector3[b.VertexCount];
            for (int i = 0; i < b.VertexCount; i++) nrm[i] = new Vector3(b.Normals[i * 3], b.Normals[i * 3 + 1], b.Normals[i * 3 + 2]);
            mesh.normals = nrm;
            mesh.RecalculateBounds();

            // texture
            if (singleTexturePath != null)
            {
                SetMainTexture(w, w.defaultTexture);
                if (singleTexture != null)
                {
                    if (assignSingle) w.AssignTexture(singleTexturePath, singleTexture);
                    else w.defaultMaterial.mainTexture = singleTexture;
                }
            }
            else SetMainTexture(w, ToTexture(atlas.Rgba, atlas.Size));

            int texCount = 0; foreach (var ms in mats) if (ms.TextureSlot >= 0) texCount = Math.Max(texCount, ms.TextureSlot + 1);
            if (atlas.Rgba != null && texCount > 0)
            {
                int k = (int)Math.Ceiling(Math.Sqrt(texCount + (mats.Exists(m => m.TextureSlot < 0) ? 1 : 0)));
                if (atlas.Layout != null) notes.Add(texCount + " textures share one " + atlas.Size + "px texture by area: " + atlas.Layout);
                else if (k > 1) notes.Add(texCount + " texture" + (texCount == 1 ? "" : "s") + " share one " + atlas.Size + "px texture, so each gets about " + (atlas.Size / k - 8) + "px" + (crop ? " (cropped to the parts the UVs use)" : ""));
            }
            if (b.ClampedUvs > 0)
                notes.Add(b.ClampedUvs + " UVs used texture tiling, which can't be combined with other materials, so they were clamped");
            // texture size / atlas notes only when the model or texture is being (re)imported, not on every rebuild
            bool importing = Importing;
            notes = notes.FindAll(n => importing || alwaysNotes.Contains(n));
            if (notes.Count > 0) w.SetErrorMessage("Note: " + string.Join("; ", notes.ToArray()) + ".");

            Debug.Log("[WorkshopPlus " + Version + "] " + path + ": " + b.VertexCount + " vertices, "
                      + model.TriangleCount + " triangles, shading " + b.Used + (b.Used == Shading.AutoSmooth ? " " + angle + " deg" : "") + ", " + mats.Count + " material(s), texture " + atlas.Size + "px");

            Debug.Log("[WorkshopPlus] build times: textures " + tTextures + " ms, atlas " + (tAtlas - tTextures) + " ms, mesh " + (tMesh - tAtlas) + " ms, Unity mesh/texture " + (sw.ElapsedMilliseconds - tMesh) + " ms");
            var go = new GameObject();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = w.defaultMaterial;
            return go;
        }

        // ---------------------------------------------------------------- faster start-up (replaces Workshop.Start)
        // The original asked Steam about every published item one at a time, inside Start(), before the window was
        // drawn. This shows the window straight away and fetches the item details in parallel.
        public static void Start(Workshop w)
        {
            w.objectItems = new List<WorkshopItem>();
            w.worldItems = new List<WorkshopItem>();
            w.defaultMaterial.mainTexture = w.defaultTexture;
            w.objectTabActive = true;
            w.activeItems = w.objectItems;
            Init(w);
            WorkshopList.Init(w);
            SteamStatus.Init(w);
            Debug.Log("[WorkshopPlus " + Version + "] started");
            ThumbPicker.Init(w);
            try { InitAtlasUi(w); } catch (Exception e) { Debug.Log("[WorkshopPlus] atlas options: " + e); }
            try { AddStockTooltips(w); } catch (Exception e) { Debug.Log("[WorkshopPlus] tooltips: " + e.Message); }
            DescScroll.Setup(w);
            AddRunGameButton(w);
            w.ActivateListView();
            w.StartCoroutine(LoadItemsAsync(w));
        }

        const int MaxInFlight = 32;

        static IEnumerator LoadItemsAsync(Workshop w)
        {
            Text title = null;
            Transform tt = w.message != null ? w.message.transform.Find("Title") : null;
            if (tt != null) title = tt.GetComponent<Text>();
            string oldTitle = title != null ? title.text : null, oldDesc = w.messageDescription.text;
            GameObject arrow = null;
            Transform at = w.message != null ? w.message.transform.Find("Arrow") : null;
            if (at != null) arrow = at.gameObject;
            bool arrowWasActive = arrow != null && arrow.activeSelf;
            if (arrow != null) arrow.SetActive(false);
            if (title != null) title.text = "Loading...";
            w.messageDescription.text = "Getting your Workshop items from Steam.";
            yield return null; yield return null;          // let the window draw first

            var ids = new List<PublishedFileId_t>();
            var details = new List<RemoteStorageGetPublishedFileDetailsResult_t>();
            int failed = 0;
            bool steamOk = SteamManager.Initialized;
            if (steamOk)
            {
                // 1) item IDs, 50 per page (as before)
                int total = -1;
                while (total < 0 || ids.Count < total)
                {
                    bool done = false, ok = false; int returned = 0;
                    var cr = CallResult<RemoteStorageEnumerateUserPublishedFilesResult_t>.Create(
                        delegate (RemoteStorageEnumerateUserPublishedFilesResult_t r, bool io)
                        {
                            if (!io && r.m_eResult == EResult.k_EResultOK)
                            {
                                total = r.m_nTotalResultCount; returned = r.m_nResultsReturned; ok = true;
                                for (int i = 0; i < r.m_nResultsReturned; i++) ids.Add(r.m_rgPublishedFileId[i]);
                            }
                            else Debug.Log("[WorkshopPlus] Steam couldn't list published items (" + r.m_eResult + ")");
                            done = true;
                        });
                    cr.Set(SteamRemoteStorage.EnumerateUserPublishedFiles((uint)ids.Count));
                    var sw = Stopwatch.StartNew();
                    while (!done && sw.Elapsed.TotalSeconds < 20) { SteamAPI.RunCallbacks(); yield return null; }
                    cr.Dispose();
                    if (!ok || returned == 0) { if (!ok) steamOk = false; break; }
                    w.messageDescription.text = "Getting your Workshop items from Steam (" + ids.Count + " found).";
                }

                // 2) details for every item, many requests at once
                var results = new RemoteStorageGetPublishedFileDetailsResult_t[ids.Count];
                var got = new bool[ids.Count];
                var calls = new List<CallResult<RemoteStorageGetPublishedFileDetailsResult_t>>();
                int next = 0, pending = 0, completed = 0;
                var timer = Stopwatch.StartNew();
                while (completed < ids.Count && timer.Elapsed.TotalSeconds < 60)
                {
                    while (next < ids.Count && pending < MaxInFlight)
                    {
                        int idx = next++;
                        var c = CallResult<RemoteStorageGetPublishedFileDetailsResult_t>.Create(
                            delegate (RemoteStorageGetPublishedFileDetailsResult_t r, bool io)
                            {
                                if (!io && r.m_eResult == EResult.k_EResultOK) { results[idx] = r; got[idx] = true; }
                                else Debug.Log("[WorkshopPlus] Steam couldn't load item " + ids[idx] + " (" + r.m_eResult + ")");
                                pending--; completed++;
                            });
                        c.Set(SteamRemoteStorage.GetPublishedFileDetails(ids[idx], 0u));
                        calls.Add(c); pending++;
                    }
                    SteamAPI.RunCallbacks();
                    w.messageDescription.text = "Getting your Workshop items from Steam (" + completed + " of " + ids.Count + ").";
                    yield return null;
                }
                foreach (var c in calls) c.Dispose();
                for (int i = 0; i < ids.Count; i++) { if (got[i]) details.Add(results[i]); else failed++; }
                Debug.Log("[WorkshopPlus] loaded " + details.Count + " of " + ids.Count + " published items in " + timer.Elapsed.TotalSeconds.ToString("0.0") + "s");
            }

            // 3) build the list exactly like Workshop.LoadItems did
            if (title != null) title.text = oldTitle;
            w.messageDescription.text = oldDesc;
            if (arrow != null) arrow.SetActive(arrowWasActive);
            if (!steamOk || !SteamManager.Initialized)
            {
                Debug.Log("[WorkshopPlus] couldn't get published items from Steam");
                SteamStatus.ItemsLoaded(w, false);
                yield break;
            }
            Dictionary<ulong, WorkshopMetadata> meta = w.LoadMetadata();
            var objs = new List<WorkshopItem>(); var worlds = new List<WorkshopItem>();
            foreach (var d in details)
            {
                var item = new WorkshopItem();
                item.id = d.m_nPublishedFileId.m_PublishedFileId;
                WorkshopList.SetUpdated(item.id, d.m_rtimeUpdated);
                Thumbnails.SetSource(item.id, d.m_hPreviewFile, d.m_rtimeUpdated);
                item.title = d.m_rgchTitle;
                item.description = d.m_rgchDescription;
                item.rotation = Quaternion.identity;
                item.scale = 1f;
                item.uniqueDirectory = (d.m_pchFileName ?? "").Replace(".gz", string.Empty);
                switch (d.m_eVisibility)
                {
                    case ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate: item.visibility = 0; break;
                    case ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly: item.visibility = 1; break;
                    case ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic: item.visibility = 2; break;
                }
                WorkshopMetadata m;
                if (meta.TryGetValue(item.id, out m))
                {
                    item.rotation = m.rotation; item.scale = m.scale; item.sourceFile = m.sourceFile; item.textureFile = m.textureFile;
                }
                if (item.uniqueDirectory.Contains("_world")) worlds.Add(item); else objs.Add(item);
            }
            // keep anything created while loading, after the published items
            w.objectItems.InsertRange(0, objs);
            w.worldItems.InsertRange(0, worlds);
            if (failed > 0)
                w.SetErrorMessage(failed + " published item" + (failed == 1 ? "" : "s") + " couldn't be loaded from Steam. Restart to try again.");
            if (w.listView.activeSelf) { w.ActivateListView(); w.tableView.ReloadData(); }
            WorkshopList.PrefetchCounts(w);
            Thumbnails.StartPump(w);
            SteamStatus.ItemsLoaded(w, true);
        }

        /// after reconnecting to Steam
        public static void ReloadItems(Workshop w) { w.StartCoroutine(LoadItemsAsync(w)); }

        static Button flipButton; static Text flipText; static Color flipOnColor, flipOnTextColor; static bool? flipShown;
        static readonly Color FlipOffColor = new Color(0.92f, 0.92f, 0.92f, 1f), FlipOffTextColor = new Color(0.2f, 0.2f, 0.2f, 1f);

        // x (from the right edge of the bar) where the "Scale:" text starts, so Flip X never covers it
        static float ScaleTextLeft(Transform contents)
        {
            float left = -290f;
            try
            {
                Transform lt = contents.Find("ScaleLabel"), ft = contents.Find("ScaleField");
                if (ft != null) { var fr = (RectTransform)ft; left = Mathf.Min(left, fr.anchoredPosition.x - fr.sizeDelta.x * fr.pivot.x); }
                var t = lt != null ? lt.GetComponent<Text>() : null;
                if (t != null)
                {
                    var r = t.rectTransform;
                    float rl = r.anchoredPosition.x - r.sizeDelta.x * r.pivot.x, rr = rl + r.sizeDelta.x, pw = t.preferredWidth;
                    float tl;
                    switch (t.alignment)
                    {
                        case TextAnchor.UpperLeft: case TextAnchor.MiddleLeft: case TextAnchor.LowerLeft: tl = rl; break;
                        case TextAnchor.UpperRight: case TextAnchor.MiddleRight: case TextAnchor.LowerRight: tl = rr - pw; break;
                        default: tl = (rl + rr - pw) * 0.5f; break;
                    }
                    left = Mathf.Min(left, tl);
                }
            }
            catch (Exception) { }
            return left;
        }

        /// shrink a button's label to fit its button instead of wrapping out of sight
        public static void FitButtonText(Text t)
        {
            if (t == null) return;
            var r = t.rectTransform;
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = new Vector2(4f, 2f); r.offsetMax = new Vector2(-4f, -2f);
            int fs = t.resizeTextForBestFit ? Math.Max(t.resizeTextMaxSize, t.fontSize) : t.fontSize;
            t.resizeTextForBestFit = true; t.resizeTextMaxSize = fs; t.resizeTextMinSize = Math.Max(8, fs / 3);
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
        }

        /// a bit bigger text for the small boxes copied into the top bar (search, sort)
        public static void Larger(Text t)
        {
            if (t == null) return;
            t.fontSize = Mathf.RoundToInt(t.fontSize * 1.45f);
            t.verticalOverflow = VerticalWrapMode.Overflow;
        }

        /// "Run Game" just left of the ? (help) button in the top bar
        static void AddRunGameButton(Workshop w)
        {
            try
            {
                var root = w.listView.transform.parent;
                Button help = null;
                foreach (var b in root.GetComponentsInChildren<Button>(true))
                    for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                        if (b.onClick.GetPersistentMethodName(i) == "OnHelpButtonClicked") help = b;
                if (help == null) { Debug.Log("[WorkshopPlus] help button not found, no Run Game button"); return; }
                var hrt = (RectTransform)help.transform;
                var run = (GameObject)UnityEngine.Object.Instantiate(w.publishButton.gameObject, hrt.parent, false);
                run.name = "RunGameButton"; run.SetActive(true);
                var rt = (RectTransform)run.transform;
                rt.anchorMin = hrt.anchorMin; rt.anchorMax = hrt.anchorMax; rt.pivot = new Vector2(1f, hrt.pivot.y);
                rt.sizeDelta = new Vector2(120f, Mathf.Max(30f, Mathf.Min(hrt.rect.height, 42f)));
                float helpLeft = hrt.anchoredPosition.x - hrt.rect.width * hrt.pivot.x;
                rt.anchoredPosition = new Vector2(helpLeft - 8f, hrt.anchoredPosition.y);
                var rb = run.GetComponent<Button>();
                rb.onClick = new Button.ButtonClickedEvent(); rb.interactable = true;
                rb.onClick.AddListener(delegate { RunGame(w); });
                var t = run.GetComponentInChildren<Text>();
                if (t != null) { t.text = "Run Game"; FitButtonText(t); }
                Tooltip.Add(run, "Start Tiny Town VR.");
                WorkshopList.MakeRoomForHeaderButton(rt);
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] Run Game button: " + e.Message); }
        }

        /// starts Tiny Town VR.exe from the game folder (the folder Workshop.exe is in)
        public static void RunGame(Workshop w)
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath).FullName;      // ...\Tiny Town VR
                string exe = Path.Combine(root, "Tiny Town VR.exe");
                if (!File.Exists(exe))
                {
                    exe = null;
                    foreach (var f in Directory.GetFiles(root, "*.exe"))
                    {
                        string n = Path.GetFileName(f).ToLowerInvariant();
                        if (n != "workshop.exe" && !n.Contains("unitycrashhandler")) { exe = f; break; }
                    }
                }
                if (exe == null) { w.SetErrorMessage("Couldn't find Tiny Town VR.exe in " + root + "."); return; }
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = root, UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                w.SetErrorMessage("Note: starting Tiny Town VR...");
            }
            catch (Exception e) { w.SetErrorMessage("Couldn't start the game: " + e.Message); }
        }

        public static void AddStockTooltips(Workshop w)
        {
            var canvas = w.listView.transform.parent;
            foreach (var b in canvas.GetComponentsInChildren<Button>(true))
                for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                {
                    string m = b.onClick.GetPersistentMethodName(i), tip = null;
                    switch (m)
                    {
                        case "OnObjectTabClicked": tip = "Your Workshop items (objects)."; break;
                        case "OnWorldTabClicked": tip = "Your Workshop worlds."; break;
                        case "OnHelpButtonClicked": tip = "Help and notes."; break;
                        case "OnBackButtonClicked": tip = "Back to the list."; break;
                        case "OnRotateXClicked": tip = "Rotate 45\u00B0 around the X axis."; break;
                        case "OnRotateYClicked": tip = "Rotate 45\u00B0 around the Y axis."; break;
                        case "OnRotateZClicked": tip = "Rotate 45\u00B0 around the Z axis."; break;
                        case "OnObjFileBrowseButtonClick": tip = "Pick a different model file (OBJ, glTF or GLB). The item's settings are kept."; break;
                        case "OnTextureBrowseButtonClick": tip = "Use one image (PNG, JPG, DDS or TGA) as the texture for the whole model."; break;
                        case "OnTextureClearButtonClick": tip = "Remove the picked image and go back to the model's own textures and colours."; break;
                        case "OnNewButtonClicked":
                            Tooltip.Add(b.gameObject, () => w.objectTabActive ? "Add a new item from a model file." : "Add a new world from a saved world file.");
                            break;
                        case "OnPublishButtonClicked":
                            Tooltip.Add(b.gameObject, () => w.activeItem != null && w.activeItem.id != 0
                                ? "Update this item on the Steam Workshop." : "Upload this item to the Steam Workshop as a new item.");
                            break;
                    }
                    if (tip != null) Tooltip.Add(b.gameObject, tip);
                }
            var scale = w.footerContents.transform.Find("ScaleField");
            if (scale != null) Tooltip.Add(scale.gameObject, "Size multiplier for the model in the game.");
            if (w.visibilityDropdown != null) Tooltip.Add(w.visibilityDropdown.gameObject, "Who can see this item on the Steam Workshop.");
        }

        static bool IsFlipped(Workshop w) { return w.objectRoot != null && w.objectRoot.localScale.x < 0f; }

        static void ToggleFlip(Workshop w)
        {
            if (w.flipToggle == null || w.objectRoot == null) return;
            bool want = !IsFlipped(w);
            w.flipToggle.isOn = want;
            w.OnFlipToggleChanged();             // also when the tick box already had that value
            UpdateFlipButton(w);
        }

        /// highlighted (Publish colour) while flipped, plain while not; called every frame
        public static void UpdateFlipButton(Workshop w)
        {
            if (flipButton == null) return;
            bool on = IsFlipped(w);
            if (flipShown.HasValue && flipShown.Value == on) return;
            flipShown = on;
            if (flipButton.image != null) flipButton.image.color = on ? flipOnColor : FlipOffColor;
            if (flipText != null) { flipText.text = on ? "Flip X: On" : "Flip X"; flipText.color = on ? flipOnTextColor : FlipOffTextColor; }
        }

        // texture picked with Browse while the item already had one: rebuild the model instead of only
        // swapping the image, so UVs that were laid out for an atlas or colours are redone for this texture
        public static void BrowseAssignTexture(Workshop w, string path, Texture2D texture)
        {
            if (!w.objectTabActive || w.activeItem == null) { w.AssignTexture(path, texture); return; }
            if (texture != null) UnityEngine.Object.Destroy(texture);
            w.activeItem.textureFile = path;
            if (w.textureField != null) w.textureField.text = path;
            w.ActivateDetailView();
        }

        static void ResetRotation(Workshop w)
        {
            if (w.activeItem == null) return;
            w.objectRoot.localRotation = Quaternion.identity;
            w.activeItem.rotation = Quaternion.identity;
            w.UpdateObjectPosition(w.objectRoot);
            w.AddOrUpdateMetadata(w.activeItem);
        }

        // ---------------------------------------------------------------- relocating a moved model
        // Only for an existing entry being opened (its stored path is the one that's missing).
        static string Relocate(Workshop w, string oldPath, ref string textureFile)
        {
            WorkshopItem item = w.activeItem;
            if (item == null || item.sourceFile != oldPath) return null;
            string startDir = Path.GetDirectoryName(oldPath);
            while (!string.IsNullOrEmpty(startDir) && !Directory.Exists(startDir)) startDir = Path.GetDirectoryName(startDir);
            var filters = new SFB.ExtensionFilter[] { new SFB.ExtensionFilter("3D Models (OBJ, glTF, GLB) ", "obj", "gltf", "glb") };
            string[] sel = SFB.StandaloneFileBrowser.OpenFilePanel("Model moved: locate " + Path.GetFileName(oldPath) + " (or a replacement)" + " for \"" + item.title + "\"",
                                                                    startDir ?? "", filters, false);
            if (sel == null || sel.Length == 0 || string.IsNullOrEmpty(sel[0])) return null;
            string newPath = sel[0].StartsWith("file://") ? sel[0].Substring(7) : sel[0];
            if (!File.Exists(newPath)) return null;

            // the chosen texture: keep it if it still exists, otherwise look for it next to the model's new location
            if (!string.IsNullOrEmpty(item.textureFile) && !File.Exists(item.textureFile))
            {
                string found = FindMoved(item.textureFile, Path.GetDirectoryName(oldPath), Path.GetDirectoryName(newPath));
                if (found != null) item.textureFile = found;
            }
            textureFile = item.textureFile;
            ItemSettings.Move(oldPath, newPath);
            item.sourceFile = newPath;
            w.AddOrUpdateMetadata(item);
            Debug.Log("[WorkshopPlus] relocated '" + item.title + "': " + oldPath + " -> " + newPath);
            return newPath;
        }

        // same relative position to the model (e.g. textures\\a.png), or same file name in the model's new folder
        static string FindMoved(string oldFile, string oldDir, string newDir)
        {
            string prefix = oldDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (oldFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string c = Path.Combine(newDir, oldFile.Substring(prefix.Length));
                if (File.Exists(c)) return c;
            }
            string n = Path.Combine(newDir, Path.GetFileName(oldFile));
            return File.Exists(n) ? n : null;
        }

        // ---------------------------------------------------------------- shading dropdown
        static Dropdown shadingDropdown;
        static bool updatingDropdown;

        // called at the end of Workshop.Start
        // ---------------------------------------------------------------- resolution scaling + text size
        const float UiScale = 0.72f;   // everything about 28% smaller than the original 1024x768 layout
        static void ScaleUi(Workshop w)
        {
            try
            {
                var canvas = w.listView.transform.parent.GetComponent<Canvas>();
                if (canvas == null || canvas.renderMode == RenderMode.WorldSpace) return;
                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
                if (scaler.uiScaleMode == CanvasScaler.ScaleMode.ConstantPixelSize)
                {   // the tool was laid out for 1024x768: scale with window height (reference 20% larger = everything 20% smaller)
                    scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    scaler.referenceResolution = new Vector2(1024f / UiScale, 768f / UiScale);
                    scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                    scaler.matchWidthOrHeight = 1f;
                }
                // "Back" labels: let them extend past their box instead of being cut off
                foreach (var bt in w.detailView.GetComponentsInChildren<Button>(true))
                    if (bt.name == "BackButton")
                        foreach (var t in bt.GetComponentsInChildren<Text>(true)) { t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow; }
                if (w.objectLabel != null)
                {
                    w.objectLabel.text = "Model:";
                    // move the model Browse button right so it doesn't cover the longer label
                    var browse = w.objectLabel.transform.parent.Find("ObjectBrowseButton") as RectTransform;
                    if (browse != null)
                    {
                        var lr = w.objectLabel.rectTransform;
                        float labelEnd = lr.anchoredPosition.x - lr.sizeDelta.x * lr.pivot.x + w.objectLabel.preferredWidth;
                        // Browse and Reimport share the space after the label
                        float x0 = Mathf.Max(labelEnd + 6f, 100f), right = 250f - 6f, gap = 4f;
                        float bw = Mathf.Min(browse.sizeDelta.x, (right - x0 - gap) * 0.5f);
                        browse.sizeDelta = new Vector2(bw, browse.sizeDelta.y);
                        browse.anchoredPosition = new Vector2(x0 + bw * browse.pivot.x, browse.anchoredPosition.y);
                        var bt = browse.GetComponentInChildren<Text>(); if (bt != null) FitButtonText(bt);
                        var rGo = (GameObject)UnityEngine.Object.Instantiate(browse.gameObject, browse.parent, false);
                        rGo.name = "ObjectReimportButton";
                        var rrt = (RectTransform)rGo.transform;
                        rrt.anchoredPosition = new Vector2(x0 + bw + gap + bw * rrt.pivot.x, browse.anchoredPosition.y);
                        var rb = rGo.GetComponent<Button>();
                        rb.onClick = new Button.ButtonClickedEvent(); rb.interactable = true;
                        rb.onClick.AddListener(delegate { Reimport(w); });
                        var rtx = rGo.GetComponentInChildren<Text>(); if (rtx != null) { rtx.text = "Reimport"; FitButtonText(rtx); }
                        Tooltip.Add(rGo, "Read the model file again from the same path, picking up any changes (and its textures). If it can't be found or read, the current model stays.");
                        reimportButton = rGo;
                    }
                }

                // window title: "Workshop PLUS" with the version a little smaller after it
                var titleT = w.listView.transform.parent.Find("Header/Text");
                var title = titleT != null ? titleT.GetComponent<Text>() : null;
                if (title != null && title.text.Trim() == "Workshop")
                {
                    int fs = title.fontSize;
                    if (title.resizeTextForBestFit)
                    {
                        Canvas.ForceUpdateCanvases();
                        int used = title.cachedTextGenerator.fontSizeUsedForBestFit;
                        if (used > 0) fs = used;
                        title.resizeTextForBestFit = false; title.fontSize = fs;
                    }
                    title.supportRichText = true;
                    title.horizontalOverflow = HorizontalWrapMode.Overflow;
                    title.text = "Workshop PLUS <size=" + Mathf.Max(8, Mathf.RoundToInt(fs * 0.55f)) + ">v" + Version + "</size>";
                }
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] UI scaling: " + e.Message); }
        }

        // the 3D preview is rendered to a texture sized in canvas units; with a scaled canvas, render at the real pixel size
        public static void KeepPreviewSharp(Workshop w)
        {
            if (w.rttCamera == null || w.rttImage == null || !w.detailView.activeInHierarchy) return;
            var canvas = w.rttImage.canvas; if (canvas == null) return;
            Rect r = w.rttImage.rectTransform.rect;                          // the area the preview is actually shown in (keeps its shape)
            int pw = Mathf.RoundToInt(r.width * canvas.scaleFactor), ph = Mathf.RoundToInt(r.height * canvas.scaleFactor);
            if (pw < 16 || ph < 16) return;
            var cur = w.rttCamera.targetTexture;
            if (cur != null && Mathf.Abs(cur.width - pw) <= 2 && Mathf.Abs(cur.height - ph) <= 2)
            {
                float want = (float)cur.width / cur.height;
                if (Mathf.Abs(w.rttCamera.aspect - want) > 0.001f) w.rttCamera.aspect = want;   // camera shape = picture shape
                return;
            }
            var rt = new RenderTexture(pw, ph, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            rt.antiAliasing = 4;
            w.rttCamera.targetTexture = rt;
            w.rttCamera.ResetAspect();
            w.rttCamera.aspect = (float)pw / ph;
            w.rttImage.texture = rt;
            if (cur != null) { cur.Release(); UnityEngine.Object.Destroy(cur); }
        }

        public static void Init(Workshop w)
        {
            ScaleUi(w);
            try { Thumbnails.Init(FileUtils.GetWorkshopDevelopmentDirectory()); } catch (Exception) { }
            try
            {
                Transform contents = w.footerContents.transform;      // bottom bar of the object editor (hidden for worlds)
                Transform flipLabel = contents.Find("FlipLabel");

                var labelGo = (GameObject)UnityEngine.Object.Instantiate(flipLabel.gameObject, contents, false);
                labelGo.name = "ShadingLabel";
                var text = labelGo.GetComponent<Text>();
                text.text = "Shading:";
                text.alignment = TextAnchor.MiddleLeft;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                var lrt = (RectTransform)labelGo.transform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 0.5f);
                lrt.pivot = new Vector2(0f, 0.5f);
                lrt.sizeDelta = new Vector2(Mathf.Ceil(text.preferredWidth) + 4f, 40f);
                lrt.anchoredPosition = new Vector2(12f, 0f);

                var ddGo = (GameObject)UnityEngine.Object.Instantiate(w.visibilityDropdown.gameObject, contents, false);
                ddGo.name = "ShadingDropdown";
                var drt = (RectTransform)ddGo.transform;
                drt.anchorMin = drt.anchorMax = new Vector2(0f, 0.5f);
                drt.pivot = new Vector2(0f, 0.5f);
                drt.anchoredPosition = new Vector2(12f + lrt.sizeDelta.x + 6f, 0f);
                shadingDropdown = ddGo.GetComponent<Dropdown>();
                shadingDropdown.onValueChanged = new Dropdown.DropdownEvent();   // drop the copied Visibility handler
                shadingDropdown.ClearOptions();
                shadingDropdown.AddOptions(new List<string> { "Imported", "Smooth", "Flat", "Auto-Smooth" });
                shadingDropdown.value = 0;
                shadingDropdown.RefreshShownValue();
                shadingDropdown.onValueChanged.AddListener(delegate (int i) { OnShadingChanged(w, i); });
                Tooltip.Add(ddGo, "How the surface is lit. Imported: the file's own normals. Smooth: everything smooth. Flat: every face flat (more vertices). Auto-Smooth: hard edges above the angle, smooth below.");
                drt.sizeDelta = new Vector2(Mathf.Max(drt.sizeDelta.x, 130f), drt.sizeDelta.y);   // room for "Auto-Smooth"

                // angle box (copy of the Scale field), only shown for Auto-Smooth
                var srcField = contents.Find("ScaleField");
                var fGo = (GameObject)UnityEngine.Object.Instantiate(srcField.gameObject, contents, false);
                fGo.name = "ShadingAngleField";
                var frt = (RectTransform)fGo.transform;
                frt.anchorMin = frt.anchorMax = new Vector2(0f, 0.5f);
                frt.pivot = new Vector2(0f, 0.5f);
                frt.sizeDelta = new Vector2(52f, drt.sizeDelta.y);
                frt.anchoredPosition = new Vector2(drt.anchoredPosition.x + drt.sizeDelta.x + 6f, 0f);
                angleField = fGo.GetComponent<InputField>();
                angleField.onValueChanged = new InputField.OnChangeEvent();     // drop the copied Scale handlers
                angleField.onEndEdit = new InputField.SubmitEvent();
                angleField.contentType = InputField.ContentType.DecimalNumber;
                angleField.characterLimit = 5;
                angleField.text = "30";
                angleField.onEndEdit.AddListener(delegate (string v) { OnAngleChanged(w, v); });
                Tooltip.Add(fGo, "Auto-Smooth angle: edges sharper than this stay hard. Press Enter to apply.");

                var degGo = (GameObject)UnityEngine.Object.Instantiate(labelGo, contents, false);
                degGo.name = "ShadingAngleUnit";
                degGo.GetComponent<Text>().text = "\u00B0";
                var urt = (RectTransform)degGo.transform;
                urt.sizeDelta = new Vector2(16f, 40f);
                urt.anchoredPosition = new Vector2(frt.anchoredPosition.x + frt.sizeDelta.x + 2f, 0f);
                angleWidgets = new GameObject[] { fGo, degGo };
                SetAngleVisible(false);

                // "Flip X" toggle button in place of the Flip tick box + label
                Transform flipToggleT = contents.Find("FlipToggle");
                RectTransform flipBtnRect = null;
                if (flipToggleT != null && w.publishButton != null)
                {
                    var fbGo = (GameObject)UnityEngine.Object.Instantiate(w.publishButton.gameObject, contents, false);
                    fbGo.name = "FlipXButton";
                    fbGo.SetActive(true);
                    flipBtnRect = (RectTransform)fbGo.transform;
                    flipBtnRect.anchorMin = flipBtnRect.anchorMax = new Vector2(1f, 0.5f);
                    flipBtnRect.pivot = new Vector2(1f, 0.5f);
                    flipBtnRect.sizeDelta = new Vector2(95f, 35f);
                    flipBtnRect.anchoredPosition = new Vector2(ScaleTextLeft(contents) - 10f, 0f);     // just left of "Scale:"
                    flipButton = fbGo.GetComponent<Button>();
                    flipButton.onClick = new Button.ButtonClickedEvent();
                    flipButton.interactable = true;
                    flipButton.onClick.AddListener(delegate { ToggleFlip(w); });
                    Tooltip.Add(fbGo, "Mirror the model left to right.");
                    flipOnColor = flipButton.image != null ? flipButton.image.color : new Color(0.3f, 0.7f, 0.3f);
                    flipText = fbGo.GetComponentInChildren<Text>();
                    if (flipText != null)
                    {
                        flipText.text = "Flip X";
                        int fs = flipText.fontSize;
                        flipText.resizeTextForBestFit = true; flipText.resizeTextMaxSize = fs; flipText.resizeTextMinSize = Math.Max(8, fs / 2);
                        flipOnTextColor = flipText.color;
                    }
                    flipToggleT.gameObject.SetActive(false);
                    if (flipLabel != null) flipLabel.gameObject.SetActive(false);
                    flipShown = null;
                    UpdateFlipButton(w);
                }

                // "Reset rotation" just left of Flip X: back to the model's original orientation
                if (flipBtnRect != null)
                {
                    var rGo = (GameObject)UnityEngine.Object.Instantiate(w.publishButton.gameObject, contents, false);
                    rGo.name = "ResetRotationButton";
                    rGo.SetActive(true);
                    var rrt = (RectTransform)rGo.transform;
                    rrt.anchorMin = rrt.anchorMax = new Vector2(1f, 0.5f);
                    rrt.pivot = new Vector2(1f, 0.5f);
                    rrt.sizeDelta = new Vector2(125f, 35f);
                    rrt.anchoredPosition = new Vector2(flipBtnRect.anchoredPosition.x - flipBtnRect.sizeDelta.x - 8f, 0f);
                    resetRect = rrt;
                    var rb = rGo.GetComponent<Button>();
                    rb.onClick = new Button.ButtonClickedEvent();          // drop the copied Publish handler
                    rb.interactable = true;
                    rb.onClick.AddListener(delegate { ResetRotation(w); });
                    Tooltip.Add(rGo, "Undo all X, Y and Z rotations and go back to the model's original orientation.");
                    var rtx = rGo.GetComponentInChildren<Text>();
                    if (rtx != null)
                    {
                        rtx.text = "Reset rotation";
                        int fs = rtx.fontSize;
                        rtx.resizeTextForBestFit = true; rtx.resizeTextMaxSize = fs; rtx.resizeTextMinSize = Math.Max(8, fs / 2);
                    }
                }

                // explanation shown when Flat would go over the vertex limit
                var warnGo = (GameObject)UnityEngine.Object.Instantiate(labelGo, contents, false);
                warnGo.name = "ShadingFlatWarning";
                flatWarning = warnGo.GetComponent<Text>();
                flatWarning.color = ErrorRed;
                flatWarning.horizontalOverflow = HorizontalWrapMode.Overflow;
                flatWarnRect = (RectTransform)warnGo.transform;
                flatWarnRect.sizeDelta = new Vector2(300f, 40f);
                flatWarnX = frt.anchoredPosition.x;
                warnGo.SetActive(false);

                // error line: wrap onto more lines (upwards) instead of being cut off
                Text et = w.errorMessageText;
                if (et != null)
                {
                    var ert = et.rectTransform;
                    ert.anchorMin = new Vector2(0f, 0f); ert.anchorMax = new Vector2(1f, 0f); ert.pivot = new Vector2(0f, 0f);
                    ert.offsetMin = new Vector2(10f, 65f);
                    ert.offsetMax = new Vector2(-260f, 95f);
                    et.horizontalOverflow = HorizontalWrapMode.Wrap;
                    et.verticalOverflow = VerticalWrapMode.Overflow;
                    et.alignment = TextAnchor.LowerLeft;
                    et.resizeTextForBestFit = false;
                    et.color = ErrorRed;
                }

                // permanent hint in the (empty) texture field
                if (w.textureField != null && w.textureField.placeholder is Text)
                    ((Text)w.textureField.placeholder).text = "PNG, JPG, DDS or TGA, max 512px";
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] could not add shading option: " + e); }
        }

        static InputField angleField;
        static GameObject[] angleWidgets;

        static void SetAngleVisible(bool on)
        {
            if (angleWidgets == null) return;
            foreach (var g in angleWidgets) if (g != null) g.SetActive(on);
        }

        // ---------------------------------------------------------------- atlas options (above the Texture field)
        static Dropdown atlasDropdown, texSizeDropdown; static Toggle cropToggle; static GameObject[] atlasWidgets; static bool updatingAtlas;

        public static void InitAtlasUi(Workshop w)
        {
            var sidebar = (RectTransform)w.titleField.transform.parent;
            var texLabel = sidebar.Find("TextureLabel") as RectTransform;
            if (texLabel == null || w.visibilityDropdown == null) return;
            const float CropY = 44f, Shift = 90f;          // crop row below the Atlas row; how far Texture/Visibility move down
            float ty = texLabel.anchoredPosition.y;
            var visLabel = sidebar.Find("VisibilityLabel") as RectTransform;
            float ddOffset = visLabel != null ? ((RectTransform)w.visibilityDropdown.transform).anchoredPosition.y - visLabel.anchoredPosition.y : 32f;
            // move the Texture and Visibility rows down (by name: their pivots differ, so positions can't be compared directly)
            foreach (var n in new[] { "TextureLabel", "TextureBrowseButton", "TextureClearButton", "TextureField", "VisibilityLabel", "VisibilityDropdown" })
            {
                var r = sidebar.Find(n) as RectTransform;
                if (r != null) r.anchoredPosition -= new Vector2(0f, Shift);
            }
            var widgets = new List<GameObject>();

            var lGo = (GameObject)UnityEngine.Object.Instantiate(texLabel.gameObject, sidebar, false);
            lGo.name = "AtlasLabel";
            ((RectTransform)lGo.transform).anchoredPosition = new Vector2(texLabel.anchoredPosition.x, ty);
            var lt = lGo.GetComponent<Text>(); lt.text = "Atlas:";
            widgets.Add(lGo);

            var vis = (RectTransform)w.visibilityDropdown.transform;
            var dGo = (GameObject)UnityEngine.Object.Instantiate(w.visibilityDropdown.gameObject, sidebar, false);
            dGo.name = "AtlasDropdown";
            var drt = (RectTransform)dGo.transform;
            drt.anchoredPosition = new Vector2(vis.anchoredPosition.x, ty + ddOffset);   // same place relative to its label as Visibility's
            drt.sizeDelta = new Vector2(Mathf.Max(vis.sizeDelta.x, 120f), vis.sizeDelta.y);
            atlasDropdown = dGo.GetComponent<Dropdown>();
            atlasDropdown.onValueChanged = new Dropdown.DropdownEvent();
            atlasDropdown.ClearOptions();
            atlasDropdown.AddOptions(new List<string> { "Equal", "By area" });
            atlasDropdown.value = 0; atlasDropdown.RefreshShownValue();
            atlasDropdown.onValueChanged.AddListener(delegate (int i)
            {
                if (updatingAtlas || w.activeItem == null || string.IsNullOrEmpty(w.activeItem.sourceFile)) return;
                ItemSettings.SetAtlas(w.activeItem, (AtlasMode)i);
                MarkImport(); w.ActivateDetailView();
            });
            Tooltip.Add(dGo, "How several textures share the item's one 512px texture. Equal: the same space each. By area: space in proportion to how much of the model's surface uses each texture, so every part ends up about equally sharp. Only matters when the model uses more than one texture.");
            widgets.Add(dGo);

            var flip = w.footerContents.transform.Find("FlipToggle");
            if (flip != null)
            {
                var tGo = (GameObject)UnityEngine.Object.Instantiate(flip.gameObject, sidebar, false);
                tGo.name = "CropToggle"; tGo.SetActive(true);
                var trt = (RectTransform)tGo.transform;
                trt.anchorMin = trt.anchorMax = new Vector2(0f, 1f); trt.pivot = new Vector2(0.5f, 0.5f);
                trt.sizeDelta = new Vector2(20f, 20f); trt.anchoredPosition = new Vector2(25f, ty + ddOffset - CropY);
                cropToggle = tGo.GetComponent<Toggle>();
                cropToggle.onValueChanged = new Toggle.ToggleEvent();
                cropToggle.isOn = false;
                cropToggle.onValueChanged.AddListener(delegate (bool on)
                {
                    if (updatingAtlas || w.activeItem == null || string.IsNullOrEmpty(w.activeItem.sourceFile)) return;
                    ItemSettings.SetCrop(w.activeItem, on);
                    MarkImport(); w.ActivateDetailView();
                    if (on) SetMessage(w, "Note: UV cropping is on. Textures that tile (repeat) will be clamped instead of repeating; turn it off for models that rely on tiling.");
                });
                const string tip = "Keep only the part of each texture the model's UVs actually use, so it gets more of the 512px space. May break tiling textures: anything that repeats is clamped instead.";
                Tooltip.Add(tGo, tip);
                widgets.Add(tGo);

                var cGo = (GameObject)UnityEngine.Object.Instantiate(texLabel.gameObject, sidebar, false);
                cGo.name = "CropLabel";
                var crt = (RectTransform)cGo.transform;
                crt.anchorMin = crt.anchorMax = new Vector2(0f, 1f); crt.pivot = new Vector2(0f, 0.5f);
                crt.sizeDelta = new Vector2(122f, 40f); crt.anchoredPosition = new Vector2(44f, ty + ddOffset - CropY);
                var ct = cGo.GetComponent<Text>();
                int small = Mathf.Max(10, Mathf.RoundToInt(ct.fontSize * 0.55f));
                ct.fontSize = small;
                ct.supportRichText = true;
                ct.alignment = TextAnchor.MiddleLeft;
                ct.horizontalOverflow = HorizontalWrapMode.Wrap; ct.verticalOverflow = VerticalWrapMode.Overflow;
                ct.lineSpacing = 0.9f;
                ct.text = "Crop to used UVs\n<size=" + Mathf.Max(9, Mathf.RoundToInt(small * 0.8f)) + "><color=#FF6A00>May break tiling</color></size>";
                var cb = cGo.AddComponent<Button>(); cb.transition = Selectable.Transition.None;
                cb.onClick.AddListener(delegate { cropToggle.isOn = !cropToggle.isOn; });
                Tooltip.Add(cGo, tip);
                widgets.Add(cGo);

                // "Export" on the same row: save the item's current texture (the atlas) as a PNG
                var eGo = (GameObject)UnityEngine.Object.Instantiate(w.publishButton.gameObject, sidebar, false);
                eGo.name = "ExportAtlasButton"; eGo.SetActive(true);
                var ert = (RectTransform)eGo.transform;
                ert.anchorMin = ert.anchorMax = new Vector2(0f, 1f); ert.pivot = new Vector2(1f, 0.5f);
                ert.sizeDelta = new Vector2(72f, 30f); ert.anchoredPosition = new Vector2(250f - 6f, ty + ddOffset - CropY);
                var eb = eGo.GetComponent<Button>();
                eb.onClick = new Button.ButtonClickedEvent(); eb.interactable = true;
                eb.onClick.AddListener(delegate { ExportAtlas(w); });
                var etx = eGo.GetComponentInChildren<Text>(); if (etx != null) { etx.text = "Export"; FitButtonText(etx); }
                Tooltip.Add(eGo, "Save the item's texture (the atlas, exactly as it will be uploaded) as a PNG file on your PC.");
                widgets.Add(eGo);
            }
            // "Size:" row under the Texture box: 512 / 256 / 128 for the item's one texture (atlas or picked image)
            var texField = sidebar.Find("TextureField") as RectTransform;
            if (texField != null && visLabel != null)
            {
                const float SizeRow = 44f;
                foreach (var n in new[] { "VisibilityLabel", "VisibilityDropdown" })
                {
                    var r = sidebar.Find(n) as RectTransform;
                    if (r != null) r.anchoredPosition -= new Vector2(0f, SizeRow);
                }
                float fieldBottom = texField.anchoredPosition.y - texField.sizeDelta.y * texField.pivot.y;
                float rowY = fieldBottom - 8f - 16f;          // centre of the new row
                var sl = (GameObject)UnityEngine.Object.Instantiate(texLabel.gameObject, sidebar, false);
                sl.name = "TextureSizeLabel";
                var slt = sl.GetComponent<Text>();
                slt.text = "Size:";
                slt.fontSize = Mathf.Max(10, Mathf.RoundToInt(slt.fontSize * 0.7f));
                var slr = (RectTransform)sl.transform;
                slr.pivot = new Vector2(0f, 0.5f); slr.sizeDelta = new Vector2(100f, 32f);
                slr.anchoredPosition = new Vector2(texLabel.anchoredPosition.x, rowY);
                slt.alignment = TextAnchor.MiddleLeft;
                widgets.Add(sl);

                var sGo = (GameObject)UnityEngine.Object.Instantiate(w.visibilityDropdown.gameObject, sidebar, false);
                sGo.name = "TextureSizeDropdown";
                var srt = (RectTransform)sGo.transform;
                srt.pivot = new Vector2(0.5f, 0.5f);
                srt.anchoredPosition = new Vector2(vis.anchoredPosition.x, rowY);
                srt.sizeDelta = new Vector2(Mathf.Max(vis.sizeDelta.x, 120f), vis.sizeDelta.y);
                texSizeDropdown = sGo.GetComponent<Dropdown>();
                texSizeDropdown.onValueChanged = new Dropdown.DropdownEvent();
                texSizeDropdown.ClearOptions();
                texSizeDropdown.AddOptions(new List<string> { "512px", "256px", "128px" });
                texSizeDropdown.value = 0; texSizeDropdown.RefreshShownValue();
                texSizeDropdown.onValueChanged.AddListener(delegate (int i)
                {
                    if (updatingAtlas || w.activeItem == null || string.IsNullOrEmpty(w.activeItem.sourceFile)) return;
                    ItemSettings.SetTexSize(w.activeItem, i == 2 ? 128 : i == 1 ? 256 : 512);
                    MarkImport(); w.ActivateDetailView();
                });
                Tooltip.Add(sGo, "Size of the item's one texture in the game (the atlas, or the image picked with Browse). 512px is the game's maximum; smaller sizes load faster and use less memory.");
                widgets.Add(sGo);
            }
            atlasWidgets = widgets.ToArray();
        }

        /// objects only (worlds have no model)
        public static void UpdateAtlasUi(Workshop w)
        {
            if (reimportButton != null && reimportButton.activeSelf != w.objectTabActive) reimportButton.SetActive(w.objectTabActive);
            if (atlasWidgets == null) return;
            bool show = w.objectTabActive;
            foreach (var g in atlasWidgets) if (g != null && g.activeSelf != show) g.SetActive(show);
        }

        static void ExportAtlas(Workshop w)
        {
            var tex = w.defaultMaterial != null ? w.defaultMaterial.mainTexture as Texture2D : null;
            if (w.activeItem == null || tex == null || tex == w.defaultTexture) { SetMessage(w, "Note: there's no texture to export for this item (it only uses the default material)."); return; }
            string model = string.IsNullOrEmpty(w.activeItem.sourceFile) ? "item" : Path.GetFileNameWithoutExtension(w.activeItem.sourceFile);
            string dir = string.IsNullOrEmpty(w.activeItem.sourceFile) ? "" : Path.GetDirectoryName(w.activeItem.sourceFile);
            string path;
            try { path = SFB.StandaloneFileBrowser.SaveFilePanel("Export texture", dir, model + "_atlas", "png"); }
            catch (Exception e) { SetMessage(w, "Couldn't open the save dialog: " + e.Message); return; }
            if (string.IsNullOrEmpty(path)) return;
            if (path.StartsWith("file://")) path = path.Substring(7);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
            try
            {
                byte[] png;
                try { png = tex.EncodeToPNG(); }
                catch (Exception)
                {   // not readable: copy it through a render texture first
                    var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0);
                    Graphics.Blit(tex, rt);
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGB24, false);
                    copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0); copy.Apply();
                    RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                    png = copy.EncodeToPNG(); UnityEngine.Object.Destroy(copy);
                }
                File.WriteAllBytes(path, png);
                SetMessage(w, "Note: saved " + tex.width + "x" + tex.height + "px texture to " + path + ".");
            }
            catch (Exception e) { SetMessage(w, "Couldn't save the texture: " + e.Message); }
        }

        static void ShowAtlas(AtlasMode m, bool crop, int texSize)
        {
            updatingAtlas = true;
            try
            {
                if (texSizeDropdown != null) { texSizeDropdown.value = texSize == 128 ? 2 : texSize == 256 ? 1 : 0; texSizeDropdown.RefreshShownValue(); }
                if (atlasDropdown != null) { atlasDropdown.value = (int)m; atlasDropdown.RefreshShownValue(); }
                if (cropToggle != null) cropToggle.isOn = crop;
            }
            finally { updatingAtlas = false; }
        }

        static void ShowShading(Shading s, float angle)
        {
            if (shadingDropdown == null) return;
            updatingDropdown = true;
            try
            {
                shadingDropdown.value = (int)s; shadingDropdown.RefreshShownValue();
                if (angleField != null) angleField.text = angle.ToString("0.#");
                SetAngleVisible(s == Shading.AutoSmooth);
            }
            finally { updatingDropdown = false; }
        }

        static void OnAngleChanged(Workshop w, string v)
        {
            if (updatingDropdown) return;
            WorkshopItem item = w.activeItem;
            if (item == null || string.IsNullOrEmpty(item.sourceFile)) return;
            float a;
            if (!float.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a))
            { ShowShading(ItemSettings.Get(item), ItemSettings.GetAngle(item)); return; }
            a = Mathf.Clamp(a, 0f, 180f);
            if (Mathf.Approximately(a, ItemSettings.GetAngle(item))) { angleField.text = a.ToString("0.#"); return; }
            ItemSettings.SetAngle(item, a);
            w.ActivateDetailView();
        }

        static BuiltMesh lastFlatMesh; static ObjModel lastFlatModel; static UvRect[] lastFlatRects; static UvRect lastFlatNoRect;
        static bool SameRect(UvRect a, UvRect b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            return a.U0 == b.U0 && a.V0 == b.V0 && a.U1 == b.U1 && a.V1 == b.V1 && a.Tile == b.Tile && a.SU0 == b.SU0 && a.SV0 == b.SV0 && a.SU1 == b.SU1 && a.SV1 == b.SV1;
        }
        static bool SameRects(UvRect[] a, UvRect[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!SameRect(a[i], b[i])) return false;
            return true;
        }
        static ObjModel lastModel; static UvRect[] lastRects; static UvRect lastNoRect; static string lastPath; static int lastFlatCount = -1;
        static Text flatWarning; static RectTransform flatWarnRect, resetRect; static float flatWarnX;
        public static readonly Color ErrorRed = new Color(1f, 0f, 0f, 1f);
        public static readonly Color NoteAmber = new Color(1f, 0.42f, 0f, 1f);   // vivid orange

        static void ShowFlatWarning(string msg)
        {
            if (flatWarning == null) return;
            if (msg == null) { flatWarning.gameObject.SetActive(false); return; }
            flatWarning.text = msg;
            float x = flatWarnX;
            if (angleWidgets != null && angleWidgets[1] != null && angleWidgets[1].activeSelf)
            {
                var u = (RectTransform)angleWidgets[1].transform; x = u.anchoredPosition.x + u.sizeDelta.x + 8f;
            }
            flatWarnRect.anchoredPosition = new Vector2(x, 0f);
            // wrap before the Reset rotation button instead of running underneath it
            float width = 300f;
            var parent = flatWarnRect.parent as RectTransform;
            if (resetRect != null && parent != null)
                width = Mathf.Max(80f, parent.rect.width + resetRect.anchoredPosition.x - resetRect.sizeDelta.x - 8f - x);
            flatWarnRect.sizeDelta = new Vector2(width, 50f);
            flatWarning.horizontalOverflow = HorizontalWrapMode.Wrap;
            flatWarning.verticalOverflow = VerticalWrapMode.Overflow;
            flatWarning.gameObject.SetActive(true);
        }

        // Workshop.SetErrorMessage replacement: errors in pure red, informational notes in amber
        public static void SetMessage(Workshop w, string text)
        {
            var t = w.errorMessageText;
            if (t == null) return;
            t.text = text ?? string.Empty;
            bool note = text != null && text.StartsWith("Note:");
            t.color = note ? NoteAmber : ErrorRed;
            t.fontStyle = FontStyle.Bold;
            noteShownAt = Time.unscaledTime;
        }

        static float noteShownAt;
        /// notes clear themselves 10 seconds after they were last set (errors stay until replaced)
        public static void FadeNotes(Workshop w)
        {
            var t = w.errorMessageText;
            if (t == null || string.IsNullOrEmpty(t.text) || !t.text.StartsWith("Note:")) return;
            if (Time.unscaledTime - noteShownAt > 10f) t.text = string.Empty;
        }

        static void OnShadingChanged(Workshop w, int i)
        {
            if (updatingDropdown) return;
            WorkshopItem item = w.activeItem;
            if (item == null || string.IsNullOrEmpty(item.sourceFile)) return;
            ShowFlatWarning(null);
            if ((Shading)i == Shading.Flat && lastModel != null && lastPath == item.sourceFile)
            {
                if (lastFlatCount < 0)
                {
                    lastFlatMesh = MeshBuilder.Build(lastModel, lastRects, lastNoRect, Shading.Flat);   // kept: the rebuild below reuses it
                    lastFlatCount = lastFlatMesh.VertexCount;
                }
                if (lastFlatCount > VertexLimit)
                {   // leave the current shading in place and say why
                    ShowShading(ItemSettings.Get(item), ItemSettings.GetAngle(item));
                    ShowFlatWarning("Flat needs " + lastFlatCount.ToString("N0") + " vertices (limit " + VertexLimit.ToString("N0") + ")");
                    return;
                }
            }
            ItemSettings.Set(item, (Shading)i);
            SetAngleVisible((Shading)i == Shading.AutoSmooth);
            w.ActivateDetailView();                    // reload the preview with the new shading
        }

    }

    // remembers the shading choice per OBJ file, next to Workshop.exe's own metadata.json
}
