using System;
using UnityEditor;
using UnityEngine;

// LightmapExposurePolicy.cs — closes the brightness loop at send time (2026-09-08).
//
// THE PROBLEM (Tanossy: "I calibrated the output brightness before, so what keeps moving?"):
//   Brightness in Resonite is a product of five hand-set constants in series —
//       bake radiance
//         x LightmapDecoder.RangeScale                  (3.5, eyeballed as "1.1 * 3.2")
//         x LightmapDecoder.ColorSaturationCompensation (1.0)
//         x BakedLightmapStandardConverter.AlbedoGain   (2.71 / 2.45 / 2.23, eyeballed)
//         x PPv2ToneMapMath.ResoniteExtraContrast       (1.4)
//         x the Resonite world's own lighting (SecondaryAlbedo is a MULTIPLY)
//   — and NOTHING measured the result, so every constant was calibrated against one bake of
//   one room and silently went wrong the moment the bake's absolute level moved. AlbedoGain's
//   own comment says as much ("expect to re-tune for a room with different bake brightness").
//   LightmapBrightnessProbe already measures exactly the missing number (atlas-independent,
//   island-interior mean luminance) but was manual-only and wired to nothing.
//
// WHAT THIS DOES:
//   Before every send, measure the current bake and set AlbedoGain so that
//       bakeMeanLuma * gainLuma == TargetSurfaceLuma
//   holds. The colour balance of the gain is kept as an authored ratio (GainBalance,
//   normalised to luminance 1) so the warm bias that matched Unity's beige walls survives;
//   only the level is servoed.
//
// WHAT IT DELIBERATELY DOES NOT DO:
//   It cannot tell an artistic change from a technical one. Dimming the room's lights lowers
//   the bake mean, and this will raise the gain to compensate — cancelling the intent. When
//   you have deliberately changed the lighting design and the new look is what you want, press
//   "capture" (CaptureReference) to adopt it as the new target. Incidental changes — texel
//   density, atlas size, the send-time downscale, a stray light — are exactly what the servo
//   is for, and those it absorbs on its own.
//
//   With Enabled = false it still measures and logs the deviation from the target, so drift is
//   visible instead of silent. That reporting is the half that is always safe.
public static class LightmapExposurePolicy
{
    /// <summary>Servo the gain to TargetSurfaceLuma. When false, only measure and report.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// The invariant: bake mean luminance x AlbedoGain luminance. Seeded 2026-09-08 from the
    /// warm/daylight relight of CoastCircuit_Send (probe mean 0.1161, gain luminance 2.489),
    /// i.e. the state Tanossy asked for after "the diner is too bright". Re-seed with
    /// CaptureReference() whenever a new look is adopted on purpose.
    /// </summary>
    public static float TargetSurfaceLuma = 0.2890f;

    /// <summary>
    /// Colour ratio of the gain, normalised to luminance 1. Authored, never measured — this is
    /// the warm bias (R 1.089 / G 0.984 / B 0.896) that the 2026-08-31 live calibration found
    /// necessary to match Unity's beige walls and warm wood.
    /// </summary>
    public static Color GainBalance = new Color(1.089f, 0.984f, 0.896f, 1f);

    public static float MinGainLuma = 0.25f;
    public static float MaxGainLuma = 12f;

    /// <summary>Deviation below which the gain is left alone, so tiny bake noise is not chased.</summary>
    public static float DeadZone = 0.02f;

    const string PrefTarget = "ResoniteSDK.LightmapExposurePolicy.TargetSurfaceLuma";
    const string PrefEnabled = "ResoniteSDK.LightmapExposurePolicy.Enabled";

    static bool _prefsLoaded;

    static void LoadPrefs()
    {
        if (_prefsLoaded) return;
        _prefsLoaded = true;
        if (EditorPrefs.HasKey(PrefTarget)) TargetSurfaceLuma = EditorPrefs.GetFloat(PrefTarget);
        if (EditorPrefs.HasKey(PrefEnabled)) Enabled = EditorPrefs.GetBool(PrefEnabled);
    }

    static void SavePrefs()
    {
        EditorPrefs.SetFloat(PrefTarget, TargetSurfaceLuma);
        EditorPrefs.SetBool(PrefEnabled, Enabled);
    }

    static float Luma(Color c) => 0.2126729f * c.r + 0.7151522f * c.g + 0.0721750f * c.b;

