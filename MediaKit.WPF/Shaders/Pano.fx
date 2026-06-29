#define PI 3.1415926535897932384626433832795
#define DEG2RAD  0.01745329251994329576923690768489

//rotationX,rotationY,zoom,fov
float4 params : register(C0) = float4(0.5, 0.5, 0.5, 90.0);

//scaleX(view.width/source.width),scaleY(view.height/source.height),aspectRatio(view.width/view.height)
float3 view : register(C1) = float3(1.0, 1.0, 1.0);

sampler2D input : register(S0);

float3 rotateXY(float3 p, float2 angle)
{
    float2 c = cos(angle);
    float2 s = sin(angle);
    p = float3(p.x, c.x * p.y + s.x * p.z, -s.x * p.y + c.x * p.z);
    return float3(c.y * p.x + s.y * p.z, p.y, -s.y * p.x + c.y * p.z);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float rotationX = params.x;
    float rotationY = params.y;
    float zoom = params.z;
    float fov = params.w;
    float scaleX = view.x;
    float scaleY = view.y;
    float aspectRatio = view.z;

    if (uv.x > scaleX || uv.y > scaleY)
        return float4(0, 0, 0, 0);

    float2 sampleUV = float2(uv.x / scaleX - 0.5, uv.y / scaleY - 0.5);

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

    return tex2D(input, texCoord);
}
