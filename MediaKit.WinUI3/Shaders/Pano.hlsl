#define PI 3.1415926535897932384626433832795

cbuffer constants : register(b0)
{
    float4 viewParams;   // x=scaleX, y=scaleY, z=zoom, w=(未用)
    float4 fovTan;       // x=tan(hfov/2), y=tan(vfov/2), z/w=（未用）
    float4 rotSinCos;    // x=sin(pitch), y=cos(pitch), z=sin(yaw), w=cos(yaw)
};

Texture2D<float4> inputTexture : register(t0);
SamplerState inputSampler : register(s0);

float4 main(
    float4 pos : SV_POSITION,
    float4 posScene : SCENE_POSITION,
    float2 uv : TEXCOORD0
) : SV_Target
{
    // 所有仅依赖 uniform（视角/FOV）的量已在 CPU 端每帧预计算并经 cbuffer 传入，
    // 避免在每个像素重复计算 tan/atan/sin/cos 等昂贵的超越函数。
    float scaleX = viewParams.x;
    float scaleY = viewParams.y;
    float zoom = viewParams.z;
    float tanHalfH = fovTan.x;
    float tanHalfV = fovTan.y;

    if (uv.x > scaleX || uv.y > scaleY)
        return float4(0, 0, 0, 0);

    float2 sampleUV = float2(uv.x / scaleX - 0.5, uv.y / scaleY - 0.5);

    // 相机方向（视口射线）
    float3 camDir = normalize(float3(
        -sampleUV.x * tanHalfH,
        sampleUV.y * tanHalfV,
        zoom
    ));

    // 绕 X（俯仰 pitch）、Y（偏航 yaw）旋转，使用预计算的 sin/cos，等价于原 rotateXY(camDir, camRot.yx)
    float sp = rotSinCos.x; // sin(pitch)
    float cp = rotSinCos.y; // cos(pitch)
    float sy = rotSinCos.z; // sin(yaw)
    float cy = rotSinCos.w; // cos(yaw)
    float3 t = float3(camDir.x, cp * camDir.y + sp * camDir.z, -sp * camDir.y + cp * camDir.z);
    float3 rd = normalize(float3(cy * t.x + sy * t.z, t.y, -sy * t.x + cy * t.z));

    // 球面方向 → 等距柱状投影纹理坐标
    float2 texCoord = float2(atan2(rd.z, rd.x) + PI, acos(-rd.y)) / float2(2.0 * PI, PI);

    return inputTexture.Sample(inputSampler, texCoord);
}
