// ステージ関数と、ステージごとに使える組み込み。t-ok- は Godot が通すもの。
export const cases = {
  // 組み込みの変数は、ステージ関数の中ではその関数のものが、利用者の関数の中ではどの関数でも使えるもの (TIME など) だけが使える
  "t-user-function-stage-builtin": "shader_type spatial;\nvec3 f() { return FRAGCOORD.xyz; }\nvoid fragment() {\n  ALBEDO = f();\n}\n",
  "t-user-function-uncalled": "shader_type spatial;\nvec3 f() { return FRAGCOORD.xyz; }\nvoid fragment() {\n  ALBEDO = vec3(1.0);\n}\n",
  "t-user-function-vertex-builtin": "shader_type spatial;\nvec3 h() { return VERTEX; }\nvoid fragment() {\n  ALBEDO = h();\n}\n",
  "t-uniform-default-global": "shader_type spatial;\nuniform float u = TIME;\n",
  "t-ok-user-function-global": "shader_type spatial;\nfloat f() { return TIME * PI; }\nvoid fragment() {\n  ALBEDO = vec3(f());\n}\n",
  "t-ok-light-albedo": "shader_type spatial;\nvoid light() {\n  DIFFUSE_LIGHT = ALBEDO;\n}\n",
  "t-ok-constant-outside": "shader_type spatial;\nconst float K = PI * 2.0;\n",
  // 関数は宣言より前で呼べない
  "t-call-before-declaration": "shader_type spatial;\nvoid fragment() {\n  ALBEDO = f();\n}\nvec3 f() { return vec3(1.0); }\n",
  // フラグメントだけの組み込み関数
  "t-dfdx-vertex": "shader_type spatial;\nvoid vertex() {\n  float a = dFdx(1.0);\n}\n",
  "t-dfdx-user-from-vertex": "shader_type spatial;\nfloat g() { return dFdx(1.0); }\nvoid vertex() {\n  float a = g();\n}\n",
  "t-dfdx-user-from-vertex-nested": "shader_type spatial;\nfloat g() { return fwidth(1.0); }\nfloat h() { return g(); }\nvoid vertex() {\n  float a = h();\n}\n",
  "t-dfdx-fog": "shader_type fog;\nvoid fog() {\n  float a = dFdx(1.0);\n}\n",
  "t-ok-dfdx-user-from-fragment": "shader_type spatial;\nfloat g() { return dFdx(1.0); }\nvoid fragment() {\n  ALBEDO = vec3(g());\n}\n",
  "t-ok-dfdx-sky": "shader_type sky;\nvoid sky() {\n  COLOR = vec3(dFdx(1.0));\n}\n",
  // ステージ関数の中でだけ使える組み込み関数
  "t-ok-texture-sdf-fragment": "shader_type canvas_item;\nvoid fragment() {\n  float d = texture_sdf(vec2(0.0));\n}\n",
  "t-texture-sdf-vertex": "shader_type canvas_item;\nvoid vertex() {\n  float d = texture_sdf(vec2(0.0));\n}\n",
  "t-texture-sdf-user": "shader_type canvas_item;\nfloat f() { return texture_sdf(vec2(0.0)); }\nvoid fragment() {\n  COLOR = vec4(f());\n}\n",
  "t-texture-sdf-spatial": "shader_type spatial;\nvoid fragment() {\n  float d = texture_sdf(vec2(0.0));\n}\n",
  "t-ok-emit-subparticle": "shader_type particles;\nvoid process() {\n  bool a = emit_subparticle(TRANSFORM, VELOCITY, COLOR, CUSTOM, 0u);\n}\n",
  // ステージ関数の形
  "t-stage-function-argument": "shader_type spatial;\nvoid fragment(float x) {\n}\n",
  "t-stage-function-return": "shader_type spatial;\nfloat fragment() {\n  return 1.0;\n}\n",
  "t-stage-function-other-type": "shader_type particles;\nvoid fragment() {\n}\n",
  "t-ok-stage-function-name-other-type": "shader_type particles;\nvoid vertex() {\n}\nvoid process() {\n}\n",
};
