using FrooxEngine;
using System.Collections.Generic;
using UnityEngine;

public abstract class StandardBaseConverter<TWrapper, TMaterial> : ResoniteMaterialConverter
    where TWrapper : ResoniteComponent<TMaterial>
    where TMaterial : FrooxEngine.PBS_Material, new()
{
    public TWrapper PBS;

    protected static float GetFloatOrDefault(UnityEngine.Material material, string property, float fallback = 0f)
    {
        if (material == null || !material.HasProperty(property))
            return fallback;

        return material.GetFloat(property);
    }

    protected static Color GetColorOrDefault(UnityEngine.Material material, string property, Color fallback)
    {
        if (material == null || !material.HasProperty(property))
            return fallback;

        return material.GetColor(property);
    }

    protected static Texture GetTextureOrDefault(UnityEngine.Material material, string property)
    {
        if (material == null || !material.HasProperty(property))
            return null;

        return material.GetTexture(property);
    }

    protected static readonly IEnumerable<string> BaseSupportedProperties = new List<string>()
    {
        "_Cutoff",
        "_Color",

        "_BumpMap",
        "_BumpScale",

        "_Parallax",
        "_ParallaxMap",

        "_OcclusionMap",

        "_EmissionColor",
        "_EmissionMap",
    };

    public override IAssetProvider<FrooxEngine.Material> UpdateConversion(UnityEngine.Material material, IConversionContext context)
    {
        if (PBS == null)
            PBS = gameObject.AddComponent<TWrapper>();

        var data = PBS.Data;

        data.RenderQueue = material.renderQueue;

        if (material.IsKeywordEnabled("_ALPHATEST_ON"))
            data.BlendMode = BlendMode.Cutout;
        else if (material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
            data.BlendMode = BlendMode.Transparent;
        else if (material.IsKeywordEnabled("_ALPHABLEND_ON"))
            data.BlendMode = BlendMode.Alpha;
        else
            data.BlendMode = BlendMode.Opaque;

        data.AlphaCutoff = GetFloatOrDefault(material, "_Cutoff");

        var uploadSourceTextures = ConversionPassState.ShouldUploadSourceMaterialTextures;

        data.AlbedoColor = ColorGradingApproximation.Apply(GetColorOrDefault(material, "_Color", Color.white)).ToColorX_sRGB();
        data.AlbedoTexture = uploadSourceTextures ? context.GetITexture2D(material.mainTexture) : null;
        data.TextureScale = material.mainTextureScale;
        data.TextureOffset = material.mainTextureOffset;

        data.NormalMap = uploadSourceTextures ? context.GetITexture2D(GetTextureOrDefault(material, "_BumpMap")) : null;
        data.NormalScale = GetFloatOrDefault(material, "_BumpScale", 1f);

        data.HeightScale = GetFloatOrDefault(material, "_Parallax");
        data.HeightMap = uploadSourceTextures ? context.GetITexture2D(GetTextureOrDefault(material, "_ParallaxMap")) : null;

        data.OcclusionMap = uploadSourceTextures ? context.GetITexture2D(GetTextureOrDefault(material, "_OcclusionMap")) : null;

        if (material.IsKeywordEnabled("_EMISSION"))
        {
            data.EmissiveColor = ColorGradingApproximation.Apply(GetColorOrDefault(material, "_EmissionColor", Color.black)).ToColorX_sRGB();
            data.EmissiveMap = uploadSourceTextures ? context.GetITexture2D(GetTextureOrDefault(material, "_EmissionMap")) : null;
        }
        else
        {
            // There's no actual toggle for emission on the Resonite version, so just set it to black
            data.EmissiveColor = Color.black.ToColorX_sRGB();
        }

        return PBS.Data;
    }
}
