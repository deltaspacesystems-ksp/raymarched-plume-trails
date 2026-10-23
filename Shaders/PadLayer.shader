// One LAYER of the depth peeling of the pad scenery. An orthographic camera looks straight down at the grid; this shader replaces every material.
// Layer 0 writes the topmost surface of every column; layer n writes the nearest surface that lies BELOW the one of layer n-1 (the previous
// layer is read back as a texture). Written (R): floor(height above the bottom of the grid * 16) * 2, plus 1 for a FRONT face (the outside of a
// solid seen from outside: it enters the solid) and plus 0 for a back face (the inside of the same solid seen from within: it leaves it);
// 0 = nothing. The topmost surface is found by a MAX blend of that value, with no depth test at all (the direction of the depth test depends
// on the graphics API). So the roof of a launch mount, its underside, the floor of the flame trench under it, the walls - all of them come out
// as layers, which a single height map can not hold.
Shader "RaymarchedPlumeTrails/PadLayer"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One One
            BlendOp Max

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _PadHBase;   // xyz = world position of the grid corner, w = local y of the bottom of the grid
            float4 _PadHUp;     // xyz = up axis
            float4 _PadHE1;     // local x axis
            float4 _PadHE2;     // local z axis
            float4 _PadHExt;    // x, y = extent of the grid in x and z (m), z = 1 for the first layer
            sampler2D _PadPrev; // the previous layer

            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 d = i.wpos - _PadHBase.xyz;
                float h = dot(d, _PadHUp.xyz) - _PadHBase.w;
                if (h <= 0.0) discard;
                if (_PadHExt.z < 0.5)
                {
                    float2 uv = float2(dot(d, _PadHE1.xyz) / _PadHExt.x, dot(d, _PadHE2.xyz) / _PadHExt.y);
                    float pv = tex2Dlod(_PadPrev, float4(uv, 0.0, 0.0)).r;
                    if (pv <= 0.0) discard;
                    if (floor(h * 16.0) >= floor(pv * 0.5)) discard;     // not strictly below the surface of the previous layer
                }
                return float4(floor(h * 16.0) * 2.0 + (facing > 0.0 ? 1.0 : 0.0), 0.0, 0.0, 0.0);
            }
            ENDCG
        }
    }
}
