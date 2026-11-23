using System.Collections.Generic;

namespace RaymarchedPlumeTrails
{
    // Capacity management only. This preserves the existing every-other-node
    // policy; geometric/property-error LOD belongs to a separate selection stage.
    internal static class TrailHistoryBudget
    {
        internal static int Select(int[] chains, int count, int target,
            bool[] keep, int[] previous, int[] next, Dictionary<int, int> scratch,
            out int evicted)
        {
            scratch.Clear();
            for (int i = 0; i < count; i++)
            {
                int last;
                previous[i] = scratch.TryGetValue(chains[i], out last) ? last : -1;
                next[i] = -1;
                if (previous[i] >= 0) next[previous[i]] = i;
                scratch[chains[i]] = i;
            }

            scratch.Clear();
            int retained = 0;
            int half = count / 2;
            for (int i = 0; i < count; i++)
            {
                int ordinal;
                if (!scratch.TryGetValue(chains[i], out ordinal)) ordinal = 0;
                scratch[chains[i]] = ordinal + 1;
                keep[i] = i >= half || previous[i] < 0 || next[i] < 0 || (ordinal & 1) == 0;
                if (keep[i]) retained++;
            }

            // Many short chains cannot be simplified. Prefer recent history to
            // letting the upload truncate the newest smoke and live heads.
            evicted = 0;
            for (int i = 0; i < count && retained > target; i++)
            {
                if (!keep[i]) continue;
                keep[i] = false;
                retained--;
                evicted++;
            }
            return retained;
        }
    }
}
