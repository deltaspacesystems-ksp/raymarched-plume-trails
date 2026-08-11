Shader "VolumetricContrails/SmokeVolume"
{
    // v2: gęstość (suma miękkich, nachodzących się kul) jest teraz liczona RAZ NA
    // KLATKĘ przez compute shader (SmokeVolumeSplat.compute), który "wmalowuje" ją do
    // małej tekstury 3D. Fragment shader tylko PRÓBKUJE tę teksturę (tani,
    // trilinear-filtrowany lookup) zamiast liczyć sumę po wszystkich kulach w KAŻDYM
    // punkcie próbki podczas marchingu - to jest fix na:
    //   - lag przy kamerze wewnątrz chmury (dawniej: cały ekran x cała pętla po
    //     kulach x każdy krok marchingu; teraz: sampling O(1), niezależnie od liczby
    //     kul czy pozycji kamery)
    //   - "nieciągłości"/twarde krawędzie między kulami (filtering wygładza za darmo,
    //     bez ręcznego kombinowania z blend radius)
    // Kalafiorowatość/wiry są teraz DOMAIN WARPEM (przesunięciem pozycji próbki przed
    // odczytem tekstury), nie perturbacją per-kula odległości jak w v1.
    Properties
    {
        _DepthBiasDistance ("Depth Bias Distance (m) - przyciąga geometrię do kamery TYLKO na potrzeby testu głębi, pomaga wygrywać z cienkimi strukturami typu wieża startowa", Float) = 6.0
        _NoiseScale ("Detail Noise Scale", Float) = 0.15
        _WorleyScale ("Worley Noise Scale (kalafiorowatość - detal wewnętrzny)", Float) = 0.35
        _ScrollSpeed ("Noise Scroll Speed", Vector) = (0.03, 0.05, 0.02, 0)
        _DetailStrength ("Detail Noise Strength (0=brak, 1=pełny)", Range(0,1)) = 0.45

        _VortexScale ("Vortex Warp Scale", Float) = 0.2
        _VortexStrength ("Vortex Warp Strength (m, skręcone wiry)", Float) = 0.8
        _SilhouetteWarpScale ("Silhouette Warp Scale", Float) = 0.15
        _SilhouetteWarpStrength ("Silhouette Warp Strength (m, przesuwa samą krawędź bryły)", Float) = 4.0
        _SilhouetteNoiseScale ("Silhouette Erosion Noise Scale", Float) = 0.1
        _EdgeErosionStrength ("Edge Erosion Strength (0=gładka krawędź, wyżej=poszarpana)", Range(0,1)) = 0.85
        _ReferenceRadius ("Reference Radius (m) - promień przy którym powyższe skale są 'poprawnie' dostrojone; cieńsze partie warstwy polyline dostają proporcjonalnie mniejszy/gęstszy szum (ten sam kalafior, mniejszy)", Float) = 18.0
        _TileRadiusRatio ("Tile Radius Ratio (per-instance, ustawiane z C# per kafelek = średni promień kłębów w kafelku / _ReferenceRadius) - domyślnie 1.0 dla warstwy osiadłej (grube kłęby, blisko referencji)", Float) = 1.0

        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _MarchSteps ("March Steps", Int) = 24
        _Density ("Density Multiplier", Float) = 4.0
        _Absorption ("Absorption", Float) = 1.5

        // Bezpośrednie kolory referencyjne (od użytkownika, ze zrzutów KSA) zamiast
        // wcześniejszej pośredniej formuły (ambient sceny x mnożniki) - ta była
        // źródłem powtarzających się problemów (szaro, lodowato, "zgniło żółto"),
        // bo zależała od nieprzewidywalnego UNITY_LIGHTMODEL_AMBIENT.
        _SunlitColor ("Sunlit Color (jasna, nasłoneczniona strona)", Color) = (0.92, 0.88, 0.82, 1)
        _ShadowColor ("Shadow Color (strona w cieniu)", Color) = (0.76, 0.78, 0.85, 1)
        _AmbientFloor ("Ambient Floor (jasność nawet w cieniu - chmury nigdy nie są czarne)", Range(0,1)) = 0.68
        _SkyTintStrength ("Sky Tint Strength (cień łapie kolor otoczenia zamiast być szarym)", Range(0,1)) = 0.5
        _ForwardScatterG ("HG Forward Scatter (g) - ostry, kierunkowy blask pod słońce", Range(0,0.99)) = 0.75
        _ScatterIntensity ("Scatter Intensity", Float) = 1.6
        _MultiScatterG ("Multi-Scatter G - szeroki, rozlany blask (symulacja wielokrotnego odbicia)", Range(0,0.6)) = 0.15
        _MultiScatterIntensity ("Multi-Scatter Intensity", Float) = 1.2
        _LightMarchSteps ("Light March Steps (self-shadowing)", Int) = 4
        _LightMarchDistance ("Light March Distance", Float) = 25.0

        // EKSPERYMENTALNE: integracja ze Scatterer (atmospheric extinction/fog).
        // Scatterer jest GPLv3 - nie czytaliśmy/kopiowaliśmy jego kodu, tylko nazwy
        // publicznych pól przez refleksję (Scatterer.ShaderProperties), żeby zgadnąć
        // nazwy globalnych zmiennych shaderowych. NIE MAMY PEWNOŚCI czy Scatterer
        // faktycznie ustawia je jako Shader.SetGlobalXxx (widoczne dla każdego
        // shadera) czy tylko per-materiał na własnych obiektach (wtedy to nic nie
        // zrobi - wartości zostaną (0,0,0,0)/0). _ScattererIntegrationStrength=0
        // wyłącza to całkowicie, gdyby wyglądało źle/nie działało.
        _ScattererIntegrationStrength ("Scatterer Integration Strength (0=wyłączone, eksperymentalne)", Range(0,1)) = 1.0
    }
    SubShader
    {
        // UWAGA: był tu eksperyment z ZTest Always (żeby dym nie chował się za
        // launchpadem) - cofnięty, bo wyłączał depth test względem WSZYSTKIEGO,
        // łącznie z samą rakietą (dym rysował się "nad" rakietą, co jest gorsze niż
        // pierwotny problem). Normalny depth test jest tu poprawny; launchpad
        // occlusion do ewentualnego osobnego rozwiązania (np. depth bias), nie przez
        // globalne wyłączenie ZTest.
        // ZTest jest sterowany globalną zmienną (_GlobalZTestMode, ustawianą z C#,
        // NIE deklarowaną w Properties - property blokowe miałoby pierwszeństwo nad
        // globalem i zablokowałoby możliwość przełączania w runtime). Domyślnie
        // (AssetLoader.Awake, zanim jakikolwiek dym może się wyrenderować) = LEqual
        // (4) - zwykły, poprawny depth test, dokładnie jak wcześniej. Dopiero gdy
        // LaunchpadOcclusionExcluder potwierdzi, że jego kamera/tekstura głębi z
        // wykluczonym launchpadem działa, przełącza na Always (8) - wtedy dym sam
        // ręcznie testuje się względem tamtej tekstury (patrz SceneEyeDepth w
        // frag()), więc nadal poprawnie chowa się za rakietą/terenem, ale NIE za
        // obiektami launchpada wykluczonymi z tamtej tekstury.
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Cull Front
        ZWrite Off
        ZTest [_GlobalZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            // Dwa warianty tego samego shadera (kompilowane osobno, zero kosztu
            // runtime): _ (domyślny) próbkuje _DensityTex (chmura u ziemi - zwarta,
            // stabilna, dobrze się mieści w teksturze 3D). SMOKE_VOLUME_POLYLINE
            // liczy gęstość ANALITYCZNIE jako łańcuch odcinków między pozycjami
            // kłębów (ruchomy ogon za rakietą - rozciągnięty, rosnący dystans, żadna
            // stała rozdzielczość tekstury by za tym nie nadążyła - patrz historia
            // tego pliku/komentarze w SmokeVolumeGroup.cs).
            #pragma multi_compile _ SMOKE_VOLUME_POLYLINE
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };

            float _NoiseScale;
            float _WorleyScale;
            float4 _ScrollSpeed;
            float _DetailStrength;
            float _VortexScale;
            float _VortexStrength;
            float _SilhouetteWarpScale;
            float _SilhouetteWarpStrength;
            float _SilhouetteNoiseScale;
            float _EdgeErosionStrength;
            fixed4 _BaseColor;
            fixed4 _SunlitColor;
            fixed4 _ShadowColor;
            float _ReferenceRadius;
            float _TileRadiusRatio;
            int _MarchSteps;
            float _Density;
            float _Absorption;
            float _AmbientFloor;
            float _SkyTintStrength;
            float _ForwardScatterG;
            float _ScatterIntensity;
            float _MultiScatterG;
            float _MultiScatterIntensity;
            int _LightMarchSteps;
            float _LightMarchDistance;

            // EKSPERYMENTALNE (patrz komentarz w Properties)
            float _ScattererIntegrationStrength;
            float4 _Extinction_Tint;
            float extinctionMultiplier;
            float extinctionGroundFade;
            float extinctionThickness;

            float3 _BoxCenter; // world space - środek aktualnego bounding boxa (ustawiane z C#)
            float3 _BoxExtents; // połowa rozmiaru boxa w każdej osi (world space)

            // Tekstura głębi renderowana przez LaunchpadOcclusionExcluder z tej samej
            // kamery co gracz widzi, ale z wykluczonymi obiektami launchpada z
            // cullingMask - globalna, ustawiana przez Shader.SetGlobalTexture. Zanim
            // ten system wystartuje, _GlobalZTestMode=LEqual sprawia że ten sampler
            // w ogóle nie jest używany do niczego istotnego (patrz frag()).
            sampler2D_float _VolumetricContrailsOcclusionDepth;
            // Odczytywana w HLSL osobno od użycia w "ZTest [_GlobalZTestMode]" -
            // trzeba jawnie zadeklarować, żeby kod mógł SPRAWDZIĆ jej wartość (patrz
            // frag() - test głębi względem powyższej tekstury wykonuje się TYLKO gdy
            // to faktycznie Always/8, czyli gdy LaunchpadOcclusionExcluder naprawdę
            // ustawił tę teksturę. W przeciwnym razie (LEqual/4, bezpieczny domyślny
            // stan z AssetLoader.Awake) tekstura nigdy nie jest bindowana - czytanie
            // jej i tak dawało błędne, bardzo bliskie "głębie", odrzucając PRAWIE
            // KAŻDY piksel dymu (stąd "wcale nie widać chmur" po wyłączeniu tamtego
            // systemu, a niedopilnowaniu tego bezwarunkowego testu tutaj).
            float _GlobalZTestMode;

#if defined(SMOKE_VOLUME_POLYLINE)
            #define MAX_SPINE_POINTS 200
            int _SpineCount;
            float4 _SpinePoints[MAX_SPINE_POINTS]; // world space, uporządkowane od najstarszego do najnowszego
            float _SpineRadii[MAX_SPINE_POINTS];
#else
            sampler3D _DensityTex; // wypełniana przez SmokeVolumeSplat.compute, raz na klatkę
#endif

            // Ile metrów geometria kafelka jest "przyciągana" w stronę kamery TYLKO
            // dla testu głębi (nie wpływa na sam raymarching - worldPos w o.worldPos
            // zostaje ORYGINALNY, nieobciążony). Pomaga wygrywać depth test z
            // cienkimi strukturami sceny (np. wieża startowa) bez pełnej kamery
            // wykluczającej z depth bufora (ta dwukrotnie crashowała grę - patrz
            // LaunchpadOcclusionExcluder.cs, obecnie wyłączony). Mały, stały bias -
            // za duży zacząłby przebijać się przez samą rakietę, tak jak wcześniejsza
            // próba z globalnym ZTest Always.
            float _DepthBiasDistance;

            v2f vert (appdata v)
            {
                v2f o;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 viewDir = normalize(worldPos - _WorldSpaceCameraPos);
                float3 biasedWorldPos = worldPos - viewDir * _DepthBiasDistance;

                o.pos = mul(UNITY_MATRIX_VP, float4(biasedWorldPos, 1.0));
                o.worldPos = worldPos; // NIEobciążony - raymarching w frag() musi liczyć się od prawdziwej geometrii
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            // ---- Noise ----

            float hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float3 hash3(float3 p)
            {
                return float3(
                    hash(p + float3(0.0, 0.0, 0.0)),
                    hash(p + float3(11.1, 7.3, 3.7)),
                    hash(p + float3(23.7, 2.1, 19.4)));
            }

            float valueNoise3D(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float n000 = hash(i + float3(0,0,0));
                float n100 = hash(i + float3(1,0,0));
                float n010 = hash(i + float3(0,1,0));
                float n110 = hash(i + float3(1,1,0));
                float n001 = hash(i + float3(0,0,1));
                float n101 = hash(i + float3(1,0,1));
                float n011 = hash(i + float3(0,1,1));
                float n111 = hash(i + float3(1,1,1));

                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);

                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);

                return lerp(nxy0, nxy1, f.z);
            }

            float fbm3D(float3 p)
            {
                float v = 0.0;
                float amp = 0.5;
                for (int i = 0; i < 4; i++)
                {
                    v += amp * valueNoise3D(p);
                    p *= 2.02;
                    amp *= 0.5;
                }
                return v;
            }

            float worleyNoise3D(float3 p)
            {
                float3 baseCell = floor(p);
                float minDist = 8.0;

                for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    float3 cell = baseCell + float3(x, y, z);
                    float3 cellPoint = cell + hash3(cell);
                    float d = length(p - cellPoint);
                    minDist = min(minDist, d);
                }

                return minDist;
            }

            // Trzy oktawy Worleya (1=blisko centrum komórki/"pełnia", 0=granica
            // komórek/"pustka") - wielopoziomowa, "billowy" wartość używana jako
            // PIERWOTNE źródło rzeźbienia w ApplyDetailAndErosion (odejmowana
            // progowo od pokrycia), nie tylko dekoracja mnożona na powierzchni.
            float worleyFbm3D(float3 p)
            {
                float sum = 0.0;
                float amp = 0.55;
                float freq = 1.0;
                [unroll(3)]
                for (int i = 0; i < 3; i++)
                {
                    float w = 1.0 - saturate(worleyNoise3D(p * freq));
                    sum += w * amp;
                    freq *= 2.3;
                    amp *= 0.5;
                }
                return saturate(sum);
            }

            // Jak worleyNoise3D, ale ZWRACA TEŻ kierunek do najbliższego centrum
            // komórki - używane do wypychania punktu próbki NA ZEWNĄTRZ w stronę
            // centrów komórek (prawdziwe wypukłe płaty), a nie tylko do erozji
            // (która może jedynie "wygryzać" powierzchnię, nigdy nic wybrzuszyć).
            float worleyNoise3DDir(float3 p, out float3 towardCenter)
            {
                float3 baseCell = floor(p);
                float minDist = 8.0;
                towardCenter = float3(0.0, 0.0, 0.0);

                for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    float3 cell = baseCell + float3(x, y, z);
                    float3 cellPoint = cell + hash3(cell);
                    float3 toCell = cellPoint - p;
                    float d = length(toCell);
                    if (d < minDist)
                    {
                        minDist = d;
                        towardCenter = toCell;
                    }
                }

                return minDist;
            }

            // Domain warp - przesuwa punkt próbki o pseudolosowy wektor zależny od
            // pozycji i czasu. Użyte dwa razy z różną skalą/siłą: raz jako drobny
            // "wir" (_VortexScale/_VortexStrength), raz jako duże przesunięcie samej
            // krawędzi bryły (_SilhouetteWarpScale/_SilhouetteWarpStrength).
            float3 DomainWarp(float3 p, float scale, float strength, float timeScale)
            {
                float3 warpBase = p * scale + _Time.y * timeScale;
                float3 warp;
                warp.x = fbm3D(warpBase + float3(37.2, 17.1, 5.3));
                warp.y = fbm3D(warpBase + float3(-11.3, 44.5, 8.1));
                warp.z = fbm3D(warpBase + float3(5.7, -22.9, 13.6));
                return (warp - 0.5) * 2.0 * strength;
            }

            // ---- Ray-box intersection (slab test) - ogranicza march do bounding boxa ----

            bool IntersectBox(float3 ro, float3 rd, float3 boxCenter, float3 boxExtents, out float tNear, out float tFar)
            {
                float3 invRd = 1.0 / rd;
                float3 t0 = (boxCenter - boxExtents - ro) * invRd;
                float3 t1 = (boxCenter + boxExtents - ro) * invRd;
                float3 tmin = min(t0, t1);
                float3 tmax = max(t0, t1);

                tNear = max(max(tmin.x, tmin.y), tmin.z);
                tFar = min(min(tmax.x, tmax.y), tmax.z);

                tNear = max(tNear, 0.0);
                return tFar > tNear;
            }

            // Głębokość (w przestrzeni widoku kamery, ta sama konwencja co
            // LinearEyeDepth) dowolnego punktu świata - liniowe w t wzdłuż promienia
            // kamery, więc interpolacja między dwoma takimi wartościami odpowiada
            // dokładnie interpolacji parametru t (patrz użycie w frag()).
            float EyeDepthOfWorldPos(float3 worldPos)
            {
                return -mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).z;
            }

            // PODEJŚCIE: szum jako PIERWOTNE źródło rzeźbienia, nie dekoracja na
            // powierzchni. Wcześniejsze wersje MNOŻYŁY pokrycie przez maskę erozji -
            // to tylko przyciemnia/teksturuje powierzchnię, nigdy nie tworzy
            // prawdziwych oddzielnych brył. Tutaj wieloskalowy Worley (worleyFbm3D)
            // jest ODEJMOWANY progowo od pokrycia (klasyczna technika "remap" z
            // renderowania chmur volumetrycznych) - miejsca gdzie szum jest niski
            // (między komórkami Worleya) są CAŁKOWICIE wycinane, nawet jeśli bazowe
            // pokrycie tam było spore, zamiast tylko przygaszone. To realnie rzeźbi
            // oddzielne, zaokrąglone bryły zamiast gładkiej powierzchni z fakturą.
            float ApplyDetailAndErosion(float coverage, float3 p, float3 pWarped, float radiusRatio, out float bumpFactor)
            {
                bumpFactor = 0.0;
                if (coverage <= 0.0) return 0.0;

                float freqScale = 1.0 / radiusRatio;

                // Mniejsza częstotliwość bazowa (było *0.5) = WIĘKSZE płaty/bryły -
                // użytkownik: kalafiorowatość musi być o wiele większa niż jest.
                float erosionNoise = worleyFbm3D(pWarped * _SilhouetteNoiseScale * freqScale * 0.22);
                bumpFactor = erosionNoise;

                // Na bardzo cienkich partiach (świeży dym tuż za silnikiem,
                // radiusRatio małe) mocna erozja "przeżera" niemal całe pokrycie -
                // tłumimy poniżej ok. 1/3 promienia referencyjnego (ten sam mechanizm
                // co wcześniej, teraz zastosowany do nowej, mocniejszej erozji).
                float erosionStrength = _DetailStrength * saturate(radiusRatio * 3.0);
                float lowThreshold = saturate(1.0 - erosionNoise * erosionStrength);
                float carved = saturate((coverage - lowThreshold) / max(1.0 - lowThreshold, 0.0001));

                // Drobna, wolno przewijająca się faktura na wierzchu - subtelny ruch/
                // turbulencja, NIE główne źródło kształtu (to robi już erozja wyżej).
                float time = _Time.y;
                float3 scrolled = p * _NoiseScale * freqScale + time * _ScrollSpeed.xyz;
                float scroll = fbm3D(scrolled);
                carved *= lerp(0.85, 1.0, scroll);

                return saturate(carved);
            }

