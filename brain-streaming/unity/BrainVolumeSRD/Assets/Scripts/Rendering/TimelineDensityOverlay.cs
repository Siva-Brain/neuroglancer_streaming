using UnityEngine;
using UnityEngine.UI;
using SRD.Utils;

namespace BrainVolume
{
    /// <summary>
    /// Shows an NpzDensityVolume (a whole-brain map, e.g. astrocyte density or fib probability) as part
    /// of the NeuronalLossSequence timeline: hidden until the timeline reaches `appearAtMark` (+ delay),
    /// then -- like the neuronal-loss block -- it comes out of the brain's cut face: tiny at the
    /// timeline's CutAnchorWorld, growing (with the block's slight overshoot) while it travels in a
    /// straight line to where this GameObject was placed in the scene (its final position / size).
    /// Then a label card fades in above it, in the neuronal-loss card's style. Purely a function of
    /// the timeline time, so seeking / looping lands exactly. Used by "NeuronalLoss Fib Astrocytes V3".
    /// </summary>
    [RequireComponent(typeof(NpzDensityVolume))]
    [DefaultExecutionOrder(110)]   // after NpzDensityVolume (100), which applies the follow rotation in LateUpdate
    public sealed class TimelineDensityOverlay : MonoBehaviour
    {
        public NeuronalLossSequence timeline;
        [Tooltip("Timeline step (TimelineTransportUI mark name) at which this map appears.")]
        public string appearAtMark = "Neuronal loss";
        [Tooltip("Seconds after the mark before the map starts to come out.")]
        public float delay = 0f;
        [Tooltip("ON = come out of the brain's cut face like the neuronal-loss block (the timeline's grow seconds, " +
                 "start size and overshoot). OFF = just fade in at the final place over fadeSeconds.")]
        public bool comeOutOfBrain = true;
        public float fadeSeconds = 1.5f;
        [Tooltip("Turntable spin about the vertical once the map appears (deg/s; 0 = none). On top of the brain's " +
                 "rotation when it follows the brain; the brain itself doesn't spin.")]
        public float spinDegreesPerSecond = 0f;

        [Header("Label card")]
        public string title = "Astrocytes";
        public string tagLine = "";
        public Color cardColor = Color.white;
        public Color titleColor = new Color(0.04f, 0.04f, 0.06f, 1f);
        public Color tagColor = new Color(0.10f, 0.10f, 0.12f, 1f);
        public int titleFontSize = 30, tagFontSize = 19;
        public float padding = 18f;
        [Tooltip("World units per canvas unit (the neuronal-loss card uses 0.001 x 0.2 / 0.56).")]
        public float cardScale = 0.000357f;
        [Tooltip("Gap between the top of the map and the card (world units).")]
        public float labelGap = 0.012f;
        [Tooltip("Seconds after the map has arrived (or faded in) before the card fades in.")]
        public float labelDelay = 0.2f;
        public float labelSeconds = 0.6f;

        NpzDensityVolume _vol;
        GameObject _canvas;
        CanvasGroup _group;
        Transform _frame;
        Vector3 _finalPos, _finalScale;
        Quaternion _baseRot;   // the scene rotation (used when not following the brain's rotation)

        void Start()
        {
            _finalPos = transform.position;
            _finalScale = transform.localScale;
            _baseRot = transform.rotation;
            _vol = GetComponent<NpzDensityVolume>();
            if (timeline == null) timeline = FindFirstObjectByType<NeuronalLossSequence>();
            var srd = SRDSceneEnvironment.GetSRDManager();
            _frame = DisplayFrame.Get(srd);   // the SRD, or the flat-screen rig standing in for it
            _vol.visibility = 0f;
            BuildCard();
        }

        float AppearTime()
        {
            foreach (var m in timeline.Marks) if (m.name == appearAtMark) return m.start + delay;
            return float.PositiveInfinity;   // mark not in this timeline: never shown
        }

