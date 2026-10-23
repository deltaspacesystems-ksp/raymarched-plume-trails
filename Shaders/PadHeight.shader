// Height map of the VISIBLE scenery under the pad cloud's grid. An orthographic camera looks straight down at the grid; with this
// shader replacing every material it writes, for the surface nearest to the camera (the topmost one), its height along the up axis
// above the bottom of the grid. So what is solid for the solver is what is drawn - the pad, the trench walls, the deflector, the
// launch mount - not just what has a collider (the Mirage pad's trench has none in the terrain collider).
Shader "RaymarchedPlumeTrails/PadHeight"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _PadHBase;   // xyz = world position of the grid corner, w = local y of the bottom of the grid
            float4 _PadHUp;     // xyz = up axis

            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float frag (v2f i) : SV_Target
            {
                return max(dot(i.wpos - _PadHBase.xyz, _PadHUp.xyz) - _PadHBase.w, 0.0);
            }
            ENDCG
        }
    }
}
