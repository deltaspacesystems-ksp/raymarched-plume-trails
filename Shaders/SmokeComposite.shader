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
                return lerp(smooth, nearest, edge);
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
                float4 nmin = cur, nmax = cur;
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        float4 c = tex2D(_MainTex, base + float2(x, y) * texel);
                        nmin = min(nmin, c);
                        nmax = max(nmax, c);
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
                }
                if (any(uvPrev < 0.0) || any(uvPrev > 1.0)) return cur;

                float4 histRaw = tex2D(_PlumeHistory, uvPrev);
                float4 hist = clamp(histRaw, nmin, nmax);

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
