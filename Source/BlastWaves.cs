using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // The air blasts of the explosions in the game: for every blast in an atmosphere a front that races out (BlastPhysics.Front: the speed of
    // sound from the pressure and temperature of the air there, the strong-shock speed near the source), which
    //  - bends the light (ShockLens draws the front as a ring that displaces the picture behind it, by the density jump across it),
    //  - is HEARD when it reaches the camera: a bang synthesised from the overpressure there, with the loudness from the sound level, the delay from
    //    the front itself (it reaches the observer when the front reaches his distance),
    //  - shakes the camera by the overpressure when it arrives (and a short tremor of the ground from a blast that is close to it).
    // In a vacuum there is no air and so no wave, no sound, no shake: the explosion is the gas expanding freely (Explosions.cs) and the fragments.
    public class BlastWave
    {
        public CelestialBody body;
        public Vector3 local;                 // body-local position of the centre
        public float t0;                      // Time.time of the blast
        public BlastPhysics.Front front;
        public double w;                      // TNT equivalent, kg
        public double pa0, temp0, gamma, c;   // the air at the blast
        public float fireR;
        public bool heard, tremor;
        public float groundK;                 // 1 when the blast is at the ground
        // for the lens, updated every frame
        public Vector3 centreWorld; public float radius; public float alpha, width;

        public float Age { get { return Time.time - t0; } }
    }

    public static class BlastWaves
    {
        // the wind that carries the dust and the smoke of a blast: the same as the trail's (SmokeTuning.WindSpeed, a logarithmic profile), horizontal
        public static Vector3 WindAt(Vector3 up, float height)
        {
            if (SmokeTuning.WindSpeed < 0.01f) return Vector3.zero;
            Vector3 dir = Vector3.ProjectOnPlane(new Vector3(1f, 0f, 0f), up);
            if (dir.sqrMagnitude < 1e-6f) return Vector3.zero;
            return dir.normalized * PuffPhysics.WindAt(SmokeTuning.WindSpeed, Mathf.Max(height, 1f), Mathf.Max(SmokeTuning.PhysWindRoughness, 0.001f));
        }

        public static readonly List<BlastWave> Active = new List<BlastWave>();
        private const int MaxWaves = 6;

        public struct Atmos { public double pa, temp, gamma, molar; public bool air; }

        // the air at a point: pressure (Pa), temperature (K), adiabatic index, molar mass (kg / mol)
        public static Atmos AirAt(CelestialBody body, Vector3 world)
        {
            Atmos a = new Atmos { pa = 0, temp = 3, gamma = 1.4, molar = 0.02896, air = false };
            if (body == null || !body.atmosphere) return a;
            double alt = body.GetAltitude(world);
            if (alt >= body.atmosphereDepth) return a;
            a.pa = body.GetPressure(alt) * 1000.0;
            a.temp = Mathf.Max((float)body.GetTemperature(alt), 30f);
            a.gamma = body.atmosphereAdiabaticIndex > 1.01 ? body.atmosphereAdiabaticIndex : 1.4;
            a.molar = body.atmosphereMolarMass > 1e-4 ? body.atmosphereMolarMass : 0.02896;
            a.air = a.pa > 300.0;                      // below 0.3 kPa there is no air worth the name: a vacuum for a blast
            return a;
        }

        // wTnt: the TNT-equivalent mass, kg. Returns false in a vacuum (no wave).
        public static bool Spawn(CelestialBody body, Vector3 world, double wTnt, float fireR, float groundK)
        {
            if (body == null || !SmokeTuning.ExplosionShockLens && !SmokeTuning.ExplosionSound) return false;
            Atmos a = AirAt(body, world);
            if (!a.air) return false;
            double c = BlastPhysics.SpeedOfSound(a.gamma, a.molar, a.temp);
            if (Active.Count >= MaxWaves) Active.RemoveAt(0);
            BlastWave bw = new BlastWave
            {
                body = body, local = body.bodyTransform.InverseTransformPoint(world), t0 = Time.time, w = wTnt, pa0 = a.pa, temp0 = a.temp, gamma = a.gamma, c = c,
                fireR = fireR, groundK = groundK,
                front = new BlastPhysics.Front(wTnt, a.pa, c, a.gamma, Mathf.Max(fireR * 0.8f, 1.5f))
            };
            Active.Add(bw);
            Debug.Log(string.Format("[PlumeTrails] blast wave: {0:F0} kg TNT eq., air {1:F1} kPa {2:F0} K, c = {3:F0} m/s; the front is at 100 m after {4:F2} s, 1 km after {5:F2} s, overpressure at 100 m {6:F1} kPa, at 1 km {7:F2} kPa",
                wTnt, a.pa / 1000.0, a.temp, c, bw.front.TimeAt(100.0), bw.front.TimeAt(1000.0),
                bw.front.PsRatioAt(100.0) * a.pa / 1000.0, bw.front.PsRatioAt(1000.0) * a.pa / 1000.0));
            return true;
        }
    }

    // Drives the waves every frame: the sound at the arrival, the shake, the data for the lens.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BlastWaveDriver : MonoBehaviour
    {
        // ---- shake: a sum of decaying bursts, evaluated once per frame
        private struct Shake { public float t0, amp, tau; public float freq; public float seed; }
        private static readonly List<Shake> shakes = new List<Shake>();
        public static Vector3 ShakeEuler; public static Vector3 ShakePos;

        private AudioSource[] sources;
        private int nextSource;

        private void Awake()
        {
            GameObject g = new GameObject("PlumeTrails.BlastAudio");
            DontDestroyOnLoad(g);
            sources = new AudioSource[12];
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource s = g.AddComponent<AudioSource>();
                s.spatialBlend = 0f; s.playOnAwake = false; s.loop = false; s.dopplerLevel = 0f;
                sources[i] = s;
            }
        }

        private void OnDestroy()
        {
            if (sources != null && sources.Length > 0 && sources[0] != null) Destroy(sources[0].gameObject);
            BlastWaves.Active.Clear(); shakes.Clear();
            ShakeEuler = Vector3.zero; ShakePos = Vector3.zero;
        }

        public static void AddShake(float delay, float ampDeg, float tau, float freq)
        {
            if (ampDeg < 0.005f) return;
            if (shakes.Count > 12) shakes.RemoveAt(0);
            shakes.Add(new Shake { t0 = Time.time + delay, amp = ampDeg, tau = tau, freq = freq, seed = Random.value * 100f });
        }

        private static float Noise(float x, float seed) { return Mathf.PerlinNoise(x, seed) * 2f - 1f; }

        private void Update()
        {
            if (!ModSettings.Enabled) return;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            float now = Time.time;
            // the shake of this frame
            Vector3 eul = Vector3.zero, pos = Vector3.zero;
            for (int i = shakes.Count - 1; i >= 0; i--)
            {
                Shake s = shakes[i];
                float ts = now - s.t0;
                if (ts < 0f) continue;
                float env = Mathf.Exp(-ts / s.tau) * (1f - Mathf.Exp(-ts / 0.025f));
                if (env < 0.01f && ts > 3f * s.tau) { shakes.RemoveAt(i); continue; }
                float a = s.amp * env;
                eul += new Vector3(Noise(ts * s.freq, s.seed), Noise(ts * s.freq, s.seed + 17f), 0.6f * Noise(ts * s.freq * 0.8f, s.seed + 31f)) * a;
                pos += new Vector3(Noise(ts * s.freq * 1.3f, s.seed + 5f), Noise(ts * s.freq * 1.1f, s.seed + 9f), 0f) * (a * 0.04f);
            }
            ShakeEuler = SmokeTuning.ExplosionShake > 0.01f ? eul * SmokeTuning.ExplosionShake : Vector3.zero;
            ShakePos = SmokeTuning.ExplosionShake > 0.01f ? pos * SmokeTuning.ExplosionShake : Vector3.zero;

            List<BlastWave> list = BlastWaves.Active;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                BlastWave bw = list[i];
                if (bw.body == null || bw.body.bodyTransform == null) { list.RemoveAt(i); continue; }
                float age = bw.Age;
                Vector3 centre = bw.body.bodyTransform.TransformPoint(bw.local);
                double R = bw.front.RadiusAt(age);
                bw.centreWorld = centre; bw.radius = (float)R;

                // lens strength: the density jump across the front, the thickness of the compressed layer, the deflection of a ray that grazes it
                double ps = bw.front.PsRatioAt(R);
                double delta = BlastPhysics.DensityRatio(bw.gamma, ps) - 1.0;
                double u = BlastPhysics.FrontSpeed(bw.c, bw.gamma, ps);
                double z = R / System.Math.Pow(bw.w, 1.0 / 3.0);
                double tp = BlastPhysics.PositivePhase(z, bw.w);
                double L = System.Math.Max(u * tp * 0.5, System.Math.Max(1.5, 0.01 * R));
                double n0 = 2.9e-4 * (bw.pa0 / BlastPhysics.P_SEA) * (BlastPhysics.T_SEA / bw.temp0);          // refractivity of the air there
                double alpha = n0 * delta * System.Math.Sqrt(2.0 * R / L);                                      // the deflection of a grazing ray, radians
                bw.alpha = (float)System.Math.Min(alpha * SmokeTuning.ExplosionShockLensGain, 0.06);
                bw.width = (float)L;

                // when the front reaches the camera: the sound, the shake
                if (cam != null && !bw.heard)
                {
                    float d = Vector3.Distance(cam.transform.position, centre);
                    if (R >= d || d < bw.fireR)
                    {
                        bw.heard = true;
                        Arrive(bw, cam, d);
                    }
                }
                if (R > 40000.0 || age > 160f) list.RemoveAt(i);
            }
        }

        private void Arrive(BlastWave bw, Camera cam, float d)
        {
            BlastWaves.Atmos here = BlastWaves.AirAt(bw.body, cam.transform.position);
            double dd = System.Math.Max(d, bw.front.startR);
            double ratio = bw.front.PsRatioAt(dd);
            double psPa = ratio * System.Math.Max(here.pa, 0.0);
            double z = dd / System.Math.Pow(bw.w, 1.0 / 3.0);
            double tp = BlastPhysics.PositivePhase(z, bw.w) * System.Math.Pow(bw.pa0 / System.Math.Max(here.pa, 1.0), 1.0 / 3.0);
            double spl = BlastPhysics.Spl(psPa);
            // the shake: the overpressure that arrives (a window-breaking blast of 0.7 kPa shakes the camera a little; tens of kPa a lot)
            float amp = 1.1f * Mathf.Min(1f, Mathf.Pow((float)(psPa / 6000.0), 0.6f));
            AddShake(0f, amp, 0.6f + 0.5f * Mathf.Min(1f, (float)tp * 8f), 11f + 6f * Mathf.Clamp01(1f - (float)psPa / 20000f));
            // the sound
            if (SmokeTuning.ExplosionSound && here.air && sources != null)
            {
                float vol = Mathf.Pow(Mathf.Clamp01((float)((spl - 70.0) / 80.0)), 2f) * SmokeTuning.ExplosionSoundVolume * Mathf.Clamp01(GameSettings.SHIP_VOLUME * 1.15f);
                if (vol > 0.002f)
                {
                    const int sr = 44100;
                    float[] pcm = BlastPhysics.SynthBang(psPa, tp, dd, sr, Random.Range(1, 100000));
                    AudioClip clip = AudioClip.Create("PlumeTrails.Bang", pcm.Length, 1, sr, false);
                    clip.SetData(pcm, 0);
                    // one source cannot be louder than its full volume, so a loud bang plays on several sources at once (the same clip: up to four times the
                    // amplitude, +12 dB); the volume slider goes up to 4 for the bang that hits the ears
                    float total = vol * 4f;
                    int k = Mathf.Clamp(Mathf.CeilToInt(Mathf.Min(total, 4f)), 1, 4);
                    for (int i = 0; i < k; i++)
                    {
                        AudioSource s = sources[nextSource++ % sources.Length];
                        s.Stop(); s.clip = clip; s.volume = Mathf.Min(total / k, 1f); s.pitch = 1f; s.Play();
                    }
                    Destroy(clip, pcm.Length / (float)sr + 1f);
                }
            }
            Debug.Log(string.Format("[PlumeTrails] the blast wave reaches the camera: {0:F0} m after {1:F2} s (d/c would be {2:F2} s); overpressure {3:F2} kPa = {4:F0} dB, positive phase {5:F0} ms, shake {6:F2} deg, volume {7:F2}",
                dd, bw.Age, dd / bw.c, psPa / 1000.0, spl, tp * 1000.0, amp, Mathf.Pow(Mathf.Clamp01((float)((spl - 70.0) / 80.0)), 2f)));
        }
    }

    // Applies the shake to the scene cameras just before they cull, and puts the camera back after they render (KSP sets the camera again every frame).
    public class CameraShakeApplier : MonoBehaviour
    {
        private Vector3 savedPos; private Quaternion savedRot; private bool applied;

        private void OnPreCull()
        {
            applied = false;
            if (BlastWaveDriver.ShakeEuler.sqrMagnitude < 1e-8f) return;
            savedPos = transform.position; savedRot = transform.rotation; applied = true;
            transform.rotation = savedRot * Quaternion.Euler(BlastWaveDriver.ShakeEuler);
            transform.position = savedPos + transform.rotation * BlastWaveDriver.ShakePos;
        }

        private void OnPostRender()
        {
            if (!applied) return;
            transform.position = savedPos; transform.rotation = savedRot; applied = false;
        }
    }

    // Puts the shake applier on the scene cameras (they are created by KSP; checked twice a second)
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CameraShakeInstaller : MonoBehaviour
    {
        private static readonly string[] Names = { "Camera 00", "Camera 01", "Camera 1", "Camera 0", "Camera ScaledSpace" };
        private float next;
        private void Update()
        {
            if (!ModSettings.Enabled || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                Camera c = all[i];
                if (c == null || System.Array.IndexOf(Names, c.name) < 0) continue;
                if (c.GetComponent<CameraShakeApplier>() == null) c.gameObject.AddComponent<CameraShakeApplier>();
            }
        }
    }
}
