using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Real scene lights for engine flames. The smoke already gets a warm glow in its own shader; these Unity point lights
    // light everything else - the pad, the ground, the rocket's own tanks, nearby scenery - in the colour of the exhaust
    // (orange kerosene, pale blue-violet hydrogen, sooty orange solids), flickering a little the way a flame does.
    // Same reporting pattern as HeatHazeRegistry: a group reports every tick, lights nobody reported for a moment are removed.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class EngineLights : MonoBehaviour
    {
        private class Entry
        {
            public Light light;
            public Vector3 pos; public Color color; public float range, intensity;
            public float shownIntensity;
            public float lastReport;
            public float seed;
        }

        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private static readonly List<int> dead = new List<int>();

        public static void Report(int key, Vector3 pos, Color color, float range, float intensity)
        {
            if (!SmokeTuning.EngineLights) return;
            Entry e;
            if (!entries.TryGetValue(key, out e))
            {
                GameObject go = new GameObject("PlumeTrails.EngineLight");
                Light l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.shadows = LightShadows.None;
                l.bounceIntensity = 0f;
                e = new Entry { light = l, seed = Random.value * 50f };
                entries[key] = e;
            }
            e.pos = pos; e.color = color; e.range = range; e.intensity = intensity;
            e.lastReport = Time.time;
        }

        private void LateUpdate()
        {
            float now = Time.time;
            dead.Clear();
            foreach (KeyValuePair<int, Entry> kv in entries)
            {
                Entry e = kv.Value;
                if (e.light == null || now - e.lastReport > 0.3f || !SmokeTuning.EngineLights) { dead.Add(kv.Key); continue; }
                // eases in and out (ignition, throttle changes) and flickers; the flicker is two uncorrelated slow noises
                e.shownIntensity = Mathf.Lerp(e.shownIntensity, e.intensity, 1f - Mathf.Exp(-Time.deltaTime * 8f));
                float flick = 1f + 0.07f * (Mathf.PerlinNoise(now * 9f, e.seed) - 0.5f) * 2f + 0.04f * (Mathf.PerlinNoise(now * 23f, e.seed + 9f) - 0.5f) * 2f;
                e.light.transform.position = e.pos;
                e.light.color = e.color;
                e.light.range = e.range * SmokeTuning.EngineLightRange;
                e.light.intensity = e.shownIntensity * flick * SmokeTuning.EngineLightIntensity;
                e.light.enabled = e.light.intensity > 0.02f;
            }
            for (int i = 0; i < dead.Count; i++)
            {
                Entry e = entries[dead[i]];
                if (e.light != null) Destroy(e.light.gameObject);
                entries.Remove(dead[i]);
            }
        }

        private void OnDestroy()
        {
            foreach (Entry e in entries.Values) if (e.light != null) Destroy(e.light.gameObject);
            entries.Clear();
        }
    }
}
