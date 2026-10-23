using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // PAD CLOUD. The cloud that rolls out over a launch pad (the Saturn V photographs): not a chain of free puffs hitting
    // colliders - that came out as noodles, or nothing - but BILLOWS ON RAILS. The rails are the surface paths PadFlow finds
    // with rays from the impact point (over a flat pad they fan out; in a flame trench they follow it, climb its walls and
    // leave through its ends). Each billow moves along its rail with the wall-jet laws (Glauert 1956, radial wall jet):
    //   sideways speed   u(s) = u0 * (r0 / (s + r0))^1.1          (falls with the distance s from the impact)
    //   half-thickness   R(s) ~ R0 + 0.1 s (+ turbulent thickening with age)
    // and, once it has slowed down, buoyancy (hot, wet gas) lifts it into a cauliflower. Water-deluge steam is white and
    // dense, the first tens of metres glow (heat), and the fire light that lights the fog sits at the impact.
    // The column above the rocket stays with the physical puffs; when a pad cloud is active the puffs that reach the pad are
    // dropped (the cloud takes over there).
#if !EXPLOSIONS_ONLY
    [KSPAddon(KSPAddon.Startup.Flight, false)]
#endif
    public class PadCloud : MonoBehaviour
    {
        private struct Billow
        {
            public int path;
            public float s, age, R0, lat, h, dens0, rise, uScale, wgt, heatReach;
            public bool sooty;
        }

        private class Entry
        {
            public float lastReport;
            public float thrustKN, nozzleR, light, lightRange, water, hot, soot, jetR;
            public readonly List<BlobVolume> sootVols = new List<BlobVolume>();
            public Color flame = new Color(1f, 0.56f, 0.22f);
            public readonly List<Billow> bills = new List<Billow>(512);
            public float[] nextEmit = new float[0];
            public readonly List<BlobVolume> vols = new List<BlobVolume>();
            public System.Random rng = new System.Random(12345);
            public float seed;
            public Vector3 impact;
            public PadFlowField field;           // the last rails seen: the cloud outlives the rocket leaving the pad
        }

        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private static readonly List<int> dead = new List<int>();
        private static float lastActive;

        // true while a pad cloud is being fed (the puffs that hit the pad are then dropped)
        public static bool Active { get { return ModSettings.Enabled && SmokeTuning.PadCloud && Time.time - lastActive < 0.6f; } }

        public static void Report(int key, float thrustKN, float nozzleRadius, float water, Color flame, float lightPower, float lightRange, float hot, float soot)
        {
            if (!ModSettings.Enabled || !SmokeTuning.PadCloud || ShaderCache.SmokeVolumeShader == null) return;
            Entry e;
            if (!entries.TryGetValue(key, out e)) { e = new Entry { seed = Random.value * 100f }; entries[key] = e; }
            e.thrustKN = thrustKN; e.nozzleR = nozzleRadius; e.water = water; e.flame = flame;
            e.light = lightPower; e.lightRange = lightRange; e.hot = hot; e.soot = soot;
            e.lastReport = Time.time;
            lastActive = Time.time;
        }

        private void OnDestroy()
        {
            foreach (Entry e in entries.Values)
            {
                for (int i = 0; i < e.vols.Count; i++) e.vols[i].Dispose();
                for (int i = 0; i < e.sootVols.Count; i++) e.sootVols[i].Dispose();
            }
            entries.Clear();
        }

        // ------------------------------------------------------------------ rails
        private static readonly Dictionary<PadFlowPath, float[]> cumCache = new Dictionary<PadFlowPath, float[]>();
        private static float[] Cum(PadFlowPath p)
        {
            float[] c;
            if (cumCache.TryGetValue(p, out c) && c.Length == p.pts.Count) return c;
            c = new float[p.pts.Count];
            for (int i = 1; i < c.Length; i++) c[i] = c[i - 1] + Vector3.Distance(p.pts[i], p.pts[i - 1]);
            if (cumCache.Count > 200) cumCache.Clear();
            cumCache[p] = c;
            return c;
        }

        // The point at arc length s along a path (the last segment is extended past its end: the flow leaves the pad).
        private static Vector3 PointAt(PadFlowPath p, float s, out Vector3 dir)
        {
            int n = p.pts.Count;
            dir = Vector3.forward;
            if (n == 0) return Vector3.zero;
            if (n == 1) return p.pts[0];
            float[] c = Cum(p);
            if (s >= c[n - 1])
            {
                Vector3 d = p.pts[n - 1] - p.pts[n - 2];
                dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
                return p.pts[n - 1] + dir * (s - c[n - 1]);
            }
            int i = 1;
            while (i < n - 1 && c[i] < s) i++;
            float seg = Mathf.Max(c[i] - c[i - 1], 1e-4f);
            float t = (s - c[i - 1]) / seg;
            dir = (p.pts[i] - p.pts[i - 1]).normalized;
            return Vector3.Lerp(p.pts[i - 1], p.pts[i], t);
        }

        // ------------------------------------------------------------------ simulation + drawing
        private void Update()
        {
            float now = Time.time, dt = Mathf.Min(Time.deltaTime, 0.1f);
            dead.Clear();
            foreach (KeyValuePair<int, Entry> kv in entries)
            {
                Entry e = kv.Value;
                if (PadFluid.Drawing)
                {
                    // the fluid cloud has taken over the pad: no billows on rails, and the ones already there are dropped at once
                    e.bills.Clear();
                    for (int v = 0; v < e.vols.Count; v++) { e.vols[v].Begin(); e.vols[v].Hide(); }
                    for (int v = 0; v < e.sootVols.Count; v++) { e.sootVols[v].Begin(); e.sootVols[v].Hide(); }
                    continue;
                }
                bool feeding = SmokeTuning.PadCloud && now - e.lastReport < 0.5f;
                PadFlowField f = PadFlow.Get(kv.Key);
                if (f != null) e.field = f; else f = e.field;
                float life = Mathf.Max(SmokeTuning.PadCloudLife, 3f);

                if (feeding && f != null && f.paths != null) Emit(e, f, dt);
                if (f != null && f.paths != null) Step(e, f, dt, life);
                else e.bills.Clear();

                if ((!feeding && e.bills.Count == 0) || !SmokeTuning.PadCloud || !ModSettings.Enabled) { dead.Add(kv.Key); continue; }
                Render(e, f);
            }
            for (int i = 0; i < dead.Count; i++)
            {
                Entry e = entries[dead[i]];
                for (int v = 0; v < e.vols.Count; v++) e.vols[v].Dispose();
                for (int v = 0; v < e.sootVols.Count; v++) e.sootVols[v].Dispose();
                entries.Remove(dead[i]);
            }
        }

        private static float U0(Entry e)
        {
            // how fast the flow leaves the impact: grows with the thrust
            return Mathf.Clamp(30f + 0.005f * e.thrustKN, 30f, 180f) * Mathf.Max(SmokeTuning.PadCloudSpeed, 0.1f);
        }

        private void Emit(Entry e, PadFlowField f, float dt)
        {
            int paths = f.paths.Length;
            if (e.nextEmit.Length != paths) e.nextEmit = new float[paths];
            e.impact = f.impact;
            float u0 = U0(e);
            // the size of the flow follows the whole jet (thrust), not one nozzle: R ~ 0.12 sqrt(F[kN]) (about 24 m for an SLS)
            float R0 = Mathf.Max(Mathf.Max(6f, f.radius * 0.8f), 0.12f * Mathf.Sqrt(e.thrustKN)) * Mathf.Max(SmokeTuning.PadCloudScale, 0.1f);
            e.jetR = R0;
            float interval = Mathf.Clamp(1.6f * R0 / u0, 0.45f, 1.0f);
            int stride = Mathf.Max(1, paths / 12);
            for (int i = 0; i < paths; i += stride)
            {
                if (f.paths[i] == null) continue;
                e.nextEmit[i] -= dt;
                if (e.nextEmit[i] > 0f) continue;
                e.nextEmit[i] += interval;
                if (e.bills.Count >= 420) continue;
                float jitter = 0.8f + 0.4f * (float)e.rng.NextDouble();
                // A flow that has to climb out of a trench carries a fraction of the mass and has lost energy on the way;
                // the rails along the trench (and its open ends) carry the rest.
                bool climbed = f.paths[i].climbed;
                float wgt = climbed ? 0.3f : 1f;
                e.bills.Add(new Billow
                {
                    path = i, s = 0f, age = 0f,
                    R0 = R0 * Mathf.Sqrt(wgt) * jitter,
                    lat = ((float)e.rng.NextDouble() * 2f - 1f) * 0.35f,
                    dens0 = Mathf.Clamp(0.75f * SmokeTuning.PadCloudDensity * Mathf.Lerp(0.6f, 1f, e.water) * (0.5f + 0.5f * wgt), 0.05f, 1.2f),
                    rise = 0.8f + 0.5f * (float)e.rng.NextDouble(),
                    uScale = climbed ? 0.55f : 1f, wgt = wgt, heatReach = 4f * R0,
                    sooty = e.rng.NextDouble() < e.soot
                });
            }
        }

        private void Step(Entry e, PadFlowField f, float dt, float life)
        {
            float u0 = U0(e);
            float jetR = Mathf.Max(Mathf.Max(6f, f.radius * 0.8f), 0.12f * Mathf.Sqrt(e.thrustKN)) * Mathf.Max(SmokeTuning.PadCloudScale, 0.1f);
            float r0 = Mathf.Max(Mathf.Max(10f, f.radius * 1.2f), 1.5f * jetR);
            float liftSpeed = (3f + 0.0001f * e.thrustKN) * Mathf.Max(SmokeTuning.PadCloudLift, 0.1f);
            for (int i = e.bills.Count - 1; i >= 0; i--)
            {
                Billow b = e.bills[i];
                b.age += dt;
                if (b.age > life || b.path >= f.paths.Length || f.paths[b.path] == null) { e.bills.RemoveAt(i); continue; }
                float u = u0 * b.uScale * Mathf.Pow(r0 / (b.s + r0), 1.1f);
                b.s += u * dt;
                // once the flow has slowed down, buoyancy takes over and the cloud rises
                float vb = Mathf.Clamp01(1f - u / 12f) * liftSpeed * b.rise;
                b.h += vb * dt;
                e.bills[i] = b;
            }
        }

        private void Render(Entry e, PadFlowField f)
        {
            if (f == null || f.paths == null || f.paths.Length == 0)
            {
                // the rails are not there yet (PadFlow builds them over a few frames): nothing to draw, and nothing to fall over
                for (int k = 0; k < e.vols.Count; k++) { e.vols[k].Begin(); e.vols[k].Hide(); }
                return;
            }
            float life = Mathf.Max(SmokeTuning.PadCloudLife, 3f);
            // billows of neighbouring rails share a volume; keep them ordered by rail
            e.bills.Sort((a, b) => a.path != b.path ? a.path.CompareTo(b.path) : b.s.CompareTo(a.s));

            // ONE set of volumes for steam and soot: the soot is a per-point level (steam = 0) the shader darkens, so the two
            // blend inside the volume instead of being drawn as separate, interpenetrating volumes (which cannot be sorted)
            int v = 0;
            BlobVolume vol = null;
            // the rails are a few directions of what is really a continuous sheet: a billow must cover the gap to its neighbours
            int stride = Mathf.Max(1, f.paths.Length / 12);
            float dTheta = 2f * Mathf.PI / Mathf.Max(1, f.paths.Length / stride);
            for (int i = 0; i < e.bills.Count; i++)
            {
                Billow b = e.bills[i];
                PadFlowPath path = f.paths[b.path];
                Vector3 dir;
                Vector3 P = PointAt(path, b.s, out dir);
                Vector3 up = f.up;
                Vector3 side = Vector3.Cross(up, dir);
                side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.right;

                float R = Mathf.Max(b.R0 + 0.1f * b.s + 0.35f * b.age, 0.5f * b.s * dTheta);   // wall jet thickening + turbulence, and the sheet
                R = Mathf.Min(R, 140f);
                Vector3 pos = P + up * (b.h + R * 0.55f) + side * (b.lat * R);

                // fades in over the first half second (nothing pops into existence) and out at the end of its life
                float fadeIn = Mathf.SmoothStep(0f, 1f, b.age / 0.6f);
                float fade = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(life * 0.55f, life, b.age)) * fadeIn;
                float d = b.dens0 * Mathf.Pow(b.R0 / R, 0.4f) * fade;
                // the glowing part of the flow reaches a few jet radii and lasts a few seconds
                float heat = e.hot * Mathf.Clamp01(1f - b.s / Mathf.Max(b.heatReach, 1f)) * Mathf.Clamp01(1f - b.age / 4f);

                if (vol == null || vol.Full || vol.Count >= BlobVolume.MaxBlobs)
                {
                    if (vol != null) FinishVolume(vol, e, f);
                    while (e.vols.Count <= v) e.vols.Add(new BlobVolume());
                    vol = e.vols[v++];
                    vol.Begin();
                }
                vol.Add(pos, R, b.sooty ? Mathf.Min(1.3f, d * 1.25f) : d, b.sooty ? 0f : 1f, heat);
            }
            if (vol != null) FinishVolume(vol, e, f);
            for (int k = v; k < e.vols.Count; k++) { e.vols[k].Begin(); e.vols[k].Hide(); }
        }

        private static void FinishVolume(BlobVolume vol, Entry e, PadFlowField f)
        {
            Vector3 lightPos = f.impact + f.up * 6f;
            // the fire lights the whole volume of fog, not a spot: a wide range and a strong light
            float range = Mathf.Max(e.lightRange, 250f, 9f * e.jetR);
            vol.Upload(lightPos, e.light * 2f, range, e.flame, SmokeTuning.DeflectorFire * 2.4f, e.seed, 0.72f);
        }
    }
}
