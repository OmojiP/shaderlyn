// システム値のセマンティクス (SV_Position など) が、どのステージのどの受け渡しの形で使えるかを、手元のコンパイラに掛けて表にする。
// 表は src/Shaderlyn.Semantics/Symbols/SystemValueSemantics.g.txt に書く (CF0302 が使う)。
//
// 使い方 (リポジトリのルートで):
//   node tools/semantics/system-values.mjs <作業フォルダー>
//     FXC と DXC は Windows SDK の最新のもの (SHADERLYN_FXC・SHADERLYN_DXC で指定できる)、
//     slangc は SHADERLYN_SLANGC (無ければ PATH の slangc)。
//     コンパイラごとの結果 (fxc.json・dxc.json・slang.json) を作業フォルダーに残す。数千回コンパイルするので時間がかかる。
//   node tools/semantics/system-values.mjs <作業フォルダー> --reuse
//     作業フォルダーに残した結果から、表だけを作り直す。
//
// 1 つの組み合わせごとに、そのセマンティクスの値を 1 つだけ受け渡すエントリポイントを作ってコンパイルする。
// 型で弾かれることがあるので、通るまで型を変えて試す。
// 表に入れるのは「通った」(o) と「セマンティクスの誤りになった」(x) だけで、ほかの誤りになったもの (骨組みの形の誤りなど) は入れない。
// 表に無い組み合わせについて、CF0302 は何も言わない。

import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const [work, mode] = process.argv.slice(2);

if (!work) {
  console.error("使い方: node tools/semantics/system-values.mjs <作業フォルダー> [--reuse]");
  process.exit(2);
}

mkdirSync(work, { recursive: true });

const semantics = [
  "SV_Position", "SV_Target", "SV_Depth", "SV_DepthGreaterEqual", "SV_DepthLessEqual", "SV_Coverage", "SV_InnerCoverage",
  "SV_StencilRef", "SV_VertexID", "SV_InstanceID", "SV_PrimitiveID", "SV_IsFrontFace", "SV_SampleIndex", "SV_ClipDistance",
  "SV_CullDistance", "SV_RenderTargetArrayIndex", "SV_ViewportArrayIndex", "SV_GSInstanceID", "SV_OutputControlPointID",
  "SV_DomainLocation", "SV_TessFactor", "SV_InsideTessFactor", "SV_DispatchThreadID", "SV_GroupID", "SV_GroupThreadID",
  "SV_GroupIndex", "SV_ViewID", "SV_Barycentrics", "SV_ShadingRate", "SV_CullPrimitive", "SV_StartVertexLocation",
  "SV_StartInstanceLocation", "SV_DrawIndex", "SV_PointSize", "SV_PointCoord", "SV_VulkanVertexID", "SV_VulkanInstanceID",
  "SV_DeviceIndex", "SV_FragInvocationCount", "SV_FragSize", "SV_QuadLaneIndex", "SV_WaveLaneIndex", "SV_WaveLaneCount",
];

/**
 * ステージごとの受け渡しの形と、その骨組み。f はセマンティクスを付けた宣言 (T x : SV_…)、w は出力に書き込む文。
 * in / out: エントリポイントの仮引数 (構造体の仮引数のフィールドと戻り値も同じ扱い)。
 * in-array: ジオメトリシェーダーの頂点の配列 (point S input[1]) のフィールド。out-stream: ストリーム (PointStream<S>) のフィールド。
 * in-patch: ハル・ドメインシェーダーの InputPatch / OutputPatch のフィールド。out-control-point: ハルシェーダーの戻り値のフィールド。
 * out-patch-constant: パッチ定数の関数の戻り値のフィールド。in-patch-constant: ドメインシェーダーのパッチ定数の構造体のフィールド。
 * out-vertices / out-primitives: メッシュシェーダーの out vertices / out primitives のフィールド。
 */
