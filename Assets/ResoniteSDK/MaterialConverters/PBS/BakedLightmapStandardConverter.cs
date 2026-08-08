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
// 2026-08-08 (Tanossy指摘「ソファーの色味が違う」): AlbedoColor/EmissiveColorが
// ColorGradingApproximation.Apply(...)を経由していなかった不備を修正。StandardBaseConverter
// (非ライトマップ材質)側は既に呼んでいたが、このクラス(ライトマップ焼き込み材質=このシーンの
// 大半)は呼び忘れていたため、Tonemap Compensationがマテリアル色に一切効いていなかった。
[MaterialConverter(false, "ResoniteSDK/BakedLightmapStandard")]
public class BakedLightmapStandardConverter : ResoniteMaterialConverter
{
    // Experimental toggle for the in-world verification pass. Default (false) folds the baked
    // lightmap into SecondaryAlbedoTexture, which multiplies with AlbedoTexture the same way
    // Unity's baked lightmap multiplies with the albedo. If real-machine (ResoniteLink) testing
    // shows PBS_MultiUV_Metallic's SecondaryAlbedo compositing doesn't match that (e.g. it turns
    // out to be additive, or blended differently), flip this to true to instead route the
    // lightmap through the additive SecondaryEmissiveMap slot.
    //
    // 2026-08-08 (Tanossy指摘「暗い・ギラつく」への対応、クヴァシル調査で裏付け): Resoniteには
    // ベイクGI機構自体が存在しない(実証済み)。SecondaryAlbedo乗算合成は「照らされて見える色」を
    // 作るだけで、実際のシーン照明を追加しない — むしろベイクデータ(実測平均linear 0.06〜0.14、
    // すなわち1未満)を乗算するので必ず暗くなる方向にしか働かない。かつ環境光/環境スペキュラーが
    // 皆無なため、今回追加した実ライトのハイライトだけが唐突に浮く。SecondaryEmissiveMap経由の
    // 加算合成に切り替え、ベイクデータを「明るさを足す疑似アンビエント光」として扱う実機検証。
    public static bool EmissiveLightmapMode = true;

    // Verification mode for large baked scenes. The first in-world check only needs geometry,
    // material colors, and the baked lightmap; uploading every source albedo/normal/metallic/
    // occlusion/emission texture can overwhelm ResoniteLink before anything appears in-world.
    //
    // 2026-08-08 (Tanossy指摘): "AlbedoTexture等が送られていない"の根本原因がこのフラグだった。
    // ライトマップ経路自体は今日実機で繰り返し確認済み（SecondaryAlbedoTexture/URLとも正常に
    // 届くことを確認済み）なので、falseへ切り替える。
    public static bool LightmapPreviewUploadOnly = false;

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
        data.AlbedoColor = ColorGradingApproximation.Apply(material.GetColor("_Color")).ToColorX_sRGB();
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
        data.Metallic = material.GetFloat("_Metallic");
        data.Smoothness = material.GetFloat("_Glossiness");
        data.MetallicMap = LightmapPreviewUploadOnly ? null : context.GetITexture2D(material.GetTexture("_MetallicGlossMap"));
        data.MetallicMapScale = mainTexScale;
        data.MetallicMapOffset = mainTexOffset;
        data.MetallicMapUV = 0;

        // --- Baked lightmap (UV1) ---
        // Written by LightmapMaterialCache from LightmapSettings.lightmaps[i].lightmapColor and
        // renderer.lightmapScaleOffset onto this marker material's _BakedLightmap / _BakedLightmapST.
        var bakedLightmap = context.GetITexture2D(material.GetTexture("_BakedLightmap"));
        var lightmapScaleOffset = material.GetVector("_BakedLightmapST");
        var lightmapScale = new Vector2(lightmapScaleOffset.x, lightmapScaleOffset.y);
        var lightmapOffset = new Vector2(lightmapScaleOffset.z, lightmapScaleOffset.w);

        if (EmissiveLightmapMode)
        {
            data.SecondaryEmissiveMap = bakedLightmap;
            data.SecondaryEmissionMapScale = lightmapScale;
            data.SecondaryEmissionMapOffset = lightmapOffset;
            data.SecondaryEmissionMapUV = 1;
            data.SecondaryEmissiveColor = Color.white.ToColorX_sRGB();
        }
        else
        {
            data.SecondaryAlbedoTexture = bakedLightmap;
            data.SecondaryAlbedoScale = lightmapScale;
            data.SecondaryAlbedoOffset = lightmapOffset;
            data.SecondaryAlbedoUV = 1;
        }

        return PBS.Data;
    }
}
