Shader "VolumetricContrails/PuffVolumetric"
{
    // Kłąb dymu jako raymarchowana kula w world space, rozwijana jako billboard
    // (zawsze zwrócony w stronę kamery, jak SmokeBillboard.shader) - nie jeden mesh
    // per kłąb, tylko wiele kłębów w jednym batchowanym meshu (patrz PuffTrailMesh.cs).
    // Miękki, kalafiorowaty brzeg (FBM+Worley+curl warp) zamiast sztywnej sylwetki
    // rurki - to jest fix na "wygląda jak rurka do napoju".
    Properties
    {
        _NoiseScale ("FBM Noise Scale (ogólny kształt)", Float) = 2.0
        _WorleyScale ("Worley Noise Scale (kalafiorowate wgłębienia)", Float) = 6.0
        _CurlStrength ("Curl Warp Strength (turbulencja)", Float) = 0.4
        _CurlScale ("Curl Warp Scale", Float) = 1.5
        _ScrollSpeed ("Noise Scroll Speed", Vector) = (0.05, 0.08, 0.03, 0)

        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _MarchSteps ("March Steps", Int) = 8
        _Density ("Density Multiplier", Float) = 3.0
        _Absorption ("Absorption", Float) = 1.2

        _ForwardScatterG ("HG Forward Scatter (g, 0-1)", Range(0,0.99)) = 0.7
        _BackScatterG ("HG Back Scatter (g, -1-0)", Range(-0.99,0)) = -0.3
        _ScatterBalance ("Forward/Back Balance", Range(0,1)) = 0.7
        _ScatterIntensity ("Scatter Intensity", Float) = 1.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float3 center : POSITION;     // środek kłębu (świat) - ten sam dla 4 wierzchołków kwadratu
                float2 corner : TEXCOORD0;    // (-1,-1)..(1,1) - róg jednostkowego kwadratu
                float2 radiusSeed : TEXCOORD1; // x = promień kuli, y = losowy seed (wariacja kształtu per-kłąb)
                float4 color : COLOR;         // alpha z wieku
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 center : TEXCOORD1;
                float2 radiusSeed : TEXCOORD2;
                fixed4 color : COLOR;
            };

            float _NoiseScale;
            float _WorleyScale;
            float _CurlStrength;
            float _CurlScale;
            float4 _ScrollSpeed;
            fixed4 _BaseColor;
            int _MarchSteps;
            float _Density;
            float _Absorption;
            float _ForwardScatterG;
            float _BackScatterG;
            float _ScatterBalance;
            float _ScatterIntensity;

            v2f vert (appdata v)
            {
                v2f o;

                float3 camRight = normalize(UNITY_MATRIX_V[0].xyz);
                float3 camUp = normalize(UNITY_MATRIX_V[1].xyz);

                float3 worldPos = v.center + (camRight * v.corner.x + camUp * v.corner.y) * v.radiusSeed.x;

                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.worldPos = worldPos;
                o.center = v.center;
                o.radiusSeed = v.radiusSeed;
                o.color = v.color;
                return o;
            }

            // ---- Value noise (gruby kształt / FBM) ----

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
                for (int i = 0; i < 3; i++)
                {
                    v += amp * valueNoise3D(p);
                    p *= 2.02;
                    amp *= 0.5;
                }
                return v;
            }

            // ---- Worley / cellular noise (kalafiorowate wgłębienia) ----

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

            // ---- Curl-ish domain warp (turbulencja) ----

            float3 curlWarp(float3 p, float time)
            {
                float3 warped = p * _CurlScale + time * 0.15;
                float3 offset;
                offset.x = fbm3D(warped + float3(37.2, 17.1, 5.3));
                offset.y = fbm3D(warped + float3(-11.3, 44.5, 8.1));
                offset.z = fbm3D(warped + float3(5.7, -22.9, 13.6));
                return (offset - 0.5) * 2.0 * _CurlStrength;
            }

            // localPos = pozycja próbki WZGLĘDEM ŚRODKA KŁĘBU (nie world, nie object) -
            // każdy kłąb w batchu ma swój środek przekazany osobno (o.center), więc to
            // jest po prostu worldPos - center, przesunięte dodatkowo o unikalny seed
            // kłębu (radiusSeed.y), żeby sąsiednie kłęby nie wyglądały identycznie mimo
            // współdzielonego materiału/shadera.
            float DensityAt(float3 localPos, float seed)
            {
                float time = _Time.y;
                float3 seeded = localPos + seed * 91.7;
                float3 scrolled = seeded + time * _ScrollSpeed.xyz;

                float3 warpedPos = scrolled + curlWarp(scrolled, time);

                float coarseShape = fbm3D(warpedPos * _NoiseScale);
                float erosion = 1.0 - saturate(worleyNoise3D(warpedPos * _WorleyScale) * 1.3);

                float combined = coarseShape * 0.6 + erosion * 0.4;

                float distFromCenter = length(localPos);
                float radial = saturate(1.0 - distFromCenter);
                radial = pow(radial, 0.6);

                return saturate((combined - 0.25) * 1.8) * radial;
            }

            // ---- Henyey-Greenstein phase function (prawdziwe rozpraszanie światła) ----

            float HenyeyGreenstein(float cosAngle, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosAngle;
                return (1.0 - g2) / (4.0 * 3.14159265 * pow(max(denom, 0.0001), 1.5));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Kula jednostkowa (promień 1) w przestrzeni WZGLĘDEM ŚRODKA KŁĘBU -
                // dzięki temu jeden shader/materiał obsługuje kłęby o dowolnym promieniu
                // (skalujemy tylko odległości poniżej, przez i.radiusSeed.x), bez
                // potrzeby osobnej transformacji obiektu per-kłąb.
                float3 localCamPos = (_WorldSpaceCameraPos - i.center) / i.radiusSeed.x;
                float3 localFragPos = (i.worldPos - i.center) / i.radiusSeed.x;
                float3 rayDir = normalize(localFragPos - localCamPos);

                const float sphereRadius = 1.0;
                float b = dot(localCamPos, rayDir);
                float c = dot(localCamPos, localCamPos) - sphereRadius * sphereRadius;
                float disc = max(b * b - c, 0.0);
                float sq = sqrt(disc);

                float tNear = max(-b - sq, 0.0);
                float tFar = -b + sq;

                float marchDist = max(tFar - tNear, 0.0);
                if (marchDist <= 0.0) discard;

                float stepSize = marchDist / _MarchSteps;
                float transmittance = 1.0;
                float3 scatteredLight = 0;

                float3 sunDir = normalize(_WorldSpaceLightPos0.xyz);
                float cosAngle = dot(-rayDir, sunDir);

                float phaseForward = HenyeyGreenstein(cosAngle, _ForwardScatterG);
                float phaseBack = HenyeyGreenstein(cosAngle, _BackScatterG);
                float phase = lerp(phaseBack, phaseForward, _ScatterBalance) * _ScatterIntensity;

                [unroll(16)]
                for (int s = 0; s < _MarchSteps; s++)
                {
                    float t = tNear + stepSize * (s + 0.5);
                    float3 samplePos = localCamPos + rayDir * t;

                    // mnożnik promienia z powrotem, żeby gęstość noise'a skalowała się
                    // w metrach świata (stały _NoiseScale niezależnie od rozmiaru kłębu)
                    float density = DensityAt(samplePos, i.radiusSeed.y) * _Density;
                    float stepTransmittance = exp(-density * _Absorption * stepSize);

                    scatteredLight += transmittance * (1.0 - stepTransmittance) * phase;

                    transmittance *= stepTransmittance;
                    if (transmittance < 0.01) break;
                }

                float alpha = (1.0 - transmittance) * i.color.a;

                fixed4 col = _BaseColor;
                col.rgb *= 1.0 + saturate(scatteredLight);
                col.a = alpha;

                return col;
            }
            ENDCG
        }
    }
    FallBack Off
}
