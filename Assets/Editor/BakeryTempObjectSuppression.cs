// BakeryTempObjectSuppression.cs
//
// ダイダロス作 — Bakery一時オブジェクトをResonite.UnitySDKのシーン変換から除外する。
// (SDK本体には一切触れない。Assets/Editor 配下の追加ファイルのみで完結する拡張。)
//
// ⚠ Bakery導入プロジェクト専用: 本ファイルは Assets/Bakery/ftLightmapsStorage.cs
// （BakeryRuntimeAssembly、autoReferenced=true）の型 ftLightmapsStorage に直接依存する。
// クラス全体を `#if BAKERY_INCLUDED` で条件コンパイル化してある（Bakery非導入環境でも
// 本ファイルはコンパイルが通り、単に「何も登録しない」空ファイル相当になる — Bakeryが
// 無ければ隠すべきBakery一時オブジェクトもそもそも存在しないので、動作上の欠落もない）。
// BAKERY_INCLUDED は Assets/Editor/BakeryPresenceDefine.cs が自動管理する。
//
// 対象: ルート直下の "!ftraceLightmaps" GameObject（ftLightmapsStorage を保持）。
// 実ソース確認済み (Assets/Editor/x64/Bakery/scripts/ftBuildGraphics.cs InitSceneStorage(),
// 同ファイル line 1517-1541 ほか、"!ftraceLightmaps" を作成/検索する全箇所を横断grep済み):
//   gg = new GameObject();
//   gg.name = "!ftraceLightmaps";
//   gg.hideFlags = HideFlags.HideInHierarchy;
//   storages[i] = gg.AddComponent<ftLightmapsStorage>();
// このオブジェクトは常に唯一のルートレベルGameObjectとして、ベイク後もシーンに永続する
// （HideFlags.HideInHierarchyはHierarchyウィンドウ上の非表示のみで、
//   SceneManager.GetActiveScene().GetRootGameObjects() には含まれ続ける — SceneConverter.cs
//   の ConvertScene() はまさにこのAPIでルート一覧を取るため、何もしなければ変換対象に入る）。
// ftLightmapsStorage は Bakery のベイク結果ブックキーピングの「正本」なので、
// シーンからの削除は禁止（要件）。削除ではなく "変換だけ止める" 必要がある。
//
// --- 除外機構の特定（正規機構）---
// SDKが公式に提供する「変換を止める」ための唯一のフックは以下の2ファイルで定義される
// ConversionSupressionHandler デリゲート機構である
// (Assets/ResoniteSDK/ComponentConverters/ConversionSupressionHandler.cs,
//  Assets/ResoniteSDK/ComponentConverters/ConverterSupressionHandlerAttribute.cs):
//
//   public delegate void ConversionSupressionHandler(Transform root, List<Component> toConvert);
//   [AttributeUsage(AttributeTargets.Method)] public class ConverterSupressionHandlerAttribute : Attribute {}
//
// ComponentConverterRepository の静的コンストラクタ (Assets/ResoniteSDK/Editor/ComponentConverterRepository.cs)
// が TypeCache.GetTypesDerivedFrom<ResoniteComponentConverter>() で「プロジェクト内の全ての」
// ResoniteComponentConverter<T> 派生型を横断的に収集し、[ConverterSupressionHandler] が付いた
// public static メソッドを1つだけデリゲートとして登録する。これはSDK本体を一切改変せずとも、
// このファイルのように外部からコンバータを追加登録するだけで有効になる（このファイル自体が
// その「正規の拡張ポイント」の使用例）。
//
// SceneConverter.UpdateComponentConversions(Transform root) (Editor/SceneConverter.cs:388-471)
// を実際に読んだ上での重要な事実（推測せず確認済み）:
//   - このメソッドは「シーン内の各GameObject」に対して再帰的に呼ばれる。引数名は歴史的に
//     `root` だが、実際には「現在処理中の1つのTransform」であり、シーンルートではない
//     (再帰呼び出し: `UpdateComponentConversions(child)` — line 469)。
//   - `components` = そのTransformが持つ、変換対象コンバータが登録されている「全ての」
//     コンポーネントのリスト（line 390-424）。
//   - suppression handler は "そのコンポーネント自身の型" だけでなく、`toConvert`
//     （= components と同一のリスト参照）全体を自由に変更できる (line 426-428:
//     `foreach (var converter in converters.Values) converter.SupressionHandler?.Invoke(root, components);`)。
//     つまり「自分の型が乗っているGameObjectの、他の型も含めた全コンポーネント変換」を
//     まとめて止められる。
//   - ただし、この機構は「そのTransform上のコンポーネント変換」だけを止めるものであり、
//     Transform自体のSlot生成 (ConvertHierarchy → Convert(transform, messages) — line 473-508)
//     は無条件で全ルートGameObjectに対して走る。ルートGameObjectそのものをスキップする
//     公式APIはこのSDKバージョンには存在しない（SceneConverter.cs / AssetConversionManager.cs /
//     ResoniteLinkWindow.cs / SkyboxConverter.cs / ProceduralSkyboxConverter.cs を横断grepし、
//     hideFlags や EditorOnly タグを考慮する分岐が皆無であることを確認済み）。
//     → よって "!ftraceLightmaps" は空のSlot（コンポーネントなし、見た目に何の影響もない）
//     としてResonite側に現れ続ける。これは既知の残存事項として報告する
//     （中身のBakery内部データが漏れることはゼロになる、という本来の目的は完全に達成する）。
//
// --- 実装方針 ---
// ftLightmapsStorage に対するコンバータを登録するSDKコンバータは存在しない
// （grep確認済み: "ftLightmapsStorage" を型として参照するコンバータはゼロ）ので、
// 何もしなければ [ConverterSupressionHandler] を発火させるトリガー自体が存在しない。
// そこで本ファイルで ftLightmapsStorage 用の "空コンバータ" を新規登録し、
// そのコンバータに suppression handler を実装する:
//   - UpdateConversion() は意図的に完全な no-op（ResoniteComponentのラッパーを一切
//     追加しない = ftLightmapsStorage からAddComponent/UpdateComponentメッセージは
//     絶対に生成されない）。
//   - Cleanup() も no-op（Initialize/UpdateConversionでラッパーを作っていないため
//     破棄するものがない）。
//   - [ConverterSupressionHandler] 静的メソッドは、そのTransformが ftLightmapsStorage
//     を持っている場合に限り toConvert.Clear() する。「等」（将来Bakeryが同じ
//     "!ftraceLightmaps" オブジェクトに他の一時コンポーネントを追加するようになった場合）
//     にも対応できるよう、"自分の型だけ" ではなく全コンポーネントを一括で除外する。
#if BAKERY_INCLUDED
using System.Collections.Generic;
using UnityEngine;

