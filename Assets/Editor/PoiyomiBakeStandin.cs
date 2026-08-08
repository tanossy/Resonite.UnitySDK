// PoiyomiBakeStandin.cs
//
// ダイダロス作 — Poiyomiシェーダーのマテリアルにベイク済みライトマップを乗せるための一時代役。
// (SDK本体には一切触れない。Assets/Editor 配下の追加ファイルのみで完結する拡張。
//  BakeryTempObjectSuppression.cs と同様、関心事を分離した専用ファイル。)
//
// --- 背景・確定事実（実物確認済み。推測ではない） -------------------------------------------
// Assets/ResoniteSDK/ComponentConverters/Unity Core/Rendering/LightmapMaterialCache.cs:180
// の適格判定は `source.shader.name != "Standard"` で弾く。Poiyomiシェーダー
// (Assets/ResoniteSDK/MaterialConverters/Custom/Poiyomi/PoiyomiConverter.cs の
// EvaluateHeuristicConversion と同一ロジック: シェーダー名が ".poiyomi/" を含む) は必ずここで
// 弾かれ、ベイク照明(SecondaryAlbedo)が一切乗らない — 警告も出ない仕様。
//
// Poiyomiマテリアルは実は Standard 互換のプロパティ名を流用している
// (PoiyomiPbsConverter.cs で確認済み): _MainTex (Material.mainTexture 経由), _Color, _BumpMap,
// _BumpScale, _EmissionColor, _EmissionMap, _EnableEmission, _EmissionStrength。
// Metallic/Smoothness は _MochieMetallicMaps 等の独自名で非互換 (今回は対象外)。
//
// 裁定: Poiyomi は元々 Resonite に移すとフラットになるので、Standard へ変換してからベイクする
// (トゥーン表現喪失は容認済み)。
//
// --- 実装方針 ---------------------------------------------------------------------------
// 新しい判定ロジックや新しい出力型は作らない。ベイク直前にシーン内の対象 Renderer の
// sharedMaterials を一時的な Standard 代役マテリアルへ差し替え、その状態でベイク & 送信する
// ことで、既存の実証済み Standard 経路 (LightmapMaterialCache の PBS_MultiUV_Metallic
// 差し込み) にそのまま乗せる。Convert() 完了直後に必ず元の Poiyomi マテリアルへ復元する
// (Unity のシーンビュー上は通常時は常に本来の Poiyomi 見た目に戻る)。
//
// 安全ゲート (重要): Standard 化するのは PoiyomiのBlendModeが元々Opaque相当のマテリアルだけ。
// Cutout/Fade/Transparent (葉っぱ・柵・髪の毛カード等) は対象外とし、既存の PoiyomiConverter
// 経路 (ベイク無し) にそのまま委ねる。理由: LightmapMaterialCache の適格条件が _Mode==0
// (Opaque) のみのため、強制Opaque化するとシルエットが崩れる新規不具合になる。BlendMode 判定
// には Assets/ResoniteSDK/MaterialConverters/Custom/Poiyomi/PoiyomiBlendModeComputer.cs の
// PoiyomiBlendModeComputer.FromPoiyomi(material) をそのまま使う (SDKフォーク側/ライブ側の
// 両方に同一ファイルが存在することを確認済み — 本ファイルはライブ側 K:/base/ResoniteWorld の
// コピーを直接参照する。ResoniteSDK配下・本ファイルの属する Assets/Editor 配下いずれにも
// asmdef が存在しないため既定アセンブリでコンパイルされ、FrooxEngine.BlendMode /
// PoiyomiBlendModeComputer を直接参照できる — LightmapTestHarness.cs 冒頭のコメントで確認済み
// の同じ理屈)。
//
// MaterialGlobalIlluminationFlags / globalIlluminationFlags / RealtimeEmissive / HasProperty /
// EnableKeyword は、このEditorが実際に使っている UnityEngine.CoreModule.dll
// (2022.3.22f1, K:/base/ResoniteWorld と同じインストール) に対する文字列grepで実在確認済み
// (2026-07-11)。
//
// 厳守: 元の Poiyomi マテリアルアセット(.mat)自体は絶対に変更しない。Renderer の
// sharedMaterials 差し替えのみ (ランタイム/エディタ上の参照差し替え)。Poiyomi非対象・
// 非Opaque・Standard以外の他シェーダーには一切触れない (回帰ゼロ)。
using System.Collections.Generic;
using FrooxEngine;
using UnityEngine;

public static class PoiyomiBakeStandin
{
    // Poiyomiソースマテリアル(アセットインスタンス) -> 生成済みStandard代役マテリアル。
    // 使い回すことで、複数回の bake/convert サイクルで毎回新しい破棄予定Material(隠しオブジェ
    // クト)を作らない。ソースの生きた参照が破棄された場合(シーン再読込等でMaterialが再ロード
    // されたケース)はUnityの疑似null比較(TryGetValue後のnullチェック)で捕捉し、キーごと素通し
    // で作り直す。
    static readonly Dictionary<UnityEngine.Material, UnityEngine.Material> _standinByPoiyomiSource = new Dictionary<UnityEngine.Material, UnityEngine.Material>();

