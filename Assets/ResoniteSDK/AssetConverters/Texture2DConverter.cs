using Elements.Assets;
using FrooxEngine;
using ResoniteLink;
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public class Texture2DConverter : AssetConverter<StaticTexture2DWrapper, StaticTexture2D, UnityEngine.Texture2D, FrooxEngine.Texture2D>
{
    Renderite.Shared.TextureWrapMode _wrapModeU;
    Renderite.Shared.TextureWrapMode _wrapModeV;
    Renderite.Shared.TextureFilterMode? _filterMode;
    Renderite.Shared.ColorProfile _colorProfile;
    int? _anisoLevel;
    bool _uncompressed;
    bool _crunchCompressed;
    int? _maxSize;
    bool _mipMaps;
    bool _readable;

    public override string AssetClass => "Texture2D";
    public override string AssetName => Source.name;
    protected override Message GenerateConversion()
    {
        // Cache the texture wrap mode and other properties. We'll need to update provider with these.
        // Unity hates accessing those properties from other threads, so we have to fetch it here while
        // we're on the main thread.
        _wrapModeU = Source.wrapModeU.ToResoniteLink();
        _wrapModeV = Source.wrapModeV.ToResoniteLink();

        if (Source.anisoLevel > 1)
        {
            _anisoLevel = Source.anisoLevel;
            _filterMode = Renderite.Shared.TextureFilterMode.Anisotropic;
        }
        else
        {
            _anisoLevel = null;

            // Only explicitly set the point filtering mode, since that's usually a stylistic choice
            // However Bilinear and up is generally ok to leave to the actual graphics settings
            if (Source.filterMode == FilterMode.Point)
                _filterMode = Renderite.Shared.TextureFilterMode.Point;
            else
                _filterMode = null;
        }

        _mipMaps = Source.mipmapCount > 1;

        // If the format is not compressed in Unity, we should also avoid compression
        // This is improtant for texturew with lots of colors, pixel art and such
        _uncompressed = !Source.format.IsCompressed();
        _crunchCompressed = Source.format.IsCrunchCompressed();

        _colorProfile = Source.isDataSRGB ? Renderite.Shared.ColorProfile.sRGB : Renderite.Shared.ColorProfile.Linear;

        _readable = Source.isReadable;

        var assetPath = AssetDatabase.GetAssetPath(Source);

        if (!string.IsNullOrWhiteSpace(assetPath) && AssetImporter.GetAtPath(assetPath) is TextureImporter textureImporter)
            _maxSize = textureImporter.maxTextureSize;
        else
            _maxSize = null;

        return ConvertTexture2D(Source, PostProcessor != null);
    }

    protected override async Task<AssetData> SendConversion(Message message, LinkInterface link)
    {
        switch (message)
        {
            case ImportTexture2DFile importFile:
                return await link.ImportTexture(importFile);

            case ImportTexture2DRawData importRawData:
                return await link.ImportTexture(importRawData);

            case ImportTexture2DRawDataHDR importRawDataHDR:
                return await link.ImportTexture(importRawDataHDR);

            default:
                throw new NotSupportedException("Unsupported conversion message: " + message.GetType());
        }
    }
    protected override ResoniteLink.Component UpdateProvider(Uri assetUrl, IConversionContext context)
    {
        Provider.Data.URL = assetUrl;

        Provider.Data.WrapModeU = _wrapModeU;
        Provider.Data.WrapModeV = _wrapModeV;

        Provider.Data.AnisotropicLevel = _anisoLevel;
        Provider.Data.FilterMode = _filterMode;

        Provider.Data.MipMaps = _mipMaps;
        Provider.Data.MipMapFilter = Filtering.Box;

        Provider.Data.PreferredProfile = _colorProfile;

        Provider.Data.Uncompressed = _uncompressed;
        Provider.Data.CrunchCompressed = _crunchCompressed;

        Provider.Data.Readable = _readable;

        Provider.Data.MaxSize = _maxSize;

        // 2026-08-30: baked-lightmap-only overrides (BC6H_LZMA/Linear/MinSize/ForceExactVariant,
        // adopted from Lumos) - no-op for every other texture. See LightmapTextureSettings.cs.
        LightmapTextureSettings.ApplyToProvider(Source, Provider.Data);

        return Provider.CollectData(context);
    }

    public static ResoniteLink.Message ConvertTexture2D(UnityEngine.Texture2D texture, bool requiresPostProcessing)
    {
        var assetPath = AssetDatabase.GetAssetPath(texture);
        bool isGeneratedLightmapPreview = IsGeneratedLightmapPreview(assetPath);

        // We can only import the file directly if there's no postprocessing required
        if (!requiresPostProcessing && !isGeneratedLightmapPreview)
        {
            // First try to import it as a file. This is easiest and will preserve most data
            // Rather than just extracting the raw pixels
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                assetPath = Path.GetFullPath(assetPath);

                if (File.Exists(assetPath) && AssetConversionHelper.IsTextureFileSupportedByResonite(assetPath))
                    return new ImportTexture2DFile() { FilePath = assetPath };
            }
        }
        else if (isGeneratedLightmapPreview)
        {
            Debug.Log($"[ResoniteSDK] Texture2DConverter: sending generated lightmap preview \"{texture.name}\" as raw pixel data instead of file import.");
        }

        if (!texture.isReadable)
        {
            var readableTexture = new UnityEngine.Texture2D(texture.width, texture.height, texture.format, false);

            Graphics.CopyTexture(texture, 0, 0, readableTexture, 0, 0);

            texture = readableTexture;
        }

        // It is not supported directly, so we have to extract the raw data and send it over
        if (texture.format.IsHDR())
        {
            var import = new ImportTexture2DRawDataHDR()
            {
                Width = texture.width,
                Height = texture.height,
            };

            var data = import.AccessRawData();
            var pixels = texture.GetPixels(0);

            for (int y = 0; y < import.Height; y++)
            {
                // GetPixels returns them from bottom to top, and ResoniteLink expects them from top to bottom
                int ySource = import.Height - 1 - y;
                for (int x = 0; x < import.Width; x++)
                {
                    var c = pixels[x + ySource * import.Width];
                    data[x, y] = c.ToResoniteLink();
                }
            }

            return import;
        }
        else
        {
            var import = new ImportTexture2DRawData()
            {
                Width = texture.width,
                Height = texture.height,
            };

            var data = import.AccessRawData();
            var pixels = texture.GetPixels32(0);

            for (int y = 0; y < import.Height; y++)
            {
                // GetPixels returns them from bottom to top, and ResoniteLink expects them from top to bottom
                int ySource = import.Height - 1 - y;
                for (int x = 0; x < import.Width; x++)
                {
                    var c = pixels[x + ySource * import.Width];
                    data[x, y] = c.ToResoniteLink();
                }
            }

            return import;
        }
    }

    static bool IsGeneratedLightmapPreview(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return false;

        var normalized = assetPath.Replace('\\', '/');
        return normalized.Contains("/ResoniteSDK/Generated/LightmapVariants/") &&
            Path.GetFileName(normalized).StartsWith("LMTex_", StringComparison.OrdinalIgnoreCase);
    }
}
