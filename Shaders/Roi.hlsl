struct Instance { float2 center; float2 axisX; float2 axisY; uint summary; uint padding; };
StructuredBuffer<Instance> instances : register(t0);
cbuffer Options : register(b0) { float4 view; float4 origin; float4 tint; float4 rotation; };
struct Pixel { float4 position : SV_POSITION; float4 color : COLOR; };
static const float2 corners[4] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,1) };
static const uint cornerIndex[6] = { 0,1,2,0,2,3 };
float2 ToScreen(float2 local) {
    float2 rotated = float2(local.x*rotation.x-local.y*rotation.y, local.x*rotation.y+local.y*rotation.x);
    return rotated*view.z + origin.xy;
}
Pixel VSMain(uint vertex : SV_VertexID, uint id : SV_InstanceID) {
    Instance r = instances[id];
    Pixel p; p.color = tint;
    float2 screen;
    if (r.summary != 0) {
        if (vertex >= 6 || origin.z != 0) { p.position=float4(2,2,0,1); return p; }
        float2 corner = corners[cornerIndex[vertex]];
        screen = ToScreen(r.center + r.axisX*corner.x + r.axisY*corner.y);
        p.color.a *= origin.w;
    } else {
        uint edge = vertex / 6; uint corner = cornerIndex[vertex % 6];
        float2 c0 = corners[edge], c1 = corners[(edge+1)%4];
        float2 a = ToScreen(r.center + r.axisX*c0.x + r.axisY*c0.y);
        float2 b = ToScreen(r.center + r.axisX*c1.x + r.axisY*c1.y);
        float2 d = b-a;
        float2 normal = float2(-d.y,d.x) * (view.w*.5/max(length(d),.00001));
        screen = corner == 0 ? a+normal : corner == 1 ? b+normal : corner == 2 ? b-normal : a-normal;
    }
    p.position = float4(screen*view.xy + float2(-1,1),0,1);
    return p;
}
float4 PSMain(Pixel p) : SV_TARGET { return p.color; }
