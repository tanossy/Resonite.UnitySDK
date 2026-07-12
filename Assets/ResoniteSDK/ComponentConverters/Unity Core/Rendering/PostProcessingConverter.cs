using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

// Converts a Unity "Post Processing Stack v2" (com.unity.postprocessing) Global Volume into
// Resonite's FrooxEngine.PostProcessingSettings component.
//
// SCOPE - READ BEFORE EXTENDING THIS FILE
// ----------------------------------------
// Resonite's PostProcessingSettings component only exposes 5 fields (verified 2026-07-12 via
// reflection against Resonite.UnitySDK.Bindings.dll - see FrooxEngine.PostProcessingSettings):
//
//     MotionBlurIntensity          float
//     BloomIntensity               float
//     AmbientOcclusionIntensity    float
//     ScreenSpaceReflections       bool
//     Antialiasing                 Renderite.Shared.AntiAliasingMethod
//
// There is NO Resonite-side equivalent for Color Grading / Vignette / Depth of Field / Exposure,
// so those Unity Post Processing effects are intentionally NOT read or converted here.
//
// Unity <-> Resonite quirks handled below:
//  - PPv2's MotionBlur effect has no "intensity" parameter, only `shutterAngle` (0-360) and
//    `sampleCount`. We approximate MotionBlurIntensity as shutterAngle / 360f (0-1 range), since
//    that's the closest analogue to a blur "strength" dial. This is an approximation, not a 1:1
//    mapping - there is no ground truth to verify this against without a live Resonite client.
//  - PPv2 has no dedicated on/off flag for the "amount" of screen space reflections - we map
//    ScreenSpaceReflections to whether the SSR effect is enabled (added + `enabled.value == true`)
//    in the profile, which is the closest match to Resonite's boolean field.
//  - Antialiasing in PPv2 is configured on `PostProcessLayer`, which lives on the CAMERA, not on
//    the PostProcessVolume/Profile. We do a best-effort scene-wide lookup for the first
//    PostProcessLayer found to source this value. If none is found, Antialiasing is left Off.
//  - Only Global volumes (PostProcessVolume.isGlobal == true) are converted to
//    PostProcessingSettings data, because Resonite has no concept of a local/collider-bound
//    post-processing volume. If more than one Global volume exists in the scene, the first one
//    encountered during conversion wins (first-wins) and a warning is logged for the rest; those
//    extra volumes still get a PostProcessingSettingsWrapper attached (standard 1:1 conversion
//    pattern used across this SDK) but its fields are left at engine defaults.
//  - Local/range-limited (isGlobal == false) volumes are explicitly OUT OF SCOPE for this pass.
//    They also get a PostProcessingSettingsWrapper attached 1:1 to match SDK conventions, but its
//    fields are intentionally left untouched (engine defaults) - Resonite has nothing to bind a
//    trigger volume's blend radius/weight to.
//
// UNVERIFIED: whether the resulting PostProcessingSettings slot needs to sit under Root vs
// UserRoot (or elsewhere) to actually apply world-wide inside a live Resonite session was NOT
// checked against a running client while writing this file (offline at the time). The converter
// simply attaches the component to whatever Slot the source PostProcessVolume's Transform maps
// to, same as every other 1:1 converter in this SDK - verify placement/effect in-world before
// relying on it.
public static class PostProcessingSettingsHelper
{
    public static void SetFrom(this FrooxEngine.PostProcessingSettings resonite, PostProcessVolume unity, IConversionContext context)
    {
        var profile = unity.sharedProfile;

        if (profile == null)
        {
            Debug.LogWarning($"[PostProcessingConverter] Global Post Process Volume '{unity.name}' has no profile assigned, skipping PostProcessingSettings conversion.");
            return;
        }

        // Bloom
        if (profile.TryGetSettings<Bloom>(out var bloom) && bloom.enabled.value)
            resonite.BloomIntensity = bloom.intensity.value;
        else
            resonite.BloomIntensity = 0f;

        // Ambient occlusion
        if (profile.TryGetSettings<AmbientOcclusion>(out var ao) && ao.enabled.value)
            resonite.AmbientOcclusionIntensity = ao.intensity.value;
        else
            resonite.AmbientOcclusionIntensity = 0f;

        // Motion blur - PPv2 has no direct "intensity" parameter, approximate from shutterAngle.
        // See scope note at the top of this file for why this is an approximation.
        if (profile.TryGetSettings<MotionBlur>(out var motionBlur) && motionBlur.enabled.value)
            resonite.MotionBlurIntensity = Mathf.Clamp01(motionBlur.shutterAngle.value / 360f);
        else
            resonite.MotionBlurIntensity = 0f;

        // Screen space reflections - Resonite only has an on/off toggle, so we map it to whether
        // the effect is present and enabled in the profile.
        resonite.ScreenSpaceReflections = profile.TryGetSettings<ScreenSpaceReflections>(out var ssr) && ssr.enabled.value;

        // Antialiasing lives on PostProcessLayer (camera-side), not on the volume/profile.
        resonite.Antialiasing = ResolveAntialiasing();
    }

