struct Instance { center: vec2f, axisX: vec2f, axisY: vec2f, summary: u32, padding: u32 }
@group(0) @binding(0) var<storage, read> instances: array<Instance>;
@group(0) @binding(1) var<storage, read> roles: array<u32>;
// RoiDrawConstants의 176바이트 배치와 일치한다.
struct Options {
    clipScale: vec2f, imageScale: f32, searchRangePass: f32,
    screenOrigin: vec2f, shadowPass: f32, summaryOpacity: f32,
    shadowColor: vec4f, rotation: vec2f, reservedRotation: vec2f,
    roleColors: array<vec4f, 5>, roleWidths: vec4f, extraRoleWidths: vec4f,
}
@group(0) @binding(2) var<uniform> options: Options;
struct Pixel {
    @builtin(position) position: vec4f,
    @location(0) color: vec4f,
    @location(1) circleOffset: vec2f,
    @location(2) circleStroke: vec2f,
    @location(3) strokeDistance: f32,
}
const corners = array<vec2f, 4>(vec2f(-1,-1), vec2f(1,-1), vec2f(1,1), vec2f(-1,1));
const cornerIndices = array<u32, 6>(0,1,2,0,2,3);
fn to_screen(local: vec2f) -> vec2f {
    let rotation = options.rotation;
    return vec2f(local.x * rotation.x - local.y * rotation.y, local.x * rotation.y + local.y * rotation.x)
        * options.imageScale + options.screenOrigin;
}
fn stroke_corner(corner: u32, start: vec2f, end: vec2f, normal: vec2f) -> vec2f {
    if corner == 0u { return start + normal; }
    if corner == 1u { return end + normal; }
    if corner == 2u { return end - normal; }
    return start - normal;
}
@vertex fn vs_main(@builtin(vertex_index) vertex: u32, @builtin(instance_index) instance: u32) -> Pixel {
    let roi = instances[instance];
    let role = min(roles[instance], 4u);
    var width = options.extraRoleWidths.x;
    if role < 4u { width = options.roleWidths[role]; }
    var pixel: Pixel;
    pixel.color = select(options.roleColors[role], options.shadowColor, options.shadowPass != 0);
    var position: vec2f;
    if roi.summary == 3u {
        if vertex >= 6u { pixel.position = vec4f(2,2,0,1); return pixel; }
        let radius = length(roi.axisX) * options.imageScale;
        let halfWidth = width * .5;
        pixel.circleOffset = corners[cornerIndices[vertex]] * (radius + halfWidth + 1);
        pixel.circleStroke = vec2f(radius, halfWidth);
        position = to_screen(roi.center) + pixel.circleOffset;
    } else if roi.summary == 1u {
        if vertex >= 6u || options.shadowPass != 0 { pixel.position = vec4f(2,2,0,1); return pixel; }
        let corner = corners[cornerIndices[vertex]];
        position = to_screen(roi.center + roi.axisX * corner.x + roi.axisY * corner.y);
        pixel.color.a *= options.summaryOpacity;
    } else {
        if roi.summary == 2u && vertex >= 6u { pixel.position = vec4f(2,2,0,1); return pixel; }
        let edge = vertex / 6u;
        let corner = cornerIndices[vertex % 6u];
        let localStart = corners[edge];
        let localEnd = corners[(edge + 1u) % 4u];
        var start = to_screen(roi.center + roi.axisX * localStart.x + roi.axisY * localStart.y);
        var end = to_screen(roi.center + roi.axisX * localEnd.x + roi.axisY * localEnd.y);
        if roi.summary == 2u { start = to_screen(roi.center); end = to_screen(roi.center + roi.axisX); }
        let direction = end - start;
        pixel.strokeDistance = select(0.0, length(direction), corner == 1u || corner == 2u);
        let normal = vec2f(-direction.y, direction.x) * (width * .5 / max(length(direction), .00001));
        position = stroke_corner(corner, start, end, normal);
    }
    pixel.position = vec4f(position * options.clipScale + vec2f(-1,1), 0, 1);
    return pixel;
}
@fragment fn fs_main(pixel: Pixel) -> @location(0) vec4f {
    if options.searchRangePass != 0 && pixel.strokeDistance % 13 >= 8 { discard; }
    var color = pixel.color;
    if pixel.circleStroke.x > 0 {
        let distance = abs(length(pixel.circleOffset) - pixel.circleStroke.x);
        color.a *= clamp(pixel.circleStroke.y + .5 - distance, 0, 1);
        if color.a < .001 { discard; }
    }
    return color;
}