    // SwapIn() が実際に上書きした Renderer -> 元の sharedMaterials 配列。差し替えが有効な間だけ
    // 非null (RestoreAll() が復元後にnullへ戻す)。これにより:
    //  - RestoreAll() を差し替え前に呼んでも安全なno-op になる
    //  - 差し替え済みの状態でもう一度 SwapIn() を呼んでも、記録済みの「本物のPoiyomi」を
    //    「今日の代役」で上書きしてしまわない(冪等性)
    static Dictionary<Renderer, UnityEngine.Material[]> _originalMaterialsByRenderer;

    // ------------------------------------------------------------------
    // SwapIn — ベイク開始時 (StartBake()/StartBakeUnity() 冒頭、SetSceneLightsEnabled(true) の
    // 直後) に LightmapTestHarness から呼ばれる。
    // ------------------------------------------------------------------
    public static void SwapIn()
    {
        if (_originalMaterialsByRenderer != null)
        {
            // 冪等性ガード: 記録済みの「本物」を「今日の代役」で上書きして永久に失う事故を防ぐ。
            Debug.Log("[PoiyomiBakeStandin] SwapIn: a swap is already active; call ignored (call RestoreAll() first).");
            return;
        }

        var recorded = new Dictionary<Renderer, UnityEngine.Material[]>();

        foreach (var renderer in CollectCandidateRenderers())
        {
            var sourceMaterials = renderer.sharedMaterials;
            UnityEngine.Material[] replacement = null; // 少なくとも1スロットが実際に変わる場合のみ確保

            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                var source = sourceMaterials[i];
                if (!IsEligiblePoiyomiOpaque(source))
                    continue;

                var standin = GetOrCreateStandin(source);
                if (standin == null)
                    continue;

                if (replacement == null)
                    replacement = (UnityEngine.Material[])sourceMaterials.Clone();
                replacement[i] = standin;
            }

            if (replacement != null)
            {
                recorded[renderer] = sourceMaterials; // 復元用に「元の」配列そのものを保持
                renderer.sharedMaterials = replacement;
            }
        }

        _originalMaterialsByRenderer = recorded;

