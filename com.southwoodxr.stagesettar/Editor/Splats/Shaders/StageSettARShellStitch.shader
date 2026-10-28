// Flattens a captured cubemap into an equirectangular panorama, feathering the face joins.
//
// A 90-degree cube tiles the sphere exactly, so every face boundary is a hard edge, and any
// disagreement between neighbours lands on it as a line. Splats do disagree there: their
// covariance is projected through the Jacobian at the splat centre, an approximation exact on the
// optical axis and off by sec^2(theta) away from it -- a factor of two at the 45 degrees where
// every cube edge sits. Both faces draw the same splat mis-shaped, in different directions.
//
// The error itself cannot be fixed here; only narrower captures would do that. What can be fixed
// is that it has a hard edge to draw itself along. Near a join, each neighbour is also sampled
// with its coordinate clamped to its own rim -- its edge pixels extended outward -- and the two
// are cross-faded. At the boundary both contribute equally, so the result is continuous from
// either side, and the step becomes a ramp a few degrees wide.
//
// This also replaces RenderTexture.ConvertToEquirect, whose mapping is undocumented. The
// direction here is the exact inverse of what StageSettARShells.shader samples with, so the writer
// and the reader cannot drift apart.

Shader "Hidden/StageSettAR/ShellStitch"
{
    Properties {}

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURECUBE(_CubeFaces);
            SAMPLER(sampler_CubeFaces);

            // Half-width of the cross-fade in tan units, measured out from the 45-degree join.
            // Zero reproduces a hard cube.
            float _FeatherTan;

            // Non-zero writes an octahedral map instead of an equirectangular one. Used for depth,
            // which the backdrop shader looks up tens of times per pixel: octahedral coordinates
            // come out of a direction with a divide and a couple of selects, where equirect needs
            // an acos and an atan2.
            float _Octahedral;

            // Inverse of DirectionToOct in StageSettARShells.shader. The two are written as a pair on
            // purpose -- the writer and the reader of this map cannot be allowed to drift.
            float3 OctToDirection(float2 uv)
            {
                float2 e = uv * 2.0 - 1.0;
                float3 direction = float3(e.x, 1.0 - abs(e.x) - abs(e.y), e.y);
                if (direction.y < 0.0)
                {
                    float2 folded = (1.0 - abs(float2(e.y, e.x))) *
                                    float2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
                    direction.x = folded.x;
                    direction.z = folded.y;
                }
                return normalize(direction);
            }

            // Any orthonormal basis per axis works. The face's footprint is the unit square in tan
            // space either way -- a different choice of right/up only rotates that square onto
            // itself -- and sampling is by reconstructed direction, so the cubemap convention never
            // has to be guessed at.
            static const float3 k_forward[6] = {
                float3( 1, 0, 0), float3(-1, 0, 0),
                float3( 0, 1, 0), float3( 0,-1, 0),
                float3( 0, 0, 1), float3( 0, 0,-1)
            };
            static const float3 k_right[6] = {
                float3( 0, 0, 1), float3( 0, 0, 1),
                float3( 1, 0, 0), float3( 1, 0, 0),
                float3( 1, 0, 0), float3( 1, 0, 0)
            };
            static const float3 k_up[6] = {
                float3( 0, 1, 0), float3( 0, 1, 0),
                float3( 0, 0, 1), float3( 0, 0, 1),
                float3( 0, 1, 0), float3( 0, 1, 0)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float longitude = (0.75 - input.uv.x) * (2.0 * PI);
                float latitude = (1.0 - input.uv.y) * PI;
                float sinLat = sin(latitude);
                float3 equirect = float3(sinLat * cos(longitude), cos(latitude), sinLat * sin(longitude));
                float3 direction = _Octahedral > 0.5 ? OctToDirection(input.uv) : equirect;

                float outer = 1.0 + _FeatherTan;
                float inner = 1.0 - _FeatherTan;

                float4 accumulated = 0.0;
                float totalWeight = 0.0;

                [unroll]
                for (int face = 0; face < 6; face++)
                {
                    float forward = dot(direction, k_forward[face]);
                    if (forward <= 1e-4)
                        continue;

                    float2 projected = float2(dot(direction, k_right[face]),
                                              dot(direction, k_up[face])) / forward;

                    // 1 well inside the face, 0 past its feather rim, 0.5 exactly on the join --
                    // so two neighbours split it evenly there and the result is continuous.
                    float extent = max(abs(projected.x), abs(projected.y));
                    float weight = smoothstep(outer, inner, extent);
                    if (weight <= 0.0)
                        continue;

                    // Clamping is the edge-repeat: outside its own square a face contributes its
                    // nearest rim pixel rather than nothing, which is what gives the fade something
                    // to fade towards.
                    float2 clamped = clamp(projected, -1.0, 1.0);
                    float3 sampleDirection = k_forward[face] +
                                             clamped.x * k_right[face] +
                                             clamped.y * k_up[face];

                    accumulated += SAMPLE_TEXTURECUBE_LOD(_CubeFaces, sampler_CubeFaces,
                                                          sampleDirection, 0) * weight;
                    totalWeight += weight;
                }

                return totalWeight > 0.0 ? accumulated / totalWeight : 0.0;
            }
            ENDHLSL
        }

        // Splitting colour and alpha into a JPEG and a PNG needs the colour un-premultiplied
        // first: JPEG is lossy, and ringing around an alpha edge would push premultiplied colour
        // above its own alpha, which reads as an additive halo. Un-premultiplied colour is
        // well-defined wherever alpha is non-zero and arbitrary where it is not, so those texels
        // are then filled from their neighbours -- otherwise JPEG rings against the black sitting
        // in them and the fringe goes dark instead.
        Pass
        {
            Name "Unpremultiply"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_Source);
            SAMPLER(sampler_Source);

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            // Alpha carries a "known" flag from here on, not coverage. The real coverage is kept
            // in the source and written out separately, so this channel is free to drive the fill.
            float4 Frag(Varyings input) : SV_Target
            {
                float4 source = SAMPLE_TEXTURE2D_LOD(_Source, sampler_Source, input.uv, 0);
                return source.a > 0.0
                    ? float4(source.rgb / source.a, 1.0)
                    : float4(0.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Dilate"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_Source);
            SAMPLER(sampler_Source);
            float4 _Source_TexelSize;

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float4 centre = SAMPLE_TEXTURE2D_LOD(_Source, sampler_Source, input.uv, 0);
                if (centre.a > 0.0)
                    return centre;

                float3 sum = 0.0;
                float count = 0.0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        // Wrap in longitude so the meridian fills from both sides rather than
                        // treating the panorama's edge as empty.
                        float2 uv = input.uv + float2(x, y) * _Source_TexelSize.xy;
                        uv.x = frac(uv.x + 1.0);
                        float4 neighbour = SAMPLE_TEXTURE2D_LOD(_Source, sampler_Source, uv, 0);
                        sum += neighbour.rgb * neighbour.a;
                        count += neighbour.a;
                    }
                }

                return count > 0.0 ? float4(sum / count, 1.0) : float4(0.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }

        // Turns the accumulated (sum d*a*T, sum a*T) pair into a plain distance per texel. Alpha
        // becomes a validity flag: zero means nothing was hit that way, and the mesh builder drops
        // those rather than inventing a distance for empty sky.
        Pass
        {
            Name "ResolveDepth"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_Source);
            SAMPLER(sampler_Source);

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float4 source = SAMPLE_TEXTURE2D_LOD(_Source, sampler_Source, input.uv, 0);
                // A real threshold rather than an epsilon: both channels are eight bit, so dividing
                // one nearly empty texel by another is noise, and a direction that barely grazed
                // anything has no depth worth displacing a vertex to.
                return source.a > 0.02
                    ? float4(source.r / source.a, 0.0, 0.0, 1.0)
                    : float4(0.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }

        // Smooths the resolved depth panorama along one axis, weighting each tap by its own
        // validity so empty directions cannot drag a real distance toward zero.
        //
        // Two things come out of this. Splats are ellipsoids, so the nearest-surface depth of a
        // cluster of them is a field of overlapping bumps rather than the smooth surface the object
        // actually has; smoothing across a few texels turns the bumps back into the surface. And
        // because validity is blurred alongside the distance, coverage grows outward a little -- so
        // the soft tail of a splat, which the capture cutoff rejected as depth but the colour mask
        // still paints, now has a distance to march to. Without that, every reprojected splat is
        // clipped to the hard edge of its own depth footprint and reads as a flat disc rather than
        // a gaussian.
        Pass
        {
            Name "SmoothDepth"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_Source);
            SAMPLER(sampler_Source);
            float2 _BlurStep;
            float _BlurRadius;

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float sigma = max(_BlurRadius, 1e-4) * 0.5;
                float weighted = 0.0;   // sum of w * validity * distance
                float validity = 0.0;   // sum of w * validity
                float total = 0.0;      // sum of w

                for (int tap = -4; tap <= 4; tap++)
                {
                    if (abs(tap) > _BlurRadius)
                        continue;

                    float w = exp(-0.5 * (tap * tap) / (sigma * sigma));
                    float4 s = SAMPLE_TEXTURE2D_LOD(
                        _Source, sampler_Source, input.uv + _BlurStep * tap, 0);

                    weighted += w * s.a * s.r;
                    validity += w * s.a;
                    total += w;
                }

                // Distance is the average over the taps that had one; validity is the average over
                // all of them, so it ramps down across the edge of the recorded region rather than
                // ending on a step.
                return validity > 1e-5
                    ? float4(weighted / validity, 0.0, 0.0, validity / total)
                    : float4(0.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
