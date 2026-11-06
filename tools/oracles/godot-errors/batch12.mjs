// サンプラーの仮引数に渡したテクスチャ。p-ok- は Godot が通すもの。
export const cases = {
  "p-ok-same-settings": "shader_type canvas_item;\nuniform sampler2D a : filter_linear, source_color;\nuniform sampler2D b : filter_linear, hint_default_white;\nvec4 f(sampler2D s) { return texture(s, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(a) + f(b);\n}\n",
  "p-repeat-differs": "shader_type canvas_item;\nuniform sampler2D a : repeat_enable;\nuniform sampler2D b;\nvec4 f(sampler2D s) { return texture(s, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(a) + f(b);\n}\n",
  "p-screen-hint-differs": "shader_type canvas_item;\nuniform sampler2D a : hint_screen_texture;\nuniform sampler2D b;\nvec4 f(sampler2D s) { return texture(s, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(a);\n  COLOR += f(b);\n}\n",
  "p-builtin-and-uniform": "shader_type canvas_item;\nuniform sampler2D a;\nvec4 f(sampler2D s) { return texture(s, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(TEXTURE) + f(a);\n}\n",
  "p-ok-same-builtin": "shader_type canvas_item;\nvec4 f(sampler2D s) { return texture(s, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(TEXTURE) + f(TEXTURE);\n}\n",
  "p-through-parameter": "shader_type canvas_item;\nuniform sampler2D a : filter_nearest;\nuniform sampler2D b : filter_linear;\nvec4 g(sampler2D s) { return texture(s, vec2(0.0)); }\nvec4 f(sampler2D s) { return g(s); }\nvoid fragment() {\n  COLOR = g(b) + f(a);\n}\n",
  "p-ok-different-parameters": "shader_type canvas_item;\nuniform sampler2D a : filter_nearest;\nuniform sampler2D b : filter_linear;\nvec4 f(sampler2D s, sampler2D t) { return texture(s, vec2(0.0)) + texture(t, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(a, b) + f(a, b);\n}\n",
  "p-radiance": "shader_type sky;\nvec4 f(samplerCube s) { return texture(s, vec3(0.0)); }\nvoid sky() {\n  COLOR = f(RADIANCE).rgb;\n}\n",
};