        if (recorded.Count > 0)
            Debug.Log($"[PoiyomiBakeStandin] SwapIn: swapped Poiyomi->Standard standin material(s) on {recorded.Count} renderer(s) for baking.");
    }

    // ------------------------------------------------------------------
    // RestoreAll — Convert() の SendCurrentScene() 呼び出しを囲む try/finally の finally から
    // (成功時も例外時も必ず) 呼ばれる。
    // ------------------------------------------------------------------
    public static void RestoreAll()
    {
        if (_originalMaterialsByRenderer == null)
            return; // SwapIn() が一度も走っていない、またはRestoreAll()が既に消費済み

        // 2026-07-12 バグ修正（バグハンター指摘, ダイダロス対応）: 以前はループ内でtry/catchを
        // 持たず、1件でも renderer.sharedMaterials セットが例外を投げると(破棄済みRenderer・
        // マテリアル数不一致等) foreach が即座に中断し、以降のRendererが復元されないまま
        // _originalMaterialsByRenderer = null にも到達しなかった(冪等性ガードにより次回の
        // SwapIn()も黙って空振りする=一度失敗すると永久にPoiyomi代役材が残り続ける)。
        // 個別にtry/catchし、失敗したRendererがあってもログを出しつつ残りの復元を継続する。
        // _originalMaterialsByRenderer = null への到達は例外の有無に関わらず必ず保証する
        // (ループ後、無条件に実行する既存の配置のまま = finally相当)。
        int restored = 0;
        int failed = 0;
        foreach (var kvp in _originalMaterialsByRenderer)
        {
            var renderer = kvp.Key;
            if (renderer == null)
                continue; // SwapIn()以降にRendererが破棄/シーン変更された場合は何もしない

            try
            {
                renderer.sharedMaterials = kvp.Value;
                restored++;
            }
            catch (System.Exception ex)
            {
                failed++;
                Debug.LogError($"[PoiyomiBakeStandin] RestoreAll: failed to restore original material(s) on renderer \"{renderer.name}\": {ex}. Continuing with the remaining renderer(s).");
            }
        }

        if (restored > 0)
            Debug.Log($"[PoiyomiBakeStandin] RestoreAll: restored original Poiyomi material(s) on {restored} renderer(s).");
        if (failed > 0)
            Debug.LogWarning($"[PoiyomiBakeStandin] RestoreAll: {failed} renderer(s) failed to restore - see the error(s) above. " +
                "Those renderer(s) may still be showing the temporary Standard standin material.");

        _originalMaterialsByRenderer = null;
    }

    static IEnumerable<Renderer> CollectCandidateRenderers()
    {
        foreach (var mr in UnityEngine.Object.FindObjectsOfType<UnityEngine.MeshRenderer>())
            yield return mr;
        foreach (var smr in UnityEngine.Object.FindObjectsOfType<UnityEngine.SkinnedMeshRenderer>())
            yield return smr;
    }

    // 検出: PoiyomiConverter.EvaluateHeuristicConversion と同一ロジック (実物確認済み)。
    // ゲート: PoiyomiBlendModeComputer.FromPoiyomi の結果が BlendMode.Opaque の場合のみ対象。
    static bool IsEligiblePoiyomiOpaque(UnityEngine.Material material)
    {
        if (material == null || material.shader == null)
            return false;

        if (!material.shader.name.Contains(".poiyomi/"))
            return false;

        return PoiyomiBlendModeComputer.FromPoiyomi(material) == BlendMode.Opaque;
    }

    static UnityEngine.Material GetOrCreateStandin(UnityEngine.Material source)
    {
        if (_standinByPoiyomiSource.TryGetValue(source, out var cached) && cached != null)
        {
            // ソースの .mat アセット自体は絶対に変更しないが (厳守事項)、Unity上でベイクの
            // 合間にPoiyomi側のプロパティが編集されることは普通にあり得る。次のベイクにその
            // 変更が反映されるよう、キャッシュヒット時も毎回プロパティを再同期する
            // (LightmapMaterialCache 自身の「毎回再読込・再書込」設計を踏襲)。
            SyncStandinFromSource(cached, source);
            return cached;
        }

        var shader = UnityEngine.Shader.Find("Standard");
        if (shader == null)
        {
            Debug.LogWarning("[PoiyomiBakeStandin] Could not find the built-in \"Standard\" shader; " +
                $"skipping standin for Poiyomi material \"{source.name}\".");
            return null;
        }

        var standin = new UnityEngine.Material(shader) { name = $"{source.name}_StandardStandin" };
        SyncStandinFromSource(standin, source);

        _standinByPoiyomiSource[source] = standin;
        return standin;
    }

    // ソース(Poiyomi)側からの読み取りは全て HasProperty でガードする — Poiyomiバージョン差で
    // 無いプロパティがある前提。代役(standin)側は常にビルトインStandardシェーダーそのもの
    // (このメソッド内で毎回 new Material(Shader.Find("Standard")) 由来) なので、standin への
    // 書き込みには _MainTex/_Color/_BumpMap/_BumpScale/_EmissionColor/_EmissionMap/_Mode の
    // いずれについてもガードは不要 (Standardが標準で持つプロパティ)。
    static void SyncStandinFromSource(UnityEngine.Material standin, UnityEngine.Material source)
    {
        if (source.HasProperty("_MainTex"))
        {
            standin.mainTexture = source.mainTexture;
            standin.mainTextureScale = source.mainTextureScale;
            standin.mainTextureOffset = source.mainTextureOffset;
        }

        if (source.HasProperty("_Color"))
            standin.color = source.color;

        UnityEngine.Texture bumpMap = source.HasProperty("_BumpMap") ? source.GetTexture("_BumpMap") : null;
        if (bumpMap != null)
        {
            standin.SetTexture("_BumpMap", bumpMap);
            float bumpScale = source.HasProperty("_BumpScale") ? source.GetFloat("_BumpScale") : 1f;
            standin.SetFloat("_BumpScale", bumpScale);
            standin.EnableKeyword("_NORMALMAP");
        }
        else
        {
            standin.SetTexture("_BumpMap", null);
            standin.DisableKeyword("_NORMALMAP");
        }

        bool emissionEnabled = source.HasProperty("_EnableEmission") && source.GetFloat("_EnableEmission") > 0f;
        if (emissionEnabled)
        {
            Color emissiveColor = source.HasProperty("_EmissionColor") ? source.GetColor("_EmissionColor") : Color.black;
            float emissiveStrength = source.HasProperty("_EmissionStrength") ? source.GetFloat("_EmissionStrength") : 1f;
            standin.SetColor("_EmissionColor", emissiveColor * emissiveStrength);

            if (source.HasProperty("_EmissionMap"))
                standin.SetTexture("_EmissionMap", source.GetTexture("_EmissionMap"));

            standin.EnableKeyword("_EMISSION");
            // Unity自身のベイクパイプライン(Progressive/Enlighten)は、マテリアルのEmissionを
            // GI寄与として拾うのにこのフラグを要求する — _EMISSIONキーワードはレンダリング上の
            // 見た目にのみ関わる別軸。globalIlluminationFlags/RealtimeEmissiveは本ファイル冒頭
            // コメントの通りDLL文字列grepで実在確認済み。
            standin.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        else
        {
            standin.SetColor("_EmissionColor", Color.black);
            standin.SetTexture("_EmissionMap", null);
            standin.DisableKeyword("_EMISSION");
            standin.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        // ゲート(IsEligiblePoiyomiOpaque)で既にOpaque確認済み、かつ新規Standardマテリアルの
        // 既定値も_Mode==0(Opaque)なので実質no-op — 単なる明示。
        standin.SetFloat("_Mode", 0f);
    }
}
