using UnityEditor;
using UnityEngine;

// 2026-08-30: Resonite-side StaticTexture2D settings for the decoded baked-lightmap textures
// (LightmapDecoder's LMTex_* assets), applied at the very end of Texture2DConverter.UpdateProvider
// through a single call-site line (per the "keep the official SDK file diff minimal" policy -
// all logic lives here).
//
// Adopted from Lumos (https://github.com/ultrawidegamer/Lumos, BSD-2-Clause, by ultrawidegamer &
// Cloud_Jumper; successor fork https://github.com/0xFLOATINGPOINT/Lumos). Lumos is a
// Resonite->Unity->Resonite light baker whose send path creates each lightmap's StaticTexture2D
// with exactly these values (ResoLinkHelper.AddTextureToResolinkSlot): Bilinear, no mipmaps,
// Clamp, MinSize=8192 + ForceExactVariant=true (so Resonite never hands out a downscaled
// variant of the lightmap - atlas UVs only line up against the exact texel grid it was baked
// at), Uncompressed=false + PreferredFormat=BC6H_LZMA (HDR block compression, ~1 byte/px in
// VRAM instead of 8 for RawRGBAHalf), PreferredProfile=Linear (radiance data, never sRGB).
// Lumos also offers a lossless RawRGBAHalf toggle driven from inside the world; ours is exposed
// the same way via scripts/build_light_tuning_panel.py ("LightTuning/LightmapLossless").
// Nothing here is copied verbatim from Lumos - it is the same field/value choices re-expressed
// against this SDK's typed StaticTexture2D wrapper. See the comparison writeup:
// C:/urd/wiki/concepts/resonite/dev-recipes/2026-08-30_rezo_con_lumos_lightbake_repo_study.md
public static class LightmapTextureSettings
{
    /// <summary>
    /// True for the decoded lightmap assets LightmapDecoder writes under
    /// Assets/ResoniteSDK/Generated/LightmapVariants/&lt;sceneGUID&gt;/LMTex_*.{png,exr}. Same rule
    /// Texture2DConverter.IsGeneratedLightmapPreview uses to force the raw-pixel upload path.
    /// </summary>
    public static bool IsGeneratedLightmapAsset(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return false;

        var normalized = assetPath.Replace('\\', '/');
        return normalized.Contains("/ResoniteSDK/Generated/LightmapVariants/") &&
            System.IO.Path.GetFileName(normalized).StartsWith("LMTex_", System.StringComparison.OrdinalIgnoreCase);
    }

    static bool IsHdrFormat(TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.RGBAHalf:
            case TextureFormat.RGBAFloat:
            case TextureFormat.BC6H:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// What ApplyToProvider needs to know about the source texture, captured on the MAIN thread
    /// (Texture2DConverter.GenerateConversion, next to the other importer-derived fields) and
    /// consumed later on whatever thread UpdateProvider runs on.
    ///
    /// 2026-08-30 live lesson (Editor.log, two aborted sends): the first version of this file
    /// called AssetDatabase.GetAssetPath(source) and read source.format from inside
    /// UpdateProvider. That runs on the asset-conversion task continuation, not the main thread,
    /// so Unity threw "can only be called from the main thread" -> "FATAL ERROR in conversion"
    /// -> conversion state reset, for every send. Texture2DConverter.GenerateConversion's own
    /// comment already warns about exactly this ("Unity hates accessing those properties from
    /// other threads, so we have to fetch it here while we're on the main thread").
    /// </summary>
    public struct Capture
    {
        public bool IsGeneratedLightmap;
        public bool IsHdr;
    }

    /// <summary>Main thread only. <paramref name="assetPath"/> is the already-resolved AssetDatabase path of <paramref name="source"/>.</summary>
    public static Capture CaptureFrom(UnityEngine.Texture2D source, string assetPath)
    {
        return new Capture
        {
            IsGeneratedLightmap = source != null && IsGeneratedLightmapAsset(assetPath),
            IsHdr = source != null && IsHdrFormat(source.format),
        };
    }

    /// <summary>
    /// Overrides the provider fields Texture2DConverter derived from the Unity importer with the
    /// lightmap-specific values above. No-op for every texture that isn't a generated lightmap,
    /// so ordinary albedo/normal/etc. textures are untouched. Thread-agnostic: touches only the
    /// plain-data provider object and the pre-captured flags, never a UnityEngine.Object.
    /// </summary>
    public static void ApplyToProvider(Capture capture, FrooxEngine.StaticTexture2D data)
    {
        if (data == null || !capture.IsGeneratedLightmap)
            return;

        data.FilterMode = Renderite.Shared.TextureFilterMode.Bilinear;
        data.AnisotropicLevel = null;
        data.MipMaps = false;
        data.WrapModeU = Renderite.Shared.TextureWrapMode.Clamp;
        data.WrapModeV = Renderite.Shared.TextureWrapMode.Clamp;
        data.MinSize = 8192;
        data.ForceExactVariant = true;
        data.CrunchCompressed = false;

        if (capture.IsHdr)
        {
            // HDR export path (LightmapDecoder.HdrExport): keep the radiance range, let Resonite
            // block-compress it. Texture2DConverter sets Uncompressed=true for any Unity format
            // that isn't itself block-compressed (RGBAHalf included), which would make Resonite
            // keep the texture as RawRGBAHalf - override so BC6H_LZMA actually applies.
            data.Uncompressed = false;
            data.PreferredFormat = Elements.Assets.TextureCompression.BC6H_LZMA;
            data.PreferredProfile = Renderite.Shared.ColorProfile.Linear;
        }
        // LDR (8-bit sRGB PNG) path: leave Uncompressed/PreferredFormat/PreferredProfile exactly as
        // Texture2DConverter derived them - BC6H is meaningless for 8-bit data.
    }
}
