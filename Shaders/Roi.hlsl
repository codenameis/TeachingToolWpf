struct Instance {
    float2 center;
    float2 axisX;
    float2 axisY;
    uint summary;
    uint padding;
};
StructuredBuffer<Instance> instances : register(t0);
StructuredBuffer<uint> roles : register(t1);

// RoiDrawConstants와 동일한 128바이트 배치. 각 줄의 묶음은 16바이트 경계를 유지한다.
cbuffer Options : register(b0) {
    float2 clipScale; float imageScale; float reservedView;
    float2 screenOrigin; float shadowPass; float summaryOpacity;
    float4 shadowColor;
    float2 rotation; float2 reservedRotation;
    float4 roleColors[3];
    float4 roleWidths;
};
struct Pixel { float4 position : SV_POSITION; float4 color : COLOR; };
static const uint RectangleEdgeCount = 4;
static const uint VerticesPerEdge = 6;
static const uint ParentRole = 2;
static const float2 corners[RectangleEdgeCount] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,1) };
static const uint cornerIndex[VerticesPerEdge] = { 0,1,2,0,2,3 };

float2 ToScreen(float2 localPosition) {
    float2 rotatedPosition = float2(
        localPosition.x*rotation.x-localPosition.y*rotation.y,
        localPosition.x*rotation.y+localPosition.y*rotation.x);
    return rotatedPosition*imageScale + screenOrigin;
}

float2 StrokeCorner(uint corner, float2 edgeStart, float2 edgeEnd, float2 halfWidthNormal) {
    if (corner == 0) return edgeStart + halfWidthNormal;
    if (corner == 1) return edgeEnd + halfWidthNormal;
    if (corner == 2) return edgeEnd - halfWidthNormal;
    return edgeStart - halfWidthNormal;
}

Pixel VSMain(uint vertexIndex : SV_VertexID, uint instanceIndex : SV_InstanceID) {
    Instance roi = instances[instanceIndex];
    uint role = min(roles[instanceIndex], ParentRole);
    Pixel pixel;
    pixel.color = shadowPass != 0 ? shadowColor : roleColors[role];
    float2 screenPosition;
    if (roi.summary != 0) {
        if (vertexIndex >= VerticesPerEdge || shadowPass != 0) {
            pixel.position = float4(2,2,0,1);
            return pixel;
        }
        float2 summaryCorner = corners[cornerIndex[vertexIndex]];
        screenPosition = ToScreen(roi.center + roi.axisX*summaryCorner.x + roi.axisY*summaryCorner.y);
        pixel.color.a *= summaryOpacity;
    } else {
        uint edgeIndex = vertexIndex / VerticesPerEdge;
        uint strokeCorner = cornerIndex[vertexIndex % VerticesPerEdge];
        float2 localEdgeStart = corners[edgeIndex];
        float2 localEdgeEnd = corners[(edgeIndex+1)%RectangleEdgeCount];
        float2 edgeStart = ToScreen(roi.center + roi.axisX*localEdgeStart.x + roi.axisY*localEdgeStart.y);
        float2 edgeEnd = ToScreen(roi.center + roi.axisX*localEdgeEnd.x + roi.axisY*localEdgeEnd.y);
        float2 edgeDirection = edgeEnd-edgeStart;
        float2 halfWidthNormal = float2(-edgeDirection.y,edgeDirection.x)
            * (roleWidths[role]*.5/max(length(edgeDirection),.00001));
        screenPosition = StrokeCorner(strokeCorner, edgeStart, edgeEnd, halfWidthNormal);
    }
    pixel.position = float4(screenPosition*clipScale + float2(-1,1),0,1);
    return pixel;
}
float4 PSMain(Pixel pixel) : SV_TARGET { return pixel.color; }
