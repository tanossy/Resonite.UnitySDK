// LightmapPipelineWindow.cs
//
// ライトマップパイプライン操作パネル。「品質プリセット選択→ベイク→自動送信」を
// ワンボタン化する便利UI。
//
// メニュー: "Resonite SDK/Lightmap Bake & Send"
//
// これは意図的に薄いUI層である。ベイク/変換のロジックは一切ここに置かず、すべて
// LightmapTestHarness.cs 側の public static メソッド/プロパティを呼ぶだけに徹する
// （ロジック重複禁止の要件）。可視性が必要だった箇所は LightmapTestHarness.cs 側の
// メソッドを public に上げて対応した（BuildScene / StartBake / StartBakeUnity /
// ConvertUnityLightsToBakeryLights / RunPipeline / IsBakeInProgress /
// GetSdkConnectionStatusText / ReadResultLogTail）。
//
// レイアウト（上から）:
//   Language / 言語   -> 言語セレクタ（最上部・常時操作可）。詳細は下記「言語切替」参照。
//   対象シーン (Target Scene) -> Popup2択（既定=現在開いているシーン / テスト部屋）。
//                          ダイダロス追加分・2026-07-12。詳細は下記「対象シーン選択」参照。
//                          DrawSceneTargetSelector() 参照。
//   Baker              -> トグル2個（Bakery / Unity Standard）。BakeryAvailable==false
//                          （BAKERY_INCLUDED未定義=Bakery非導入）のときは "Bakery" トグルを
//                          常に表示したまま無効化+ラベルを「Bakery（要導入）」に変更（隠さ
//                          ない — 買えば使えると分かるUXが要件）。DrawBakerSelector() 参照。
//   Quality Preset     -> low/mid/high/custom（1つだけ。customのときだけベイカー別の詳細
//                          数値フィールドを表示 — 元からある挙動、そのまま）
//   Lighting セクション -> Baker==UnityStandard のときだけ表示。つまみ5個＋実験的チェック
//                          ボックス1個（下記表）
//   送信時オプション   -> どちらのBakerでも常時表示。Force Refresh Generated Lightmaps /
//                          Send Tonemap Compensation（旧・Resonite SDKパネルにあった非vanilla
//                          トグル2個、2026-08-18移設）。DrawSendTimeOptionsSection()参照。
//   送信時ライト調整   -> どちらのBakerでも常時表示（ResoniteSDK/LightTuning.csの
//                          IntensityCeilingを直接編集。ベイク結果自体ではなく変換/送信時にのみ
//                          効く値のためBaker非依存。2026-08-27、WhiteBalanceShiftは不要と判断
//                          し撤去済み）。
//                          DrawSendTimeLightTuningSection()参照。
//   [Convert Lights]   -> Baker==Bakery のときだけ表示（Unity Standard時は非表示）
//   [Bake] [Bake & Send] の1列のみ
//   Debug / Cleanup    -> どちらのBakerでも常時表示。2026-08-26、ResoniteSDKDebugWindow.cs
//                          （メニュー "Resonite SDK/Open Debug Tools"）を統廃合してここへ移設
//                          （Tanossy指摘: "Lightmap Bake & SendとDebug toolが冗長すぎる。まとめて"）。
//                          Send Meshes/Materials/Lightmaps Only（部分送信）・Retry Missing
//                          Asset URLs・Log Messages JSONの4点。「Cleanup converters in the
//                          scene」「Cleanup Resonite Components in the scene」に加え、
//                          2026-08-27には「Reset conversion state」「Clear Generated Lightmap
//                          Variants」の2ボタンも撤去した（いつ押せばいいか利用者が判断しない
//                          といけないこと自体が複雑さの元、とのTanossy指摘。どちらも既に
//                          自動で走るため手動操作の出番が無い — 詳細はDrawDebugCleanupSection()
//                          参照）。
//   Status / Result Log
//
// つまみ5個は Baker==UnityStandard 専用（Bakeryは BakerySkyLight 等ambient設計が全く別物
// のため対象外 — LightmapTestHarness.BuildLights()の header comment 参照）。ボタンに
// つまみのシーン適用を内包する設計（「Apply」単独ボタンは存在しない）:
//
// つまみ対応表（すべて実ソース/ildasm確認済み。詳細は各フィールドのコメント参照）:
//   Shadow Strength     -> UnityEngine.Light.shadowStrength（shadows==NoneならSoftに強制）
//   Ambient Brightness  -> RenderSettings.ambientLight = Ambient Color * brightness
//   Ambient Color       -> 同上（ambientMode=Flat に固定）
//   Sun Color           -> UnityEngine.Light.color（主ディレクショナルライト）
//   Sun Angle           -> transform.rotation = Quaternion.Euler(elevation, azimuth, 0)
//
// ボタン対応表:
//   Convert Lights (Bakery選択時のみ表示) -> LightmapTestHarness.ConvertUnityLightsToBakeryLights()
//   Bake         -> Baker==UnityStandardならまずApplyTuningToScene(sun)でつまみ適用(Undo記録)
//                   -> LightmapTestHarness.StartBake(...) / StartBakeUnity(...)
//                   （ベイカー選択に応じてどちらか一方。auto-convertなし。どちらも
//                   useCurrentSceneInsteadOfTestRoom引数に「対象シーン」選択を渡す
//                   — RunBakeOnly()参照）
//   Bake & Send  -> 同様にApplyTuningToScene(sun)を先に適用 ->
//                   LightmapTestHarness.RunPipeline(quality, baker, bakeNormalDetail, useCurrentScene)
//                   （ベイク完了イベントで自動convertまで実行。harness既存のpipelineチェーン
//                   をそのまま使う — 独自のbakeCompleted購読はここでは持たない）
//
// --- ツールチップ（ダイダロス追加分）-------------------------------------------------
// 全コントロールを GUIContent(label, tooltip) 化してホバー説明を出す。文言は Tooltip*
// 定数にまとめてあり、指示された文言をそのまま使用（改変なし）。GUIContent受け取り
// オーバーロードの存在は UnityEditor.CoreModule.dll / UnityEngine.IMGUIModule.dll を
// ikdasmで逆アセンブルして実物確認済み（Popup/Slider/ColorField/Vector2Field/
// FloatField/IntField/LabelField は GUIContentオーバーロードあり、GUILayout.Toggle/
// Button も GUIContentオーバーロードあり）。
//
// --- 「法線を焼き込む」チェックボックス（実装範囲は正直に）---------------------------
// Lighting セクションに追加。OFF→ON切り替え時に EditorUtility.DisplayDialog で実験的機能
// である旨の確認を取る（Cancelならfalseに戻す）。
//
// ⚠ 現状の実装範囲（できる範囲／できない範囲を明記・2026-07-11 Step1+2実装後に更新）:
//   - できる範囲（実装済み）:
//     ・チェック状態の保持＋OFF→ON確認ダイアログ＋説明表示（本ファイル内で完結）。
//     ・Step 1: 委譲値が RunBakeOnly()/RunBakeAndSend() から
//       LightmapTestHarness.StartBakeUnity(..., bakeNormalDetail) / RunPipeline(..., bool) に
//       配線済み。ONのとき LightingSettings.directionalityMode = LightmapsMode.
//       CombinedDirectional（OFFのときは明示的に NonDirectional）でベイクされる。
//     ・Step 2: SDKフォーク側（Resonite.UnitySDK の LightmapMaterialCache /
//       DirectionalLightmapBaker、Assets/ResoniteSDK/ComponentConverters/Unity Core/
//       Rendering/ 配下）が、変換時に LightmapData.lightmapDir が非nullのレンダラーを検出
//       すると、メッシュの頂点法線をライトマップUV(UV2)空間にラスタライズした「法線パッチ」
//       と comp_dir/comp_color を UnityCG.cginc の DecodeDirectionalLightmap 式で合成した
//       per-objectテクスチャを生成し、既存の _BakedLightmap/SecondaryAlbedo 経路へ流し込む。
//       このウィンドウ自身はそのSDK側処理を一切呼ばない（ConvertはあくまでSendCurrentScene
//       経由・SDK側が焼かれたデータを見て自律的に分岐する設計）。
//   - できない範囲（未実装・「第2段階」として別タスク）:
//     ・タンジェント空間のノーマルマップ（法線テクスチャ）自体のサンプリングは行わない。
//       今回焼き込まれるのは「メッシュの頂点(ジオメトリ)法線」由来の陰影のみ — マテリアル
//       のノーマルマップの凹凸そのものはまだ反映されない。
//     ・UVアイランドの外側（パディング/ガター領域）の色にじみ対策（dilate）は未実装 —
//       境界にシームが出る可能性がある。
//   - 実機でのベイク結果・Resonite側の見た目は未検証（本セッションはコンパイル成立と
//     ロジックの正しさまで。実機確認はアテナ＋Tanossy）。
using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class LightmapPipelineWindow : EditorWindow
{
    // Whether the Bakery baker option should be selectable. Mirrors BAKERY_INCLUDED (see
    // BakeryPresenceDefine.cs, which owns that symbol) so this window can compile and run
    // identically with or without Bakery installed — the Bakery toggle button is always
    // SHOWN (never hidden), just disabled + relabeled when unavailable, per spec ("買えば
    // 使えると分かるUX"). LightBaker enum itself has no Bakery dependency (see its own
    // declaration in LightmapTestHarness.cs), so it's safe to keep both enum values
    // regardless of this constant.
#if BAKERY_INCLUDED
    const bool BakeryAvailable = true;
#else
    const bool BakeryAvailable = false;
#endif

    // --- 言語切替（ダイダロス追加分）----------------------------------------------------
    //
    // 全ユーザー可視文字列は L(ja, en) を経由する軽量ローカライズ（フルローカライズ基盤は
    // 導入しない、という指示どおり）。EditorPrefs（キー "ResoLightbake.Lang"）で永続化 —
    // GetString/SetString の存在は UnityEditor.CoreModule.dll を ikdasm で確認済み
    // (public hidebysig static)。既定値は初回起動時のみ Application.systemLanguage で判定
    // （SystemLanguage.JapaneseならJapanese、それ以外はEnglish — Application.systemLanguage
    // が public static get property であることも同ildasm確認内で確認済み）。以後は
    // ユーザーが選んだ言語を毎回そのまま復元する。
    enum UiLang { Japanese, English }

    const string LangPrefKey = "ResoLightbake.Lang";

    UiLang _lang = UiLang.Japanese;

    string L(string ja, string en) => _lang == UiLang.Japanese ? ja : en;

    // --- 対象シーン選択（ダイダロス追加分・2026-07-12）--------------------------------------
    //
    // 背景: 従来はBake/Bake&Send共通でLightmapTestHarness.EnsureTestSceneOpen()が問答無用で
    // Assets/LightmapTest.unityへ強制的に開き直していた。本番規模のワールド（実際に開いて
    // いる別シーン）でベイクしようとしても、テスト部屋へ引き戻されてしまう実害があったため
    // 選択式にする。既定値は CurrentOpenScene（実運用を主、回帰テストを従にする）— 言語設定
    // (LangPrefKey)と同じ EditorPrefs 文字列パターンで永続化。
    enum SceneTarget { CurrentOpenScene, TestRoom }

    const string SceneTargetPrefKey = "ResoLightbake.SceneTarget";

    SceneTarget _sceneTarget = SceneTarget.CurrentOpenScene;

    const string TooltipSceneTargetJA = "ベイク対象のシーン。「現在開いているシーン」=今Unityで開いているシーンをそのまま使う（切り替えない・実運用向け）。「テスト部屋」=専用のテストシーン(LightmapTest.unity)を強制的に開く（回帰確認用）";
    const string TooltipSceneTargetEN = "Which scene to bake. \"Current Open Scene\" uses whatever scene is open right now, without switching (for real work). \"Test Room\" force-opens the dedicated Assets/LightmapTest.unity test scene (for regression checks).";

    const string SceneTargetLabelJA = "対象シーン";
    const string SceneTargetLabelEN = "Target Scene";

    const string SceneTargetCurrentLabelJA = "現在開いているシーン";
    const string SceneTargetCurrentLabelEN = "Current Open Scene";

    const string SceneTargetTestRoomLabelJA = "テスト部屋 (LightmapTest.unity)";
    const string SceneTargetTestRoomLabelEN = "Test Room (LightmapTest.unity)";

    const string SceneTargetCurrentNoteJA = "「現在開いているシーン」を使用中: シーンは切り替えません。ベイク対象のシーンを先にUnityで開いておいてください。";
    const string SceneTargetCurrentNoteEN = "Using \"Current Open Scene\": no scene switch will happen. Make sure the scene you want to bake is already open in Unity.";

    static readonly string[] QualityLabelsJA = { "低 (Low)", "中 (Mid)", "高 (High)", "カスタム (Custom)" };
    static readonly string[] QualityLabelsEN = { "Low", "Mid", "High", "Custom" };

    static readonly string[] QualityKeys = { "low", "mid", "high", "custom" };

    // --- ツールチップ文言（指示どおり、改変なし）----------------------------------------
    const string TooltipBakerJA = "使用するライトベイカー。Unity Standard=Unity標準ライトマッパー。Bakery=別途Bakeryアセットが必要（未導入時はグレーアウト）";
    const string TooltipBakerEN = "Lightmap baker. Unity Standard = Unity's built-in lightmapper. Bakery = requires the Bakery asset (greyed out if not installed).";

    const string TooltipQualityPresetJA = "ベイク品質のプリセット（解像度・サンプル数）。customで手動指定";
    const string TooltipQualityPresetEN = "Bake quality preset (resolution & sample counts). Choose custom to set values manually.";

    const string TooltipLightmapResolutionJA = "ライトマップ解像度（texels/unit）。高いほど精細だが重い";
    const string TooltipLightmapResolutionEN = "Lightmap resolution (texels per unit). Higher = sharper but slower.";

    const string TooltipDirectSampleCountJA = "直接光のサンプル数。多いほど影が滑らか";
    const string TooltipDirectSampleCountEN = "Direct light samples. More = smoother shadows.";

    const string TooltipIndirectSampleCountJA = "間接光（GI）のサンプル数。多いほどノイズが減る";
    const string TooltipIndirectSampleCountEN = "Indirect (GI) samples. More = less noise.";

    const string TooltipAmbientBrightnessJA = "環境光の明るさ。低すぎると太陽の当たらない面が真っ黒に潰れる。0.1前後が目安";
    const string TooltipAmbientBrightnessEN = "Ambient light brightness. Too low turns surfaces the sun can't reach pure black. ~0.1 is a good start.";

    const string TooltipAmbientColorJA = "環境光の色味（空の色など）";
    const string TooltipAmbientColorEN = "Ambient light color (e.g. sky tint).";

    const string TooltipShadowStrengthJA = "焼き込む影の濃さ。1で最も濃い";
    const string TooltipShadowStrengthEN = "Darkness of baked shadows. 1 = darkest.";

    const string TooltipSunColorJA = "主ディレクショナルライトの色。暖色で陽だまり感";
    const string TooltipSunColorEN = "Color of the main directional light. Warm tones read as sunlight.";

    const string TooltipSunAngleJA = "太陽の仰角(X=Elevation)と方位(Y=Azimuth)。仰角を低くすると影が長く明瞭になる";
    const string TooltipSunAngleEN = "Sun elevation (X) and azimuth (Y). A lower elevation throws longer, clearer shadows.";

    const string TooltipBakeJA = "調整値をシーンに適用してからUnityでベイク（Resoniteには送らない）";
    const string TooltipBakeEN = "Apply the tuning values to the scene, then bake in Unity (does not send to Resonite).";

    const string TooltipBakeAndSendJA = "ベイク後、接続中のResoniteへ自動送信";
    const string TooltipBakeAndSendEN = "Bake, then automatically send to the connected Resonite session.";

    const string TooltipConvertLightsJA = "UnityライトをBakery用ライトに変換（Bakery経路専用）";
    const string TooltipConvertLightsEN = "Convert Unity lights to Bakery lights (Bakery path only).";

    // --- 「法線を焼き込む」チェックボックス文言（指示どおり、改変なし）------------------
    const string NormalBakeLabelJA = "法線を焼き込む（実験的）";
    const string NormalBakeLabelEN = "Bake normal detail into lightmap (experimental)";

    const string NormalBakeHelpJA = "Resoniteは方向ライトマップ(directional lightmap)非対応のため、通常はノーマルマップの凹凸が焼き光に反応する表現が平坦化する。ONにすると、ベイク段階でその法線陰影をカラーライトマップに焼き付けて近似する。制約: 静的固定（ランタイムで光や法線を動かすと破綻）。実験的機能・要実機検証。";
    const string NormalBakeHelpEN = "Resonite has no directional-lightmap support, so normal-map detail that would react to baked light direction is normally flattened. When on, that normal shading is baked into the color lightmap as an approximation. Trade-off: static (breaks if lights/normals move at runtime). Experimental — verify on the real machine.";

    const string NormalBakeDialogTitleJA = "実験的機能";
    const string NormalBakeDialogTitleEN = "Experimental feature";

    const string NormalBakeDialogMessageJA = "『法線を焼き込む』は実験的機能です。\nResoniteは方向ライトマップ非対応のため、ベイク段階で法線の陰影を色ライトマップに焼き付けて近似します。\n・静的に固定されます（ランタイムで光や法線を動かすと破綻）\n・実機での見え方は要検証です\n有効にしますか？";
    const string NormalBakeDialogMessageEN = "\"Bake normal detail into lightmap\" is experimental.\nResonite has no directional-lightmap support, so the normal shading is baked into the color lightmap as an approximation at bake time.\n- The result is static (breaks if lights or normals move at runtime)\n- Appearance must be verified on the real machine\nEnable it?";

    const string NormalBakeDialogOkJA = "有効にする";
    const string NormalBakeDialogOkEN = "Enable";

    const string NormalBakeDialogCancelJA = "やめる";
    const string NormalBakeDialogCancelEN = "Cancel";

    // --- その他のHelpBox/注記文言 --------------------------------------------------------
    const string BakeryUnavailableNoteJA = "Bakeryを導入すると選択可能になります。";
    const string BakeryUnavailableNoteEN = "Install Bakery to enable this option.";

    const string NoSunWarningJA = "シーン内に主ディレクショナルライトが見つかりません。Sun系のコントロールは無効です。";
    const string NoSunWarningEN = "No directional light found in the scene; Sun controls are disabled.";

    LightBaker _baker = LightBaker.Bakery;
    string _qualityKey = "mid";

    // Custom-mode raw values, one set per backend (only the relevant set is shown/used).
    float _customBakeryTexelsPerUnit = 20f;
    int _customBakeryGiSamples = 16;
    float _customUnityLightmapResolution = 20f;
    int _customUnityDirectSampleCount = 32;
    int _customUnityIndirectSampleCount = 128;

    Vector2 _logScroll;

    // --- Lighting section state (Unity standard baker only) ----------------------------
    //
    // Only ever drawn/applied when _baker == LightBaker.UnityStandard (Bakery has its own
    // completely separate ambient model via BakerySkyLight and never reads
    // RenderSettings.ambient* — see LightmapTestHarness.BuildLights()'s header comment).
    // Deliberately shares the single "Quality Preset" dropdown above instead of having its
    // own — the preset only ever controls sample count/resolution, which is completely
    // orthogonal to these art-direction knobs.
    //
    // Defaults match the spec exactly (Shadow Strength 1.0 / Ambient Brightness 0.12 /
    // Ambient Color near-white / Sun Color warm / Sun Angle 35°el, -30°az — the last two
    // match LightmapTestHarness.BuildLights()'s own Sun_Directional values, so leaving
    // every slider untouched reproduces the already-verified shadow-bearing test bake).
    float _shadowStrength = 1.0f;
    float _ambientBrightness = 0.12f;
    Color _ambientColor = new Color(1f, 1f, 1.06f);
    Color _sunColor = new Color(1f, 0.85f, 0.62f);
    // x = elevation (deg), y = azimuth (deg) — fed straight into Quaternion.Euler(x, y, 0)
    // in ApplyTuningToScene(), same convention LightmapTestHarness.BuildLights() uses for
    // its own Sun_Directional (`Quaternion.Euler(35f, -30f, 0f)`).
    Vector2 _sunAngle = new Vector2(35f, -30f);

    // "Bake normal detail into lightmap (experimental)" — see the header comment block's
    // "できる範囲／できない範囲" note. Committed value only ever changes via
    // DrawLightingSection()'s OFF->ON confirmation-dialog gate below; comparing this
    // (last-committed) value against the Toggle's return value each frame is what detects
    // the OFF->ON transition, so no separate "previous value" field is needed.
    bool _bakeNormalDetail;

    // --- Send-Time Light Tuning section state -------------------------------------------
    //
    // Unlike the Lighting section above (Unity Standard baker only, affects the *baked*
    // result), these mirror ResoniteSDK/LightTuning.cs's static tuning fields, which are
    // applied at conversion/send time in LightConverter.cs regardless of which baker
    // produced the lightmap — drawn unconditionally (not gated on _baker) for that reason.
    // Kept as this window's own fields (synced into LightTuning's statics every OnGUI, same
    // pattern DrawLightingSection() already uses for its own knobs) so the values are
    // editable from this panel instead of requiring a code edit.
    // 2026-08-27 (per Tanossy's feedback: "white balance isn't needed"): removed
    // _lightWhiteBalanceShift and its slider along with LightTuning.WhiteBalanceShift itself
    // (see that file). Only the intensity knob remains below.
    float _lightIntensityCeiling = 0.9f;

    const string SendTimeTuningHeaderJA = "送信時ライト調整";
    const string SendTimeTuningHeaderEN = "Send-Time Light Tuning";

    const string TooltipIntensityCeilingJA = "シーン内で最も明るいライトが送信時にこの値になるよう、全ライトへ同じ比率で倍率を逆算する（固定倍率ではなくシーンごとに自動正規化）";
    const string TooltipIntensityCeilingEN = "Target intensity for the scene's single brightest light at send time; every light is scaled by the same ratio needed to put the brightest one exactly here (self-normalizing per scene, not a fixed multiplier).";

    // --- Baked Lightmap Exposure section state --------------------------------------------
    //
    // 2026-08-18: mirrors ResoniteSDK/LightmapDecoder.cs's static RangeScale/
    // ColorSaturationCompensation fields — the exposure boost and desaturation applied to the
    // *baked lightmap texture itself* before it multiplies onto material color (see that file's
    // own doc comments for the "why" and the multiply-vs-additive history). Distinct from
    // Send-Time Light Tuning above, which scales the scene's live Unity Light components, not
    // the baked texture — a room with no baked lightmap on it is untouched by this section.
    // Both fields were tuned against one specific scene's bake data; a new room's bake can be
    // darker or brighter, so these need to be re-checked per room rather than assumed correct
    // (this is the whole reason they're a slider here instead of only a code constant).
    float _bakedLightmapRangeScale = 1.1f;
    float _bakedLightmapSaturation = 0.6f;

    const string BakedLightmapExposureHeaderJA = "ベイクライトマップ露出";
    const string BakedLightmapExposureHeaderEN = "Baked Lightmap Exposure";

    const string TooltipRangeScaleJA = "ベイクライトマップの明るさブースト（マテリアル色への乗算前に適用）。暗い部屋ほど高い値が要る。シーンごとに焼きデータの明るさが違うため、部屋を変えたら要調整";
    const string TooltipRangeScaleEN = "Brightness boost applied to the baked lightmap before it multiplies onto material color. Darker bakes need a higher value; re-check this whenever you switch to a different room, since bake brightness varies per scene.";

    const string TooltipBakedSaturationJA = "ベイクライトマップの彩度（1=元の色のまま、0=完全に無彩色）。暖色ライトで焼かれたデータが部屋全体を色被りさせるのを抑える";
    const string TooltipBakedSaturationEN = "Saturation of the baked lightmap itself (1 = unchanged, 0 = fully desaturated). Tames a warm-colored bake tinting the whole room.";

    // --- Send-Time Options section state --------------------------------------------------
    //
    // 2026-08-18: moved here from the main Resonite SDK Manager panel (ResoniteLinkWindow.cs)
    // per Tanossy's feedback — "Convert Skybox and the rest weren't originally there either,
    // right? Don't dirty ResoniteSDK." Checked against upstream Yellow-Dog-Man/Resonite.UnitySDK
    // directly: Convert Skybox IS vanilla (left in place on the main panel), but these two are
    // genuine overlay additions (both from the 2026-08-08 tonemap-compensation commit), so they
    // move here. Same drawn-unconditionally rationale as Send-Time Light Tuning above (both take
    // effect at conversion/send time, regardless of which baker produced the lightmap). Backing
    // state lives in ConversionPassState.ForceRefreshGeneratedLightmaps / ToneMapCompensationState
    // .Enabled — this window just mirrors the checkbox into those statics every OnGUI, same as
    // DrawSendTimeLightTuningSection() does for LightTuning's statics.
    bool _forceRefreshGeneratedLightmaps = true;
    bool _sendToneMapCompensation = true;

    const string SendTimeOptionsHeaderJA = "送信時オプション";
    const string SendTimeOptionsHeaderEN = "Send-Time Options";

    const string TooltipForceRefreshJA = "生成済みライトマップ差分ファイルを毎回強制再生成する";
    const string TooltipForceRefreshEN = "Force-regenerate the generated lightmap variant files on every send.";

    const string TooltipToneMapCompensationJA = "マテリアル色とReflection Probe強度へトーンマップ近似補正を掛ける（実験的）";
    const string TooltipToneMapCompensationEN = "Apply a tonemap approximation to material colors and Reflection Probe intensity (experimental).";

    // --- Debug / Cleanup section state ----------------------------------------------------
    //
    // 2026-08-26: folded in from the now-deleted ResoniteSDKDebugWindow.cs (menu: "Resonite
    // SDK/Open Debug Tools") per Tanossy's feedback that having a separate Debug Tools window
    // alongside this one was redundant. Everything here is a thin call into
    // LightmapTestHarness.cs's public static members (RetryMissingAssetURLs() /
    // LogMessageJSON), same no-logic-in-the-GUI-layer rule the rest of this file follows.
    //
    // "Send Meshes/Materials Only" buttons already existed lower in this file before this
    // section was added; "Send Lightmaps Only" is the new third button that fills the gap
    // (LightmapTestHarness.ConvertLightmapsOnly() already existed as a receiver, it just had
    // no button anywhere - see LightmapTestHarness.cs's file-driven "lightmaps_only" command).
    //
    // Deliberately NOT included here: "Cleanup converters in the scene" and "Cleanup Resonite
    // Components in the scene" (see ResoniteLinkWindow.ResetConversionState()'s own comment -
    // both are subsumed/redundant with what that method already does internally). 2026-08-27:
    // also removed the "Reset Conversion State" and "Clear Generated Lightmap Variants"
    // buttons that used to be here, for the same reason Tanossy raised about the two above -
    // a button only worth having if the user can tell when it's their job to press it, and
    // neither one actually needed a human to decide that:
    //   - Reset Conversion State: ResoniteLinkWindow.EnsureConverter() already resets whenever
    //     UniqueSessionId/Port changes OR _converter.IsCorrupted is set, and IsCorrupted is
    //     exactly what a timeout or mid-send disconnect sets (SceneConverter.cs's catch
    //     blocks) - so pressing any Send button again already self-heals with no extra step.
    //   - Clear Generated Lightmap Variants: SceneConverter.ConvertScene() already calls
    //     LightmapMaterialCache.ClearGeneratedLightmapVariants() automatically whenever the
    //     "Force Refresh Generated Lightmaps" toggle (Send-Time Options section, above) is on -
    //     that toggle is the discoverable, one-click equivalent.
    const string DebugCleanupHeaderJA = "デバッグ / クリーンアップ";
    const string DebugCleanupHeaderEN = "Debug / Cleanup";

    const string TooltipSendLightmapsOnlyJA = "ライトマップのみ送信テスト（メッシュ/マテリアルは変更しない）";
    const string TooltipSendLightmapsOnlyEN = "Send only lightmap updates for testing; meshes and materials are left untouched.";

    // 2026-08-27 (per Tanossy's feedback: "there's a button that isn't localized"): these two
    // predate this section's L(ja, en) convention (they moved here verbatim from the deleted
    // ResoniteSDKDebugWindow.cs, which never localized anything) and were missed when this
    // section was written. Send Lightmaps Only above was new at that point so it got the
    // convention from the start; these two didn't.
    const string TooltipSendMeshesOnlyJA = "メッシュのみ送信テスト（マテリアルは変更しない）";
    const string TooltipSendMeshesOnlyEN = "Send only mesh asset/provider updates; material slots are left untouched.";

    const string TooltipSendMaterialsOnlyJA = "マテリアルのみ送信テスト（メッシュは変更しない）";
    const string TooltipSendMaterialsOnlyEN = "Send only material and texture updates; mesh providers are left untouched.";

    const string TooltipRetryMissingAssetURLsJA = "URLが確定していない（送信済みだが未解決の）アセットへの送信を再試行する";
    const string TooltipRetryMissingAssetURLsEN = "Retry sending assets that were sent but never received a resolved URL.";

    const string TooltipLogMessageJSONJA = "ResoniteLinkの送受信メッセージをJSONとしてログへ出力する（デバッグ用）";
    const string TooltipLogMessageJSONEN = "Log ResoniteLink send/receive messages as JSON (for debugging).";

    // Cached via reflection the first time it's needed; see GetRenderSettingsObjectForUndo().
    static MethodInfo _getRenderSettingsMethod;

    [MenuItem("Resonite SDK/Lightmap Bake & Send")]
    static void ShowWindow()
    {
        var window = GetWindow<LightmapPipelineWindow>("Lightmap Bake & Send");
        window.minSize = new Vector2(360f, 420f);
    }

    void OnEnable()
    {
        // Bake progress and the SDK connection state both change outside of any GUI event
        // this window itself raises (background bake threads, a separately-opened SDK
        // Manager window), so repaint continuously while this window is open to keep the
        // status section live.
        EditorApplication.update += Repaint;

        // Safety net for the field initializer default (`_baker = LightBaker.Bakery`) on a
        // machine without Bakery installed — corrected again defensively in OnGUI() below
        // in case _baker ever ends up Bakery again some other way (e.g. a stale
        // EditorWindow instance surviving a domain reload that happened between Bakery
        // being installed and removed).
        if (!BakeryAvailable && _baker == LightBaker.Bakery)
            _baker = LightBaker.UnityStandard;

        LoadLanguagePref();
        LoadSceneTargetPref();
    }

    // Reads the persisted language choice; on first-ever launch (no pref saved yet) falls
    // back to Application.systemLanguage. Split out from OnEnable so it only runs the
    // EditorPrefs/Application lookups once per window-open, not every OnGUI call.
    void LoadLanguagePref()
    {
        string saved = EditorPrefs.GetString(LangPrefKey, "");

        if (saved == "EN")
        {
            _lang = UiLang.English;
        }
        else if (saved == "JA")
        {
            _lang = UiLang.Japanese;
        }
        else
        {
            _lang = Application.systemLanguage == SystemLanguage.Japanese ? UiLang.Japanese : UiLang.English;
        }
    }

    // Reads the persisted scene-target choice. Unlike LoadLanguagePref(), an unset/unknown
    // pref value (first-ever launch, or a value written by some future third option) falls
    // back to SceneTarget.CurrentOpenScene — the deliberate new default (see the field-block
    // comment above) — rather than to the old TestRoom-forcing behavior.
    void LoadSceneTargetPref()
    {
        string saved = EditorPrefs.GetString(SceneTargetPrefKey, "");
        _sceneTarget = saved == "TESTROOM" ? SceneTarget.TestRoom : SceneTarget.CurrentOpenScene;
    }

    void OnDisable()
    {
        EditorApplication.update -= Repaint;
    }

    void OnGUI()
    {
        bool baking = LightmapTestHarness.IsBakeInProgress;

        // Language selector stays interactive even mid-bake (GUI.enabled is set to
        // !baking further below, deliberately AFTER this) — there's no reason switching
        // the panel's display language should be blocked just because a bake happens to
        // be running.
        DrawLanguageSelector();
        EditorGUILayout.Space();

        // Scene target selector drawn first (before Baker/Quality): deciding WHICH scene to
        // bake is the first, most consequential choice in this panel, so it should not be
        // buried under baker/quality settings that are meaningless without it. Disabled
        // (unlike the language selector above) while a bake is running, same as everything
        // below it — GUI.enabled is reset to !baking again just below for the rest of the
        // panel, this line only needs to cover this one section.
        GUI.enabled = !baking;
        DrawSceneTargetSelector();
        EditorGUILayout.Space();

        if (!BakeryAvailable && _baker == LightBaker.Bakery)
            _baker = LightBaker.UnityStandard;

        GUI.enabled = !baking;

        EditorGUILayout.LabelField(new GUIContent(L("ベイカー", "Baker"), L(TooltipBakerJA, TooltipBakerEN)), EditorStyles.boldLabel);
        DrawBakerSelector(baking);

        if (!BakeryAvailable)
            EditorGUILayout.HelpBox(L(BakeryUnavailableNoteJA, BakeryUnavailableNoteEN), MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(new GUIContent(L("品質プリセット", "Quality Preset"), L(TooltipQualityPresetJA, TooltipQualityPresetEN)), EditorStyles.boldLabel);
        int selectedIndex = Array.IndexOf(QualityKeys, _qualityKey);
        if (selectedIndex < 0) selectedIndex = 1; // "mid"
        var qualityLabels = _lang == UiLang.Japanese ? QualityLabelsJA : QualityLabelsEN;
        selectedIndex = EditorGUILayout.Popup(selectedIndex, qualityLabels);
        _qualityKey = QualityKeys[selectedIndex];

        if (_qualityKey == "custom")
        {
            EditorGUI.indentLevel++;
            if (_baker == LightBaker.Bakery)
            {
                _customBakeryTexelsPerUnit = EditorGUILayout.FloatField(L("テクセル/ユニット", "Texels Per Unit"), _customBakeryTexelsPerUnit);
                _customBakeryGiSamples = EditorGUILayout.IntField(L("GIサンプル数", "GI Samples"), _customBakeryGiSamples);
            }
            else
            {
                _customUnityLightmapResolution = EditorGUILayout.FloatField(
                    new GUIContent(L("ライトマップ解像度", "Lightmap Resolution"), L(TooltipLightmapResolutionJA, TooltipLightmapResolutionEN)),
                    _customUnityLightmapResolution);
                _customUnityDirectSampleCount = EditorGUILayout.IntField(
                    new GUIContent(L("直接光サンプル数", "Direct Sample Count"), L(TooltipDirectSampleCountJA, TooltipDirectSampleCountEN)),
                    _customUnityDirectSampleCount);
                _customUnityIndirectSampleCount = EditorGUILayout.IntField(
                    new GUIContent(L("間接光サンプル数", "Indirect Sample Count"), L(TooltipIndirectSampleCountJA, TooltipIndirectSampleCountEN)),
                    _customUnityIndirectSampleCount);
            }
            EditorGUI.indentLevel--;
        }

        // Only meaningful for the Unity standard baker (see the field-block comment above
        // Lighting section state) — sun stays null for Bakery, and RunBakeOnly()/
        // RunBakeAndSend() below only ever call ApplyTuningToScene(sun) when
        // _baker == LightBaker.UnityStandard, so a null sun here never matters for Bakery.
        Light sun = null;
        if (_baker == LightBaker.UnityStandard)
        {
            EditorGUILayout.Space();
            sun = DrawLightingSection(baking);
        }

        EditorGUILayout.Space();
        DrawSendTimeOptionsSection(baking);

        EditorGUILayout.Space();
        DrawSendTimeLightTuningSection(baking);

        EditorGUILayout.Space();
        DrawBakedLightmapExposureSection(baking);

        EditorGUILayout.Space();

        EditorGUILayout.BeginHorizontal();

        // "Convert Lights" only makes sense (and is only wired up) for the Bakery backend —
        // Unity standard bakes UnityEngine.Light components directly, no Bakery-native
        // counterpart is ever created or needed. Hidden entirely (not just disabled) for
        // Unity Standard per spec, to avoid a dead/no-op button cluttering the row.
        if (_baker == LightBaker.Bakery)
        {
            GUI.enabled = !baking;
            if (GUILayout.Button(new GUIContent(L("ライトを変換", "Convert Lights"), L(TooltipConvertLightsJA, TooltipConvertLightsEN))))
                LightmapTestHarness.ConvertUnityLightsToBakeryLights();
        }

        GUI.enabled = !baking;
        if (GUILayout.Button(new GUIContent(L("ベイク", "Bake"), L(TooltipBakeJA, TooltipBakeEN))))
            RunBakeOnly(sun);

        if (GUILayout.Button(new GUIContent(L("ベイク＆送信", "Bake & Send"), L(TooltipBakeAndSendJA, TooltipBakeAndSendEN))))
            RunBakeAndSend(sun);

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        DrawDebugCleanupSection(baking);

        GUI.enabled = true;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(L("ステータス", "Status"), EditorStyles.boldLabel);
        EditorGUILayout.LabelField(L("ベイク: ", "Bake: ") + (baking ? L("実行中...", "in progress...") : L("待機中", "idle")));
        EditorGUILayout.LabelField(L("SDK接続: ", "SDK: ") + LightmapTestHarness.GetSdkConnectionStatusText());

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(L("結果ログ（末尾）", "Result Log (tail)"), EditorStyles.boldLabel);
        _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(220));
        EditorGUILayout.TextArea(LightmapTestHarness.ReadResultLogTail(20), GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // Small Popup at the very top of the panel. Options are always shown in BOTH languages
    // ("日本語" / "English") regardless of the currently selected language, since this is
    // the selector itself — translating its own option labels into "whichever language is
    // currently active" would make it impossible to switch back once on the wrong one.
    void DrawLanguageSelector()
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Language / 言語", GUILayout.Width(100));

        int langIndex = _lang == UiLang.Japanese ? 0 : 1;
        int newLangIndex = EditorGUILayout.Popup(langIndex, new[] { "日本語", "English" });

        EditorGUILayout.EndHorizontal();

        if (newLangIndex != langIndex)
        {
            _lang = newLangIndex == 0 ? UiLang.Japanese : UiLang.English;
            EditorPrefs.SetString(LangPrefKey, _lang == UiLang.Japanese ? "JA" : "EN");
        }
    }

    // Popup with exactly 2 choices (see the SceneTarget field-block comment for the "why").
    // Same "bold LabelField (with tooltip) above a plain, no-reserved-label Popup below" idiom
    // OnGUI() already uses for the Quality Preset dropdown just below this section — kept
    // identical for visual consistency rather than inventing a second popup layout style.
    void DrawSceneTargetSelector()
    {
        EditorGUILayout.LabelField(
            new GUIContent(L(SceneTargetLabelJA, SceneTargetLabelEN), L(TooltipSceneTargetJA, TooltipSceneTargetEN)),
            EditorStyles.boldLabel);

        string[] labels = {
            L(SceneTargetCurrentLabelJA, SceneTargetCurrentLabelEN),
            L(SceneTargetTestRoomLabelJA, SceneTargetTestRoomLabelEN),
        };

        int index = _sceneTarget == SceneTarget.TestRoom ? 1 : 0;
        int newIndex = EditorGUILayout.Popup(index, labels);

        if (newIndex != index)
        {
            _sceneTarget = newIndex == 1 ? SceneTarget.TestRoom : SceneTarget.CurrentOpenScene;
            EditorPrefs.SetString(SceneTargetPrefKey, _sceneTarget == SceneTarget.TestRoom ? "TESTROOM" : "CURRENT");
        }

        if (_sceneTarget == SceneTarget.CurrentOpenScene)
            EditorGUILayout.HelpBox(L(SceneTargetCurrentNoteJA, SceneTargetCurrentNoteEN), MessageType.Info);
    }

    // Two exclusive toggle buttons standing in for what used to be a single
    // EditorGUILayout.EnumPopup(_baker) — a Popup/EnumPopup has no way to disable one item
    // while leaving the others selectable, and the spec requires the Bakery option to
    // always be VISIBLE (never hidden) but grayed out + relabeled when unavailable, so a
    // dropdown can't satisfy this on its own. The exclusive-toggle-pair idiom below (each
    // GUILayout.Toggle only flips _baker when the OTHER one was just turned on) is the
    // standard immediate-mode pattern for a 2-option radio group; clicking the
    // already-selected button harmlessly returns false for one frame with no visible
    // effect (immediate mode redraws before it would ever be perceived).
    void DrawBakerSelector(bool baking)
    {
        EditorGUILayout.BeginHorizontal();

        string bakeryLabel = BakeryAvailable ? "Bakery" : "Bakery" + L("（要導入）", " (not installed)");

        GUI.enabled = !baking && BakeryAvailable;
        bool bakeryClicked = GUILayout.Toggle(
            _baker == LightBaker.Bakery,
            new GUIContent(bakeryLabel, L(TooltipBakerJA, TooltipBakerEN)),
            EditorStyles.miniButtonLeft);

        GUI.enabled = !baking;
        bool unityClicked = GUILayout.Toggle(
            _baker == LightBaker.UnityStandard,
            new GUIContent("Unity Standard", L(TooltipBakerJA, TooltipBakerEN)),
            EditorStyles.miniButtonRight);

        if (BakeryAvailable && bakeryClicked && _baker != LightBaker.Bakery)
            _baker = LightBaker.Bakery;
        else if (unityClicked && _baker != LightBaker.UnityStandard)
            _baker = LightBaker.UnityStandard;

        EditorGUILayout.EndHorizontal();

        GUI.enabled = !baking;
    }

    // ------------------------------------------------------------------
    // Lighting section (Unity standard baker only) — see the header comment block for the
    // full knob mapping table. Only ever called from OnGUI() when _baker ==
    // LightBaker.UnityStandard. Draws the 5 knobs + the experimental normal-bake checkbox;
    // no separate "Apply" button here — "Bake"/"Bake & Send" below apply these values via
    // ApplyTuningToScene(sun) as their own first step (see RunBakeOnly()/RunBakeAndSend()).
    // ------------------------------------------------------------------

    Light DrawLightingSection(bool baking)
    {
        EditorGUILayout.LabelField(L("ライティング", "Lighting"), EditorStyles.boldLabel);

        // Looked up fresh every OnGUI call (not cached across calls) so the panel always
        // reflects whichever scene is currently open/active — this section is explicitly
        // NOT test-room-specific (spec requirement). FindObjectsOfType<Light>() over a
        // typical scene's light count is cheap; this window already repaints continuously
        // (see OnEnable's EditorApplication.update hook) so re-resolving here costs nothing
        // extra beyond what the window already pays for every other live status field.
        Light sun = FindMainDirectionalLight();
        bool hasSun = sun != null;

        GUI.enabled = !baking;
        _ambientBrightness = EditorGUILayout.Slider(
            new GUIContent(L("環境光の明るさ", "Ambient Brightness"), L(TooltipAmbientBrightnessJA, TooltipAmbientBrightnessEN)),
            _ambientBrightness, 0f, 1f);
        _ambientColor = EditorGUILayout.ColorField(
            new GUIContent(L("環境光の色", "Ambient Color"), L(TooltipAmbientColorJA, TooltipAmbientColorEN)),
            _ambientColor);

        if (!hasSun)
            EditorGUILayout.HelpBox(L(NoSunWarningJA, NoSunWarningEN), MessageType.Info);

        GUI.enabled = !baking && hasSun;
        _shadowStrength = EditorGUILayout.Slider(
            new GUIContent(L("影の濃さ", "Shadow Strength"), L(TooltipShadowStrengthJA, TooltipShadowStrengthEN)),
            _shadowStrength, 0f, 1f);
        _sunColor = EditorGUILayout.ColorField(
            new GUIContent(L("太陽光の色", "Sun Color"), L(TooltipSunColorJA, TooltipSunColorEN)),
            _sunColor);
        _sunAngle = EditorGUILayout.Vector2Field(
            new GUIContent(L("太陽角度（仰角, 方位）", "Sun Angle (Elevation, Azimuth)"), L(TooltipSunAngleJA, TooltipSunAngleEN)),
            _sunAngle);

        GUI.enabled = !baking;

        EditorGUILayout.Space();
        DrawNormalBakeToggle();

        return sun;
    }

    // ------------------------------------------------------------------
    // Send-Time Options section — see the field-block comment above (near
    // _forceRefreshGeneratedLightmaps/_sendToneMapCompensation) for why these moved here from
    // the main SDK panel. Drawn unconditionally (both bakers), same rationale as Send-Time Light
    // Tuning below.
    // ------------------------------------------------------------------

    void DrawSendTimeOptionsSection(bool baking)
    {
        EditorGUILayout.LabelField(L(SendTimeOptionsHeaderJA, SendTimeOptionsHeaderEN), EditorStyles.boldLabel);

        GUI.enabled = !baking;

        // 2026-08-27 (per Tanossy's feedback: "the checkbox is overlapping the label text, move
        // it further right"): EditorGUILayout.Toggle draws its checkbox glyph starting at
        // EditorGUIUtility.labelWidth from the left edge, not after however long the label text
        // actually is - Unity's default labelWidth (~150px) isn't wide enough for the longer
        // Japanese labels below, so the checkbox ends up drawn on top of the tail of the text.
        // Widened just for this section's two toggles and restored immediately after, so it
        // doesn't affect any other section's layout.
        float previousLabelWidth = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 240f;

        _forceRefreshGeneratedLightmaps = EditorGUILayout.Toggle(
            new GUIContent(L("生成済みライトマップを強制再生成", "Force Refresh Generated Lightmaps"), L(TooltipForceRefreshJA, TooltipForceRefreshEN)),
            _forceRefreshGeneratedLightmaps);

        _sendToneMapCompensation = EditorGUILayout.Toggle(
            new GUIContent(L("トーンマップ補正を送信（実験的）", "Send Tonemap Compensation (experimental)"), L(TooltipToneMapCompensationJA, TooltipToneMapCompensationEN)),
            _sendToneMapCompensation);

        EditorGUIUtility.labelWidth = previousLabelWidth;

        ConversionPassState.ForceRefreshGeneratedLightmaps = _forceRefreshGeneratedLightmaps;
        ToneMapCompensationState.Enabled = _sendToneMapCompensation;
    }

    // ------------------------------------------------------------------
    // Send-Time Light Tuning section — see the field-block comment above (near
    // _lightIntensityCeiling) for why this is drawn unconditionally (both bakers) rather than
    // only for LightBaker.UnityStandard like DrawLightingSection(). No "Apply" button here
    // either — the value is synced straight into LightTuning's static field on every OnGUI
    // call, so it takes effect on whatever the next Send Current Scene/Bake & Send actually
    // does, without needing a scene edit first.
    // ------------------------------------------------------------------

    // Default the "Reset to Defaults" button below restores - kept as a named constant (rather
    // than repeating the field initializer's literal inline at the button call site) so the
    // two stay in sync if the default is ever re-tuned again.
    const float DefaultLightIntensityCeiling = 0.9f;

    void DrawSendTimeLightTuningSection(bool baking)
    {
        EditorGUILayout.LabelField(L(SendTimeTuningHeaderJA, SendTimeTuningHeaderEN), EditorStyles.boldLabel);

        GUI.enabled = !baking;

        _lightIntensityCeiling = EditorGUILayout.Slider(
            new GUIContent(L("明るさの上限", "Intensity Ceiling"), L(TooltipIntensityCeilingJA, TooltipIntensityCeilingEN)),
            _lightIntensityCeiling, 0.1f, 3f);

        // 2026-08-27 (per Tanossy's feedback: "a reset button for the sliders is needed"):
        // restores the slider to the tuned default above, not to some neutral/no-op value -
        // it was arrived at through several rounds of live-tuning earlier (see LightTuning.cs's
        // own field comments), so "reset" here means "back to the last known good value", the
        // same way undoing an accidental drag would.
        if (GUILayout.Button(new GUIContent(L("既定値に戻す", "Reset to Defaults"), L(
            "明るさの上限を、実機で調整済みの既定値に戻す",
            "Restores Intensity Ceiling to its real-machine-tuned default value."))))
        {
            _lightIntensityCeiling = DefaultLightIntensityCeiling;
        }

        LightTuning.IntensityCeiling = _lightIntensityCeiling;
    }

    // ------------------------------------------------------------------
    // Baked Lightmap Exposure section — see the field-block comment above (near
    // _bakedLightmapRangeScale/_bakedLightmapSaturation) for how this differs from Send-Time
    // Light Tuning above it. Drawn unconditionally (both bakers) for the same reason as that
    // section: LightmapDecoder runs on whatever baked lightmap ended up on a material regardless
    // of which baker produced it. No "Apply" button — writes straight into LightmapDecoder's
    // statics every OnGUI, and LightmapDecoder's own cache key already includes both values, so a
    // changed slider naturally invalidates the cached decode and forces a re-decode on the next
    // send (no separate "Force Refresh" needed for this specific pair of knobs).
    // ------------------------------------------------------------------

    void DrawBakedLightmapExposureSection(bool baking)
    {
        EditorGUILayout.LabelField(L(BakedLightmapExposureHeaderJA, BakedLightmapExposureHeaderEN), EditorStyles.boldLabel);

        GUI.enabled = !baking;

        _bakedLightmapRangeScale = EditorGUILayout.Slider(
            new GUIContent(L("明るさブースト", "Range Scale"), L(TooltipRangeScaleJA, TooltipRangeScaleEN)),
            _bakedLightmapRangeScale, 0.1f, 5f);

        _bakedLightmapSaturation = EditorGUILayout.Slider(
            new GUIContent(L("彩度", "Saturation"), L(TooltipBakedSaturationJA, TooltipBakedSaturationEN)),
            _bakedLightmapSaturation, 0f, 1f);

        LightmapDecoder.RangeScale = _bakedLightmapRangeScale;
        LightmapDecoder.ColorSaturationCompensation = _bakedLightmapSaturation;
    }

    // ------------------------------------------------------------------
    // Debug / Cleanup section — see the "Debug / Cleanup section state" field-block comment
    // above for the full 2026-08-26 consolidation background (ResoniteSDKDebugWindow.cs
    // removed, its buttons folded in here) and for why exactly two of its original buttons
    // ("Cleanup converters in the scene" / "Cleanup Resonite Components in the scene") were
    // deliberately NOT carried over. Drawn unconditionally (both bakers), same as Send-Time
    // Options/Light Tuning/Baked Lightmap Exposure above — none of this is baker-specific.
    // ------------------------------------------------------------------

    void DrawDebugCleanupSection(bool baking)
    {
        EditorGUILayout.LabelField(L(DebugCleanupHeaderJA, DebugCleanupHeaderEN), EditorStyles.boldLabel);

        // Partial sends: same "not gated on SDK connection state, the harness call just logs
        // a failure to the Result Log if disconnected" behavior "Send Meshes/Materials Only"
        // already had before this section existed — kept identical rather than adding a new
        // connection check here.
        GUI.enabled = !baking;

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button(new GUIContent(L("メッシュのみ送信", "Send Meshes Only"), L(TooltipSendMeshesOnlyJA, TooltipSendMeshesOnlyEN))))
            LightmapTestHarness.ConvertMeshesOnly();

        if (GUILayout.Button(new GUIContent(L("マテリアルのみ送信", "Send Materials Only"), L(TooltipSendMaterialsOnlyJA, TooltipSendMaterialsOnlyEN))))
            LightmapTestHarness.ConvertMaterialsOnly();

        if (GUILayout.Button(new GUIContent(L("ライトマップのみ送信", "Send Lightmaps Only"), L(TooltipSendLightmapsOnlyJA, TooltipSendLightmapsOnlyEN))))
            LightmapTestHarness.ConvertLightmapsOnly();

        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button(new GUIContent(L("不足アセットURLを再試行", "Retry Missing Asset URLs"), L(TooltipRetryMissingAssetURLsJA, TooltipRetryMissingAssetURLsEN))))
            LightmapTestHarness.RetryMissingAssetURLs();

        LightmapTestHarness.LogMessageJSON = EditorGUILayout.Toggle(
            new GUIContent(L("メッセージJSONをログ出力", "Log Messages JSON"), L(TooltipLogMessageJSONJA, TooltipLogMessageJSONEN)),
            LightmapTestHarness.LogMessageJSON);
    }

    // "Bake normal detail into lightmap (experimental)" — see the file header comment's
    // "できる範囲／できない範囲" note for exactly what this control does and does not do.
    // Interactive (not GUI.enabled=false) because an OFF->ON transition must be able to
    // trigger the confirmation dialog below. As of the Step 1/2 wiring, this *committed* bool
    // value IS read: RunBakeOnly()/RunBakeAndSend() pass it straight through to
    // LightmapTestHarness.StartBakeUnity(..., bakeNormalDetail)/RunPipeline(..., bool), which
    // sets LightingSettings.directionalityMode accordingly — flipping it now changes the next
    // bake's directional-lightmap mode (and, transitively, whether the SDK fork's
    // DirectionalLightmapBaker path activates at conversion time; see that class).
    void DrawNormalBakeToggle()
    {
        bool requestedValue = EditorGUILayout.Toggle(
            new GUIContent(L(NormalBakeLabelJA, NormalBakeLabelEN), L(NormalBakeHelpJA, NormalBakeHelpEN)),
            _bakeNormalDetail);

        if (requestedValue && !_bakeNormalDetail)
        {
            // OFF -> ON transition detected (requestedValue is this frame's click result;
            // _bakeNormalDetail is still last frame's committed value at this point in the
            // method) — gate the actual state change behind the confirmation dialog. A
            // Toggle click on an already-true box (requestedValue==true, _bakeNormalDetail
            // already true) or on an already-false box (both false) never lands here, so
            // the dialog only ever appears on the genuine OFF->ON edge, never on every
            // repaint and never while already ON.
            bool confirmed = EditorUtility.DisplayDialog(
                L(NormalBakeDialogTitleJA, NormalBakeDialogTitleEN),
                L(NormalBakeDialogMessageJA, NormalBakeDialogMessageEN),
                L(NormalBakeDialogOkJA, NormalBakeDialogOkEN),
                L(NormalBakeDialogCancelJA, NormalBakeDialogCancelEN));

            _bakeNormalDetail = confirmed; // Cancel -> stays false, exactly as spec'd.
        }
        else
        {
            _bakeNormalDetail = requestedValue;
        }

        EditorGUILayout.HelpBox(L(NormalBakeHelpJA, NormalBakeHelpEN), MessageType.Warning);
    }

    // Object.FindObjectsOfType<Light>() (no includeInactive arg) already excludes lights on
    // inactive GameObjects — matches every other FindObjectsOfType<T>() call already made in
    // this codebase (e.g. LightmapTestHarness.Report()'s MeshRenderer scan). The `enabled`
    // check below additionally excludes a Light component that itself has its checkbox
    // unchecked (a disabled component on an otherwise-active GameObject is NOT filtered out
    // by FindObjectsOfType, so this is not redundant).
    static Light FindMainDirectionalLight()
    {
        Light best = null;
        foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
        {
            if (light == null || !light.enabled)
                continue;
            if (light.type != LightType.Directional)
                continue;
            if (best == null || light.intensity > best.intensity)
                best = light;
        }
        return best;
    }

    // Applies the 5 tuning knobs to the scene. `sun` may be null (no directional light in
    // the open scene) — in that case only the ambient knobs are applied, matching
    // DrawLightingSection()'s graying-out of the sun-only controls. Only ever called (from
    // RunBakeOnly()/RunBakeAndSend()) when _baker == LightBaker.UnityStandard.
    void ApplyTuningToScene(Light sun)
    {
        if (sun != null)
        {
            Undo.RecordObject(sun, "Apply Bake Tuning (Sun Light)");
            Undo.RecordObject(sun.transform, "Apply Bake Tuning (Sun Light)");

            // A scripted/inspector-added Light left at LightShadows.None bakes illumination
            // but NO shadows regardless of shadowStrength — same root cause documented in
            // LightmapTestHarness.BuildLights()'s Sun_Directional setup. Force it on rather
            // than letting the Shadow Strength slider silently have no visible effect.
            if (sun.shadows == LightShadows.None)
                sun.shadows = LightShadows.Soft;
            sun.shadowStrength = _shadowStrength;
            sun.color = _sunColor;
            sun.transform.rotation = Quaternion.Euler(_sunAngle.x, _sunAngle.y, 0f);

            EditorUtility.SetDirty(sun);
        }

        // RenderSettings itself is a static class, not a UnityEngine.Object — the hidden
        // per-scene Object backing RenderSettings.ambient*/ambientMode is only reachable via
        // the internal RenderSettings.GetRenderSettings() (ildasm-confirmed: ".method
        // assembly hidebysig static class UnityEngine.Object GetRenderSettings()" in
        // UnityEngine.CoreModule.dll — "assembly" = C# `internal`, friend-accessible only to
        // UnityEditor.CoreModule itself, NOT to this project's own editor assembly). Reached
        // via reflection here, same idiom this codebase already uses in
        // LightmapTestHarness.Convert() for ResoniteLinkWindow.SendCurrentScene().
        var renderSettingsObj = GetRenderSettingsObjectForUndo();
        if (renderSettingsObj != null)
            Undo.RecordObject(renderSettingsObj, "Apply Bake Tuning (Ambient)");

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = _ambientColor * _ambientBrightness;

        if (renderSettingsObj != null)
            EditorUtility.SetDirty(renderSettingsObj);

        var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (activeScene.IsValid())
            EditorSceneManager.MarkSceneDirty(activeScene);
    }

    static UnityEngine.Object GetRenderSettingsObjectForUndo()
    {
        if (_getRenderSettingsMethod == null)
        {
            _getRenderSettingsMethod = typeof(RenderSettings).GetMethod(
                "GetRenderSettings", BindingFlags.NonPublic | BindingFlags.Static);
        }

        if (_getRenderSettingsMethod == null)
            return null;

        try
        {
            return (UnityEngine.Object)_getRenderSettingsMethod.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LightmapPipelineWindow] RenderSettings.GetRenderSettings() reflection call failed " +
                "(ambient changes will still apply, just without Undo/dirty tracking):\n" + ex);
            return null;
        }
    }

    BakeQuality ResolveQuality()
    {
        if (_qualityKey == "custom")
        {
            return new BakeQuality
            {
                BakeryTexelsPerUnit = _customBakeryTexelsPerUnit,
                BakeryGiSamples = _customBakeryGiSamples,
                UnityLightmapResolution = _customUnityLightmapResolution,
                UnityDirectSampleCount = _customUnityDirectSampleCount,
                UnityIndirectSampleCount = _customUnityIndirectSampleCount,
            };
        }

        return LightmapTestHarness.QualityPresets[_qualityKey];
    }

    // `sun` is whatever DrawLightingSection() found in this same OnGUI pass (null when
    // _baker == LightBaker.Bakery, since that section isn't drawn at all in that case —
    // see OnGUI()). ApplyTuningToScene() is only ever called for the Unity standard baker:
    // Bakery's ambient/light model is entirely separate (BakerySkyLight, not
    // RenderSettings.ambient*) and untouched by these 5 knobs.
    void RunBakeOnly(Light sun)
    {
        var quality = ResolveQuality();
        // _sceneTarget == TestRoom is the only case that still force-opens
        // Assets/LightmapTest.unity (via EnsureTestSceneOpen() inside the harness) — the new
        // default, CurrentOpenScene, passes true here so StartBake()/StartBakeUnity() never
        // switch away from whatever scene the user already has open. See the SceneTarget
        // field-block comment above for why CurrentOpenScene is now the default.
        bool useCurrentScene = _sceneTarget == SceneTarget.CurrentOpenScene;

        if (_baker == LightBaker.Bakery)
        {
            LightmapTestHarness.StartBake(quality.BakeryTexelsPerUnit, quality.BakeryGiSamples, useCurrentScene);
        }
        else
        {
            ApplyTuningToScene(sun);
            // _bakeNormalDetail's committed value is now actually read (see the file header
            // comment's updated "できる範囲/できない範囲" note) - wired straight through to
            // StartBakeUnity()'s bakeNormalDetail parameter, which sets
            // LightingSettings.directionalityMode accordingly.
            LightmapTestHarness.StartBakeUnity(quality.UnityLightmapResolution, quality.UnityDirectSampleCount, quality.UnityIndirectSampleCount, _bakeNormalDetail, useCurrentScene);
        }
    }

    // Same tuning-apply step as RunBakeOnly() above, then hands off to
    // LightmapTestHarness.RunPipeline(...) exactly as before — that already chains into
    // Convert() once the bake completes (via the harness's own OnBakeFinished/
    // OnUnityBakeFinished + _pipelineChainConvert flag) for BOTH backends, so no separate
    // bakeCompleted subscription is needed on this window's side.
    void RunBakeAndSend(Light sun)
    {
        var quality = ResolveQuality();
        // See the matching comment in RunBakeOnly() above.
        bool useCurrentScene = _sceneTarget == SceneTarget.CurrentOpenScene;

        if (_baker == LightBaker.UnityStandard)
            ApplyTuningToScene(sun);

        // _bakeNormalDetail is only meaningful for _baker == LightBaker.UnityStandard (see
        // RunPipeline()'s own header comment) - harmlessly ignored by the harness for Bakery.
        LightmapTestHarness.RunPipeline(quality, _baker, _bakeNormalDetail, useCurrentScene);
    }
}
