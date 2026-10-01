// TTVR Workshop Plus - hover tooltips. Tooltip.Add(control, "text") shows a small box above the control
// after hovering for half a second (below it if there's no room), kept inside the window.
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TTVRPlus
{
    public class Tooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
    {
        const float Delay = 0.5f, MaxWidth = 320f, Pad = 8f;
        public string Text;
        public System.Func<string> Dynamic;      // text worked out when shown (e.g. Publish vs Update)
        float enterTime = -1f;
        static RectTransform box; static Text boxText; static Tooltip showing;

        public static Tooltip Add(GameObject go, string text)
        {
            var t = go.GetComponent<Tooltip>() ?? go.AddComponent<Tooltip>();
            t.Text = text;
            return t;
        }

        public static Tooltip Add(GameObject go, System.Func<string> text)
        {
            var t = Add(go, (string)null);
            t.Dynamic = text;
            return t;
        }

        public void OnPointerEnter(PointerEventData e) { enterTime = Time.unscaledTime; }
        public void OnPointerExit(PointerEventData e) { enterTime = -1f; Hide(); }
        public void OnPointerDown(PointerEventData e) { enterTime = -1f; Hide(); }    // clicking dismisses it
        void OnDisable() { enterTime = -1f; Hide(); }

        void Hide() { if (showing == this && box != null) { box.gameObject.SetActive(false); showing = null; } }

        void Update()
        {
            if (enterTime < 0f || showing == this) return;
            if (Dynamic != null) { try { Text = Dynamic(); } catch (System.Exception) { } }
            if (string.IsNullOrEmpty(Text)) return;
            if (Time.unscaledTime - enterTime >= Delay) Show();
        }

        void Show()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;
            canvas = canvas.rootCanvas;
            var crt = (RectTransform)canvas.transform;
            if (box == null || box.parent != crt) Create(crt);
            box.SetAsLastSibling();
            boxText.text = Text;

            // width: up to MaxWidth, then wrap; height from the wrapped text
            var trt = boxText.rectTransform;
            float w = Mathf.Min(boxText.preferredWidth, MaxWidth);
            box.sizeDelta = new Vector2(w + Pad * 2f, 10f);
            trt.offsetMin = new Vector2(Pad, Pad); trt.offsetMax = new Vector2(-Pad, -Pad);
            float h = boxText.preferredHeight;
            box.sizeDelta = new Vector2(w + Pad * 2f, h + Pad * 2f);

            // above the control, centred; below it if it would leave the top; kept inside left/right
            var wc = new Vector3[4]; ((RectTransform)transform).GetWorldCorners(wc);
            Vector3 bl = crt.InverseTransformPoint(wc[0]), tr = crt.InverseTransformPoint(wc[2]);
            Rect r = crt.rect;
            float x = (bl.x + tr.x) * 0.5f, y;
            if (tr.y + 6f + box.sizeDelta.y <= r.yMax) { box.pivot = new Vector2(0.5f, 0f); y = tr.y + 6f; }
            else { box.pivot = new Vector2(0.5f, 1f); y = bl.y - 6f; }
            float half = box.sizeDelta.x * 0.5f;
            x = Mathf.Clamp(x, r.xMin + half + 4f, r.xMax - half - 4f);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            box.anchoredPosition = new Vector2(x, y);
            box.gameObject.SetActive(true);
            showing = this;
        }

        static void Create(RectTransform canvas)
        {
            var go = new GameObject("WorkshopPlusTooltip", typeof(RectTransform));
            box = (RectTransform)go.transform;
            box.SetParent(canvas, false);
            var bg = go.AddComponent<Image>(); bg.color = new Color(0.1f, 0.1f, 0.1f, 0.95f); bg.raycastTarget = false;
            var tgo = new GameObject("Text", typeof(RectTransform));
            var trt = (RectTransform)tgo.transform; trt.SetParent(box, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            boxText = tgo.AddComponent<Text>();
            var any = canvas.GetComponentInChildren<Text>(true);
            boxText.font = any != null ? any.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            boxText.fontSize = any != null ? Mathf.Clamp(any.fontSize - 4, 12, 18) : 14;
            boxText.color = Color.white; boxText.raycastTarget = false;
            boxText.alignment = TextAnchor.MiddleLeft;
            boxText.horizontalOverflow = HorizontalWrapMode.Wrap; boxText.verticalOverflow = VerticalWrapMode.Overflow;
            go.SetActive(false);
        }
    }
}