        // after NpzDensityVolume's LateUpdate (its follow rotation), so the card sits on this frame's pose
        void LateUpdate()
        {
            if (timeline == null || !timeline.Ready || !_vol.Loaded)
            {
                _vol.visibility = 0f;
                if (_canvas != null) _canvas.SetActive(false);
                return;
            }
            float t = timeline.Time - AppearTime();
            float back = timeline.CombineAmount;   // 0 -> 1 at the end: back into the brain with the block
            float arrive;
            if (comeOutOfBrain)
            {
                // the neuronal-loss block's motion: straight line out of the cut face, growing with a slight overshoot
                arrive = Mathf.Max(0.01f, timeline.GrowSeconds);
                float u = Mathf.Clamp01(t / arrive);
                Vector3 anchor = timeline.CutAnchorWorld;
                float size = Mathf.LerpUnclamped(timeline.GrowStartScale, 1f, EaseOutBack(u, timeline.GrowOvershoot));
                Vector3 pos = Vector3.Lerp(anchor, _finalPos, EaseInOutCubic(u));
                transform.position = Vector3.Lerp(pos, anchor, back);
                transform.localScale = _finalScale * Mathf.Lerp(size, timeline.GrowStartScale, back);
                _vol.visibility = Mathf.Clamp01(t / (0.15f * arrive));
            }
            else
            {
                arrive = Mathf.Max(0.01f, fadeSeconds);
                transform.position = _finalPos;
                transform.localScale = _finalScale;
                _vol.visibility = Mathf.Clamp01(t / arrive);
            }
            _vol.visibility *= Mathf.Clamp01((1f - back) / 0.25f);
            if (spinDegreesPerSecond != 0f)
            {
                // a function of the time since it appeared (seeking / looping lands exactly); on top of the brain's
                // rotation of this frame when NpzDensityVolume follows it, else on top of the scene rotation
                bool following = _vol.follow != null && _vol.follow.isActiveAndEnabled && _vol.followRotation;
                Quaternion baseRot = following ? transform.rotation : _baseRot;
                transform.rotation = Quaternion.AngleAxis(spinDegreesPerSecond * Mathf.Max(0f, t), Vector3.up) * baseRot;
            }
            float label = Mathf.Clamp01((t - arrive - labelDelay) / Mathf.Max(0.01f, labelSeconds)) *
                          Mathf.Clamp01(1f - back * 5f);
            _canvas.SetActive(label > 0f);
            if (label <= 0f) return;
            _group.alpha = label;

            // above the map's box, facing the viewer with world-up as up (like the neuronal-loss card)
            Matrix4x4 m = _vol.UnitCubeToWorld;
            var b = new Bounds(m.MultiplyPoint(Vector3.zero), Vector3.zero);
            for (int i = 1; i < 8; i++) b.Encapsulate(m.MultiplyPoint(new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)));
            Vector3 f = _frame != null ? _frame.forward : Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            _canvas.transform.SetPositionAndRotation(new Vector3(b.center.x, b.max.y + labelGap, b.center.z),
                                                     Quaternion.LookRotation(f.normalized, Vector3.up));
        }

        void BuildCard()
        {
            _canvas = new GameObject(name + "_Label", typeof(Canvas));
            _canvas.transform.SetParent(transform.parent, false);
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            _canvas.transform.localScale = Vector3.one * cardScale;
            _group = _canvas.AddComponent<CanvasGroup>();
            _group.interactable = false; _group.blocksRaycasts = false;

            var font = UiKit.DefaultFont;
            var card = UiKit.Rect("Card", _canvas.transform);
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0f);                         // bottom-centre on the anchor
            card.gameObject.AddComponent<Image>().color = cardColor;
            var titleText = UiKit.Label("Title", card, title, titleFontSize, FontStyle.BoldAndItalic, titleColor, TextAnchor.UpperCenter, font);
            float w = titleText.preferredWidth, y = padding;
            Text tagText = null;
            if (!string.IsNullOrEmpty(tagLine))
            {
                tagText = UiKit.Label("Tag", card, tagLine, tagFontSize, FontStyle.BoldAndItalic, tagColor, TextAnchor.UpperCenter, font);
                w = Mathf.Max(w, tagText.preferredWidth);
            }
            w = Mathf.Ceil(w) + 2f * padding + 8f;
            float inner = w - 2f * padding;
            Top(titleText.rectTransform, y, inner, titleFontSize * 1.25f); y += titleFontSize * 1.25f;
            if (tagText != null) { Top(tagText.rectTransform, y, inner, tagFontSize * 1.3f); y += tagFontSize * 1.3f; }
            card.sizeDelta = new Vector2(w, y + padding);
            _canvas.SetActive(false);
        }

        // same curves as NeuronalLossSequence
        static float EaseInOutCubic(float x)
        {
            x = Mathf.Clamp01(x);
            return x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) * 0.5f;
        }

        static float EaseOutBack(float x, float amount)
        {
            x = Mathf.Clamp01(x);
            float c1 = amount * 17f, c3 = c1 + 1f, y = x - 1f;
            return 1f + c3 * y * y * y + c1 * y * y;
        }

        static void Top(RectTransform rt, float top, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(0f, -top);
        }

        void OnDestroy()
        {
            if (_canvas != null) Destroy(_canvas);
        }
    }
}
