// Live preview of Post's colour work. Corrections and LUTs are baked on the CPU into
// one lookup table laid out as a strip, so this only samples that table and then adds
// the vignette. The exported render is still produced by ffmpeg.

sampler2D implicitInput : register(s0);
sampler2D colorTable    : register(s1);

float lutSize  : register(c0);   // lattice size of the table, e.g. 17
float vignette : register(c1);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 source = tex2D(implicitInput, uv);
    float alpha = source.a;
    float3 rgb = saturate(source.rgb / max(alpha, 0.0001));

    // The strip holds lutSize slices of lutSize x lutSize; blue picks the slice, and
    // the coordinates are inset by half a texel so bilinear never bleeds across one.
    float slice = rgb.b * (lutSize - 1.0);
    float slice0 = floor(slice);
    float sliceWidth = 1.0 / lutSize;
    float texel = sliceWidth / lutSize;
    float u = texel * 0.5 + rgb.r * texel * (lutSize - 1.0);
    float v = (0.5 + rgb.g * (lutSize - 1.0)) / lutSize;
    float3 low = tex2D(colorTable, float2(slice0 * sliceWidth + u, v)).rgb;
    float3 high = tex2D(colorTable, float2(min(slice0 + 1.0, lutSize - 1.0) * sliceWidth + u, v)).rgb;
    rgb = lerp(low, high, slice - slice0);

    // Darken towards the corners; 1.0 is the corner.
    float radius = saturate(length(uv - 0.5) * 1.41421);
    rgb = rgb * (1.0 - vignette * radius);

    return float4(saturate(rgb) * alpha, alpha);
}
