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

        static readonly System.Collections.Generic.Dictionary<int, Sprite> _rounded = new System.Collections.Generic.Dictionary<int, Sprite>();

        /// <summary>Make img a rounded rectangle (9-sliced, so any size keeps the corner radius, in canvas units).
        /// feather > 1 = a soft edge that far outside the shape (a drop shadow: grow the rect by feather on every side).</summary>
        public static Image Rounded(Image img, float radius, float feather = 1f)
        {
            img.sprite = RoundedSprite(radius, feather);
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f;
            return img;
        }

        public static Sprite RoundedSprite(float radius, float feather = 1f)
        {
            int r = Mathf.Max(1, Mathf.CeilToInt(radius)), f = Mathf.Max(1, Mathf.CeilToInt(feather));
            int key = r * 1000 + f;
            if (_rounded.TryGetValue(key, out var s) && s != null) return s;
            int b = r + f, n = 2 * b + 2;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            float c = n * 0.5f, inner = c - f - r;   // the shape is the texture inset by f, corner radius r
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - c) - inner, 0f), dy = Mathf.Max(Mathf.Abs(y + 0.5f - c) - inner, 0f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - r;                  // < 0 inside
                    float a = f <= 1 ? Mathf.Clamp01(0.5f - d) : 1f - Mathf.SmoothStep(0f, 1f, (d + 1f) / f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(255f * Mathf.Clamp01(a)));
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            s = Sprite.Create(tex, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0,
                              SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            _rounded[key] = s;
            return s;
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
