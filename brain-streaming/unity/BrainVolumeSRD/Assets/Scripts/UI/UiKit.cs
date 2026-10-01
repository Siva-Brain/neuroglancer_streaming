using UnityEngine;
using UnityEngine.UI;

namespace BrainVolume
{
    /// <summary>Tiny world-space uGUI builders (rects, plates, labels, a round knob) for the
    /// label card and the timeline transport bar. Same idea as SRD_test's SliceUI.UIKit.</summary>
    public static class UiKit
    {
        public static readonly Color TextPrimary = new Color(0.93f, 0.95f, 0.97f, 1f);
        public static readonly Color TextDim = new Color(0.62f, 0.66f, 0.72f, 1f);

        static Font _font;
        public static Font DefaultFont => _font != null ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        static Sprite _circle;

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static Image Panel(string name, Transform parent, Color c)
        {
            var img = Rect(name, parent).gameObject.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        public static Text Label(string name, Transform parent, string s, int size, FontStyle style, Color c,
                                 TextAnchor anchor, Font font = null)
        {
            var t = Rect(name, parent).gameObject.AddComponent<Text>();
            t.font = font != null ? font : DefaultFont;
            t.text = s; t.fontSize = size; t.fontStyle = style; t.color = c;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        /// <summary>An Image with an anti-aliased white disc sprite, tinted c.</summary>
        public static Image Circle(string name, Transform parent, Color c)
        {
            var img = Panel(name, parent, c);
            img.sprite = CircleSprite();
            return img;
        }

        static Sprite CircleSprite()
        {
            if (_circle != null) return _circle;
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            float r = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                    byte a = (byte)(255f * Mathf.Clamp01(r - 1f - d + 0.5f));
                    px[y * n + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            _circle = Sprite.Create(tex, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            return _circle;
        }
    }
}
