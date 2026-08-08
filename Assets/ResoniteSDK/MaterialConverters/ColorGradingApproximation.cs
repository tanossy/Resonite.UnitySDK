using UnityEngine;

// 2026-07-27 (per Tanossy's feedback): the PostProcessingSettings component on the Resonite side only
// has 5 fields — Bloom/AO/MotionBlur/SSR/Antialiasing — and has no field corresponding to Color Grading
// (Contrast/Saturation/Tonemapping) (confirmed 2026-07-12 via the scope note in
// PostProcessingConverter.cs). Since it's impossible to reproduce this as a screen effect, we instead
// approximate Unity's color grading/tonemap effect by baking it directly into each material's
// Albedo/Emissive color at conversion time.
//
// 2026-08-08 update (per Tanossy's feedback: "reflections are glaring, Reflection Probe intensity is
// too strong", option 2): the actual calculation (WhiteBalance, Contrast in LogC space, Saturation,
// NeutralTonemap) has been moved to Assets/ResoniteSDK/ToneMapCompensation/PPv2ToneMapMath.cs (a folder
// independent of the main SDK), and this file is kept as a thin wrapper acting as the call site (no
// change needed to the existing call sites in StandardBaseConverter.cs / BakedLightmapStandardConverter.cs).
// The old implementation (Contrast in linear space with a pivot of 0.5, no tonemapper reproduction) was
// inaccurate, so it was replaced. Setting ToneMapCompensationState.Enabled = false disables this
// approximation entirely (controlled via the "Send Tonemap Compensation" toggle in the Resonite SDK
// Manager panel).
//
// 2026-08-08 further (per Tanossy's feedback: "the sofa should be darker/browner, the black wall and
// picture frame aren't dark enough either"): materials in this scene are almost universally
// `_Color = white (1,1,1)`, with the actual color coming from the texture. Because the contrast boost
// (PPv2ToneMapMath.ResoniteExtraContrast) pushes "values above the near-white pivot" even further
// toward brighter, it had the reverse effect of making the white `_Color` tag itself even whiter and
// washing out the texture's original deep browns and blacks. We keep the Reflection Probe intensity
// correction (ToneMapCompensationState.Enabled) enabled, while making it possible to disable just the
// grading applied to material colors via a separate flag (default OFF).
// 2026-08-08 further2 (per Tanossy's feedback: "not enough brown tone"): when fully disabled via
// MaterialGradingEnabled=false above, the original Unity scene's Saturation setting (=10 -> a mild
// 1.1x saturation boost) was also lost along with the Contrast side effect. Switched, after on-device
// testing, to PPv2ToneMapMath.ApplySaturationOnly(), which applies only Saturation on its own without
// going through Contrast/WhiteBalance/Tonemap.
// To go back to the original behavior (MaterialGradingEnabled=false, full ApplyGrading()), it's enough
// to replace the body of Apply() below with either `return linearColor;` (same effect as
// MaterialGradingEnabled=false) or `PPv2ToneMapMath.ApplyGrading(c)` (full application).
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
