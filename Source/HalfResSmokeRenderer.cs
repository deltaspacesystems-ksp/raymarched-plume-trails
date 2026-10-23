using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RaymarchedPlumeTrails
{
    // Which smoke volumes want drawing this frame. The renderers are never enabled in the
    // normal sense - they are drawn explicitly by the command buffer below - so this list
    // is what "visible" means for them.
    public static class SmokeRenderRegistry
    {
        public static readonly List<Renderer> Active = new List<Renderer>();

        // The property block each renderer was given, kept alongside it.
        //
        // The cast-shadow pass needs the same spine arrays the volume pass uses, and the
        // obvious way to get them - Renderer.GetPropertyBlock - is not reliable for ARRAY
        // properties: it is a copy, and array round-tripping through it has never been
        // dependable. Holding the original object removes the question entirely.
        public static readonly Dictionary<Renderer, MaterialPropertyBlock> Blocks =
            new Dictionary<Renderer, MaterialPropertyBlock>();

        // Fixed drawing order for volumes that interpenetrate (sorting those by distance makes them swap places as the camera moves):
        // lower priority is drawn first = behind. 0 (the default) = sorted by distance among themselves. The pad fluid is -1 (always behind
        // the trails), the physical plumes +1 (always in front).
        public static readonly Dictionary<Renderer, int> Priority = new Dictionary<Renderer, int>();
        public static void SetPriority(Renderer r, int priority) { if (r != null) Priority[r] = priority; }

        public static void Register(Renderer r, MaterialPropertyBlock block)
        {
            if (r != null && block != null) Blocks[r] = block;
        }

        public static void SetActive(Renderer r, bool active)
        {
            if (r == null) return;
            bool present = Active.Contains(r);
            if (active && !present) Active.Add(r);
            else if (!active && present) Active.Remove(r);
        }

        public static void Remove(Renderer r)
        {
            if (r == null) return;
            Active.Remove(r);
            Blocks.Remove(r);
            Priority.Remove(r);
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
        // Where the smoke is laid over the picture. After the transparent queue (the default) it lies on top of everything transparent, clouds
        // included - and cloud layers write no depth, so smoke that is BEHIND a cloud deck is drawn over it. "Before clouds" puts it earlier, so
        // the transparent cloud layers are drawn over the smoke (right for smoke behind them, wrong for smoke in front of them): a switch to test.
        private CameraEvent Stage { get { return SmokeTuning.SmokeLast ? CameraEvent.BeforeImageEffects : (SmokeTuning.SmokeBeforeClouds ? CameraEvent.BeforeForwardAlpha : CameraEvent.AfterForwardAlpha); } }
        private readonly Dictionary<Camera, CameraEvent> bufferStage = new Dictionary<Camera, CameraEvent>();
        private static readonly int HalfResId = Shader.PropertyToID("_RaymarchedPlumeTrailsHalfRes");

        private readonly Dictionary<Camera, CommandBuffer> buffers = new Dictionary<Camera, CommandBuffer>();
        // Second buffer, at a different stage: the cast shadow has to land on the scene
        // BEFORE the smoke itself is composited over it, and it multiplies the camera
        // target rather than drawing into the smoke's own buffer.
        // BeforeForwardAlpha, not AfterForwardOpaque. The pass reads _CameraDepthTexture,
        // and at the earlier event that texture is not reliably resolved yet - which would
        // make every pixel read as sky, take the early-out, and output white. White is the
        // identity for a multiply blend, so the pass would run and be perfectly invisible.
        private const CameraEvent ShadowStage = CameraEvent.BeforeForwardAlpha;
        private readonly Dictionary<Camera, CommandBuffer> shadowBuffers = new Dictionary<Camera, CommandBuffer>();
        private Mesh fullscreenQuad;
        private MaterialPropertyBlock scratchBlock;
        private bool shadowDiagLogged;
        private readonly List<Renderer> sortedVolumes = new List<Renderer>();

        private float proxScale = 1f, proxWanted = 1f, proxSince;
        private float ProximityScale(Camera cam)
        {
            Vector3 eye = cam.transform.position;
            float best = float.MaxValue;
            for (int i = 0; i < SmokeRenderRegistry.Active.Count; i++)
            {
                Renderer r = SmokeRenderRegistry.Active[i];
                if (r == null) continue;
                float d = Mathf.Sqrt(r.bounds.SqrDistance(eye));
                if (d < best) best = d;
            }
            float want = best < 25f ? 0.70f : best < 70f ? 0.85f : 1f;
            if (Mathf.Abs(want - proxWanted) > 0.01f) { proxWanted = want; proxSince = Time.unscaledTime; }
            if (Mathf.Abs(proxWanted - proxScale) > 0.01f && Time.unscaledTime - proxSince > (proxWanted < proxScale ? 0.25f : 1.5f)) proxScale = proxWanted;
            return proxScale;
        }
        private Material compositeMaterial;

        // ---- temporal upscaling ----------------------------------------------------------
        private static readonly int AuxId = Shader.PropertyToID("_RaymarchedPlumeTrailsAux");
        private static readonly int DepthTmpId = Shader.PropertyToID("_RaymarchedPlumeTrailsDepth");

        private class TemporalState
        {
            public readonly RenderTexture[] hist = new RenderTexture[2];
            public int write;                 // which history this frame's resolve writes to
            public int width, height;
            public Matrix4x4 prevW2C, prevBodyL2W;
            public CelestialBody prevBody;
            public float prevFov, prevAspect;
            public float lastTime = -999f;
            public bool haveHistory;
            public bool armed;                // a temporal buffer was recorded for this frame
            public int frame;
        }
        private readonly Dictionary<Camera, TemporalState> temporal = new Dictionary<Camera, TemporalState>();
        private bool temporalLogged;

        private static float Halton(int index, int b)
        {
            float f = 1f, r = 0f;
            while (index > 0) { f /= b; r += f * (index % b); index /= b; }
            return r;
        }

        // Full resolution on purpose: the history is where the accumulated detail lives,
        // which is what makes this an upscale rather than a blur of the small frame.
        private TemporalState GetTemporal(Camera cam)
        {
            TemporalState st;
            if (!temporal.TryGetValue(cam, out st))
            {
                st = new TemporalState();
                temporal[cam] = st;
            }

            int fw = cam.pixelWidth, fh = cam.pixelHeight;
            if (st.hist[0] == null || st.width != fw || st.height != fh)
            {
                for (int k = 0; k < 2; k++)
                {
                    if (st.hist[k] != null) { st.hist[k].Release(); Destroy(st.hist[k]); }
                    RenderTexture rt = new RenderTexture(fw, fh, 0, RenderTextureFormat.ARGBHalf)
                    {
                        name = "PlumeHistory" + k,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        useMipMap = false
                    };
                    rt.Create();
                    st.hist[k] = rt;
                }
                st.width = fw;
                st.height = fh;
                st.haveHistory = false;
            }
            return st;
        }

        // Runs just before the camera renders, when its matrices are final. Reading them
        // from LateUpdate was a frame stale whenever the flight camera moved after us.
        private void OnCameraPreRender(Camera cam)
        {
            if (!ModSettings.Enabled) return;
            RecordSunMask(cam);
            TemporalState st;
            if (!temporal.TryGetValue(cam, out st) || !st.armed) return;
            st.armed = false;

            CelestialBody body = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody : null;
            Matrix4x4 bodyL2W = body != null && body.bodyTransform != null
                ? body.bodyTransform.localToWorldMatrix : Matrix4x4.identity;

            bool valid = st.haveHistory
                && st.prevBody == body
                && Time.unscaledTime - st.lastTime < 0.25f
                && Mathf.Abs(cam.fieldOfView - st.prevFov) < 0.01f
                && Mathf.Abs(cam.aspect - st.prevAspect) < 0.001f;

            // Where a point that is at rest relative to the BODY was, in last frame's view
            // space: back out of the current view, through the body's own motion since
            // then (rotation, floating-origin shifts and Krakensbane all show up here),
            // into the previous view. Smoke sits still in the air, and the air sits still
            // in the body frame, so this is exact for it.
            Matrix4x4 reproj = st.prevW2C * (st.prevBodyL2W * bodyL2W.inverse) * cam.cameraToWorldMatrix;
            Shader.SetGlobalMatrix("_PlumeReprojView", reproj);
            Shader.SetGlobalFloat("_PlumeHistoryValid", valid ? 1f : 0f);
            Shader.SetGlobalFloat("_PlumeFeedback", Mathf.Clamp(SmokeTuning.TemporalFeedback, 0f, 0.98f));

            int idx = st.frame % 8 + 1;
            Shader.SetGlobalVector("_PlumeJitter", new Vector4(
                Halton(idx, 2) - 0.5f, Halton(idx, 3) - 0.5f,
                (st.frame * 13.37f) % 256f, (st.frame * 7.11f) % 256f));
            st.frame++;

            st.prevW2C = cam.worldToCameraMatrix;
            st.prevBodyL2W = bodyL2W;
            st.prevBody = body;
            st.prevFov = cam.fieldOfView;
            st.prevAspect = cam.aspect;
            st.lastTime = Time.unscaledTime;
            st.haveHistory = true;
        }
        private Light sunLight;
        private float sunSearchTimer;

        // THE SHADOW IN THE SUN'S SHADOW MASK (deferred rendering, e.g. the Deferred mod): instead of darkening the finished picture (sky light, lamps
        // and glow too), the trail's shadow is multiplied into the screen-space shadow mask of the sun - the same place EVE puts its cloud shadows - so
        // the smoke only takes away the SUN, and the shadow lies correctly on the terrain, the tower, the rocket and the buildings. One buffer on the
        // light, re-recorded before every camera renders (each camera has its own depth and matrices; a camera that is not ours gets an empty buffer).
        private CommandBuffer maskCb;
        private Light maskLight;
        private static int lastMaskDraws;

        private bool MaskMode(Camera cam)
        {
            return SmokeTuning.ShadowIntoSunMask && cam != null && cam.actualRenderingPath == RenderingPath.DeferredShading
                && sunLight != null && sunLight.shadows != LightShadows.None && SmokeTuning.ShadowCastStrength > 0.001f;
        }

        private void RecordSunMask(Camera cam)
        {
            if (maskLight != sunLight)
            {
                if (maskLight != null && maskCb != null) maskLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskCb);
                maskLight = sunLight;
                if (maskLight != null)
                {
                    if (maskCb == null) maskCb = new CommandBuffer { name = "RaymarchedPlumeTrails shadow into the sun mask" };
                    maskLight.AddCommandBuffer(LightEvent.AfterScreenspaceMask, maskCb);
                }
            }
            if (maskCb == null) return;
            maskCb.Clear();
            if (!IsSceneCamera(cam) || !MaskMode(cam)) return;
            int drawn = 0;
            Matrix4x4 invP = cam.projectionMatrix.inverse;
            Matrix4x4 c2w = cam.cameraToWorldMatrix;
            // _ZBufferParams of this camera (reversed Z on D3D: x = -1 + far / near, y = 1)
            float n = cam.nearClipPlane, f = cam.farClipPlane;
            float zx = SystemInfo.usesReversedZBuffer ? -1f + f / n : 1f - f / n;
            float zy = SystemInfo.usesReversedZBuffer ? 1f : f / n;
            Vector4 zb = new Vector4(zx, zy, zx / f, zy / f);
            for (int i = 0; i < SmokeRenderRegistry.Active.Count; i++)
            {
                Renderer r = SmokeRenderRegistry.Active[i];
                if (r == null || r.sharedMaterial == null || r.sharedMaterial.passCount < 2) continue;
                MaterialPropertyBlock block;
                if (!SmokeRenderRegistry.Blocks.TryGetValue(r, out block) || block == null) continue;
                Vector4 castRect;
                if (!TryCastRect(cam, r.bounds, out castRect)) continue;
                block.SetVector("_CastRect", castRect);
                block.SetFloat("_ShadowCastStrength", SmokeTuning.ShadowCastStrength);
                block.SetFloat("_ShadowCastDensity", SmokeTuning.ShadowCastDensity);
                block.SetFloat("_ShadowCastFloor", 0f);         // only the sun is taken away: no floor needed, the sky light stays
                block.SetInt("_ShadowCastSteps", Mathf.Max(4, Mathf.RoundToInt(SmokeTuning.ShadowCastSteps * SmokeQuality.Current.shadowSteps / 16f)));
                block.SetFloat("_TrMaskMode", 1f);
                block.SetFloat("_TrMaskFlip", SmokeTuning.ShadowMaskFlip ? 1f : 0f);
                block.SetMatrix("_TrInvP", invP);
                block.SetMatrix("_TrC2W", c2w);
                block.SetVector("_TrZB", zb);
                maskCb.DrawMesh(fullscreenQuad, Matrix4x4.identity, r.sharedMaterial, 0, ShadowCastPass, block);
                block.SetFloat("_TrMaskMode", 0f);              // the copy is in the buffer; the overlay path (another camera) sees the old mode
                drawn++;
            }
            lastMaskDraws = drawn;
        }

        private bool inited, noShaderLogged;

        private void Start() { if (ModSettings.Enabled) TryInit(); }

        // made when the mod is on and the shaders are loaded (a mod that was off at the start loads nothing: this runs when it is switched on)
        private bool TryInit()
        {
            if (inited) return true;
            if (ShaderCache.SmokeCompositeShader == null)
            {
                if (!noShaderLogged) { noShaderLogged = true; Debug.LogError("[PlumeTrails] No composite shader - half-res rendering is off and smoke will not draw. Rebuild the AssetBundle."); }
                return false;
            }
            inited = true;
            compositeMaterial = new Material(ShaderCache.SmokeCompositeShader);
            Camera.onPreRender += OnCameraPreRender;

            scratchBlock = new MaterialPropertyBlock();
            // Clip-space quad. The cast pass writes its vertices straight out, so no
            // transform is involved and one mesh serves every camera.
            fullscreenQuad = new Mesh
            {
                name = "RaymarchedPlumeTrails fullscreen",
                vertices = new[]
                {
                    new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f),
                    new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            // The vertices are clip-space already, so a bounds check against them would
            // cull the quad the moment the camera moved.
            fullscreenQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e9f);
            return true;
        }

        private bool hooksReleased;

        // everything this renderer hung on the cameras and the sun: the command buffers, the history of the temporal resolve. Called when the mod is switched off
        // (the buffers are made again by themselves when it is switched on) and when the renderer goes.
        private void ReleaseCameraHooks()
        {
            foreach (KeyValuePair<Camera, TemporalState> pair in temporal)
            {
                for (int k = 0; k < 2; k++)
                {
                    if (pair.Value.hist[k] != null) { pair.Value.hist[k].Release(); Destroy(pair.Value.hist[k]); }
                }
            }
            temporal.Clear();
            foreach (KeyValuePair<Camera, CommandBuffer> pair in buffers)
            {
                if (pair.Key != null) { CameraEvent se; if (!bufferStage.TryGetValue(pair.Key, out se)) se = CameraEvent.AfterForwardAlpha; pair.Key.RemoveCommandBuffer(se, pair.Value); }
                pair.Value.Release();
            }
            buffers.Clear();

            foreach (KeyValuePair<Camera, CommandBuffer> pair in shadowBuffers)
            {
                if (pair.Key != null) pair.Key.RemoveCommandBuffer(ShadowStage, pair.Value);
                pair.Value.Release();
            }
            shadowBuffers.Clear();
            if (maskLight != null && maskCb != null) maskLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskCb);
            if (maskCb != null) maskCb.Release();
            maskCb = null; maskLight = null;

        }

        private void OnDestroy()
        {
            Camera.onPreRender -= OnCameraPreRender;
            ReleaseCameraHooks();

            if (fullscreenQuad != null) Destroy(fullscreenQuad);
            if (compositeMaterial != null) Destroy(compositeMaterial);
        }

        // rebuilt every frame: the set of live volumes changes constantly, and a command
        // buffer is a recording rather than a callback
        // CommandBuffer.DrawRenderer draws the pass by hand, bypassing the forward
        // renderer that would normally bind per-light data - so _WorldSpaceLightPos0 is
        // meaningless here no matter what the pass is tagged, and the light march would
        // walk off in an arbitrary direction. Publishing the direction ourselves makes
        // self-shadowing independent of how the geometry gets submitted.
        private static Vector3 toSunWorld = Vector3.up;

        // Where on screen the cast shadow can possibly land, as an NDC rectangle.
        //
        // The pass used to shade a full-screen quad, so every pixel of the frame paid for
        // a sun-ray march whether or not the plume could reach it. A ground point can only
        // be shadowed if the sun ray from it enters the plume within the cast reach, which
        // means it lies inside the volume's bounds swept AWAY from the sun by that reach.
        // That is a convex hull, so projecting its corners bounds it exactly.
        //
        // Returns false when the region is entirely off screen or behind the camera, and
        // the pass is skipped. If the hull straddles the camera plane it cannot be bounded
        // by projection, so the full screen is used - always correct, only slower.
        private static bool TryCastRect(Camera cam, Bounds b, out Vector4 rect)
        {
            rect = new Vector4(-1f, -1f, 1f, 1f);
            Vector3 shift = -toSunWorld.normalized * SmokeTuning.ShadowCastDistance;
            float minX = 1e9f, minY = 1e9f, maxX = -1e9f, maxY = -1e9f;
            int behind = 0;
            const int corners = 16;

            for (int i = 0; i < corners; i++)
            {
                Vector3 c = b.center + new Vector3(
                    (i & 1) != 0 ? b.extents.x : -b.extents.x,
                    (i & 2) != 0 ? b.extents.y : -b.extents.y,
                    (i & 4) != 0 ? b.extents.z : -b.extents.z);
                if ((i & 8) != 0) c += shift;

                Vector3 v = cam.WorldToViewportPoint(c);
                if (v.z <= 0.01f) { behind++; continue; }
                if (v.x < minX) minX = v.x;
                if (v.y < minY) minY = v.y;
                if (v.x > maxX) maxX = v.x;
                if (v.y > maxY) maxY = v.y;
            }

            if (behind == corners) return false;
            if (behind > 0) return true;

            const float margin = 0.03f;
            minX -= margin; minY -= margin; maxX += margin; maxY += margin;
            if (maxX < 0f || minX > 1f || maxY < 0f || minY > 1f) return false;

            // viewport rectangle -> clip rectangle, plus its vertical mirror image (see HeatHazeRenderer.ScreenRect)
            float cy0 = Mathf.Clamp01(minY) * 2f - 1f, cy1 = Mathf.Clamp01(maxY) * 2f - 1f;
            rect = new Vector4(
                Mathf.Clamp01(minX) * 2f - 1f, Mathf.Min(cy0, -cy1),
                Mathf.Clamp01(maxX) * 2f - 1f, Mathf.Max(cy1, -cy0));
            return true;
        }

        // KSP's own sun light (cheap: one component lookup). Scanning every light of the scene (FindObjectsOfType) was what stalled the game every 5 s: a KSP scene
        // holds thousands of lights (the lamps of the space centre, parts, mods) and RenderSettings.sun is not set.
        private static Light CheapSun()
        {
            try { if (Sun.Instance != null) { Light l = Sun.Instance.GetComponent<Light>(); if (l != null) return l; } } catch { }
            return RenderSettings.sun;
        }

        private float nextFullLightScan;

        private void UpdateSunDirection()
        {
            sunSearchTimer -= Time.deltaTime;
            bool lost = sunLight == null || !sunLight.isActiveAndEnabled;
            if (lost || sunSearchTimer <= 0f)
            {
                sunSearchTimer = lost ? 1f : 5f;
                Light cand = CheapSun();
                if (cand != null) sunLight = cand;
                else if (lost && Time.unscaledTime >= nextFullLightScan)
                {
                    // nothing found the sun: the expensive scan of every light in the scene, at most every 30 s
                    nextFullLightScan = Time.unscaledTime + 30f;
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
                toSunWorld = toSun;
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

            // Body centre and radius, so the shader can work out whether the planet is
            // between a given sample and the sun. Without it the smoke is lit at night.
            Vector4 centre = Vector4.zero;
            if (FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.mainBody != null)
            {
                CelestialBody b = FlightGlobals.ActiveVessel.mainBody;
                Vector3 c = b.position;
                centre = new Vector4(c.x, c.y, c.z, (float)b.Radius);
            }
            Shader.SetGlobalVector("_SmokeBodyCentre", centre);
            float seaClip = 0f;
            if (FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.mainBody != null
                && FlightGlobals.ActiveVessel.mainBody.ocean
                // Only over open water. At a coastal pad the ground sits within a few metres of sea
                // level, and cutting at the sea-level sphere there slices the base off the smoke.
                && (FlightGlobals.ActiveVessel.Splashed || FlightGlobals.ActiveVessel.terrainAltitude < -1.0))
                seaClip = (float)FlightGlobals.ActiveVessel.mainBody.Radius;
            Shader.SetGlobalFloat("_SmokeSeaClip", seaClip);
        }

        // Same pair DepthTextureEnabler uses; keep the two lists in step.
        private static readonly string[] SceneCameraNames = { "Camera 00", "Camera 01" };

        private static bool IsSceneCamera(Camera cam)
        {
            for (int i = 0; i < SceneCameraNames.Length; i++)
            {
                if (cam.name == SceneCameraNames[i]) return true;
            }
            return false;
        }

        // Records the cast-shadow pass: one screen-covering multiply per live volume,
        // each carrying that volume's own spine data.
        //
        // Separate from the main buffer because it runs at a different stage and writes to
        // the camera target, not to the smoke's offscreen buffer. Cleared and rebuilt every
        // frame for the same reason the main one is - the set of volumes keeps changing.
        private void BuildShadowCast(Camera cam)
        {
            CommandBuffer cb;
            if (!shadowBuffers.TryGetValue(cam, out cb))
            {
                cb = new CommandBuffer { name = "RaymarchedPlumeTrails cast shadow" };
                cam.AddCommandBuffer(ShadowStage, cb);
                shadowBuffers[cam] = cb;
            }

            cb.Clear();
            if (SmokeTuning.ShadowCastStrength <= 0.001f) return;
            if (MaskMode(cam)) return;          // deferred: the shadow goes into the sun's shadow mask (RecordSunMask), not over the picture
            int drawn = 0;

            for (int i = 0; i < SmokeRenderRegistry.Active.Count; i++)
            {
                Renderer r = SmokeRenderRegistry.Active[i];
                if (r == null || r.sharedMaterial == null) continue;
                if (r.sharedMaterial.passCount < 2) continue;      // the plume / pad cloud shaders have no cast-shadow pass

                // The spine arrays live in the renderer's property block, so the pass has
                // to be handed the same block rather than relying on material state.
                MaterialPropertyBlock block;
                if (!SmokeRenderRegistry.Blocks.TryGetValue(r, out block) || block == null) continue;

                // Same two as the volume pass: this pass marches the same density
                // field, and the softness value divides a radius in there.
                block.SetFloat("_ThinEdgeSoftness", SmokeTuning.ThinEdgeSoftness);
                block.SetFloat("_ThinDetailFade", SmokeTuning.ThinDetailFade);
                block.SetFloat("_ShadowCastStrength", SmokeTuning.ShadowCastStrength);
                block.SetFloat("_ShadowCastDistance", SmokeTuning.ShadowCastDistance);
                block.SetFloat("_ShadowCastDensity", SmokeTuning.ShadowCastDensity);
                block.SetFloat("_ShadowCastFloor", SmokeTuning.ShadowCastFloor);
                // The slider is the High-quality value; the preset and the governor scale it.
                int castSteps = Mathf.Max(4, Mathf.RoundToInt(
                    SmokeTuning.ShadowCastSteps * SmokeQuality.Current.shadowSteps / 16f));
                block.SetInt("_ShadowCastSteps", castSteps);

                Vector4 castRect;
                if (!TryCastRect(cam, r.bounds, out castRect)) continue;
                block.SetVector("_CastRect", castRect);

                cb.DrawMesh(fullscreenQuad, Matrix4x4.identity, r.sharedMaterial, 0,
                            ShadowCastPass, block);
                drawn++;
            }

            lastShadowDraws = drawn;
        }

        private static int lastShadowDraws;

        // index of the "SmokeShadowCast" pass in SmokeVolume.shader
        private const int ShadowCastPass = 1;

        private float renderPerfLogTimer;
        private float lastResLogTime = -999f;

        private void LateUpdate()
        {
            // the mod is off: nothing is recorded, nothing hangs on the cameras, nothing is looked up
            if (!ModSettings.Enabled)
            {
                if (!hooksReleased) { ReleaseCameraHooks(); hooksReleased = true; }
                return;
            }
            hooksReleased = false;
            if (!inited && !TryInit()) return;
            var perfSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LateUpdateBody();
            }
            finally
            {
                renderPerfLogTimer -= Time.unscaledDeltaTime;
                if (renderPerfLogTimer <= 0f)
                {
                    renderPerfLogTimer = 1f;
                    double ms = perfSw.Elapsed.TotalMilliseconds;
                    if (ms >= 0.5 && SmokeTuning.DebugLogging)
                    {
                        Debug.Log(string.Format(
                            "[PlumeTrails] render: cpuMs={0:F2} activeVolumes={1} shadowDraws={2} sunMaskDraws={3} (path {4}, sun shadows {5})",
                            ms, SmokeRenderRegistry.Active.Count, lastShadowDraws, lastMaskDraws,
                            Camera.main != null ? Camera.main.actualRenderingPath.ToString() : "-", sunLight != null ? sunLight.shadows.ToString() : "no sun"));
                    }
                }
            }
        }

        // 1 / the distance at which 63% of the contrast is lost, at the camera's own air density (so the haze thins out
        // with altitude and is absent in vacuum). 0 when switched off.
        private float AerialK()
        {
            if (SmokeTuning.AerialStrength <= 0.001f) return 0f;
            Camera cam = Camera.main;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (cam == null || body == null || !body.atmosphere) return 0f;
            double alt = body.GetAltitude(cam.transform.position);
            double p0 = body.GetPressure(0.0);
            if (p0 <= 0.0) return 0f;
            float density = (float)(body.GetPressure(alt) / p0);
            if (density < 0.005f) return 0f;
            return 1f / (Mathf.Max(SmokeTuning.AerialDistance, 500f) * Mathf.Max(density, 0.1f));
        }

        private void LateUpdateBody()
        {
            if (compositeMaterial == null) return;
            Shader.SetGlobalFloat("_PlumeOpacity", Mathf.Clamp01(SmokeTuning.Opacity));
            Shader.SetGlobalFloat("_SmokeAerialK", AerialK());
            // Volumes are uploaded here, after every group has ticked, so trails that have
            // grown together can be drawn as one.
            SmokeVolumeGroup.RenderAll();
            PlumeExplosions.RenderAll();
            SmokeQuality.Tick(SmokeRenderRegistry.Active.Count > 0);
            UpdateSunDirection();

            Camera cam = Camera.main;
            if (cam == null) return;

            CommandBuffer cb;
            if (!buffers.TryGetValue(cam, out cb))
            {
                cb = new CommandBuffer { name = "RaymarchedPlumeTrails half-res smoke" };
                cam.AddCommandBuffer(Stage, cb);
                buffers[cam] = cb;
                bufferStage[cam] = Stage;
            }
            else
            {
                CameraEvent have;
                if (bufferStage.TryGetValue(cam, out have) && have != Stage)
                {
                    // the switch changed: move the buffer to the other stage
                    cam.RemoveCommandBuffer(have, cb);
                    cam.AddCommandBuffer(Stage, cb);
                    bufferStage[cam] = Stage;
                    Debug.Log("[PlumeTrails] smoke is now composited " + (SmokeTuning.SmokeLast ? "at the very END (before the image effects: after every cloud layer)" : SmokeTuning.SmokeBeforeClouds ? "BEFORE the transparent queue (cloud layers are drawn over it)" : "AFTER the transparent queue (smoke over the cloud layers)"));
                }
            }

            cb.Clear();

            // Every scene camera, not just Camera.main. KSP splits the local scene across
            // "Camera 00" (near) and "Camera 01" (far), and the TERRAIN is drawn by the far
            // one - so a shadow pass attached only to Camera.main multiplied a target that
            // never had any ground in it. That is why nothing showed on the ground while
            // the smoke itself rendered fine.
            Camera[] all = Camera.allCameras;
            int matched = 0;
            for (int c = 0; c < all.Length; c++)
            {
                if (all[c] != null && IsSceneCamera(all[c])) { BuildShadowCast(all[c]); matched++; }
            }

            // One-shot diagnostic. The cast shadow has now failed twice for reasons that
            // were invisible from the outside, and each guess cost a build. This prints the
            // three things that can independently make it draw nothing: which cameras exist
            // and which matched, whether any volume was submitted, and whether the pass is
            // switched on at all.
            if (!shadowDiagLogged && SmokeRenderRegistry.Active.Count > 0)
            {
                shadowDiagLogged = true;
                string names = "";
                for (int c = 0; c < all.Length; c++)
                {
                    if (all[c] != null) names += all[c].name + (IsSceneCamera(all[c]) ? "[MATCH] " : " ");
                }
                Debug.Log(string.Format(
                    "[PlumeTrails] castshadow: cameras={0} matched={1} volumes={2} blocks={3} draws={4} strength={5:F2} | all: {6}",
                    all.Length, matched, SmokeRenderRegistry.Active.Count,
                    SmokeRenderRegistry.Blocks.Count, lastShadowDraws,
                    SmokeTuning.ShadowCastStrength, names));
            }

            if (SmokeRenderRegistry.Active.Count == 0) return;

            // Resolution follows the quality profile. At 1.0 the buffer is screen-sized and the
            // composite blit is a pass-through that cannot resample anything - which is why
            // High and Ultra are free of the banded stripes a fixed half-size buffer gave
            // (a half-width buffer resolves the silhouette at every other pixel and the
            // bilinear upscale smears that into stairs). Below 1.0 the composite shader's
            // edge-aware upsample takes over, and the governor only gets there when the
            // frame rate says it has to.
            float renderScale = SmokeQuality.Current.renderScale;
            // close to the smoke it fills the screen and the cost is the number of pixels: the buffer is made smaller there (it is upscaled with
            // the temporal accumulation anyway). Three steps, and a step is only taken after it has been wanted for half a second, so that the
            // buffers are not reallocated back and forth.
            if (SmokeTuning.TemporalUpscale && renderScale < 0.999f) renderScale = Mathf.Max(0.30f, renderScale * ProximityScale(cam));
            int w = Mathf.Max(1, Mathf.RoundToInt(cam.pixelWidth * renderScale));
            int h = Mathf.Max(1, Mathf.RoundToInt(cam.pixelHeight * renderScale));

            // ARGBHalf, not ARGB32: the buffer holds premultiplied colour that gets
            // composited later, and 8 bits per channel bands visibly on smoke gradients
            bool temporalOn = SmokeTuning.TemporalUpscale && renderScale < 0.999f
                && SystemInfo.supportedRenderTargetCount >= 2;
            TemporalState tst = temporalOn ? GetTemporal(cam) : null;

            cb.GetTemporaryRT(HalfResId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            if (temporalOn)
            {
                // Second target: distance to the smoke, for reprojection. A depth buffer is
                // bound only because a multiple-render-target set-up requires one; the
                // volume pass tests depth by hand and never uses it.
                cb.GetTemporaryRT(AuxId, w, h, 0, FilterMode.Point, RenderTextureFormat.ARGBFloat);
                cb.GetTemporaryRT(DepthTmpId, w, h, 24, FilterMode.Point, RenderTextureFormat.Depth);
                cb.SetRenderTarget(new RenderTargetIdentifier[] { HalfResId, AuxId }, DepthTmpId);
            }
            else
            {
                cb.SetRenderTarget(HalfResId);
                // no jitter or dither offset when nothing is accumulating them
                Shader.SetGlobalVector("_PlumeJitter", Vector4.zero);
                foreach (KeyValuePair<Camera, TemporalState> pair in temporal) pair.Value.haveHistory = false;
            }
            cb.ClearRenderTarget(false, true, Color.clear);

            // BACK TO FRONT. Each volume raymarches itself correctly, but SEPARATE volumes
            // are composited with an over-blend, and that operation is not commutative -
            // whichever is drawn last ends up in front. Registry order is creation order,
            // so a booster trail behind the core could be laid down afterwards and show
            // through it. Sorting by distance is what makes a near trail actually hide what
            // is behind it.
            //
            // Approximate on purpose: it sorts by volume centre, so two volumes that
            // genuinely interpenetrate still cannot be resolved this way. The real answer
            // for those is one shared volume, which is the per-engine sub-chain plan.
            sortedVolumes.Clear();
            for (int i = 0; i < SmokeRenderRegistry.Active.Count; i++)
            {
                if (SmokeRenderRegistry.Active[i] != null) sortedVolumes.Add(SmokeRenderRegistry.Active[i]);
            }
            Vector3 eye = cam.transform.position;
            sortedVolumes.Sort((a, b) =>
            {
                int pa, pb;
                SmokeRenderRegistry.Priority.TryGetValue(a, out pa);
                SmokeRenderRegistry.Priority.TryGetValue(b, out pb);
                if (pa != pb) return pa.CompareTo(pb);
                return (b.bounds.center - eye).sqrMagnitude.CompareTo((a.bounds.center - eye).sqrMagnitude);
            });

            // "Laguje mimo ze rakieta jest 4km od dymu" - this pass costs PIXELS, not
            // vessel-to-camera distance, so the number that actually predicts its cost is
            // how much of the SCREEN the volumes' boxes cover, not how far away they are.
            // A trail that is far from the vessel but close to (or behind) the camera, or
            // one whose box is huge from a bloomed/stretched-geometry bug, fills the
            // screen just as expensively as a near one - logging both numbers together is
            // what tells those two apart instead of guessing from "it's far, why is it
            // slow".
            if (Time.time - lastResLogTime > 1f && sortedVolumes.Count > 0)
            {
                lastResLogTime = Time.time;
                float nearestDist = float.MaxValue;
                for (int i = 0; i < sortedVolumes.Count; i++)
                {
                    float d = Vector3.Distance(eye, sortedVolumes[i].bounds.center);
                    if (d < nearestDist) nearestDist = d;
                }
                Debug.Log(string.Format(
                    "[PlumeTrails] renderRes: {0}x{1} (scale {2:F2}) temporal={3} volumes={4} "
                    + "nearestDist={5:F0}m camPixels={6}x{7}",
                    w, h, renderScale, temporalOn, sortedVolumes.Count, nearestDist,
                    cam.pixelWidth, cam.pixelHeight));
            }

            for (int i = 0; i < sortedVolumes.Count; i++)
            {
                Renderer r = sortedVolumes[i];
                // DrawRenderer picks up the renderer's MaterialPropertyBlock, which is
                // where all the per-volume data (spine points, box, LOD) lives.
                // Pass 0 explicitly: without a pass index this draws EVERY pass in the
                // shader, which since the shadow-cast pass was added would also blend a
                // multiply of it over the smoke buffer.
                cb.DrawRenderer(r, r.sharedMaterial, 0, 0);
            }

            if (temporalOn)
            {
                int read = 1 - tst.write;
                cb.SetGlobalTexture("_PlumeAux", AuxId);
                cb.SetGlobalTexture("_PlumeHistory", tst.hist[read]);
                // resolve into this frame's history, then lay that over the scene
                cb.Blit(HalfResId, tst.hist[tst.write], compositeMaterial, 1);
                cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                cb.Blit(tst.hist[tst.write], BuiltinRenderTextureType.CameraTarget, compositeMaterial, 0);
                cb.ReleaseTemporaryRT(AuxId);
                cb.ReleaseTemporaryRT(DepthTmpId);

                tst.write = read;   // next frame writes the other one and reads this one
                tst.armed = true;
                if (!temporalLogged)
                {
                    temporalLogged = true;
                    Debug.Log(string.Format(
                        "[PlumeTrails] temporal upscaling on: smoke {0}x{1} -> {2}x{3}, feedback {4:F2}",
                        w, h, cam.pixelWidth, cam.pixelHeight, SmokeTuning.TemporalFeedback));
                }
            }
            else
            {
                cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                // Pass 0 explicitly (27 IX fix): Blit with no pass argument defaults to -1,
                // which runs every pass in the shader in sequence into the same target.
                // SmokeComposite has a second pass (temporal reprojection, Blend Off) meant
                // to write into a history buffer, not the screen - left implicit, it ran
                // straight after pass 0 and overwrote the whole framebuffer with the raw
                // smoke sample instead of blending it, which is the full-screen blackout
                // reported with temporal off. The temporal-on branch above never hit this
                // because it already targets each pass explicitly.
                cb.Blit(HalfResId, BuiltinRenderTextureType.CameraTarget, compositeMaterial, 0);
            }
            cb.ReleaseTemporaryRT(HalfResId);
        }
    }
}
