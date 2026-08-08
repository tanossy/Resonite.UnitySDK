using UnityEngine;

// 2026-08-08 (per Tanossy's feedback: "I want to touch the original upstream source as little as
// possible, so factor this out"): send-time tuning values used to replace just the two
// Intensity/Color assignment lines inside LightHelper.SetFrom() in the official LightConverter.cs.
// The tuning values and logic have been factored out entirely into this new file, so that the
// only change needed on the official file's side is swapping in these two lines:
// `resonite.Intensity = LightTuning.ApplyIntensity(unity.intensity);` /
// `resonite.Color = new ColorX(LightTuning.ApplyColor(unity.color));`
public static class LightTuning
{
    // 2026-08-08 (in response to Tanossy's feedback of "too dark"): in Unity, Light.intensity=0.5
    // already looks appropriately bright live (confirmed with a combination of indirect baking +
    // real lights, verified in the same scene at the same camera angle), whereas sending that
    // same value straight through makes it noticeably darker in Resonite. Raising the baked-data
    // additive fill (BakedLightmapStandardConverter.AdditiveFillStrength) hit a ceiling, so the
    // cause was attributed to differences between Unity and Resonite in Point/Spot Light falloff
    // curves and luminance conversion, and a send-time-only multiplier was added here instead.
    // 3.0 was the first live-verified value and is not final.
    //
    // 2026-08-08 addendum (per Tanossy's feedback: "it's black in Unity but not black in
    // Resonite"): 3.0 caused excessive reflections on specular-dominant materials such as TV
    // screens, metal frames, and glass, with the side effect that surfaces which looked black in
    // Unity appeared bright and floating in Resonite (materials with a high Metallic value in
    // particular have almost no diffuse reflection, so nearly everything you see is specular
    // reflection, which no amount of darkening the Albedo can cancel out). Lowered to 1.8.
    // Afterward, once the user confirmed "so the importer is matching the light brightness as a
    // multiplier of the original brightness?" and was satisfied that it's simply a multiplier, it
    // was readjusted to 2.5.
    //
    // 2026-08-08 further addendum (per Tanossy's feedback: "it's too bright, maybe reduce how
    // much point light brightness gets increased"): raised during re-verification right after the
    // factor-out. Per-light-type differentiation (a separate multiplier just for Point lights)
    // was offered as an option, but simplicity was prioritized and this single shared value for
    // all lights was set back to 1.8.
    public static float IntensityMultiplier = 1.8f;

    // 2026-08-08 (per Tanossy's feedback: "too much yellow, want to boost the whiteness"): the
    // room's light color is being transferred straight through from Unity as a warm tone (roughly
    // ColorX(1, 0.89, 0.75)), and this reads as more yellow than intended once it's in Resonite.
    // The light color itself is blended toward white at send time only (0 = Unity's color
    // unchanged, 1 = pure white). Finalized at 0.4 -> 0.7.
    //
    // Known limitation (2026-08-08, explained to the user and agreed to leave as-is for now):
    // since this Lerps uniformly toward white regardless of the original hue, if a scene has
    // multiple light sources of different colors, their original color differences get equally
    // diluted (this world only has a single warm-toned lighting scheme, so it was judged to cause
    // no real harm; properly addressing it is deferred to a future pass).
    public static float WhiteBalanceShift = 0.7f;

    public static float ApplyIntensity(float unityIntensity) => unityIntensity * IntensityMultiplier;

    public static Color ApplyColor(Color unityColor) => Color.Lerp(unityColor, Color.white, Mathf.Clamp01(WhiteBalanceShift));
}
