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
//
// 2026-08-08further（Tanossy指摘「ソファはもっと暗く茶色であるべき、黒い壁も額縁も黒味が
// 足りない」）: このシーンのマテリアルは軒並み`_Color=白(1,1,1)`で、実際の色はテクスチャ由来。
// コントラスト強化(PPv2ToneMapMath.ResoniteExtraContrast)は「白に近いピボット以上の値」を
// さらに明るい側へ押し上げるため、白い`_Color`タグそのものが更に白くなり、テクスチャ本来の
// 濃い茶色・黒が薄まって見える逆効果になっていた。ReflectionProbeの強度補正
// (ToneMapCompensationState.Enabled)は有効なまま維持しつつ、マテリアル色へのグレーディング
// 適用だけを独立したフラグで無効化できるようにする(デフォルトOFF)。
// 2026-08-08further2（Tanossy指摘「茶色味が足りない」）: 上のMaterialGradingEnabled=falseで
// フル無効化した際、Contrastの副作用と一緒にUnity元シーンのSaturation設定(=10→1.1倍の穏やかな
// 彩度ブースト)まで道連れで失っていた。Contrast/WhiteBalance/Tonemapは経由させず、Saturationの
// みを独立して適用するPPv2ToneMapMath.ApplySaturationOnly()に切り替える実機検証。
// 元の値(MaterialGradingEnabled=false, フル ApplyGrading())に戻したい場合は、下のApply()の
// 中身を `return linearColor;`（MaterialGradingEnabled=falseと同じ効果）か
// `PPv2ToneMapMath.ApplyGrading(c)`（フル適用）に差し替えるだけでよい。
public static class ColorGradingApproximation
{
    public static bool MaterialGradingEnabled = true;

    public static Color Apply(Color linearColor)
    {
        if (!ToneMapCompensationState.Enabled || !MaterialGradingEnabled)
            return linearColor;

        var c = new Vector3(linearColor.r, linearColor.g, linearColor.b);
        var graded = PPv2ToneMapMath.ApplySaturationOnly(c);

        return new Color(graded.x, graded.y, graded.z, linearColor.a);
    }
}
