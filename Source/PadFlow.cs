using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // PAD FLOW: where the exhaust of an engine actually goes once it meets the ground, found with rays.
    //
    //   1. Primary rays: from the nozzle along the exhaust. Where they hit is the IMPACT (the red patch in the sketch): a
    //      point, the surface normal there and how wide the patch is.
    //   2. Secondary rays: from the impact, a fan of rays along the surface (the sideways yellow lines). Each one walks
    //      outward in short steps, following the floor (a ray down at every step), and turns up a wall when it meets one
    //      (a trench side), then over the top and on. Flat pad: they radiate in every direction. Flame trench: the ones
    //      that run into the walls climb them, the ones along the trench leave through its open ends. A V-shaped
    //      deflector splits the flow into its two channels with no guessing about "the trench axis".
    //   3. The result is a set of PATHS (polylines over the surface). Puffs that hit the pad are meant to be guided along
    //      the nearest path, with density, steam and glow falling off with the distance from the impact.
    //
    // This file is the first stage: it computes the field (a few rays per frame, so no spike) and can draw it. It is not yet
    // wired into the puff physics; PadFlowRays is OFF by default.
    public sealed class PadFlowPath
    {
        public readonly List<Vector3> pts = new List<Vector3>(64);
        public readonly List<bool> wall = new List<bool>(64);   // true where the path runs up / over a wall
        public float length;
        public bool climbed;                 // the flow had to climb a wall: it carries less mass and has lost energy
    }

    public sealed class PadFlowField
    {
        public bool valid;
        public Vector3 impact, normal, up;
        public float radius;                 // half-width of the impact patch
        public float time;
        public PadFlowPath[] paths;
        public int built;                    // number of secondary rays finished
    }

#if !EXPLOSIONS_ONLY
    [KSPAddon(KSPAddon.Startup.Flight, false)]
