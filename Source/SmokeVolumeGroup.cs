using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Dym startowy jako raymarchowany volume (SmokeVolume.shader). Fizyka/
    /// spawnowanie kłębów zostaje taka sama jak wcześniej (sprawdzona).
    ///
    /// v2: gęstość (suma miękkich, nachodzących się kul) była liczona RAZ NA KLATKĘ
    /// przez compute shader (SmokeVolumeSplat.compute) do tekstury 3D, potem
    /// rozmywana (Blur).
    ///
    /// v3: dwie osobne warstwy zamiast jednej wspólnej (osiadła chmura u ziemi vs
    /// ruchomy ogon) - ale nawet wtedy ogon (rozciągnięty, rosnący dystans) źle się
    /// mieścił w JAKIEJKOLWIEK stałej rozdzielczości tekstury: im wyżej leci rakieta,
    /// tym większy box, tym mniej tekseli na metr.
    ///
    /// v4: AKTYWNY ogon dostał analityczną funkcję gęstości (capsule chain /
    /// krzywa Catmull-Rom) zamiast tekstury - eliminowało to problem rozdzielczości,
    /// ale miało dwa fundamentalne ograniczenia: (a) koszt O(N segmentów) na KAŻDĄ
    /// próbkę gęstości (setki wywołań na piksel z light marchem) - poważny spadek
    /// FPS przy kamerze blisko/w środku, i (b) zbyt CIENKA geometria (rurka) żeby
    /// self-shadowing (light march) miał przez co realnie przejść - stąd brak
    /// widocznego cienia niezależnie od dostrajania parametrów.
    ///
    /// v5 (obecna): WARSTWA KAFELKOWA - zamiast jednej wspólnej tekstury (problem
    /// rozdzielczości z v3) albo analitycznej funkcji (problemy z v4), aktywny ogon
    /// jest dzielony na KAWAŁKI (chunki) sąsiednich kłębów, każdy pieczony do
    /// WŁASNEJ, małej tekstury 3D - dokładnie tym samym mechanizmem co już dobrze
    /// działająca warstwa osiadła (ten sam kod, ten sam kernel compute). Każdy
    /// kafelek ma stały, kompaktowy rozmiar fizyczny (nie rośnie z długością
    /// ogona), więc nie ma problemu rozdzielczości v3, a sampling tekstury jest
    /// tani O(1) na krok marchingu niezależnie od długości ogona - nie ma problemu
    /// wydajności v4. Kafelki nachodzą się nieco na granicach (overlap) dla
    /// płynnego przejścia bez szwów.
    /// </summary>
    public class SmokeVolumeGroup : MonoBehaviour
    {
        private const int MaxTextureLayerPuffs = 256; // musi się zgadzać z MAX_PUFFS w SmokeVolumeSplat.compute

        // Osiadła chmura (u ziemi) jest kompaktowa - sześcienna rozdzielczość
        // wystarcza.
        private static readonly Vector3Int SettledTexResolution = new Vector3Int(64, 64, 64);

        // Kafelki aktywnego ogona - mniejsza rozdzielczość niż osiadła warstwa, bo
        // jest ich potencjalnie wiele naraz (jeden kafelek na ~14 kłębów), a każdy
        // pokrywa dużo mniejszy fizyczny obszar niż cała osiadła chmura.
        private static readonly Vector3Int ActiveTileResolution = new Vector3Int(28, 28, 28);
        private const int ActiveTilePuffChunkSize = 14;
        // Ile sąsiednich punktów z KAŻDEJ strony chunku dolicza się dodatkowo przy
        // pieczeniu danego kafelka - bez tego sąsiednie kafelki nie "widziałyby"
        // swoich brzegowych kłębów nawzajem, dając widoczny szew/przerwę na styku.
        private const int ActiveTileOverlap = 4;
        private const int MaxActiveTiles = 48;

        private const float BlendRadiusMultiplier = 1.4f; // jak mocno kule "rozciągają się" poza swój promień przy zlewaniu

        // Musi się zgadzać z domyślną wartością _ReferenceRadius w SmokeVolumeMat -
        // używane TYLKO do wyliczenia _TileRadiusRatio (skalowanie szumu rzeźbiącego
        // per-kafelek, patrz BakeActiveTile), nie zmienia samego materiału.
        private const float ShaderReferenceRadius = 18f;

        // Jaka część budżetu kłębów (fizyka/spawnowanie) jest zarezerwowana dla
        // osiadłych - bez tego gęste spawnowanie aktywnych przy dużej prędkości
        // wypychało z jednej wspólnej puli starsze, osiadłe kłęby dużo szybciej niż
        // ich naturalny czas życia.
        private const float SettledFraction = 0.6f;

        private struct Puff
        {
            public Vector3 localPos; // względem body.bodyTransform
            public Vector3 velocity; // w przestrzeni świata
            public float age;
            public float sizeMultiplier;
            public bool settled;
            public bool markedForRemoval; // patrz Tick()/EnforcePuffBudget - usuwanie zbiorcze przez RemoveAll zamiast wielu RemoveAt
        }

        private readonly List<Puff> puffs = new List<Puff>();

        private float startSize;
        private float maxSize;
        private float growthSharpness;
        private float lifeTime;
        private float settledLifeTime;
        private int maxPuffs;

        private float buoyancySpeed;
        private Vector3 windDrift;
        private CelestialBody body;
        private float fadeStartAltitude;
        private float fadeEndAltitude;
        private const float GroundBounceDamping = 0.35f;
        // Było 0.35 (stała czasowa ~2.9s) - prędkość wyrzutu z silnika (ejectionSpeed
        // w LaunchSmokeController) tłumiła się do dryfu (wiatr+buoyancy) zanim
        // ruch zdążył być widoczny, zwłaszcza tuż po starcie gdy sama rakieta
        // ledwo się rusza (niskie TWR) - kłąb wyglądał jak "przyklejony" do
        // silnika zamiast widocznie wystrzelony. Wolniejsza konwergencja (~4.5s)
        // zostawia wyrzutowej prędkości więcej czasu, zanim ustąpi dryfowi.
        private const float VelocityConvergeRate = 0.22f;

        // Margines boxa: promień*BoxRadiusMarginMultiplier PLUS stały zapas w metrach
        // na domain warp w shaderze (_VortexStrength + _SilhouetteWarpStrength
        // przesuwają punkt próbki przed odczytem tekstury - box musi to pokrywać,
        // inaczej wypchnięta część zostaje ucięta na granicy = płaskie krawędzie).
        private const float BoxRadiusMarginMultiplier = 1.6f;
        private const float BoxFixedWarpMargin = 15f;

        private MeshRenderer settledRenderer;
        private MaterialPropertyBlock settledPropertyBlock;
        private RenderTexture settledDensityTex;
        private RenderTexture settledBlurredTex;
        private readonly Vector4[] settledCentersBuffer = new Vector4[MaxTextureLayerPuffs];
        private readonly float[] settledRadiiBuffer = new float[MaxTextureLayerPuffs];

        private class ActiveTile
        {
            public GameObject obj;
            public MeshRenderer renderer;
            public MaterialPropertyBlock propertyBlock;
            public RenderTexture densityTex;
            public RenderTexture blurredTex;
        }

        // WYCOFANE: throttling pieczenia kafelków (odświeżanie co N klatek zamiast
        // co klatkę, "temporal upscaling" inspirowany EVE). Przyczyna: tileIndex
        // NIE jest stabilnym ID fizycznego fragmentu ogona w czasie - granice
        // chunków przesuwają się co klatkę wraz z nowymi punktami. Throttlowany
        // (zamrożony) kafelek zostawał na STAREJ pozycji, podczas gdy sąsiedni,
        // świeżo odpieczony kafelek już "poszedł dalej" wraz z przesuniętą
        // granicą chunku - dawało to okresowe, widoczne dziury w ogonie. Do
        // ponownego rozważenia dopiero z ID kafelka stabilnym względem
        // fizycznego fragmentu ogona, nie względem pozycji w liście.

        private readonly List<ActiveTile> activeTiles = new List<ActiveTile>();
        private readonly Vector4[] tileCentersBuffer = new Vector4[MaxTextureLayerPuffs];
        private readonly float[] tileRadiiBuffer = new float[MaxTextureLayerPuffs];

        // Uporządkowana (najstarszy -> najnowszy) lista pozycji/promieni aktywnych
        // (nieosiadłych) kłębów + żywy czubek na końcu - budowana raz na klatkę,
        // potem dzielona na kafelki. Reużywane listy (Clear, nie nowa alokacja
        // każdej klatki).
        private readonly List<Vector3> activeOrderedPos = new List<Vector3>();
        private readonly List<float> activeOrderedRadius = new List<float>();

        private int splatKernel = -1;
        private int blurKernel = -1;

        public bool HasActivePuffs => puffs.Count > 0;

        // Liczba żywych instancji (statków z aktywnym dymem) - LaunchpadOcclusionExcluder
        // sprawdza to zamiast renderować swoją kamerę głębi co klatkę przez CAŁY lot
        // (łącznie z godzinami na orbicie, gdzie dym dawno wygasł).
        private static int activeInstanceCount;
        public static bool AnyActive => activeInstanceCount > 0;

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private Vector3 WorldToLocal(Vector3 worldPos) => body.bodyTransform.InverseTransformPoint(worldPos);
        private Vector3 LocalToWorld(Vector3 localPos) => body.bodyTransform.TransformPoint(localPos);

        // Object.Destroy(collider) usuwa go dopiero na KONIEC klatki, nie od razu -
        // przez tę jedną klatkę świeżo utworzony prymityw (duży sześcian-kafelek,
        // często tworzony DOKŁADNIE tam gdzie jest rakieta) ma aktywny collider,
        // co potrafi wywołać gwałtowną kolizję/wybuch fizyki. enabled=false
        // działa NATYCHMIAST (synchronicznie).
        private static void RemoveColliderImmediate(GameObject obj)
        {
            Collider collider = obj.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Object.Destroy(collider);
            }
        }

        public void Initialize(
            float startSize, float maxSize, float growthSharpness, float lifeTime, float settledLifeTime, int maxPuffs,
            float buoyancySpeed, Vector3 windDrift, CelestialBody body,
            float fadeStartAltitude, float fadeEndAltitude)
        {
            this.startSize = startSize;
            this.maxSize = maxSize;
            this.growthSharpness = growthSharpness;
            this.lifeTime = lifeTime;
            this.settledLifeTime = settledLifeTime;
            this.maxPuffs = maxPuffs;
            this.buoyancySpeed = buoyancySpeed;
            this.windDrift = windDrift;
            this.body = body;
            this.fadeStartAltitude = fadeStartAltitude;
            this.fadeEndAltitude = fadeEndAltitude;

            activeInstanceCount++;

            GameObject settledObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            settledObj.name = "SettledCloud";
            settledObj.transform.SetParent(transform, false);
            RemoveColliderImmediate(settledObj);
            settledRenderer = settledObj.GetComponent<MeshRenderer>();
            settledRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            settledRenderer.receiveShadows = false;
            settledRenderer.enabled = false;
            settledPropertyBlock = new MaterialPropertyBlock();

            if (ShaderCache.SmokeVolumeShader != null)
            {
                settledRenderer.material = new Material(ShaderCache.SmokeVolumeShader);
            }
            else
            {
                Debug.LogWarning("[VolumetricContrails] ShaderCache.SmokeVolumeShader jest null przy tworzeniu SmokeVolumeGroup.");
            }

            settledDensityTex = CreateDensityTexture(SettledTexResolution);
            settledBlurredTex = CreateDensityTexture(SettledTexResolution);

            if (ShaderCache.SmokeVolumeSplatCompute != null)
            {
                splatKernel = ShaderCache.SmokeVolumeSplatCompute.FindKernel("Splat");
                blurKernel = ShaderCache.SmokeVolumeSplatCompute.FindKernel("Blur");
            }
            else
            {
                Debug.LogWarning("[VolumetricContrails] ShaderCache.SmokeVolumeSplatCompute jest null przy tworzeniu SmokeVolumeGroup.");
            }
        }

        private static RenderTexture CreateDensityTexture(Vector3Int resolution)
        {
            RenderTexture tex = new RenderTexture(resolution.x, resolution.y, 0, RenderTextureFormat.RHalf)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = resolution.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            tex.Create();
            return tex;
        }

        // Losowy 3D offset pozycji przy spawnie (nie tylko wzdłuż linii silnika) -
        // bez tego kłęby układają się w idealnie prostą linię z fakturą na
        // powierzchni.
        private const float PositionJitterFraction = 0.25f;

        // "Żywy czubek" ogona - śledzi aktualną pozycję silnika co klatkę FIZYKI (nie
        // tylko co spawn nowego kłębu, który leci co ~0.2s). Bez tego, między
        // spawnami, ostatni skomitowany kłąb po prostu dryfuje sam, a silnik ucieka
        // mu do przodu - kolejny spawn nagle "dogania" tę lukę, co wygląda jak
        // pyknięcie zamiast płynnego wydzielania. Ten punkt NIE jest zapisywany do
        // puffs (nie starzeje się, nie ma fizyki) - tylko dokładany na końcu
        // uporządkowanej listy przy pieczeniu kafelków, więc kafelek łączący się z
        // silnikiem zawsze jest dokładnie tam, gdzie silnik.
        private Vector3 liveTipPos;
        private float liveTipRadius;
        private bool hasLiveTip;
        private float lastActiveDebugLogTime = -999f;

        public void SetLiveTip(Vector3 worldPos, float radius)
        {
            liveTipPos = worldPos;
            liveTipRadius = radius;
            hasLiveTip = true;
        }

        public void ClearLiveTip()
        {
            hasLiveTip = false;
        }

        public void AddPuff(Vector3 worldPos, Vector3 initialVelocity, float sizeScale = 1f)
        {
            Vector3 jitter = Random.insideUnitSphere * (startSize * PositionJitterFraction);
            puffs.Add(new Puff
            {
                localPos = WorldToLocal(worldPos + jitter),
                velocity = initialVelocity,
                age = 0f,
                // Węższy zakres niż wcześniej (było 0.65-1.4) - duży losowy rozrzut
                // dobrze wyglądał na grubej podstawie kalafiora, ale na smukłym ogonie
                // tworzył widoczne "koraliki"/bąble zamiast płynnego zwężenia.
                sizeMultiplier = Random.Range(0.9f, 1.1f) * sizeScale
            });
        }

        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            if (body == null) return;

            // Oznaczamy do usunięcia (markedForRemoval) i usuwamy WSZYSTKIE naraz
            // przez RemoveAll PO pętli, zamiast wołać RemoveAt() dla każdego z
            // osobna - RemoveAt(i) przesuwa CAŁĄ resztę listy (O(N-i)), więc przy
            // budżecie rzędu tysięcy kłębów i wielu usunięciach na klatkę sumaryczny
            // koszt potrafił być O(N²). RemoveAll robi to w jednym przebiegu O(N).
            bool anyRemoved = false;

            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                Puff p = puffs[i];
                p.age += dt;

                // Osiadłe kłęby (chmura u ziemi) żyją dużo dłużej niż ogon w locie -
                // inaczej rakieta przestaje dokładać nowych kłębów do bazy już ok.
                // 10-20s po starcie (powyżej ~500m kłęby nie mogą już "osiąść"), a
                // przy wspólnym 25s lifeTime chmura u ziemi zaczynała gasnąć zanim
                // zdążyła urosnąć do czegoś trwałego.
                float effectiveLifeTime = p.settled ? settledLifeTime : lifeTime;
                if (p.age >= effectiveLifeTime)
                {
                    p.markedForRemoval = true;
                    puffs[i] = p;
                    anyRemoved = true;
                    continue;
                }

                Vector3 worldPos = LocalToWorld(p.localPos);
                Vector3 up = (worldPos - body.position).normalized;

                // Osiadłe kłęby prawie nie unoszą się dalej (tylko odrobina buoyancy,
                // żeby nie wyglądały całkiem martwo) - inaczej przy wydłużonym
                // settledLifeTime pudełko chmury u ziemi rośnie w górę przez całe
                // 100s, a stała rozdzielczość tekstury rozmywa tę samą liczbę
                // kłębów na coraz większej objętości.
                Vector3 target = p.settled
                    ? windDrift * 0.5f + up * (buoyancySpeed * 0.1f)
                    : windDrift + up * buoyancySpeed;
                p.velocity = Vector3.Lerp(p.velocity, target, dt * VelocityConvergeRate);
                Vector3 newWorldPos = worldPos + p.velocity * dt;

                TryBounceOffGround(ref newWorldPos, ref p.velocity, up, ref p.settled, p.age, dt);

                if (!IsFinite(newWorldPos) || !IsFinite(p.velocity))
                {
                    Debug.LogWarning(string.Format(
                        "[VolumetricContrails][Smoke] Wykryto nieprawidłowy kłąb (NaN/Infinity) - usuwam. pos={0} vel={1} age={2:F1}",
                        newWorldPos, p.velocity, p.age));
                    p.markedForRemoval = true;
                    puffs[i] = p;
                    anyRemoved = true;
                    continue;
                }

                p.localPos = WorldToLocal(newWorldPos);
                puffs[i] = p;
            }

            if (anyRemoved)
            {
                puffs.RemoveAll(p => p.markedForRemoval);
            }

            EnforcePuffBudget();

            UpdateSettledLayer();
            UpdateActiveTiles();
        }

        private void EnforcePuffBudget()
        {
            int maxSettled = Mathf.Min(Mathf.RoundToInt(maxPuffs * SettledFraction), MaxTextureLayerPuffs);
            int maxActive = maxPuffs - maxSettled;

            int settledCount = 0;
            for (int i = 0; i < puffs.Count; i++)
            {
                if (puffs[i].settled) settledCount++;
            }
            int activeCount = puffs.Count - settledCount;

            int settledOver = Mathf.Max(0, settledCount - maxSettled);
            int activeOver = Mathf.Max(0, activeCount - maxActive);

            if (settledOver <= 0 && activeOver <= 0) return;

            // Jeden przebieg RemoveAll zamiast wielu RemoveAt (patrz komentarz w
            // Tick()) - te same najstarsze-pierwsze semantyki (puffs jest
            // uporządkowane od najstarszego), tylko bez powtarzanego przesuwania
            // reszty listy przy każdym pojedynczym usunięciu.
            int settledToRemove = settledOver;
            int activeToRemove = activeOver;
            puffs.RemoveAll(p =>
            {
                if (p.settled && settledToRemove > 0) { settledToRemove--; return true; }
                if (!p.settled && activeToRemove > 0) { activeToRemove--; return true; }
                return false;
            });
        }

        // Ile z pionowej prędkości uderzenia zamienia się w rozlanie na boki (billowing) -
        // tak jak prawdziwy dym startowy, który po uderzeniu w płytę nie odbija się
        // prosto w górę, tylko rozchodzi się promieniście wokół miejsca uderzenia.
        private const float GroundSpreadFactor = 1.8f;

        private void TryBounceOffGround(ref Vector3 worldPos, ref Vector3 velocity, Vector3 up, ref bool settled, float age, float dt)
        {
            if (body.pqsController == null) return;

            double altitude = body.GetAltitude(worldPos);
            if (altitude > 500.0 || altitude < -500.0) return;

            Vector3d radialDir = ((Vector3d)worldPos - body.position).normalized;
            double terrainRadius = body.pqsController.GetSurfaceHeight(radialDir);
            double groundAltitude = terrainRadius - body.Radius;

            const float buffer = 1.0f;
            if (altitude < groundAltitude + buffer)
            {
                float penetration = (float)(groundAltitude + buffer - altitude);
                worldPos += up * penetration;

                float verticalSpeed = Vector3.Dot(velocity, up);
                if (verticalSpeed < 0f)
                {
                    velocity -= up * verticalSpeed;
                    velocity += up * (-verticalSpeed * GroundBounceDamping);

                    Vector3 outward = Vector3.Cross(up, Random.onUnitSphere).normalized;
                    velocity += outward * (-verticalSpeed * GroundSpreadFactor);
                }

                settled = true;
            }
        }

        // TYMCZASOWO wyłączony wzrost promienia z wiekiem (był: Lerp(startSize,
        // maxSize, eased(age/lifeTime)) - rozszerzający się "kalafior") - user
        // chce na razie jednolitą grubość ogona, żeby ustabilizować bazę przed
        // powrotem do stylizacji rozszerzania (osobne zadanie na później).
        private float SizeForPuff(Puff p)
        {
            return startSize * p.sizeMultiplier;
        }

        // WYCOFANE: fade-in alfy na starcie - przy nowej, kafelkowej architekturze
        // dawało widoczne "bloby" (młode, jeszcze małe kłęby dostawały DODATKOWO
        // obniżoną alfę na starcie, więc świeży fragment ogona blisko silnika
        // wyglądał jak odizolowana, słabo połączona plama zamiast płynnie
        // zwężającej się reszty). Kłąb jest teraz w pełnej alfie od razu po
        // spawnie - tylko fade-out pod koniec życia zostaje.
        private float AlphaForAge(float age, bool settled)
        {
            float effectiveLifeTime = settled ? settledLifeTime : lifeTime;
            float t = Mathf.Clamp01(age / effectiveLifeTime);
            float fadeOut = 1f - Mathf.Clamp01((t - 0.65f) / 0.35f);
            return fadeOut;
        }

        private float AlphaForAltitude(Vector3 worldPos)
        {
            double altitude = body.GetAltitude(worldPos);
            if (altitude <= fadeStartAltitude) return 1f;
            if (altitude >= fadeEndAltitude) return 0f;
            return 1f - (float)((altitude - fadeStartAltitude) / (fadeEndAltitude - fadeStartAltitude));
        }

        // ---- Warstwa OSIADŁA (u ziemi) - tekstura 3D wypełniana przez compute shader ----

        private void UpdateSettledLayer()
        {
            if (puffs.Count == 0 || splatKernel < 0)
            {
                settledRenderer.enabled = false;
                return;
            }

            Vector3 boxMin = Vector3.positiveInfinity;
            Vector3 boxMax = Vector3.negativeInfinity;
            int count = 0;

            for (int i = 0; i < puffs.Count && count < MaxTextureLayerPuffs; i++)
            {
                Puff p = puffs[i];
                if (!p.settled) continue;

                Vector3 worldPos = LocalToWorld(p.localPos);

                float alpha = AlphaForAge(p.age, true) * AlphaForAltitude(worldPos);
                if (alpha <= 0.01f) continue;

                float radius = SizeForPuff(p);

                settledCentersBuffer[count] = new Vector4(worldPos.x, worldPos.y, worldPos.z, 0f);
                settledRadiiBuffer[count] = radius;
                count++;

                float boundRadius = radius * BoxRadiusMarginMultiplier + BoxFixedWarpMargin;
                boxMin = Vector3.Min(boxMin, worldPos - Vector3.one * boundRadius);
                boxMax = Vector3.Max(boxMax, worldPos + Vector3.one * boundRadius);
            }

            for (int i = count; i < MaxTextureLayerPuffs; i++)
            {
                settledRadiiBuffer[i] = 0f;
            }

            if (count == 0)
            {
                settledRenderer.enabled = false;
                return;
            }

            Vector3 boxCenter = (boxMin + boxMax) * 0.5f;
            Vector3 boxExtents = (boxMax - boxMin) * 0.5f;
            Vector3 boxSize = boxExtents * 2f;

            ComputeShader compute = ShaderCache.SmokeVolumeSplatCompute;
            compute.SetTexture(splatKernel, "_DensityTex", settledDensityTex);
            compute.SetInt("_PuffCount", count);
            compute.SetVectorArray("_PuffCenters", settledCentersBuffer);
            compute.SetFloats("_PuffRadii", settledRadiiBuffer);
            compute.SetFloat("_BlendRadiusMultiplier", BlendRadiusMultiplier);
            compute.SetVector("_BoxMin", boxMin);
            compute.SetVector("_BoxSize", boxSize);
            compute.SetInts("_Resolution", SettledTexResolution.x, SettledTexResolution.y, SettledTexResolution.z);

            int groupsX = Mathf.CeilToInt(SettledTexResolution.x / 4f);
            int groupsY = Mathf.CeilToInt(SettledTexResolution.y / 4f);
            int groupsZ = Mathf.CeilToInt(SettledTexResolution.z / 4f);
            compute.Dispatch(splatKernel, groupsX, groupsY, groupsZ);

            if (blurKernel >= 0)
            {
                compute.SetTexture(blurKernel, "_DensityTex", settledDensityTex);
                compute.SetTexture(blurKernel, "_BlurredTex", settledBlurredTex);
                compute.SetInts("_Resolution", SettledTexResolution.x, SettledTexResolution.y, SettledTexResolution.z);
                compute.Dispatch(blurKernel, groupsX, groupsY, groupsZ);
            }

            settledRenderer.enabled = true;
            settledRenderer.transform.position = boxCenter;
            settledRenderer.transform.localScale = boxExtents * 2f;

            settledPropertyBlock.Clear();
            settledPropertyBlock.SetTexture("_DensityTex", settledBlurredTex);
            settledPropertyBlock.SetVector("_BoxCenter", new Vector4(boxCenter.x, boxCenter.y, boxCenter.z, 0f));
            settledPropertyBlock.SetVector("_BoxExtents", new Vector4(boxExtents.x, boxExtents.y, boxExtents.z, 0f));
            ApplyCameraInsideMarchReduction(settledPropertyBlock, boxCenter, boxExtents);
            settledRenderer.SetPropertyBlock(settledPropertyBlock);
        }

        // ---- Warstwa AKTYWNA (ogon w locie) - kafelkowa tekstura 3D ----

        private void UpdateActiveTiles()
        {
            activeOrderedPos.Clear();
            activeOrderedRadius.Clear();

            for (int i = 0; i < puffs.Count; i++)
            {
                Puff p = puffs[i];
                if (p.settled) continue;

                Vector3 worldPos = LocalToWorld(p.localPos);
                float alpha = AlphaForAge(p.age, false) * AlphaForAltitude(worldPos);
                if (alpha <= 0.01f) continue;

                activeOrderedPos.Add(worldPos);
                activeOrderedRadius.Add(SizeForPuff(p));
            }

            if (hasLiveTip)
            {
                activeOrderedPos.Add(liveTipPos);
                activeOrderedRadius.Add(liveTipRadius);
            }

            int totalPoints = activeOrderedPos.Count;
            int usedTiles = 0;

            if (totalPoints > 0 && splatKernel >= 0)
            {
                int totalChunks = Mathf.CeilToInt((float)totalPoints / ActiveTilePuffChunkSize);
                // Jeśli chunków jest więcej niż limit kafelków, obcinamy od strony
                // NAJSTARSZEJ (blisko ziemi) - żywy czubek/połączenie z silnikiem
                // (najnowszy koniec) musi zawsze zostać pokryty.
                int startChunk = Mathf.Max(0, totalChunks - MaxActiveTiles);

                for (int chunk = startChunk; chunk < totalChunks; chunk++)
                {
                    int chunkStart = chunk * ActiveTilePuffChunkSize;
                    int rangeStart = Mathf.Max(0, chunkStart - ActiveTileOverlap);
                    int rangeEnd = Mathf.Min(totalPoints, chunkStart + ActiveTilePuffChunkSize + ActiveTileOverlap);

                    BakeActiveTile(usedTiles, rangeStart, rangeEnd);
                    usedTiles++;
                }
            }

            for (int i = usedTiles; i < activeTiles.Count; i++)
            {
                activeTiles[i].renderer.enabled = false;
            }

            if (Time.time - lastActiveDebugLogTime > 1f)
            {
                lastActiveDebugLogTime = Time.time;
                double tipAltitude = hasLiveTip ? body.GetAltitude(liveTipPos) : 0.0;
                Debug.Log(string.Format(
                    "[VolumetricContrails][ActiveDebug] alt={0:F0} totalPoints={1} tilesUsed={2}/{3}",
                    tipAltitude, totalPoints, usedTiles, activeTiles.Count));
            }
        }

        private void BakeActiveTile(int tileIndex, int rangeStart, int rangeEnd)
        {
            ActiveTile tile = GetOrCreateActiveTile(tileIndex);

            Vector3 boxMin = Vector3.positiveInfinity;
            Vector3 boxMax = Vector3.negativeInfinity;
            int count = 0;
            float radiusSum = 0f;

            for (int i = rangeStart; i < rangeEnd && count < MaxTextureLayerPuffs; i++)
            {
                float radius = activeOrderedRadius[i];
                if (radius <= 0f) continue;
                Vector3 pos = activeOrderedPos[i];

                tileCentersBuffer[count] = new Vector4(pos.x, pos.y, pos.z, 0f);
                tileRadiiBuffer[count] = radius;
                count++;
                radiusSum += radius;

                // Stały margines (BoxFixedWarpMargin) był dobrany pod warp GRUBYCH,
                // referencyjnych kłębów - na cienkim, młodym kłębie (mały promień)
                // sam warp jest teraz proporcjonalnie mały (patrz _TileRadiusRatio w
                // shaderze), więc pełny 15m margines tylko zjadał budżet tekseli
                // stałej rozdzielczości 28^3 kosztem samego kształtu dymu (kłąb
                // wypadał na 1-2 tekslach = widoczna "kulka" zamiast płynnej gęstości).
                // Skalujemy margines tym samym stosunkiem promień/referencja.
                float warpMarginScale = Mathf.Clamp(radius / ShaderReferenceRadius, 0.15f, 1f);
                float boundRadius = radius * BoxRadiusMarginMultiplier + BoxFixedWarpMargin * warpMarginScale;
                boxMin = Vector3.Min(boxMin, pos - Vector3.one * boundRadius);
                boxMax = Vector3.Max(boxMax, pos + Vector3.one * boundRadius);
            }

            for (int i = count; i < MaxTextureLayerPuffs; i++)
            {
                tileRadiiBuffer[i] = 0f;
            }

            if (count == 0)
            {
                tile.renderer.enabled = false;
                return;
            }

            Vector3 boxCenter = (boxMin + boxMax) * 0.5f;
            Vector3 boxExtents = (boxMax - boxMin) * 0.5f;
            Vector3 boxSize = boxExtents * 2f;

            ComputeShader compute = ShaderCache.SmokeVolumeSplatCompute;
            compute.SetTexture(splatKernel, "_DensityTex", tile.densityTex);
            compute.SetInt("_PuffCount", count);
            compute.SetVectorArray("_PuffCenters", tileCentersBuffer);
            compute.SetFloats("_PuffRadii", tileRadiiBuffer);
            compute.SetFloat("_BlendRadiusMultiplier", BlendRadiusMultiplier);
            compute.SetVector("_BoxMin", boxMin);
            compute.SetVector("_BoxSize", boxSize);
            compute.SetInts("_Resolution", ActiveTileResolution.x, ActiveTileResolution.y, ActiveTileResolution.z);

            int groups = Mathf.CeilToInt(ActiveTileResolution.x / 4f);
            compute.Dispatch(splatKernel, groups, groups, groups);

            if (blurKernel >= 0)
            {
                compute.SetTexture(blurKernel, "_DensityTex", tile.densityTex);
                compute.SetTexture(blurKernel, "_BlurredTex", tile.blurredTex);
                compute.SetInts("_Resolution", ActiveTileResolution.x, ActiveTileResolution.y, ActiveTileResolution.z);
                compute.Dispatch(blurKernel, groups, groups, groups);
            }

            tile.renderer.enabled = true;
            tile.renderer.transform.position = boxCenter;
            tile.renderer.transform.localScale = boxExtents * 2f;

            float avgRadius = radiusSum / count;
            float tileRadiusRatio = Mathf.Clamp(avgRadius / ShaderReferenceRadius, 0.05f, 1f);

            tile.propertyBlock.Clear();
            tile.propertyBlock.SetTexture("_DensityTex", tile.blurredTex);
            tile.propertyBlock.SetVector("_BoxCenter", new Vector4(boxCenter.x, boxCenter.y, boxCenter.z, 0f));
            tile.propertyBlock.SetVector("_BoxExtents", new Vector4(boxExtents.x, boxExtents.y, boxExtents.z, 0f));
            tile.propertyBlock.SetFloat("_TileRadiusRatio", tileRadiusRatio);
            ApplyCameraInsideMarchReduction(tile.propertyBlock, boxCenter, boxExtents);
            tile.renderer.SetPropertyBlock(tile.propertyBlock);
        }

        private ActiveTile GetOrCreateActiveTile(int index)
        {
            if (index < activeTiles.Count) return activeTiles[index];

            GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.name = "ActiveTile_" + index;
            obj.transform.SetParent(transform, false);
            RemoveColliderImmediate(obj);

            MeshRenderer renderer = obj.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;

            // BEZ żadnego specjalnego keyworda - domyślny wariant shadera (próbkujący
            // teksturę), dokładnie ten sam co warstwa osiadła (SettledCloud).
            if (ShaderCache.SmokeVolumeShader != null)
            {
                renderer.material = new Material(ShaderCache.SmokeVolumeShader);
            }

            ActiveTile tile = new ActiveTile
            {
                obj = obj,
                renderer = renderer,
                propertyBlock = new MaterialPropertyBlock(),
                densityTex = CreateDensityTexture(ActiveTileResolution),
                blurredTex = CreateDensityTexture(ActiveTileResolution)
            };

            activeTiles.Add(tile);
            return tile;
        }

        // Gdy kamera jest WEWNĄTRZ boxa danego kafelka, praktycznie cały ekran
        // zaczyna robić raymarch zamiast tylko sylwetki chmury na jego małym
        // fragmencie - ograniczamy liczbę kroków marchingu w takiej sytuacji.
        private static void ApplyCameraInsideMarchReduction(MaterialPropertyBlock propertyBlock, Vector3 boxCenter, Vector3 boxExtents)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 camLocal = cam.transform.position - boxCenter;
            bool cameraInside = Mathf.Abs(camLocal.x) < boxExtents.x
                && Mathf.Abs(camLocal.y) < boxExtents.y
                && Mathf.Abs(camLocal.z) < boxExtents.z;

            if (cameraInside)
            {
                propertyBlock.SetInt("_MarchSteps", 10);
                propertyBlock.SetInt("_LightMarchSteps", 2);
            }
        }

        private void OnDestroy()
        {
            activeInstanceCount--;

            if (settledDensityTex != null)
            {
                settledDensityTex.Release();
                Object.Destroy(settledDensityTex);
            }
            if (settledBlurredTex != null)
            {
                settledBlurredTex.Release();
                Object.Destroy(settledBlurredTex);
            }

            foreach (ActiveTile tile in activeTiles)
            {
                if (tile.densityTex != null)
                {
                    tile.densityTex.Release();
                    Object.Destroy(tile.densityTex);
                }
                if (tile.blurredTex != null)
                {
                    tile.blurredTex.Release();
                    Object.Destroy(tile.blurredTex);
                }
            }
        }
    }
}
