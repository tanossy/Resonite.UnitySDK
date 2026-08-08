using UnityEditor;
using UnityEngine;

// 2026-08-08 (Tanossy指摘「汚してしまったSDKのパネルを戻したい・独自パネルにすべて機能はおいて」):
// ResoniteLinkWindow（公式然としたメインパネル）から、部分送信テスト・クリーンアップ・状態リセット
// 等のデバッグ専用機能をこちらへ切り出した。ResoniteLinkWindowは接続・Send Current Scene・
// Realtime Modeという「常時使う導線」だけを残し、シンプルな見た目に戻している。
//
// ここに移した機能はいずれもResoniteLinkWindow側に既にpublicメソッドとして実装済みのものを
// 呼び出すだけで、ロジックの複製は一切していない（呼び出し先を直接参照するため、
// LightmapTestHarness.csのようなリフレクション経由の間接呼び出しは不要）。
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

    // LightmapTestHarness.PickResoniteLinkWindow()と同じ理由(複数インスタンスが
    // FindObjectsOfTypeAllで返りうる)で、接続済みのものを優先して選ぶ。1つも接続済みが
    // 無ければ最初に見つかったものを返す(Disconnected状態のガイダンス表示用)。
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
