// LightmapTestHarness.cs
//
// ライトマップベイク・パイプラインの実処理。LightmapPipelineWindow.csから呼ばれるほか、
// ファイル経由（下記コマンド）でも外部から駆動できる。
//
// アテナ（外部プロセス）がファイル経由でこの Editor を駆動できるようにする:
//   コマンド:   <ProjectRoot>/Temp/lightmap_harness_cmd.txt
//                 "build_scene"
//                 "bake [texelsPerUnit] [giSamples] [current]"  (Bakery専用・単発)
//                 "report"
//                 "convert"
//                 "pipeline <low|mid|high> [bakery|unity] [normal] [current]"
//                                                                (準備->ベイク->完了イベントで自動convert。
//                                                                 quality省略時mid、baker省略時bakery)
//
//               "current" トークン（bake / bake_unity / pipeline、順不同でどこに置いてもよい）:
//                 指定すると EnsureTestSceneOpen() を呼ばず、現在Unityで開いているシーンを
//                 そのままベイク対象にする（シーン切替なし）。省略時は従来どおり
//                 Assets/LightmapTest.unity を強制的に開く（テスト部屋固定・後方互換）。
//                 例: "bake_unity high current"
//   結果ログ:   <ProjectRoot>/Temp/lightmap_harness_result.txt (追記、[HH:mm:ss] タイムスタンプ付き)
//
// 併設ファイル（同じく Assets/Editor 配下、SDK非改変）:
//   - BakeryTempObjectSuppression.cs : "!ftraceLightmaps"(ftLightmapsStorage) をSDKの正規
//     ConversionSupressionHandler機構でシーン変換から除外する追加コンバータ。
//   - LightmapPipelineWindow.cs      : 本ファイルの static メソッドを呼ぶだけの薄いGUI層
//     （メニュー "Resonite SDK/Lightmap Pipeline"）。ロジックは全て本ファイル側に集約。
//
// 参照 API の実在確認元（推測禁止・すべて実ソースを読んで確認済み）:
//   - Bakery: Assets/Editor/x64/Bakery/scripts/ftRenderLightmap.cs
//       public static ftRenderLightmap instance;
//       public static void RenderLightmap()                         (メニューエントリ。instance を生成/表示)
//       public void RenderButton(bool showMsgWindows = true)        (実ベイク開始。showMsgWindows=false でダイアログ抑制)
//       public static event System.EventHandler OnFinishedFullRender (ベイク完了通知)
//       public static bool bakeInProgress
//       public static RenderDirMode renderDirMode  { None, BakedNormalMaps, DominantDirection, RNM, SH, MonoSH }
//       public float texelsPerUnit = 20;  public int giSamples = 16;
//       public RenderMode userRenderMode = RenderMode.FullLighting;
//     asmdef: Assets/Editor/x64/Bakery/scripts/BakeryEditorAssembly.asmdef
//       ("includePlatforms": ["Editor"], "autoReferenced": true)
//       → autoReferenced=true のため、既定アセンブリ(Assembly-CSharp-Editor)からは
//         参照追加なしで ftRenderLightmap を直接呼べる。本ファイルにも同様の enclosing
//         asmdef が存在しない(Assets/Editor直下はBakeryのx64/Bakery/scripts配下ではない)ため
//         既定アセンブリでコンパイルされ、同じ理屈で到達可能。
//
//   - Resonite.UnitySDK: Assets/ResoniteSDK/Editor/Managers/ResoniteLinkWindow.cs
//                        Assets/ResoniteSDK/Editor/SceneConverter.cs
//       public class ResoniteLinkWindow : EditorWindow
//         public ConnectionState State { Disconnected, Connecting, Initializing, Connected }
//         public string UniqueSessionId; public LinkInterface Link;
//         private void SendCurrentScene() → EnsureConverter(); _converter.ConvertScene();
//         private void ConnectPressed()   → Manual/AutoDiscovery 問わず Port フィールドと
//                                            ws://localhost:{Port} で接続を試みる
//       ResoniteSDK 配下に asmdef が存在しないため、これも既定アセンブリでコンパイルされ、
//       本ハーネスと同一アセンブリ内 → 型を直接参照可能（SendCurrentScene 等の private
//       メンバのみリフレクション経由で呼び出す）。
//
// --- BAKERY_INCLUDED 条件コンパイル（Bakery非導入環境でも本ファイルがコンパイルできるように）---
//
// Bakery型（ftRenderLightmap / BakerySkyLight / BakeryDirectLight / BakeryPointLight /
// ftLightmaps / ftGlobalStorage）を直接参照する箇所はすべて `#if BAKERY_INCLUDED` で
// ガードしてある。このシンボルは Assets/Editor/BakeryPresenceDefine.cs
// （[InitializeOnLoad]、Bakery型を一切直接参照せず AppDomain 走査のみで存在検出）が
// エディタ起動時/スクリプトリロード時に自動で立てる/降ろす — 手動設定は不要。
//
// ガード方針（呼び出し元を変更せずに済むよう、公開シグネチャは常に維持する）:
//   - LightmapPipelineWindow.cs から呼ばれる public メソッド（StartBake /
//     ConvertUnityLightsToBakeryLights）は「シグネチャそのまま・本体だけ #if/#else」。
//     #else 側は Bakery型に一切触れず AppendResult でユーザーに導入を促して安全に return。
//   - Bakery専用の内部ヘルパ（ForceRTXMode / LogActualFtraceExe）はメソッドごと丸ごと
//     #if で囲む（呼び出し元も同じ #if ブロック内にしか存在しないため #else 不要）。
//   - BuildLights() は Unity標準のSun/Cornerライト生成をガード外に残し、
//     BakerySkyLight生成 + ConvertUnityLightsToBakeryLights() 呼び出しだけをガードする
//     （Unity Standard経路の生成物に影響を与えないため）。
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Which lightmapper backend a bake/pipeline run should use. Bakery is the original,
// fully-verified path; UnityStandard drives Unity's own built-in Progressive Lightmapper
// (see StartBakeUnity() for the exact, ildasm-confirmed API surface) and needs none of
// Bakery's light-conversion/RTX-force/RefreshFull machinery.
public enum LightBaker
{
    Bakery,
    UnityStandard,
}

// One resolved quality setting for both backends at once, since a "pipeline"/window run
// only ever drives ONE backend per invocation but the caller (window UI, quality preset
// lookup) shouldn't have to know which fields matter for which backend.
public struct BakeQuality
{
    public float BakeryTexelsPerUnit;
    public int BakeryGiSamples;
    public float UnityLightmapResolution;
    public int UnityDirectSampleCount;
    public int UnityIndirectSampleCount;
}

public static class LightmapTestHarness
{
    // Quality presets shared by the "pipeline low|mid|high [bakery|unity]" command and the
    // LightmapPipelineWindow preset dropdown. "low"/"mid" Bakery values intentionally match
    // this harness's pre-existing "bake" defaults (8/8) and StartBake()'s documented
    // defaults (20/16) exactly, so switching to the pipeline/window doesn't change behavior
    // for anyone already using the bare "bake" command at those values.
    public static readonly Dictionary<string, BakeQuality> QualityPresets = new Dictionary<string, BakeQuality>(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = new BakeQuality
        {
            BakeryTexelsPerUnit = 8f, BakeryGiSamples = 8,
            UnityLightmapResolution = 10f, UnityDirectSampleCount = 16, UnityIndirectSampleCount = 64,
        },
        ["mid"] = new BakeQuality
        {
            BakeryTexelsPerUnit = 20f, BakeryGiSamples = 16,
            UnityLightmapResolution = 20f, UnityDirectSampleCount = 32, UnityIndirectSampleCount = 128,
        },
        ["high"] = new BakeQuality
        {
            BakeryTexelsPerUnit = 40f, BakeryGiSamples = 64,
            UnityLightmapResolution = 40f, UnityDirectSampleCount = 64, UnityIndirectSampleCount = 256,
        },
    };

    const string SceneAssetPath = "Assets/LightmapTest.unity";

    // Brick Project Studio アセット（実在確認済み: guid付きprefab/materialを直接パス指定）
    const string BpsFloorPrefabPath = "Assets/Brick Project Studio/_BPS Basic Assets/_Prefabs/Basic Asset/BA Build Kit/Interior/Int_01/Int_BA_01_Floor_01.prefab";
    const string BpsWallPrefabPath = "Assets/Brick Project Studio/_BPS Basic Assets/_Prefabs/Basic Asset/BA Build Kit/Interior/Int_01/Int_BA_01_Wall_01.prefab";
    const string BpsMaterialPath = "Assets/Brick Project Studio/_BPS Basic Assets/Common/Materials/Basic Assets/Int_BS_01.mat";
    // Int_BA_01_Floor_01 / Int_BA_01_Wall_01 が参照するメッシュの出所FBX（Lightmap UV有効化対象）
    const string BpsFloorFbxPath = "Assets/Brick Project Studio/_BPS Basic Assets/Common/Models/Build Kits/Build Kit Interior - 01.fbx";
    const string BpsWallFbxPath = "Assets/Brick Project Studio/_BPS Basic Assets/Common/Models/Build Kits/Build Kit Interior Updated_01.fbx";

    const string PlainBoxMaterialPath = "Assets/LightmapTest_PlainBoxMaterial.mat";

    const float RoomSize = 10f;
    const float TileSize = 2.5f;
    const float WallHeight = 4f;

    const double PollIntervalSeconds = 1.0;
    static double _lastPollTime;
    static bool _bakeEventsSubscribed; // OK as a plain field: re-evaluated (and cheaply re-subscribed if false) on every OnUpdate() poll after a domain reload anyway, and C# event subscriber lists themselves don't survive a domain reload regardless of what this flag says.

    // ------------------------------------------------------------------
    // Session-persisted bake/pipeline state. A Unity domain reload (script recompile,
    // entering/exiting Play mode, etc.) wipes every plain static field AND every C# event
    // subscriber list. If a bake happened to be in flight across a reload, plain static
    // flags like "_bakeStartedByHarness" would silently reset to false/default, so when
    // OnBakeFinished/OnUnityBakeFinished eventually fired (after SubscribeBakeEvents()
    // re-subscribes in Init()/OnUpdate()) they'd see "not started by us" and drop the
    // Report()/pipeline-convert chain on the floor — the bake finishes for real, but the
    // harness never notices.
    //
    // UnityEditor.SessionState (SetBool/GetBool/SetString/GetString) persists across domain
    // reloads for the lifetime of the editor process and clears on editor restart, which is
    // exactly the semantics needed here (a bake genuinely can't survive killing the editor
    // anyway). Wrapped as properties so every existing call site keeps reading/writing
    // "_bakeStartedByHarness" etc. exactly as before.
    // ------------------------------------------------------------------

    const string SessionKeyBakeStarted = "LightmapTestHarness.BakeStartedByHarness";
    const string SessionKeyUnityBakeStarted = "LightmapTestHarness.UnityBakeStartedByHarness";
    const string SessionKeyPipelineChainConvert = "LightmapTestHarness.PipelineChainConvert";
    const string SessionKeyLastBaker = "LightmapTestHarness.LastBaker";
    const string SessionKeyLastUsedCurrentScene = "LightmapTestHarness.LastUsedCurrentScene";

    static bool _bakeStartedByHarness
    {
        get => SessionState.GetBool(SessionKeyBakeStarted, false);
        set => SessionState.SetBool(SessionKeyBakeStarted, value);
    }

    static bool _unityBakeStartedByHarness
    {
        get => SessionState.GetBool(SessionKeyUnityBakeStarted, false);
        set => SessionState.SetBool(SessionKeyUnityBakeStarted, value);
    }

    // Which backend produced the most recent bake. Report()/Convert() both need to know
    // this: EnsureBakeryLightmapsLoaded() (ftLightmaps.RefreshFull()) is a Bakery-only
    // recovery step and must be skipped after a Unity-standard bake (LightmapSettings is
    // populated directly by Unity's own Lightmapping system in that case; an empty
    // LightmapSettings.lightmaps there legitimately means "no lightmaps", not "stale").
    // Defaults to Bakery so standalone "report"/"bake"/"convert" commands run with no prior
    // 'pipeline'/window bake in this session keep their already-verified behavior exactly.
    static LightBaker _lastBaker
    {
        get => Enum.TryParse<LightBaker>(SessionState.GetString(SessionKeyLastBaker, LightBaker.Bakery.ToString()), out var baker) ? baker : LightBaker.Bakery;
        set => SessionState.SetString(SessionKeyLastBaker, value.ToString());
    }

