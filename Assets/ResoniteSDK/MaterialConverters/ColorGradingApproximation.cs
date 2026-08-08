using UnityEngine;

// 2026-07-27 (Tanossy指摘): Resonite側のPostProcessingSettingsコンポーネントはBloom/AO/
// MotionBlur/SSR/Antialiasingの5項目しか持たず、Color Grading(Contrast/Saturation/
// Tonemapping)に対応するフィールドが存在しない(PostProcessingConverter.csのスコープ注記で
// 2026-07-12に確認済み)。スクリーンエフェクトとしての再現は不可能なため、代わりにUnity側の
// 色調補正・トーンマップの効果を、変換時に各マテリアルのAlbedo/Emissive色へ直接焼き込むことで
// 近似する。
//
// 2026-08-08更新（Tanossy指摘「反射のぎらつき・Reflection Probeが強すぎる」への対応、案2）:
// 実際の計算(WhiteBalance・LogC空間でのContrast・Saturation・NeutralTonemap)は
// Assets/ResoniteSDK/ToneMapCompensation/PPv2ToneMapMath.cs（本体SDKとは独立したフォルダ）に
// 移設し、こちらは呼び出し口として薄いラッパーのまま維持している（StandardBaseConverter.cs/
// BakedLightmapStandardConverter.csからの既存の呼び出し方は変更不要）。
// 旧実装（線形空間・ピボット0.5でのContrast、Tonemapper再現なし）は不正確だったため置き換えた。
// ToneMapCompensationState.Enabled = false でこの近似自体を無効化できる
// (Resonite SDK ManagerパネルのSend Tonemap Compensationトグルから操作)。
public static class ColorGradingApproximation
{
    public static Color Apply(Color linearColor)
    {
        if (!ToneMapCompensationState.Enabled)
            return linearColor;

        var c = new Vector3(linearColor.r, linearColor.g, linearColor.b);
        var graded = PPv2ToneMapMath.ApplyGrading(c);

        return new Color(graded.x, graded.y, graded.z, linearColor.a);
    }
}
