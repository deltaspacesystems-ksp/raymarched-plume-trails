// The shock front of an explosion as a lens. Across the front the air is denser (the density jump of the Rankine-Hugoniot relations), and light that
// grazes the front is bent by the density gradient: the picture behind the shell is displaced - outward inside the front, inward outside it, so a ring
// forms where the background piles up (what a shadowgraph or a schlieren photograph of a blast shows, and what makes the shock wave of a big
// explosion visible as a ring that races over the landscape).
//
// A screen-space refraction, like the heat haze: the command buffer copies the camera target, then this pass redraws the screen through a displaced
// lookup into that copy. For every pixel the view ray's closest approach to the centre of the blast (its impact parameter b) is compared with the
// radius R of the front: the deflection is an odd function of (R - b) over the thickness of the compressed layer, x exp(-x^2 / 2), directed along the
// ray's closest-approach vector. Only what is BEHIND the front is displaced (the scene depth is compared with the point of closest approach).
Shader "RaymarchedPlumeTrails/ShockLens"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "ShockLens"
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _ShockSrc;
            sampler2D_float _CameraDepthTexture;

            #define MAX_SHOCKS 6
            int _ShockCount;
            float4 _ShockA[MAX_SHOCKS];     // xyz = centre of the blast (world), w = radius of the front (m)
            float4 _ShockB[MAX_SHOCKS];     // x = deflection of a grazing ray (radians), y = thickness of the compressed layer (m)
            float4x4 _ShockCamToWorld;      // the camera's own camera-to-world matrix
            float4x4 _ShockWorldToCam;
            float _ShockMax;                // the largest shift (fraction of the screen)

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float4 scr : TEXCOORD0; float3 ray : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                float2 ndc = v.vertex.xy;
                o.pos = float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
                o.scr = ComputeScreenPos(o.pos);
                float flip = _ProjectionParams.x;
                o.ray = float3((ndc.x + unity_CameraProjection._m02) / unity_CameraProjection._m00,
                               (flip * ndc.y + unity_CameraProjection._m12) / unity_CameraProjection._m11, -1.0);
                return o;
            }

            float hash13(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float vnoise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(hash13(i), hash13(i + float3(1, 0, 0)), f.x), lerp(hash13(i + float3(0, 1, 0)), hash13(i + float3(1, 1, 0)), f.x), f.y),
                            lerp(lerp(hash13(i + float3(0, 0, 1)), hash13(i + float3(1, 0, 1)), f.x), lerp(hash13(i + float3(0, 1, 1)), hash13(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.scr.xy / i.scr.w;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float depth01 = Linear01Depth(raw);
                float3 viewDir = i.ray / max(-i.ray.z, 1e-6);
                float3 wd = mul((float3x3)_ShockCamToWorld, viewDir);
                float3 D = normalize(wd);
                float tScene = depth01 > 0.9999 ? 1e6 : LinearEyeDepth(raw) * length(wd);
                float3 O = _WorldSpaceCameraPos;

                float2 offset = float2(0.0, 0.0);
                [loop]
                for (int k = 0; k < _ShockCount; k++)
                {
                    float3 C = _ShockA[k].xyz;
                    float R = _ShockA[k].w;
                    float a0 = _ShockB[k].x;
                    float w = max(_ShockB[k].y, 0.5);
                    float3 oc = C - O;
                    float tc = dot(oc, D);
                    if (tc <= 0.0 || tc > tScene) continue;
                    float3 perp = oc - D * tc;
                    float b = length(perp);
                    float x = (R - b) / w;
                    if (abs(x) > 3.5) continue;
                    // odd profile, 1 at |x| = 1: the background is pushed out inside the front and pulled in outside it
                    float prof = x * exp(-0.5 * x * x) * 1.6487;
                    // the front is ragged: turbulence of the air it crosses, along the sphere
                    float3 P = O + D * tc;
                    float3 n = normalize(P - C);
                    float rag = 0.7 + 0.6 * vnoise(n * 11.0 + k * 3.7 + 1.3);
                    float alpha = a0 * prof * rag;
                    float3 dir = b > 1e-3 ? perp / b : float3(0.0, 0.0, 0.0);
                    float3 D2 = normalize(D - dir * alpha);
                    float3 v1 = mul((float3x3)_ShockWorldToCam, D);
                    float3 v2 = mul((float3x3)_ShockWorldToCam, D2);
                    float2 p1 = float2(v1.x, v1.y) / max(-v1.z, 1e-4);
                    float2 p2 = float2(v2.x, v2.y) / max(-v2.z, 1e-4);
                    offset += 0.5 * float2(unity_CameraProjection._m00, unity_CameraProjection._m11) * (p2 - p1);
                }
                float shift = length(offset);
                offset *= min(1.0, _ShockMax / max(shift, 1e-6));
                clip(shift - 1e-6);
                return tex2D(_ShockSrc, uv + offset);
            }
            ENDCG
        }
    }
    FallBack Off
}
