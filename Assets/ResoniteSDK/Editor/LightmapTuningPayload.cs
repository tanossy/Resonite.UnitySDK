using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 2026-08-30: gathers the Resonite field IDs that scripts/build_light_tuning_panel.py (eldorado
// repo) needs to add in-world "lightmap tint" and "lightmap lossless" controls to the Light
// Tuning Panel. Called from SceneConverter.WriteLightTuningPanelInputAndLaunchBuilder() through
// one line, so the official file's diff stays minimal.
//
// Adopted from Lumos (https://github.com/ultrawidegamer/Lumos, BSD-2-Clause; successor fork
// https://github.com/0xFLOATINGPOINT/Lumos). Lumos's send step creates ONE ValueMultiDriver<colorX>
// whose Drives list points at every lightmap material's TintColor, fed from a single
// ValueField<colorX> (the fork exposes it as DynamicVariable "LumosConfig/LumosLightmapTint"),
// and one ValueMultiDriver<TextureCompression?> over every lightmap texture's PreferredFormat,
// fed from a BooleanValueDriver (false=BC6H_LZMA, true=RawRGBAHalf) - so the whole bake's color
// and VRAM/quality trade-off can be changed inside Resonite without re-sending anything.
//
// Mapping onto this SDK's output: Lumos tints an overlay Unlit material; we fold the lightmap
// into PBS_MultiUV_Metallic.SecondaryAlbedoTexture, and that material has no separate
// secondary-tint field - but multiplication commutes, so tinting AlbedoColor is the exact same
// operation on the final color. AlbedoColor also carries the source material's own _Color,
// though, so only materials whose baseline AlbedoColor is white (the overwhelming majority) are
// handed to the driver; non-white ones are listed in `skipped` and left alone rather than
// having their authored color overwritten.
//
// Field IDs: every Sync field's ID is allocated by SceneConverter.GetIdOrAllocate when the
// component's CollectMembers ran during the send that just completed, so at call time they are
// exactly the IDs that exist in the world. The Python side references them directly (no getSlot
// discovery needed).
public static class LightmapTuningPayload
{
    const float WhiteTolerance = 0.02f;

    /// <summary>
    /// The value the in-world LightmapTint driver should start at: the same gain
    /// BakedLightmapStandardConverter already multiplied into every AlbedoColor at send time,
    /// so that "driver on" and "driver off" look identical until someone nudges it in-world.
    /// </summary>
    public static float[] TintDefault()
    {
        var g = BakedLightmapStandardConverter.AlbedoGain;
        return new[] { g.r, g.g, g.b };
    }

    public static void Fill(SceneConverter converter, List<string> tintTargets, List<string> formatTargets, List<string> skipped)
    {
        if (converter == null)
            return;

        // 2026-08-31: the sent AlbedoColor is baseline * AlbedoGain, so "white baseline" now
        // means "equals the gain" - compare against it rather than against (1,1,1).
        var gain = BakedLightmapStandardConverter.AlbedoGain;

        foreach (var mat in Object.FindObjectsOfType<BakedLightmapStandardConverter>())
        {
            if (mat == null || mat.PBS == null || mat.PBS.Data == null)
                continue;

            var data = mat.PBS.Data;
            var c = data.AlbedoColor.color;

            bool white = Mathf.Abs(c.r - gain.r) <= WhiteTolerance * gain.r &&
                Mathf.Abs(c.g - gain.g) <= WhiteTolerance * gain.g &&
                Mathf.Abs(c.b - gain.b) <= WhiteTolerance * gain.b;

            if (!white)
            {
                skipped.Add(mat.name);
                continue;
            }

            var id = TryGetId(converter, data.AlbedoColor_Element.Member);
            if (!string.IsNullOrEmpty(id))
                tintTargets.Add(id);
        }

        foreach (var tex in Object.FindObjectsOfType<Texture2DConverter>())
        {
            if (tex == null || tex.Source == null || tex.Provider == null || tex.Provider.Data == null)
                continue;

            if (!LightmapTextureSettings.IsGeneratedLightmapAsset(AssetDatabase.GetAssetPath(tex.Source)))
                continue;

            var id = TryGetId(converter, tex.Provider.Data.PreferredFormat_Element.Member);
            if (!string.IsNullOrEmpty(id))
                formatTargets.Add(id);
        }
    }

    static string TryGetId(SceneConverter converter, FrooxEngine.IWorldElement element)
    {
        try
        {
            return converter.GetId(element);
        }
        catch (System.Exception ex)
        {
            // A field that was never sent (e.g. a converter left over from an earlier session
            // that this send skipped) has no ID - not an error worth aborting the panel over.
            Debug.LogWarning($"[LightTuningPanel] LightmapTuningPayload: no ID for {element?.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
