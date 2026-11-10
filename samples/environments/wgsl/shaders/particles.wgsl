// 計算シェーダーで粒子を動かし、頂点とフラグメントのシェーダーで描く。
struct Particle {
    position: vec2f,
    velocity: vec2f,
}

struct Params {
    delta_time: f32,
    count: u32,
}

@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var<storage> particles: array<Particle>;   // read_write でない

@compute @workgroup_size(64)
fn simulate(@builtin(global_invocation_id) id: vec3u) {
    if id.x >= params.count {
        return;
    }

    let p = particles[id.x];
    particles[id.x].position = p.position + p.velocity * params.delta_time;   // WG0309: 読み取りだけのバッファー
}

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) color: vec4f,
}

@vertex
fn vs(@builtin(vertex_index) index: u32) -> VertexOutput {
    let p = particles[index];
    let speed: f32 = length(p.velocity);
    let bucket: f32 = index % 4u;                                             // CF0410: u32 は f32 に変換しない
    return VertexOutput(vec4f(p.position, 0.0, 1.0), vec4f(speed, bucket, 1.0, 1.0));
}

@fragment
fn fs(input: VertexOutput) -> @location(0) vec4f {
    return input.color;
}
