using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RaymarchedPlumeTrails
{
    // PAD FLUID. The cloud and the deflected flame at a launch pad as ONE simulated medium, on the GPU (PadFluid.compute): stable
    // fluids on a grid around the pad. What decides the shape is the pad itself - rays down every column of the grid give the
    // height of the surface (floor, trench, deflector, walls), the cells below are solid - so the jet that arrives from the engines
    // is stopped by the floor, turned by the deflector, confined by the trench walls, leaves through its open ends, and rolls up
    // into billows by itself. Nothing is placed by hand. Fire (hot gas, glows and lights the cloud), steam and soot are fields of
    // the same flow. PadFluid.shader draws the grid (eroded by noise, sun self-shadow, fire light).
    //
    // The grid is anchored to a spot on the planet (latitude / longitude / altitude) and rebuilt every frame from it, so the
    // floating origin and the planet's rotation do not tear it. The engines report through PadFluid.Report (the same place the
    // old pad cloud was fed from).
#if !EXPLOSIONS_ONLY
    [KSPAddon(KSPAddon.Startup.Flight, false)]
#endif
    public class PadFluid : MonoBehaviour
    {
        private class Src
        {
            public Vector3 nozzle, impact, up;
            public float thrKN, jetR, water, light, lightRange, hot, soot, standoff, t;
            public Color flame;
        }

        private static bool broken;     // switched off for the session after repeated errors: the old pad cloud takes over
        private static readonly Dictionary<int, Src> sources = new Dictionary<int, Src>();
        private static float lastReport = -999f;
        private static PadFluid instance;

        // SURFACE MODE (no solver): the grid, the height map and the flame hole are kept, and the puffs of the smoke volumes are made to
        // collide with that surface (see SurfaceMarch) - so the pad smoke and the root of the trail go down the trench and along the
        // deflector like the old system's puffs did, with the same look as the trail. Works with the GPU cloud off.
        public static bool SurfaceWanted { get { return !broken && SmokeTuning.PadSurface; } }
        public static bool Wanted { get { return Usable || SurfaceWanted; } }
        public static bool SurfaceReady { get { return SurfaceWanted && instance != null && instance.hasGrid && instance.heightReady; } }
        public static bool SurfaceActive { get { return SurfaceReady && !Drawing; } }

        public static bool Usable
        {
            get { return !broken && SmokeTuning.PadFluid && SystemInfo.supportsComputeShaders && ShaderCache.PadFluidCompute != null && ShaderCache.PadFluidShader != null; }
        }

        // true while a pad fluid is being fed (the puffs that reach the pad are then dropped)
        // true only while the cloud is really being drawn: the pad puffs are dropped in its favour, so it must not claim the pad during the
        // first second (heights not baked yet) or if the solver never gets going
        public static bool Drawing { get { return Usable && Time.time - lastDraw < 0.6f; } }
        public static bool Active { get { return Drawing && Time.time - lastReport < 0.6f; } }
        private static float lastDraw = -999f;

        // The hand-over from the pad cloud to the trail. While the fluid draws the pad, the smoke of the trail near the ground would be
        // drawn twice (the fluid makes its steam in the lowest ~20-45 m above the surface), so the trail's density grows from 35% at the pad
        // to 100% once the nozzle is ~120 m above it. Smooth in height, and 1 whenever the fluid is not drawing.
        public static float TrailHandover(Vector3 nozzle, Vector3 up)
        {
            if (!Drawing || instance == null || !instance.hasGrid) return 1f;
            float h = Vector3.Dot(nozzle - instance.corner, up.sqrMagnitude > 0.5f ? up.normalized : instance.upv) - instance.lastSurfaceY;
            float t = Mathf.Clamp01(h / 120f);
            return Mathf.Lerp(0.30f, 1f, t * t * (3f - 2f * t));
        }
        // the ignition wave: its front (for the solver, advanced per step; for the picture, per frame), the strength, the reference thrust
        private float lookStamp, nextStatus;
        private float surfStatT; private int surfStatCount; private string surfText = "";
        private string dbgPath = "none yet";      // which way through LateUpdateBody the last frame went (for the debug line)
        private MaterialPropertyBlock lookBlock;
        private Color lookSky = Color.white, lookGnd = Color.gray, lookShadow = Color.gray, lookSun = Color.white;
        private bool shockOn;
        private float shockR, shockPrev, shockRVis, shockAmp, thrRef;
        private Vector3 shockC;
        private const float SoundSpeed = 340f, ShockMaxR = 230f;
        private float nextPrewarmCheck, warmSteps, nextOnPadCheck;
        private bool everInjected, onPad;
        private static readonly RaycastHit[] pwHits = new RaycastHit[16];
        private float lastSurfaceY;       // world height (above the grid corner along up) of the surface under the first jet

        public static void Report(int key, Vector3 nozzle, Vector3 impact, Vector3 up, float thrKN, float jetR, float water, Color flame,
                                  float light, float lightRange, float hot, float soot, float standoff)
        {
            if (!Wanted) return;
            bool unusedCompat = ModCompat.GroundBlastFxPresent;      // logs the hint once when GroundBlastFx is installed
            Src s;
            if (!sources.TryGetValue(key, out s)) { s = new Src(); sources[key] = s; }
            s.nozzle = nozzle; s.impact = impact; s.up = up; s.thrKN = thrKN; s.jetR = jetR; s.water = water; s.flame = flame;
            s.light = light; s.lightRange = lightRange; s.hot = hot; s.soot = soot; s.standoff = standoff; s.t = Time.time;
            if (Time.time - lastReport > 5f)
                Debug.Log("[PlumeTrails] PadFluid: engines report an impact " + standoff.ToString("F1") + " m below the nozzle, thrust " + thrKN.ToString("F0") + " kN, jet radius " + jetR.ToString("F1") + " m");
            lastReport = Time.time;
        }

        // ------------------------------------------------------------------ grid state
        private bool hasGrid;
        private CelestialBody body;
        private double aLat, aLon, aAlt;
        private int nx, ny, nz;
        private float cell, oy;
        private RenderTexture[] vel = new RenderTexture[2], scl = new RenderTexture[2], prs = new RenderTexture[2];
        private RenderTexture tmp, curl, div;
        private int vc, sc, pc;
        private Texture2D height;
        private RenderTexture heightRT;          // heights of the rendered scenery (an orthographic camera looking down)
        private Camera hcam;
        private float nextRebake;
        private int rebakes;
        private Color[] hCols;
        private float[] lowCols;
        private readonly Dictionary<string, int> rayLayers = new Dictionary<string, int>();
        private float[] visH;                     // heights read back from the rendered-scenery map (local y), for the debug view
        private GameObject dbgGo;
        private Mesh dbgMesh;
        private LineRenderer[] dbgLines;
        private string dbgText = "";                  // the LOWEST surface under each column (the floor of the trench under a mount), for the flame hole
        private int bakeIdx;
        private bool heightReady;
        private Vector3 e1, upv, e2, corner;
        private float simAcc, lastActive;
        private GameObject go;
        private MeshRenderer mr;
        private Material mat;
        private readonly MaterialPropertyBlock mb = new MaterialPropertyBlock();
        private static Mesh cubeMesh;
        private readonly List<Src> active = new List<Src>(8);
        private int holeCount;
        private readonly Vector4[] holeA = new Vector4[8], holeB = new Vector4[8];
        private readonly Vector4[] srcA = new Vector4[8], srcB = new Vector4[8], srcC = new Vector4[8], srcD = new Vector4[8], srcE = new Vector4[8];

        private int kShock;
        private int kClear, kInject, kForces, kCurl, kVort, kAdvVel, kAdvFwd, kAdvBack, kDiv, kJac, kProj;
        private bool kernelsReady;

        private float heightTop = 60f;             // m above the impact plane where the height rays / the height camera start: the engine nozzle
        private const float SimDt = 0.05f;
        private const int PressureIters = 44;

        private void Awake() { instance = this; }

        private void OnDestroy()
        {
            Free();
            if (instance == this) instance = null;
            sources.Clear();
        }

        // ------------------------------------------------------------------ resources
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

        private void Free()
        {
            for (int i = 0; i < 2; i++)
            {
                if (vel[i] != null) vel[i].Release(); vel[i] = null;
                if (scl[i] != null) scl[i].Release(); scl[i] = null;
                if (prs[i] != null) prs[i].Release(); prs[i] = null;
            }
            if (tmp != null) tmp.Release(); tmp = null;
            if (curl != null) curl.Release(); curl = null;
            if (div != null) div.Release(); div = null;
            if (height != null) Destroy(height); height = null;
            if (heightRT != null) { heightRT.Release(); Destroy(heightRT); } heightRT = null;
            if (hcam != null) Destroy(hcam.gameObject); hcam = null;
            if (heightCb != null) { heightCb.Release(); heightCb = null; }
            if (heightMat != null) { Destroy(heightMat); heightMat = null; }
            if (mr != null) SmokeRenderRegistry.Remove(mr);
            if (dbgGo != null) Destroy(dbgGo); dbgGo = null; dbgMesh = null;
            if (go != null) Destroy(go); go = null;
            if (mat != null) Destroy(mat); mat = null;
            hasGrid = false; heightReady = false;
            padVox = null; voxTried = false;
        }

        private void CreateGrid(Src s)
        {
            Free();
            body = FlightGlobals.currentMainBody;
            if (body == null) return;
            Vector3d w = (Vector3d)s.impact;
            aLat = body.GetLatitude(w); aLon = body.GetLongitude(w); aAlt = body.GetAltitude(w);
            cell = Mathf.Clamp(SmokeTuning.PadFluidCell, 1.5f, 8f);
            // a 300 m wide, 160 m tall box around the pad (the trench is ~150 m long each way): half the width of the old one, so the same
            // number of cells gives twice the resolution
            nx = nz = Mathf.Clamp(Mathf.RoundToInt(300f / cell / 4f) * 4, 64, 176);
            ny = Mathf.Clamp(Mathf.RoundToInt(160f / cell / 4f) * 4, 32, 96);
            oy = -40f;
            // The rays (and the height camera) start 30 m above the engine nozzle: the nozzle of a big rocket sits INSIDE the trench, below the edge
            // of the deck, and a start at the nozzle would be below the deck's top and never see it (the camera looks down; what is behind it is gone).
            heightTop = Mathf.Clamp(Vector3.Dot(s.nozzle - s.impact, s.up.sqrMagnitude > 0.5f ? s.up.normalized : Vector3.up) + 30f, 30f, oy + ny * cell - 6f);
            bool solver = Usable;
            for (int i = 0; solver && i < 2; i++)
            {
                vel[i] = Make(nx, ny, nz, RenderTextureFormat.ARGBHalf);
                scl[i] = Make(nx, ny, nz, RenderTextureFormat.ARGBHalf);
                prs[i] = Make(nx, ny, nz, RenderTextureFormat.RFloat);
            }
            if (solver)
            {
                tmp = Make(nx, ny, nz, RenderTextureFormat.ARGBHalf);
                curl = Make(nx, ny, nz, RenderTextureFormat.ARGBHalf);
                div = Make(nx, ny, nz, RenderTextureFormat.RFloat);
            }
            height = new Texture2D(nx, nz, TextureFormat.RFloat, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            heightRT = new RenderTexture(nx, nz, 24, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, antiAliasing = 1 };
            heightRT.Create();
            rebakes = 0; nextRebake = Time.time + 0.3f;
            hCols = new Color[nx * nz];
            lowCols = new float[nx * nz];
            for (int i = 0; i < lowCols.Length; i++) lowCols[i] = oy;
            for (int i = 0; i < hCols.Length; i++) hCols[i] = new Color(oy - 5f, 0, 0, 0);
            bakeIdx = 0; heightReady = false; corrDone = false; corrValid = false; vc = sc = pc = 0; simAcc = 0f;

            if (!solver)
            {
                hasGrid = true;
                Debug.Log("[PlumeTrails] PadFluid: surface-mode grid " + nx + "x" + nz + " columns of " + cell.ToString("F1") + " m at lat " + aLat.ToString("F4") + " lon " + aLon.ToString("F4") + " alt " + aAlt.ToString("F1"));
                return;
            }
            ComputeShader cs = ShaderCache.PadFluidCompute;
            if (!kernelsReady)
            {
                kClear = cs.FindKernel("Clear"); kInject = cs.FindKernel("Inject"); kForces = cs.FindKernel("Forces");
                kCurl = cs.FindKernel("Curl"); kVort = cs.FindKernel("Vorticity"); kAdvVel = cs.FindKernel("AdvectVelocity");
                kAdvFwd = cs.FindKernel("AdvectScalarFwd"); kAdvBack = cs.FindKernel("AdvectScalarBack");
                kShock = cs.FindKernel("Shock"); kDiv = cs.FindKernel("Divergence"); kJac = cs.FindKernel("Jacobi"); kProj = cs.FindKernel("Project");
                kernelsReady = true;
            }
            cs.SetInts("_Dim", nx, ny, nz);
            for (int i = 0; i < 2; i++)
            {
                cs.SetTexture(kClear, "_VelOut", vel[i]); cs.SetTexture(kClear, "_SclOut", scl[i]); cs.SetTexture(kClear, "_PrsOut", prs[i]);
                Dispatch(cs, kClear);
            }

            go = new GameObject("PlumeTrails.PadFluid");
            if (cubeMesh == null)
            {
                GameObject tmpGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cubeMesh = tmpGo.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmpGo);
            }
            go.AddComponent<MeshFilter>().sharedMesh = cubeMesh;
            mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mat = new Material(ShaderCache.PadFluidShader) { renderQueue = 3005 };
            mr.sharedMaterial = mat;
            mr.enabled = false;                                   // drawn by HalfResSmokeRenderer (reduced resolution, temporal upscaling)
            SmokeRenderRegistry.Register(mr, mb);
            SmokeRenderRegistry.SetPriority(mr, -1);          // always drawn behind the trails (no swapping places with the camera)
            hasGrid = true;
            Debug.Log("[PlumeTrails] PadFluid: grid " + nx + "x" + ny + "x" + nz + " cells of " + cell.ToString("F1") + " m at lat " + aLat.ToString("F4") + " lon " + aLon.ToString("F4") + " alt " + aAlt.ToString("F1"));
        }

        private void Dispatch(ComputeShader cs, int kernel)
        {
            cs.Dispatch(kernel, (nx + 3) / 4, (ny + 3) / 4, (nz + 3) / 4);
        }

        // ------------------------------------------------------------------ the frame of the grid (from the planet spot)
        private bool UpdateFrame()
        {
            if (body == null) return false;
            Vector3d p0 = body.GetWorldSurfacePosition(aLat, aLon, aAlt);
            Vector3 up = (Vector3)(p0 - body.position).normalized;
            Vector3 north = (Vector3)(body.GetWorldSurfacePosition(aLat + 0.001, aLon, aAlt) - p0);
            north -= up * Vector3.Dot(north, up);
            if (north.sqrMagnitude < 1e-8f) north = Vector3.Cross(up, Vector3.right);
            e2 = north.normalized; upv = up; e1 = Vector3.Cross(up, e2).normalized;
            Vector3 center = (Vector3)p0;
            corner = center - e1 * (nx * cell * 0.5f) - e2 * (nz * cell * 0.5f);
            return true;
        }

        private Vector3 ToLocal(Vector3 w)
        {
            Vector3 d = w - corner;
            return new Vector3(Vector3.Dot(d, e1), Vector3.Dot(d, upv), Vector3.Dot(d, e2));
        }

        // ------------------------------------------------------------------ heights of the pad (rays, a few hundred per frame)
        private static readonly RaycastHit[] hits = new RaycastHit[10];

        private void BakeHeights()
        {
            if (heightReady) return;
            float top = heightTop;
            int budget = 700;
            if (bakeIdx == 0) rayLayers.Clear();
            int mask = SmokeVolumeGroup.GetSceneryCollisionMaskPublic();
            float reach = top - oy;
            while (budget-- > 0 && bakeIdx < nx * nz)
            {
                int i = bakeIdx % nx, k = bakeIdx / nx;
                Vector3 from = corner + e1 * ((i + 0.5f) * cell) + e2 * ((k + 0.5f) * cell) + upv * top;
                int n = Physics.RaycastNonAlloc(from, -upv, hits, reach, mask, QueryTriggerInteraction.Ignore);
                float best = -1f, worst = -1f;
                for (int h = 0; h < n; h++)
                {
                    Collider c = hits[h].collider;
                    if (c == null || c.GetComponentInParent<Part>() != null) continue;
                    if (best < 0f || hits[h].distance < best) best = hits[h].distance;
                    if (hits[h].distance > worst) worst = hits[h].distance;
                }
                hCols[bakeIdx] = new Color(best >= 0f ? top - best : oy - 5f, 0, 0, 0);
                lowCols[bakeIdx] = worst >= 0f ? top - worst : oy;
                if (best >= 0f)
                {
                    // which collider (layer, name) was the topmost hit of this column: the census says what the rays can actually see
                    for (int h = 0; h < n; h++)
                    {
                        Collider cc = hits[h].collider;
                        if (cc == null || cc.GetComponentInParent<Part>() != null) continue;
                        if (Mathf.Abs(hits[h].distance - best) < 1e-3f)
                        {
                            string key = LayerMask.LayerToName(cc.gameObject.layer) + ": " + cc.gameObject.name + (cc is MeshCollider ? " (mesh)" : "");
                            int cnt; rayLayers.TryGetValue(key, out cnt); rayLayers[key] = cnt + 1;
                            break;
                        }
                    }
                }
                bakeIdx++;
            }
            if (bakeIdx >= nx * nz)
            {
                height.SetPixels(hCols);
                height.Apply(false);
                heightReady = true; corrDone = false;
                int solidCols = 0; float hMin = 1e9f, hMax = -1e9f;
                for (int i = 0; i < hCols.Length; i++) { float hv = hCols[i].r; if (hv > oy) solidCols++; hMin = Mathf.Min(hMin, hv); hMax = Mathf.Max(hMax, hv); }
                System.Text.StringBuilder rl = new System.Text.StringBuilder("[PlumeTrails] PadFluid rays: topmost hit per column, by collider:");
                foreach (KeyValuePair<string, int> kv in rayLayers) rl.Append((char)10 + "  " + kv.Value + " x " + kv.Key);
                Debug.Log(rl.ToString());
                Debug.Log(AsciiMap((x, z) => hCols[z * nx + x].r, "from rays, start " + heightTop.ToString("F1") + " m above the impact plane"));
                Debug.Log("[PlumeTrails] PadFluid: heights baked, " + solidCols + " of " + hCols.Length + " columns hit a surface, heights " + hMin.ToString("F1") + " .. " + hMax.ToString("F1") + " m");
            }
        }

        // The height of the rendered scenery. Not through a camera with a layer mask (the pad's models are on whatever layer their mod put
        // them on): every renderer that touches the grid and is not part of a vessel is drawn into the height map with PadHeight, straight
        // from a command buffer. So what is drawn is what is solid - the pad, the trench walls, the deflector, the launch mount.
        private Material heightMat;
        private CommandBuffer heightCb;
        private readonly List<Renderer> sceneRenderers = new List<Renderer>(256);
        private string censusText = "";

        private void RebakeVisibleHeights()
        {
            if (ShaderCache.PadHeightShader == null || heightRT == null) return;
            if (hcam == null)
            {
                GameObject g = new GameObject("PlumeTrails.PadHeightCam");
                hcam = g.AddComponent<Camera>();
                hcam.enabled = false;
                hcam.orthographic = true;
                hcam.clearFlags = CameraClearFlags.SolidColor;
                hcam.backgroundColor = Color.black;
                hcam.allowHDR = false; hcam.allowMSAA = false;
                hcam.nearClipPlane = 0.05f;
                hcam.cullingMask = 0;
            }
            if (heightMat == null) heightMat = new Material(ShaderCache.PadHeightShader);
            Vector3 centre = corner + e1 * (nx * cell * 0.5f) + e2 * (nz * cell * 0.5f);
            hcam.transform.position = centre + upv * heightTop;
            hcam.transform.rotation = Quaternion.LookRotation(-upv, e2);
            hcam.orthographicSize = nz * cell * 0.5f;
            hcam.aspect = (float)nx / nz;
            hcam.farClipPlane = heightTop - oy + 5f;
            Shader.SetGlobalVector("_PadHBase", new Vector4(corner.x, corner.y, corner.z, oy));
            Shader.SetGlobalVector("_PadHUp", upv);

            // collect: everything drawn within the grid's sphere, that is not a vessel, not ours, not see-through
            sceneRenderers.Clear();
            float gridR = 0.5f * Mathf.Sqrt(nx * cell * nx * cell + nz * cell * nz * cell + ny * cell * ny * cell);
            Vector3 gc = corner + e1 * (nx * cell * 0.5f) + e2 * (nz * cell * 0.5f) + upv * (oy + ny * cell * 0.5f);
            Renderer[] all = FindObjectsOfType<Renderer>();
            Dictionary<string, int> byLayer = new Dictionary<string, int>();
            List<KeyValuePair<float, string>> big = new List<KeyValuePair<float, string>>();
            int skippedParts = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!(r is MeshRenderer)) continue;
                if (r.sharedMaterial == null || r.sharedMaterial.renderQueue >= 2500) continue;
                Bounds bd = r.bounds;
                if (Vector3.Distance(bd.center, gc) > gridR + bd.extents.magnitude) continue;
                if (r.gameObject.name.StartsWith("PlumeTrails")) continue;
                if (r.GetComponentInParent<Part>() != null) { skippedParts++; continue; }
                sceneRenderers.Add(r);
                string ln = LayerMask.LayerToName(r.gameObject.layer);
                int cnt; byLayer.TryGetValue(ln, out cnt); byLayer[ln] = cnt + 1;
                float vol = bd.size.x * bd.size.y * bd.size.z;
                string sh = r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?";
                big.Add(new KeyValuePair<float, string>(vol, r.gameObject.name + " [" + ln + "] " + bd.size.x.ToString("F0") + "x" + bd.size.y.ToString("F0") + "x" + bd.size.z.ToString("F0") + " m, shader " + sh));
            }
            big.Sort((x, y) => y.Key.CompareTo(x.Key));
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[PlumeTrails] PadFluid scenery census: " + sceneRenderers.Count + " renderers drawn into the height map (" + skippedParts + " vessel parts skipped). By layer:");
            foreach (KeyValuePair<string, int> kv in byLayer) sb.Append(" " + kv.Key + "=" + kv.Value);
            sb.Append((char)10 + "largest:");
            for (int i = 0; i < big.Count && i < 12; i++) sb.Append((char)10 + "  " + big[i].Value);
            censusText = sb.ToString();
            if (rebakes <= 2) Debug.Log(censusText);

            if (heightCb == null) heightCb = new CommandBuffer { name = "PadFluid heights" };
            heightCb.Clear();
            heightCb.SetRenderTarget(heightRT);
            heightCb.ClearRenderTarget(true, true, Color.black);
            heightCb.SetViewProjectionMatrices(hcam.worldToCameraMatrix, GL.GetGPUProjectionMatrix(hcam.projectionMatrix, true));
            for (int i = 0; i < sceneRenderers.Count; i++)
            {
                Renderer r = sceneRenderers[i];
                if (r == null) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                int subs = mf != null && mf.sharedMesh != null ? mf.sharedMesh.subMeshCount : 1;
                for (int sm = 0; sm < subs; sm++) heightCb.DrawRenderer(r, heightMat, sm, 0);
            }
            Graphics.ExecuteCommandBuffer(heightCb);
        }

        // A small text map of a height field, for the log: what the solver thinks the pad looks like. '#' deck (>= -2 m), '+' -2..-10,
        // '=' -10..-25, '-' -25..-44, '.' nothing found. East (x) to the right, z up the page; one character = 4 cells.
        private string AsciiMap(System.Func<int, int, float> h, string title)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[PlumeTrails] PadFluid height map (" + title + "), rocket at the centre (X):" + (char)10);
            for (int k = nz - 1; k >= 0; k -= 4)
            {
                for (int i = 0; i < nx; i += 4)
                {
                    float v = h(i, k);
                    char c = v >= -2f ? '#' : v >= -10f ? '+' : v >= -25f ? '=' : v >= -44f ? '-' : '.';
                    if (Mathf.Abs(i - nx / 2) <= 2 && Mathf.Abs(k - nz / 2) <= 2) c = 'X';
                    sb.Append(c);
                }
                sb.Append((char)10);
            }
            return sb.ToString();
        }

        private void DumpVisibleHeights()
        {
            if (heightRT == null) return;
            RenderTexture prev = RenderTexture.active;
            Texture2D rd = new Texture2D(nx, nz, TextureFormat.RFloat, false);
            RenderTexture.active = heightRT;
            rd.ReadPixels(new Rect(0, 0, nx, nz), 0, 0);
            rd.Apply(false);
            RenderTexture.active = prev;
            Color[] px = rd.GetPixels();
            Destroy(rd);
            if (visH == null || visH.Length != px.Length) visH = new float[px.Length];
            for (int i = 0; i < px.Length; i++) visH[i] = px[i].r > 0.01f ? px[i].r + oy : float.NegativeInfinity;
            int hit = 0; for (int i = 0; i < px.Length; i++) if (px[i].r > 0.01f) hit++;
            Debug.Log("[PlumeTrails] PadFluid: the rendered scenery covers " + hit + " of " + px.Length + " columns");
            Debug.Log(AsciiMap((x, z) => px[z * nx + x].r + oy, "from the rendered scenery"));
        }


        // PRE-RENDER. The grid, the height map (12 thousand rays, the scenery census) and the first run of the compute kernels all cost time, and
        // doing them when the engines light made the cloud appear late and the first seconds of the flight stutter. A vessel standing on a
        // surface in an atmosphere gets all of it done beforehand: the grid is built under its lowest nozzle, the heights are baked, and the
        // solver is run a few empty steps (the kernels compile on first use) - so the cloud is there the moment the engines are.
        private bool TryPrewarm(float now)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null || !v.mainBody.atmosphere || v.packed || !v.loaded) return false;
            if (!(v.situation == Vessel.Situations.PRELAUNCH || v.situation == Vessel.Situations.LANDED) || v.srfSpeed > 2.0) return false;
            Vector3 up = (Vector3)v.upAxis;
            Transform low = null; float lowH = float.MaxValue; float thrKN = 0f;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part pt = v.parts[i];
                for (int m = 0; m < pt.Modules.Count; m++)
                {
                    ModuleEngines me = pt.Modules[m] as ModuleEngines;
                    if (me == null || me.thrustTransforms == null) continue;
                    thrKN += me.maxThrust;
                    for (int t = 0; t < me.thrustTransforms.Count; t++)
                    {
                        Transform tt = me.thrustTransforms[t];
                        if (tt == null) continue;
                        float h = Vector3.Dot(tt.position, up);
                        if (h < lowH) { lowH = h; low = tt; }
                    }
                }
            }
            if (low == null || thrKN < 20f) return false;
            int mask = SmokeVolumeGroup.GetSceneryCollisionMaskPublic();
            int n = Physics.RaycastNonAlloc(low.position, -up, pwHits, 300f, mask, QueryTriggerInteraction.Ignore);
            float best = -1f;
            for (int i = 0; i < n; i++)
            {
                Collider c = pwHits[i].collider;
                if (c == null || c.GetComponentInParent<Part>() != null) continue;
                if (pwHits[i].distance > 0.3f && (best < 0f || pwHits[i].distance < best)) best = pwHits[i].distance;
            }
            if (best < 0f) return false;
            Src s = new Src
            {
                nozzle = low.position, impact = low.position - up * best, up = up, thrKN = thrKN,
                jetR = Mathf.Max(6f, 0.12f * Mathf.Sqrt(thrKN)), water = 0.7f, flame = new Color(1f, 0.56f, 0.22f), standoff = best, t = now
            };
            CreateGrid(s);
            if (hasGrid) Debug.Log("[PlumeTrails] PadFluid: pre-rendering the pad of " + v.vesselName + " before the engines light (thrust " + thrKN.ToString("F0") + " kN)");
            return hasGrid;
        }


        // ------------------------------------------------------------------ the pad as a surface to hit (for the turned flame)
        // The pre-rendered height map seen as a surface: where does a ray from the nozzle meet it, and which way does the surface face there?
        // The same combined map the solver uses (the rendered scenery where it has seen something, the rays elsewhere).
        public static bool SurfaceAlong(Vector3 origin, Vector3 dir, float maxDist, out float dist, out Vector3 normal)
        {
            dist = 0f; normal = Vector3.up;
            return instance != null && instance.Trace(origin, dir, maxDist, out dist, out normal);
        }

        private float HeightAt(float lx, float lz)
        {
            float gx = lx / cell - 0.5f, gz = lz / cell - 0.5f;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(gx), 0, nx - 2), k0 = Mathf.Clamp(Mathf.FloorToInt(gz), 0, nz - 2);
            float fx = Mathf.Clamp01(gx - i0), fz = Mathf.Clamp01(gz - k0);
            float h00 = Cell(i0, k0), h10 = Cell(i0 + 1, k0), h01 = Cell(i0, k0 + 1), h11 = Cell(i0 + 1, k0 + 1);
            return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
        }

        private float Cell(int i, int k)
        {
            int id = k * nx + i;
            if (visH != null && visH[id] > oy + 0.01f) return visH[id];
            return hCols[id].r;
        }

        private bool Trace(Vector3 originW, Vector3 dirW, float maxDist, out float dist, out Vector3 normalW)
        {
            dist = 0f; normalW = upv;
            if (!hasGrid || !heightReady || hCols == null) return false;
            Vector3 o = ToLocal(originW);
            Vector3 d = new Vector3(Vector3.Dot(dirW, e1), Vector3.Dot(dirW, upv), Vector3.Dot(dirW, e2));
            float ex = nx * cell, ez = nz * cell;
            float step = Mathf.Max(0.5f, cell * 0.25f);
            float prev = 0f; bool was = false;
            for (float t = 0f; t <= maxDist; t += step)
            {
                Vector3 p = o + d * t;
                if (p.x < 0.5f * cell || p.z < 0.5f * cell || p.x > ex - 0.5f * cell || p.z > ez - 0.5f * cell) { if (was) break; prev = t; continue; }
                float hgt = HeightAtY(p.x, p.z, p.y);
                if (p.y < hgt)
                {
                    if (t == 0f) return false;                       // starts inside: no surface in front of it
                    float lo = prev, hi = t;
                    for (int it = 0; it < 7; it++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 pm = o + d * mid;
                        if (pm.y < HeightAtY(pm.x, pm.z, pm.y)) hi = mid; else lo = mid;
                    }
                    dist = 0.5f * (lo + hi);
                    Vector3 ph = o + d * dist;
                    float e = cell * 1.5f;
                    float dx = HeightAt(ph.x + e, ph.z) - HeightAt(ph.x - e, ph.z);
                    float dz = HeightAt(ph.x, ph.z + e) - HeightAt(ph.x, ph.z - e);
                    Vector3 nl = new Vector3(-dx, 2f * e, -dz).normalized;
                    normalW = (e1 * nl.x + upv * nl.y + e2 * nl.z).normalized;
                    return true;
                }
                was = true; prev = t;
            }
            return false;
        }


        // ------------------------------------------------------------------ full debug: what is IN the solver (read back from the GPU twice a second... no: every 2 s)
        private float nextStatsAt, statsAge;
        private string statsText = "flow stats: waiting for the first read-back";
        private int statsSteps; private float statsFpsT; private int statsFrames;
        private bool statsBusy;

        private static float Half(ushort h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 31, man = h & 1023;
            float v = exp == 0 ? man * (1f / 1024f) * (1f / 16384f) : exp == 31 ? 65504f : (1f + man / 1024f) * Mathf.Pow(2f, exp - 15);
            return sign == 1 ? -v : v;
        }

        private void RequestStats()
        {
            if (statsBusy || !Usable || vel[vc] == null || !SystemInfo.supportsAsyncGPUReadback) return;
            statsBusy = true;
            RenderTexture vt = vel[vc], st = scl[sc];
            int dx = nx, dy = ny, dz = nz;
            AsyncGPUReadback.Request(vt, 0, rv =>
            {
                if (rv.hasError) { statsBusy = false; statsText = "flow stats: read-back of the velocity failed"; return; }
                float vmax = 0f, vsum = 0f; int n = 0, fast = 0;
                var a = rv.GetData<ushort>();
                for (int i = 0; i + 3 < a.Length; i += 4)
                {
                    float x = Half(a[i]), y = Half(a[i + 1]), z = Half(a[i + 2]);
                    float m = Mathf.Sqrt(x * x + y * y + z * z);
                    if (m > vmax) vmax = m;
                    if (m > 0.05f) { vsum += m; n++; }
                    if (m > 100f) fast++;
                }
                AsyncGPUReadback.Request(st, 0, rs =>
                {
                    statsBusy = false;
                    if (rs.hasError) { statsText = "flow stats: read-back of the scalars failed"; return; }
                    var b = rs.GetData<ushort>();
                    float steam = 0f, soot = 0f, hot = 0f, water = 0f, edge = 0f, tot = 0f;
                    for (int i = 0; i + 3 < b.Length; i += 4)
                    {
                        int cell = i / 4, cx = cell % dx, cz = (cell / dx) % dz, cy = cell / (dx * dz);
                        float sx = Half(b[i]), sy = Half(b[i + 1]), sz = Half(b[i + 2]), sw = Half(b[i + 3]);
                        steam += sx; soot += sy; hot = Mathf.Max(hot, sz); water += sw;
                        float m = sx + sy;
                        tot += m;
                        if (cx < 3 || cz < 3 || cx >= dx - 3 || cz >= dz - 3 || cy >= dy - 3) edge += m;
                    }
                    statsText = "flow: max speed " + vmax.ToString("F0") + " m/s, mean " + (n > 0 ? vsum / n : 0f).ToString("F1") + " m/s, " + fast + " cells > 100 m/s | smoke total " + tot.ToString("F0")
                              + " (steam " + steam.ToString("F0") + ", soot " + soot.ToString("F0") + ", liquid " + water.ToString("F0") + "), IN THE 3 OUTER CELLS " + edge.ToString("F0") + " (" + (tot > 1f ? (100f * edge / tot).ToString("F0") : "0") + " %), hottest " + hot.ToString("F2");
                    Debug.Log("[PlumeTrails] PadFluid " + statsText);
                });
            });
        }

        private string StatsLine()
        {
            statsFrames++;
            if (Time.unscaledTime - statsFpsT >= 1f) { statsAge = statsFrames / Mathf.Max(Time.unscaledTime - statsFpsT, 0.01f); statsFrames = 0; statsFpsT = Time.unscaledTime; }
            if (Time.unscaledTime >= nextStatsAt) { nextStatsAt = Time.unscaledTime + 2f; RequestStats(); }
            if (Time.unscaledTime - surfStatT >= 2f)
            {
                int hc = SmokeVolumeGroup.DbgHitCount;
                surfText = SurfaceWanted ? SmokeVolumeGroup.GroundStats() + " | SURFACE: " + (hc - surfStatCount) + " turns in 2 s, mean speed after " + (hc > surfStatCount ? (SmokeVolumeGroup.DbgHitSpeedSum / hc).ToString("F0") : "-") + " m/s, mean upward share " + (hc > 0 ? (SmokeVolumeGroup.DbgHitUpSum / hc).ToString("F2") : "-") + " (0 = along the surface, 1 = straight up)" : "SURFACE mode off";
                surfStatCount = hc; surfStatT = Time.unscaledTime;
                Debug.Log("[PlumeTrails] PadFluid " + surfText);
            }
            return surfText + " | VOXEL: " + (padVox != null ? padVox.report : (voxTried ? "failed or no scenery" : "not built yet")) + " | PATH " + dbgPath + ", engines now " + active.Count + ", timeScale " + Time.timeScale.ToString("F2") + " | fps " + statsAge.ToString("F0") + ", solver steps " + statsSteps + ", simAcc " + simAcc.ToString("F2") + ", cell " + cell.ToString("F1") + " m, grid " + nx + "x" + ny + "x" + nz
                 + " | sliders: jet x" + SmokeTuning.PadFluidJet.ToString("F2") + " size x" + SmokeTuning.PadFluidSize.ToString("F2") + " spread x" + SmokeTuning.PadFluidSpread.ToString("F2") + " density " + SmokeTuning.PadFluidDensity.ToString("F2")
                 + " | shock " + (shockOn ? "RUNNING r=" + shockR.ToString("F0") + " m" : "off") + " ring r=" + shockRVis.ToString("F0") + " | " + statsText;
        }

        // ------------------------------------------------------------------ the pad in 3D (PadVoxel)
        private PadVoxel padVox;
        private bool voxTried;

        private void BuildVoxels()
        {
            voxTried = true;
            if (ShaderCache.PadLayerShader == null || sceneRenderers.Count == 0) { Debug.Log("[PlumeTrails] PadVoxel: nothing to build from (shader " + (ShaderCache.PadLayerShader != null) + ", renderers " + sceneRenderers.Count + ")"); return; }
            try
            {
                float[] fb = new float[nx * nz];
                for (int i = 0; i < fb.Length; i++) fb[i] = hCols[i].r;
                // the triangles of the meshes first (they have the vertical walls); the depth layers only if too little could be read
                int mRead, mSkip; long tUsed;
                padVox = PadVoxel.BuildFromMeshes(sceneRenderers, corner, e1, upv, e2, nx, ny, nz, cell, oy, out mRead, out mSkip, out tUsed);
                Debug.Log("[PlumeTrails] PadVoxel from triangles: " + padVox.report + (padVox.ready ? " -> USED" : " -> too little, the depth layers are used instead"));
                if (!padVox.ready || tUsed < 200)
                    padVox = PadVoxel.Build(ShaderCache.PadLayerShader, sceneRenderers, corner, e1, upv, e2, nx, ny, nz, cell, oy, heightTop, fb);
                Debug.Log("[PlumeTrails] PadVoxel: " + padVox.report);
                if (!padVox.ready) { padVox = null; return; }
                // what the geometry looks like: vertical sections through the centre of the grid (the pad), 5 m per character, '#' = solid
                float cx = nx * cell * 0.5f, cz = nz * cell * 0.5f, top = oy + ny * cell - 2f;
                Debug.Log("[PlumeTrails] PadVoxel section along the grid's z axis (north) through the centre, y from " + top.ToString("F0") + " down to " + oy.ToString("F0") + " m:" + (char)10
                          + padVox.CrossSection(new Vector3(cx, 0f, 0f), Vector3.forward, nz * cell, top, oy + 2f, 5f));
                Debug.Log("[PlumeTrails] PadVoxel section along the grid's x axis (east) through the centre:" + (char)10
                          + padVox.CrossSection(new Vector3(0f, 0f, cz), Vector3.right, nx * cell, top, oy + 2f, 5f));
                // and from above, at three heights: just over the floor of the pit, in the middle of the walls, above the lid (x to the right, z up, 2 cells per character)
                foreach (float hy in new[] { -9f, -5f, 1f })
                    Debug.Log("[PlumeTrails] PadVoxel map from above, solid at y = " + hy.ToString("F0") + " m (x east to the right, z north up, " + (2 * cell).ToString("F1") + " m per character, + = the centre):" + (char)10 + padVox.TopMap(hy, 2));
            }
            catch (System.Exception e) { Debug.LogWarning("[PlumeTrails] PadVoxel failed: " + e.Message); padVox = null; }
        }

        // ------------------------------------------------------------------ the surface for the puffs (surface mode)
        // The height map as a surface that is walked in steps of 30 cm: a puff that meets it is turned along it (PuffPhysics.Collide), so the
        // flow follows the trench floor, climbs and is stopped by the walls, runs down the deflector. The flame hole under the nozzle is open.
        private float HeightAtY(float lx, float lz, float ly)
        {
            float h = HeightAt(lx, lz);
            for (int i = 0; i < holeCount; i++)
            {
                float dx = lx - holeA[i].x, dz = lz - holeA[i].z;
                if (dx * dx + dz * dz < holeA[i].w * holeA[i].w && ly < holeA[i].y && h > holeB[i].x) h = holeB[i].x;
            }
            return h;
        }

        // the normal of the surface: the gradient of the height over +-1.2 cells, so that it turns smoothly where the slope changes
        private Vector3 NormalAt(float lx, float lz, float ly)
        {
            float e = cell * 1.2f;
            float dx = HeightAtY(lx + e, lz, ly) - HeightAtY(lx - e, lz, ly);
            float dz = HeightAtY(lx, lz + e, ly) - HeightAtY(lx, lz - e, ly);
            Vector3 nl = new Vector3(-dx, 2f * e, -dz).normalized;
            return (e1 * nl.x + upv * nl.y + e2 * nl.z).normalized;
        }

        private Vector3 ToWorld(Vector3 l) { return corner + e1 * l.x + upv * l.y + e2 * l.z; }

        private bool InGrid(Vector3 l) { return l.x > cell && l.z > cell && l.x < nx * cell - cell && l.z < nz * cell - cell; }

        // the cheap test: only the height above the surface under the point
        public static bool SurfaceAbove(Vector3 world, out float above)
        {
            above = 1e9f;
            if (!SurfaceReady) return false;
            Vector3 l = instance.ToLocal(world);
            if (!instance.InGrid(l)) return false;
            above = l.y - instance.HeightAtY(l.x, l.z, l.y);
            return true;
        }

        // height of the point above the surface under it (negative = inside the structure), and the surface's normal there
        public static bool SurfaceQuery(Vector3 world, out float above, out Vector3 normal)
        {
            above = 1e9f; normal = Vector3.up;
            if (!SurfaceReady) return false;
            Vector3 l = instance.ToLocal(world);
            if (!instance.InGrid(l)) return false;
            above = l.y - instance.HeightAtY(l.x, l.z, l.y);
            normal = instance.NormalAt(l.x, l.z, l.y);
            return true;
        }

        // Walks from a to b in steps of 30 cm and returns where the path first goes below the surface (and the surface's normal there).
        // A start that is already inside the structure returns the surface point straight above it.
        public static bool SurfaceMarch(Vector3 a, Vector3 b, out Vector3 hit, out Vector3 normal)
        {
            hit = a; normal = Vector3.up;
            if (!SurfaceReady) return false;
            PadFluid f = instance;
            Vector3 la = f.ToLocal(a), lb = f.ToLocal(b);
            float len = (lb - la).magnitude;
            bool inA = f.InGrid(la), inB = f.InGrid(lb);
            if (!inA && !inB) return false;
            if (inA)
            {
                float h0 = f.HeightAtY(la.x, la.z, la.y);
                if (la.y < h0) { hit = f.ToWorld(new Vector3(la.x, h0, la.z)); normal = f.NormalAt(la.x, la.z, la.y); return true; }
            }
            int steps = Mathf.Clamp(Mathf.CeilToInt(len / 0.3f), 1, 80);
            float prevT = 0f;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector3 p = Vector3.Lerp(la, lb, t);
                if (!f.InGrid(p)) { prevT = t; continue; }
                if (p.y < f.HeightAtY(p.x, p.z, p.y))
                {
                    float lo = prevT, hi = t;
                    for (int it = 0; it < 6; it++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 pm = Vector3.Lerp(la, lb, mid);
                        if (pm.y < f.HeightAtY(pm.x, pm.z, pm.y)) hi = mid; else lo = mid;
                    }
                    Vector3 ph = Vector3.Lerp(la, lb, lo);
                    hit = f.ToWorld(ph);
                    normal = f.NormalAt(ph.x, ph.z, ph.y);
                    return true;
                }
                prevT = t;
            }
            return false;
        }

        // THE FLAME TRENCH UNDER THE MOUNT. The height map sees the top of the launch mount (a lid): what is under it is invisible to the rays and
        // to the height camera. But the trench is a straight channel, and its open parts on both sides of the mount ARE visible: their low columns
        // lie along a line through the flame hole. The direction with the most low columns within 12 m of a line through the hole is the axis of
        // the trench, their spread across it its half-width, and the channel is taken to run under the lid along that line, at the floor of the hole.
        private bool corrDone, corrValid;
        private Vector2 corrC, corrDir;
        private float corrHalf, corrFloor;
        private int corrColumns;

        private void EnsureCorridor()
        {
            if (corrDone) return;
            if (holeCount <= 0 || !heightReady) return;          // not yet: try again at the next call
            corrDone = true; corrValid = false;
            Vector2 c = new Vector2(holeA[0].x, holeA[0].z);
            float floorY = holeB[0].x;
            const float R = 170f;
            int i0 = Mathf.Clamp(Mathf.FloorToInt((c.x - R) / cell), 0, nx - 1), i1 = Mathf.Clamp(Mathf.CeilToInt((c.x + R) / cell), 0, nx - 1);
            int k0 = Mathf.Clamp(Mathf.FloorToInt((c.y - R) / cell), 0, nz - 1), k1 = Mathf.Clamp(Mathf.CeilToInt((c.y + R) / cell), 0, nz - 1);
            // the floor of the open trench AS THE HEIGHT MAP SEES IT (the rendered scenery): the lowest columns of the region. (The floor of the
            // flame hole comes from the colliders and may be lower than the rendered floor, so it is not used to pick the columns.)
            float lowest = float.MaxValue;
            for (int k = k0; k <= k1; k++)
                for (int i = i0; i <= i1; i++)
                {
                    Vector2 p0 = new Vector2((i + 0.5f) * cell, (k + 0.5f) * cell) - c;
                    if (p0.sqrMagnitude > R * R) continue;
                    float hv = Cell(i, k);
                    if (hv > oy + 0.5f && hv < lowest) lowest = hv;
                }
            float lowTop = lowest + 2.5f;
            var cols = new System.Collections.Generic.List<Vector2>(2048);
            for (int k = k0; k <= k1; k++)
                for (int i = i0; i <= i1; i++)
                {
                    if (Cell(i, k) > lowTop) continue;
                    Vector2 p = new Vector2((i + 0.5f) * cell, (k + 0.5f) * cell) - c;
                    if (p.sqrMagnitude > R * R) continue;
                    cols.Add(p);
                }
            corrColumns = cols.Count;
            if (cols.Count < 25) { Debug.Log("[PlumeTrails] PadFluid: flame trench under the mount NOT found: only " + cols.Count + " low columns (lowest rendered " + lowest.ToString("F1") + " m, hole floor " + floorY.ToString("F1") + " m)"); return; }
            float bestN = 0f; float bestA = 0f;
            for (int a = 0; a < 36; a++)
            {
                float ang = a * Mathf.PI / 36f;
                Vector2 d = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                int n = 0;
                for (int j = 0; j < cols.Count; j++)
                {
                    float perp = Mathf.Abs(cols[j].x * -d.y + cols[j].y * d.x);
                    if (perp <= 12f) n++;
                }
                if (n > bestN) { bestN = n; bestA = ang; }
            }
            Vector2 axis = new Vector2(Mathf.Cos(bestA), Mathf.Sin(bestA));
            // half-width: the spread across the axis of the low columns that lie near the line (within 20 m), mean absolute distance x 1.9
            float sum = 0f; int cnt = 0;
            for (int j = 0; j < cols.Count; j++)
            {
                float perp = Mathf.Abs(cols[j].x * -axis.y + cols[j].y * axis.x);
                if (perp <= 20f) { sum += perp; cnt++; }
            }
            if (cnt < 20) { Debug.Log("[PlumeTrails] PadFluid: flame trench under the mount NOT found: no line through the hole (" + cols.Count + " low columns, " + cnt + " near the best line, lowest " + lowest.ToString("F1") + " m)"); return; }
            corrC = c; corrDir = axis; corrFloor = floorY;
            corrHalf = Mathf.Clamp(1.9f * sum / cnt, 5f, 25f);
            corrValid = true;
            Debug.Log("[PlumeTrails] PadFluid: flame trench under the mount: axis " + (bestA * Mathf.Rad2Deg).ToString("F0") + " deg (grid frame), half-width " + corrHalf.ToString("F1")
                + " m, floor " + floorY.ToString("F1") + " m, from " + cols.Count + " low columns (" + bestN + " on the line)");
        }

        // the lateral distance of a point from the axis of the trench (m), + / - = the two sides
        private float CorrPerp(float lx, float lz) { return (lx - corrC.x) * -corrDir.y + (lz - corrC.y) * corrDir.x; }

        // the lowest surface under a column when the point is in the cavity under the lid: the floor of the trench inside the corridor,
        // the top itself (= no cavity) outside it
        private float CavLow(float lx, float lz, float top)
        {
            if (!corrValid) return top;
            return Mathf.Abs(CorrPerp(lx, lz)) < corrHalf ? corrFloor : top;
        }

        // The lowest surface under a column (the floor of a trench under a mount), from the bake of the heights
        private float Low(float lx, float lz)
        {
            if (lowCols == null) return oy;
            int i = Mathf.Clamp((int)(lx / cell), 0, nx - 1), k = Mathf.Clamp((int)(lz / cell), 0, nz - 1);
            return lowCols[k * nx + i];
        }

        // A BOUNCING RAY (the flow of the wall jet, drawn as a trail). It leaves the impact point of the jet in the direction az, tilted `tilt`
        // radians away from straight down, and flies for as long as the longest scheduled distance (200+ m) in steps of 1 m. Where it goes into
        // a surface it is turned: most of its direction stays ALONG the surface (the wall jet clings to what it hits), a part is mirrored away
        // from it (so a ray that meets a wall head-on comes back off it), and it goes on. Rays that leave the trench fly on above the ground.
        // THE SPACE UNDER THE MOUNT: the height map sees the top of the launch mount (a lid over the flame trench); the trench itself is open
        // only under the nozzles. A ray that starts in that cavity flies BETWEEN the floor of the trench (the lowest surface of the column) and
        // the underside of the lid, bouncing off both and off the walls of the trench, until it comes out of the lid into the open trench.
        // sched = the arc lengths (m) to report, ascending; outW gets the world position of each, outClear the free width across the flow there
        // (walls higher than 2.5 m on both sides, 40 m = open). Returns how many points were written.
        private static Vector3[] voxPos;
        // with the voxels, outWidthH (4 per point) and outHead say how much room the flow has at the heights of PadVoxel.WidthHeights; without them every width is the plain clearance
        public static int TraceStream(Vector3 srcW, float az, float tilt, float[] sched, int n, Vector3[] outW, float[] outClear, float[] outWidthH = null, float[] outHead = null)
        {
            int cnt = TraceStreamInner(srcW, az, tilt, sched, n, outW, outClear, outWidthH, outHead);
            if (instance != null && instance.padVox == null)
                for (int q = 0; q < cnt; q++)
                {
                    if (outWidthH != null) for (int hi = 0; hi < 4; hi++) outWidthH[q * 4 + hi] = outClear[q];
                    if (outHead != null) outHead[q] = 40f;
                }
            return cnt;
        }

        private static int TraceStreamInner(Vector3 srcW, float az, float tilt, float[] sched, int n, Vector3[] outW, float[] outClear, float[] outWidthH, float[] outHead)
        {
            if (!SurfaceReady || n <= 0) return 0;
            PadFluid f = instance;
            Vector3 l = f.ToLocal(srcW);
            if (!f.InGrid(l)) return 0;
            if (f.padVox != null)
            {
                if (voxPos == null || voxPos.Length < n) { voxPos = new Vector3[n]; }
                int bn = 0;
                int c = f.padVox.Trace(l + Vector3.up * 0.6f, az, tilt, sched, n, voxPos, outClear, ref bn, outWidthH, outHead);
                for (int q = 0; q < c; q++) outW[q] = f.ToWorld(voxPos[q]);
                return c;
            }
            float sinT = Mathf.Sin(tilt), cosT = Mathf.Cos(tilt);
            Vector3 d = new Vector3(sinT * Mathf.Cos(az), -cosT, sinT * Mathf.Sin(az));
            f.EnsureCorridor();
            float topAt = f.HeightAt(l.x, l.z), lowAt = f.CavLow(l.x, l.z, topAt);
            bool cav = topAt - lowAt > 4f && l.y < topAt - 2f;
            Vector3 pos = cav ? new Vector3(l.x, Mathf.Max(l.y, lowAt) + 0.4f, l.z)
                              : new Vector3(l.x, f.HeightAtY(l.x, l.z, l.y) + 0.4f, l.z);
            float s = 0f;
            bool outside = false;
            int k = 0, bounces = 0;
            Vector3 lastHoriz = new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az));
            while (k < n)
            {
                while (s < sched[k] - 1e-3f)
                {
                    float step = Mathf.Min(1f, sched[k] - s);
                    Vector3 q = pos + d * step;
                    if (outside || !f.InGrid(q)) { outside = true; pos = q; s += step; continue; }
                    bool hit = false; float surfY = 0f; Vector3 nrm = Vector3.up;
                    float e = f.cell * 1.2f;
                    if (cav)
                    {
                        float top = f.HeightAt(q.x, q.z), low = f.CavLow(q.x, q.z, top);
                        if (top - low > 4f)
                        {
                            if (q.y < low) { hit = true; surfY = low; nrm = Vector3.up; }
                            else if (q.y > top - 1.5f) { hit = true; surfY = top - 1.5f; nrm = Vector3.down; }
                        }
                        else
                        {
                            // out of the cavity. Out through the SIDE of the corridor (still below the lid): the wall of the trench turns it back
                            float pdN = f.CorrPerp(q.x, q.z);
                            if (f.corrValid && Mathf.Abs(pdN) >= f.corrHalf && q.y < top - 2f && top - f.corrFloor > 4f)
                            {
                                float sgn = pdN > 0f ? 1f : -1f;
                                Vector2 inw = -sgn * new Vector2(-f.corrDir.y, f.corrDir.x);
                                float vnW = d.x * inw.x + d.z * inw.y;
                                if (vnW < 0f)
                                {
                                    Vector3 nW = new Vector3(inw.x, 0f, inw.y);
                                    Vector3 tangW = d - nW * vnW; Vector3 mirW = d - 2f * vnW * nW;
                                    d = (tangW * 0.9f + mirW * 0.25f + nW * 0.04f).normalized;
                                    bounces++; if (bounces > 90) return k;
                                }
                                s += step; continue;                              // the ray stays where it was: it was turned at the wall
                            }
                            cav = false;                                        // the open trench / the ground
                        }
                    }
                    if (!cav)
                    {
                        float h = f.HeightAtY(q.x, q.z, q.y);
                        if (q.y < h)
                        {
                            hit = true; surfY = h;
                            nrm = new Vector3(-(f.HeightAtY(q.x + e, q.z, q.y) - f.HeightAtY(q.x - e, q.z, q.y)), 2f * e, -(f.HeightAtY(q.x, q.z + e, q.y) - f.HeightAtY(q.x, q.z - e, q.y))).normalized;
                        }
                    }
                    if (hit)
                    {
                        float vn = Vector3.Dot(d, nrm);
                        if (vn < 0f)
                        {
                            Vector3 tang = d - nrm * vn;                         // what stays along the surface
                            Vector3 mirror = d - 2f * vn * nrm;                  // the mirrored part, away from the surface
                            d = (tang * 0.9f + mirror * 0.25f + nrm * 0.04f).normalized;
                            if (Vector3.Dot(d, nrm) < 0.03f) d = (d + nrm * 0.06f).normalized;
                            bounces++;
                            if (bounces > 90) return k;
                        }
                        pos = new Vector3(q.x, surfY + (nrm.y >= 0f ? 0.1f : -0.1f), q.z);
                        s += step;
                        continue;
                    }
                    pos = q; s += step;
                }
                Vector3 hd = new Vector3(d.x, 0f, d.z);
                if (hd.sqrMagnitude > 0.04f) lastHoriz = hd.normalized;
                // free width across the flow (in the cavity: the walls of the trench are where its floor rises)
                float clear = 0f;
                for (int side = -1; side <= 1; side += 2)
                {
                    float w = 40f;
                    for (int j = 1; j <= 16; j++)
                    {
                        float px = pos.x - lastHoriz.z * side * j * 2.5f, pz = pos.z + lastHoriz.x * side * j * 2.5f;
                        if (!f.InGrid(new Vector3(px, pos.y, pz))) break;
                        if (cav && f.corrValid) { if (Mathf.Abs(f.CorrPerp(px, pz)) >= f.corrHalf) { w = j * 2.5f; break; } continue; }
                        float hh = f.HeightAt(px, pz);
                        if (hh > pos.y + 2.5f) { w = j * 2.5f; break; }
                    }
                    clear += w;
                }
                outW[k] = f.ToWorld(pos);
                outClear[k] = clear;
                k++;
            }
            return k;
        }

        // the ignition wave for the puffs: the front moved from rPrev to rNow since the last frame (surface mode only)
        public static bool ShockFront(out Vector3 centre, out float rPrev, out float rNow, out float amp)
        {
            centre = Vector3.zero; rPrev = rNow = amp = 0f;
            if (!SurfaceActive || !instance.shockOn || !SmokeTuning.PadFluidShock) return false;
            centre = instance.ToWorld(instance.shockC);
            rPrev = instance.shockPrev; rNow = instance.shockR; amp = instance.shockAmp;
            return true;
        }

        // ------------------------------------------------------------------ debug view
        // What the solver is working with, drawn in the world: a coloured surface over the pad (the height it uses per column) -
        //   yellow = the collider is more than 2 m HIGHER than the drawn surface (a lid over a trench; the render map is used there),
        //   green  = rays (colliders) and the rendered scenery agree,   red  = only the rays found a surface (render map empty there),
        //   blue   = only the rendered scenery (the colliders miss it), grey = neither found anything
        // plus the jet (yellow line from every nozzle), the flame hole (cyan circle) and a one-line summary in the log and on screen.
        private void DebugView(int jets)
        {
            if (!SmokeTuning.PadFluidDebug || !heightReady)
            {
                if (dbgGo != null) dbgGo.SetActive(false);
                dbgText = "";
                return;
            }
            if (dbgGo == null)
            {
                dbgGo = new GameObject("PlumeTrails.PadFluidDebug");
                dbgGo.AddComponent<MeshFilter>();
                MeshRenderer r = dbgGo.AddComponent<MeshRenderer>();
                Shader sh = Shader.Find("Hidden/Internal-Colored");
                Material m = new Material(sh);
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_Cull", 0); m.SetInt("_ZWrite", 0); m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                dbgLines = new LineRenderer[8 + 8 + 2 * SmokeVolumeGroup.DbgHitMax];
                for (int i = 0; i < dbgLines.Length; i++)
                {
                    GameObject lg = new GameObject("l"); lg.transform.SetParent(dbgGo.transform, false);
                    LineRenderer lr = lg.AddComponent<LineRenderer>();
                    lr.useWorldSpace = true; lr.widthMultiplier = 0.6f; lr.sharedMaterial = m; lr.numCapVertices = 0;
                    dbgLines[i] = lr;
                }
            }
            dbgGo.SetActive(true);
            dbgGo.transform.position = Vector3.zero; dbgGo.transform.rotation = Quaternion.identity;

            // the surface: one vertex per column, every 2nd column (a light mesh)
            int step = 1, gw = (nx + step - 1) / step, gh = (nz + step - 1) / step;
            Vector3[] vs = new Vector3[gw * gh]; Color[] cs = new Color[gw * gh];
            int agree = 0, rayOnly = 0, visOnly = 0, none = 0, coverLid = 0;
            for (int kk = 0; kk < gh; kk++)
                for (int ii = 0; ii < gw; ii++)
                {
                    int i = Mathf.Min(ii * step, nx - 1), k = Mathf.Min(kk * step, nz - 1);
                    float hr = hCols[k * nx + i].r, hv = visH != null ? visH[k * nx + i] : float.NegativeInfinity;
                    bool r = hr > oy, v = hv > oy + 0.01f;
                    Color col; float y;
                    if (r && v && hr - hv > 2f) { col = new Color(1f, 0.85f, 0.1f, 0.5f); y = hv; coverLid++; }       // the collider is HIGHER than the drawn surface: a lid over a trench
                    else if (r && v) { col = new Color(0.1f, 0.9f, 0.2f, 0.35f); y = hv; agree++; }
                    else if (r) { col = new Color(0.95f, 0.15f, 0.15f, 0.45f); y = hr; rayOnly++; }
                    else if (v) { col = new Color(0.2f, 0.4f, 1f, 0.45f); y = hv; visOnly++; }
                    else { col = new Color(0.5f, 0.5f, 0.5f, 0.12f); y = -2f; none++; }
                    vs[kk * gw + ii] = corner + e1 * ((i + 0.5f) * cell) + e2 * ((k + 0.5f) * cell) + upv * (y + 0.4f);
                    cs[kk * gw + ii] = col;
                }
            if (dbgMesh == null)
            {
                dbgMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                int[] tri = new int[(gw - 1) * (gh - 1) * 6]; int q = 0;
                for (int kk = 0; kk < gh - 1; kk++)
                    for (int ii = 0; ii < gw - 1; ii++)
                    {
                        int a0 = kk * gw + ii, b0 = a0 + 1, c0 = a0 + gw, d0 = c0 + 1;
                        tri[q++] = a0; tri[q++] = c0; tri[q++] = b0; tri[q++] = b0; tri[q++] = c0; tri[q++] = d0;
                    }
                dbgMesh.vertices = vs; dbgMesh.triangles = tri;
                dbgGo.GetComponent<MeshFilter>().sharedMesh = dbgMesh;
            }
            dbgMesh.vertices = vs; dbgMesh.colors = cs;
            dbgMesh.RecalculateBounds();

            // jets and flame holes
            int li = 0;
            for (int i = 0; i < jets && li < 8; i++)
            {
                Vector3 a = corner + e1 * srcA[i].x + e2 * srcA[i].z + upv * srcA[i].y, b = corner + e1 * srcB[i].x + e2 * srcB[i].z + upv * srcB[i].y;
                LineRenderer lr = dbgLines[li++];
                lr.positionCount = 2; lr.SetPosition(0, a); lr.SetPosition(1, b);
                lr.startColor = lr.endColor = new Color(1f, 0.9f, 0.1f, 0.9f); lr.enabled = true;
            }
            for (int i = 0; i < holeCount && li < dbgLines.Length; i++)
            {
                LineRenderer lr = dbgLines[li++];
                lr.positionCount = 25; lr.loop = false;
                for (int a = 0; a <= 24; a++)
                {
                    float t = a / 24f * Mathf.PI * 2f;
                    Vector3 pt = corner + e1 * (holeA[i].x + Mathf.Cos(t) * holeA[i].w) + e2 * (holeA[i].z + Mathf.Sin(t) * holeA[i].w) + upv * (holeA[i].y);
                    lr.SetPosition(a, pt);
                }
                lr.startColor = lr.endColor = new Color(0.1f, 1f, 1f, 0.9f); lr.enabled = true;
            }
            // the parcels the surface has turned lately: cyan = the surface's normal there, magenta = the velocity after the turn (10 cm per m/s, at most 25 m)
            for (int hi = 0; hi < SmokeVolumeGroup.DbgHitMax && li + 1 < dbgLines.Length; hi++)
            {
                if (Time.time - SmokeVolumeGroup.DbgHitTime[hi] > 4f || SmokeVolumeGroup.DbgHitTime[hi] <= 0f) continue;
                Vector3 hp = SmokeVolumeGroup.DbgHitPos[hi];
                LineRenderer ln = dbgLines[li++];
                ln.positionCount = 2; ln.SetPosition(0, hp); ln.SetPosition(1, hp + SmokeVolumeGroup.DbgHitNormal[hi] * 6f);
                ln.startColor = ln.endColor = new Color(0.1f, 1f, 1f, 0.9f); ln.enabled = true;
                LineRenderer lv = dbgLines[li++];
                Vector3 hv = SmokeVolumeGroup.DbgHitVel[hi];
                lv.positionCount = 2; lv.SetPosition(0, hp); lv.SetPosition(1, hp + hv.normalized * Mathf.Min(hv.magnitude * 0.1f, 25f));
                lv.startColor = lv.endColor = new Color(1f, 0.2f, 1f, 0.95f); lv.enabled = true;
            }
            for (; li < dbgLines.Length; li++) dbgLines[li].enabled = false;

            int total = agree + coverLid + rayOnly + visOnly + none;
            string txt = "PadFluid debug (the solver uses the RENDER map where it has one): " + agree + " agree, " + coverLid + " collider-higher-than-render (yellow), " + rayOnly + " rays only, " + visOnly + " render only, " + none + " nothing (of " + total + ") | jets " + jets
                       + ", holes " + holeCount + (holeCount > 0 ? ", hole floor " + holeB[0].x.ToString("F1") + " m" : "")
                       + " | scenery map " + (visH == null ? "NOT READ YET" : "read, " + sceneRenderers.Count + " renderers") + " | nozzle " + heightTop.ToString("F1") + " m above the impact plane | " + StatsLine();
            if (txt != dbgText)
            {
                dbgText = txt;
                if (Time.time - lastDbgLog > 3f) { lastDbgLog = Time.time; Debug.Log("[PlumeTrails] " + txt); }
            }
        }
        private static float lastDbgLog;

        private void OnGUI()
        {
            if (!SmokeTuning.PadFluidDebug || dbgText.Length == 0) return;
            GUI.Box(new Rect(8, 6, Mathf.Min(Screen.width - 16, 1700), 70), GUIContent.none);
            GUI.Label(new Rect(12, 8, Mathf.Min(Screen.width - 24, 1690), 66), dbgText);
        }

        // ------------------------------------------------------------------ one solver step
        private void SetTex(ComputeShader cs, int k, string name, Texture t) { cs.SetTexture(k, name, t); }

        private void Step(ComputeShader cs, float dt, int ns)
        {
            cs.SetFloat("_Cell", cell);
            cs.SetFloat("_OriginY", oy);
            cs.SetFloat("_Dt", dt);
            cs.SetVector("_Params", new Vector4(14f * SmokeTuning.PadFluidBuoyancy, 0.9f, 0.015f, SmokeTuning.PadFluidVorticity));
            cs.SetVector("_Params2", new Vector4(0.012f, 0.010f, 0.45f, 0f));
            cs.SetVector("_Params3", new Vector4(1.6f, 0.35f, SmokeTuning.PadFluidWater ? 1f : 0f, 0f));
            cs.SetInt("_SrcCount", ns);
            cs.SetInt("_HoleCount", holeCount); cs.SetVectorArray("_HoleA", holeA); cs.SetVectorArray("_HoleB", holeB);
            cs.SetVectorArray("_SrcA", srcA); cs.SetVectorArray("_SrcB", srcB); cs.SetVectorArray("_SrcC", srcC); cs.SetVectorArray("_SrcD", srcD); cs.SetVectorArray("_SrcE", srcE);
            int[] all = { kInject, kForces, kVort, kAdvVel, kAdvFwd, kAdvBack, kDiv, kJac, kProj };
            for (int i = 0; i < all.Length; i++) { cs.SetTexture(all[i], "_Height", height); cs.SetTexture(all[i], "_Height2", heightRT); }

            // the ignition wave: one outward kick for every cell that the front swept over in this step
            if (shockOn)
            {
                shockPrev = shockR; shockR += SoundSpeed * SmokeTuning.PadFluidShockSpeed * dt;
                cs.SetVector("_ShockA", new Vector4(shockC.x, shockC.y, shockC.z, shockR));
                cs.SetVector("_ShockB", new Vector4(Mathf.Max(3f, cell * 1.5f) * SmokeTuning.PadFluidShockSize, shockAmp, 0.25f, shockPrev));
                SetTex(cs, kShock, "_VelIn", vel[vc]); SetTex(cs, kShock, "_VelOut", vel[1 - vc]);
                cs.SetTexture(kShock, "_Height", height); cs.SetTexture(kShock, "_Height2", heightRT);
                Dispatch(cs, kShock); vc = 1 - vc;
                if (shockR > ShockMaxR * SmokeTuning.PadFluidShockSize) shockOn = false;
            }
            // jet
            SetTex(cs, kInject, "_VelIn", vel[vc]); SetTex(cs, kInject, "_SclIn", scl[sc]);
            SetTex(cs, kInject, "_VelOut", vel[1 - vc]); SetTex(cs, kInject, "_SclOut", scl[1 - sc]);
            Dispatch(cs, kInject); vc = 1 - vc; sc = 1 - sc;
            // buoyancy, weight, drag
            SetTex(cs, kForces, "_VelIn", vel[vc]); SetTex(cs, kForces, "_SclIn", scl[sc]); SetTex(cs, kForces, "_VelOut", vel[1 - vc]);
            Dispatch(cs, kForces); vc = 1 - vc;
            // vorticity confinement
            SetTex(cs, kCurl, "_VelIn", vel[vc]); SetTex(cs, kCurl, "_CurlOut", curl);
            Dispatch(cs, kCurl);
            SetTex(cs, kVort, "_VelIn", vel[vc]); SetTex(cs, kVort, "_CurlIn", curl); SetTex(cs, kVort, "_VelOut", vel[1 - vc]);
            Dispatch(cs, kVort); vc = 1 - vc;
            // advect the velocity
            SetTex(cs, kAdvVel, "_VelIn", vel[vc]); SetTex(cs, kAdvVel, "_VelOut", vel[1 - vc]);
            Dispatch(cs, kAdvVel); vc = 1 - vc;
            // pressure projection
            SetTex(cs, kDiv, "_VelIn", vel[vc]); SetTex(cs, kDiv, "_DivOut", div);
            Dispatch(cs, kDiv);
            SetTex(cs, kJac, "_DivIn", div);
            for (int it = 0; it < PressureIters; it++)
            {
                SetTex(cs, kJac, "_PrsIn", prs[pc]); SetTex(cs, kJac, "_PrsOut", prs[1 - pc]);
                Dispatch(cs, kJac); pc = 1 - pc;
            }
            SetTex(cs, kProj, "_VelIn", vel[vc]); SetTex(cs, kProj, "_PrsIn", prs[pc]); SetTex(cs, kProj, "_VelOut", vel[1 - vc]);
            Dispatch(cs, kProj); vc = 1 - vc;
            // advect the fields (MacCormack)
            SetTex(cs, kAdvFwd, "_VelIn", vel[vc]); SetTex(cs, kAdvFwd, "_SclIn", scl[sc]); SetTex(cs, kAdvFwd, "_TmpOut", tmp);
            Dispatch(cs, kAdvFwd);
            SetTex(cs, kAdvBack, "_VelIn", vel[vc]); SetTex(cs, kAdvBack, "_SclIn", scl[sc]); SetTex(cs, kAdvBack, "_TmpIn", tmp);
            SetTex(cs, kAdvBack, "_SclOut", scl[1 - sc]);
            Dispatch(cs, kAdvBack); sc = 1 - sc;
        }

        // ------------------------------------------------------------------ frame
        private int failures;
        private bool disabledByErrors;

        // A fault in this module must never take the frame down: the first one is logged with its stack, three in a row switch the
        // module off for the rest of the session (the old rendering paths carry on).
        private void LateUpdate()
        {
            if (disabledByErrors) return;
            try { LateUpdateBody(); failures = 0; }
            catch (System.Exception e)
            {
                failures++;
                if (failures == 1) Debug.LogError("[PlumeTrails] pad fluid failed: " + e);
                if (failures >= 3)
                {
                    disabledByErrors = true; broken = true;
                    Debug.LogError("[PlumeTrails] pad fluid switched off for this session after repeated errors");
                    try { Free(); } catch { }
                }
            }
        }

        private void LateUpdateBody()
        {
            float now = Time.time;
            if (!Wanted || !ModSettings.Enabled)
            {
                if (hasGrid) Free();
                return;
            }

            // the engines that report right now
            active.Clear();
            foreach (KeyValuePair<int, Src> kv in sources)
                if (now - kv.Value.t < 0.5f && active.Count < 8) active.Add(kv.Value);
            if (active.Count > 0) lastActive = now;

            if (!hasGrid)
            {
                if (active.Count > 0) CreateGrid(active[0]);
                else if (now >= nextPrewarmCheck) { nextPrewarmCheck = now + 1f; TryPrewarm(now); }
                if (!hasGrid) return;
                warmSteps = 0; everInjected = false;
            }
            // a vessel standing on its pad keeps the pre-rendered grid alive
            if (now >= nextOnPadCheck)
            {
                nextOnPadCheck = now + 0.5f;
                Vessel av = FlightGlobals.ActiveVessel;
                onPad = av != null && (av.situation == Vessel.Situations.PRELAUNCH || av.situation == Vessel.Situations.LANDED) && av.srfSpeed < 2.0;
            }
            if (active.Count == 0 && onPad && !everInjected) lastActive = now;
            if (!UpdateFrame()) return;

            // the cloud outlives the rocket; it is dropped when it has had its time, or the rocket launches somewhere else
            if (active.Count == 0 && now - lastActive > Mathf.Max(SmokeTuning.PadFluidLife, 10f)) { Free(); return; }
            if (active.Count > 0)
            {
                Vector3 l = ToLocal(active[0].impact);
                float ex = nx * cell, ez = nz * cell;
                if (l.x < 0.04f * ex || l.x > 0.96f * ex || l.z < 0.04f * ez || l.z > 0.96f * ez)
                {
                    CreateGrid(active[0]);
                    if (!hasGrid || !UpdateFrame()) return;
                }
            }

            BakeHeights();
            if (!heightReady) return;
            // the scenery is drawn when the pad's meshes are there; redo it a few times while the scene settles
            if (rebakes < (SmokeTuning.PadFluidDebug ? 4 : 2) && now >= nextRebake)
            {
                RebakeVisibleHeights(); rebakes++; nextRebake = now + 1.5f;
                if (rebakes == 2 || SmokeTuning.PadFluidDebug) DumpVisibleHeights();
            }

            // THE GEOMETRY IN 3D: the scenery peeled in layers (the roof of the mount, its underside, the floor of the flame trench under it ...),
            // a solid grid and its signed distance field. Built once, after the scenery has settled (the visible heights have been baked twice).
            if (!voxTried && rebakes >= 2 && SmokeTuning.PadVoxel) BuildVoxels();

            if (now >= nextStatus && (active.Count > 0 || everInjected))
            {
                nextStatus = now + 4f;
                Debug.Log("[PlumeTrails] PadFluid status: " + (Usable ? "solver" : "surface mode") + ", engines reporting " + active.Count + ", drawing " + Drawing + ", everInjected " + everInjected
                          + ", solver steps " + statsSteps + ", simAcc " + simAcc.ToString("F2") + ", holes " + holeCount + ", wave " + (shockOn ? "running r=" + shockR.ToString("F0") : "off") + ", fps " + (1f / Mathf.Max(Time.unscaledDeltaTime, 0.001f)).ToString("F0"));
            }
            ComputeShader cs = ShaderCache.PadFluidCompute;
            // the jets as segments (local coordinates)
            int ns = 0;
            int ns0 = 0;
            for (int i = 0; i < active.Count && ns < 8; i++)
            {
                Src s = active[i];
                // The jet is injected FROM THE NOZZLE along the exhaust, as far as it can go: the grid's solid cells (the mount, the trench
                // walls, the deflector) stop it where it really hits. A ray to "the" impact point found the structure right under the
                // engines (the launch mount, 1 m below the nozzle) and not the floor of the trench the jet passes through.
                Vector3 nz = ToLocal(s.nozzle);
                Vector3 dirL = Vector3.down;
                const float len = 170f;
                Vector3 a = nz, b = nz + dirL * len;
                float rA = Mathf.Max(s.jetR * 0.35f, 1.5f) * SmokeTuning.PadFluidSize, rB = Mathf.Min(rA + 0.12f * len * SmokeTuning.PadFluidSpread, 4f * s.jetR * SmokeTuning.PadFluidSize);
                // the jet leaves the nozzle at ~2.5-3 km/s; the grid cannot carry that, but the pad cloud has to be THROWN, not drift: the
                // jet speed here is a few hundred m/s (the solid cells and the pressure solve turn and slow it into the wall jet)
                float uj = Mathf.Min(480f, 120f + 0.012f * s.thrKN) * SmokeTuning.PadFluidJet;
                float mass = 110f;
                srcA[ns] = new Vector4(a.x, a.y, a.z, rA);
                srcB[ns] = new Vector4(b.x, b.y, b.z, rB);
                srcC[ns] = new Vector4(uj, mass * Mathf.Max(s.water, 0.7f), mass * s.soot * 0.10f, Mathf.Clamp01(s.hot));
                srcD[ns] = new Vector4(dirL.x, dirL.y, dirL.z, 0.05f * SmokeTuning.PadFluidSpread);
                srcE[ns] = new Vector4(0.12f, 0f, 0f, 0f);
                ns++; ns0 = ns;
            }

            if (active.Count > 0) lastSurfaceY = ToLocal(active[0].impact).y;

            // a flame hole under every nozzle, down to the lowest surface found around it (the floor of the trench)
            holeCount = 0;
            for (int i = 0; i < active.Count && holeCount < 8; i++)
            {
                Src hs = active[i];
                Vector3 hn = ToLocal(hs.nozzle);
                float rHole = Mathf.Max(hs.jetR, 6f);
                float floorY = oy;
                float scan = 2.5f * rHole;
                int i0 = Mathf.Clamp(Mathf.FloorToInt((hn.x - scan) / cell), 0, nx - 1), i1 = Mathf.Clamp(Mathf.CeilToInt((hn.x + scan) / cell), 0, nx - 1);
                int k0 = Mathf.Clamp(Mathf.FloorToInt((hn.z - scan) / cell), 0, nz - 1), k1 = Mathf.Clamp(Mathf.CeilToInt((hn.z + scan) / cell), 0, nz - 1);
                float lowest = float.MaxValue;
                for (int kk = k0; kk <= k1; kk++)
                    for (int ii = i0; ii <= i1; ii++) lowest = Mathf.Min(lowest, lowCols[kk * nx + ii]);
                if (lowest < float.MaxValue) floorY = Mathf.Max(oy, lowest);
                holeA[holeCount] = new Vector4(hn.x, hn.y, hn.z, rHole);
                holeB[holeCount] = new Vector4(floorY, 0f, 0f, 0f);
                holeCount++;
            }

            // the ignition wave starts with the engines, and again when a bigger engine joins (the boosters light after the main engines):
            // the reference thrust follows the current one slowly, so a sudden jump over it is a new ignition
            if (active.Count > 0)
            {
                float thrNow = 0f;
                for (int i = 0; i < active.Count; i++) thrNow += active[i].thrKN;
                if (SmokeTuning.PadFluidShock && (thrRef < 1f || (thrNow > 2.2f * thrRef && thrNow - thrRef > 2500f)))
                {
                    shockOn = true; shockR = 0f; shockPrev = 0f; shockRVis = 0f;
                    shockC = ToLocal(active[0].impact) + Vector3.up * 3f;
                    shockAmp = Mathf.Clamp(14f * Mathf.Sqrt(thrNow / 40000f), 3f, 24f) * SmokeTuning.PadFluidShockStrength;
                    Debug.Log("[PlumeTrails] PadFluid: ignition wave, thrust " + thrNow.ToString("F0") + " kN, induced velocity " + shockAmp.ToString("F1") + " m/s at 30 m");
                    thrRef = thrNow;
                }
                else { if (thrRef < 1f) thrRef = thrNow; thrRef = Mathf.Lerp(thrRef, thrNow, 1f - Mathf.Exp(-Time.deltaTime / 2f)); }
            }
            else thrRef = Mathf.Lerp(thrRef, 0f, 1f - Mathf.Exp(-Time.deltaTime / 4f));
            if (shockOn || shockRVis < ShockMaxR * SmokeTuning.PadFluidShockSize) shockRVis += SoundSpeed * SmokeTuning.PadFluidShockSpeed * Time.deltaTime;
            if (active.Count > 0) everInjected = true;
            if (!Usable)
            {
                // surface mode: no solver. The wave front advances for the puffs (SmokeVolumeGroup applies it), nothing is drawn here
                dbgPath = "SURFACE MODE (no solver: PadFluid GPU cloud is off or unusable)";
                shockPrev = shockR;
                if (shockOn) { shockR += SoundSpeed * SmokeTuning.PadFluidShockSpeed * Time.deltaTime; if (shockR > ShockMaxR * SmokeTuning.PadFluidShockSize) shockOn = false; }
                DebugView(0);
                return;
            }
            if (!everInjected)
            {
                // warm-up: run the kernels a few times, empty, so that the first real step does not stall on compiling them
                dbgPath = "WARM-UP (no engine has reported yet, everInjected=false)";
                if (warmSteps < 3) { Step(cs, SimDt, 0); warmSteps++; }
                if (go != null) go.SetActive(false);
                SmokeRenderRegistry.SetActive(mr, false);
                DebugView(0);
                return;
            }

            // The flow runs in REAL time whatever the frame rate: the game here runs at 10-15 fps, and a fixed step of 0.05 s with a cap of two per
            // frame advanced it at half speed (the cloud looked frozen). Steps are as long as the time that has passed (at most 0.1 s: the
            // semi-Lagrangian advection and the projection are stable for it), and at a high frame rate they are combined (every ~0.03 s).
            simAcc += Mathf.Min(Time.deltaTime, 0.1f);
            if (simAcc >= 0.03f)
            {
                float stepDt = Mathf.Min(simAcc, 0.1f);
                Step(cs, stepDt, ns); statsSteps++;
                ns = 0;
                simAcc = 0f;
            }
            dbgPath = "SOLVER (simAcc " + simAcc.ToString("F3") + ", deltaTime " + Time.deltaTime.ToString("F3") + ", ns " + ns + ", ns0 " + ns0 + ")";

            Draw(now);
            lastDraw = now;
            DebugView(ns0);
        }

        private void Draw(float now)
        {
            if (go == null || mr == null) return;
            go.SetActive(true);
            SmokeRenderRegistry.SetActive(mr, true);
            go.transform.position = corner + e1 * (nx * cell * 0.5f) + upv * (oy + ny * cell * 0.5f) + e2 * (nz * cell * 0.5f);
            go.transform.rotation = Quaternion.LookRotation(e2, upv);
            go.transform.localScale = new Vector3(nx * cell, ny * cell, nz * cell);

            Src ref0 = active.Count > 0 ? active[0] : null;
            Vector3 firePos = ref0 != null ? ref0.impact + upv * 6f : corner + e1 * (nx * cell * 0.5f) + e2 * (nz * cell * 0.5f);
            float range = ref0 != null ? Mathf.Max(60f, 5f * ref0.jetR) : 120f;
            Color fc = ref0 != null ? ref0.flame : new Color(1f, 0.56f, 0.22f);
            float fa = ref0 != null ? Mathf.Clamp(ref0.light, 0f, 3f) * 0.6f * Mathf.Clamp01(ref0.hot + 0.2f) : 0f;

            mb.SetTexture("_FluidScl", scl[sc]);
            mb.SetVector("_FluidAnchor", corner);
            mb.SetVector("_FluidE1", e1); mb.SetVector("_FluidUp", upv); mb.SetVector("_FluidE2", e2);
            mb.SetVector("_FluidDim", new Vector4(nx, ny, nz, cell));
            mb.SetVector("_FluidOY", new Vector4(oy, 0, 0, 0));
            mb.SetVector("_FluidLight", new Vector4(firePos.x, firePos.y, firePos.z, range));
            mb.SetVector("_FluidLightCol", new Vector4(fc.r * fa, fc.g * fa, fc.b * fa, 0f));
            mb.SetVector("_FluidLook", new Vector4(SmokeTuning.PadFluidDensity, SmokeTuning.PadFluidErode, Mathf.Clamp(Mathf.RoundToInt(SmokeTuning.PadFluidSteps * SmokeQuality.Current.marchScale), 20, 160), SmokeTuning.PadFluidExposure));
            mb.SetVector("_FluidLook2", new Vector4(SmokeTuning.PadFluidFire, 0.15f, SmokeTuning.PadFluidSun, SmokeTuning.PadFluidAmbient));
            if (Time.time >= lookStamp)
            {
                // the trail's own colours (sky / ground ambient, shadow, sun-lit), read from the block the trail material gets
                lookStamp = Time.time + 0.5f;
                if (lookBlock == null) lookBlock = new MaterialPropertyBlock();
                SmokeTuning.Apply(lookBlock);
                lookSky = lookBlock.GetColor("_AmbientSkyColor"); lookGnd = lookBlock.GetColor("_AmbientGroundColor");
                lookShadow = lookBlock.GetColor("_ShadowColor"); lookSun = lookBlock.GetColor("_SunlitColor");
            }
            mb.SetColor("_FluidAmbSky", lookSky); mb.SetColor("_FluidAmbGnd", lookGnd);
            mb.SetColor("_FluidShadowC", lookShadow); mb.SetColor("_FluidSunC", lookSun);
            mb.SetVector("_FluidLook4", new Vector4(SmokeTuning.Washout, SmokeTuning.SteamWhite, SmokeTuning.SkyTintStrength, SmokeTuning.ShadowStrength));
            mb.SetVector("_FluidLook3", new Vector4(SmokeTuning.AmbientFloor, SmokeTuning.SteamBright, SmokeTuning.SunlitBrightness, SmokeTuning.NightAmbient));
            float ringK = SmokeTuning.PadFluidShock && shockRVis < ShockMaxR * SmokeTuning.PadFluidShockSize ? Mathf.Clamp01(1f - shockRVis / (ShockMaxR * SmokeTuning.PadFluidShockSize)) * Mathf.Clamp01(shockAmp / 12f) : 0f;
            mb.SetVector("_FluidShock", new Vector4(shockC.x, shockC.y, shockC.z, shockRVis));
            mb.SetVector("_FluidShock2", new Vector4(ringK, Mathf.Max(3f, cell * 1.2f), 0f, 0f));
            mb.SetVector("_FluidSeed", new Vector4(0.6f * now, -1.4f * now, 0.4f * now, now));
            mr.SetPropertyBlock(mb);
        }
    }
}
