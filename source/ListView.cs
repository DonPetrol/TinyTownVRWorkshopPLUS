// TTVR Workshop Plus - item list: search, sort, columns (thumbnail, title/path, vertices, visibility, last published),
// Steam / Duplicate buttons, selection with multi-delete and bulk visibility, clickable paths, and the confirm dialog.
// The list shows a filtered/sorted view of Workshop.activeItems; Edit/Delete always pass the item's real
// index in activeItems, so every original handler keeps working unchanged.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Steamworks;
using Tacticsoft;
using UnityEngine;
using UnityEngine.UI;

namespace TTVRPlus
{
    public static class WorkshopList
    {
        // sort modes (saved by number, so they keep their meaning): 0 latest, 1 oldest, 2 A-Z, 3 Z-A, 4 most vertices, 5 fewest, 6 visibility
        static readonly string[] SortNames = { "Latest published", "Oldest published", "Title A-Z", "Title Z-A", "Most vertices", "Fewest vertices", "Visibility" };
        static readonly int[] ObjectSortModes = { 0, 1, 2, 3, 6, 4, 5 }, WorldSortModes = { 0, 1, 2, 3, 6 };
        static int[] sortModes;
        static int SortMode() { return sort != null && sortModes != null && sort.value < sortModes.Length ? sortModes[sort.value] : 0; }
        const string SortPref = "WorkshopPlus.Sort";
        const int VertexLimit = 65000;

        static List<WorkshopItem> view = new List<WorkshopItem>();
        static readonly Dictionary<ulong, uint> updated = new Dictionary<ulong, uint>();   // Steam "last updated" (unix time)
        static readonly HashSet<WorkshopItem> selected = new HashSet<WorkshopItem>();
        static readonly Dictionary<WorkshopRow, WorkshopItem> liveCells = new Dictionary<WorkshopRow, WorkshopItem>();
        static InputField search;
        static Dropdown sort;
        static GameObject controls, noMatches, footerControls, verticesHeader;
        static Button selectAll;
        static Button deselectAll, fixSubs;
        static Action pendingYes, pendingNo;
        static Dropdown bulkVis; static bool settingBulk;
        static Button deleteSelected;
        static Text deleteSelectedText, confirmTitle;
        static string confirmTitleOriginal;
        static List<WorkshopItem> pendingMulti;
        static bool settingUp;
        static Workshop ws;

        public static void SetUpdated(ulong id, uint unixTime) { if (id != 0) updated[id] = unixTime; }

