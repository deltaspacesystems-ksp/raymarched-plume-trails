// A physical rocket plume, evaluated analytically per ray (no texture, no mesh, no particles).
//
// The CPU side (PlumePhysics.cs) turns an engine's own numbers (thrust, mass flow, propellant, nozzle area ratio, throttle) and the
// ambient pressure into a PlumeState: the nozzle exit size, the fully expanded jet, the SHOCK-CELL length, the Mach disk, the core
// length, the mixing spread. This shader evaluates the field those numbers describe at every sample of a ray through a box around
// the plume:
//   - the jet boundary radius swells and contracts once per shock cell
//   - the shock cells themselves: oblique shocks run from the boundary to the axis, cross at a bright KNOT and reflect back
//   - the core gas (incandescent soot / particles, blackbody colour of its temperature) and the afterburning shell around it
//   - the gas's own line emission (OH, CH, H2O bands): pink in the core, blue-violet at the edge, brighter in the shocks
//   - streaky turbulence that grows downstream
// and integrates emission + absorption along the ray (premultiplied, over the scene), clipped by the scene depth.
Shader "RaymarchedPlumeTrails/PlumeCore"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "PlumeCore"
            // drawn into the mod's reduced-resolution smoke buffer (HalfResSmokeRenderer): premultiplied colour, and the distance to the
            // light in a second target for the temporal upscaling (accumulated additively, weighted by how much each sample contributes)
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

            float4 _PlumeJitter;  // temporal upscaling: sub-pixel offset (xy) and dither seed (zw), set by HalfResSmokeRenderer
            float4 _PlumeFrame;   // xyz = nozzle exit centre (world), w = render length along the axis (m)
            float4 _PlumeAxis;    // xyz = exhaust direction (unit), w = bounding radius (m)
            float4 _PlumeRight;   // xyz = a unit vector across the axis
            float4 _PlumeA;       // De (exit diameter), Dj (fully expanded diameter), Ls (shock cell length), Lc (core length)
            float4 _PlumeB;       // spread, shockStrength, machDiskX, machDiskR
            float4 _PlumeC;       // Te (exit temperature), Ta (ambient), Pc / Pc_full (throttle), radBoost (K)
            float4 _PlumeD;       // sootLoading x sootScale, afterburn, gas emissivity x boost, exposure
            float4 _PlumeF;       // tan(angle of the jet boundary at the nozzle lip), oxygen available for afterburning, pressure ratio Pe/Pa, 2/(gamma-1)
            float4 _PlumeH;       // the surface the flame hits: x = distance along the axis (0 = none), yzw = its normal in the plume frame, facing the flame
            float4 _PlumeG;       // x = far-field (vacuum fan) weight: EXPERIMENTAL, 0 = off; y = floor of the afterburning glow strength; z = thickening of the turned flame; w = STREAK (0..1): the long, smooth, narrow streak of a long exposure
            float4 _PlumeE;       // seed, steps, density of the surrounding air (0..1), unused
            float4 _PlumeJ;       // x = SPARKS (burning alumina particles flying in the plume, 0 = none), y = SOOT FRINGE (dark smoke that absorbs at the edge of the flame), zw unused
            float4 _PlumeCoreCol; // gas colour in the core
            float4 _PlumeRimCol;  // gas colour at the edge
            float4 _PlumeKnotCol; // gas colour in the shock knots
            float4 _PlumeAfterCol; // colour of the afterburning zone (rgb) and how much it replaces the black body orange (a)

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float4 scr : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.scr = ComputeScreenPos(o.pos);
                return o;
            }

            float Nz(float3 p) { return tex3Dlod(_RaymarchedPlumeTrailsNoise, float4(p / _NoiseTilePeriod, 0.0)).r; }

            float Sm(float a, float b, float x) { float t = saturate((x - a) / (b - a)); return t * t * (3.0 - 2.0 * t); }

            // blackbody colour (linear rgb), Tanner Helland's fit of the Planckian locus; T in kelvin
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

            // jet boundary radius at axial distance x: swells to the fully expanded size and oscillates once per cell
            float BoundaryRadius(float x)
            {
                float De = _PlumeA.x, Dj = _PlumeA.y, Ls = _PlumeA.z, Lc = _PlumeA.w;
                float r0 = 0.5 * De, rj = 0.5 * Dj;
                // a real over-expanded jet only necks by ~30% (the ideal Dj overshoots for strong over-expansion)
                rj = clamp(rj, 0.72 * r0, 1.8 * r0);
                // The boundary leaves the nozzle lip at the angle given by the nozzle's exit angle and the Prandtl-Meyer turning of the
                // expansion fan (computed in PlumePhysics) and oscillates about the fully expanded radius, once per shock cell:
                //   r(x) = rj + A cos(k x - ph),  r(0) = r0  and  r'(0) = tan(lip)
                float k = 6.2831853 / Ls;
                float A0 = r0 - rj, B0 = _PlumeF.x / k;
                float Am = sqrt(A0 * A0 + B0 * B0);
                float ph = atan2(B0, A0);
                float damp = exp(-x / (4.0 * Lc));
                // the luminous plume is not the jet boundary: photographs (Shuttle SSME, RS-25, RS-68) show smooth edges with the diamonds as a
                // faint structure inside, so the visible edge follows only part of the boundary oscillation
                float rbound = rj + Am * cos(k * x - ph) * damp;
                return max(r0 + 0.18 * (rbound - r0), 0.5 * r0);
            }

            // emission (rgb per metre) and extinction (per metre) at a point in the plume frame
            void Field(float x, float y, float z, out float sigma, out float3 j)
            {
                sigma = 0.0; j = float3(0.0, 0.0, 0.0);
                if (x < 0.0) return;
                float De = _PlumeA.x, Dj = _PlumeA.y, Ls = _PlumeA.z, Lc = _PlumeA.w;
                float stk = _PlumeG.w;
                float spread = _PlumeB.x * (1.0 - 0.5 * stk), shockStrength = _PlumeB.y * (1.0 - 0.9 * stk), mdX = _PlumeB.z, mdR = _PlumeB.w;
                float Te = _PlumeC.x, Ta = _PlumeC.y, thr = _PlumeC.z, radBoost = _PlumeC.w;
                float sootK = _PlumeD.x, afterburn = _PlumeD.y * _PlumeF.y, gasK = _PlumeD.z;     // no oxygen in the atmosphere: nothing to afterburn with
                float seed = _PlumeE.x;

                // Early out (performance): far outside the plume's envelope nothing is emitted, so skip every noise lookup. The envelope is
                // generous - the body, its shear layer, the turbulent edge and the bending of the tail all fit inside.
                float rbE = BoundaryRadius(x);
                float bE = 0.025 * rbE + 0.10 * spread * x;
                float tailE = Sm(0.7 * Lc, 2.2 * Lc, x);
                float rEnv = 1.6 * (rbE + 0.9 * spread * max(0.0, x - 0.6 * Lc) + 2.0 * bE) + 1.1 * tailE * (0.9 * De + 0.12 * x);
                if (sqrt(y * y + z * z) > max(rEnv, mdR * 1.3)) return;

                // time (wrapped: float precision) and the speed at which the large turbulent structures are carried (Papamoschou & Roshko:
                // the convective velocity, computed on the CPU in PlumePhysics) - hundreds of m/s for a rocket, so the tail seethes
                float tm = _PlumeE.w > 0.0 ? _PlumeE.w : fmod(_Time.y, 300.0);
                float adv = tm * max(_PlumeE.z, 5.0);
                // RAGING TAIL: where the shock diamonds end the jet is a turbulent flame - tongues that lash sideways, pinch off and
                // flare, carried downstream fast. The whole column is bent by large, quickly moving eddies that grow with distance.
                float tail = Sm(0.7 * Lc, 2.2 * Lc, x);
                float3 wq = float3(x - adv, y, z) / (1.6 * De) + seed;
                float wy = Nz(wq) - 0.5, wz = Nz(wq + float3(5.2, 1.7, 9.1)) - 0.5;
                float bendK = tail * (0.9 * De + lerp(0.12, 0.03, stk) * x) * (1.0 - 0.6 * stk);
                float rS = sqrt(y * y + z * z);          // the shock cells live in the core: the eddies do not bend them (before the train breaks)
                y += 1.5 * bendK * wy;
                z += 1.5 * bendK * wz;
                float r = sqrt(y * y + z * z);
                float rb = BoundaryRadius(x);
                float rRef = 0.5 * max(Dj, De);

                // turbulence: crisp near the nozzle, breaking up and streaking as the shear layer rolls up downstream
                float growth = Sm(0.15 * Lc, 1.6 * Lc, x);
                // the shear layer rolls up from the nozzle lip: ripples on the edge from the very start (Kelvin-Helmholtz), the whole body breaks up later
                float turbAmp = (0.04 + 0.30 * Sm(0.5 * Lc, 2.4 * Lc, x)) * (1.0 - 0.75 * stk);
                // life: the eddies are carried downstream by the flow, the whole jet flaps a little, the cells breathe
                float3 pw = float3(x - adv, y + 0.03 * De * sin(tm * 17.0 + seed), z + 0.03 * De * cos(tm * 13.0 + seed)) + seed;
                float nEdge = Nz(pw / (0.9 * De)) - 0.5;
                float nDens = Nz(pw / (0.45 * De) + 11.0);
                float nStreak = Nz(float3(pw.x / (3.2 * De) + 3.0, pw.y / (0.30 * De), pw.z / (0.30 * De) + 8.0));
                float streak = 1.0 + growth * (1.7 * (nStreak - 0.45));
                // flame intermittency: bright bursts and dark gaps travelling down the tail
                float burst = Nz(float3((x - adv) / (2.2 * De), y / (1.1 * De), z / (1.1 * De)) + seed * 1.7);
                streak *= lerp(1.0, 0.25 + 1.9 * burst * burst, tail * (1.0 - 0.8 * _PlumeG.w));
                float rT = r * (1.0 + 1.5 * turbAmp * nEdge);
                float b = 0.025 * rb + 0.10 * spread * x;
                float decay = (x <= Lc) ? 1.0 : pow(Lc / x, lerp(1.0, 0.30, _PlumeG.w));
                float rBody = rb + 0.9 * spread * max(0.0, x - 0.6 * Lc);
                float wBody = 1.0 - Sm(rBody - b, rBody + 1.2 * b, rT);

                // FAR FIELD of a strongly under-expanded jet (high altitude, vacuum). The gas expands freely from the nozzle lip like a
                // SOURCE FLOW: it streams along rays from a virtual source behind the nozzle, the density falls as 1/R^2 (conservation of mass
                // in radial expansion), and across the cone it follows a cosine law (Simons-type vacuum plume model; the exponent 2/(gamma-1)
                // is the isentropic one, not checked against the original paper). The cone's half-angle is the lip angle. The streamlines show
                // as fibres along the rays: the fibrous cone of a Saturn V at altitude.
                float vacW = Sm(1.5, 6.0, _PlumeF.z) * _PlumeG.x;
                float vacDens = 0.0, vacQ = 1.0;
                if (vacW > 0.001)
                {
                    float tl = max(_PlumeF.x, 0.18);
                    float phim = atan(tl);
                    float xr = x - (-0.5 * De / tl);
                    float R = sqrt(xr * xr + r * r);
                    float uphi = saturate(atan2(r, xr) / phim);
                    float fa = pow(max(cos(uphi * 1.5708), 0.0), _PlumeF.w);
                    float Rref = 0.5 * De / sin(phim);
                    float q = Rref / max(R, Rref);
                    vacQ = q;
                    float fib = Nz(float3(uphi * 4.0 + seed, (R - adv) / (12.0 * De), seed * 0.5 + 2.0));
                    vacDens = q * q * fa * (0.55 + 0.9 * fib);
                    rBody = lerp(rBody, xr * tl, vacW);
                    wBody = lerp(wBody, 1.0 - Sm(0.8, 1.0, uphi), vacW);
                }

                // shock cells from geometry: |r| = rRef |1 - 2u| (incident shock down to the axis at u = 0.5, reflected back up)
                // The shock-cell train (NASA GRC, Wernet et al. 2021, PIV of M 1.4-2.0 jets): the first ~4 cells are stable in size and
                // position; beyond them large turbulent structures distort and break the train, STRETCH it (5, 6, 7+ cells, further than the
                // mean potential core) and after they pass it snaps back to ~4. The count is a random function of time.
                float Lsb = Ls * (1.0 + 0.012 * sin(tm * 23.0 + seed));
                float ci0 = x / Ls;
                float nBase = clamp(1.3 * Lc / Ls, 4.0, 9.0);
                float Nt = Nz(float3(tm * max(_PlumeE.z, 5.0) / max(Lc, 1.0) * 0.9, seed * 1.3, 4.0));
                float nVis = 5.6 + (nBase * 1.25 - 5.6) * Sm(0.3, 0.8, Nt);
                float distort = Sm(3.5, 6.0, ci0) * (Nz(float3((x - adv) / (1.4 * De), y / (1.4 * De), z / (1.4 * De)) + seed * 0.7) - 0.5);
                float xs = x + 0.7 * Ls * distort;
                float u = xs / Lsb - floor(xs / Lsb);
                float dShock = abs(rS - rRef * abs(1.0 - 2.0 * u));
                float lineW = 0.045 * rRef + 0.002 * x;
                float shockLine = exp(-pow(dShock / lineW, 2.0));
                float knotU = exp(-pow((u - 0.5) / 0.27, 2.0));
                float knotR = exp(-pow(rS / (0.88 * rRef), 2.0));
                float knot = knotU * knotR;
                float compressed = Sm(0.0, 0.25, u) * (1.0 - Sm(0.75, 1.0, u)) * saturate(1.0 - rS / max(rb, 1e-3));
                float cellFade = (1.0 - Sm(5.0, nVis + 1.2, ci0)) * exp(-x / (9.0 * Lc));
                float amp = shockStrength * cellFade * (0.55 + 0.45 * _PlumeF.y) * (0.88 + 0.12 * sin(tm * 47.0 + seed * 3.0) * sin(tm * 29.0 + x * 0.7));
                // the diamonds of real plumes are soft bright knots on the axis with a faint glow around them - not outlined rhombi
                float shock = amp * (3.2 * knot + 0.22 * shockLine * (1.0 - 0.6 * knotR) + 0.70 * compressed);

                // incandescent soot / particles at the gas temperature
                // a freely expanding gas cools as it expands: T ~ rho^(gamma-1) ~ R^(-2(gamma-1))  (2(gamma-1) = 4 / (2/(gamma-1)))
                float vacCool = lerp(1.0, pow(vacQ, 4.0 / max(_PlumeF.w, 0.5)), vacW);
                float Tcore = Ta + (Te - Ta) * decay * vacCool;
                Tcore *= 1.0 + 2.4 * shock;
                float dens = lerp(pow(0.5 * De / max(rBody, 0.1 * De), 2.0), vacDens, vacW) * (1.0 + 0.9 * shock) * thr;
                float sigmaCore = 0.30 * lerp(1.0, 0.3, vacW) * sootK * dens * (0.6 + 0.8 * nDens) * streak * wBody * decay * exp(-x / (lerp(3.0, 40.0, _PlumeG.w) * Lc));
                float Tlum = Tcore + radBoost * decay + 750.0 * stk * exp(-x / (7.0 * Lc));
                float shellR = rb + 0.4 * b;
                float shell = exp(-pow((r - shellR) / (1.4 * b + 0.03 * rb), 2.0)) * Sm(0.08 * Lc, 0.55 * Lc, x) * exp(-max(0.0, x - 2.2 * Lc) / (lerp(1.5, 24.0, _PlumeG.w) * Lc));
                // the excess temperature of the mixed gas falls like 1/x (and the light as T^4): the burning zone cools from ~2150 K to a dull red
                // (~1100 K) over a few core lengths, then it is only smoke
                float Tab = lerp(2150.0, 1100.0, Sm(1.8 * Lc, lerp(5.0, 70.0, _PlumeG.w) * Lc, x));
                float sigmaShell = 0.30 * afterburn * max(sootK, _PlumeG.y) * shell * min(1.0, 0.4 + dens);
                // Afterburning of the whole mixed region behind the potential core: the fuel-rich exhaust that has entrained air burns
                // (CO -> CO2, H2 -> H2O) and keeps the plume hot far downstream (Gomberg & Stewart; Leone & Turns, NASA CR-197503) - a broad
                // turbulent flame that flares and gutters with the large structures
                float mixBody = wBody * Sm(0.6 * Lc, 1.4 * Lc, x) * exp(-max(0.0, x - 3.2 * Lc) / (lerp(1.8, 28.0, _PlumeG.w) * Lc));
                mixBody *= lerp(1.0, vacQ, vacW);          // the afterburning gas thins out with the expansion (density ~ 1/R^2; the glow of a column ~ 1/R)
                sigmaShell += 0.20 * afterburn * max(sootK, _PlumeG.y) * mixBody * streak * (0.5 + 0.9 * nDens);

                if (sigmaCore > 0.0)
                {
                    float I = pow(Tlum / 1800.0, 4.0) * 1.9;
                    j += Blackbody(Tlum) * (I * sigmaCore);
                }
                if (sigmaShell > 0.0)
                {
                    float I = pow(Tab / 1800.0, 4.0) * 1.1;
                    float cw = _PlumeAfterCol.a * saturate((Tab - 1100.0) / 1050.0);       // the species colour fades as the gas cools toward a black body
                    j += lerp(Blackbody(Tab), _PlumeAfterCol.rgb, cw) * (I * sigmaShell);
                }
                sigma = sigmaCore + sigmaShell;

                // the gas's own line emission: pink core, blue-violet edge, pink-white in the knots, streaked downstream
                float gasShape = wBody * decay * pow(0.5 * De / max(rBody, 0.1 * De), 2.0) * thr;
                float gasE = gasK * gasShape * (0.10 + 2.6 * shock) * streak * (0.55 + 0.9 * nDens);
                if (gasE > 0.0)
                {
                    float edge = Sm(0.45, 1.0, r / max(rBody, 1e-3));
                    float3 gc = lerp(_PlumeCoreCol.rgb, _PlumeRimCol.rgb, edge);
                    gc = lerp(gc, _PlumeKnotCol.rgb, min(1.0, 1.6 * shock));
                    j += gc * (gasE * 1.6);
                    sigma += 0.004 * gasE;
                }
                // SPARKS: burning particles of the exhaust (alumina in a solid motor) streaking down the plume - sparse bright points, brighter near the core
                if (_PlumeJ.x > 0.001)
                {
                    float3 sq = float3((x - adv * 1.25) / (0.55 * De + 0.004 * x), y / (0.20 * De), z / (0.20 * De)) + seed * 3.1;
                    float spk = smoothstep(0.80, 0.93, Nz(sq)) * smoothstep(0.05 * De, 0.9 * rb + 0.02 * x, 1.4 * rb - r + 0.5 * rb);
                    j += float3(1.0, 0.82, 0.55) * (spk * _PlumeJ.x * (1.0 + 4.0 * decay) * thr);
                    sigma += 0.02 * spk * _PlumeJ.x;
                }
                // SOOT FRINGE: smoke at the edge of the flame, it absorbs the light behind it (the brown-black rim of a booster firing)
                if (_PlumeJ.y > 0.001)
                {
                    float rim = Sm(0.55, 1.05, r / max(rBody + 1.5 * b, 1e-3)) * (1.0 - Sm(1.05, 1.9, r / max(rBody + 1.5 * b, 1e-3)));
                    float edgeN = 0.5 + 0.9 * Nz(float3(x - adv * 0.6, y, z) / (0.7 * De) + seed * 2.0);
                    sigma += 0.55 * _PlumeJ.y * rim * edgeN * Sm(0.3 * Lc, 1.2 * Lc, x) * thr / (1.0 + 0.002 * x);
                }
                float halo = exp(-pow(r / (1.7 * rb + lerp(0.05, 0.022, stk) * x + 0.25 * De), 2.0)) * decay * thr * Sm(0.0, 0.3 * Lc, x) * (1.0 - Sm(0.45 * _PlumeAxis.w, 0.9 * _PlumeAxis.w, r));
                j += Blackbody(1650.0) * (0.11 * halo * max(sootK, 0.35));
                // fade out over the last part of the drawn length so the plume never ends in a flat cut
                float endFade = 1.0 - Sm(0.62 * _PlumeFrame.w, _PlumeFrame.w, x);
                endFade *= endFade;
                sigma *= endFade; j *= endFade;
                if (mdX > 0.0)
                {
                    float md = exp(-pow((x - mdX) / max(0.025 * mdX, 0.08), 2.0)) * (1.0 - Sm(0.75 * mdR, mdR, r));
                    if (md > 0.01) { j += float3(1.0, 0.85, 0.75) * (md * 7.0); sigma += md * 0.3; }
                }
            }

            // THE FLAME TURNED BY A SURFACE (flame deflector, trench floor). A supersonic jet that hits an inclined plate does not stop: it turns
            // and runs away along the plate as a wall jet. The straight plume continued past the plate and then MIRRORED in the plate's plane is
            // that turned flame to first order (the specular reflection of the jet's axis), and it spreads as it runs (the wall jet thickens
            // with distance, Glauert). Everything below the plate is the structure: nothing is emitted there.
            void FieldBent(float x, float y, float z, out float sigma, out float3 j)
            {
                if (_PlumeH.x <= 0.0) { Field(x, y, z, sigma, j); return; }
                float De = _PlumeA.x;
                float L = _PlumeH.x;
                float3 n = _PlumeH.yzw;
                float3 q = float3(x, y, z);
                float h = dot(q - float3(L, 0.0, 0.0), n);          // height above the plate
                sigma = 0.0; j = float3(0.0, 0.0, 0.0);
                if (h < 0.0) return;                                 // inside the structure
                // the incident flame, only as far as the plate
                float wIn = 1.0 - Sm(L - 0.4 * De, L + 0.4 * De, x);
                if (wIn > 0.001)
                {
                    float s0; float3 j0;
                    Field(x, y, z, s0, j0);
                    sigma += s0 * wIn; j += j0 * wIn;
                }
                // Two ways in which the flame is turned, mixed by how much the plate is tilted. A plate that stands across the jet (the flame
                // hits it head on) makes a RADIAL WALL JET: the flow runs out in every direction along the plate from the stagnation point, a
                // sheet whose thickness is the height above the plate and whose axis is the distance along it. A tilted plate (the A-frame
                // deflector) turns the jet like a mirror: the straight plume continued past the plate and mirrored in it.
                float3 tIn = float3(1.0, 0.0, 0.0) - n * n.x;                // the incident axis projected on the plate
                float tl = length(tIn);
                float wM = smoothstep(0.25, 0.65, tl);                       // 0 = head-on (radial), 1 = tilted (mirror)
                float g0 = 0.10 * _PlumeG.z / max(De, 0.2);
                if (wM > 0.001)
                {
                    float3 qm = q - 2.0 * h * n;
                    float d = qm.x - L;
                    if (d > -0.4 * De)
                    {
                        float g = 1.0 + g0 * max(d, 0.0);
                        float s1; float3 j1;
                        Field(qm.x, qm.y / g, qm.z / g, s1, j1);
                        float hug = exp(-h / (0.55 * De + 0.35 * max(d, 0.0)));
                        float k = Sm(-0.4 * De, 0.4 * De, d) * hug / (g * g) * wM;
                        sigma += s1 * k; j += j1 * k;
                    }
                }
                if (wM < 0.999)
                {
                    float3 et = tl > 1e-3 ? tIn / tl : normalize(cross(n, abs(n.y) < 0.9 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0)));
                    float3 eb = cross(n, et);
                    float3 u = q - float3(L, 0.0, 0.0) - h * n;               // the displacement from the impact point, along the plate
                    float ut = dot(u, et), ub = dot(u, eb);
                    float rho = sqrt(ut * ut + ub * ub);
                    float phi = atan2(ub, ut);
                    float g = 1.0 + g0 * rho;
                    float s2; float3 j2;
                    Field(L + rho, h / g, rho * phi * 0.5, s2, j2);
                    float k = Sm(0.0, 0.5 * De, rho + 0.5 * De) * (1.0 - wM) / (g * g);
                    sigma += s2 * k; j += j2 * k;
                }
            }

            struct FragOut { fixed4 col : SV_Target0; float4 aux : SV_Target1; };

            FragOut frag (v2f i)
            {
                FragOut fo; fo.aux = float4(0.0, 0.0, 0.0, 0.0);
                float3 O = _WorldSpaceCameraPos;
                // sub-pixel jitter of the ray, as in the smoke volumes: the temporal resolve accumulates detail between the frames
                float3 wpj = i.wpos + ddx(i.wpos) * _PlumeJitter.x + ddy(i.wpos) * _PlumeJitter.y;
                float3 D = normalize(wpj - O);

                // into the plume frame (x along the exhaust)
                float3 ax = _PlumeAxis.xyz;
                float3 rt = _PlumeRight.xyz;
                float3 sd = cross(ax, rt);
                float3 oRel = O - _PlumeFrame.xyz;
                float3 o = float3(dot(oRel, ax), dot(oRel, rt), dot(oRel, sd));
                float3 d = float3(dot(D, ax), dot(D, rt), dot(D, sd));
                float len = _PlumeFrame.w, rMax = _PlumeAxis.w;

                // ray vs the bounding cylinder
                float a = d.y * d.y + d.z * d.z;
                float bq = 2.0 * (o.y * d.y + o.z * d.z);
                float cq = o.y * o.y + o.z * o.z - rMax * rMax;
                float t0 = 0.0, t1 = 1e9;
                if (a < 1e-10) { if (cq > 0.0) { fo.col = fixed4(0, 0, 0, 0); return fo; } }
                else
                {
                    float disc = bq * bq - 4.0 * a * cq;
                    if (disc < 0.0) discard;
                    float sq = sqrt(disc);
                    t0 = (-bq - sq) / (2.0 * a); t1 = (-bq + sq) / (2.0 * a);
                }
                if (abs(d.x) > 1e-6)
                {
                    float ta = (0.0 - o.x) / d.x, tb = (len - o.x) / d.x;
                    if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
                    t0 = max(t0, ta); t1 = min(t1, tb);
                }
                else if (o.x < 0.0 || o.x > len) discard;
                t0 = max(t0, 0.0);

                // the scene in front of the plume hides it: clip the ray at the opaque depth
                float2 suv = i.scr.xy / i.scr.w;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, suv);
                float eye = LinearEyeDepth(raw);
                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                float tScene = raw > 0.00001 ? eye / max(dot(D, camFwd), 1e-4) : 1e9;
                t1 = min(t1, tScene);
                if (t1 <= t0) discard;

                float steps = _PlumeE.y;
                float De = _PlumeA.x;
                float ds = max(0.006 * De, (t1 - t0) / steps);
                float T = 1.0;
                float3 L = float3(0.0, 0.0, 0.0);
                float distAcc = 0.0, distW = 0.0;
                float tt = t0 + 0.5 * ds;
                // jitter against banding
                tt += (frac(sin(dot(i.pos.xy + _PlumeJitter.zw, float2(12.9898, 78.233))) * 43758.5453) - 0.5) * ds;
                [loop]
                for (int k = 0; k < 384; k++)
                {
                    if (tt >= t1 || T < 0.016) break;           // 4/255 of the light left: the rest cannot be seen
                    float3 p = o + d * tt;
                    float sg; float3 em;
                    FieldBent(p.x, p.y, p.z, sg, em);
                    float dsN = _PlumeG.w > 0.01 ? clamp(0.09 * (0.5 * De + 0.09 * max(p.x, 0.0)), 0.02 * De, 22.0) : ds;
                    float3 dL = em * (T * dsN);
                    float a = T * (1.0 - exp(-sg * dsN));
                    float wgt = a + dot(dL, float3(0.33, 0.33, 0.33)) * _PlumeD.w;      // how much this sample shows: opacity plus light
                    distAcc += tt * wgt; distW += wgt;
                    L += dL;
                    T *= exp(-sg * ds);
                    // empty space (the bounding cylinder is mostly air beside a thin column) is crossed in 3 times longer steps
                    float stepNow = ds;
                    if (_PlumeG.w > 0.01) stepNow = clamp(0.09 * (0.5 * De + 0.09 * max(p.x, 0.0)), 0.02 * De, 22.0);
                    tt += (sg <= 0.0 && dot(em, float3(1.0, 1.0, 1.0)) <= 0.0) ? 3.0 * stepNow : stepNow;
                }
                // tone compression keeps the hot core from clipping flat
                float3 Lc = 1.0 - exp(-L * _PlumeD.w);
                fo.aux = float4(distAcc, distW, 0.0, 0.0);
                fo.col = fixed4(Lc, 1.0 - T);
                return fo;
            }
            ENDCG
        }
    }
    FallBack Off
}
