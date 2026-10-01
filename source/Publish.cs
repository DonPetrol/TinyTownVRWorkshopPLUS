// TTVR Workshop Plus - faster, non-freezing Publish.
// 1) The tool's per-vertex colour step (AverageNormalCalculator) read mesh.vertices[i] / mesh.normals[j] inside its loops;
//    in Unity each of those copies the whole array, so it took (vertices x vertices) time - minutes for big models.
//    ComputeAverageNormals below gives exactly the same result reading each array once.
// 2) Publish waited for Steam in a tight loop, so the window froze until the upload finished. It now runs as a coroutine
//    with a progress note, doing the same Steam calls in the same order.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;

namespace TTVRPlus
{
    public static class Publisher
    {
        public static bool Busy;

        struct Avg { public Vector3 normal; public int count; }

        /// replaces AverageNormalCalculator.ComputeAverageNormals (same maths, same order, arrays read once)
        public static Color[] ComputeAverageNormals(Mesh mesh, float textureCoordinate)
        {
            Vector3[] verts = mesh.vertices, normals = mesh.normals;
            int n = mesh.vertexCount;
            var keys = new int[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 v = verts[i];
                keys[i] = (int)(v.x * 1000f) * 9323 + (int)(v.y * 1000f) * 7699 + (int)(v.z * 1000f) * 4259;
            }
            var sums = new Dictionary<int, Avg>();
            for (int j = 0; j < n; j++)
            {
                Avg a;
                if (sums.TryGetValue(keys[j], out a))
                {
                    Vector3 nv = normals[j];
                    a.normal.x += nv.x; a.normal.y += nv.y; a.normal.z += nv.z;
                    a.count++;
                    sums[keys[j]] = a;
                }
                else sums[keys[j]] = new Avg { normal = normals[j], count = 1 };
            }
            var avg = new Dictionary<int, Vector3>();
            foreach (var kv in sums)
            {
                Avg a = kv.Value;
                a.normal.x /= a.count; a.normal.y /= a.count; a.normal.z /= a.count;
                avg[kv.Key] = a.normal.normalized;
            }
            var cols = new Color[n];
            for (int k = 0; k < n; k++)
            {
                Vector3 v = avg[keys[k]];
                cols[k] = new Color(0.5f * v.x + 0.5f, 0.5f * v.y + 0.5f, 0.5f * v.z + 0.5f, textureCoordinate);
            }
            return cols;
        }

        /// replaces Workshop.OnPublishButtonClicked
        public static void OnPublishButtonClicked(Workshop w)
        {
            if (Busy) return;
            var item = w.activeItem;
            if (item == null) { Debug.Log("Publish button was clicked without an active item"); return; }
            if (!w.activeItems.Contains(item)) w.activeItems.Add(item);
            if (!SteamManager.Initialized) { w.SetErrorMessage("Unable to publish. Make sure Steam client is running and logged in."); return; }
            if (string.IsNullOrEmpty(item.title)) { w.SetErrorMessage("Unable to publish. Missing required title."); return; }
            w.StartCoroutine(Run(w, item, w.objectTabActive));
        }

        static IEnumerator Run(Workshop w, WorkshopItem item, bool isObject)
        {
            Busy = true;
            if (w.publishButton != null) w.publishButton.interactable = false;
            w.SetErrorMessage("Note: preparing " + (string.IsNullOrEmpty(item.title) ? "item" : "\"" + item.title + "\"") + " for upload...");
            yield return null; yield return null;          // let the note appear before the work below

            string dir = Path.Combine(FileUtils.GetWorkshopDevelopmentDirectory(), item.uniqueDirectory);
            string dataName = item.uniqueDirectory + ".gz", previewName = item.uniqueDirectory + ".jpg";
            byte[] zipData = null, previewData = null;
            string error = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (isObject) w.GenerateObjectData(dir, dataName, out zipData, out previewData);
                else
                {
                    w.GenerateWorldData(dir, dataName, out zipData, out previewData);
                    var custom = ThumbPicker.WorldPreviewOverride(item);     // Workshop thumbnail only; the world keeps its preview.jpg
                    if (custom != null) previewData = custom;
                }
                Debug.Log("[WorkshopPlus] publish data prepared in " + sw.ElapsedMilliseconds + " ms");
                if (!SteamRemoteStorage.FileWrite(dataName, zipData, zipData.Length)) error = "Failed uploading workshop data file";
                else if (!SteamRemoteStorage.FileWrite(previewName, previewData, previewData.Length)) error = "Failed uploading workshop preview image";
            }
            catch (Exception e) { error = e.Message; Debug.Log("[WorkshopPlus] publish: " + e); }

