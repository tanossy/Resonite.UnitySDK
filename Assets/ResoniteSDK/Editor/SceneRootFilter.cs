using UnityEditor;
using UnityEngine;

// 2026-08-08 (Tanossy指摘「インポート時にUnityのカメラ削除・VRChat設定など余計なものを除外して」/
// 「できる限りオリジナルソースはいじりたくないので外だしして」): SceneConverter.ConvertScene()の
// シーンルート走査に対するフィルタ。公式SDK側(SceneConverter.cs)の変更は
// `.Where(g => !SceneRootFilter.ShouldExclude(g))` の1行だけで済むよう、判定ロジックを
// 完全にこの新規ファイルへ外だししている。
public static class SceneRootFilter
{
    // Resoniteは独自のビュー/カメラ系を持つためUnity側のCameraは不要、VRChat由来の壊れた
    // Prefab参照(このワールドでは"VRCWorld (Missing Prefab with guid: ...)"として毎回警告に
    // 出ていた)もResoniteには存在しないコンポーネントで変換のしようがない。シーンルート単位で
    // 対象外にする(子オブジェクトとして紛れ込んだ場合は現状未対応 - このワールドではどちらも
    // シーン直下のルートオブジェクトだったため、まずはルート単位のフィルタで対応)。
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
