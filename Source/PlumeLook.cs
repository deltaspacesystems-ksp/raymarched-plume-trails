using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // How the physical plume (PlumePhysics) is turned into what the PlumeCore shader draws: the colour of the exhaust per
    // propellant, the size of the box around it, and the shader's properties. Pure Unity code, shared by the mod and by Plume Lab's
    // Unity render server, so a plume previewed there is the plume drawn in the game.
    public static class PlumeLook
    {
        public struct Look
        {
            public double radBoost;     // K above the gas temperature at which the particles glow (alumina in a solid motor)
            public double gasBoost;     // how much of the plume is the gas's own line emission (hydrogen / methane flames)
            public double sootScale;    // incandescent soot, relative to the propellant's typical loading
            public Color core, rim, knot;
            public Color after;         // colour of the afterburning zone (rgb) and how much it replaces the black body orange (a)
        }

        private static readonly System.Collections.Generic.Dictionary<int, float[][]> paletteCache = new System.Collections.Generic.Dictionary<int, float[][]>();
        private static void Palette(PropellantKind kind, double air, out float[] core, out float[] rim, out float[] knot, out float[] after)
        {
            int q = (int)System.Math.Round(System.Math.Max(0.0, System.Math.Min(1.0, air)) * 10.0);
            int key = (int)kind * 16 + q;
            float[][] p;
            if (!paletteCache.TryGetValue(key, out p))
            {
                float[] c, r, k, a;
                PlumeSpectrum.Palette(kind, q / 10.0, out c, out r, out k, out a);
                p = new[] { c, r, k, a };
                paletteCache[key] = p;
            }
            core = p[0]; rim = p[1]; knot = p[2]; after = p[3];
        }

        public static Look For(PropellantKind k)
        {
            Look l = new Look
            {
                radBoost = 0, gasBoost = 1, sootScale = 1,
                core = new Color(0.95f, 0.38f, 0.82f), rim = new Color(0.35f, 0.45f, 1.0f), knot = new Color(1.0f, 0.45f, 0.7f), after = new Color(1f, 0.5f, 0.2f, 0f)
            };
            switch (k)
            {
                case PropellantKind.Kerolox: l.radBoost = 520; l.gasBoost = 0.5; break;                       // orange: soot
                case PropellantKind.Methalox:                                                                   // violet-blue, pink knots (Raptor)
                    l.gasBoost = 9; l.sootScale = 0.08;
                    break;                                                                                  // colours: PlumeSpectrum
                case PropellantKind.Hydrolox: l.gasBoost = 2.2; l.sootScale = 0; break;                         // nearly clear; colours: PlumeSpectrum
                case PropellantKind.Solid: l.radBoost = 900; l.gasBoost = 0.3; break;                          // white-hot: alumina
                case PropellantKind.Hypergolic: l.gasBoost = 1.2; l.sootScale = 0.4; break;
                default: l.gasBoost = 2; l.sootScale = 0; break;                                                // nuclear hydrogen
            }
            return l;
        }

        // the length of the plume that is drawn and the radius of the box around it
        public static void RenderBox(PlumeState s, float scale, out float length, out float rMax, float deflectAt = 0f, float streak = 0f)
        {
            double len = System.Math.Min(s.coreLength * 5.1, 22.0 * s.De + 8.0 * s.Ls) * scale;
            // the long-exposure STREAK of a high-altitude launch: tens of core lengths of luminous column, not five
            if (streak > 0.01f) len = System.Math.Max(len, len + (System.Math.Min(s.coreLength * 55.0, 1500.0) * scale - len) * streak);
            len = System.Math.Min(len, streak > 0.01f ? 1500.0 : 320.0);
            double r = System.Math.Max(s.Dj, s.De) * 0.9 + s.spread * len + 0.6 * s.De + 0.5 * s.Ls * System.Math.Max(0.0, System.Math.Min(s.tanLip, 3.0)) + (s.PR > 1.5 ? len * System.Math.Max(0.0, System.Math.Min(s.tanLip, 3.0)) : 0.0);
            length = (float)System.Math.Max(len, 1.0);
            if (deflectAt > 0f && deflectAt < len) r = System.Math.Max(r, (len - deflectAt) * 1.05 + 0.6 * s.De);      // the turned flame runs sideways
            rMax = (float)System.Math.Min(r, 140.0);
        }

        public static void Fill(MaterialPropertyBlock mb, PlumeState s, PropellantKind kind, Vector3 origin, Vector3 axis, Vector3 right,
                                float length, float rMax, float exposure, int steps, float seed, float timeOverride = 0f, float deflectAt = 0f, Vector3 deflectNormal = default(Vector3), float deflectSpread = 1f, float streak = 0f)
        {
            ExhaustChem ch = ExhaustChem.For(kind);
            Look l = For(kind);
            // the colours come from the spectrum of what emits (PlumeSpectrum), with the oxygen the atmosphere brings
            float[] pc, pr, pk, pa;
            Palette(kind, s.oxygen, out pc, out pr, out pk, out pa);
            l.core = new Color(pc[0], pc[1], pc[2]); l.rim = new Color(pr[0], pr[1], pr[2]); l.knot = new Color(pk[0], pk[1], pk[2]);
            l.after = new Color(pa[0], pa[1], pa[2], 1f);
            double thr = s.PcFull > 0 ? s.Pc / s.PcFull : 1.0;
            mb.SetVector("_PlumeFrame", new Vector4(origin.x, origin.y, origin.z, length));
            mb.SetVector("_PlumeAxis", new Vector4(axis.x, axis.y, axis.z, rMax));
            mb.SetVector("_PlumeRight", new Vector4(right.x, right.y, right.z, 0f));
            mb.SetVector("_PlumeA", new Vector4((float)s.De, (float)s.Dj, (float)s.Ls, (float)s.coreLength));
            mb.SetVector("_PlumeB", new Vector4((float)s.spread, (float)s.shockStrength * (kind == PropellantKind.Solid ? 0.12f : 1f), (float)s.machDiskX, (float)s.machDiskR));
            mb.SetVector("_PlumeC", new Vector4((float)s.Te, (float)(s.ambientTemp > 0 ? s.ambientTemp : 285.0), (float)thr, (float)l.radBoost));
            mb.SetVector("_PlumeF", new Vector4((float)System.Math.Min(s.tanLip, 3.0), (float)s.oxygen, (float)System.Math.Min(s.PR, 60.0), (float)(2.0 / System.Math.Max(s.gamma - 1.0, 0.05))));
            mb.SetVector("_PlumeD", new Vector4((float)(ch.sootLoading * l.sootScale), (float)ch.afterburn, (float)(ch.emissivityGas * l.gasBoost), exposure));
            mb.SetVector("_PlumeE", new Vector4(seed, steps, (float)s.Uc, timeOverride));
            mb.SetVector("_PlumeJ", kind == PropellantKind.Solid ? new Vector4(0.55f, 0.9f, 0f, 0f) : (kind == PropellantKind.Kerolox ? new Vector4(0.12f, 0.25f, 0f, 0f) : Vector4.zero));
            if (kind == PropellantKind.Solid) l.after = new Color(1f, 0.70f, 0.40f, 1f);        // a solid motor burns cream-white (alumina) with an orange fringe
            mb.SetColor("_PlumeCoreCol", l.core);
            mb.SetColor("_PlumeRimCol", l.rim);
            mb.SetColor("_PlumeKnotCol", l.knot);
            mb.SetColor("_PlumeAfterCol", l.after);
            mb.SetVector("_PlumeG", new Vector4(0f, kind == PropellantKind.Hydrolox ? 0.35f : 0.1f, deflectSpread, Mathf.Clamp01(streak)));
            mb.SetVector("_PlumeH", deflectAt > 0f ? new Vector4(deflectAt, deflectNormal.x, deflectNormal.y, deflectNormal.z) : Vector4.zero);   // x: far field (off); y: floor of the afterburning glow (hydrogen has no soot, its burning is the glow)
        }
    }
}