#if defined(SMOKE_VOLUME_POLYLINE)
            // ---- Gęstość: łańcuch odcinków (capsule chain) między kolejnymi pozycjami
            // kłębów - NAPRAWDĘ ciągłe pokrycie wzdłuż całej trasy (odcinek wypełnia
            // przestrzeń między sąsiednimi punktami matematycznie, nie ma czego
            // "rozstawiać" jak przy sumie osobnych kul), bez tekstury/rozdzielczości -
            // ważne dla ogona, który rozciąga się na rosnący, nieograniczony dystans.
            // MAX (nie suma) między segmentami - przy złączeniu dwóch segmentów oba
            // dają dokładnie tę samą wartość w punkcie stycznym (wspólny koniec, wspólny
            // promień), więc przejście jest ciągłe bez sztucznego "spuchnięcia" w
            // miejscach złączeń, jakie dałoby sumowanie.
            // Tani pre-pass (tylko odległości do punktów, bez rzutowania na odcinki)
            // - szacuje lokalny promień rurki w tym miejscu, żeby SKALOWAĆ warp/szum
            // do niego (patrz ApplyDetailAndErosion). Osobno od głównej pętli po
            // segmentach, bo warp musi być policzony RAZ, przed nią (przesuwa pozycję
            // próbki używaną do trafiania w segmenty).
            float EstimateLocalRadius(float3 p)
            {
                float bestDistSq = 1e18;
                float bestR = _ReferenceRadius;
                // Rzadkie próbkowanie (stały krok, max ~16 iteracji niezależnie od
                // _SpineCount) zamiast przechodzenia po WSZYSTKICH do 200 punktach -
                // promień zmienia się gładko wzdłuż trasy, więc nawet sąsiedni rzadki
                // punkt daje wystarczająco bliską wartość do samego skalowania szumu.
                // Pełna pętla tutaj PODWAJAŁA koszt każdej próbki gęstości (licząc się
                // przy każdym kroku marchingu głównego I light march) - to był
                // realny powód spadku FPS przy zbliżeniu kamery.
                // Przesunięcie bitowe zamiast dzielenia (/16 == >>4) - dzielenie liczb
                // całkowitych na GPU jest wolniejsze (stąd ostrzeżenie kompilatora
                // "integer divides may be much slower"), a to i tak hot path (liczone
                // przy każdej próbce gęstości).
                int stride = max(1, _SpineCount >> 4);
                for (int i = 0; i < _SpineCount; i += stride)
                {
                    float r = _SpineRadii[i];
                    if (r <= 0.0) continue;
                    float3 d = p - _SpinePoints[i].xyz;
                    float distSq = dot(d, d);
                    if (distSq < bestDistSq) { bestDistSq = distSq; bestR = r; }
                }
                return bestR;
            }

            float DensityAt(float3 p, out float bumpFactor, out float outRadiusRatio)
            {
                // Górna klamra na 1.0: bez niej duże klastry silników (promień >
                // _ReferenceRadius) dostawały WIĘKSZY niż oryginalnie dostrojony warp
                // (_SilhouetteWarpStrength * radiusRatio > _SilhouetteWarpStrength),
                // co przesuwało próbki poza margines boxa (BoxFixedWarpMargin,
                // dostrojony dla radiusRatio<=1) i ucinało sylwetkę płasko na
                // krawędzi boxa ("płaskie ściany").
                float radiusRatio = clamp(EstimateLocalRadius(p) / _ReferenceRadius, 0.05, 1.0);
                outRadiusRatio = radiusRatio;

                float3 vortexOffset = DomainWarp(p, _VortexScale / radiusRatio, _VortexStrength * radiusRatio, 0.08);
                float3 silhouetteOffset = DomainWarp(p, _SilhouetteWarpScale / radiusRatio, _SilhouetteWarpStrength * radiusRatio, 0.03);

                // WYCOFANE: wypukłe płaty przez worleyNoise3DDir - kosztowało kolejne
                // przeszukanie siatki 27 komórek NA KAŻDĄ próbkę gęstości (spadek z
                // 80 do 30 fps), a efekt wizualny i tak nie dawał tego czego
                // szukaliśmy. Wraca do (tańszego) samego DomainWarp.
                float3 pWarped = p + vortexOffset + silhouetteOffset;

                float coverage = 0.0;

                for (int i = 0; i < _SpineCount - 1; i++)
                {
                    float3 a = _SpinePoints[i].xyz;
                    float3 b = _SpinePoints[i + 1].xyz;
                    float ra = _SpineRadii[i];
                    float rb = _SpineRadii[i + 1];
                    if (ra <= 0.0 || rb <= 0.0) continue;

                    // Tani wstępny odrzut przed liczeniem rzutu na odcinek: jeśli punkt
                    // próbki jest dalej niż promień segmentu (z zapasem na największy
                    // możliwy blendRadius) od ŚRODKA odcinka, na pewno nie wpłynie - dla
                    // większości z do 200 segmentów w pętli oszczędza to sqrt/dot z
                    // pełnej projekcji na odcinek.
                    float3 mid = (a + b) * 0.5;
                    float halfLen = length(b - a) * 0.5;
                    float maxR = max(ra, rb) * 1.4;
                    float cullRadius = halfLen + maxR;
                    if (dot(pWarped - mid, pWarped - mid) > cullRadius * cullRadius) continue;

                    float3 ab = b - a;
                    float abLenSq = dot(ab, ab);
                    float t = abLenSq > 0.0001 ? saturate(dot(pWarped - a, ab) / abLenSq) : 0.0;
                    float3 closest = a + ab * t;
                    float r = lerp(ra, rb, t);

                    float blendRadius = r * 1.4; // odpowiednik _BlendRadiusMultiplier z compute shadera
                    float dSq = dot(pWarped - closest, pWarped - closest);
                    if (dSq >= blendRadius * blendRadius) continue;
                    float d = sqrt(dSq);

                    float tt = saturate(1.0 - d / blendRadius);
                    coverage = max(coverage, tt * tt * (3.0 - 2.0 * tt));
                }

                // Cienka igła = trochę bardziej przezroczysta (mniej gęsta), gruba
                // podstawa (radiusRatio bliskie 1) = trochę gęstsza - to daje efekt
                // "wolno leci = kumuluje się gęsty dym, szybko = cienki, przezroczysty
                // welon". Dolna granica (0.6, nie 0) żeby nie wrócił problem
                // "znikającej" cienkiej smugi sprzed paru poprawek.
                coverage *= lerp(0.6, 1.15, saturate(radiusRatio * 1.5));

                return ApplyDetailAndErosion(coverage, p, pWarped, radiusRatio, bumpFactor);
            }