public class BakeryTempObjectSuppressionConverter : ResoniteComponentConverter<ftLightmapsStorage>
{
    protected override void UpdateConversion(ftLightmapsStorage target, IConversionContext context)
    {
        // Intentionally empty: ftLightmapsStorage has no Resonite-side equivalent and must
        // never produce an AddComponent/UpdateComponent message. Do not call
        // EnsureComponent<...> here.
    }

    protected override void Cleanup()
    {
        // Nothing was ever created in UpdateConversion, so there is nothing to destroy.
    }

    [ConverterSupressionHandler]
    public static void SuppressBakeryStorageObject(Transform root, List<Component> toConvert)
    {
        // NOTE: 'root' is the specific GameObject currently being processed by
        // SceneConverter.UpdateComponentConversions(Transform root), not the scene root —
        // see the long comment block above. Only touch GameObjects that actually carry
        // Bakery's own storage component (normally just the single "!ftraceLightmaps" root
        // object; this check is name-agnostic so it still works if Bakery ever renames it).
        if (root.GetComponent<ftLightmapsStorage>() == null)
            return;

        // Clear the WHOLE list, not just ftLightmapsStorage's own entry: if Bakery ever
        // places another convertible component on the same object (the "等" case), it must
        // be excluded too. This never touches the scene hierarchy itself (no
        // DestroyImmediate / SetActive / re-parenting) — only which components get sent to
        // Resonite.
        toConvert.Clear();
    }
}
#endif
