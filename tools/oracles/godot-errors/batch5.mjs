// 関数の外の宣言 (uniform のヒントと既定値、render_mode / stencil_mode、varying)。u-ok- は Godot が通すもの。
export const cases = {
  // uniform のヒント
  "u-ok-hints": "shader_type spatial;\nuniform sampler2D t : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;\nuniform vec4 c : source_color, color_conversion_disabled;\nuniform float r : hint_range(0, PI, 0.1) = 0.5;\nuniform int e : hint_enum(\"A\", \"B\") = 0;\ninstance uniform vec4 i : instance_index(15);\nuniform sampler2D a[2] : source_color, filter_nearest;\n",
  "u-ok-range-const": "shader_type spatial;\nconst float K = 2.0;\nuniform float r : hint_range(0.0, K);\n",
  "u-ok-screen-canvas": "shader_type canvas_item;\nuniform sampler2D s : hint_screen_texture, filter_linear_mipmap;\n",
  "u-sampler-source-color-twice": "shader_type spatial;\nuniform sampler2D s : source_color, source_color;\n",
  "u-conversion-on-sampler": "shader_type spatial;\nuniform sampler2D s : color_conversion_disabled;\n",
  "u-range-on-int-vector": "shader_type spatial;\nuniform ivec2 u : hint_range(0, 1);\n",
  "u-hint-on-vec4": "shader_type spatial;\nuniform vec4 u : hint_normal;\n",
  "u-instance-in-sky": "shader_type sky;\ninstance uniform float u;\nvoid sky() {\n}\n",
  "u-depth-in-sky": "shader_type sky;\nuniform sampler2D s : hint_depth_texture;\nvoid sky() {\n}\n",
  "u-blit-in-spatial": "shader_type spatial;\nuniform sampler2D s : hint_blit_source0;\n",
  // uniform の既定値は、int・uint の定数を float に、int と uint を互いに変換する
  "u-ok-default-convert": "shader_type spatial;\nuniform float a = 1;\nuniform float b = 1u;\nuniform uint c = 1;\nuniform int d = 1u;\n",
  "u-default-negative-uint": "shader_type spatial;\nuniform uint u = -1;\n",
  "u-default-vector-convert": "shader_type spatial;\nuniform vec2 u = ivec2(1);\n",
  "u-varying-sampler-array": "shader_type spatial;\nvarying samplerCube v;\n",
  "u-varying-in-fog": "shader_type fog;\nvarying float v;\nvoid fog() {\n}\n",
  "u-struct-varying": "shader_type spatial;\nstruct S { float a; };\nvarying S v;\n",
  "u-struct-void-member": "shader_type spatial;\nstruct S { void a; };\n",
  "u-ok-struct-struct-member": "shader_type spatial;\nstruct A { float x; };\nstruct B { A a; };\n",
  // render_mode と stencil_mode
  "u-ok-render-modes": "shader_type spatial;\nrender_mode blend_add, depth_draw_always, cull_disabled, unshaded, diffuse_toon, specular_disabled, alpha_to_coverage_and_one;\n",
  "u-ok-render-mode-prefix": "shader_type spatial;\nrender_mode unshaded_extra;\n",
  "u-ok-stencil": "shader_type spatial;\nstencil_mode read, compare_equal, 255;\n",
  "u-ok-render-mode-twice": "shader_type spatial;\nrender_mode unshaded;\nrender_mode cull_front;\n",
  "u-render-mode-two-declarations": "shader_type spatial;\nrender_mode unshaded;\nrender_mode unshaded;\n",
  "u-render-mode-redefined-two-declarations": "shader_type spatial;\nrender_mode cull_back;\nrender_mode cull_front;\n",
  "u-render-mode-bad-option": "shader_type spatial;\nrender_mode blend_nope;\n",
  "u-stencil-unknown": "shader_type spatial;\nstencil_mode nope;\n",
  "u-stencil-redefined": "shader_type spatial;\nstencil_mode compare_equal, compare_less;\n",
  "u-render-mode-sky": "shader_type sky;\nrender_mode use_half_res_pass, unshaded;\nvoid sky() {\n}\n",
};
