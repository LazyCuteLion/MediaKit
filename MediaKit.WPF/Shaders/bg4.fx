// @effect: default
// @animate
float time : register(c0);

float gmod(float x, float y) { return x - y * floor(x / y); }

float2 rot2(float2 p, float a)
{
    float c = cos(a), s = sin(a);
    return float2(c * p.x + s * p.y, -s * p.x + c * p.y);
}

static const float pi = 3.14159265359;
static const float pi2 = 6.28318530718;

float2 pmod(float2 p, float r)
{
    float a = atan2(p.x, p.y) + pi / r;
    float n = pi2 / r;
    a = floor(a / n) * n;
    return rot2(p, -a);
}

float box(float3 p, float3 b)
{
    float3 d = abs(p) - b;
    return min(max(d.x, max(d.y, d.z)), 0.0) + length(max(d, 0.0));
}

float ifsBox(float3 p)
{
    for (int i = 0; i < 5; i++)
    {
        p = abs(p) - 1.0;
        p.xy = rot2(p.xy, time * 0.3);
        p.xz = rot2(p.xz, time * 0.1);
    }
    p.xz = rot2(p.xz, time);
    return box(p, float3(0.4, 0.8, 0.3));
}

float map(float3 p, float3 cPos)
{
    float3 p1 = p;
    p1.x = gmod(p1.x - 5.0, 10.0) - 5.0;
    p1.y = gmod(p1.y - 5.0, 10.0) - 5.0;
    p1.z = gmod(p1.z, 16.0) - 8.0;
    p1.xy = pmod(p1.xy, 5.0);
    return ifsBox(p1);
}

float4 main(float2 texUV : TEXCOORD) : COLOR
{
    float2 p = float2(texUV.x, 1.0 - texUV.y) * 2.0 - 1.0;

    float3 cPos = float3(0.0, 0.0, -3.0 * time);
    float3 cDir = normalize(float3(0.0, 0.0, -1.0));
    float3 cUp = float3(sin(time), 1.0, 0.0);
    float3 cSide = cross(cDir, cUp);

    float3 ray = normalize(cSide * p.x + cUp * p.y + cDir);

    float acc = 0.0;
    float acc2 = 0.0;
    float t = 0.0;
    [loop]
    for (int i = 0; i < 99; i++)
    {
        float3 pos = cPos + ray * t;
        float dist = map(pos, cPos);
        dist = max(abs(dist), 0.02);
        float a = exp(-dist * 3.0);
        if (gmod(length(pos) + 24.0 * time, 30.0) < 3.0)
        {
            a *= 2.0;
            acc2 += a;
        }
        acc += a;
        t += dist * 0.5;
    }

    float3 col = float3(acc * 0.01, acc * 0.011 + acc2 * 0.002, acc * 0.012 + acc2 * 0.005);
    float alpha = 1.0 - t * 0.03;
    float4 fragColor = float4(col * alpha, alpha);
    return fragColor;
}
