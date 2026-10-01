// TTVR Workshop Plus - background vertex counting for the item list.
// Runs the same parse + atlas-rule + mesh-build steps the editor uses (minus pixel work) on a worker thread,
// caches results on disk, and is overwritten by the exact count whenever an item is opened in the editor.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TTVRPlus
{
    /// remembers whether files exist (and their size/time) for the item list, so scrolling and count updates don't
    /// ask Windows again and again; cleared whenever the list is opened or changes tab (main thread only)
    public static class FileCache
    {
        struct Info { public bool Exists; public long Stamp; public long Ticks; }
        static readonly Dictionary<string, Info> map = new Dictionary<string, Info>();

        static Info Get(string path)
        {
            Info i;
            if (map.TryGetValue(path, out i)) return i;
            try
            {
                var fi = new FileInfo(path);
                i.Exists = fi.Exists;
                if (i.Exists) { i.Ticks = fi.LastWriteTimeUtc.Ticks; i.Stamp = i.Ticks ^ (fi.Length << 1); }
            }
            catch (Exception) { i.Exists = false; }
            map[path] = i;
            return i;
        }

        public static bool Exists(string path) { return !string.IsNullOrEmpty(path) && Get(path).Exists; }
        public static long Stamp(string path) { return Get(path).Stamp; }        // same value as VertexCounter's stamp
        public static long Ticks(string path) { return Get(path).Ticks; }
        public static void Clear() { map.Clear(); }
        public static void Forget(string path) { if (path != null) map.Remove(path); }
    }

    public static class VertexCounter
    {
        public const int NoPath = -2, Missing = -3, Failed = -4, Pending = -1;

        class Entry { public long Stamp; public int Count; }
        struct Job { public string Path, Key, Tex; public long Stamp; public int Mode; public float Angle; public bool Crop; }

        static readonly object Gate = new object();
        static Dictionary<string, Entry> cache;
        static readonly Queue<Job> queue = new Queue<Job>();
        static readonly HashSet<string> queued = new HashSet<string>();
        static Thread worker;
        static string cacheFile;
        static volatile int version;
        public static int Version { get { return version; } }

        public static void Init(string dataDir)
        {
            lock (Gate)
            {
                if (cache != null) return;
                cache = new Dictionary<string, Entry>();
                cacheFile = Path.Combine(dataDir, "workshopplus_vertices.txt");
                try
                {
                    if (File.Exists(cacheFile))
                        foreach (var line in File.ReadAllLines(cacheFile))
                        {
                            var p = line.Split('\t');
                            if (p.Length != 3) continue;           // (older format lines are ignored)
                            cache[p[2]] = new Entry { Stamp = long.Parse(p[0]), Count = int.Parse(p[1]) };
                        }
                }
                catch (Exception) { cache.Clear(); }
            }
        }

        static string Key(string path) { try { return Path.GetFullPath(path).ToLowerInvariant(); } catch { return path.ToLowerInvariant(); } }

        // one cache entry per model + shading + chosen texture, so entries sharing a model don't overwrite each other
        static string FullKey(string path, int mode, float angle, string tex, bool crop)
        {
            return Key(path) + "|" + mode + "|" + angle.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (tex ?? "").ToLowerInvariant()
                 + "|v2" + (crop ? "c" : "");      // v2: materials sharing a texture share vertices; c: UV cropping
        }

        static long Stamp(string path)
        {
            var fi = new FileInfo(path);
            return fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 1);
        }

        /// count, or Pending (queued), NoPath, Missing, Failed
        public static int Get(string path, int mode, float angle, string textureFile, bool crop)
        {
            if (string.IsNullOrEmpty(path)) return NoPath;
            if (!FileCache.Exists(path)) return Missing;
            if (cache == null) return Pending;
            string tex = textureFile ?? "", key = FullKey(path, mode, angle, tex, crop);
            long stamp;
            stamp = FileCache.Stamp(path);
            lock (Gate)
            {
                Entry e;
                if (cache.TryGetValue(key, out e) && e.Stamp == stamp) return e.Count;
                if (!queued.Contains(key))
                {
                    queued.Add(key);
                    queue.Enqueue(new Job { Path = path, Key = key, Tex = tex, Stamp = stamp, Mode = mode, Angle = angle, Crop = crop });
                    if (worker == null || !worker.IsAlive)
                    {
                        worker = new Thread(Work); worker.IsBackground = true; worker.Priority = ThreadPriority.BelowNormal; worker.Start();
                    }
                }
            }
            return Pending;
        }

        /// exact count from the editor
        public static void SetExact(string path, int mode, float angle, string textureFile, bool crop, int count)
        {
            if (cache == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            FileCache.Forget(path);                 // the editor just read it: take its current time/size
            lock (Gate) cache[FullKey(path, mode, angle, textureFile, crop)] = new Entry { Stamp = Stamp(path), Count = count };
            version++;
            saveDue = true;                         // written by SaveIfDue (at most every few seconds)
        }

        static void Work()
        {
            while (true)
            {
                Job j;
                lock (Gate)
                {
                    if (queue.Count == 0) { worker = null; break; }
                    j = queue.Dequeue();
                }
                int count;
                try { count = Count(j.Path, (Shading)j.Mode, j.Angle, j.Tex, j.Crop); }
                catch (Exception) { count = Failed; }
                lock (Gate)
                {
                    cache[j.Key] = new Entry { Stamp = j.Stamp, Count = count };
                    queued.Remove(j.Key);
                }
                version++;
                if (queue.Count == 0) saveDue = true;
            }
        }

        static volatile bool saveDue;
        static DateTime lastSave;
        /// from the list follower: write the cache file if something changed, at most every 5 seconds
        public static void SaveIfDue(bool now = false)
        {
            if (!saveDue || (!now && (DateTime.UtcNow - lastSave).TotalSeconds < 5)) return;
            saveDue = false; lastSave = DateTime.UtcNow;
            Save();
        }

        static void Save()
        {
            try
            {
                var lines = new List<string>();
                lock (Gate)
                    foreach (var kv in cache)
                        if (kv.Value.Count >= 0)
                            lines.Add(kv.Value.Stamp + "\t" + kv.Value.Count + "\t" + kv.Key);
                File.WriteAllLines(cacheFile, lines.ToArray());
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- same vertex rules as the editor
        public static int Count(string path, Shading mode, float angle, string textureFile, bool crop)
        {
            ObjModel m;
            bool[] textured;
            if (GltfReader.IsGltf(path))
            {
                List<ModelMaterial> mats; string warn;
                m = GltfReader.Load(path, out mats, out warn);
                textured = new bool[mats.Count];
                var keys = new List<string>();
                for (int i = 0; i < mats.Count; i++) textured[i] = mats[i].TexturePath != null || mats[i].TextureData != null;
                return Build(m, textured, TextureKeys(mats), mode, angle, textureFile, crop);
            }
            m = ObjParser.Parse(path);
            string[] texPaths = MtlTextures(path, m);
            textured = new bool[m.MaterialNames.Count];
            for (int i = 0; i < textured.Length; i++) textured[i] = texPaths[i] != null;
            return Build(m, textured, texPaths, mode, angle, textureFile, crop);
        }

        static string[] TextureKeys(List<ModelMaterial> mats)
        {
            var r = new string[mats.Count];
            for (int i = 0; i < mats.Count; i++) r[i] = mats[i].TexturePath ?? (mats[i].TextureData != null ? mats[i].TextureKey : null);
            return r;
        }

        static int Build(ObjModel m, bool[] textured, string[] texKeys, Shading mode, float angle, string textureFile, bool crop)
        {
            var tile = new UvRect { U0 = 0, V0 = 0, U1 = 1, V1 = 1, Tile = true };
            var cell = new UvRect { U0 = 0, V0 = 0, U1 = 1, V1 = 1 };
            var swatch = new UvRect { U0 = 0.5f, V0 = 0.5f, U1 = 0.5f, V1 = 0.5f };
            int n = m.MaterialNames.Count;
            var rects = new UvRect[n];
            bool facesWithoutMaterial = m.TriMaterial.Contains(-1);
            UvRect noRect;
            if (!string.IsNullOrEmpty(textureFile))
            {   // texture picked in the editor: one texture, original UVs
                for (int i = 0; i < n; i++) rects[i] = tile;
                noRect = tile;
                if (crop && WouldCrop(m, textureFile)) { for (int i = 0; i < n; i++) rects[i] = cell; noRect = cell; }
            }
            else
            {
                var unique = new HashSet<string>(); bool anyColour = false;
                for (int i = 0; i < n; i++) { if (textured[i]) unique.Add(texKeys[i]); else anyColour = true; }
                bool single = unique.Count == 1 && !anyColour && !facesWithoutMaterial;
                for (int i = 0; i < n; i++) rects[i] = single ? tile : textured[i] ? cell : swatch;
                noRect = single ? tile : swatch;
                if (single && crop) { string only = null; foreach (var u in unique) only = u; if (WouldCrop(m, only)) { for (int i = 0; i < n; i++) rects[i] = cell; noRect = cell; } }
            }
            return MeshBuilder.Build(m, rects, noRect, mode, angle).VertexCount;
        }

        // same test as the editor: does UV cropping replace a single texture (and so drop tiling)?
        static bool WouldCrop(ObjModel m, string texPath)
        {
            var mats = new List<MaterialSource>();
            for (int i = 0; i < m.MaterialNames.Count; i++) mats.Add(new MaterialSource { TextureSlot = 0 });
            float[] win = AtlasStats.Measure(m, mats, 1).Windows[0];
            int w, h;
            if (texPath == null || !ImageInfo.TryGetSize(texPath, out w, out h)) return true;    // embedded images: assume it does
            int x0, y0, x1, y1;
            return Atlas.CropPixels(w, h, win, out x0, out y0, out x1, out y1);
        }

        // texture file per material from the OBJ's MTL file(s), resolved like the editor does (null = colour only)
        static string[] MtlTextures(string objPath, ObjModel m)
        {
            var result = new string[m.MaterialNames.Count];
            string dir = Path.GetDirectoryName(objPath);
            var map = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(objPath))
            {
                string t = line.Trim();
                if (!t.StartsWith("mtllib ")) continue;
                string name = t.Substring(7).Trim();
                string mtl = Path.Combine(dir, name);
                if (!File.Exists(mtl)) mtl = Path.Combine(Path.Combine(dir, Path.GetFileNameWithoutExtension(objPath) + "_Textures"), name);
                if (!File.Exists(mtl)) continue;
                string cur = null;
                foreach (var ml in File.ReadAllLines(mtl))
                {
                    string s = ml.Trim().Replace("  ", " ");
                    if (s.StartsWith("newmtl ")) cur = s.Substring(7);
                    else if (cur != null && s.StartsWith("map_Kd "))
                    {
                        string tp = Path.Combine(Path.GetDirectoryName(mtl), s.Substring(7));
                        if (!File.Exists(tp))
                        {
                            string alt = Path.Combine(dir, Path.GetFileName(s.Substring(7).Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
                            tp = File.Exists(alt) ? alt : null;
                        }
                        map[cur] = tp;
                    }
                }
            }
            for (int i = 0; i < result.Length; i++) { string tp; if (map.TryGetValue(m.MaterialNames[i], out tp)) result[i] = tp; }
            return result;
        }
    }
}
