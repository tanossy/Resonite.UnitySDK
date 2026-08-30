using FrooxEngine;
using UnityEngine;

// Converts the ResoniteSDK/BakedLightmapStandard marker shader (see BakedLightmapStandard.shader
// and LightmapMaterialCache) into PBS_MultiUV_Metallic, folding the baked lightmap into the
// SecondaryAlbedo slot (UV1) since Renderite has no custom-shader support and MeshConverter
// already forwards Unity's mesh.uv2 into Resonite's TexCoord1.
//
// This does NOT inherit from StandardBaseConverter<TWrapper, TMaterial>, because that base is
// constrained to `TMaterial : FrooxEngine.PBS_Material`, and PBS_MultiUV_Metallic instead derives
// from the separate PBS_MultiUV_Material hierarchy (different field set: AlbedoScale/AlbedoOffset
// per-channel instead of a single shared TextureScale/TextureOffset). The property-by-property
// mapping below otherwise follows the same conventions as StandardConverter/StandardBaseConverter
// (context.GetITexture2D for all textures, Color.ToColorX_sRGB() for colors, "_EMISSION" keyword
// gating for emissive).
//
// 2026-08-08 (per Tanossy's feedback: "the sofa's color looks off"): fixed a bug where
// AlbedoColor/EmissiveColor weren't being routed through ColorGradingApproximation.Apply(...).
// StandardBaseConverter (for non-lightmapped materials) was already calling it, but this class
// (baked-lightmap materials, which make up most of this scene) had forgotten to, so Tonemap
// Compensation had no effect at all on material colors.
[MaterialConverter(false, "ResoniteSDK/BakedLightmapStandard")]
public class BakedLightmapStandardConverter : ResoniteMaterialConverter
{
    // 2026-08-08 (continuing the response to Tanossy's feedback of "dark / blown out"): both
    // options - pure multiply (too dark) and pure additive (colors blown out to white) - failed
    // live testing, so switched to a hybrid approach. The SecondaryAlbedo multiply (the proper
    // compositing that preserves color fidelity) is always kept active, and separately, the same
    // baked data is added back in at a modest strength via SecondaryEmissiveMap, approximating
    // the "shadow-lifting ambient fill" that Unity's baked GI used to provide. This value is the
    // strength of the additive component relative to the multiplicative one (0 = pure multiply
    // only, 1 = equivalent to the old EmissiveLightmapMode=true full-additive behavior). 0.35 was
    // the first rough estimate, and it remains an unconfirmed value meant to be tuned while
    // checking the look live.
    // 2026-08-08 (per Tanossy's feedback: "white needs to look white and brown needs to look
    // brown - there's no contrast and that's a problem"): the additive fill inherently crushes
    // contrast - adding a fixed amount to a dark color shrinks its ratio to a bright color,
    // washing out the difference between white and brown (simple math: a 9:1 reflectance ratio
    // shrinks to about 2.5:1 once you add +0.3). Multiply (SecondaryAlbedo) keeps the ratio
    // intact and only changes brightness, so it doesn't break the color distinction. Reverted the
    // additive fill to 0, and switched the approach so brightness boosting is instead handled on
    // the LightmapDecoder.RangeScale side (the gain applied before the multiply).
    public static float AdditiveFillStrength = 0.0f;

    // 2026-08-08 (continuing Tanossy's feedback): since LightConverter.IntensityMultiplier makes
    // lights brighter, the same Smoothness value now produces proportionally stronger highlights
    // (confirmed live: the bed's comforter started looking like polished metal). This value only
    // affects the scalar Smoothness path (materials without a MetallicMap set, e.g. bed01) -
    // materials that do have a MetallicMap (e.g. couch01, where Smoothness comes from the
    // texture's alpha channel) need a separate attenuation applied on the alpha side (to be
    // reflected when regenerating textures like couch01_MetallicSmoothness). 0.6 was the first
    // rough estimate.
    public static float SmoothnessCompensation = 0.05f;

    // 2026-08-08 addendum (per Tanossy's feedback: "black doesn't look black"): Smoothness
    // compensation alone couldn't tame the glare on metallic materials (e.g. black metal,
    // Metallic=0.844) - metals have almost no diffuse reflection, so nearly everything you see is
    // specular reflection, meaning no matter how far Smoothness is lowered, the reflection only
    // gets "duller," it never actually stops reflecting. Even with AlbedoColor set to pure black,
    // a metallic surface keeps mirroring bright light sources. So Metallic itself is now also
    // attenuated at send time.
    public static float MetallicCompensation = 0.0f;