        // ---------------------------------------------------------------- set-up (from WorkshopPlus.Init)
        public static void Init(Workshop w)
        {
            ws = w;
            try { VertexCounter.Init(FileUtils.GetWorkshopDevelopmentDirectory()); } catch (Exception e) { Debug.Log("[WorkshopPlus] " + e.Message); }
            try
            {
                // ---- top bar: search + sort
                Transform header = w.listView.transform.parent.Find("Header");
                controls = new GameObject("WorkshopPlusListControls", typeof(RectTransform));
                var crt = (RectTransform)controls.transform;
                crt.SetParent(header, false);
                crt.anchorMin = crt.anchorMax = new Vector2(1f, 0f);
                crt.pivot = new Vector2(1f, 0f);
                crt.sizeDelta = new Vector2(400f, 40f);
                crt.anchoredPosition = new Vector2(-90f, 2f);

                var sGo = Clone(w.titleField.gameObject, crt, "SearchField", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(220f, 35f), Vector2.zero);
                search = sGo.GetComponent<InputField>();
                search.onValueChanged = new InputField.OnChangeEvent();     // drop the copied Title handlers
                search.onEndEdit = new InputField.SubmitEvent();
                search.lineType = InputField.LineType.SingleLine;
                search.characterLimit = 0;
                search.text = "";
                if (search.placeholder is Text) ((Text)search.placeholder).text = "Search titles and paths";
                WorkshopPlus.Larger(search.textComponent); WorkshopPlus.Larger(search.placeholder as Text);
                search.onValueChanged.AddListener(delegate (string s) { Refresh(true); });

                var dGo = Clone(w.visibilityDropdown.gameObject, crt, "SortDropdown", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(165f, 35f), new Vector2(-230f, 0f));
                sort = dGo.GetComponent<Dropdown>();
                sort.onValueChanged = new Dropdown.DropdownEvent();
                SortOptionsFor(true);
                sort.onValueChanged.AddListener(delegate (int i)
                {
                    if (settingSort) return;
                    PlayerPrefs.SetInt(SortPref, SortMode()); PlayerPrefs.Save(); Refresh(true);
                });
                Tooltip.Add(dGo, "Sort the list.");
                WorkshopPlus.Larger(sort.captionText);
                if (sort.captionText != null)
                {   // shrink rather than wrap onto two lines (e.g. "Latest published")
                    var ct = sort.captionText; int fs = ct.fontSize;
                    ct.horizontalOverflow = HorizontalWrapMode.Wrap; ct.verticalOverflow = VerticalWrapMode.Truncate;
                    ct.resizeTextForBestFit = true; ct.resizeTextMaxSize = fs; ct.resizeTextMinSize = Math.Max(8, fs / 2);
                }

                // ---- column headers
                Transform table = w.table.transform;
                Transform pathLabel = table.Find("PathColumnLabel");
                Transform titleLabel = table.Find("TitleColumnLabel");
                if (titleLabel != null)
                {
                    var r = (RectTransform)titleLabel; r.anchoredPosition = new Vector2(TitleLeft, r.anchoredPosition.y);
                    var tl = titleLabel.GetComponent<Text>(); tl.text = "Title / Path"; tl.horizontalOverflow = HorizontalWrapMode.Overflow;
                }
                if (pathLabel != null)
                {
                    pathLabel.gameObject.SetActive(false);
                    HeaderLabel(pathLabel, table, "UpdatedColumnLabel", "Last Published", UpdatedRight, UpdatedWidth, false);
                    verticesHeader = HeaderLabel(pathLabel, table, "VerticesColumnLabel", "Vertices", VertsRight, VertsWidth, true);
                    HeaderLabel(pathLabel, table, "VisibilityColumnLabel", "Visibility", VisRight, VisWidth, false);

                    var nGo = (GameObject)UnityEngine.Object.Instantiate(pathLabel.gameObject, table, false);
                    nGo.name = "NoMatches";
                    var nrt = (RectTransform)nGo.transform;
                    nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 0.5f);
                    nrt.pivot = new Vector2(0.5f, 0.5f);
                    nrt.sizeDelta = new Vector2(600f, 100f);
                    nrt.anchoredPosition = Vector2.zero;
                    var nt = nGo.GetComponent<Text>();
                    nt.text = "No items match your search or filter.";
                    nt.alignment = TextAnchor.MiddleCenter;
                    noMatches = nGo;
                    noMatches.SetActive(false);
                }

                // ---- list footer: select all, delete selected, vertex filter
                Transform footer = w.listView.transform.Find("Footer");
                Transform fc = w.footerContents.transform;
                footerControls = new GameObject("WorkshopPlusFooter", typeof(RectTransform));
                var frt = (RectTransform)footerControls.transform;
                frt.SetParent(footer, false);
                frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = frt.offsetMax = Vector2.zero;

                var pb = w.publishButton;
                var bGo = Clone(pb.gameObject, frt, "DeleteSelected", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(230f, 42f), new Vector2(292f, 0f));
                deleteSelected = bGo.GetComponent<Button>();
                deleteSelected.onClick = new Button.ButtonClickedEvent();     // drop the copied Publish handler
                deleteSelected.onClick.AddListener(OnDeleteSelected);
                Tooltip.Add(bGo, "Delete every ticked item (and remove published ones from the Steam Workshop).");
                if (deleteSelected.image != null) deleteSelected.image.color = new Color(0.85f, 0.25f, 0.25f, 1f);
                deleteSelectedText = bGo.GetComponentInChildren<Text>(); WorkshopPlus.FitButtonText(deleteSelectedText);

                // bulk visibility for the ticked items
                var bvGo = Clone(w.visibilityDropdown.gameObject, frt, "BulkVisibility", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(200f, 38f), new Vector2(532f, 0f));
                bulkVis = bvGo.GetComponent<Dropdown>();
                bulkVis.onValueChanged = new Dropdown.DropdownEvent();
                bulkVis.ClearOptions();
                bulkVis.AddOptions(new List<string> { "Set visibility...", "Private", "Friends only", "Public" });
                bulkVis.value = 0; bulkVis.RefreshShownValue();
                var bt = bulkVis.template;   // the footer is at the bottom of the window: open the list upwards
                if (bt != null) { bt.anchorMin = new Vector2(0f, 1f); bt.anchorMax = new Vector2(1f, 1f); bt.pivot = new Vector2(0.5f, 0f); bt.anchoredPosition = new Vector2(0f, 2f); }
                if (bulkVis.captionText != null) { var ct = bulkVis.captionText; int fs = ct.fontSize; ct.resizeTextForBestFit = true; ct.resizeTextMaxSize = fs; ct.resizeTextMinSize = Math.Max(8, fs / 2); }
                bulkVis.onValueChanged.AddListener(delegate (int i)
                {
                    if (i == 0) return;
                    int vis = i - 1;                       // 0 private, 1 friends, 2 public (like the editor)
                    settingBulk = true; bulkVis.value = 0; bulkVis.RefreshShownValue(); settingBulk = false;
                    AskBulkVisibility(w, vis);
                });
                Tooltip.Add(bvGo, "Change the Steam Workshop visibility of every ticked item at once.");

                var saGo = Clone(pb.gameObject, frt, "SelectAll", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(120f, 42f), new Vector2(16f, 0f));
                selectAll = saGo.GetComponent<Button>();
                selectAll.onClick = new Button.ButtonClickedEvent();          // drop the copied Publish handler
                selectAll.onClick.AddListener(delegate { SelectAllInView(true); });
                Tooltip.Add(saGo, "Tick every item currently shown (after search and filter).");
                var sat = saGo.GetComponentInChildren<Text>(); if (sat != null) { sat.text = "Select all"; WorkshopPlus.FitButtonText(sat); }

                var dsGo = Clone(pb.gameObject, frt, "DeselectAll", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(140f, 42f), new Vector2(144f, 0f));
                deselectAll = dsGo.GetComponent<Button>();
                deselectAll.onClick = new Button.ButtonClickedEvent();
                deselectAll.onClick.AddListener(DeselectAll);
                Tooltip.Add(dsGo, "Untick every item.");
                var dst = dsGo.GetComponentInChildren<Text>(); if (dst != null) { dst.text = "Deselect all"; WorkshopPlus.FitButtonText(dst); }

                // bottom right: bring the game's downloaded subscriptions in line with Steam
                var fsGo = Clone(pb.gameObject, frt, "FixSubscriptions", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(190f, 42f), new Vector2(-16f, 0f));
                fixSubs = fsGo.GetComponent<Button>();
                fixSubs.onClick = new Button.ButtonClickedEvent();
                fixSubs.onClick.AddListener(delegate { Subscriptions.Start(w); });
                var fst = fsGo.GetComponentInChildren<Text>(); if (fst != null) { fst.text = "Fix Subscriptions"; WorkshopPlus.FitButtonText(fst); }
                Tooltip.Add(fsGo, "Fix new workshop subscriptions not loading or old ones stuck in the menu.");

                // confirmation dialog title (reused for multi-delete)
                if (w.confirmationView != null)
                {
                    var ct = w.confirmationView.transform.Find("Title");
                    if (ct != null) { confirmTitle = ct.GetComponent<Text>(); confirmTitleOriginal = confirmTitle.text; }
                }

                UpdateSelectionUi();
                w.gameObject.AddComponent<ListControlsFollower>();
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] could not add list controls: " + e); }
        }

