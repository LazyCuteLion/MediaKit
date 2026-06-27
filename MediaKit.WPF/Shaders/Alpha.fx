
float position : register(C0) = float(2.0);


sampler2D input : register(S0);


float4 main(float2 uv : TEXCOORD) : COLOR
{
    if (position == 3)
    {
        //bottom: alpha on bottom, RGB on top, vertical
        if (uv.y > 0.5)
            return float4(0, 0, 0, 0);
        return float4(tex2D(input, float2(uv.x, uv.y)).rgb,
                      tex2D(input, float2(uv.x, 0.5 + uv.y)).r);
    }
    else if (position == 0)
    {
        //left: alpha on left, RGB on right, horizontal
        if (uv.x > 0.5)
            return float4(0, 0, 0, 0);
        return float4(tex2D(input, float2(0.5 + uv.x, uv.y)).rgb,
                      tex2D(input, float2(uv.x, uv.y)).r);
    }
    else if (position == 1)
    {
        //top: alpha on top, RGB on bottom, vertical
        if (uv.y > 0.5)
            return float4(0, 0, 0, 0);
        return float4(tex2D(input, float2(uv.x, 0.5 + uv.y)).rgb,
                      tex2D(input, float2(uv.x, uv.y)).r);
    }
    else
    {
        //right: alpha on right, RGB on left, horizontal (default)
        if (uv.x > 0.5)
            return float4(0, 0, 0, 0);
        return float4(tex2D(input, float2(uv.x, uv.y)).rgb,
                      tex2D(input, float2(0.5 + uv.x, uv.y)).r);
    }
}