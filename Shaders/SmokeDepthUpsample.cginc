#ifndef PLUME_DEPTH_UPSAMPLE_INCLUDED
#define PLUME_DEPTH_UPSAMPLE_INCLUDED

float4 _PlumeJitter;

struct PlumeDepthSelection
{
    float2 uv0, uv1, uv2, uv3;
    float4 weights;
};

float2 PlumeSampleRayUV(float2 center)
{
    // Intended subpixel phase of this low-res texel. The volume pass projects
    // its actual jittered proxy point; that derivative-based jitter can differ
    // slightly from this screen-space phase on a tilted proxy face.
    return saturate(center + _PlumeJitter.xy * abs(_MainTex_TexelSize.xy));
}

PlumeDepthSelection SelectPlumeDepthSamples(float2 uv)
{
    float2 texel = abs(_MainTex_TexelSize.xy);
    float2 grid = uv / texel - 0.5;
    float2 lower = (floor(grid) + 0.5) * texel;
    float2 f = frac(grid);
    PlumeDepthSelection s;
    s.uv0 = clamp(lower, texel * 0.5, 1.0 - texel * 0.5);
    s.uv1 = clamp(lower + float2(texel.x, 0.0), texel * 0.5, 1.0 - texel * 0.5);
    s.uv2 = clamp(lower + float2(0.0, texel.y), texel * 0.5, 1.0 - texel * 0.5);
    s.uv3 = clamp(lower + texel, texel * 0.5, 1.0 - texel * 0.5);

    float target = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
    float4 difference;
    difference.x = abs(LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, PlumeSampleRayUV(s.uv0))) - target);
    difference.y = abs(LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, PlumeSampleRayUV(s.uv1))) - target);
    difference.z = abs(LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, PlumeSampleRayUV(s.uv2))) - target);
    difference.w = abs(LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, PlumeSampleRayUV(s.uv3))) - target);

    float best = difference.x;
    float4 nearest = float4(1, 0, 0, 0);
    if (difference.y < best) { best = difference.y; nearest = float4(0, 1, 0, 0); }
    if (difference.z < best) { best = difference.z; nearest = float4(0, 0, 1, 0); }
    if (difference.w < best) { best = difference.w; nearest = float4(0, 0, 0, 1); }
    float worst = max(max(difference.x, difference.y), max(difference.z, difference.w));
    // Detect disagreement among all candidates, even when one matches exactly.
    // A relative tolerance avoids forcing point samples on distant smooth slopes.
    float edge = saturate((worst - best) / max(2.0, 0.1 * target));
    float4 spatial = float4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y),
        (1.0 - f.x) * f.y, f.x * f.y);
    s.weights = lerp(spatial, nearest, edge);
    return s;
}

float4 SamplePlumeCurrentColor(PlumeDepthSelection s)
{
    // Explicit texel centres avoid unintended bilinear blending in the nearest
    // branch. Color and aux consumers use the identical weights and centres.
    return tex2D(_MainTex, s.uv0) * s.weights.x + tex2D(_MainTex, s.uv1) * s.weights.y +
        tex2D(_MainTex, s.uv2) * s.weights.z + tex2D(_MainTex, s.uv3) * s.weights.w;
}

#endif
