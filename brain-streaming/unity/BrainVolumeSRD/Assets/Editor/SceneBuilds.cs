using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BrainVolume.EditorTools
{
    /// <summary>
    /// One Windows exe per presentation scene, also from the command line:
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod BrainVolume.EditorTools.SceneBuilds.BuildNisslAndLabelsV1
    /// Output: Builds/&lt;scene name&gt;/&lt;scene name&gt;.exe
    /// </summary>
    public static class SceneBuilds
    {
        [MenuItem("Brain/Build/Nissl and Labels V1")]
        public static void BuildNisslAndLabelsV1() => Build("Nissl and Labels V1");

        // V1 three-way split: BFI (left), Nissl (middle), labels (right), smaller so the three fit side by side.
        // At z -0.35 the viewer (0, 0.64, -1.62) sees about x +-0.70.
        public static Vector3 V1BfiPosition = new Vector3(-0.47f, 0.49f, -0.35f);
        public static Vector3 V1NisslPosition = new Vector3(0f, 0.49f, -0.35f);
        public static Vector3 V1LabelsPosition = new Vector3(0.47f, 0.49f, -0.35f);
        public static float V1SplitScale = 0.65f;

        /// <summary>
        /// V1: adds the BFI NIfTI (bfi/BFI_in_MRI_2.nii, shipped from StreamingAssets/Nifti) as a third split brain.
        /// It follows the Nissl brain's rotation and cut; the timeline moves it left while Nissl goes to the
        /// middle and the labels to the right.
        /// </summary>
        [MenuItem("Brain/Scenes/V1: add BFI (three-way split)")]
        public static void AddBfiToV1()
        {
            if (EditorSceneManager.GetActiveScene().isDirty) EditorSceneManager.SaveOpenScenes();
            var sc = EditorSceneManager.OpenScene("Assets/Scenes/Nissl and Labels V1.unity", OpenSceneMode.Single);
            GameObject bfi = null, brain = null;
            foreach (var go in sc.GetRootGameObjects())
            {
                if (go.name == "BFI") bfi = go;
                if (go.name == "Brain") brain = go;
            }
            if (bfi == null)
            {
                bfi = new GameObject("BFI");
                SceneManager.MoveGameObjectToScene(bfi, sc);
                bfi.AddComponent<NpzDensityVolume>();
            }
            bfi.transform.SetPositionAndRotation(brain.transform.position, brain.transform.rotation);
            bfi.transform.localScale = brain.transform.localScale;
            var vol = bfi.GetComponent<NpzDensityVolume>();
            var v = new SerializedObject(vol);
            v.FindProperty("npzPath").stringValue = "bfi/BFI_in_MRI_2.nii";
            v.FindProperty("fillGapMm").floatValue = 0f;          // a continuous volume, no section gaps to fill
            v.FindProperty("inPlaneFillMm").floatValue = 0f;
            v.FindProperty("percentileHigh").floatValue = 99.5f;
            v.FindProperty("threshold").floatValue = 0.35f;       // values 0..1, tissue mostly 0.5..1
            v.FindProperty("density").floatValue = 25f;
            v.FindProperty("valueGamma").floatValue = 1.5f;
            v.FindProperty("raySteps").intValue = 400;
            v.FindProperty("shading").floatValue = 0.7f;
            v.FindProperty("brightness").floatValue = 1.1f;
            v.FindProperty("colorLow").colorValue = new Color(0.45f, 0.30f, 0.25f);
            v.FindProperty("colorMid").colorValue = new Color(0.80f, 0.62f, 0.52f);
            v.FindProperty("colorHigh").colorValue = new Color(1.00f, 0.92f, 0.85f);
            v.FindProperty("follow").objectReferenceValue = brain.GetComponent<BrainVolume.SRD.BrickVolumeLoader>();
            v.FindProperty("followRotation").boolValue = true;
            v.FindProperty("followSlice").boolValue = true;
            v.FindProperty("visibility").floatValue = 0f;
            v.ApplyModifiedPropertiesWithoutUndo();

            var seq = Object.FindFirstObjectByType<NeuronalLossSequence>();
            var so = new SerializedObject(seq);
            so.FindProperty("splitThird").objectReferenceValue = vol;
            so.FindProperty("splitThirdPosition").vector3Value = V1BfiPosition;
            so.FindProperty("splitLeftPosition").vector3Value = V1NisslPosition;    // the driven (Nissl) brain
            so.FindProperty("splitRightPosition").vector3Value = V1LabelsPosition;
            so.FindProperty("splitScale").floatValue = V1SplitScale;
            // ending like V3: after the 360 turn the three slide back together (Combine) and the brain turns back to
            // its start pose (Return), so the loop is seamless. 30 s: brain 2.5 + rotate 4 + split 3 + slice 5.5
            // + 0.3 + slice back 5.5 + turn 5 + combine 2 + return 2.2 (no hold)
            so.FindProperty("finalTurnSeconds").floatValue = 5f;
            so.FindProperty("hold").floatValue = 0f;
            so.FindProperty("returnToStart").boolValue = true;
            so.FindProperty("combineSeconds").floatValue = 2f;
            so.FindProperty("returnSeconds").floatValue = 2.2f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.SaveScene(sc);
            Debug.Log($"[SceneBuilds] V1: BFI {V1BfiPosition}, Nissl {V1NisslPosition}, labels {V1LabelsPosition}, split scale {V1SplitScale}.");
        }

        [MenuItem("Brain/Build/NeuronalLoss Display V2")]
        public static void BuildNeuronalLossDisplayV2() => Build(V2);

        const string V2 = "NeuronalLoss Display V2";
        // V2 brain start (the user's V1 transform, kept for V2)
        public static Vector3 V2BrainStart = new Vector3(0.082f, 0.49f, -0.57f);

        /// <summary>V2 ending only (no re-copy from V1): Combine + Return like V1/V3, still 30 s.</summary>
        [MenuItem("Brain/Scenes/V2: return to start at the end")]
        public static void SetV2ReturnToStart()
        {
            if (EditorSceneManager.GetActiveScene().isDirty) EditorSceneManager.SaveOpenScenes();
            var sc = EditorSceneManager.OpenScene($"Assets/Scenes/{V2}.unity", OpenSceneMode.Single);
            var seq = Object.FindFirstObjectByType<NeuronalLossSequence>();
            var so = new SerializedObject(seq);
            so.FindProperty("hold").floatValue = 3.2f;
            so.FindProperty("returnToStart").boolValue = true;
            so.FindProperty("combineSeconds").floatValue = 2.5f;
            so.FindProperty("returnSeconds").floatValue = 2.5f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.SaveScene(sc);
            Debug.Log($"[SceneBuilds] {V2}: returnToStart, hold 3.2 + combine 2.5 + return 2.5.");
        }

        /// <summary>
        /// V2 = brain -> turn to the left sagittal view -> slice -> the neuronal-loss block (the two
        /// npy_neuronal_loss/*.npy ROI stacks, packed in StreamingAssets/NeuronalLoss/*_RG.bytes) grows
        /// out of the cut face, then loops. Copied from V1 (same brain, start pose, shaders, cameras)
        /// with the split / slice back / final turn off and the labelled FusedVolume switched off.
        /// </summary>
        [MenuItem("Brain/Scenes/Create NeuronalLoss Display V2")]
        public static void CreateNeuronalLossDisplayV2()
        {
            string src = "Assets/Scenes/Nissl and Labels V1.unity", dst = $"Assets/Scenes/{V2}.unity";
            if (EditorSceneManager.GetActiveScene().isDirty) EditorSceneManager.SaveOpenScenes();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(dst) == null && !AssetDatabase.CopyAsset(src, dst))
            {
                Debug.LogError($"[SceneBuilds] Could not copy {src} -> {dst}");
                return;
            }
            var sc = EditorSceneManager.OpenScene(dst, OpenSceneMode.Single);
            foreach (var go in sc.GetRootGameObjects())
                if (go.name == "FusedVolume" || go.name == "AstrocyteDensity" || go.name == "BFI") go.SetActive(false);

            var seq = Object.FindFirstObjectByType<NeuronalLossSequence>();
            var so = new SerializedObject(seq);
            so.FindProperty("splitVolume").objectReferenceValue = null;
            so.FindProperty("splitThird").objectReferenceValue = null;    // V1's BFI
            so.FindProperty("splitScale").floatValue = 1f;
            so.FindProperty("startPosition").vector3Value = V2BrainStart;
            so.FindProperty("showNeuronalLoss").boolValue = true;
            so.FindProperty("clippingDepth").floatValue = 0.3f;
            so.FindProperty("sliceBack").boolValue = false;
            so.FindProperty("finalTurnSeconds").floatValue = 0f;
            so.FindProperty("loop").boolValue = true;
            // 30 s: brain 3 + rotate 6 + slice 6 + grow 6 + label 0.2 + 0.6 + hold 3.2 (-> 25)
            // + combine 2.5 (the block flies back into the cut face) + return 2.5 (the brain turns back to its start pose)
            so.FindProperty("showBrain").floatValue = 3f;
            so.FindProperty("rotate").floatValue = 6f;
            so.FindProperty("slice").floatValue = 6f;
            so.FindProperty("grow").floatValue = 6f;
            so.FindProperty("labelDelay").floatValue = 0.2f;
            so.FindProperty("labelSeconds").floatValue = 0.6f;
            so.FindProperty("hold").floatValue = 3.2f;
            so.FindProperty("returnToStart").boolValue = true;
            so.FindProperty("combineSeconds").floatValue = 2.5f;
            so.FindProperty("returnSeconds").floatValue = 2.5f;
            so.ApplyModifiedPropertiesWithoutUndo();
            var rec = seq.GetComponent<TimelineRecorder>();
            if (rec != null)
            {
                var r = new SerializedObject(rec);
                r.FindProperty("tail").floatValue = 0f;
                r.FindProperty("filePrefix").stringValue = "neuronal_loss_display_v2";
                r.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorSceneManager.SaveScene(sc);
            Debug.Log($"[SceneBuilds] {dst}: loop={seq.loop}, block={seq.showNeuronalLoss}, cut={seq.clippingDepth}, " +
                      $"start={seq.startPosition}, total={seq.showBrain + seq.rotate + seq.slice + seq.grow + seq.labelDelay + seq.labelSeconds + seq.hold:F1} s");
        }

        [MenuItem("Brain/Build/NeuronalLoss Fib Astrocytes V3")]
        public static void BuildNeuronalLossFibAstrocytesV3() => Build(V3);

        const string V3 = "NeuronalLoss Fib Astrocytes V3";

        // V3 map placement (world): to the right of the brain, Fib above, astrocytes below. The SRD panel is
        // x +-0.895, y 0..1.007 at z = 0 and the viewer sits at about (0, 0.64, -1.62), so the closer to the
        // viewer, the narrower the visible area: at z -0.3 about x +-0.73, y 0.12..0.94.
        public static Vector3 V3FibPosition = new Vector3(0.53f, 0.70f, -0.30f);
        public static Vector3 V3AstroPosition = new Vector3(0.53f, 0.32f, -0.30f);
        public static float V3MapScale = 0.85f;
        // the neuronal-loss block settles this far out of the cut face (V2: 0.3, near the left edge of the view)
        public static float V3BlockComeOut = 0.1f;
        // its long-axis length (V2: 0.2); its card scales with it
        public static float V3BlockLength = 0.27f;
        // before the three come out, the brain moves this far back (deeper into the display) and shrinks to this
        public static Vector3 V3BrainBack = new Vector3(0f, 0f, 0.35f);
        public static float V3BrainBackScale = 0.7f;
        // V3 brain start: centred (x 0), same height / depth / rotation / scale as V1 and V2
        public static Vector3 V3BrainStart = new Vector3(0f, 0.49f, -0.57f);

        /// <summary>
        /// V3 = V2 (brain -> turn -> 30% slice -> neuronal-loss block upper-left, 30 s, loops) plus, when the
        /// block appears, two whole-brain maps fading in on the right with label cards: Fib (fib_probability
        /// npz) upper-right, astrocytes (astrocyte_density npz) lower-right. Both follow the brain's rotation.
        /// </summary>
        [MenuItem("Brain/Scenes/Create NeuronalLoss Fib Astrocytes V3")]
        public static void CreateNeuronalLossFibAstrocytesV3()
        {
            string src = $"Assets/Scenes/{V2}.unity", dst = $"Assets/Scenes/{V3}.unity";
            if (EditorSceneManager.GetActiveScene().isDirty) EditorSceneManager.SaveOpenScenes();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(dst) == null && !AssetDatabase.CopyAsset(src, dst))
            {
                Debug.LogError($"[SceneBuilds] Could not copy {src} -> {dst}");
                return;
            }
            var sc = EditorSceneManager.OpenScene(dst, OpenSceneMode.Single);
            GameObject astro = null, fib = null;
            foreach (var go in sc.GetRootGameObjects())
            {
                if (go.name == "AstrocyteDensity") astro = go;
                if (go.name == "FibProbability") fib = go;
            }
            if (astro == null) { Debug.LogError("[SceneBuilds] V3: no AstrocyteDensity in " + src); return; }
            if (fib == null)
            {
                fib = Object.Instantiate(astro);
                fib.name = "FibProbability";
                SceneManager.MoveGameObjectToScene(fib, sc);
            }
            var seq = Object.FindFirstObjectByType<NeuronalLossSequence>();
            var sq = new SerializedObject(seq);
            sq.FindProperty("comeOut").floatValue = V3BlockComeOut;
            // the brain moves back and shrinks (first 2 s) while the three come out of it, all starting together;
            // from 25 s they fly back into it (2.5 s) and it turns back to its start pose (2.5 s), so the loop is seamless.
            // 30 s: brain 3 + rotate 6 + slice 6 + grow 6 + label 0.2 + 0.6 + hold 3.2 (-> 25) + combine 2.5 + return 2.5
            sq.FindProperty("startPosition").vector3Value = V3BrainStart;
            sq.FindProperty("returnToStart").boolValue = true;
            sq.FindProperty("combineSeconds").floatValue = 2.5f;
            sq.FindProperty("returnSeconds").floatValue = 2.5f;
            sq.FindProperty("recede").floatValue = 2f;
            sq.FindProperty("recedeDuringGrow").boolValue = true;
            sq.FindProperty("recedeOffset").vector3Value = V3BrainBack;
            sq.FindProperty("recedeScale").floatValue = V3BrainBackScale;
            sq.FindProperty("blockLength").floatValue = V3BlockLength;
            sq.FindProperty("hold").floatValue = 3.2f;
            sq.ApplyModifiedPropertiesWithoutUndo();
            SetupMap(astro, seq, "npz_files/astrocyte_density_HB02_0.24mm.npz", "density", "Astrocytes", 0f, V3AstroPosition,
                     new Color(0.15f, 0.35f, 1.00f), new Color(0.20f, 0.95f, 0.70f), new Color(1.00f, 0.85f, 0.20f));
            SetupMap(fib, seq, "npz_files/fib_probability_HB02_0.24mm_0.9999.npz", "probability", "Fib", 0f, V3FibPosition,
                     new Color(0.85f, 0.20f, 0.10f), new Color(1.00f, 0.50f, 0.15f), new Color(1.00f, 0.90f, 0.45f));

            var rec = seq.GetComponent<TimelineRecorder>();
            if (rec != null)
            {
                var r = new SerializedObject(rec);
                r.FindProperty("filePrefix").stringValue = "neuronal_loss_fib_astrocytes_v3";
                r.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorSceneManager.SaveScene(sc);
            Debug.Log($"[SceneBuilds] {dst}: Fib at {V3FibPosition}, astrocytes at {V3AstroPosition}, scale {V3MapScale}.");
        }

        static void SetupMap(GameObject go, NeuronalLossSequence seq, string npz, string array, string title, float delay,
                             Vector3 pos, Color low, Color mid, Color high)
        {
            go.SetActive(true);
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * V3MapScale;
            // the keyboard/mouse move the brain (or the block); the maps stay where the timeline puts them
            var mover = go.GetComponent<ModelMoveController>();
            if (mover != null) Object.DestroyImmediate(mover);

            var vol = go.GetComponent<NpzDensityVolume>();
            var v = new SerializedObject(vol);
            v.FindProperty("npzPath").stringValue = npz;
            v.FindProperty("arrayName").stringValue = array;
            v.FindProperty("followRotation").boolValue = true;
            v.FindProperty("followSlice").boolValue = false;    // the whole map, not the brain's cut
            v.FindProperty("colorLow").colorValue = low;
            v.FindProperty("colorMid").colorValue = mid;
            v.FindProperty("colorHigh").colorValue = high;
            v.ApplyModifiedPropertiesWithoutUndo();

            var ov = go.GetComponent<TimelineDensityOverlay>();
            if (ov == null) ov = go.AddComponent<TimelineDensityOverlay>();
            var o = new SerializedObject(ov);
            o.FindProperty("timeline").objectReferenceValue = seq;
            o.FindProperty("appearAtMark").stringValue = "Neuronal loss";
            o.FindProperty("delay").floatValue = delay;
            o.FindProperty("title").stringValue = title;
            o.FindProperty("comeOutOfBrain").boolValue = true;   // out of the cut face, like the neuronal-loss block
            o.FindProperty("labelDelay").floatValue = 0.2f;      // card 0.2 s after it has arrived
            // farther from the viewer than the neuronal-loss card (and a one-word title): bigger cards
            o.FindProperty("cardScale").floatValue = 0.0006f;
            o.FindProperty("titleFontSize").intValue = 34;
            o.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("Brain/Build/Axon Damage Repair V4")]
        public static void BuildAxonDamageRepairV4() => Build(V4);

        const string V4 = "Axon Damage Repair V4";

        /// <summary>
        /// V4 = the reference video npz_files/V4/axon_damage_repair_APP_GAP43_540p15.mp4 on the bricked brain:
        /// brain (V3's centred start pose) -> turn to the left sagittal view -> (no slice) the brain fades to a
        /// shell while the healthy fibres fade in -> APP+ damage spreads from the stroke -> GAP43+ repair grows
        /// from the core -> all together -> Combine (fade out) + Return to the start pose; 30 s, loops.
        /// The three maps (npz_files/V4/*.npz) are one AxonDamageRepairVolume, staged by AxonRepairTimeline.
        /// </summary>
        [MenuItem("Brain/Scenes/Create Axon Damage Repair V4")]
        public static void CreateAxonDamageRepairV4()
        {
            string src = $"Assets/Scenes/{V3}.unity", dst = $"Assets/Scenes/{V4}.unity";
            if (EditorSceneManager.GetActiveScene().isDirty) EditorSceneManager.SaveOpenScenes();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(dst) == null && !AssetDatabase.CopyAsset(src, dst))
            {
                Debug.LogError($"[SceneBuilds] Could not copy {src} -> {dst}");
                return;
            }
            AlwaysInclude("Brain/AxonDamageRepair");
            var sc = EditorSceneManager.OpenScene(dst, OpenSceneMode.Single);
            GameObject brain = null, axons = null;
            foreach (var go in sc.GetRootGameObjects())
            {
                if (go.name == "AstrocyteDensity" || go.name == "FibProbability" || go.name == "FusedVolume" || go.name == "BFI")
                    go.SetActive(false);
                if (go.name == "Brain") brain = go;
                if (go.name == "AxonDamageRepair") axons = go;
            }
            if (brain == null) { Debug.LogError("[SceneBuilds] V4: no Brain in " + src); return; }
            if (axons == null)
            {
                axons = new GameObject("AxonDamageRepair");
                SceneManager.MoveGameObjectToScene(axons, sc);
            }
            var vol = axons.GetComponent<AxonDamageRepairVolume>();
            if (vol == null) vol = axons.AddComponent<AxonDamageRepairVolume>();   // (not ??: Unity fake-null)
            var v = new SerializedObject(vol);
            v.FindProperty("follow").objectReferenceValue = brain.GetComponent<BrainVolume.SRD.BrickVolumeLoader>();
            v.ApplyModifiedPropertiesWithoutUndo();
            var stages = axons.GetComponent<AxonRepairTimeline>();
            if (stages == null) stages = axons.AddComponent<AxonRepairTimeline>();

            var seq = Object.FindFirstObjectByType<NeuronalLossSequence>();
            var so = new SerializedObject(seq);
            so.FindProperty("showNeuronalLoss").boolValue = false;
            so.FindProperty("slicing").boolValue = false;        // the fibres are shown inside the whole brain
            so.FindProperty("recede").floatValue = 0f;
            so.FindProperty("loop").boolValue = true;
            so.FindProperty("returnToStart").boolValue = true;
            // 30 s: brain 3 + rotate 5 (-> 8) + hold 17.5 (healthy 4 + APP 5.5 + GAP43 5 + together 3) (-> 25.5)
            // + combine 2 (everything fades, the brain becomes solid) + return 2.5 (back to the start pose)
            so.FindProperty("showBrain").floatValue = 3f;
            so.FindProperty("rotate").floatValue = 5f;
            so.FindProperty("hold").floatValue = 17.5f;
            so.FindProperty("combineSeconds").floatValue = 2f;
            so.FindProperty("returnSeconds").floatValue = 2.5f;
            so.ApplyModifiedPropertiesWithoutUndo();
            var st = new SerializedObject(stages);
            st.FindProperty("timeline").objectReferenceValue = seq;
            st.FindProperty("axons").objectReferenceValue = vol;
            st.FindProperty("brain").objectReferenceValue = brain.GetComponent<BrainVolume.SRD.BrickVolumeLoader>();
            st.ApplyModifiedPropertiesWithoutUndo();

            var rec = seq.GetComponent<TimelineRecorder>();
            if (rec != null)
            {
                var r = new SerializedObject(rec);
                r.FindProperty("filePrefix").stringValue = "axon_damage_repair_v4";
                r.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorSceneManager.SaveScene(sc);
            Debug.Log($"[SceneBuilds] {dst}: total {seq.showBrain + seq.rotate + seq.hold + seq.combineSeconds + seq.returnSeconds:F1} s.");
        }

        [MenuItem("Brain/Build/Presentation All")]
        public static void BuildPresentationAll() => Build(All);

        const string All = "Presentation All";

        /// <summary>
        /// "Presentation All" = V1-V4 in one scene with a menu bar (PresentationMenu) at the bottom of the display:
        /// play/pause + V1..V4 (keys 1-4). Rebuilt from the four scenes every time (safe to re-run; the four scenes
        /// are only read): a copy of V4 (the shared rig: Brain, SRD, cameras, AxonDamageRepair), plus each scene's
        /// timeline and its own objects (V1: FusedVolume labels + BFI, V3: Fib + astrocyte maps) moved in under a
        /// group per version. References to the source scene's Brain are pointed at this scene's Brain.
        /// </summary>
        [MenuItem("Brain/Scenes/Create Presentation All (V1-V4 + menu bar)")]
        public static void CreatePresentationAll()
        {
            string dst = $"Assets/Scenes/{All}.unity";
            if (EditorSceneManager.GetActiveScene().isDirty) EditorSceneManager.SaveOpenScenes();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(dst) != null) AssetDatabase.DeleteAsset(dst);
            if (!AssetDatabase.CopyAsset($"Assets/Scenes/{V4}.unity", dst)) { Debug.LogError("[SceneBuilds] Could not copy V4 -> " + dst); return; }
            var sc = EditorSceneManager.OpenScene(dst, OpenSceneMode.Single);

            // the per-version objects come from their own scenes: drop the (inactive) copies V4 inherited
            foreach (var go in sc.GetRootGameObjects())
                if (go.name == "FusedVolume" || go.name == "BFI" || go.name == "AstrocyteDensity" || go.name == "FibProbability")
                    Object.DestroyImmediate(go);

            string[] scenes = { "Nissl and Labels V1", V2, V3, V4 };
            string[][] extras =
            {
                new[] { "FusedVolume", "BFI" },
                new string[0],
                new[] { "FibProbability", "AstrocyteDensity" },
                new[] { "AxonDamageRepair" },
            };
            var timelines = new NeuronalLossSequence[4];
            for (int v = 0; v < 4; v++)
            {
                var group = new GameObject("V" + (v + 1));
                SceneManager.MoveGameObjectToScene(group, sc);
                Scene src = sc;
                if (v < 3) src = EditorSceneManager.OpenScene($"Assets/Scenes/{scenes[v]}.unity", OpenSceneMode.Additive);

                var moved = new System.Collections.Generic.List<GameObject>();
                foreach (var go in src.GetRootGameObjects())
                {
                    bool isTimeline = go.GetComponent<NeuronalLossSequence>() != null;
                    if (!isTimeline && System.Array.IndexOf(extras[v], go.name) < 0) continue;
                    if (src != sc) SceneManager.MoveGameObjectToScene(go, sc);
                    go.transform.SetParent(group.transform, true);
                    go.SetActive(true);
                    if (isTimeline)
                    {
                        go.name = "Timeline V" + (v + 1);
                        timelines[v] = go.GetComponent<NeuronalLossSequence>();
                        // one recorder per scene was fine; four in one scene would all answer F10
                        var rec = go.GetComponent<TimelineRecorder>();
                        if (rec != null) Object.DestroyImmediate(rec);
                    }
                    moved.Add(go);
                }
                if (src != sc)
                {
                    // the moved objects still point at the source scene's Brain etc.: point them at ours
                    foreach (var go in moved)
                        foreach (var c in go.GetComponentsInChildren<Component>(true))
                            if (c != null && !(c is Transform)) RemapToScene(c, src, sc);
                    EditorSceneManager.CloseScene(src, true);   // not saved: the source scene is untouched on disk
                }
                if (timelines[v] == null) { Debug.LogError($"[SceneBuilds] {All}: no timeline in {scenes[v]}"); return; }
                var so = new SerializedObject(timelines[v]);
                so.FindProperty("transportBar").boolValue = false;   // the menu adds one shared seek bar
                so.FindProperty("loop").boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var menuGo = new GameObject("PresentationMenu");
            SceneManager.MoveGameObjectToScene(menuGo, sc);
            var menu = menuGo.AddComponent<PresentationMenu>();
            var m = new SerializedObject(menu);
            var list = m.FindProperty("versions");
            list.arraySize = 4;
            string[] captions = { "Nissl & Labels", "Neuronal Loss", "Fib & Astrocytes", "Axon Repair" };
            m.FindProperty("startIndex").intValue = -1;   // only the brain until a version is selected
            for (int v = 0; v < 4; v++)
            {
                list.GetArrayElementAtIndex(v).FindPropertyRelative("label").stringValue = "V" + (v + 1);
                list.GetArrayElementAtIndex(v).FindPropertyRelative("caption").stringValue = captions[v];
                list.GetArrayElementAtIndex(v).FindPropertyRelative("timeline").objectReferenceValue = timelines[v];
            }
            m.ApplyModifiedPropertiesWithoutUndo();
            menuGo.AddComponent<TimelineTransportUI>();

            // Ultraleap: move / rotate / scale the brain by hand (creates a desktop LeapServiceProvider at runtime)
            var hands = new GameObject("HandControl");
            SceneManager.MoveGameObjectToScene(hands, sc);
            var leap = hands.AddComponent<LeapBrainManipulator>();
            var view = hands.AddComponent<LeapHandRenderer>();   // the hands themselves, drawn on top of the brain
            AlwaysInclude("Brain/HandOverlay");
            var hv = new SerializedObject(view);
            hv.FindProperty("handsPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.ultraleap.tracking/Hands/Runtime/Prefabs/Built In Render Pipeline (Dynamically Upgradable)/GhostHands.prefab");
            hv.FindProperty("manipulator").objectReferenceValue = leap;
            hv.ApplyModifiedPropertiesWithoutUndo();
            var point = hands.AddComponent<LeapMenuInteractor>();   // point + pinch = click the menu bar
            var pm = new SerializedObject(point);
            pm.FindProperty("menu").objectReferenceValue = menu;
            pm.FindProperty("manipulator").objectReferenceValue = leap;
            pm.ApplyModifiedPropertiesWithoutUndo();
            if (view.handsPrefab == null) Debug.LogError("[SceneBuilds] GhostHands prefab not found in the Ultraleap package.");
            foreach (var go in sc.GetRootGameObjects())
                if (go.name == "Brain")
                {
                    var l = new SerializedObject(leap);
                    l.FindProperty("volume").objectReferenceValue = go.GetComponent<BrainVolume.SRD.BrickVolumeLoader>();
                    l.ApplyModifiedPropertiesWithoutUndo();
                }

            EditorSceneManager.MarkSceneDirty(sc);
            EditorSceneManager.SaveScene(sc);
            Debug.Log($"[SceneBuilds] {dst}: V1-V4 timelines + menu bar (keys 1-4).");
        }

        const string Flat = "Presentation All Flat";

        [MenuItem("Brain/Build/Presentation All Flat (85-inch screen)")]
        public static void BuildPresentationAllFlat() => Build(Flat);

        /// <summary>
        /// "Presentation All Flat" = Presentation All on an ordinary flat screen (the 85-inch display). A copy of
        /// Presentation All (re-run after changing it; the source is only read) with the SRDisplayManager saved
        /// inactive (the Sony plugin never starts; its transform stays the display frame), a FlatCamera (MainCamera,
        /// Display 1) with FlatDisplayRig (virtual ELF-SR2 panel + camera framing it, and the Ultraleap mapping:
        /// HandControl stays on, its provider placed by FlatDisplayRig.PlaceHandDevice) and DualScreenView off.
        /// </summary>
        [MenuItem("Brain/Scenes/Create Presentation All Flat (85-inch screen)")]
        public static void CreatePresentationAllFlat()
        {
            string dst = $"Assets/Scenes/{Flat}.unity";
            if (EditorSceneManager.GetActiveScene().isDirty) EditorSceneManager.SaveOpenScenes();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(dst) != null) AssetDatabase.DeleteAsset(dst);
            if (!AssetDatabase.CopyAsset($"Assets/Scenes/{All}.unity", dst)) { Debug.LogError("[SceneBuilds] Could not copy " + All + " -> " + dst); return; }
            var sc = EditorSceneManager.OpenScene(dst, OpenSceneMode.Single);

            Camera cam = null;
            foreach (var go in sc.GetRootGameObjects())
            {
                if (go.GetComponent<global::SRD.Core.SRDManager>() != null) go.SetActive(false);
                foreach (var dv in go.GetComponentsInChildren<DualScreenView>(true)) dv.enabled = false;
                if (go.name == "Display2Camera" || go.name == "FlatCamera") cam = go.GetComponent<Camera>();
            }
            if (cam == null)
            {
                var g = new GameObject("FlatCamera");
                SceneManager.MoveGameObjectToScene(g, sc);
                cam = g.AddComponent<Camera>();
            }
            var cg = cam.gameObject;
            cg.name = "FlatCamera";
            cg.tag = "MainCamera";
            cg.SetActive(true);
            cam.targetDisplay = 0;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.nearClipPlane = 0.01f;
            if (cg.GetComponent<AudioListener>() == null) cg.AddComponent<AudioListener>();
            var rig = cg.GetComponent<FlatDisplayRig>();
            if (rig == null) rig = cg.AddComponent<FlatDisplayRig>();
            rig.viewCamera = cam;
            EditorUtility.SetDirty(cam);
            EditorUtility.SetDirty(rig);

            EditorSceneManager.MarkSceneDirty(sc);
            EditorSceneManager.SaveScene(sc);
            Debug.Log($"[SceneBuilds] {dst}: SRD off, FlatCamera + FlatDisplayRig (virtual SR2 panel, hands mapped onto it).");
        }

        // Object references into `src` (e.g. the source scene's Brain) -> the object at the same hierarchy path
        // (same component type and index) in `dst`. Anything without a counterpart is cleared, with a warning.
        static void RemapToScene(Object target, Scene src, Scene dst)
        {
            var so = new SerializedObject(target);
            var p = so.GetIterator();
            bool changed = false;
            while (p.Next(true))
            {
                if (p.propertyType != SerializedPropertyType.ObjectReference || p.objectReferenceValue == null) continue;
                var o = p.objectReferenceValue;
                GameObject g = o is GameObject go ? go : o is Component c ? c.gameObject : null;
                if (g == null || g.scene != src) continue;
                Object to = Counterpart(o, g, dst);
                if (to == null) Debug.LogWarning($"[SceneBuilds] {target.name}.{target.GetType().Name}.{p.propertyPath}: no '{g.name}' in {dst.name}, cleared.");
                p.objectReferenceValue = to;
                changed = true;
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }

        static Object Counterpart(Object o, GameObject g, Scene dst)
        {
            string path = g.name;
            for (var t = g.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            string[] names = path.Split('/');
            GameObject found = null;
            foreach (var r in dst.GetRootGameObjects()) if (r.name == names[0]) { found = r; break; }
            for (int i = 1; i < names.Length && found != null; i++)
            {
                var child = found.transform.Find(names[i]);
                found = child != null ? child.gameObject : null;
            }
            if (found == null || o is GameObject) return found;
            var type = o.GetType();
            var all = g.GetComponents(type);
            var mine = found.GetComponents(type);
            int idx = System.Array.IndexOf(all, o);
            return idx >= 0 && idx < mine.Length ? mine[idx] : null;
        }

        // Shader.Find only finds shaders in a build that something references (or that are always included).
        static void AlwaysInclude(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) { Debug.LogError("[SceneBuilds] Shader not found: " + shaderName); return; }
            var gs = new SerializedObject(AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset"));
            var list = gs.FindProperty("m_AlwaysIncludedShaders");
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            gs.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuilds] Always Included Shaders += " + shaderName);
        }

        static void Build(string sceneName)
        {
            string scene = $"Assets/Scenes/{sceneName}.unity";
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = $"Builds/{sceneName}/{sceneName}.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"[SceneBuilds] {sceneName}: {s.result}, {s.totalErrors} errors, {s.totalSize / (1024 * 1024):N0} MB, " +
                      $"{s.totalTime.TotalSeconds:F0} s -> {options.locationPathName}");
            if (Application.isBatchMode && s.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }

        /// <summary>Batch-mode helper: turn the V1 timeline's loop on and save the scene.</summary>
        public static void SetV1Loop()
        {
            var sc = EditorSceneManager.OpenScene("Assets/Scenes/Nissl and Labels V1.unity", OpenSceneMode.Single);
            var seq = Object.FindFirstObjectByType<NeuronalLossSequence>();
            var so = new SerializedObject(seq);
            so.FindProperty("loop").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.SaveScene(sc);
            Debug.Log($"[SceneBuilds] V1 loop={seq.loop}, finalTurnSeconds={seq.finalTurnSeconds}, total={seq.showBrain + seq.rotate + seq.split + seq.slice + seq.sliceBackDelay + seq.sliceBackSeconds + seq.finalTurnSeconds + seq.hold} s");
        }
    }
}
