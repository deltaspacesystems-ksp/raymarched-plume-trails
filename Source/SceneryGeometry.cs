using UnityEngine;
namespace RaymarchedPlumeTrails
{
    internal static class SceneryGeometry
    {
        private static readonly RaycastHit[] hits = new RaycastHit[24];
        private static int mask = -1;
        internal static int Mask
        {
            get
            {
                if (mask >= 0) return mask;
                mask = 0;
                foreach (string name in new[] { "Local Scenery", "Default", "TerrainColliders" })
                {
                    int layer = LayerMask.NameToLayer(name);
                    if (layer >= 0) mask |= 1 << layer;
                }
                return mask;
            }
        }
        internal static float SurfaceAlong(Vector3 origin, Vector3 direction, float reach)
        {
            int count = Physics.RaycastNonAlloc(origin, direction, hits, reach, Mask, QueryTriggerInteraction.Ignore);
            float best = -1f;
            for (int i = 0; i < count; i++)
                if (hits[i].collider != null && hits[i].collider.GetComponentInParent<Part>() == null
                    && hits[i].distance > 0.5f && (best < 0f || hits[i].distance < best)) best = hits[i].distance;
            return best;
        }
    }
}
