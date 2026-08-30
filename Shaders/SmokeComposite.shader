// Composites the half-resolution smoke buffer back onto the screen.
//
// A plain bilinear stretch would bleed smoke across geometry edges: a half-res texel
// straddling a silhouette holds a blend of "in front of the rocket" and "behind it", and
// smearing that over full-res pixels produces halos. Nearest-depth upsampling picks, per
// destination pixel, the source texel whose scene depth is closest to this pixel's own -
// so an edge pixel takes its colour from a source texel on the same side of the edge.
Shader "VolumetricContrails/SmokeComposite"
{
    Properties
    {
        _MainTex ("Half-res smoke", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        // the source is already premultiplied (see SmokeVolume.shader), so the classic
        // over-operator is One / OneMinusSrcAlpha rather than SrcAlpha / OneMinusSrcAlpha
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize; // .xy = 1/width, 1/height of the HALF-res buffer
            sampler2D_float _CameraDepthTexture;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 texel = _MainTex_TexelSize.xy;

                // depth this full-res pixel actually belongs to
                float targetDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));

                // the four half-res texels around it, and the scene depth each of them
                // was rendered against
                float2 uvs[4];
                uvs[0] = i.uv + float2(-0.5, -0.5) * texel;
                uvs[1] = i.uv + float2( 0.5, -0.5) * texel;
                uvs[2] = i.uv + float2(-0.5,  0.5) * texel;
                uvs[3] = i.uv + float2( 0.5,  0.5) * texel;

                float bestDiff = 1e20;
                int best = 0;
                for (int s = 0; s < 4; s++)
                {
                    float d = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uvs[s]));
                    float diff = abs(d - targetDepth);
                    if (diff < bestDiff) { bestDiff = diff; best = s; }
                }

                // Where all four agree on depth there is no edge to protect, so take the
                // smooth bilinear sample; only fall back to the single nearest texel where
                // they disagree, which is exactly where point sampling avoids a halo.
                fixed4 smooth = tex2D(_MainTex, i.uv);
                fixed4 nearest = tex2D(_MainTex, uvs[best]);
                float edge = saturate(bestDiff * 0.5);
                return lerp(smooth, nearest, edge);
            }
            ENDCG
        }
    }
    FallBack Off
}
