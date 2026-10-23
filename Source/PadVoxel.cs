using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RaymarchedPlumeTrails
{
    // THE GEOMETRY OF A LAUNCH PAD, in 3D. A height map holds one surface per column, so the flame trench under a launch mount (a cavity with
    // a roof) is not in it. This class peels the scenery in layers from above (PadLayer.shader: the topmost surface, the next one below it, ...,
    // each with its facing: entering a solid or leaving it), builds a solid / air grid from the layers, and from it a SIGNED DISTANCE FIELD
    // (metres to the nearest surface, negative inside the solids). The field gives smooth normals and lets a ray be traced through the trench,
    // under the roof of the mount, along the walls and out of the ends (Trace), with nothing assumed about the shape of the pad.
    // Pure Unity code (no game types): used by the mod (PadFluid) and by the offline test in the Unity project.
    public class PadVoxel
    {
        public int nx, ny, nz;
        public float cell, oy;
        public byte[] solid;          // 1 = solid, index (y * nz + z) * nx + x
        public float[] sdf;           // signed distance to the nearest surface, m (+ in the air, - inside)
        public bool ready;
        public int layersUsed, cavityCells;
        public string report = "";
        public string layerDebug = ""; public bool facingInverted;

        public const float SheetThickness = 1.5f;     // a surface without a back face (a single sheet) is a solid this thick

        // ------------------------------------------------------------------ the capture
        // rends: the scenery to peel (not vessels). The grid frame: corner (world), e1 / up / e2, nx x ny x nz cells of `cell` m, the bottom at local
        // y = oy. heightTop: the local y the camera looks from. fallbackTop: per column (nx * nz) the local y of a surface found by rays, or null:
        // used where the scenery has nothing drawn (bare terrain). Synchronous: draws and reads back the layers in this frame.
        public static PadVoxel Build(Shader peel, IList<Renderer> rends, Vector3 corner, Vector3 e1, Vector3 up, Vector3 e2,
                                     int nx, int ny, int nz, float cell, float oy, float heightTop, float[] fallbackTop, int maxLayers = 10)
        {
            PadVoxel v = new PadVoxel { nx = nx, ny = ny, nz = nz, cell = cell, oy = oy };
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            if (peel == null || rends == null || rends.Count == 0) { v.report = "no scenery to peel"; return v; }

            GameObject cg = new GameObject("PlumeTrails.PadLayerCam");
            Camera cam = cg.AddComponent<Camera>();
            cam.enabled = false; cam.orthographic = true; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.allowHDR = false; cam.allowMSAA = false; cam.nearClipPlane = 0.05f; cam.cullingMask = 0;
            Vector3 centre = corner + e1 * (nx * cell * 0.5f) + e2 * (nz * cell * 0.5f);
            cam.transform.position = centre + up * heightTop;
            cam.transform.rotation = Quaternion.LookRotation(-up, e2);
            cam.orthographicSize = nz * cell * 0.5f;
            cam.aspect = (float)nx / nz;
            cam.farClipPlane = heightTop - oy + 5f;

            Material mat = new Material(peel);
            mat.SetVector("_PadHBase", new Vector4(corner.x, corner.y, corner.z, oy));
            mat.SetVector("_PadHUp", up); mat.SetVector("_PadHE1", e1); mat.SetVector("_PadHE2", e2);
            RenderTexture[] rt = { MakeRT(nx, nz), MakeRT(nx, nz) };
            Texture2D rd = new Texture2D(nx, nz, TextureFormat.RGBAFloat, false);
            CommandBuffer cb = new CommandBuffer { name = "PadVoxel layers" };
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);

            // layers[l][column] = (height, front)
            List<Color[]> layers = new List<Color[]>();
            for (int l = 0; l < maxLayers; l++)
            {
                RenderTexture cur = rt[l & 1], prev = rt[(l + 1) & 1];
                mat.SetVector("_PadHExt", new Vector4(nx * cell, nz * cell, l == 0 ? 1f : 0f, 0f));
                mat.SetTexture("_PadPrev", prev);
                cb.Clear();
                cb.SetRenderTarget(cur);
                cb.ClearRenderTarget(true, true, Color.black);
                cb.SetViewProjectionMatrices(cam.worldToCameraMatrix, vp);
                for (int i = 0; i < rends.Count; i++)
                {
                    Renderer r = rends[i];
                    if (r == null) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    int subs = mf != null && mf.sharedMesh != null ? mf.sharedMesh.subMeshCount : 1;
                    for (int sm = 0; sm < subs; sm++) cb.DrawRenderer(r, mat, sm, 0);
                }
                Graphics.ExecuteCommandBuffer(cb);
                RenderTexture keep = RenderTexture.active;
                RenderTexture.active = cur;
                rd.ReadPixels(new Rect(0, 0, nx, nz), 0, 0);
                rd.Apply(false);
                RenderTexture.active = keep;
                Color[] px = rd.GetPixels();
                int any = 0;
                for (int i = 0; i < px.Length; i++) if (px[i].r > 0f) any++;
                if (any == 0) break;
                layers.Add(px);
                {
                    float mn = 1e9f, mx = -1e9f; for (int q = 0; q < px.Length; q++) if (px[q].r > 0f) { float hq = Mathf.Floor(px[q].r * 0.5f) / 16f; mn = Mathf.Min(mn, hq); mx = Mathf.Max(mx, hq); }
                    System.Text.StringBuilder row = new System.Text.StringBuilder();
                    for (int q = 0; q < nx; q += 6) { Color c0 = px[(nz / 2) * nx + q]; float hq0 = Mathf.Floor(c0.r * 0.5f) / 16f; row.Append(c0.r > 0f ? (hq0 + oy).ToString("F0") + (c0.r - 2f * Mathf.Floor(c0.r * 0.5f) > 0.5f ? "f" : "b") : "-").Append(' '); }
                    v.layerDebug += "layer " + l + ": " + any + " px, h " + (mn + oy).ToString("F1") + " .. " + (mx + oy).ToString("F1") + ", row z=mid: " + row + (char)10;
                }
            }
            cb.Release();
            foreach (RenderTexture t in rt) { t.Release(); UnityEngine.Object.DestroyImmediate(t); }
            UnityEngine.Object.DestroyImmediate(rd); UnityEngine.Object.DestroyImmediate(mat); UnityEngine.Object.DestroyImmediate(cg);
            // The facing flag (VFACE) is inverted when the projection is flipped for a render texture, and that depends on the API. The first layer is
            // the outside of the solids seen from above: its faces are FRONT ones, so if most of them came out as back faces the flag is inverted.
            bool invert = false;
            if (layers.Count > 0)
            {
                int fr = 0, tot = 0;
                foreach (Color c0 in layers[0]) if (c0.r > 0f) { tot++; if (c0.r - 2f * Mathf.Floor(c0.r * 0.5f) > 0.5f) fr++; }
                invert = fr * 2 < tot;
            }
            v.facingInverted = invert;
            v.layersUsed = layers.Count;
            double tCapture = sw.Elapsed.TotalMilliseconds;

            v.Rasterize(layers, fallbackTop);
            double tRaster = sw.Elapsed.TotalMilliseconds;
            v.BuildSdf();
            v.ready = true;
            Debug.Log("[PadVoxel] " + v.layerDebug);
            v.report = "layers " + layers.Count + (v.facingInverted ? " (facing flag inverted)" : "") + ", capture " + tCapture.ToString("F0") + " ms, raster " + (tRaster - tCapture).ToString("F0") + " ms, sdf " + (sw.Elapsed.TotalMilliseconds - tRaster).ToString("F0") + " ms, cavity cells " + v.cavityCells;
            return v;
        }


        // ------------------------------------------------------------------ the geometry from the TRIANGLES of the meshes
        // The depth layers see a surface only from above: a vertical wall (the side of a flame trench) has no area seen from there, so it was not in the grid and the
        // flow had nothing to run along. Here every triangle of every readable mesh marks the cells it passes through (an exact triangle / box test), vertical
        // walls included. A cell is then SOLID where a surface passes through it, or where it is enclosed (not reachable from outside the grid through air): the
        // inside of a building. The trench, open at its ends, stays air. Meshes that cannot be read (isReadable false) are counted and left out; the caller
        // decides whether what was read is enough.
        public static PadVoxel BuildFromMeshes(IList<Renderer> rends, Vector3 corner, Vector3 e1, Vector3 up, Vector3 e2,
                                               int nx, int ny, int nz, float cell, float oy, out int meshesRead, out int meshesSkipped, out long trianglesUsed)
        {
            PadVoxel v = new PadVoxel { nx = nx, ny = ny, nz = nz, cell = cell, oy = oy };
            meshesRead = 0; meshesSkipped = 0; trianglesUsed = 0;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            v.solid = new byte[nx * ny * nz];
            float inv = 1f / cell;
            List<Vector3> verts = new List<Vector3>(4096);
            List<int> tris = new List<int>(4096);
            Vector3[] p = new Vector3[3];
            for (int ri = 0; ri < rends.Count; ri++)
            {
                Renderer r = rends[ri];
                if (r == null) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                if (!mesh.isReadable) { meshesSkipped++; continue; }
                Matrix4x4 m = r.localToWorldMatrix;
                verts.Clear(); mesh.GetVertices(verts);
                // to the units of the grid: x, y, z in cells, y from the bottom
                Vector3[] gv = new Vector3[verts.Count];
                for (int i = 0; i < gv.Length; i++)
                {
                    Vector3 w = m.MultiplyPoint3x4(verts[i]) - corner;
                    gv[i] = new Vector3(Vector3.Dot(w, e1) * inv, (Vector3.Dot(w, up) - oy) * inv, Vector3.Dot(w, e2) * inv);
                }
                bool any = false;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    if (mesh.GetTopology(sm) != MeshTopology.Triangles) continue;
                    tris.Clear(); mesh.GetTriangles(tris, sm);
                    for (int t = 0; t + 2 < tris.Count; t += 3)
                    {
                        p[0] = gv[tris[t]]; p[1] = gv[tris[t + 1]]; p[2] = gv[tris[t + 2]];
                        float x0 = Mathf.Min(p[0].x, Mathf.Min(p[1].x, p[2].x)), x1 = Mathf.Max(p[0].x, Mathf.Max(p[1].x, p[2].x));
                        float y0 = Mathf.Min(p[0].y, Mathf.Min(p[1].y, p[2].y)), y1 = Mathf.Max(p[0].y, Mathf.Max(p[1].y, p[2].y));
                        float z0 = Mathf.Min(p[0].z, Mathf.Min(p[1].z, p[2].z)), z1 = Mathf.Max(p[0].z, Mathf.Max(p[1].z, p[2].z));
                        if (x1 < 0f || y1 < 0f || z1 < 0f || x0 > nx || y0 > ny || z0 > nz) continue;
                        int ix0 = Mathf.Max(0, Mathf.FloorToInt(x0)), ix1 = Mathf.Min(nx - 1, Mathf.FloorToInt(x1));
                        int iy0 = Mathf.Max(0, Mathf.FloorToInt(y0)), iy1 = Mathf.Min(ny - 1, Mathf.FloorToInt(y1));
                        int iz0 = Mathf.Max(0, Mathf.FloorToInt(z0)), iz1 = Mathf.Min(nz - 1, Mathf.FloorToInt(z1));
                        int count = (ix1 - ix0 + 1) * (iy1 - iy0 + 1) * (iz1 - iz0 + 1);
                        if (count <= 0) continue;
                        any = true; trianglesUsed++;
                        if (count <= 4)
                        {
                            for (int y = iy0; y <= iy1; y++) for (int z = iz0; z <= iz1; z++) for (int x = ix0; x <= ix1; x++) v.solid[(y * nz + z) * nx + x] = 1;
                            continue;
                        }
                        for (int y = iy0; y <= iy1; y++)
                            for (int z = iz0; z <= iz1; z++)
                                for (int x = ix0; x <= ix1; x++)
                                {
                                    int idx = (y * nz + z) * nx + x;
                                    if (v.solid[idx] == 1) continue;
                                    if (TriBox(p[0], p[1], p[2], new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), 0.5f)) v.solid[idx] = 1;
                                }
                    }
                }
                if (any) meshesRead++;
            }
            double tSurf = sw.Elapsed.TotalMilliseconds;
            // what is enclosed (not reachable from outside the grid through air) is the inside of something: solid
            int N = nx * ny * nz;
            byte[] seen = new byte[N];
            Stack<int> st = new Stack<int>(4096);
            for (int y = 0; y < ny; y++)
                for (int z = 0; z < nz; z++)
                    for (int x = 0; x < nx; x++)
                    {
                        if (!(x == 0 || x == nx - 1 || z == 0 || z == nz - 1 || y == 0 || y == ny - 1)) continue;
                        int idx = (y * nz + z) * nx + x;
                        if (v.solid[idx] == 0 && seen[idx] == 0) { seen[idx] = 1; st.Push(idx); }
                    }
            while (st.Count > 0)
            {
                int idx = st.Pop();
                int x = idx % nx, z = (idx / nx) % nz, y = idx / (nx * nz);
                if (x > 0) Visit(v.solid, seen, st, idx - 1);
                if (x < nx - 1) Visit(v.solid, seen, st, idx + 1);
                if (z > 0) Visit(v.solid, seen, st, idx - nx);
                if (z < nz - 1) Visit(v.solid, seen, st, idx + nx);
                if (y > 0) Visit(v.solid, seen, st, idx - nx * nz);
                if (y < ny - 1) Visit(v.solid, seen, st, idx + nx * nz);
            }
            int enclosed = 0, surface = 0;
            for (int i = 0; i < N; i++)
            {
                if (v.solid[i] == 1) surface++;
                else if (seen[i] == 0) { v.solid[i] = 1; enclosed++; }
            }
            // cavities (air with solid above it in the column), as the layer version counts them
            v.cavityCells = 0;
            for (int k = 0; k < nz; k++)
                for (int i = 0; i < nx; i++)
                {
                    bool seenSolid = false;
                    for (int y = ny - 1; y >= 0; y--)
                    {
                        if (v.solid[(y * nz + k) * nx + i] == 1) seenSolid = true;
                        else if (seenSolid) v.cavityCells++;
                    }
                }
            double tFill = sw.Elapsed.TotalMilliseconds;
            v.BuildSdf();
            v.layersUsed = 0;
            v.ready = surface > 100;
            v.report = "MESH triangles: " + meshesRead + " meshes read (" + trianglesUsed + " triangles in the grid), " + meshesSkipped + " not readable, surface cells " + surface + ", enclosed cells " + enclosed
                     + ", cavity cells " + v.cavityCells + ", surface " + tSurf.ToString("F0") + " ms, fill " + (tFill - tSurf).ToString("F0") + " ms, sdf " + (sw.Elapsed.TotalMilliseconds - tFill).ToString("F0") + " ms";
            return v;
        }

        private static void Visit(byte[] solid, byte[] seen, Stack<int> st, int idx)
        {
            if (solid[idx] == 0 && seen[idx] == 0) { seen[idx] = 1; st.Push(idx); }
        }

        // triangle / axis-aligned box overlap (Akenine-Moller): the box centre c, half size h on every axis
        private static bool TriBox(Vector3 a, Vector3 b, Vector3 c, Vector3 centre, float h)
        {
            a -= centre; b -= centre; c -= centre;
            Vector3 e0 = b - a, e1 = c - b, e2 = a - c;
            // the 9 axes of the edges against the box axes
            if (AxisTest(e0.z, e0.y, a.y, a.z, c.y, c.z, h, h)) return false;
            if (AxisTest(e0.z, e0.x, a.x, a.z, c.x, c.z, h, h)) return false;
            if (AxisTest(e0.y, e0.x, b.x, b.y, c.x, c.y, h, h)) return false;
            if (AxisTest(e1.z, e1.y, a.y, a.z, b.y, b.z, h, h)) return false;
            if (AxisTest(e1.z, e1.x, a.x, a.z, b.x, b.z, h, h)) return false;
            if (AxisTest(e1.y, e1.x, a.x, a.y, b.x, b.y, h, h)) return false;
            if (AxisTest(e2.z, e2.y, a.y, a.z, b.y, b.z, h, h)) return false;
            if (AxisTest(e2.z, e2.x, a.x, a.z, b.x, b.z, h, h)) return false;
            if (AxisTest(e2.y, e2.x, b.x, b.y, c.x, c.y, h, h)) return false;
            // the box axes
            if (Mathf.Min(a.x, Mathf.Min(b.x, c.x)) > h || Mathf.Max(a.x, Mathf.Max(b.x, c.x)) < -h) return false;
            if (Mathf.Min(a.y, Mathf.Min(b.y, c.y)) > h || Mathf.Max(a.y, Mathf.Max(b.y, c.y)) < -h) return false;
            if (Mathf.Min(a.z, Mathf.Min(b.z, c.z)) > h || Mathf.Max(a.z, Mathf.Max(b.z, c.z)) < -h) return false;
            // the plane of the triangle
            Vector3 n = Vector3.Cross(e0, e1);
            float d = -Vector3.Dot(n, a);
            float r = h * (Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z));
            return Mathf.Abs(d) <= r;
        }

        // one separating-axis test of an edge: the axis is (0, -e.z, e.y)-like, the projections of two vertices and the radius of the box on it. true = separated
        private static bool AxisTest(float ea, float eb, float v0a, float v0b, float v1a, float v1b, float h0, float h1)
        {
            float p0 = ea * v0a - eb * v0b, p1 = ea * v1a - eb * v1b;
            float mn = Mathf.Min(p0, p1), mx = Mathf.Max(p0, p1);
            float rad = (Mathf.Abs(ea) + Mathf.Abs(eb)) * h0;
            return mn > rad || mx < -rad;
        }

        private static RenderTexture MakeRT(int w, int h)
        {
            RenderTexture rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGBFloat) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, antiAliasing = 1 };
            rt.Create();
            return rt;
        }

        // ------------------------------------------------------------------ the solid grid
        private void Rasterize(List<Color[]> layers, float[] fallbackTop)
        {
            solid = new byte[nx * ny * nz];
            List<Vector2> hits = new List<Vector2>(16);       // (height above the bottom, 1 = front face)
            List<float> stack = new List<float>(8);
            for (int k = 0; k < nz; k++)
                for (int i = 0; i < nx; i++)
                {
                    int col = k * nx + i;
                    hits.Clear();
                    for (int l = 0; l < layers.Count; l++)
                    {
                        float enc = layers[l][col].r;
                        if (enc > 0f) { float qh = Mathf.Floor(enc * 0.5f); bool fr0 = enc - 2f * qh > 0.5f; if (facingInverted) fr0 = !fr0; hits.Add(new Vector2(qh / 16f, fr0 ? 1f : 0f)); }
                    }
                    if (hits.Count == 0)
                    {
                        // nothing drawn here: bare terrain, which only the rays saw
                        if (fallbackTop != null && fallbackTop[col] > oy + 0.01f) hits.Add(new Vector2(fallbackTop[col] - oy, 1f));
                        else continue;
                    }
                    // Front and back faces are matched with a STACK (descending): a front face opens a solid, the next back face closes the one opened
                    // last. So solids that overlap (a roof that runs into a deck) are merged right. A front face that is never closed (a sheet - the ground,
                    // a lid that is not a closed mesh) is a solid SheetThickness thick, except the lowest one: that one goes down to the bottom of the grid.
                    stack.Clear();
                    for (int h = 0; h < hits.Count; h++)
                    {
                        if (hits[h].y > 0.5f) { stack.Add(hits[h].x); continue; }
                        if (stack.Count > 0) { float topM = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); Fill(i, k, hits[h].x, topM); }
                        else Fill(i, k, Mathf.Max(hits[h].x - SheetThickness, 0f), hits[h].x);            // a back face with no front above it
                    }
                    for (int s = 0; s < stack.Count; s++)
                        Fill(i, k, s == stack.Count - 1 ? 0f : Mathf.Max(stack[s] - SheetThickness, 0f), stack[s]);
                }
            // the cavities: air cells with solid above them in the column (under a roof)
            cavityCells = 0;
            for (int k = 0; k < nz; k++)
                for (int i = 0; i < nx; i++)
                {
                    bool seenSolid = false;
                    for (int y = ny - 1; y >= 0; y--)
                    {
                        if (solid[(y * nz + k) * nx + i] == 1) seenSolid = true;
                        else if (seenSolid) cavityCells++;
                    }
                }
        }

        private void Fill(int i, int k, float bottom, float top)
        {
            int y0 = Mathf.Max(0, Mathf.CeilToInt(bottom / cell - 0.5f)), y1 = Mathf.Min(ny - 1, Mathf.FloorToInt(top / cell - 0.5f));
            for (int y = y0; y <= y1; y++) solid[(y * nz + k) * nx + i] = 1;
        }

        // ------------------------------------------------------------------ the signed distance field (exact Euclidean distance transform)
        private void BuildSdf()
        {
            int N = nx * ny * nz;
            float[] dOut = new float[N], dIn = new float[N];
            for (int i = 0; i < N; i++) { dOut[i] = solid[i] == 1 ? 0f : 1e8f; dIn[i] = solid[i] == 1 ? 1e8f : 0f; }
            Edt3D(dOut); Edt3D(dIn);
            sdf = new float[N];
            for (int i = 0; i < N; i++)
                sdf[i] = solid[i] == 1 ? -(Mathf.Sqrt(Mathf.Min(dIn[i], 1e7f)) - 0.5f) * cell : (Mathf.Sqrt(Mathf.Min(dOut[i], 1e7f)) - 0.5f) * cell;
        }

        private void Edt3D(float[] f)
        {
            int m = Mathf.Max(nx, Mathf.Max(ny, nz));
            float[] line = new float[m], outL = new float[m], z = new float[m + 1]; int[] v = new int[m];
            for (int y = 0; y < ny; y++) for (int k = 0; k < nz; k++)
            { int b = (y * nz + k) * nx; for (int i = 0; i < nx; i++) line[i] = f[b + i]; Edt1D(line, nx, outL, v, z); for (int i = 0; i < nx; i++) f[b + i] = outL[i]; }
            for (int y = 0; y < ny; y++) for (int i = 0; i < nx; i++)
            { for (int k = 0; k < nz; k++) line[k] = f[(y * nz + k) * nx + i]; Edt1D(line, nz, outL, v, z); for (int k = 0; k < nz; k++) f[(y * nz + k) * nx + i] = outL[k]; }
            for (int k = 0; k < nz; k++) for (int i = 0; i < nx; i++)
            { for (int y = 0; y < ny; y++) line[y] = f[(y * nz + k) * nx + i]; Edt1D(line, ny, outL, v, z); for (int y = 0; y < ny; y++) f[(y * nz + k) * nx + i] = outL[y]; }
        }

        // Felzenszwalb & Huttenlocher: the lower envelope of parabolas, f = squared distances so far
        private static void Edt1D(float[] f, int n, float[] d, int[] v, float[] z)
        {
            int k = 0; v[0] = 0; z[0] = -1e20f; z[1] = 1e20f;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                while (s <= z[k]) { k--; s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]); }
                k++; v[k] = q; z[k] = s; z[k + 1] = 1e20f;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                float dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }

        // ------------------------------------------------------------------ queries (local coordinates, metres)
        public bool InGrid(Vector3 l) { return l.x > 0f && l.z > 0f && l.x < nx * cell && l.z < nz * cell && l.y > oy && l.y < oy + ny * cell; }

        private float At(int i, int j, int k)
        {
            if (i < 0) i = 0; else if (i >= nx) i = nx - 1;
            if (k < 0) k = 0; else if (k >= nz) k = nz - 1;
            if (j < 0) j = 0; else if (j >= ny) j = ny - 1;
            return sdf[(j * nz + k) * nx + i];
        }

        // the signed distance, trilinear; far outside the grid: 50 m of air
        public float Sdf(Vector3 l)
        {
            if (!ready) return 50f;
            float gx = l.x / cell - 0.5f, gy = (l.y - oy) / cell - 0.5f, gz = l.z / cell - 0.5f;
            if (gx < -1f || gz < -1f || gx > nx || gz > nz || gy > ny) return 50f;
            int i = Mathf.FloorToInt(gx), j = Mathf.FloorToInt(gy), k = Mathf.FloorToInt(gz);
            float fx = gx - i, fy = gy - j, fz = gz - k;
            float c00 = Mathf.Lerp(At(i, j, k), At(i + 1, j, k), fx), c10 = Mathf.Lerp(At(i, j + 1, k), At(i + 1, j + 1, k), fx);
            float c01 = Mathf.Lerp(At(i, j, k + 1), At(i + 1, j, k + 1), fx), c11 = Mathf.Lerp(At(i, j + 1, k + 1), At(i + 1, j + 1, k + 1), fx);
            return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
        }

        public bool Solid(Vector3 l) { return Sdf(l) < 0f; }

        public Vector3 Normal(Vector3 l)
        {
            float e = cell * 0.8f;
            Vector3 g = new Vector3(Sdf(l + new Vector3(e, 0, 0)) - Sdf(l - new Vector3(e, 0, 0)),
                                    Sdf(l + new Vector3(0, e, 0)) - Sdf(l - new Vector3(0, e, 0)),
                                    Sdf(l + new Vector3(0, 0, e)) - Sdf(l - new Vector3(0, 0, e)));
            return g.sqrMagnitude < 1e-8f ? Vector3.up : g.normalized;
        }

        // ------------------------------------------------------------------ the trace
        // A BOUNCING RAY through the real geometry. From `start` in the direction az (an angle in the horizontal plane), tilted `tilt` radians off
        // straight down, in steps that follow the distance field (sphere tracing, at most 1.5 m). A surface turns it: most of its direction stays
        // ALONG the surface (the wall jet clings to what it hits), a part is mirrored away from it; a ray that meets a wall head-on splits along the
        // wall (and up it) instead of going back. It flies on above the ground, under roofs, through the trench and out of its ends.
        // sched = arc lengths (m) to report, ascending; outPos gets the local position of each, outClear the free width / headroom there (m).
        public static readonly float[] WidthHeights = { 1f, 3f, 6f, 12f };           // the heights above the flow (m) at which the free width is measured
        public int Trace(Vector3 start, float az, float tilt, float[] sched, int n, Vector3[] outPos, float[] outClear, ref int bounces, float[] outWidthH = null, float[] outHead = null)
        {
            if (!ready || n <= 0) return 0;
            const float clearance = 0.5f;
            Vector3 d = new Vector3(Mathf.Sin(tilt) * Mathf.Cos(az), -Mathf.Cos(tilt), Mathf.Sin(tilt) * Mathf.Sin(az));
            Vector3 pos = start;
            for (int it = 0; it < 40 && Sdf(pos) < clearance; it++) { Vector3 nn = Normal(pos); pos += nn * Mathf.Max(clearance - Sdf(pos), 0.3f); }
            float s = 0f; int k = 0; int hitCount = 0;
            Vector3 lastHoriz = new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az));
            while (k < n)
            {
                int guard = 0;
                while (s < sched[k] - 1e-3f && guard++ < 4000)
                {
                    float sd = Sdf(pos);
                    float step = Mathf.Min(Mathf.Clamp(sd * 0.8f, 0.4f, 1.5f), sched[k] - s);
                    Vector3 q = pos + d * step;
                    float sq = Sdf(q);
                    if (sq < clearance && InGrid(q))
                    {
                        Vector3 nrm = Normal(q);
                        float vn = Vector3.Dot(d, nrm);
                        if (vn < 0f)
                        {
                            Vector3 tang = d - nrm * vn;
                            if (tang.magnitude < 0.3f)
                            {
                                // head-on: no direction of its own along the surface. It splits to the side (the side that this ray's azimuth leans to)
                                // and, where the surface is a wall, up it.
                                Vector3 side = Vector3.Cross(nrm, Vector3.up);
                                if (side.sqrMagnitude < 0.05f) side = Vector3.Cross(nrm, Vector3.right);
                                side.Normalize();
                                if (Vector3.Dot(side, new Vector3(Mathf.Cos(az + 1.3f), 0f, Mathf.Sin(az + 1.3f))) < 0f) side = -side;
                                Vector3 upAlong = Vector3.up - nrm * Vector3.Dot(Vector3.up, nrm);
                                float wall = 1f - Mathf.Abs(Vector3.Dot(nrm, Vector3.up));
                                // up the wall only a little: a wall jet that meets a wall splashes up and back, it does not fly over a wall that is higher than it is thick
                                d = (side * 0.85f + upAlong.normalized * (0.12f * wall) + nrm * 0.1f).normalized;
                            }
                            else
                            {
                                Vector3 mirror = d - 2f * vn * nrm;
                                d = (tang * 0.9f + mirror * 0.25f + nrm * 0.04f).normalized;
                                if (Vector3.Dot(d, nrm) < 0.03f) d = (d + nrm * 0.06f).normalized;
                            }
                            hitCount++; bounces++;
                            if (hitCount > 160) return k;
                        }
                        q += nrm * Mathf.Max(clearance - sq, 0f);
                    }
                    pos = q; s += step;
                }
                Vector3 hd = new Vector3(d.x, 0f, d.z);
                if (hd.sqrMagnitude > 0.04f) lastHoriz = hd.normalized;
                // free width across the flow and headroom (the roof of the mount)
                float width = 0f;
                for (int side = -1; side <= 1; side += 2)
                {
                    float w = 40f;
                    Vector3 lat = new Vector3(-lastHoriz.z * side, 0f, lastHoriz.x * side);
                    for (int j = 1; j <= 16; j++) if (Solid(pos + lat * (j * 2.5f) + Vector3.up * 1.0f)) { w = j * 2.5f; break; }
                    width += w;
                }
                float head = 40f;
                for (int j = 1; j <= 20; j++) if (Solid(pos + Vector3.up * (j * 1.5f))) { head = j * 1.5f; break; }
                // the free width across the flow at several heights: a wall blocks a stream only if it reaches as high as the stream is thick
                if (outWidthH != null)
                {
                    for (int hi = 0; hi < WidthHeights.Length; hi++)
                    {
                        float wsum = 0f;
                        for (int side = -1; side <= 1; side += 2)
                        {
                            float w = 60f;
                            Vector3 lat = new Vector3(-lastHoriz.z * side, 0f, lastHoriz.x * side);
                            for (int j = 1; j <= 24; j++) if (Solid(pos + lat * (j * 2.5f) + Vector3.up * WidthHeights[hi])) { w = j * 2.5f; break; }
                            wsum += w;
                        }
                        outWidthH[k * 4 + hi] = wsum;
                    }
                }
                if (outHead != null) outHead[k] = head;
                outPos[k] = pos;
                outClear[k] = Mathf.Min(width, head * 2.2f);
                k++;
            }
            return k;
        }

        // ------------------------------------------------------------------ text views for the log / the test
        // A vertical cross-section: along the horizontal direction `dir` from `origin`, `length` m, `height` m from the bottom up. '#' solid, ' ' air.
        // the raw solid cells along x at a given row (j) and column (k), for the test
        public string RawRow(int j, int k)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < nx; i++) sb.Append(solid[(j * nz + k) * nx + i] == 1 ? '#' : '.');
            return sb.ToString();
        }

        // A map from above of what is solid at one height: x to the right (east), z up (north), `step` cells per character, '#' = solid, '+' = the centre
        public string TopMap(float y, int step)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int k = nz - step; k >= 0; k -= step)
            {
                for (int i = 0; i + step <= nx; i += step)
                {
                    bool s = false;
                    for (int dk = 0; dk < step && !s; dk++) for (int di = 0; di < step && !s; di++)
                        if (Solid(new Vector3((i + di + 0.5f) * cell, y, (k + dk + 0.5f) * cell))) s = true;
                    bool centre = i <= nx / 2 && nx / 2 < i + step && k <= nz / 2 && nz / 2 < k + step;
                    sb.Append(centre ? '+' : (s ? '#' : '.'));
                }
                sb.Append((char)10);
            }
            return sb.ToString();
        }

        public string CrossSection(Vector3 origin, Vector3 dir, float length, float yTop, float yBottom, float step)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (float y = yTop; y >= yBottom; y -= step)
            {
                sb.Append(y.ToString("F0").PadLeft(5)).Append(' ');
                for (float t = 0f; t <= length; t += step)
                {
                    Vector3 p = origin + dir * t; p.y = y;
                    sb.Append(Solid(p) ? '#' : '.');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
