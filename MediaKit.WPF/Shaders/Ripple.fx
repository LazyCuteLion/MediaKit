// @effect: RippleEffect

// @property
float time : register(c0);
// @property
float aspectRatio : register(c1) = 1.7778;
// @property
float4 params : register(c2) = float4(0.1, 50.0, 10.0, 3.0);
// @property
float3 ripple0 : register(c3);
// @property
float3 ripple1 : register(c4);
// @property
float3 ripple2 : register(c5);
// @property
float3 ripple3 : register(c6);

sampler2D input : register(s0);

float3 calcRipple(float2 uv, float2 center, float age, float aspect)
{
    float amplitude = params.x;
    float frequency = params.y;
    float speed = params.z;
    float duration = params.w;

    if (age < 0.0 || age > duration)
        return float3(0.0, 0.0, 0.0);

    float timeFade = 1.0 - age / duration;

    float2 diff = uv - center;
    float dist = length(float2(diff.x, diff.y / aspect));
    if (dist < 0.001)
        return float3(0.0, 0.0, 0.0);

    float2 dir = normalize(float2(diff.x, diff.y / aspect));
    dir.y *= aspect;

    float wavefront = age * speed * 0.03;
    if (dist > wavefront)
        return float3(0.0, 0.0, 0.0);

    float behind = wavefront - dist;
    float envelope = exp(-behind / 0.15) * timeFade / (1.0 + dist * 4.0);

    float wave = sin(frequency * dist - age * speed);
    float displacement = amplitude * wave * envelope;
    float light = amplitude * 0.15 * (1.0 - cos(frequency * dist - age * speed)) * envelope;

    return float3(dir * displacement, light);
}

float3 calcRippleWithReflection(float2 uv, float2 center, float age, float aspect)
{
    float3 result = calcRipple(uv, center, age, aspect);
    result += calcRipple(uv, float2(-center.x, center.y), age, aspect) * 0.3;
    result += calcRipple(uv, float2(2.0 - center.x, center.y), age, aspect) * 0.3;
    result += calcRipple(uv, float2(center.x, -center.y), age, aspect) * 0.3;
    result += calcRipple(uv, float2(center.x, 2.0 - center.y), age, aspect) * 0.3;
    return result;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float3 r0 = calcRippleWithReflection(uv, ripple0.xy, time - ripple0.z, aspectRatio);
    float3 r1 = calcRippleWithReflection(uv, ripple1.xy, time - ripple1.z, aspectRatio);
    float3 r2 = calcRippleWithReflection(uv, ripple2.xy, time - ripple2.z, aspectRatio);
    float3 r3 = calcRippleWithReflection(uv, ripple3.xy, time - ripple3.z, aspectRatio);

    float2 offset = r0.xy + r1.xy + r2.xy + r3.xy;
    float lighting = r0.z + r1.z + r2.z + r3.z;

    float4 color = tex2D(input, uv + offset);
    color.rgb *= (1.0 - lighting);
    return color;
}