#endif
    public class PadFlow : MonoBehaviour
    {
        private class Entry
        {
            public Vector3 origin, dir, up; public float nozzleR;
            public float lastReport;
            public PadFlowField field = new PadFlowField();
            public PadFlowField building;
            public float lastStart = -999f;
            public GameObject debugRoot;
            public readonly List<LineRenderer> lines = new List<LineRenderer>();
        }

        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private static readonly List<int> dead = new List<int>();
        private static readonly RaycastHit[] hits = new RaycastHit[24];
        private static Material lineMat;

        // The current field of a group (null if none).
        public static PadFlowField Get(int key)
        {
            Entry e;
            return entries.TryGetValue(key, out e) && e.field.valid ? e.field : null;
        }

        public static void Report(int key, Vector3 origin, Vector3 dir, float nozzleRadius, Vector3 up)
        {
            if (!ModSettings.Enabled || (!SmokeTuning.PadFlowRays && !SmokeTuning.PadCloud)) return;
            Entry e;
            if (!entries.TryGetValue(key, out e)) { e = new Entry(); entries[key] = e; }
            e.origin = origin; e.dir = dir.normalized; e.nozzleR = nozzleRadius; e.up = up.normalized;
            e.lastReport = Time.time;
        }

        // A collider counts as a surface only if something is drawn with it: an enabled renderer on the same object or
        // below it (invisible lids and helper colliders have none). Terrain quads are drawn, so they pass. Cached per collider.
        private static readonly Dictionary<Collider, bool> visibleCache = new Dictionary<Collider, bool>();
        private static bool IsVisibleSurface(Collider c)
        {
            bool v;
            if (visibleCache.TryGetValue(c, out v)) return v;
            Renderer r = c.GetComponent<Renderer>();
            if (r == null) r = c.GetComponentInChildren<Renderer>();
            v = r != null && r.enabled && r.gameObject.activeInHierarchy;
            if (visibleCache.Count > 4000) visibleCache.Clear();
            visibleCache[c] = v;
            return v;
        }

        // nearest hit that is scenery or terrain, not a part of any vessel
        private static bool Cast(Vector3 o, Vector3 d, float dist, out RaycastHit best)
        {
            best = default(RaycastHit);
            int n = Physics.RaycastNonAlloc(o, d, hits, dist, SmokeVolumeGroup.GetSceneryCollisionMaskPublic(), QueryTriggerInteraction.Ignore);
            float bd = float.MaxValue; bool found = false;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.GetComponentInParent<Part>() != null) continue;
                if (SmokeTuning.PadFlowVisibleOnly && !IsVisibleSurface(c)) continue;
                if (hits[i].distance > 0.01f && hits[i].distance < bd) { bd = hits[i].distance; best = hits[i]; found = true; }
            }
            return found;
        }

        private void Update()
        {
            float now = Time.time;
            dead.Clear();
            foreach (KeyValuePair<int, Entry> kv in entries)
            {
                Entry e = kv.Value;
                if (!ModSettings.Enabled || (!SmokeTuning.PadFlowRays && !SmokeTuning.PadCloud) || now - e.lastReport > 0.6f) { dead.Add(kv.Key); continue; }
                Step(e, now);
                DrawDebug(e);
            }
            for (int i = 0; i < dead.Count; i++)
            {
                Entry e = entries[dead[i]];
                if (e.debugRoot != null) Destroy(e.debugRoot);
                entries.Remove(dead[i]);
            }
        }

        private void OnDestroy()
        {
            foreach (Entry e in entries.Values) if (e.debugRoot != null) Destroy(e.debugRoot);
            entries.Clear();
        }

        // ------------------------------------------------------------------ building the field, a few rays per frame
        private void Step(Entry e, float now)
        {
            int rays = Mathf.Clamp(SmokeTuning.PadFlowRayCount, 6, 64);
            if (e.building == null && now - e.lastStart > 0.5f)
            {
                e.lastStart = now;
                PadFlowField f = BeginField(e, rays);
                if (f != null) e.building = f;
                else e.field = new PadFlowField();      // nothing below: no impact
            }
            if (e.building == null) return;

            const int PerFrame = 3;
            for (int k = 0; k < PerFrame && e.building.built < rays; k++)
            {
                int i = e.building.built++;
                float ang = i * 360f / rays;
                Vector3 tangent = Vector3.ProjectOnPlane(Mathf.Abs(Vector3.Dot(e.up, Vector3.forward)) < 0.9f ? Vector3.forward : Vector3.right, e.building.normal).normalized;
                Vector3 d = Quaternion.AngleAxis(ang, e.building.normal) * tangent;
                PadFlowPath p = new PadFlowPath();
                March(p, e.building.impact, d, e.building.normal, e.up, Mathf.Max(SmokeTuning.PadFlowStep, 1f), Mathf.Max(SmokeTuning.PadFlowMaxLength, 20f));
                e.building.paths[i] = p;
            }
            if (e.building.built >= rays)
            {
                e.building.time = now;
                e.field = e.building;
                e.building = null;
            }
        }

        // primary rays: the centre one plus a ring; the impact is the centre hit, the normal the average of the ring
        private static PadFlowField BeginField(Entry e, int rays)
        {
            RaycastHit centre;
            if (!Cast(e.origin, e.dir, 600f, out centre)) return null;
            PadFlowField f = new PadFlowField { valid = true, impact = centre.point, up = e.up, paths = new PadFlowPath[rays] };
            Vector3 nSum = centre.normal; float rMax = e.nozzleR;
            Vector3 side = Vector3.Cross(e.dir, Mathf.Abs(Vector3.Dot(e.dir, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward).normalized;
            float tilt = Mathf.Atan2(Mathf.Max(e.nozzleR * 1.5f, 1f), Mathf.Max(centre.distance, 1f)) * Mathf.Rad2Deg;
            for (int i = 0; i < 6; i++)
            {
                Vector3 d = Quaternion.AngleAxis(i * 60f, e.dir) * (Quaternion.AngleAxis(tilt, side) * e.dir);
                RaycastHit h;
                if (!Cast(e.origin, d, 600f, out h)) continue;
                nSum += h.normal;
                rMax = Mathf.Max(rMax, Vector3.Distance(h.point, centre.point));
            }
            f.normal = nSum.normalized;
            f.radius = rMax;
            if (SmokeTuning.PadFlowDebug && Time.time - lastProbe > 5f) { lastProbe = Time.time; Probe(f); }
            return f;
        }

        // DIAGNOSTIC: every collider under the impact (a ray straight down from 40 m above it, and the same 25 m to each side
        // of the impact), so odd pad collisions (a lid over the trench, colliders that sit off from the model) can be read in the log.
        private static float lastProbe;
        private static void Probe(PadFlowField f)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[PlumeTrails] pad probe: impact=" + f.impact.ToString("F1") + " normal=" + f.normal.ToString("F2"));
            Vector3 side = Vector3.Cross(f.up, Vector3.forward).normalized;
            Vector3[] offs = { Vector3.zero, side * 25f, -side * 25f, Vector3.Cross(f.up, side).normalized * 25f };
            for (int o = 0; o < offs.Length; o++)
            {
                Vector3 from = f.impact + offs[o] + f.up * 40f;
                int n = Physics.RaycastNonAlloc(from, -f.up, hits, 300f, ~0, QueryTriggerInteraction.Collide);
                sb.Append("\n  ray" + o + " from " + from.ToString("F0") + ": " + n + " hits");
                for (int i = 0; i < n; i++)
                {
                    Collider c = hits[i].collider;
                    if (c == null) continue;
                    string chain = c.name; Transform t = c.transform.parent;
                    for (int k = 0; k < 4 && t != null; k++, t = t.parent) chain += " < " + t.name;
                    sb.Append("\n    d=" + hits[i].distance.ToString("F1") + " layer=" + LayerMask.LayerToName(c.gameObject.layer)
                        + " " + c.GetType().Name + (c is MeshCollider ? (((MeshCollider)c).convex ? "(convex)" : "") : "")
                        + (c.isTrigger ? " TRIGGER" : "") + " part=" + (c.GetComponentInParent<Part>() != null) + " " + chain);
                }
            }
            Debug.Log(sb.ToString());
        }

        // one secondary ray: walk outward over the surface, climbing walls
        private static void March(PadFlowPath path, Vector3 start, Vector3 outward, Vector3 n, Vector3 up, float step, float maxLen)
        {
            Vector3 pos = start + n * 0.6f;
            path.pts.Add(pos); path.wall.Add(false);
            Vector3 flat = Vector3.ProjectOnPlane(outward, up).normalized;     // the outward direction we return to after a wall
            if (flat.sqrMagnitude < 0.5f) flat = outward;
            Vector3 d = Vector3.ProjectOnPlane(outward, n).normalized;
            bool climbing = false; Vector3 wallN = up; int climbSteps = 0;
            float len = 0f;

            while (len < maxLen && path.pts.Count < 160)
            {
                RaycastHit hit;
                if (!climbing)
                {
                    if (Cast(pos, d, step + 0.5f, out hit))
                    {
                        Vector3 hn = hit.normal;
                        pos = hit.point + hn * 0.6f; len += hit.distance;
                        if (Vector3.Dot(hn, up) < 0.6f)
                        {
                            // a wall: the flow turns up it
                            climbing = true; wallN = hn; climbSteps = 0;
                            path.pts.Add(pos); path.wall.Add(true);
                            continue;
                        }
                        d = Vector3.ProjectOnPlane(d, hn).normalized;       // a gentle slope: follow it
                        path.pts.Add(pos); path.wall.Add(false);
                        continue;
                    }
                    // open ahead: step on and find the floor under the new spot
                    Vector3 next = pos + d * step;
                    if (Cast(next + up * 3f, -up, 40f, out hit))
                    {
                        pos = hit.point + hit.normal * 0.6f; len += step;
                        d = Vector3.ProjectOnPlane(d, hit.normal).normalized;
                        if (d.sqrMagnitude < 0.5f) break;
                        path.pts.Add(pos); path.wall.Add(false);
                    }
                    else break;                                             // a ledge: the flow leaves as a free jet
                }
                else
                {
                    // CLIMBING: small steps, re-attached to the wall at every step (a steep slope is not vertical, so a long step
                    // leaves the wall behind and the "still a wall?" test fails half way up)
                    Vector3 climb = Vector3.ProjectOnPlane(up, wallN);
                    Vector3 cd = climb.sqrMagnitude > 1e-4f ? climb.normalized : up;
                    float cstep = Mathf.Min(step, 1.5f);
                    pos += cd * cstep; len += cstep; climbSteps++;
                    RaycastHit wh;
                    bool stillWall = false;
                    if (Cast(pos, -wallN, 4f, out wh) && Vector3.Dot(wh.normal, up) < 0.6f)
                    {
                        stillWall = true; wallN = wh.normal; pos = wh.point + wh.normal * 0.6f;
                    }
                    path.pts.Add(pos); path.wall.Add(true);
                    if (!stillWall || climbSteps > 60)
                    {
                        // over the top: onto the surface a little way beyond the edge, and on in the outward direction
                        climbing = false;
                        path.climbed = true;
                        Vector3 over = pos + flat * (step * 0.6f);
                        RaycastHit fh;
                        if (Cast(over + up * 30f, -up, 80f, out fh)) over = fh.point + fh.normal * 0.6f;
                        pos = over; d = flat;
                        path.pts.Add(pos); path.wall.Add(false);
                    }
                }
            }
            path.length = len;
        }

        // ------------------------------------------------------------------ debug drawing
        private void DrawDebug(Entry e)
        {
            if (!SmokeTuning.PadFlowDebug)
            {
                if (e.debugRoot != null) { Destroy(e.debugRoot); e.debugRoot = null; e.lines.Clear(); }
                return;
            }
            PadFlowField f = e.field;
            if (e.debugRoot == null) e.debugRoot = new GameObject("PlumeTrails.PadFlowDebug");
            if (lineMat == null)
            {
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Hidden/Internal-Colored");
                lineMat = new Material(sh);
            }
            int need = f.valid ? f.paths.Length : 0;
            while (e.lines.Count < need)
            {
                GameObject go = new GameObject("p"); go.transform.SetParent(e.debugRoot.transform, false);
                LineRenderer lr = go.AddComponent<LineRenderer>();
                lr.material = lineMat; lr.useWorldSpace = true;
                lr.startWidth = lr.endWidth = 0.35f;
                e.lines.Add(lr);
            }
            for (int i = 0; i < e.lines.Count; i++)
            {
                LineRenderer lr = e.lines[i];
                if (i >= need || f.paths[i] == null) { lr.enabled = false; continue; }
                PadFlowPath p = f.paths[i];
                lr.enabled = true;
                lr.positionCount = p.pts.Count;
                for (int k = 0; k < p.pts.Count; k++) lr.SetPosition(k, p.pts[k]);
                lr.startColor = lr.endColor = new Color(1f, 0.9f, 0.1f, 1f);
            }
        }
    }
}
