// TTVR Workshop Plus - "Fix subscriptions": brings the game's local copy of your Workshop subscriptions in line with Steam.
// The game downloads subscribed items into ...\LocalLow\Lumbernauts\TinyTown\Internal\Workshop\Subscribed\wi_<id>,
// never removes items you unsubscribed from, and stops its whole sync at the first Steam request that takes over 5 seconds.
// This removes folders for items you're no longer subscribed to (moved aside, not deleted) and downloads missing or
// outdated ones the same way the game does, so the game finds them already there.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Steamworks;
using UnityEngine;

namespace TTVRPlus
{
    public static class Subscriptions
    {
        public static bool Busy;
        static List<string> pendingRemove;          // folders
        static List<RemoteStorageGetPublishedFileDetailsResult_t> pendingDownload;
        static int pendingTotal;

        public static string GameSubscribedDir()
        {
            // the tool's data lives in ...\Lumbernauts\TinyTownWorkshop, the game's in ...\Lumbernauts\TinyTown
            string parent = Directory.GetParent(Application.persistentDataPath).FullName;
            return Path.Combine(Path.Combine(Path.Combine(Path.Combine(parent, "TinyTown"), "Internal"), "Workshop"), "Subscribed");
        }

        static string RemovedDir()
        {
            return Path.Combine(Directory.GetParent(GameSubscribedDir()).FullName, "Unsubscribed (removed by Workshop Plus)");
        }

        static bool TryFolderId(string folder, out ulong id)
        {
            string n = Path.GetFileName(folder);
            id = 0;
            if (n.StartsWith("wi_world_")) return ulong.TryParse(n.Substring(9), out id);
            if (n.StartsWith("wi_")) return ulong.TryParse(n.Substring(3), out id);
            return false;
        }

        public static void Start(Workshop w)
        {
            if (Busy) return;
            if (!SteamManager.Initialized) { w.SetErrorMessage("Unable to check subscriptions. Make sure Steam client is running and logged in."); return; }
            w.StartCoroutine(Check(w));
        }

