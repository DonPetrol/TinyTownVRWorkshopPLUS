// TTVR Workshop Plus - Steam Workshop preview images as list thumbnails.
// Downloads each published item's preview (the image Workshop.exe uploads when publishing), shrinks it,
// and caches it on disk per item + publish time, so later starts load instantly.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;

namespace TTVRPlus
{
    public static class Thumbnails
    {
        const int Size = 128, MaxInFlight = 4;

        static readonly Dictionary<ulong, UGCHandle_t> handles = new Dictionary<ulong, UGCHandle_t>();
        static readonly Dictionary<ulong, uint> stamps = new Dictionary<ulong, uint>();
        static readonly Dictionary<ulong, Texture2D> cache = new Dictionary<ulong, Texture2D>();
        static readonly HashSet<ulong> failed = new HashSet<ulong>(), queuedSet = new HashSet<ulong>();
        static readonly List<ulong> queue = new List<ulong>();
        static string dir;
        static bool pumping;
        public static int Version;

        public static void Init(string devDir) { dir = Path.Combine(devDir, "workshopplus_thumbs"); }

        public static void SetSource(ulong id, UGCHandle_t preview, uint updated)
        {
            if (id == 0 || preview == UGCHandle_t.Invalid) return;
            handles[id] = preview; stamps[id] = updated;
        }

        static string FileFor(ulong id)
        {
            uint s; if (dir == null || !stamps.TryGetValue(id, out s)) return null;
            return Path.Combine(dir, id + "_" + s + ".png");
        }

        /// thumbnail if ready (null otherwise; it is then fetched in the background)
        public static Texture2D Get(ulong id)
        {
            if (id == 0) return null;
            Texture2D t;
            if (cache.TryGetValue(id, out t)) return t;
            if (failed.Contains(id)) return null;
            string f = FileFor(id);
            if (f != null && FileCache.Exists(f))
            {
                try
                {
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (t.LoadImage(File.ReadAllBytes(f))) { t.wrapMode = TextureWrapMode.Clamp; cache[id] = t; return t; }
                    UnityEngine.Object.Destroy(t);
                }
                catch (Exception) { }
            }
            if (!queuedSet.Contains(id) && handles.ContainsKey(id)) { queuedSet.Add(id); queue.Add(id); }
            return null;
        }

        // ---- local image files (a world's preview.jpg, an unpublished item's custom thumbnail), loaded a few per frame
        static readonly Dictionary<string, Texture2D> local = new Dictionary<string, Texture2D>();
        static readonly List<string> localQueue = new List<string>();
        static readonly HashSet<string> localQueued = new HashSet<string>(), localFailed = new HashSet<string>();

        static string LocalKey(string path)
        {
            return path + "|" + FileCache.Ticks(path);
        }

        public static Texture2D GetLocal(string path)
        {
            if (string.IsNullOrEmpty(path) || !FileCache.Exists(path)) return null;
            string key = LocalKey(path);
            if (key == null || localFailed.Contains(key)) return null;
            Texture2D t;
            if (local.TryGetValue(key, out t)) return t;
            if (!localQueued.Contains(key)) { localQueued.Add(key); localQueue.Add(key); }
            return null;
        }

        /// from the list follower: load up to 2 queued local images per frame
        public static void PumpLocal()
        {
            for (int n = 0; n < 2 && localQueue.Count > 0; n++)
            {
                string key = localQueue[0]; localQueue.RemoveAt(0); localQueued.Remove(key);
                string path = key.Substring(0, key.LastIndexOf('|'));
                try
                {
                    var t = Shrink(File.ReadAllBytes(path));
                    if (t == null) { localFailed.Add(key); continue; }
                    local[key] = t; Version++;
                }
                catch (Exception) { localFailed.Add(key); }
            }
        }

