using System.Collections.Generic;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Keeps a world-space label card (a uGUI canvas) in front of the brain. The brain volumes are drawn in
    /// OnRenderObject with ZTest Always, after all scene geometry, so a canvas drawn the normal way is painted over
    /// wherever the brain is on screen (partly or wholly hidden). Instead the canvas itself is switched off, and every
    /// frame it is shown it is rendered into a texture (a hidden orthographic camera, layer 31 only) and drawn as a
    /// quad at the card's place in Camera.onPostRender, which runs after every OnRenderObject: after all the volumes,
    /// for every camera (flat screen, both SRD eyes before the SRD's warp, the scene view).
    ///
    /// Add with CardOverlay.Attach(canvasRoot, card): the owner keeps showing / hiding the canvas GameObject,
    /// fading its CanvasGroup and moving it as before.
    /// </summary>
    [DefaultExecutionOrder(10000)]   // LateUpdate after the timelines have placed / faded / filled the card
    public sealed class CardOverlay : MonoBehaviour
    {
        const int Layer = 31;                 // only the cards (the snapshot camera sees nothing else)
        const float PixelsPerUnit = 2f;       // texture pixels per canvas unit (a 36-unit title = 72 px)
        const int MaxPixels = 2048;

        static readonly List<CardOverlay> Shown = new List<CardOverlay>();
        static Camera _snap;
        static Material _mat;
        static Mesh _quad;
        static bool _hooked;

        /// <summary>Is this the hidden camera that renders the cards? (Volumes skip drawing for it.)</summary>
        public static bool IsSnapshot(Camera cam) => cam != null && cam == _snap;

        Canvas _canvas;
        CanvasGroup _group;
        RectTransform _card;
        RenderTexture _rt;
        readonly Vector3[] _corners = new Vector3[4];
        bool _ready;

        /// <summary>Draw this canvas (root) on top of the brain; card = the RectTransform to frame (its background).</summary>
        public static CardOverlay Attach(GameObject canvasRoot, RectTransform card)
        {
            var o = canvasRoot.AddComponent<CardOverlay>();
            o._canvas = canvasRoot.GetComponent<Canvas>();
            o._group = canvasRoot.GetComponent<CanvasGroup>();
            o._card = card;
            foreach (var t in canvasRoot.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
            if (o._canvas != null) o._canvas.enabled = false;   // never drawn the normal way
            return o;
        }

        void OnEnable()
        {
            Shown.Add(this);
            if (!_hooked) { Camera.onPostRender += DrawAll; _hooked = true; }
        }

        void OnDisable()
        {
            Shown.Remove(this);
            _ready = false;
        }

        void LateUpdate()
        {
            _ready = false;
            if (_canvas == null || _card == null || (_group != null && _group.alpha <= 0.001f)) return;
            if (!Setup()) return;

            // the card's size in canvas units -> texture size
            Rect r = _card.rect;
            int w = Mathf.Clamp(Mathf.CeilToInt(r.width * PixelsPerUnit), 8, MaxPixels);
            int h = Mathf.Clamp(Mathf.CeilToInt(r.height * PixelsPerUnit), 8, MaxPixels);
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                      { name = name + "_Card", antiAliasing = 1, filterMode = FilterMode.Bilinear };
            }

            // render the canvas alone, unfaded (the fade is applied when drawing), face-on
            _card.GetWorldCorners(_corners);   // bottom-left, top-left, top-right, bottom-right
            Vector3 up = _corners[1] - _corners[0], right = _corners[3] - _corners[0];
            float hw = up.magnitude;
            if (hw < 1e-7f || right.sqrMagnitude < 1e-12f) return;
            Vector3 centre = 0.5f * (_corners[0] + _corners[2]);
            Vector3 fwd = Vector3.Cross(right, up).normalized;   // into the card (a canvas is seen looking along its +Z)
            float d = Mathf.Max(hw, right.magnitude);
            _snap.transform.SetPositionAndRotation(centre - fwd * d, Quaternion.LookRotation(fwd, up));
            _snap.orthographicSize = 0.5f * hw;
            _snap.aspect = right.magnitude / hw;
            _snap.nearClipPlane = 0.5f * d;
            _snap.farClipPlane = 2f * d;
            _snap.targetTexture = _rt;

            float alpha = _group != null ? _group.alpha : 1f;
            if (_group != null) _group.alpha = 1f;
            _canvas.enabled = true;
            Canvas.ForceUpdateCanvases();
            _snap.Render();
            _canvas.enabled = false;
            if (_group != null) _group.alpha = alpha;
            _snap.targetTexture = null;
            _ready = true;
        }

        static bool Setup()
        {
            if (_snap == null)
            {
                var go = new GameObject("CardOverlay_Snapshot") { hideFlags = HideFlags.HideAndDontSave };
                _snap = go.AddComponent<Camera>();
                _snap.enabled = false;               // rendered by hand only
                _snap.orthographic = true;
                _snap.clearFlags = CameraClearFlags.SolidColor;
                _snap.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _snap.cullingMask = 1 << Layer;
                _snap.allowHDR = false; _snap.allowMSAA = false;
            }
            if (_mat == null)
            {
                var sh = Shader.Find("Brain/OverlayCard");
                if (sh == null) { Debug.LogError("[Card] Shader Brain/OverlayCard not found (Always Included Shaders?)."); return false; }
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_quad == null)
            {
                _quad = new Mesh { name = "CardQuad", hideFlags = HideFlags.HideAndDontSave };
                _quad.vertices = new Vector3[4];
                _quad.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
                _quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                _quad.MarkDynamic();
            }
            return true;
        }

        // after every OnRenderObject (all the volumes) of each camera
        static void DrawAll(Camera cam)
        {
            if (cam == null || cam == _snap || _mat == null || _quad == null) return;
            foreach (var o in Shown)
            {
                if (!o._ready) continue;
                _quad.vertices = o._corners;   // the card's world corners (same order as the uvs)
                _quad.RecalculateBounds();
                _mat.SetTexture("_MainTex", o._rt);
                _mat.SetFloat("_Alpha", o._group != null ? o._group.alpha : 1f);
                _mat.SetPass(0);
                Graphics.DrawMeshNow(_quad, Matrix4x4.identity);
            }
        }

        void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
        }
    }
}
