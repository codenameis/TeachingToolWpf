Texture2D imageTexture : register(t0);
SamplerState linearSampler : register(s0);
SamplerState pointSampler : register(s1);
cbuffer Options : register(b0) { uint grayscale; uint pointSampling; };
struct Vertex { float2 position : POSITION; float2 uv : TEXCOORD; float4 color : COLOR; };
struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD; float4 color : COLOR; };
Pixel VSMain(Vertex v) { Pixel p; p.position=float4(v.position,0,1); p.uv=v.uv; p.color=v.color; return p; }
float4 PSMain(Pixel p) : SV_TARGET {
    float4 value = pointSampling ? imageTexture.Sample(pointSampler,p.uv) : imageTexture.Sample(linearSampler,p.uv);
    if (grayscale) value=float4(value.rrr,1);
    return value*p.color;
}
