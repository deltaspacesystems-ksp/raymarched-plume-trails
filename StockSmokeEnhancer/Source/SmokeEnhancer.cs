using System;
using System.Collections.Generic;
using UnityEngine;

namespace StockSmokeEnhancer
{
    /// <summary>
    /// Scans active smoke trail effects and multiplies emission / lifetime / size
    /// according to SmokeEnhancerSettings - only smoke, not engine flame/exhaust/spark
    /// effects, which look wrong when boosted the same way smoke does.
    ///
    /// Stock's built-in smoke trail (fx_smokeTrail_light/medium/heavy) is driven by the
    /// legacy KSPParticleEmitter wrapper, not directly by the underlying ParticleSystem:
    /// KSPParticleEmitter re-pushes its own minEmission/maxEmission/minEnergy/maxEnergy
    /// fields onto its ParticleSystem every frame, so multiplying the ParticleSystem
    /// directly gets silently overwritten a moment later. We boost the KSPParticleEmitter
    /// fields instead, which is what actually sticks. Some modded engines (e.g. ones using
    /// MODEL_MULTI_PARTICLE effects) drive a ParticleSystem directly with no
    /// KSPParticleEmitter involved at all, so we handle both, skipping any ParticleSystem
    /// that's already owned by a KSPParticleEmitter we've already boosted (avoids double
    /// multiplication).
    ///
    /// Emission is re-applied every frame from the value stock just wrote (stock re-derives
    /// it from a throttle curve every frame, so multiplying "current value" every frame is
    /// equivalent to multiplying "base value" - it does not compound).
    ///
    /// Lifetime/size are NOT re-written by stock every frame, so instead we cache the
    /// original ("base") value the first time we see a given instance and re-derive the
    /// boosted value from that cached base every frame. This is what makes slider changes
    /// in the UI apply live to already-running effects, without the value exploding from
    /// repeated multiplication.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SmokeEnhancer : MonoBehaviour
    {
        private static readonly string[] Keywords = { "smoke" };

        private struct EmitterBase
        {
            public int minEmission;
            public int maxEmission;
            public float minEnergy;
            public float maxEnergy;
            public float minSize;
            public float maxSize;
        }

        private readonly Dictionary<int, EmitterBase> emitterBases = new Dictionary<int, EmitterBase>();
        private readonly HashSet<int> handledParticleSystemIds = new HashSet<int>();

        private readonly Dictionary<int, ParticleSystem.MinMaxCurve> baseLifetime = new Dictionary<int, ParticleSystem.MinMaxCurve>();
        private readonly Dictionary<int, ParticleSystem.MinMaxCurve> baseSize = new Dictionary<int, ParticleSystem.MinMaxCurve>();

        private readonly HashSet<int> activeEmitterIds = new HashSet<int>();
        private readonly HashSet<int> activeParticleSystemIds = new HashSet<int>();
        private readonly List<int> staleIds = new List<int>();

        private void Awake()
        {
            SmokeEnhancerSettings.Load();
        }

        private void LateUpdate()
        {
            activeEmitterIds.Clear();
            activeParticleSystemIds.Clear();
            handledParticleSystemIds.Clear();

            KSPParticleEmitter[] emitters = FindObjectsOfType<KSPParticleEmitter>();
            for (int i = 0; i < emitters.Length; i++)
            {
                KSPParticleEmitter emitter = emitters[i];
                if (emitter == null || !IsTargetEffect(emitter.gameObject.name)) continue;

                int id = emitter.GetInstanceID();
                activeEmitterIds.Add(id);
                BoostEmitter(emitter, id);

                if (emitter.ps != null) handledParticleSystemIds.Add(emitter.ps.GetInstanceID());
            }

            ParticleSystem[] systems = FindObjectsOfType<ParticleSystem>();
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null || !IsTargetEffect(ps.gameObject.name)) continue;

                int id = ps.GetInstanceID();
                if (handledParticleSystemIds.Contains(id)) continue;

                activeParticleSystemIds.Add(id);
                BoostEmission(ps);
                BoostLifetimeAndSize(ps, id);
            }