#else
            // ---- Gęstość: próbka z tekstury (wypełnionej przez compute shader) -
            // chmura u ziemi, zwarta i stabilna, dobrze się mieści w stałej rozdzielczości.
            float DensityAt(float3 p, out float bumpFactor, out float outRadiusRatio)
            {
                // _TileRadiusRatio przychodzi per-instancję z C# (BakeActiveTile) -
                // bez tego szum rzeźbiący (freqScale = 1/radiusRatio w
                // ApplyDetailAndErosion) był zawsze dostrojony pod GRUBĄ referencyjną
                // chmurę (18m) niezależnie od tego, czy kafelek zawiera świeże,
                // cienkie kłęby tuż przy silniku - te same, za duże komórki Worleya
                // wycinały cienkie kłęby na kilka rozłącznych brył zamiast
                // proporcjonalnie drobnej rzeźby ("pojedyncze cząstki zanim się
                // połączą"). Warstwa osiadła zostawia domyślne 1.0 z Properties (nie
                // ustawia tego property).
                float radiusRatio = clamp(_TileRadiusRatio, 0.05, 1.0);
                outRadiusRatio = radiusRatio;

                // Skalowane tak samo jak w wycofanej wersji polyline (patrz
                // DensityAt powyżej w gałęzi SMOKE_VOLUME_POLYLINE) - bez tego
                // przesunięcie warpu byłoby stałe niezależnie od promienia kafelka,
                // więc na cienkim kłębie mogłoby przesuwać próbkę daleko poza jego
                // faktyczny rozmiar.
                float3 vortexOffset = DomainWarp(p, _VortexScale / radiusRatio, _VortexStrength * radiusRatio, 0.08);
                float3 silhouetteOffset = DomainWarp(p, _SilhouetteWarpScale / radiusRatio, _SilhouetteWarpStrength * radiusRatio, 0.03);
                float3 pWarped = p + vortexOffset + silhouetteOffset;

                float3 boxMin = _BoxCenter - _BoxExtents;
                float3 uv = (pWarped - boxMin) / (_BoxExtents * 2.0);
                if (any(uv < 0.0) || any(uv > 1.0)) { bumpFactor = 0.0; return 0.0; }

                float coverage = tex3Dlod(_DensityTex, float4(uv, 0.0)).r;
                return ApplyDetailAndErosion(coverage, p, pWarped, radiusRatio, bumpFactor);
            }
