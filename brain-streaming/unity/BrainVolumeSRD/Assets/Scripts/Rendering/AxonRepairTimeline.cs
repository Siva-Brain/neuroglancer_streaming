using UnityEngine;
using UnityEngine.UI;
using SRD.Utils;

namespace BrainVolume
{
    /// <summary>
    /// V4 "Axon Damage Repair": the four stages of the reference video
    /// (npz_files/V4/axon_damage_repair_APP_GAP43_540p15.mp4), run in the NeuronalLossSequence's hold
    /// (after Brain + Rotate to the left sagittal view, before Combine / Return):
    ///   1. Healthy axons    the brain fades to a see-through shell, the blue fibres fade in   healthySeconds
    ///   2. Axonal damage    APP+ (orange / red) spreads out from the stroke centre             appSeconds
    ///   3. Axonal repair    GAP43+ (green) grows out from the core                             gapSeconds
    ///   4. Damage and repair  all together, until Combine
    /// On Combine everything fades out and the brain becomes solid again; Return turns it back to the
    /// start pose, so the loop is seamless. A white card above the brain shows the stage title, a line
    /// of text and a colour legend (the video's title / subtitle / legend). Purely a function of the
    /// timeline time, so seeking / looping lands exactly.
    /// </summary>
    [DefaultExecutionOrder(110)]   // after AxonDamageRepairVolume (100) has followed the brain
    public sealed class AxonRepairTimeline : MonoBehaviour
    {
        public NeuronalLossSequence timeline;
        public AxonDamageRepairVolume axons;
        [Tooltip("The brain whose opacity fades to a shell while the fibres are shown (BrickVolumeLoader).")]
        public BrainVolume.SRD.BrickVolumeLoader brain;

        [Header("Stages (seconds, from the timeline's hold)")]
        public float healthySeconds = 4f;
        public float appSeconds = 5.5f;
        public float gapSeconds = 5f;
        [Tooltip("Seconds for the brain to fade to a shell / the fibres to fade in.")]
        public float fadeSeconds = 2f;
        [Tooltip("The brain's opacity while the fibres are shown (its own opacity is restored on Combine).")]
        [Range(0.02f, 1f)] public float shellOpacity = 0.2f;

        [Header("Spread (mm around the stroke centre)")]
        [Tooltip("How far APP+ has spread at the end of its stage (the far back of the hemisphere stays blue).")]
        public float appRadiusEnd = 65f;
        [Tooltip("How far GAP43+ has grown at the end of its stage (all of it is within ~50 mm).")]
        public float gapRadiusEnd = 60f;

        [Header("Card")]
        public Color cardColor = Color.white;
        public Color titleColor = new Color(0.04f, 0.04f, 0.06f, 1f);
        public Color tagColor = new Color(0.10f, 0.10f, 0.12f, 1f);
        public int titleFontSize = 34, tagFontSize = 19, legendFontSize = 17;
        public float padding = 18f;
        [Tooltip("World units per canvas unit.")]
        public float cardScale = 0.0005f;
        [Tooltip("Gap between the top of the brain and the card (world units).")]
        public float labelGap = 0.02f;

        static readonly string[] Titles = { "Healthy axons", "Axonal damage  ·  APP", "Axonal repair  ·  GAP43", "Damage and repair" };
        static readonly string[] Tags =
        {
            "White matter of the stroke hemisphere  ·  histology volume",
            "APP+ axons spreading from the stroke: orange = light, red = dense",
            "Regenerating GAP43+ axons growing back into the damaged tissue",
            "APP (red) and GAP43 (green) in the same hemisphere",
        };

        float _brainOpacity;
        GameObject _canvas;
        CanvasGroup _group;
        Text _title, _tag, _legend;
        Transform _frame;
        int _shownStage = -1;

        // before the timeline computes its phases (Start): the stage names on the transport bar
        void Awake()
        {
            if (timeline == null) timeline = FindFirstObjectByType<NeuronalLossSequence>();
            if (timeline == null) return;
            float[] starts = { 0f, healthySeconds, healthySeconds + appSeconds, healthySeconds + appSeconds + gapSeconds };
            string[] names = { "Healthy axons", "APP damage", "GAP43 repair", "Damage + repair" };
            for (int i = 0; i < starts.Length; i++)
                timeline.holdMarks.Add(new NeuronalLossSequence.Mark { name = names[i], start = starts[i] });
            // start the timeline only once the three maps are in (they take a few seconds to read)
            if (axons == null) axons = FindFirstObjectByType<AxonDamageRepairVolume>();
            var a = axons;
            if (a != null) timeline.waitFor.Add(() => a == null || !a.isActiveAndEnabled || a.Loaded);
        }

        void Start()
        {
            if (axons == null) axons = FindFirstObjectByType<AxonDamageRepairVolume>();
            if (brain == null) brain = FindFirstObjectByType<BrainVolume.SRD.BrickVolumeLoader>();
            _brainOpacity = brain != null ? brain.opacity : 1f;
            var srd = SRDSceneEnvironment.GetSRDManager();
            _frame = srd != null && srd.isActiveAndEnabled ? srd.transform : null;
            if (axons != null) axons.visibility = 0f;
            BuildCard();
        }

