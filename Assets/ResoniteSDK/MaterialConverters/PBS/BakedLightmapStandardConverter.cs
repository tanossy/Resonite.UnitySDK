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
    // 2026-08-08 (Tanossy指摘「暗い・ギラつく」への対応、続き): 乗算のみ→暗すぎ、加算のみ→色が
    // 白飛び、という二択がどちらも実機で不合格だったため、ハイブリッドに変更。SecondaryAlbedo
    // 乗算(色の忠実度を保つ本来の合成)は常時維持しつつ、それとは別にSecondaryEmissiveMap経由で
    // 同じベイクデータを控えめな強度で追加し、Unity側のベイクGIが担っていた「陰を持ち上げる
    // アンビエントフィル」を近似する。この値は乗算成分に対する加算成分の相対的な強さ
    // (0=純粋乗算のみ、1=旧EmissiveLightmapMode=true相当のフル加算)。0.35は初回の勘所であり、
    // 実機で見た目を確認しながら調整する前提の未確定値。
    // 2026-08-08 (Tanossy指摘「白は白っぽく、茶は茶っぽくコントラストが無いと困る」): 加算フィルは
    // 原理的にコントラストを潰す — 暗い色に一定量を足すと明るい色との比率が縮まり、
    // 白と茶色の差が薄れる(9:1の反射率差が、+0.3を足すと2.5:1まで縮む、という単純な算数)。
    // 乗算(SecondaryAlbedo)は比率を保ったまま明暗だけ変わるので、色の書き分けを壊さない。
    // 加算フィルは0に戻し、明るさの底上げはLightmapDecoder.RangeScale(乗算前のゲイン)側で行う
    // 方針に転換。
    public static float AdditiveFillStrength = 0.0f;

    // 2026-08-08 (Tanossy指摘、続き): LightConverter.IntensityMultiplierでライトを明るくした分、
    // 同じSmoothness値でもハイライトが比例して強く出るようになった(実機確認: ベッドの掛け布団が
    // 金属光沢のように見えた)。この値はスカラーSmoothness経路(MetallicMap未設定のマテリアル、
    // 例: bed01)にのみ効く — MetallicMapがあるマテリアル(couch01等、テクスチャのアルファ経由で
    // Smoothnessが決まる)には別途アルファ側の減衰が必要(couch01_MetallicSmoothness等の
    // 再生成時に反映)。0.6は初回の勘所。
    public static float SmoothnessCompensation = 0.05f;

    // 2026-08-08追記(Tanossy指摘「黒が黒でない」): Smoothness補正だけでは金属マテリアル
    // (例: black metal, Metallic=0.844)のギラつきを抑えきれなかった——金属は拡散反射をほぼ
    // 持たず、見た目のほぼ全てがスペキュラー反射なので、Smoothnessをいくら下げても
    // 「反射が鈍くなる」だけで「反射しなくなる」わけではない。AlbedoColorを真っ黒にしても
    // 金属面は明るい光源を映し込み続ける。Metallic自体も送信時に減衰させる。
    public static float MetallicCompensation = 0.0f;

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
        var bakedLightmapGray = context.GetITexture2D(material.GetTexture("_BakedLightmapGray"));
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
