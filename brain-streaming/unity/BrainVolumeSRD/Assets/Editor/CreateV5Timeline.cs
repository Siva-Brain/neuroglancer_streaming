using BrainVolume;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Presentation > Create V5 Timeline: puts V5 into the open scene as GameObjects, like V1-V4: a root "V5"
/// (after "V4") holding "Timeline V5": a NeuronalLossSequence with version v5CopyFrom's settings (its start pose,
/// timings, brain) plus V5's (PresentationMenu.ConfigureV5: left sagittal, slice to the IIP slide's section, no
/// block), an IipSlidePanel (the square that comes out of the cut face), and a "V5" entry in the menu bar that returns to
/// the start pose and loops, like V1-V4. Then PresentationMenu no longer builds V5 at play time. If the scene already has Timeline V5 (e.g.
/// under V2), it is moved into the "V5" root. Save the scene afterwards.
/// </summary>
public static class CreateV5Timeline
{
    [MenuItem("Tools/Presentation/Create V5 Timeline")]
    static void Create()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("[V5] Stop Play first: this edits the scene."); return; }
        var menu = Object.FindFirstObjectByType<PresentationMenu>();
        if (menu == null) { Debug.LogWarning("[V5] No PresentationMenu in the open scene."); return; }
        foreach (var v in menu.versions)
            if (v.label == menu.v5Label && v.timeline != null)
            {
                var existing = v.timeline.gameObject;
                var r = Root(existing.scene);
                if (existing.transform.parent != r)
                {
                    Undo.SetTransformParent(existing.transform, r, "Move V5 Timeline");
                    existing.transform.SetAsFirstSibling();
                    EditorSceneManager.MarkSceneDirty(existing.scene);
                    Debug.Log($"[V5] Moved '{existing.name}' into '{r.name}'. Save the scene.");
                }
                else Debug.Log($"[V5] The scene already has V5: '{existing.name}' in '{r.name}'.");
                Selection.activeObject = existing;
                return;
            }
        int from = menu.v5CopyFrom;
        if (from < 0 || from >= menu.versions.Length || menu.versions[from].timeline == null)
        { Debug.LogWarning($"[V5] Version {from + 1} has no timeline to copy."); return; }
        var src = menu.versions[from].timeline;

        var root = Root(src.gameObject.scene);
        var go = new GameObject("Timeline V5");
        Undo.RegisterCreatedObjectUndo(go, "Create V5 Timeline");
        go.transform.SetParent(root, false);
        var t = Undo.AddComponent<NeuronalLossSequence>(go);
        EditorUtility.CopySerialized(src, t);
        PresentationMenu.ConfigureV5(t, menu.v5Section);
        var panel = Undo.AddComponent<IipSlidePanel>(go);
        panel.timeline = t;
        EditorUtility.SetDirty(t);
        EditorUtility.SetDirty(panel);

        Undo.RecordObject(menu, "Create V5 Timeline");
        menu.AddVersion(new PresentationMenu.Version { label = menu.v5Label, caption = menu.v5Caption, timeline = t, holdAtEnd = false });
        EditorUtility.SetDirty(menu);
        EditorSceneManager.MarkSceneDirty(go.scene);
        Selection.activeObject = go;
        Debug.Log($"[V5] Created '{root.name}/{go.name}' from '{src.name}' and added it to the menu. Save the scene.");
    }

    // where the square ends up (world), measured in Play 2026-10-05
    static readonly Vector3 PanelEndPosition = new Vector3(-0.038f, 0.5888364f, 0.02064002f);
    static readonly Quaternion PanelEndRotation = new Quaternion(0.006258397f, 0.9270847f, 0.3746562f, -0.01037030f);

    /// <summary>Tools > Presentation > Set V5 Panel End: "V5 Panel End" under Timeline V5 (made if missing) at the
    /// measured world pose, scale 1. IipSlidePanel then uses it instead of its default end place.</summary>
    [MenuItem("Tools/Presentation/Set V5 Panel End")]
    static void SetPanelEnd()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("[V5] Stop Play first: this edits the scene."); return; }
        var panel = Object.FindFirstObjectByType<IipSlidePanel>();
        if (panel == null) { Debug.LogWarning("[V5] No IipSlidePanel (Timeline V5) in the open scene: run Create V5 Timeline first."); return; }
        var e = GameObject.Find("V5 Panel End");
        if (e == null)
        {
            e = new GameObject("V5 Panel End");
            Undo.RegisterCreatedObjectUndo(e, "Set V5 Panel End");
            e.transform.SetParent(panel.transform, false);
        }
        else Undo.RecordObject(e.transform, "Set V5 Panel End");
        e.transform.SetPositionAndRotation(PanelEndPosition, PanelEndRotation.normalized);
        e.transform.localScale = Vector3.one;
        EditorSceneManager.MarkSceneDirty(e.scene);
        Selection.activeObject = e;
        Debug.Log($"[V5] 'V5 Panel End' placed at {PanelEndPosition}. Save the scene.");
    }

    // the scene's root "V5" (like V1-V4), made after "V4" if it has none
    static Transform Root(UnityEngine.SceneManagement.Scene scene)
    {
        GameObject v4 = null;
        foreach (var g in scene.GetRootGameObjects())
        {
            if (g.name == "V5") return g.transform;
            if (g.name == "V4") v4 = g;
        }
        var root = new GameObject("V5");
        Undo.RegisterCreatedObjectUndo(root, "Create V5");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        if (v4 != null) root.transform.SetSiblingIndex(v4.transform.GetSiblingIndex() + 1);
        return root.transform;
    }
}
