// NextBots/ScreenSaturation
//
// A full-screen pass for URP, run by a FullScreenPassRendererFeature over the finished picture -
// transparents included. It does the whole "you just got hit" look in one go:
//
//   * real desaturation: every pixel is pulled towards its OWN luminance, so colour drains without
//     the picture being flattened or brightened, which is what a see-through grey layer cannot do;
//   * an optional dim, for the moment after impact;
//   * a vignette at the edges in any colour - red for the hit, dark afterwards - shaped to the screen.
//
// Everything is driven by five material properties and is a no-op at their defaults.
Shader "NextBots/ScreenSaturation"
{
    Properties
    {
        _Desaturate ("Desaturate (0 = normal, 1 = black and white)", Range(0, 1)) = 0
        _Dim ("Dim", Range(0, 1)) = 0
        _VigColor ("Vignette colour", Color) = (0, 0, 0, 1)
        _VigAmount ("Vignette amount", Range(0, 1)) = 0
        _VigStart ("Vignette starts at (0 = centre, 1 = corner)", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "NextBotsScreenSaturation"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // Single-pass instanced VR draws both eyes in one go and needs its own variant of the
            // shader. Unity's built-in STEREO_INSTANCING_ON variant is stripped from a bundle built in a
            // project with no XR set up, whatever the settings say, so this is a keyword of the shader's
            // own that cannot be stripped: the mod turns it on only when the game really is rendering
            // in single-pass VR, and it stands in for the built-in one before anything reads it.
            #pragma multi_compile _ NB_STEREO
            #if defined(NB_STEREO) && !defined(STEREO_INSTANCING_ON)
                #define STEREO_INSTANCING_ON 1
            #endif

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Desaturate;
            float _Dim;
            float4 _VigColor;
            float _VigAmount;
            float _VigStart;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

                // Rec.709 luminance: the grey each pixel is already worth.
                half luma = dot(c, half3(0.2126h, 0.7152h, 0.0722h));
                c = lerp(c, half3(luma, luma, luma), (half)_Desaturate);
                c *= (half)(1.0 - _Dim);

                // Distance from the centre over the distance to a corner, so the vignette reaches
                // every corner of whatever shape the screen is.
                float2 d = (uv - 0.5) * 2.0;
                float r = saturate(length(d) * 0.70710678);
                float v = smoothstep(_VigStart, 0.98, r) * _VigAmount;
                c = lerp(c, _VigColor.rgb, (half)v);

                return half4(c, 1.0h);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
