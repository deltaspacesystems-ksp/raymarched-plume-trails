using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RaymarchedPlumeTrails
{
    // The hot region of one exhaust, as the engine controller reports it every physics tick.
    public struct HazeSource
    {
        public Vector3 origin;      // nozzle
        public Vector3 dir;         // exhaust direction (unit)
        public float length;        // m
        public float radius0;       // m, at the nozzle
        public float radius1;       // m, at the far end
        public float strength;      // 0..1
        public float time;          // Time.time of the last report
    }

    public static class HeatHazeRegistry
    {
        private static readonly Dictionary<int, HazeSource> sources = new Dictionary<int, HazeSource>();

        public static void Report(int id, Vector3 origin, Vector3 dir, float length, float radius0, float radius1, float strength)
        {
            sources[id] = new HazeSource
            {
                origin = origin, dir = dir.normalized, length = length, radius0 = radius0, radius1 = radius1,
                strength = strength, time = Time.time
            };
        }

        public static void Clear(int id) { sources.Remove(id); }

        // fresh sources only: an engine that stopped reporting stops hazing within a fraction of a second
        public static int Collect(List<HazeSource> into)
        {
            into.Clear();
            List<int> stale = null;
            foreach (KeyValuePair<int, HazeSource> kv in sources)
            {
                if (Time.time - kv.Value.time > 0.3f)
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(kv.Key);
                    continue;
                }
                if (kv.Value.strength > 0.01f) into.Add(kv.Value);
            }
            if (stale != null) foreach (int id in stale) sources.Remove(id);
            return into.Count;
        }
    }

    // Records the heat-haze pass on every scene camera: copy the camera target, then redraw the part of the screen
    // around each hot exhaust through a displaced lookup into that copy (see Shaders/HeatHaze.shader).
    //
    // BeforeForwardAlpha, the same stage as the cast shadow: terrain, sky and vehicles are in the target and the depth
    // texture is resolved; the flame particles and the smoke are drawn afterwards, over the distorted background.
#if !EXPLOSIONS_ONLY
    [KSPAddon(KSPAddon.Startup.Flight, false)]
