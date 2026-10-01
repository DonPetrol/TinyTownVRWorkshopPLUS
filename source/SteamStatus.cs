// TTVR Workshop Plus - Steam connection status on the list page, with a Reconnect button.
// When Steam isn't running / logged in (at start, or lost later), the welcome panel turns into a red
// "Can't connect to Steam" message; Reconnect starts the Steam API again and reloads the items.
using System;
using System.Collections;
using System.IO;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace TTVRPlus
{
    public static class SteamStatus
    {
        public static bool Problem;              // true while Steam isn't usable
        public static bool Busy;
        static Workshop ws;
        static Text title, desc;
        static string titleText, descText;
        static Color titleColor, descColor;
        static GameObject arrow, reconnect;
        static float lastAuto;
        static Text reconnectText;
        static float lastCheck;
        static bool itemsLoaded;

        public static void Init(Workshop w)
        {
            ws = w;
            try
            {
                var tt = w.message.transform.Find("Title"); if (tt != null) title = tt.GetComponent<Text>();
                desc = w.messageDescription;
                var at = w.message.transform.Find("Arrow"); if (at != null) arrow = at.gameObject;
                var go = (GameObject)UnityEngine.Object.Instantiate(w.publishButton.gameObject, w.message.transform, false);
                go.name = "ReconnectButton";
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(220f, 50f); rt.anchoredPosition = new Vector2(0f, -170f);
                var b = go.GetComponent<Button>();
                b.onClick = new Button.ButtonClickedEvent(); b.interactable = true;
                b.onClick.AddListener(delegate { if (!Busy) w.StartCoroutine(Reconnect(w, false)); });
                reconnectText = go.GetComponentInChildren<Text>();
                if (reconnectText != null) { reconnectText.text = "Reconnect"; WorkshopPlus.FitButtonText(reconnectText); }
                Tooltip.Add(go, "Try connecting to Steam again (start Steam and log in first).");
                reconnect = go;
                reconnect.SetActive(false);
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] Steam status: " + e.Message); }
        }


        static bool ready;                       // the first load has finished (the panel shows its normal text again)

        /// end of the item load: ok = items came from Steam
        public static void ItemsLoaded(Workshop w, bool ok)
        {
            itemsLoaded = itemsLoaded || ok; ready = true;
            Problem = !ok || !SteamUsable();
            Debug.Log("[WorkshopPlus] items " + (ok ? "loaded" : "not loaded") + ", Steam " + State());
            Show(w);
        }

        static string State()
        {
            try
            {
                bool init = SteamManager.Initialized;
                return "initialised=" + init + (init ? " running=" + SteamAPI.IsSteamRunning() + " loggedOn=" + SteamUser.BLoggedOn() : "");
            }
            catch (Exception e) { return "error " + e.Message; }
        }

        static bool SteamClientRunning()
        {
            try { return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0; } catch (Exception) { return true; }
        }

        static bool SteamUsable()
        {
            try { return SteamManager.Initialized && SteamAPI.IsSteamRunning() && SteamUser.BLoggedOn(); }
            catch (Exception) { return false; }
        }

        /// every frame from the list follower; checks the connection every 2 seconds
        public static void Update(Workshop w)
        {
            if (ws == null || Busy || !ready) return;
            if (Time.unscaledTime - lastCheck < 2f) return;
            lastCheck = Time.unscaledTime;
            bool problem = !SteamUsable();
            if (problem != Problem)
            {
                Problem = problem;
                Debug.Log("[WorkshopPlus] Steam " + (problem ? "lost" : "back") + ": " + State());
                Show(w);
                if (!problem)
                {
                    w.SetErrorMessage("Note: connected to Steam.");
                    if (!itemsLoaded) WorkshopPlus.ReloadItems(w);
                }
            }
            // while it's down: once the Steam client is running again, reconnect by ourselves every few seconds
            if (Problem && Time.unscaledTime - lastAuto > 5f && SteamClientRunning())
            {
                lastAuto = Time.unscaledTime;
                bool needInit = true;
                try { needInit = !SteamManager.Initialized || !SteamAPI.IsSteamRunning(); } catch (Exception) { }
                if (needInit) w.StartCoroutine(Reconnect(w, true));        // (if it's just logging on, the check above picks it up)
            }
        }

        /// the welcome panel: normal text, or the red Steam message with Reconnect
        public static void Show(Workshop w)
        {
            if (title == null || desc == null) { Debug.Log("[WorkshopPlus] Steam status: welcome text not found"); return; }
            if (titleText == null) { titleText = title.text; descText = desc.text; titleColor = title.color; descColor = desc.color; }
            if (Problem)
            {
                title.text = "Can't connect to Steam";
                desc.text = "Start the Steam client and log in, then click Reconnect.\nYour published items can't be listed, published or deleted until then.";
                title.color = desc.color = WorkshopPlus.ErrorRed;
                if (arrow != null) arrow.SetActive(false);
                w.message.SetActive(true);
                w.table.SetActive(false);
            }
            else if (title.text == "Can't connect to Steam")
            {
                title.text = titleText; desc.text = descText; title.color = titleColor; desc.color = descColor;
                if (arrow != null) arrow.SetActive(true);
                if (w.listView.activeSelf) w.ActivateListView();
            }
            if (reconnect != null) reconnect.SetActive(Problem);
        }

        static IEnumerator Reconnect(Workshop w, bool auto)
        {
            Busy = true;
            if (reconnectText != null) reconnectText.text = "Connecting...";
            yield return null; yield return null;
            bool ok = false;
            try
            {
                var mgr = SteamManager.s_instance;
                if (mgr != null && mgr.m_bInitialized) { try { SteamAPI.Shutdown(); } catch (Exception) { } mgr.m_bInitialized = false; }
                EnsureAppId();
                ok = SteamAPI.Init();
                if (ok && mgr != null) { mgr.m_bInitialized = true; SteamManager.s_EverInialized = true; }
                if (!auto || ok) Debug.Log("[WorkshopPlus] Steam reconnect" + (auto ? " (automatic)" : "") + ": " + (ok ? "connected" : "failed"));
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] Steam reconnect: " + e.Message); }
            // give it a moment to log on
            float t0 = Time.unscaledTime;
            while (ok && !SteamUsable() && Time.unscaledTime - t0 < 5f) { SteamAPI.RunCallbacks(); yield return null; }
            Problem = !SteamUsable();
            if (reconnectText != null) reconnectText.text = "Reconnect";
            Show(w);
            Busy = false;
            if (Problem) { if (!auto) w.SetErrorMessage("Still can't connect to Steam. Make sure the Steam client is running and you're logged in."); }
            else
            {
                w.SetErrorMessage("Note: connected to Steam.");
                if (!itemsLoaded) WorkshopPlus.ReloadItems(w);
            }
        }

        // Steam needs to know which game this is; when the tool wasn't started by Steam, take it from the
        // game's Steam library manifest (falls back to Tiny Town VR's app ID).
        static void EnsureAppId()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId"))) return;
            string id = "653930";
            try
            {
                var game = new DirectoryInfo(Path.GetDirectoryName(Application.dataPath));      // ...\steamapps\common\Tiny Town VR
                var steamapps = game.Parent != null ? game.Parent.Parent : null;
                if (steamapps != null)
                    foreach (var f in steamapps.GetFiles("appmanifest_*.acf"))
                    {
                        string txt = File.ReadAllText(f.FullName);
                        if (txt.IndexOf("\"installdir\"", StringComparison.OrdinalIgnoreCase) >= 0 && txt.IndexOf("\"" + game.Name + "\"", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string n = Path.GetFileNameWithoutExtension(f.Name).Substring("appmanifest_".Length);
                            uint u; if (uint.TryParse(n, out u)) { id = n; break; }
                        }
                    }
            }
            catch (Exception) { }
            Environment.SetEnvironmentVariable("SteamAppId", id);
            Environment.SetEnvironmentVariable("SteamGameId", id);
        }
    }
}