    // Verification mode for large baked scenes. The first in-world check only needs geometry,
    // material colors, and the baked lightmap; uploading every source albedo/normal/metallic/
    // occlusion/emission texture can overwhelm ResoniteLink before anything appears in-world.
    //
    // 2026-08-08 (per Tanossy's feedback): this flag turned out to be the root cause of
    // "AlbedoTexture etc. aren't being sent." The lightmap path itself has already been verified
    // repeatedly live today (confirmed SecondaryAlbedoTexture/URL both arrive correctly), so
    // switching this to false.
    public static bool LightmapPreviewUploadOnly = false;

    // 2026-08-31 (per Tanossy: "Resonite is far darker than Unity - adjust"): send-time gain on
    // AlbedoColor for every lightmapped material. In Unity a lightmapped surface is
    // albedo * lightmap; in Resonite the lightmap sits in SecondaryAlbedo and MULTIPLIES the
    // scene lighting (albedo * lightmap * (ambient + direct)), and this world's ambient is
    // small, so the same bake renders several times darker. Calibrated live against Unity's
    // Scene view with the in-world LightTuning/LightmapTint driver (which writes AlbedoColor,
    // i.e. the exact same lever): with LightmapDecoder.RangeScale=3.5 already applied to the
    // lightmap, a warm x3.3/3.1/2.7 on AlbedoColor matched (walls beige, warm ceiling, wood
    // tones right). Raising RangeScale alone did not get there - the lightmap's HDR range
    // contributed less than linearly in Resonite - so the remaining gain lives here.
    //
    // Applied to ALL lightmapped materials (baseline _Color * gain, so authored tints keep
    // their ratios); the in-world LightmapTint driver is initialised to the same value
    // (SceneConverter payload -> build_light_tuning_panel.py) so a re-send reproduces this
    // look and Tanossy can still nudge it in-world. Per-world value: expect to re-tune for a
    // room with different bake brightness or a different ambient setup.
    public static Color AlbedoGain = new Color(2.71f, 2.45f, 2.23f, 1f);

    public PBS_MultiUV_MetallicWrapper PBS;

