using System;

namespace RaymarchedPlumeTrails
{
    // No Unity dependency: emitters observe UT; only the renderer advances its
    // previous-frame timestamp. Multiple emitters cannot consume render delta.
    internal sealed class TrailSimulationClock
    {
        internal const double RewindToleranceSeconds = 0.000001;
        internal const double MaximumHistoryDeltaSeconds = 0.25;
        private bool haveRenderTime;
        private double lastRenderTime;
        private bool renderPending;
        private double pendingRenderTime;

        public bool HasTime { get; private set; }
        public double Now { get; private set; }
        public double PreviousRenderTime { get; private set; }
        public double RenderDeltaSeconds { get; private set; }

        public bool TryObserve(double universalTime, out bool rewound)
        {
            rewound = false;
            if (double.IsNaN(universalTime) || double.IsInfinity(universalTime)) return false;
            if (HasTime && universalTime < Now && Now - universalTime > RewindToleranceSeconds)
            {
                Reset();
                rewound = true;
            }
            // Clamp sub-microsecond roundoff so stored nodes remain ordered.
            Now = HasTime ? Math.Max(Now, universalTime) : universalTime;
            HasTime = true;
            return true;
        }

        // Returns whether image history must be invalidated. The quarter-second
        // simulation limit complements the renderer's real-time history limit.
        public bool BeginRenderFrame()
        {
            if (!HasTime) return true;
            PreviousRenderTime = haveRenderTime ? lastRenderTime : Now;
            RenderDeltaSeconds = Math.Min(double.MaxValue, Math.Max(0.0, Now - PreviousRenderTime));
            bool invalidate = !haveRenderTime || RenderDeltaSeconds > MaximumHistoryDeltaSeconds;
            pendingRenderTime = Now;
            renderPending = true;
            return invalidate;
        }

        // Skipped/empty uploads leave the last published frame unchanged.
        public void CommitRenderFrame()
        {
            if (!renderPending) return;
            lastRenderTime = pendingRenderTime;
            haveRenderTime = true;
            renderPending = false;
        }

        public float Age(double createdTime) { return AgeSeconds(Now, createdTime); }
        public float PreviousRenderAge(double createdTime) { return AgeSeconds(PreviousRenderTime, createdTime); }

        private static float AgeSeconds(double now, double createdTime)
        {
            // Subtract in double before reducing the short-lived age to float.
            return (float)Math.Min(float.MaxValue, Math.Max(0.0, now - createdTime));
        }

        public void Reset()
        {
            HasTime = haveRenderTime = renderPending = false;
            Now = PreviousRenderTime = RenderDeltaSeconds = lastRenderTime = pendingRenderTime = 0.0;
        }
    }
}