#endif

            // Krótki dodatkowy march w stronę słońca z bieżącego punktu próbki - ile
            // światła w ogóle dotarło tutaj zanim rozproszy się do kamery. Bez tego
            // cała chmura ma jednolitą jasność (płasko, bez wrażenia głębi); z tym -
            // wnętrze/zacienione zagłębienia są ciemniejsze niż strona zwrócona do
            // słońca, dokładnie jak w prawdziwej chmurze. Ta sama technika co
            // "lightMarchSteps" w configu EVE (blackrack) - krótszy, tańszy march niż
            // główny, bo tu tylko liczy się przybliżone przyciemnienie, nie kolor.
            // radiusRatio skaluje dystans marchu - _LightMarchDistance jest
            // dostrojony pod grubą podstawę; na cienkiej igle próbki lądowały daleko
            // POZA nią (prawie zerowe zacienienie, płaski wygląd). Krótszy march na
            // cienkich partiach próbkuje tam, gdzie faktycznie jest gęstość.
            float LightMarch(float3 p, float3 sunDir, float radiusRatio)
            {
                float stepLen = (_LightMarchDistance * lerp(0.25, 1.0, radiusRatio)) / _LightMarchSteps;
                float transmittance = 1.0;

                [unroll(8)]
                for (int s = 0; s < _LightMarchSteps; s++)
                {
                    float3 samplePos = p + sunDir * stepLen * (s + 1);
                    float unusedBump, unusedRatio;
                    float d = DensityAt(samplePos, unusedBump, unusedRatio) * _Density;
                    transmittance *= exp(-d * _Absorption * stepLen);
                }

                return transmittance;
            }

            float HenyeyGreenstein(float cosAngle, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosAngle;
                return (1.0 - g2) / (4.0 * 3.14159265 * pow(max(denom, 0.0001), 1.5));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 ro = _WorldSpaceCameraPos;
                float3 rd = normalize(i.worldPos - ro);

                float tNear, tFar;
                if (!IntersectBox(ro, rd, _BoxCenter, _BoxExtents, tNear, tFar)) discard;

                // Ręczny test głębi względem tekstury z WYKLUCZONYM launchpadem - TYLKO
                // gdy _GlobalZTestMode faktycznie == Always (8), czyli gdy
                // LaunchpadOcclusionExcluder jest aktywny i ta tekstura jest naprawdę
                // ustawiona. Obecnie ten system jest wyłączony (patrz komentarz przy
                // deklaracji _GlobalZTestMode) - bez tego warunku kod czytałby
                // niezbindowaną teksturę i odrzucał niemal każdy piksel dymu.
                if (_GlobalZTestMode > 5.0)
                {
                    float2 screenUV = i.screenPos.xy / i.screenPos.w;
                    float rawSceneDepth = tex2D(_VolumetricContrailsOcclusionDepth, screenUV);
                    float sceneEyeDepth = LinearEyeDepth(rawSceneDepth);

                    float nearEyeDepth = EyeDepthOfWorldPos(ro + rd * tNear);
                    float farEyeDepth = EyeDepthOfWorldPos(ro + rd * tFar);

                    if (sceneEyeDepth <= nearEyeDepth) discard;
                    if (sceneEyeDepth < farEyeDepth)
                    {
                        float ratio = saturate((sceneEyeDepth - nearEyeDepth) / max(farEyeDepth - nearEyeDepth, 0.0001));
                        tFar = tNear + (tFar - tNear) * ratio;
                    }
                }

                float marchDist = tFar - tNear;
                if (marchDist <= 0.0) discard;

                float stepSize = marchDist / _MarchSteps;
                float transmittance = 1.0;
                float3 scatteredLight = 0;
                float bumpAccum = 0.0;

                // Dithering offsetu startowego marchingu - bez tego, przy małej
                // liczbie kroków (np. _MarchSteps=10 gdy kamera blisko/w środku),
                // sąsiednie piksele próbkują dokładnie te same "warstwy" odległości
                // od kamery, co daje widoczne pasy/pierścienie (klasyczny artefakt
                // undersamplingu raymarchingu, gorzej widoczny właśnie w tych samych
                // sytuacjach co spadek FPS). Tani hash pozycji ekranowej rozbija tę
                // spójność w szum zamiast regularnych pasów.
                float ditherHash = frac(sin(dot(i.pos.xy, float2(12.9898, 78.233))) * 43758.5453);
                float ditherOffset = (ditherHash - 0.5) * stepSize;

                float3 sunDir = normalize(_WorldSpaceLightPos0.xyz);
                float cosAngle = dot(-rd, sunDir);
                // Dwa loby: ostry "forward" (blask pod słońce, single scattering) +
                // szeroki, prawie wszechkierunkowy "multi" (przybliżenie wielokrotnego
                // odbicia światła wewnątrz chmury - to jest to, co daje wrażenie, że
                // chmura "świeci od środka", a nie tylko odbija światło z jednej strony).
                float phase = HenyeyGreenstein(cosAngle, _ForwardScatterG) * _ScatterIntensity
                    + HenyeyGreenstein(cosAngle, _MultiScatterG) * _MultiScatterIntensity;

                [loop]
                for (int s = 0; s < _MarchSteps; s++)
                {
                    float t = tNear + stepSize * (s + 0.5) + ditherOffset;
                    float3 samplePos = ro + rd * t;

                    float bump, sampleRadiusRatio;
                    float density = DensityAt(samplePos, bump, sampleRadiusRatio) * _Density;
                    if (density <= 0.001) continue;

                    float lightTransmittance = LightMarch(samplePos, sunDir, sampleRadiusRatio);

                    float stepTransmittance = exp(-density * _Absorption * stepSize);
                    float contribution = transmittance * (1.0 - stepTransmittance);
                    scatteredLight += contribution * phase * lightTransmittance;
                    bumpAccum += contribution * bump;
                    transmittance *= stepTransmittance;
                    if (transmittance < 0.01) break;
                }

                float alpha = 1.0 - transmittance;
                if (alpha <= 0.001) discard;

                // Bezpośrednio referencyjne kolory (_SunlitColor/_ShadowColor) zamiast
                // wyliczania z ambientu sceny - przewidywalne, niezależne od
                // aktualnego oświetlenia/pogody w scenie.
                fixed3 shadowColor = _ShadowColor.rgb;
                fixed3 litColor = _SunlitColor.rgb;

                fixed4 col;
                col.rgb = lerp(shadowColor, litColor, saturate(scatteredLight));
                col.a = alpha;

                // "Górki" (blisko punktu komórki Worleya, ten sam szum co erozja
                // krawędzi) dostają jasną, zimną biel - "dołki"/wnętrze zimny szary.
                // avgBump to średnia ważona wkładem w alpha wzdłuż promienia (ten sam
                // trik normalizacji co przy scatteredLight/alpha).
                float avgBump = alpha > 0.0001 ? saturate(bumpAccum / alpha) : 0.0;
                // Wcześniejsze wersje mnożyły tu przez DODATKOWY kolor (cold/warm
                // tint) - w połączeniu z _SunlitColor/_ShadowColor dawało to
                // złożenie dwóch niezależnie dobieranych barw, źródło powtarzających
                // się problemów z kolorem (lód, "zgniły żółty"). Teraz to TYLKO
                // modulacja jasności (górki odrobinę jaśniejsze, dołki odrobinę
                // ciemniejsze), bez zmiany odcienia - sam kolor w całości pochodzi z
                // _SunlitColor/_ShadowColor powyżej.
                col.rgb *= lerp(0.7, 1.15, avgBump);

                // EKSPERYMENTALNE: standardowa formuła aerial perspective (im dalej od
                // kamery, tym bardziej kolor zlewa się z kolorem/gęstością atmosfery
                // zamiast wyglądać jakby atmosfery nie było). Kształt formuły
                // (dystans * multiplier * thickness -> blend w stronę Extinction_Tint)
                // to nasza własna, standardowa implementacja tej koncepcji - nie kod
                // Scatterera - podpięta pod ZGADNIĘTE nazwy jego globalnych zmiennych.
                if (_ScattererIntegrationStrength > 0.0001)
                {
                    float camDist = distance(i.worldPos, _WorldSpaceCameraPos);
                    float fogAmount = saturate(camDist * extinctionMultiplier * max(extinctionThickness, 0.0001))
                        * saturate(extinctionGroundFade);
                    col.rgb = lerp(col.rgb, _Extinction_Tint.rgb, fogAmount * _ScattererIntegrationStrength);
                }

                return col;
            }
            ENDCG
        }
    }
    FallBack Off
}
