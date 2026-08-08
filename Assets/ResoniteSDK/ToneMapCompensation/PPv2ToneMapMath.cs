using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

// 2026-08-08 (Tanossy指摘「反射のぎらつき・Reflection Probeが強すぎる」への対応、案2を選択):
// Resonite(Renderite)は主カメラでColorGrading相当のポスト処理を一切行わない
// (2026-07-30 Renderite.Unity.Renderer調査で確定済み: CameraPostprocessingManager.cs内に
// "if (IsPrimary && item is ColorGrading) continue; // skip, it breaks things" という直接証拠あり)。
// Unity側のPPv2による色調補正・トーンマッピングの効果を全く受けないため、ハイライト(反射・
// グレア等)が生のまま出て「きつい」見た目になる。
//
// 本来はUnity側のトーンマップカーブを3D LUTへ焼き込みResonite公式のLUT Materialへ適用するのが
// 理想だが、ResoniteLinkのAPIにTexture3Dインポートの手段が存在しない(2026-08-08確認、
// ImportTexture2D/ImportCubemap/ImportMesh/ImportAudioClipはあるがImportTexture3D相当は無い)
// ため実装不可能と判明。次善策として、Unity公式のPPv2パッケージソースを直接読んで確定させた
// 実際の計算式を、マテリアル色(ColorGradingApproximation経由)とReflection Probe強度
// (ReflectionProbeConverter経由)へ個別に適用することで近似する。
//
// 検証済みの数式・定数の出典（推測なし、全てcom.unity.postprocessing@3.4.0パッケージ本体から）:
//   - PostProcessing/Shaders/Builtins/Lut3DBaker.compute （全体のパイプライン順序）
//   - PostProcessing/Shaders/Colors.hlsl （WhiteBalance/LogC変換/Contrast/Saturation/NeutralTonemap）
//   - PostProcessing/Runtime/Utils/ColorUtilities.cs （温度・Tintから_ColorBalanceベクトルへの変換）
//   - PostProcessing/Runtime/Effects/ColorGrading.cs （UIパラメータの実際のスケール変換）
//
// 対応範囲の限界（正直に明記）:
//   - gradingMode=HighDefinitionRange かつ tonemapper=None/Neutral の経路のみ実装。
//     tonemapper=ACESが選択されている場合は全く別のACEScc/ACEScgパイプラインが必要になるため
//     未対応（該当時はトーンマップ段をスキップし、Contrast/Saturation/WhiteBalanceのみ適用）。
//   - ChannelMixer・Lift/Gamma/Gain・Hue系カーブ(HueVsHue/SatVsSat/LumVsSat)は、このプロジェクトの
//     実シーンではUnity既定値(恒等変換)であることを実機確認した上で未実装(恒等として扱う)。
//     これらを実際に編集しているプロジェクトでは近似精度が落ちる。
public static class PPv2ToneMapMath
{
    // --- LogC (ARRI LogC相当、Colors.hlslのParamsLogC定数と完全一致) ---
    const float LogC_cut = 0.011361f;
    const float LogC_a = 5.555556f;
    const float LogC_b = 0.047996f;
    const float LogC_c = 0.244161f;
    const float LogC_d = 0.386036f;
    const float LogC_e = 5.301883f;
    const float LogC_f = 0.092819f;

    // Contrastのピボット。Lut3DBaker.compute の LogGrade() が使う定数(ACES.hlsl)そのもの。
    // 以前のColorGradingApproximation.csでは「線形空間で0.5」としていたが誤りで、実際は
    // LogC空間でこの値がピボットに使われている。
    const float ACEScc_MIDGRAY = 0.4135884f;

    static readonly Matrix4x4 LIN_2_LMS_MAT = MakeMatrix(
        3.90405e-1f, 5.49941e-1f, 8.92632e-3f,
        7.08416e-2f, 9.63172e-1f, 1.35775e-3f,
        2.31082e-2f, 1.28021e-1f, 9.36245e-1f);

