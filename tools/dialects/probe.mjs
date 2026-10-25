// コンパイラと HLSL のバージョンの組ごとに、使える構文と型の表を手元のコンパイラで作り直す。
//
// 使い方 (リポジトリのルートで):
//   node tools/dialects/probe.mjs                  # Windows SDK の最新の fxc.exe / dxc.exe を使う
//   node tools/dialects/probe.mjs <fxc> <dxc>       # コンパイラを指定する
//
// 結果を表にして出し、Shaderlyn が持つ表 (src/Shaderlyn.Hlsl/Parsing/HlslDialect.cs の HlslLanguageFeatures.Of) と
// 食い違えば終了コード 1 で終わる。下の expected はその表を写したもの。表を変えたら両方を直す。
//
// コンパイラを入れ替えたとき (DXC の版を上げたときなど) に、表がまだ正しいかを確かめるために使う。
// テストからは呼ばない (コンパイラがある環境は限られる)。

import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

/** コンパイラと HLSL のバージョンの組。dxc は -HV の版。 */
const dialects = [
  { name: "FXC", compiler: "fxc" },
  { name: "2016", compiler: "dxc", version: "2016" },
  { name: "2017", compiler: "dxc", version: "2017" },
  { name: "2018", compiler: "dxc", version: "2018" },
  { name: "2021", compiler: "dxc", version: "2021" },
];

/**
 * 確かめる項目。code はピクセルシェーダー main を含む完全なコード。
 * expected は HlslLanguageFeatures.Of の値 (組の順)。"warn" は通るが警告が出る、"noop" は通るが何もしない。
 */
const features = [
  { name: "template", code: "template<typename T> T Twice(T x) { return x + x; }\nfloat4 main() : SV_Target { return Twice(1.0); }", expected: [false, false, false, false, true] },
  { name: "operator", code: "struct S { float v; S operator+(S o) { S r; r.v = v + o.v; return r; } };\nfloat4 main() : SV_Target { S a = (S)1; S b = a + a; return b.v; }", expected: [false, false, false, false, true] },
  { name: "bitfield", code: "struct P { uint a : 4; uint b : 28; };\nfloat4 main(uint v : V) : SV_Target { P p; p.a = v; p.b = v; return p.a + p.b; }", expected: [false, false, false, false, true] },
  { name: "enum", code: "enum Mode { A, B };\nfloat4 main() : SV_Target { Mode m = B; return m == B ? 1 : 0; }", expected: [false, false, true, true, true] },
  { name: "int64_t", code: "float4 main(uint v : V) : SV_Target { int64_t x = v; return (float)x; }", expected: [false, true, true, true, true] },
  { name: "float32_t", code: "float4 main(float v : V) : SV_Target { float32_t x = v; return x; }", expected: [false, false, false, true, true] },
  { name: "class / interface", code: "interface I { float Get(); };\nclass C : I { float v; float Get() { return v; } };\nfloat4 main(float v : V) : SV_Target { C c; c.v = v; return c.Get(); }", expected: [true, true, true, true, true] },
  { name: "vector &&", code: "float4 main(float3 a : A, float3 b : B) : SV_Target { bool3 x = a && b; return x.x; }", expected: [true, true, true, true, false] },
  { name: "vector ?:", code: "float4 main(float3 a : A, float3 b : B) : SV_Target { float3 x = a > 0 ? a : b; return x.x; }", expected: [true, true, true, true, false] },
  { name: "for variable after loop", code: "float4 main() : SV_Target { float s = 0; for (int i = 0; i < 4; i++) { s += i; } return s + i; }", expected: [true, true, true, true, false] },
  { name: "select()", code: "float4 main(float3 a : A, float3 b : B) : SV_Target { return select(a > 0, a, b).x; }", expected: [false, true, true, true, true] },
  { name: "same-layout struct copy", code: "struct A { float x; }; struct B { float x; };\nfloat4 main(float v : V) : SV_Target { A a; a.x = v; B b = a; return b.x; }", expected: [true, true, true, true, false] },
  { name: "struct ++", code: "struct P { float x; };\nfloat4 main() : SV_Target { P a = (P)0; a++; return a.x; }", expected: [true, false, false, false, false] },
  { name: "sampler2D / tex2D", code: "sampler2D s;\nfloat4 main(float2 uv : TEXCOORD0) : SV_Target { return tex2D(s, uv); }", expected: [true, false, false, false, false], fxcFlags: ["/Gec"] },
  { name: "uniform entry parameter", code: "float4 main(uniform float k) : SV_Target { return k; }", expected: [true, false, false, false, false] },
  { name: "global interface variable (SM5)", code: "interface I { float Get(); };\nclass C : I { float Get() { return 1; } };\nI g;\nfloat4 main() : SV_Target { return g.Get(); }", expected: [true, false, false, false, false] },
];

/** Windows SDK の bin から、最も新しい版のコンパイラを探す。 */
function findInSdk(exe) {
  const bin = "C:\\Program Files (x86)\\Windows Kits\\10\\bin";

  if (!existsSync(bin)) {
    return undefined;
  }

  const versions = readdirSync(bin).filter((v) => /^10\./.test(v)).sort().reverse();

  for (const version of versions) {
    const path = join(bin, version, "x64", exe);

    if (existsSync(path)) {
      return path;
    }
  }

  return undefined;
}

const fxc = process.argv[2] ?? findInSdk("fxc.exe");
const dxc = process.argv[3] ?? findInSdk("dxc.exe");

if (!fxc || !dxc) {
  console.error("fxc.exe と dxc.exe が見つかりません。node tools/dialects/probe.mjs <fxc> <dxc> で指定してください。");
  process.exit(2);
}

const work = mkdtempSync(join(tmpdir(), "shaderlyn-dialects-"));

/** 1 つの項目を 1 つの組でコンパイルし、通ったかを返す。 */
function compiles(feature, dialect) {
  const file = join(work, "probe.hlsl");
  writeFileSync(file, feature.code + "\n");

  const args = dialect.compiler === "fxc"
    ? ["/nologo", ...(feature.fxcFlags ?? []), "/T", "ps_5_0", "/E", "main", "/Fo", join(work, "out.bin"), file]
    : ["-T", "ps_6_2", "-E", "main", "-HV", dialect.version, "-Fo", join(work, "out.bin"), file];

  try {
    execFileSync(dialect.compiler === "fxc" ? fxc : dxc, args, { stdio: "pipe" });
    return true;
  } catch {
    return false;
  }
}

let mismatches = 0;
const lines = [
  `| 項目 | ${dialects.map((d) => d.name).join(" | ")} |`,
  `| --- | ${dialects.map(() => "---").join(" | ")} |`,
];

for (const feature of features) {
  const cells = dialects.map((dialect, i) => {
    const actual = compiles(feature, dialect);
    const ok = actual === feature.expected[i];

    if (!ok) {
      mismatches++;
    }

    return `${actual ? "○" : "×"}${ok ? "" : " (表と違う)"}`;
  });

  lines.push(`| ${feature.name} | ${cells.join(" | ")} |`);
}

rmSync(work, { recursive: true, force: true });

console.log(`FXC: ${fxc}`);
console.log(`DXC: ${dxc}`);
console.log("");
console.log(lines.join("\n"));
console.log("");
console.log(mismatches === 0 ? "表と一致しました。" : `表と ${mismatches} か所食い違いました。`);
process.exit(mismatches === 0 ? 0 : 1);
