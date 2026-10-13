cbuffer DrawOptions : register(b0)
{
    float2 Center;
    float2 AxisX;
    float2 AxisY;
    uint Columns;
    uint Rows;
    float PitchPixels;
    float LinePixels;
    uint SummaryStride;
    uint OverrideCount;
    float4 Color;
};
StructuredBuffer<uint> Overrides : register(t0);
StructuredBuffer<uint4> Blocks : register(t1);
struct PixelInput { float4 Position : SV_POSITION; float2 Cell : TEXCOORD0; nointerpolation float4 Block : TEXCOORD1; };

PixelInput VSMain(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    const float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };
    float2 p = corners[vertex];
    uint4 block = Blocks[instance];
    float2 cell = lerp((float2)block.xy, (float2)block.zw, p * .5 + .5);
    cell += p * (LinePixels * .5 + .5) / PitchPixels;
    p = cell / float2(Columns, Rows) * 2 - 1;
    PixelInput output;
    output.Position = float4(Center + AxisX * p.x + AxisY * p.y, 0, 1);
    output.Cell = cell;
    output.Block = block;
    return output;
}

bool IsOverridden(uint index)
{
    uint low = 0, high = OverrideCount;
    while (low < high)
    {
        uint middle = low + (high - low) / 2;
        uint value = Overrides[middle];
        if (value == index) return true;
        if (value < index) low = middle + 1;
        else high = middle;
    }
    return false;
}

float Coverage(float2 cell, float4 block)
{
    float2 tile = floor(clamp(cell, block.xy + .001, block.zw - .001) / SummaryStride) * SummaryStride;
    // A partial batch can lie entirely inside a summary tile. Its occupied edges must remain visible.
    float2 start = max(tile, block.xy);
    float2 size = min(tile + SummaryStride, block.zw) - start;
    float2 halfSize = size * .5 - float2(.2, .3);
    float2 delta = (abs(cell - start - size * .5) - halfSize) * PitchPixels;
    float signedDistance = length(max(delta, 0)) + min(max(delta.x, delta.y), 0);
    return saturate(LinePixels * .5 + .5 - abs(signedDistance));
}

float4 PSMain(PixelInput input) : SV_TARGET
{
    float2 cell = clamp(input.Cell, input.Block.xy + .001, input.Block.zw - .001);
    uint index = (uint)floor(cell.y) * Columns + (uint)floor(cell.x);
    if (SummaryStride == 1 && IsOverridden(index)) discard;
    float coverage = Coverage(input.Cell, input.Block);
    if (coverage <= 0) discard;
    return float4(Color.rgb, Color.a * coverage);
}

float4 PSMainCpu(PixelInput input) : SV_TARGET
{
    float2 cell = clamp(input.Cell, input.Block.xy + .001, input.Block.zw - .001);
    float coverage = Coverage(input.Cell, input.Block);
    // WARP는 빈 픽셀의 수정 ID 조회를 생략한다. GPU는 기존 계산 순서를 유지한다.
    [branch] if (coverage <= 0) { discard; return 0; }
    uint index = (uint)floor(cell.y) * Columns + (uint)floor(cell.x);
    if (SummaryStride == 1 && IsOverridden(index)) discard;
    return float4(Color.rgb, Color.a * coverage);
}