        static GameObject Clone(GameObject src, Transform parent, string name, Vector2 anchor, Vector2 pivot, Vector2 size, Vector2 pos)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(src, parent, false);
            go.name = name;
            go.SetActive(true);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = pivot; rt.sizeDelta = size; rt.anchoredPosition = pos;
            return go;
        }

        static GameObject HeaderLabel(Transform template, Transform table, string name, string text, float right, float width, bool alignRight)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(template.gameObject, table, false);
            go.name = name;
            go.SetActive(true);
            var rt = (RectTransform)go.transform; var tr = (RectTransform)template;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(alignRight ? 1f : 0f, 0.5f);
            rt.sizeDelta = new Vector2(width, tr.sizeDelta.y);
            // rows are 20px narrower than the table (scrollbar)
            rt.anchoredPosition = new Vector2(alignRight ? -(right + 20f) : -(right + width + 20f), tr.anchoredPosition.y);
            var txt = go.GetComponent<Text>(); txt.text = text;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.alignment = alignRight ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            return go;
        }

        // ---------------------------------------------------------------- Workshop.ActivateListView replacement
        public static void ActivateListView(Workshop w)
        {
            FileCache.Clear();                      // coming back to the list (or switching tab): look at the files again
            ActivateListViewCore(w);
        }

        static void ActivateListViewCore(Workshop w)
        {
            ws = w;
            view = BuildView(w.activeItems, ObjectsTab(w));
            w.tableViewController.WorkshopItems = view;
            w.table.SetActive(w.activeItems.Count > 0);
            w.message.SetActive(w.activeItems.Count == 0);
            if (noMatches != null) noMatches.SetActive(w.activeItems.Count > 0 && view.Count == 0);
            if (verticesHeader != null) verticesHeader.SetActive(ObjectsTab(w));
            SortOptionsFor(ObjectsTab(w));
            if (SteamStatus.Problem) SteamStatus.Show(w);
            w.listView.SetActive(true);
            w.detailView.SetActive(false);
            UpdateSelectionUi();
        }

        static bool settingSort;
        // the Worlds tab has no vertex counts, so its sort list stops before "Most/Fewest vertices"
        static void SortOptionsFor(bool objects)
        {
            if (sort == null) return;
            var modes = objects ? ObjectSortModes : WorldSortModes;
            if (sortModes == modes) return;
            settingSort = true;
            sortModes = modes;
            sort.ClearOptions();
            var names = new List<string>();
            foreach (int m in modes) names.Add(SortNames[m]);
            sort.AddOptions(names);
            int saved = PlayerPrefs.GetInt(SortPref, 0);
            int at = Array.IndexOf(modes, saved);
            sort.value = at >= 0 ? at : 0;
            sort.RefreshShownValue();
            settingSort = false;
        }

        static void Refresh(bool toTop)
        {
            if (ws == null || ws.activeItems == null || !ws.listView.activeSelf) return;
            float y = ws.tableView.scrollY;
            ActivateListViewCore(ws);
            ws.tableView.ReloadData();
            ws.tableView.scrollY = toTop ? 0f : y;
        }

        static long SortTime(WorkshopItem it)
        {
            if (it.id == 0) return long.MaxValue;              // not published yet: treat as newest
            uint t; return updated.TryGetValue(it.id, out t) ? t : 0;
        }

        static int Verts(WorkshopItem it)
        {
            return VertexCounter.Get(it.sourceFile, (int)ItemSettings.Get(it), ItemSettings.GetAngle(it), it.textureFile, ItemSettings.GetCrop(it));
        }

        static List<WorkshopItem> BuildView(List<WorkshopItem> items, bool objects)
        {
            string q = search != null ? (search.text ?? "").Trim() : "";
            int mode = SortMode();
            bool byVerts = mode == 4 || mode == 5;
            if (!objects && byVerts) mode = 0;
            bool needVerts = objects && byVerts;
            var counts = new Dictionary<WorkshopItem, int>();
            var idx = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (q.Length > 0 && !Contains(it.title, q) && !Contains(it.sourceFile, q)) continue;
                if (needVerts)
                {
                    counts[it] = Verts(it);
                }
                idx.Add(i);
            }
            idx.Sort(delegate (int a, int b)
            {
                WorkshopItem x = items[a], y = items[b];
                int c = 0;
                switch (mode)
                {
                    case 0: c = SortTime(y).CompareTo(SortTime(x)); break;
                    case 1: c = SortTime(x).CompareTo(SortTime(y)); break;
                    case 2: c = string.Compare(x.title ?? "", y.title ?? "", StringComparison.OrdinalIgnoreCase); break;
                    case 3: c = string.Compare(y.title ?? "", x.title ?? "", StringComparison.OrdinalIgnoreCase); break;
                    case 4: c = (objects ? counts[y] : 0).CompareTo(objects ? counts[x] : 0); break;
                    case 5:
                        if (objects)
                        {   // unknown counts go last
                            int vx = counts[x] < 0 ? int.MaxValue : counts[x], vy = counts[y] < 0 ? int.MaxValue : counts[y];
                            c = vx.CompareTo(vy);
                        }
                        break;
                    case 6:   // Private, then Friends, then Public; latest published first within each
                        c = x.visibility.CompareTo(y.visibility);
                        if (c == 0) c = SortTime(y).CompareTo(SortTime(x));
                        break;
                }
                return c != 0 ? c : a.CompareTo(b);            // stable
            });
            var res = new List<WorkshopItem>(idx.Count);
            foreach (int i in idx) res.Add(items[i]);
            return res;
        }

        static bool Contains(string s, string q) { return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0; }

        // called once the published items have loaded: count everything in the background
        public static void PrefetchCounts(Workshop w)
        {
            foreach (var it in w.objectItems) Verts(it);
        }

        // ---------------------------------------------------------------- rows (DynamicHeightTableViewController.GetCellForRowInTableView replacement)
        //  [x] [thumb] Title / path (stacked) | Vertices | Last Published | Duplicate | Edit | X
        const float ThumbLeft = 38f, ThumbSize = 84f, TitleLeft = 132f;
        const float DupRight = 185f, DupWidth = 100f;
        const float SteamRight = 290f, SteamWidth = 72f;            // "Steam" button, after Last Published
        const float UpdatedRight = 372f, UpdatedWidth = 120f;
        const float VisRight = 520f, VisWidth = 135f;               // Visibility, between Vertices and Last Published (room for its bold header)
        const float VertsRight = 667f, VertsWidth = 85f;
        const float TextRight = VertsRight + VertsWidth + 15f;     // title/path stop here
        static readonly Color MissingRed = new Color(1f, 0f, 0f, 1f);

        public static TableViewCell GetCell(DynamicHeightTableViewController c, TableView tableView, int row)
        {
            var cell = tableView.GetReusableCell(c.m_cellPrefab.reuseIdentifier) as WorkshopRow;
            if (cell == null)
            {
                cell = UnityEngine.Object.Instantiate(c.m_cellPrefab);
                cell.name = "WorkshopRow_" + (++created);
            }
            var parts = EnsureColumns(cell);
            if (row < 0 || row >= view.Count) return cell;
            WorkshopItem item = view[row];
            Workshop w = c.workshop;
            liveCells[cell] = item;

            Fit(cell.transform.Find("Title")).Set(string.IsNullOrEmpty(item.title) ? "Unknown" : item.title);
            var pathT = cell.transform.Find("Path");
            bool noPath = string.IsNullOrEmpty(item.sourceFile);
            bool missing = !noPath && !FileCache.Exists(item.sourceFile);
            Fit(pathT).Set(noPath ? (ObjectsTab(w) ? "Unknown (click Edit to locate the model)" : "Unknown") : item.sourceFile);
            parts.Path.color = missing || noPath ? MissingRed : parts.PathColor;
            var pathBtn = pathT.GetComponent<Button>();
            pathBtn.onClick.RemoveAllListeners();
            pathBtn.onClick.AddListener(delegate { if (string.IsNullOrEmpty(item.sourceFile)) { if (ObjectsTab(w)) OnEdit(w, item); } else ShowInExplorer(item.sourceFile); });

            parts.Updated.text = FormatTime(item);
            if (parts.Vis != null) parts.Vis.text = item.visibility == 2 ? "Public" : item.visibility == 1 ? "Friends" : "Private";
            if (parts.Steam != null)
            {
                parts.Steam.interactable = item.id != 0;
                parts.Steam.onClick.RemoveAllListeners();
                ulong sid = item.id;
                parts.Steam.onClick.AddListener(delegate { if (sid != 0) Application.OpenURL("steam://url/CommunityFilePage/" + sid); });
            }
            SetThumb(parts, item);
            SetVerts(parts, item, ObjectsTab(w));

            settingUp = true;
            parts.Select.isOn = selected.Contains(item);
            settingUp = false;
            parts.Select.onValueChanged.RemoveAllListeners();
            parts.Select.onValueChanged.AddListener(delegate (bool on) { if (settingUp) return; if (on) selected.Add(item); else selected.Remove(item); UpdateSelectionUi(); });

            parts.Dup.onClick.RemoveAllListeners();
            parts.Dup.onClick.AddListener(delegate { Duplicate(w, item); });
            var edit = cell.transform.Find("EditButton").GetComponent<Button>();
            edit.onClick.RemoveAllListeners();
            edit.onClick.AddListener(delegate { OnEdit(w, item); });
            var del = cell.transform.Find("DeleteButton").GetComponent<Button>();
            del.onClick.RemoveAllListeners();
            del.onClick.AddListener(delegate { AskDelete(new List<WorkshopItem> { item }); });
            return cell;
        }

        static void SetVerts(RowParts p, WorkshopItem item, bool objects)
        {
            Text t = p.Verts;
            if (t == null) return;
            if (!objects) { t.text = ""; return; }
            int v = Verts(item);
            t.color = v > VertexLimit ? MissingRed : p.VertsColor;
            switch (v)
            {
                case VertexCounter.Pending: t.text = "..."; break;
                case VertexCounter.NoPath: case VertexCounter.Missing: t.text = "-"; break;
                case VertexCounter.Failed: t.text = "error"; break;
                default: t.text = v.ToString("N0", CultureInfo.InvariantCulture); break;
            }
        }

        static int created;

        // which list is on screen. Workshop's tab buttons refresh the list *before* they change objectTabActive,
        // so the list being shown is the reliable answer.
        static bool ObjectsTab(Workshop w) { return w.activeItems == null || w.activeItems == w.objectItems; }

        static bool IsWorld(WorkshopItem item) { return item != null && ws != null && ws.worldItems != null && ws.worldItems.Contains(item); }

        static void SetThumb(RowParts p, WorkshopItem item)
        {
            if (p.Thumb == null) return;
            Texture2D t = Thumbnails.Get(item.id);
            if (t == null)
            {   // nothing on Steam (yet): a custom thumbnail, or a world's own preview.jpg
                string custom = ThumbPicker.CustomFile(item);
                if (custom != null && FileCache.Exists(custom)) t = Thumbnails.GetLocal(custom);
                else if (IsWorld(item)) t = Thumbnails.GetLocal(item.textureFile);
            }
            p.Thumb.texture = t;
            p.Thumb.enabled = t != null;
            if (t != null)
            {   // fit inside the box, keeping the image's shape
                float a = (float)t.width / Mathf.Max(1, t.height);
                p.ThumbRect.sizeDelta = a >= 1f ? new Vector2(ThumbSize, ThumbSize / a) : new Vector2(ThumbSize * a, ThumbSize);
            }
        }

        class RowParts
        {
            public Text Path, Updated, Verts, Vis; public Button Steam; public Color PathColor, VertsColor; public Toggle Select; public Button Dup;
            public RawImage Thumb; public RectTransform ThumbRect;
        }
        static readonly Dictionary<WorkshopRow, RowParts> rowParts = new Dictionary<WorkshopRow, RowParts>();

        static RowParts EnsureColumns(WorkshopRow cell)
        {
            RowParts p;
            if (rowParts.TryGetValue(cell, out p)) return p;
            p = new RowParts();
            Transform title = cell.transform.Find("Title");
            Transform path = cell.transform.Find("Path");
            Transform edit = cell.transform.Find("EditButton");

            // title on top, path underneath; both stop before the Vertices column, wrap, and end in "..."
            var trt = (RectTransform)title;
            trt.anchorMin = new Vector2(0f, 0.5f); trt.anchorMax = new Vector2(1f, 0.5f); trt.pivot = new Vector2(0f, 0f);
            trt.sizeDelta = new Vector2(-(TitleLeft + TextRight), 46f);
            trt.anchoredPosition = new Vector2(TitleLeft, 1f);
            var titleText = title.GetComponent<Text>();
            Clip(titleText);
            titleText.alignment = TextAnchor.LowerLeft;

            var prt = (RectTransform)path;
            prt.anchorMin = new Vector2(0f, 0.5f); prt.anchorMax = new Vector2(1f, 0.5f); prt.pivot = new Vector2(0f, 1f);
            prt.sizeDelta = new Vector2(-(TitleLeft + TextRight), 46f);
            prt.anchoredPosition = new Vector2(TitleLeft, -1f);
            p.Path = path.GetComponent<Text>();
            p.PathColor = p.Path.color;
            Clip(p.Path);
            p.Path.alignment = TextAnchor.UpperLeft;
            p.Path.raycastTarget = true;
            var pbtn = path.gameObject.AddComponent<Button>();      // click the path to show the file in Explorer
            pbtn.transition = Selectable.Transition.None;
            pbtn.targetGraphic = p.Path;
            path.gameObject.AddComponent<HoverUnderline>();        // underline on hover so it reads as a link

            // thumbnail (Steam Workshop preview), fitted inside a square box
            var thumbBox = new GameObject("ThumbBox", typeof(RectTransform));
            var tbr = (RectTransform)thumbBox.transform;
            tbr.SetParent(cell.transform, false);
            tbr.anchorMin = tbr.anchorMax = new Vector2(0f, 0.5f); tbr.pivot = new Vector2(0f, 0.5f);
            tbr.sizeDelta = new Vector2(ThumbSize, ThumbSize); tbr.anchoredPosition = new Vector2(ThumbLeft, 0f);
            var bg = thumbBox.AddComponent<Image>(); bg.color = new Color(0f, 0f, 0f, 0.08f); bg.raycastTarget = false;
            var thumbGo = new GameObject("Thumb", typeof(RectTransform));
            p.ThumbRect = (RectTransform)thumbGo.transform;
            p.ThumbRect.SetParent(tbr, false);
            p.ThumbRect.anchorMin = p.ThumbRect.anchorMax = p.ThumbRect.pivot = new Vector2(0.5f, 0.5f);
            p.ThumbRect.sizeDelta = new Vector2(ThumbSize, ThumbSize);
            p.Thumb = thumbGo.AddComponent<RawImage>(); p.Thumb.raycastTarget = false;

            p.Updated = Column(path, cell, "Updated", UpdatedRight, UpdatedWidth);
            p.Verts = Column(path, cell, "Vertices", VertsRight, VertsWidth);
            p.Vis = Column(path, cell, "Visibility", VisRight, VisWidth);
            p.Verts.alignment = TextAnchor.MiddleRight;
            p.VertsColor = p.Verts.color;
            p.Updated.color = p.VertsColor = p.Vis.color = p.PathColor;

            // duplicate button: a copy of Edit
            var dGo = (GameObject)UnityEngine.Object.Instantiate(edit.gameObject, cell.transform, false);
            dGo.name = "DuplicateButton";
            var drt = (RectTransform)dGo.transform;
            drt.anchoredPosition = new Vector2(-DupRight, drt.anchoredPosition.y);
            drt.sizeDelta = new Vector2(DupWidth, drt.sizeDelta.y);
            var dt = dGo.GetComponentInChildren<Text>();
            if (dt != null)
            {
                dt.text = "Duplicate";
                int fs = dt.fontSize;
                dt.resizeTextForBestFit = true; dt.resizeTextMaxSize = fs; dt.resizeTextMinSize = Math.Max(8, fs / 2);
                var dtr = dt.rectTransform; dtr.anchorMin = Vector2.zero; dtr.anchorMax = Vector2.one; dtr.offsetMin = new Vector2(4f, 2f); dtr.offsetMax = new Vector2(-4f, -2f);
            }
            Tooltip.Add(dGo, "Make an unpublished copy of this item just below it.");

            // "Steam" button: the item's Workshop page
            var sGo = (GameObject)UnityEngine.Object.Instantiate(dGo, cell.transform, false);
            sGo.name = "SteamButton";
            var srt = (RectTransform)sGo.transform;
            srt.anchoredPosition = new Vector2(-SteamRight, srt.anchoredPosition.y);
            srt.sizeDelta = new Vector2(SteamWidth, srt.sizeDelta.y);
            var stx = sGo.GetComponentInChildren<Text>(); if (stx != null) stx.text = "Steam";
            p.Steam = sGo.GetComponent<Button>();
            p.Steam.onClick = new Button.ButtonClickedEvent();
            Tooltip.Add(sGo, "Open this item's Steam Workshop page (in the Steam client). Only for published items.");
            Tooltip.Add(edit.gameObject, "Open this item in the editor.");
            var delT = cell.transform.Find("DeleteButton");
            if (delT != null) Tooltip.Add(delT.gameObject, "Delete this item (and remove it from the Steam Workshop if it's published).");
            Tooltip.Add(path.gameObject, "Show the model file in File Explorer.");
            p.Dup = dGo.GetComponent<Button>();
            p.Dup.onClick = new Button.ButtonClickedEvent();

            // selection tick box: a copy of the Flip toggle
            var src = ws != null ? ws.footerContents.transform.Find("FlipToggle") : null;
            if (src != null)
            {
                var tGo = (GameObject)UnityEngine.Object.Instantiate(src.gameObject, cell.transform, false);
                tGo.name = "SelectToggle";
                tGo.SetActive(true);
                var rt = (RectTransform)tGo.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(20f, 20f); rt.anchoredPosition = new Vector2(20f, 0f);
                p.Select = tGo.GetComponent<Toggle>();
                Tooltip.Add(tGo, "Tick to include this item in Delete selected.");
                p.Select.onValueChanged = new Toggle.ToggleEvent();
            }
            rowParts[cell] = p;
            return p;
        }

        static Text Column(Transform template, WorkshopRow cell, string name, float right, float width)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(template.gameObject, cell.transform, false);
            go.name = name;
            var ft = go.GetComponent<FitText>(); if (ft != null) UnityEngine.Object.Destroy(ft);
            var b = go.GetComponent<Button>(); if (b != null) UnityEngine.Object.Destroy(b);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(width, 90f);
            rt.anchoredPosition = new Vector2(-(right + width), 0f);
            var txt = go.GetComponent<Text>();
            txt.alignment = TextAnchor.MiddleLeft;
            txt.raycastTarget = false;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            return txt;
        }

        static void Clip(Text t)
        {
            if (t == null) return;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.resizeTextForBestFit = false;
        }

        static FitText Fit(Transform tr)
        {
            var f = tr.GetComponent<FitText>();
            return f != null ? f : tr.gameObject.AddComponent<FitText>();
        }

        // ---------------------------------------------------------------- path -> Explorer
        static void ShowInExplorer(string path)
        {
            try
            {
                if (File.Exists(path)) { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); return; }
                string dir = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir)) { ws.SetErrorMessage("Can't open the folder: neither the file nor its folder exists any more."); return; }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                ws.SetErrorMessage("Note: " + Path.GetFileName(path) + " isn't there any more, so its nearest existing folder was opened.");
            }
            catch (Exception e)
            {
                Debug.Log("[WorkshopPlus] " + e);
                Application.OpenURL("file:///" + Path.GetDirectoryName(path).Replace('\\', '/'));
            }
        }

        // ---------------------------------------------------------------- edit / duplicate
        // Edit: entries with no saved model path (made on another PC, or before the tool kept records) ask for the model first
        static void OnEdit(Workshop w, WorkshopItem item)
        {
            int i = w.activeItems.IndexOf(item);
            if (i < 0) return;
            if (ObjectsTab(w) && string.IsNullOrEmpty(item.sourceFile))
            {
                var filters = new SFB.ExtensionFilter[] { new SFB.ExtensionFilter("3D Models (OBJ, glTF, GLB) ", "obj", "gltf", "glb") };
                string[] sel = SFB.StandaloneFileBrowser.OpenFilePanel("Locate the model for \"" + item.title + "\"", "", filters, false);
                if (sel == null || sel.Length == 0 || string.IsNullOrEmpty(sel[0])) return;
                string p = sel[0].StartsWith("file://") ? sel[0].Substring(7) : sel[0];
                if (!File.Exists(p)) return;
                item.sourceFile = p;
                if (item.scale == 0f) item.scale = 1f;
                w.AddOrUpdateMetadata(item);
                Debug.Log("[WorkshopPlus] set model for '" + item.title + "': " + p);
            }
            w.OnEditButtonClicked(i);
        }

        // a new, unpublished entry with the same model, texture and settings
        static void Duplicate(Workshop w, WorkshopItem src)
        {
            var copy = new WorkshopItem();
            copy.id = 0;
            copy.title = (src.title ?? "") + " (copy)";
            copy.description = src.description;
            copy.visibility = src.visibility;
            copy.sourceFile = src.sourceFile;
            copy.textureFile = src.textureFile;
            copy.scale = src.scale == 0f ? 1f : src.scale;
            copy.rotation = src.rotation;
            bool world = !string.IsNullOrEmpty(src.uniqueDirectory) && src.uniqueDirectory.Contains("_world");
            string baseDir = (world ? "wi_world_" : "wi_") + DateTime.Now.ToString("dMyyyyHHmmss", CultureInfo.InvariantCulture);
            string dir = baseDir; int n = 2;
            while (w.objectItems.Exists(x => x.uniqueDirectory == dir) || w.worldItems.Exists(x => x.uniqueDirectory == dir)) dir = baseDir + "_" + (n++);
            copy.uniqueDirectory = dir;
            ItemSettings.Copy(src, copy);
            ThumbPicker.CopyCustom(src, copy);
            int at = w.activeItems.IndexOf(src);
            w.activeItems.Insert(at >= 0 ? at + 1 : w.activeItems.Count, copy);
            Refresh(false);
            w.SetErrorMessage("Note: duplicated \"" + src.title + "\". Edit the copy and press Publish to create it as a new Workshop item.");
        }

        // ---------------------------------------------------------------- selection + multi-delete
        static int SelectedInList()
        {
            if (ws == null || ws.activeItems == null) return 0;
            selected.RemoveWhere(x => !ws.objectItems.Contains(x) && !ws.worldItems.Contains(x));
            int n = 0; foreach (var it in ws.activeItems) if (selected.Contains(it)) n++;
            return n;
        }

        static void UpdateSelectionUi()
        {
            int n = SelectedInList();
            if (deleteSelected != null)
            {
                deleteSelected.interactable = n > 0;
                if (deleteSelectedText != null) deleteSelectedText.text = n > 0 ? "Delete selected (" + n + ")" : "Delete selected";
            }
            if (deselectAll != null) deselectAll.interactable = selected.Count > 0;
            if (selectAll != null)
            {
                bool all = view.Count > 0; foreach (var it in view) if (!selected.Contains(it)) { all = false; break; }
                selectAll.interactable = !all;
            }
        }

        // clears every selection, including items hidden by the search or filter
        static void DeselectAll()
        {
            selected.Clear();
            settingUp = true;
            foreach (var kv in liveCells) { RowParts p; if (rowParts.TryGetValue(kv.Key, out p) && p.Select != null) p.Select.isOn = false; }
            settingUp = false;
            UpdateSelectionUi();
        }

        // keep the search/sort controls clear of a button added to the top bar (night mode)
        public static void MakeRoomForHeaderButton(RectTransform b)
        {
            if (controls == null || b == null) return;
            var crt = (RectTransform)controls.transform;
            var header = crt.parent as RectTransform;
            if (header == null) return;
            Canvas.ForceUpdateCanvases();
            var wc = new Vector3[4]; b.GetWorldCorners(wc);
            Vector3 bl = header.InverseTransformPoint(wc[0]), tr = header.InverseTransformPoint(wc[2]);
            var cc = new Vector3[4]; crt.GetWorldCorners(cc);
            Vector3 cbl = header.InverseTransformPoint(cc[0]), ctr = header.InverseTransformPoint(cc[2]);
            bool vertOverlap = bl.y < ctr.y && tr.y > cbl.y;
            if (!vertOverlap || ctr.x <= bl.x - 10f) return;
            crt.anchoredPosition = new Vector2(crt.anchoredPosition.x - (ctr.x - (bl.x - 10f)), crt.anchoredPosition.y);
        }

        static void SelectAllInView(bool on)
        {
            foreach (var it in view) { if (on) selected.Add(it); else selected.Remove(it); }
            settingUp = true;
            foreach (var kv in liveCells) { RowParts p; if (rowParts.TryGetValue(kv.Key, out p) && p.Select != null) p.Select.isOn = selected.Contains(kv.Value); }
            settingUp = false;
            UpdateSelectionUi();
        }

        static void OnDeleteSelected()
        {
            if (ws == null) return;
            var list = new List<WorkshopItem>();
            foreach (var it in ws.activeItems) if (selected.Contains(it)) list.Add(it);
            AskDelete(list);
        }

        // both the row X and "Delete selected": same "are you sure" dialog, then removed from the Steam Workshop too
        static void AskDelete(List<WorkshopItem> list)
        {
            if (ws == null || list.Count == 0) return;
            pendingMulti = list;
            int published = list.FindAll(x => x.id != 0).Count;
            if (confirmTitle != null)
            {
                string what = list.Count == 1 ? "\"" + (string.IsNullOrEmpty(list[0].title) ? "this item" : list[0].title) + "\"" : list.Count + " items";
                string steam = published == 0 ? "" : list.Count == 1 ? "\nIt will also be removed from the Steam Workshop."
                             : "\n" + published + " of them will also be removed from the Steam Workshop.";
                confirmTitle.text = "Are you sure you want to delete " + what + "?" + steam;
            }
            ws.confirmationView.SetActive(true);
        }

        /// the "are you sure" dialog for something other than deleting (Fix subscriptions)
        public static void AskConfirm(string question) { AskConfirm(question, delegate { Subscriptions.Confirmed(ws); }, delegate { Subscriptions.Cancelled(ws); }, "Fix"); }

        public static readonly Color ConfirmBlue = new Color(0.12f, 0.66f, 0.85f, 1f);
        static Button confirmButton; static Text confirmButtonText; static string confirmLabelOriginal; static Color confirmColorOriginal;

        // the dialog's red "Delete" button; for other questions it's relabelled and turned blue
        static void SetConfirmButton(string label)
        {
            if (confirmButton == null && ws != null)
                foreach (var b in ws.confirmationView.GetComponentsInChildren<Button>(true))
                    for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                        if (b.onClick.GetPersistentMethodName(i) == "OnDeleteItemConfirmed")
                        {
                            confirmButton = b; confirmButtonText = b.GetComponentInChildren<Text>();
                            confirmLabelOriginal = confirmButtonText != null ? confirmButtonText.text : null;
                            confirmColorOriginal = b.image != null ? b.image.color : Color.white;
                        }
            if (confirmButton == null) return;
            if (confirmButtonText != null) confirmButtonText.text = label ?? confirmLabelOriginal;
            if (confirmButton.image != null) confirmButton.image.color = label == null ? confirmColorOriginal : ConfirmBlue;
        }

        public static void AskConfirm(string question, Action yes, Action no, string button)
        {
            if (ws == null) return;
            pendingMulti = null; pendingYes = yes; pendingNo = no;
            SetConfirmButton(button);
            if (confirmTitle != null) confirmTitle.text = question;
            ws.confirmationView.SetActive(true);
        }

        static void RestoreConfirmTitle()
        {
            if (confirmTitle != null && confirmTitleOriginal != null) confirmTitle.text = confirmTitleOriginal;
            SetConfirmButton(null);                  // back to the red "Delete"
        }

        // prefix of Workshop.OnDeleteItemConfirmed: true = handled here (multi-delete)
        public static bool OnDeleteConfirmed(Workshop w)
        {
            RestoreConfirmTitle();
            if (pendingYes != null) { var a = pendingYes; pendingYes = pendingNo = null; w.confirmationView.SetActive(false); a(); return true; }
            if (pendingMulti == null) return false;
            var list = pendingMulti; pendingMulti = null;
            w.confirmationView.SetActive(false);
            w.StartCoroutine(DeleteMany(w, list));
            return true;
        }

        // prefix of Workshop.OnDeleteItemCancelled
        public static void OnDeleteCancelled(Workshop w)
        {
            pendingMulti = null; RestoreConfirmTitle();
            if (pendingNo != null || pendingYes != null) { var a = pendingNo; pendingYes = pendingNo = null; if (a != null) a(); }
        }

        static readonly string[] VisNames = { "Private", "Friends only", "Public" };

        static void AskBulkVisibility(Workshop w, int vis)
        {
            if (settingBulk) return;
            var list = new List<WorkshopItem>();
            foreach (var it in view) if (selected.Contains(it)) list.Add(it);
            if (list.Count == 0) { w.SetErrorMessage("Note: tick the items to change first."); return; }
            int published = list.FindAll(x => x.id != 0).Count;
            AskConfirm("Set " + (list.Count == 1 ? "\"" + list[0].title + "\"" : list.Count + " items") + " to " + VisNames[vis] + "?" +
                       (published > 0 ? "\nThis changes " + (published == list.Count ? (list.Count == 1 ? "it" : "them") : published + " of them") + " on the Steam Workshop straight away." : ""),
                       delegate { w.StartCoroutine(SetVisibilityMany(w, list, vis)); }, null, "Change");
        }

        // visibility only (no re-upload), 8 at a time
        static IEnumerator SetVisibilityMany(Workshop w, List<WorkshopItem> list, int vis)
        {
            var ev = vis == 2 ? ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic
                   : vis == 1 ? ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly
                   : ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate;
            var toSteam = new List<WorkshopItem>();
            int done = 0, failed = 0;
            foreach (var it in list) { if (it.id == 0) { it.visibility = vis; done++; } else toSteam.Add(it); }
            if (toSteam.Count > 0 && !SteamManager.Initialized)
            {
                w.SetErrorMessage("Unable to change visibility. Make sure Steam client is running and logged in.");
                Refresh(false); yield break;
            }
            var calls = new List<CallResult<RemoteStorageUpdatePublishedFileResult_t>>();
            int pending = 0, next = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while ((next < toSteam.Count || pending > 0) && timer.Elapsed.TotalSeconds < 60)
            {
                while (next < toSteam.Count && pending < 8)
                {
                    var it = toSteam[next++];
                    var h = SteamRemoteStorage.CreatePublishedFileUpdateRequest(new PublishedFileId_t(it.id));
                    SteamRemoteStorage.UpdatePublishedFileVisibility(h, ev);
                    var cr = CallResult<RemoteStorageUpdatePublishedFileResult_t>.Create(
                        delegate (RemoteStorageUpdatePublishedFileResult_t r, bool io)
                        {
                            pending--;
                            if (!io && r.m_eResult == EResult.k_EResultOK) { it.visibility = vis; done++; }
                            else { failed++; Debug.Log("[WorkshopPlus] couldn't change visibility of " + it.id + " (" + r.m_eResult + ")"); }
                        });
                    cr.Set(SteamRemoteStorage.CommitPublishedFileUpdate(h));
                    calls.Add(cr); pending++;
                }
                SteamAPI.RunCallbacks();
                w.SetErrorMessage("Note: changing visibility " + (done + failed) + " of " + list.Count + "...");
                yield return null;
            }
            foreach (var c in calls) c.Dispose();
            failed += pending;
            Refresh(false);
            if (failed > 0) w.SetErrorMessage("Unable to change the visibility of " + failed + " of " + list.Count + " items on Steam. Make sure Steam is running, then try again.");
            else w.SetErrorMessage("Note: " + done + " item" + (done == 1 ? "" : "s") + " set to " + VisNames[vis] + ".");
        }

        static IEnumerator DeleteMany(Workshop w, List<WorkshopItem> list)
        {
            int done = 0, failed = 0, total = list.Count;
            var toSteam = new List<WorkshopItem>();
            foreach (var it in list)
            {
                if (it.id == 0) { Remove(w, it); done++; }
                else toSteam.Add(it);
            }
            if (toSteam.Count > 0 && !SteamManager.Initialized)
            {
                w.SetErrorMessage("Unable to delete published items. Make sure Steam client is running and logged in.");
                Refresh(false); yield break;
            }
            var calls = new List<CallResult<RemoteStorageDeletePublishedFileResult_t>>();
            int pending = 0, next = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while ((next < toSteam.Count || pending > 0) && timer.Elapsed.TotalSeconds < 60)
            {
                while (next < toSteam.Count && pending < 8)
                {
                    var it = toSteam[next++];
                    var cr = CallResult<RemoteStorageDeletePublishedFileResult_t>.Create(
                        delegate (RemoteStorageDeletePublishedFileResult_t r, bool io)
                        {
                            pending--;
                            if (!io && r.m_eResult == EResult.k_EResultOK) { Remove(w, it); done++; }
                            else { failed++; Debug.Log("[WorkshopPlus] couldn't delete " + it.id + " (" + r.m_eResult + ")"); }
                        });
                    cr.Set(SteamRemoteStorage.DeletePublishedFile(new PublishedFileId_t(it.id)));
                    calls.Add(cr); pending++;
                }
                SteamAPI.RunCallbacks();
                w.SetErrorMessage("Note: deleting " + (done + failed) + " of " + total + "...");
                yield return null;
            }
            foreach (var c in calls) c.Dispose();
            failed += pending;     // timed out
            Refresh(false);
            if (failed > 0) w.SetErrorMessage("Unable to delete " + failed + " of " + total + " items from Steam. Make sure Steam is running, then try again.");
            else w.SetErrorMessage("Note: deleted " + done + " item" + (done == 1 ? "" : "s") + ".");
        }

        static void Remove(Workshop w, WorkshopItem it)
        {
            w.objectItems.Remove(it); w.worldItems.Remove(it); selected.Remove(it);
        }

        // ---------------------------------------------------------------- dates
        static string FormatTime(WorkshopItem it)
        {
            if (it.id == 0) return "Not published";
            uint t;
            if (!updated.TryGetValue(it.id, out t) || t == 0) return "";
            DateTime d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t).ToLocalTime();
            return d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + "\n" + d.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- after Workshop.OnPublishButtonClicked
        public static void AfterPublish(Workshop w, WorkshopItem item)
        {
            if (item == null || item.id == 0) return;
            SetUpdated(item.id, (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        }

        /// draws a line under each line of a Text while the mouse is over it
        public class HoverUnderline : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
        {
            Text text; bool over; string drawnFor; Color drawnColor;
            readonly List<Image> lines = new List<Image>();

            public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData e) { over = true; drawnFor = null; }
            public void OnPointerExit(UnityEngine.EventSystems.PointerEventData e) { over = false; Hide(); }
            void OnDisable() { over = false; Hide(); }
            void Hide() { foreach (var l in lines) if (l != null) l.gameObject.SetActive(false); drawnFor = null; }

            void LateUpdate()
            {
                if (!over) return;
                if (text == null) text = GetComponent<Text>();
                if (text == null) return;
                if (drawnFor == text.text && drawnColor == text.color) return;
                drawnFor = text.text; drawnColor = text.color;
                var gen = text.cachedTextGenerator;
                if (gen.lineCount == 0 || gen.characterCount == 0) { Hide(); drawnFor = null; return; }
                float ppu = text.pixelsPerUnit, thick = Mathf.Max(1f, text.fontSize / 14f);
                var chars = gen.characters; var ls = gen.lines;
                int used = 0;
                for (int i = 0; i < ls.Count; i++)
                {
                    int start = ls[i].startCharIdx;
                    int end = (i + 1 < ls.Count ? ls[i + 1].startCharIdx : gen.characterCount) - 1;
                    end = Mathf.Min(end, chars.Count - 1);
                    while (end > start && end < text.text.Length && char.IsWhiteSpace(text.text[end])) end--;
                    if (end < start || start >= chars.Count) continue;
                    float x0 = chars[start].cursorPos.x / ppu, x1 = (chars[end].cursorPos.x + chars[end].charWidth) / ppu;
                    float y = (ls[i].topY - ls[i].height) / ppu + thick;
                    if (x1 - x0 < 1f) continue;
                    Image im;
                    if (used < lines.Count) im = lines[used];
                    else
                    {
                        var go = new GameObject("Underline", typeof(RectTransform));
                        go.transform.SetParent(transform, false);
                        im = go.AddComponent<Image>(); im.raycastTarget = false;
                        lines.Add(im);
                    }
                    used++;
                    var rt = im.rectTransform;
                    var pivot = text.rectTransform.pivot;
                    rt.anchorMin = rt.anchorMax = pivot; rt.pivot = new Vector2(0f, 0.5f);
                    rt.anchoredPosition = new Vector2(x0, y); rt.sizeDelta = new Vector2(x1 - x0, thick);
                    im.color = text.color;
                    im.gameObject.SetActive(true);
                }
                for (int i = used; i < lines.Count; i++) lines[i].gameObject.SetActive(false);
            }
        }

        // wraps text inside its box and ends it with "..." when it doesn't fit (Unity 2017 Text can't do this itself)
        public class FitText : MonoBehaviour
        {
            string full = "", shown; Vector2 lastSize; Text text;

            public void Set(string s) { full = s ?? ""; Apply(); }

            void LateUpdate()
            {
                if (text == null) return;
                Vector2 size = ((RectTransform)transform).rect.size;
                if (size != lastSize || text.text != shown) Apply();
            }

            void Apply()
            {
                if (text == null) text = GetComponent<Text>();
                Vector2 size = ((RectTransform)transform).rect.size;
                lastSize = size;
                if (size.x < 2f || size.y < 2f || Fits(full, size)) { shown = text.text = full; return; }
                int lo = 0, hi = full.Length;
                while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Fits(full.Substring(0, mid).TrimEnd() + "...", size)) lo = mid; else hi = mid - 1; }
                shown = text.text = full.Substring(0, lo).TrimEnd() + "...";
            }

            bool Fits(string s, Vector2 size)
            {
                var settings = text.GetGenerationSettings(new Vector2(size.x, 0f));
                settings.horizontalOverflow = HorizontalWrapMode.Wrap;
                settings.verticalOverflow = VerticalWrapMode.Overflow;
                float h = text.cachedTextGeneratorForLayout.GetPreferredHeight(s, settings) / text.pixelsPerUnit;
                return h <= size.y + 0.5f;
            }
        }

        // shows the list controls only on the list screen, and fills in vertex counts as they arrive
        public class ListControlsFollower : MonoBehaviour
        {
            int seenVersion = -1, seenThumbs = -1; float lastReload;

            void OnApplicationQuit() { VertexCounter.SaveIfDue(true); }

            void Update()
            {
                if (ws == null) return;
                WorkshopPlus.KeepPreviewSharp(ws);
                WorkshopPlus.UpdateFlipButton(ws);
                WorkshopPlus.FadeNotes(ws);
                SteamStatus.Update(ws);
                WorkshopPlus.UpdateAtlasUi(ws);
                Thumbnails.PumpLocal();
                VertexCounter.SaveIfDue();
                ThumbPicker.Update();
                ThumbPicker.UpdateToggles();
                if (fixSubs != null) fixSubs.interactable = !Subscriptions.Busy;
                bool on = ws.listView.activeSelf;
                if (controls != null && controls.activeSelf != on) controls.SetActive(on);
                if (!on) return;
                if (Thumbnails.Version != seenThumbs)
                {
                    seenThumbs = Thumbnails.Version;
                    foreach (var kv in liveCells)
                    {
                        RowParts tp;
                        if (kv.Key != null && kv.Key.gameObject.activeInHierarchy && rowParts.TryGetValue(kv.Key, out tp)) SetThumb(tp, kv.Value);
                    }
                }
                int v = VertexCounter.Version;
                if (v == seenVersion) return;
                seenVersion = v;
                foreach (var kv in liveCells)
                {
                    RowParts p;
                    if (kv.Key != null && kv.Key.gameObject.activeInHierarchy && rowParts.TryGetValue(kv.Key, out p)) SetVerts(p, kv.Value, ObjectsTab(ws));
                }
                // filtering/sorting by vertex count: re-sort as counts arrive (at most once a second)
                bool byVerts = ObjectsTab(ws) && (SortMode() == 4 || SortMode() == 5);
                if (byVerts && Time.realtimeSinceStartup - lastReload > 1f) { lastReload = Time.realtimeSinceStartup; Refresh(false); }
            }
        }
    }
}