    static Renderite.Shared.AntiAliasingMethod ResolveAntialiasing()
    {
        // Best-effort: PPv2 puts antialiasing on whichever camera's PostProcessLayer is
        // configured, not on the volume. We just take the first one found in the scene.
        // If there are multiple cameras with different antialiasing modes, this will pick an
        // arbitrary one - that's an accepted simplification for this pass.
#if UNITY_2023_1_OR_NEWER
        var layer = Object.FindFirstObjectByType<PostProcessLayer>();
#else
        var layer = Object.FindObjectOfType<PostProcessLayer>();
#endif

        if (layer == null)
            return Renderite.Shared.AntiAliasingMethod.Off;

        switch (layer.antialiasingMode)
        {
            case PostProcessLayer.Antialiasing.None:
                return Renderite.Shared.AntiAliasingMethod.Off;

            case PostProcessLayer.Antialiasing.FastApproximateAntialiasing:
                return Renderite.Shared.AntiAliasingMethod.FXAA;

            case PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing:
                return Renderite.Shared.AntiAliasingMethod.SMAA;

            case PostProcessLayer.Antialiasing.TemporalAntialiasing:
                return Renderite.Shared.AntiAliasingMethod.TAA;

            default:
                // Resonite's CTAA has no Unity/PPv2 equivalent, so it's unreachable here.
                return Renderite.Shared.AntiAliasingMethod.Off;
        }
    }
}

public class PostProcessingConverter : ResoniteSingleComponentConverter<PostProcessVolume, FrooxEngine.PostProcessingSettingsWrapper>
{
    // Tracks which Global volume "won" the first-wins policy for this editor session. Unity's
    // overloaded `== null` check correctly detects destroyed volumes (e.g. after switching
    // scenes), so a stale claim is naturally released when its volume goes away.
    static PostProcessVolume s_claimedGlobalVolume;

    // Avoids spamming the console every single time UpdateConversion runs (e.g. during Realtime
    // Mode) for a volume we've already warned about.
    static readonly System.Collections.Generic.HashSet<PostProcessVolume> s_warnedVolumes = new System.Collections.Generic.HashSet<PostProcessVolume>();

    protected override void UpdateConversion(PostProcessVolume target, IConversionContext context)
    {
        // Local/range-limited (collider-bound) volumes are out of scope - see the scope note at
        // the top of this file. We leave the auto-attached PostProcessingSettingsWrapper at
        // engine defaults for these.
        if (!target.isGlobal)
            return;

        if (s_claimedGlobalVolume != null && s_claimedGlobalVolume != target)
        {
            if (s_warnedVolumes.Add(target))
                Debug.LogWarning($"[PostProcessingConverter] Multiple Global Post Process Volumes found in the scene. " +
                    $"'{s_claimedGlobalVolume.name}' was already converted as the world-wide PostProcessingSettings; " +
                    $"'{target.name}' will be skipped (first-wins policy). Resonite has no concept of multiple " +
                    $"world-wide post processing configs.");
            return;
        }

        s_claimedGlobalVolume = target;

        Binding.Data.SetFrom(target, context);
    }
}
