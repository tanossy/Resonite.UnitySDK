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
//
// 2026-07-12 bugfix (バグハンター指摘, ダイダロス対応): vert() below used to additionally flip its
// own manually-built clip-space Y by `_ProjectionParams.x`, attempting to hand-replicate Unity's
// automatic per-platform Blit-quad Y-orientation compensation. That's unreliable here:
// `_ProjectionParams` is populated by Unity's camera pipeline (SetupCameraProperties) for
// whichever camera is CURRENTLY rendering, and this pass has no camera at all — it's driven by
// Graphics.ExecuteCommandBuffer against an offscreen target with no SetViewProjectionMatrices
// call either — so the value actually read here was leftover global shader state from whatever
// last rendered a real camera (e.g. the Scene/Game view), not something meaningfully tied to THIS
// render target. This is the same class of silent orientation bug this feature's
// DirectionalLightmapBaker.cs already found (three times) for its own color/dir atlas readback.
//
// Fixed by removing the manual flip attempt from this shader entirely: this pass now writes its
// raw, UNCOMPENSATED clip-space output, and DirectionalLightmapBaker.RenderUv2NormalPatch instead
// routes the resulting RenderTexture through BlitReadableAtlas — the SAME already-proven
// (real-machine, luminance-correlation-verified) RawPassthroughBlit.shader + UnityObjectToClipPos
// Blit-quad readback DirectionalLightmapBaker.cs already uses for the directional lightmap
// texture — instead of a second, independent, never-verified guess at the same correction. See
// RenderUv2NormalPatch's own doc comment (DirectionalLightmapBaker.cs) for the full reasoning.
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
                //
                // 2026-07-12 bugfix (バグハンター指摘): deliberately NO per-platform Y-flip here
                // anymore (see this file's own header comment) — this pass's raw, uncompensated
                // clip-space output is intentional; DirectionalLightmapBaker.RenderUv2NormalPatch
                // applies the actual (proven) orientation correction downstream via
                // BlitReadableAtlas instead of this shader guessing at it with no camera context.
                float2 ndc = v.uv2 * 2.0 - 1.0;
                o.vertex = float4(ndc.x, ndc.y, 0.5, 1.0);

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
