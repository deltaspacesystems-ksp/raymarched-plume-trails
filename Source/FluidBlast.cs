using System;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // AN EXPLOSION AS A FLUID. A box of 64 x 128 x 64 cells around the blast is solved on the GPU (BlastFluid.compute): a sphere of hot gas is
    // injected, it expands, rises by its buoyancy, rolls up into vortices and breaks into billows - the cauliflower, the fire licking up
    // through the smoke, the rolling cap of a big blast all come out of the flow, nothing is modelled by hand. Drawn by PadFluid.shader (the
    // same medium as the pad cloud): steam (white vapour), soot (dark smoke) and temperature (the fire glows by a black-body ramp).
    // Pure Unity code (no game types): used by the mod (Explosions.cs) and by the offline preview in the Unity project.
    public class FluidBlast
    {
        public static ComputeShader Compute;
        public static Shader RenderShader;
        public const int NX = 64, NY = 128, NZ = 64;
        private const int PressureIters = 26;

        public struct Fuel
        {
            public float white;            // 0 = soot (kerosene, solid) ... 1 = white vapour (hydrogen, methane)
            public float fireLife, fireDens, smokeDens;
            public Color flame;
        }

        // hooks of the owner (the mod registers the renderer with the reduced-resolution smoke buffer; the preview draws it directly)
        public Action<MeshRenderer, MaterialPropertyBlock> OnRegister;
        public Action<MeshRenderer> OnRemove;
        public Action<MeshRenderer, bool> OnActive;     // the reduced-resolution smoke buffer draws only the renderers that are active: said every frame
        public bool DirectDraw;

        private static int kClear = -1, kBurst, kShock, kForces, kCurl, kVort, kAdvVel, kAdvFwd, kAdvBack, kDiv, kJac, kProj;
        private static Mesh cubeMesh;

        private RenderTexture[] vel = new RenderTexture[2], scl = new RenderTexture[2], prs = new RenderTexture[2];
        private RenderTexture tmp, curl, div;
        private int vc, sc, pc;

        private readonly Vector3 e1, up, e2; private Vector3 corner;
        private float cell; private readonly float refSmokeR, speed, cell0;
        // A BLAST IN A VACUUM: the cloud expands freely, so the grid EXPANDS WITH IT (the cell grows with a(t), the flow that is solved is the motion
        // relative to the expansion), the concentration falls as the volume grows, the gas cools as it expands; the burned gas is thrown out of the box in a
        // fraction of a second, the droplets stay and spread. No ground, no gravity, no buoyancy.
        private readonly bool vacuum;
        public System.Func<Vector3> VacuumCenter;      // where the middle of the cloud is now (it moves with the velocity of the rocket)
        private const float DropletSpeed = 90f;        // the speed at which the cloud of droplets spreads, m/s
        private float VacA(float tt) { return 1f + DropletSpeed * Mathf.Max(tt, 0f) / (0.8f * srcs[0].fireR); }
        private float t;                    // seconds of the blast (already slowed by the speed factor)
        // Every blast in the grid is a SOURCE with its own clock: the flows of the blasts that are close to each other push, merge and roll up together
        private class Src { public Vector3 pos; public float fireR, smokeR, fireLife, smokeLife, injectT, t0, seed; public Fuel fuel; public bool kicked; public float shockR, shockPrev; }
        private readonly System.Collections.Generic.List<Src> srcs = new System.Collections.Generic.List<Src>(8);
        private readonly Vector4[] bA = new Vector4[32], bB = new Vector4[32], bC = new Vector4[32];
        private readonly Vector4[] sA = new Vector4[8], sB = new Vector4[8], sC = new Vector4[8];
        private Vector4 ringPos; private float ringK;           // the visible condensation ring of the newest blast
        public const int MaxSources = 8;
        public int SourceCount { get { return srcs.Count; } }
        private GameObject go;
        private MeshRenderer mr;
        private Material mat;
        private MaterialPropertyBlock mb = new MaterialPropertyBlock();
        private Light light;
        private MaterialPropertyBlock lookBlock = new MaterialPropertyBlock();
        private Color lookSky = Color.white, lookGnd = Color.gray, lookShadow = Color.gray, lookSun = Color.white;
        private float lookStamp;
        public Vector3 windWorld;          // the wind that carries the blast (set by the owner)
        // A BLAST IN THE AIR (no ground within reach): the box is centred on the blast and has no floor, and it can FOLLOW the cloud: the cloud of a rocket
        // that blew up at 2 km/s does not stand where it started, its gas goes on at the speed of the vehicle and the air stops it (Follow = the displacement of the
        // centre of the cloud at the time t, from the drag of the air on it, see Explosions.Spawn)
        private readonly bool airborne;
        private Vector3 airV0; private float airK, airV0m; private bool bulkPending;       // see SetAirMotion
        private Vector3 corner0;
        // the position of the box is kept relative to the body (the owner gives its position): in raw world space the cloud stayed with the vessel whenever KSP
        // moved the world to follow it (Krakensbane, the floating origin), the blast of a rocket at speed went along with the rocket
        public System.Func<Vector3> BodyPos; private Vector3 cornerRel; private bool relInit;
        // the box follows the MEASURED centre of the smoke (the solver adds the pressure of the air that the cloud displaces to the drag that is put in, so the
        // cloud does not go exactly where a formula says; a box that kept to the formula cut the cloud off with its walls): the analytic velocity plus a correction
        private Vector3 corr; private ComputeBuffer momBuf; private Vector4[] momArr; private int kMom = -1; private float momStamp;
        // the speed of the vehicle (relative to the air) and the drag constant k (1/m): the cloud starts with this speed, the air stops it (dv/dt = - k v^2 for the
        // mean, per cell in the solver, lighter where it is thin), and the box follows the centre of the cloud: x(t) = ln(1 + k v0 t) / k
        public void SetAirMotion(Vector3 v0, float k) { airV0 = v0; airV0m = v0.magnitude; airK = Mathf.Max(k, 1e-5f); bulkPending = airborne && airV0m > 1f; }
        // the centre of the smoke, from the GPU (moments of every line, summed here), a few times a second: the box is steered so that it is in the middle of it
        private void MeasureCloud()
        {
            if (Time.unscaledTime < momStamp) return;
            momStamp = Time.unscaledTime + 0.12f;
            if (kMom < 0) kMom = Compute.FindKernel("Moments");
            if (momBuf == null) { momBuf = new ComputeBuffer(NY * NZ, 16); momArr = new Vector4[NY * NZ]; }
            Compute.SetInts("_Dim", NX, NY, NZ);
            Compute.SetTexture(kMom, "_SclIn", scl[sc]);
            Compute.SetBuffer(kMom, "_MomOut", momBuf);
            Compute.Dispatch(kMom, (NY + 7) / 8, (NZ + 7) / 8, 1);
            momBuf.GetData(momArr);
            double m = 0, mx = 0, my = 0, mz = 0;
            for (int i = 0; i < momArr.Length; i++) { m += momArr[i].x; mx += momArr[i].y; my += momArr[i].z; mz += momArr[i].w; }
            if (m < 5.0) { corr = Vector3.zero; return; }
            // the centre in the box, metres, against the middle of the box
            Vector3 c = new Vector3((float)(mx / m + 0.5) * cell, (float)(my / m + 0.5) * cell, (float)(mz / m + 0.5) * cell);
            Vector3 err = c - new Vector3(NX * cell * 0.5f, NY * cell * 0.5f, NZ * cell * 0.5f);
            Vector3 errW = e1 * err.x + up * err.y + e2 * err.z;
            corr = errW / 0.5f;                                      // the box catches up with the cloud in about half a second
        }

        private Vector3 FrameVelocity(float tt) { return (airborne && airV0m > 1f) ? airV0 / (1f + airK * airV0m * Mathf.Max(tt, 0f)) : Vector3.zero; }
        public bool Ready { get; private set; }
        public float Age { get { return t; } }

        private static RenderTexture Make(int x, int y, int z, RenderTextureFormat f)
        {
            RenderTexture rt = new RenderTexture(x, y, 0, f)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D, volumeDepth = z, enableRandomWrite = true,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        // world = the point on the ground under the blast, up = the vertical there; fireR / smokeR / the lives from Explosions.cs
        public FluidBlast(Vector3 world, Vector3 up, float fireR, float smokeR, float fireLife, float smokeLife, Fuel fuel, float seed, float speed, bool vacuum = false, bool airborne = false)
        {
            this.vacuum = vacuum; this.airborne = airborne && !vacuum;
            this.up = up.normalized;
            Vector3 north = Vector3.ProjectOnPlane(Vector3.forward, this.up);
            if (north.sqrMagnitude < 1e-3f) north = Vector3.ProjectOnPlane(Vector3.right, this.up);
            e2 = north.normalized; e1 = Vector3.Cross(this.up, e2).normalized;
            this.speed = Mathf.Clamp(speed, 0.15f, 2f);
            refSmokeR = smokeR;
            // the grid is sized for the FIRST blast, with room: blasts that come later and close are added to it (TryAdd)
            cell = vacuum ? Mathf.Max(0.8f * fireR / 14f, 0.15f) : Mathf.Clamp(3.4f * smokeR / NX, 0.8f, 6f);
            cell0 = cell;
            bool centred = vacuum || this.airborne;
            corner = centred ? world - (e1 * (NX * cell * 0.5f) + this.up * (NY * cell * 0.5f) + e2 * (NZ * cell * 0.5f)) : world - e1 * (NX * cell * 0.5f) - e2 * (NZ * cell * 0.5f);
            corner0 = corner;
            srcs.Add(new Src { pos = new Vector3(NX * cell * 0.5f, centred ? NY * cell * 0.5f : 0f, NZ * cell * 0.5f), fireR = fireR, smokeR = smokeR, fireLife = fireLife, smokeLife = smokeLife, injectT = vacuum ? 0.25f : 0.8f * fireLife, t0 = 0f, seed = seed, fuel = fuel });

            if (Compute == null || RenderShader == null || !SystemInfo.supportsComputeShaders) return;
            for (int i = 0; i < 2; i++)
            {
                vel[i] = Make(NX, NY, NZ, RenderTextureFormat.ARGBHalf);
                scl[i] = Make(NX, NY, NZ, RenderTextureFormat.ARGBHalf);
                prs[i] = Make(NX, NY, NZ, RenderTextureFormat.RFloat);
            }
            tmp = Make(NX, NY, NZ, RenderTextureFormat.ARGBHalf);
            curl = Make(NX, NY, NZ, RenderTextureFormat.ARGBHalf);
            div = Make(NX, NY, NZ, RenderTextureFormat.RFloat);
            if (kClear < 0)
            {
                kClear = Compute.FindKernel("Clear"); kBurst = Compute.FindKernel("Burst"); kShock = Compute.FindKernel("Shock"); kForces = Compute.FindKernel("Forces");
                kCurl = Compute.FindKernel("Curl"); kVort = Compute.FindKernel("Vorticity"); kAdvVel = Compute.FindKernel("AdvectVelocity");
                kAdvFwd = Compute.FindKernel("AdvectScalarFwd"); kAdvBack = Compute.FindKernel("AdvectScalarBack");
                kDiv = Compute.FindKernel("Divergence"); kJac = Compute.FindKernel("Jacobi"); kProj = Compute.FindKernel("Project");
            }
            Compute.SetInts("_Dim", NX, NY, NZ);
            for (int i = 0; i < 2; i++)
            {
                Compute.SetTexture(kClear, "_VelOut", vel[i]); Compute.SetTexture(kClear, "_SclOut", scl[i]); Compute.SetTexture(kClear, "_PrsOut", prs[i]);
                Dispatch(kClear);
            }

            go = new GameObject("PlumeTrails.FluidExplosion");
            if (cubeMesh == null)
            {
                GameObject tg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cubeMesh = tg.GetComponent<MeshFilter>().sharedMesh;
                Kill(tg);
            }
            go.AddComponent<MeshFilter>().sharedMesh = cubeMesh;
            mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mat = new Material(RenderShader) { renderQueue = 3006 };
            mr.sharedMaterial = mat;
            mr.enabled = DirectDraw;
            GameObject lg = new GameObject("flash");
            lg.transform.SetParent(go.transform, false);
            light = lg.AddComponent<Light>();
            light.type = LightType.Point; light.shadows = LightShadows.None;
            light.color = Color.Lerp(new Color(1f, 0.72f, 0.3f), fuel.flame, 0.7f);
            Ready = true;
        }

        // Adds another blast to this grid when it is close enough (inside the middle part of the box, not much bigger than the first one). The
        // flows then interact. False = it does not fit: the owner makes a grid of its own.
        public bool TryAdd(Vector3 world, float fireR, float smokeR, float fireLife, float smokeLife, Fuel fuel, float seed)
        {
            if (!Ready || vacuum || airborne || srcs.Count >= MaxSources) return false;
            // Only a blast that follows at once joins (the parts of one rocket going off together: their flows have to push each other). A later one is its own
            // grid: in the game a blast added to an older, grown grid did not show its fire (the light, the smoke and the debris did), and one that was
            // far enough for a grid of its own did.
            if (t > 1.5f) return false;
            Vector3 d = world - corner;
            Vector3 l = new Vector3(Vector3.Dot(d, e1), Vector3.Dot(d, up), Vector3.Dot(d, e2));
            float ext = NX * cell;
            if (l.x < 0.22f * ext || l.x > 0.78f * ext || l.z < 0.22f * ext || l.z > 0.78f * ext) return false;
            if (l.y < -6f || l.y > 0.15f * ext) return false;
            if (smokeR > 1.6f * refSmokeR) return false;                       // much bigger than the grid was made for: it would be a coarse blob
            // the grid still has to be alive for the whole life of the new blast
            srcs.Add(new Src { pos = new Vector3(l.x, Mathf.Max(l.y, 0f), l.z), fireR = fireR, smokeR = smokeR, fireLife = fireLife, smokeLife = smokeLife, injectT = 0.8f * fireLife, t0 = t, seed = seed, fuel = fuel });
            return true;
        }

        private void Dispatch(int kernel) { Compute.Dispatch(kernel, (NX + 3) / 4, (NY + 3) / 4, (NZ + 3) / 4); }
        private static void SetTex(int k, string name, Texture tx) { Compute.SetTexture(k, name, tx); }

        public void Register()
        {
            if (OnRegister != null && mr != null) OnRegister(mr, mb);
        }

        public void Dispose()
        {
            if (mr != null && OnActive != null) OnActive(mr, false);
            if (mr != null && OnRemove != null) OnRemove(mr);
            for (int i = 0; i < 2; i++)
            {
                if (vel[i] != null) { vel[i].Release(); Kill(vel[i]); vel[i] = null; }
                if (scl[i] != null) { scl[i].Release(); Kill(scl[i]); scl[i] = null; }
                if (prs[i] != null) { prs[i].Release(); Kill(prs[i]); prs[i] = null; }
            }
            if (tmp != null) { tmp.Release(); Kill(tmp); tmp = null; }
            if (curl != null) { curl.Release(); Kill(curl); curl = null; }
            if (div != null) { div.Release(); Kill(div); div = null; }
            if (momBuf != null) { momBuf.Release(); momBuf = null; }
            if (go != null) Kill(go);
            if (mat != null) Kill(mat);
            go = null; mr = null; mat = null; Ready = false;
        }

        private static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        private static void Kill(UnityEngine.Object o) { if (o == null) return; if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o); }

        // one solver step of dt seconds
        private void Step(float dt)
        {
            float tNow = t;
            float aOld = 1f, aNew = 1f;
            if (vacuum)
            {
                aOld = VacA(tNow); aNew = VacA(tNow + dt);
                cell = cell0 * aNew;
                Compute.SetFloat("_Vacuum", 1f);
                // the dilution: the concentration of the droplets falls with the column density (a^-2: the volume a^3 grows, the path a), the temperature
                // with the adiabatic cooling of the expanding gas (T ~ a^-0.9), the peculiar velocity with the momentum (a^-1)
                Compute.SetVector("_Dilute", new Vector4(Mathf.Pow(aOld / aNew, 0.9f), Mathf.Pow(aOld / aNew, 0.9f), aOld / aNew, 0f));
            }
            else { Compute.SetFloat("_Vacuum", airborne ? 1f : 0f); Compute.SetVector("_Dilute", new Vector4(1f, 1f, 1f, 0f)); }          // in the air: no floor
            Compute.SetFloat("_Cell", cell);
            Compute.SetFloat("_Dt", dt);
            Vector3 boxVel = FrameVelocity(tNow);
            if (airborne && airV0m > 1f) { boxVel += corr; corner += boxVel * dt; cornerRel += boxVel * dt; }
            Vector3 windRel = windWorld - boxVel;
            Compute.SetVector("_Wind", new Vector4(Vector3.Dot(windRel, e1), Vector3.Dot(windRel, up), Vector3.Dot(windRel, e2), 0f));
            Compute.SetFloat("_DragK", airborne && airV0m > 1f ? airK : 0f);
            Compute.SetVector("_Bulk", new Vector4(Vector3.Dot(airV0, e1), Vector3.Dot(airV0, up), Vector3.Dot(airV0, e2), 0f));
            Compute.SetFloat("_BulkOn", bulkPending ? 1f : 0f);
            bulkPending = false;
            // the sources that are still fed (up to MaxSources), the newest first when there are more
            int nb = 0; float endT = 0f, newestFireLife = 1f, newestStart = -1f, maxLife = 1f;
            for (int i = 0; i < srcs.Count; i++)
            {
                Src sc0 = srcs[i];
                endT = Mathf.Max(endT, sc0.t0 + sc0.smokeLife); maxLife = Mathf.Max(maxLife, sc0.smokeLife);
                if (sc0.t0 >= newestStart) { newestStart = sc0.t0; newestFireLife = sc0.fuel.fireLife; }
                float ts = tNow - sc0.t0;
                float inj = ts < sc0.injectT ? Mathf.Pow(1f - ts / sc0.injectT, 1.4f) : 0f;
                if (inj <= 0f && sc0.kicked) continue;
                if (nb + 5 > bA.Length) break;
                float white = sc0.fuel.white;
                float amount = 4.0f * sc0.fuel.smokeDens;                // vapour / soot made per second inside the source (units of the picture's density)
                if (vacuum)
                {
                    // the burned gas leaves at a thousand metres per second (here the grid is the frame of the droplets: 700 m/s relative to it), lumpy
                    amount = 70f * sc0.fuel.smokeDens;
                    bA[nb] = new Vector4(NX * cell * 0.5f, NY * cell * 0.5f, NZ * cell * 0.5f, 0.55f * sc0.fireR);
                    bB[nb] = new Vector4(sc0.kicked ? 0f : 700f, amount * white + 0.1f * amount * (1f - white), amount * (1f - white), 0f);
                }
                else
                {
                // A fireball is not a sphere: fuel is thrown out of the tank in clouds and every one of them lights. A main ball and four lumps of other sizes
                // (placed by a hash of the seed, the same every frame) make an irregular body, a ball with bulges, that then boils up as one.
                float R0 = 0.62f * sc0.fireR;
                float[] fr = { 0.72f, 0.62f, 0.56f, 0.50f, 0.46f };
                for (int sb = 0; sb < fr.Length; sb++)
                {
                    float h1 = Mathf.Repeat(Mathf.Sin(sc0.seed * 12.9898f + sb * 78.233f) * 43758.5453f, 1f);
                    float h2 = Mathf.Repeat(Mathf.Sin(sc0.seed * 39.3468f + sb * 11.135f) * 24634.6345f, 1f);
                    float h3 = Mathf.Repeat(Mathf.Sin(sc0.seed * 17.1234f + sb * 51.791f) * 91823.1234f, 1f);
                    float rs = fr[sb] * R0;
                    Vector3 off = sb == 0 ? Vector3.zero : new Vector3(Mathf.Cos(6.2832f * h1), 0f, Mathf.Sin(6.2832f * h1)) * (0.95f * R0 * (0.35f + 0.65f * h2));
                    float cy = sb == 0 ? 0.46f * sc0.fireR : Mathf.Max(0.55f * rs, 0.12f * sc0.fireR) + 0.42f * sc0.fireR * h3 * h3;
                    if (airborne) cy = sc0.pos.y - 0.5f * sc0.fireR + (sb == 0 ? 0f : (h3 - 0.35f) * 0.7f * sc0.fireR) + 0.5f * sc0.fireR;         // round the centre of the blast, not on a floor
                    if (nb >= bA.Length) break;
                    bA[nb] = new Vector4(sc0.pos.x + off.x, cy, sc0.pos.z + off.z, rs);
                    bB[nb] = new Vector4(sc0.kicked ? 0f : 0.35f * sc0.fireR, amount * white + 0.15f * amount * (1f - white), amount * (1f - white), 0.35f);
                    bC[nb] = new Vector4(1.15f * sc0.fuel.fireDens, sc0.seed + sb * 3.7f, 1.0f, inj);
                    nb++;
                }
                }
                if (vacuum) { bC[nb] = new Vector4(1.15f * sc0.fuel.fireDens, sc0.seed, 1.0f, inj); nb++; }
                sc0.kicked = true;
            }
            // the SHOCK WAVES of the blasts: the front runs out fast and slows down, the induced velocity falls with the time and the size of the blast
            int ns = 0; ringK = 0f; float newestT0 = -1f;
            float shockK = SmokeTuning.ExplosionShock;
            for (int i = 0; i < srcs.Count && shockK > 0.01f && !vacuum; i++)
            {
                Src q = srcs[i];
                float ts = tNow - q.t0;
                float amp = 16f * Mathf.Pow(q.fireR / 37f, 0.6f) * Mathf.Exp(-ts / 1.1f) * shockK;
                if (amp < 0.25f) continue;
                q.shockPrev = ts <= dt * 1.01f ? 0.8f * q.fireR : q.shockR;
                q.shockR = 0.8f * q.fireR + 3.2f * q.smokeR * (1f - Mathf.Exp(-ts / 0.9f));
                if (ns < MaxSources)
                {
                    sA[ns] = new Vector4(q.pos.x, (airborne ? q.pos.y : 0f) + 0.3f * q.fireR, q.pos.z, q.shockR);
                    sB[ns] = new Vector4(q.shockPrev, amp, 0.25f, 0f);
                    sC[ns] = new Vector4(0.45f * Mathf.Clamp01(1.2f - q.shockR / (0.42f * NX * cell)), 0f, 0f, 0f);       // no dust where the front leaves the box (a dirty square ring at its edge)
                    ns++;
                }
                if (q.t0 >= newestT0) { newestT0 = q.t0; ringPos = new Vector4(q.pos.x, (airborne ? q.pos.y : 0f) + 0.3f * q.fireR, q.pos.z, q.shockR); ringK = Mathf.Clamp01(amp / 7f) * Mathf.Clamp01(1f - ts / 2.2f); }
            }
            if (ns > 0)
            {
                Compute.SetInt("_ShockCount", ns);
                Compute.SetVectorArray("_ShockA", sA); Compute.SetVectorArray("_ShockB", sB); Compute.SetVectorArray("_ShockC", sC);
                SetTex(kShock, "_VelIn", vel[vc]); SetTex(kShock, "_SclIn", scl[sc]); SetTex(kShock, "_VelOut", vel[1 - vc]); SetTex(kShock, "_SclOut", scl[1 - sc]);
                Dispatch(kShock); vc = 1 - vc; sc = 1 - sc;
            }
            Compute.SetInt("_BurstCount", nb);
            Compute.SetVectorArray("_BurstA", bA); Compute.SetVectorArray("_BurstB", bB); Compute.SetVectorArray("_BurstC", bC);
            // buoyancy of the hot gas, weight of the smoke, drag, vorticity confinement (the billows)
            Compute.SetVector("_Params", vacuum ? new Vector4(0f, 0f, 0f, 0.6f) : new Vector4(9f, 0.05f, 0.012f, 2.2f));
            float fade = Smooth((tNow - (endT - 0.28f * maxLife)) / (0.28f * maxLife));      // the cloud thins out at the end of its life
            float cool = 0.24f / Mathf.Max(newestFireLife, 0.4f);                            // hydrogen and methane burn longer
            Compute.SetVector("_Params2", vacuum ? new Vector4(0.02f * fade * 10f, 0.02f * fade * 10f, 2.2f, 0f) : new Vector4(0.010f + 0.25f * fade, 0.008f + 0.25f * fade, cool, 1.7f));
            // injection (and the kick, once)
            SetTex(kBurst, "_VelIn", vel[vc]); SetTex(kBurst, "_SclIn", scl[sc]); SetTex(kBurst, "_VelOut", vel[1 - vc]); SetTex(kBurst, "_SclOut", scl[1 - sc]);
            Dispatch(kBurst); vc = 1 - vc; sc = 1 - sc;
            SetTex(kForces, "_VelIn", vel[vc]); SetTex(kForces, "_SclIn", scl[sc]); SetTex(kForces, "_VelOut", vel[1 - vc]);
            Dispatch(kForces); vc = 1 - vc;
            SetTex(kCurl, "_VelIn", vel[vc]); SetTex(kCurl, "_CurlOut", curl);
            Dispatch(kCurl);
            SetTex(kVort, "_VelIn", vel[vc]); SetTex(kVort, "_CurlIn", curl); SetTex(kVort, "_VelOut", vel[1 - vc]);
            Dispatch(kVort); vc = 1 - vc;
            SetTex(kAdvVel, "_VelIn", vel[vc]); SetTex(kAdvVel, "_VelOut", vel[1 - vc]);
            Dispatch(kAdvVel); vc = 1 - vc;
            SetTex(kDiv, "_VelIn", vel[vc]); SetTex(kDiv, "_DivOut", div); SetTex(kDiv, "_ExpScl", scl[sc]);
            Dispatch(kDiv);
            SetTex(kJac, "_DivIn", div);
            for (int it = 0; it < PressureIters; it++)
            {
                SetTex(kJac, "_PrsIn", prs[pc]); SetTex(kJac, "_PrsOut", prs[1 - pc]);
                Dispatch(kJac); pc = 1 - pc;
            }
            SetTex(kProj, "_VelIn", vel[vc]); SetTex(kProj, "_PrsIn", prs[pc]); SetTex(kProj, "_VelOut", vel[1 - vc]);
            Dispatch(kProj); vc = 1 - vc;
            SetTex(kAdvFwd, "_VelIn", vel[vc]); SetTex(kAdvFwd, "_SclIn", scl[sc]); SetTex(kAdvFwd, "_TmpOut", tmp);
            Dispatch(kAdvFwd);
            SetTex(kAdvBack, "_VelIn", vel[vc]); SetTex(kAdvBack, "_SclIn", scl[sc]); SetTex(kAdvBack, "_TmpIn", tmp); SetTex(kAdvBack, "_SclOut", scl[1 - sc]);
            Dispatch(kAdvBack); sc = 1 - sc;
        }

        // Advances the blast by `dt` seconds of real time (slowed by the speed factor) and updates what is drawn. False when the blast is over.
        public bool Update(float dtReal)
        {
            if (!Ready) return false;
            float simDt = Mathf.Min(dtReal, 0.1f) * speed;
            int subs = Mathf.Clamp(Mathf.CeilToInt(simDt / 0.035f), 1, 3);
            float dt = simDt / subs;
            for (int i = 0; i < subs; i++) { Step(dt); t += dt; }
            // the grid lives until the last of its blasts is over; the newest one sets the light, the mix of fuels the colour of the smoke
            float endT = 0f; Src newest = srcs[0];
            float wSum = 0f, whiteSum = 0f, fdSum = 0f;
            for (int i = 0; i < srcs.Count; i++)
            {
                Src q = srcs[i];
                endT = Mathf.Max(endT, q.t0 + q.smokeLife);
                if (q.t0 >= newest.t0) newest = q;
                float alive = Mathf.Clamp01(1f - (t - q.t0) / q.smokeLife);
                float wq = q.fireR * q.fireR * q.fireR * alive + 1e-3f;
                wSum += wq; whiteSum += wq * q.fuel.white; fdSum += wq * q.fuel.fireDens;
            }
            if (t >= endT) return false;
            float fireR = newest.fireR; Fuel fuel = newest.fuel; float seed = newest.seed;
            float white = whiteSum / wSum;
            float tl = t - newest.t0;

            if (vacuum && VacuumCenter != null)
            {
                Vector3 vcn = VacuumCenter();
                corner = vcn - (e1 * (NX * cell * 0.5f) + up * (NY * cell * 0.5f) + e2 * (NZ * cell * 0.5f));
            }
            if (airborne && airV0m > 1f) MeasureCloud();
            if (BodyPos != null) { Vector3 bp = BodyPos(); if (!relInit) { cornerRel = corner - bp; relInit = true; } corner = bp + cornerRel; }
            go.SetActive(true);
            mr.enabled = DirectDraw;
            go.transform.position = corner + e1 * (NX * cell * 0.5f) + up * (NY * cell * 0.5f) + e2 * (NZ * cell * 0.5f);
            go.transform.rotation = Quaternion.LookRotation(e2, up);
            go.transform.localScale = new Vector3(NX * cell, NY * cell, NZ * cell);
            // the proxy cube must also cover the front of the shock wave, which goes on far past the walls of the grid: a cube centred on the source that holds both
            if (ringK > 0.01f)
            {
                Vector3 boxC = go.transform.position;
                Vector3 srcW = corner + e1 * ringPos.x + up * ringPos.y + e2 * ringPos.z;
                float half = 0.5f * new Vector3(NX, NY, NZ).magnitude * cell;
                float cover = Mathf.Max(ringPos.w + 5f * Mathf.Max(3f, cell * 1.4f), (boxC - srcW).magnitude + half);
                go.transform.position = srcW;
                go.transform.localScale = Vector3.one * (2f * cover);
            }

            // the light of the fireball
            float fl = Mathf.Exp(-tl / (0.9f * fuel.fireLife * Mathf.Max(0.6f, newest.fireLife / 7f)));
            Vector3 firePos = vacuum ? corner + e1 * (NX * cell * 0.5f) + up * (NY * cell * 0.5f) + e2 * (NZ * cell * 0.5f) : corner + e1 * newest.pos.x + e2 * newest.pos.z + up * ((airborne ? newest.pos.y : 0f) + 0.7f * fireR);
            float fireA = Mathf.Clamp01(fl) * SmokeTuning.ExplosionFire * 1.2f;
            light.transform.position = firePos;
            light.range = 8f * fireR + 60f;
            light.intensity = 6f * Mathf.Exp(-tl / 0.45f) * SmokeTuning.ExplosionFire + 2.5f * fl;
            light.enabled = light.intensity > 0.02f;

            if (Time.time >= lookStamp)
            {
                lookStamp = Time.time + 0.5f;
                SmokeTuning.Apply(lookBlock);
                ScattererSun.ApplyAt(lookBlock, corner);
                lookSky = lookBlock.GetColor("_AmbientSkyColor"); lookGnd = lookBlock.GetColor("_AmbientGroundColor");
                lookShadow = lookBlock.GetColor("_ShadowColor"); lookSun = lookBlock.GetColor("_SunlitColor");
            }
            mb.SetTexture("_FluidScl", scl[sc]);
            mb.SetVector("_FluidAnchor", corner);
            mb.SetVector("_FluidE1", e1); mb.SetVector("_FluidUp", up); mb.SetVector("_FluidE2", e2);
            mb.SetVector("_FluidDim", new Vector4(NX, NY, NZ, cell));
            mb.SetVector("_FluidOY", Vector4.zero);
            mb.SetVector("_FluidLight", new Vector4(firePos.x, firePos.y, firePos.z, 1.2f * fireR + 30f));
            Color fc = fuel.flame;
            mb.SetVector("_FluidLightCol", new Vector4(fc.r * fireA, fc.g * fireA, fc.b * fireA, 0f));
            // density: the blast is a much thicker medium than the pad steam
            mb.SetVector("_FluidLook", new Vector4(1.5f * SmokeTuning.PadFluidDensity / 1.4f, SmokeTuning.ExplosionFluidErosion, 110f, SmokeTuning.PadFluidExposure));
            mb.SetVector("_FluidLook2", new Vector4(SmokeTuning.ExplosionFire * 1.7f, 0.18f + 0.6f * (1f - white), SmokeTuning.PadFluidSun, SmokeTuning.PadFluidAmbient));
            mb.SetColor("_FluidAmbSky", lookSky); mb.SetColor("_FluidAmbGnd", lookGnd);
            mb.SetColor("_FluidShadowC", lookShadow); mb.SetColor("_FluidSunC", lookSun);
            mb.SetVector("_FluidLook4", new Vector4(SmokeTuning.Washout, SmokeTuning.SteamWhite, SmokeTuning.SkyTintStrength, SmokeTuning.ShadowStrength));
            mb.SetVector("_FluidLook3", new Vector4(SmokeTuning.AmbientFloor, SmokeTuning.SteamBright, SmokeTuning.SunlitBrightness, SmokeTuning.NightAmbient));
            // the ring of vapour that condenses in the rarefaction behind the shock (PadFluid.shader draws it): white, ragged, racing out
            mb.SetVector("_FluidShock", ringK > 0.01f ? ringPos : Vector4.zero);
            mb.SetVector("_FluidShock2", new Vector4(ringK > 0.01f ? ringK * 1.6f : 0f, Mathf.Max(3f, cell * 1.4f), 0f, 0f));
            mb.SetVector("_FluidSeed", new Vector4(0.6f * t + seed, -1.4f * t, 0.4f * t + 2f * seed, t));
            mr.SetPropertyBlock(mb);
            if (OnActive != null) OnActive(mr, true);
            return true;
        }
    }
}
