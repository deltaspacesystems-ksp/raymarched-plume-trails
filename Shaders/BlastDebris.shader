// Flat unlit colour for the fragments of an explosion and their trails: _Color times the vertex colour (a TrailRenderer writes its gradient there),
// alpha-blended, no depth write. The fragments themselves are tinted per object with a property block (hot orange while they glow, grey when cool).
Shader "RaymarchedPlumeTrails/BlastDebris"
{
    Properties { _Color ("Color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "Queue" = "Transparent+1" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; };
            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.color = v.color; return o; }
            fixed4 frag (v2f i) : SV_Target { return _Color * i.color; }
            ENDCG
        }
    }
    FallBack Off
}
