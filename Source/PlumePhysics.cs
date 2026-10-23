using System;

namespace RaymarchedPlumeTrails
{
    // PLUME PHYSICS. What a rocket exhaust is, from the engine's own numbers - no pictures, no hand-drawn flame:
    //
    //   chamber (Pc, Tc, gamma, molecular weight) --isentropic nozzle (area ratio)--> exit state (Mach, Pe, Te, ve, density)
    //   exit state + ambient pressure --> the free jet: fully expanded diameter Dj and Mach number Mj, the shock-cell length Ls
    //   (Prandtl), the Mach disk (Ashkenas-Sherman), the potential-core length, the turbulent spread of the mixing layer,
    //   and how far the fuel-rich gas burns with the air (afterburning).
    // The renderer then evaluates the field analytically (temperature, soot / alumina loading, emission) along the axis, so the
    // plume is volumetric and changes by itself with altitude (it swells and the diamonds stretch as the air thins), throttle
    // and propellant. Pure C#, no engine types: used by the mod, and by Plume Lab through the Unity render server.
    //
    // The correlations are the standard engineering ones (NASA SP-8120 / Ashkenas & Sherman 1966 / Tam & Tanna 1982 for the
    // shock cells, Abramovich for the mixing layer). They are approximations of a real CFD plume - but they respond to the right
    // inputs in the right direction, which is what a real-time plume needs.
    public enum PropellantKind { Kerolox, Hydrolox, Methalox, Solid, Hypergolic, NuclearH2 }

    // chemistry of the exhaust, per propellant
    public struct ExhaustChem
    {
        public double gamma;          // effective ratio of specific heats of the flow
        public double molWeight;      // g/mol
        public double Tc;             // chamber temperature, K
        public double Pc;             // typical chamber pressure, Pa
        public double expansion;      // typical nozzle area ratio (sea-level-ish)
        public double sootLoading;    // how much incandescent condensate the exhaust carries (0 = none, 1 = kerosene)
        public double afterburn;      // how fuel-rich the exhaust is (burns when it mixes with air), 0..1
        public double emissivityGas;  // faint line emission of the gas itself (OH, H2O bands), 0..1
        public double spread;         // turbulent spread rate of the mixing layer (db/dx)

        public static ExhaustChem For(PropellantKind k)
        {
            switch (k)
            {
                case PropellantKind.Kerolox:   return new ExhaustChem { gamma = 1.22, molWeight = 22.5, Tc = 3670, Pc = 10e6, expansion = 16, sootLoading = 1.0, afterburn = 0.7, emissivityGas = 0.15, spread = 0.085 };
                case PropellantKind.Hydrolox:  return new ExhaustChem { gamma = 1.23, molWeight = 13.6, Tc = 3550, Pc = 20e6, expansion = 69, sootLoading = 0.0, afterburn = 0.25, emissivityGas = 0.5,  spread = 0.075 };
                case PropellantKind.Methalox:  return new ExhaustChem { gamma = 1.21, molWeight = 20.6, Tc = 3550, Pc = 30e6, expansion = 34, sootLoading = 0.25, afterburn = 0.45, emissivityGas = 0.3, spread = 0.08 };
                case PropellantKind.Solid:     return new ExhaustChem { gamma = 1.18, molWeight = 28.5, Tc = 3450, Pc = 6.2e6, expansion = 8, sootLoading = 2.2, afterburn = 1.6, emissivityGas = 0.2, spread = 0.09 };
                case PropellantKind.Hypergolic:return new ExhaustChem { gamma = 1.22, molWeight = 22.0, Tc = 3300, Pc = 5e6, expansion = 40, sootLoading = 0.12, afterburn = 0.35, emissivityGas = 0.25, spread = 0.08 };
                default:                       return new ExhaustChem { gamma = 1.30, molWeight = 2.2, Tc = 2700, Pc = 3e6, expansion = 100, sootLoading = 0.0, afterburn = 0.1, emissivityGas = 0.1, spread = 0.07 };
            }
        }
    }

    // How the nozzle shapes the flow it releases. Conical: exit half-angle ~15 deg (about 98% efficient); bell / parabolic: a smaller exit
    // angle; aerospike / plug: no outer wall, the ambient pressure itself confines the jet against the spike, so the effective nozzle
    // adapts to the altitude (en.wikipedia.org/wiki/Rocket_engine_nozzle, /Aerospike_engine)
    public enum NozzleType { Bell, Conical, Aerospike }