        static IEnumerator Check(Workshop w)
        {
            Busy = true;
            string dir = GameSubscribedDir();
            if (!Directory.Exists(dir))
            {
                w.SetErrorMessage("Couldn't find the game's Workshop folder (" + dir + "). Start Tiny Town VR once, then try again.");
                Busy = false; yield break;
            }

            // 1) every subscribed item id, all pages; give up (and change nothing) if any page fails
            w.SetErrorMessage("Note: asking Steam for your subscriptions...");
            var ids = new List<PublishedFileId_t>();
            int total = -1; bool failed = false;
            while (!failed && (total < 0 || ids.Count < total))
            {
                bool done = false, ok = false; int got = 0;
                for (int attempt = 0; attempt < 3 && !ok; attempt++)
                {
                    done = false;
                    var cr = CallResult<RemoteStorageEnumerateUserSubscribedFilesResult_t>.Create(
                        delegate (RemoteStorageEnumerateUserSubscribedFilesResult_t r, bool io)
                        {
                            done = true;
                            if (io || r.m_eResult != EResult.k_EResultOK) return;
                            ok = true; total = r.m_nTotalResultCount; got = r.m_nResultsReturned;
                            for (int i = 0; i < r.m_nResultsReturned; i++) ids.Add(r.m_rgPublishedFileId[i]);
                        });
                    cr.Set(SteamRemoteStorage.EnumerateUserSubscribedFiles((uint)ids.Count));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (!done && sw.Elapsed.TotalSeconds < 20) { SteamAPI.RunCallbacks(); yield return null; }
                    cr.Dispose();
                }
                if (!ok || (got == 0 && ids.Count < total)) failed = true;
            }
            if (failed)
            {
                w.SetErrorMessage("Steam didn't return your full subscription list, so nothing was changed. Try again in a moment.");
                Busy = false; yield break;
            }
            var subscribed = new HashSet<ulong>();
            foreach (var id in ids) subscribed.Add(id.m_PublishedFileId);

            // 2) local folders: which are no longer subscribed, and what's already downloaded
            var remove = new List<string>();
            var localStamp = new Dictionary<ulong, uint>();
            foreach (var folder in Directory.GetDirectories(dir))
            {
                ulong id;
                if (!TryFolderId(folder, out id)) continue;              // not one of the game's folders: leave alone
                if (!subscribed.Contains(id)) { remove.Add(folder); continue; }
                uint stamp;
                if (File.Exists(Path.Combine(folder, "data.bytes")) && ReadStamp(Path.Combine(folder, "metadata.json"), out stamp)) localStamp[id] = stamp;
            }

            // 3) details for every subscribed item (in parallel) to find missing or outdated downloads
            var download = new List<RemoteStorageGetPublishedFileDetailsResult_t>();
            int next = 0, pending = 0, detailFails = 0;
            var calls = new List<CallResult<RemoteStorageGetPublishedFileDetailsResult_t>>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while ((next < ids.Count || pending > 0) && timer.Elapsed.TotalSeconds < 120)
            {
                while (next < ids.Count && pending < 24)
                {
                    var cr = CallResult<RemoteStorageGetPublishedFileDetailsResult_t>.Create(
                        delegate (RemoteStorageGetPublishedFileDetailsResult_t r, bool io)
                        {
                            pending--;
                            if (io || r.m_eResult != EResult.k_EResultOK || r.m_hFile == UGCHandle_t.Invalid) { detailFails++; return; }
                            uint have;
                            if (!localStamp.TryGetValue(r.m_nPublishedFileId.m_PublishedFileId, out have) || have != r.m_rtimeUpdated) download.Add(r);
                        });
                    cr.Set(SteamRemoteStorage.GetPublishedFileDetails(ids[next++], 0u));
                    calls.Add(cr); pending++;
                }
                SteamAPI.RunCallbacks();
                w.SetErrorMessage("Note: checking subscribed items... " + (next - pending) + " of " + ids.Count);
                yield return null;
            }
            foreach (var c in calls) c.Dispose();
            detailFails += pending;

            if (remove.Count == 0 && download.Count == 0)
            {
                w.SetErrorMessage("Note: your subscriptions are in sync (" + ids.Count + " item" + (ids.Count == 1 ? "" : "s") + ")." +
                    (detailFails > 0 ? " " + detailFails + " couldn't be checked; try again later." : ""));
                Busy = false; yield break;
            }

            // 4) confirm
            pendingRemove = remove; pendingDownload = download; pendingTotal = ids.Count;
            var parts = new List<string>();
            if (remove.Count > 0) parts.Add("remove " + remove.Count + " item" + (remove.Count == 1 ? "" : "s") + " you're no longer subscribed to");
            if (download.Count > 0) parts.Add("download " + download.Count + " missing or outdated item" + (download.Count == 1 ? "" : "s"));
            WorkshopList.AskConfirm("Fix subscriptions? This will " + string.Join(" and ", parts.ToArray()) + ".\nClose Tiny Town VR first.");
            w.SetErrorMessage(detailFails > 0 ? "Note: " + detailFails + " subscribed item" + (detailFails == 1 ? "" : "s") + " couldn't be checked and will be left as they are." : "");
            Busy = false;
        }

        public static void Cancelled(Workshop w)
        {
            pendingRemove = null; pendingDownload = null;
            w.SetErrorMessage("Note: subscriptions left unchanged.");
        }

        public static void Confirmed(Workshop w)
        {
            if (pendingRemove == null) return;
            w.StartCoroutine(Apply(w, pendingRemove, pendingDownload, pendingTotal));
            pendingRemove = null; pendingDownload = null;
        }

