using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

// 2026-07-27 (Tanossy指摘): Resonite側のPostProcessingSettingsコンポーネントはBloom/AO/
// MotionBlur/SSR/Antialiasingの5項目しか持たず、Color Grading(Contrast/Saturation/
// Tonemapping)に対応するフィールドが存在しない(PostProcessingConverter.csのスコープ注記で
// 2026-07-12に確認済み)。スクリーンエフェクトとしての再現は不可能なため、代わりにUnity側の
// ColorGrading.contrast/saturationの値を、変換時に各マテリアルのAlbedo/Emissive色へ直接
// 焼き込むことで近似する。
//
// 数式はPPv2本体(com.unity.postprocessing@3.4.0)のColors.hlslから直接確認したもの
// (推測なし):
//   - Contrast(c, midpoint, contrast) = (c - midpoint) * contrast + midpoint  (Colors.hlsl:584)
//   - Saturation(c, sat) = luma + sat * (c - luma)                            (Colors.hlsl:574)
//   - Luminance(rgb) = dot(rgb, {0.2126729, 0.7151522, 0.0721750})            (Colors.hlsl:229)
//   - どちらもリニア空間の色に対して適用される(ColorGrading.cs:493-494等で確認)
// ColorGrading.cs側のcontrast/saturationパラメータは-100..100スケールで、実際の乗数への
// 変換は `value/100f + 1f` ([0,2]へのリマップ、ColorGrading.cs:493-494で確認)。
//
// 注意: これはスクリーン全体へのポストエフェクトの正確な再現ではなく、マテリアルの基本色への
// 近似焼き込みに過ぎない(テクスチャのピクセル自体は変更しないため、白でないTintを持つ
// マテリアルにしか効果が及ばない)。Tonemapping(HDRレンジ圧縮)による見た目の違いは
// この方式では再現できない。
public static class ColorGradingApproximation
{
    // PostProcessVolume.sharedProfileへのGetSettings<T>()呼び出しは軽くないため、
    // シーン内のグローバルボリューム探索1回分だけキャッシュする。ドメインリロードや
    // シーン切り替えをまたいで古い値を使い続けないよう、明示的なInvalidate()は設けず
    // 呼び出し毎に軽量な存在チェックのみ行う。
    static bool s_resolved;
    static float s_contrastMultiplier = 1f;
    static float s_saturationMultiplier = 1f;

    static void EnsureResolved()
    {
        if (s_resolved)
            return;

        s_resolved = true;
        s_contrastMultiplier = 1f;
        s_saturationMultiplier = 1f;

#if UNITY_2023_1_OR_NEWER
        var volumes = Object.FindObjectsByType<PostProcessVolume>(FindObjectsSortMode.None);
#else
        var volumes = Object.FindObjectsOfType<PostProcessVolume>();
#endif

        foreach (var volume in volumes)
        {
            if (!volume.isGlobal || volume.sharedProfile == null)
                continue;

            if (!volume.sharedProfile.TryGetSettings<ColorGrading>(out var colorGrading) || !colorGrading.enabled.value)
                continue;

            // 最初に見つかったグローバルボリュームを採用(PostProcessingConverterのfirst-wins方針と同一)。
            s_contrastMultiplier = colorGrading.contrast.value / 100f + 1f;
            s_saturationMultiplier = colorGrading.saturation.value / 100f + 1f;
            break;
        }
    }

    public static Color Apply(Color linearColor)
    {
        EnsureResolved();

        if (s_contrastMultiplier == 1f && s_saturationMultiplier == 1f)
            return linearColor;

        var c = new Vector3(linearColor.r, linearColor.g, linearColor.b);

        // Saturation: luma + sat * (c - luma)
        float luma = Vector3.Dot(c, new Vector3(0.2126729f, 0.7151522f, 0.0721750f));
        c = new Vector3(luma, luma, luma) + s_saturationMultiplier * (c - new Vector3(luma, luma, luma));

        // Contrast: (c - midpoint) * contrast + midpoint (PPv2 uses a log-space midpoint of
        // ACEScc(0.18) internally for its full grading path, but for this simple linear-space
        // approximation on material tints, 0.5 - the standard "middle grey point" for a plain
        // contrast pivot - is the correct, non-ACES-specific choice)
        const float midpoint = 0.5f;
        c = (c - new Vector3(midpoint, midpoint, midpoint)) * s_contrastMultiplier + new Vector3(midpoint, midpoint, midpoint);

        return new Color(
            Mathf.Max(0f, c.x),
            Mathf.Max(0f, c.y),
            Mathf.Max(0f, c.z),
            linearColor.a);
    }
}
