// TTVR Workshop Plus - the Help page (the ? button): one scrollable page explaining how the tool reads models,
// builds the texture, and the game's limits. Replaces Workshop.UpdateHelpText.
using System;
using UnityEngine;
using UnityEngine.UI;

namespace TTVRPlus
{
    public static class Help
    {
        static Text body;
        static ScrollRect scroll;
        static bool? shownFor;

        /// replaces Workshop.UpdateHelpText
        public static void UpdateHelpText(Workshop w)
        {
            try
            {
                if (body == null) Build(w);
                if (body == null) return;
                if (shownFor == null)
                {
                    shownFor = true;
                    body.text = PageText(body.fontSize);          // one page for both tabs
                }
                scroll.verticalNormalizedPosition = 1f;      // open at the top
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] help: " + e); }
        }

        static void Build(Workshop w)
        {
            var view = w.helpInstructionMessage.transform.parent as RectTransform;
            // the original page: title, instructions, notes title, notes -> replaced by one scrolling page
            Text template = w.helpInstructionMessage;
            foreach (var n in new[] { "InstructionTitle", "InstructionMessage", "NotesTitle", "NotesMessage" })
            {
                var t = view.Find(n); if (t != null) t.gameObject.SetActive(false);
            }

            var sGo = new GameObject("WorkshopPlusHelp", typeof(RectTransform));
            var srt = (RectTransform)sGo.transform;
            srt.SetParent(view, false);
            srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
            srt.offsetMin = new Vector2(40f, 20f); srt.offsetMax = new Vector2(-40f, -85f);   // below the Back button

            var vpGo = new GameObject("Viewport", typeof(RectTransform));
            var vrt = (RectTransform)vpGo.transform; vrt.SetParent(srt, false);
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.offsetMin = Vector2.zero; vrt.offsetMax = new Vector2(-26f, 0f);
            var mask = vpGo.AddComponent<Image>(); mask.color = new Color(1f, 1f, 1f, 0.004f);   // raycasts for wheel/drag; mask needs a graphic
            vpGo.AddComponent<RectMask2D>();

            var cGo = (GameObject)UnityEngine.Object.Instantiate(template.gameObject, vrt, false);
            cGo.name = "Content"; cGo.SetActive(true);
            var crt = (RectTransform)cGo.transform;
            crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero; crt.sizeDelta = new Vector2(-10f, 100f);
            body = cGo.GetComponent<Text>();
            body.supportRichText = true;
            body.alignment = TextAnchor.UpperLeft;
            body.horizontalOverflow = HorizontalWrapMode.Wrap; body.verticalOverflow = VerticalWrapMode.Overflow;
            body.resizeTextForBestFit = false;
            body.raycastTarget = true;
            var fit = cGo.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll = sGo.AddComponent<ScrollRect>();
            scroll.viewport = vrt; scroll.content = crt;
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;

            // scrollbar on the right
            var bGo = new GameObject("Scrollbar", typeof(RectTransform));
            var brt = (RectTransform)bGo.transform; brt.SetParent(srt, false);
            brt.anchorMin = new Vector2(1f, 0f); brt.anchorMax = Vector2.one; brt.pivot = new Vector2(1f, 0.5f);
            brt.sizeDelta = new Vector2(16f, 0f); brt.anchoredPosition = Vector2.zero;
            var track = bGo.AddComponent<Image>(); track.color = new Color(0f, 0f, 0f, 0.12f);
            var area = new GameObject("Sliding Area", typeof(RectTransform));
            var art = (RectTransform)area.transform; art.SetParent(brt, false);
            art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = new Vector2(2f, 2f); art.offsetMax = new Vector2(-2f, -2f);
            var hGo = new GameObject("Handle", typeof(RectTransform));
            var hrt = (RectTransform)hGo.transform; hrt.SetParent(art, false);
            hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one; hrt.offsetMin = hrt.offsetMax = Vector2.zero;
            var handle = hGo.AddComponent<Image>(); handle.color = new Color(0f, 0f, 0f, 0.45f);
            var sb = bGo.AddComponent<Scrollbar>();
            sb.handleRect = hrt; sb.targetGraphic = handle; sb.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = sb;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }

        // formatting helpers: big section title, small heading, bold, bullet
        static string Big(string t, int fs) { return "<b><size=" + (fs + 14) + ">" + t + "</size></b>\n\n"; }
        static string Sub(string t, int fs) { return "<b><size=" + (fs + 5) + ">" + t + "</size></b>\n"; }
        static string B(string t) { return "<b>" + t + "</b>"; }
        const string Dot = "    •  ";
        const string Gap = "\n\n";

