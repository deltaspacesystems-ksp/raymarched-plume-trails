using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Named quality presets plus a frame-time governor, in the manner of EVE's cloud
    // quality settings: a few discrete levels the player picks from, and a scaler that
    // only ever trades RESOLUTION and SAMPLE COUNT for speed - it never changes what the
    // smoke looks like, only how finely it is sampled.
    public static class SmokeQuality
    {
        public struct Profile
        {
            public float renderScale;   // fraction of screen resolution the volume renders at
            public float marchScale;    // multiplier on the primary march step budget
            public int lightNear;       // shadow-ray samples with the camera close to the volume
            public int lightFull;       // ... at full-quality distance
            public int lightMin;        // ... at the far LOD floor
            public int shadowSteps;     // ground cast-shadow march
        }

        public static readonly string[] PresetNames = { "Low", "Medium", "High", "Ultra" };

        private static readonly Profile[] Presets =
        {
            new Profile { renderScale = 0.60f, marchScale = 0.50f, lightNear = 2, lightFull = 2, lightMin = 2, shadowSteps = 6 },
            new Profile { renderScale = 0.80f, marchScale = 0.75f, lightNear = 2, lightFull = 4, lightMin = 2, shadowSteps = 10 },
            // High reproduces the constants that were hard-coded before presets existed.
            new Profile { renderScale = 1.00f, marchScale = 1.00f, lightNear = 3, lightFull = 4, lightMin = 2, shadowSteps = 16 },
            new Profile { renderScale = 1.00f, marchScale = 1.50f, lightNear = 6, lightFull = 6, lightMin = 4, shadowSteps = 24 },
        };

        // What each governor step multiplies resolution / samples by.
        private static readonly float[] StepScale = { 1f, 0.85f, 0.72f, 0.60f };

        public static int AutoStep { get; private set; }
        public static float FrameMs { get { return ema * 1000f; } }

        // The profile actually in force: the chosen preset, scaled by the governor.
        public static Profile Current
        {
            get
            {
                Profile p = Presets[Mathf.Clamp(SmokeTuning.QualityPreset, 0, Presets.Length - 1)];
                float a = StepScale[Mathf.Clamp(AutoStep, 0, StepScale.Length - 1)];
                p.renderScale = Mathf.Max(0.40f, p.renderScale * a);
                p.marchScale = Mathf.Max(0.35f, p.marchScale * a);
                p.shadowSteps = Mathf.Max(4, Mathf.RoundToInt(p.shadowSteps * a));
                return p;
            }
        }

        private static float ema = 0.02f;
        private static float degradeTimer, recoverTimer;
        private static float verifyAt = -1f, emaAtStep;
        private static float lockUntil;
        private static int failedAttempts;

        // Called once a frame from the renderer. smokeActive = a plume is actually on
        // screen, so an idle frame never counts against the smoke.
        public static void Tick(bool smokeActive)
        {
            float dt = Time.unscaledDeltaTime;
            // loading hitches and alt-tabs would otherwise read as "the smoke is slow"
            if (dt <= 0f || dt > 0.5f) return;
            ema = Mathf.Lerp(ema, dt, 1f - Mathf.Exp(-dt / 0.6f));

            if (!SmokeTuning.AutoQuality)
            {
                AutoStep = 0;
                degradeTimer = recoverTimer = 0f;
                verifyAt = -1f;
                return;
            }

            float now = Time.unscaledTime;
            float target = Mathf.Max(SmokeTuning.TargetFrameMs, 8f) * 0.001f;

            // Did the last step actually buy anything? KSP is very often CPU-bound, and in
            // that case dropping smoke resolution costs quality for nothing. If the frame
            // time did not move, undo it and stop trying for a while - longer each time.
            if (verifyAt > 0f && now >= verifyAt)
            {
                verifyAt = -1f;
                if (AutoStep > 0 && ema > emaAtStep * 0.985f)
                {
                    AutoStep--;
                    failedAttempts++;
                    lockUntil = now + Mathf.Min(45f * Mathf.Pow(2f, failedAttempts - 1), 600f);
                    if (SmokeTuning.DebugLogging)
                        Debug.Log("[PlumeTrails] quality: step did not help, reverted; smoke is not the bottleneck");
                }
                else
                {
                    failedAttempts = 0;
                }
            }

            if (!smokeActive) { degradeTimer = 0f; return; }
            if (now < lockUntil) return;

            // Wide hysteresis band (1.10 down, 0.72 up) and a long hold before recovering,
            // so it settles instead of oscillating between two neighbouring levels.
            if (ema > target * 1.10f) { degradeTimer += dt; recoverTimer = 0f; }
            else if (ema < target * 0.72f) { recoverTimer += dt; degradeTimer = 0f; }
            else { degradeTimer = 0f; recoverTimer = 0f; }

            if (degradeTimer > 1.5f && AutoStep < StepScale.Length - 1)
            {
                emaAtStep = ema;
                AutoStep++;
                verifyAt = now + 3f;
                degradeTimer = 0f;
                Report("degraded");
            }
            else if (recoverTimer > 8f && AutoStep > 0)
            {
                AutoStep--;
                recoverTimer = 0f;
                Report("recovered");
            }
        }

        private static void Report(string what)
        {
            Profile p = Current;
            Debug.Log(string.Format(
                "[PlumeTrails] quality: {0} to step {1} (scale {2:F2}, march x{3:F2}, shadow {4}) at {5:F1}ms, target {6:F0}ms",
                what, AutoStep, p.renderScale, p.marchScale, p.shadowSteps, ema * 1000f, SmokeTuning.TargetFrameMs));
        }
    }
}
