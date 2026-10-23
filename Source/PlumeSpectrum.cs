using System;

namespace RaymarchedPlumeTrails
{
    // SPECTRAL COLOUR OF EXHAUST. The colour of a plume is not picked: it is the sum of what its gas and particles emit, turned into RGB
    // by the CIE colour matching functions. The components (flame spectroscopy):
    //   - incandescent soot (hydrocarbon fuels) and alumina particles (solid motors): a grey-body continuum, Planck's law at the particle
    //     temperature - yellow-orange at ~2000 K, white at 3000 K+
    //   - hydrocarbon radicals in the fuel-rich zone: CH at 387/389 and 431/432 nm (violet-blue), C2 Swan bands at 474, 516 and 563 nm
    //     (blue-green); OH peaks at 306 nm, in the ultraviolet, invisible (Zizak, Flame emission spectroscopy; Gaydon / Royal Society 1940)
    //   - hydrogen flames: a faint blue continuum, water-vapour bands, H-alpha 656 nm and the sodium doublet 588.99 / 589.59 nm from trace
    //     contamination, which is what turns a hydrogen flame orange (Visible emission of hydrogen flames; Resolving visible emission lines
    //     in hydrogen diffusion flames)
    // The relative strengths of the components (how much soot, how much radical emission in each zone) are calibrated against photographs;
    // the wavelengths and the colour science are not.
    // Colour matching: Wyman, Sloan & Shirley 2013, multi-lobe Gaussian fit of the CIE 1931 functions (their integrals match the CIE
    // tables to 0.1%). Pure C#, used by the mod and by the Unity previews.
    public static class PlumeSpectrum
    {
        private static double G(double l, double mu, double s1, double s2)
        {
            double s = l < mu ? s1 : s2;
            double t = (l - mu) / s;
            return Math.Exp(-0.5 * t * t);
        }

        private static void Cie(double l, out double x, out double y, out double z)
        {
            x = 1.056 * G(l, 599.8, 37.9, 31.0) + 0.362 * G(l, 442.0, 16.0, 26.7) - 0.065 * G(l, 501.1, 20.4, 26.2);
            y = 0.821 * G(l, 568.8, 46.9, 40.5) + 0.286 * G(l, 530.9, 16.3, 31.1);
            z = 1.217 * G(l, 437.0, 11.8, 36.0) + 0.681 * G(l, 459.0, 26.0, 13.8);
        }

        // Planck's law, normalised so that a 2000 K body is 1 at 600 nm (the absolute scale is set by the grey-body weights)
        private static double Planck(double lnm, double T)
        {
            const double h = 6.62607015e-34, c = 2.99792458e8, k = 1.380649e-23;
            double l = lnm * 1e-9;
            double b = 2.0 * h * c * c / Math.Pow(l, 5.0) / (Math.Exp(h * c / (l * k * T)) - 1.0);
            double l0 = 600e-9;
            double b0 = 2.0 * h * c * c / Math.Pow(l0, 5.0) / (Math.Exp(h * c / (l0 * k * 2000.0)) - 1.0);
            return b / b0;
        }

        private static double Line(double l, double mu, double sigma)
        {
            double t = (l - mu) / sigma;
            return Math.Exp(-0.5 * t * t) / sigma * 2.5;     // area-normalised, ~1 at a sigma of 2.5 nm
        }

        // What is emitting, in one zone of the plume. Weights are relative radiance.
        public struct Mix
        {
            public double bbT, bb;           // grey-body (soot / alumina): temperature K, weight
            public double radicals;          // CH + C2 chemiluminescence (hydrocarbons)
            public double blueContinuum;     // the hydrogen flame's blue continuum
            public double sodium;            // Na D doublet (trace contamination in hydrogen flames)
            public double hAlpha;            // H-alpha 656 nm
        }

        private const double RadicalScale = 25.0;

        public static double Spectrum(Mix m, double l)
        {
            double s = 0.0;
            if (m.bb > 0.0) s += m.bb * Planck(l, m.bbT);
            if (m.radicals > 0.0)
            {
                // narrow bands against a broad continuum: the line weights carry a scale so that "radicals = 1" is comparable to a soot weight of 1
                s += RadicalScale * m.radicals * (1.0 * Line(l, 431.5, 3.0) + 0.25 * Line(l, 388.5, 2.0)         // CH
                                 + 0.20 * Line(l, 516.5, 6.0) + 0.14 * Line(l, 473.7, 6.0) + 0.08 * Line(l, 563.5, 6.0));   // C2 Swan
            }
            if (m.blueContinuum > 0.0) s += m.blueContinuum * 0.5 * Math.Exp(-(l - 300.0) / 110.0);
            if (m.sodium > 0.0) s += m.sodium * 3.0 * (Line(l, 589.0, 0.9) + 0.5 * Line(l, 589.6, 0.9));
            if (m.hAlpha > 0.0) s += m.hAlpha * Line(l, 656.3, 1.2);
            return s;
        }