function stages(slang) {
  const attr = s => (slang ? `[shader("${s}")]\n` : "");

  const hull = ({ param = "", input = "C", inputDecl = "", output = "C", outputDecl = "", patchConstant = "" }) =>
    `struct C { float4 p : POSITION; };\n${inputDecl}${outputDecl}`
    + `struct PC { float e[3] : SV_TessFactor; float i : SV_InsideTessFactor; ${patchConstant} };\n`
    + `PC pc(InputPatch<${input}, 3> ip) { PC o = (PC)0; return o; }\n`
    + `${attr("hull")}[domain("tri")]\n[partitioning("integer")]\n[outputtopology("triangle_cw")]\n[outputcontrolpoints(3)]\n[patchconstantfunc("pc")]\n`
    + `${output} main(${param}InputPatch<${input}, 3> ip, uint id : SV_OutputControlPointID) { ${output} o = (${output})0; return o; }\n`;

  const domain = ({ param = "", controlPoint = "", patchConstant = "", body = "" }) =>
    `struct C { float4 p : POSITION; ${controlPoint} };\nstruct PC { float e[3] : SV_TessFactor; float i : SV_InsideTessFactor; ${patchConstant} };\n`
    + `${attr("domain")}[domain("tri")]\nvoid main(${param}PC pc, OutputPatch<C, 3> op) { ${body} }\n`;

  const mesh = ({ param = "", vertex = "", primitive = "" }) =>
    `struct V { float4 p : TEXCOORD0; ${vertex} };\nstruct P { uint q : TEXCOORD1; ${primitive} };\n`
    + `${attr("mesh")}[numthreads(1,1,1)]\n[outputtopology("triangle")]\n`
    + `void main(${param}out vertices V verts[3], out indices uint3 tris[1], out primitives P prims[1]) { SetMeshOutputCounts(0, 0); }\n`;

  return {
    vertex: { profile: "vs", shapes: {
      "in": f => `${attr("vertex")}void main(in ${f}) {}\n`,
      "out": (f, w) => `${attr("vertex")}void main(out ${f}) { ${w} }\n`,
    } },
    pixel: { profile: "ps", shapes: {
      "in": f => `${attr("fragment")}void main(in ${f}) {}\n`,
      "out": (f, w) => `${attr("fragment")}void main(out ${f}) { ${w} }\n`,
    } },
    compute: { profile: "cs", shapes: {
      "in": f => `${attr("compute")}[numthreads(1,1,1)]\nvoid main(in ${f}) {}\n`,
      "out": (f, w) => `${attr("compute")}[numthreads(1,1,1)]\nvoid main(out ${f}) { ${w} }\n`,
    } },
    amplification: { profile: "as", sm6: true, shapes: {
      "in": f => `struct P { uint v; };\ngroupshared P gp;\n${attr("amplification")}[numthreads(1,1,1)]\nvoid main(in ${f}) { DispatchMesh(1,1,1,gp); }\n`,
      "out": (f, w) => `struct P { uint v; };\ngroupshared P gp;\n${attr("amplification")}[numthreads(1,1,1)]\nvoid main(out ${f}) { ${w} DispatchMesh(1,1,1,gp); }\n`,
    } },
    geometry: { profile: "gs", shapes: {
      "in": f => `struct V { float4 p : TEXCOORD0; };\n${attr("geometry")}[maxvertexcount(1)]\nvoid main(in ${f}, point V input[1], inout PointStream<V> s) {}\n`,
      "in-array": f => `struct S { ${f}; };\nstruct V { float4 p : TEXCOORD0; };\n${attr("geometry")}[maxvertexcount(1)]\nvoid main(point S input[1], inout PointStream<V> s) {}\n`,
      "out-stream": f => `struct S { ${f}; };\nstruct V { float4 p : TEXCOORD0; };\n${attr("geometry")}[maxvertexcount(1)]\nvoid main(point V input[1], inout PointStream<S> s) { S o = (S)0; s.Append(o); }\n`,
    } },
    hull: { profile: "hs", shapes: {
      "in": f => hull({ param: `in ${f}, ` }),
      "in-patch": f => hull({ input: "I", inputDecl: `struct I { ${f}; float4 p : POSITION; };\n` }),
      "out-control-point": f => hull({ output: "O", outputDecl: `struct O { ${f}; float4 p : POSITION; };\n` }),
      "out-patch-constant": f => hull({ patchConstant: `${f};` }),
    } },
    domain: { profile: "ds", shapes: {
      "in": f => domain({ param: `in ${f}, ` }),
      "in-patch": f => domain({ controlPoint: `${f};` }),
      "in-patch-constant": f => domain({ patchConstant: `${f};` }),
      "out": (f, w) => domain({ param: `out ${f}, `, body: w }),
    } },
    mesh: { profile: "ms", sm6: true, shapes: {
      "in": f => mesh({ param: `in ${f}, ` }),
      "out-vertices": f => mesh({ vertex: `${f};` }),
      "out-primitives": f => mesh({ primitive: `${f};` }),
    } },
  };
}

const types = ["uint", "float", "float4", "float3", "float2", "bool", "uint3", "uint2", "float[3]", "float[2]", "float[4]"];

/** コンパイラごとの掛け方と、誤りがセマンティクスの誤りかの判定。 */
const compilers = {
  fxc: {
    exe: () => process.env.SHADERLYN_FXC ?? findInSdk("fxc.exe"),
    extension: "hlsl",
    args: (exe, file, out, profile) => ["/nologo", "/T", `${profile}_5_0`, "/E", "main", "/Fo", out, file],
    errors: text => [...text.matchAll(/error (X\d+): ([^\r\n]*)/g)].map(m => ({ code: m[1], message: m[2] })),
    // X4502: invalid vs_5_0 output semantic 'SV_Depth'
    rejects: e => e.code === "X4502",
    skips: stage => stage.sm6,
    version: exe => "fxc (" + exe.replace(/.*Windows Kits\\10\\bin\\([^\\]+).*/, "Windows SDK $1") + ")",
  },
  dxc: {
    exe: () => process.env.SHADERLYN_DXC ?? findInSdk("dxc.exe"),
    extension: "hlsl",
    args: (exe, file, out, profile) => ["-T", `${profile}_6_8`, "-E", "main", "-Fo", out, file],
    errors: text => [...text.matchAll(/error: ([^\r\n]*)/g)].map(m => ({ message: m[1] })),
    // invalid semantic 'SV_Depth' for vs 6.8 / Semantic SV_Depth is invalid for shader model: gs
    rejects: e => /invalid semantic '|Semantic \S+ is invalid for shader model/.test(e.message),
    skips: () => false,
    version: exe => spawnSync(exe, ["--version"], { encoding: "utf8" }).stdout.trim().split(/\r?\n/)[0],
  },
  slang: {
    exe: () => process.env.SHADERLYN_SLANGC ?? "slangc",
    extension: "slang",
    args: (exe, file, out) => [file, "-target", "spirv", "-entry", "main", "-o", out],
    errors: text => [...text.matchAll(/error\[(E\d+)\]: ([^\r\n]*)/g)].map(m => ({ code: m[1], message: m[2] })),
    // E30702: system value semantic '…' cannot be used as output in '…' shader stage
    // E30703: per-primitive system value semantic '…' must be placed in an 'OutputPrimitives' …
    rejects: e => e.code === "E30702" || e.code === "E30703",
    skips: () => false,
    version: exe => "slangc " + spawnSync(exe, ["-version"], { encoding: "utf8" }).stderr.trim(),
  },
};

