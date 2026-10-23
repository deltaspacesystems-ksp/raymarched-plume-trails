using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // A bag of up to ~100 independent spheres drawn by the smoke raymarcher as ONE volume. Each blob is three spine points
    // (a zero-radius separator, then two coincident points of the blob's radius: a segment with a zero-radius end draws
    // nothing, so nothing connects to anything). The point's w packs density + steam + heat (see SpinePointDensity in the
    // shader). Used by the pad cloud; the explosions have their own copy of the same upload.
    public sealed class BlobVolume
    {
        public const int MaxPts = 320;
        public const int MaxBlobs = 100;

        private GameObject go;
        private MeshRenderer mr;
        private MaterialPropertyBlock mb;
        private readonly Vector4[] pts = new Vector4[MaxPts];
        private readonly float[] radii = new float[MaxPts];
        private readonly Vector3[] pos = new Vector3[MaxPts];
        private readonly Vector4[] groups = new Vector4[MaxPts / 16];
        private int n;

        public int Count { get { return n / 3; } }
        public bool Full { get { return n + 4 > MaxPts - 1; } }

        public BlobVolume()
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "PlumeTrails.BlobVolume";
            Collider c = go.GetComponent<Collider>();
            if (c != null) Object.DestroyImmediate(c);
            mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.enabled = false;
            Material m = new Material(ShaderCache.SmokeVolumeShader);
            m.EnableKeyword("SMOKE_VOLUME_POLYLINE");
            m.SetFloat("_ZTestMode", 8f);
            mr.material = m;
            mb = new MaterialPropertyBlock();
        }

        public void Begin() { n = 0; }

        public bool Add(Vector3 p, float r, float density, float steam, float heat)
        {
            if (n + 4 > MaxPts - 1 || r < 0.05f) return false;
            float w = Mathf.Clamp(density, 0.03f, 1.4f)
                + 2f * Mathf.RoundToInt(Mathf.Clamp01(steam) * 7f)
                + 16f * Mathf.RoundToInt(Mathf.Clamp01(heat) * 7f);
            pos[n] = p; radii[n] = 0f; pts[n] = new Vector4(p.x, p.y, p.z, 1f); n++;
            pos[n] = p; radii[n] = r; pts[n] = new Vector4(p.x, p.y, p.z, w); n++;
            Vector3 q = p + Vector3.up * Mathf.Max(0.03f, r * 0.02f);
            pos[n] = q; radii[n] = r; pts[n] = new Vector4(q.x, q.y, q.z, w); n++;
            return true;
        }

        public void Hide()
        {
            if (mr != null) SmokeRenderRegistry.SetActive(mr, false);
        }

        public void Dispose()
        {
            if (mr != null) SmokeRenderRegistry.Remove(mr);
            if (go != null) Object.Destroy(go);
            go = null; mr = null;
        }

        // Uploads the blobs added since Begin(). The flame light is the fire that lights the fog (position, strength, range, colour).
        public void Upload(Vector3 flamePos, float flameStrength, float flameRange, Color flameColor, float fireEmission, float seed, float sootDark = 0f)
        {
            if (mr == null) return;
            if (n < 2) { Hide(); return; }
            // close the last blob
            if (n < MaxPts) { pos[n] = pos[n - 1]; radii[n] = 0f; pts[n] = new Vector4(pos[n].x, pos[n].y, pos[n].z, 1f); n++; }

            float warp = (SmokeTuning.SilhouetteWarpStrength + SmokeTuning.VortexStrength) * 1.733f;
            Vector3 bmin = Vector3.positiveInfinity, bmax = Vector3.negativeInfinity;
            float rSum = 0f, rMax = 0f; int rCount = 0;
            for (int i = 0; i < n; i++)
            {
                float r = radii[i];
                float b = r * 2.9f + warp + 0.5f * r + 10f;
                bmin = Vector3.Min(bmin, pos[i] - Vector3.one * b);
                bmax = Vector3.Max(bmax, pos[i] + Vector3.one * b);
                if (r > 0f) { rSum += r; rCount++; if (r > rMax) rMax = r; }
            }
            for (int i = n; i < MaxPts; i++) radii[i] = 0f;

            int last = n - 1;
            int gc = Mathf.Min((last + 15) / 16, groups.Length);
            for (int g = 0; g < gc; g++)
            {
                int s = g * 16, e = Mathf.Min(s + 16, last), lp = Mathf.Min(e, n - 1);
                Vector3 mn = pos[s], mx = mn;
                for (int i = s + 1; i <= lp; i++) { mn = Vector3.Min(mn, pos[i]); mx = Vector3.Max(mx, pos[i]); }
                Vector3 ctr = (mn + mx) * 0.5f;
                float reach = 0f;
                for (int i = s; i <= lp; i++)
                {
                    float rr = Vector3.Distance(ctr, pos[i]) + radii[i] * 1.4f * (1f + Mathf.Clamp01(SmokeTuning.EdgeProtrusion)) + warp + 0.6f * radii[i] + 10f;
                    if (rr > reach) reach = rr;
                }
                groups[g] = new Vector4(ctr.x, ctr.y, ctr.z, reach);
            }

            Vector3 bc = (bmin + bmax) * 0.5f, be = (bmax - bmin) * 0.5f;
            SmokeRenderRegistry.SetActive(mr, true);
            go.transform.position = bc;
            go.transform.localScale = be * 2f;

            mb.Clear();
            mb.SetInt("_SpineCount", n);
            mb.SetVectorArray("_SpinePoints", pts);
            mb.SetFloatArray("_SpineRadii", radii);
            mb.SetInt("_SpineGroupCount", gc);
            mb.SetVectorArray("_SpineGroupBounds", groups);
            mb.SetVector("_BoxCenter", new Vector4(bc.x, bc.y, bc.z, 0f));
            mb.SetVector("_BoxExtents", new Vector4(be.x, be.y, be.z, 0f));
            mb.SetFloat("_TileRadiusRatio", Mathf.Clamp((rCount > 0 ? rSum / rCount : 1f) / 18f, 0.05f, 200f));
            mb.SetFloat("_MaxSpineRadius", rMax);
            mb.SetVector("_FlameLight", new Vector4(flamePos.x, flamePos.y, flamePos.z, flameStrength * SmokeTuning.FlameLightStrength));
            mb.SetVector("_FlameColor", new Vector4(flameColor.r, flameColor.g, flameColor.b, flameRange));
            SmokeTuning.Apply(mb);
            mb.SetFloat("_Density", SmokeTuning.Density * 1.1f);
            mb.SetVector("_SmokeNoiseOffset", new Vector4(seed, seed * 0.37f, seed * 0.71f, 0f));
            mb.SetInt("_MarchSteps", 72);
            mb.SetFloat("_FireEmission", fireEmission);
            mb.SetFloat("_SootDark", sootDark);
            mr.SetPropertyBlock(mb);
            SmokeRenderRegistry.Register(mr, mb);
        }
    }
}
