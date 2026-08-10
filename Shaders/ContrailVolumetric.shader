Shader "VolumetricContrails/ContrailVolumetric"
{
    Properties
    {
        _NoiseScale ("Noise Scale", Float) = 0.3
        _ScrollSpeed ("Noise Scroll Speed", Vector) = (0.02, 0.05, 0.01, 0)
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _MarchSteps ("March Steps", Int) = 10
        _Density ("Density Multiplier", Float) = 2.0
        _Absorption ("Absorption", Float) = 1.0
        _SunDirBoost ("Sun-facing Brightness Boost", Range(0,2)) = 0.6
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        // Tylne ścianki - fragment dostaje punkt wyjścia z bryły, wejście liczymy
        // matematycznie. Działa poprawnie nawet gdy kamera jest wewnątrz rurki.
        Cull Front
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
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float4 segA_r : TEXCOORD1; // xyz = start segmentu (obiekt), w = promień
                float3 segB : TEXCOORD2;   // koniec segmentu (obiekt)
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                fixed4 color : COLOR;
                float4 segA_r : TEXCOORD1; // teraz w przestrzeni świata
                float3 segB : TEXCOORD2;
            };

            float _NoiseScale;
            float4 _ScrollSpeed;
            fixed4 _BaseColor;
            int _MarchSteps;
            float _Density;
            float _Absorption;
            float _SunDirBoost;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = v.color;

                float3 worldSegA = mul(unity_ObjectToWorld, float4(v.segA_r.xyz, 1.0)).xyz;
                float3 worldSegB = mul(unity_ObjectToWorld, float4(v.segB, 1.0)).xyz;

                o.segA_r = float4(worldSegA, v.segA_r.w);
                o.segB = worldSegB;
                return o;
            }

            float hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
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

            float DensityAt(float3 worldPos)
            {
                float3 p = worldPos * _NoiseScale + _Time.y * _ScrollSpeed.xyz;
                float n = fbm3D(p);
                return saturate((n - 0.35) * 1.8);
            }

            // Przecięcie promienia (ro, rd) z NIESKOŃCZONYM cylindrem wzdłuż osi pa-pb
            // o promieniu r, przycięte do zakresu wzdłuż samej osi [0, |pb-pa|] -
            // klasyczna formuła (Inigo Quilez), zwraca oba punkty (near/far) w jednym
            // równaniu kwadratowym, bez oddzielnej obsługi "zaślepek" na końcach -
            // sąsiednie segmenty w łańcuchu naturalnie się stykają, więc nie potrzeba
            // osobnych półkul jak w pełnej kapsule.
            bool IntersectSegmentCylinder(float3 ro, float3 rd, float3 pa, float3 pb, float r, out float tNear, out float tFar)
            {
                float3 ba = pb - pa;
                float baba = dot(ba, ba);
                float3 oc = ro - pa;
                float bard = dot(ba, rd);
                float baoc = dot(ba, oc);

                float k2 = baba - bard * bard;
                float k1 = baba * dot(oc, rd) - baoc * bard;
                float k0 = baba * dot(oc, oc) - baoc * baoc - r * r * baba;

                tNear = 0; tFar = 0;
                if (abs(k2) < 1e-6) return false;

                float h = k1 * k1 - k2 * k0;
                if (h < 0.0) return false;
                h = sqrt(h);

                float t1 = (-k1 - h) / k2;
                float t2 = (-k1 + h) / k2;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }

                float y1 = baoc + t1 * bard;
                float y2 = baoc + t2 * bard;

                if ((y1 < 0.0 && y2 < 0.0) || (y1 > baba && y2 > baba)) return false;

                tNear = max(t1, 0.0);
                tFar = t2;
                return tFar > tNear;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 ro = _WorldSpaceCameraPos;
                float3 rd = normalize(i.worldPos - ro);

                float tNear, tFar;
                bool hit = IntersectSegmentCylinder(ro, rd, i.segA_r.xyz, i.segB, i.segA_r.w, tNear, tFar);
                if (!hit) discard;

                float marchDist = tFar - tNear;
                float stepSize = marchDist / _MarchSteps;
                float transmittance = 1.0;

                [unroll(16)]
                for (int s = 0; s < _MarchSteps; s++)
                {
                    float t = tNear + stepSize * (s + 0.5);
                    float3 samplePos = ro + rd * t;

                    float density = DensityAt(samplePos) * _Density;
                    transmittance *= exp(-density * _Absorption * stepSize);
                    if (transmittance < 0.01) break;
                }

                float alpha = (1.0 - transmittance) * i.color.a;

                float3 axisDir = normalize(i.segB - i.segA_r.xyz);
                float3 exitPos = ro + rd * tFar;
                float3 toAxis = exitPos - i.segA_r.xyz;
                float3 alongAxis = axisDir * dot(toAxis, axisDir);
                float3 radialDir = normalize(toAxis - alongAxis + 0.0001);

                float3 sunDir = normalize(_WorldSpaceLightPos0.xyz);
                float sunDot = saturate(dot(radialDir, sunDir));
                float scatter = 1.0 + sunDot * _SunDirBoost;

                fixed4 col = _BaseColor;
                col.rgb *= scatter;
                col.a = alpha;

                return col;
            }
            ENDCG
        }
    }
    FallBack Off
}