/** Windows SDK の bin から、最も新しいバージョンのコンパイラを探す。 */
function findInSdk(exe) {
  const bin = "C:\\Program Files (x86)\\Windows Kits\\10\\bin";

  if (!existsSync(bin)) {
    return undefined;
  }

  for (const version of readdirSync(bin).filter(v => /^10\./.test(v)).sort().reverse()) {
    const path = join(bin, version, "x64", exe);

    if (existsSync(path)) {
      return path;
    }
  }

  return undefined;
}

/** 型 T のセマンティクス付きの宣言と、出力に書き込む文。 */
function declaration(type, semantic) {
  const array = type.match(/^(\w+)\[(\d+)\]$/);
  return array
    ? [`${array[1]} x[${array[2]}] : ${semantic}`, Array.from({ length: +array[2] }, (_, i) => `x[${i}] = 0;`).join(" ")]
    : [`${type} x : ${semantic}`, `x = (${type})0;`];
}

function probe(name) {
  const compiler = compilers[name];
  const exe = compiler.exe();

  if (!exe) {
    throw new Error(`${name} が見つかりません。`);
  }

  const dir = join(work, `tmp-${name}`);
  mkdirSync(dir, { recursive: true });
  const file = join(dir, `p.${compiler.extension}`);
  const out = join(dir, "p.out");
  const results = [];

  for (const [stage, info] of Object.entries(stages(name === "slang"))) {
    if (compiler.skips(info)) {
      continue;
    }

    for (const [shape, make] of Object.entries(info.shapes)) {
      for (const semantic of semantics) {
        let first = null;
        let passed = null;

        for (const type of types) {
          const [f, w] = declaration(type, semantic);
          writeFileSync(file, make(f, w));
          const run = spawnSync(exe, compiler.args(exe, file, out, info.profile), { encoding: "utf8" });
          const errors = compiler.errors((run.stdout ?? "") + (run.stderr ?? ""));
          first ??= { type, errors };

          if (errors.length === 0 && run.status === 0) {
            passed = type;
            break;
          }
        }

        results.push({ semantic, stage, shape, passed, first });
      }

      process.stdout.write(`${name} ${stage} ${shape}\n`);
    }
  }

  return { version: compiler.version(exe), results };
}

const recorded = {};

for (const name of Object.keys(compilers)) {
  const path = join(work, `${name}.json`);

  if (mode === "--reuse") {
    recorded[name] = JSON.parse(readFileSync(path, "utf8"));
  } else {
    recorded[name] = probe(name);
    writeFileSync(path, JSON.stringify(recorded[name], null, 1));
  }
}

const lines = [
  "# システム値のセマンティクスを、ステージと受け渡しの形ごとに、使用できる (o) ものと使用できないもの (x) に分けた表。",
  "# tools/semantics/system-values.mjs が作る。手で書き換えない。",
  "# 列: コンパイラ、ステージ、受け渡しの形、使用できるもの、使用できないもの。名前は大文字にしてある。表に無い組み合わせは判断しない。",
  ...Object.entries(recorded).map(([name, { version }]) => `# ${name}: ${version}`),
];

for (const [name, { results }] of Object.entries(recorded)) {
  const cells = new Map();

  for (const r of results) {
    const key = `${r.stage}\t${r.shape}`;

    if (!cells.has(key)) {
      cells.set(key, { allowed: [], rejected: [] });
    }

    const semantic = r.semantic.toUpperCase();

    if (r.passed) {
      cells.get(key).allowed.push(semantic);
    } else if (r.first.errors.some(compilers[name].rejects)) {
      cells.get(key).rejected.push(semantic);
    }
  }

  for (const [key, { allowed, rejected }] of cells) {
    lines.push(`${name}\t${key}\t${allowed.join(",")}\t${rejected.join(",")}`);
  }
}

const table = "src/Shaderlyn.Semantics/Symbols/SystemValueSemantics.g.txt";
writeFileSync(table, lines.join("\n") + "\n");
console.log(`${table} を書きました。`);
