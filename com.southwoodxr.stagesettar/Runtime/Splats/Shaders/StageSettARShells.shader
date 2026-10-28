// Inside-out sphere carrying a baked equirectangular capture of a splat's far field.
//
// The capture is rendered against a fully transparent clear, so it arrives premultiplied and
// keeps its holes: wherever the far field was empty, this backdrop stays empty too and the
// camera's clear colour (or passthrough) shows through. That is the whole reason this is a mesh
// with an alpha blend rather than a skybox, which would have to bake some opaque colour into
// every gap.

Shader "StageSettAR/Shells"
{
    Properties
    {
        _MainTex ("Panorama (equirect)", 2D) = "black" {}

        // Set when colour and coverage are stored separately -- a lossy JPEG for the colour, which
        // is then un-premultiplied, and a lossless mask for the alpha it would otherwise ring
        // against.
        [Toggle(GSPLAT_SPLIT_ALPHA)] _SplitAlpha ("Separate Alpha Mask", Float) = 0
        _AlphaTex ("Coverage Mask", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)

        // Exposed so a mesh wound the other way can be corrected without re-baking. Front (1) is
        // right for an outward-wound sphere, which is what the baker and Unity's built-in produce.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 1

        // Yaw offset in degrees, for panoramas that did not come out of this baker.
        _Rotation ("Yaw Offset", Range(-180, 180)) = 0

        // Per-pixel reprojection against a captured depth panorama.
        [Toggle(GSPLAT_BACKDROP_REPROJECT)] _Reproject ("Reproject By Depth", Float) = 0
        _DepthTex ("Depth Panorama", 2D) = "black" {}
        _DepthNear ("Depth Near", Float) = 0
        _DepthFar ("Depth Far", Float) = 1
        _MarchSteps ("March Steps", Range(4, 64)) = 24
        _RefineSteps ("Refine Steps", Range(0, 8)) = 4
        // 0 off, 1 marched hit distance, 2 raw depth panorama. Metres are mapped across the band,
        // so black is its inner radius and white its outer.
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            // Ahead of the splats, which sit at Transparent (3000), so they blend over this.
            "Queue" = "Transparent-100"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Backdrop"

            // Seen from the inside.
            Cull [_Cull]
            ZWrite Off
            ZTest LEqual
            // Matches the splat shader: the captured texels are already premultiplied.
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment GSPLAT_SPLIT_ALPHA
            // Both stages, not _fragment. This keyword guards positionWS in Varyings and the
            // vertex line that fills it, so scoping it to the fragment compiles the vertex stage
            // without that field while the fragment expects it -- the signatures disagree and
            // nothing draws at all. GSPLAT_SPLIT_ALPHA below is genuinely fragment-only and stays
            // that way.
            #pragma shader_feature_local GSPLAT_BACKDROP_REPROJECT
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_AlphaTex);
            SAMPLER(sampler_AlphaTex);
            TEXTURE2D(_DepthTex);
            SAMPLER(sampler_DepthTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Tint;
                // Declared here as well as in Properties so the SRP Batcher stays happy.
                float _Cull;
                float _Rotation;
                float _SplitAlpha;
                float4 _AlphaTex_ST;
                float _Reproject;
                float4 _DepthTex_ST;
                float _DepthNear;
                float _DepthFar;
                float _MarchSteps;
                float _RefineSteps;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 directionOS : TEXCOORD0;
                #ifdef GSPLAT_BACKDROP_REPROJECT
                float3 positionWS : TEXCOORD1;
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Direction to equirect coordinates. Shared so the reprojection and the final sample
            // cannot disagree about the mapping.
            //
            // 0.75, not the 0.5 Unity's Skybox/Panoramic uses. That shader reads a panorama
            // supplied from outside, and puts +X at the centre of the image; the stitch that writes
            // this one puts forward there instead. Forward is (0,0,1), so longitude is PI/2 and 0.5
            // would land it at u = 0.25 -- a quarter turn out, which is exactly how the mismatch
            // showed up.
            // Direction to octahedral coordinates, which is how the depth panorama is stored.
            //
            // The march samples depth once per step, per refinement, and once more for the probe --
            // getting on for twenty lookups per pixel, per shell. Through an equirect that is
            // twenty acos and twenty atan2, neither of them a single instruction on a mobile GPU,
            // and it was plausibly costing more than the fetches themselves. This is a divide, an
            // L1 norm and a couple of selects.
            //
            // It also spends its texels better. Equirect crowds them at the poles and starves the
            // equator; an octahedron is near enough uniform, so a square map of half the area
            // carries the same angular detail where the detail actually is.
            //
            // Colour stays equirect: it is sampled once, so the transcendentals are free there,
            // and a JPEG of a lat-long panorama is inspectable in a way an octahedral one is not.
            float2 DirectionToOct(float3 direction)
            {
                direction /= abs(direction.x) + abs(direction.y) + abs(direction.z);
                float2 e = float2(direction.x, direction.z);
                if (direction.y < 0.0)
                    e = (1.0 - abs(float2(e.y, e.x))) *
                        float2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
                return e * 0.5 + 0.5;
            }

            float2 DirectionToUV(float3 direction)
            {
                float latitude = acos(clamp(direction.y, -1.0, 1.0));
                float longitude = atan2(direction.z, direction.x);
                return float2(0.75, 1.0) -
                       float2(longitude, latitude) * float2(0.5 / PI, 1.0 / PI);
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // Object space, not view direction. The sphere sits at a real distance, so a
                // viewer moving inside it gets real parallax against the backdrop instead of the
                // locked-to-the-head feel a skybox would give.
                output.directionOS = input.positionOS.xyz;
                #ifdef GSPLAT_BACKDROP_REPROJECT
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                #endif
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 direction = normalize(input.directionOS);
                // Derivatives are taken here, from the shell's own direction, before anything
                // below can branch. ddx and ddy compare neighbouring pixels in a quad, so they are
                // only meaningful in control flow every pixel of that quad took -- and the
                // reprojection below has a break in it. Computing them afterwards returns garbage,
                // the hardware picks the smallest mip, and a mostly transparent panorama averages
                // to nothing: the shell disappears entirely.
                //
                // Reprojection only nudges the direction, by a stride against tens of metres, so
                // the footprint measured here is still the right size for the texel it lands on.
                float2 baseUV = DirectionToUV(direction);
                float2 dx = ddx(baseUV);
                float2 dy = ddy(baseUV);
                // atan2 wraps at +-PI, so u jumps a full turn along one meridian. Harmless under
                // Repeat, but the jump makes the derivative enormous and picks the smallest mip
                // again -- which read as a missing strip of sky before this was unwrapped.
                dx.x = frac(dx.x + 0.5) - 0.5;
                dy.x = frac(dy.x + 0.5) - 0.5;

                #ifdef GSPLAT_BACKDROP_REPROJECT
                // How much recorded validity a texel needs before its distance is trusted.
                //
                // Deliberately low. The capture writes validity as a hard one or zero, but the bake
                // blurs it, so the edge of a recorded region arrives here as a ramp. Half would cut
                // that ramp in the middle and put the hard edge straight back; a small threshold
                // keeps the dilated rim, which is exactly where a splat's soft tail needs a distance
                // to march to if it is to come out a gaussian rather than a disc.
                #define GSPLAT_DEPTH_VALID 0.04

                // Search the view ray for where it first crosses the recorded surface.
                //
                // The obvious cheaper thing -- refine the shell's own direction a few times until
                // it settles -- corrects the interior of a surface nicely and cannot move its
                // outline at all. A pixel whose ray should now strike the house, because the viewer
                // stepped sideways, begins by sampling sky, nudges, and samples sky again: it never
                // discovers the house, because it is refining a guess rather than looking for one.
                // The result is corrected content seen through the uncorrected silhouette, with
                // untouched shell surrounding it.
                //
                // Marching finds the crossing instead, so silhouettes move. It is the expensive
                // part of this technique and the reason it works.
                {
                    float3 origin = mul(unity_ObjectToWorld, float4(0.0, 0.0, 0.0, 1.0)).xyz;
                    float3 viewRay = normalize(input.positionWS - _WorldSpaceCameraPos);

                    // The band is a shell between two spheres about the capture origin, and the
                    // viewer is inside both, so the ray leaves each exactly once. Those two exits
                    // bound the search: there is nothing of this band's to find outside them.
                    float3 toOrigin = origin - _WorldSpaceCameraPos;
                    float along = dot(toOrigin, viewRay);
                    float perpSq = dot(toOrigin, toOrigin) - along * along;
                    float tNear = along + sqrt(max(_DepthNear * _DepthNear - perpSq, 0.0));
                    float tFar = along + sqrt(max(_DepthFar * _DepthFar - perpSq, 0.0));

                    if (tFar > tNear)
                    {
                        int marchSteps = (int)max(_MarchSteps, 4.0);
                        float dt = (tFar - tNear) / marchSteps;

                        // The bracket, and the signed distance from the surface at each end of
                        // it: negative in front of the recorded surface, positive behind. Bisection
                        // needs only the signs, but the secant step at the end needs the
                        // magnitudes, which is the whole reason they are carried.
                        float tPrev = tNear;
                        float fPrev = 0.0;
                        bool bracketed = false;
                        float tHit = -1.0;
                        float fHit = 0.0;

                        // Stepping to tNear + dt * step, so the last sample lands on tFar rather
                        // than a step short of it, and the bracket starts at tNear -- which the ray
                        // has only just entered, so it is in front of anything recorded here.
                        for (int step = 1; step <= marchSteps; step++)
                        {
                            float t = tNear + dt * step;
                            float3 samplePoint = _WorldSpaceCameraPos + viewRay * t;
                            float3 outward = samplePoint - origin;
                            float radius = length(outward);

                            float4 sampled = SAMPLE_TEXTURE2D_LOD(
                                _DepthTex, sampler_DepthTex, DirectionToOct(outward / radius), 0);
                            // Nothing recorded that way, so there is no surface here to cross.
                            // The bracket still moves forward, but there is no distance to
                            // interpolate from, so the secant step has to sit this one out.
                            if (sampled.a < GSPLAT_DEPTH_VALID)
                            {
                                tPrev = t;
                                bracketed = false;
                                continue;
                            }

                            float f = radius - lerp(_DepthNear, _DepthFar, sampled.r);

                            // Crossed behind the recorded surface: the hit is between here and the
                            // previous step.
                            if (f >= 0.0)
                            {
                                tHit = t;
                                fHit = f;
                                break;
                            }

                            tPrev = t;
                            fPrev = f;
                            bracketed = true;
                        }

                        // A miss has to be a miss. Falling back to the shell's own direction
                        // draws the panorama where an unreprojected sphere would have put it,
                        // which is what produced a corrected house standing in front of an
                        // uncorrected copy of itself.
                        if (tHit <= 0.0)
                        {
                            discard;
                        }
                        else
                        {
                            // Bisect the straddling interval a few times. This is the robust
                            // half of the refinement: it assumes nothing about the surface, which
                            // is what makes it safe across a silhouette, where the recorded
                            // distance jumps and no interpolation between the two ends means
                            // anything.
                            int refineSteps = (int)max(_RefineSteps, 0.0);
                            for (int refine = 0; refine < refineSteps; refine++)
                            {
                                float mid = 0.5 * (tPrev + tHit);
                                float3 samplePoint = _WorldSpaceCameraPos + viewRay * mid;
                                float3 outward = samplePoint - origin;
                                float radius = length(outward);

                                float4 sampled = SAMPLE_TEXTURE2D_LOD(
                                    _DepthTex, sampler_DepthTex, DirectionToOct(outward / radius), 0);
                                if (sampled.a < GSPLAT_DEPTH_VALID)
                                {
                                    tPrev = mid;
                                    bracketed = false;
                                    continue;
                                }

                                float f = radius - lerp(_DepthNear, _DepthFar, sampled.r);
                                if (f >= 0.0)
                                {
                                    tHit = mid;
                                    fHit = f;
                                }
                                else
                                {
                                    tPrev = mid;
                                    fPrev = f;
                                    bracketed = true;
                                }
                            }

                            // One probe to rescue the interpolation where the march lost it.
                            //
                            // The near end of the bracket carries no distance if the step that set
                            // it landed somewhere nothing was recorded, and that is not a rare
                            // case: a band's depth covers a tenth of the sky, so plenty of rays
                            // reach a surface having passed through empty directions to get there.
                            // Those rays fell back to bisection alone -- terracing, on exactly the
                            // silhouettes and band entries where it is most visible. Bisection has
                            // by now pulled the near end close to the hit, so a single sample there
                            // usually finds the same surface and hands the secant what it needs.
                            if (!bracketed)
                            {
                                float3 outward = _WorldSpaceCameraPos + viewRay * tPrev - origin;
                                float radius = length(outward);

                                float4 sampled = SAMPLE_TEXTURE2D_LOD(
                                    _DepthTex, sampler_DepthTex, DirectionToOct(outward / radius), 0);
                                float f = radius - lerp(_DepthNear, _DepthFar, sampled.r);
                                if (sampled.a >= GSPLAT_DEPTH_VALID && f < 0.0)
                                {
                                    fPrev = f;
                                    bracketed = true;
                                }
                            }

                            // Then one secant step, and this is what stops the terracing.
                            //
                            // Bisection halves the bracket and leaves one; a bracket is a step, and
                            // a step is what the eye reads as a terrace. Interpolating instead uses
                            // how far each end actually sits from the surface rather than only
                            // which side it is on. Where the surface is locally flat and the ray
                            // locally straight -- which is nearly everywhere that is not an edge --
                            // the signed distance is near-linear in t, so this lands essentially on
                            // the crossing and, more to the point, slides continuously as the
                            // surface and the viewer move. No residual bracket, so nothing to
                            // terrace.
                            //
                            // Clamped inside the bracket because at a silhouette the linear
                            // assumption is worthless, and bisection has already made the bracket
                            // small enough that being wrong inside it costs little.
                            if (bracketed)
                            {
                                float span = fHit - fPrev;
                                if (span > 1e-6)
                                    tHit = clamp(tPrev + (tHit - tPrev) * (-fPrev / span),
                                                 tPrev, tHit);
                            }

                            direction = normalize(
                                _WorldSpaceCameraPos + viewRay * tHit - origin);
                        }
                    }
                    else
                    {
                        // Degenerate band, nothing to search.
                        discard;
                    }
                }
                #endif

                float2 uv = DirectionToUV(direction);
                uv.x += _Rotation * (1.0 / 360.0);

                half4 colour = SAMPLE_TEXTURE2D_GRAD(_MainTex, sampler_MainTex, uv, dx, dy);

                #ifdef GSPLAT_SPLIT_ALPHA
                // The JPEG holds un-premultiplied colour and carries no usable alpha of its own,
                // so coverage comes from the mask and the two are recombined here into the
                // premultiplied form this pass blends with.
                half coverage = SAMPLE_TEXTURE2D_GRAD(_AlphaTex, sampler_AlphaTex, uv, dx, dy).r;
                colour = half4(colour.rgb * coverage, coverage);
                #endif

                return colour * _Tint;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
