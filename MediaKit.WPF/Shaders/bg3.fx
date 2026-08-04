// @effect: default
// @animate
float time : register(c0);

float3 gmod3(float3 p, float y) { return p - y * floor(p / y); }

float2 rot2(float2 p, float a)
{
    float c = cos(a), s = sin(a);
    return float2(c * p.x + s * p.y, -s * p.x + c * p.y);
}

float sdBox(float3 p, float3 b)
{
    float3 q = abs(p) - b;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

float box(float3 pos, float scale)
{
    pos *= scale;
    float base = sdBox(pos, float3(0.4, 0.4, 0.1)) / 1.5;
    pos.xy *= 5.0;
    pos.y -= 3.5;
    pos.xy = rot2(pos.xy, 0.75);
    float result = -base;
    return result;
}

float box_set(float3 pos, float gTime)
{
    float3 pos_origin = pos;
    pos = pos_origin;
    pos.y += sin(gTime * 0.4) * 2.5;
    pos.xy = rot2(pos.xy, 0.8);
    float box1 = box(pos, 2.0 - abs(sin(gTime * 0.4)) * 1.5);
    pos = pos_origin;
    pos.y -= sin(gTime * 0.4) * 2.5;
    pos.xy = rot2(pos.xy, 0.8);
    float box2 = box(pos, 2.0 - abs(sin(gTime * 0.4)) * 1.5);
    pos = pos_origin;
    pos.x += sin(gTime * 0.4) * 2.5;
    pos.xy = rot2(pos.xy, 0.8);
    float box3 = box(pos, 2.0 - abs(sin(gTime * 0.4)) * 1.5);
    pos = pos_origin;
    pos.x -= sin(gTime * 0.4) * 2.5;
    pos.xy = rot2(pos.xy, 0.8);
    float box4 = box(pos, 2.0 - abs(sin(gTime * 0.4)) * 1.5);
    pos = pos_origin;
    pos.xy = rot2(pos.xy, 0.8);
    float box5 = box(pos, 0.5) * 6.0;
    pos = pos_origin;
    float box6 = box(pos, 0.5) * 6.0;
    float result = max(max(max(max(max(box1, box2), box3), box4), box5), box6);
    return result;
}

float map(float3 pos, float gTime)
{
    return box_set(pos, gTime);
}

float4 main(float2 texUV : TEXCOORD) : COLOR
{
    float2 p = texUV * 2.0 - 1.0;
    float3 ro = float3(0.0, -0.2, time * 4.0);
    float3 ray = normalize(float3(p, 1.5));
    ray.xy = rot2(ray.xy, sin(time * 0.03) * 5.0);
    ray.yz = rot2(ray.yz, sin(time * 0.05) * 0.2);
    float t = 0.1;
    float3 col = float3(0.0, 0.0, 0.0);
    float ac = 0.0;

    [loop]
    for (int i = 0; i < 99; i++)
    {
        float3 pos = ro + ray * t;
        pos = gmod3(pos - 2.0, 4.0) - 2.0;
        float gTime = time - float(i) * 0.01;

        float d = map(pos, gTime);

        d = max(abs(d), 0.01);
        ac += exp(-d * 23.0);

        t += d * 0.55;
    }

    col = float3(ac * 0.02, ac * 0.02, ac * 0.02);
    col += float3(0.0, 0.2 * abs(sin(time)), 0.5 + sin(time) * 0.2);
    return float4(col, 1.0);
}