        static Texture2D Shrink(byte[] bytes)
        {
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!src.LoadImage(bytes)) { UnityEngine.Object.Destroy(src); return null; }
            int w = src.width, h = src.height;
            int tw = w >= h ? Size : Mathf.Max(1, Size * w / h), th = h >= w ? Size : Mathf.Max(1, Size * h / w);
            var px = src.GetPixels32();
            UnityEngine.Object.Destroy(src);
            var raw = new byte[px.Length * 4];
            for (int i = 0; i < px.Length; i++) { raw[i * 4] = px[i].r; raw[i * 4 + 1] = px[i].g; raw[i * 4 + 2] = px[i].b; raw[i * 4 + 3] = px[i].a; }
            byte[] small = Atlas.Resize(new TextureSource { Width = w, Height = h, Rgba = raw }, tw, th);
            var cols = new Color32[tw * th];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(small[i * 4], small[i * 4 + 1], small[i * 4 + 2], 255);
            var t = new Texture2D(tw, th, TextureFormat.RGBA32, false);
            t.SetPixels32(cols); t.Apply(); t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        public static void StartPump(MonoBehaviour host)
        {
            if (pumping) return;
            pumping = true;
            host.StartCoroutine(Pump());
        }

        static IEnumerator Pump()
        {
            var inFlight = new List<CallResult<RemoteStorageDownloadUGCResult_t>>();
            int pending = 0;
            while (true)
            {
                while (pending < MaxInFlight && queue.Count > 0 && SteamManager.Initialized)
                {
                    ulong id = queue[0]; queue.RemoveAt(0);
                    UGCHandle_t h = handles[id];
                    CallResult<RemoteStorageDownloadUGCResult_t> cr = null;
                    cr = CallResult<RemoteStorageDownloadUGCResult_t>.Create(
                        delegate (RemoteStorageDownloadUGCResult_t r, bool io)
                        {
                            pending--;
                            try
                            {
                                if (io || r.m_eResult != EResult.k_EResultOK || r.m_nSizeInBytes <= 0) { failed.Add(id); return; }
                                var bytes = new byte[r.m_nSizeInBytes];
                                SteamRemoteStorage.UGCRead(r.m_hFile, bytes, r.m_nSizeInBytes, 0u, EUGCReadAction.k_EUGCRead_Close);
                                Store(id, bytes);
                            }
                            catch (Exception e) { failed.Add(id); Debug.Log("[WorkshopPlus] thumbnail " + id + ": " + e.Message); }
                            finally { inFlight.Remove(cr); if (cr != null) cr.Dispose(); }
                        });
                    cr.Set(SteamRemoteStorage.UGCDownload(h, 0u));
                    inFlight.Add(cr); pending++;
                }
                if (pending > 0) SteamAPI.RunCallbacks();
                yield return null;
            }
        }

        static void Store(ulong id, byte[] bytes)
        {
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!src.LoadImage(bytes)) { UnityEngine.Object.Destroy(src); failed.Add(id); return; }
            int w = src.width, h = src.height;
            int tw = w >= h ? Size : Mathf.Max(1, Size * w / h), th = h >= w ? Size : Mathf.Max(1, Size * h / w);
            var px = src.GetPixels32();
            UnityEngine.Object.Destroy(src);
            var raw = new byte[px.Length * 4];
            for (int i = 0; i < px.Length; i++) { raw[i * 4] = px[i].r; raw[i * 4 + 1] = px[i].g; raw[i * 4 + 2] = px[i].b; raw[i * 4 + 3] = px[i].a; }
            byte[] small = Atlas.Resize(new TextureSource { Width = w, Height = h, Rgba = raw }, tw, th);
            var cols = new Color32[tw * th];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(small[i * 4], small[i * 4 + 1], small[i * 4 + 2], 255);
            var t = new Texture2D(tw, th, TextureFormat.RGBA32, false);
            t.SetPixels32(cols); t.Apply(); t.wrapMode = TextureWrapMode.Clamp;
            cache[id] = t;
            Version++;
            try
            {
                string f = FileFor(id);
                if (f != null)
                {
                    Directory.CreateDirectory(dir);
                    foreach (var old in Directory.GetFiles(dir, id + "_*.png")) if (old != f) File.Delete(old);   // older publishes
                    File.WriteAllBytes(f, t.EncodeToPNG());
                }
            }
            catch (Exception) { }
        }
    }
}
