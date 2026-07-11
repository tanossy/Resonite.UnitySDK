// Used by DirectionalLightmapBaker.cs (via CommandBuffer.DrawMesh, not Graphics.Blit — this pass
// rasterizes a mesh's own triangles positioned by their LIGHTMAP UV (UV2) instead of by an actual
// camera/MVP transform, exactly the technique a lightmap/AO baker itself uses to bake data "into
// UV space"). Renders one renderer's interpolated *geometry* (vertex) world normal into every
// texel its own lightmap UV island covers, at the same texel density as its footprint in the
// shared lightmap atlas.
//
// Deliberately geometry-normal-only (reads the mesh's NORMAL vertex attribute, never samples a
// material's normal/bump map) — see DirectionalLightmapBaker.cs's header comment for why this is
// scoped as "第1段階" only; a tangent-space normal-map version is an explicit follow-up, not
// attempted here.
//
// Built-in RP shader (uses UnityCG.cginc), same style as LightmapDecode.shader in this same
// folder. Only ever driven by CommandBuffer.DrawMesh into an offscreen RenderTexture from Editor
// tooling code — never assigned to a renderer, so URP/HDRP compatibility is not a concern.
Shader "ResoniteSDK/Internal/Uv2NormalPatch"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                // Mesh's lightmap UV channel (Unity's own "UV2"/second UV set, as enabled by
                // ModelImporter.generateSecondaryUV — see LightmapTestHarness.
                // EnsureLightmapUVsEnabled) is bound to TEXCOORD1, matching Unity's own
                // convention for a mesh's uv2 stream (mirrors how unity_LightmapST/lightmapUV is
                // fed from the SAME stream in every one of Unity's own built-in lit shaders, e.g.
                // UnityStandardCore.cginc's appdata_full.texcoord1).
                float2 uv2 : TEXCOORD1;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;

                // Map the lightmap UV (0..1) directly to this pass's own clip-space position —
                // there is no camera involved; the render target IS the UV parameterization
                // (this pass covers exactly one renderer's own lightmap tile, at that tile's own
                // pixel dimensions — see DirectionalLightmapBaker.RenderUv2NormalPatch).
                float2 ndc = v.uv2 * 2.0 - 1.0;

                // _ProjectionParams.x is 1 or -1 depending on whether the currently active render
                // target needs its clip-space Y flipped (ildasm/CGIncludes-confirmed doc comment
                // on _ProjectionParams in UnityShaderVariables.cginc: "x = 1 or -1 (-1 if
                // projection is flipped)") — required here so a later plain 0..1 UV read of the
                // resulting texture lines up texel-for-texel with the same UV2 <-> pixel mapping
                // the shared atlas color/dir textures already use (same platform-Y-flip idiom
                // used by any offscreen UV-space bake, e.g. decal/lightmap tooling).
                o.vertex = float4(ndc.x, ndc.y * _ProjectionParams.x, 0.5, 1.0);

                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Encode -1..1 normal into 0..1 color, matching the C#-side decode in
                // DirectionalLightmapBaker.GetNormalBakedLightmapInner (`raw*2-1`, normalized).
                half3 n = normalize(i.worldNormal);
                return fixed4(n * 0.5 + 0.5, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