    public override IAssetProvider<FrooxEngine.Material> UpdateConversion(UnityEngine.Material material, IConversionContext context)
    {
        if (PBS == null)
            PBS = gameObject.AddComponent<PBS_MultiUV_MetallicWrapper>();

        var data = PBS.Data;

        data.RenderQueue = material.renderQueue;

        // --- Alpha handling / culling (explicit) ---
        // LightmapMaterialCache only ever produces this marker material for Opaque-mode ("_Mode"
        // == 0) Standard materials (see LightmapMaterialCache's "_Mode != 0f" eligibility guard),
        // and the marker shader itself doesn't expose a per-material Cull property (Standard is
        // always back-face culled), so these three are hardcoded rather than derived from the
        // source material. AlphaClip still forwards _Cutoff in case a future v2 relaxes the
        // Opaque-only restriction, at which point AlphaHandling would need to switch based on
        // material.GetFloat("_Mode") again. Enum values verified against
        // BindingsGenerator/Resonite.UnitySDK.Bindings/Generated/Enums/FrooxEngine/FrooxEngine/AlphaHandling.cs
        // and .../Culling.cs (Opaque = 0, Back = 2).
        data.Culling = FrooxEngine.Culling.Back;
        data.AlphaHandling = FrooxEngine.AlphaHandling.Opaque;
        data.AlphaClip = material.GetFloat("_Cutoff");

        // --- Albedo (UV0) ---
        var gradedAlbedo = ColorGradingApproximation.Apply(material.GetColor("_Color"));
        // 2026-08-31: see AlbedoGain's comment. Alpha untouched.
        data.AlbedoColor = new Color(gradedAlbedo.r * AlbedoGain.r, gradedAlbedo.g * AlbedoGain.g, gradedAlbedo.b * AlbedoGain.b, gradedAlbedo.a).ToColorX_sRGB();
        data.AlbedoTexture = LightmapPreviewUploadOnly ? null : context.GetITexture2D(material.GetTexture("_MainTex"));
        var mainTexScale = material.GetTextureScale("_MainTex");
        var mainTexOffset = material.GetTextureOffset("_MainTex");
        data.AlbedoScale = mainTexScale;
        data.AlbedoOffset = mainTexOffset;
        data.AlbedoUV = 0;

        // --- Normal (UV0) ---
        // Unity's Standard shader samples the normal/occlusion/emission maps using the same
        // uv_MainTex transform as albedo (only the detail albedo map gets its own UV2 transform),
        // so we mirror _MainTex's ScaleOffset here rather than leaving these at their zeroed
        // Sync<Vector2> default (which would otherwise collapse every sample to a single texel).
        data.NormalMap = LightmapPreviewUploadOnly ? null : context.GetITexture2D(material.GetTexture("_BumpMap"));
        data.NormalScale = material.GetFloat("_BumpScale");
        data.NormalMapScale = mainTexScale;
        data.NormalMapOffset = mainTexOffset;
        data.NormalMapUV = 0;

        // --- Occlusion (UV0) ---
        data.OcclusionMap = LightmapPreviewUploadOnly ? null : context.GetITexture2D(material.GetTexture("_OcclusionMap"));
        data.OcclusionMapScale = mainTexScale;
        data.OcclusionMapOffset = mainTexOffset;
        data.OcclusionMapUV = 0;

        // --- Emission (UV0) ---
        if (material.IsKeywordEnabled("_EMISSION"))
        {
            data.EmissiveColor = ColorGradingApproximation.Apply(material.GetColor("_EmissionColor")).ToColorX_sRGB();
            data.EmissiveMap = LightmapPreviewUploadOnly ? null : context.GetITexture2D(material.GetTexture("_EmissionMap"));
        }
        else
        {
            // There's no actual toggle for emission on the Resonite version, so just set it to black
            data.EmissiveColor = Color.black.ToColorX_sRGB();
        }
        data.EmissionMapScale = mainTexScale;
        data.EmissionMapOffset = mainTexOffset;
        data.EmissionMapUV = 0;

        // --- Metallic / Smoothness (UV0) ---
        data.Metallic = material.GetFloat("_Metallic") * Mathf.Clamp01(MetallicCompensation);
        data.Smoothness = material.GetFloat("_Glossiness") * Mathf.Clamp01(SmoothnessCompensation);
        data.MetallicMap = LightmapPreviewUploadOnly ? null : context.GetITexture2D(material.GetTexture("_MetallicGlossMap"));
        data.MetallicMapScale = mainTexScale;
        data.MetallicMapOffset = mainTexOffset;
        data.MetallicMapUV = 0;

        // --- Baked lightmap (UV1) ---
        // Written by LightmapMaterialCache from LightmapSettings.lightmaps[i].lightmapColor and
        // renderer.lightmapScaleOffset onto this marker material's _BakedLightmap / _BakedLightmapST.
        var bakedLightmap = context.GetITexture2D(material.GetTexture("_BakedLightmap"));
        // Desaturated (luma-only) companion, see LightmapMaterialCache/LightmapDecoder's
        // desaturate doc comments - used for the additive fill below so per-object baked-lightmap
        // hue (window=cool, lamp=warm) doesn't leak into the brightness-only approximation.
        //
        // 2026-08-08 structural cleanup: only fetch/upload when the additive fill is actually
        // enabled - see AdditiveFillStrength's doc comment and the matching guard in
        // LightmapMaterialCache.GetVariantOrOriginalInner. At AdditiveFillStrength=0 this texture
        // contributes nothing (SecondaryEmissiveColor below is pure black), so uploading it would
        // be pure network waste.
        var bakedLightmapGray = AdditiveFillStrength > 0f
            ? context.GetITexture2D(material.GetTexture("_BakedLightmapGray"))
            : null;
        var lightmapScaleOffset = material.GetVector("_BakedLightmapST");
        var lightmapScale = new Vector2(lightmapScaleOffset.x, lightmapScaleOffset.y);
        var lightmapOffset = new Vector2(lightmapScaleOffset.z, lightmapScaleOffset.w);

        // Multiplicative half: always on, preserves the base albedo's own color instead of
        // washing it toward the lightmap's own hue/brightness.
        data.SecondaryAlbedoTexture = bakedLightmap;
        data.SecondaryAlbedoScale = lightmapScale;
        data.SecondaryAlbedoOffset = lightmapOffset;
        data.SecondaryAlbedoUV = 1;

        // Additive half: a damped, desaturated copy of the same data, approximating the ambient
        // fill Unity's baked GI would otherwise provide (Resonite has no baked-GI pipeline of its
        // own). AdditiveFillStrength=0 recovers the old pure-multiply behavior; 1 recovers the old
        // pure-additive (EmissiveLightmapMode=true) behavior applied on top of the multiply.
        // Desaturated rather than full-color: the raw baked lightmap carries each object's own
        // local color (window-side=cool, lamp-side=warm), and adding that directly made
        // differently-lit objects (couch near the window vs. bed near the lamp) diverge in hue
        // from each other in a way Unity's actual GI light transport never does.
        data.SecondaryEmissiveMap = bakedLightmapGray;
        data.SecondaryEmissionMapScale = lightmapScale;
        data.SecondaryEmissionMapOffset = lightmapOffset;
        data.SecondaryEmissionMapUV = 1;
        var fill = Mathf.Clamp01(AdditiveFillStrength);
        data.SecondaryEmissiveColor = new Color(fill, fill, fill, 1f).ToColorX_sRGB();

        return PBS.Data;
    }
}
