using System.Threading;

namespace RaymarchedPlumeTrails
{
    // Process-local identities shared by all controller instances. A matched
    // group keeps its identity instead of recomputing one from vessel numbers.
    internal static class EmitterIdentity
    {
        private static int next;
        public static int Next() { return Interlocked.Increment(ref next); }
    }
}
