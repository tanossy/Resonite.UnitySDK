// Full-screen decode pass used by LightmapDecoder.cs (via Graphics.Blit) to turn a Unity baked
// lightmap texture (LightmapSettings.lightmaps[i].lightmapColor) into linear HDR values before
// they're clamped/gamma-encoded to a persisted PNG asset for use by
// ResoniteSDK/BakedLightmapStandard's _BakedLightmap.
//
// _DecodeMode is set explicitly from C# (LightmapDecoder.DetermineDecodeMode) rather than
// resolved via shader_feature/multi_compile keywords, so the exact decode path taken is always
// known and controllable from the calling code instead of depending on which keyword variant
// happened to get compiled/stripped.
//
// _DecodeInstructions is likewise set explicitly from C# (LightmapDecoder.DetermineDecodeInstructions)
// rather than hardcoded here as literals (Loki 2nd-pass review 指摘1) - see that method's doc
// comment for exactly which UnityCG.cginc constants it mirrors and why (colorSpace-dependent for
// mode 2/Double-LDR, colorSpace-independent for mode 1/RGBM).
//
// Built-in RP shader (uses UnityCG.cginc). This is only ever run through Graphics.Blit from
// Editor tooling code, never assigned to a renderer, so URP/HDRP compatibility is not a concern.
Shader "ResoniteSDK/Internal/LightmapDecode"
{
    Properties
    {
        _MainTex ("Encoded Lightmap", 2D) = "white" {}
        _DecodeMode ("Decode Mode (0 = HDR passthrough, 1 = RGBM, 2 = Double-LDR)", Float) = 1
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
            float _DecodeMode;
            float4 _DecodeInstructions; // Set from C# - see LightmapDecoder.DetermineDecodeInstructions.

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 raw = tex2D(_MainTex, i.uv);

                half3 decoded;

                if (_DecodeMode < 0.5)
                {
                    // Mode 0: source is already true HDR pixel data (BC6H / RGBAHalf /
                    // RGBAFloat) - passthrough, mirrors UnityCG.cginc's DecodeLightmap()
                    // "#else //defined(UNITY_LIGHTMAP_FULL_HDR)" branch (see that function).
                    decoded = raw.rgb;
                }
                else if (_DecodeMode < 1.5)
                {
                    // Mode 1: RGBM decode, using UnityCG.cginc's real DecodeLightmapRGBM().
                    // _DecodeInstructions.x/.y are supplied by C# (LightmapDecoder.
                    // DetermineDecodeInstructions) rather than hardcoded here - see that method's
                    // doc comment for exactly which UnityCG.cginc constants they mirror (x =
                    // LIGHTMAP_RGBM_SCALE = 5.0, y = 2.2). DecodeLightmapRGBM() itself already
                    // branches on UNITY_COLORSPACE_GAMMA internally (it's the real engine
                    // function, included above), silently ignoring y in the gamma-space branch.
                    decoded = DecodeLightmapRGBM(raw, _DecodeInstructions);
                }
                else
                {
                    // Mode 2: Double-LDR decode (legacy scheme used by some mobile build
                    // targets), using UnityCG.cginc's real DecodeLightmapDoubleLDR(). Not
                    // currently selected by LightmapDecoder.DetermineDecodeMode() in C# - see
                    // that method's comment for why (can't reliably distinguish this from RGBM
                    // using only the baked texture's pixel format). Kept here so the shader is
                    // ready once that C#-side detection is confirmed on real hardware.
                    // _DecodeInstructions.x is colorSpace-dependent here (see
                    // DetermineDecodeInstructions) - unlike DecodeLightmapRGBM above,
                    // DecodeLightmapDoubleLDR() does NOT branch on UNITY_COLORSPACE_GAMMA
                    // internally, so the caller (C#) must pick the right constant.
                    decoded = DecodeLightmapDoubleLDR(raw, _DecodeInstructions);
                }

                return fixed4(decoded, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