        static string PageText(int fs)
        {
            var s = new System.Text.StringBuilder();
            s.Append("<b><size=" + (fs + 20) + ">Workshop Plus</size></b>  <size=" + fs + ">" + WorkshopPlus.Version + "</size>\n\n");
            s.Append("This tool uploads your own 3D models (" + B("Items") + ") and saved towns (" + B("Worlds") + ") to the Tiny Town VR Steam Workshop. " +
                     "Everything it uploads uses the game's normal format, so other players can see your items without installing anything." + Gap);
            s.Append("Hover over any button to see what it does. Scroll with the mouse wheel or the bar on the right." + Gap + Gap);

            // ================================================================ instructions
            s.Append(Big("Instructions", fs));

            s.Append(Sub("Publishing an item (a 3D model)", fs));
            s.Append("1.  On the " + B("Items") + " tab, click the green " + B("+") + " and choose your model file (" + B("OBJ") + ", " + B("glTF") + " or " + B("GLB") + ").\n");
            s.Append("2.  The editor opens with a 3D preview. Check that the model looks right: drag to look around it, scroll to zoom.\n");
            s.Append("3.  Type a " + B("title") + " and, if you like, a " + B("description") + ".\n");
            s.Append("4.  Adjust it if needed: " + B("Scale") + ", the " + B("X / Y / Z") + " rotate buttons, " + B("Flip X") + ", " + B("Shading") + " and the texture options. Changes are saved as you make them.\n");
            s.Append("5.  Click " + B("Publish") + ". New items are " + B("Private") + ", so only you can see them." + Gap);

            s.Append(Sub("How your model is loaded", fs));
            s.Append("The tool reads the " + B("shape") + " of your model (its points and faces) and its " + B("materials") + ". " +
                     "For an OBJ file the materials come from the " + B("MTL") + " file that sits next to it; a glTF or GLB file carries its own. " +
                     "Each material gives a colour and, usually, a texture image." + Gap);
            s.Append("The game only allows " + B("one texture per item") + ", so when your model uses several textures or plain colours, the tool " + B("combines them into one image") + " for you (called an " + B("atlas") + "). " +
                     "If you'd rather use a single picture for the whole model, pick it with the Texture " + B("Browse") + " button." + Gap);
            s.Append("The finished model appears in the preview, and the counter in its bottom corner shows how many " + B("vertices") + " and " + B("triangles") + " it has in the game. " +
                     "Short " + B("orange notes") + " at the bottom tell you about anything the tool changed (for example a texture that was made smaller); " + B("red messages") + " are problems." + Gap);

            s.Append(Sub("Publishing a world", fs));
            s.Append("1.  On the " + B("Worlds") + " tab, click the green " + B("+") + " and choose a saved town. World files are always named " + B("data") + "; the file browser opens in the game's save folder.\n");
            s.Append("2.  Give it a title and description. Its picture is the screenshot the game saved with it; you can choose a nicer one in the " + B("Info") + " box.\n");
            s.Append("3.  Click " + B("Publish") + "." + Gap);

            s.Append(Sub("Testing and making it public", fs));
            s.Append("1.  Open the item's Workshop page (the " + B("Steam") + " button in the list) and " + B("subscribe") + " to it.\n");
            s.Append("2.  Start the game (" + B("Run Game") + " at the top). Items appear in the " + B("Workshop") + " category of the inventory; worlds in the " + B("World") + " tab of the main menu.\n");
            s.Append("3.  Something missing, or old items still there? Close the game and click " + B("Fix Subscriptions") + " at the bottom of the list.\n");
            s.Append("4.  When you're happy, set " + B("Visibility") + " to " + B("Public") + " and click " + B("Publish") + " again. Publishing again always updates the same Workshop item." + Gap + Gap);

            // ================================================================ restrictions
            s.Append(Big("Restrictions and requirements", fs));

            s.Append(Sub("The game's limits", fs));
            s.Append(Dot + B("65,000 vertices") + " per item at most. A vertex is counted for every different combination of position, texture position and lighting direction, so the number can be higher than your 3D program shows (UV seams and sharp edges add extra).\n");
            s.Append(Dot + B("One texture") + " per item, " + B("square") + ", " + B("512px") + " at most (128, 256 or 512). Bigger images are made smaller and non-square ones are stretched.\n");
            s.Append(Dot + B("Colour only") + ": no transparency, and no extra maps such as roughness, metal or normal maps.\n");
            s.Append(Dot + "Worlds must be saved from the game first (a file named " + B("data") + ")." + Gap);

            s.Append(Sub("Files you can use", fs));
            s.Append(Dot + B("Models") + ": OBJ (with its MTL file), glTF and GLB. Compressed glTF (Draco, meshopt) isn't supported; export without compression.\n");
            s.Append(Dot + B("Textures") + ": PNG, JPG, DDS (BC1 to BC5, BC7 and uncompressed) and TGA." + Gap);

            s.Append(Sub("What you need", fs));
            s.Append(Dot + B("Steam") + " running and logged in. If it isn't, the list says so in red; start Steam and click " + B("Reconnect") + " (or just wait, it reconnects by itself).\n");
            s.Append(Dot + "Tiny Town VR installed, to test your items.\n");
            s.Append(Dot + "Keep your model and texture files where they are: the tool remembers their location so you can update the item later." + Gap + Gap);

            // ================================================================ details
            s.Append(Big("In more detail", fs));

            s.Append(Sub("Textures and the atlas", fs));
            s.Append(B("Single texture") + ": if the model uses exactly one texture and no plain colours, that texture is used as it is, and textures that repeat (tile) keep working." + Gap);
            s.Append(B("Atlas") + ": with several textures and/or plain colours, everything is packed into one image. Each colour becomes a small patch. Repeating textures can't repeat inside an atlas, so those parts are clamped (a note says how many).\n" +
                     Dot + B("Equal") + " gives every texture the same amount of space.\n" +
                     Dot + B("By area") + " gives more space to textures that cover more of the model, so every part looks about equally sharp.\n" +
                     Dot + B("Crop to used UVs") + " keeps only the part of each texture the model actually uses, giving it more room. It may break repeating textures." + Gap);
            s.Append(B("Size") + " sets the texture to 512, 256 or 128px. Smaller textures load faster and use less memory in the game." + Gap);
            s.Append(B("Browse / Reset") + ": Browse puts one picture of your choice on the whole model (using the model's own texture positions); Reset goes back to the model's materials." + Gap);

            s.Append(Sub("Shading", fs));
            s.Append(B("Imported") + " uses the smooth and sharp edges saved in your file. " + B("Smooth") + " makes everything smooth and uses the fewest vertices. " +
                     B("Flat") + " makes every face flat, which needs many more vertices; if that would go over 65,000 it isn't applied and the reason is shown. " +
                     B("Auto-Smooth") + " keeps edges sharper than the angle you type and smooths the rest (press Enter to apply)." + Gap);

            s.Append(Sub("Model tools", fs));
            s.Append(Dot + B("Reimport") + " reads the model and its textures again from the same file after you've edited them. If the file is missing or broken, the current model stays and a message explains why.\n");
            s.Append(Dot + B("Reset rotation") + " undoes all rotate clicks. " + B("Flip X") + " mirrors the model.\n");
            s.Append(Dot + "The " + B("Floor") + ", " + B("Grid") + ", " + B("Ruler") + " and " + B("Direction") + " buttons above the preview show or hide those helpers.\n");
            s.Append(Dot + "If a model file was moved or renamed, opening the item asks where it is now and keeps all its settings.\n");
            s.Append(Dot + "Shading, Atlas, Crop and Size are saved " + B("for each item") + ". New items start from the defaults (Imported, Equal, no cropping, 512px)." + Gap);

            s.Append(Sub("Thumbnail (Info box)", fs));
            s.Append("The picture shown on the Workshop page. By default the tool makes one automatically. " + B("From camera") + " uses the current preview angle, " +
                     B("Choose image") + " uses a picture file, and " + B("Use default") + " goes back. The new picture is uploaded the next time you click Publish." + Gap);

            s.Append(Sub("The item list", fs));
            s.Append(Dot + B("Search") + " and " + B("Sort") + " (newest, oldest, title, visibility, vertex count) are at the top.\n");
            s.Append(Dot + B("Columns") + ": thumbnail, title and file path, vertices (red if over the limit), visibility and when it was last published.\n");
            s.Append(Dot + "Click a " + B("path") + " to show the file in Explorer. Red paths are missing or unknown.\n");
            s.Append(Dot + B("Steam") + " opens the item's Workshop page. " + B("Duplicate") + " makes an unpublished copy with the same settings.\n");
            s.Append(Dot + "Tick rows, or use " + B("Select all") + ", then " + B("Delete selected") + " or " + B("Set visibility...") + ". Published items are changed on Steam too, after an \"are you sure\" prompt." + Gap);

            s.Append(Sub("Fix Subscriptions", fs));
            s.Append("The game keeps its own copy of every item you subscribe to, but never removes ones you unsubscribe from, and sometimes stops downloading part way. " +
                     "This button compares that copy with your Steam subscriptions: unsubscribed items are moved out of the game's folder (not deleted) and missing or outdated items are downloaded. Close the game first." + Gap);

            s.Append(Sub("Where things are kept", fs));
            s.Append(Dot + "Your items and settings: %USERPROFILE%\\AppData\\LocalLow\\Lumbernauts\\TinyTownWorkshop\\Internal\\Workshop\\Development\n");
            s.Append(Dot + "Log file, useful when something goes wrong (look for [WorkshopPlus]): %USERPROFILE%\\AppData\\LocalLow\\Lumbernauts\\TinyTownWorkshop\\output_log.txt" + Gap + Gap);
            s.Append("<i>By Don Petrol - Made using Claude</i>");
            return s.ToString();
        }
    }
}
