// Camera image -> network input, a GPU port of star-mobile camera/resample.ts.
// Output pixel (top-left origin) -> point in the rotated image (crop window) -> point in the source,
// averaged over taps x taps bilinear fetches (box-filtered, so downscaling is anti-aliased like the
// training frames). Taps that land outside the source are black, which pads a small image up to the
// fixed network size. The result is the 0..1 value of the sRGB-ENCODED 8-bit colour (what a browser
// canvas gives the web app); the detector multiplies by 255 inside the Sentis graph.
// The source is either an RGB texture or camera YUV 4:2:0 planes (_Yuv.x = 1 or 2), converted here with
// BT.601 full range in the same pass, so the camera needs no CPU colour conversion. YUV values are already
// the encoded 8-bit colour, so they skip the linear-to-sRGB step.
Shader "Hidden/StageSettAR/Preprocess"
{
    Properties
    {
        _MainTex ("Source (RGB, or luma)", 2D) = "white" {}
        _ChromaU ("Chroma (Cb, or Cb and Cr interleaved)", 2D) = "gray" {}
        _ChromaV ("Chroma Cr", 2D) = "gray" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Sizes;  // source w, h, rotated w, h
            float4 _Crop;   // x0, y0, cw, ch in the rotated image
            float4 _Out;    // output w, h, clockwise quarter turns, taps
            float4 _Flags;  // x: 1 = texture row 0 (v = 0) is the image's bottom row; y: 1 = fetches are linear, encode to sRGB
            float4 _Yuv;    // x: 0 = RGB, 1 = luma + interleaved CbCr (RG), 2 = luma + separate Cb, Cr; y: chroma texel step (mode 2); z, w: luma texture size
            float4 _ChromaSize; // chroma texture w, h
            sampler2D _ChromaU, _ChromaV;

            float3 LinearToSrgb(float3 c)
            {
                c = saturate(c);
                return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
            }

            // BT.601 full range, all values 0..1 (Cb and Cr centred on 0.5).
            float3 YuvToRgb(float y, float2 cbcr)
            {
                float cb = cbcr.x - 0.5, cr = cbcr.y - 0.5;
                return saturate(float3(y + 1.402 * cr, y - 0.344136 * cb - 0.714136 * cr, y + 1.772 * cb));
            }

            float2 FetchCbCr(float ix, float iy)
            {
                float texelStep = _Yuv.x > 1.5 ? _Yuv.y : 1.0;
                float4 uv = float4((ix * texelStep + 0.5) / _ChromaSize.x, (iy + 0.5) / _ChromaSize.y, 0, 0);
                if (_Yuv.x < 1.5) return tex2Dlod(_ChromaU, uv).rg;
                return float2(tex2Dlod(_ChromaU, uv).r, tex2Dlod(_ChromaV, uv).r);
            }

            // Bilinear chroma at source pixel position s (pixel centres at +0.5). Chroma sample j covers luma pixels 2j, 2j+1.
            float2 SampleCbCr(float2 s)
            {
                float2 maxIdx = floor((_Sizes.xy + 1.0) * 0.5) - 1.0;
                float2 c = s * 0.5 - 0.5;
                float2 i0 = floor(c);
                float2 f = c - i0;
                float2 a = clamp(i0, 0.0, maxIdx), b = clamp(i0 + 1.0, 0.0, maxIdx);
                float2 top = lerp(FetchCbCr(a.x, a.y), FetchCbCr(b.x, a.y), f.x);
                float2 bottom = lerp(FetchCbCr(a.x, b.y), FetchCbCr(b.x, b.y), f.x);
                return lerp(top, bottom, f.y);
            }

            // Undo clockwise turns: one turn maps (u, v) of a w x h image to (h - v, u).
            float2 ToSource(float2 r)
            {
                float2 size = _Sizes.zw;
                [unroll] for (int i = 0; i < 3; i++)
                {
                    if (i >= (int)_Out.z) break;
                    r = float2(r.y, size.x - r.x);
                    size = size.yx;
                }
                return r;
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float2 o = float2(i.uv.x, 1.0 - i.uv.y) * _Out.xy;   // pixel centre, y from the top
                float scale = _Crop.z / _Out.x;
                int taps = (int)_Out.w;
                float3 acc = 0;
                [loop] for (int a = 0; a < 4; a++)
                {
                    if (a >= taps) break;
                    [loop] for (int b = 0; b < 4; b++)
                    {
                        if (b >= taps) break;
                        float2 sub = (float2(a, b) + 0.5) / taps - 0.5;
                        float2 s = ToSource(_Crop.xy + (o + sub) * scale);
                        if (s.x < 0 || s.y < 0 || s.x > _Sizes.x || s.y > _Sizes.y) continue;
                        s = clamp(s, 0.5, _Sizes.xy - 0.5);       // clamp-to-edge for the bilinear footprint
                        if (_Yuv.x > 0.5)
                        {
                            // luma texture keeps the row padding, so it can be wider than the image
                            float y = tex2Dlod(_MainTex, float4(s / _Yuv.zw, 0, 0)).r;
                            acc += YuvToRgb(y, SampleCbCr(s));
                        }
                        else
                        {
                            float2 uv = s / _Sizes.xy;
                            if (_Flags.x > 0.5) uv.y = 1.0 - uv.y;
                            float3 c = tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb;
                            acc += _Flags.y > 0.5 ? LinearToSrgb(c) : c;
                        }
                    }
                }
                return float4(acc / (taps * taps), 1);
            }
            ENDCG
        }
    }
}