            ulong newId = 0; bool ok = false;
            if (error == null)
            {
                bool done = false; EResult result = EResult.k_EResultFail;
                var vis = item.visibility == 1 ? ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly
                        : item.visibility == 2 ? ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic
                        : ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate;
                IDisposable call;
                if (item.id == 0)
                {
                    var cr = CallResult<RemoteStoragePublishFileResult_t>.Create(delegate (RemoteStoragePublishFileResult_t r, bool io)
                    {
                        result = io ? EResult.k_EResultIOFailure : r.m_eResult;
                        if (result == EResult.k_EResultOK) { newId = r.m_nPublishedFileId.m_PublishedFileId; SteamRemoteStorage.FileDelete(dataName); }
                        done = true;
                    });
                    cr.Set(SteamRemoteStorage.PublishWorkshopFile(dataName, previewName, SteamUtils.GetAppID(), item.title, item.description, vis, null, EWorkshopFileType.k_EWorkshopFileTypeFirst));
                    call = cr;
                }
                else
                {
                    var h = SteamRemoteStorage.CreatePublishedFileUpdateRequest(new PublishedFileId_t(item.id));
                    SteamRemoteStorage.UpdatePublishedFileFile(h, dataName);
                    SteamRemoteStorage.UpdatePublishedFilePreviewFile(h, previewName);
                    SteamRemoteStorage.UpdatePublishedFileTitle(h, item.title);
                    SteamRemoteStorage.UpdatePublishedFileDescription(h, item.description);
                    SteamRemoteStorage.UpdatePublishedFileVisibility(h, vis);
                    SteamRemoteStorage.UpdatePublishedFileTags(h, null);
                    var cr = CallResult<RemoteStorageUpdatePublishedFileResult_t>.Create(delegate (RemoteStorageUpdatePublishedFileResult_t r, bool io)
                    {
                        result = io ? EResult.k_EResultIOFailure : r.m_eResult;
                        if (result == EResult.k_EResultOK) newId = r.m_nPublishedFileId.m_PublishedFileId;
                        done = true;
                    });
                    cr.Set(SteamRemoteStorage.CommitPublishedFileUpdate(h));
                    call = cr;
                }
                var wait = System.Diagnostics.Stopwatch.StartNew();
                float lastNote = -1f;
                while (!done && wait.Elapsed.TotalMinutes < 10)
                {
                    SteamAPI.RunCallbacks();
                    int s = (int)wait.Elapsed.TotalSeconds;
                    if (s != lastNote) { lastNote = s; w.SetErrorMessage("Note: uploading to the Steam Workshop... " + s + "s"); }
                    yield return null;
                }
                call.Dispose();
                ok = done && result == EResult.k_EResultOK;
                if (!ok) Debug.Log("[WorkshopPlus] publish failed: " + (done ? result.ToString() : "timed out"));
            }
            else Debug.Log("[WorkshopPlus] " + error);

            if (ok)
            {
                item.id = newId;
                w.AddOrUpdateMetadata(item);
                WorkshopList.AfterPublish(w, item);
                w.SetErrorMessage("Note: published \"" + item.title + "\" to the Steam Workshop.");
            }
            else w.SetErrorMessage("Unable to publish item to Steam. Make sure Steam client is running and logged in.");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
            if (w.publishButton != null) w.publishButton.interactable = w.titleField == null || !string.IsNullOrEmpty(w.titleField.text);
            Busy = false;
        }
    }
}