        void LateUpdate()
        {
            if (timeline == null || axons == null || !timeline.Ready)
            {
                if (axons != null) axons.visibility = 0f;
                if (_canvas != null) _canvas.SetActive(false);
                return;
            }
            float t = timeline.Time - timeline.HoldStart;
            float tApp = healthySeconds, tGap = tApp + appSeconds, tBoth = tGap + gapSeconds;
            float back = Mathf.Clamp01(timeline.CombineAmount);          // 0 -> 1 on Combine
            float shown = t < 0f ? 0f : Ease(t / Mathf.Max(0.01f, fadeSeconds)) * (1f - back);

            axons.visibility = shown;
            axons.healthyAmount = 1f;
            axons.appRadiusMm = t < tApp ? -100f : appRadiusEnd * EaseOut((t - tApp) / Mathf.Max(0.01f, appSeconds));
            axons.gapRadiusMm = t < tGap ? -100f : gapRadiusEnd * EaseOut((t - tGap) / Mathf.Max(0.01f, gapSeconds));
            if (brain != null) brain.opacity = Mathf.Lerp(_brainOpacity, shellOpacity, shown);

            // card: the current stage, fading in at its start and out just before the next
            int stage = t < 0f ? -1 : t < tApp ? 0 : t < tGap ? 1 : t < tBoth ? 2 : 3;
            float[] starts = { 0f, tApp, tGap, tBoth, float.PositiveInfinity };
            float alpha = 0f;
            if (stage >= 0)
            {
                alpha = Mathf.Clamp01((t - starts[stage]) / 0.4f) * Mathf.Clamp01((starts[stage + 1] - t) / 0.3f) *
                        Mathf.Clamp01(1f - back * 4f);
                if (stage == 0) alpha *= Mathf.Clamp01(t / Mathf.Max(0.01f, fadeSeconds));
            }
            _canvas.SetActive(alpha > 0f);
            if (alpha <= 0f) return;
            if (stage != _shownStage) SetStage(stage);
            _group.alpha = alpha;

            // above the brain, facing the viewer with world-up as up
            Matrix4x4 m = axons.UnitCubeToWorld;
            var b = new Bounds(m.MultiplyPoint(Vector3.zero), Vector3.zero);
            for (int i = 1; i < 8; i++) b.Encapsulate(m.MultiplyPoint(new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)));
            Vector3 f = _frame != null ? _frame.forward : Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            _canvas.transform.SetPositionAndRotation(new Vector3(b.center.x, b.max.y + labelGap, b.center.z),
                                                     Quaternion.LookRotation(f.normalized, Vector3.up));
        }

        // ------------------------------------------------------------------ card

        string Legend(int stage)
        {
            string s = Dot(axons.healthyColor) + " Healthy axons (white matter)";
            if (stage >= 1) s += "     " + Dot(axons.appLightColor) + " APP+ light     " + Dot(axons.appDenseColor) + " APP+ dense (damage)";
            if (stage >= 2) s += "     " + Dot(axons.gapColor) + " GAP43+ (repair)";
            return s;
        }

        static string Dot(Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>●</color>";

        void SetStage(int stage)
        {
            _shownStage = stage;
            _title.text = Titles[stage];
            _tag.text = Tags[stage];
            _legend.text = Legend(stage);
        }

        void BuildCard()
        {
            _canvas = new GameObject("AxonRepair_Label", typeof(Canvas));
            _canvas.transform.SetParent(transform, false);
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            _canvas.transform.localScale = Vector3.one * cardScale;
            _group = _canvas.AddComponent<CanvasGroup>();
            _group.interactable = false; _group.blocksRaycasts = false;

            var font = UiKit.DefaultFont;
            var card = UiKit.Rect("Card", _canvas.transform);
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0f);                         // bottom-centre on the anchor
            card.gameObject.AddComponent<Image>().color = cardColor;
            _title = UiKit.Label("Title", card, "", titleFontSize, FontStyle.BoldAndItalic, titleColor, TextAnchor.UpperCenter, font);
            _tag = UiKit.Label("Tag", card, "", tagFontSize, FontStyle.BoldAndItalic, tagColor, TextAnchor.UpperCenter, font);
            _legend = UiKit.Label("Legend", card, "", legendFontSize, FontStyle.Bold, tagColor, TextAnchor.UpperCenter, font);
            _legend.supportRichText = true;

            // one size for every stage: the widest title / line / legend
            float w = 0f;
            for (int s = 0; s < Titles.Length; s++)
            {
                _title.text = Titles[s]; _tag.text = Tags[s]; _legend.text = Legend(s);
                w = Mathf.Max(w, _title.preferredWidth, _tag.preferredWidth, _legend.preferredWidth);
            }
            w = Mathf.Ceil(w) + 2f * padding + 8f;
            float inner = w - 2f * padding, y = padding;
            Top(_title.rectTransform, y, inner, titleFontSize * 1.25f); y += titleFontSize * 1.25f;
            Top(_tag.rectTransform, y, inner, tagFontSize * 1.3f); y += tagFontSize * 1.3f + 8f;
            Top(_legend.rectTransform, y, inner, legendFontSize * 1.3f); y += legendFontSize * 1.3f;
            card.sizeDelta = new Vector2(w, y + padding);
            _canvas.SetActive(false);
        }

        static void Top(RectTransform rt, float top, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(0f, -top);
        }

        static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        static float EaseOut(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x); }

        void OnDestroy()
        {
            if (brain != null) brain.opacity = _brainOpacity;
            if (_canvas != null) Destroy(_canvas);
        }
    }
}
