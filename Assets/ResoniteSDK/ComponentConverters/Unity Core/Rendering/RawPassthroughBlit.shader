// Full-screen passthrough Blit pass used by DirectionalLightmapBaker.cs (2026-07-11 3rd-pass
// bugfix, ロキ) to read LightmapData.lightmapDir (the RAW, un-decoded directional lightmap
// texture) back to the CPU via a Blit+ReadPixels sequence that is a FAITHFUL COPY of
// LightmapDecoder.DecodeAndSave's own Graphics.Blit(source, rt, material) call (see that method
// and LightmapDecode.shader in this same folder) — NOT DirectionalLightmapBaker's own earlier,
// argument-less Graphics.Blit(source, rt) (no material) call, which real-machine verification
// (per-renderer luminance correlation against LightmapDecoder's own production-proven decoded
// atlas, LMTex_0.png) proved does NOT reliably preserve the same bottom-origin pixel orientation
// LightmapDecoder's material-based Blit does on this project's actual D3D11 Editor target.
//
// LightmapDecode.shader's own vert() — o.vertex = UnityObjectToClipPos(v.vertex) — inherits
// Unity's own automatic per-platform Y-orientation compensation for a Blit's fullscreen quad,
// which the material-less Graphics.Blit overload apparently does not get on this target. This
// shader exists purely so dirTex's Blit readback can use that exact same (now-proven) vertex
// idiom while leaving ALL FOUR channels — including alpha — untouched: LightmapData.lightmapDir's
// alpha/.w channel is DecodeDirectionalLightmap's own denominator (see
// DirectionalLightmapBaker.cs's combine step), so it must survive this read bit-for-bit, unlike
// LightmapDecode.shader's own frag() (which always forces alpha to 1 for its own, color-only,
// purposes and would silently destroy dirTex's alpha channel if reused here as-is).
//
// Built-in RP shader (uses UnityCG.cginc). Only ever run through Graphics.Blit from Editor
// tooling code, never assigned to a renderer, so URP/HDRP compatibility is not a concern.
Shader "ResoniteSDK/Internal/RawPassthroughBlit"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }

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
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;

            v2f vert (appdata v)
            {
                v2f o;
                // Same vertex idiom as LightmapDecode.shader's own vert() — this is the piece
                // that carries Unity's automatic per-platform Blit Y-orientation compensation;
                // see this file's header comment for why that matters here.
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Pure passthrough — all four channels unmodified, no decode of any kind.
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }

    Fallback Off
}