    public struct EngineSpec
    {
        public PropellantKind kind;
        public double thrust;         // N, one nozzle, at full throttle
        public double mdot;           // kg/s, one nozzle, at full throttle (thrust / (Isp g0) for the game's engines)
        public double throttle;       // 0..1
        public double chamberPressure;     // Pa, 0 = the propellant's typical value
        public double expansionRatio;      // nozzle area ratio, 0 = the propellant's typical value
        public double exitDiameter;        // m, 0 = derived from the mass flow and the exit state
        // The engine's own specific impulse in vacuum and at sea level (s), 0 = unknown. They give the nozzle: from the thrust equation
        // F = mdot ve + (Pe - Pa) Ae, so Isp_vac - Isp_SL = Pa0 Ae / (mdot g0)  =>  the exit area Ae; and Isp_vac fixes the exit Mach number.
        public double ispVac, ispSL;
        public NozzleType nozzle;          // default Bell
        public double exitHalfAngleDeg;    // nozzle exit half-angle, degrees; 0 = the type's typical value
        // the atmosphere the jet flows into (0 = Earth air)
        public double ambientTemp;         // K, 0 = 285
        public double ambientMolarWeight;  // g/mol, 0 = 28.96 (air)
        public bool noOxygen;              // true: no oxygen to burn the fuel-rich exhaust with (CO2 / H2 atmospheres, vacuum)
    }

    public struct PlumeState
    {
        // nozzle exit
        public double Me, Pe, Te, ve, rhoe, De;
        // the engine at this throttle and ambient pressure
        public double Pa, Pc, PcFull, PR;       // PcFull: the chamber pressure at full throttle (Pc / PcFull = the throttle)
        // PR = Pe / Pa: > 1 under-expanded (swells), < 1 over-expanded (contracts)
        // the free jet
        public double Mj, Dj;                   // fully expanded jet: Mach number and diameter
        public double Ls;                       // shock-cell length
        public double machDiskX, machDiskR;     // position and radius of the Mach disk (0 = none)
        public double coreLength;               // potential core: how far the jet keeps its exit temperature
        public double spread;                   // mixing-layer growth rate db/dx
        public double Uj, Uc;                   // fully expanded jet speed and the convection speed of its large turbulent structures (m/s)
        public double shockStrength;            // 0..1, how visible the cells are (the pressure mismatch)
        public double lipAngle, tanLip;         // angle (rad, + outward) the jet boundary leaves the nozzle lip at
        public double oxygen;                   // 0..1: how much air-oxygen the afterburning has to work with
        public double ambientTemp;              // K
        public bool separated;                  // the flow has separated from the nozzle wall (strongly over-expanded)
        // chemistry (for the renderer)
        public double sootLoading, afterburn, gasEmission, Tc;
        public double gamma;
    }

    public static class PlumePhysics
    {
        private const double R_UNIVERSAL = 8314.462;    // J / (kmol K)
        public const double G0 = 9.80665;

        // A / A* for isentropic flow at Mach M
        public static double AreaRatio(double M, double g)
        {
            double t = (2.0 / (g + 1.0)) * (1.0 + 0.5 * (g - 1.0) * M * M);
            return (1.0 / M) * Math.Pow(t, (g + 1.0) / (2.0 * (g - 1.0)));
        }

