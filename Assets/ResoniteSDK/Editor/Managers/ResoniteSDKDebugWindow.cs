using UnityEditor;
using UnityEngine;

// 2026-08-08 (per Tanossy's feedback: "I want to restore the SDK panel I'd messed up - keep all
// the custom functionality in its own panel"): Debug-only features such as partial-send testing,
// cleanup, and state reset have been split out of ResoniteLinkWindow (the official-looking main
// panel) into this window. ResoniteLinkWindow has been restored to its simple appearance, keeping
// only the "always-used controls" - Connect, Send Current Scene, and Realtime Mode.
//
// Everything moved here just calls the public methods already implemented on the
// ResoniteLinkWindow side; none of the logic is duplicated (since we reference the target methods
// directly, there's no need for indirect calls via reflection like in LightmapTestHarness.cs).
public class ResoniteSDKDebugWindow : EditorWindow
{
    [MenuItem("Resonite SDK/Open Debug Tools")]
    public static void ShowWindow()
    {
        var window = GetWindow<ResoniteSDKDebugWindow>();
        window.titleContent = new GUIContent("Resonite SDK Debug Tools");
    }

    void OnGUI()
    {
        var window = PickResoniteLinkWindow();

        if (window == null)
        {
            GUILayout.Label("Resonite SDK Managerウィンドウが開かれていません。\n" +
                "先に「Resonite SDK > Open Resonite SDK Manager」を開いて接続してください。");

            if (GUILayout.Button("Open Resonite SDK Manager"))
                ResoniteLinkWindow.ShowWindow();

            return;
        }

        EditorGUILayout.LabelField("接続状態: ", window.State.ToString());

        GUI.enabled = window.State == ResoniteLinkWindow.ConnectionState.Connected;

        GUILayout.Label("部分送信（一部だけテスト送信したい時に使用）:");

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Send Meshes Only"))
            window.SendMeshesOnly();

        if (GUILayout.Button("Send Materials Only"))
            window.SendMaterialsOnly();
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Send Lightmaps Only"))
            window.SendLightmapsOnly();

        if (GUILayout.Button("Retry Missing Asset URLs"))
            window.RetryMissingAssetURLs();

        GUILayout.Space(16);
        GUILayout.Label("クリーンアップ / 状態リセット:");

        window.LogMessageJSON = GUILayout.Toggle(window.LogMessageJSON, "Log Messages JSON");

        if (GUILayout.Button("Cleanup converters in the scene"))
            window.CleanupConverters();

        if (GUILayout.Button("Cleanup Resonite Components in the scene"))
            window.CleanupReosniteComponents();

        if (GUILayout.Button("Reset conversion state"))
            window.ResetConversionState();

        GUI.enabled = true;
    }

    // For the same reason as LightmapTestHarness.PickResoniteLinkWindow() (multiple instances can
    // be returned by FindObjectsOfTypeAll), prefer a connected one. If none are connected, return
    // the first one found (used to show guidance for the Disconnected state).
    static ResoniteLinkWindow PickResoniteLinkWindow()
    {
        var windows = Resources.FindObjectsOfTypeAll<ResoniteLinkWindow>();

        ResoniteLinkWindow fallback = null;

        foreach (var w in windows)
        {
            if (w == null)
                continue;

            if (fallback == null)
                fallback = w;

            if (w.State == ResoniteLinkWindow.ConnectionState.Connected)
                return w;
        }

        return fallback;
    }
}
