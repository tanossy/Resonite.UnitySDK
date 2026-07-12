using FrooxEngine;
using UnityEngine;

// ============================================================================================
// Heuristic converter: any Unity shader whose name contains "water" (case-insensitive)
// -> Resonite PBS_Metallic + native Panner2D UV scroll.
// ============================================================================================
//
// This reproduces the community-standard water-surface pattern confirmed by a live-world
// investigation on 2026-07-12 (4 worlds / 48 getSlot samples via the resonite-world-study
// skill): water in existing Resonite worlds is built from a plain PBS_Metallic (or
// PBS_Specular) material - normal map + detail normal map, high metallic/smoothness - driven
// by a Panner2D component that scrolls the material's TextureOffset field over time. There is
// no dedicated "water" material/shader type in Resonite itself; every water surface found in
// the wild is just this PBS + Panner2D combo.
//
// This converter targets custom water shaders (e.g. MomomaShader/Surface/ParallaxWater,
// "SeaWater", and similar - confirmed present via the shader_survey diagnostic in
// K:/base/ResoniteWorld/Assets/Editor/LightmapTestHarness.cs) that are NOT one of the
// directly-supported Unity/Resonite shaders and therefore have no dedicated converter. Since
// these are arbitrary third-party/custom shaders, this converter only reads:
//   - Properties guaranteed to exist on every UnityEngine.Material regardless of shader
//     (mainTexture / mainTextureScale / mainTextureOffset, and the always-present "_Color"
//     property read via HasProperty like everywhere else in this codebase): read
//     unconditionally.
//   - Everything else (_BumpMap / _BumpScale / _Metallic / _Glossiness) is read only behind
//     material.HasProperty(...), exactly like StandardBaseConverter does for the built-in
//     Standard shader. No shader-specific property name is ever assumed to exist - most water
//     shaders (Momoma's ParallaxWater included) do not use the Standard naming convention at
//     all, so guessing would silently produce nothing.
//
// The Panner2D setup itself follows the officially-provided sample 1:1:
// Assets/ResoniteSDK/MaterialConverters/Custom/TestPanningConverter.cs.
[MaterialConverter(true)]
public class WaterPanningConverter : ResoniteMaterialConverter
{
    public FrooxEngine.PBS_MetallicWrapper PBS;
    public FrooxEngine.Panner2DWrapper Panner;

    // Default UV scroll speed (UV units/second), used because none of the water shaders
    // matched by this converter expose a readable shader-specific flow/panning-speed
    // property (unlike Custom/TestPanningShader's "_PanningSpeed", these are real-world
    // custom shaders with unknown/undocumented property layouts - see file header). Kept
    // deliberately slow/gentle: a calm scrolling water surface rather than a fast-scrolling
    // texture. If a specific water shader is later found to expose its own flow-speed
    // property, prefer reading it via HasProperty over relying on this default.
    private static readonly Vector2 DefaultPanningSpeed = new Vector2(0.02f, 0.015f);

    public static float? EvaluateHeuristicConversion(UnityEngine.Material material)
    {
        if (material == null || material.shader == null)
            return null;

        if (material.shader.name.IndexOf("water", System.StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        return 0;
    }

    public override IAssetProvider<FrooxEngine.Material> UpdateConversion(UnityEngine.Material material, IConversionContext context)
    {
        if (PBS == null)
            PBS = gameObject.AddComponent<FrooxEngine.PBS_MetallicWrapper>();

        if (Panner == null)
            Panner = gameObject.AddComponent<FrooxEngine.Panner2DWrapper>();

        var data = PBS.Data;
        var pannerData = Panner.Data;

        data.RenderQueue = material.renderQueue;

        // Always present on any UnityEngine.Material, regardless of shader.
        data.AlbedoTexture = context.GetITexture2D(material.mainTexture);
        data.TextureScale = material.mainTextureScale;
        data.AlbedoColor = GetColorOrDefault(material, "_Color", Color.white).ToColorX_sRGB();

        // Shader-specific - guarded, since these are arbitrary custom water shaders that are
        // not guaranteed to use the Standard-shader property naming convention.
        data.NormalMap = context.GetITexture2D(GetTextureOrDefault(material, "_BumpMap"));
        data.NormalScale = GetFloatOrDefault(material, "_BumpScale", 1f);

        // Water reads as convincingly wet/reflective even without an explicit metallic/gloss
        // map, so fall back to a "water-like" high metallic / high smoothness default (per
        // the community pattern from the file header) when the shader doesn't expose
        // _Metallic/_Glossiness itself.
        data.Metallic = GetFloatOrDefault(material, "_Metallic", 0.8f);
        data.Smoothness = GetFloatOrDefault(material, "_Glossiness", 0.95f);

        // Panner2D setup - identical pattern to TestPanningConverter.cs: drive the PBS
        // material's TextureOffset field to continuously scroll its (normal map) UVs.
        pannerData.Enabled = true;
        pannerData.persistent = true;

        // The panner drives the TextureOffset field on the PBS material.
        pannerData._target = data.TextureOffset_Element.Member;

        // See DefaultPanningSpeed's doc comment: no shader-specific speed property could be
        // read, so a fixed gentle default is used instead.
        pannerData._speed = DefaultPanningSpeed;

        // Carry over whatever static texture offset the source material already had.
        pannerData._offset = material.mainTextureOffset;

        // We don't need it to repeat & wrap around.
        pannerData._repeat = new Vector2(float.PositiveInfinity, float.PositiveInfinity);

        return data;
    }

    private static float GetFloatOrDefault(UnityEngine.Material material, string property, float fallback = 0f)
    {
        if (material == null || !material.HasProperty(property))
            return fallback;

        return material.GetFloat(property);
    }

    private static Color GetColorOrDefault(UnityEngine.Material material, string property, Color fallback)
    {
        if (material == null || !material.HasProperty(property))
            return fallback;

        return material.GetColor(property);
    }

    private static Texture GetTextureOrDefault(UnityEngine.Material material, string property)
    {
        if (material == null || !material.HasProperty(property))
            return null;

        return material.GetTexture(property);
    }
}
