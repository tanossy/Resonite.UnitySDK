using UnityEngine;

public static class LightHelper
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
    // 打ち消せない)。1.8へ引き下げ。
    public static float IntensityMultiplier = 2.5f;

    // 2026-08-08 (Tanossy指摘「黄身が多い、白味を上げたい」): 部屋の光源色がUnity側のまま
    // 暖色(ColorX(1, 0.89, 0.75)相当)で転送されており、Resonite側でこれが実際より黄色く
    // 感じられている。光源色そのものを送信時だけ白側へブレンドする(0=Unityの色そのまま、
    // 1=純白)。0.4は最初の実機検証値。
    public static float WhiteBalanceShift = 0.7f;

    public static void SetFrom(this FrooxEngine.Light resonite, UnityEngine.Light unity, IConversionContext context)
    {
        // Set the basics
        resonite.SetFrom((UnityEngine.Behaviour)unity);

        switch (unity.type)
        {
            case UnityEngine.LightType.Point:
                resonite.LightType = Renderite.Shared.LightType.Point;
                break;

            case UnityEngine.LightType.Spot:
                resonite.LightType = Renderite.Shared.LightType.Spot;
                break;

            case UnityEngine.LightType.Directional:
                resonite.LightType = Renderite.Shared.LightType.Directional;
                break;

            default:
                // Not supported, set it to invalid value
                resonite.LightType = (Renderite.Shared.LightType)(255);
                break;
        }

        resonite.Intensity = unity.intensity * IntensityMultiplier;
        var whiteBalancedColor = Color.Lerp(unity.color, Color.white, Mathf.Clamp01(WhiteBalanceShift));
        resonite.Color = new ColorX(whiteBalancedColor);

        switch (unity.shadows)
        {
            case UnityEngine.LightShadows.None:
                resonite.ShadowType = Renderite.Shared.ShadowType.None;
                break;

            case UnityEngine.LightShadows.Hard:
                resonite.ShadowType = Renderite.Shared.ShadowType.Hard;
                break;

            case UnityEngine.LightShadows.Soft:
                resonite.ShadowType = Renderite.Shared.ShadowType.Soft;
                break;
        }

        resonite.ShadowStrength = unity.shadowStrength;
        resonite.ShadowNearPlane = unity.shadowNearPlane;
        resonite.ShadowMapResolution = unity.shadowCustomResolution;
        resonite.ShadowBias = unity.shadowBias;
        resonite.ShadowNormalBias = unity.shadowNormalBias;

        resonite.Range = unity.range;
        resonite.SpotAngle = unity.spotAngle;

        resonite.Cookie = context.GetITexture(unity.cookie);
    }
}

public class LightConverter : ResoniteSingleComponentConverter<Light, FrooxEngine.LightWrapper>
{
    protected override void UpdateConversion(Light target, IConversionContext context)
    {
        // We just assign the data
        Binding.Data.SetFrom(target, context);
    }
}