#endif
    public class HeatHazeRenderer : MonoBehaviour
    {
        // After all forward rendering (smoke and flame included): the Blit that copies the target must not run between
        // other passes that rely on the camera matrices (the cast shadow did, and drew nothing after it).
        private const CameraEvent Stage = CameraEvent.BeforeImageEffects;
        private static readonly int SrcId = Shader.PropertyToID("_RaymarchedPlumeTrailsHazeSrc");
        private static readonly string[] SceneCameraNames = { "Camera 00", "Camera 01", "Camera 1", "Camera 0" };

        private readonly Dictionary<Camera, CommandBuffer> buffers = new Dictionary<Camera, CommandBuffer>();
        private readonly List<HazeSource> active = new List<HazeSource>();
        private Material material;
        private Mesh quad;
        private MaterialPropertyBlock block;
        private readonly Vector4[] vecA = new Vector4[8], vecB = new Vector4[8], vecC = new Vector4[8];
        private float logTimer;
        private bool loggedOnce;

        private void Awake()
        {
            block = new MaterialPropertyBlock();
            quad = new Mesh
            {
                name = "RaymarchedPlumeTrails haze quad",
                vertices = new[]
                {
                    new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f),
                    new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e9f);
        }

        private void OnDestroy()
        {
            foreach (KeyValuePair<Camera, CommandBuffer> pair in buffers)
            {
                if (pair.Key != null) pair.Key.RemoveCommandBuffer(Stage, pair.Value);
                pair.Value.Release();
            }
            buffers.Clear();
            if (material != null) Destroy(material);
        }

        private static bool IsSceneCamera(Camera cam)
        {
            for (int i = 0; i < SceneCameraNames.Length; i++) if (cam.name == SceneCameraNames[i]) return true;
            return false;
        }

        private void LateUpdate()
        {
            if (!ModSettings.Enabled || !SmokeTuning.HeatHaze || ShaderCache.HeatHazeShader == null) { ClearAll(); return; }
            if (material == null) material = new Material(ShaderCache.HeatHazeShader) { hideFlags = HideFlags.HideAndDontSave };

            int n = HeatHazeRegistry.Collect(active);
            if (n == 0) { ClearAll(); return; }
            if (n > 8) { active.Sort((a, b) => b.strength.CompareTo(a.strength)); n = 8; }

            for (int i = 0; i < n; i++)
            {
                HazeSource s = active[i];
                vecA[i] = new Vector4(s.origin.x, s.origin.y, s.origin.z, s.length);
                vecB[i] = new Vector4(s.dir.x, s.dir.y, s.dir.z, s.radius0);
                vecC[i] = new Vector4(s.strength, s.radius1, 0f, 0f);
            }

            Camera[] all = Camera.allCameras;
            int drawn = 0;
            for (int c = 0; c < all.Length; c++)
            {
                if (all[c] == null || !IsSceneCamera(all[c])) continue;
                if (Build(all[c], n)) drawn++;
            }

            if (!loggedOnce || Time.unscaledTime > logTimer)
            {
                if (SmokeTuning.DebugLogging || !loggedOnce)
                    Debug.Log("[PlumeTrails] heat haze: sources=" + n + " cameras drawn=" + drawn);
                loggedOnce = true;
                logTimer = Time.unscaledTime + 5f;
            }
        }

        private void ClearAll()
        {
            foreach (KeyValuePair<Camera, CommandBuffer> pair in buffers) pair.Value.Clear();
        }

        // NDC rectangle that surely contains the hot regions on screen. Null when nothing is in front of the camera.
        private bool ScreenRect(Camera cam, int n, out Vector4 rect)
        {
            float x0 = 2f, y0 = 2f, x1 = -2f, y1 = -2f;
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                HazeSource s = active[i];
                Vector3 centre = s.origin + s.dir * (s.length * 0.5f);
                float R = s.length * 0.5f + Mathf.Max(s.radius0, s.radius1) * 1.4f;
                Vector3 toC = centre - cam.transform.position;
                float dist = toC.magnitude;
                if (dist <= R * 1.05f) { rect = new Vector4(-1f, -1f, 1f, 1f); return true; }   // camera inside: whole screen
                if (Vector3.Dot(toC, cam.transform.forward) <= 0f) continue;
                Vector3 vp = cam.WorldToViewportPoint(centre);
                // angular radius -> NDC extent
                float ang = Mathf.Asin(Mathf.Clamp(R / dist, 0f, 0.999f));
                float halfFovY = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
                float ndcY = Mathf.Tan(ang) / Mathf.Tan(halfFovY);
                float ndcX = ndcY / cam.aspect;
                float cx = vp.x * 2f - 1f, cy = vp.y * 2f - 1f;
                x0 = Mathf.Min(x0, cx - ndcX); x1 = Mathf.Max(x1, cx + ndcX);
                y0 = Mathf.Min(y0, cy - ndcY); y1 = Mathf.Max(y1, cy + ndcY);
                any = true;
            }
            // The rectangle is in viewport orientation; the clip space the quad is drawn in may be flipped vertically
            // (a camera rendering into a texture). Covering the mirror image too costs a bigger rectangle and makes the
            // orientation irrelevant - the shader decides per pixel, from the real view ray.
            float ya = Mathf.Min(y0, -y1), yb = Mathf.Max(y1, -y0);
            rect = new Vector4(Mathf.Clamp(x0 - 0.05f, -1f, 1f), Mathf.Clamp(ya - 0.05f, -1f, 1f),
                               Mathf.Clamp(x1 + 0.05f, -1f, 1f), Mathf.Clamp(yb + 0.05f, -1f, 1f));
            return any && rect.z > rect.x && rect.w > rect.y;
        }

        private bool Build(Camera cam, int n)
        {
            CommandBuffer cb;
            if (!buffers.TryGetValue(cam, out cb))
            {
                cb = new CommandBuffer { name = "RaymarchedPlumeTrails heat haze" };
                cam.AddCommandBuffer(Stage, cb);
                buffers[cam] = cb;
            }
            cb.Clear();

            Vector4 rect;
            if (!ScreenRect(cam, n, out rect)) return false;

            block.Clear();
            block.SetInt("_HazeCount", n);
            block.SetVector("_HazeRect", rect);
            block.SetVectorArray("_HazeA", vecA);
            block.SetVectorArray("_HazeB", vecB);
            block.SetVectorArray("_HazeC", vecC);
            block.SetFloat("_HazeAmp", SmokeTuning.HeatHazeAmount);
            block.SetFloat("_HazeScale", SmokeTuning.HeatHazeScale);
            block.SetFloat("_HazeSpeed", SmokeTuning.HeatHazeSpeed);
            block.SetFloat("_HazeTimeNow", Time.time);
            block.SetFloat("_HazeMax", SmokeTuning.HeatHazeMaxShift);
            block.SetMatrix("_HazeCamToWorld", cam.cameraToWorldMatrix);

            // the camera target as it is right now, then the distorted rectangle over it
            cb.GetTemporaryRT(SrcId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
            cb.Blit(BuiltinRenderTextureType.CameraTarget, SrcId);
            cb.SetGlobalTexture("_HazeSrc", SrcId);
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cb.DrawMesh(quad, Matrix4x4.identity, material, 0, 0, block);
            cb.ReleaseTemporaryRT(SrcId);
            return true;
        }
    }
}
