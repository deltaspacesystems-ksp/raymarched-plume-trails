using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // The stock explosion prefabs are replaced by copies with their renderers off (sound and logic stay) plus this marker.
    // When FXMonger spawns one, the marker tells PlumeExplosions where and roughly how big the blast was.
    public class StockExplosionMarker : MonoBehaviour
    {
        public int index;
        private void Start() { PlumeExplosions.OnStockExplosion(transform.position, index); }
    }

    // Volumetric explosions, drawn by the same raymarcher as the smoke. A blast is a few expanding spheres (a fireball that
    // glows by a heat ramp and a darker, longer-lived smoke cloud), each made of a core plus lobes so the outline is lumpy.
    // Sizes follow the cube root of the energy (here: the mass of what burned), the way real fireballs scale.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class PlumeExplosions : MonoBehaviour
    {
        // ---------------------------------------------------------------- stock swap
        private static PlumeExplosions instance;
        private GameObject holder;
        private GameObject[] originalExplosions, originalDebris;
        private bool swapped;
        private float lastSwapWarn;

        private struct DyingPart { public float time, kg; public Vector3 pos; public bool used; }
        private static readonly List<DyingPart> dying = new List<DyingPart>();
        private struct Pending { public Vector3 pos; public float time; public int index; }
        private static readonly List<Pending> pending = new List<Pending>();

        private void Awake()
        {
            instance = this;
            // each subscription on its own: a GameEvents field that is null in some KSP build must not stop the rest
            Guard("onPartWillDie", () => GameEvents.onPartWillDie.Add(OnPartWillDie));
            Guard("onPartExplode", () => GameEvents.onPartExplode.Add(OnExplodeEvent));
            Guard("onPartExplodeGroundCollision", () => GameEvents.onPartExplodeGroundCollision.Add(OnExplodePart));
            Guard("onVesselExplodeGroundCollision", () => GameEvents.onVesselExplodeGroundCollision.Add(OnExplodeVessel));
            Guard("onCrash", () => GameEvents.onCrash.Add(OnReport));
            Guard("onCrashSplashdown", () => GameEvents.onCrashSplashdown.Add(OnReport));
        }

        private static void Guard(string what, System.Action a)
        {
            try { a(); }
            catch (System.Exception e) { Debug.LogWarning("[PlumeTrails] explosions: cannot subscribe to " + what + " (" + e.GetType().Name + ")"); }
        }

        // Anything that blows a part up arms a short window; parts dying inside it are the blast. (Another mod can bypass
        // the stock effect prefabs, so the prefab marker alone is not a reliable trigger.)
        private static float armedUntil;
        private static void Arm(string why) { armedUntil = Time.time + 0.6f; if (SmokeTuning.DebugLogging) Debug.Log("[PlumeTrails] explosion armed by " + why); }
        private static void OnExplodeEvent(GameEvents.ExplosionReaction r) { Arm("onPartExplode"); }
        private static void OnExplodePart(Part p) { Arm("onPartExplodeGroundCollision"); }
        private static void OnExplodeVessel(Vessel v) { Arm("onVesselExplodeGroundCollision"); }
        private static void OnReport(EventReport r) { Arm("crash"); }

        private void OnDestroy()
        {
            Guard("remove onPartWillDie", () => GameEvents.onPartWillDie.Remove(OnPartWillDie));
            Guard("remove onPartExplode", () => GameEvents.onPartExplode.Remove(OnExplodeEvent));
            Guard("remove onPartExplodeGroundCollision", () => GameEvents.onPartExplodeGroundCollision.Remove(OnExplodePart));
            Guard("remove onVesselExplodeGroundCollision", () => GameEvents.onVesselExplodeGroundCollision.Remove(OnExplodeVessel));
            Guard("remove onCrash", () => GameEvents.onCrash.Remove(OnReport));
            Guard("remove onCrashSplashdown", () => GameEvents.onCrashSplashdown.Remove(OnReport));
            Restore();
            for (int i = 0; i < effects.Count; i++) effects[i].Dispose();
            effects.Clear();
            dying.Clear(); pending.Clear();
            if (instance == this) instance = null;
            if (holder != null) Destroy(holder);
        }

        private void Update()
        {
            bool want = SmokeTuning.ExplosionsEnabled && ShaderCache.SmokeVolumeShader != null;
            if (want && !swapped) Swap();
            else if (!want && swapped) Restore();

            float now = Time.time;
            for (int i = dying.Count - 1; i >= 0; i--) if (now - dying[i].time > 4f) dying.RemoveAt(i);

            // a blast waits a moment so the parts that die with it can be summed into one energy
            for (int i = 0; i < pending.Count; i++)
            {
                if (now - pending[i].time < 0.15f) continue;
                Pending p = pending[i];
                float kg = 0f;
                bool any = false;
                for (int d = 0; d < dying.Count; d++)
                {
                    DyingPart dp = dying[d];
                    if (dp.used || Mathf.Abs(dp.time - p.time) > 1.2f) continue;
                    if ((dp.pos - p.pos).sqrMagnitude > 60f * 60f) continue;
                    kg += dp.kg; any = true;
                    dp.used = true; dying[d] = dp;
                }
                // blasts of the same event close to this one are merged into it
                for (int j = pending.Count - 1; j > i; j--)
                {
                    if ((pending[j].pos - p.pos).sqrMagnitude < 40f * 40f && Mathf.Abs(pending[j].time - p.time) < 0.6f) pending.RemoveAt(j);
                }
                if (!any) kg = 400f * (1 + p.index) * (1 + p.index);
                Spawn(p.pos, kg);
                pending.RemoveAt(i);
                i--;
            }
        }

        private static void OnPartWillDie(Part p)
        {
            if (p == null) return;
            float kg = 1000f * (0.3f * p.mass + (float)p.GetResourceMass());
            dying.Add(new DyingPart { time = Time.time, kg = Mathf.Max(kg, 20f), pos = p.transform.position });
            if (SmokeTuning.ExplosionsEnabled && Time.time < armedUntil)
                pending.Add(new Pending { pos = p.transform.position, time = Time.time, index = 0 });
        }

        public static void OnStockExplosion(Vector3 pos, int index)
        {
            if (!SmokeTuning.ExplosionsEnabled) return;
            pending.Add(new Pending { pos = pos, time = Time.time, index = index });
        }

        private GameObject[] StripCopies(GameObject[] src, bool marker)
        {
            if (src == null) return null;
            GameObject[] res = new GameObject[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                if (src[i] == null) continue;
                GameObject c = Instantiate(src[i]);
                c.name = src[i].name;
                c.transform.SetParent(holder.transform, false);
                foreach (Renderer r in c.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (Light l in c.GetComponentsInChildren<Light>(true)) l.enabled = false;
                if (marker) c.AddComponent<StockExplosionMarker>().index = i;
                res[i] = c;
            }
            return res;
        }

        private void Swap()
        {
            FXMonger fx = FindObjectOfType<FXMonger>();
            if (fx == null || fx.explosions == null)
            {
                if (Time.time - lastSwapWarn > 5f) { lastSwapWarn = Time.time; Debug.Log("[PlumeTrails] explosions: FXMonger not ready (stock effect not replaced yet)"); }
                return;
            }
            if (holder == null) { holder = new GameObject("PlumeTrails.ExplosionHolder"); holder.SetActive(false); }
            originalExplosions = fx.explosions;
            originalDebris = fx.debrisExplosion;
            fx.explosions = StripCopies(originalExplosions, true);
            if (originalDebris != null) fx.debrisExplosion = StripCopies(originalDebris, false);
            swapped = true;
            Debug.Log("[PlumeTrails] explosions: stock FX replaced (" + fx.explosions.Length + " prefabs)");
        }

        private void Restore()
        {
            if (!swapped) return;
            swapped = false;
            FXMonger fx = FindObjectOfType<FXMonger>();
            if (fx == null) return;
            if (originalExplosions != null) fx.explosions = originalExplosions;
            if (originalDebris != null) fx.debrisExplosion = originalDebris;
        }

        // ---------------------------------------------------------------- effects
        private static readonly List<Effect> effects = new List<Effect>();
        private const int MaxEffects = 6;

        private static void Spawn(Vector3 worldPos, float kg)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || body.bodyTransform == null) return;
            if (effects.Count >= MaxEffects) { effects[0].Dispose(); effects.RemoveAt(0); }
            effects.Add(new Effect(body, worldPos, kg));
            Debug.Log(string.Format("[PlumeTrails] explosion: {0:F0} kg, fireball radius {1:F0} m", kg, effects[effects.Count - 1].fireR));
        }

        // Called every frame by HalfResSmokeRenderer, right after the trails are uploaded.
        public static void RenderAll()
        {
            for (int i = effects.Count - 1; i >= 0; i--)
            {
                if (!effects[i].Update(Time.deltaTime)) { effects[i].Dispose(); effects.RemoveAt(i); }
            }
        }

        private static float Env(float a, float start, float peak, float peakAt, float end)
        {
            a = Mathf.Clamp01(a);
            if (a < peakAt) return Mathf.Lerp(start, peak, Smooth(a / peakAt));
            return Mathf.Lerp(peak, end, Smooth((a - peakAt) / Mathf.Max(1f - peakAt, 1e-4f)));
        }
        private static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        private class Effect
        {
            private const int MaxPts = 320;
            private readonly CelestialBody body;
            private readonly Vector3 local;
            public readonly float fireR, smokeR;
            private readonly float fireLife, smokeLife;
            private float t;
            private readonly Vector3[] fireDir = new Vector3[5];
            private readonly float[] fireSz = new float[5];
            private readonly Vector3[] smokeDir = new Vector3[9];
            private readonly float[] smokeSz = new float[9];
            private readonly float seed;

            private GameObject go;
            private MeshRenderer mr;
            private MaterialPropertyBlock mb;
            private Light light;
            private readonly Vector4[] pts = new Vector4[MaxPts];
            private readonly float[] radii = new float[MaxPts];
            private readonly Vector4[] groups = new Vector4[MaxPts / 16];
            private readonly Vector3[] pos = new Vector3[MaxPts];
            private int n;

            public Effect(CelestialBody body, Vector3 worldPos, float kg)
            {
                this.body = body;
                local = body.bodyTransform.InverseTransformPoint(worldPos);
                float scale = Mathf.Max(SmokeTuning.ExplosionScale, 0.1f);
                fireR = Mathf.Max(2.5f, scale * Mathf.Pow(Mathf.Max(kg, 10f), 1f / 3f));
                smokeR = fireR * 1.25f;
                float lifeK = Mathf.Clamp(Mathf.Pow(fireR / 56f, 0.4f), 0.35f, 1.3f) * Mathf.Max(SmokeTuning.ExplosionLife, 0.2f);
                fireLife = 7f * lifeK;
                smokeLife = 32f * lifeK;

                Random.State st = Random.state;
                Random.InitState(Mathf.RoundToInt(kg * 13.7f) ^ Time.frameCount);
                seed = Random.value * 100f;
                for (int i = 0; i < fireDir.Length; i++)
                {
                    fireDir[i] = RandUp(0.15f); fireSz[i] = Random.Range(0.45f, 0.65f);
                }
                for (int i = 0; i < smokeDir.Length; i++)
                {
                    smokeDir[i] = RandUp(0.0f); smokeSz[i] = Random.Range(0.45f, 0.65f);
                }
                Random.state = st;

                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "PlumeTrails.Explosion";
                Collider c = go.GetComponent<Collider>();
                if (c != null) DestroyImmediate(c);
                mr = go.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = false;
                Material m = new Material(ShaderCache.SmokeVolumeShader);
                m.EnableKeyword("SMOKE_VOLUME_POLYLINE");
                m.SetFloat("_ZTestMode", 8f);
                mr.material = m;
                mb = new MaterialPropertyBlock();

                GameObject lg = new GameObject("flash");
                lg.transform.SetParent(go.transform, false);
                light = lg.AddComponent<Light>();
                light.type = LightType.Point;
                light.shadows = LightShadows.None;
                light.color = new Color(1f, 0.72f, 0.3f);
            }

            private static Vector3 RandUp(float minY)
            {
                Vector3 v = Random.onUnitSphere;
                if (v.y < minY) v.y = Mathf.Abs(v.y) + minY;
                return v.normalized;
            }

            public void Dispose()
            {
                if (mr != null) SmokeRenderRegistry.Remove(mr);
                if (go != null) Destroy(go);
                go = null; mr = null;
            }

            private void AddBlob(Vector3 p, float r, float d, float heat)
            {
                if (n + 4 > MaxPts || r < 0.05f) return;
                float w = Mathf.Clamp(d, 0.03f, 1.4f) + 16f * Mathf.RoundToInt(Mathf.Clamp01(heat) * 7f);
                pos[n] = p; radii[n] = 0f; pts[n] = new Vector4(p.x, p.y, p.z, 1f); n++;
                pos[n] = p; radii[n] = r; pts[n] = new Vector4(p.x, p.y, p.z, w); n++;
                Vector3 q = p + Vector3.up * Mathf.Max(0.03f, r * 0.02f);
                pos[n] = q; radii[n] = r; pts[n] = new Vector4(q.x, q.y, q.z, w); n++;
            }

            private void Close()
            {
                if (n == 0 || n >= MaxPts) return;
                pos[n] = pos[n - 1]; radii[n] = 0f; pts[n] = new Vector4(pos[n].x, pos[n].y, pos[n].z, 1f); n++;
            }

            public bool Update(float dt)
            {
                t += dt;
                if (t >= smokeLife) return false;
                Vector3 world = body.bodyTransform.TransformPoint(local);
                Vector3 up = (world - (Vector3)body.position).normalized;
                // spheres are built in an up-aligned frame: rotate offsets so "up" is the planet's up
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, up);

                n = 0;
                float af = t / fireLife;
                float fireFade = Env(af, 1f, 1f, 0.27f, 0f);
                if (af < 1f && fireFade > 0.01f)
                {
                    float r = fireR * Env(af, 6f / 56f, 1f, 0.0871f, 85f / 56f);
                    float dens = Env(af, 0.8f, 0.5f, 0.19f, 0.03f) * 1.7f * fireFade;
                    float heat = Mathf.Pow(Mathf.Clamp01(1f - af / 0.5f), 0.7f);
                    Vector3 c = world + up * (0.3f * fireR + 0.45f * fireR * (1f - Mathf.Exp(-t / 1.6f)));
                    AddBlob(c, r, dens, heat);
                    for (int i = 0; i < fireDir.Length; i++)
                        AddBlob(c + rot * (fireDir[i] * r * 0.8f), r * fireSz[i], dens * 0.9f, heat * 0.9f);
                }
                Close();

                float asm = t / smokeLife;
                float sFade = Env(asm, 1f, 1f, 0.6f, 0f);
                {
                    float r = smokeR * Env(asm, 6f / 70f, 1f, 0.025f, 95f / 70f);
                    float dens = Env(asm, 0.22f, 0.82f, 0.18f, 0f) * 1.4f * sFade * Smooth((t - 0.05f * fireLife) / (0.3f * fireLife));
                    float rise = 1.3f * smokeR * (1f - Mathf.Exp(-t / 5f));
                    Vector3 c = world + up * (0.35f * smokeR + rise);
                    AddBlob(c, r, dens, 0f);
                    for (int i = 0; i < smokeDir.Length; i++)
                        AddBlob(c + rot * (smokeDir[i] * r * 0.75f), r * smokeSz[i], dens * 0.9f, 0f);
                    // the stem the cloud rises on, fading as it lifts off
                    float stem = Mathf.Clamp01(1f - t / 6f);
                    if (stem > 0.05f)
                        AddBlob(world + up * (0.25f * smokeR + rise * 0.4f), r * 0.5f * stem + 0.5f, dens * stem, 0f);
                }
                Close();
                if (n < 2) return false;

                // bounds
                float warp = (SmokeTuning.SilhouetteWarpStrength + SmokeTuning.VortexStrength) * 1.733f;
                Vector3 bmin = Vector3.positiveInfinity, bmax = Vector3.negativeInfinity;
                float rSum = 0f, rMax = 0f; int rCount = 0;
                for (int i = 0; i < n; i++)
                {
                    float r = radii[i];
                    float b = r * 2.9f + warp + 0.5f * r + 10f;
                    bmin = Vector3.Min(bmin, pos[i] - Vector3.one * b);
                    bmax = Vector3.Max(bmax, pos[i] + Vector3.one * b);
                    if (r > 0f) { rSum += r; rCount++; if (r > rMax) rMax = r; }
                }
                for (int i = n; i < MaxPts; i++) radii[i] = 0f;

                int last = n - 1;
                int gc = Mathf.Min((last + 15) / 16, groups.Length);
                for (int g = 0; g < gc; g++)
                {
                    int s = g * 16, e = Mathf.Min(s + 16, last), lp = Mathf.Min(e, n - 1);
                    Vector3 mn = pos[s], mx = mn;
                    for (int i = s + 1; i <= lp; i++) { mn = Vector3.Min(mn, pos[i]); mx = Vector3.Max(mx, pos[i]); }
                    Vector3 ctr = (mn + mx) * 0.5f;
                    float reach = 0f;
                    for (int i = s; i <= lp; i++)
                    {
                        float rr = Vector3.Distance(ctr, pos[i]) + radii[i] * 1.4f * (1f + Mathf.Clamp01(SmokeTuning.EdgeProtrusion)) + warp + 0.6f * radii[i] + 10f;
                        if (rr > reach) reach = rr;
                    }
                    groups[g] = new Vector4(ctr.x, ctr.y, ctr.z, reach);
                }

                Vector3 bc = (bmin + bmax) * 0.5f, be = (bmax - bmin) * 0.5f;
                SmokeRenderRegistry.SetActive(mr, true);
                go.transform.position = bc;
                go.transform.localScale = be * 2f;

                mb.Clear();
                mb.SetInt("_SpineCount", n);
                mb.SetVectorArray("_SpinePoints", pts);
                mb.SetFloatArray("_SpineRadii", radii);
                mb.SetInt("_SpineGroupCount", gc);
                mb.SetVectorArray("_SpineGroupBounds", groups);
                mb.SetVector("_BoxCenter", new Vector4(bc.x, bc.y, bc.z, 0f));
                mb.SetVector("_BoxExtents", new Vector4(be.x, be.y, be.z, 0f));
                mb.SetFloat("_TileRadiusRatio", Mathf.Clamp((rCount > 0 ? rSum / rCount : 1f) / 18f, 0.05f, 200f));
                mb.SetFloat("_MaxSpineRadius", rMax);
                mb.SetVector("_FlameLight", Vector4.zero);
                mb.SetVector("_FlameColor", new Vector4(1f, 0.6f, 0.25f, 25f));
                SmokeTuning.Apply(mb);
                mb.SetFloat("_Density", SmokeTuning.Density * 1.3f);
                mb.SetVector("_SmokeNoiseOffset", new Vector4(seed, seed * 0.37f, seed * 0.71f, 0f));
                mb.SetInt("_MarchSteps", 40);
                // soot: dark grey that lightens as it disperses (KSA's colour stops, but tinted by the sun and sky)
                float cs = Mathf.Lerp(0.42f, 0.2f, Smooth(asm / 0.27f)) * (asm > 0.27f ? Mathf.Lerp(1f, 0.55f, Smooth((asm - 0.27f) / 0.73f)) : 1f);
                mb.SetColor("_SunlitColor", new Color(cs * 1.05f, cs, cs * 0.95f, 1f));
                mb.SetColor("_ShadowColor", new Color(cs * 0.3f, cs * 0.3f, cs * 0.34f, 1f));
                mb.SetFloat("_Washout", 0.04f);
                mb.SetFloat("_WashoutDesaturate", 0f);
                mb.SetFloat("_FireEmission", SmokeTuning.ExplosionFire * 3.2f);
                mr.SetPropertyBlock(mb);
                SmokeRenderRegistry.Register(mr, mb);

                // flash
                float fl = Mathf.Exp(-t / 0.45f);
                light.transform.position = world + up * (0.4f * fireR);
                light.range = 8f * fireR + 60f;
                light.intensity = 6f * fl * SmokeTuning.ExplosionFire;
                light.enabled = fl > 0.02f;
                return true;
            }
        }
    }
}