    static readonly Matrix4x4 LMS_2_LIN_MAT = MakeMatrix(
        2.85847e+0f, -1.62879e+0f, -2.48910e-2f,
        -2.10182e-1f, 1.15820e+0f, 3.24281e-4f,
        -4.18120e-2f, -1.18169e-1f, 1.06867e+0f);

    static Matrix4x4 MakeMatrix(
        float m00, float m01, float m02,
        float m10, float m11, float m12,
        float m20, float m21, float m22)
    {
        var m = Matrix4x4.identity;
        m.m00 = m00; m.m01 = m01; m.m02 = m02;
        m.m10 = m10; m.m11 = m11; m.m12 = m12;
        m.m20 = m20; m.m21 = m21; m.m22 = m22;
        return m;
    }

    static Vector3 MulMat(Matrix4x4 m, Vector3 v) => new Vector3(
        m.m00 * v.x + m.m01 * v.y + m.m02 * v.z,
        m.m10 * v.x + m.m11 * v.y + m.m12 * v.z,
        m.m20 * v.x + m.m21 * v.y + m.m22 * v.z);

    public struct ResolvedSettings
    {
        public bool Found;
        public bool TonemapperSupported; // false = ACES等、未対応トーンマッパー
        public bool ApplyTonemap;

        public Vector3 ColorBalance; // WhiteBalance()に渡す係数（CAT02 LMS空間）
        public Vector3 ColorFilter;
        public float ContrastMultiplier;
        public float SaturationMultiplier;
        public float HueShift01; // 0..1 (360度を1に正規化)
    }

    static bool s_resolved;
    static ResolvedSettings s_settings;

    // 2026-08-08 (Tanossy指摘「コントラストが足りない」): シーンのPost Processing Volumeの
    // 実測値はcontrast=2(PPv2スライダー値)——実際の乗数に直すと`2/100+1=1.02`とほぼ無効に近い。
    // Unity側はAmbient Occlusion/Bloom等トーンマッピング以外の要因で見た目のメリハリが出ている
    // 可能性が高く、シーンのPPv2設定をそのまま模しても同じ迫力は出ない。Resonite送信専用の
    // 追加コントラスト係数をここに設け、シーンの(ほぼ無効な)ContrastMultiplierに掛け合わせる。
    // 1.0=追加なし。1.4は最初の実機検証値。
    public static float ResoniteExtraContrast = 1.4f;

    public static ResolvedSettings GetSettings()
    {
        if (!s_resolved)
            Resolve();

        return s_settings;
    }

    public static void Invalidate() => s_resolved = false;

    static void Resolve()
    {
        s_resolved = true;
        s_settings = default;

#if UNITY_2023_1_OR_NEWER
        var volumes = Object.FindObjectsByType<PostProcessVolume>(FindObjectsSortMode.None);
#else
        var volumes = Object.FindObjectsOfType<PostProcessVolume>();
#endif

        foreach (var volume in volumes)
        {
            if (!volume.isGlobal || volume.sharedProfile == null)
                continue;

            if (!volume.sharedProfile.TryGetSettings<ColorGrading>(out var cg) || !cg.enabled.value)
                continue;

            s_settings.Found = true;

            s_settings.ContrastMultiplier = (cg.contrast.value / 100f + 1f) * ResoniteExtraContrast;
            s_settings.SaturationMultiplier = cg.saturation.value / 100f + 1f;
            s_settings.HueShift01 = cg.hueShift.value / 360f;
            s_settings.ColorFilter = new Vector3(cg.colorFilter.value.r, cg.colorFilter.value.g, cg.colorFilter.value.b);
            s_settings.ColorBalance = ComputeColorBalance(cg.temperature.value, cg.tint.value);

            s_settings.TonemapperSupported = cg.tonemapper.value == Tonemapper.Neutral || cg.tonemapper.value == Tonemapper.None;
            s_settings.ApplyTonemap = cg.tonemapper.value == Tonemapper.Neutral;

            break; // 最初に見つかったグローバルボリュームを採用(PostProcessingConverterと同方針)
        }
    }

