using System;

namespace RaymarchedPlumeTrails
{
    // THE AIR BLAST OF AN EXPLOSION. Pure C# (no engine types), shared by the mod and by a console test.
    //
    // What is modelled:
    //  - the peak overpressure of the blast wave at a distance, from the scaled distance Z = r / W^(1/3) (r in m, W = the TNT-equivalent mass in
    //    kg): the Kinney & Graham (1985) fit of the free-air burst data, as it is given in the explosion-effects literature (Baker 1983, the
    //    Wikipedia article "Blast wave"); the positive phase duration from the same source. The fits were typed from the literature and checked
    //    against the well-known points (about 1 bar at Z = 2, 0.1 bar at Z = 8, 0.01 bar at Z = 40 m/kg^1/3), not against the original paper;
    //  - the speed of the shock front from the Rankine-Hugoniot relations: U = c sqrt(1 + (g + 1) / (2 g) Ps / Pa), with c the speed of sound of the
    //    ambient air, c = sqrt(g R T / M) - computed from the pressure and temperature of the atmosphere the blast is in. The radius of the front
    //    is integrated in time from this (a strong shock is much faster than sound near the source, and slows down to c), so the time at which the
    //    wave reaches an observer is the time at which the front reaches his distance - not distance / c;
    //  - the density jump across the front (the same relations), which is what bends light.
    // The yield (the TNT equivalent of a tank of propellant) is an engineering estimate: the fraction of the propellant energy that goes into the
    // blast, from the practice of the launch-site safety standards (about 10-20 % for LOX / kerosene, more for hydrogen); it is not a measurement.
    public static class BlastPhysics
    {
        public const double R_UNIVERSAL = 8.314462;     // J / (mol K)
        public const double P_SEA = 101325.0, T_SEA = 288.15;

        // Peak side-on overpressure divided by the ambient pressure, at the scaled distance Z (m / kg^(1/3)), Kinney & Graham
        public static double OverpressureRatio(double z)
        {
            z = Math.Max(z, 0.02);
            double a = 808.0 * (1.0 + (z / 4.5) * (z / 4.5));
            double b = Math.Sqrt(1.0 + (z / 0.048) * (z / 0.048)) * Math.Sqrt(1.0 + (z / 0.32) * (z / 0.32)) * Math.Sqrt(1.0 + (z / 1.35) * (z / 1.35));
            return a / b;
        }

        // Duration of the positive phase, seconds, for a yield of W kg of TNT at the distance r (the ambient pressure scales it: lower pressure, longer)
        public static double PositivePhase(double z, double w)
        {
            z = Math.Max(z, 0.05);
            double zz = z;
            double num = 980.0 * (1.0 + Math.Pow(zz / 0.54, 10.0));
            double den = (1.0 + Math.Pow(zz / 0.02, 3.0)) * (1.0 + Math.Pow(zz / 0.74, 6.0)) * Math.Sqrt(1.0 + (zz / 6.9) * (zz / 6.9));
            return num / den * Math.Pow(w, 1.0 / 3.0) * 1e-3;
        }

        public static double SpeedOfSound(double gamma, double molarMassKg, double tempK)
        {
            return Math.Sqrt(gamma * R_UNIVERSAL * Math.Max(tempK, 20.0) / Math.Max(molarMassKg, 1e-3));
        }

        // front speed for the pressure ratio Ps / Pa
        public static double FrontSpeed(double c, double gamma, double psRatio)
        {
            return c * Math.Sqrt(1.0 + (gamma + 1.0) / (2.0 * gamma) * psRatio);
        }

        // rho2 / rho1 behind the front, for the pressure ratio p2 / p1 = 1 + Ps / Pa
        public static double DensityRatio(double gamma, double psRatio)
        {
            double p = 1.0 + psRatio;
            return ((gamma + 1.0) * p + (gamma - 1.0)) / ((gamma - 1.0) * p + (gamma + 1.0));
        }

        // sound pressure level (dB re 20 micropascal) of an overpressure in Pa
        public static double Spl(double pa) { return 20.0 * Math.Log10(Math.Max(pa, 1e-6) / 2e-5); }

        // TNT-equivalent mass (kg) of an exploding mass of propellant of the given mix (shares of hydrogen, methane, kerosene, solid, hypergolic)
        public static double TntEquivalent(double kg, double h2, double ch4, double hc, double solid, double hyp)
        {
            double tot = h2 + ch4 + hc + solid + hyp;
            if (tot <= 1e-9) { hc = 1.0; tot = 1.0; }
            double y = (h2 * 0.30 + ch4 * 0.18 + hc * 0.12 + solid * 0.35 + hyp * 0.15) / tot;
            return Math.Max(kg * y, 1.0);
        }

        // ------------------------------------------------------------------ the front in time
        public class Front
        {
            public double w, c, gamma, pa, startR;
            public float[] t, r;
            public int n;

