// Heat haze over a rocket nozzle: the scene behind the hot exhaust is sampled through a shimmering, noisy offset, the
// way hot gas bends light (refraction by the density gradients of the hot, turbulent air).
//
// It is a screen-space refraction. The command buffer copies the camera target into _HazeSrc, then this pass redraws
// the part of the screen near each exhaust, reading that copy with a displaced UV. The hot region of each exhaust is
// a cone (a capsule whose radius grows along the axis) from the nozzle along the exhaust direction; for every pixel the
// view ray is intersected with it, so only what is BEHIND the gas is distorted - a surface in front of it (the rocket,
// the tower) is left alone, by comparing the scene depth with where the ray meets the cone.
//
// The displacement field is 3D noise evaluated where the ray passes the axis and scrolled DOWN the exhaust, so the
// shimmer streams away from the nozzle instead of boiling in place.
Shader "RaymarchedPlumeTrails/HeatHaze"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "HeatHaze"
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _HazeSrc;
            sampler2D_float _CameraDepthTexture;

            #define MAX_HAZE 8
            int _HazeCount;
            float4 _HazeRect;            // NDC rectangle the quad covers (xy min, zw max)
            float4 _HazeA[MAX_HAZE];     // xyz = nozzle position, w = length of the hot region (m)
            float4 _HazeB[MAX_HAZE];     // xyz = exhaust direction (unit), w = radius at the nozzle (m)
            float4 _HazeC[MAX_HAZE];     // x = strength, y = radius at the far end (m), z/w unused
            float _HazeAmp;              // displacement scale
            float _HazeScale;            // noise cell size (m)
            float _HazeSpeed;            // how fast the pattern streams down the exhaust (m/s)
            float _HazeTimeNow;
            float _HazeMax;              // largest shift (fraction of the screen)
            float4x4 _HazeCamToWorld;    // the camera's own camera-to-world matrix (set per camera by the mod)

            struct appdata { float4 vertex : POSITION; };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 scr : TEXCOORD0;
                float3 ray : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                float2 ndc = lerp(_HazeRect.xy, _HazeRect.zw, v.vertex.xy * 0.5 + 0.5);
                o.pos = float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
                o.scr = ComputeScreenPos(o.pos);
                // View-space ray through this corner, built from the projection the GPU is ACTUALLY using
                // (UNITY_MATRIX_P, which carries the vertical flip when the camera renders into a texture) rather than
                // unity_CameraInvProjection, which never flips. With the latter the reconstructed world position was
                // mirrored top-to-bottom whenever the target was flipped (heat haze and cast shadow landed above the
                // rocket instead of below it).
                // The CPU projection (unity_CameraProjection, a per-camera constant) plus the flip of the CURRENT target
                // (_ProjectionParams.x = -1 when the camera renders into a flipped texture): the same ray the GPU used,
                // without touching UNITY_MATRIX_P / V, which a Blit earlier in the command buffer leaves undefined.
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
                float n000 = hash13(i);
                float n100 = hash13(i + float3(1, 0, 0));
                float n010 = hash13(i + float3(0, 1, 0));
                float n110 = hash13(i + float3(1, 1, 0));
                float n001 = hash13(i + float3(0, 0, 1));
                float n101 = hash13(i + float3(1, 0, 1));
                float n011 = hash13(i + float3(0, 1, 1));
                float n111 = hash13(i + float3(1, 1, 1));
                return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                            lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.scr.xy / i.scr.w;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float depth01 = Linear01Depth(raw);

                // world-space view ray of this pixel
                float3 viewDir = i.ray / max(-i.ray.z, 1e-6);
                float3 wd = mul((float3x3)_HazeCamToWorld, viewDir);
                float3 D = normalize(wd);
                // distance along the ray to the surface (a sky pixel is effectively infinitely far)
                float tScene = depth01 > 0.9999 ? 1e6 : LinearEyeDepth(raw) * length(wd);
                float3 O = _WorldSpaceCameraPos;

                float2 offset = float2(0.0, 0.0);

                [loop]
                for (int k = 0; k < _HazeCount; k++)
                {
                    float3 A = _HazeA[k].xyz;
                    float len = max(_HazeA[k].w, 0.1);
                    float3 u = _HazeB[k].xyz;
                    float r0 = _HazeB[k].w;
                    float r1 = _HazeC[k].y;

                    // closest approach of the view ray to the axis segment A..A+u*len
                    float3 w0 = O - A;
                    float b = dot(D, u);
                    float d = dot(D, w0);
                    float e = dot(u, w0);
                    float denom = 1.0 - b * b;
                    float s = denom > 1e-4 ? (e - b * d) / denom : 0.0;   // distance along the axis
                    s = clamp(s, 0.0, len);
                    float t = b * s - d;                                   // distance along the view ray
                    if (t <= 0.0 || t > tScene) continue;                  // behind the camera, or hidden by a surface in front

                    float3 P = A + u * s;
                    float dist = length(O + D * t - P);
                    float rad = lerp(r0, r1, s / len);
                    if (dist > rad) continue;

                    // fades: across the width, in at the nozzle, out toward the far end
                    float across = 1.0 - smoothstep(0.0, rad, dist);
                    across *= across;
                    float along = smoothstep(0.0, 0.12 * len, s) * (1.0 - smoothstep(0.45 * len, len, s));
                    float mask = across * along * _HazeC[k].x;
                    if (mask <= 0.001) continue;

                    // streaming 3D noise, two octaves, two decorrelated channels
                    float3 q = (O + D * t) / max(_HazeScale, 0.05) - u * (_HazeTimeNow * _HazeSpeed / max(_HazeScale, 0.05));
                    float nx = vnoise(q) * 0.65 + vnoise(q * 2.3 + 7.1) * 0.35;
                    float ny = vnoise(q + 31.7) * 0.65 + vnoise(q * 2.3 + 53.3) * 0.35;
                    float2 n = float2(nx, ny) * 2.0 - 1.0;

                    // angular size of the gas column: wider/nearer gas shifts the picture more
                    float amp = _HazeAmp * rad / max(t, 1.0);
                    // directions: mostly vertical wobble, as rising hot air does
                    offset += n * float2(0.6, 1.0) * mask * amp;
                }

                // however close the gas is (rad / t grows near the camera) the shift stays a shimmer, not a smear
                float shift = length(offset);
                offset *= min(1.0, _HazeMax / max(shift, 1e-6));
                // pixels the haze does not touch are left alone (no rectangle where the copy differs from the frame)
                clip(shift - 1e-6);
                float2 suv = uv + offset;
                return tex2D(_HazeSrc, suv);
            }
            ENDCG
        }
    }
    FallBack Off
}
