using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // The SHAPE of a mushroom cloud, as a list of blobs. Pure Unity code (no game types), shared by the mod (Explosions.cs) and by the offline
    // preview in the Unity project, so the cloud tuned there is the cloud drawn in the game.
    public struct ExplosionBlob
    {
        public Vector3 p; public float r, density, heat;
        public Vector3 end; public bool hasEnd;       // a blob can be a short capsule (a billow that is taller than wide), from p to end
        public ExplosionBlob(Vector3 p, float r, float d, float h) { this.p = p; this.r = r; density = d; heat = h; end = p; hasEnd = false; }
        public ExplosionBlob(Vector3 p, float r, float d, float h, Vector3 end) { this.p = p; this.r = r; density = d; heat = h; this.end = end; hasEnd = true; }
    }

    public static class ExplosionShape
    {
        public static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        public static float Env(float a, float start, float peak, float peakAt, float end)
        {
            a = Mathf.Clamp01(a);
            if (a < peakAt) return Mathf.Lerp(start, peak, Smooth(a / peakAt));
            return Mathf.Lerp(peak, end, Smooth((a - peakAt) / Mathf.Max(1f - peakAt, 1e-4f)));
        }
        private static float Frac(float x) { return x - Mathf.Floor(x); }
        private static float Hash(float k, float seed) { return Frac(Mathf.Sin(k * 12.9898f + seed * 78.233f) * 43758.5453f); }

        private static Vector3 FibDir(int i, int n, float seed, float yTop, float yBottom)
        {
            float y = yTop + (yBottom - yTop) * (i + 0.5f) / n;
            float rxy = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float phi = i * 2.399963f + seed;
            return new Vector3(rxy * Mathf.Cos(phi), y, rxy * Mathf.Sin(phi));
        }

        // a lobe is a BILLOW: a capsule that is taller than it is wide (a rising turret of the cloud), with two smaller billows on it - the outline is
        // lumpy at three scales (a cauliflower), not a bunch of equal balls
        private static void Lobe(List<ExplosionBlob> into, Vector3 centre, Quaternion rot, Vector3 up, Vector3 dir, float dist, float radius, float stretch,
                                 float dens, float heat, float seed, int k)
        {
            Vector3 p = centre + rot * (dir * dist);
            into.Add(new ExplosionBlob(p, radius, dens, heat, p + up * (radius * stretch)));
            Vector3 d2 = FibDir(k * 3 + 1, 7, seed * 1.7f, 0.9f, -0.4f);
            Vector3 p2 = p + rot * (d2 * radius * 0.85f);
            into.Add(new ExplosionBlob(p2, radius * (0.46f + 0.14f * Hash(k + 90, seed)), dens * 0.85f, heat * 0.8f, p2 + up * (radius * stretch * 0.5f)));
            Vector3 d3 = FibDir(k * 5 + 2, 9, seed * 2.3f, 0.7f, -0.7f);
            Vector3 p3 = p + rot * (d3 * radius * 0.9f);
            into.Add(new ExplosionBlob(p3, radius * (0.30f + 0.12f * Hash(k + 95, seed)), dens * 0.8f, heat * 0.7f));
        }

        // THE BLAST ITSELF (no mushroom): a fireball that bursts out as a ring of lobes, glows by a heat ramp and cools lobe by lobe; a ring of
        // dust / condensation that runs out over the ground; and a billowing smoke cloud that rolls as it rises on a column that feeds it.
        //   t = seconds, fireR / smokeR = radii, fireLife / smokeLife = lives, lifeK = duration scale, the density multipliers come from the fuel.
        public static void Blast(List<ExplosionBlob> fire, List<ExplosionBlob> smoke, Vector3 world, Vector3 up, Quaternion rot, float t,
                                 float fireR, float smokeR, float fireLife, float smokeLife, float lifeK, float fireDensMul, float smokeDensMul, float seed)
        {
            // ---------- the fireball
            float af = t / fireLife;
            float fireFade = Env(af, 1f, 1f, 0.27f, 0f);
            if (af < 1f && fireFade > 0.01f)
            {
                float r = fireR * Env(af, 6f / 56f, 1f, 0.0871f, 85f / 56f);
                float dens = Env(af, 0.8f, 0.5f, 0.19f, 0.03f) * 1.7f * fireFade * fireDensMul;
                float heat = Mathf.Pow(Mathf.Clamp01(1f - af / 0.8f), 0.6f);      // the fire glows for most of its life
                Vector3 c = world + up * (0.3f * fireR + 0.45f * fireR * (1f - Mathf.Exp(-t / 1.6f)));
                fire.Add(new ExplosionBlob(c, r * 0.72f, dens, heat));
                const int N = 10;
                for (int i = 0; i < N; i++)
                {
                    // each lobe bursts out at its own moment and cools at its own pace
                    float g = Smooth(af / 0.10f - 0.9f * Hash(i, seed));
                    if (g <= 0.01f) continue;
                    Vector3 d = FibDir(i, N, seed, 0.85f, -0.25f);
                    float lr = r * (0.36f + 0.30f * Hash(i + 30, seed)) * (0.45f + 0.55f * g);
                    float dist = r * (0.60f + 0.35f * Hash(i + 60, seed)) * g;
                    Lobe(fire, c, rot, up, d, dist, lr, 0.3f + 0.6f * Hash(i + 140, seed), dens * 0.9f, heat * (0.5f + 0.5f * Hash(i + 120, seed)), seed, i);
                }
            }

            // ---------- the smoke cloud
            float asm = t / smokeLife;
            float sFade = Env(asm, 1f, 1f, 0.6f, 0f);
            float sr = smokeR * Env(asm, 6f / 70f, 1f, 0.025f, 95f / 70f);
            float sd = Env(asm, 0.22f, 0.82f, 0.18f, 0f) * 1.4f * sFade * smokeDensMul * Smooth((t - 0.45f * fireLife) / (0.6f * fireLife));
            float rise = 1.3f * smokeR * (1f - Mathf.Exp(-t / 5f));
            Vector3 sc = world + up * (0.20f * smokeR + 1.15f * rise);
            if (sd < 0.02f) return;                       // no smoke yet: nothing is drawn (a thin haze of a giant radius showed as a grey cloud over the fire)
            smoke.Add(new ExplosionBlob(sc - up * (0.1f * sr), sr * 0.5f, sd, 0f, sc + up * (0.5f * sr)));
            const int SN = 17;
            for (int i = 0; i < SN; i++)
            {
                Vector3 d = FibDir(i, SN, seed * 0.6f, 0.9f, -0.5f);
                // the cloud ROLLS: every lobe turns slowly about the vertical, each at its own rate, so the outline keeps changing
                float spin = t * (0.10f + 0.10f * Hash(i + 200, seed)) * (Hash(i + 210, seed) > 0.5f ? 1f : -1f) / Mathf.Max(lifeK, 0.5f);
                Vector3 dr = Quaternion.AngleAxis(spin * 57.2958f, Vector3.up) * d;
                float lr = sr * (0.22f + 0.20f * Hash(i + 240, seed));
                float dist = sr * (0.50f + 0.40f * Hash(i + 270, seed));
                // pockets of the fire still burning inside the smoke at the start
                float emb = i < 4 ? 0.5f * Mathf.Clamp01(1f - t / (9f * lifeK)) * Hash(i + 300, seed) : 0f;
                Lobe(smoke, sc, rot, up, dr, dist, lr, 0.5f + 1.6f * Hash(i + 330, seed), sd * 0.92f, emb, seed + 3f, i);
            }
        }

        // The surface of a blast cloud is far more ragged than a trail's: the lobes alone give a smooth bumpy ball. Stronger erosion and warp of the
        // silhouette (set after SmokeTuning.Apply) turn the lobes into billows with torn edges.
        public static void Look(MaterialPropertyBlock mb, float scale)
        {
            mb.SetFloat("_SilhouetteWarpStrength", Mathf.Clamp(0.35f * scale, 6f, 30f));
            mb.SetFloat("_VortexStrength", Mathf.Clamp(0.12f * scale, 1f, 6f));
            mb.SetFloat("_EdgeErosionStrength", 1f);
            mb.SetFloat("_DetailStrength", 0.95f);
            mb.SetFloat("_GrainStrength", 0.5f);
        }

        // A BLAST IN A VACUUM. There is no air to hold the gas: the burned propellant expands freely at the speed of its own sound, a thousand
        // metres per second and more (R grows linearly, the density falls as 1 / R^3, the gas cools as it expands), so the fireball is a flash that thins out
        // within a fraction of a second and a faint hot cloud that is gone in seconds. The unburned propellant leaves as droplets and vapour, in clumps with
        // their own velocities (no billows, no rising: nothing buoys it), a cloud that grows ~linearly and thins out as 1 / R^2 over half a minute.
        public static void Vacuum(List<ExplosionBlob> fire, List<ExplosionBlob> smoke, Vector3 world, float t, float fireR, float burn, float smokeLife, float seed, float smokeDensMul)
        {
            // the burned gas
            if (burn > 0.04f)
            {
                float r0 = 0.55f * fireR;
                float r = r0 + 900f * t;
                float dens = 2.8f * burn * Mathf.Pow(r0 / r, 2.6f);
                float heat = Mathf.Clamp01(burn * Mathf.Pow(r0 / r, 0.6f) * Mathf.Exp(-t / 0.9f) * 1.4f);
                if (dens > 0.004f) fire.Add(new ExplosionBlob(world, r, Mathf.Min(dens, 1.4f), heat));
            }
            // the cloud of droplets and vapour
            float fadeOut = 1f - Smooth((t - 0.55f * smokeLife) / (0.45f * smokeLife));
            float rc = 0.8f * fireR + 60f * Mathf.Pow(t, 0.9f);
            float dc = 1.3f * smokeDensMul * Mathf.Pow(0.8f * fireR / rc, 2.2f) * fadeOut * Smooth(t / 0.15f);
            if (dc > 0.01f) smoke.Add(new ExplosionBlob(world, rc * 0.7f, Mathf.Min(dc, 1.2f), 0f));
            // the clumps: each flies out at its own speed
            const int N = 14;
            for (int i = 0; i < N; i++)
            {
                Vector3 d = FibDir(i, N, seed, 0.97f, -0.97f);
                float speed = 25f + 115f * Hash(i + 11, seed);
                float dist = 0.4f * fireR + speed * t;
                float rr = (0.35f + 0.2f * Hash(i + 40, seed)) * fireR * (1f + 1.2f * t / (0.3f * smokeLife));
                float growth = 1f + 1.2f * t / (0.3f * smokeLife);
                float dl = 0.55f * smokeDensMul * fadeOut * Smooth(t / 0.15f) * (0.85f + 0.3f * Hash(i + 70, seed)) * Mathf.Pow(1f / growth, 0.8f);
                float hl = burn * 0.14f * Mathf.Exp(-t / 0.3f) * Hash(i + 100, seed);
                if (dl > 0.01f) smoke.Add(new ExplosionBlob(world + d * dist, rr, Mathf.Min(dl * 0.7f, 1f), hl));
            }
        }

        public const int RingN = 22, StemN = 11;

        // THE MUSHROOM CLOUD (https://en.wikipedia.org/wiki/Mushroom_cloud): the fireball is a hot thermal that rises and rolls up into a VORTEX
        // RING - the cap: the gas goes up through the middle, out over the top and down on the outside. It leaves a thin STEM behind, drawn up by
        // the afterwinds, flared at the foot. Proportions of a real one: the cap is a flattened torus about as wide as the column is tall, the stem
        // is a quarter of its width, the underside of the cap is hollow where the ring curls in.
        //   t = seconds since the blast, lifeK = the duration scale, fireLife = the life of the fireball, smokeR = the radius of the smoke,
        //   asm = t / smokeLife, sFade = the fade of the smoke, dens = density multiplier, seed = a random number per blast.
        public static void Mushroom(List<ExplosionBlob> into, Vector3 world, Vector3 up, Quaternion rot, float t, float lifeK, float fireLife,
                                    float smokeR, float asm, float sFade, float smokeDensMul, float seed)
        {
            float riseK = 1f - Mathf.Exp(-t / (7f * lifeK));
            float Rc = smokeR * Env(asm, 0.20f, 1f, 0.12f, 1.35f);              // the outer radius of the cap
            float H = 0.35f * smokeR + 2.9f * smokeR * riseK;                  // the height of the middle of the cap
            float dens = Env(asm, 0.25f, 0.85f, 0.15f, 0f) * 1.4f * sFade * smokeDensMul * Smooth((t - 0.05f * fireLife) / (0.3f * fireLife));
            float rho = 0.70f * Rc, a = 0.30f * Rc;                             // the ring: its radius and the radius of its tube
            float spin = t * (2.2f / Mathf.Max(lifeK, 0.5f));                   // how fast the ring turns
            float flat = 0.82f;                                                 // the cap is flattened
            Vector3 cc = world + up * H;

            // the ring: blobs round the major circle, each at its own angle round the tube (the golden angle spreads them); a little lumpy
            for (int k = 0; k < RingN; k++)
            {
                float th = k * 6.2831853f / RingN + seed;
                float ph = spin + k * 2.399963f;
                Vector3 radial = rot * new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th));
                float lump = 0.92f + 0.16f * Hash(k, seed);
                into.Add(new ExplosionBlob(cc + radial * (rho + 0.55f * a * Mathf.Cos(ph)) + up * (0.55f * a * Mathf.Sin(ph) * flat), a * lump, dens, 0f));
            }
            // the top of the cap: the gas that went up through the middle spreads over it (a flat disc of blobs inside the ring)
            into.Add(new ExplosionBlob(cc + up * (0.45f * a * flat), 0.85f * a, dens * 0.95f, 0f));
            for (int k = 0; k < 7; k++)
            {
                float th = k * 6.2831853f / 7f + seed * 1.3f;
                Vector3 radial = rot * new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th));
                into.Add(new ExplosionBlob(cc + radial * (0.50f * rho) + up * (0.40f * a * flat), 0.8f * a, dens * 0.9f, 0f));
            }
            // the underside, where the ring curls in: a ring of smaller blobs under it, the hollow of the cap stays open in the middle
            for (int k = 0; k < 9; k++)
            {
                float th = k * 6.2831853f / 9f + 0.35f + seed;
                Vector3 radial = rot * new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th));
                into.Add(new ExplosionBlob(cc + radial * (0.62f * rho) - up * (0.85f * a * flat), 0.55f * a, dens * 0.8f, 0f));
            }
            // the stem: from the ground to the underside of the cap, narrow in the middle and flared at the foot
            float stemK = Smooth(1f - asm * 1.5f);
            if (stemK > 0.03f)
            {
                float top = Mathf.Max(H - 0.35f * Rc, 0.2f * smokeR);
                for (int s = 0; s < StemN; s++)
                {
                    float u = (s + 0.5f) / StemN;
                    float rs = Rc * (0.085f + 0.20f * (1f - u) * (1f - u) * (1f - u));
                    float wob = Mathf.Sin(u * 6f + spin * 0.5f + seed) * 0.07f * Rc;
                    Vector3 side = rot * new Vector3(Mathf.Cos(seed + u * 3f), 0f, Mathf.Sin(seed + u * 3f));
                    into.Add(new ExplosionBlob(world + up * (0.15f * smokeR + u * top) + side * wob, rs, dens * 0.9f * stemK, 0f));
                }
                // the dust and smoke the blast throws up round the foot of the stem
                for (int k = 0; k < 6; k++)
                {
                    float th = k * 6.2831853f / 6f + seed * 2.1f;
                    Vector3 radial = rot * new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th));
                    float spread = Mathf.Lerp(0.25f, 0.65f, riseK);
                    into.Add(new ExplosionBlob(world + radial * (spread * Rc) + up * (0.1f * Rc), 0.2f * Rc, dens * 0.7f * stemK, 0f));
                }
            }
        }
    }
}
