// レンダラーで変わるもの。フォルダーごとに project.godot (projects) を置き、そのレンダラーで読む。g-ok- は Godot が通すもの。
// varying の数の上限と Compatibility の high-end の組み込み関数は、ヘッドレスでは確かめられない (SHADERLYN_GODOT_WINDOW=1 で記録する)。

/** フォルダーの名前と、その project.godot の rendering/renderer/rendering_method。 */
export const projects = {
  "renderer-forward-plus": "forward_plus",
  "renderer-mobile": "mobile",
  "renderer-compatibility": "gl_compatibility",
};

/** 型 type の varying を count 個書き、vertex で全部に書き込むシェーダー。 */
function varyings(shaderType, type, count, suffix = "") {
  const names = Array.from({ length: count }, (_, i) => `v${i}`);
  const value = `${type}(1.0)`;
  const output = shaderType === "spatial" ? "ALBEDO" : "COLOR.rgb";
  return `shader_type ${shaderType};\n${names.map(n => `varying ${type} ${n}${suffix};`).join("\n")}\nvoid vertex() {\n${names.map(n => `  ${n}${suffix ? "[0]" : ""} = ${value};`).join("\n")}\n}\nvoid fragment() {\n  ${output} = vec3(1.0);\n}\n`;
}

const gather = "uniform sampler2D t;\nvoid fragment() {\n  ALBEDO = textureGather(t, vec2(0.0)).rgb;\n}\n";
const rendererBranch = name => `shader_type spatial;\n#if CURRENT_RENDERER == ${name}\nint a = 1;\n#endif\n`;

export const cases = {
  // Forward+: 組み込みの varying が spatial で 15、canvas_item で 9
  "renderer-forward-plus/g-ok-varyings-spatial-17": varyings("spatial", "vec4", 17),
  "renderer-forward-plus/g-varyings-spatial-18": varyings("spatial", "vec4", 18),
  "renderer-forward-plus/g-ok-varyings-canvas-23": varyings("canvas_item", "vec4", 23),
  "renderer-forward-plus/g-varyings-canvas-24": varyings("canvas_item", "vec4", 24),
  "renderer-forward-plus/g-ok-varyings-mat4-4": varyings("spatial", "mat4", 4),
  "renderer-forward-plus/g-varyings-mat4-5": varyings("spatial", "mat4", 5),
  "renderer-forward-plus/g-ok-varyings-float-17": varyings("spatial", "float", 17),
  "renderer-forward-plus/g-varyings-array": "shader_type spatial;\nvarying vec4 a[10];\nvarying vec4 b[8];\nvoid vertex() {\n  a[0] = vec4(1.0);\n  b[0] = vec4(1.0);\n}\n",
  "renderer-forward-plus/g-ok-normal-roughness": "shader_type spatial;\nuniform sampler2D t : hint_normal_roughness_texture;\n",
  "renderer-forward-plus/g-ok-gather": `shader_type spatial;\n${gather}`,
  "renderer-forward-plus/g-current-renderer": rendererBranch("RENDERER_FORWARD_PLUS"),
  "renderer-forward-plus/g-ok-current-renderer-mobile": rendererBranch("RENDERER_MOBILE"),

  // Mobile
  "renderer-mobile/g-varyings-spatial-18": varyings("spatial", "vec4", 18),
  "renderer-mobile/g-normal-roughness": "shader_type spatial;\nuniform sampler2D t : hint_normal_roughness_texture;\n",
  "renderer-mobile/g-normal-roughness-canvas": "shader_type canvas_item;\nuniform sampler2D t : hint_normal_roughness_texture;\n",
  "renderer-mobile/g-ok-gather": `shader_type spatial;\n${gather}`,
  "renderer-mobile/g-current-renderer": rendererBranch("RENDERER_MOBILE"),
  "renderer-mobile/g-ok-current-renderer-compatibility": rendererBranch("RENDERER_COMPATIBILITY"),

  // Compatibility: 組み込みの varying を数えない。high-end の組み込み関数は使えない
  "renderer-compatibility/g-ok-varyings-spatial-32": varyings("spatial", "vec4", 32),
  "renderer-compatibility/g-varyings-spatial-33": varyings("spatial", "vec4", 33),
  "renderer-compatibility/g-normal-roughness": "shader_type spatial;\nuniform sampler2D t : hint_normal_roughness_texture;\n",
  "renderer-compatibility/g-gather": `shader_type spatial;\n${gather}`,
  "renderer-compatibility/g-dfdx-fine": "shader_type spatial;\nvoid fragment() {\n  ALBEDO = vec3(dFdxFine(UV.x));\n}\n",
  "renderer-compatibility/g-bit-count": "shader_type spatial;\nvoid fragment() {\n  int a = bitCount(3);\n}\n",
  "renderer-compatibility/g-fma": "shader_type spatial;\nvoid fragment() {\n  float a = fma(1.0, 2.0, 3.0);\n}\n",
  "renderer-compatibility/g-texture-query-levels": "shader_type spatial;\nuniform sampler2D t;\nvoid fragment() {\n  int a = textureQueryLevels(t);\n}\n",
  "renderer-compatibility/g-ok-dfdx": "shader_type spatial;\nvoid fragment() {\n  ALBEDO = vec3(dFdx(UV.x));\n}\n",
  "renderer-compatibility/g-current-renderer": rendererBranch("RENDERER_COMPATIBILITY"),
};
