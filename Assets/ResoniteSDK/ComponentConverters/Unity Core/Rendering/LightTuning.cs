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
    //
    // 2026-08-09 replaced entirely (per Tanossy's feedback, after a real incident): a fixed
    // multiplier tuned for one scene (bedroom, native Light.intensity around 0.5) turned out to
    // wildly overexpose a different scene sent later (a restaurant/diner scene whose Directional
    // Light was natively 5.0) - 5.0 * 1.8 = 9.0, badly blown out. Fixed multipliers don't
    // generalize across scenes with very different native brightness scales.
    //
    // Replaced with a self-normalizing ceiling: every send, the scene's single brightest Light
    // is found, and the effective multiplier is computed so THAT light lands exactly at
    // IntensityCeiling; every other light in the scene is scaled by that same ratio (so relative
    // brightness between lights within one scene is preserved, only the overall scale changes).
    // This directly fixes the "sun goes from 5.0 to 9.0" failure mode, since the multiplier now
    // adapts per-scene instead of being reused blindly.
    //
    // Known remaining gap: this only bounds the single brightest light, not the *cumulative*
    // brightness of many lights added together (e.g. a scene with dozens of moderate-intensity
    // fill lights, all individually under the ceiling, can still sum to an overexposed result -
    // this was also a contributing factor in the incident above, via a scene's dense
    // "AmbientLights" fill-light group, and is not solved here).
    //
    // Starting value carried over from the previous fixed-multiplier tuning (bedroom scene's
    // Point Lights: native ~0.5 * old multiplier 1.8 = 0.9). Not yet re-verified live against
    // either scene under this new formula - Unity MCP was disconnected when this was written.
    public static float IntensityCeiling = 0.9f;

    // 2026-08-08 (per Tanossy's feedback: "too much yellow, want to boost the whiteness"): the
    // room's light color is being transferred straight through from Unity as a warm tone (roughly
    // ColorX(1, 0.89, 0.75)), and this reads as more yellow than intended once it's in Resonite.
    // The light color itself is blended toward white at send time only (0 = Unity's color
    // unchanged, 1 = pure white). Finalized at 0.4 -> 0.7.
    //
    // 2026-08-24 (per Tanossy's feedback: opposite direction - "barely any yellow left, looks
    // almost white"): confirmed live (Point Light color read back as ColorX(1, 0.979, 0.933) at
    // 0.7), and 0.7 also left almost no headroom for the separate in-world Light Tuning Panel's
    // own White Balance slider (which only Lerps further from whatever baseline was actually
    // sent - it can't recover color this static field already blended away before send).
    // 0.7 itself turned out to be too strong; 0.4 (the value it replaced) was rejected earlier
    // for the opposite reason ("too much yellow"). Split the difference to 0.55 as a starting
    // point bracketed by both known-bad real-machine data points - still unverified at this
    // exact value, re-check live after the next send (the Lightmap Pipeline panel's own
    // "Send-Time Light Tuning" slider can be nudged further without a code edit).
    //
    // Known limitation (2026-08-08, explained to the user and agreed to leave as-is for now):
    // since this Lerps uniformly toward white regardless of the original hue, if a scene has
    // multiple light sources of different colors, their original color differences get equally
    // diluted (this world only has a single warm-toned lighting scheme, so it was judged to cause
    // no real harm; properly addressing it is deferred to a future pass).
    public static float WhiteBalanceShift = 0.55f;

    public static float ApplyIntensity(float unityIntensity) => unityIntensity * GetEffectiveIntensityMultiplier();

    public static Color ApplyColor(Color unityColor) => Color.Lerp(unityColor, Color.white, Mathf.Clamp01(WhiteBalanceShift));

    // Recomputed from the live scene on every call rather than cached: this only ever runs
    // during an Editor-time scene conversion (never per-frame/runtime), so even a few hundred
    // lights costs nothing worth caching for, and a fresh scan avoids any risk of a stale cached
    // max surviving a scene edit between sends.
    static float GetEffectiveIntensityMultiplier()
    {
        float sceneMax = GetSceneMaxLightIntensity();

        // No positive-intensity lights found (empty scene, or every light at 0) - nothing to
        // scale against. Pass through unchanged rather than dividing by zero.
        if (sceneMax <= 0f)
            return 1f;

        return IntensityCeiling / sceneMax;
    }

    static float GetSceneMaxLightIntensity()
    {
        float max = 0f;

        foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
        {
            if (light != null && light.intensity > max)
                max = light.intensity;
        }

        return max;
    }
}