        static IEnumerator Apply(Workshop w, List<string> remove, List<RemoteStorageGetPublishedFileDetailsResult_t> download, int total)
        {
            Busy = true;
            int moved = 0, moveFails = 0;
            if (remove.Count > 0)
            {
                string bin = RemovedDir();
                try { Directory.CreateDirectory(bin); } catch (Exception) { }
                foreach (var f in remove)
                {
                    try
                    {
                        string to = Path.Combine(bin, Path.GetFileName(f));
                        if (Directory.Exists(to)) Directory.Delete(to, true);   // an older removed copy
                        Directory.Move(f, to);
                        moved++;
                    }
                    catch (Exception e) { moveFails++; Debug.Log("[WorkshopPlus] couldn't move " + f + ": " + e.Message); }
                }
            }

            int got = 0, dlFails = 0, next = 0, pending = 0;
            var calls = new List<CallResult<RemoteStorageDownloadUGCResult_t>>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            float lastProgress = Time.realtimeSinceStartup;
            int lastDone = 0;
            while ((next < download.Count || pending > 0) && Time.realtimeSinceStartup - lastProgress < 60f)
            {
                while (next < download.Count && pending < 4)
                {
                    var d = download[next++];
                    var cr = CallResult<RemoteStorageDownloadUGCResult_t>.Create(
                        delegate (RemoteStorageDownloadUGCResult_t r, bool io)
                        {
                            pending--;
                            try
                            {
                                if (io || r.m_eResult != EResult.k_EResultOK || r.m_nSizeInBytes <= 0) { dlFails++; return; }
                                var bytes = new byte[r.m_nSizeInBytes];
                                SteamRemoteStorage.UGCRead(r.m_hFile, bytes, r.m_nSizeInBytes, 0u, EUGCReadAction.k_EUGCRead_Close);
                                Save(d, bytes);
                                got++;
                            }
                            catch (Exception e) { dlFails++; Debug.Log("[WorkshopPlus] couldn't save " + d.m_nPublishedFileId + ": " + e.Message); }
                        });
                    cr.Set(SteamRemoteStorage.UGCDownload(d.m_hFile, 0u));
                    calls.Add(cr); pending++;
                }
                SteamAPI.RunCallbacks();
                if (got + dlFails != lastDone) { lastDone = got + dlFails; lastProgress = Time.realtimeSinceStartup; }
                w.SetErrorMessage("Note: downloading " + (got + dlFails) + " of " + download.Count + "...");
                yield return null;
            }
            foreach (var c in calls) c.Dispose();
            dlFails += pending + (download.Count - next);

            var msg = new List<string>();
            if (remove.Count > 0) msg.Add("removed " + moved + " unsubscribed item" + (moved == 1 ? "" : "s"));
            if (download.Count > 0) msg.Add("downloaded " + got + " item" + (got == 1 ? "" : "s"));
            string text = string.Join(", ", msg.ToArray());
            text = char.ToUpper(text[0]) + text.Substring(1) + ".";
            if (moveFails + dlFails > 0)
                w.SetErrorMessage(text + " " + (moveFails > 0 ? moveFails + " couldn't be removed (is the game running?). " : "") +
                                  (dlFails > 0 ? dlFails + " couldn't be downloaded. " : "") + "Try again to finish.");
            else
                w.SetErrorMessage("Note: " + text + " Removed items were moved to \"" + RemovedDir() + "\".");
            Busy = false;
        }

        // same layout the game writes: wi_<id> (or wi_world_<id>) with the unpacked files + metadata.json
        static void Save(RemoteStorageGetPublishedFileDetailsResult_t d, byte[] gz)
        {
            bool world = d.m_pchFileName != null && d.m_pchFileName.Contains("_world_");
            ulong id = d.m_nPublishedFileId.m_PublishedFileId;
            string name = (world ? "wi_world_" : "wi_") + id;
            string dir = Path.Combine(GameSubscribedDir(), name);
            var files = Unpack(gz);                                   // throws on a damaged download, before touching the folder
            Directory.CreateDirectory(dir);
            foreach (var kv in files)
            {
                string path = Path.GetFullPath(Path.Combine(dir, kv.Key));
                if (!path.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar)) continue;   // stay inside the item folder
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, kv.Value);
            }
            var info = new SteamWorkshop.WorkshopItemInfo();
            info.id = id; info.title = d.m_rgchTitle; info.directory = name; info.updateTime = d.m_rtimeUpdated; info.isWorld = world;
            File.WriteAllText(Path.Combine(dir, "metadata.json"), JsonUtility.ToJson(info));
        }

        // the game's container: gzip of [int32 name length][UTF-16 name][int32 size][bytes] repeated
        static List<KeyValuePair<string, byte[]>> Unpack(byte[] gz)
        {
            byte[] raw;
            using (var src = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                var buf = new byte[65536]; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                raw = ms.ToArray();
            }
            var list = new List<KeyValuePair<string, byte[]>>();
            int p = 0;
            while (p + 4 <= raw.Length)
            {
                int len = BitConverter.ToInt32(raw, p); p += 4;
                if (len < 0 || len > 1024 || p + len * 2 + 4 > raw.Length) throw new Exception("damaged download");
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++) { sb.Append(BitConverter.ToChar(raw, p)); p += 2; }
                int size = BitConverter.ToInt32(raw, p); p += 4;
                if (size < 0 || p + size > raw.Length) throw new Exception("damaged download");
                var data = new byte[size]; Buffer.BlockCopy(raw, p, data, 0, size); p += size;
                list.Add(new KeyValuePair<string, byte[]>(sb.ToString(), data));
            }
            if (list.Count == 0) throw new Exception("empty download");
            return list;
        }

        static bool ReadStamp(string metadataFile, out uint stamp)
        {
            stamp = 0;
            try
            {
                if (!File.Exists(metadataFile)) return false;
                var info = JsonUtility.FromJson<SteamWorkshop.WorkshopItemInfo>(File.ReadAllText(metadataFile));
                if (info == null) return false;
                stamp = info.updateTime; return true;
            }
            catch (Exception) { return false; }
        }
    }
}
