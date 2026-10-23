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

    // What burned: the shares (of the FUEL mass; the oxidiser burns with it and does not count) of the kinds of propellant, which decide the colour
    // of the smoke and how much fire there is and for how long. Hydrogen and methane burn clean: a white cloud of water vapour (and CO2) and a big,
    // slowly fading fireball; kerosene and solid fuel leave soot (a dark smoke).
    public struct FuelMix
    {
        public float h2, ch4, hc, solid, hyp;
        public static FuelMix Default { get { FuelMix m = new FuelMix(); m.hc = 1f; return m; } }
        public float Total { get { return h2 + ch4 + hc + solid + hyp; } }
        public void Add(FuelMix o, float w) { h2 += o.h2 * w; ch4 += o.ch4 * w; hc += o.hc * w; solid += o.solid * w; hyp += o.hyp * w; }
        public FuelMix Normalized()
        {
            float t = Total;
            if (t <= 1e-6f) return Default;
            FuelMix m = this; m.h2 /= t; m.ch4 /= t; m.hc /= t; m.solid /= t; m.hyp /= t;
            return m;
        }
        // 0 = black soot ... 1 = white vapour
        public float Whiteness { get { return Mathf.Clamp01(h2 * 1.0f + ch4 * 0.72f + hyp * 0.35f + hc * 0.0f + solid * 0.08f); } }
        public float FireLife { get { return h2 * 1.6f + ch4 * 1.35f + hc * 1.0f + solid * 1.1f + hyp * 0.9f; } }
        public float FireDensity { get { return h2 * 1.55f + ch4 * 1.3f + hc * 1.0f + solid * 0.95f + hyp * 0.9f; } }
        public float SmokeDensity { get { return h2 * 0.75f + ch4 * 0.85f + hc * 1.0f + solid * 1.1f + hyp * 0.8f; } }
        // the colour of the flame: orange for hydrocarbons, paler and yellower for methane, near white for hydrogen
        public Color Flame { get { return h2 * new Color(1f, 0.80f, 0.62f) + ch4 * new Color(1f, 0.66f, 0.38f) + hc * new Color(1f, 0.58f, 0.24f) + solid * new Color(1f, 0.60f, 0.26f) + hyp * new Color(1f, 0.62f, 0.34f); }}

        // from the resources of a part: the mass of every kind (by the name of the resource, also the RealFuels names)
        // the masses (kg) of oxidiser and of fuel in a part, and the stoichiometric oxidiser / fuel ratio of what it carries
        public static void Propellants(Part p, out float oxKg, out float fuelKg)
        {
            oxKg = 0f; fuelKg = 0f;
            if (p == null || p.Resources == null) return;
            foreach (PartResource r in p.Resources)
            {
                if (r == null || r.info == null) continue;
                float kg = (float)(r.amount * r.info.density * 1000.0);
                if (kg <= 0f) continue;
                string n = r.resourceName.ToLowerInvariant();
                if (n.Contains("oxidizer") || n.Contains("oxidiser") || n.Contains("oxygen") || n == "lox" || n.Contains("lqdox") || n == "nto" || n.Contains("n2o4") || n.Contains("fluorine")) oxKg += kg;
                else if (n.Contains("hydrogen") || n == "lh2" || n.Contains("lqdh2") || n.Contains("methane") || n.Contains("lch4") || n.Contains("lqdch4") || n.Contains("liquidfuel") || n.Contains("kerosene") || n.Contains("rp1") || n.Contains("rp-1")
                         || n.Contains("udmh") || n.Contains("aerozine") || n.Contains("hydrazine") || n.Contains("mono") || n.Contains("ethanol") || n.Contains("alcohol")) fuelKg += kg;
            }
        }

        // oxidiser / fuel by mass at which the propellant burns completely
        public float StoichOF { get { float t = Total; if (t <= 1e-6f) return 3.4f; return (h2 * 8f + ch4 * 4f + hc * 3.4f + hyp * 2.5f) / Mathf.Max(h2 + ch4 + hc + hyp, 1e-6f); } }

        public static FuelMix FromPart(Part p)
        {
            FuelMix m = new FuelMix();
            if (p == null || p.Resources == null) return Default;
            foreach (PartResource r in p.Resources)
            {
                if (r == null || r.info == null) continue;
                float kg = (float)(r.amount * r.info.density * 1000.0);
                if (kg <= 0f) continue;
                string n = r.resourceName.ToLowerInvariant();
                if (n.Contains("hydrogen") || n == "lh2" || n.Contains("lqdh2")) m.h2 += kg;
                else if (n.Contains("methane") || n.Contains("lch4") || n.Contains("lqdch4")) m.ch4 += kg;
                else if (n.Contains("solid") || n.Contains("pban") || n.Contains("apcp")) m.solid += kg;
                else if (n.Contains("mono") || n.Contains("hydrazine") || n.Contains("udmh") || n.Contains("aerozine") || n.Contains("htp") || n.Contains("hydroxyl")) m.hyp += kg;
                else if (n.Contains("liquidfuel") || n.Contains("kerosene") || n.Contains("rp1") || n.Contains("rp-1") || n.Contains("jet") || n.Contains("diesel") || n.Contains("ethanol") || n.Contains("alcohol")) m.hc += kg;
                // oxidiser (LqdOxygen, Oxidizer, NTO ...), electric charge, ore: not a fuel
            }
            return m.Total > 0f ? m : Default;
        }
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
        private FXMonger swappedFx;
        private float nextWatch;
        private int rebuilds;
        private bool swapGivenUp;
        private float lastSwapWarn;

        private struct DyingPart { public float time, kg; public Vector3 pos; public bool used; public FuelMix fuel; public float oxKg, fuelKg; public Vector3d vel; }
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
        private void OnExplodeEvent(GameEvents.ExplosionReaction r) { Arm("onPartExplode"); }
        private void OnExplodePart(Part p) { Arm("onPartExplodeGroundCollision"); }
        private void OnExplodeVessel(Vessel v) { Arm("onVesselExplodeGroundCollision"); }
        private void OnReport(EventReport r) { Arm("crash"); }

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
            for (int i = 0; i < fluids.Count; i++) fluids[i].Dispose();
            fluids.Clear();
            dying.Clear(); pending.Clear();
            if (instance == this) instance = null;
            if (holder != null) Destroy(holder);
        }

        private void Update()
        {
            bool want = ModSettings.Enabled && SmokeTuning.ExplosionsEnabled && ShaderCache.SmokeVolumeShader != null;
            if (!ModSettings.Enabled && (effects.Count > 0 || fluids.Count > 0 || BlastWaves.Active.Count > 0))
            {
                // the mod is switched off: what is burning goes out, the stock effect is put back
                for (int i = 0; i < effects.Count; i++) effects[i].Dispose();
                effects.Clear();
                for (int i = 0; i < fluids.Count; i++) fluids[i].Dispose();
                fluids.Clear();
                BlastWaves.Active.Clear(); BlastDebris.ClearAll(); pending.Clear();
            }
            if (want && !swapped && !swapGivenUp) Swap();
            else if (!want && swapped) Restore();
            if (swapped && Time.unscaledTime >= nextWatch) { nextWatch = Time.unscaledTime + 0.5f; WatchSwap(); }

            float now = Time.time;
            for (int i = dying.Count - 1; i >= 0; i--) if (now - dying[i].time > 4f) dying.RemoveAt(i);

            // a blast waits a moment so the parts that die with it can be summed into one energy
            for (int i = 0; i < pending.Count; i++)
            {
                if (now - pending[i].time < 0.15f) continue;
                Pending p = pending[i];
                float kg = 0f;
                bool any = false;
                FuelMix mixSum = new FuelMix();
                float oxSum = 0f, fuelSum = 0f; Vector3d velSum = Vector3d.zero; float velW = 0f;
                for (int d = 0; d < dying.Count; d++)
                {
                    DyingPart dp = dying[d];
                    if (dp.used || Mathf.Abs(dp.time - p.time) > 1.2f) continue;
                    if ((dp.pos - p.pos).sqrMagnitude > 60f * 60f) continue;
                    kg += dp.kg; any = true;
                    mixSum.Add(dp.fuel.Normalized(), dp.kg);      // weighted by the mass of the part that burned
                    oxSum += dp.oxKg; fuelSum += dp.fuelKg; velSum += dp.vel * dp.kg; velW += dp.kg;
                    dp.used = true; dying[d] = dp;
                }
                // blasts of the same event close to this one are merged into it
                for (int j = pending.Count - 1; j > i; j--)
                {
                    if ((pending[j].pos - p.pos).sqrMagnitude < 40f * 40f && Mathf.Abs(pending[j].time - p.time) < 0.6f) pending.RemoveAt(j);
                }
                if (!any) kg = 400f * (1 + p.index) * (1 + p.index);
                Vector3d vBase = velW > 0f ? velSum / velW : (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.obt_velocity : Vector3d.zero);
                Spawn(p.pos, kg, any ? mixSum.Normalized() : FuelMix.Default, any ? oxSum : -1f, any ? fuelSum : -1f, vBase, false);
                pending.RemoveAt(i);
                i--;
            }
        }

        private void OnPartWillDie(Part p)
        {
            if (p == null) return;
            float kg = 1000f * (0.3f * p.mass + (float)p.GetResourceMass());
            float oxK, fuelK; FuelMix.Propellants(p, out oxK, out fuelK);
            Vector3d pv = p.vessel != null ? p.vessel.obt_velocity : (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.obt_velocity : Vector3d.zero);
            dying.Add(new DyingPart { time = Time.time, kg = Mathf.Max(kg, 20f), pos = p.transform.position, fuel = FuelMix.FromPart(p), oxKg = oxK, fuelKg = fuelK, vel = pv });
            if (ModSettings.Enabled && SmokeTuning.ExplosionsEnabled && Time.time < armedUntil)
                pending.Add(new Pending { pos = p.transform.position, time = Time.time, index = 0 });
        }

        public static void OnStockExplosion(Vector3 pos, int index)
        {
            if (!ModSettings.Enabled || !SmokeTuning.ExplosionsEnabled) return;
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
            swappedFx = fx;
            originalExplosions = fx.explosions;
            originalDebris = fx.debrisExplosion;
            fx.explosions = StripCopies(originalExplosions, true);
            if (originalDebris != null) fx.debrisExplosion = StripCopies(originalDebris, false);
            swapped = true;
            Debug.Log("[PlumeTrails] explosions: stock FX replaced (" + fx.explosions.Length + " prefabs)");
        }

        // The stock FXMonger picks fx.explosions[i] and instantiates it; a replaced prefab that has been destroyed (or an array that was swapped
        // under it) made that throw every frame (1800 exceptions in one session). Check the array twice a second: if anything in it is gone, the
        // stock prefabs are put back and the swap is redone from them (three times at most, then it stays off for the scene).
        private void WatchSwap()
        {
            FXMonger fx = swappedFx != null ? swappedFx : FindObjectOfType<FXMonger>();
            if (fx == null) return;
            bool bad = fx.explosions == null;
            if (!bad)
                for (int i = 0; i < fx.explosions.Length; i++)
                    if (fx.explosions[i] == null && originalExplosions != null && i < originalExplosions.Length && originalExplosions[i] != null) { bad = true; break; }
            if (!bad && fx.debrisExplosion != null && originalDebris != null)
                for (int i = 0; i < fx.debrisExplosion.Length; i++)
                    if (fx.debrisExplosion[i] == null && i < originalDebris.Length && originalDebris[i] != null) { bad = true; break; }
            if (!bad) return;
            Debug.LogWarning("[PlumeTrails] explosions: a replaced stock prefab is gone - the stock ones are put back (" + (rebuilds + 1) + ")");
            Restore();
            if (holder != null) { Destroy(holder); holder = null; }
            rebuilds++;
            if (rebuilds >= 3) { swapGivenUp = true; Debug.LogWarning("[PlumeTrails] explosions: the stock effect stays as it is for this scene"); }
        }

        private void Restore()
        {
            if (!swapped) return;
            swapped = false;
            FXMonger fx = swappedFx != null ? swappedFx : FindObjectOfType<FXMonger>();
            swappedFx = null;
            if (fx == null) return;
            if (originalExplosions != null) fx.explosions = originalExplosions;
            if (originalDebris != null) fx.debrisExplosion = originalDebris;
        }

        // ---------------------------------------------------------------- effects
        private static readonly List<Effect> effects = new List<Effect>();
        private static readonly List<FluidBlast> fluids = new List<FluidBlast>();
        private const int MaxFluidGrids = 4;
        private const int MaxEffects = 8;

        // a test blast in front of the camera (the buttons in the Explosions tab)
        public static void TestBlast(string fuel, float kg, bool vacuum = false)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !ModSettings.Enabled) return;
            FuelMix m = new FuelMix();
            if (fuel == "hydrogen") m.h2 = 1f; else if (fuel == "methane") m.ch4 = 1f; else m.hc = 1f;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            Vector3 up = (Vector3)v.upAxis;
            Vector3 fwd = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, up).normalized : Vector3.forward;
            Vector3 p = v.transform.position + fwd * (120f + Mathf.Pow(kg, 1f / 3f) * 3f);
            RaycastHit hit;
            if (Physics.Raycast(p + up * 300f, -up, out hit, 800f, 1 << 15, QueryTriggerInteraction.Ignore)) p = hit.point;
            // a test blast carries the oxidiser it needs (kerosene 3.4 : 1, methane 4 : 1, hydrogen 8 : 1 by mass of the fuel)
            float fuelPart = kg / (1f + m.StoichOF);
            Spawn(p, kg, m, kg - fuelPart, fuelPart, v.obt_velocity, vacuum);
        }

        // oxKg / fuelKg < 0 = not known (the stock effect without a dying part): the propellant is assumed to be in balance
        private static void Spawn(Vector3 worldPos, float kg, FuelMix mix, float oxKg, float fuelKg, Vector3d vBase, bool forceVacuum)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || body.bodyTransform == null) return;
            // the velocity that was given is the ORBITAL one (the inertial frame); close to the body the scene is the ROTATING frame of the body (the ground
            // stands still in it), so the rotation of the ground (400 m/s at the Cape) has to come off or everything flies away to the east
            if (body.inverseRotation) vBase -= body.getRFrmVel((Vector3d)worldPos);
            BlastWaves.Atmos air = BlastWaves.AirAt(body, worldPos);
            bool vacuum = (!air.air || forceVacuum) && SmokeTuning.ExplosionVacuum;
            FuelMix mxn = mix.Normalized();
            // IN A VACUUM the fuel burns only with the oxidiser that is aboard (no air): the burned share is limited by it. In an atmosphere the air
            // provides the rest. A solid motor carries its own oxidiser.
            float burn = 1f;
            if (vacuum)
            {
                if (mxn.solid > 0.5f) burn = 1f;
                else if (oxKg >= 0f && fuelKg >= 0f) burn = fuelKg > 1e-3f ? Mathf.Clamp01(oxKg / (fuelKg * mxn.StoichOF)) : 0f;
                else burn = 1f;
            }
            float kgEff = Mathf.Max(kg * Mathf.Max(burn, 0.03f), 10f);
            float fireR0 = Mathf.Max(2.5f, Mathf.Max(SmokeTuning.ExplosionScale, 0.1f) * Mathf.Pow(kgEff, 1f / 3f));
            double wTnt = BlastPhysics.TntEquivalent(kg * burn, mxn.h2, mxn.ch4, mxn.hc, mxn.solid, mxn.hyp);
            // the air wave (the lens, the bang, the shake): only where there is air
            if (!vacuum && burn > 0.05f)
            {
                RaycastHit gh; float groundK = 0f;
                if (Physics.Raycast(worldPos + ((Vector3)(worldPos - (Vector3)body.position)).normalized * 2f, -((Vector3)(worldPos - (Vector3)body.position)).normalized, out gh, 40f, 1 << 15, QueryTriggerInteraction.Ignore)) groundK = 1f;
                BlastWaves.Spawn(body, worldPos, wTnt, fireR0, groundK);
                if (groundK > 0f && SmokeTuning.ExplosionDust && BlastWaves.Active.Count > 0)
                {
                    BlastWave last = BlastWaves.Active[BlastWaves.Active.Count - 1];
                    if (effects.Count >= MaxEffects) { effects[0].Dispose(); effects.RemoveAt(0); }
                    effects.Add(new Effect(body, worldPos, kgEff, last.front, last.gamma, last.c, air.pa / (287.0 * air.temp)));
                }
                // a blast close to the ground also shakes it (the seismic wave runs at about 2.5 km/s and is faster than the air)
                if (groundK > 0f)
                {
                    Camera gc = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
                    if (gc != null)
                    {
                        float d = Vector3.Distance(gc.transform.position, worldPos);
                        float amp = 0.5f * Mathf.Pow((float)wTnt, 1f / 3f) / (Mathf.Pow((float)wTnt, 1f / 3f) + 0.08f * d + 8f);
                        BlastWaveDriver.AddShake(d / 2500f, amp, 1.4f, 7f);
                    }
                }
            }
            BlastDebris.Spawn(body, worldPos, vBase, kg, fireR0, vacuum, air.pa / (287.0 * air.temp));
            if (vacuum)
            {
                // the same fluid solver, in its vacuum form (the grid expands with the cloud; no ground, no gravity): no blobs
                if (SmokeTuning.ExplosionFluid && ShaderCache.BlastFluidCompute != null && ShaderCache.PadFluidShader != null && SystemInfo.supportsComputeShaders)
                {
                    FluidBlast.Compute = ShaderCache.BlastFluidCompute; FluidBlast.RenderShader = ShaderCache.PadFluidShader;
                    Vector3 upV = (worldPos - (Vector3)body.position).normalized;
                    float lifeKV = Mathf.Clamp(Mathf.Pow(fireR0 / 56f, 0.4f), 0.35f, 1.3f) * Mathf.Max(SmokeTuning.ExplosionLife, 0.2f);
                    FluidBlast.Fuel fuelV = new FluidBlast.Fuel { white = Mathf.Max(mxn.Whiteness, 0.88f), fireLife = 0.5f, fireDens = mxn.FireDensity * burn, smokeDens = mxn.SmokeDensity, flame = mxn.Flame };
                    Vector3d rel0 = (Vector3d)worldPos - body.position; float tBorn = Time.time;
                    if (fluids.Count >= MaxFluidGrids) { fluids[0].Dispose(); fluids.RemoveAt(0); }
                    FluidBlast fv = new FluidBlast(worldPos, upV, fireR0, fireR0 * 1.25f, 1f, 28f * lifeKV, fuelV, Random.value * 100f, Mathf.Max(SmokeTuning.ExplosionFluidSpeed, 0.6f), true);
                    if (fv.Ready)
                    {
                        fv.VacuumCenter = () => (Vector3)(body.position + rel0 + vBase * (Time.time - tBorn));
                        fv.OnRegister = (r, b) => SmokeRenderRegistry.Register(r, b);
                        fv.OnRemove = r => SmokeRenderRegistry.Remove(r);
                        fv.OnActive = (r, a2) => SmokeRenderRegistry.SetActive(r, a2);
                        fv.Register();
                        fluids.Add(fv);
                        Debug.Log(string.Format("[PlumeTrails] explosion in a VACUUM (fluid, expanding grid): {0:F0} kg of propellant, burned share {1:F2} (oxidiser {2:F0} kg, fuel {3:F0} kg, stoichiometric {4:F1}:1) - free expansion of the gas, no air wave", kg, burn, oxKg, fuelKg, mxn.StoichOF));
                        return;
                    }
                    fv.Dispose();
                }
                if (effects.Count >= MaxEffects) { effects[0].Dispose(); effects.RemoveAt(0); }
                effects.Add(new Effect(body, worldPos, kgEff, mix, true, vBase, burn));
                Debug.Log(string.Format("[PlumeTrails] explosion in a VACUUM: {0:F0} kg of propellant, burned share {1:F2} (oxidiser {2:F0} kg, fuel {3:F0} kg, stoichiometric {4:F1}:1) - free expansion of the gas, no air wave", kg, burn, oxKg, fuelKg, mxn.StoichOF));
                return;
            }
            kg = kgEff;
            if (SmokeTuning.ExplosionFluid && ShaderCache.BlastFluidCompute != null && ShaderCache.PadFluidShader != null && SystemInfo.supportsComputeShaders)
            {
                FluidBlast.Compute = ShaderCache.BlastFluidCompute; FluidBlast.RenderShader = ShaderCache.PadFluidShader;
                Vector3 upF = (worldPos - (Vector3)body.position).normalized;
                float fireRF = Mathf.Max(2.5f, Mathf.Max(SmokeTuning.ExplosionScale, 0.1f) * Mathf.Pow(Mathf.Max(kg, 10f), 1f / 3f));
                FuelMix mx = mix.Normalized();
                float lifeKF = Mathf.Clamp(Mathf.Pow(fireRF / 56f, 0.4f), 0.35f, 1.3f) * Mathf.Max(SmokeTuning.ExplosionLife, 0.2f);
                FluidBlast.Fuel fuelF = new FluidBlast.Fuel { white = mx.Whiteness, fireLife = mx.FireLife, fireDens = mx.FireDensity, smokeDens = mx.SmokeDensity, flame = mx.Flame };
                // a blast near one that is still burning joins ITS grid: the flows push each other, merge and roll up together
                for (int fi = fluids.Count - 1; fi >= 0; fi--)
                {
                    if (fluids[fi].TryAdd(worldPos, fireRF, fireRF * 1.25f, 7f * lifeKF * mx.FireLife, 32f * lifeKF, fuelF, Random.value * 100f))
                    {
                        Debug.Log(string.Format("[PlumeTrails] explosion (FLUID): {0:F0} kg joins a grid with {1} blasts", kg, fluids[fi].SourceCount));
                        return;
                    }
                }
                if (fluids.Count >= MaxFluidGrids) { fluids[0].Dispose(); fluids.RemoveAt(0); }
                // In the air (nothing under the blast within reach) the cloud has no floor, and if the vehicle was fast the cloud goes on in its direction: the gas
                // leaves with the speed of the rocket relative to the air and is stopped by the air in front of it, dv/dt = - k v^2 with
                //     k = 3 Cd rho_a / (8 rho_c R)        (a sphere of radius R and mean density rho_c = mass of the burnt propellant / its volume)
                // so v(t) = v0 / (1 + k v0 t) and the centre of the cloud has gone x(t) = ln(1 + k v0 t) / k along the line of flight. At low altitude the air
                // stops it within a few radii; high up (thin air) it flies on for kilometres.
                bool airborne = true;
                {
                    Vector3 dn = -upF;
                    RaycastHit ah;
                    if (Physics.Raycast(worldPos + upF * 2f, dn, out ah, 4f * fireRF + 60f, 1 << 15, QueryTriggerInteraction.Ignore)) airborne = false;
                }
                Vector3 v0v = (Vector3)vBase;
                float v0m = v0v.magnitude;
                FluidBlast fb = new FluidBlast(worldPos, upF, fireRF, fireRF * 1.25f, 7f * lifeKF * mx.FireLife, 32f * lifeKF, fuelF, Random.value * 100f, SmokeTuning.ExplosionFluidSpeed, false, airborne);
                if (airborne && v0m > 25f)
                {
                    float Rc = 1.5f * fireRF;
                    float rhoC = Mathf.Max(kg / (4.18879f * Rc * Rc * Rc), 0.01f);
                    float rhoAir = (float)(air.pa / (287.0 * Mathf.Max((float)air.temp, 30f)));
                    float Lstop = Mathf.Clamp(8f * rhoC * Rc / (3f * 0.5f * Mathf.Max(rhoAir, 1e-5f)), 3f * Rc, 8000f);
                    Vector3 dirF = v0v / v0m;
                    fb.SetAirMotion(v0v, 1f / Lstop);
                    Debug.Log(string.Format("[PlumeTrails] explosion in the air at {0:F0} m/s: the cloud ({1:F0} m radius, {2:F2} kg/m3) is stopped by the air (rho {3:F3}) within a length of {4:F0} m", v0m, Rc, rhoC, rhoAir, Lstop));
                }
                fb.windWorld = BlastWaves.WindAt(upF, 20f);
                fb.BodyPos = () => (Vector3)body.position;                         // kept relative to the body, not in raw world space
                if (fb.Ready)
                {
                    fb.OnRegister = (r, b) => SmokeRenderRegistry.Register(r, b);
                    fb.OnRemove = r => SmokeRenderRegistry.Remove(r);
                    fb.OnActive = (r, a) => SmokeRenderRegistry.SetActive(r, a);
                    fb.Register();
                    fluids.Add(fb);
                    Debug.Log(string.Format("[PlumeTrails] explosion (FLUID): {0:F0} kg, fireball radius {1:F0} m, cell {2:F1} m, speed x{3:F2}, white {4:F2}", kg, fireRF, Mathf.Clamp(3.4f * fireRF * 1.25f / FluidBlast.NX, 0.8f, 6f), SmokeTuning.ExplosionFluidSpeed, mx.Whiteness));
                    return;
                }
                fb.Dispose();
            }
            if (effects.Count >= MaxEffects) { effects[0].Dispose(); effects.RemoveAt(0); }
            effects.Add(new Effect(body, worldPos, kg, mix));
            Debug.Log(string.Format("[PlumeTrails] explosion: {0:F0} kg, fireball radius {1:F0} m, fuel H2 {2:F2} CH4 {3:F2} kerosene {4:F2} solid {5:F2} hypergolic {6:F2}{7}",
                kg, effects[effects.Count - 1].fireR, mix.h2, mix.ch4, mix.hc, mix.solid, mix.hyp, effects[effects.Count - 1].Mushroom ? ", MUSHROOM cloud" : ""));
        }

        // Called every frame by HalfResSmokeRenderer, right after the trails are uploaded.
        public static void RenderAll()
        {
            for (int i = fluids.Count - 1; i >= 0; i--)
            {
                if (!fluids[i].Update(Time.deltaTime)) { fluids[i].Dispose(); fluids.RemoveAt(i); }
            }
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
            private readonly float fireLife; private float smokeLife;
            private readonly FuelMix mix;
            private readonly float lifeK, fireDensMul, smokeDensMul; private float white;
            private readonly Color flameCol;
            public readonly bool Mushroom;
            private const int RingN = 14, StemN = 9;
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

            // in a vacuum: the position is kept relative to the centre of the body in the inertial frame and moves with the velocity of the rocket
            private bool vacuum; private Vector3d relBody, velBase; private float burn = 1f;
            // the dust that the shock front lifts: the front, the air it runs in
            private bool dustMode; private BlastPhysics.Front dustFront; private double dustGamma, dustC, dustRho; private float dustT0, dustT1;
            // a ring of DUST on the ground behind the shock front
            public Effect(CelestialBody body, Vector3 worldPos, float kg, BlastPhysics.Front front, double gamma, double c, double rho) : this(body, worldPos, kg, FuelMix.Default)
            {
                dustMode = true; dustFront = front; dustGamma = gamma; dustC = c; dustRho = rho;
                // it lasts until the wind behind the front is too weak to lift anything, plus the time the dust takes to settle
                double rEnd = front.startR;
                for (double rr = front.startR; rr < 2500.0; rr += 10.0)
                {
                    double ps = front.PsRatioAt(rr), u = c * ps / (gamma * System.Math.Sqrt(1.0 + (gamma + 1.0) / (2.0 * gamma) * ps));
                    if (0.5 * rho * u * u < 250.0) break;
                    rEnd = rr;
                }
                dustT0 = (float)front.TimeAt(System.Math.Max(front.startR * 1.6, 8.0)); dustT1 = System.Math.Max((float)front.TimeAt(rEnd), dustT0 + 1f);
                smokeLife = dustT1 + 34f;
            }

            public Effect(CelestialBody body, Vector3 worldPos, float kg, FuelMix fuel, bool vacuum, Vector3d vBase, float burn) : this(body, worldPos, kg, fuel)
            {
                this.vacuum = vacuum; this.burn = burn; velBase = vBase; relBody = (Vector3d)worldPos - body.position;
                white = Mathf.Max(white, 0.88f);          // the unburned propellant is a cloud of droplets, vapour and ice: white; soot only from what burned
                // the fireball of a vacuum blast is only the burned share; the rest of the propellant is a cloud of droplets and vapour
                smokeLife = vacuum ? 28f * Mathf.Max(SmokeTuning.ExplosionLife, 0.2f) : smokeLife;
            }

            public Effect(CelestialBody body, Vector3 worldPos, float kg, FuelMix fuel)
            {
                this.body = body;
                mix = fuel.Normalized();
                white = mix.Whiteness; fireDensMul = mix.FireDensity; smokeDensMul = mix.SmokeDensity; flameCol = mix.Flame;
                local = body.bodyTransform.InverseTransformPoint(worldPos);
                float scale = Mathf.Max(SmokeTuning.ExplosionScale, 0.1f);
                fireR = Mathf.Max(2.5f, scale * Mathf.Pow(Mathf.Max(kg, 10f), 1f / 3f));
                smokeR = fireR * 1.25f;
                lifeK = Mathf.Clamp(Mathf.Pow(fireR / 56f, 0.4f), 0.35f, 1.3f) * Mathf.Max(SmokeTuning.ExplosionLife, 0.2f);
                fireLife = 7f * lifeK * mix.FireLife;            // a hydrogen / methane fireball burns longer
                smokeLife = 32f * lifeK;
                // a big blast of a tank (about 6000 kg of propellant and more, a fireball over ~18 m) rises as a mushroom cloud
                Mushroom = SmokeTuning.ExplosionMushroom && fireR >= 18f;

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
                light.color = Color.Lerp(new Color(1f, 0.72f, 0.3f), flameCol, 0.7f);
            }

            private static Vector3 RandUp(float minY)
            {
                Vector3 v = Random.onUnitSphere;
                if (v.y < minY) v.y = Mathf.Abs(v.y) + minY;
                return v.normalized;
            }

            // THE MUSHROOM CLOUD (the physics of https://en.wikipedia.org/wiki/Mushroom_cloud, in blobs): the fireball is a hot thermal; it rises and,
            // as air flows around it, rolls up into a VORTEX RING - the cap: the gas goes up through the middle, out over the top and down on the
            // outside (the ring turns, blobs move round its tube). It leaves a STEM behind, drawn up by the afterwinds (smoke, dust, debris), narrow
            // in the middle and flared at the foot. The cap rises fast and slows as it entrains air and cools, and widens all the time.
            private readonly List<ExplosionBlob> shapeBlobs = new List<ExplosionBlob>(80), fireBlobs = new List<ExplosionBlob>(48), smokeBlobs = new List<ExplosionBlob>(80);
            private void BuildMushroom(Vector3 world, Vector3 up, Quaternion rot, float asm, float sFade)
            {
                shapeBlobs.Clear();
                ExplosionShape.Mushroom(shapeBlobs, world, up, rot, t, lifeK, fireLife, smokeR, asm, sFade, smokeDensMul, seed);
                for (int i = 0; i < shapeBlobs.Count; i++) AddBlob(shapeBlobs[i]);
            }

            private static float Frac(float x) { return x - Mathf.Floor(x); }

            public void Dispose()
            {
                if (mr != null) SmokeRenderRegistry.Remove(mr);
                if (go != null) Destroy(go);
                go = null; mr = null;
            }

            // The dust: where the wind behind the shock front is strong the front lifts the ground. The wind speed behind the front is the particle velocity
            // u = c Ps / (g sqrt(1 + (g + 1) / (2 g) Ps)) (Ps the ambient pressure ratio), the dynamic pressure q = rho u^2 / 2, and dust rises where q is over
            // about a quarter of a kilopascal (a wind of 25 m/s). The path of the front is cut into segments at fixed moments: each is the ring of dust that the
            // front lifted at that moment, at the radius where it was then, with the strength of the wind it had there. After that every segment AGES on its own:
            // it rises and spreads, is carried by the wind of the weather, and settles (it thins out with a time constant of about nine seconds).
            private void BuildDust(Vector3 world, Vector3 up)
            {
                const int NA = 12, NS = 8;
                Vector3 refv = Mathf.Abs(Vector3.Dot(up, Vector3.forward)) < 0.9f ? Vector3.forward : Vector3.right;
                Vector3 e1 = Vector3.ProjectOnPlane(refv, up).normalized, e2 = Vector3.Cross(up, e1);
                float tEndFade = smokeLife - 8f;
                for (int sg = 0; sg < NS; sg++)
                {
                    float ts = Mathf.Lerp(dustT0, dustT1, (sg + 0.5f) / NS);
                    float age = t - ts;
                    if (age <= 0f) continue;
                    float Rs = (float)dustFront.RadiusAt(ts);
                    double ps = dustFront.PsRatioAt(Rs);
                    double u = dustC * ps / (dustGamma * System.Math.Sqrt(1.0 + (dustGamma + 1.0) / (2.0 * dustGamma) * ps));
                    float q = (float)(0.5 * dustRho * u * u);
                    float lift = Mathf.Clamp01((q - 250f) / 3500f);
                    if (lift < 0.02f) continue;
                    float settle = Mathf.Exp(-age / 9f) * (1f - Smooth((t - tEndFade) / 8f)) * Smooth(age / 0.6f);
                    float dens = 0.95f * lift * settle;
                    if (dens < 0.02f) continue;
                    float rr = Mathf.Clamp(0.08f * Rs, 4f, 80f) * (1f + 0.18f * Mathf.Pow(age, 0.8f));
                    float rise = 0.7f * Mathf.Pow(age, 0.85f) * (0.5f + lift);
                    Vector3 drift = BlastWaves.WindAt(up, rise + 0.5f * rr) * (age * 0.55f);          // dust is slower than the wind that carries it
                    for (int a = 0; a < NA; a++)
                    {
                        float th = (a + 0.5f * Frac(Mathf.Sin(a * 12.9898f + sg * 4.1f + seed) * 43758.5453f)) * 6.2831853f / NA;
                        Vector3 p = world + (e1 * Mathf.Cos(th) + e2 * Mathf.Sin(th)) * Rs;
                        RaycastHit gh;
                        if (Physics.Raycast(p + up * 60f, -up, out gh, 160f, 1 << 15, QueryTriggerInteraction.Ignore)) p = gh.point;
                        float jit = 0.75f + 0.5f * Frac(Mathf.Sin(a * 78.233f + sg * 9.1f + seed) * 43758.5453f);
                        smokeBlobs.Add(new ExplosionBlob(p + drift + up * (0.35f * rr + rise), rr * jit, dens, 0f));
                    }
                }
            }

            private void AddBlob(ExplosionBlob b)
            {
                if (!b.hasEnd) { AddBlob(b.p, b.r, b.density, b.heat); return; }
                if (n + 4 > MaxPts || b.r < 0.05f) return;
                float w = Mathf.Clamp(b.density, 0.03f, 1.4f) + 16f * Mathf.RoundToInt(Mathf.Clamp01(b.heat) * 7f);
                pos[n] = b.p; radii[n] = 0f; pts[n] = new Vector4(b.p.x, b.p.y, b.p.z, 1f); n++;
                pos[n] = b.p; radii[n] = b.r; pts[n] = new Vector4(b.p.x, b.p.y, b.p.z, w); n++;
                pos[n] = b.end; radii[n] = b.r; pts[n] = new Vector4(b.end.x, b.end.y, b.end.z, w); n++;
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
                Vector3 world = vacuum ? (Vector3)(body.position + relBody + velBase * t) : body.bodyTransform.TransformPoint(local);
                Vector3 up = (world - (Vector3)body.position).normalized;
                // spheres are built in an up-aligned frame: rotate offsets so "up" is the planet's up
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, up);

                n = 0;
                fireBlobs.Clear(); smokeBlobs.Clear();
                if (dustMode) BuildDust(world, up);
                else if (vacuum) ExplosionShape.Vacuum(fireBlobs, smokeBlobs, world, t, fireR, burn, smokeLife, seed, smokeDensMul);
                else ExplosionShape.Blast(fireBlobs, smokeBlobs, world, up, rot, t, fireR, smokeR, fireLife, smokeLife, lifeK, fireDensMul, smokeDensMul, seed);
                for (int i = 0; i < fireBlobs.Count; i++) AddBlob(fireBlobs[i]);
                Close();

                float asm = t / smokeLife;
                float sFade = Env(asm, 1f, 1f, 0.6f, 0f);
                if (Mushroom && !vacuum && !dustMode) BuildMushroom(world, up, rot, asm, sFade);
                else for (int i = 0; i < smokeBlobs.Count; i++) AddBlob(smokeBlobs[i]);
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
                mb.SetVector("_FlameColor", new Vector4(flameCol.r, flameCol.g * 0.92f, flameCol.b * 0.8f, 25f));
                SmokeTuning.Apply(mb);
                ScattererSun.ApplyAt(mb, world);
                ExplosionShape.Look(mb, smokeR);
                mb.SetFloat("_Density", SmokeTuning.Density * 1.3f);
                mb.SetVector("_SmokeNoiseOffset", new Vector4(seed, seed * 0.37f, seed * 0.71f, 0f));
                mb.SetInt("_MarchSteps", 40);
                // soot: dark grey that lightens as it disperses (KSA's colour stops, but tinted by the sun and sky)
                float cs = Mathf.Lerp(0.42f, 0.2f, Smooth(asm / 0.27f)) * (asm > 0.27f ? Mathf.Lerp(1f, 0.55f, Smooth((asm - 0.27f) / 0.73f)) : 1f);
                // clean propellants (hydrogen, methane): the smoke is water vapour, white, and only slowly greys
                float csWhite = Mathf.Lerp(0.88f, 0.70f, Smooth(asm));
                cs = Mathf.Lerp(cs, csWhite, white);
                mb.SetColor("_SunlitColor", new Color(cs * 1.05f, cs, cs * 0.95f, 1f));
                mb.SetColor("_ShadowColor", new Color(cs * 0.3f, cs * 0.3f, cs * 0.34f, 1f) * Mathf.Lerp(1f, 1.9f, white));
                mb.SetFloat("_Washout", Mathf.Lerp(0.04f, 0.22f, white));
                mb.SetFloat("_WashoutDesaturate", 0f);
                mb.SetFloat("_FireEmission", SmokeTuning.ExplosionFire * 3.2f);
                if (dustMode)
                {
                    float dl = 0.5f;
                    mb.SetColor("_SunlitColor", new Color(0.66f * 1.05f, 0.54f * 1.05f, 0.40f * 1.05f, 1f) * dl * 1.7f);
                    mb.SetColor("_ShadowColor", new Color(0.30f, 0.25f, 0.20f, 1f) * 0.8f);
                    mb.SetFloat("_Washout", 0.1f); mb.SetFloat("_FireEmission", 0f);
                }
                mr.SetPropertyBlock(mb);
                SmokeRenderRegistry.Register(mr, mb);

                // flash
                float fl = Mathf.Exp(-t / 0.45f);
                light.transform.position = world + up * (0.4f * fireR);
                light.range = 8f * fireR + 60f;
                light.intensity = 6f * fl * SmokeTuning.ExplosionFire;
                light.enabled = fl > 0.02f && !dustMode;
                return true;
            }
        }
    }
}
