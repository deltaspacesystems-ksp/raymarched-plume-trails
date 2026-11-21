using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Distance along a ray to the first scenery / terrain surface that is not a part of any vessel (the ground, the pad, a building), for where the exhaust hits.
    internal static class SurfaceProbe
    {
        private static readonly RaycastHit[] hits = new RaycastHit[24];
        private static int mask = -1;

        private static int Mask()
        {
            if (mask == -1)
            {
                mask = 0;
                foreach (string name in new[] { "Local Scenery", "Default", "TerrainColliders" })
                {
                    int layer = LayerMask.NameToLayer(name);
                    if (layer >= 0) mask |= 1 << layer;
                }
            }
            return mask;
        }

        public static bool Along(Vector3 origin, Vector3 dir, float maxDist, out float dist, out Vector3 normal)
        {
            dist = -1f; normal = Vector3.up;
            int n = Physics.RaycastNonAlloc(origin, dir, hits, maxDist, Mask(), QueryTriggerInteraction.Ignore);
            int bi = -1;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.GetComponentInParent<Part>() != null) continue;
                float d = hits[i].distance;
                if (d > 0.5f && (bi < 0 || d < dist)) { dist = d; bi = i; }
            }
            if (bi >= 0) normal = hits[bi].normal;
            return bi >= 0;
        }

        public static float Along(Vector3 origin, Vector3 dir, float maxDist)
        {
            float d; Vector3 n;
            return Along(origin, dir, maxDist, out d, out n) ? d : -1f;
        }
    }
}