    // ColorUtilities.ComputeColorBalance()の直接移植（推測なし）。
    static Vector3 ComputeColorBalance(float temperature, float tint)
    {
        float t1 = temperature / 60f;
        float t2 = tint / 60f;

        float x = 0.31271f - t1 * (t1 < 0f ? 0.1f : 0.05f);
        float y = StandardIlluminantY(x) + t2 * 0.05f;

        var w1 = new Vector3(0.949237f, 1.03542f, 1.08728f);
        var w2 = CIExyToLMS(x, y);
        return new Vector3(w1.x / w2.x, w1.y / w2.y, w1.z / w2.z);
    }

    static float StandardIlluminantY(float x) => 2.87f * x - 3f * x * x - 0.27509507f;

    static Vector3 CIExyToLMS(float x, float y)
    {
        float Y = 1f;
        float X = Y * x / y;
        float Z = Y * (1f - x - y) / y;

        float L = 0.7328f * X + 0.4296f * Y - 0.1624f * Z;
        float M = -0.7036f * X + 1.6975f * Y + 0.0061f * Z;
        float S = 0.0030f * X + 0.0136f * Y + 0.9834f * Z;

        return new Vector3(L, M, S);
    }

    static Vector3 WhiteBalance(Vector3 c, Vector3 balance)
    {
        var lms = MulMat(LIN_2_LMS_MAT, c);
        lms = Vector3.Scale(lms, balance);
        return MulMat(LMS_2_LIN_MAT, lms);
    }

    static Vector3 LinearToLogC(Vector3 x) => new Vector3(
        LogC_c * Mathf.Log10(LogC_a * x.x + LogC_b) + LogC_d,
        LogC_c * Mathf.Log10(LogC_a * x.y + LogC_b) + LogC_d,
        LogC_c * Mathf.Log10(LogC_a * x.z + LogC_b) + LogC_d);

    static Vector3 LogCToLinear(Vector3 x) => new Vector3(
        (Mathf.Pow(10f, (x.x - LogC_d) / LogC_c) - LogC_b) / LogC_a,
        (Mathf.Pow(10f, (x.y - LogC_d) / LogC_c) - LogC_b) / LogC_a,
        (Mathf.Pow(10f, (x.z - LogC_d) / LogC_c) - LogC_b) / LogC_a);

    static float Luminance(Vector3 c) => Vector3.Dot(c, new Vector3(0.2126729f, 0.7151522f, 0.0721750f));

    static Vector3 Contrast(Vector3 c, float midpoint, float contrast) =>
        (c - new Vector3(midpoint, midpoint, midpoint)) * contrast + new Vector3(midpoint, midpoint, midpoint);

    static Vector3 Saturation(Vector3 c, float sat)
    {
        float luma = Luminance(c);
        return new Vector3(luma, luma, luma) + sat * (c - new Vector3(luma, luma, luma));
    }

    // Colors.hlsl の NeutralCurve()/NeutralTonemap() の直接移植（定数含め推測なし）。
    static float NeutralCurveScalar(float x, float a, float b, float c, float d, float e, float f) =>
        ((x * (a * x + c * b) + d * e) / (x * (a * x + b) + d * f)) - e / f;

    static Vector3 NeutralCurve(Vector3 x, float a, float b, float c, float d, float e, float f) => new Vector3(
        NeutralCurveScalar(x.x, a, b, c, d, e, f),
        NeutralCurveScalar(x.y, a, b, c, d, e, f),
        NeutralCurveScalar(x.z, a, b, c, d, e, f));

