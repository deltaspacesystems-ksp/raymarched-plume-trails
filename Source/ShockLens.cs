using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RaymarchedPlumeTrails
{
    // Records the shock-lens pass on every scene camera while a blast wave is strong enough to be seen: copy the camera target, redraw the screen
    // through the displaced lookup (Shaders/ShockLens.shader). Same stage and the same method as the heat haze.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ShockLensRenderer : MonoBehaviour
    {
        private const CameraEvent Stage = CameraEvent.BeforeImageEffects;
        private static readonly int SrcId = Shader.PropertyToID("_RaymarchedPlumeTrailsShockSrc");
        private static readonly string[] SceneCameraNames = { "Camera 00", "Camera 01", "Camera 1", "Camera 0" };

        private readonly Dictionary<Camera, CommandBuffer> buffers = new Dictionary<Camera, CommandBuffer>();
        private Material material;
        private Mesh quad;
        private MaterialPropertyBlock block;
        private readonly Vector4[] vecA = new Vector4[6], vecB = new Vector4[6];
        private readonly List<BlastWave> use = new List<BlastWave>(6);

        private void Awake()
        {
            block = new MaterialPropertyBlock();
            quad = new Mesh { name = "RaymarchedPlumeTrails shock quad", vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) }, triangles = new[] { 0, 1, 2, 0, 2, 3 } };
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
            if (!ModSettings.Enabled || !SmokeTuning.ExplosionShockLens || ShaderCache.ShockLensShader == null) { ClearAll(); return; }
            use.Clear();
            for (int i = 0; i < BlastWaves.Active.Count; i++)
            {
                BlastWave w = BlastWaves.Active[i];
                if (w.alpha > 0.0004f) use.Add(w);
            }
            if (use.Count == 0) { ClearAll(); return; }
            if (use.Count > 6) { use.Sort((a, b) => b.alpha.CompareTo(a.alpha)); use.RemoveRange(6, use.Count - 6); }
            if (material == null) material = new Material(ShaderCache.ShockLensShader) { hideFlags = HideFlags.HideAndDontSave };
            for (int i = 0; i < use.Count; i++)
            {
                vecA[i] = new Vector4(use[i].centreWorld.x, use[i].centreWorld.y, use[i].centreWorld.z, use[i].radius);
                vecB[i] = new Vector4(use[i].alpha, use[i].width, 0f, 0f);
            }
            Camera[] all = Camera.allCameras;
            for (int c = 0; c < all.Length; c++)
                if (all[c] != null && IsSceneCamera(all[c])) Build(all[c], use.Count);
        }

        private void ClearAll()
        {
            foreach (KeyValuePair<Camera, CommandBuffer> pair in buffers) pair.Value.Clear();
        }

        private void Build(Camera cam, int n)
        {
            CommandBuffer cb;
            if (!buffers.TryGetValue(cam, out cb))
            {
                cb = new CommandBuffer { name = "RaymarchedPlumeTrails shock lens" };
                cam.AddCommandBuffer(Stage, cb);
                buffers[cam] = cb;
            }
            cb.Clear();
            block.Clear();
            block.SetInt("_ShockCount", n);
            block.SetVectorArray("_ShockA", vecA);
            block.SetVectorArray("_ShockB", vecB);
            block.SetMatrix("_ShockCamToWorld", cam.cameraToWorldMatrix);
            block.SetMatrix("_ShockWorldToCam", cam.worldToCameraMatrix);
            block.SetFloat("_ShockMax", 0.08f);
            cb.GetTemporaryRT(SrcId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
            cb.Blit(BuiltinRenderTextureType.CameraTarget, SrcId);
            cb.SetGlobalTexture("_ShockSrc", SrcId);
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cb.DrawMesh(quad, Matrix4x4.identity, material, 0, 0, block);
            cb.ReleaseTemporaryRT(SrcId);
        }
    }
}
