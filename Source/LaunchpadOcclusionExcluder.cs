using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Zwykły ZTest chowa dym za DOWOLNĄ geometrią bliżej kamery niż on sam - poprawne
    /// dla rakiety/terenu, ale wieża/launchpad też go zasłania, co wygląda źle (dym
    /// "ucina się" na krawędzi wieży). Wcześniejsza próba (globalne ZTest Always) była
    /// zła, bo wyłączała depth test wobec WSZYSTKIEGO, także rakiety.
    ///
    /// Tym razem: druga kamera, identyczna jak główna, ale z WYKLUCZONYMI (po nazwie)
    /// obiektami launchpada z cullingMask, renderuje głębię do osobnej tekstury.
    /// SmokeVolume.shader ręcznie testuje się względem TEJ tekstury zamiast (albo poza)
    /// standardowego ZTest - patrz _GlobalZTestMode/_VolumetricContrailsOcclusionDepth
    /// w shaderze. Dym nadal poprawnie chowa się za rakietą i terenem (bo te NIE są
    /// wykluczone z tej tekstury), tylko nie za launchpadem.
    ///
    /// _GlobalZTestMode zostaje na bezpiecznym LEqual (zwykłe zachowanie, patrz
    /// AssetLoader.Awake) dopóki nie znajdziemy przynajmniej jednego obiektu
    /// pasującego do LaunchpadNameKeywords - jeśli lista słów kluczowych nie pasuje do
    /// niczego w danej instalacji KSP (inne nazewnictwo, mody zmieniające KSC), system
    /// zostaje wyłączony zamiast zgadywać i zepsuć okluzję względem rakiety.
    ///
    /// WYŁĄCZONE (KSPAddon poniżej zakomentowany) - dwie próby naprawy (CameraType.
    /// Reflection, potem RenderingPath.VertexLit) nie powstrzymały crasha/zepsutego
    /// oświetlenia terenu powodowanego przez tę drugą kamerę. Bez podglądu żywej gry
    /// dalsze zgadywanie tylko marnuje czas testera - wracamy do zwykłego ZTest
    /// (launchpad zasłania dym, zaakceptowany kosmetyczny drobiazek). Kod zostaje
    /// na wypadek gdyby ktoś chciał to kiedyś dokończyć z realnym debugowaniem.
    /// </summary>
    // [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class LaunchpadOcclusionExcluder : MonoBehaviour
    {
        // Dopasowywane bez rozróżniania wielkości liter, jako podciąg nazwy GameObject.
        // Jeśli po pierwszym starcie w logu (szukaj "[VolumetricContrails][Occlusion]")
        // okaże się, że nic nie pasuje - lista do rozszerzenia na podstawie
        // faktycznych nazw obiektów zalogowanych przez LogNearbyRendererNames.
        private static readonly string[] LaunchpadNameKeywords =
        {
            "launchpad", "launch pad", "launchsite", "launch site",
            "crawler", "gantry", "flametrench", "flame trench",
            "flamediverter", "flame diverter", "launchtower", "launch tower",
            "launchclamp", "launch clamp", "umbilical"
        };

        private const int ExcludeLayer = 30;
        private const int CompareFunctionLEqual = 4;
        private const int CompareFunctionAlways = 8;

        private Camera depthCam;
        private RenderTexture depthTex;
        private bool scanned;
        private bool exclusionActive;

        private void Awake()
        {
            GameEvents.onFlightReady.Add(OnFlightReady);
        }

        private void OnDestroy()
        {
            GameEvents.onFlightReady.Remove(OnFlightReady);
            Shader.SetGlobalInt("_GlobalZTestMode", CompareFunctionLEqual);

            if (depthTex != null)
            {
                depthTex.Release();
                Object.Destroy(depthTex);
            }
            if (depthCam != null) Object.Destroy(depthCam.gameObject);
        }

        private void OnFlightReady()
        {
            if (scanned) return;
            scanned = true;

            int matched = ReassignLaunchpadObjects();
            if (matched > 0)
            {
                SetupDepthCamera();
            }
            else
            {
                Debug.LogWarning("[VolumetricContrails][Occlusion] Nie znaleziono żadnego obiektu launchpada po nazwie " +
                    "- naprawa okluzji NIE jest aktywna (zostaje zwykły ZTest). Poniżej lista rendererów blisko statku, " +
                    "dopisz pasującą nazwę do LaunchpadNameKeywords w LaunchpadOcclusionExcluder.cs:");
                LogNearbyRendererNames();
            }
        }

        private int ReassignLaunchpadObjects()
        {
            Renderer[] all = FindObjectsOfType<Renderer>();
            int matched = 0;

            foreach (Renderer r in all)
            {
                string n = r.gameObject.name.ToLowerInvariant();
                bool isMatch = false;
                foreach (string kw in LaunchpadNameKeywords)
                {
                    if (n.Contains(kw)) { isMatch = true; break; }
                }

                if (isMatch)
                {
                    r.gameObject.layer = ExcludeLayer;
                    matched++;
                    Debug.Log("[VolumetricContrails][Occlusion] Wykluczam z depth (dopasowano nazwę): " + r.gameObject.name);
                }
            }

            Debug.Log(string.Format(
                "[VolumetricContrails][Occlusion] Przeskanowano {0} rendererów, wykluczono {1}.",
                all.Length, matched));

            // Główna kamera gry ma WŁASNĄ, ograniczoną maskę warstw (nie "wszystko") -
            // nigdy nie obejmowała tej nowej, nieużywanej warstwy, więc przeniesienie
            // obiektów na ExcludeLayer robiło je niewidocznymi też dla GRACZA, nie
            // tylko dla mojej kamery głębi. Dopisujemy warstwę z powrotem do maski
            // głównej kamery - moja kamera głębi i tak jawnie ją wyklucza osobno
            // (patrz ApplyCameraSettings).
            if (matched > 0 && Camera.main != null)
            {
                Camera.main.cullingMask |= (1 << ExcludeLayer);
            }

            return matched;
        }

        private void LogNearbyRendererNames()
        {
            if (FlightGlobals.ActiveVessel == null) return;
            Vector3 vesselPos = FlightGlobals.ActiveVessel.transform.position;

            Renderer[] all = FindObjectsOfType<Renderer>();
            foreach (Renderer r in all)
            {
                if (Vector3.Distance(r.transform.position, vesselPos) < 300f)
                {
                    Debug.Log("[VolumetricContrails][Occlusion][Diag] Blisko startu: " + r.gameObject.name);
                }
            }
        }

        private void SetupDepthCamera()
        {
            if (Camera.main == null)
            {
                Debug.LogWarning("[VolumetricContrails][Occlusion] Camera.main jest null przy starcie - okluzja nieaktywna.");
                return;
            }

            GameObject camObj = new GameObject("VolumetricContrails_OcclusionDepthCam");
            depthCam = camObj.AddComponent<Camera>();
            depthCam.CopyFrom(Camera.main);
            depthCam.enabled = false; // renderujemy ręcznie w LateUpdate

            // CameraType.Reflection NIE pomogło (błąd dalej leciał) - okazało się, że
            // problem to NIE inny mod skanujący kamery, tylko to, że ta kamera,
            // kopiując ustawienia z głównej, próbowała RÓWNIEŻ liczyć realtime
            // shadow mapy dla słońca przy każdym renderze, i to się wywalało (target
            // to depth-only tekstura, nie pełny kolor+cień). To najpewniej też
            // psuło globalny stan cieni używany przez teren (stąd źle oświetlony
            // teren przy dobrze oświetlonej trawie). VertexLit całkowicie pomija
            // realtime shadow mapping - nie są nam i tak potrzebne, liczy się tylko
            // surowa geometria/głębia.
            depthCam.renderingPath = RenderingPath.VertexLit;
            depthCam.useOcclusionCulling = false;

            // Połowa rozdzielczości ekranu - to tylko miękkie przycięcie promienia do
            // sceny, nie potrzebuje pixel-perfect dokładności, a to pełny drugi
            // render całej sceny co klatkę, więc rozdzielczość ma spory wpływ na koszt.
            int texWidth = Mathf.Max(1, Screen.width / 2);
            int texHeight = Mathf.Max(1, Screen.height / 2);
            depthTex = new RenderTexture(texWidth, texHeight, 24, RenderTextureFormat.Depth);
            depthTex.Create();

            ApplyCameraSettings();

            Shader.SetGlobalTexture("_VolumetricContrailsOcclusionDepth", depthTex);

            exclusionActive = true;
            Shader.SetGlobalInt("_GlobalZTestMode", CompareFunctionAlways);

            Debug.Log("[VolumetricContrails][Occlusion] Kamera głębi z wykluczonym launchpadem gotowa.");
        }

        private void ApplyCameraSettings()
        {
            depthCam.cullingMask = Camera.main.cullingMask & ~(1 << ExcludeLayer);
            depthCam.clearFlags = CameraClearFlags.Depth;
            depthCam.depthTextureMode = DepthTextureMode.None;
            depthCam.targetTexture = depthTex;
            // CopyFrom() nie gwarantuje zachowania tych ustawień - ustawiamy ponownie
            // po każdym CopyFrom (patrz komentarz przy pierwszym ustawieniu wyżej).
            depthCam.renderingPath = RenderingPath.VertexLit;
            depthCam.useOcclusionCulling = false;
        }

        private void LateUpdate()
        {
            // Bez tego kamera renderowałaby CAŁĄ scenę drugi raz co klatkę przez
            // resztę lotu (godziny na orbicie) - dym istnieje realnie tylko przez
            // pierwsze 1-2 minuty po starcie.
            if (!exclusionActive || !SmokeVolumeGroup.AnyActive || depthCam == null || Camera.main == null) return;

            // Camera.main potrafi się zmieniać (przełączanie widoków) - upewniamy się
            // za każdym razem, że AKTUALNA główna kamera nadal widzi ExcludeLayer.
            Camera.main.cullingMask |= (1 << ExcludeLayer);

            depthCam.CopyFrom(Camera.main);
            depthCam.enabled = false;
            ApplyCameraSettings();
            depthCam.Render();
        }
    }
}
