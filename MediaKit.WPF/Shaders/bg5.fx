// @effect: default
// @animate
float time : register(c0);

float3 palette(float d)
{
    return lerp(float3(0.2, 0.7, 0.9), float3(1.0, 0.0, 1.0), d);
}

float2 rot2(float2 p, float a)
{
    float c = cos(a), s = sin(a);
    return float2(c * p.x + s * p.y, -s * p.x + c * p.y);
}

float map(float3 p)
{
    for (int i = 0; i < 8; i++)
    {
        float t = time * 0.2;
        p.xz = rot2(p.xz, t);
        p.xy = rot2(p.xy, t * 1.89);
        p.xz = abs(p.xz);
        p.xz -= 0.5;
    }
    return dot(sign(p), p) / 5.0;
}

float4 rm(float3 ro, float3 rd)
{
    float t = 0.0;
    float3 col = float3(0.0, 0.0, 0.0);
    float d = 0.0;
    [loop]
    for (float i = 0.0; i < 64.0; i++)
    {
        float3 p = ro + rd * t;
        d = map(p) * 0.5;
        if (d < 0.02) break;
        if (d > 100.0) break;
        col += palette(length(p) * 0.1) / (400.0 * d);
        t += d;
    }
    return float4(col, 1.0);
}

float4 main(float2 texUV : TEXCOORD) : COLOR
{
    float2 uv = float2(texUV.x, 1.0 - texUV.y) - 0.5;

    float3 ro = float3(0.0, 0.0, -50.0);
    ro.xz = rot2(ro.xz, time);

    float3 cf = normalize(-ro);
    float3 cs = normalize(cross(cf, float3(0.0, 1.0, 0.0)));
    float3 cu = normalize(cross(cf, cs));

    float3 uuv = ro + cf * 3.0 + uv.x * cs + uv.y * cu;
    float3 rd = normalize(uuv - ro);

    float4 col = rm(ro, rd);
    return col;
}
