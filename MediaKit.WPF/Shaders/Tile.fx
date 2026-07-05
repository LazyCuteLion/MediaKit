// @effect: TileEffect
sampler2D input : register(s0);

// @property: Rows = 1.0
float rows : register(c0) = float(1.0);
// @property: Columns = 1.0
float columns : register(c1) = float(1.0);
float2 spacing : register(c2) = float2(0.0, 0.0); // UV space (x, y)
// @property: SpacingColor : Color = Transparent
float4 spacingColor : register(c3) = float4(0, 0, 0, 0);
float aspectRatio : register(c4) = float(1.0); // targetAspect / videoAspect

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // Layout: edge margin + cells + gaps + edge margin
    // (columns+1) vertical gaps, (rows+1) horizontal gaps (including edges)
    float totalGapX = spacing.x * (columns + 1.0);
    float totalGapY = spacing.y * (rows + 1.0);
    float cellW = (1.0 - totalGapX) / columns;
    float cellH = (1.0 - totalGapY) / rows;

    // Early out if cells too small
    if (cellW <= 0.0 || cellH <= 0.0)
    {
        float4 c = spacingColor;
        c.rgb *= c.a;
        return c;
    }

    // Step between adjacent cell starts
    float stepX = cellW + spacing.x;
    float stepY = cellH + spacing.y;

    // Offset by edge margin
    float ax = uv.x - spacing.x;
    float ay = uv.y - spacing.y;

    // Left/top edge margin
    if (ax < 0.0 || ay < 0.0)
    {
        float4 c = spacingColor;
        c.rgb *= c.a;
        return c;
    }

    // Which cell (clamped)
    float colIdx = min(floor(ax / stepX), columns - 1.0);
    float rowIdx = min(floor(ay / stepY), rows - 1.0);

    // Local position within cell
    float lx = ax - colIdx * stepX;
    float ly = ay - rowIdx * stepY;

    // In gap or right/bottom edge margin
    if (lx > cellW || ly > cellH)
    {
        float4 c = spacingColor;
        c.rgb *= c.a;
        return c;
    }

    // Local UV [0,1] within cell
    float2 localUV = float2(lx / cellW, ly / cellH);

    // Aspect ratio correction: fit video preserving ratio
    float cellRatio = (cellW / cellH) * aspectRatio;
    float2 sampleUV = localUV;

    if (cellRatio > 1.0)
    {
        // Cell wider than video: fit height, center horizontally
        sampleUV.x = (localUV.x - 0.5) / cellRatio + 0.5;
        if (sampleUV.x < 0.0 || sampleUV.x > 1.0)
        {
            float4 c = spacingColor;
            c.rgb *= c.a;
            return c;
        }
    }
    else if (cellRatio < 1.0)
    {
        // Cell taller than video: fit width, center vertically
        sampleUV.y = (localUV.y - 0.5) * cellRatio + 0.5;
        if (sampleUV.y < 0.0 || sampleUV.y > 1.0)
        {
            float4 c = spacingColor;
            c.rgb *= c.a;
            return c;
        }
    }

    return tex2D(input, sampleUV);
}
