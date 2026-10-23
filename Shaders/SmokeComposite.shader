// Composites the half-resolution smoke buffer back onto the screen.
//
// A plain bilinear stretch would bleed smoke across geometry edges: a half-res texel
// straddling a silhouette holds a blend of "in front of the rocket" and "behind it", and
// smearing that over full-res pixels produces halos. Nearest-depth upsampling picks, per
// destination pixel, the source texel whose scene depth is closest to this pixel's own -
// so an edge pixel takes its colour from a source texel on the same side of the edge.
Shader "RaymarchedPlumeTrails/SmokeComposite"
{
    Properties
    {
        _MainTex ("Half-res smoke", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        // the source is already premultiplied (see SmokeVolume.shader), so the classic
        // over-operator is One / OneMinusSrcAlpha rather than SrcAlpha / OneMinusSrcAlpha
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize; // .xy = 1/width, 1/height of the HALF-res buffer
            sampler2D_float _CameraDepthTexture;
            // global, set from HalfResSmokeRenderer: overall smoke opacity slider
            float _PlumeOpacity;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 texel = _MainTex_TexelSize.xy;

                // depth this full-res pixel actually belongs to
                float targetDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));

                // the four half-res texels around it, and the scene depth each of them
                // was rendered against
                float2 uvs[4];
                uvs[0] = i.uv + float2(-0.5, -0.5) * texel;
                uvs[1] = i.uv + float2( 0.5, -0.5) * texel;
                uvs[2] = i.uv + float2(-0.5,  0.5) * texel;
                uvs[3] = i.uv + float2( 0.5,  0.5) * texel;

                float bestDiff = 1e20;
                int best = 0;
                for (int s = 0; s < 4; s++)
                {
                    float d = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uvs[s]));
                    float diff = abs(d - targetDepth);
                    if (diff < bestDiff) { bestDiff = diff; best = s; }
                }

                // Where all four agree on depth there is no edge to protect, so take the
                // smooth bilinear sample; only fall back to the single nearest texel where
                // they disagree, which is exactly where point sampling avoids a halo.
                fixed4 smooth = tex2D(_MainTex, i.uv);
                fixed4 nearest = tex2D(_MainTex, uvs[best]);
                float edge = saturate(bestDiff * 0.5);
                // premultiplied source, so scaling every channel is the correct fade
                return lerp(smooth, nearest, edge) * _PlumeOpacity;
            }
            ENDCG
        }

        // ------------------------------------------------------------------
        // Pass 1: temporal resolve.
        //
        // Takes this frame's low-resolution smoke and blends it with the previous frame's
        // FULL-resolution result, reprojected to where each pixel was a frame ago. Because
        // the jitter moves the sample point every frame, the history accumulates detail
        // that no single low-resolution frame contains - that is the upscale.
        //
        // The reprojection uses the smoke's own distance (written by the volume pass into
        // a second target) rather than the scene depth: the smoke is not a surface, and
        // reprojecting it as if it sat at the terrain behind it, or at infinity, smears it
        // as soon as the camera moves.
        // ------------------------------------------------------------------
        Pass
        {
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragResolve
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;   // the LOW-res current frame
            sampler2D_float _CameraDepthTexture;
            sampler2D_float _PlumeAux;   // x = sum(distance * weight), y = sum(weight)
            sampler2D _PlumeHistory;     // last frame's resolved result, full resolution
            float4 _PlumeHistory_TexelSize;
            // previous view space <- current view space, with the body's own motion folded
            // in (rotation, floating origin, Krakensbane), so it is exact for smoke that is
            // at rest in the air
            float4x4 _PlumeReprojView;
            float _PlumeHistoryValid;
            float _PlumeFeedback;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            // The same depth-aware upsample the plain composite uses, so silhouettes of the
            // rocket do not grow a halo when the current frame is stretched to full size.
            float4 CurrentSample(float2 uv)
            {
                float2 texel = _MainTex_TexelSize.xy;
                float targetDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));

                float2 uvs[4];
                uvs[0] = uv + float2(-0.5, -0.5) * texel;
                uvs[1] = uv + float2( 0.5, -0.5) * texel;
                uvs[2] = uv + float2(-0.5,  0.5) * texel;
                uvs[3] = uv + float2( 0.5,  0.5) * texel;

                float bestDiff = 1e20;
                int best = 0;
                for (int s = 0; s < 4; s++)
                {
                    float d = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uvs[s]));
                    float diff = abs(d - targetDepth);
                    if (diff < bestDiff) { bestDiff = diff; best = s; }
                }
                float4 smooth = tex2D(_MainTex, uv);
                float4 nearest = tex2D(_MainTex, uvs[best]);
                return lerp(smooth, nearest, saturate(bestDiff * 0.5));
            }

            // Catmull-Rom (sharpness 0.5) in 5 taps: the history is resampled without the blur of a bilinear fetch (that blur, repeated every frame, softens the smoke)
            float4 SampleHistoryCR(float2 uv)
            {
                float2 dims = _PlumeHistory_TexelSize.zw, inv = _PlumeHistory_TexelSize.xy;
                float2 pos = dims * uv;
                float2 c = floor(pos - 0.5) + 0.5;
                float2 f = pos - c, f2 = f * f, f3 = f * f2;
                const float S = 0.5;
                float2 w0 = -S * f3 + 2.0 * S * f2 - S * f;
                float2 w1 = (2.0 - S) * f3 + (S - 3.0) * f2 + 1.0;
                float2 w2 = (S - 2.0) * f3 + (3.0 - 2.0 * S) * f2 + S * f;
                float2 w3 = S * f3 - S * f2;
                float2 w12 = w1 + w2;
                float2 tc12 = inv * (c + w2 / w12), tc0 = inv * (c - 1.0), tc3 = inv * (c + 2.0);
                float4 s0 = tex2D(_PlumeHistory, float2(tc12.x, tc0.y));
                float4 s1 = tex2D(_PlumeHistory, float2(tc0.x, tc12.y));
                float4 s2 = tex2D(_PlumeHistory, float2(tc12.x, tc12.y));
                float4 s3 = tex2D(_PlumeHistory, float2(tc3.x, tc12.y));
                float4 s4 = tex2D(_PlumeHistory, float2(tc12.x, tc3.y));
                float k0 = w12.x * w0.y, k1 = w0.x * w12.y, k2 = w12.x * w12.y, k3 = w3.x * w12.y, k4 = w12.x * w3.y;
                return (s0 * k0 + s1 * k1 + s2 * k2 + s3 * k3 + s4 * k4) / (k0 + k1 + k2 + k3 + k4);
            }

            float4 fragResolve(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float4 cur = CurrentSample(uv);
                if (_PlumeHistoryValid < 0.5) return cur;

                // 3x3 neighbourhood of the CURRENT frame, read at texel centres. History
                // is clamped into this range: anything the current frame does not support
                // (smoke that has moved on, an edge that has been uncovered) is thrown away
                // instead of smeared behind as a ghost.
                float2 texel = _MainTex_TexelSize.xy;
                float2 base = (floor(uv / texel) + 0.5) * texel;
                float4 nmin = cur, nmax = cur, m1 = float4(0, 0, 0, 0), m2 = float4(0, 0, 0, 0);
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        float4 c = tex2D(_MainTex, base + float2(x, y) * texel);
                        nmin = min(nmin, c);
                        nmax = max(nmax, c);
                        m1 += c; m2 += c * c;
                    }
                }

                // Reproject. Pixels with no smoke have nothing to place, so they look up
                // their own position; the clamp then removes whatever history disagrees.
                float2 uvPrev = uv;
                float4 aux = tex2D(_PlumeAux, uv);
                if (aux.y > 1e-4)
                {
                    float dist = aux.x / aux.y;
                    float2 p11_22 = float2(unity_CameraProjection._11, unity_CameraProjection._22);
                    float2 p13_23 = float2(unity_CameraProjection._13, unity_CameraProjection._23);
                    float3 viewRay = float3((uv * 2.0 - 1.0 - p13_23) / p11_22, -1.0);
                    float3 vpos = normalize(viewRay) * dist;
                    float3 vprev = mul(_PlumeReprojView, float4(vpos, 1.0)).xyz;
                    if (-vprev.z > 0.05)
                    {
                        float2 ndc = (vprev.xy / -vprev.z) * p11_22 + p13_23;
                        uvPrev = ndc * 0.5 + 0.5;
                    }
                    // the smoke itself moved (it grew): where this point of it was a frame ago, from the volume pass
                    uvPrev += aux.zw / aux.y;
                }
                if (any(uvPrev < 0.0) || any(uvPrev > 1.0)) return cur;

                float4 histRaw = SampleHistoryCR(uvPrev);
                // the history is kept inside mean +- 1.25 sigma of the 3x3 of the current frame (a statistical clamp: min/max alone lets a ghost through at the edge of
                // the range), then inside min / max so that no colour appears that the neighbourhood does not have
                float4 mean = m1 / 9.0;
                float4 sigma = sqrt(max(m2 / 9.0 - mean * mean, 0.0));
                float4 hist = clamp(histRaw, mean - 1.25 * sigma, mean + 1.25 * sigma);
                hist = clamp(hist, nmin, nmax);

                // Where the clamp had to move the history a long way it was describing
                // something else, so trust it less rather than only clipping it.
                float miss = saturate(abs(histRaw.a - hist.a) * 4.0);
                float w = _PlumeFeedback * (1.0 - miss);
                return lerp(cur, hist, w);
            }
            ENDCG
        }
    }
    FallBack Off
}