    // Which scene-selection mode the most recently STARTED bake used (set at the same moment
    // as _lastBaker, in StartBake()/StartBakeUnity()). Report() reads this instead of
    // unconditionally force-opening the test room - see its own comment for why: a bake that
    // targeted "Current Open Scene" must not have its post-bake report force-switch the
    // active scene away before the pipeline's chained Convert()/send step runs against it.
    // Defaults to false (test room) so a totally fresh session's standalone "report" command
    // keeps its original, already-verified behavior.
    static bool _lastUsedCurrentScene
    {
        get => SessionState.GetBool(SessionKeyLastUsedCurrentScene, false);
        set => SessionState.SetBool(SessionKeyLastUsedCurrentScene, value);
    }

    // Set only at the moment a 'pipeline' run (or the window's "Bake & Send" button) starts
    // a bake; consumed and cleared exactly once by whichever bake-finished handler
    // (OnBakeFinished for Bakery, OnUnityBakeFinished for Unity standard) fires next. A bare
    // "bake" command or a manual in-editor bake never sets this, so it never triggers an
    // unwanted auto-convert.
    static bool _pipelineChainConvert
    {
        get => SessionState.GetBool(SessionKeyPipelineChainConvert, false);
        set => SessionState.SetBool(SessionKeyPipelineChainConvert, value);
    }

    static string ProjectDir => Directory.GetParent(Application.dataPath).FullName;
    static string TempDir => Path.Combine(ProjectDir, "Temp");
    static string CommandFilePath => Path.Combine(TempDir, "lightmap_harness_cmd.txt");
    static string ResultFilePath => Path.Combine(TempDir, "lightmap_harness_result.txt");

    [InitializeOnLoadMethod]
    static void Init()
    {
        EditorApplication.update -= OnUpdate;
        EditorApplication.update += OnUpdate;
        SubscribeBakeEvents();
        AppendResult("LightmapTestHarness loaded. Watching: " + CommandFilePath);
    }

    static void SubscribeBakeEvents()
    {
        if (_bakeEventsSubscribed) return;

        // Two independent try/catch blocks (not one shared try wrapping both
        // subscriptions): if Bakery's event throws (e.g. a future Bakery version renames
        // OnFinishedFullRender), that must not also prevent the Unity-standard
        // bakeCompleted subscription from going through, and vice versa. Each backend's
        // pipeline is otherwise fully independent, so a failure in one should never take
        // the other down with it.
        bool bakeryOk = false;
#if BAKERY_INCLUDED
        try
        {
            ftRenderLightmap.OnFinishedFullRender -= OnBakeFinished;
            ftRenderLightmap.OnFinishedFullRender += OnBakeFinished;
            bakeryOk = true;
        }
        catch (Exception ex)
        {
            AppendResult("Failed to subscribe to ftRenderLightmap.OnFinishedFullRender:\n" + ex);
        }
#else
        // Bakery not installed (BAKERY_INCLUDED not defined) — there is nothing to
        // subscribe to on this side. Treat it as trivially "ok" so _bakeEventsSubscribed
        // can still become true once the Unity-standard subscription below succeeds; this
        // half is simply never exercised (StartBake()'s #else stub never lets a Bakery bake
        // start, so OnBakeFinished would never fire even if it were subscribed).
        bakeryOk = true;
#endif

        // Lightmapping.bakeCompleted: confirmed via ildasm of the installed
        // UnityEditor.CoreModule.dll (2022.3.22f1) — static event of type System.Action
        // (add_bakeCompleted(System.Action)/remove_bakeCompleted(...)), i.e. a plain
        // no-argument delegate, not EventHandler. See StartBakeUnity()'s header comment
        // for the full confirmed API list.
        bool unityOk = false;
        try
        {
            Lightmapping.bakeCompleted -= OnUnityBakeFinished;
            Lightmapping.bakeCompleted += OnUnityBakeFinished;
            unityOk = true;
        }
        catch (Exception ex)
        {
            AppendResult("Failed to subscribe to Lightmapping.bakeCompleted:\n" + ex);
        }

        // Only mark fully subscribed once both succeeded, so a transient failure in either
        // one gets retried on the next OnUpdate() poll instead of being silently accepted
        // as "done" forever (re-running -=/+= on the side that already succeeded is a
        // harmless no-op).
        _bakeEventsSubscribed = bakeryOk && unityOk;
    }