    public static Vector3 NeutralTonemap(Vector3 x)
    {
        const float a = 0.2f, b = 0.29f, c = 0.24f, d = 0.272f, e = 0.02f, f = 0.3f;
        const float whiteLevel = 5.3f;
        const float whiteClip = 1.0f;

        float whiteScaleScalar = 1f / NeutralCurveScalar(whiteLevel, a, b, c, d, e, f);
        var whiteScale = new Vector3(whiteScaleScalar, whiteScaleScalar, whiteScaleScalar);

        x = NeutralCurve(Vector3.Scale(x, whiteScale), a, b, c, d, e, f);
        x = Vector3.Scale(x, whiteScale);
        x /= whiteClip;

        return x;
    }

    // Lut3DBaker.compute の LogGrade()→LUT_SPACE_DECODE→LinearGrade()→Tonemap の順序を、
    // HueVsHue/SatVsSat/LumVsSatカーブとChannelMixer/Lift-Gamma-Gainを恒等とみなした上でそのまま
    // 移植したもの。入力・出力ともリニア空間の色。
    public static Vector3 ApplyGrading(Vector3 linearColor)
    {
        var settings = GetSettings();

        if (!settings.Found)
            return linearColor;

        // LogGrade: LogC空間でコントラストを適用
        var logSpace = LinearToLogC(linearColor);
        logSpace = Contrast(logSpace, ACEScc_MIDGRAY, settings.ContrastMultiplier);

        // Linear空間へ戻してWhiteBalance・ColorFilter・Saturationを適用
        var c = LogCToLinear(logSpace);
        c = WhiteBalance(c, settings.ColorBalance);
        c = Vector3.Scale(c, settings.ColorFilter);
        c = new Vector3(Mathf.Max(0f, c.x), Mathf.Max(0f, c.y), Mathf.Max(0f, c.z));
        c = Saturation(c, settings.SaturationMultiplier);

        if (settings.ApplyTonemap)
            c = NeutralTonemap(c);

        return new Vector3(Mathf.Max(0f, c.x), Mathf.Max(0f, c.y), Mathf.Max(0f, c.z));
    }

    // 2026-08-08 (Tanossy指摘「茶色味が足りない」): ApplyGrading()フル適用(Contrast込み)は
    // ContrastMultiplier(1.4×約1.02≈1.428)が「_Color=白(1,1,1)」のマテリアルを更に白へ
    // 押し上げてしまい(ピボットACEScc_MIDGRAY=0.4136より上にある値はcontrastを掛けるほど
    // 明るい側へ)、テクスチャ本来の色を洗い流す逆効果だったため、MaterialGradingEnabledごと
    // 無効化していた。しかしそれによりUnity元シーンのSaturation設定(=10 → 1.1倍という
    // 穏やかな彩度ブースト)も道連れで失われ、ソファの茶色が「素の彩度」のまま出ていた。
    // Contrast/WhiteBalance/Tonemapは経由せず、Saturationだけを独立して適用する。
    public static Vector3 ApplySaturationOnly(Vector3 linearColor)
    {
        var settings = GetSettings();

        if (!settings.Found)
            return linearColor;

        return Saturation(linearColor, settings.SaturationMultiplier);
    }

    // Reflection Probeの強度を代表輝度における実際のNeutralTonemap圧縮率で減衰させる。
    // 単一のスカラー係数でカーブ全体を正確に再現することはできない(低輝度はほぼ無圧縮・
    // 高輝度ほど強く圧縮される非線形カーブのため)が、何も補正しないよりは実際の計算式に
    // 基づいた値になる。代表輝度は「そこそこ明るい反射」を想定しリニア値1.0を採用。
    public static float ComputeReflectionProbeCompensationFactor()
    {
        var settings = GetSettings();

        if (!settings.Found || !settings.ApplyTonemap)
            return 1f;

        const float representativeBrightness = 1.0f;
        var input = new Vector3(representativeBrightness, representativeBrightness, representativeBrightness);
        var toneMapped = NeutralTonemap(input);

        float ratio = Luminance(toneMapped) / representativeBrightness;

        return Mathf.Clamp(ratio, 0.05f, 1f);
    }
}
