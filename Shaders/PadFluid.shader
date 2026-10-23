// Renders the GPU pad-cloud solver's grid (PadFluid.compute): steam, soot and the hot gas of the deflected flame as ONE medium.
//   - the coarse grid gives the shape; the surface is eroded by multi-scale noise (the cauliflower), strongest at the edges
//   - sun: a short march toward the sun gives the self-shadow; two phase lobes (silver-lined rims, forward scatter) plus a soft
//     multiple-scattering term so the shaded undersides stay grey-white instead of black
//   - fire: the hot gas emits (blackbody ramp of its temperature) and the flame at the impact lights the cloud around it
//   - clipped by the scene depth, drawn over the scene (premultiplied)
Shader "RaymarchedPlumeTrails/PadFluid"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+5" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "PadFluid"
            // drawn into the mod's reduced-resolution smoke buffer (HalfResSmokeRenderer), with the distance to the cloud in a second target
            Blend 0 One OneMinusSrcAlpha
            Blend 1 One One
            ZWrite Off
            ZTest Always
            Cull Front

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler3D _RaymarchedPlumeTrailsNoise;
            float _NoiseTilePeriod;
            sampler2D_float _CameraDepthTexture;
            float4 _PlumeJitter;   // temporal upscaling: sub-pixel offset (xy) and dither seed (zw)
            float4 _SmokeSunDir;
            float4 _SmokeUpDir;

            sampler3D _FluidScl;       // steam, soot, temperature
            float4 _FluidAnchor;       // world position of the local origin (x = 0, y = 0, z = 0)
            float4 _FluidE1;           // local x axis in the world
            float4 _FluidUp;           // local y axis (up)
            float4 _FluidE2;           // local z axis
            float4 _FluidDim;          // nx, ny, nz, cell (m)
            float4 _FluidOY;           // local y of the bottom of the grid
            float4 _FluidLight;        // world position of the fire, range (m)
            float4 _FluidLightCol;     // colour * strength of the fire light
            float4 _FluidLook;         // density scale, erosion, steps, exposure
            float4 _FluidLook2;        // fire emission, soot darkening, sun intensity, ambient
            float4 _FluidLook3;        // the trail's own lighting values: ambient floor, steam brightness, sun-lit brightness, night ambient (0 = not set)
            float4 _FluidShock;        // xyz = source of the ignition wave (local), w = radius of the front
            float4 _FluidShock2;       // x = strength of the visible ring (0 = none), y = its thickness (m)
            float4 _FluidAmbSky, _FluidAmbGnd, _FluidShadowC, _FluidSunC;   // the trail's own colours
            float4 _FluidLook4;        // washout, steam whiteness, sky tint strength, shadow strength
            float4 _FluidSeed;         // noise offset (flow drift), time

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float4 scr : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.scr = ComputeScreenPos(o.pos);
                return o;
            }

            float Nz(float3 p) { return tex3Dlod(_RaymarchedPlumeTrailsNoise, float4(p / _NoiseTilePeriod, 0.0)).r; }

            float3 Blackbody(float T)
            {
                float t = max(T, 800.0) / 100.0;
                float r, g, b;
                if (t <= 66.0) { r = 1.0; g = saturate((99.4708025861 * log(t) - 161.1195681661) / 255.0); }
                else { r = saturate(1.292936186 * pow(t - 60.0, -0.1332047592)); g = saturate(1.129890861 * pow(t - 60.0, -0.0755148492)); }
                if (t >= 66.0) b = 1.0;
                else if (t <= 19.0) b = 0.0;
                else b = saturate((138.5177312231 * log(t - 10.0) - 305.0447927307) / 255.0);
                return float3(r, g, b);
            }

            float HG(float c, float g) { float g2 = g * g; return (1.0 - g2) / (4.0 * 3.14159265 * pow(max(1.0 + g2 - 2.0 * g * c, 1e-3), 1.5)); }

            // the grid (local coordinates, metres) -> (steam, soot, temperature)
            // steam, soot, temperature, liquid water
            float4 Grid(float3 pl)
            {
                float3 uvw = float3(pl.x / _FluidDim.w, (pl.y - _FluidOY.x) / _FluidDim.w, pl.z / _FluidDim.w) / _FluidDim.xyz;
                if (any(uvw < 0.0) || any(uvw > 1.0)) return float4(0, 0, 0, 0);
                // the cloud fades out toward the walls of the grid instead of being cut flat there (the bottom is the floor: no fade)
                float3 e = min(uvw, 1.0 - uvw);
                float fade = smoothstep(0.0, 0.07, e.x) * smoothstep(0.0, 0.07, e.z) * smoothstep(0.0, 0.10, 1.0 - uvw.y);
                return tex3Dlod(_FluidScl, float4(uvw, 0.0)) * fade;
            }

            float Raw(float4 s) { return s.x + 1.25 * s.y + 0.6 * s.w; }       // the liquid spray is a thinner mist than the steam

            float Erode(float3 pl, float raw, float ds)
            {
                // three scales (about 14 m, 6 m, 2.5 m): the cauliflower. Eroded most where the cloud is thin (its surface).
                float3 q = pl + _FluidSeed.xyz;
                // the finest octave fades out when the ray step is too coarse to resolve it (no speckle)
                float wf = 0.18 * saturate(3.2 / ds), wm = 0.30 * saturate(7.0 / ds);
                float n = (0.52 * Nz(q * 0.16) + wm * Nz(q * 0.40 + 5.0) + wf * Nz(q * 1.0 + 11.0)) / (0.52 + wm + wf);
                float e = _FluidLook.y * (1.05 - n);
                return max(raw - e * 0.55, 0.0);
            }

            // THE FRONT OF THE WAVE: a thin shell of vapour that condenses in the rarefaction behind the shock (the Prandtl-Glauert ring), racing outward, ragged.
            // It is marched on its own, around the two places where the ray crosses the sphere of the front, NOT inside the fluid box: the wave goes on far past the
            // walls of the grid (the proxy cube is made big enough to hold it).
            void RingMarch(float3 o, float3 d, float tMax, float3 sunL, inout float3 L, inout float T)
            {
                float R = _FluidShock.w;
                float3 oc = o - _FluidShock.xyz;
                float b = dot(oc, d);
                float disc = b * b - (dot(oc, oc) - R * R);
                if (disc <= 0.0) return;
                float sq = sqrt(disc);
                float th = max(_FluidShock2.y, 0.5);
                float3 colS = lerp(float3(0.50, 0.56, 0.68), float3(1.0, 1.0, 1.0), smoothstep(-0.12, 0.18, sunL.y)) * 0.95;
                [unroll] for (int side = 0; side < 2; side++)
                {
                    float tc = side == 0 ? -b - sq : -b + sq;
                    float3 n = (o + d * tc - _FluidShock.xyz) / max(R, 1.0);
                    float cs = max(abs(dot(n, d)), 0.15);
                    float w = min(3.0 * th / cs, 0.5 * R);
                    float ds = 2.0 * w / 14.0;
                    for (int k = 0; k < 14; k++)
                    {
                        float t = tc - w + ds * (k + 0.5);
                        if (t < 0.0 || t > tMax || T < 0.01) continue;
                        float3 p0 = o + d * t;
                        float sd = (length(p0 - _FluidShock.xyz) - R) / th;
                        float rag = saturate(Nz(p0 * 0.11 + 3.0 + _FluidSeed.xyz * 0.25) * 1.9 - 0.55) * saturate(Nz(p0 * 0.31 + 9.0) * 1.6);
                        float ring = exp(-sd * sd) * _FluidShock2.x * rag * 0.55;
                        if (ring > 0.02)
                        {
                            float aS = 1.0 - exp(-ring * 0.07 * ds);
                            L += T * colS * aS;
                            T *= 1.0 - aS;
                        }
                    }
                }
            }

            struct FragOut { fixed4 col : SV_Target0; float4 aux : SV_Target1; };

            FragOut frag (v2f i)
            {
                FragOut fo; fo.aux = float4(0.0, 0.0, 0.0, 0.0);
                float3 O = _WorldSpaceCameraPos;
                float3 wpj = i.wpos + ddx(i.wpos) * _PlumeJitter.x + ddy(i.wpos) * _PlumeJitter.y;
                float3 D = normalize(wpj - O);
                float3 e1 = _FluidE1.xyz, up = _FluidUp.xyz, e2 = _FluidE2.xyz;
                float3 oRel = O - _FluidAnchor.xyz;
                float3 o = float3(dot(oRel, e1), dot(oRel, up), dot(oRel, e2));
                float3 d = float3(dot(D, e1), dot(D, up), dot(D, e2));
                float3 bmin = float3(0.0, _FluidOY.x, 0.0);
                float3 bmax = float3(_FluidDim.x, _FluidDim.y, _FluidDim.z) * _FluidDim.w + float3(0.0, _FluidOY.x, 0.0);

                // ray vs the box
                float3 inv = 1.0 / (d + 1e-9);
                float3 ta = (bmin - o) * inv, tb = (bmax - o) * inv;
                float3 tmn = min(ta, tb), tmx = max(ta, tb);
                float t0 = max(max(tmn.x, tmn.y), max(tmn.z, 0.0));
                float t1 = min(min(tmx.x, tmx.y), tmx.z);

                float2 suv = i.scr.xy / i.scr.w;
                float raw0 = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, suv);
                float eye = LinearEyeDepth(raw0);
                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                float tScene = raw0 > 0.00001 ? eye / max(dot(D, camFwd), 1e-4) : 1e9;
                t1 = min(t1, tScene);
                bool hasBox = t1 > t0;
                float3 sunW0 = dot(_SmokeSunDir.xyz, _SmokeSunDir.xyz) > 0.0001 ? normalize(_SmokeSunDir.xyz) : normalize(float3(0.3, 0.8, 0.4));
                float3 sunL0 = float3(dot(sunW0, e1), dot(sunW0, up), dot(sunW0, e2));
                float3 Lr = float3(0.0, 0.0, 0.0); float Tr = 1.0;
                if (_FluidShock2.x > 0.001) RingMarch(o, d, tScene, sunL0, Lr, Tr);
                if (!hasBox && Tr > 0.999) discard;
                if (!hasBox) t1 = t0;

                float3 sunW = dot(_SmokeSunDir.xyz, _SmokeSunDir.xyz) > 0.0001 ? normalize(_SmokeSunDir.xyz) : normalize(float3(0.3, 0.8, 0.4));
                float3 sunL = float3(dot(sunW, e1), dot(sunW, up), dot(sunW, e2));
                float cosS = dot(D, sunW);
                float phase = lerp(HG(cosS, -0.15), HG(cosS, 0.62), 0.55) * 4.0 * 3.14159265 * 0.22;

                float steps = _FluidLook.z;
                // the step follows the cell: in a vacuum blast the grid expands with the cloud (the cell grows to tens of metres), and with a fixed 0.9..5 m step
                // every pixel of a camera inside the box ran all 320 iterations through the thin gas (a bad lag close to it)
                float ds = clamp((t1 - t0) / steps, max(0.9, 0.55 * _FluidDim.w), max(5.0, 2.2 * _FluidDim.w));
                float tt = t0 + ds * frac(sin(dot(i.pos.xy + _PlumeJitter.zw, float2(12.9898, 78.233))) * 43758.5453);
                float T = 1.0;
                float3 L = float3(0.0, 0.0, 0.0);
                float distAcc = 0.0, distW = 0.0;
                float sigK = 0.075 * _FluidLook.x;
                float3 fireP = float3(dot(_FluidLight.xyz - _FluidAnchor.xyz, e1), dot(_FluidLight.xyz - _FluidAnchor.xyz, up), dot(_FluidLight.xyz - _FluidAnchor.xyz, e2));
                float fr2 = max(_FluidLight.w * _FluidLight.w, 1.0);

                [loop]
                for (int k = 0; k < 320; k++)
                {
                    if (tt >= t1 || T < 0.01) break;
                    float3 p0 = o + d * tt;
                    // empty air (nothing within a cell): skip quickly, without the noise lookups
                    float4 s0 = Grid(p0);
                    if (Raw(s0) < 0.002 && s0.z < 0.03) { tt += ds * 1.5; continue; }
                    // break the grid's straight edges: sample the grid at a point moved by a slow noise vector (about one cell)
                    float3 wv = float3(Nz(p0 * 0.21 + 1.3), Nz(p0 * 0.21 + 7.1), Nz(p0 * 0.21 + 13.7)) - 0.5;
                    float3 p = p0 + wv * (1.9 * _FluidDim.w);
                    float4 s = Grid(p);
                    float rw = Raw(s);
                    float heat = s.z;
                    bool inside = rw > 0.004 || heat > 0.05;
                    float stepL = inside ? ds * 0.5 : ds * 1.5;     // finer inside the cloud, quick through the air
                    if (inside)
                    {
                        float den = min(Erode(p, rw, ds) * 2.4, 1.9);
                        float sigma = den * sigK;
                        float3 emit = float3(0.0, 0.0, 0.0);
                        float sigE = 0.0;
                        // the hot gas: a flame that emits (black body of its temperature) and absorbs a little
                        if (heat > 0.08)
                        {
                            // the flame is ragged: broken up by noise, so it licks along the floor instead of being a smooth ball
                            float hn = Nz(p * 0.33 + _FluidSeed.xyz * 1.6) * 0.65 + Nz(p * 0.9 + 3.0 + _FluidSeed.xyz * 2.2) * 0.35;
                            float h = saturate((heat * (0.35 + 1.25 * hn) - 0.12) * 1.5);
                            float Tk = 700.0 + 2400.0 * h * h;       // red at the rim, orange, yellow, white only in the very core
                            float I = pow(Tk / 1800.0, 4.0) * 0.55 * _FluidLook2.x;
                            sigE = 0.05 * h * h;
                            emit = Blackbody(Tk) * I * sigE;
                        }
                        float3 lit = float3(0.0, 0.0, 0.0);
                        if (sigma > 1e-4)
                        {
                            // sun self-shadow: optical depth toward the sun from the coarse grid
                            float tau = 0.0;
                            tau += Raw(Grid(p + sunL * 3.0)) * 3.0;
                            tau += Raw(Grid(p + sunL * 8.0)) * 6.0;
                            tau += Raw(Grid(p + sunL * 17.0)) * 11.0;
                            tau += Raw(Grid(p + sunL * 33.0)) * 21.0;
                            tau += Raw(Grid(p + sunL * 60.0)) * 33.0;
                            float sh = exp(-tau * sigK * 1.25);
                            float sh2 = exp(-tau * sigK * 0.22);             // multiple scattering: light that wanders around
                            float soot = s.y / max(s.x + s.y, 1e-3);
                            float alb = lerp(0.96, 0.96 - _FluidLook2.y, soot);
                            float skyF = saturate(0.5 + 0.5 * dot(float3(0, 1, 0), 0.0) + 0.5 * dot(up, _SmokeUpDir.xyz));
                            // The same lighting inputs as the smoke trail (SmokeTuning: ambient floor, sun-lit brightness, night ambient), so the pad
                            // cloud and the trail read as one medium: sun only by day (and fading through the terminator), a floor of sky light
                            // that never goes black, the hemisphere tint of the sky above and the ground below.
                            float day = smoothstep(-0.12, 0.18, sunL.y);
                            float3 ambient = lerp(float3(0.46, 0.47, 0.44), float3(0.55, 0.68, 0.92), saturate(0.55 + 0.45 * dot(-D, up)));
                            float have = _FluidLook3.x > 0.0 ? 1.0 : 0.0;
                            float floorK = lerp(_FluidLook2.w, max(_FluidLook2.w, _FluidLook3.x * 1.5), have);
                            float nightK = lerp(1.0, lerp(_FluidLook3.w, 1.0, day), have);
                            float sunK = lerp(1.0, _FluidLook3.z * 1.25, have);
                            float3 sunCol = float3(1.0, 0.995, 0.98) * _FluidLook2.z * sunK * lerp(1.0, day, have);
                            float3 litOld = alb * (sunCol * (sh * phase + 0.30 * sh2) + ambient * floorK * nightK * lerp(0.55, 1.0, sh2));
                            // THE TRAIL'S OWN COMPOSITION (SmokeVolume.shader): hemisphere ambient, shadow colour, litness from the floor and the
                            // self-shadow, steam brighter and whiter than soot, washout toward the ambient. Same colours, same formula.
                            float skyFacing = saturate(0.5 + 0.5 * dot(-D, up));
                            float3 ambT = lerp(_FluidAmbGnd.rgb, _FluidAmbSky.rgb, skyFacing) * lerp(_FluidLook3.w, 1.0, day);
                            float3 shadedT = lerp(_FluidShadowC.rgb, ambT, _FluidLook4.z);
                            float flatLit = lerp(_FluidLook3.x * 0.45, 1.0, saturate(sh * phase * 0.25));
                            float shadedLit = lerp(_FluidLook3.x, 1.0, sh);
                            float litness = lerp(flatLit, shadedLit, _FluidLook4.w);
                            float steamK = saturate(1.0 - soot);
                            litness = lerp(litness, max(litness, _FluidLook3.y), steamK);
                            shadedT = lerp(shadedT, float3(1, 1, 1), steamK * _FluidLook4.y);
                            float3 litC = lerp(_FluidSunC.rgb * lerp(0.35, 1.0, day), float3(1, 1, 1), steamK * _FluidLook4.y * 0.6);
                            float3 colT = lerp(shadedT, litC, litness);
                            colT *= lerp(1.0, 1.22, saturate(sh * phase * 0.1));
                            colT *= lerp(1.0, 0.62, soot);
                            colT = lerp(colT, max(colT, ambT), _FluidLook4.x);
                            lit = lerp(litOld, colT, have);
                            // the flame lights the cloud around it
                            float3 dl = p - fireP;
                            float f = 1.0 / (1.0 + dot(dl, dl) / fr2);
                            lit += alb * lerp(_FluidLightCol.rgb, dot(_FluidLightCol.rgb, float3(0.33, 0.33, 0.33)).xxx, 0.45) * (f * f * 1.3);   // the flame's light, a little less orange
                        }
                        float sTot = sigma + sigE;
                        float3 jj = lit * sigma + emit;
                        float a = 1.0 - exp(-sTot * stepL);
                        float3 dL = T * (a / max(sTot, 1e-5)) * jj;
                        float wgt = T * a + dot(dL, float3(0.33, 0.33, 0.33)) * _FluidLook.w;
                        distAcc += tt * wgt; distW += wgt;
                        L += dL;
                        T *= 1.0 - a;
                    }
                    tt += stepL;
                }
                L = Lr + Tr * L; T *= Tr;
                float3 Lc = L * _FluidLook.w;
                Lc = Lc / (1.0 + 0.12 * max(Lc - 1.0, 0.0));      // below 1 untouched: the cloud keeps the trail's colours
                fo.aux = float4(distAcc, distW, 0.0, 0.0);
                fo.col = fixed4(Lc, 1.0 - T);
                return fo;
            }
            ENDCG
        }

        // THE SHADOW THE CLOUD CASTS ON THE SCENE (the same job as the cast-shadow pass of SmokeVolume.shader, drawn by HalfResSmokeRenderer.BuildShadowCast as pass 1
        // of this material, with the same property block): for every pixel of the scene inside the rectangle where a shadow can fall the position is rebuilt
        // from the depth, and a short march toward the sun through the grid adds up how much smoke the sun ray crosses. The result multiplies the picture.
        Pass
        {
            Name "FluidShadowCast"
            Blend DstColor Zero
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertS
            #pragma fragment fragS
            #include "UnityCG.cginc"

            sampler2D_float _CameraDepthTexture;
            float4 _SmokeSunDir;
            float4 _SmokeUpDir;
            sampler3D _FluidScl;
            float4 _FluidAnchor, _FluidE1, _FluidUp, _FluidE2, _FluidDim, _FluidOY, _FluidLook;
            float4 _CastRect;
            float _ShadowCastStrength;
            float _ShadowCastFloor;

            struct appdata { float4 vertex : POSITION; };
            struct v2fS { float4 pos : SV_POSITION; float4 scr : TEXCOORD0; float3 ray : TEXCOORD1; };

            v2fS vertS (appdata v)
            {
                v2fS o;
                float2 ndc = lerp(_CastRect.xy, _CastRect.zw, v.vertex.xy * 0.5 + 0.5);
                o.pos = float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
                o.scr = ComputeScreenPos(o.pos);
                o.ray = float3((ndc.x + UNITY_MATRIX_P._m02) / UNITY_MATRIX_P._m00,
                               (ndc.y + UNITY_MATRIX_P._m12) / UNITY_MATRIX_P._m11, -1.0);
                return o;
            }

            float4 GridS(float3 pl)
            {
                float3 uvw = float3(pl.x / _FluidDim.w, (pl.y - _FluidOY.x) / _FluidDim.w, pl.z / _FluidDim.w) / _FluidDim.xyz;
                if (any(uvw < 0.0) || any(uvw > 1.0)) return float4(0, 0, 0, 0);
                float3 e = min(uvw, 1.0 - uvw);
                float fade = smoothstep(0.0, 0.07, e.x) * smoothstep(0.0, 0.07, e.z) * smoothstep(0.0, 0.10, 1.0 - uvw.y);
                return tex3Dlod(_FluidScl, float4(uvw, 0.0)) * fade;
            }

            float IGN(float2 p) { return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715)))); }

            fixed4 fragS (v2fS i) : SV_Target
            {
                float2 uv = i.scr.xy / i.scr.w;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                if (Linear01Depth(raw) > 0.999) return fixed4(1, 1, 1, 1);
                float eye = LinearEyeDepth(raw);
                float3 viewDir = i.ray / max(-i.ray.z, 1e-6);
                float3 worldDir = mul((float3x3)UNITY_MATRIX_I_V, viewDir);
                float3 worldPos = _WorldSpaceCameraPos + worldDir * eye;

                float3 sunW = dot(_SmokeSunDir.xyz, _SmokeSunDir.xyz) > 0.0001 ? normalize(_SmokeSunDir.xyz) : normalize(float3(0.3, 0.8, 0.4));
                if (dot(sunW, _SmokeUpDir.xyz) < -0.05) return fixed4(1, 1, 1, 1);          // the sun is down

                float3 e1 = _FluidE1.xyz, up = _FluidUp.xyz, e2 = _FluidE2.xyz;
                float3 oRel = worldPos + sunW * 0.3 - _FluidAnchor.xyz;
                float3 o = float3(dot(oRel, e1), dot(oRel, up), dot(oRel, e2));
                float3 d = float3(dot(sunW, e1), dot(sunW, up), dot(sunW, e2));
                float3 bmin = float3(0.0, _FluidOY.x, 0.0);
                float3 bmax = float3(_FluidDim.x, _FluidDim.y, _FluidDim.z) * _FluidDim.w + float3(0.0, _FluidOY.x, 0.0);
                float3 inv = 1.0 / (d + 1e-9);
                float3 ta = (bmin - o) * inv, tb = (bmax - o) * inv;
                float3 tmn = min(ta, tb), tmx = max(ta, tb);
                float t0 = max(max(tmn.x, tmn.y), max(tmn.z, 0.0));
                float t1 = min(min(tmx.x, tmx.y), tmx.z);
                if (t1 <= t0) return fixed4(1, 1, 1, 1);

                const int N = 28;
                float dt = (t1 - t0) / N;
                float t = t0 + dt * IGN(i.pos.xy);
                float sigK = 0.075 * _FluidLook.x;
                float tau = 0.0;
                [loop]
                for (int k = 0; k < N; k++)
                {
                    float4 s = GridS(o + d * t);
                    float rw = s.x + 2.4 * s.y + 0.6 * s.w;           // the soot takes more of the light than the steam
                    tau += min(rw * 1.7, 1.9) * sigK * dt;
                    t += dt;
                }
                float transmittance = exp(-tau);
                float shade = lerp(1.0, max(transmittance, _ShadowCastFloor), _ShadowCastStrength);
                return fixed4(shade, shade, shade, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
