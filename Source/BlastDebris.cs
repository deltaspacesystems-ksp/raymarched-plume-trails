using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // THE FRAGMENTS OF AN EXPLOSION. Pieces of the rocket thrown out by the blast: each a small plate that tumbles, glows while it is hot and leaves
    // a trail (a flame that turns into smoke in the air; only a short glow in a vacuum). They fly in the frame of the scene (velocities as in the scene, not the orbital ones; the position
    // is kept relative to the body's centre, the velocity includes that of the rocket and of the rotating ground under it), under the gravity of the
    // body and, in an atmosphere, the drag of the air: rho v^2 / (2 beta), with the ballistic coefficient beta (kg / m^2) of the piece. They land on
    // the terrain (or the sea) and stop; the trail goes out.
    public static class BlastDebris
    {
        private class Piece
        {
            public Vector3d rel, vel;           // relative to the centre of the body, world axes
            public GameObject go; public MeshRenderer mr; public TrailRenderer trail;
            public float size, beta, heat, spinSpeed; public Vector3 spinAxis;
            public bool landed; public float landedAt;
            public bool flame;
            public float T, thick;                       // the temperature of the skin (K) and its thickness (m): heated by the air, cooled by radiation
            public bool volumetric;                       // its smoke is part of the smoke volume of the field (a real smoke), not a ribbon
            public List<Vector3d> hist = new List<Vector3d>(); public List<float> histT = new List<float>(); public float nextHist;
            public float qNow;                            // the heating of the skin now, W/m2 (for the colour of a piece in re-entry)
        }

        private class Field { public CelestialBody body; public List<Piece> pieces = new List<Piece>(); public float born; public bool vacuum; public BlobVolume smoke; public float seed; }

        private static readonly List<Field> fields = new List<Field>();
        private static Material mat, trailMat;
        private static Mesh cube;
        private static MaterialPropertyBlock mpb;
        private static Gradient flameGrad, glowGrad;

        public static void Spawn(CelestialBody body, Vector3 world, Vector3d baseVelocity, double kg, float fireR, bool vacuum, double airRho)
        {
            if (!SmokeTuning.ExplosionDebris || SmokeTuning.ExplosionDebrisAmount < 0.05f || ShaderCache.DebrisShader == null || body == null) return;
            if (fields.Count >= 6) Clear(fields[0]);
            if (mat == null) { mat = new Material(ShaderCache.DebrisShader); trailMat = new Material(ShaderCache.DebrisShader); }
            if (cube == null) { GameObject t = GameObject.CreatePrimitive(PrimitiveType.Cube); cube = t.GetComponent<MeshFilter>().sharedMesh; Object.Destroy(t); }
            if (mpb == null) mpb = new MaterialPropertyBlock();
            if (flameGrad == null) { flameGrad = MakeGradient(true); glowGrad = MakeGradient(false); }

            int n = Mathf.Clamp(Mathf.RoundToInt((10f + 6.5f * Mathf.Log(Mathf.Max((float)kg, 20f) / 50f, 2f)) * SmokeTuning.ExplosionDebrisAmount), 4, 110);
            Field f = new Field { body = body, born = Time.time, vacuum = vacuum };
            Vector3d rel0 = (Vector3d)world - body.position;
            Vector3 up = ((Vector3)rel0).normalized;
            float baseSpeed = (28f + 9f * Mathf.Sqrt(fireR)) * (vacuum ? 2.4f : 1f);
            for (int i = 0; i < n; i++)
            {
                // a cone of fragments biased upward (the ground reflects the blast), a few go sideways low
                Vector3 dir = Random.onUnitSphere;
                if (!vacuum) { float lift = Mathf.Abs(Vector3.Dot(dir, up)); dir = (dir + up * (0.55f + lift)).normalized; }
                float u1 = Random.value;
                float speed = baseSpeed * Mathf.Lerp(0.25f, 1.7f, u1 * u1);
                Piece p = new Piece
                {
                    rel = rel0 + (Vector3d)(up * 0.5f + dir * (fireR * 0.3f)),
                    vel = baseVelocity + (Vector3d)(dir * speed),
                    size = Mathf.Clamp(Mathf.Pow((float)kg, 1f / 3f) * Random.Range(0.04f, 0.16f), 0.15f, 2.4f),
                    beta = Random.Range(25f, 400f),
                    heat = Random.Range(0.6f, 1f),
                    spinAxis = Random.onUnitSphere, spinSpeed = Random.Range(90f, 720f),
                    flame = Random.value < 0.65f
                };
                p.T = Random.Range(1000f, 1900f);                 // thrown out of the fire
                p.thick = Mathf.Clamp(p.size * 0.03f, 0.003f, 0.03f);
                p.go = new GameObject("PlumeTrails.Debris");
                p.go.AddComponent<MeshFilter>().sharedMesh = cube;
                p.mr = p.go.AddComponent<MeshRenderer>();
                p.mr.sharedMaterial = mat;
                p.mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; p.mr.receiveShadows = false;
                p.go.transform.localScale = new Vector3(p.size, p.size * 0.22f, p.size * 0.7f);
                p.go.transform.rotation = Random.rotation;
                TrailRenderer tr = p.go.AddComponent<TrailRenderer>();
                tr.sharedMaterial = trailMat;
                tr.time = vacuum ? 0.5f : (p.flame ? 3.2f : 1.6f);
                tr.widthMultiplier = Mathf.Clamp(p.size * 0.9f, 0.15f, 1.6f);
                tr.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.15f));
                tr.colorGradient = vacuum ? glowGrad : flameGrad;
                tr.numCapVertices = 0; tr.minVertexDistance = 0.4f; tr.autodestruct = false;
                tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; tr.receiveShadows = false;
                p.trail = tr;
                p.go.transform.position = (Vector3)(body.position + p.rel);
                f.pieces.Add(p);
            }
            // the real smoke goes with the biggest pieces (a smoke volume holds about a hundred blobs: 14 pieces with six each); the others keep a thin ribbon
            f.seed = Random.value * 100f;
            List<Piece> bySize = new List<Piece>(f.pieces);
            bySize.Sort((x, y) => y.size.CompareTo(x.size));
            if (SmokeTuning.DebrisSmokeVolume && ShaderCache.SmokeVolumeShader != null)
            {
                for (int i = 0; i < bySize.Count && i < 14; i++) { bySize[i].volumetric = true; bySize[i].trail.emitting = false; bySize[i].trail.enabled = false; }
                f.smoke = new BlobVolume();
            }
            fields.Add(f);
            Debug.Log("[PlumeTrails] explosion fragments: " + n + " pieces, speeds up to " + (baseSpeed * 1.7f).ToString("F0") + " m/s" + (vacuum ? " (vacuum: no drag)" : ", air density " + airRho.ToString("F3") + " kg/m3"));
        }

        private static Gradient MakeGradient(bool flame)
        {
            Gradient g = new Gradient();
            if (flame)
                g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.62f, 0.22f), 0f), new GradientColorKey(new Color(0.75f, 0.30f, 0.10f), 0.18f), new GradientColorKey(new Color(0.42f, 0.41f, 0.40f), 0.45f), new GradientColorKey(new Color(0.5f, 0.5f, 0.5f), 1f) },
                          new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0.75f, 0.2f), new GradientAlphaKey(0.45f, 0.55f), new GradientAlphaKey(0f, 1f) });
            else
                g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.55f, 0.2f), 0f), new GradientColorKey(new Color(0.8f, 0.2f, 0.08f), 1f) },
                          new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        private static void Clear(Field f)
        {
            if (f.smoke != null) { f.smoke.Dispose(); f.smoke = null; }
            foreach (Piece p in f.pieces) if (p.go != null) Object.Destroy(p.go);
            f.pieces.Clear();
            fields.Remove(f);
        }

        public static void ClearAll() { for (int i = fields.Count - 1; i >= 0; i--) Clear(fields[i]); }

        // the colour of a black body (the same ramp as the shaders), normalised so that the brightest channel is 1
        private static Color BlackBody(float T)
        {
            float t = Mathf.Max(T, 800f) / 100f;
            float r, g, b;
            if (t <= 66f) { r = 1f; g = Mathf.Clamp01((99.4708025861f * Mathf.Log(t) - 161.1195681661f) / 255f); }
            else { r = Mathf.Clamp01(1.292936186f * Mathf.Pow(t - 60f, -0.1332047592f)); g = Mathf.Clamp01(1.129890861f * Mathf.Pow(t - 60f, -0.0755148492f)); }
            if (t >= 66f) b = 1f; else if (t <= 19f) b = 0f; else b = Mathf.Clamp01((138.5177312231f * Mathf.Log(t - 10f) - 305.0447927307f) / 255f);
            return new Color(r, g, b, 1f);
        }

        // THE SMOKE OF THE FRAGMENTS, as a real smoke: every one of the biggest pieces leaves six blobs along its track, that swell as they age (a puff of smoke in the
        // air that the piece has left behind, it does not travel with it) and thin out; the youngest ones glow with the heat of the piece. All of them are ONE
        // volume of the smoke raymarcher, lit and shaded like the rest of the smoke.
        private static void BuildSmoke(Field f, CelestialBody body)
        {
            f.smoke.Begin();
            float now = Time.time;
            Vector3 hotCentre = Vector3.zero; float hotW = 0f;
            Color tint = FireflyColors.For(body).trailPrimary; float tm = Mathf.Max(tint.r, Mathf.Max(tint.g, tint.b)); if (tm > 0.01f) tint /= tm;
            for (int i = 0; i < f.pieces.Count; i++)
            {
                Piece p = f.pieces[i];
                if (!p.volumetric || p.go == null) continue;
                float sizeScale = Mathf.Clamp(p.size * 1.1f, 0.35f, 2.4f);
                float life = p.flame ? 2.6f : 1.7f;
                int n = p.hist.Count;
                // six blobs: the piece itself, then five of its track, spaced over its history
                for (int k = 0; k < 6; k++)
                {
                    Vector3 wp; float age;
                    if (k == 0) { wp = (Vector3)(body.position + p.rel); age = 0f; }
                    else
                    {
                        if (n == 0) break;
                        int idx = Mathf.Clamp(n - 1 - (k - 1) * Mathf.Max(1, n / 5), 0, n - 1);
                        wp = (Vector3)(body.position + p.hist[idx]); age = now - p.histT[idx];
                    }
                    if (age > 4.5f) continue;
                    float r = sizeScale * (0.5f + 1.25f * Mathf.Pow(age, 0.75f));
                    float d = 0.65f * Mathf.Exp(-age / life) * (p.landed ? 0.4f : 1f);
                    if (d < 0.04f) continue;
                    float heat = p.heat * Mathf.Exp(-age / 0.5f);
                    if (!f.smoke.Add(wp, r, d, 0.25f, heat)) goto done;
                    if (k == 0 && p.T > 900f) { hotCentre += wp; hotW += 1f; }
                }
            }
            done:
            Vector3 fl = hotW > 0f ? hotCentre / hotW : (Vector3)(body.position + (f.pieces.Count > 0 ? f.pieces[0].rel : Vector3d.zero));
            f.smoke.Upload(fl, hotW > 0f ? 0.6f : 0f, 40f, tint, 0.5f, f.seed, 0.25f);
        }

        // advanced every frame by the driver
        public static void Update(float dt)
        {
            dt = Mathf.Min(dt, 0.05f);
            for (int fi = fields.Count - 1; fi >= 0; fi--)
            {
                Field f = fields[fi];
                CelestialBody body = f.body;
                if (body == null) { Clear(f); continue; }
                float age = Time.time - f.born;
                int alive = 0;
                for (int i = 0; i < f.pieces.Count; i++)
                {
                    Piece p = f.pieces[i];
                    if (p.go == null) continue;
                    if (!p.landed)
                    {
                        double rhoAir = 0.0, vRelSp = 0.0;
                        Vector3d pos = body.position + p.rel;
                        double r2 = p.rel.sqrMagnitude, r = System.Math.Sqrt(r2);
                        Vector3d acc = p.rel * (-body.gravParameter / (r2 * r));
                        if (!f.vacuum)
                        {
                            double alt = body.GetAltitude(pos);
                            if (body.atmosphere && alt < body.atmosphereDepth)
                            {
                                double pa = body.GetPressure(alt) * 1000.0, temp = System.Math.Max(body.GetTemperature(alt), 30.0);
                                double rho = pa / (287.0 * temp);
                                Vector3d vair = body.inverseRotation ? p.vel : p.vel - body.getRFrmVel(pos);     // near the body the scene is its rotating frame: the air is at rest in it
                                double sp = vair.magnitude;
                                acc -= vair * (rho * sp / (2.0 * p.beta));
                                rhoAir = rho; vRelSp = sp;
                            }
                        }
                        p.vel += acc * dt;
                        p.rel += p.vel * dt;
                        Vector3d npos = body.position + p.rel;
                        // the ground: the terrain under the piece (and the surface of the sea)
                        double alt2 = body.GetAltitude(npos);
                        double ground = body.pqsController != null ? body.TerrainAltitude(body.GetLatitude(npos), body.GetLongitude(npos), true) : 0.0;
                        if (alt2 <= ground + 0.2)
                        {
                            p.landed = true; p.landedAt = Time.time;
                            Vector3d up = p.rel.normalized;
                            p.rel = up * (body.Radius + ground + 0.2);
                            p.vel = Vector3d.zero;
                        }
                        p.go.transform.position = (Vector3)(body.position + p.rel);
                        p.go.transform.Rotate(p.spinAxis, p.spinSpeed * dt, Space.World);
                        // THE SKIN OF THE PIECE. Heated by the air that it runs into (the stagnation heating, Sutton-Graves: q = 1.74e-4 sqrt(rho / Rn) v^3 W/m2, Rn the radius of
                        // its nose) and cooled by radiation from both faces (2 eps sigma T^4), a plate of the thickness d with the heat capacity rho c d per area:
                        //     C dT/dt = q - 2 eps sigma (T^4 - Ta^4)
                        // At 500 m/s in thick air it warms to a dull red, at 2 km/s in thin air to white: the incandescence of a re-entry, from the speed and the air.
                        float Rn = Mathf.Max(0.05f, 0.4f * p.size);
                        float q = rhoAir > 0.0 ? (float)(1.7415e-4 * System.Math.Sqrt(rhoAir / Rn) * vRelSp * vRelSp * vRelSp) : 0f;
                        p.qNow = q;
                        float Cs = 7800f * 500f * p.thick;
                        float T4 = p.T * p.T; T4 *= T4;
                        float rad = 2f * 0.8f * 5.67e-8f * (T4 - 2.4e10f);
                        p.T = Mathf.Clamp(p.T + (q - rad) / Cs * dt, 250f, 4500f);
                        p.heat = Mathf.Clamp01(Mathf.InverseLerp(600f, 2200f, p.T));
                    }
                    else if (Time.time - p.landedAt > 6f) { Object.Destroy(p.go); p.go = null; continue; }
                    if (p.landed) p.T = Mathf.Max(300f, p.T - 40f * dt * Mathf.Max(1f, (p.T - 300f) / 400f));          // it cools where it lies
                    float h = Mathf.InverseLerp(600f, 2200f, p.T);
                    Color hot = BlackBody(p.T);
                    // in re-entry (the skin heated by the air, not by the fire) the glow has the colours of Firefly's plasma of this body
                    float re = Mathf.Clamp01((p.qNow - 1.5e5f) / 1.5e6f);
                    if (re > 0.01f) { FireflyColors.Palette fp = FireflyColors.For(body); Color gl = Color.Lerp(fp.glow, fp.glowHot, Mathf.Clamp01(p.T / 3000f)); float mx = Mathf.Max(gl.r, Mathf.Max(gl.g, gl.b)); if (mx > 0.01f) hot = Color.Lerp(hot, gl / mx * Mathf.Max(hot.r, 0.4f), 0.55f * re); }
                    float emis = Mathf.Clamp(Mathf.Pow(p.T / 1800f, 4f) * 0.9f, 0f, 3f) * (p.T > 650f ? 1f : 0f);
                    Color col = new Color(0.10f, 0.095f, 0.09f, 1f) + hot * emis;
                    mpb.SetColor("_Color", col);
                    p.mr.SetPropertyBlock(mpb);
                    alive++;
                    // the track of the piece for its smoke (positions relative to the body, so that they hold when the scene moves)
                    if (p.volumetric && !p.landed && Time.time >= p.nextHist)
                    {
                        p.nextHist = Time.time + 0.1f;
                        p.hist.Add(p.rel); p.histT.Add(Time.time);
                        while (p.hist.Count > 0 && Time.time - p.histT[0] > 4.5f) { p.hist.RemoveAt(0); p.histT.RemoveAt(0); }
                    }
                }
                if (f.smoke != null) BuildSmoke(f, body);
                if (alive == 0 || age > 60f) Clear(f);
            }
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BlastDebrisDriver : MonoBehaviour
    {
        private void Update() { if (ModSettings.Enabled) BlastDebris.Update(Time.deltaTime); }
        private void OnDestroy() { BlastDebris.ClearAll(); }
    }
}
