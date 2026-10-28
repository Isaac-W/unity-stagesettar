// Capture variant of UnitySplats' Gsplat/Standard, used only while baking splat shells.
//
// A copy of UnitySplats 2.0.0's Runtime/Shaders/Gsplat.shader (Yize Wu, MIT) with one addition:
// the GSPLAT_DEPTH_CAPTURE keyword, which swaps the fragment output for view distance so a bake
// can record depth through the same coverage and blending the colour pass uses. Everything else
// is upstream's, including relighting and volume lighting, so a bake sees what the scene shows.
// The .hlsl files it builds on are included from the package rather than copied, so only this
// ShaderLab skeleton can drift when UnitySplats updates; re-diff it against upstream's
// Gsplat.shader after upgrading. SplatCaptureSwap installs it for the duration of a bake.

Shader "Hidden/StageSettAR/GsplatCapture"
{
    Properties {}
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }

        Pass
        {
            ZWrite Off
            Blend One OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // The structured-buffer variant needs shader model 4.5. The texture variant targets
            // shader model 3.5 so it remains available to GLES3 and Unity WebGL 2 players.
            #pragma target 4.5 GSPLAT_VERTEX_BUFFERS
            #pragma target 3.5 GSPLAT_VERTEX_TEXTURES
            #pragma multi_compile SH_BANDS_0 SH_BANDS_1 SH_BANDS_2 SH_BANDS_3 SH_BANDS_4
            #pragma multi_compile UNCOMPRESSED SPARK
            #pragma multi_compile GSPLAT_VERTEX_BUFFERS GSPLAT_VERTEX_TEXTURES
            // Swaps the fragment output for distance so a capture can record depth through the
            // exact coverage and blending the colour pass uses.
            //
            // Both stages, not _fragment. The v2f carries viewDistance and the vertex shader is
            // what fills it, so a fragment-only keyword compiles the vertex stage with the field
            // absent while the fragment stage expects it -- the signatures disagree and the draw
            // silently produces nothing at all.
            //
            // multi_compile rather than shader_feature: GsplatMaterial builds these materials at
            // runtime, so there is no material asset for a shader_feature variant to be compiled
            // against and enabling the keyword would find nothing.
            #pragma multi_compile_local __ GSPLAT_DEPTH_CAPTURE

            #ifdef GSPLAT_VERTEX_TEXTURES
            int _GsplatTextureWidth;
            #endif

            #include "UnityCG.cginc"
            #include "Packages/com.arloopa.unitysplats/Runtime/Shaders/Gsplat.hlsl"
            #ifdef UNCOMPRESSED
            #include "Packages/com.arloopa.unitysplats/Runtime/Shaders/GsplatUncompressed.hlsl"
            #endif
            #ifdef SPARK
            #include "Packages/com.arloopa.unitysplats/Runtime/Shaders/GsplatSpark.hlsl"
            #endif


            bool _GammaToLinear;
            int _SplatCount;
            int _SplatInstanceSize;
            int _SHDegree;
            float4x4 _MATRIX_M;
            float _Brightness;
            float _ScaleFactor;
            int _Antialiased;
            sampler2D _GsplatRelightMap;
            float4 _GsplatRelightScreen;
            sampler2D _GsplatCameraRelightMap;
            float _GsplatCameraRelightOverride;
            float4 _GsplatCameraRelightScreen;
            float4x4 _GsplatCameraRelightView;
            float4 _GsplatCameraRelightPosition;
            float4 _GsplatCameraRelightProjection;
            float4 _GsplatRelightParams;

            // Depth is written normalized between these, not against an absolute scale. 0 is the
            // band's inner radius and 1 its outer, so every bit of an eight-bit target is spent
            // inside the band, nothing beyond it can saturate into a wrong distance, and no
            // absolute range has to be guessed anywhere.
            float _DepthCaptureNear;
            float _DepthCaptureFar;

            // Above zero, capture the nearest surface instead of the mean through the volume.
            float _DepthCaptureCutoff;

            #include "Packages/com.arloopa.unitysplats/Runtime/Shaders/GsplatVolumeLighting.hlsl"
            #ifdef GSPLAT_VERTEX_TEXTURES
            Texture2D<float4> _OrderTexture;

            uint LoadSplatOrder(uint index)
            {
                float4 encoded = _OrderTexture.Load(
                    int3(GsplatLinearTextureCoord(index, _GsplatTextureWidth), 0));
                return GsplatDecodeTextureUInt(encoded);
            }
            #else
            StructuredBuffer<uint> _OrderBuffer;

            uint LoadSplatOrder(uint index)
            {
                return _OrderBuffer[index];
            }
            #endif

            struct appdata
            {
                float4 vertex : POSITION;
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint instanceID : SV_InstanceID;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            bool InitSource(appdata v, out SplatSource source)
            {
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                source.order = v.instanceID * _SplatInstanceSize + (uint)v.vertex.z;
                #else
                source.order = unity_InstanceID * _SplatInstanceSize + (uint)v.vertex.z;
                #endif

                if (source.order >= _SplatCount)
                    return false;

                source.id = LoadSplatOrder(source.order);
                source.cornerUV = float2(v.vertex.x, v.vertex.y) * _ScaleFactor;
                return true;
            }

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float3 volumeLighting : TEXCOORD1;
                float4 vertex : SV_POSITION;
                float4 color: COLOR;
                #ifdef GSPLAT_DEPTH_CAPTURE
                nointerpolation float viewDistance : TEXCOORD2;
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            bool GsplatCameraOverrideMatchesCurrentCamera()
            {
                float3 positionDelta =
                    _GsplatCameraRelightPosition.xyz - _WorldSpaceCameraPos.xyz;
                float positionError = dot(positionDelta, positionDelta);

                const float3 weights = float3(1.0, 1.0, 1.0);
                float rotationError =
                    dot(abs(_GsplatCameraRelightView[0].xyz - UNITY_MATRIX_V[0].xyz), weights) +
                    dot(abs(_GsplatCameraRelightView[1].xyz - UNITY_MATRIX_V[1].xyz), weights) +
                    dot(abs(_GsplatCameraRelightView[2].xyz - UNITY_MATRIX_V[2].xyz), weights);

                float2 currentProjection = float2(
                    abs(UNITY_MATRIX_P[0][0]),
                    abs(UNITY_MATRIX_P[1][1]));
                float2 projectionError =
                    abs(_GsplatCameraRelightProjection.xy - currentProjection);

                // A stale Scene-view global must never replace the Game-camera map carried by
                // this draw. The tolerances allow normal float roundoff but reject even a small
                // difference in camera pose, FOV, orthographic scale, or aspect.
                return positionError < 1e-4 &&
                       rotationError < 1e-3 &&
                       max(projectionError.x, projectionError.y) < 1e-3;
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = discardVec;

                SplatSource source;
                if (!InitSource(v, source))
                    return o;

                SplatCenter center;
                SplatCorner corner;
                float4 color;
                float3 modelCenter;
                float3 modelNormal;
                if (!InitSplatData(source, mul(UNITY_MATRIX_V, _MATRIX_M), _Antialiased != 0,
                                   center, corner, color, modelCenter, modelNormal))
                    return o;

                if (color.w <= 1.0 / 255.0)
                    return o;

                #ifndef SH_BANDS_0
                // calculate the model-space view direction
                float3 dir = normalize(mul(center.view, (float3x3)center.modelView));
                float3 sh[SH_COEFFS];
                InitSH(source.id, sh);
                color.rgb += EvalSH(sh, dir, _SHDegree);
                #endif

                ClipCorner(corner, color.w);

                o.vertex = center.proj + float4(corner.offset.x, _ProjectionParams.x * corner.offset.y, 0, 0);
                o.color = color;
                o.uv = corner.uv;
                o.volumeLighting = GsplatEvaluateVolumeLighting(modelCenter, modelNormal);
                #ifdef GSPLAT_DEPTH_CAPTURE
                // Distance from the camera, not depth along its axis, so every cube face agrees.
                o.viewDistance = length(center.view);
                #endif
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float A = dot(i.uv, i.uv);
                if (A > 1.0) discard;

                float2 absUV = abs(i.uv);
                float maxUV = max(absUV.x, absUV.y);

                float falloff = -exp((maxUV - _ScaleFactor * 1.16) * 25 * _ScaleFactor);
                float alpha = (exp(-A * 4.0) + falloff) * i.color.a;

                if (alpha < 1.0 / 255.0) discard;

                #ifdef GSPLAT_DEPTH_CAPTURE
                float normalized = saturate(
                    (i.viewDistance - _DepthCaptureNear) /
                    max(_DepthCaptureFar - _DepthCaptureNear, 1e-4));

                if (_DepthCaptureCutoff > 0.0)
                {
                    // Nearest surface. Splats are sorted back to front, so writing full alpha makes
                    // each qualifying splat completely replace whatever is already there and the
                    // last one drawn -- the nearest -- wins. No blend state changes hands: with
                    // One OneMinusSrcAlpha and alpha of 1, dst becomes exactly src.
                    //
                    // The mean through the volume is smoother but averages across depth
                    // discontinuities, so a ray catching floor at 15 m and treeline at 90 m reports
                    // neither, and the geometry ends up floating between them.
                    if (alpha < _DepthCaptureCutoff)
                        discard;
                    return float4(normalized, 0.0, 0.0, 1.0);
                }

                // Alpha-weighted expected depth: red accumulates sum(d*a*T) against alpha's
                // sum(a*T) through the unchanged blend, so dividing afterwards gives the mean
                // distance through the volume.
                return float4(normalized * alpha, 0.0, 0.0, alpha);
                #endif

                float3 color = _GammaToLinear ? GammaToLinearSpace(i.color.rgb) : i.color.rgb;
                if (_GsplatRelightParams.w > 0.5)
                {
                    // The normal Game-camera map and dimensions travel with the queued draw.
                    // This is deliberate: camera globals can be reset by a hidden proxy or
                    // another SRP camera after RenderMeshPrimitives has queued the splats.
                    // Scene view rendering opts into its camera-specific global capture.
                    bool useCameraOverride =
                        _GsplatCameraRelightOverride > 0.5 &&
                        GsplatCameraOverrideMatchesCurrentCamera();
                    float2 inverseScreen = useCameraOverride
                        ? _GsplatCameraRelightScreen.zw
                        : _GsplatRelightScreen.zw;
                    if (inverseScreen.x <= 0.0 || inverseScreen.y <= 0.0)
                        inverseScreen = 1.0 / _ScreenParams.xy;
                    float2 screenUV = saturate(i.vertex.xy * inverseScreen);
                    float4 lit;
                    if (useCameraOverride)
                        lit = tex2D(_GsplatCameraRelightMap, screenUV);
                    else
                        lit = tex2D(_GsplatRelightMap, screenUV);
                    float3 factor = lerp(
                        _GsplatRelightParams.zzz,
                        lit.rgb * _GsplatRelightParams.y,
                        lit.a);
                    color = lerp(color, color * factor, _GsplatRelightParams.x);
                }
                if (_GsplatVolumeEnabled != 0)
                    color = lerp(color, color * i.volumeLighting,
                                 _GsplatVolumeLightingParams.x);
                return float4(color * alpha * _Brightness, alpha);
            }
            ENDHLSL


        }
    }
}
