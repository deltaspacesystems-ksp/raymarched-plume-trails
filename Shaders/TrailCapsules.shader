// THE TRAIL AS A LIST OF CAPSULES (the new core of the smoke trail, drawn into the mod's reduced-resolution smoke buffer like PadFluid).
//
// Data: segments (capsules, two end points with their radii, the fade of their life and the density), in the frame of the trail (fixed to the body, so the
// floating origin and the rotation of the planet do not touch it). Three levels of bounds (blocks of 8 chunks of 8 segments) are tested by the ray, so a sample
// reads only the capsules that are near it.
//
// Look (own implementation of well known ideas: the horizon zero dawn erosion, Wrenninge's multiple scattering, the dark edge of the Nubis clouds):
//   - the profile of a capsule p = 1 - d / r is the LOW detail shape; a noise sample erodes it: density = clamp((n * depth + 1 - depth - (1 - p)) / (1 - sharp))
//     so the core is solid, the rim is ragged and the structure is never lost
//   - the scale of the noise is tied to the radius (the lobes of a wide plume are wide) and blended between two neighbouring octaves (no popping)
//   - the density falls as 1 / radius (the column of smoke is the same at any size, a plume thins out by its fade, not by being a different kind of smoke)
//   - light: beer toward the sun, a forward lobe for the direct light, a multiple scattering term with two wide lobes that is darker at the rim (dark edge),
//     the sky ambient occluded by the depth of the smoke itself
Shader "RaymarchedPlumeTrails/TrailCapsules"
{
    SubShader
    {
        // ======================================================================================================================================================
        // THE CLOUD SHADOW FROM EVE (shared by the passes): EVE's volumetric clouds keep a LIGHT VOLUME around the camera and set it as GLOBAL shader values
        // every frame (EVE release-5, LightVolume.cs): directLightVolume (direct part: z in [0, 0.5), the ambient part above), worldToParaboloid (world -> the
        // frame of the volume, z up), innerLightVolumeRadius / outerLightVolumeRadius (the cloud shell, from the planet centre), lightVolumeZClamp.
        // Read from a dump of the real texture (Kerbin, 224x224x32 direct slices): the texel holds the AMOUNT OF CLOUD BETWEEN THE POINT AND THE SUN (an optical
        // depth: ~0.006 in clear air, 0.1-0.4 under the clouds, 0 above them; the pattern of a lower slice is the one above moved away from the sun by the sun's
        // angle) - so sampling at the smoke point itself gives its cloud shadow, sun angle included. Mapping (checked: the slices line up in world space):
        // uv = d.xy / (1 + d.z) for the direction d from the volume's origin (it sits at the cloud base, the disc reaches to the horizon), slice = altitude
        // fraction in the shell, w = fraction * 0.5 clamped to lightVolumeZClamp. _TrEveMap still flips the axes (developer check; 0 = the found mapping).
        // ======================================================================================================================================================
        CGINCLUDE
        sampler3D directLightVolume;
        float4x4 worldToParaboloid;
        float innerLightVolumeRadius;
        float outerLightVolumeRadius;
        float4 lightVolumeZClamp;
        float4 _TrEve;              // x = strength (0 = off), yzw = the planet centre (world)
        float4 _TrEveMap;           // x = swap x/y, y = flip x, z = flip y, w = flip z (each 0 or 1)

        // the share of the sun light that gets through EVE's clouds to the world point wp (1 = no cloud / outside the volume)
        float EveCloudLight(float3 wp, float3 sunW)
        {
            if (_TrEve.x <= 0.0 || outerLightVolumeRadius <= innerLightVolumeRadius) return 1.0;
            float3 c = _TrEve.yzw;
            float3 rel = wp - c;
            float r = length(rel);
            if (r > outerLightVolumeRadius) return 1.0;                   // above the clouds
            float3 p = wp;
            if (r < innerLightVolumeRadius)
            {
                // below the shell: up to its base along the sun (the point is inside the inner sphere: the far root); the clouds above are all ahead of it
                float b = dot(rel, sunW);
                float q = dot(rel, rel) - innerLightVolumeRadius * innerLightVolumeRadius;
                float t = -b + sqrt(max(b * b - q, 0.0));
                p = wp + sunW * t;
            }
            float3 l = mul(worldToParaboloid, float4(p, 1.0)).xyz;
            if (_TrEveMap.x > 0.5) l.xy = l.yx;
            if (_TrEveMap.y > 0.5) l.x = -l.x;
            if (_TrEveMap.z > 0.5) l.y = -l.y;
            if (_TrEveMap.w > 0.5) l.z = -l.z;
            float3 d = normalize(l);
            // low and far points sit near the rim of the disc - that is where the smoke and the ground are, so the rim is used; only below the horizontal
            // plane of the origin is nothing stored (fade there)
            float cover = saturate(d.z * 200.0);
            if (cover <= 0.0) return 1.0;
            float2 uv = d.xy / (1.0 + d.z) * 0.5 + 0.5;
            float h = saturate((length(p - c) - innerLightVolumeRadius) / (outerLightVolumeRadius - innerLightVolumeRadius));
            float w = clamp(h * 0.5, lightVolumeZClamp.x, lightVolumeZClamp.y);
            float od = max(tex3Dlod(directLightVolume, float4(uv, w, 0.0)).r, 0.0);
            // the optical depth to the sun -> transmittance; 14 makes the core of a cumulus shadow (od ~0.15) let through ~1/8 of the sun
            float tr = exp(-od * 14.0 * _TrEve.x);
            return lerp(1.0, tr, cover);
        }
        ENDCG

        Tags { "Queue" = "Transparent+4" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "TrailCapsules"
            Blend 0 One OneMinusSrcAlpha
            Blend 1 One One
            ZWrite Off
            ZTest Always
            Cull Front

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler3D _RaymarchedPlumeTrailsNoise;
            sampler3D _TrWorleyNoise;              // inverted worley fbm, baked by AssetLoader (the noise of the trail)
            sampler2D_float _CameraDepthTexture;
            float4 _PlumeJitter;
            float4 _SmokeSunDir;
            float4 _SmokeUpDir;

            StructuredBuffer<float4> _TrSeg;       // 4 per segment: (a.xyz, ra), (b.xyz, rb), (fadeA, fadeB, density, growth), (heatA, heatB, density multiplier at A, at B)
            StructuredBuffer<float4> _TrChunk;     // bounding spheres of 8 segments
            StructuredBuffer<float4> _TrBlock;     // bounding spheres of 8 chunks
            int _TrSegCount, _TrChunkCount, _TrBlockCount;
            float4 _TrAnchorW;                     // world position of the origin of the trail frame
            float4x4 _TrW2L;                       // rotation: world -> trail frame
            float4 _TrLook;                        // x = density constant K (column of smoke), y = erosion depth, z = edge sharpness, w = noise base frequency at the reference radius
            float4 _TrLook2;                       // x = sun intensity, y = ambient, z = dark edge minimum, w = exposure
            float4 _TrAmbSky, _TrAmbGnd, _TrSunC, _TrShadowC;
            float4 _TrLook3;                       // x = inflation of the capsules (the bulges of the billows reach out of the capsule), y = amplitude of the billows, z = frequency of the billows relative to the erosion noise
            float4 _TrSunRaw;                      // the colour of the sun at the smoke, WITH its tint (the light that shines THROUGH the smoke keeps it: warm at a low sun)
            float4 _TrLook5;                       // x = gain of the light that shines through the thin parts of the smoke (silver lining), unclamped
            float4 _TrAerial;                      // x = 1 / distance of the haze (1/m), y = strength
            float4 _TrHaze;                        // rgb = colour of the haze (the sky at the horizon)
            float4 _TrFlame;                       // xyz = the engine (trail frame), w = range of its light (m)
            float4 _TrFlameCol;                    // rgb = colour * strength of the light of the engine
            float4 _TrKsa;                         // x = 1: physical lighting (sun scattering + sky light, added, not a palette blend), y = sun brightness, z = sky brightness, w = dark edge
            float4 _TrGlowCol;                     // rgb = the colour * strength of the glow of the hot exhaust (the heat of the segments: next to the nozzle)
            float4 _TrLook6;                       // x = the inner fill: from this profile inward the smoke is solid, not eroded (0 = off)
            float4 _TrLook4;                       // x = how much of the extinction the light feels (the light goes deeper than the view ray)
            float _TrDebug;                        // 1 = no bounding tests (to tell a culling bug from a shape bug)
            float4 _TrTint;                        // rgb = colour of the smoke, a = unused

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

            // the noise lookup: the coordinate is turned by an irrational rotation first, so the repetition of the tiled texture does not line up with the axis of the trail (no diagonal hatching)
            float Nz1(float3 p)
            {
                float3 q = float3(0.80 * p.x + 0.36 * p.y + 0.48 * p.z, -0.48 * p.x + 0.86 * p.y + 0.17 * p.z, -0.36 * p.x - 0.36 * p.y + 0.86 * p.z);
                // _TrLook5.w = 0: the old fbm (stringy: its contours read as terraces and wood grain); 1 = the inverted worley fbm (round, billowy lobes,
                // like KSA's trail and cloud renderers); above 1 the excess is added to it (fuller lobes, less eroded)
                if (_TrLook5.w <= 0.0) return tex3Dlod(_RaymarchedPlumeTrailsNoise, float4(q, 0.0)).r;
                float w = tex3Dlod(_TrWorleyNoise, float4(q, 0.0)).r;
                return lerp(tex3Dlod(_RaymarchedPlumeTrailsNoise, float4(q, 0.0)).r, w + max(_TrLook5.w - 1.0, 0.0), saturate(_TrLook5.w));
            }

            float HG(float c, float g) { float g2 = g * g; return (1.0 - g2) / (4.0 * 3.14159265 * pow(max(1.0 + g2 - 2.0 * g * c, 1e-3), 1.5)); }
            float BeerMS(float d) { return 1.0 / (1.0 + 0.35 * d + 0.065 * d * d); }

            // the ray against a sphere: the interval of the distance along the ray
            bool RaySphere(float3 o, float3 d, float4 s, out float t0, out float t1)
            {
                float3 oc = o - s.xyz;
                float b = dot(oc, d);
                float c = dot(oc, oc) - s.w * s.w;
                float disc = b * b - c;
                t0 = 0.0; t1 = 0.0;
                if (disc < 0.0) return false;
                float sq = sqrt(disc);
                t0 = -b - sq; t1 = -b + sq;
                return t1 > 0.0;
            }

            // the smoke at a point: the low-detail profile and the properties of the capsules that cover it
            // returns the profile (0 = none); radius = weighted radius, dmul = density multiplier
            // where the material at a point was ONE FRAME AGO relative to now (the radius of the capsules grows: the point moved outward with it), for the motion vectors of the
            // temporal upscaling; set by Profile, read right after the main density call
            static float3 gPrevDisp;
            static float gHeat;                    // the heat at the point (the hot exhaust next to the nozzle glows), set by Profile like gPrevDisp

            float Profile(float3 p, uint2 blockMask, out float radius, out float dmul, out float gap)
            {
                float prof = 0.0, sumW = 0.0, sumWr = 0.0, sumP = 0.0;
                float3 sumWd = float3(0.0, 0.0, 0.0);
                gPrevDisp = float3(0.0, 0.0, 0.0);
                gHeat = 0.0;
                float sumWh = 0.0;
                gap = 1e9;
                if (_TrDebug > 1.5)
                {
                    for (int s2 = 0; s2 < _TrSegCount; s2++)
                    {
                        float4 A = _TrSeg[s2 * 4], B = _TrSeg[s2 * 4 + 1], M = _TrSeg[s2 * 4 + 2];
                        float3 ab = B.xyz - A.xyz; float l2 = dot(ab, ab); float3 pa = p - A.xyz;
                        float h = l2 > 1e-6 ? saturate(dot(pa, ab) / l2) : 0.0; float3 q = pa - ab * h;
                        float r = lerp(A.w, B.w, h) * _TrLook3.x; float d2 = dot(q, q);
                        if (d2 >= r * r) continue;
                        float pr = (1.0 - sqrt(d2) / r) * lerp(M.x, M.y, h);
                        float w = pr * M.z * _TrLook.x / r;
                        prof = max(prof, pr); sumP += pr; sumW += w; sumWr += w * r;
                    }
                    radius = sumW > 0.0 ? sumWr / sumW : 0.0; dmul = sumP > 0.0 ? sumW / sumP : 0.0;
                    return prof;
                }
                for (int b = 0; b < _TrBlockCount; b++)
                {
                    uint bit = b < 32 ? (blockMask.x >> b) & 1u : (blockMask.y >> (b - 32)) & 1u;
                    if (bit == 0u) continue;
                    float4 bs = _TrBlock[b];
                    float3 bd = p - bs.xyz;
                    if (dot(bd, bd) > bs.w * bs.w) { gap = min(gap, sqrt(dot(bd, bd)) - bs.w); continue; }
                    for (int c = b * 8; c < b * 8 + 8 && c < _TrChunkCount; c++)
                    {
                        float4 cs = _TrChunk[c];
                        float3 cd = p - cs.xyz;
                        if (dot(cd, cd) > cs.w * cs.w) { gap = min(gap, sqrt(dot(cd, cd)) - cs.w); continue; }
                        for (int s = c * 8; s < c * 8 + 8 && s < _TrSegCount; s++)
                        {
                            float4 A = _TrSeg[s * 4], B = _TrSeg[s * 4 + 1], M = _TrSeg[s * 4 + 2];
                            float3 ab = B.xyz - A.xyz;
                            float l2 = dot(ab, ab);
                            float3 pa = p - A.xyz;
                            float h = l2 > 1e-6 ? saturate(dot(pa, ab) / l2) : 0.0;
                            float3 q = pa - ab * h;
                            float r = lerp(A.w, B.w, h) * _TrLook3.x;
                            float d2 = dot(q, q);
                            if (d2 >= r * r) { gap = min(gap, sqrt(d2) - r); continue; }
                            float nd = sqrt(d2) / r;
                            gap = 0.0;          // inside a capsule: never an empty-space jump from here (its own surface is not in the gap)
                            float fade = lerp(M.x, M.y, h);
                            float pr = (1.0 - nd) * fade;
                            if (pr <= 0.0) continue;
                            float4 H = _TrSeg[s * 4 + 3];
                            float w = pr * M.z * lerp(H.z, H.w, h) * _TrLook.x / r;      // the density of this capsule: constant column of smoke / radius
                            prof = max(prof, pr);
                            sumP += pr; sumW += w; sumWr += w * r;
                            sumWd += w * q * (M.w - 1.0);
                            sumWh += w * lerp(H.x, H.y, h);
                        }
                    }
                }
                radius = sumW > 0.0 ? sumWr / sumW : 0.0;
                if (sumW > 0.0) { gPrevDisp = sumWd / sumW; gHeat = sumWh / sumW; }
                dmul = sumP > 0.0 ? sumW / sumP : 0.0;
                return prof;
            }

            // the noise, its scale tied to the radius and blended over two octaves (no popping when the plume grows)
            float ErodeNoise(float3 p, float radius, bool lite)
            {
                float f = _TrLook.w * 80.0 / max(radius, 0.2);                 // frequency in 1 / m
                float lg = log2(f);
                float lo = exp2(floor(lg));
                float bl = frac(lg);
                float n = lerp(Nz1(p * lo), Nz1(p * lo * 2.0), bl);
                if (lite) return n;                                         // the light march does not need the fine detail: half the texture fetches
                float nd = lerp(Nz1(p * lo * 4.7 + 3.1), Nz1(p * lo * 9.4 + 3.1), bl);       // a finer detail, weaker
                return lerp(n, nd, 0.05);          // the fine detail is almost off: it is what made the surface look like sandpaper
            }

            float Density(float3 p, uint2 blockMask, out float prof, out float radius, out float noiseProf, out float gap, bool lite)
            {
                float dmul;
                prof = Profile(p, blockMask, radius, dmul, gap);
                noiseProf = 0.0;
                if (prof <= 0.0) return 0.0;
                float n = ErodeNoise(p, radius, lite);
                // the billows: a slow noise moves the edge of the shape by a good part of the radius (big lobes, not only fine grain)
                float fb = _TrLook.w * 80.0 / max(radius, 0.2) * _TrLook3.z;
                float lgb = log2(fb); float lob = exp2(floor(lgb));
                float bn = lerp(Nz1(p * lob + 7.7), Nz1(p * lob * 2.0 + 7.7), frac(lgb));
// the cauliflower: a second, smaller scale of lobes on top of the big ones, stronger toward the rim (the core stays solid)
                float bn2 = lite ? 0.5 : lerp(Nz1(p * lob * 3.1 + 13.3), Nz1(p * lob * 6.2 + 13.3), frac(lgb));
                // young, thin smoke is a smooth column: the lobes grow with the radius (otherwise a thread of radius 5 m is a string of beads)
                float young = saturate(radius * 0.06);
                float shape = saturate(prof + (bn - 0.5) * _TrLook3.y * young + (bn2 - 0.5) * _TrLook5.y * young * (1.0 - 0.6 * prof) - 0.06);
                // the erosion is weaker on a thin young jet (a few metres): the noise is tied to the radius, so a full erosion there broke it into beads
                float ero = _TrLook.y * lerp(0.3, 1.0, saturate(radius * 0.125));
                float hd = n * ero + (1.0 - ero);
                float eroded = hd - (1.0 - shape);
                noiseProf = eroded;
                float profIn = prof;
                prof = shape;
                // the LIGHT feels a softer edge than the eye: with a sharp edge the self-shadow turns binary (full light / full shadow with a crisp border) and the
                // sunlit lobes read as flat plates torn like paper, most of all looking toward the sun; a soft edge for the light march gives a penumbra
                float sharpEff = (lite && _TrLook6.z > 0.0) ? min(_TrLook.z, _TrLook6.z) : _TrLook.z;
                float core = saturate(eroded / max(1.0 - sharpEff, 0.02)) * dmul;
                // THE INNER FILL: the noise shapes the surface, not the inside. Eroded deep inside, the thin smoke showed the contours of the noise through
                // itself (plates and terraces); from the fill profile inward it fades to a solid core
                if (_TrLook6.x > 0.0) core = max(core, smoothstep(_TrLook6.x, min(_TrLook6.x + 0.3, 1.0), profIn + (n - 0.5) * _TrLook6.y) * dmul);      // its edge is moved by the noise too: a smooth fill edge showed as a straight band where the lobes were eroded
                // THE POWDER: a thin, light fringe around the solid edge, made of fine grains (a few metres) - the smoke does not end on a line, it thins out in dust that
                // the light goes through. Only where the shape is near its edge; the core stays solid.
                if (lite || _TrLook5.z <= 0.0) return core;                  // no powder: no fetches for it
                float fr = smoothstep(-0.22, 0.02, shape) * (1.0 - smoothstep(0.02, 0.25, shape));
                float fine = lerp(Nz1(p * lob * 14.0 + 5.1), Nz1(p * lob * 28.0 + 5.1), frac(lgb));       // blended between the two octaves like the others: no seams where the octave changes
                float powder = fr * smoothstep(0.0, 0.25, profIn) * pow(saturate(fine * 1.3 - 0.25), 2.2) * _TrLook5.z * dmul * 0.35;
                return max(core, powder);
            }

            struct FragOut { fixed4 col : SV_Target0; float4 aux : SV_Target1; };

            FragOut frag (v2f i)
            {
                FragOut fo; fo.aux = float4(0.0, 0.0, 0.0, 0.0);
                float3 Ow = _WorldSpaceCameraPos;
                float3 wpj = i.wpos + ddx(i.wpos) * _PlumeJitter.x + ddy(i.wpos) * _PlumeJitter.y;
                float3 Dw = normalize(wpj - Ow);
                float3x3 R = (float3x3)_TrW2L;
                float3 o = mul(R, Ow - _TrAnchorW.xyz);
                float3 d = mul(R, Dw);
                float3 sunL = normalize(mul(R, dot(_SmokeSunDir.xyz, _SmokeSunDir.xyz) > 0.0001 ? normalize(_SmokeSunDir.xyz) : normalize(float3(0.3, 0.8, 0.4))));
                float3 upL = normalize(mul(R, _SmokeUpDir.xyz));

                float2 suv = i.scr.xy / i.scr.w;
                float raw0 = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, suv);
                float eye = LinearEyeDepth(raw0);
                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                float tScene = raw0 > 0.00001 ? eye / max(dot(Dw, camFwd), 1e-4) : 1e9;

                // which blocks does the ray touch, and over what interval
                uint2 mask = uint2(0, 0);
                float tmin = 1e9, tmax = 0.0;
                for (int b = 0; b < _TrBlockCount; b++)
                {
                    float t0, t1;
                    if (RaySphere(o, d, _TrBlock[b], t0, t1))
                    {
                        if (b < 32) mask.x |= (1u << b); else mask.y |= (1u << (b - 32));
                        tmin = min(tmin, max(t0, 0.0)); tmax = max(tmax, t1);
                    }
                }
                tmax = min(tmax, tScene);
                if (tmax <= tmin) discard;
                // The mask above holds the blocks the VIEW ray touches. The light rays (toward the sun, toward the sky) from a point on it leave through other blocks: with
                // the view mask they saw nothing there, the smoke of the neighbouring blocks cast no shadow, and the lighting changed at the surface of a block's sphere - flat
                // lit plates with torn, arc-shaped borders that depended on the camera angle. The light rays are tested against ALL blocks (a cheap sphere test each).
                uint2 lightMask = uint2(_TrBlockCount >= 32 ? 0xFFFFFFFFu : ((1u << _TrBlockCount) - 1u),
                                        _TrBlockCount > 32 ? (_TrBlockCount >= 64 ? 0xFFFFFFFFu : ((1u << (_TrBlockCount - 32)) - 1u)) : 0u);

                float cosS = dot(d, sunL);
                float directPh = 0.2 * HG(cosS, 0.8);
                float indirectPh = 2.5 * HG(cosS, 0.2) + 0.7 * HG(cosS, -0.4);
                float day = smoothstep(-0.12, 0.18, dot(sunL, upL));

                float T = 1.0;
                float3 L = float3(0.0, 0.0, 0.0);
                float distAcc = 0.0, distW = 0.0, dbgA = 0.0, dbgB = 0.0;
                float3 dispAcc = float3(0.0, 0.0, 0.0);
                float3 litCache = float3(0.7, 0.7, 0.7); float dlCache = 0.0; int litTick = 0;
                float ds = 2.0;
                // interleaved gradient noise (low discrepancy, no clumps) plus the golden ratio per frame: the grain is spread evenly and the temporal accumulation removes it
                // faster than white noise would (a hash of the pixel looks like sandpaper)
                float ign = frac(52.9829189 * frac(dot(i.pos.xy, float2(0.06711056, 0.00583715))));
                float ditherK = frac(ign + 0.61803398875 * _PlumeJitter.z + 0.7548776662 * _PlumeJitter.w);
                float tt = tmin + ds * ditherK;
                float lightJit = frac(ign * 1.7 + 0.3247179572 * _PlumeJitter.z);

                int iters = 0, denseN = 0;
                [loop]
                for (int k = 0; k < 400; k++)
                {
                    if (tt >= tmax || T < 0.02) break;
                    iters++;
                    float3 p = o + d * tt;
                    float prof, rad, nprof, gap;
                    float den = Density(p, mask, prof, rad, nprof, gap, false);
                    float3 pdisp = gPrevDisp;
                    float heat = gHeat;
                    if (prof <= 0.0)
                    {
                        // empty: the step is the distance to the nearest bound (the sphere or the capsule): never over a piece of smoke.
                        // In the eroded shell of a capsule the shape is 0 but the point is INSIDE the capsule (gap 0): a normal step there - the jump by the distance
                        // to the other bounds went straight through the smoke (cuts and holes at every joint of the capsules)
                        // (inside a capsule: never shorter than what still reaches the end of the ray with the steps left - see below)
                        ds = gap > 0.0 ? clamp(gap * 0.85, 0.4, 400.0) : max(clamp(0.09 * rad + tt * 0.0015, 0.25, 60.0), (tmax - tt) / float(400 - k));
                        tt += ds;
                        continue;
                    }
                    // the step follows the radius, but never shorter than what still reaches the end of the ray with the steps left: a ray along the axis of the
                    // trail (camera above the rocket, looking down) went through the thin jet at the nozzle in steps of a fraction of a metre and ran out of
                    // steps there - the rest of the column was missing until the camera moved a little
                    ds = max(clamp(0.09 * rad + tt * 0.0015, 0.25, 60.0), (tmax - tt) / float(400 - k));
                    if (den > 0.0005)
                    {
                        denseN++;
                        // The light changes slowly along the ray (a sample is a few metres, the light march is tens of metres long): it is computed on every SECOND dense sample
                        // and reused for the next - half the cost of the most expensive part, no visible difference (the dither moves between frames anyway)
                        [branch] if ((litTick++ & 1) == 0)
                        {
                        // toward the sun: a few samples over the length of the local radius
                        float dl = 0.0;
                        float Ls = max(rad * _TrLook4.z, 2.0);
                        for (int j = 0; j < 4; j++)
                        {
                            float tj = Ls * (j + lightJit) / 4.0;      // stratified with a FULL jitter: with a fixed position the 4 samples quantized the self-shadow of the sharp-edged lobes into flat plates
                            float pr2, r2, np2, g2;
                            dl += Density(p + sunL * tj, lightMask, pr2, r2, np2, g2, true) * (Ls / 4.0);
                        }
                        float dlRaw = dl;
                        dl *= _TrLook4.x;      // the light is scattered forward and around: it goes much deeper than the extinction of the view ray says
                        float edge = lerp(_TrLook2.z, 1.0, saturate(prof));              // the dark edge: the rim gets little of the multiple scattering
                        // how much of the sun gets here: the direct beam (beer) and the multiple scattering (softer, goes deeper), the rim gets little of the latter (the dark edge);
                        // the forward lobe adds a silver lining when the smoke is between the eye and the sun
                        float sunReach = lerp(exp(-dl), BeerMS(dl) * edge, 0.55);
                        float scat = sunReach * (1.0 + 6.0 * directPh);
                        float skyF = saturate(0.5 + 0.5 * dot(-d, upL));
                        float3 amb = lerp(_TrAmbGnd.rgb, _TrAmbSky.rgb, skyF);
                        float occ = BeerMS(prof * 3.0);
                        // the colour from the palette of the old trail (the shadow colour and the colour of the sun at the smoke's place), by how lit the sample is:
                        // the same smoke, so the two read as one; the floor of the light is the ambient setting, the inside of the smoke gets less of it
                        // the sky light is blocked by the smoke ABOVE: undersides and valleys are darker, tops are bright (shading that does not need the sun at the side)
                        float dlu = 0.0;
                        for (int ju = 0; ju < 2; ju++)
                        {
                            float tu = Ls * 0.4 * (ju + 0.5) / 2.0;
                            float pu, ru, nu, gu;
                            dlu += Density(p + upL * tu, lightMask, pu, ru, nu, gu, true) * (Ls * 0.4 / 2.0);
                        }
                        float skyOcc = exp(-dlu * _TrLook4.x);
                        float litness = lerp(_TrLook2.y, 1.0, pow(saturate(_TrLook2.x * scat * day * _TrLook3.w), _TrLook4.y));
                        litness *= lerp(0.72, 1.0, occ);
                        litness *= lerp(1.0, lerp(0.6, 1.0, skyOcc), _TrLook4.w);
                        float3 shadowTint = lerp(_TrShadowC.rgb, _TrShadowC.rgb * amb / max(dot(amb, float3(0.33, 0.33, 0.33)), 0.05), 0.35);
                        float3 lit = lerp(shadowTint, _TrSunC.rgb, saturate(litness));
                        if (_TrKsa.x > 0.5)
                        {
                            // PHYSICAL LIGHT (the model of cloud renderers): the sun reaching this point through the smoke toward it, scattered toward the eye -
                            // a narrow forward lobe for the direct beam (exp) and a broad lobe for the light scattered many times (BeerMS: it goes deep, the
                            // smoke is soft and bright inside), the latter weaker at the rim (dark edge); PLUS the sky, blocked by the smoke around the point
                            // (the core gets less than the rim). The colours are the sun's and the sky's at the place of the smoke (Scatterer).
                            float edgeK = lerp(_TrKsa.w, 1.0, saturate(prof));
                            float sc = exp(-dl) * directPh + BeerMS(dl) * indirectPh * edgeK;          // dl: the depth the light feels (self-shadow depth setting)
                            float3 skyAmb = _TrAmbSky.rgb;
                            float skyL = dot(skyAmb, float3(0.3333, 0.3333, 0.3333));
                            skyAmb = lerp(float3(skyL, skyL, skyL), skyAmb, 0.6);
                            // the clouds of EVE above the smoke: the sun through them (the sky light dims a little under a cloud deck too)
                            float cloudT = EveCloudLight(mul(transpose(R), p) + _TrAnchorW.xyz, normalize(mul(transpose(R), sunL)));
                            lit = _TrSunRaw.rgb * (sc * _TrKsa.y * day * cloudT) + skyAmb * (BeerMS(prof * 3.0) * _TrKsa.z * lerp(0.6, 1.0, cloudT));
                        }
                        lit *= _TrTint.rgb;
                        litCache = lit; dlCache = dl;
                        }
                        float3 lit = litCache; float dl = dlCache;
                        // THE SUN SHINING THROUGH: toward the sun the thin parts (edges, wisps) pass the beam on and glow, brighter than the sunlit surface (not clamped:
                        // it is a light, not an albedo); the beam keeps the colour of the sun (red at a low sun) and dies in the thick parts (exp(-depth))
                        lit += _TrSunRaw.rgb * (exp(-dl * 1.6) * directPh * _TrLook5.x * day) * (1.0 - _TrKsa.x);      // (the physical light has its own direct lobe)
                        // the light of the engine's flame: falls off with the distance from it (the smoke next to the nozzle glows)
                        float3 dfl = p - _TrFlame.xyz;
                        float fl = 1.0 / (1.0 + dot(dfl, dfl) / max(_TrFlame.w * _TrFlame.w, 1.0));
                        lit += _TrFlameCol.rgb * (fl * fl);
                        float stepT = exp(-den * ds);
                        float a = 1.0 - stepT;
                        // THE HOT EXHAUST GLOWS: next to the nozzle the gas is still hot (a light of its own, not lit), orange fading into the grey smoke;
                        // the heat is squared so the glow dies quickly, and the hottest part is a little whiter
                        float h2 = heat * heat;
                        float3 glow = _TrGlowCol.rgb * float3(1.0, 0.72, 0.5) * h2;         // pulled toward orange: the flame colour alone read as a yellow lamp
                        // hot gas scatters little sunlight (it is a thin, glowing gas, not yet soot and droplets): the glow takes over, the colour stays orange
                        float3 dL = T * a * (lit * (1.0 - 0.35 * h2) + glow);      // 0.85 left the hot gas next to the nozzle nearly black where its glow is weak
                        L += dL;
                        float wgt = T * a;
                        distAcc += tt * wgt; distW += wgt; dispAcc += wgt * pdisp;
                        dbgA += wgt * (_TrDebug > 3.5 ? saturate(dl * 0.3) : dot(litCache, float3(0.33, 0.33, 0.33))); dbgB += wgt;
                        T *= stepT;
                    }
                    tt += ds;
                }
// AERIAL PERSPECTIVE: far smoke is veiled by the air between it and the eye (the colour of the sky at the horizon, by the distance to the smoke)
                if (distW > 0.0)
                {
                    float dd = distAcc / distW;
                    float hz = (1.0 - exp(-dd * _TrAerial.x)) * _TrAerial.y;
                    L = lerp(L, _TrHaze.rgb * (1.0 - T), hz);
                }
                float3 Lc = L * _TrLook2.w;
                Lc = Lc / (1.0 + 0.12 * max(Lc - 1.0, 0.0));
                // MOTION VECTORS for the temporal upscaling: the density-weighted point of the smoke on this pixel, where it was a frame ago (it moved with the growth of the
                // capsules), as a shift in screen uv, weighted like the distance (the resolve divides by the weight)
                float2 motionUv = float2(0.0, 0.0);
                if (distW > 1e-4)
                {
                    float3 avgL = o + d * (distAcc / distW);
                    float3 dispL = dispAcc / distW;
                    float3x3 Ri = transpose(R);
                    float3 w0 = mul(Ri, avgL) + _TrAnchorW.xyz;
                    float3 w1 = mul(Ri, avgL + dispL) + _TrAnchorW.xyz;
                    float3 v0 = mul(UNITY_MATRIX_V, float4(w0, 1.0)).xyz;
                    float3 v1 = mul(UNITY_MATRIX_V, float4(w1, 1.0)).xyz;
                    float2 p11_22 = float2(unity_CameraProjection._11, unity_CameraProjection._22);
                    if (-v0.z > 0.05 && -v1.z > 0.05)
                        motionUv = ((v1.xy / -v1.z) - (v0.xy / -v0.z)) * p11_22 * 0.5 * distW;
                }
                fo.aux = float4(distAcc, distW, motionUv);
                if (_TrDebug > 4.5) { fo.col = fixed4(iters / 60.0, denseN / 30.0, 0.0, 1.0); return fo; }        // the cost: red = all steps / 60, green = dense steps / 30
                if (_TrDebug > 2.5) { float dv = dbgB > 0.0 ? dbgA / dbgB : 0.0; fo.col = fixed4(dv, dv, dv, 1.0 - T); return fo; }
                fo.col = fixed4(Lc, 1.0 - T);
                return fo;
            }
            ENDCG
        }

        // THE SHADOW THE TRAIL CASTS ON THE SCENE (pass 1, drawn by HalfResSmokeRenderer.BuildShadowCast with the same property block): for every pixel of the scene inside
        // the rectangle where a shadow can fall the position is rebuilt from the depth, and a ray toward the sun is walked through the capsules (only the coarse profile, no noise:
        // a shadow is soft); the more smoke the sun ray crosses, the darker the picture there. Most pixels miss every bounding sphere and leave at once.
        Pass
        {
            Name "TrailShadowCast"
            Blend DstColor Zero
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vertS
            #pragma fragment fragS
            #include "UnityCG.cginc"

            sampler2D_float _CameraDepthTexture;
            float4 _SmokeSunDir;
            float4 _SmokeUpDir;
            StructuredBuffer<float4> _TrSeg;
            StructuredBuffer<float4> _TrChunk;
            StructuredBuffer<float4> _TrBlock;
            int _TrSegCount, _TrChunkCount, _TrBlockCount;
            float4 _TrAnchorW;
            float4x4 _TrW2L;
            float4 _TrLook;
            float4 _CastRect;
            float _ShadowCastStrength;
            float _ShadowCastFloor;
            float _ShadowCastDensity;
            int _ShadowCastSteps;
            float _TrShadowOn;             // 0 = this trail casts no shadow on the scene (its own switch, next to the global strength)
            float _TrShadowDbg;            // > 0.5: developer test - tints the whole cast rectangle blue (the pass reaches the screen, whatever the shadow computes)
            float _TrMaskMode;             // 1 = drawn into the sun's shadow mask (a light command buffer: the camera is rebuilt from our own matrices)
            float _TrMaskFlip;
            float4x4 _TrInvP;              // the camera's inverse projection (OpenGL convention) and camera -> world, for the mask mode
            float4x4 _TrC2W;
            float4 _TrZB;                  // the camera's own _ZBufferParams (mask mode)

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

            bool RaySphereS(float3 o, float3 d, float4 s, out float t0, out float t1)
            {
                float3 oc = o - s.xyz;
                float b = dot(oc, d);
                float c = dot(oc, oc) - s.w * s.w;
                float disc = b * b - c;
                t0 = 0.0; t1 = 0.0;
                if (disc < 0.0) return false;
                float sq = sqrt(disc);
                t0 = -b - sq; t1 = -b + sq;
                return t1 > 0.0;
            }

            // the coarse density of the smoke at a point (the profile of the capsules, no noise) and the distance to the nearest bound (for the step)
            float CoarseDensity(float3 p, uint2 blockMask, out float gap)
            {
                float best = 0.0;
                gap = 1e9;
                for (int b = 0; b < _TrBlockCount; b++)
                {
                    uint bit = b < 32 ? (blockMask.x >> b) & 1u : (blockMask.y >> (b - 32)) & 1u;
                    if (bit == 0u) continue;
                    float4 bs = _TrBlock[b];
                    float3 bd = p - bs.xyz;
                    if (dot(bd, bd) > bs.w * bs.w) { gap = min(gap, sqrt(dot(bd, bd)) - bs.w); continue; }
                    for (int c = b * 8; c < b * 8 + 8 && c < _TrChunkCount; c++)
                    {
                        float4 cs = _TrChunk[c];
                        float3 cd = p - cs.xyz;
                        if (dot(cd, cd) > cs.w * cs.w) { gap = min(gap, sqrt(dot(cd, cd)) - cs.w); continue; }
                        for (int s = c * 8; s < c * 8 + 8 && s < _TrSegCount; s++)
                        {
                            float4 A = _TrSeg[s * 4], B = _TrSeg[s * 4 + 1], M = _TrSeg[s * 4 + 2];
                            float3 ab = B.xyz - A.xyz;
                            float l2 = dot(ab, ab);
                            float3 pa = p - A.xyz;
                            float h = l2 > 1e-6 ? saturate(dot(pa, ab) / l2) : 0.0;
                            float3 q = pa - ab * h;
                            float r = lerp(A.w, B.w, h) * 0.85;
                            float d2 = dot(q, q);
                            if (d2 >= r * r) { gap = min(gap, sqrt(d2) - r); continue; }
                            float pr = (1.0 - sqrt(d2) / r) * lerp(M.x, M.y, h);
                            float4 H = _TrSeg[s * 4 + 3];
                            best = max(best, saturate(pr * 2.2) * M.z * lerp(H.z, H.w, h) * _TrLook.x / max(r, 0.5));
                        }
                    }
                }
                return best;
            }

            float IGNs(float2 p) { return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715)))); }

            fixed4 fragS (v2fS i) : SV_Target
            {
                if (_TrShadowOn < 0.5) return fixed4(1, 1, 1, 1);
                if (_TrShadowDbg > 0.5) return fixed4(0.45, 0.65, 1.0, 1.0);
                float2 uv = i.scr.xy / i.scr.w;
                if (_TrMaskMode > 0.5 && _TrMaskFlip > 0.5) uv.y = 1.0 - uv.y;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float4 zb = _TrMaskMode > 0.5 ? _TrZB : _ZBufferParams;
                if (1.0 / (zb.x * raw + zb.y) > 0.999) return fixed4(1, 1, 1, 1);          // Linear01Depth: the sky
                float eye = 1.0 / (zb.z * raw + zb.w);                                      // LinearEyeDepth
                float3 worldPos;
                if (_TrMaskMode > 0.5)
                {
                    // inside the light's command buffer the camera matrices are not the camera's: the ray from our own (uv -> the view ray at depth 1 -> world)
                    float4 vr = mul(_TrInvP, float4(uv * 2.0 - 1.0, 1.0, 1.0));
                    float3 v = vr.xyz / vr.w;
                    v /= max(-v.z, 1e-6);
                    worldPos = mul(_TrC2W, float4(v * eye, 1.0)).xyz;
                }
                else
                {
                    float3 viewDir = i.ray / max(-i.ray.z, 1e-6);
                    float3 worldDir = mul((float3x3)UNITY_MATRIX_I_V, viewDir);
                    worldPos = _WorldSpaceCameraPos + worldDir * eye;
                }

                float3 sunW = dot(_SmokeSunDir.xyz, _SmokeSunDir.xyz) > 0.0001 ? normalize(_SmokeSunDir.xyz) : normalize(float3(0.3, 0.8, 0.4));
                if (dot(sunW, _SmokeUpDir.xyz) < -0.05) return fixed4(1, 1, 1, 1);          // the sun is down

                float3x3 R = (float3x3)_TrW2L;
                float3 o = mul(R, worldPos + sunW * 0.3 - _TrAnchorW.xyz);
                float3 d = normalize(mul(R, sunW));

                uint2 mask = uint2(0, 0);
                float tmin = 1e9, tmax = 0.0;
                for (int b = 0; b < _TrBlockCount; b++)
                {
                    float t0, t1;
                    if (RaySphereS(o, d, _TrBlock[b], t0, t1))
                    {
                        if (b < 32) mask.x |= (1u << b); else mask.y |= (1u << (b - 32));
                        tmin = min(tmin, max(t0, 0.0)); tmax = max(tmax, t1);
                    }
                }
                if (tmax <= tmin) return fixed4(1, 1, 1, 1);

                float tau = 0.0;
                float t = tmin + 2.0 * IGNs(i.pos.xy);
                int maxIter = max(_ShadowCastSteps * 3, 12);
                [loop]
                for (int k = 0; k < maxIter; k++)
                {
                    if (t >= tmax || tau > 4.0) break;
                    float gap;
                    float dens = CoarseDensity(o + d * t, mask, gap);
                    float stepL = dens > 0.0 ? max(1.5, 0.05 * tmax) : clamp(gap * 0.85, 1.5, 400.0);
                    if (dens > 0.0) tau += dens * stepL;
                    t += stepL;
                }
                float transmittance = exp(-tau * 0.25 * max(_ShadowCastDensity, 0.05) * 2.0);
                float shade = lerp(1.0, max(transmittance, _ShadowCastFloor), _ShadowCastStrength);
                return fixed4(shade, shade, shade, 1.0);
            }
            ENDCG
        }

        // CALIBRATION (developer tool, pass 2): the cloud light EVE's volume gives at the ground for every pixel (the view ray against a sphere of the planet's
        // radius, _TrEveCalib.x), as grey - laid over the screen it is compared with the shadows EVE's clouds cast on the terrain to find the mapping.
        Pass
        {
            Name "EveCalib"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vertC
            #pragma fragment fragC
            #include "UnityCG.cginc"
            float4 _SmokeSunDir;
            float4 _TrEveCalib;          // x = ground radius, y = opacity of the overlay

            struct appdata { float4 vertex : POSITION; };
            struct v2fC { float4 pos : SV_POSITION; float3 ray : TEXCOORD0; };

            v2fC vertC (appdata v)
            {
                v2fC o;
                float2 ndc = v.vertex.xy * 2.0 - 1.0;
                o.pos = float4(v.vertex.xy * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                o.ray = float3((ndc.x + UNITY_MATRIX_P._m02) / UNITY_MATRIX_P._m00, (ndc.y + UNITY_MATRIX_P._m12) / UNITY_MATRIX_P._m11, -1.0);
                return o;
            }

            fixed4 fragC (v2fC i) : SV_Target
            {
                float3 dir = normalize(mul((float3x3)UNITY_MATRIX_I_V, i.ray));
                float3 o = _WorldSpaceCameraPos;
                float3 oc = o - _TrEve.yzw;
                float b = dot(oc, dir);
                float q = dot(oc, oc) - _TrEveCalib.x * _TrEveCalib.x;
                float disc = b * b - q;
                if (disc < 0.0) return fixed4(0, 0, 0, 0);
                float t = -b - sqrt(disc);
                if (t < 0.0) return fixed4(0, 0, 0, 0);
                float3 g = o + dir * t;
                float3 sunW = normalize(_SmokeSunDir.xyz);
                float v = EveCloudLight(g, sunW);
                // _TrEveCalib.y > 0.5: the in-game overlay - red where our cloud shadow falls (compare with the real shadows on the ground); else the grey value
                if (_TrEveCalib.y > 0.5) return fixed4(1.0, 0.1, 0.05, (1.0 - v) * 0.75);
                return fixed4(v, v, v, 1);
            }
            ENDCG
        }
    }
}