    /// <summary>
    /// Measure the current bake and (when Enabled) set BakedLightmapStandardConverter.AlbedoGain.
    /// Safe to call with no bake present — it logs and leaves the gain untouched.
    /// </summary>
    public static void Apply(Action<string> log)
    {
        LoadPrefs();
        log = log ?? (s => Debug.Log("[LightmapExposurePolicy] " + s));

        if (LightmapSettings.lightmaps == null || LightmapSettings.lightmaps.Length == 0)
        {
            log("exposure policy: no baked lightmaps in the scene; AlbedoGain left at " + Fmt(BakedLightmapStandardConverter.AlbedoGain) + ".");
            return;
        }

        double bakeMean;
        Color bakeRgb;
        long texels;
        try
        {
            LightmapBrightnessProbe.Measure(out bakeMean, out bakeRgb, out texels);
        }
        catch (Exception ex)
        {
            log("exposure policy: probe failed (" + ex.Message + "); AlbedoGain left at " + Fmt(BakedLightmapStandardConverter.AlbedoGain) + ".");
            return;
        }

        if (texels <= 0 || bakeMean <= 1e-6)
        {
            log($"exposure policy: probe found nothing usable (texels={texels}, mean={bakeMean:0.####}); AlbedoGain left at {Fmt(BakedLightmapStandardConverter.AlbedoGain)}.");
            return;
        }

        var current = BakedLightmapStandardConverter.AlbedoGain;
        float currentLuma = Luma(current);
        double currentSurface = bakeMean * currentLuma;
        double deviation = currentSurface / TargetSurfaceLuma - 1.0;

        if (!Enabled)
        {
            log($"exposure policy (report only): bake mean luma={bakeMean:0.####} over {texels} texel(s), " +
                $"gain luma={currentLuma:0.###} -> surface {currentSurface:0.####} vs target {TargetSurfaceLuma:0.####} " +
                $"({deviation * 100:+0.0;-0.0}%). Enable the policy to correct it.");
            return;
        }

        if (Math.Abs(deviation) <= DeadZone)
        {
            log($"exposure policy: bake mean luma={bakeMean:0.####}, surface {currentSurface:0.####} is within " +
                $"{DeadZone * 100:0.#}% of target {TargetSurfaceLuma:0.####}; AlbedoGain unchanged at {Fmt(current)}.");
            return;
        }

        float wantedLuma = Mathf.Clamp((float)(TargetSurfaceLuma / bakeMean), MinGainLuma, MaxGainLuma);
        float balanceLuma = Luma(GainBalance);
        if (balanceLuma <= 1e-4f)
        {
            log("exposure policy: GainBalance has no luminance; AlbedoGain left unchanged.");
            return;
        }

        float k = wantedLuma / balanceLuma;
        var next = new Color(GainBalance.r * k, GainBalance.g * k, GainBalance.b * k, 1f);
        BakedLightmapStandardConverter.AlbedoGain = next;

        log($"exposure policy: bake mean luma={bakeMean:0.####} rgb=({bakeRgb.r:0.###},{bakeRgb.g:0.###},{bakeRgb.b:0.###}) over {texels} texel(s); " +
            $"surface was {currentSurface:0.####} ({deviation * 100:+0.0;-0.0}% off target {TargetSurfaceLuma:0.####}); " +
            $"AlbedoGain {Fmt(current)} -> {Fmt(next)} (luma {currentLuma:0.###} -> {wantedLuma:0.###}).");

        if (wantedLuma >= MaxGainLuma - 1e-3f || wantedLuma <= MinGainLuma + 1e-3f)
            log($"exposure policy: gain hit its clamp ({MinGainLuma}..{MaxGainLuma}). The bake is far off the target — " +
                "check the lighting rather than the gain, or re-capture the reference.");
    }

    /// <summary>
    /// Adopt the current bake + current AlbedoGain as the new target, i.e. "this look is right".
    /// Use after a deliberate lighting change.
    /// </summary>
    public static string CaptureReference()
    {
        LoadPrefs();
        if (LightmapSettings.lightmaps == null || LightmapSettings.lightmaps.Length == 0)
            return "capture failed: no baked lightmaps in the scene.";

        double bakeMean;
        Color bakeRgb;
        long texels;
        try
        {
            LightmapBrightnessProbe.Measure(out bakeMean, out bakeRgb, out texels);
        }
        catch (Exception ex)
        {
            return "capture failed: probe error " + ex.Message;
        }
        if (texels <= 0 || bakeMean <= 1e-6)
            return $"capture failed: probe found nothing usable (texels={texels}, mean={bakeMean:0.####}).";

        var gain = BakedLightmapStandardConverter.AlbedoGain;
        float gainLuma = Luma(gain);
        float before = TargetSurfaceLuma;
        TargetSurfaceLuma = (float)(bakeMean * gainLuma);

        // Keep the authored colour ratio in step with the gain that was just adopted.
        if (gainLuma > 1e-4f)
            GainBalance = new Color(gain.r / gainLuma, gain.g / gainLuma, gain.b / gainLuma, 1f);

        SavePrefs();
        return $"exposure reference captured: bake mean luma={bakeMean:0.####}, gain luma={gainLuma:0.###} " +
               $"-> TargetSurfaceLuma {before:0.####} -> {TargetSurfaceLuma:0.####}, " +
               $"GainBalance=({GainBalance.r:0.###},{GainBalance.g:0.###},{GainBalance.b:0.###}).";
    }

    /// <summary>Set the target as a relative change, e.g. 0.8 for "20% darker".</summary>
    public static string ScaleTarget(float factor)
    {
        LoadPrefs();
        if (factor <= 0f) return "scale failed: factor must be positive.";
        float before = TargetSurfaceLuma;
        TargetSurfaceLuma *= factor;
        SavePrefs();
        return $"exposure target {before:0.####} -> {TargetSurfaceLuma:0.####} (x{factor:0.###}).";
    }

    public static void SetEnabled(bool value)
    {
        LoadPrefs();
        Enabled = value;
        SavePrefs();
    }

    static string Fmt(Color c) => $"({c.r:0.###},{c.g:0.###},{c.b:0.###})";
}