    static void OnUpdate()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now - _lastPollTime < PollIntervalSeconds)
            return;
        _lastPollTime = now;

        SubscribeBakeEvents();

        try
        {
            if (!File.Exists(CommandFilePath))
                return;

            string command;
            try
            {
                command = File.ReadAllText(CommandFilePath).Trim();
            }
            catch (IOException)
            {
                // Still being written by the external process; retry on next poll.
                return;
            }

            // Delete immediately after a successful read to prevent double execution.
            try { File.Delete(CommandFilePath); }
            catch (Exception delEx) { AppendResult("Warning: could not delete command file after reading: " + delEx.Message); }

            if (string.IsNullOrEmpty(command))
                return;

            DispatchCommand(command);
        }
        catch (Exception ex)
        {
            AppendResult("Harness poll error:\n" + ex);
        }
    }

    static void DispatchCommand(string command)
    {
        AppendResult($"Command received: {command}");
        try
        {
            // "bake" accepts optional quality args: "bake <texelsPerUnit> <giSamples>"
            // (e.g. "bake 32 32" for a quality bake; bare "bake" keeps the fast test defaults).
            var parts = command.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "build_scene": BuildScene(); break;
                case "bake":
                    // Double-bake guard (command-file entry point): a second "bake"/
                    // "pipeline" command arriving while one is already running would
                    // otherwise stomp on ftRenderLightmap.instance's live settings or race
                    // Lightmapping.BakeAsync() — reject it instead of letting it through.
                    if (IsBakeInProgress)
                    {
                        AppendResult($"bake already in progress; command '{command}' ignored");
                        break;
                    }
                    // Optional "current" token anywhere in the args (see
                    // ExtractCurrentSceneFlag) opts into baking whatever scene is CURRENTLY
                    // open instead of force-opening Assets/LightmapTest.unity — same
                    // "Current Open Scene" concept as bake_unity/pipeline below. Supported
                    // here too even though this task's priority is the Unity Standard path,
                    // because the flag detection/plumbing is shared and StartBake() already
                    // takes the same parameter — no Bakery-specific work was needed.
                    bool bakeUseCurrentScene = ExtractCurrentSceneFlag(ref parts);
                    float texels = parts.Length > 1 && float.TryParse(parts[1], out var t) ? t : 8f;
                    int samples = parts.Length > 2 && int.TryParse(parts[2], out var s) ? s : 8;
                    StartBake(texels, samples, bakeUseCurrentScene);
                    break;
                case "bake_unity":
                    // Unity-standard bake ONLY — no Resonite convert afterwards. Lets us verify
                    // the baked shadows in the Unity editor first (RunPipeline auto-converts,
                    // which is pointless until the Unity bake itself is confirmed good).
                    //
                    // Optional trailing "normal" token (e.g. "bake_unity mid normal") opts into
                    // the experimental directional/normal-bake path (Step 1) from the file-driven
                    // harness interface, without requiring the interactive
                    // LightmapPipelineWindow toggle+confirmation-dialog — useful for Athena-driven
                    // real-machine verification. Bare "bake_unity [quality]" is unchanged.
                    //
                    // Optional "current" token (anywhere, e.g. "bake_unity high current" or
                    // "bake_unity high normal current") opts into baking whatever scene is
                    // CURRENTLY open instead of force-opening Assets/LightmapTest.unity — see
                    // ExtractCurrentSceneFlag()/EnsureBakeSceneReady() below.
                    if (IsBakeInProgress)
                    {
                        AppendResult($"bake already in progress; command '{command}' ignored");
                        break;
                    }
                    bool unityUseCurrentScene = ExtractCurrentSceneFlag(ref parts);
                    string uq = parts.Length > 1 ? parts[1] : "mid";
                    if (!QualityPresets.TryGetValue(uq, out var uqp))
                    {
                        AppendResult($"bake_unity: unknown quality '{uq}' (expected low/mid/high)");
                        break;
                    }
                    bool uqNormalDetail = parts.Length > 2 && string.Equals(parts[2], "normal", StringComparison.OrdinalIgnoreCase);
                    StartBakeUnity(uqp.UnityLightmapResolution, uqp.UnityDirectSampleCount, uqp.UnityIndirectSampleCount, uqNormalDetail, unityUseCurrentScene);
                    break;
                case "report": Report(); break;
                case "convert": Convert(); break;
                case "meshes_only": ConvertMeshesOnly(); break;
                case "materials_only": ConvertMaterialsOnly(); break;
                case "lightmaps_only": ConvertLightmapsOnly(); break;
                case "pipeline":
                {
                    if (IsBakeInProgress)
                    {
                        AppendResult($"bake already in progress; command '{command}' ignored");
                        break;
                    }

                    // "pipeline <low|mid|high> [bakery|unity] [normal] [current]" — quality
                    // defaults to "mid", baker defaults to "bakery" (preserves this harness's
                    // original, already-verified Bakery-only behavior for anyone not passing a
                    // second arg). Optional trailing "normal" token (unity baker only) opts into
                    // the experimental directional/normal-bake path (Step 1) — same file-driven
                    // shortcut as "bake_unity"'s trailing "normal" token above. Optional
                    // "current" token (anywhere) opts into the currently-open scene instead of
                    // the test room — extracted FIRST so it never gets misread as a quality/
                    // baker/normal positional arg regardless of where it appears.
                    bool pipelineUseCurrentScene = ExtractCurrentSceneFlag(ref parts);
                    string qualityKey = parts.Length > 1 ? parts[1] : "mid";
                    LightBaker baker = LightBaker.Bakery;
                    if (parts.Length > 2)
                    {
                        if (string.Equals(parts[2], "unity", StringComparison.OrdinalIgnoreCase))
                            baker = LightBaker.UnityStandard;
                        else if (!string.Equals(parts[2], "bakery", StringComparison.OrdinalIgnoreCase))
                            AppendResult($"pipeline: unknown baker '{parts[2]}' (expected bakery/unity); defaulting to bakery.");
                    }
                    bool pipelineNormalDetail = parts.Length > 3 && string.Equals(parts[3], "normal", StringComparison.OrdinalIgnoreCase);
                    if (pipelineNormalDetail && baker != LightBaker.UnityStandard)
                        AppendResult("pipeline: 'normal' flag is only meaningful for the unity baker; ignored for bakery.");
                    RunPipeline(qualityKey, baker, pipelineNormalDetail, pipelineUseCurrentScene);
                    break;
                }
                case "poiyomi_smoketest": PoiyomiSmokeTest(); break;
                case "poiyomi_swap_only":
                    // Manual escape hatch for prepping a REAL, non-test scene (e.g. an imported
                    // VRChat world) for a bake driven entirely outside this harness (Unity's own
                    // Window > Rendering > Lighting panel). StartBake()/StartBakeUnity() CAN
                    // now reach a real scene too (pass useCurrentSceneInsteadOfTestRoom=true, or
                    // the "current" token on the bake/bake_unity/pipeline commands — see
                    // EnsureBakeSceneReady()) without force-switching to Assets/LightmapTest.unity
                    // via EnsureTestSceneOpen() (still the default when that flag/token is
                    // omitted, so the automated test-room pipeline stays safe to re-run as-is).
                    // This command itself is independent of that flag and always operates on
                    // whatever scene is CURRENTLY open, no scene switch.
                    // Workflow: open the real scene in Unity -> 'poiyomi_swap_only' -> bake via
                    // Unity's own Window > Rendering > Lighting panel (so the Progressive
                    // Lightmapper computes GI against the Standard standins, not raw Poiyomi) ->
                    // 'poiyomi_restore_only' -> 'convert' (or the SDK's own Send Current Scene).
                    PoiyomiBakeStandin.SwapIn();
                    // PoiyomiBakeStandin logs its own swap count via Debug.Log (Unity console /
                    // Editor.log), not AppendResult (this result file) — it only logs when
                    // count > 0, so 0-eligible-renderers is a silent no-op there too. State that
                    // explicitly here so a real-scene run with zero Poiyomi-Opaque materials
                    // doesn't read as "it worked" just because this line appeared.
                    AppendResult("poiyomi_swap_only: dispatched. Swap count is NOT in this file — check " +
                        "Unity's Console window (or Editor.log) for '[PoiyomiBakeStandin] SwapIn:'; " +
                        "no such line means 0 renderers were eligible (nothing swapped).");
                    break;
                case "poiyomi_restore_only":
                    PoiyomiBakeStandin.RestoreAll();
                    AppendResult("poiyomi_restore_only: dispatched. Restore count is NOT in this file — check " +
                        "Unity's Console window (or Editor.log) for '[PoiyomiBakeStandin] RestoreAll:'.");
                    break;
                case "shader_survey": ShaderSurvey(); break;
                default: AppendResult($"Unknown command: '{command}' (expected build_scene / bake [texels] [samples] [current] / report / convert / pipeline <low|mid|high> [bakery|unity] [normal] [current] / poiyomi_smoketest / poiyomi_swap_only / poiyomi_restore_only / shader_survey)"); break;
            }
        }
        catch (Exception ex)
        {
            AppendResult($"Command '{command}' failed:\n{ex}");
        }
    }

    // Scans a file-driven command's already-split args for a case-insensitive "current"
    // token, removes ALL occurrences of it from `parts` in place, and reports whether one
    // was found. Used by the "bake"/"bake_unity"/"pipeline" cases above to let "current" be
    // placed anywhere among the other tokens (e.g. "bake_unity high current" and
    // "bake_unity high normal current" both work) without colliding with quality names
    // (low/mid/high), baker names (bakery/unity), or the "normal" flag — "current" is a
    // reserved keyword that never matches any of those. Called BEFORE the rest of each
    // case's positional parsing so the remaining `parts` array is exactly as it would have
    // been without the flag.
    static bool ExtractCurrentSceneFlag(ref string[] parts)
    {
        var filtered = new List<string>(parts.Length);
        bool found = false;
        foreach (var p in parts)
        {
            if (string.Equals(p, "current", StringComparison.OrdinalIgnoreCase))
                found = true;
            else
                filtered.Add(p);
        }
        parts = filtered.ToArray();
        return found;
    }

    static void AppendResult(string text)
    {
        try
        {
            Directory.CreateDirectory(TempDir);
            string ts = DateTime.Now.ToString("HH:mm:ss");
            string line = $"[{ts}] {text}\n";
            File.AppendAllText(ResultFilePath, line, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.LogError("[LightmapTestHarness] Failed to write result log: " + ex);
        }
        Debug.Log("[LightmapTestHarness] " + text);
    }

    // ------------------------------------------------------------------
    // build_scene
    // ------------------------------------------------------------------

    public static void BuildScene()
    {
        EnsureLightmapUVsEnabled(BpsFloorFbxPath);
        EnsureLightmapUVsEnabled(BpsWallFbxPath);

        var floorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BpsFloorPrefabPath);
        var wallPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BpsWallPrefabPath);
        var bpsMat = AssetDatabase.LoadAssetAtPath<Material>(BpsMaterialPath);

        if (floorPrefab == null)
            AppendResult($"Warning: floor prefab not found at '{BpsFloorPrefabPath}'. Falling back to a primitive floor.");
        if (wallPrefab == null)
            AppendResult($"Warning: wall prefab not found at '{BpsWallPrefabPath}'. Falling back to primitive walls.");
        if (bpsMat == null)
            AppendResult($"Warning: BPS material not found at '{BpsMaterialPath}'.");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var root = new GameObject("LightmapTestRoom");

        BuildFloor(root.transform, floorPrefab, bpsMat);
        BuildWalls(root.transform, wallPrefab, bpsMat);
        BuildTestObjects(root.transform, bpsMat);
        BuildLights(root.transform);

        // Moderate flat ambient. Near-black (0.04) was a Bakery-specific choice, but for the
        // Unity Progressive Lightmapper it turns every surface the sun can't reach pure black
        // (the "black charts"). Bakery ignores RenderSettings.ambient* entirely (it uses its
        // own BakerySkyLight), so raising this only affects the Unity bake — exactly what we
        // want: shadowed areas read as dark gray, not dead black.
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.12f, 0.12f, 0.14f);

        // Avoid Unity's own Progressive Lightmapper auto-baking in the background while
        // we drive Bakery explicitly through this harness.
        Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene, SceneAssetPath);
        AssetDatabase.SaveAssets();

        AppendResult(saved
            ? $"build_scene complete. Saved to '{SceneAssetPath}'."
            : $"build_scene: SaveScene reported failure for '{SceneAssetPath}'.");
    }

    static void EnsureLightmapUVsEnabled(string fbxPath)
    {
        var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
        if (importer == null)
        {
            AppendResult($"Warning: could not resolve ModelImporter for '{fbxPath}' (file missing or not a model).");
            return;
        }

        if (importer.generateSecondaryUV)
        {
            AppendResult($"'{fbxPath}' already has Generate Lightmap UVs enabled.");
            return;
        }

        importer.generateSecondaryUV = true;
        importer.SaveAndReimport();
        AppendResult($"Enabled Generate Lightmap UVs on '{fbxPath}' and reimported.");
    }

    static void BuildFloor(Transform parent, GameObject floorPrefab, Material bpsMat)
    {
        var floorParent = new GameObject("Floor");
        floorParent.transform.SetParent(parent);

        int tilesPerSide = Mathf.RoundToInt(RoomSize / TileSize);

        for (int x = 0; x < tilesPerSide; x++)
        {
            for (int z = 0; z < tilesPerSide; z++)
            {
                Vector3 slotCenter = new Vector3(
                    (x + 0.5f) * TileSize - RoomSize * 0.5f,
                    0f,
                    (z + 0.5f) * TileSize - RoomSize * 0.5f);

                GameObject tile;
                if (floorPrefab != null)
                {
                    tile = PlaceModularPiece(floorPrefab, floorParent.transform, slotCenter, Quaternion.identity, $"FloorTile_{x}_{z}");
                }
                else
                {
                    tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    tile.transform.SetParent(floorParent.transform);
                    tile.transform.localScale = new Vector3(TileSize, 0.1f, TileSize);
                    tile.transform.position = slotCenter + new Vector3(0f, 0.05f, 0f);
                    tile.name = $"FloorTile_{x}_{z}";
                    if (bpsMat != null) tile.GetComponent<Renderer>().sharedMaterial = bpsMat;
                }

                MarkStaticGI(tile);
            }
        }
    }

    static void BuildWalls(Transform parent, GameObject wallPrefab, Material bpsMat)
    {
        var wallParent = new GameObject("Walls");
        wallParent.transform.SetParent(parent);

        int segmentsPerSide = Mathf.RoundToInt(RoomSize / TileSize);
        float half = RoomSize * 0.5f;

        // 3 walls only (North / East / West). South stays open so a screenshot/camera
        // can see the baked interior at a glance.
        BuildWallRun(wallParent.transform, wallPrefab, bpsMat, segmentsPerSide,
            i => new Vector3((i + 0.5f) * TileSize - half, WallHeight * 0.5f, half),
            Quaternion.identity, "WallNorth");

        BuildWallRun(wallParent.transform, wallPrefab, bpsMat, segmentsPerSide,
            i => new Vector3(half, WallHeight * 0.5f, (i + 0.5f) * TileSize - half),
            Quaternion.Euler(0f, 90f, 0f), "WallEast");

        BuildWallRun(wallParent.transform, wallPrefab, bpsMat, segmentsPerSide,
            i => new Vector3(-half, WallHeight * 0.5f, (i + 0.5f) * TileSize - half),
            Quaternion.Euler(0f, -90f, 0f), "WallWest");
    }

    static void BuildWallRun(Transform parent, GameObject wallPrefab, Material bpsMat, int segmentCount,
        Func<int, Vector3> slotCenterForIndex, Quaternion rot, string label)
    {
        for (int i = 0; i < segmentCount; i++)
        {
            Vector3 slotCenter = slotCenterForIndex(i);

            GameObject seg;
            if (wallPrefab != null)
            {
                seg = PlaceModularPiece(wallPrefab, parent, slotCenter, rot, $"{label}_{i}");
            }
            else
            {
                seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seg.transform.SetParent(parent);
                seg.transform.rotation = rot;
                seg.transform.localScale = new Vector3(TileSize, WallHeight, 0.1f);
                seg.transform.position = slotCenter;
                seg.name = $"{label}_{i}";
                if (bpsMat != null) seg.GetComponent<Renderer>().sharedMaterial = bpsMat;
            }

            MarkStaticGI(seg);
        }
    }

    // Places a modular kit prefab so that its *mesh's actual local bounds center*
    // (read at runtime via MeshFilter.sharedMesh.bounds, NOT guessed from the collider)
    // ends up exactly at slotCenter after rotation. This makes placement correct
    // regardless of where Brick Project Studio put the prefab's own pivot.
    static GameObject PlaceModularPiece(GameObject prefab, Transform parent, Vector3 slotCenter, Quaternion rot, string name)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);

        var mf = instance.GetComponentInChildren<MeshFilter>();
        Vector3 localCenter = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.bounds.center : Vector3.zero;

        instance.transform.rotation = rot;
        instance.transform.position = slotCenter - rot * localCenter;
        instance.name = name;
        return instance;
    }

    static void BuildTestObjects(Transform parent, Material bpsMat)
    {
        var testParent = new GameObject("TestObjects");
        testParent.transform.SetParent(parent);

        // Central sphere
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "TestSphere";
        sphere.transform.SetParent(testParent.transform);
        // y=0.6: floor top is at y=0.1 (tile center 0.05 + half-thickness 0.05), so a 0.5-radius
        // sphere sits ON the floor. At y=0.5 its base was buried 0.1 into the floor, hiding the
        // very contact shadow/AO we want to see.
        sphere.transform.position = new Vector3(0f, 0.6f, 0f);
        MarkStaticGI(sphere);

        // Box #1: Brick Project Studio material (textured, Standard/Opaque)
        var boxBps = GameObject.CreatePrimitive(PrimitiveType.Cube);
        boxBps.name = "TestBox_BPSMaterial";
        boxBps.transform.SetParent(testParent.transform);
        boxBps.transform.position = new Vector3(-1.5f, 0.6f, 1.5f);
        if (bpsMat != null) boxBps.GetComponent<Renderer>().sharedMaterial = bpsMat;
        else AppendResult("Warning: BPS material unavailable, TestBox_BPSMaterial keeps the default material.");
        MarkStaticGI(boxBps);

        // Box #2: plain solid-color Standard material (no texture)
        var plainMat = AssetDatabase.LoadAssetAtPath<Material>(PlainBoxMaterialPath);
        if (plainMat == null)
        {
            plainMat = new Material(Shader.Find("Standard"));
            plainMat.color = new Color(0.75f, 0.18f, 0.18f);
            AssetDatabase.CreateAsset(plainMat, PlainBoxMaterialPath);
        }

        var boxPlain = GameObject.CreatePrimitive(PrimitiveType.Cube);
        boxPlain.name = "TestBox_PlainMaterial";
        boxPlain.transform.SetParent(testParent.transform);
        boxPlain.transform.position = new Vector3(1.5f, 0.6f, -1.5f);
        boxPlain.GetComponent<Renderer>().sharedMaterial = plainMat;
        MarkStaticGI(boxPlain);
    }

    static void BuildLights(Transform parent)
    {
        var lightParent = new GameObject("Lights");
        lightParent.transform.SetParent(parent);

        // Directional "sun": warm, baked, shining down at an angle so shadow
        // direction is obvious in the bake result.
        var sunGo = new GameObject("Sun_Directional");
        sunGo.transform.SetParent(lightParent.transform);
        // 35deg elevation (was 50): a lower sun throws long, obvious shadows across the floor
        // instead of short ones that hide under the objects.
        sunGo.transform.rotation = Quaternion.Euler(35f, -30f, 0f);
        var sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.lightmapBakeType = LightmapBakeType.Baked;
        sun.color = new Color(1f, 0.85f, 0.62f);
        sun.intensity = 1.4f;
        // A scripted AddComponent<Light>() defaults to LightShadows.None, so the Progressive
        // Lightmapper bakes illumination but NO shadows. Bakery avoids this because its
        // BakeryDirectLight casts shadows by default; the Unity path needs it set explicitly.
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 1f;

        // Point light in a corner: cool blue, baked, to make the two light
        // colors/directions clearly distinguishable in the lightmap.
        var cornerGo = new GameObject("Corner_PointLight");
        cornerGo.transform.SetParent(lightParent.transform);
        cornerGo.transform.position = new Vector3(RoomSize * 0.5f - 1f, WallHeight - 0.5f, RoomSize * 0.5f - 1f);
        var corner = cornerGo.AddComponent<Light>();
        corner.type = LightType.Point;
        corner.lightmapBakeType = LightmapBakeType.Baked;
        corner.color = new Color(0.5f, 0.72f, 1f);
        // Was 2 — a strong fill light floods the room and washes the sun's cast shadows out
        // to invisibility. Kept low (0.7) as a subtle cool accent so the warm directional
        // shadow stays the dominant, readable feature of the bake.
        corner.intensity = 0.7f;
        corner.range = 8f;
        // Same reason as the sun: enable baked shadows explicitly (default is None).
        corner.shadows = LightShadows.Soft;

#if BAKERY_INCLUDED
        // Weak neutral-gray BakerySkyLight: Bakery NEVER reads RenderSettings.ambientMode/
        // ambientLight (confirmed: no RenderSettings.ambient* reference anywhere in
        // ftRenderLightmap.cs/ftBuildLights.cs — only RenderSettings.skybox, which Bakery
        // temporarily swaps for its own capture pass). The only way to get any ambient/sky
        // contribution into the actual bake is a real BakerySkyLight component. Kept weak
        // (0.2) so Sun_Directional + Corner_PointLight remain the dominant, clearly
        // directional shading in the result.
        var skyGo = new GameObject("Sky_BakeryAmbient");
        skyGo.transform.SetParent(lightParent.transform);
        var sky = skyGo.AddComponent<BakerySkyLight>();
        sky.color = new Color(0.5f, 0.5f, 0.55f);
        sky.intensity = 0.2f;

        ConvertUnityLightsToBakeryLights();
#endif
        // No #else here: with Bakery absent, Sun_Directional/Corner_PointLight above (plain
        // UnityEngine.Light, already baked-shadow-capable) are everything the Unity standard
        // path needs — it never reads Bakery-native light components at all.
    }

    // Adds the Bakery-native light component matching each UnityEngine.Light in the
    // scene, copying color/intensity across.
    //
    // Root cause (per coordinator's real-machine finding, confirmed by reading source):
    // Bakery's tracer NEVER reads plain UnityEngine.Light components. It only collects
    // lights via (ftRenderLightmap.cs lines 4888, 4998-4999):
    //   FindObjectsOfType(typeof(BakeryDirectLight))
    //   FindObjectsOfType(typeof(BakeryPointLight))
    //   FindObjectsOfType(typeof(BakerySkyLight))
    // and ftBuildLights.BuildDirectLight()/BuildPointLight() read straight from those
    // components' own `color`/`intensity` fields (BakeryDirectLight.cs, BakeryPointLight.cs),
    // never from a paired Light. A scene with only Light components + lightmapBakeType=Baked
    // has ZERO light sources as far as Bakery is concerned, which is exactly why the bake
    // came back all-black.
    //
    // NOTE: We searched the entire installed Bakery source tree (Assets/Editor/x64/Bakery/scripts
    // and Assets/Bakery) for an official "Convert Unity lights to Bakery lights" menu/utility —
    // there is NO MenuItem and no class/method with "Convert"+"Light" in its name anywhere in
    // this Bakery version (grep across both folders: zero matches). The bidirectional
    // color/intensity sync code that DOES exist (ftDirectLightInspector.cs ~line 128-158,
    // 594-630) is an editor-inspector-only convenience for a Light placed alongside a
    // BakeryDirectLight for realtime-GI use; it is not invoked at bake time and not a
    // reusable public API. So instead of calling a nonexistent utility, we add the
    // equivalent Bakery components ourselves here, using only confirmed public fields.
    public static void ConvertUnityLightsToBakeryLights()
    {
#if BAKERY_INCLUDED
        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        int convertedDirect = 0, convertedPoint = 0, skippedExisting = 0, skippedUnsupported = 0;

        foreach (var light in lights)
        {
            switch (light.type)
            {
                case LightType.Directional:
                {
                    // Idempotency guard: BakeryDirectLight has [DisallowMultipleComponent],
                    // so AddComponent would log a Unity warning and no-op anyway — but we
                    // check explicitly so re-running this on an already-converted scene is
                    // silent and clearly logged instead.
                    if (light.GetComponent<BakeryDirectLight>() != null)
                    {
                        skippedExisting++;
                        AppendResult($"Light->Bakery: skip '{light.name}' (BakeryDirectLight already present).");
                        continue;
                    }

                    var bdl = light.gameObject.AddComponent<BakeryDirectLight>();
                    bdl.color = light.color;
                    bdl.intensity = light.intensity;
                    convertedDirect++;
                    AppendResult($"Light->Bakery: '{light.name}' (Directional) -> BakeryDirectLight (color={light.color}, intensity={light.intensity}).");
                    break;
                }

                case LightType.Point:
                {
                    if (light.GetComponent<BakeryPointLight>() != null)
                    {
                        skippedExisting++;
                        AppendResult($"Light->Bakery: skip '{light.name}' (BakeryPointLight already present).");
                        continue;
                    }

                    var bpl = light.gameObject.AddComponent<BakeryPointLight>();
                    bpl.color = light.color;
                    bpl.intensity = light.intensity;
                    bpl.cutoff = light.range;
                    convertedPoint++;
                    AppendResult($"Light->Bakery: '{light.name}' (Point) -> BakeryPointLight (color={light.color}, intensity={light.intensity}, cutoff={light.range}).");
                    break;
                }

                default:
                    // Spot/Area/etc: not produced by this harness's build_scene, and not
                    // mapped here to avoid guessing at BakeryPointLight's cone/cookie
                    // settings without a concrete case to validate against.
                    skippedUnsupported++;
                    AppendResult($"Light->Bakery: skip '{light.name}' (LightType.{light.type} not auto-converted by this harness).");
                    break;
            }
        }

        AppendResult($"Light->Bakery conversion summary: {convertedDirect} -> BakeryDirectLight, {convertedPoint} -> BakeryPointLight, {skippedExisting} already-converted, {skippedUnsupported} unsupported type.");
#else
        AppendResult("Light->Bakery conversion skipped: Bakery is not installed in this project (BAKERY_INCLUDED not defined).");
#endif
    }

    static void MarkStaticGI(GameObject go)
    {
        // ContributeGI only. StaticEditorFlags.BatchingStatic is intentionally EXCLUDED:
        // static batching merges multiple renderers' meshes into one shared vertex/index
        // buffer, which breaks the clean 1:1 mapping between a MeshRenderer and its
        // lightmapIndex/lightmapScaleOffset (and would confuse the Resonite SDK's
        // per-object mesh/material conversion). Keeping batching off preserves per-object
        // identity for both the lightmap report and the ResoniteLink scene conversion.
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);
    }

    // ------------------------------------------------------------------
    // bake
    // ------------------------------------------------------------------

    // useCurrentSceneInsteadOfTestRoom defaults to false so every pre-existing call site
    // (bare "bake" command, RunPipeline()'s Bakery branch, any external caller compiled
    // against the old 2-arg signature) keeps forcing open Assets/LightmapTest.unity exactly
    // as before. Passing true skips that force-switch entirely and bakes whatever scene is
    // already open (see EnsureBakeSceneReady()) — this is how LightmapPipelineWindow's
    // "Current Open Scene" selector and the command-file "current" token reach this backend.
    public static void StartBake(float texelsPerUnit = 8f, int giSamples = 8, bool useCurrentSceneInsteadOfTestRoom = false)
    {
        // Second line of defense (the first is the command-file dispatch guard in
        // DispatchCommand): StartBake() is also called directly by RunPipeline() and by
        // LightmapPipelineWindow's "Bake"/"Bake & Send" buttons, neither of which is
        // guaranteed to have re-checked IsBakeInProgress immediately beforehand.
        if (IsBakeInProgress)
        {
            AppendResult("StartBake: bake already in progress; call ignored.");
            return;
        }

#if BAKERY_INCLUDED
        if (!EnsureBakeSceneReady(useCurrentSceneInsteadOfTestRoom))
            return;

        // Counterpart of Convert()'s disable step: a previous send leaves scene lights off,
        // and a disabled light contributes nothing to the bake — re-enable before baking.
        SetSceneLightsEnabled(true);

        // Poiyomi materials never receive a baked lightmap (LightmapMaterialCache only
        // accepts shader.name == "Standard" — see PoiyomiBakeStandin.cs's header comment).
        // Swap eligible (Opaque-only) Poiyomi materials to a temporary Standard standin
        // before this bake reads the scene; Convert()'s finally block restores the real
        // Poiyomi materials once the send completes.
        PoiyomiBakeStandin.SwapIn();

        _lastBaker = LightBaker.Bakery;
        _lastUsedCurrentScene = useCurrentSceneInsteadOfTestRoom;

        // Defensive re-check: build_scene already adds Bakery light components, but if
        // 'bake' is run against a scene that was opened/edited some other way, make sure
        // every UnityEngine.Light still has its Bakery-native counterpart before baking.
        // ConvertUnityLightsToBakeryLights() is idempotent (skips lights that already
        // have a Bakery light component), so calling it again here is safe and just logs
        // "skip ... already present" for anything build_scene already converted.
        ConvertUnityLightsToBakeryLights();

        // ftRenderLightmap.RenderLightmap() is the public entry point used by the
        // "Bakery/Render lightmap..." menu item; it creates/shows the EditorWindow and
        // assigns the static `instance` field. See ftRenderLightmap.cs line ~13246.
        ftRenderLightmap.RenderLightmap();
        var instance = ftRenderLightmap.instance;
        if (instance == null)
        {
            AppendResult("Bake failed: ftRenderLightmap.instance is null after RenderLightmap().");
            return;
        }

        // Color-only lightmaps (no directional/SH data). Defaults (8/8) are fast smoke-test
        // values; pass higher values via "bake <texels> <samples>" for quality bakes.
        ftRenderLightmap.renderDirMode = ftRenderLightmap.RenderDirMode.None;
        instance.texelsPerUnit = texelsPerUnit;
        instance.giSamples = giSamples;

        // 2026-08-08 (Tanossy指摘): FullLightingは直接光+間接光の両方をライトマップへ焼き込む
        // ため、Resonite側で実ライト(LightConverter経由)も生かすと二重に光ることになる
        // (これが2026-07-11のDouble-lighting fix、Convert()側でSetSceneLightsEnabled(false)して
        // いた本来の理由)。今回「Unityの実ライト点灯時の見た目にResoniteを近づけたい」という
        // 要望に対応するため、Indirectモード(間接光/GIバウンスのみベイク)に切り替え、直接光は
        // 常時有効なUnityEngine.Lightをそのまま実ライトとしてResoniteへ送ることで賄う。
        // BakeryPointLight/BakeryDirectLightのbakeToIndirectは両方デフォルトfalseのままでよい
        // (Indirectモードで各ライト自身の直接寄与は焼き込まれず、GIバウンスだけが焼き込まれる —
        // Unity公式のMixed/Baked Indirectと同じ考え方)。Convert()側のライト無効化ステップも
        // このタイミングで撤去する。
        instance.userRenderMode = ftRenderLightmap.RenderMode.Indirect;

        ForceRTXMode();

        SubscribeBakeEvents();
        _bakeStartedByHarness = true;

        AppendResult($"Bake starting (renderDirMode=None, texelsPerUnit={texelsPerUnit}, giSamples={giSamples})...");

        // showMsgWindows=false: suppresses TestNeedsBenchmark()/TestSystemSpecs() dialogs
        // (see ftRenderLightmap.cs RenderButton/TestSystemSpecs), which is required for
        // unattended/file-driven automation.
        instance.RenderButton(false);

        LogActualFtraceExe();

        if (!ftRenderLightmap.bakeInProgress)
        {
            AppendResult("Warning: RenderButton() returned without bakeInProgress becoming true. " +
                "Check the Unity Console for a Bakery error (TestSystemSpecs / TestNeedsBenchmark / path validation failure).");
            _bakeStartedByHarness = false;
        }
#else
        // Bakery is not installed in this project (BAKERY_INCLUDED not defined — see
        // BakeryPresenceDefine.cs). Deliberately does NOT touch _lastBaker/EnsureTestSceneOpen/
        // any Bakery type: just report and return, matching the "hard fail safely" contract
        // callers (RunPipeline()'s LightBaker.Bakery case, LightmapPipelineWindow's "Bake"
        // button) already rely on via _bakeStartedByHarness staying false.
        AppendResult("StartBake: Bakery is not installed in this project (BAKERY_INCLUDED not defined). " +
            "Use the Unity Standard baker instead, or install Bakery and let BakeryPresenceDefine " +
            "re-detect it on the next domain reload.");
#endif
    }

    // ------------------------------------------------------------------
    // bake — Unity standard (built-in Progressive) path, no Bakery involvement
    // ------------------------------------------------------------------
    //
    // API surface below is NOT guessed: this project ships no Bakery-equivalent source
    // tree for Unity's own lightmapper, so every member was confirmed directly against the
    // installed Unity 2022.3.22f1 editor DLLs via ildasm (Assets/Editor/x64 has no
    // equivalent for this path; verification method noted per-member):
    //
    //   UnityEngine.LightingSettings  (UnityEngine.CoreModule.dll)
    //     public sealed class LightingSettings : UnityEngine.Object, with a public
    //     parameterless constructor (`.ctor() cil managed` calling `UnityEngine.Object::.ctor()`
    //     directly — confirmed in the disassembled IL body of the class).
    //       bool bakedGI                                  (.property instance bool bakedGI())
    //       bool realtimeGI                                (.property instance bool realtimeGI())
    //       LightingSettings.Lightmapper lightmapper       (nested enum: Enlighten=0,
    //                                                        ProgressiveCPU=1, ProgressiveGPU=2 —
    //                                                        confirmed as .field public static
    //                                                        literal int32 entries on the nested
    //                                                        `LightingSettings/Lightmapper` type)
    //       float lightmapResolution                       (.property instance float32 ...)
    //       int directSampleCount / indirectSampleCount    (.property instance int32 ...)
    //
    //   UnityEditor.Lightmapping  (UnityEditor.CoreModule.dll, static class)
    //     LightingSettings lightingSettings { get; set; }   (static property; applies to the
    //                                                         ACTIVE scene — this harness only
    //                                                         ever operates on one scene, so we
    //                                                         use this instead of the also-real
    //                                                         SetLightingSettingsForScene(Scene,
    //                                                         LightingSettings) overload, which
    //                                                         exists for multi-scene setups)
    //     static bool BakeAsync()                            (.method public hidebysig static
    //                                                         bool BakeAsync() ... internalcall —
    //                                                         returns false if the bake did not
    //                                                         actually start, exactly like
    //                                                         Bakery's RenderButton()/bakeInProgress
    //                                                         pattern above)
    //     static bool isRunning { get; }                     (get_isRunning(), internalcall)
    //     static event System.Action bakeCompleted           (add_/remove_bakeCompleted(System.Action);
    //                                                         plain no-arg delegate, not EventHandler
    //                                                         — see SubscribeBakeEvents())
    //
    // Deliberately skipped for this path (per spec — this is the whole point of offering it):
    // ConvertUnityLightsToBakeryLights(), ForceRTXMode(), and EnsureBakeryLightmapsLoaded()
    // (ftLightmaps.RefreshFull()) are all Bakery-specific recovery/setup steps that Unity's
    // own Lightmapping system does not need and must not run for. The Bakery-object
    // suppression converter (BakeryTempObjectSuppression.cs) is baker-agnostic and simply
    // has nothing to do if "!ftraceLightmaps" was never created in this session.
    //
    // The LightingSettings instance itself is persisted as a reusable asset (see
    // UnityLightingSettingsAssetPath below) instead of being a throwaway in-memory
    // UnityEngine.Object: without this, the settings only exist for the duration of the
    // editor session and nothing records *what was baked with* once Unity closes, so a
    // saved scene alone wouldn't be reproducible. One asset is reused and overwritten in
    // place on every Unity-standard bake (matches Bakery's own "!ftraceLightmaps" single-
    // persistent-storage-object pattern, not one asset per bake).
    public static readonly string UnityLightingSettingsAssetPath = "Assets/ResoniteSDK/Generated/LightmapPipeline_LightingSettings.asset";

    // "Bake normal detail into lightmap (experimental)" - Step 1 (directional bake mode).
    // Default false so every pre-existing call site (bare "bake_unity" command, RunPipeline()'s
    // 2-arg overloads, any external caller compiled against the old 3-arg signature) keeps
    // baking exactly as before - only a caller that explicitly passes true opts into the new
    // path. When false, directionalityMode is set to NonDirectional EXPLICITLY below (not left
    // untouched) so behavior does not depend on whatever value happened to already be on a
    // reused LightingSettings asset from a previous ON bake.
    //
    // LightmapsMode / LightingSettings.directionalityMode confirmed via ikdasm against this
    // Editor's own installed UnityEngine.CoreModule.dll (2022.3.22f1):
    //   UnityEngine.LightingSettings.directionalityMode : UnityEngine.LightmapsMode { get; set; }
    //     (native name "LightmapsBakeMode" - .property instance class UnityEngine.LightmapsMode
    //     directionalityMode(), with both get_/set_ accessors present)
    //   UnityEngine.LightmapsMode : System.Enum, [Flags]
    //     NonDirectional = 0, CombinedDirectional = 1
    //     (a third literal, SeparateDirectional = 2, also exists on the enum but carries
    //     [Obsolete("...has been removed. Use CombinedDirectional instead...", true)] -
    //     ikdasm-confirmed on this same enum - so only NonDirectional/CombinedDirectional are
    //     ever valid choices here.)
    // useCurrentSceneInsteadOfTestRoom: same meaning and same default (false = unchanged,
    // force-open Assets/LightmapTest.unity) as StartBake()'s own parameter of the same name
    // — see that method's header comment. Appended after bakeNormalDetail (not before) so
    // every pre-existing 4-arg call site keeps compiling unchanged.
    public static void StartBakeUnity(float lightmapResolution, int directSampleCount, int indirectSampleCount, bool bakeNormalDetail = false, bool useCurrentSceneInsteadOfTestRoom = false)
    {
        // Second line of defense — see the matching guard/comment at the top of StartBake().
        if (IsBakeInProgress)
        {
            AppendResult("StartBakeUnity: bake already in progress; call ignored.");
            return;
        }

        if (!EnsureBakeSceneReady(useCurrentSceneInsteadOfTestRoom))
            return;

        // Counterpart of Convert()'s disable step: a previous send leaves scene lights off,
        // and a disabled light contributes nothing to the bake — re-enable before baking.
        SetSceneLightsEnabled(true);

        // See the matching comment in StartBake() above — same Poiyomi->Standard standin
        // swap, needed for this backend too.
        PoiyomiBakeStandin.SwapIn();

        _lastBaker = LightBaker.UnityStandard;
        _lastUsedCurrentScene = useCurrentSceneInsteadOfTestRoom;

        var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(UnityLightingSettingsAssetPath);
        bool isNewAsset = settings == null;
        if (isNewAsset)
            settings = new LightingSettings();

        settings.bakedGI = true;
        settings.realtimeGI = false;
        settings.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
        settings.lightmapResolution = lightmapResolution;
        settings.directSampleCount = directSampleCount;
        settings.indirectSampleCount = indirectSampleCount;
        // Ambient Occlusion gives contact shadows where geometry meets (object bases on the
        // floor, wall/floor seams) that direct light shadows alone don't produce — this is the
        // main reason Bakery's result reads as "grounded". Independent of light.shadows above.
        settings.ao = true;
        settings.aoMaxDistance = 1f;
        // Exponent > 1 deepens the contact-shadow falloff so object bases read as grounded
        // (property names ildasm-confirmed against UnityEngine.CoreModule: aoExponentDirect /
        // aoExponentIndirect — there is no "aoExposure").
        settings.aoExponentDirect = 1f;
        settings.aoExponentIndirect = 1.5f;

        // "法線を焼き込む（実験的）" Step 1: directional lightmap mode. Explicit in BOTH branches
        // (not just the ON one) so a LightingSettings asset that was left CombinedDirectional by
        // a previous ON bake doesn't silently stay directional the next time this runs with
        // bakeNormalDetail=false — OFF must always mean "exactly today's NonDirectional bake",
        // never "whatever the asset happened to have last".
        settings.directionalityMode = bakeNormalDetail
            ? LightmapsMode.CombinedDirectional
            : LightmapsMode.NonDirectional;

        if (isNewAsset)
        {
            EnsureFolderExists(Path.GetDirectoryName(UnityLightingSettingsAssetPath).Replace('\\', '/'));
            AssetDatabase.CreateAsset(settings, UnityLightingSettingsAssetPath);
            AppendResult($"Created LightingSettings asset at '{UnityLightingSettingsAssetPath}'.");
        }
        else
        {
            EditorUtility.SetDirty(settings);
        }

        AssetDatabase.SaveAssets();

        Lightmapping.lightingSettings = settings;

        SubscribeBakeEvents();
        _unityBakeStartedByHarness = true;

        AppendResult($"Unity standard bake starting (lightmapper=ProgressiveGPU, lightmapResolution={lightmapResolution}, directSampleCount={directSampleCount}, indirectSampleCount={indirectSampleCount}, directionalityMode={settings.directionalityMode})...");

        bool started = Lightmapping.BakeAsync();
        if (!started)
        {
            AppendResult("Warning: Lightmapping.BakeAsync() returned false — bake did not start. Check the Unity Console for a Progressive Lightmapper error.");
            _unityBakeStartedByHarness = false;
        }
    }

    // AssetDatabase.CreateAsset() requires every parent folder to already exist as a real
    // asset folder; this creates any missing ones one path segment at a time (standard
    // idiom — AssetDatabase.CreateFolder has no recursive/mkdir -p equivalent). In
    // practice this is expected to be a no-op: "Assets/ResoniteSDK/Generated/" already
    // exists (the SDK's own LightmapVariants materials live there), kept defensive only
    // for a checkout state where it doesn't yet.
    static void EnsureFolderExists(string folderPath)
    {
        var parts = folderPath.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    // RTX 4070 Ti Super (Ada) crashes on Bakery's old default ftrace.exe
    // (CUDA _rtBufferCreate error) — that binary predates Ada's compute capability.
    // TestSystemSpecs() (ftRenderLightmap.cs ~line 626-666), called from inside
    // RenderButton(), only switches to an RTX-capable ftrace binary when:
    //   gstorage != null && !rtxMode && !gstorage.runsNonRTX
    // and then picks the binary via `gstorage.alwaysEnableRTX` (auto, no dialog) vs.
    // an interactive EditorUtility.DisplayDialogComplex (blocks unattended runs).
    // We go through gstorage.alwaysEnableRTX (Bakery's own auto-select path) rather
    // than setting `rtxMode`/`ftraceExe` ourselves, because:
    //   - `ftraceExe` is a PRIVATE static field on ftRenderLightmap — not settable
    //     without reflection, and Bakery re-derives it from `gstorage.runsRTX6` anyway
    //     (ftraceExe6="ftraceRTX.exe" if runsRTX6, else ftraceExe9="ftraceRTX9.exe";
    //     see ftRenderLightmap.cs lines 268-272, 635-666, 2508, 13201).
    //   - `runsRTX6` reflects Bakery's own hardware-capability decision (normally set by
    //     "Bakery/Utilities/Detect optimal settings" / ftDetectSettings.DetectCompatSettings()).
    //     Trusting it (rather than hardcoding either RTX6 or RTX9 ourselves) avoids picking
    //     the wrong binary for this specific driver/CUDA combination.
    // We DO need `gstorage.runsNonRTX = false` in addition to `alwaysEnableRTX = true`,
    // because ftGlobalStorage.cs ships with `runsNonRTX = true` by default (confirmed:
    // Assets/Bakery/ftGlobalStorage.cs line 119) — that default is exactly what gated the
    // auto-enable block shut and let the crash-prone non-RTX ftrace.exe run in the first
    // place, so `alwaysEnableRTX` alone would not have been enough.
    //
    // Entire method (not just its body) is gated: it is only ever called from within
    // StartBake()'s own #if BAKERY_INCLUDED block above, so with Bakery absent there is no
    // call site left to satisfy and no #else stub is needed.
#if BAKERY_INCLUDED
    static void ForceRTXMode()
    {
        var gstorage = ftRenderLightmap.FindGlobalStorage();
        if (gstorage == null)
        {
            AppendResult("Warning: ftGlobalStorage.asset not found via FindGlobalStorage() — cannot force RTX mode. Bake may crash on this GPU (old ftrace.exe / CUDA _rtBufferCreate).");
            return;
        }

        gstorage.runsNonRTX = false;     // un-default from the crash-prone legacy ftrace.exe path
        gstorage.alwaysEnableRTX = true; // let TestSystemSpecs() auto-pick an RTX ftrace binary, no blocking dialog
        // TestSystemSpecs() only enters its RTX auto-enable block when the stored
        // gpuName_2 matches the current GPU (ftRenderLightmap.cs ~line 628-635:
        // a mismatch means "last optimal settings detection ran on another GPU" and
        // sets foundCompatibleSetup=false instead). Detection results (runsRTX6 etc.)
        // are already populated in gstorage, so mark them as valid for this GPU.
        gstorage.gpuName_2 = SystemInfo.graphicsDeviceName;
        // Real-machine result (2026-07-10): ftraceRTX.exe (the OptiX 6 build selected
        // when runsRTX6==true) fails on this system with "Error (1546): Failed to load
        // OptiX library" — current NVIDIA drivers no longer ship the OptiX 6 ABI.
        // ftraceRTX9.exe (OptiX 9 build) is the correct binary for modern GPUs/drivers,
        // so force the runsRTX6 flag off; TestSystemSpecs() then picks ftraceExe9.
        // (runsRTX6==true was a stale/corrupted detection result to begin with.)
        gstorage.runsRTX6 = false;
        EditorUtility.SetDirty(gstorage);

        // Denoiser support checks in TestSystemSpecs() (~line 668+) raise blocking
        // DisplayDialogComplex dialogs NOT gated by verbose/showMsgWindows when
        // gstorage.runsOptixN is false. Disable denoising entirely for the unattended
        // test bake — noise is acceptable for a pipeline verification bake.
        if (ftRenderLightmap.instance != null)
            ftRenderLightmap.instance.denoise = false;

        // Real-machine result (2026-07-10, attempt 6): setting the gstorage flags via
        // this method is NOT sufficient. RenderLightmap() (window creation, called
        // before ForceRTXMode) already runs Bakery's RTX auto-enable with the values
        // current at that moment: it sets the static rtxMode=true and picked
        // ftraceExe from the then-stale runsRTX6. Once rtxMode is true,
        // TestSystemSpecs()'s `!rtxMode` gate never re-picks the binary, so our flag
        // updates above are ignored for this session. Deterministic fix: assign the
        // private static `ftraceExe` directly via reflection.
        try
        {
            ftRenderLightmap.rtxMode = true;
            var exeField = typeof(ftRenderLightmap).GetField("ftraceExe", BindingFlags.NonPublic | BindingFlags.Static);
            if (exeField != null)
                exeField.SetValue(null, "ftraceRTX9.exe"); // OptiX 9 build — the only one current drivers can load
            else
                AppendResult("Warning: ftraceExe field not found via reflection; Bakery may still use a stale binary.");
        }
        catch (Exception ex)
        {
            AppendResult("Warning: failed to force ftraceExe via reflection:\n" + ex);
        }

        AppendResult($"RTX mode enabled (gstorage.runsRTX6={gstorage.runsRTX6}, gpuName_2='{gstorage.gpuName_2}', denoise=off -> expected ftraceExe={(gstorage.runsRTX6 ? "ftraceRTX.exe" : "ftraceRTX9.exe")}).");
    }
#endif

    // Authoritative confirmation: reads Bakery's actual private static `ftraceExe`
    // field via reflection right after RenderButton() ran TestSystemSpecs(), so we
    // log what Bakery really selected rather than just our prediction.
    //
    // Entire method gated for the same reason as ForceRTXMode() above: its only call site
    // (inside StartBake()) is itself inside #if BAKERY_INCLUDED.
#if BAKERY_INCLUDED
    static void LogActualFtraceExe()
    {
        try
        {
            var field = typeof(ftRenderLightmap).GetField("ftraceExe", BindingFlags.NonPublic | BindingFlags.Static);
            string value = field != null ? System.Convert.ToString(field.GetValue(null)) : "(reflection failed: field not found)";
            AppendResult($"RTX mode enabled (ftraceExe={value}).");
        }
        catch (Exception ex)
        {
            AppendResult("Warning: could not read ftRenderLightmap.ftraceExe via reflection:\n" + ex);
        }
    }
#endif

    static void OnBakeFinished(object sender, EventArgs e)
    {
        if (!_bakeStartedByHarness)
            return; // Ignore bakes not triggered by this harness (e.g. manual clicks).

        _bakeStartedByHarness = false;
        AppendResult("bake completed");
        Report();
        MaybeContinuePipeline();
    }

    // Lightmapping.bakeCompleted fires for ANY Unity bake (manual "Lighting" window click
    // included, and also fires once for every scene when baking multiple scenes at once) —
    // same "only react to bakes this harness itself started" guard as OnBakeFinished above.
    static void OnUnityBakeFinished()
    {
        if (!_unityBakeStartedByHarness)
            return;

        _unityBakeStartedByHarness = false;
        AppendResult("unity bake completed");
        Report();
        MaybeContinuePipeline();
    }

    // ------------------------------------------------------------------
    // pipeline — build/prepare -> bake -> (on completion) convert, chained via a flag that
    // is only ever set here and only ever consumed once by whichever bake-finished handler
    // fires next.
    // ------------------------------------------------------------------

    public static void RunPipeline(string qualityKey, LightBaker baker, bool bakeNormalDetail = false, bool useCurrentSceneInsteadOfTestRoom = false)
    {
        if (!QualityPresets.TryGetValue(qualityKey, out var quality))
        {
            AppendResult($"pipeline: unknown quality '{qualityKey}' (expected low/mid/high). Aborting.");
            return;
        }

        RunPipeline(quality, baker, bakeNormalDetail, useCurrentSceneInsteadOfTestRoom);
    }

    // bakeNormalDetail defaults to false so both pre-existing 2-arg call sites (the window's
    // RunBakeAndSend() before this session's change, and the "pipeline <quality> [baker]"
    // command-file path without a trailing "normal" token) keep compiling and behaving exactly
    // as before. Only meaningful for LightBaker.UnityStandard - see StartBakeUnity()'s own
    // header comment; silently has no effect for LightBaker.Bakery (Bakery's own directional
    // modes are a separate, unrelated feature - RenderDirMode - not wired to this flag).
    //
    // useCurrentSceneInsteadOfTestRoom: same meaning/default as StartBake()'s/StartBakeUnity()'s
    // own parameter of the same name (false = unchanged, force-open Assets/LightmapTest.unity).
    // Appended last so every pre-existing 3-arg call site keeps compiling unchanged.
    public static void RunPipeline(BakeQuality quality, LightBaker baker, bool bakeNormalDetail = false, bool useCurrentSceneInsteadOfTestRoom = false)
    {
        // Operates on the scene selected by useCurrentSceneInsteadOfTestRoom — no forced
        // scene open here beyond what EnsureBakeSceneReady() already does inside
        // StartBake()/StartBakeUnity() (which only force-opens the harness's own test scene
        // when useCurrentSceneInsteadOfTestRoom is false; it never switches scenes when true).
        AppendResult($"pipeline starting (baker={baker}, bakeNormalDetail={bakeNormalDetail}, useCurrentSceneInsteadOfTestRoom={useCurrentSceneInsteadOfTestRoom})...");

        // Set BEFORE starting the bake (not after) so this is the single, unambiguous
        // "pipeline requested a chained convert" flag for whichever handler observes the
        // next bake-finished event.
        _pipelineChainConvert = true;

        switch (baker)
        {
            case LightBaker.Bakery:
                // Step 1 (light prep) is handled inside StartBake() itself
                // (ConvertUnityLightsToBakeryLights() defensive re-check) — no separate step
                // needed here.
                StartBake(quality.BakeryTexelsPerUnit, quality.BakeryGiSamples, useCurrentSceneInsteadOfTestRoom);
                break;

            case LightBaker.UnityStandard:
                // No light-prep step for this baker by design (see StartBakeUnity()'s header
                // comment): Unity Lights are used as-is.
                StartBakeUnity(quality.UnityLightmapResolution, quality.UnityDirectSampleCount, quality.UnityIndirectSampleCount, bakeNormalDetail, useCurrentSceneInsteadOfTestRoom);
                break;

            default:
                AppendResult($"pipeline: unhandled baker '{baker}'. Aborting.");
                _pipelineChainConvert = false;
                return;
        }

        // Both Start*() methods flip their own *StartedByHarness flag back to false (with
        // an AppendResult warning already logged) if the bake never actually started. If
        // that happened, the chain must not silently wait forever for a bake-finished event
        // that will never fire.
        bool actuallyStarted = baker == LightBaker.Bakery ? _bakeStartedByHarness : _unityBakeStartedByHarness;
        if (!actuallyStarted)
        {
            _pipelineChainConvert = false;
            AppendResult("pipeline: bake did not start (see warning above). Aborting chain; convert was not attempted.");
        }
    }

    // Invoked from OnBakeFinished/OnUnityBakeFinished after Report(). Convert() already
    // logs a full, safe "Resonite not connected" explanation and returns without throwing
    // when the SDK isn't connected — so this satisfies "SDK未接続なら手順ログ出して安全終了"
    // with no extra handling needed here.
    static void MaybeContinuePipeline()
    {
        if (!_pipelineChainConvert)
            return;

        _pipelineChainConvert = false;
        AppendResult("pipeline: bake finished -> auto-converting...");
        Convert();
    }

    // ------------------------------------------------------------------
    // report
    // ------------------------------------------------------------------

    // Bakery keeps baked lightmap references in its per-scene ftLightmapsStorage and
    // loads them into LightmapSettings via ftLightmaps.RefreshFull() — after a scene
    // (re)open LightmapSettings.lightmaps can legitimately be empty even though the
    // bake succeeded (renderers keep their lightmapIndex/ScaleOffset). Both Report()
    // and Convert() depend on LightmapSettings (the SDK's LightmapMaterialCache reads
    // it), so restore it first when it looks unloaded.
    static void EnsureBakeryLightmapsLoaded()
    {
        if (LightmapSettings.lightmaps.Length > 0)
            return;

#if BAKERY_INCLUDED
        try
        {
            ftLightmaps.RefreshFull();
            AppendResult($"ftLightmaps.RefreshFull() called (LightmapSettings was empty) -> lightmaps now: {LightmapSettings.lightmaps.Length}");
        }
        catch (Exception ex)
        {
            AppendResult("Warning: ftLightmaps.RefreshFull() failed:\n" + ex);
        }
#else
        // Bakery not installed. NOTE this branch IS reachable even without ever calling
        // StartBake(): _lastBaker's SessionState getter defaults to LightBaker.Bakery when
        // never explicitly set (see its property above), so Report()/Convert() can call
        // this on a totally fresh session before any bake. Nothing to refresh from without
        // Bakery installed — safe no-op.
#endif
    }

    // Bug fix: this used to call EnsureTestSceneOpen() unconditionally, which force-switches
    // the active scene to the test room. Report() runs from OnBakeFinished()/
    // OnUnityBakeFinished() BEFORE MaybeContinuePipeline()'s chained Convert()/send step, so
    // a "Bake & Send" run that correctly baked whatever scene the user had open would get its
    // active scene yanked away to the test room by this call right before the send - meaning
    // Convert() would send the WRONG (test room) scene instead of the one that was actually
    // baked. Report() now respects the same scene-selection mode the triggering bake used
    // (_lastUsedCurrentScene, set alongside _lastBaker in StartBake()/StartBakeUnity()) via
    // EnsureBakeSceneReady(), instead of always forcing the test room open.
    public static void Report()
    {
        if (!EnsureBakeSceneReady(_lastUsedCurrentScene))
            return;

        // Skip after a Unity-standard bake: ftLightmaps.RefreshFull() is Bakery-only and an
        // empty LightmapSettings.lightmaps there is either a real "no lightmaps yet" state
        // or already being populated by Unity's own Lightmapping system.
        if (_lastBaker == LightBaker.Bakery)
            EnsureBakeryLightmapsLoaded();

        var sb = new StringBuilder();
        sb.AppendLine("--- Lightmap Report ---");
        sb.AppendLine($"Active scene: {SceneManager.GetActiveScene().path}");

        var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>()
            .OrderBy(r => r.name, StringComparer.Ordinal)
            .ToArray();

        sb.AppendLine($"MeshRenderer count: {renderers.Length}");
        foreach (var r in renderers)
        {
            sb.AppendLine($"  {r.name}: lightmapIndex={r.lightmapIndex}, lightmapScaleOffset={r.lightmapScaleOffset}");
        }

        var lightmaps = LightmapSettings.lightmaps;
        sb.AppendLine($"LightmapSettings.lightmaps.Length: {lightmaps.Length}");
        for (int i = 0; i < lightmaps.Length; i++)
        {
            var lm = lightmaps[i];
            string colorInfo = DescribeTexture(lm.lightmapColor);
            string dirInfo = DescribeTexture(lm.lightmapDir);
            string shadowInfo = DescribeTexture(lm.shadowMask);
            string colorStats = SamplePixelStats(lm.lightmapColor);
            sb.AppendLine($"  Lightmap[{i}]: color={colorInfo} [{colorStats}], dir={dirInfo}, shadowMask={shadowInfo}");
        }

        AppendResult(sb.ToString());
    }

    static string DescribeTexture(Texture2D tex)
    {
        if (tex == null) return "null";
        return $"format={tex.format}, size={tex.width}x{tex.height}";
    }

    // Approximate "is this actually black?" check, done inside Unity instead of an
    // external .hdr parse. Full CPU readback (not a 1x1 GPU downsample): a single Blit
    // to a much smaller RenderTexture would just bilinear-sample a few source texels
    // near the center rather than truly averaging the whole image, which could hide a
    // partially-black bake. This harness only ever bakes a tiny test scene, so reading
    // back the whole (small) lightmap atlas on the CPU is cheap.
    //
    // Caveat: depending on Player Settings > Lightmap Encoding, lightmapColor may be
    // RGBM-encoded rather than plain linear RGB. Graphics.Blit here does a plain copy
    // (no RGBM decode), so these numbers are raw stored channel values, not true
    // radiance. That's still sufficient to answer "is it all zero" (a black bake
    // encodes to ~(0,0,0[,0]) either way) — for a physically accurate luminance value,
    // decode via the platform's DecodeLightmap logic (out of scope here; Athena can do
    // the exact check against BakeryLightmaps/*.hdr directly with Python).
    static string SamplePixelStats(Texture2D tex)
    {
        if (tex == null) return "no texture";

        RenderTexture rt = null;
        RenderTexture prevActive = null;
        Texture2D readable = null;
        try
        {
            rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(tex, rt);

            prevActive = RenderTexture.active;
            RenderTexture.active = rt;

            readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBAFloat, false, true);
            readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            readable.Apply();

            var pixels = readable.GetPixels();

            float maxChannel = 0f;
            double sumChannel = 0f;
            foreach (var p in pixels)
            {
                float m = Mathf.Max(p.r, Mathf.Max(p.g, p.b));
                if (m > maxChannel) maxChannel = m;
                sumChannel += m;
            }
            float avgChannel = pixels.Length > 0 ? (float)(sumChannel / pixels.Length) : 0f;

            return $"raw maxChannel={maxChannel:F5}, avgChannel={avgChannel:F5}, texels={pixels.Length}";
        }
        catch (Exception ex)
        {
            return "pixel sampling failed: " + ex.Message;
        }
        finally
        {
            if (prevActive != null || rt != null) RenderTexture.active = prevActive;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
        }
    }

    static bool EnsureTestSceneOpen()
    {
        var active = SceneManager.GetActiveScene();
        if (active.path == SceneAssetPath && active.isLoaded)
            return true;

        string fullScenePath = Path.Combine(ProjectDir, SceneAssetPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullScenePath))
        {
            AppendResult($"Scene '{SceneAssetPath}' does not exist. Run 'build_scene' first.");
            return false;
        }

        EditorSceneManager.OpenScene(SceneAssetPath, OpenSceneMode.Single);
        AppendResult($"Opened scene '{SceneAssetPath}'.");
        return true;
    }

    // Scene-readiness guard shared by StartBake()/StartBakeUnity(). Dispatches to
    // EnsureTestSceneOpen() (force-open the dedicated test room) when
    // useCurrentSceneInsteadOfTestRoom is false — the pre-existing, still-default behavior —
    // or, when true, deliberately does NOT touch the active scene at all: it only confirms
    // one is actually open and safe to bake against. This is the opposite responsibility
    // from EnsureTestSceneOpen() (whose entire job is to force-switch TO the test room), so
    // it is kept as its own method rather than adding a branch inside EnsureTestSceneOpen()
    // itself — callers that only ever want the old force-switch behavior (e.g. Report(),
    // which is unrelated to this scene-selection feature) keep calling EnsureTestSceneOpen()
    // directly and are completely unaffected by this addition.
    static bool EnsureBakeSceneReady(bool useCurrentSceneInsteadOfTestRoom)
    {
        if (!useCurrentSceneInsteadOfTestRoom)
            return EnsureTestSceneOpen();

        var active = SceneManager.GetActiveScene();
        if (!active.IsValid() || !active.isLoaded)
        {
            AppendResult("EnsureBakeSceneReady: no valid scene is currently open. " +
                "'Current Open Scene' mode never force-opens a scene for you — open the scene " +
                "you want to bake in Unity first, then retry.");
            return false;
        }

        AppendResult($"EnsureBakeSceneReady: using currently open scene '{active.path}' " +
            "(Current Open Scene mode; not switching to the test room).");
        return true;
    }

    // ------------------------------------------------------------------
    // convert
    // ------------------------------------------------------------------

    public static void Convert()
    {
        // See Report() above: this is a Bakery-only recovery step.
        if (_lastBaker == LightBaker.Bakery)
            EnsureBakeryLightmapsLoaded();

        // We deliberately do NOT force-open/force-create the ResoniteLinkWindow here:
        // doing so would disturb a session the user may already have set up manually,
        // and the SDK is explicitly marked beta ("missing a lot of converters").
        // We only look for an ALREADY OPEN window instance.
        // FindObjectsOfTypeAll returns hidden/undocked and even stale-but-not-yet-GCed window
        // instances, so windows[0] can be a dead leftover whose State is Disconnected while the
        // live session the user actually connected is a different element. Prefer a Connected
        // instance; fall back to the first only when none report Connected.
        var window = PickResoniteLinkWindow();

        if (window == null)
        {
            AppendResult(
                "Resonite not connected: ResoniteLinkWindow is not open.\n" +
                "Connection prerequisites (read from Assets/ResoniteSDK/Editor/Managers/ResoniteLinkWindow.cs):\n" +
                "  1. In Unity: menu 'Resonite SDK > Open Resonite SDK Manager'.\n" +
                "  2. In Resonite, make sure a ResoniteLink session is running; LinkSessionListener auto-discovers\n" +
                "     it and lists it as a button 'Connect to session \"<name>\" (port: N)' under AutoDiscovery.\n" +
                "  3. Click that button (AutoDiscovery), or switch to 'Manual', type the port, and click 'Connect'.\n" +
                "  4. Wait until State becomes Connected (shown as 'ResoniteLinkStatus: Connected on port N').\n" +
                "  5. Click 'Send Current Scene' (or 'Start Realtime Mode') to run the conversion.\n" +
                "Aborting safely; convert was not attempted.");
            return;
        }

        var state = window.State;
        if (state != ResoniteLinkWindow.ConnectionState.Connected)
        {
            AppendResult(
                $"Resonite not connected: ResoniteLinkWindow.State = {state}.\n" +
                "Open 'Resonite SDK > Open Resonite SDK Manager', connect (AutoDiscovery or Manual port), " +
                "then re-run 'convert'. Aborting safely.");
            return;
        }

        try
        {
            // 2026-08-08 (Tanossy指摘、設計変更): 2026-07-11のDouble-lighting fixでは
            // FullLighting(直接+間接光を両方ベイク)を前提に、送信直前にライトを無効化して
            // 二重発光を防いでいた。StartBake()をIndirectモード(間接光のみベイク)へ切り替えた
            // ことでこの前提が崩れたため、無効化ステップ自体を撤去する — 直接光はもう
            // ライトマップに含まれないので、実ライト(LightConverter経由)を常時有効なまま送って
            // Resonite側で直接光を担わせる。これによりUnityの「実ライト点灯時」の見た目に
            // Resonite側も近づく設計になる。
            //
            // SendCurrentScene() is private on ResoniteLinkWindow (see ResoniteLinkWindow.cs);
            // it calls EnsureConverter() then SceneConverter.ConvertScene() internally.
            var method = typeof(ResoniteLinkWindow).GetMethod("SendCurrentScene", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                AppendResult("Convert failed: could not locate private method 'SendCurrentScene' on ResoniteLinkWindow via reflection (SDK API may have changed).");
                return;
            }

            method.Invoke(window, null);
            AppendResult($"Convert: SendCurrentScene() invoked on connected session (UniqueSessionId={window.UniqueSessionId}).");
        }
        catch (Exception ex)
        {
            AppendResult("Convert failed while invoking SendCurrentScene():\n" + ex);
        }
        finally
        {
            // Always restore the real Poiyomi materials once the send attempt (success or
            // failure) is over, so the Unity scene view goes back to its normal Poiyomi look
            // rather than staying on the temporary Standard standins. Safe no-op if SwapIn()
            // was never called (e.g. Convert() invoked without a prior bake this session).
            PoiyomiBakeStandin.RestoreAll();
        }
    }

    public static void ConvertMeshesOnly()
    {
        InvokeConnectedSdkSend("SendMeshesOnly", "Convert Meshes Only");
    }

    public static void ConvertMaterialsOnly()
    {
        InvokeConnectedSdkSend("SendMaterialsOnly", "Convert Materials Only");
    }

    // 2026-08-08 (Tanossy指摘): UnityMCP(MCP for Unity、独自HTTPサーバー+JSON-RPCをUnity Editor
    // プロセス内で常駐させる「被せるプラグイン」)経由でのexecute_code呼び出しを介さず、この
    // ファイルポーリング式ハーネス（Temp/lightmap_harness_cmd.txtをEditorApplication.updateで
    // 監視するだけの、常駐HTTPサーバーを持たない軽量な仕組み）だけでSDKの送信を駆動できるように
    // するための追加。ResoniteLink.dll側の未同期レースコンディション(2026-07-30ソース読解で確定済み)
    // が、UnityMCPの常駐サーバーとの相互作用で再現しやすくなっている可能性を切り分けるのが狙い。
    public static void ConvertLightmapsOnly()
    {
        InvokeConnectedSdkSend("SendLightmapsOnly", "Convert Lightmaps Only");
    }

    static void InvokeConnectedSdkSend(string methodName, string label)
    {
        var window = PickResoniteLinkWindow();

        if (window == null)
        {
            AppendResult($"{label}: ResoniteLinkWindow is not open. Open 'Resonite SDK > Open Resonite SDK Manager' and connect first.");
            return;
        }

        if (window.State != ResoniteLinkWindow.ConnectionState.Connected)
        {
            AppendResult($"{label}: ResoniteLinkWindow.State = {window.State}. Connect first.");
            return;
        }

        try
        {
            var method = typeof(ResoniteLinkWindow).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
            {
                AppendResult($"{label}: could not locate ResoniteLinkWindow.{methodName}().");
                return;
            }

            method.Invoke(window, null);
            AppendResult($"{label}: {methodName}() invoked on connected session (UniqueSessionId={window.UniqueSessionId}).");
        }
        catch (Exception ex)
        {
            AppendResult($"{label}: failed while invoking {methodName}():\n" + ex);
        }
    }

    // Enables/disables every Light component in the active scene. Returns how many
    // components actually changed state. Used by the enable->bake->disable->send cycle
    // (see the comments in Convert() / StartBake() / StartBakeUnity()); FindObjectsOfType
    // skips inactive GameObjects, which is fine — a light on an inactive object neither
    // bakes nor converts, so it needs no toggling.
    static int SetSceneLightsEnabled(bool enabled)
    {
        int changed = 0;
        foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
        {
            if (light == null || light.enabled == enabled)
                continue;
            light.enabled = enabled;
            changed++;
        }
        return changed;
    }

    // ------------------------------------------------------------------
    // poiyomi_smoketest — isolated, non-destructive check that PoiyomiBakeStandin's
    // SwapIn()/RestoreAll() actually swap and restore a real Poiyomi-Opaque material,
    // without touching build_scene/bake/convert or any real scene content. Spawns one
    // throwaway cube with a real Poiyomi shader (loaded from the actual shader asset,
    // not Shader.Find, since Poiyomi's internal shader name may not be registered until
    // a material referencing it has been imported/used), forces it Opaque via
    // _AlphaForceOpaque (confirmed as a real declared property on the shader — see
    // PoiyomiBlendModeComputer.FromPoiyomi's first branch), then destroys it again.
    // ------------------------------------------------------------------
    const string PoiyomiTestShaderPath = "Assets/_PoiyomiShaders/Shaders/8.0/Poiyomi.shader";

    static void PoiyomiSmokeTest()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(PoiyomiTestShaderPath);
        if (shader == null)
        {
            AppendResult($"poiyomi_smoketest: could not load shader at '{PoiyomiTestShaderPath}'. Aborting safely.");
            return;
        }

        var testMat = new Material(shader) { name = "PoiyomiSmokeTest_TempMaterial" };
        testMat.SetFloat("_AlphaForceOpaque", 1f); // forces PoiyomiBlendModeComputer.FromPoiyomi() -> Opaque
        string originalShaderName = testMat.shader.name;

        // Diagnostics: surface the exact gate inputs so a FAIL is debuggable from the result
        // log alone, without needing to attach a debugger.
        bool containsPoiyomiToken = originalShaderName.Contains(".poiyomi/");
        float alphaForceOpaqueReadback = testMat.GetFloat("_AlphaForceOpaque");
        var blendModeResult = PoiyomiBlendModeComputer.FromPoiyomi(testMat);
        AppendResult($"poiyomi_smoketest: diagnostics — shader='{originalShaderName}', " +
            $"Contains(\".poiyomi/\")={containsPoiyomiToken}, _AlphaForceOpaque readback={alphaForceOpaqueReadback}, " +
            $"PoiyomiBlendModeComputer.FromPoiyomi={blendModeResult} (expected Opaque).");

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "PoiyomiSmokeTest_TempCube";
        // No HideFlags.DontSave here: Object.FindObjectsOfType (which PoiyomiBakeStandin.
        // CollectCandidateRenderers relies on) excludes DontSave-flagged objects, which would
        // make this smoke test's temp cube invisible to the very code under test. Cleanup is
        // guaranteed unconditionally by the finally block below instead.
        var renderer = go.GetComponent<Renderer>();
        renderer.sharedMaterial = testMat;

        try
        {
            PoiyomiBakeStandin.SwapIn();
            string afterSwapName = renderer.sharedMaterial.shader != null ? renderer.sharedMaterial.shader.name : "(null shader)";
            bool swapOk = afterSwapName == "Standard";
            AppendResult(swapOk
                ? $"poiyomi_smoketest: PASS — SwapIn() replaced the Poiyomi material (shader was '{originalShaderName}') with a Standard standin."
                : $"poiyomi_smoketest: FAIL — after SwapIn() the renderer's shader is '{afterSwapName}', expected 'Standard'.");

            PoiyomiBakeStandin.RestoreAll();
            string afterRestoreName = renderer.sharedMaterial.shader != null ? renderer.sharedMaterial.shader.name : "(null shader)";
            bool restoreOk = afterRestoreName == originalShaderName;
            AppendResult(restoreOk
                ? $"poiyomi_smoketest: PASS — RestoreAll() restored the original Poiyomi material ('{afterRestoreName}')."
                : $"poiyomi_smoketest: FAIL — after RestoreAll() the renderer's shader is '{afterRestoreName}', expected '{originalShaderName}'.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(testMat);
        }

        AppendResult("poiyomi_smoketest: done (temp cube/material cleaned up; no scene file changes).");
    }

    // ------------------------------------------------------------------
    // shader_survey — read-only diagnostic. Groups every material currently on a
    // MeshRenderer/SkinnedMeshRenderer in the loaded scene(s) by shader name, and reports
    // which ones are lightmap-eligible today (shader.name == "Standard", per
    // LightmapMaterialCache.cs:180) vs. not. Answers "are there other non-Standard shaders
    // besides Poiyomi blocking baked lighting?" directly from the live scene instead of
    // guessing from .mat file greps (which can miss instanced/variant materials).
    // Never modifies anything — pure enumeration.
    // ------------------------------------------------------------------
    static void ShaderSurvey()
    {
        var countByShader = new Dictionary<string, int>();
        var materialNamesByShader = new Dictionary<string, List<string>>();

        void Tally(Renderer renderer)
        {
            foreach (var mat in renderer.sharedMaterials)
            {
                string shaderName = (mat != null && mat.shader != null) ? mat.shader.name : "(null shader / missing material)";
                countByShader.TryGetValue(shaderName, out int c);
                countByShader[shaderName] = c + 1;

                if (!materialNamesByShader.TryGetValue(shaderName, out var names))
                {
                    names = new List<string>();
                    materialNamesByShader[shaderName] = names;
                }
                if (mat != null && names.Count < 5 && !names.Contains(mat.name))
                    names.Add(mat.name);
            }
        }

        foreach (var mr in UnityEngine.Object.FindObjectsOfType<UnityEngine.MeshRenderer>())
            Tally(mr);
        foreach (var smr in UnityEngine.Object.FindObjectsOfType<UnityEngine.SkinnedMeshRenderer>())
            Tally(smr);

        var sb = new StringBuilder();
        sb.AppendLine("--- Shader Survey (read-only; groups every material slot in the loaded scene by shader) ---");
        sb.AppendLine($"Active scene: {SceneManager.GetActiveScene().path}");

        int totalSlots = 0;
        foreach (var kv in countByShader) totalSlots += kv.Value;
        sb.AppendLine($"Total material slots scanned: {totalSlots} across {countByShader.Count} distinct shader(s).");
        sb.AppendLine();

        foreach (var kv in countByShader.OrderByDescending(kv => kv.Value))
        {
            string shaderName = kv.Key;
            // Single source of truth: LightmapMaterialCache.StandardCompatibleShaderNames (internal,
            // ResoniteSDK). Reading it directly instead of duplicating the shader-name list here
            // means this label can never silently drift out of sync with the real eligibility check.
            bool eligible = LightmapMaterialCache.StandardCompatibleShaderNames.Contains(shaderName);
            string sampleNames = string.Join(", ", materialNamesByShader[shaderName]);
            sb.AppendLine($"  [{(eligible ? "ELIGIBLE" : "not eligible")}] {kv.Value,5} slot(s)  shader='{shaderName}'  e.g. {sampleNames}");
        }

        AppendResult(sb.ToString());
    }

    // ------------------------------------------------------------------
    // LightmapPipelineWindow support — thin, read-only accessors so the window can show
    // live status without duplicating any bake/convert logic (all of it stays here).
    // ------------------------------------------------------------------

    // ftRenderLightmap.bakeInProgress and Lightmapping.isRunning are both plain static bool
    // properties (no side effects, safe to poll every OnGUI/repaint) — see StartBake()'s and
    // StartBakeUnity()'s header comments for where each was confirmed. Without Bakery
    // installed, StartBake() never actually starts a bake (its #else stub just logs and
    // returns), so only the Unity-standard half of this OR ever needs to be true.
    public static bool IsBakeInProgress
    {
        get
        {
#if BAKERY_INCLUDED
            return ftRenderLightmap.bakeInProgress || Lightmapping.isRunning;
#else
            return Lightmapping.isRunning;
#endif
        }
    }

    public static string GetSdkConnectionStatusText()
    {
        var window = PickResoniteLinkWindow();

        if (window == null)
            return "SDK Manager not open";

        return $"ResoniteLinkWindow: {window.State}";
    }

    // Resolves the "live" ResoniteLinkWindow. FindObjectsOfTypeAll can return multiple
    // instances including hidden/undocked or stale-but-not-yet-GCed leftovers, and their
    // order is not meaningful — so a naive windows[0] often picks a Disconnected corpse
    // while the user's actually-connected session sits at another index. Prefer a Connected
    // instance; fall back to the first only when none are Connected (so "Disconnected"
    // status/messages still work).
    static ResoniteLinkWindow PickResoniteLinkWindow()
    {
        var windows = Resources.FindObjectsOfTypeAll<ResoniteLinkWindow>();
        if (windows == null || windows.Length == 0)
            return null;
        foreach (var w in windows)
        {
            if (w != null && w.State == ResoniteLinkWindow.ConnectionState.Connected)
                return w;
        }
        return windows[0];
    }

    // Tail-read window in bytes: the result log is append-only and monotonically growing
    // (AppendResult never truncates/rewrites), so the last N requested lines are always
    // near the end of the file. Reading only the last TailReadWindowBytes via
    // FileStream.Seek (instead of File.ReadAllLines, which pages in and allocates the
    // WHOLE file every call) keeps LightmapPipelineWindow's every-repaint polling cheap
    // even once the log has accumulated many Report() dumps across a long session.
    const int TailReadWindowBytes = 8192;

    static long _resultLogCacheLength = -1;
    static DateTime _resultLogCacheWriteTimeUtc;
    static int _resultLogCacheLineCount = -1;
    static string _resultLogCacheTail = "";

    public static string ReadResultLogTail(int lineCount = 12)
    {
        try
        {
            if (!File.Exists(ResultFilePath))
                return "(no result log yet)";

            var info = new FileInfo(ResultFilePath);
            long length = info.Length;
            DateTime writeTimeUtc = info.LastWriteTimeUtc;

            // Cache hit: file identical to last call (same size + same mtime) AND the
            // caller asked for the same number of lines as last time. LightmapPipelineWindow
            // repaints continuously while open (see its OnEnable/EditorApplication.update
            // hook) but the log file itself only actually changes when AppendResult() runs,
            // so this turns "read+parse every repaint" into "read+parse only when the log
            // actually grew".
            if (lineCount == _resultLogCacheLineCount && length == _resultLogCacheLength && writeTimeUtc == _resultLogCacheWriteTimeUtc)
                return _resultLogCacheTail;

            string tail = ReadTailLinesFromFile(ResultFilePath, length, lineCount);

            _resultLogCacheLength = length;
            _resultLogCacheWriteTimeUtc = writeTimeUtc;
            _resultLogCacheLineCount = lineCount;
            _resultLogCacheTail = tail;

            return tail;
        }
        catch (Exception ex)
        {
            return "(failed to read result log: " + ex.Message + ")";
        }
    }

    // Reads only the last min(fileLength, TailReadWindowBytes) bytes of the file and
    // returns its last `lineCount` newline-delimited lines. Note: if the requested
    // lineCount worth of content doesn't fit inside TailReadWindowBytes (e.g. right after
    // a very large single multi-line Report() dump), this can legitimately return fewer
    // than `lineCount` lines rather than reading further back — an accepted trade-off for
    // an interactive status panel over a test-harness log, not a general-purpose "tail -n".
    static string ReadTailLinesFromFile(string path, long fileLength, int lineCount)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            long readSize = Math.Min(fileLength, TailReadWindowBytes);
            fs.Seek(fileLength - readSize, SeekOrigin.Begin);

            var buffer = new byte[readSize];
            int totalRead = 0;
            while (totalRead < readSize)
            {
                int read = fs.Read(buffer, totalRead, (int)readSize - totalRead);
                if (read <= 0)
                    break;
                totalRead += read;
            }

            string chunk = Encoding.UTF8.GetString(buffer, 0, totalRead);

            // File.AppendAllText(path, text, Encoding.UTF8) writes a UTF-8 BOM as the very
            // first bytes of a brand new file (StreamWriter's default preamble-on-first-
            // write behavior) — File.ReadAllLines used to strip that automatically via its
            // own StreamReader; this raw byte read does not, so strip it explicitly. Only
            // relevant when we actually read starting at byte 0 of the file (small logs,
            // before TailReadWindowBytes worth of content has accumulated).
            bool readFromStart = readSize >= fileLength;
            if (readFromStart && chunk.Length > 0 && chunk[0] == '﻿')
                chunk = chunk.Substring(1);

            var rawLines = chunk.Split('\n');

            // If we didn't start at byte 0 of the file, the very first split fragment is
            // almost certainly a truncated mid-line remainder — drop it rather than show a
            // corrupted partial line.
            int startIndex = (!readFromStart && rawLines.Length > 1) ? 1 : 0;

            var lines = new List<string>();
            for (int i = startIndex; i < rawLines.Length; i++)
                lines.Add(rawLines[i].TrimEnd('\r'));

            // AppendResult() always ends each entry with '\n', so the file (and therefore
            // this chunk, when it reaches EOF) ends in '\n' too — Split('\n') on a
            // trailing '\n' produces one trailing empty string that isn't a real line.
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            int start = Math.Max(0, lines.Count - lineCount);
            return string.Join("\n", lines.Skip(start));
        }
    }
}
