using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // THE NEW CORE OF THE TRAIL: a list of nodes, drawn as capsules by TrailCapsules.shader. No particles, no per-puff physics:
    //   - a node is a point of the track of an emitter (body-fixed, in double precision: the floating origin, the Krakensbane and the rotation of the planet cannot
    //     touch it), with the radius it was born with, its time and the density of the smoke there
    //   - everything else is a function of the AGE: the radius grows from the radius of the nozzle to a large end radius, the smoke fades at the end of its life
    //   - consecutive nodes of one chain are the two ends of a capsule
    // The renderer only needs the capsules and three levels of bounding spheres (blocks of 8 chunks of 8 segments).
    public sealed class CapsuleTrail
    {
        public static Shader TrailShader;

        public const int MaxSegments = 4096;
        // the shader inflates every capsule by this much and lets the noise decide where the smoke ends inside it: the lobes then stand OUT of the base
        // cone (at 1.3 the edge of the capsule was the edge of the smoke: straight, "cut" sides)
        public const float Inflate = 1.6f;           // 64 blocks x 8 chunks x 8 segments (the shader keeps a 64 bit mask of blocks)
        private struct Node { public Vector3d rel; public float r0; public float t; public float dens; public int chain; }

        private static CapsuleTrail instance;
        public static CapsuleTrail Instance { get { if (instance == null) instance = new CapsuleTrail(); return instance; } }

        private readonly List<Node> nodes = new List<Node>();
        private CelestialBody body;
        private Vector3d anchor;                          // the origin of the trail frame, body-fixed
        private bool haveAnchor;
        private int nextChain = 1;
        private readonly Dictionary<int, int> chainOf = new Dictionary<int, int>();       // emitter id -> the chain it is on
        // THE LIVE HEAD of a chain, refreshed every frame by its emitter: the newest node lags behind the emission point (a node is laid only every 0.45 radius), and a
        // bare chain ended in a round cap hanging under the flame. The head draws (1) the link from the newest node to the emission point and (2) the ROOT: from inside the
        // flame (toward the nozzle) down to the emission point, as wide as the flame there and growing to the start radius, its density rising from nothing - the smoke
        // comes OUT of the flame instead of starting as a ball below it.
        public const int HeadPoints = 7;              // the root: 6 capsules along the bent jet, from the nozzle to the emission point
        private struct Head { public Vector3d[] pts; public int n; public float rRoot, rTip, dens, t; }
        private readonly Dictionary<int, Head> heads = new Dictionary<int, Head>();
        private readonly Dictionary<int, float> lastPush = new Dictionary<int, float>();       // chain -> the last time its emitter pushed (a gap ends the chain)        // chain -> its head

        private GameObject go;
        private MeshRenderer mr;
        private Material mat;
        private static Mesh cubeMesh;
        private readonly MaterialPropertyBlock mb = new MaterialPropertyBlock();
        private readonly List<int> headDead = new List<int>();
        private readonly MaterialPropertyBlock lookBlock = new MaterialPropertyBlock();
        private ComputeBuffer segBuf, chunkBuf, blockBuf;
        private int segCap, chunkCap, blockCap;
        private Vector4[] segArr = new Vector4[0], chunkArr = new Vector4[0], blockArr = new Vector4[0];
        private float lookStamp = -10f;
        private Color lookSky, lookGnd, lookSun, lookShadow, lookSunRaw;

        public int NodeCount { get { return nodes.Count; } }

        private static QuaternionD Rot(CelestialBody b) { return (QuaternionD)b.bodyTransform.rotation; }

        // the track of one emitter: a new node (when it has moved enough), at the world position of its nozzle
        // 1 = full smoke, 0 = none: the thinning of the trail with altitude (the air is where smoke persists; high up the exhaust is a plume, not a trail)
        public static float AltitudeFade(CelestialBody b, Vector3 world)
        {
            float end = SmokeTuning.NewTrailEndAltitude * 1000f;
            if (end <= 0f || b == null) return 1f;
            float len = Mathf.Clamp(SmokeTuning.NewTrailFadeLength * 1000f, 1f, end);
            float alt = (float)b.GetAltitude((Vector3d)world);
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((alt - (end - len)) / len));
        }

        public void Push(int emitterId, CelestialBody b, Vector3 world, float radius, float density, bool newChain)
        {
            if (b == null) return;
            float altFade = AltitudeFade(b, world);
            if (altFade < 0.02f) return;           // above the end of the trail: no new smoke (the chain closes by itself after half a second)
            density *= altFade;
            if (body != b) { Clear(); body = b; }
            if (SmokeTuning.NewTrail && TrailShader != null) Ensure();
            Vector3d rel = QuaternionD.Inverse(Rot(b)) * ((Vector3d)world - b.position);
            if (!haveAnchor) { anchor = rel; haveAnchor = true; }
            int chain;
            if (newChain || !chainOf.TryGetValue(emitterId, out chain)) { chain = nextChain++; chainOf[emitterId] = chain; }
            else
            {
                // the engine was off for a while (no push for half a second): its smoke ends where it stopped and a NEW chain starts where it lights again -
                // the old chain joined across the gap drew a straight capsule from the last smoke to the new position
                float lastT;
                if (lastPush.TryGetValue(chain, out lastT) && Time.time - lastT > 0.5f) { heads.Remove(chain); chain = nextChain++; chainOf[emitterId] = chain; }
            }
            lastPush[chain] = Time.time;
            if (nodes.Count > 0)
            {
                Node last = nodes[nodes.Count - 1];
                // the nodes of one chain are appended in order; the last node of THIS chain decides whether it moved enough
                for (int i = nodes.Count - 1; i >= 0 && i >= nodes.Count - 8; i--)
                    if (nodes[i].chain == chain) { last = nodes[i]; if ((last.rel - rel).magnitude < Mathf.Max(0.45f * radius, 0.8f)) return; break; }
            }
            nodes.Add(new Node { rel = rel, r0 = StartRadius(radius), t = Time.time, dens = density, chain = chain });
            if (nodes.Count > MaxSegments - 8) Thin();
        }

        // too many nodes: the oldest half keeps every second one (the old smoke is wide, it does not need the detail)
        private void Thin()
        {
            int half = nodes.Count / 2;
            List<Node> keep = new List<Node>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                bool boundary = i == 0 || i == nodes.Count - 1 || nodes[i - 1].chain != nodes[i].chain || nodes[i + 1].chain != nodes[i].chain;
                if (i >= half || (i & 1) == 0 || boundary) keep.Add(nodes[i]);
            }
            nodes.Clear(); nodes.AddRange(keep);
        }

        public void EndChain(int emitterId) { int c; if (chainOf.TryGetValue(emitterId, out c)) heads.Remove(c); chainOf.Remove(emitterId); }

        // the head of the emitter's chain this frame (after Push): the points of the jet from the nozzle (pts[0]) to the emission point (pts[n - 1]), world space,
        // the radius at the nozzle, and the colour of the glow of the hot exhaust
        public void SetHead(int emitterId, List<Vector3> pts, float nozzleRadius, float radius, float density, Color glow)
        {
            int chain;
            if (body == null || pts == null || pts.Count < 1 || !chainOf.TryGetValue(emitterId, out chain)) return;
            float headFade = AltitudeFade(body, pts[0]);
            if (headFade < 0.02f) { heads.Remove(chain); return; }
            density *= headFade;
            QuaternionD inv = QuaternionD.Inverse(Rot(body));
            Head h;
            if (!heads.TryGetValue(chain, out h) || h.pts == null) h.pts = new Vector3d[HeadPoints];
            h.n = Mathf.Min(pts.Count, HeadPoints);
            for (int i = 0; i < h.n; i++) h.pts[i] = inv * ((Vector3d)pts[pts.Count - h.n + i] - body.position);
            h.rRoot = Mathf.Max(nozzleRadius, 0.3f); h.rTip = StartRadius(radius); h.dens = density; h.t = Time.time;
            heads[chain] = h;
            glowCol = glow;
        }
        private Color glowCol;

        private static float JetDensity(float f) { return Mathf.Lerp(0.3f, 1f, Mathf.SmoothStep(0f, 1f, f)); }
        private static float Heat(float f) { return Mathf.Pow(Mathf.Clamp01(1f - f / 0.7f), 1.3f); }

        public static float StartRadius(float radius) { return Mathf.Max(radius * 3.5f, 5f); }

        public void Clear()
        {
            nodes.Clear(); chainOf.Clear(); heads.Clear(); lastPush.Clear(); haveAnchor = false;
            if (mr != null) { mr.enabled = false; SmokeRenderRegistry.SetActive(mr, false); }
        }

        private static float RadiusAt(float r0, float age)
        {
            // A SATURATING growth, measured on KSA's trail (11 s after liftoff: radius ~37 m at 4 s, ~60 m at 11 s, its template end radius 80 m): the smoke
            // widens quickly next to the rocket and then hardly at all - a tall, slender column, not a widening pyramid. r = r0 + (rEnd - r0) (1 - exp(-t / tau)),
            // the rate at the start finite ((rEnd - r0) / tau: no bulb popping out of a thread).
            float rEnd = Mathf.Max(SmokeTuning.NewTrailMaxRadius, 4f * r0);
            float ta = Mathf.Max(SmokeTuning.NewTrailGrowTau, 0.5f);
            return r0 + (rEnd - r0) * (1f - Mathf.Exp(-age / ta));
        }

        // smooth 1-D value noise in [0, 1]
        private static float Hash1(int i, int seed) { unchecked { int h = i * 374761393 + seed * 668265263; h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xffff) / 65535f; } }
        private static float Smooth1D(float x, int seed)
        {
            int i = Mathf.FloorToInt(x); float f = x - i; f = f * f * (3f - 2f * f);
            return Mathf.Lerp(Hash1(i, seed), Hash1(i + 1, seed), f);
        }
        private static float WidthK(float tEmit)
        {
            float w = SmokeTuning.NewTrailWobble;
            if (w <= 0f) return 1f;
            return 1f + 0.15f * w * (Smooth1D(tEmit * 0.10f, 11) * 2f - 1f);
        }
        private static Vector3 Wander(float tEmit, float age, float radius)
        {
            float w = SmokeTuning.NewTrailWobble;
            if (w <= 0f) return Vector3.zero;
            // the wander grows with the age (a young node is where the nozzle is), up to about half of the radius
            float grow = Mathf.Clamp01(age / 8f);
            Vector3 d = new Vector3(Smooth1D(tEmit * 0.09f, 21) - 0.5f, Smooth1D(tEmit * 0.09f, 22) - 0.5f, Smooth1D(tEmit * 0.09f, 23) - 0.5f) * 2f;
            return d * (0.22f * w * radius * grow);
        }

        private static float FadeAt(float age)
        {
            float life = Mathf.Max(SmokeTuning.NewTrailLife, 10f);
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f * life, life, age));
        }

        private void Ensure()
        {
            if (go != null) return;
            go = new GameObject("PlumeTrails.CapsuleTrail");
            Object.DontDestroyOnLoad(go);
            if (cubeMesh == null)
            {
                GameObject tg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cubeMesh = tg.GetComponent<MeshFilter>().sharedMesh;
                Object.Destroy(tg);
            }
            go.AddComponent<MeshFilter>().sharedMesh = cubeMesh;
            mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mat = new Material(TrailShader) { renderQueue = 3004 };
            mr.sharedMaterial = mat;
            mr.enabled = false;
            SmokeRenderRegistry.Register(mr, mb);
            go.AddComponent<CapsuleTrailDriver>();
        }

        private static ComputeBuffer Grow(ComputeBuffer buf, ref int cap, int need)
        {
            if (buf != null && cap >= need) return buf;
            if (buf != null) buf.Release();
            cap = Mathf.Max(need, 64);
            return new ComputeBuffer(cap, 16);
        }

        // the mod is off: the proxy is not drawn (the nodes stay: switched on again, the trail is still there)
        public void HideWhenOff() { if (mr != null) { mr.enabled = false; SmokeRenderRegistry.SetActive(mr, false); } }

        // every frame: the age laws, the capsules, the bounding spheres, the buffers, the proxy cube
        public void Tick()
        {
            if (!SmokeTuning.NewTrail || !ModSettings.Enabled || TrailShader == null || !SystemInfo.supportsComputeShaders || body == null || nodes.Count < 2)
            {
                if (mr != null) { mr.enabled = false; SmokeRenderRegistry.SetActive(mr, false); }
                return;
            }
            float now = Time.time;
            float life = Mathf.Max(SmokeTuning.NewTrailLife, 10f);
            // the end of life: the nodes go (from the front of the list: it is ordered by time within a chain, and the oldest are first)
            int cut = 0;
            while (cut < nodes.Count && now - nodes[cut].t > life) cut++;
            if (cut > 0) nodes.RemoveRange(0, cut);
            if (nodes.Count < 2) { if (mr != null) { mr.enabled = false; SmokeRenderRegistry.SetActive(mr, false); } return; }
            Ensure();

            // segments between consecutive nodes of one chain: nodes of different chains interleave, so the next node of the same chain is searched
            int n = nodes.Count;
            if (segArr.Length < MaxSegments * 4) segArr = new Vector4[MaxSegments * 4];
            int ns = 0;
            Vector3 mn = new Vector3(1e30f, 1e30f, 1e30f), mx = new Vector3(-1e30f, -1e30f, -1e30f);
            Dictionary<int, int> prevOfChain = new Dictionary<int, int>();
            for (int i = 0; i < n && ns < MaxSegments; i++)
            {
                Node b = nodes[i];
                int pi;
                if (prevOfChain.TryGetValue(b.chain, out pi))
                {
                    Node a = nodes[pi];
                    float ageA = now - a.t, ageB = now - b.t;
                    float ra = RadiusAt(a.r0, ageA), rb = RadiusAt(b.r0, ageB);
                    // THE SMOKE IS NOT A MACHINED CONE: the width wanders along the trail and the whole column meanders (the air it flies through is turbulent, a gust moves a
                    // stretch of it sideways). Slow smooth noise of the moment a node was emitted: the neighbours move together (no zig-zag), the older the wider the wander.
                    Vector3 pa = (Vector3)(a.rel - anchor) + Wander(a.t, ageA, ra), pb = (Vector3)(b.rel - anchor) + Wander(b.t, ageB, rb);
                    ra *= WidthK(a.t); rb *= WidthK(b.t);
                    segArr[ns * 4] = new Vector4(pa.x, pa.y, pa.z, ra);
                    segArr[ns * 4 + 1] = new Vector4(pb.x, pb.y, pb.z, rb);
                    // how much smaller the capsule was a frame ago (the growth of the radius): the motion vectors of the temporal upscaling
                    float dtp = Mathf.Max(Time.deltaTime, 0.005f);
                    float growth = 0.5f * (RadiusAt(a.r0, Mathf.Max(ageA - dtp, 0f)) / Mathf.Max(RadiusAt(a.r0, ageA), 0.01f) + RadiusAt(b.r0, Mathf.Max(ageB - dtp, 0f)) / Mathf.Max(RadiusAt(b.r0, ageB), 0.01f));
                    segArr[ns * 4 + 2] = new Vector4(FadeAt(ageA), FadeAt(ageB), 0.5f * (a.dens + b.dens), growth);
                    segArr[ns * 4 + 3] = new Vector4(0f, 0f, 1f, 1f);
                    mn = Vector3.Min(mn, Vector3.Min(pa - Vector3.one * ra, pb - Vector3.one * rb));
                    mx = Vector3.Max(mx, Vector3.Max(pa + Vector3.one * ra, pb + Vector3.one * rb));
                    ns++;
                }
                prevOfChain[b.chain] = i;
            }
            // the live heads: the link from the newest node of the chain to the emission point, and the root from inside the flame
            headDead.Clear();
            foreach (KeyValuePair<int, Head> kv in heads)
            {
                Head h = kv.Value;
                if (now - h.t > 0.3f) { headDead.Add(kv.Key); continue; }       // the engine stopped: the chain ends at its last node
                int li;
                if (!prevOfChain.TryGetValue(kv.Key, out li)) continue;
                if (ns + 2 > MaxSegments) break;
                Node last = nodes[li];
                float ageL = now - last.t;
                float rl = RadiusAt(last.r0, ageL);
                Vector3 pl = (Vector3)(last.rel - anchor) + Wander(last.t, ageL, rl);
                rl *= WidthK(last.t);
                if (h.n < 1 || ns + h.n + 1 > MaxSegments) continue;
                Vector3 pt = (Vector3)(h.pts[h.n - 1] - anchor);
                float rt = h.rTip * WidthK(now);
                if ((pt - pl).sqrMagnitude > 0.01f)
                {
                    segArr[ns * 4] = new Vector4(pl.x, pl.y, pl.z, rl); segArr[ns * 4 + 1] = new Vector4(pt.x, pt.y, pt.z, rt);
                    segArr[ns * 4 + 2] = new Vector4(FadeAt(ageL), 1f, 0.5f * (last.dens + h.dens), 1f);
                    segArr[ns * 4 + 3] = new Vector4(0f, 0f, 1f, 1f);
                    ns++;
                }
                mn = Vector3.Min(mn, Vector3.Min(pl - Vector3.one * rl, pt - Vector3.one * rt));
                mx = Vector3.Max(mx, Vector3.Max(pl + Vector3.one * rl, pt + Vector3.one * rt));
                // THE ROOT along the bent jet: the radius grows from the nozzle to the start radius, the density rises (the first part is inside the flame), the heat
                // (the glow of the hot exhaust) is full at the nozzle and gone at about two thirds of the way
                for (int k = 0; k + 1 < h.n; k++)
                {
                    float f0 = k / (float)(h.n - 1), f1 = (k + 1) / (float)(h.n - 1);
                    Vector3 p0 = (Vector3)(h.pts[k] - anchor), p1 = (Vector3)(h.pts[k + 1] - anchor);
                    if ((p1 - p0).sqrMagnitude < 0.0001f) continue;
                    float r0s = Mathf.Lerp(h.rRoot, rt, Mathf.Pow(f0, 0.8f)), r1s = Mathf.Lerp(h.rRoot, rt, Mathf.Pow(f1, 0.8f));
                    segArr[ns * 4] = new Vector4(p0.x, p0.y, p0.z, r0s); segArr[ns * 4 + 1] = new Vector4(p1.x, p1.y, p1.z, r1s);
                    segArr[ns * 4 + 2] = new Vector4(1f, 1f, h.dens, 1f);
                    // the hot jet is thinner (by the density, not the profile: a low profile is eroded into holes), smoothly along the capsules
                    segArr[ns * 4 + 3] = new Vector4(Heat(f0), Heat(f1), JetDensity(f0), JetDensity(f1));
                    ns++;
                    mn = Vector3.Min(mn, Vector3.Min(p0 - Vector3.one * r0s, p1 - Vector3.one * r1s));
                    mx = Vector3.Max(mx, Vector3.Max(p0 + Vector3.one * r0s, p1 + Vector3.one * r1s));
                }
            }
            for (int i = 0; i < headDead.Count; i++) heads.Remove(headDead[i]);
            if (ns == 0) { mr.enabled = false; SmokeRenderRegistry.SetActive(mr, false); return; }
            // pad the last chunk with empty segments (radius 0: the shader rejects them)
            int padded = ((ns + 7) / 8) * 8;
            for (int s = ns; s < padded; s++) { segArr[s * 4] = Vector4.zero; segArr[s * 4 + 1] = Vector4.zero; segArr[s * 4 + 2] = Vector4.zero; segArr[s * 4 + 3] = Vector4.zero; }
            int nChunks = padded / 8;
            int nBlocks = (nChunks + 7) / 8;
            if (chunkArr.Length < nChunks) chunkArr = new Vector4[Mathf.Max(nChunks, 512)];
            if (blockArr.Length < nBlocks) blockArr = new Vector4[Mathf.Max(nBlocks, 64)];
            for (int c = 0; c < nChunks; c++) chunkArr[c] = Bound(segArr, c * 8, Mathf.Min(c * 8 + 8, ns));
            for (int bl = 0; bl < nBlocks; bl++)
            {
                // the sphere of a block holds the spheres of its chunks
                int c0 = bl * 8, c1 = Mathf.Min(c0 + 8, nChunks);
                Vector3 ctr = Vector3.zero; for (int c = c0; c < c1; c++) ctr += (Vector3)chunkArr[c];
                ctr /= (c1 - c0);
                float rr = 0f; for (int c = c0; c < c1; c++) rr = Mathf.Max(rr, Vector3.Distance(ctr, (Vector3)chunkArr[c]) + chunkArr[c].w);
                blockArr[bl] = new Vector4(ctr.x, ctr.y, ctr.z, rr);
            }
            segBuf = Grow(segBuf, ref segCap, padded * 4); segBuf.SetData(segArr, 0, 0, padded * 4);
            chunkBuf = Grow(chunkBuf, ref chunkCap, nChunks); chunkBuf.SetData(chunkArr, 0, 0, nChunks);
            blockBuf = Grow(blockBuf, ref blockCap, nBlocks); blockBuf.SetData(blockArr, 0, 0, nBlocks);

            // the frame in the world: the anchor (double precision, then a float: the world is the floating one, small numbers) and the rotation of the body
            QuaternionD rot = Rot(body);
            Vector3 anchorW = (Vector3)(body.position + rot * anchor);
            Quaternion q = body.bodyTransform.rotation;
            Matrix4x4 w2l = Matrix4x4.Rotate(Quaternion.Inverse(q));
            Vector3 ctrL = (mn + mx) * 0.5f;
            float rad = (mx - mn).magnitude * 0.5f;
            Vector3 ctrW = anchorW + q * ctrL;

            go.SetActive(true);
            mr.enabled = false;       // drawn explicitly by the reduced-resolution renderer (a second, direct draw would double it)
            go.transform.position = ctrW;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one * (2f * rad + 2f * (Inflate - 1f) * SmokeTuning.NewTrailMaxRadius * 1.2f + 4f);      // + the inflated rim

            if (now >= lookStamp)
            {
                lookStamp = now + 0.5f;
                SmokeTuning.Apply(lookBlock);
                ScattererSun.ApplyAt(lookBlock, ctrW);
                lookSky = lookBlock.GetColor("_AmbientSkyColor"); lookGnd = lookBlock.GetColor("_AmbientGroundColor");
                lookShadow = lookBlock.GetColor("_ShadowColor"); lookSun = lookBlock.GetColor("_SunlitColor");
                // the tint of the light (Scatterer's sun at a low sun, the sky and the ground) is kept only in part: under a cloud deck the smoke is nearly neutral, and the full
                // tint made the whole plume yellow. The luminance stays (only the hue is pulled toward grey).
                lookSunRaw = lookSun;
                float k = Mathf.Clamp01(SmokeTuning.NewTrailTint);
                lookSun = Neutral(lookSun, k); lookShadow = Neutral(lookShadow, k); lookSky = Neutral(lookSky, k); lookGnd = Neutral(lookGnd, k);
            }
            mb.SetBuffer("_TrSeg", segBuf); mb.SetBuffer("_TrChunk", chunkBuf); mb.SetBuffer("_TrBlock", blockBuf);
            mb.SetInt("_TrSegCount", padded); mb.SetInt("_TrChunkCount", nChunks); mb.SetInt("_TrBlockCount", nBlocks);
            mb.SetVector("_TrAnchorW", anchorW);
            mb.SetMatrix("_TrW2L", w2l);
            mb.SetVector("_TrLook", new Vector4(SmokeTuning.NewTrailDensityK, SmokeTuning.NewTrailErosion, SmokeTuning.NewTrailSharpness, SmokeTuning.NewTrailNoiseFreq));
            mb.SetFloat("_TrShadowOn", SmokeTuning.NewTrailShadow ? 1f : 0f);
            mb.SetFloat("_TrShadowDbg", SmokeTuning.ShadowCastDebug);
            mb.SetColor("_TrSunRaw", lookSunRaw);
            mb.SetVector("_TrLook6", new Vector4(SmokeTuning.NewTrailFill, 0.5f, SmokeTuning.NewTrailLightSoft, 0f));      // y: the noise moves the edge of the fill (no straight band)
            mb.SetVector("_TrLook5", new Vector4(SmokeTuning.NewTrailSilver, SmokeTuning.NewTrailDetail, SmokeTuning.NewTrailPowder ? SmokeTuning.NewTrailDust : 0f, SmokeTuning.NewTrailRoundLobes));
            mb.SetVector("_TrAerial", new Vector4(1f / Mathf.Max(SmokeTuning.AerialDistance, 500f), (SmokeTuning.NewTrailAerial ? SmokeTuning.AerialStrength : 0f), 0f, 0f));
            mb.SetColor("_TrHaze", Color.Lerp(lookSky, lookGnd, 0.15f) * 1.15f);
            mb.SetVector("_TrLook4", new Vector4(SmokeTuning.NewTrailShadowExt, 1f, SmokeTuning.NewTrailLightReach, SmokeTuning.NewTrailSkyOcc));
            // the flame of the engine lights the smoke next to the nozzle, while the engine burns: at the NOZZLE (the start of the root of a live head) - the
            // emission point can be hundreds of metres behind it in thin air, and the smoke at the nozzle was out of reach of the light (black)
            {
                Node nl = nodes[nodes.Count - 1];
                Vector3 fp = (Vector3)(nl.rel - anchor);
                foreach (KeyValuePair<int, Head> kv in heads)
                    if (kv.Value.pts != null && kv.Value.n > 0 && now - kv.Value.t < 0.3f) { fp = (Vector3)(kv.Value.pts[0] - anchor); break; }
                float burning = Mathf.Exp(-Mathf.Max(0f, now - nl.t) / 0.5f) * SmokeTuning.NewTrailEngineLight;
                float fr = 70f + 6f * nl.r0;
                mb.SetVector("_TrFlame", new Vector4(fp.x, fp.y, fp.z, fr));
                mb.SetVector("_TrFlameCol", new Vector4(1.0f * burning, 0.62f * burning, 0.30f * burning, 0f));
            }
            // the cloud shadow from EVE's light volume (set globally by EVE when its volumetric clouds run)
            {
                bool eveLv = SmokeTuning.NewTrailCloudShadow && Shader.GetGlobalTexture("directLightVolume") != null;
                Vector3 pc = body.position;
                mb.SetVector("_TrEve", new Vector4(eveLv ? SmokeTuning.NewTrailCloudShadowStrength : 0f, pc.x, pc.y, pc.z));
                int mv = SmokeTuning.NewTrailEveMapVariant;
                mb.SetVector("_TrEveMap", new Vector4(mv & 1, (mv >> 1) & 1, (mv >> 2) & 1, (mv >> 3) & 1));
            }
            mb.SetVector("_TrKsa", new Vector4(SmokeTuning.NewTrailPhysicalLight ? 1f : 0f, SmokeTuning.NewTrailSunBright, SmokeTuning.NewTrailSkyBright, SmokeTuning.NewTrailDarkEdge));
            mb.SetColor("_TrGlowCol", glowCol * SmokeTuning.NewTrailGlow);
            mb.SetVector("_TrLook3", new Vector4(Inflate, SmokeTuning.NewTrailBillow, SmokeTuning.NewTrailBillowFreq, SmokeTuning.NewTrailLightGain));
            mb.SetVector("_TrLook2", new Vector4(SmokeTuning.NewTrailSun, SmokeTuning.NewTrailAmbient, SmokeTuning.NewTrailDarkEdge, 1f));
            mb.SetColor("_TrAmbSky", lookSky); mb.SetColor("_TrAmbGnd", lookGnd); mb.SetColor("_TrSunC", lookSun); mb.SetColor("_TrShadowC", lookShadow);
            mb.SetColor("_TrTint", Color.white);
            mr.SetPropertyBlock(mb);
            SmokeRenderRegistry.SetActive(mr, true);
        }

        private static Color Neutral(Color c, float keep)
        {
            float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            return new Color(Mathf.Lerp(l, c.r, keep), Mathf.Lerp(l, c.g, keep), Mathf.Lerp(l, c.b, keep), c.a);
        }

        // the bounding sphere of segments [i0, i1): the middle of the end points and the farthest reach of a capsule
        private static Vector4 Bound(Vector4[] seg, int i0, int i1)
        {
            if (i1 <= i0) return new Vector4(0, 0, 0, 0);
            Vector3 c = Vector3.zero; int cnt = 0;
            for (int s = i0; s < i1; s++) { c += (Vector3)seg[s * 4]; c += (Vector3)seg[s * 4 + 1]; cnt += 2; }
            c /= cnt;
            float r = 0f;
            for (int s = i0; s < i1; s++)
            {
                r = Mathf.Max(r, Vector3.Distance(c, (Vector3)seg[s * 4]) + seg[s * 4].w * (Inflate + 0.05f));      // the shader inflates the capsules: the bound must hold the inflated ones (else their outer shell was culled: holes)
                r = Mathf.Max(r, Vector3.Distance(c, (Vector3)seg[s * 4 + 1]) + seg[s * 4 + 1].w * (Inflate + 0.05f));
            }
            return new Vector4(c.x, c.y, c.z, r);
        }
    }

    // ticks the trail once per frame (after the cameras have moved)
    public class CapsuleTrailDriver : MonoBehaviour
    {
        private bool hidden;
        private void LateUpdate()
        {
            if (!ModSettings.Enabled) { if (!hidden) { CapsuleTrail.Instance.HideWhenOff(); hidden = true; } return; }
            hidden = false;
            CapsuleTrail.Instance.Tick();
        }
        private void OnDestroy() { }
    }
}