        // supersonic Mach number for a given area ratio (bisection)
        public static double MachFromAreaRatio(double eps, double g)
        {
            double lo = 1.0001, hi = 40.0;
            if (eps <= 1.0) return 1.0001;
            for (int i = 0; i < 70; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (AreaRatio(mid, g) < eps) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        public static double PressureRatio(double M, double g)   // P0 / P
        {
            return Math.Pow(1.0 + 0.5 * (g - 1.0) * M * M, g / (g - 1.0));
        }

        public static double MachFromPressureRatio(double p0OverP, double g)
        {
            double x = Math.Pow(Math.Max(p0OverP, 1.0), (g - 1.0) / g) - 1.0;
            return Math.Sqrt(Math.Max(2.0 / (g - 1.0) * x, 0.0));
        }

        public const double SeparationRatio = 0.38;         // Summerfield: separation when exit pressure < ~30-45% of ambient (Rocket engine nozzle)
        public const double DefaultBellHalfAngleDeg = 10.0; // typical bell exit half-angle (smaller than the 15 deg cone)

        // Prandtl-Meyer function (en.wikipedia.org/wiki/Prandtl-Meyer_expansion_fan): nu(M) = sqrt((g+1)/(g-1)) atan(sqrt((g-1)/(g+1) (M^2-1))) - atan(sqrt(M^2-1))
        public static double PrandtlMeyer(double M, double g)
        {
            double m2 = Math.Max(M * M - 1.0, 0.0);
            double k = Math.Sqrt((g + 1.0) / (g - 1.0));
            return k * Math.Atan(Math.Sqrt(m2 / k / k)) - Math.Atan(Math.Sqrt(m2));
        }
        public static double PrandtlMeyerMax(double g) { return 0.5 * Math.PI * (Math.Sqrt((g + 1.0) / (g - 1.0)) - 1.0); }

        public static PlumeState Compute(EngineSpec e, double ambientPa)
        {
            ExhaustChem ch = ExhaustChem.For(e.kind);
            double g = ch.gamma;
            double Rs = R_UNIVERSAL / ch.molWeight;                    // specific gas constant, J/(kg K)
            double eps = e.expansionRatio > 0 ? e.expansionRatio : ch.expansion;
            double PcFull = e.chamberPressure > 0 ? e.chamberPressure : ch.Pc;
            double thr = Math.Max(e.throttle, 0.02);

            // The nozzle from the engine's own Isp curve (when it is known and no explicit nozzle is configured): the exit area from the
            // vacuum / sea-level difference, the exit Mach number from the vacuum Isp (Isp_vac g0 = ve + Rs Te / ve), the chamber pressure
            // from the exit pressure that the mass flow requires. A low expansion ratio (a small Isp gain in vacuum) therefore gives a nozzle
            // that is nearly pressure-matched at sea level - and no shock diamonds there.
            double derivedDe = 0.0;
            if (e.ispVac > 0 && e.ispSL > 0 && e.ispVac - e.ispSL > 0.5 && e.expansionRatio <= 0 && e.exitDiameter <= 0 && e.mdot > 0)
            {
                double Aex = (e.ispVac - e.ispSL) * G0 * e.mdot / 101325.0;
                double target = e.ispVac * G0;
                double lo = 1.3, hi = 12.0;
                for (int it = 0; it < 60; it++)
                {
                    double mid = 0.5 * (lo + hi);
                    double Tt = ch.Tc / (1.0 + 0.5 * (g - 1.0) * mid * mid);
                    double vv = mid * Math.Sqrt(g * Rs * Tt);
                    if (vv + Rs * Tt / vv < target) lo = mid; else hi = mid;
                }
                double MeD = 0.5 * (lo + hi);
                double TeD = ch.Tc / (1.0 + 0.5 * (g - 1.0) * MeD * MeD);
                double veD = MeD * Math.Sqrt(g * Rs * TeD);
                double PeD = e.mdot * Rs * TeD / (veD * Aex);
                double deD = Math.Sqrt(4.0 * Aex / Math.PI);
                double epsD = AreaRatio(MeD, g), pcD = PeD * PressureRatio(MeD, g);
                // an odd Isp curve (almost no vacuum gain, a made-up value) must not give a pin-sized nozzle or a absurd chamber pressure
                if (deD > 0.1 && deD < 15.0 && epsD > 1.5 && epsD < 400.0 && pcD > 2e5 && pcD < 4e7
                    && !double.IsNaN(deD) && !double.IsNaN(pcD))
                {
                    eps = epsD; PcFull = pcD; derivedDe = deD;
                }
            }

            // nozzle geometry from the FULL-throttle exit state (the nozzle does not change with throttle)
            double Me = MachFromAreaRatio(eps, g);
            double fT = 1.0 + 0.5 * (g - 1.0) * Me * Me;
            double TeFull = ch.Tc / fT;
            double veFull = Me * Math.Sqrt(g * Rs * TeFull);
            double PeFull = PcFull / PressureRatio(Me, g);
            double rhoeFull = PeFull / (Rs * TeFull);
            double De = e.exitDiameter > 0 ? e.exitDiameter : derivedDe;
            if (De <= 0.0)
            {
                // A_e from the mass flow through the exit: mdot = rho_e v_e A_e
                double Ae = e.mdot / Math.Max(rhoeFull * veFull, 1e-6);
                De = Math.Sqrt(4.0 * Ae / Math.PI);
            }

            PlumeState s = new PlumeState();
            s.gamma = g; s.Tc = ch.Tc;
            s.sootLoading = ch.sootLoading; s.afterburn = ch.afterburn; s.gasEmission = ch.emissivityGas; s.spread = ch.spread;

            // at this throttle the chamber pressure (and the mass flow) scale, the exit Mach number and temperature do not
            s.Pc = PcFull * thr; s.PcFull = PcFull;
            double PeNozzle = PeFull * thr;
            double paAmb = Math.Max(ambientPa, PeNozzle * 1e-4);        // vacuum: keep the numbers finite

            // What the jet really is when it leaves the nozzle depends on the nozzle type and on the ambient pressure:
            //  - aerospike / plug: the ambient pressure confines the jet against the spike, so below the design altitude the jet expands only
            //    to the ambient pressure (an altitude-compensating nozzle); above it it is under-expanded as usual
            //  - a bell / conical nozzle that is strongly over-expanded (exit pressure below roughly 30-45% of the ambient) separates
            //    from the wall: the jet leaves from the separation point, narrower than the nozzle exit (Summerfield criterion)
            double MeEff = Me, DeEff = De;
            s.separated = false;
            if (e.nozzle == NozzleType.Aerospike && PeNozzle < paAmb)
            {
                MeEff = Math.Max(MachFromPressureRatio(s.Pc / paAmb, g), 1.05);
                DeEff = De * Math.Sqrt(AreaRatio(MeEff, g) / AreaRatio(Me, g));
            }
            else if (PeNozzle < SeparationRatio * paAmb)
            {
                MeEff = Math.Max(MachFromPressureRatio(s.Pc / (SeparationRatio * paAmb), g), 1.05);
                DeEff = De * Math.Sqrt(AreaRatio(MeEff, g) / AreaRatio(Me, g));
                s.separated = true;
            }
            double TeEff = ch.Tc / (1.0 + 0.5 * (g - 1.0) * MeEff * MeEff);
            // The visible exit of the plume stays the nozzle's own: a separated jet still leaves the bell, filling it with the recirculating
            // exhaust, so the plume is not drawn narrower than the nozzle (DeEff only enters the jet's expansion below)
            s.Me = MeEff; s.Te = TeEff; s.ve = MeEff * Math.Sqrt(g * Rs * TeEff); s.De = De;
            s.Pe = s.Pc / PressureRatio(MeEff, g); s.rhoe = s.Pe / (Rs * TeEff);
            s.Pa = paAmb;
            s.PR = s.Pe / s.Pa;
            double DeGeom = De;
            De = DeEff;

            // fully expanded jet: expand isentropically from the chamber to the ambient pressure
            s.Mj = Math.Max(MachFromPressureRatio(s.Pc / s.Pa, g), 1.05);
            double arJ = AreaRatio(s.Mj, g), arE = AreaRatio(MeEff, g);
            s.Dj = De * Math.Sqrt(arJ / arE);                           // < De over-expanded, > De under-expanded

            // The angle the jet boundary leaves the nozzle lip at: the nozzle's exit half-angle plus the Prandtl-Meyer turning of the
            // expansion fan at the lip, nu(Mj) - nu(Me) (positive: under-expanded, the jet swells; negative: over-expanded, it necks in).
            // The turning is limited by nu_max (about 130 deg for air). Conical exit angle ~15 deg, bell smaller, plug nozzle none.
            double thetaE = e.nozzle == NozzleType.Aerospike ? 0.0
                          : (e.exitHalfAngleDeg > 0 ? e.exitHalfAngleDeg : (e.nozzle == NozzleType.Conical ? 15.0 : DefaultBellHalfAngleDeg)) * Math.PI / 180.0;
            double nuE = PrandtlMeyer(MeEff, g), nuJ = PrandtlMeyer(s.Mj, g), nuMax = PrandtlMeyerMax(g);
            double theta0 = thetaE + (nuJ - nuE);
            theta0 = Math.Max(-0.30, Math.Min(theta0, Math.Min(thetaE + nuMax - nuE, 1.25)));   // over-expanded necking is limited (oblique shock, not isentropic)
            s.lipAngle = theta0; s.tanLip = Math.Tan(theta0);
            s.De = DeGeom;      // drawn with the nozzle's own exit; the separated jet's smaller diameter only set Dj, Ls and the Mach disk

            // the atmosphere
            double Ta = e.ambientTemp > 0 ? e.ambientTemp : 285.0;
            double Ra = R_UNIVERSAL / (e.ambientMolarWeight > 0 ? e.ambientMolarWeight : 28.96);
            s.ambientTemp = Ta;
            s.oxygen = e.noOxygen ? 0.0 : Math.Sqrt(Math.Min(1.0, ambientPa / 1000.0));

            // shock cells (Tam & Tanna / Prandtl): they get longer as the jet gets faster, and vanish when the pressures match
            // Prandtl-Pack: 1.306 Dj sqrt(Mj^2-1); NASA GRC PIV (Wernet 2021) shows real cells about 25% shorter than the vortex-sheet theory
            double rawLs = 0.6 * s.Dj * Math.Sqrt(Math.Max(s.Mj * s.Mj - 1.0, 0.1));
            s.Ls = Math.Max(rawLs, 0.35 * De);
            double mism = Math.Abs(Math.Log(Math.Max(s.PR, 1e-4)));      // 0 when matched
            // a nearly pressure-matched jet (|ln Pe/Pa| small) has hardly any shock structure: the diamonds only appear with a real mismatch
            double mt = Math.Max(0.0, Math.Min(1.0, (mism - 0.12) / (1.5 - 0.12)));
            s.shockStrength = mt * mt * (3.0 - 2.0 * mt);

            // the Mach disk: only for strongly under-expanded jets (Ashkenas & Sherman: x = 0.67 D_e sqrt(P0 / Pa))
            if (s.PR > 3.0)
            {
                s.machDiskX = 0.67 * De * Math.Sqrt(s.Pc / s.Pa);
                s.machDiskR = 0.5 * s.Dj * Math.Min(0.8, 0.25 + 0.1 * Math.Log(s.PR));
            }

            // The large turbulent structures of the shear layer are carried at the convective velocity (Papamoschou & Roshko 1988, J. Fluid
            // Mech. 197: with the ambient air at rest Uc = U1 / (1 + sqrt(rho_ambient / rho_jet))). A hot, light rocket jet in dense air
            // gives Uc of about a quarter of the jet speed: the eddies of the tail flash past at hundreds of m/s.
            double Tj = ch.Tc / (1.0 + 0.5 * (g - 1.0) * s.Mj * s.Mj);
            s.Uj = s.Mj * Math.Sqrt(g * Rs * Tj);
            double rhoRatio = Rs * Tj / (Ra * Ta);                 // rho_ambient / rho_jet at the same (fully expanded) pressure
            s.Uc = s.Uj / (1.0 + Math.Sqrt(Math.Max(rhoRatio, 0.05)));

            // The potential core is about 6 effective (fully expanded) diameters for supersonic jets and gets ~30% shorter when the jet
            // is hot (Wernet, Georgiadis & Locke 2021, NASA GRC, AIAA 2021-0596: PIV + Raman scattering, M 1.36-2.0).
            s.coreLength = Math.Max(6.5 * s.Dj, 5.0 * De);     // the luminous column of a kerosene engine is 7-10 nozzle diameters long (photographs)
            return s;
        }

        // ------------------------------------------------------------------ the field (evaluated per sample by the renderer too)
        // The jet boundary radius at axial distance x: the exit radius swells to the fully expanded size (or contracts) and
        // oscillates once per shock cell, damping away as mixing takes over.
        public static double BoundaryRadius(PlumeState s, double x)
        {
            double r0 = 0.5 * s.De, rj = 0.5 * s.Dj;
            double cell = Math.Cos(2.0 * Math.PI * x / s.Ls);
            double damp = Math.Exp(-x / (4.0 * s.coreLength));
            double rb = rj + (r0 - rj) * cell * damp;
            return Math.Max(rb, 0.15 * r0);
        }

        // Temperature (K) on the axis relative position: 1 inside the core, decaying as the jet mixes with the air
        public static double AxialDecay(PlumeState s, double x)
        {
            if (x <= s.coreLength) return 1.0;
            return Math.Pow(s.coreLength / x, 1.0);                    // excess temperature falls like 1/x
        }

        // Blackbody colour (linear RGB, normalised to the brightest channel) of a gas / soot at temperature T (K).
        // Piecewise fit of the Planckian locus, good between about 900 K and 6000 K.
        public static void Blackbody(double T, out double r, out double g, out double b)
        {
            double t = Math.Max(T, 800.0) / 100.0;
            // Tanner Helland's approximation, T in hundreds of kelvin
            if (t <= 66.0) { r = 1.0; g = Clamp01((99.4708025861 * Math.Log(t) - 161.1195681661) / 255.0); }
            else { r = Clamp01(1.292936186 * Math.Pow(t - 60.0, -0.1332047592)); g = Clamp01(1.129890861 * Math.Pow(t - 60.0, -0.0755148492)); }
            if (t >= 66.0) b = 1.0;
            else if (t <= 19.0) b = 0.0;
            else b = Clamp01((138.5177312231 * Math.Log(t - 10.0) - 305.0447927307) / 255.0);
        }

        private static double Clamp01(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }
    }
}
