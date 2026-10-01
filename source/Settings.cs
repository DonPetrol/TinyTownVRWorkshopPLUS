// TTVR Workshop Plus - per-item editor settings (shading, Auto-Smooth angle, atlas type, UV cropping, texture size).
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TTVRPlus
{
    /// per-item editor settings (shading, Auto-Smooth angle, atlas type, UV cropping, texture size), saved in
    /// Development\workshopplus_shading.txt. Older versions saved them per model file; those are still used for
    /// published items that don't have their own yet. New items always start from the defaults.
    public static class ItemSettings
    {
        public const int DefaultTexSize = 512;
        class Entry { public Shading Mode; public float Angle = 30f; public AtlasMode Atlas = AtlasMode.Equal; public bool Crop; public int TexSize = DefaultTexSize; }
        static Dictionary<string, Entry> cache;
        static readonly Entry Defaults = new Entry();
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        static string FilePath { get { return Path.Combine(FileUtils.GetWorkshopDevelopmentDirectory(), "workshopplus_shading.txt"); } }
        static string PathKey(string p) { try { return Path.GetFullPath(p).ToLowerInvariant(); } catch { return p.ToLowerInvariant(); } }
        static string ItemKey(WorkshopItem it) { return "item:" + it.uniqueDirectory; }

        static void Load()
        {
            if (cache != null) return;
            cache = new Dictionary<string, Entry>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    // mode, angle, [atlas, crop, [texsize]], key (an "item:<folder>" or, from older versions, a model path)
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    int v; if (!int.TryParse(parts[0], out v) || v < 0 || v > 3) continue;
                    var e = new Entry { Mode = (Shading)v };
                    string key = parts[parts.Length - 1];
                    float a; if (parts.Length >= 3 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, Inv, out a)) e.Angle = a;
                    if (parts.Length >= 5)
                    {
                        int am; if (int.TryParse(parts[2], out am) && am >= 0 && am <= 1) e.Atlas = (AtlasMode)am;
                        e.Crop = parts[3] == "1";
                    }
                    if (parts.Length >= 6) { int ts; if (int.TryParse(parts[4], out ts) && (ts == 128 || ts == 256 || ts == 512)) e.TexSize = ts; }
                    cache[key] = e;
                }
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] " + e.Message); }
        }

        static Entry Find(WorkshopItem it)
        {
            Load();
            if (it == null) return Defaults;
            Entry e;
            if (!string.IsNullOrEmpty(it.uniqueDirectory) && cache.TryGetValue(ItemKey(it), out e)) return e;
            // published items made before settings were per item: use what was saved for their model file
            if (it.id != 0 && !string.IsNullOrEmpty(it.sourceFile) && cache.TryGetValue(PathKey(it.sourceFile), out e)) return e;
            return Defaults;
        }

        static Entry Ensure(WorkshopItem it)
        {
            Load();
            Entry e;
            if (cache.TryGetValue(ItemKey(it), out e)) return e;
            var from = Find(it);
            e = new Entry { Mode = from.Mode, Angle = from.Angle, Atlas = from.Atlas, Crop = from.Crop, TexSize = from.TexSize };
            cache[ItemKey(it)] = e;
            return e;
        }

        public static Shading Get(WorkshopItem it) { return Find(it).Mode; }
        public static float GetAngle(WorkshopItem it) { return Find(it).Angle; }
        public static AtlasMode GetAtlas(WorkshopItem it) { return Find(it).Atlas; }
        public static bool GetCrop(WorkshopItem it) { return Find(it).Crop; }
        public static int GetTexSize(WorkshopItem it) { return Find(it).TexSize; }
        public static void Set(WorkshopItem it, Shading s) { Ensure(it).Mode = s; Save(); }
        public static void SetAngle(WorkshopItem it, float a) { Ensure(it).Angle = a; Save(); }
        public static void SetAtlas(WorkshopItem it, AtlasMode m) { Ensure(it).Atlas = m; Save(); }
        public static void SetCrop(WorkshopItem it, bool c) { Ensure(it).Crop = c; Save(); }
        public static void SetTexSize(WorkshopItem it, int s) { Ensure(it).TexSize = s; Save(); }

        /// a duplicate starts with the same settings as its original
        public static void Copy(WorkshopItem from, WorkshopItem to)
        {
            var f = Find(from);
            Load();
            cache[ItemKey(to)] = new Entry { Mode = f.Mode, Angle = f.Angle, Atlas = f.Atlas, Crop = f.Crop, TexSize = f.TexSize };
            Save();
        }

        /// legacy (per model file) entries follow a moved model
        public static void Move(string from, string to)
        {
            Load();
            Entry e;
            if (!cache.TryGetValue(PathKey(from), out e) || cache.ContainsKey(PathKey(to))) return;
            cache[PathKey(to)] = new Entry { Mode = e.Mode, Angle = e.Angle, Atlas = e.Atlas, Crop = e.Crop, TexSize = e.TexSize };
            Save();
        }

        static void Save()
        {
            try
            {
                FileUtils.EnsureDirectoryExists(FileUtils.GetWorkshopDevelopmentDirectory());
                var lines = new List<string>();
                foreach (var kv in cache)
                    lines.Add(((int)kv.Value.Mode) + "\t" + kv.Value.Angle.ToString("0.###", Inv) + "\t" + ((int)kv.Value.Atlas) + "\t" + (kv.Value.Crop ? "1" : "0") + "\t" + kv.Value.TexSize + "\t" + kv.Key);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] " + e.Message); }
        }
    }
}
