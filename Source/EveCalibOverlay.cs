using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace RaymarchedPlumeTrails
{
    // CALIBRATION of the cloud shadow (Advanced -> Trail): lays red over the screen where the trail's shader thinks EVE's clouds shade the GROUND
    // (pass 2 of TrailCapsules: the view ray against the planet sphere, then the same light-volume lookup the trail uses). With the camera under the
    // clouds looking down, the red patches must sit on the real cloud shadows; the "mapping variant" slider tries the 16 arrangements of the axes.
#if !EXPLOSIONS_ONLY
    [KSPAddon(KSPAddon.Startup.Flight, false)]
#endif
    public class EveCalibOverlay : MonoBehaviour
    {
        private Material mat;
        private Mesh quad;
        private CommandBuffer cb;
        private Camera cbCam;

        // DUMP for the developer (button under the calibration toggle): a clean screenshot (no overlay, no window), EVE's light volume (raw half floats)
        // and every matrix / position needed to map it, into PluginData/eve_dump. Frames: 4..3 hide everything, 2 shot + values, 1 the readback.
        public static int DumpFrames = 0;
        public static string DumpStatus = "";
        private static string DumpDir { get { return Path.Combine(Path.GetDirectoryName(AssetLoader.AssemblyDirectory()), "PluginData/eve_dump"); } }

        private void DumpStep(Camera cam)
        {
            DumpFrames--;
            try
            {
                Directory.CreateDirectory(DumpDir);
                if (DumpFrames == 2)
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(DumpDir, "shot.png"));
                    File.WriteAllText(Path.Combine(DumpDir, "info.txt"), Info(cam));
                }
                else if (DumpFrames == 1)
                {
                    Texture gt = Shader.GetGlobalTexture("directLightVolume");
                    RenderTexture rt = gt as RenderTexture;
                    if (rt == null) { DumpStatus = "dump: no light volume (" + (gt == null ? "null" : gt.GetType().Name) + ")"; return; }
                    int w = rt.width, h = rt.height, d = rt.dimension == TextureDimension.Tex3D ? rt.volumeDepth : 1;
                    string file = Path.Combine(DumpDir, "volume_" + w + "x" + h + "x" + d + "_" + rt.format + ".raw");
                    DumpStatus = "dump: reading " + w + "x" + h + "x" + d + " " + rt.format + "...";
                    AsyncGPUReadback.Request(rt, 0, 0, w, 0, h, 0, d, req =>
                    {
                        try
                        {
                            if (req.hasError) { DumpStatus = "dump: readback error"; return; }
                            using (var fs = new FileStream(file, FileMode.Create))
                            {
                                for (int l = 0; l < req.layerCount; l++)
                                {
                                    byte[] b = req.GetData<byte>(l).ToArray();
                                    fs.Write(b, 0, b.Length);
                                }
                            }
                            DumpStatus = "dump: done (" + req.layerCount + " layers) -> " + DumpDir;
                        }
                        catch (Exception e) { DumpStatus = "dump: " + e.Message; }
                    });
                }
            }
            catch (Exception e) { DumpStatus = "dump: " + e.Message; Debug.LogException(e); }
        }

        private static string V(Vector4 v) { return v.x.ToString("R") + " " + v.y.ToString("R") + " " + v.z.ToString("R") + " " + v.w.ToString("R"); }
        private static string V(Vector3 v) { return v.x.ToString("R") + " " + v.y.ToString("R") + " " + v.z.ToString("R"); }
        private static string M(Matrix4x4 m) { return V(m.GetRow(0)) + " | " + V(m.GetRow(1)) + " | " + V(m.GetRow(2)) + " | " + V(m.GetRow(3)); }

        private static string Info(Camera cam)
        {
            var sb = new StringBuilder();
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody b = v.mainBody;
            sb.AppendLine("body " + b.bodyName + " radius " + b.Radius.ToString("R"));
            sb.AppendLine("planetPos " + V((Vector3)b.position));
            sb.AppendLine("vesselPos " + V((Vector3)v.GetWorldPos3D()) + " alt " + v.altitude.ToString("R") + " terrainAlt " + v.terrainAltitude.ToString("R") + " lat " + v.latitude.ToString("R") + " lon " + v.longitude.ToString("R"));
            Vector3 sun = (Vector3)(Planetarium.fetch.Sun.position - b.position);
            sb.AppendLine("sunDirFromPlanet " + V(sun.normalized));
            sb.AppendLine("smokeSunDir " + V(Shader.GetGlobalVector("_SmokeSunDir")));
            foreach (Camera c in Camera.allCameras)
                sb.AppendLine("camera '" + c.name + "' depth " + c.depth + " main " + (c == cam) + " pos " + V(c.transform.position) + " fwd " + V(c.transform.forward)
                    + " px " + c.pixelWidth + "x" + c.pixelHeight + " fov " + c.fieldOfView + " near " + c.nearClipPlane + " far " + c.farClipPlane
                    + "\n   worldToCamera " + M(c.worldToCameraMatrix) + "\n   projection " + M(c.projectionMatrix));
            sb.AppendLine("screen " + Screen.width + "x" + Screen.height);
            sb.AppendLine("worldToParaboloid " + M(Shader.GetGlobalMatrix("worldToParaboloid")));
            sb.AppendLine("paraboloidToWorld " + M(Shader.GetGlobalMatrix("paraboloidToWorld")));
            sb.AppendLine("paraboloidPosition " + V(Shader.GetGlobalVector("paraboloidPosition")));
            sb.AppendLine("innerLightVolumeRadius " + Shader.GetGlobalFloat("innerLightVolumeRadius").ToString("R"));
            sb.AppendLine("outerLightVolumeRadius " + Shader.GetGlobalFloat("outerLightVolumeRadius").ToString("R"));
            sb.AppendLine("lightVolumeZClamp " + V(Shader.GetGlobalVector("lightVolumeZClamp")));
            sb.AppendLine("ambientVolumeZClamp " + V(Shader.GetGlobalVector("ambientVolumeZClamp")));
            sb.AppendLine("lightVolumeDimensions " + V(Shader.GetGlobalVector("lightVolumeDimensions")));
            sb.AppendLine("combinedLightVolumeDimensions " + V(Shader.GetGlobalVector("combinedLightVolumeDimensions")));
            Texture t = Shader.GetGlobalTexture("directLightVolume");
            sb.AppendLine("directLightVolume " + (t == null ? "null" : t.name + " " + t.GetType().Name + " " + t.width + "x" + t.height + " dim " + t.dimension + (t is RenderTexture ? " depth " + ((RenderTexture)t).volumeDepth + " fmt " + ((RenderTexture)t).format : "")));
            sb.AppendLine("lightVolume same " + (Shader.GetGlobalTexture("lightVolume") == t));
            sb.AppendLine("variant " + SmokeTuning.NewTrailEveMapVariant);
            return sb.ToString();
        }

        private void LateUpdate()
        {
            if (!ModSettings.Enabled) { Remove(); return; }
            if (DumpFrames > 0)
            {
                Remove();
                Camera dc = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
                if (FlightGlobals.ActiveVessel != null) DumpStep(dc); else DumpFrames = 0;
                return;
            }
            bool on = SmokeTuning.EveCalibOverlay && CapsuleTrail.TrailShader != null && FlightGlobals.ActiveVessel != null;
            Camera cam = Camera.main;
            if (!on || cam == null) { Remove(); return; }
            if (mat == null) mat = new Material(CapsuleTrail.TrailShader);
            if (quad == null)
            {
                quad = new Mesh();
                quad.vertices = new Vector3[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
                quad.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
                quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            }
            if (cb == null || cbCam != cam)
            {
                Remove();
                cb = new CommandBuffer { name = "PlumeTrails EVE cloud shadow calibration" };
                cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
                cbCam = cam;
                cb.DrawMesh(quad, Matrix4x4.identity, mat, 0, 2);
            }
            Vessel v = FlightGlobals.ActiveVessel;
            Vector3 pc = v.mainBody.position;
            mat.SetVector("_TrEve", new Vector4(1f, pc.x, pc.y, pc.z));
            int mv = SmokeTuning.NewTrailEveMapVariant;
            mat.SetVector("_TrEveMap", new Vector4(mv & 1, (mv >> 1) & 1, (mv >> 2) & 1, (mv >> 3) & 1));
            mat.SetVector("_TrEveCalib", new Vector4((float)(v.mainBody.Radius + System.Math.Max(0.0, v.terrainAltitude)), 1f, 0f, 0f));
        }

        private void Remove()
        {
            if (cb != null && cbCam != null) cbCam.RemoveCommandBuffer(CameraEvent.AfterEverything, cb);
            cb = null; cbCam = null;
        }

        private void OnDestroy() { Remove(); }
    }
}
