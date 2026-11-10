shader_type spatial;
render_mode cull_disabled;

uniform sampler2D noise : repeat_enable;
uniform vec4 shallow : source_color = vec4(0.2, 0.6, 0.8, 1.0);
uniform float speed : hint_range(0.0, 2.0) = 0.5;

void vertex() {
    VERTEX.y += sin(TIME * speed + VERTEX.x) * 0.1;
}

void fragment() {
    vec4 waves = textureGather(noise, UV + TIME * 0.05);   // GD0001: Compatibility では使えない
    ALBEDO = shallow.rgb * waves.r;
    ROUGHNESS = 1;                                         // CF0410: int を float に入れられない
}
