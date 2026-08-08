using UnityEditor;
using UnityEngine;

// 2026-08-08 (per Tanossy's feedback: "exclude junk like Unity's camera and VRChat settings from
// the import" / "keep the original source untouched as much as possible, so pull this out"):
// filter applied to the scene-root traversal in SceneConverter.ConvertScene(). The change on the
// official SDK side (SceneConverter.cs) is kept to a single line -
// `.Where(g => !SceneRootFilter.ShouldExclude(g))` - by fully externalizing the exclusion logic
// into this new file.
public static class SceneRootFilter
{
    // Resonite has its own view/camera system, so Unity's Camera component has no use there;
    // VRChat-derived broken prefab references (seen in this world as a "VRCWorld (Missing Prefab
    // with guid: ...)" warning on every run) also have no corresponding component in Resonite and
    // can't be converted at all. These are excluded at the scene-root level (children that carry
    // this issue are not currently handled - in this world, both problem cases happened to be
    // top-level scene root objects, so a root-level filter was sufficient for now).
    public static bool ShouldExclude(GameObject root)
    {
        // Unity's own Camera - Resonite has no use for it and it has no Resonite-side
        // equivalent component anyway.
        if (root.GetComponent<UnityEngine.Camera>() != null)
            return true;

        // Any prefab instance whose source asset is missing (e.g. VRChat SDK components like
        // VRCWorld/VRC_SceneDescriptor when the VRC SDK isn't installed in this project) can't
        // meaningfully be converted - PrefabUtility can't even tell us what components it was
        // supposed to have. Detected generically via PrefabInstanceStatus.MissingAsset rather
        // than by name, so it also catches any other missing-prefab junk beyond the specific
        // VRCWorld case seen in this scene (confirmed via live query: this scene's "VRCWorld
        // (Missing Prefab with guid: ...)" root reports GetPrefabInstanceStatus == MissingAsset).
        if (PrefabUtility.GetPrefabInstanceStatus(root) == PrefabInstanceStatus.MissingAsset)
            return true;

        return false;
    }
}