        // RGB (display space, normalised so the brightest channel is 1) of a mix
        public static void ToRgb(Mix m, out float r, out float g, out float b)
        {
            double X = 0, Y = 0, Z = 0;
            for (int l = 380; l <= 780; l += 2)
            {
                double x, y, z; Cie(l, out x, out y, out z);
                double v = Spectrum(m, l);
                X += x * v; Y += y * v; Z += z * v;
            }
            double rl = 3.2406 * X - 1.5372 * Y - 0.4986 * Z;
            double gl = -0.9689 * X + 1.8758 * Y + 0.0415 * Z;
            double bl = 0.0557 * X - 0.2040 * Y + 1.0570 * Z;
            double mx = Math.Max(Math.Max(rl, gl), Math.Max(bl, 1e-12));
            rl = Math.Max(rl, 0) / mx; gl = Math.Max(gl, 0) / mx; bl = Math.Max(bl, 0) / mx;
            r = (float)Math.Pow(rl, 1.0 / 2.2); g = (float)Math.Pow(gl, 1.0 / 2.2); b = (float)Math.Pow(bl, 1.0 / 2.2);   // the game renders in gamma space
        }

        // The four colours the plume shader needs, for a propellant. air: 0..1, how much oxygen from the atmosphere reaches the plume
        // (it burns the fuel-rich exhaust and, for hydrogen, brings the sodium-excited orange).
        public static void Palette(PropellantKind kind, double air, out float[] core, out float[] rim, out float[] knot, out float[] after)
        {
            Mix c = new Mix(), rm = new Mix(), kn = new Mix(), af = new Mix();
            switch (kind)
            {
                case PropellantKind.Kerolox:
                    // sooty: the grey-body of the soot over weak CH / C2
                    c = new Mix { bbT = 2300, bb = 1.0, radicals = 0.02 };
                    rm = new Mix { bbT = 1950, bb = 0.7, radicals = 0.03 };
                    kn = new Mix { bbT = 3000, bb = 1.2, radicals = 0.05 };
                    af = new Mix { bbT = 2150, bb = 1.0, radicals = 0.01 };
                    break;
                case PropellantKind.Methalox:
                    // little soot: radical emission dominates near the nozzle (blue, blue-white in the knots); downstream the cooler soot
                    // glow adds to the violet CH and the zone turns pink
                    c = new Mix { bbT = 2100, bb = 0.02, radicals = 0.06 };
                    rm = new Mix { bbT = 1900, bb = 0.07, radicals = 0.05 };
                    kn = new Mix { bbT = 2100, bb = 0.012, radicals = 0.08 };
                    af = new Mix { bbT = 1750, bb = 0.30, radicals = 0.02 };
                    break;
                case PropellantKind.Hydrolox:
                    // the blue continuum; with air around (the plume's afterburning with the entrained oxygen, and the trace sodium
                    // lit by it) the sodium doublet adds orange
                    c = new Mix { blueContinuum = 1.0 - 0.1 * air, sodium = 0.05 * air, hAlpha = 0.05 };
                    rm = new Mix { blueContinuum = 0.6 - 0.1 * air, sodium = 0.09 * air, hAlpha = 0.05 };
                    kn = new Mix { blueContinuum = 1.2 - 0.1 * air, sodium = 0.05 * air, hAlpha = 0.08 };
                    af = new Mix { blueContinuum = 0.3 - 0.05 * air, sodium = 0.12 * air, bbT = 1800, bb = 0.01 * air };
                    break;
                case PropellantKind.Solid:
                    // alumina particles: a grey body, reheated by the afterburning
                    c = new Mix { bbT = 2600, bb = 1.0 };
                    rm = new Mix { bbT = 2200, bb = 0.8 };
                    kn = new Mix { bbT = 3000, bb = 1.2 };
                    af = new Mix { bbT = 2150, bb = 1.0 };
                    break;
                case PropellantKind.Hypergolic:
                    c = new Mix { bbT = 2300, bb = 0.25, radicals = 0.02 };
                    rm = new Mix { bbT = 1900, bb = 0.25, radicals = 0.02 };
                    kn = new Mix { bbT = 2900, bb = 0.3, radicals = 0.03 };
                    af = new Mix { bbT = 2000, bb = 0.2, radicals = 0.01 };
                    break;
                default:    // nuclear hydrogen
                    c = new Mix { blueContinuum = 1.0, hAlpha = 0.1 };
                    rm = new Mix { blueContinuum = 0.6, hAlpha = 0.1 };
                    kn = new Mix { blueContinuum = 1.2, hAlpha = 0.15 };
                    af = new Mix { blueContinuum = 0.3, hAlpha = 0.05 };
                    break;
            }
            core = Rgb(c); rim = Rgb(rm); knot = Rgb(kn); after = Rgb(af);
        }

        private static float[] Rgb(Mix m)
        {
            float r, g, b;
            ToRgb(m, out r, out g, out b);
            return new[] { r, g, b };
        }
    }
}
