// 添字。i-ok- は Godot が通すもの。
export const cases = {
  "i-ok-index": "shader_type spatial;\nstruct S { float a[3]; vec3 v; };\nconst int K = 7;\nvoid fragment() {\n  float a[2] = { 1.0, 2.0 };\n  vec4 v = vec4(1.0);\n  mat3 m = mat3(1.0);\n  S s;\n  uint u = 1u;\n  float b = a[1] + v[3] + m[2][2] + s.a[2] + s.v[2] + v[u] + v.xyz[2] + a[K - 6];\n}\n",
  "i-array-negative": "shader_type spatial;\nvoid fragment() {\n  float a[2] = { 1.0, 2.0 };\n  float b = a[-1];\n}\n",
  "i-array-length": "shader_type spatial;\nvoid fragment() {\n  float a[2] = { 1.0, 2.0 };\n  float b = a[2];\n}\n",
  "i-struct-array-range": "shader_type spatial;\nstruct S { float a[3]; };\nvoid fragment() {\n  S s;\n  float b = s.a[3];\n}\n",
  "i-vector-negative": "shader_type spatial;\nvoid fragment() {\n  vec2 v = vec2(1.0);\n  float b = v[-1];\n}\n",
  "i-mat-range": "shader_type spatial;\nvoid fragment() {\n  mat2 m = mat2(1.0);\n  vec2 c = m[2];\n}\n",
  "i-swizzle-range": "shader_type spatial;\nvoid fragment() {\n  vec4 v = vec4(1.0);\n  float b = v.xy[2];\n}\n",
  "i-float-index": "shader_type spatial;\nvoid fragment() {\n  vec2 v = vec2(1.0);\n  float b = v[1.0];\n}\n",
  "i-struct-index": "shader_type spatial;\nstruct S { float a; };\nvoid fragment() {\n  S s;\n  float b = s[0];\n}\n",
  "i-builtin-index": "shader_type spatial;\nvoid fragment() {\n  float b = TIME[0];\n}\n",
  "i-ivec-index": "shader_type spatial;\nvoid fragment() {\n  vec2 v = vec2(1.0);\n  float b = v[ivec2(0).x + ivec2(0)];\n}\n",
};
