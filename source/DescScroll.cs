// TTVR Workshop Plus - scrollable Description box. The tool's description field is a fixed-size box: long text
// ran off the bottom. It now sits in a scroll area (mouse wheel / scrollbar) and grows with its text, and the view
// follows the caret while typing.
using System;
using UnityEngine;
using UnityEngine.UI;

namespace TTVRPlus
{
    public class DescScroll : MonoBehaviour
    {
        InputField field; RectTransform fieldRt, viewport, textRt; ScrollRect scroll;
        float padTop, padBottom; string lastText; int lastCaret = -1; float lastWidth;

        public static void Setup(Workshop w)
        {
            try
            {
                var f = w.descriptionField;
                if (f == null) return;
                var frt = (RectTransform)f.transform;
                var parent = frt.parent;

                // scroll area in the field's place, with the field's look
                var area = new GameObject("DescriptionScroll", typeof(RectTransform));
                var art = (RectTransform)area.transform;
                art.SetParent(parent, false);
                art.SetSiblingIndex(frt.GetSiblingIndex());
                art.anchorMin = frt.anchorMin; art.anchorMax = frt.anchorMax; art.pivot = frt.pivot;
                art.sizeDelta = frt.sizeDelta; art.anchoredPosition = frt.anchoredPosition;
                var bg = area.AddComponent<Image>();
                var fbg = f.GetComponent<Image>();
                if (fbg != null) { bg.sprite = fbg.sprite; bg.type = fbg.type; bg.color = fbg.color; fbg.color = new Color(1f, 1f, 1f, 0f); }

                var vp = new GameObject("Viewport", typeof(RectTransform));
                var vrt = (RectTransform)vp.transform; vrt.SetParent(art, false);
                vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.offsetMin = Vector2.zero; vrt.offsetMax = new Vector2(-12f, 0f);
                vp.AddComponent<RectMask2D>();

                // the field itself becomes the scrolling content: full width, top-anchored, as tall as its text
                frt.SetParent(vrt, false);
                frt.anchorMin = new Vector2(0f, 1f); frt.anchorMax = new Vector2(1f, 1f); frt.pivot = new Vector2(0.5f, 1f);
                frt.anchoredPosition = Vector2.zero; frt.sizeDelta = new Vector2(0f, art.sizeDelta.y);

                var sr = area.AddComponent<ScrollRect>();
                sr.viewport = vrt; sr.content = frt; sr.horizontal = false; sr.vertical = true;
                sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 25f;

                var sbGo = new GameObject("Scrollbar", typeof(RectTransform));
                var sbt = (RectTransform)sbGo.transform; sbt.SetParent(art, false);
                sbt.anchorMin = new Vector2(1f, 0f); sbt.anchorMax = Vector2.one; sbt.pivot = new Vector2(1f, 0.5f);
                sbt.sizeDelta = new Vector2(10f, -6f); sbt.anchoredPosition = new Vector2(-2f, 0f);
                sbGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.08f);
                var sa = new GameObject("Sliding Area", typeof(RectTransform)); var sat = (RectTransform)sa.transform; sat.SetParent(sbt, false);
                sat.anchorMin = Vector2.zero; sat.anchorMax = Vector2.one; sat.offsetMin = sat.offsetMax = Vector2.zero;
                var h = new GameObject("Handle", typeof(RectTransform)); var hrt = (RectTransform)h.transform; hrt.SetParent(sat, false);
                hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one; hrt.offsetMin = hrt.offsetMax = Vector2.zero;
                var hi = h.AddComponent<Image>(); hi.color = new Color(0f, 0f, 0f, 0.35f);
                var sb = sbGo.AddComponent<Scrollbar>(); sb.handleRect = hrt; sb.targetGraphic = hi; sb.direction = Scrollbar.Direction.BottomToTop;
                sr.verticalScrollbar = sb; sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

                var ds = area.AddComponent<DescScroll>();
                ds.field = f; ds.fieldRt = frt; ds.viewport = vrt; ds.scroll = sr;
                ds.textRt = f.textComponent != null ? f.textComponent.rectTransform : null;
                if (ds.textRt != null) { ds.padTop = -ds.textRt.offsetMax.y; ds.padBottom = ds.textRt.offsetMin.y; }
                f.textComponent.verticalOverflow = VerticalWrapMode.Overflow;
            }
            catch (Exception e) { Debug.Log("[WorkshopPlus] description scroll: " + e.Message); }
        }

        void LateUpdate()
        {
            if (field == null || textRt == null) return;
            float width = textRt.rect.width;
            string txt = field.text ?? "";
            int caret = field.isFocused ? field.caretPosition : -1;
            if (txt == lastText && caret == lastCaret && Mathf.Approximately(width, lastWidth)) return;
            bool grew = lastText != null && txt.Length > lastText.Length;
            lastText = txt; lastCaret = caret; lastWidth = width;

            // height of the wrapped text
            var settings = field.textComponent.GetGenerationSettings(new Vector2(width, 0f));
            float textH = field.textComponent.cachedTextGeneratorForLayout.GetPreferredHeight(txt + "\n", settings) / field.textComponent.pixelsPerUnit;
            float view = viewport.rect.height;
            float h = Mathf.Max(view, textH + padTop + padBottom);
            if (!Mathf.Approximately(fieldRt.sizeDelta.y, h)) fieldRt.sizeDelta = new Vector2(fieldRt.sizeDelta.x, h);

            // follow the caret while typing at the end
            if (field.isFocused && grew && caret >= txt.Length - 1) scroll.verticalNormalizedPosition = 0f;
        }
    }
}
