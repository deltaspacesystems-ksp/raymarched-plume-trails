Shader "VolumetricContrails/SmokeBillboard"
{
    // Bilbordy zawsze zwrócone w stronę kamery (rozwijane w vertex shaderze, nie na
    // CPU - poprawne nawet gdy Tick() nie biegnie co wyrenderowaną klatkę, np. przy
    // dużym time warp). Cieniowanie: normal mapa w przestrzeni stycznej bilbordu
    // (oś kamery) daje wiarygodne fałszywe wypukłości pod prawdziwym kierunkiem
    // słońca - to jest różnica między "płaską naklejką" a czymś co wygląda
    // objętościowo, mimo że geometrycznie to płaski kwadrat.
    Properties
    {
        _MainTex ("Tekstura kłębu (A=gęstość)", 2D) = "white" {}
        _BumpMap ("Normal mapa", 2D) = "bump" {}
        _BaseColor ("Kolor bazowy", Color) = (1,1,1,1)
        _AmbientFloor ("Próg ambientu (nigdy całkiem czarne)", Range(0,1)) = 0.35
        _SunBoost ("Wzmocnienie od słońca", Range(0,2)) = 0.9
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
                float3 center : POSITION;   // pozycja środka kłębu (świat) - ta sama dla 4 wierzchołków kwadratu
                float2 corner : TEXCOORD0;  // (-1,-1)..(1,1) - róg jednostkowego kwadratu
                float size : TEXCOORD1;
                float rotation : TEXCOORD2; // radiany
                float4 color : COLOR;       // alpha z wieku/wysokości
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            sampler2D _BumpMap;
            fixed4 _BaseColor;
            float _AmbientFloor;
            float _SunBoost;

            v2f vert (appdata v)
            {
                v2f o;

                float3 camRight = normalize(UNITY_MATRIX_V[0].xyz);
                float3 camUp = normalize(UNITY_MATRIX_V[1].xyz);

                float s = sin(v.rotation);
                float c = cos(v.rotation);
                float2 rotatedCorner = float2(
                    v.corner.x * c - v.corner.y * s,
                    v.corner.x * s + v.corner.y * c);

                float3 worldPos = v.center + (camRight * rotatedCorner.x + camUp * rotatedCorner.y) * v.size;

                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.uv = v.corner * 0.5 + 0.5;
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                if (tex.a < 0.01) discard;

                // Normal mapa w przestrzeni stycznej BILBORDU - baza to kierunki
                // kamery (right/up/forward), bo bilbord ZAWSZE jest do niej zwrócony.
                // To pozwala prawdziwemu kierunkowi słońca wpływać na jasność każdego
                // piksela osobno, dając wrażenie wypukłej bryły na płaskim kwadracie.
                float3 camRight = normalize(UNITY_MATRIX_V[0].xyz);
                float3 camUp = normalize(UNITY_MATRIX_V[1].xyz);
                float3 camForward = normalize(UNITY_MATRIX_V[2].xyz);

                fixed3 normalTS = UnpackNormal(tex2D(_BumpMap, i.uv));
                float3 worldNormal = normalTS.x * camRight + normalTS.y * camUp + normalTS.z * (-camForward);
                worldNormal = normalize(worldNormal);

                float3 sunDir = normalize(_WorldSpaceLightPos0.xyz);
                float ndotl = saturate(dot(worldNormal, sunDir));
                float shading = _AmbientFloor + (1.0 - _AmbientFloor) * ndotl * _SunBoost;

                fixed4 col = _BaseColor;
                col.rgb *= shading;
                col.a = tex.a * i.color.a;

                return col;
            }
            ENDCG
        }
    }
    FallBack Off
}
