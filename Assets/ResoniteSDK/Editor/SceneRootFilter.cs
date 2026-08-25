using System.Linq;
using UnityEditor;
using UnityEngine;

// 2026-08-08 (per Tanossy's feedback: "exclude junk like Unity's camera and VRChat settings from
// the import" / "keep the original source untouched as much as possible, so pull this out"):
// filter applied to the scene-root traversal in SceneConverter.ConvertScene(). The change on the
// official SDK side (SceneConverter.cs) is kept to a single line -
// `.Where(g => !SceneRootFilter.ShouldExclude(g))` - by fully externalizing the exclusion logic
// into this new file.
//
// 2026-08-27 (per Tanossy's feedback: "VRCWorld/Easy Mirror leftover hierarchies from VRChat are
// still showing up in the sent scene" / decided to keep excluding on the SDK side rather than
// touch the source Unity scene): investigated both roots directly in the scene file
// (`Assets/One Bedroom Room Tutorial/Scenes/room tutorial.unity`) and their source prefab
// (`Assets/One Bedroom Room Tutorial/VRS_easy_mirror/Easy Mirror.prefab`) rather than guessing.
// Findings, in order of what was actually checked:
//   - "VRCWorld" is NOT a broken prefab instance (m_PrefabInstance/m_PrefabAsset are both
//     fileID 0 in the scene YAML) - it's a plain GameObject, so the existing MissingAsset check
//     above correctly does not (and structurally cannot) catch it.
//   - "VRCWorld" does carry two MonoBehaviour components whose m_Script guid
//     (661092b4961be7145bfbe56e1e62337b, and separately 4ecd63eff847044b68db9453ce219299) has no
//     matching .meta file anywhere under this Unity project - i.e. the VRChat SDK assembly these
//     scripts belong to (VRC_SceneDescriptor / PipelineManager) isn't installed here at all, so
//     Unity resolves them as "Missing Script" and these component slots read back as null.
//   - "Easy Mirror" IS a normal, resolvable PrefabInstance (valid m_SourcePrefab guid
//     848039dc964190945b7e6051d068d337) - so it is *not* caught by the MissingAsset check either,
//     and its root GameObject itself carries only a Transform (nothing suspicious there). The
//     giveaway is on a *child* of the prefab ("UI"), which has a third MonoBehaviour pointing at
//     the exact same unresolvable guid (661092b4961be7145bfbe56e1e62337b) seen on VRCWorld - i.e.
//     the same absent VRChat SDK assembly, just reached from a nested VRC_MirrorReflection-style
//     component instead of directly on the root.
// Both cases therefore reduce to the same underlying, non-VRChat-specific structural fact:
// "this hierarchy contains at least one MonoBehaviour whose script can't be resolved", which
// Unity itself always surfaces as a null entry in GetComponentsInChildren<Component>(true) (the
// standard editor-tooling idiom for finding "missing script" objects - e.g. Unity's own
// "select prefabs with missing scripts" utilities use exactly this check). Using this instead of
// a name check means it generalizes to *any* unresolvable-script junk (VRChat-derived or not),
// and unlike a root-only null check it also has to walk the hierarchy, since Easy Mirror's
// missing script sits on a descendant rather than the root itself.
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

        // 2026-08-27: catches "VRCWorld" (missing script directly on the root - not a broken
        // prefab instance, so the check above doesn't apply) and "Easy Mirror" (a resolvable,
        // non-broken prefab instance whose "UI" child nonetheless carries an unresolvable VRChat
        // SDK script). Walking the whole hierarchy (not just the root) is required for the latter
        // case. See the file-level comment above for how both were confirmed structurally rather
        // than by name before writing this check.
        if (HasMissingScriptInHierarchy(root))
            return true;

        return false;
    }

    // True if `root` or any of its descendants (active or inactive) carries a MonoBehaviour whose
    // backing script Unity could not resolve. Unity represents such a component slot as a literal
    // null entry in GetComponentsInChildren<Component>(true) - this is the standard idiom editor
    // tooling uses to find "Missing Script" objects, and unlike PrefabInstanceStatus it also
    // covers scripts that are missing on an otherwise perfectly normal (non-prefab, or
    // non-broken-prefab) GameObject.
    private static bool HasMissingScriptInHierarchy(GameObject root)
    {
        return root.GetComponentsInChildren<Component>(true).Any(c => c == null);
    }
}
