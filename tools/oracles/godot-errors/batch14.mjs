// 構造体の演算子とメンバー。o-ok- は Godot が通すもの (C 系の共通のルールが HLSL の規則で誤報告しないかを見る)。
const head = "shader_type spatial;\nstruct S { float a; vec3 v; };\n";

export const cases = {
  "o-ok-struct-equal": `${head}void fragment() {\n  S x;\n  S y;\n  bool b = x == y;\n}\n`,
  "o-ok-struct-not-equal": `${head}void fragment() {\n  S x;\n  S y;\n  bool b = x != y;\n}\n`,
  "o-ok-struct-select": `${head}void fragment() {\n  S x;\n  S y;\n  S z = true ? x : y;\n}\n`,
  "o-ok-struct-assign": `${head}void fragment() {\n  S x;\n  S y = x;\n  y = x;\n  y.v.xz = vec2(1.0);\n}\n`,
  "o-ok-struct-param-return": `${head}S make(float a) { S s; s.a = a; return s; }\nfloat get(S s) { return s.a; }\nvoid fragment() {\n  float a = get(make(1.0));\n}\n`,
  "o-ok-struct-constructor": `${head}void fragment() {\n  S x = S(1.0, vec3(0.0));\n  S arr[2] = { x, S(2.0, vec3(1.0)) };\n  float a = arr[1].v.y;\n}\n`,
  "o-ok-array-length": "shader_type spatial;\nvoid fragment() {\n  float a[3];\n  int n = a.length();\n}\n",
  "o-struct-increment": `${head}void fragment() {\n  S x;\n  x++;\n}\n`,
  "o-struct-plus-assign": `${head}void fragment() {\n  S x;\n  S y;\n  x += y;\n}\n`,
  "o-struct-negate": `${head}void fragment() {\n  S x;\n  S y = -x;\n}\n`,
  "o-struct-less": `${head}void fragment() {\n  S x;\n  S y;\n  bool b = x < y;\n}\n`,
  "o-struct-unknown-member": `${head}void fragment() {\n  S x;\n  float b = x.b;\n}\n`,
};
