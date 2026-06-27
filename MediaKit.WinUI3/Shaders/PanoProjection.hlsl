#define PI 3.1415926535897932384626433832795
#define DEG2RAD 0.01745329251994329576923690768489

// Win2D PixelShaderEffect properties (cbuffer at b0)
cbuffer constants : register(b0)
{
    float4 params;  // rotationX, rotationY, zoom, fov
    float4 view;    // boundX, boundY, aspectRatio, unused
};

// Source1 texture (t0)
Texture2D<float4> inputTexture : register(t0);
// 注意：Win2D 运行时强制使用 CLAMP 寻址，此处声明仅供编译用
SamplerState inputSampler : register(s0);

float3 rotateXY(float3 p, float2 angle)
{
    float2 c = cos(angle);
    float2 s = sin(angle);
    p = float3(p.x, c.x * p.y + s.x * p.z, -s.x * p.y + c.x * p.z);
    return float3(c.y * p.x + s.y * p.z, p.y, -s.y * p.x + c.y * p.z);
}

// 手动实现 WRAP 采样：在 U 方向边界处双采样插值，避免 CLAMP 导致的拼缝
float4 SampleWrapU(float2 uv)
{
    float u = frac(uv.x);
    float v = saturate(uv.y);

    // 修正 ddx：atan2 环绕点处 raw ddx 跳变约±1.0，需还原为实际小增量
    float raw_dx = ddx(uv.x);
    float pixelWidth = abs(raw_dx);
    if (pixelWidth > 0.5)
        pixelWidth = abs(raw_dx - sign(raw_dx));
    pixelWidth = max(pixelWidth, 0.0001);

    float4 center = inputTexture.SampleLevel(inputSampler, float2(u, v), 0);

    // 左边界附近：混合右侧（尾部）纹理实现环绕
    if (u < pixelWidth)
    {
        float4 wrapped = inputTexture.SampleLevel(inputSampler, float2(1.0 - pixelWidth + u, v), 0);
        float t = u / pixelWidth * 0.5 + 0.5;
        return lerp(wrapped, center, t);
    }
    // 右边界附近：混合左侧（头部）纹理实现环绕
    if (u > 1.0 - pixelWidth)
    {
        float4 wrapped = inputTexture.SampleLevel(inputSampler, float2(pixelWidth - (1.0 - u), v), 0);
        float t = (1.0 - u) / pixelWidth * 0.5 + 0.5;
        return lerp(wrapped, center, t);
    }

    return center;
}

float4 main(
    float4 pos : SV_POSITION,
    float4 posScene : SCENE_POSITION,
    float2 uv : TEXCOORD0
) : SV_Target
{
    float rotationX = params.x;
    float rotationY = params.y;
    float zoom = params.z;
    float fov = params.w;
    float boundX = view.x;
    float boundY = view.y;
    float aspectRatio = view.z;

    if (uv.x > boundX || uv.y > boundY)
        return float4(0, 0, 0, 0);

    float2 sampleUV = float2(uv.x / boundX - 0.5, uv.y / boundY - 0.5);

    float hfovRad = fov * DEG2RAD;
    float vfovRad = 2.0 * atan(tan(hfovRad * 0.5) / aspectRatio);

    float3 camDir = normalize(float3(
        -sampleUV.x * tan(hfovRad * 0.5),
        sampleUV.y * tan(vfovRad * 0.5),
        zoom
    ));

    float3 camRot = float3(
        (rotationX - 0.5) * 2.0 * PI,
        (rotationY - 0.5) * PI,
        0.0
    );

    float3 rd = normalize(rotateXY(camDir, camRot.yx));
    float2 texCoord = float2(atan2(rd.z, rd.x) + PI, acos(-rd.y)) / float2(2.0 * PI, PI);

    return SampleWrapU(texCoord);
}
