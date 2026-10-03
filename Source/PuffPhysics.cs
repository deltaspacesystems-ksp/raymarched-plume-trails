using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Lagrangian "puff" model of an exhaust plume, replacing the hand-tuned kinematics.
    //
    // A puff is a parcel of exhaust gas that entrains ambient air as it moves, and obeys conservation of
    // mass, momentum and heat. Nothing here is fitted to a look; every number is a measured constant from
    // the turbulence / plume literature (see Docs/PHYSICS.md):
    //
    //   * Entrainment (Morton, Taylor & Turner 1956; Turner 1973): the rate air is drawn into the parcel is
    //     proportional to its surface area times its velocity relative to the ambient air,
    //         dM/dt = rho_a * 4 pi r^2 * alpha * |v - v_wind|
    //     alpha ~ 0.055 for a momentum-driven jet (Fischer et al. 1979: 0.0535-0.057) and
    //     ~ 0.2-0.25 for a buoyant thermal (Turner 1973; Scorer 1957), blended here by how much of the
    //     motion is momentum and how much is buoyancy-driven.
    //   * Momentum: the entrained air arrives with the WIND's velocity, so the parcel decelerates and is bent
    //     downwind by the same mechanism, with no separate drag or wind-drift term:
    //         (M + k rho_a V) dv/dt = dM/dt (v_wind - v) + (rho_a - rho_p) V g
    //     k = 0.5 is the added (virtual) mass of a sphere.
    //   * Heat: mixing is mass-weighted, T' = (M T + dM T_a) / (M + dM), plus dry-adiabatic cooling/heating
    //     g/cp = 9.8 K/km as the parcel rises/sinks, so a stable (inversion) layer stops the rise by itself.
    //   * Buoyancy uses the ideal-gas density of the exhaust/air mixture at the AMBIENT pressure, with the
    //     ambient density, pressure and temperature taken from the body's real atmosphere.
    //   * Ground (Craft & Launder 1996; Hall & Ewing 2007): a parcel meeting the ground does not bounce like
    //     a ball. Its normal momentum is turned into a radial WALL JET along the surface (fraction wallJet),
    //     a small rebound (restitution) remains, and the tangential flow is slowed by wall friction.
    public static class PuffPhysics
    {
        public const float G0 = 9.80665f;
        public const float RAir = 287.05f;      // J/(kg K), dry air
        public const float RExhaust = 354f;     // J/(kg K): molar mass ~23.5 g/mol (HCl, H2O, CO, CO2, N2 + Al2O3 solids)
        public const float Cp = 1005f;          // J/(kg K)

        public struct Ambient
        {
            public float rho;   // kg/m3
            public float T;     // K
            public float P;     // Pa
            public Vector3 wind; // m/s, relative to the (rotating) surface
        }

        public struct State
        {
            public Vector3 vel;     // m/s relative to the surface-fixed frame
            public float M;         // total mass of the parcel (kg)
            public float Me;        // exhaust mass in it (kg), constant
            public float T;         // temperature (K)
            public float M0;        // mass when released, for the dilution (Me/M) display
            public float radius;    // equivalent sphere radius (m), updated by Step
        }

        public struct Params
        {
            public float alphaJet, alphaThermal;
            public float ambientTurb;   // m/s, entrainment velocity floor from ambient turbulence
            public float addedMass;     // virtual-mass coefficient k
            public float groundRestitution;
            public float wallJetFraction;
            public float groundFriction; // 1/s per (speed / radius)
        }

        public static Params Defaults()
        {
            return new Params
            {
                alphaJet = 0.055f, alphaThermal = 0.25f, ambientTurb = 0.15f, addedMass = 0.5f,
                groundRestitution = 0.1f, wallJetFraction = 0.9f, groundFriction = 0.04f
            };
        }

        public static float MixtureR(float Me, float M)
        {
            return (RExhaust * Me + RAir * Mathf.Max(M - Me, 0f)) / Mathf.Max(M, 1e-6f);
        }

        public static float Density(float P, float T, float Me, float M)
        {
            return P / (MixtureR(Me, M) * Mathf.Max(T, 50f));
        }

        public static float RadiusFromMass(float M, float rho)
        {
            return Mathf.Pow(3f * M / (4f * Mathf.PI * Mathf.Max(rho, 1e-5f)), 1f / 3f);
        }

        // One explicit step. up = local vertical, g = local gravity (m/s^2).
        public static void Step(ref State s, float dt, Ambient a, Vector3 up, float g, Params p)
        {
            float rhoP = Density(a.P, s.T, s.Me, s.M);
            float V = s.M / rhoP;
            float r = Mathf.Pow(3f * V / (4f * Mathf.PI), 1f / 3f);

            Vector3 rel = s.vel - a.wind;
            float speed = rel.magnitude;

            // blend jet <-> thermal entrainment by the ratio of forced to buoyant velocity scale
            float gReduced = g * Mathf.Abs(a.rho - rhoP) / Mathf.Max(rhoP, 1e-4f);
            float wThermal = Mathf.Sqrt(gReduced * r);
            float forced = speed / (speed + wThermal + 1e-3f);
            float alpha = Mathf.Lerp(p.alphaThermal, p.alphaJet, forced);
            float ue = alpha * speed + p.ambientTurb;

            float dM = a.rho * 4f * Mathf.PI * r * r * ue * dt;

            float Meff = s.M + p.addedMass * a.rho * V;
            Vector3 buoyancy = (a.rho - rhoP) * V * g * up;
            s.vel = (Meff * s.vel + dM * a.wind + buoyancy * dt) / (Meff + dM);

            s.T = (s.M * s.T + dM * a.T) / (s.M + dM);
            s.M += dM;
            // dry-adiabatic cooling while rising (heating while sinking)
            s.T -= (g / Cp) * Vector3.Dot(s.vel, up) * dt;
            s.T = Mathf.Max(s.T, 0.5f * a.T);

            float rhoNew = Density(a.P, s.T, s.Me, s.M);
            s.radius = RadiusFromMass(s.M, rhoNew);
        }

        // The parcel hits a surface with outward unit normal n. Its normal momentum is not bounced back like a
        // ball: it becomes a wall jet running along the surface in `outward` (a fraction wallJet of the normal
        // speed), a small rebound (restitution) stays, and wall friction slows the tangential flow.
        public static void Collide(ref State s, Vector3 n, Vector3 outward, float dt, Params p)
        {
            float vn = Vector3.Dot(s.vel, n);
            Vector3 vt = s.vel - n * vn;
            if (vn < 0f)
            {
                vt += outward * (-vn * p.wallJetFraction);
                vn = -vn * p.groundRestitution;
            }
            float sp = vt.magnitude;
            if (sp > 0.01f && s.radius > 0.01f) vt *= Mathf.Exp(-p.groundFriction * sp / s.radius * dt);
            s.vel = vt + n * vn;
        }

        // Flat ground (terrain / sea level). h = height of the parcel's centre above it. Returns the height the
        // centre should be moved to (>= 0.6 r), or h when it is clear of the surface.
        public static float Ground(ref State s, float h, Vector3 up, Vector3 outward, float dt, Params p)
        {
            float floor = 0.6f * s.radius;
            if (h >= floor) return h;
            Collide(ref s, up, outward, dt, p);
            return floor;
        }

        // Saturation vapour pressure of water (Pa), Magnus / Tetens form: over water above 0 C, over ice below.
        public static float SaturationPressure(float T)
        {
            float tc = T - 273.15f;
            return tc >= 0f
                ? 611.2f * Mathf.Exp(17.62f * tc / (243.12f + tc))
                : 611.2f * Mathf.Exp(22.46f * tc / (272.62f + tc));
        }

        // CONDENSATION. The parcel holds the exhaust's own water (mass fraction fw of the exhaust mass) plus the
        // water vapour of the air it has entrained (relative humidity rh0 at the surface, thinning with altitude
        // with a ~2 km scale height). Whatever vapour exceeds what the parcel's temperature can hold at saturation
        // (mixing ratio 0.622 es / (P - es) per kg of dry mass) condenses to droplets. Returns the liquid-water
        // MASS FRACTION of the parcel. Hot, fresh exhaust holds all its water as invisible vapour; as it mixes and
        // cools it passes the dew point and the cloud appears; in dry or warm air it evaporates again.
        public static float CondensedFraction(Ambient a, State s, float fw, float rh0, float altitude)
        {
            float mAir = Mathf.Max(s.M - s.Me, 0f);
            float rh = Mathf.Clamp01(rh0 * Mathf.Exp(-Mathf.Max(altitude, 0f) / 2000f));
            float ea = Mathf.Min(rh * SaturationPressure(a.T), 0.95f * a.P);
            float qa = 0.622f * ea / Mathf.Max(a.P - ea, 1f);
            float mw = s.Me * fw + mAir * qa / (1f + qa);
            float md = Mathf.Max(s.M - mw, 1e-6f);
            float esp = Mathf.Min(SaturationPressure(s.T), 0.99f * a.P);
            float mvSat = 0.622f * esp / Mathf.Max(a.P - esp, 1f) * md;
            return Mathf.Max(mw - mvSat, 0f) / Mathf.Max(s.M, 1e-6f);
        }

        // Turbulent round jet, Ricou & Spalding (1961): entrained mass flux at distance x from the nozzle,
        //   mdot(x)/mdot0 = 0.32 * (x / d) * sqrt(rho_a / rho_e)
        public static float JetDilution(float x, float d, float rhoAmbient, float rhoExit, float max)
        {
            return Mathf.Clamp(0.32f * (x / Mathf.Max(d, 0.05f)) * Mathf.Sqrt(rhoAmbient / Mathf.Max(rhoExit, 1e-4f)), 1f, max);
        }

        // Wind profile in the neutral surface layer: u(z) = u10 * ln(z / z0) / ln(10 / z0)  (Monin-Obukhov, neutral)
        public static float WindAt(float u10, float height, float z0)
        {
            float z = Mathf.Max(height, 2f * z0);
            return u10 * Mathf.Log(z / z0) / Mathf.Log(10f / z0);
        }
    }
}
