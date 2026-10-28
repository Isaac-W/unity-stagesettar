// CameraOverlay
// The material behind CameraFade and CameraCircleWipe: a flat colour over the view, with an
// optional circular hole punched in it.
//
// Written for URP with XR single-pass instanced rendering, which is what this project uses.
// The stereo macros are not optional there -- without them the overlay is written for one eye
// only and the other eye sees straight through it, which is both wrong and deeply unpleasant
// in a headset.
//
// This deliberately is not a post-process. A fullscreen blit fights XR, and a head-locked
// quad is what VR comfort fades are normally built from: it costs one transparent quad, it
// cannot break when the pipeline changes, and it sits at a real depth so the eyes converge
// on it instead of on nothing.

Shader "StageSettAR/CameraOverlay"
{
    Properties
    {
        _Color ("Colour", Color) = (0, 0, 0, 1)

        // Radius of the clear hole, where 0 is the centre and 1 reaches the edge midpoints.
        // A corner is at about 1.42, so ~1.5 clears the view entirely. Negative covers
        // everything, which is what a plain fade wants.
        _Radius ("Hole Radius", Float) = -1

        _Softness ("Edge Softness", Range(0.0001, 1)) = 0.02
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Overlay"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Overlay"

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            // Always, not LEqual: the point is to cover the view, and anything closer than
            // the quad would otherwise poke through the fade.
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Radius;
                float _Softness;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // -1..1 across the quad, so distance is measured from the middle outward.
                float2 centred = (input.uv - 0.5) * 2.0;
                float distance = length(centred);

                // Clear inside the hole, opaque outside it, with a soft edge between. A
                // ramp rather than smoothstep because the softness can be driven to almost
                // zero and smoothstep is undefined when its two edges meet.
                float cover = saturate((distance - _Radius) / max(_Softness, 0.0001));

                return half4(_Color.rgb, _Color.a * cover);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
