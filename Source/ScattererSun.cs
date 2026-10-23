using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // THE COLOUR OF THE SUN AT A PLACE. The light that reaches a trail has crossed the part of the atmosphere that lies between the trail and the sun, and the
    // atmosphere takes out the blue more than the red (Rayleigh, with the grey extinction of the aerosols on top): a low sun is orange, a trail high over
    // the horizon at dusk is less red than the ground below it. It is computed for the POSITION of the smoke, the way Scatterer does it for its own sky
    // (the same optical depth along the ray to the sun, with the atmosphere of the body: its scale height from the pressure curve, its sea-level pressure):
    //     T(lambda) = exp( - beta_R(lambda) * column_R - beta_M * column_M )
    // along the straight ray from the point to the top of the atmosphere in the direction of the sun, in spherical shells. Only the HUE is used (T divided by
    // its largest channel): how bright the light is at dusk is the business of the smoke's own day / twilight factor. A ray that the planet blocks (the point
    // is in the planet's shadow) gets the colour of the grazing ray. The result is cached by (body, height, sun elevation).
    public static class ScattererSun
    {
        private static readonly Dictionary<long, Color> cache = new Dictionary<long, Color>();
        private static readonly Vector3 betaR = new Vector3(5.8e-6f, 13.5e-6f, 33.1e-6f);      // Rayleigh scattering of the air at sea level, per metre, red green blue (Earth)
        private const float BetaMieExt = 2.2e-5f;                                              // aerosol extinction at sea level, per metre (grey)
        private const float MieHeightRatio = 0.15f;                                            // the aerosols lie low: their scale height against that of the air

        public static Color TintAt(Vector3 worldPos)
        {
            if (!SmokeTuning.ScattererSunTint || SmokeTuning.ScattererTintStrength < 0.01f) return Color.white;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || !body.atmosphere || body.atmosphereDepth < 1000.0) return Color.white;
            Vector4 sv = Shader.GetGlobalVector("_SmokeSunDir");
            Vector3 toSun = new Vector3(sv.x, sv.y, sv.z);
            if (toSun.sqrMagnitude < 1e-4f) return Color.white;
            toSun.Normalize();
            Vector3 rel = worldPos - (Vector3)body.position;
            double r0 = rel.magnitude;
            if (r0 < 1.0) return Color.white;
            Vector3 up = rel / (float)r0;
            double mu = Vector3.Dot(up, toSun);
            double alt = r0 - body.Radius;
            if (alt > body.atmosphereDepth) return Color.white;                     // above the air: the sun has no colour of its own
            long key = ((long)body.flightGlobalsIndex << 40) ^ ((long)(alt / (alt < 3000.0 ? 100.0 : 500.0)) << 20) ^ (long)(System.Math.Round(mu * 300.0) + 400.0);
            Color tint;
            if (!cache.TryGetValue(key, out tint))
            {
                tint = Compute(body, r0, mu);
                if (cache.Count > 6000) cache.Clear();
                cache[key] = tint;
            }
            return Color.Lerp(Color.white, tint, Mathf.Clamp01(SmokeTuning.ScattererTintStrength));
        }

        // multiplies the sunlit colour that SmokeTuning.Apply has put in the block by the tint at this place
        public static void ApplyAt(MaterialPropertyBlock block, Vector3 worldPos)
        {
            Color t = TintAt(worldPos);
            if (t.r > 0.999f && t.g > 0.999f && t.b > 0.999f) return;
            block.SetColor("_SunlitColor", block.GetColor("_SunlitColor") * t);
        }

        private static Color Compute(CelestialBody body, double r0, double mu)
        {
            double R = body.Radius, depth = body.atmosphereDepth, top = R + depth;
            double p0 = body.GetPressure(0.0), p1 = body.GetPressure(0.2 * depth);
            double H = (p0 > 1e-6 && p1 > 1e-9 && p1 < p0) ? 0.2 * depth / System.Math.Log(p0 / p1) : depth / 7.0;
            H = System.Math.Max(H, 500.0);
            float pk = Mathf.Clamp((float)(p0 / 101.325), 0.02f, 20f);                  // the thickness of the air against Earth's
            // the ray from the point towards the sun up to the top of the atmosphere
            double b = r0 * mu, c = r0 * r0 - top * top;
            double disc = b * b - c;
            if (disc <= 0.0) return Color.white;
            double tmax = -b + System.Math.Sqrt(disc);
            if (tmax <= 0.0) return Color.white;
            bool blocked = mu < 0.0 && r0 * System.Math.Sqrt(1.0 - mu * mu) < R;
            if (blocked)
            {
                // in the shadow of the planet: take the grazing ray (it just touches the surface), the colour of the last light that gets round it
                tmax = System.Math.Sqrt(System.Math.Max(top * top - R * R, 1.0)) * 2.0;
                mu = -System.Math.Sqrt(System.Math.Max(1.0 - (R * R) / (r0 * r0), 0.0)) * 0.9999;
                b = r0 * mu; c = r0 * r0 - top * top; disc = b * b - c;
                tmax = -b + System.Math.Sqrt(System.Math.Max(disc, 1.0));
            }
            const int N = 40;
            double colR = 0.0, colM = 0.0;
            for (int i = 0; i < N; i++)
            {
                double x = (i + 0.5) / N;
                double t = tmax * x * x, dt = tmax * 2.0 * x / N;
                double rr = System.Math.Sqrt(System.Math.Max(r0 * r0 + 2.0 * r0 * t * mu + t * t, 1.0));
                double h = rr - R;
                if (h < 0.0) h = 0.0;
                colR += System.Math.Exp(-h / H) * dt;
                colM += System.Math.Exp(-h / (H * MieHeightRatio)) * dt;
            }
            double tr = System.Math.Exp(-betaR.x * pk * colR - BetaMieExt * pk * colM);
            double tg = System.Math.Exp(-betaR.y * pk * colR - BetaMieExt * pk * colM);
            double tb = System.Math.Exp(-betaR.z * pk * colR - BetaMieExt * pk * colM);
            double m = System.Math.Max(tr, System.Math.Max(tg, tb));
            if (m < 1e-12) return new Color(1f, 0.2f, 0.05f, 1f);
            return new Color((float)(tr / m), (float)(tg / m), (float)(tb / m), 1f);
        }
    }
}
