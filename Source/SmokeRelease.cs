using UnityEngine;
namespace RaymarchedPlumeTrails
{
    internal static class SmokeRelease
    {
        public const float RExhaust = 354f;
        public static float MixtureR(float Me, float M) { return (RExhaust * Me + 287.05f * Mathf.Max(M - Me, 0f)) / Mathf.Max(M, 1e-6f); }
        public static float Density(float P, float T, float Me, float M)
        {
            return P / (MixtureR(Me, M) * Mathf.Max(T, 50f));
        }

        public static float RadiusFromMass(float M, float rho)
        {
            return Mathf.Pow(3f * M / (4f * Mathf.PI * Mathf.Max(rho, 1e-5f)), 1f / 3f);
        }

        public static float JetDilution(float x, float d, float rhoAmbient, float rhoExit, float max)
        {
            return Mathf.Clamp(0.32f * (x / Mathf.Max(d, 0.05f)) * Mathf.Sqrt(rhoAmbient / Mathf.Max(rhoExit, 1e-4f)), 1f, max);
        }
    }
}
