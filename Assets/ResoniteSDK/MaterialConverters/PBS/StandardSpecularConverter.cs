using FrooxEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[MaterialConverter(false, "Standard (Specular setup)")]
public class StandardSpecularConverter : StandardBaseConverter<PBS_SpecularWrapper, PBS_Specular>
{
    protected static readonly IEnumerable<string> SupportedProperties = new List<string>()
    {
        "_SpecColor",
        "_Glossiness",
        "_SpecGlossMap",
    };

    public override IAssetProvider<FrooxEngine.Material> UpdateConversion(UnityEngine.Material material, IConversionContext context)
    {
        var provider = base.UpdateConversion(material, context);

        var data = PBS.Data;

        var specColor = GetColorOrDefault(material, "_SpecColor", Color.white);

        // Resonite uses the alpha channel to determine the glossiness
        specColor.a = GetFloatOrDefault(material, "_Glossiness");

        data.SpecularColor = specColor.ToColorX_sRGB();
        data.SpecularMap = ConversionPassState.ShouldUploadSourceMaterialTextures
            ? context.GetITexture2D(GetTextureOrDefault(material, "_SpecGlossMap"))
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
