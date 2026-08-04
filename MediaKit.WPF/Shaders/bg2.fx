// @effect: default
// @animate
float time : register(c0);

float2 rot2(float2 p, float a)
{
    float c = cos(a), s = sin(a);
    return float2(c * p.x + s * p.y, -s * p.x + c * p.y);
}

static const float3x3 m3 = float3x3(
    0.6434234, 1.0814562, -1.3860681,
    -1.6962191, 0.6301643, -0.2957339,
    0.2926266, 1.3432028, 1.1838427);

float mag2(float2 p) { return dot(p, p); }
float linstep(float mn, float mx, float x) { return clamp((x - mn) / (mx - mn), 0.0, 1.0); }
float2 disp(float t) { return float2(sin(t * 0.22), cos(t * 0.175)) * 2.0; }

float2 map(float3 p, float prm1, float bsMoY)
{
    float3 p2 = p;
    p2.xy -= disp(p.z);
    p.xy = rot2(p.xy, sin(p.z + time) * (0.1 + prm1 * 0.05) + time * 0.09);
    float cl = mag2(p2.xy);
    float d = 0.0;
    p *= 0.61;
    float z = 1.0;
    float trk = 1.0;
    float dspAmp = 0.1 + prm1 * 0.2;
    for (int i = 0; i < 5; i++)
    {
        p += sin(p.zxy * 0.75 * trk + time * trk * 0.8) * dspAmp;
        d -= abs(dot(cos(p), sin(p.yzx)) * z);
        z *= 0.57;
        trk *= 1.4;
        p = mul(m3, p);
    }
    d = abs(d + prm1 * 3.0) + prm1 * 0.3 - 2.5 + bsMoY;
    return float2(d + cl * 0.2 + 0.25, cl);
}

float4 render(float3 ro, float3 rd, float tm, float prm1, float bsMoY)
{
    float4 rez = float4(0.0, 0.0, 0.0, 0.0);
    const float ldst = 8.0;
    float t = 1.5;
    float fogT = 0.0;
    [loop]
    for (int i = 0; i < 50; i++)
    {
        if (rez.a > 0.99) break;

        float3 pos = ro + t * rd;
        float2 mpv = map(pos, prm1, bsMoY);
        float den = clamp(mpv.x - 0.3, 0.0, 1.0) * 1.5;
        float dn = clamp(mpv.x + 2.0, 0.0, 3.0);

        float4 col = float4(0.0, 0.0, 0.0, 0.0);
        if (mpv.x > 0.6)
        {
            col = float4(sin(float3(5.0, 0.4, 0.2) + mpv.y * 0.1 + sin(pos.z * 0.4) * 0.5 + 1.8) * 0.5 + 0.5, 0.08);
            col *= den * den * den;
            col.rgb *= linstep(4.0, -2.5, mpv.x) * 2.3;
            float dif = clamp((den - map(pos + 0.8, prm1, bsMoY).x) / 6.0, 0.001, 1.0);
            col.xyz *= den * (float3(0.005, 0.045, 0.075) + 1.5 * float3(0.033, 0.07, 0.03) * dif);
        }

        float fogC = exp(t * 0.2 - 2.2);
        col += float4(0.06, 0.11, 0.11, 0.1) * clamp(fogC - fogT, 0.0, 1.0);
        fogT = fogC;
        rez = rez + col * (1.0 - rez.a);
        t += clamp(0.5 - dn * dn * 0.05, 0.15, 0.5);
    }
    return clamp(rez, 0.0, 1.0);
}

float getsat(float3 c)
{
    float mi = min(min(c.x, c.y), c.z);
    float ma = max(max(c.x, c.y), c.z);
    return (ma - mi) / (ma + 1e-7);
}

float3 iLerp(float3 a, float3 b, float x)
{
    float3 ic = lerp(a, b, x) + float3(1e-6, 0.0, 0.0);
    float sd = abs(getsat(ic) - lerp(getsat(a), getsat(b), x));
    float3 dir = normalize(float3(2.0 * ic.x - ic.y - ic.z, 2.0 * ic.y - ic.x - ic.z, 2.0 * ic.z - ic.y - ic.x));
    float lgt = dot(float3(1.0, 1.0, 1.0), ic);
    float ff = dot(dir, normalize(ic));
    ic += 1.5 * dir * sd * ff * lgt;
    return clamp(ic, 0.0, 1.0);
}

float4 main(float2 texUV : TEXCOORD) : COLOR
{
    float2 q = texUV;
    float2 p = texUV - 0.5;
    float2 bsMo = float2(0.0, 0.0);

    float tm = time * 3.0;
    float3 ro = float3(0.0, 0.0, tm);
    ro += float3(sin(time) * 0.5, sin(time) * 0.0, 0.0);

    float dspAmp = 0.85;
    ro.xy += disp(ro.z) * dspAmp;
    float tgtDst = 3.5;

    float3 target = normalize(ro - float3(disp(tm + tgtDst) * dspAmp, tm + tgtDst));
    ro.x -= bsMo.x * 2.0;
    float3 rightdir = normalize(cross(target, float3(0.0, 1.0, 0.0)));
    float3 updir = normalize(cross(rightdir, target));
    rightdir = normalize(cross(updir, target));
    float3 rd = normalize((p.x * rightdir + p.y * updir) * 1.0 - target);
    rd.xy = rot2(rd.xy, -disp(tm + 3.5).x * 0.2 + bsMo.x);

    float prm1 = smoothstep(-0.4, 0.4, sin(time * 0.3));
    float4 scn = render(ro, rd, tm, prm1, bsMo.y);

    float3 col = scn.rgb;
    col = iLerp(col.bgr, col.rgb, clamp(1.0 - prm1, 0.05, 1.0));
    col = pow(col, float3(0.55, 0.65, 0.6)) * float3(1.0, 0.97, 0.9);
    col *= pow(16.0 * q.x * q.y * (1.0 - q.x) * (1.0 - q.y), 0.12) * 0.7 + 0.3;

    return float4(col, 1.0);
}
