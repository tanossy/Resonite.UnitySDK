using UnityEngine;

public static class ReflectionProbeHelper
{
    public static void SetFrom(this FrooxEngine.ReflectionProbe resonite, UnityEngine.ReflectionProbe unity, IConversionContext context)
    {
        resonite.SetFrom((UnityEngine.Behaviour)unity);

        switch (unity.mode)
        {
            case UnityEngine.Rendering.ReflectionProbeMode.Baked:
                resonite.ProbeType = Renderite.Shared.ReflectionProbeType.Baked;
                break;

            case UnityEngine.Rendering.ReflectionProbeMode.Realtime:
                resonite.ProbeType = Renderite.Shared.ReflectionProbeType.Realtime;
                break;

            case UnityEngine.Rendering.ReflectionProbeMode.Custom:
                // This is the closest equivalent, but whatever logic is triggering the custom one won't likely
                // translate, so this might need additional work
                resonite.ProbeType = Renderite.Shared.ReflectionProbeType.OnChanges;
                break;
        }

        switch (unity.timeSlicingMode)
        {
            case UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.NoTimeSlicing:
                resonite.TimeSlicing = Renderite.Shared.ReflectionProbeTimeSlicingMode.NoTimeSlicing;
                break;

            case UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces:
                resonite.TimeSlicing = Renderite.Shared.ReflectionProbeTimeSlicingMode.IndividualFaces;
                break;

            case UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.AllFacesAtOnce:
                resonite.TimeSlicing = Renderite.Shared.ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
                break;
        }

        resonite.Importance = unity.importance;

        // 2026-08-08 (Tanossy指摘「Reflection Probeが強すぎる」への対応、案2):
        // Resonite(Renderite)は主カメラでポスト処理のトーンマッピングを一切行わない
        // (2026-07-30 Renderite.Unity.Renderer調査で確定)。Unity側ではHDRの明るい反射成分が
        // Scene ViewのPPv2 NeutralTonemapperで圧縮されて見えるが、Resonite側はそれが無いため
        // 同じIntensity値でも反射が生の輝度のまま出て「ぎらつく」。単一スカラー係数でカーブ全体を
        // 正確に再現することはできないが(詳細はPPv2ToneMapMath.ComputeReflectionProbeCompensationFactor
        // のコメント参照)、実際のNeutralTonemap計算式に基づいた減衰係数を掛けることで近似する。
        // ToneMapCompensationState.Enabled = false で無効化可能。
        resonite.Intensity = unity.intensity * (ToneMapCompensationState.Enabled
            ? PPv2ToneMapMath.ComputeReflectionProbeCompensationFactor()
            : 1f);

        resonite.BlendDistance = unity.blendDistance;
        resonite.BoxSize = unity.size;
        resonite.BoxProjection = unity.boxProjection;

        resonite.Resolution = unity.resolution;
        resonite.HDR = unity.hdr;

        resonite.ShadowDistance = unity.shadowDistance;

        resonite.ClearFlags = unity.clearFlags.ToResoniteLink();
        resonite.BackgroundColor = unity.backgroundColor.ToColorX_Auto();

        resonite.NearClip = unity.nearClipPlane;
        resonite.FarClip = unity.farClipPlane;

        // If the probe has everything culled, then we can consider it skybox only
        resonite.SkyboxOnly = unity.cullingMask == 0;

        if (resonite.ProbeType == Renderite.Shared.ReflectionProbeType.Baked)
            resonite.BakedCubemap = context.GetCubemap(unity.bakedTexture as UnityEngine.Cubemap ?? unity.customBakedTexture as UnityEngine.Cubemap);
        else
            resonite.BakedCubemap = null;
    }
}


public class ReflectionProbeConverter : ResoniteSingleComponentConverter<ReflectionProbe, FrooxEngine.ReflectionProbeWrapper>
{
    protected override void UpdateConversion(ReflectionProbe target, IConversionContext context)
    {
        Binding.Data.SetFrom(target, context);
    }
}