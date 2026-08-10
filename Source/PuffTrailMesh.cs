using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Smuga w locie jako łańcuch nakładających się, raymarchowanych kłębów
    /// (PuffVolumetric.shader) zamiast ciągłego mesha rurki (stary ContrailTrailMesh) -
    /// to jest fix na "wygląda jak rurka do napoju": każdy kłąb to osobna, kłębiasta
    /// kula (FBM+Worley noise), więc sylwetka smugi nigdy nie jest idealnie gładkim
    /// walcem, a sąsiednie kłęby zachodzą na siebie tworząc ciągłość.
    ///
    /// Batchowanie i pozycje względem body.bodyTransform - dokładnie ten sam,
    /// sprawdzony mechanizm co SmokeVolumeGroup (odporność na
    /// floating origin za darmo, jeden mesh/draw call na całą grupę kłębów).
    /// </summary>
    public class PuffTrailMesh : MonoBehaviour
    {
        private struct Puff
        {
            public Vector3 localPos; // względem body.bodyTransform
            public Vector3 velocity; // w przestrzeni świata
            public float age;
            public float seed; // losowa wariacja kształtu noise'a per-kłąb
        }

        private readonly List<Puff> puffs = new List<Puff>();

        private float startRadius;
        private float maxRadius;
        private float growthSharpness;
        private float lifeTime;
        private int maxPuffs;
        private CelestialBody body;

        private Mesh mesh;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;

        private readonly List<Vector3> vertBuffer = new List<Vector3>(2048);
        private readonly List<Vector2> cornerBuffer = new List<Vector2>(2048);
        private readonly List<Vector2> radiusSeedBuffer = new List<Vector2>(2048);
        private readonly List<Color> colorBuffer = new List<Color>(2048);
        private readonly List<int> triBuffer = new List<int>(3072);

        public bool HasActivePoints => puffs.Count > 0;

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private Vector3 WorldToLocal(Vector3 worldPos) => body.bodyTransform.InverseTransformPoint(worldPos);
        private Vector3 LocalToWorld(Vector3 localPos) => body.bodyTransform.TransformPoint(localPos);

        public void Initialize(float startRadius, float maxRadius, float growthSharpness, float lifeTime, int maxPuffs, CelestialBody body)
        {
            this.startRadius = startRadius;
            this.maxRadius = maxRadius;
            this.growthSharpness = growthSharpness;
            this.lifeTime = lifeTime;
            this.maxPuffs = maxPuffs;
            this.body = body;

            mesh = new Mesh { name = "PuffTrailMesh" };
            mesh.MarkDynamic();

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            // Wierzchołki to tylko "środki" kłębów - shader rozwija je do pełnego
            // billboardu, więc realny zasięg jest większy niż surowe pozycje sugerują.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000000f);

            Shader shader = ShaderCache.PuffShader;
            if (shader != null)
            {
                meshRenderer.material = new Material(shader);
            }
            else
            {
                Debug.LogWarning("[VolumetricContrails] ShaderCache.PuffShader jest null - AssetLoader nie wczytał shadera (sprawdź KSP.log przy starcie gry).");
            }
        }

        public void AddPoint(Vector3 worldPos, Vector3 initialVelocity)
        {
            puffs.Add(new Puff
            {
                localPos = WorldToLocal(worldPos),
                velocity = initialVelocity,
                age = 0f,
                seed = Random.Range(0f, 1000f)
            });
        }

        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            if (body == null) return;

            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                Puff p = puffs[i];
                p.age += dt;

                if (p.age >= lifeTime)
                {
                    puffs.RemoveAt(i);
                    continue;
                }

                Vector3 worldPos = LocalToWorld(p.localPos);
                p.velocity = Vector3.Lerp(p.velocity, Vector3.zero, dt * 0.5f);
                Vector3 newWorldPos = worldPos + p.velocity * dt;

                if (!IsFinite(newWorldPos) || !IsFinite(p.velocity))
                {
                    Debug.LogWarning(string.Format(
                        "[VolumetricContrails] Wykryto nieprawidłowy kłąb (NaN/Infinity) - usuwam. pos={0} vel={1} age={2:F1}",
                        newWorldPos, p.velocity, p.age));
                    puffs.RemoveAt(i);
                    continue;
                }

                p.localPos = WorldToLocal(newWorldPos);
                puffs[i] = p;
            }

            if (puffs.Count > maxPuffs)
            {
                puffs.RemoveRange(0, puffs.Count - maxPuffs);
            }

            RebuildMesh();
        }

        private float RadiusForAge(float age)
        {
            float t = Mathf.Clamp01(age / lifeTime);
            float eased = 1f - Mathf.Pow(1f - t, growthSharpness);
            return Mathf.Lerp(startRadius, maxRadius, eased);
        }

        private float AlphaForAge(float age)
        {
            float t = Mathf.Clamp01(age / lifeTime);
            float fadeIn = Mathf.Clamp01(t / 0.08f);
            float fadeOut = 1f - Mathf.Clamp01((t - 0.7f) / 0.3f);
            return fadeIn * fadeOut;
        }

        private void RebuildMesh()
        {
            vertBuffer.Clear();
            cornerBuffer.Clear();
            radiusSeedBuffer.Clear();
            colorBuffer.Clear();
            triBuffer.Clear();

            if (puffs.Count == 0)
            {
                mesh.Clear();
                return;
            }

            Vector2[] corners = { new Vector2(-1,-1), new Vector2(1,-1), new Vector2(1,1), new Vector2(-1,1) };

            for (int i = 0; i < puffs.Count; i++)
            {
                Puff p = puffs[i];
                Vector3 worldPos = LocalToWorld(p.localPos);

                float radius = RadiusForAge(p.age);
                float alpha = AlphaForAge(p.age);

                int baseIndex = vertBuffer.Count;

                for (int c = 0; c < 4; c++)
                {
                    vertBuffer.Add(worldPos); // POSITION = środek świata - shader traktuje to jako gotową pozycję świata
                    cornerBuffer.Add(corners[c]);
                    radiusSeedBuffer.Add(new Vector2(radius, p.seed));
                    colorBuffer.Add(new Color(1f, 1f, 1f, alpha));
                }

                triBuffer.Add(baseIndex); triBuffer.Add(baseIndex + 1); triBuffer.Add(baseIndex + 2);
                triBuffer.Add(baseIndex); triBuffer.Add(baseIndex + 2); triBuffer.Add(baseIndex + 3);
            }

            mesh.Clear();
            mesh.SetVertices(vertBuffer);
            mesh.SetUVs(0, cornerBuffer);
            mesh.SetUVs(1, radiusSeedBuffer);
            mesh.SetColors(colorBuffer);
            mesh.SetTriangles(triBuffer, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000000f);
        }
    }
}
