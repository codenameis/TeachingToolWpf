struct Options {
    center: vec2f, axisX: vec2f, axisY: vec2f,
    maskCount: u32, vertexCount: u32, isCircle: u32, selectedIndex: u32, dpi: f32, padding: u32,
}
struct MaskData { bounds: vec4f, options: vec4f }
@group(0) @binding(0) var<storage, read> masks: array<MaskData>;
@group(0) @binding(1) var<storage, read> outline: array<vec2f>;
@group(0) @binding(2) var<uniform> options: Options;
struct Pixel { @builtin(position) position: vec4f, @location(0) uv: vec2f }
const corners = array<vec2f, 6>(vec2f(-1,-1), vec2f(1,-1), vec2f(1,1), vec2f(-1,-1), vec2f(1,1), vec2f(-1,1));
@vertex fn vs_main(@builtin(vertex_index) vertex: u32) -> Pixel {
    let corner = corners[vertex];
    return Pixel(vec4f(options.center + options.axisX * corner.x + options.axisY * corner.y, 0, 1), corner * .5 + .5);
}
fn inside_owner(p: vec2f) -> bool {
    if options.isCircle != 0u { return dot(p, p) <= .25; }
    var inside = false;
    for (var i = 0u; i < options.vertexCount; i++) {
        let a = outline[i];
        let b = outline[(i + 1u) % options.vertexCount];
        if (a.y > p.y) != (b.y > p.y) && p.x < a.x + (p.y - a.y) * (b.x - a.x) / (b.y - a.y) {
            inside = !inside;
        }
    }
    return inside;
}
@fragment fn fs_main(pixel: Pixel) -> @location(0) vec4f {
    if !inside_owner(pixel.uv - .5) { discard; }
    var covered = false;
    var selected = false;
    for (var i = 0u; i < options.maskCount; i++) {
        let mask = masks[i];
        let local = (pixel.uv - mask.bounds.xy) / (mask.bounds.zw - mask.bounds.xy) * 2 - 1;
        let inside = select(all(abs(local) <= vec2f(1)), dot(local, local) <= 1, mask.options.x > .5);
        covered = covered || inside;
        selected = selected || (inside && i == options.selectedIndex);
    }
    if !covered { discard; }
    let hatch = select(0.0, 1.0, fract((pixel.position.x + pixel.position.y) / (9 * options.dpi)) < .2);
    return vec4f(.88, .20, .37, select(.13, .25, selected) + hatch * .28);
}