            public Front(double wTnt, double ambientPa, double soundSpeed, double gamma, double startRadius, double maxRadius = 40000.0, double maxTime = 150.0)
            {
                w = wTnt; c = soundSpeed; this.gamma = gamma; pa = ambientPa; startR = Math.Max(startRadius, 1.0);
                double w13 = Math.Pow(w, 1.0 / 3.0);
                var ts = new System.Collections.Generic.List<float>(8192);
                var rs = new System.Collections.Generic.List<float>(8192);
                double time = 0.0, rad = startR;
                ts.Add(0f); rs.Add((float)rad);
                while (rad < maxRadius && time < maxTime)
                {
                    double ps = OverpressureRatio(rad / w13);
                    double u = FrontSpeed(c, gamma, ps);
                    double dt = time < 0.5 ? 0.002 : time < 5.0 ? 0.01 : 0.05;
                    // do not step further than a few percent of the radius
                    dt = Math.Min(dt, 0.03 * rad / Math.Max(u, 1.0));
                    rad += u * dt; time += dt;
                    ts.Add((float)time); rs.Add((float)rad);
                }
                t = ts.ToArray(); r = rs.ToArray(); n = t.Length;
            }

            // radius of the front at time tt (s since the blast)
            public double RadiusAt(double tt)
            {
                if (tt <= 0.0) return r[0];
                if (tt >= t[n - 1]) return r[n - 1] + c * (tt - t[n - 1]);
                int lo = 0, hi = n - 1;
                while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (t[mid] <= tt) lo = mid; else hi = mid; }
                double f = (tt - t[lo]) / Math.Max(t[hi] - t[lo], 1e-9);
                return r[lo] + (r[hi] - r[lo]) * f;
            }

            // time at which the front reaches the radius rr
            public double TimeAt(double rr)
            {
                if (rr <= r[0]) return 0.0;
                if (rr >= r[n - 1]) return t[n - 1] + (rr - r[n - 1]) / c;
                int lo = 0, hi = n - 1;
                while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (r[mid] <= rr) lo = mid; else hi = mid; }
                double f = (rr - r[lo]) / Math.Max(r[hi] - r[lo], 1e-9);
                return t[lo] + (t[hi] - t[lo]) * f;
            }

            public double PsRatioAt(double radius) { return OverpressureRatio(radius / Math.Pow(w, 1.0 / 3.0)); }
        }

        // ------------------------------------------------------------------ the bang
        // The pressure signal that a listener at `distance` hears, synthesised from the physics: the Friedlander pulse of the blast wave (rise to the
        // peak, a decay of the length of the positive phase, a negative phase), the crack of the steep front (it is only there when the front is still
        // strong and the air has not yet taken its high frequencies), and the rumble of the low-frequency tail that ground and air spread out. The air
        // absorbs the high frequencies with the distance, so a far explosion is a deep boom and a near one is a sharp BANG.
        // Returns samples in -1..1 (the loudness is set separately, from the sound level).
        public static float[] SynthBang(double psPa, double tPlus, double distance, int sampleRate, int seed)
        {
            double dur = Math.Min(8.0, 2.0 + 1.6 * Math.Log10(1.0 + distance / 80.0) + 0.4 * Math.Log10(1.0 + psPa / 1000.0));
            int n = (int)(dur * sampleRate);
            float[] s = new float[n];
            var rng = new Random(seed);
            double fcAir = Math.Max(250.0, 12000.0 / (1.0 + distance / 120.0));           // high-frequency absorption of the air
            double aLp = 1.0 - Math.Exp(-2.0 * Math.PI * fcAir / sampleRate);
            double fcRumble = 55.0 + 150.0 / (1.0 + distance / 400.0);
            double aRum = 1.0 - Math.Exp(-2.0 * Math.PI * fcRumble / sampleRate);
            double tp = Math.Max(tPlus, 0.003);
            double crackAmp = 1.3 * Math.Exp(-distance / 450.0) * Math.Min(1.0, psPa / 3000.0 + 0.2);
            double rumTau = 0.55 + 0.45 * Math.Log10(1.0 + distance / 40.0);
            double lp1 = 0.0, rum1 = 0.0, rum2 = 0.0, pulseLp = 0.0, hp = 0.0, prevN = 0.0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sampleRate;
                double white = rng.NextDouble() * 2.0 - 1.0;
                // the Friedlander pulse (positive phase, then a weaker negative one), slightly rounded by the air
                double x = t / tp;
                double pulse = x < 8.0 ? (1.0 - x) * Math.Exp(-x) * 1.6 : 0.0;
                pulseLp += (pulse - pulseLp) * aLp;
                // the crack: noise with the energy at the high frequencies, gone in a few ms for a small charge, longer for a big one
                lp1 += (white - lp1) * aLp;
                double hf = lp1 - (hp += (lp1 - hp) * 0.08);
                double crack = hf * crackAmp * Math.Exp(-t / (0.02 + 0.5 * tp));
                // the rumble: low-passed noise with a soft attack and a long decay
                rum1 += (white - rum1) * aRum; rum2 += (rum1 - rum2) * aRum;
                double att = 1.0 - Math.Exp(-t / 0.018);
                double rumble = rum2 * 5.5 * att * Math.Exp(-t / rumTau);
                // the sub-bass thump under the pulse: a damped low sine, its frequency from the length of the positive phase
                double f0 = Math.Min(90.0, Math.Max(28.0, 0.5 / tp));
                double thump = Math.Sin(2.0 * Math.PI * f0 * t) * Math.Exp(-t / (0.09 + 3.0 * tp)) * 0.7 * (1.0 - Math.Exp(-t / 0.004));
                double v = pulseLp * 0.9 + crack + rumble + thump;
                s[i] = (float)Math.Tanh(2.6 * v) * 0.97f;
                prevN = white;
            }
            // fade the last 0.2 s
            int fade = Math.Min(n, (int)(0.2 * sampleRate));
            for (int i = 0; i < fade; i++) s[n - 1 - i] *= (float)i / fade;
            return s;
        }
    }
}
