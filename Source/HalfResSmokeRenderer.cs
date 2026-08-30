using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricContrails
{
    // Which smoke volumes want drawing this frame. The renderers are never enabled in the
    // normal sense - they are drawn explicitly by the command buffer below - so this list
    // is what "visible" means for them.
    public static class SmokeRenderRegistry
    {
        public static readonly List<Renderer> Active = new List<Renderer>();

        public static void SetActive(Renderer r, bool active)
        {
            if (r == null) return;
            bool present = Active.Contains(r);
            if (active && !present) Active.Add(r);
            else if (!active && present) Active.Remove(r);
        }

        public static void Remove(Renderer r)
        {
            if (r != null) Active.Remove(r);
        }
    }

    // Draws the smoke into a reduced-resolution buffer and composites it back.
    //
    // Sample count is the quality ceiling - the same shader renders as flat plates at 12
    // march steps and as a cloud at 96 - and sample cost scales with PIXELS, which is why
    // artefacts are worst close up where the box fills the screen. Quartering the pixels
    // buys roughly four times the steps for the same cost.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class HalfResSmokeRenderer : MonoBehaviour
    {
        // after transparents, so the smoke sits where its Transparent-queue draw used to
        private const CameraEvent Stage = CameraEvent.AfterForwardAlpha;
        private static readonly int HalfResId = Shader.PropertyToID("_VolumetricContrailsHalfRes");

        private readonly Dictionary<Camera, CommandBuffer> buffers = new Dictionary<Camera, CommandBuffer>();
        private Material compositeMaterial;
        private Light sunLight;
        private float sunSearchTimer;

        private void Start()
        {
            if (ShaderCache.SmokeCompositeShader == null)
            {
                Debug.LogError("[HairyBlob] No composite shader - half-res rendering is off and smoke " +
                    "will not draw. Rebuild the AssetBundle.");
                enabled = false;
                return;
            }
            compositeMaterial = new Material(ShaderCache.SmokeCompositeShader);
        }

        private void OnDestroy()
        {
            foreach (KeyValuePair<Camera, CommandBuffer> pair in buffers)
            {
                if (pair.Key != null) pair.Key.RemoveCommandBuffer(Stage, pair.Value);
                pair.Value.Release();
            }
            buffers.Clear();
            if (compositeMaterial != null) Destroy(compositeMaterial);
        }

        // rebuilt every frame: the set of live volumes changes constantly, and a command
        // buffer is a recording rather than a callback
        // CommandBuffer.DrawRenderer draws the pass by hand, bypassing the forward
        // renderer that would normally bind per-light data - so _WorldSpaceLightPos0 is
        // meaningless here no matter what the pass is tagged, and the light march would
        // walk off in an arbitrary direction. Publishing the direction ourselves makes
        // self-shadowing independent of how the geometry gets submitted.
        private void UpdateSunDirection()
        {
            sunSearchTimer -= Time.deltaTime;
            if (sunLight == null || sunSearchTimer <= 0f)
            {
                sunSearchTimer = 5f; // scene lights change rarely; searching every frame is waste
                sunLight = RenderSettings.sun;
                if (sunLight == null)
                {
                    Light[] lights = FindObjectsOfType<Light>();
                    float best = -1f;
                    for (int i = 0; i < lights.Length; i++)
                    {
                        if (lights[i].type != LightType.Directional) continue;
                        if (lights[i].intensity <= best) continue;
                        best = lights[i].intensity;
                        sunLight = lights[i];
                    }
                }
            }

            if (sunLight != null)
            {
                // direction TO the light, matching _WorldSpaceLightPos0's convention
                Vector3 toSun = -sunLight.transform.forward;
                Shader.SetGlobalVector("_SmokeSunDir", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            }

            // Planet-up, for the shader's hemisphere ambient split (sky above vs. ground
            // bounce below). Body-relative, not Vector3.up: on a globe those diverge as
            // soon as the vessel is anywhere but directly over KSC, and the shading would
            // silently tilt with longitude. Falls back to world up only if there is no
            // body to reference.
            Vector3 up = Vector3.up;
            if (FlightGlobals.ActiveVessel != null)
            {
                CelestialBody body = FlightGlobals.ActiveVessel.mainBody;
                if (body != null)
                {
                    up = (FlightGlobals.ActiveVessel.transform.position - body.position).normalized;
                }
            }
            Shader.SetGlobalVector("_SmokeUpDir", new Vector4(up.x, up.y, up.z, 0f));
        }

        private void LateUpdate()
        {
            if (compositeMaterial == null) return;
            UpdateSunDirection();

            Camera cam = Camera.main;
            if (cam == null) return;

            CommandBuffer cb;
            if (!buffers.TryGetValue(cam, out cb))
            {
                cb = new CommandBuffer { name = "VolumetricContrails half-res smoke" };
                cam.AddCommandBuffer(Stage, cb);
                buffers[cam] = cb;
            }

            cb.Clear();

            if (SmokeRenderRegistry.Active.Count == 0) return;

            // Full resolution. Rendering the smoke into a half-size buffer and blitting it
            // back up is what produces the banded stripes across the plume: a half-width
            // buffer resolves the trail's silhouette at every other pixel and the bilinear
            // upscale smears that into stairs. The 2026-08-18 build had no intermediate
            // buffer at all - the volume was drawn straight at screen resolution - so this
            // divider is 1 to match it. The CommandBuffer path itself is kept because at
            // 1:1 the composite blit is a pass-through and cannot resample anything.
            const int ResolutionDivider = 1;
            int w = Mathf.Max(1, cam.pixelWidth / ResolutionDivider);
            int h = Mathf.Max(1, cam.pixelHeight / ResolutionDivider);

            // ARGBHalf, not ARGB32: the buffer holds premultiplied colour that gets
            // composited later, and 8 bits per channel bands visibly on smoke gradients
            cb.GetTemporaryRT(HalfResId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            cb.SetRenderTarget(HalfResId);
            cb.ClearRenderTarget(false, true, Color.clear);

            for (int i = 0; i < SmokeRenderRegistry.Active.Count; i++)
            {
                Renderer r = SmokeRenderRegistry.Active[i];
                if (r == null) continue;
                // DrawRenderer picks up the renderer's MaterialPropertyBlock, which is
                // where all the per-volume data (spine points, box, LOD) lives
                cb.DrawRenderer(r, r.sharedMaterial);
            }

            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cb.Blit(HalfResId, BuiltinRenderTextureType.CameraTarget, compositeMaterial);
            cb.ReleaseTemporaryRT(HalfResId);
        }
    }
}
