// @effect: default
// @animate
float time : register(c0);

float2 tanh2(float2 x)
{
    x = clamp(x, -10.0, 10.0);
    float2 e = exp(2.0 * x);
    return (e - 1.0) / (e + 1.0);
}

float4 main(float2 texUV : TEXCOORD) : COLOR
{
    float2 v = float2(0.0, 0.0);
    float2 u = 0.2 * (texUV * 2.0 - 1.0);

    float4 z = float4(1.0, 2.0, 3.0, 0.0);
    float4 o = z;

    float a = 0.5;
    float t = time;

    for (float i = 1.0; i < 19.0; i += 1.0)
    {
        a += 0.03;
        t += 1.0;
        v = cos(t - 7.0 * u * pow(a, i)) - 5.0 * u;

        float4 c4 = cos(i + 0.02 * t - float4(0.0, 11.0, 33.0, 0.0));
        u = mul(float2x2(c4.x, c4.y, c4.z, c4.w), u);

        float d = dot(u, u);
        u += tanh2(40.0 * d * cos(100.0 * u.yx + t)) / 200.0
           + 0.2 * a * u
           + cos(4.0 / exp(dot(o, o) / 100.0) + t) / 300.0;

        o += (1.0 + cos(z + t))
           / length((1.0 + i * dot(v, v)) * sin(1.5 * u / (0.5 - dot(u, u)) - 9.0 * u.yx + t));
    }

    o = 25.6 / (min(o, 13.0) + 164.0 / o) - dot(u, u) / 250.0;
    return float4(o.rgb, 1.0);
}
