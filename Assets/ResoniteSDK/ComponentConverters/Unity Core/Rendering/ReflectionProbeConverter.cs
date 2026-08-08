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

        // 2026-08-08 (per Tanossy's feedback: "Reflection Probe intensity is too strong", option 2):
        // Resonite (Renderite) doesn't apply any post-processing tonemapping on the main camera
        // (confirmed via the 2026-07-30 investigation of Renderite.Unity.Renderer). On the Unity side,
        // the bright HDR reflection components appear compressed in the Scene View by PPv2's
        // NeutralTonemapper, but since Resonite has nothing equivalent, the reflection comes through at
        // its raw brightness even with the same Intensity value, and looks "glaring". A single scalar
        // coefficient can't exactly reproduce the entire curve (see the comment on
        // PPv2ToneMapMath.ComputeReflectionProbeCompensationFactor for details), but we approximate it
        // by multiplying in an attenuation coefficient based on the actual NeutralTonemap formula.
        // Can be disabled via ToneMapCompensationState.Enabled = false.
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