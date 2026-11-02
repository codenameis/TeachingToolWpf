struct Options { grayscale: u32, pointSampling: u32 }
@group(0) @binding(0) var image: texture_2d<f32>;
@group(0) @binding(2) var<uniform> options: Options;
@group(0) @binding(3) var linearSampler: sampler;
@group(0) @binding(4) var pointSampler: sampler;
struct Pixel {
    @builtin(position) position: vec4f,
    @location(0) uv: vec2f,
    @location(1) color: vec4f,
}
@vertex fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f, @location(2) color: vec4f) -> Pixel {
    return Pixel(vec4f(position, 0, 1), uv, color);
}
@fragment fn fs_main(pixel: Pixel) -> @location(0) vec4f {
    var value: vec4f;
    if options.pointSampling != 0u {
        value = textureSampleLevel(image, pointSampler, pixel.uv, 0);
    } else {
        value = textureSampleLevel(image, linearSampler, pixel.uv, 0);
    }
    if options.grayscale != 0u { value = vec4f(value.rrr, 1); }
    return value * pixel.color;
}
