// 型の変換。Godot は暗黙の変換をしないが、関数の呼び出しの実引数に書いた整数のリテラルだけは仮引数の型へ変換する。
// 通るもの (c-ok-) と誤りになるもの (c-) を並べる。
const frag = (decls, body) => `shader_type spatial;\n${decls}\nvoid fragment() {\n${body}\n}\n`;

export const cases = {
  "c-literal-init": frag("", "  float a = 1;"),
  "c-negative-literal-init": frag("", "  float a = -1;"),
  "c-literal-assign": frag("", "  float a;\n  a = 2;"),
  "c-literal-operator": frag("", "  float a = 1.0 + 1;"),
  "c-literal-return": frag("float f() { return 1; }", ""),
  "c-uint-from-int": frag("", "  uint a = 1;"),
  "c-const-name-init": frag("const int K = 1;", "  float a = K;"),
  "c-const-name-arg": frag("const int K = 1;\nfloat f(float x) { return x; }", "  float a = f(K);"),
  "c-expression-arg": frag("float f(float x) { return x; }", "  float a = f(1 + 2);"),
  "c-variable-arg": frag("float f(float x) { return x; }", "  int i = 0;\n  float a = f(i);"),
  "c-ivec-arg": frag("void f(vec2 x) {}", "  f(ivec2(1));"),
  "c-variable-constructor": frag("", "  int i = 1;\n  vec2 a = vec2(i, 1.0);"),
  "c-ok-literal-arg": frag("float f(float x) { return x; }", "  float a = f(0);"),
  "c-ok-negative-literal-arg": frag("float f(float x) { return x; }", "  float a = f(-1);"),
  "c-ok-literal-builtin": frag("", "  float a = sin(1);\n  float b = mix(0, 1, 0.5);"),
  "c-ok-literal-constructor": frag("", "  vec2 a = vec2(1, 2);"),
};