            PruneStaleCacheEntries();
        }

        private static bool IsTargetEffect(string name)
        {
            for (int i = 0; i < Keywords.Length; i++)
            {
                if (name.IndexOf(Keywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private void BoostEmitter(KSPParticleEmitter emitter, int id)
        {
            if (!emitterBases.TryGetValue(id, out EmitterBase emitterBase))
            {
                emitterBase = new EmitterBase
                {
                    minEmission = emitter.minEmission,
                    maxEmission = emitter.maxEmission,
                    minEnergy = emitter.minEnergy,
                    maxEnergy = emitter.maxEnergy,
                    minSize = emitter.minSize,
                    maxSize = emitter.maxSize
                };
                emitterBases[id] = emitterBase;
            }

            emitter.minEmission = Mathf.RoundToInt(emitterBase.minEmission * SmokeEnhancerSettings.EmissionMultiplier);
            emitter.maxEmission = Mathf.RoundToInt(emitterBase.maxEmission * SmokeEnhancerSettings.EmissionMultiplier);
            emitter.minEnergy = emitterBase.minEnergy * SmokeEnhancerSettings.LifetimeMultiplier;
            emitter.maxEnergy = emitterBase.maxEnergy * SmokeEnhancerSettings.LifetimeMultiplier;
            emitter.minSize = emitterBase.minSize * SmokeEnhancerSettings.SizeMultiplier;
            emitter.maxSize = emitterBase.maxSize * SmokeEnhancerSettings.SizeMultiplier;
        }

        private static void BoostEmission(ParticleSystem ps)
        {
            ParticleSystem.EmissionModule emission = ps.emission;
            if (!emission.enabled) return;

            emission.rateOverTime = ScaleCurve(emission.rateOverTime, SmokeEnhancerSettings.EmissionMultiplier);
            emission.rateOverDistance = ScaleCurve(emission.rateOverDistance, SmokeEnhancerSettings.EmissionMultiplier);
        }

        private void BoostLifetimeAndSize(ParticleSystem ps, int id)
        {
            ParticleSystem.MainModule main = ps.main;

            if (!baseLifetime.TryGetValue(id, out ParticleSystem.MinMaxCurve lifetimeBase))
            {
                lifetimeBase = main.startLifetime;
                baseLifetime[id] = lifetimeBase;
            }
            main.startLifetime = ScaleCurve(lifetimeBase, SmokeEnhancerSettings.LifetimeMultiplier);

            if (!baseSize.TryGetValue(id, out ParticleSystem.MinMaxCurve sizeBase))
            {
                sizeBase = main.startSize;
                baseSize[id] = sizeBase;
            }
            main.startSize = ScaleCurve(sizeBase, SmokeEnhancerSettings.SizeMultiplier);
        }

        private void PruneStaleCacheEntries()
        {
            staleIds.Clear();
            foreach (int id in emitterBases.Keys)
            {
                if (!activeEmitterIds.Contains(id)) staleIds.Add(id);
            }
            for (int i = 0; i < staleIds.Count; i++)
            {
                emitterBases.Remove(staleIds[i]);
            }

            staleIds.Clear();
            foreach (int id in baseLifetime.Keys)
            {
                if (!activeParticleSystemIds.Contains(id)) staleIds.Add(id);
            }
            for (int i = 0; i < staleIds.Count; i++)
            {
                baseLifetime.Remove(staleIds[i]);
                baseSize.Remove(staleIds[i]);
            }
        }

        /// <summary>Multiplies a curve's value regardless of its mode (constant, two constants, curve, two curves).</summary>
        private static ParticleSystem.MinMaxCurve ScaleCurve(ParticleSystem.MinMaxCurve curve, float factor)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    curve.constant *= factor;
                    break;
                case ParticleSystemCurveMode.TwoConstants:
                    curve.constantMin *= factor;
                    curve.constantMax *= factor;
                    break;
                default: // Curve, TwoCurves
                    curve.curveMultiplier *= factor;
                    break;
            }

            return curve;
        }
    }
}
