cbuffer DrawOptions : register(b0)
{
    float2 Center;
    float2 AxisX;
    float2 AxisY;
    uint MaskCount;
    uint VertexCount;
    uint IsCircle;
    uint SelectedIndex;
    float Dpi;
    uint Padding;
};
struct MaskData { float4 Bounds; float4 Options; };
StructuredBuffer<MaskData> Masks : register(t0);
StructuredBuffer<float2> Outline : register(t1);
struct PixelInput { float4 Position : SV_POSITION; float2 Uv : TEXCOORD0; };

PixelInput VSMain(uint vertex : SV_VertexID)
{
    const float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };
    float2 corner = corners[vertex];
    PixelInput output;
    output.Position = float4(Center + AxisX * corner.x + AxisY * corner.y, 0, 1);
    output.Uv = corner * .5 + .5;
    return output;
}

bool InsideOwner(float2 p)
{
    if (IsCircle != 0) return dot(p, p) <= .25;
    bool inside = false;
    for (uint i = 0; i < VertexCount; i++)
    {
        float2 a = Outline[i];
        float2 b = Outline[(i + 1) % VertexCount];
        if ((a.y > p.y) != (b.y > p.y) && p.x < a.x + (p.y - a.y) * (b.x - a.x) / (b.y - a.y))
            inside = !inside;
    }
    return inside;
}

float4 PSMain(PixelInput input) : SV_TARGET
{
    if (!InsideOwner(input.Uv - .5)) discard;
    bool covered = false;
    bool selected = false;
    for (uint i = 0; i < MaskCount; i++)
    {
        MaskData mask = Masks[i];
        float2 local = (input.Uv - mask.Bounds.xy) / (mask.Bounds.zw - mask.Bounds.xy) * 2 - 1;
        bool inside = mask.Options.x > .5 ? dot(local, local) <= 1 : all(abs(local) <= 1);
        covered = covered || inside;
        selected = selected || (inside && i == SelectedIndex);
    }
    if (!covered) discard;
    float hatch = frac((input.Position.x + input.Position.y) / (9 * Dpi)) < .2 ? 1 : 0;
    return float4(.88, .20, .37, (selected ? .25 : .13) + hatch * .28);
}
