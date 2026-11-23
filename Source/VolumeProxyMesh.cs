using UnityEngine;

namespace RaymarchedPlumeTrails
{
    internal static class VolumeProxyMesh
    {
        private static Mesh cube;

        internal static Mesh Cube
        {
            get
            {
                if (cube != null) return cube;
                const float h = 0.5f;
                cube = new Mesh { name = "Plume volume bounds", hideFlags = HideFlags.HideAndDontSave };
                cube.vertices = new[]
                {
                    new Vector3(-h,-h,-h), new Vector3(h,-h,-h),
                    new Vector3(h,h,-h), new Vector3(-h,h,-h),
                    new Vector3(-h,-h,h), new Vector3(h,-h,h),
                    new Vector3(h,h,h), new Vector3(-h,h,h)
                };
                cube.triangles = new[]
                {
                    0,2,1, 0,3,2, 4,5,6, 4,6,7,
                    0,4,7, 0,7,3, 1,2,6, 1,6,5,
                    0,1,5, 0,5,4, 3,7,6, 3,6,2
                };
                cube.RecalculateBounds();
                return cube;
            }
        }
    }
}
