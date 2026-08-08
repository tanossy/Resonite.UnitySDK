using FrooxEngine;
using System.Collections.Generic;
using System.Linq;

[MaterialConverter(true, "Standard")]
public class StandardConverter : StandardBaseConverter<PBS_MetallicWrapper, PBS_Metallic>
{
    protected static readonly IEnumerable<string> SupportedProperties = new List<string>()
    {
        "_Glossiness",
        "_Metallic",
        "_MetallicGlossMap",
    };

    public override IAssetProvider<FrooxEngine.Material> UpdateConversion(UnityEngine.Material material, IConversionContext context)
    {
        var provider = base.UpdateConversion(material, context);

        var data = PBS.Data;

        data.Smoothness = GetFloatOrDefault(material, "_Glossiness");
        data.Metallic = GetFloatOrDefault(material, "_Metallic");

        data.MetallicMap = ConversionPassState.ShouldUploadSourceMaterialTextures
            ? context.GetITexture2D(GetTextureOrDefault(material, "_MetallicGlossMap"))
            : null;

        return provider;
    }

    public static float? EvaluateHeuristicConversion(UnityEngine.Material material)
    {
        if (material.shader == null)
            return null;

        return PropertyBasedCompatibilityEvaluator.ComputeCompatibility(material,
            SupportedProperties.Concat(BaseSupportedProperties));
    }
}
