using UnityEngine;

// 2026-08-08 (Tanossy指摘「できる限りオリジナルソースはいじりたくないので外だしして」):
// 公式LightConverter.cs内のLightHelper.SetFrom()が行うIntensity/Color代入2行だけを差し替える
// ための送信時チューニング値。公式ファイル側の変更は
// `resonite.Intensity = LightTuning.ApplyIntensity(unity.intensity);` /
// `resonite.Color = new ColorX(LightTuning.ApplyColor(unity.color));` の2行差し替えのみで済む
// よう、調整値・調整ロジックを完全にこの新規ファイルへ外だししている。
public static class LightTuning
{
    // 2026-08-08 (Tanossy指摘「暗い」への対応): UnityではLight.intensity=0.5のまま実機で
    // 適切な明るさに見えている(Indirectベイク+実ライトの組み合わせ、同一シーン・同一カメラ角度で
    // 実証確認済み)のに対し、同じ値をそのまま送ったResonite側は明らかに暗い。ベイクデータの
    // 加算フィル(BakedLightmapStandardConverter.AdditiveFillStrength)を上げても頭打ちだった
    // ため、原因をUnity/Resonite間のPoint/Spot Light減衰カーブ・輝度換算の違いと見て、
    // 送信時だけ効く倍率をここに追加する。3.0は最初の実機検証値であり確定値ではない。
    //
    // 2026-08-08追記(Tanossy指摘「Unityで黒いのにResoniteで黒くない」): 3.0はTV画面・
    // 金属フレーム・ガラス等スペキュラー主体のマテリアルで反射が過剰になり、Unity側では黒く
    // 見えている面がResoniteでは明るく浮いてしまう副作用があった(特にMetallic値が高い素材は
    // 拡散反射をほぼ持たず、見た目のほぼ全てがスペキュラー反射のため、Albedoをどれだけ暗くしても
    // 打ち消せない)。1.8へ引き下げ。その後ユーザーが「ライトの明るさを元の明るさからの倍率で
    // 合わせるようにインポーターがなっている？」と確認・単純な倍率であることに納得した上で2.5に
    // 再調整。
    //
    // 2026-08-08さらに追記(Tanossy指摘「明るすぎるのでポイントライトの明るさの増加具合、
    // 減らした方がいいかも」): 外だし直後の再検証中に指摘。ライト種別ごとの差別化(Point限定の
    // 別倍率)も選択肢として提示したが、シンプルさを優先し全ライト共通のこの値を再度1.8へ。
    public static float IntensityMultiplier = 1.8f;

    // 2026-08-08 (Tanossy指摘「黄身が多い、白味を上げたい」): 部屋の光源色がUnity側のまま
    // 暖色(ColorX(1, 0.89, 0.75)相当)で転送されており、Resonite側でこれが実際より黄色く
    // 感じられている。光源色そのものを送信時だけ白側へブレンドする(0=Unityの色そのまま、
    // 1=純白)。0.4→0.7で確定。
    //
    // 既知の限界(2026-08-08、ユーザーへ説明済み・保留合意): 元の色相に関わらず一律で白へ
    // Lerpするため、シーン内に複数の異なる色の光源があった場合、本来の色差が均等に薄まって
    // しまう(このワールドは単一の暖色系照明のみのため実害なしと判断・対応は次回以降に持ち越し)。
    public static float WhiteBalanceShift = 0.7f;

    public static float ApplyIntensity(float unityIntensity) => unityIntensity * IntensityMultiplier;

    public static Color ApplyColor(Color unityColor) => Color.Lerp(unityColor, Color.white, Mathf.Clamp01(WhiteBalanceShift));
}
