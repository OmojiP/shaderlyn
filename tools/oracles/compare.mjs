// record.mjs が記録した正解 (コンパイラの誤りの行) と、Shaderlyn の指摘を行ごとに比べる。言語に依らない (GLSL は compare-glsl.mjs)。
//
// 使い方 (リポジトリのルートで):
//   node tools/oracles/compare.mjs <正解の JSON>... [--details] [--json=<パス>] [--use-args]
//
//   node tools/oracles/compare.mjs .work/v0.3.4/oracle/godot/*.json --details
//
// --use-args: 正解に記録したコンパイラの引数 (Slang の試験の -target・-experimental-feature・-specialize と -entry f<T>・-entry と -stage・-unscoped-enum・-D・-I) を、
//   ファイルごとの一時の設定ファイル (slang: target / experimental-features / generic-entry-points / unscoped-enums と defines・include-paths・units の entry と stage) にして Shaderlyn に渡す。
//   -I のパスは、試験を動かすフォルダー (ファイルの上のフォルダーのどれか) からの相対として探す。
//   設定に当たる項目の無い引数 (-entry・-stage・-capability など) は渡さない。付けなければ、どのファイルも --no-config で解析する。
//
// Shaderlyn の CLI の場所は環境変数 SHADERLYN_CLI で渡す (無ければ src/Shaderlyn.Cli.App の Release のビルドの出力)。
//
// 数えるもの:
//   誤報告   コンパイラが誤りにしないファイルに Shaderlyn が出したエラーと警告
//   見落とし コンパイラの誤りの行に、Shaderlyn のエラーが無いもの。取り込んだファイルの中の誤りは、そのファイルの同じ行で探す
//   先走り   コンパイラが誤りにするファイルで、コンパイラの最初の誤りより前の行に Shaderlyn が出したエラー。
//            Godot のように最初の誤りで止まるコンパイラでは、その前は正しいはずなので、誤報告の疑いがある
//   Godot のプリプロセッサの失敗 (最初の # の行に出る) は、行を比べず、ファイルにエラーがあれば合うとする
// 終了コードは、食い違いが無ければ 0、あれば 1、使い方の誤りは 2。

import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, relative, resolve } from "node:path";

const args = process.argv.slice(2);
const details = args.includes("--details");
const jsonOut = args.find(a => a.startsWith("--json="))?.slice("--json=".length);
const useArgs = args.includes("--use-args");
const configDirectory = useArgs ? mkdtempSync(join(tmpdir(), "shaderlyn-oracle-")) : null;
const configs = new Map();
const oracles = args.filter(a => !a.startsWith("--"));

if (oracles.length === 0) {
  console.error("使い方: node tools/oracles/compare.mjs <正解の JSON>... [--details] [--json=<パス>]");
  process.exit(2);
}

const cli = process.env.SHADERLYN_CLI
  || resolve("src/Shaderlyn.Cli.App/bin/Release/net10.0", process.platform === "win32" ? "shaderlyn.exe" : "shaderlyn");

if (!existsSync(cli)) {
  console.error(`Shaderlyn の CLI がありません (${cli})。dotnet build src/Shaderlyn.Cli.App -c Release でビルドするか、SHADERLYN_CLI に場所を渡してください。`);
  process.exit(2);
}

const key = path => resolve(path).toLowerCase();
const rows = [];
let anyDifference = false;

for (const oraclePath of oracles) {
  const oracle = JSON.parse(readFileSync(oraclePath, "utf8"));
  const paths = Object.keys(oracle.files);
  const ours = analyze(paths, oracle.language, path => configFor(oracle.language, oracle.files[path].args, path));

  let falsePositives = 0;
  let misses = 0;
  let early = 0;
  let failing = 0;

  for (const path of paths) {
    const expected = oracle.files[path].errors.map(e => ({ ...e, path: e.file ? includedPath(path, e.file) : path }));
    const reported = ours.filter(d => d.analyzed === key(path));
    const row = { file: path, theirs: expected, ours: reported.map(({ analyzed, ...d }) => d), falsePositives: [], misses: [], early: [] };

    if (expected.length === 0) {
      row.falsePositives = reported.filter(d => d.severity !== "info");
    } else {
      failing++;
      // 行が記録の仕方で決まる誤り (Godot のプリプロセッサの失敗) は、ファイルのどこかに Shaderlyn のエラーがあれば合うとする。
      row.misses = expected.filter(e => !reported.some(d => d.severity === "error" && key(d.file) === key(e.path) && (d.line === e.line || e.lines?.includes(d.line) || anyLine(e))));
      const first = expected.find(e => key(e.path) === key(path) && !anyLine(e));
      row.early = first ? reported.filter(d => d.severity === "error" && key(d.file) === key(path) && d.line < first.line) : [];
    }

    falsePositives += row.falsePositives.length;
    misses += row.misses.length;
    early += row.early.length;
    rows.push(row);

    if (details) {
      for (const d of row.falsePositives) {
        console.log(`誤報告   ${d.file}(${d.line}): ${d.severity} ${d.id}: ${d.message}`);
      }
      for (const e of row.misses) {
        console.log(`見落とし ${e.path}(${e.line}): ${e.message}`);
      }
      for (const d of row.early) {
        console.log(`先走り   ${d.file}(${d.line}): ${d.id}: ${d.message}`);
      }
    }
  }

  anyDifference ||= falsePositives + misses + early > 0;
  console.log(`${oraclePath} (${oracle.tool}): ${paths.length} ファイル、コンパイラが誤りにする ${failing}。`
    + `誤報告 ${falsePositives} 件、見落とし ${misses} 件、先走り ${early} 件`);
}

if (jsonOut) {
  writeFileSync(jsonOut, JSON.stringify(rows, null, 1));
}

if (configDirectory) {
  rmSync(configDirectory, { recursive: true, force: true });
}

process.exit(anyDifference ? 1 : 0);

/** Shaderlyn に掛け、指摘に「どのファイルを解析したときのものか」を付けて返す。 */
function analyze(paths, language, configOf) {
  const results = [];
  for (const path of paths) {
    // 取り込んだファイルの指摘も、解析したファイルごとに分けたいので、1 ファイルずつ渡す。
    const config = configOf(path);
    const run = spawnSync(cli, [...(config ? ["--config", config] : ["--no-config"]), "--environment", language, "--format", "json", "--include-trace", "none", path],
      { encoding: "utf8", maxBuffer: 1 << 30 });
    if (!run.stdout) {
      throw new Error(`Shaderlyn が結果を返しませんでした (${path})。\n${run.stderr}`);
    }
    for (const d of JSON.parse(run.stdout).diagnostics) {
      results.push({ analyzed: key(path), id: d.id, severity: d.severity, file: d.file, line: d.line, message: d.message });
    }
  }
  return results;
}

/**
 * 記録したコンパイラの引数に当たる設定ファイルのパス。--use-args が無いか、設定に当たる引数が無ければ null (--no-config で解析する)。
 * 同じ内容の設定ファイルは 1 つだけ作る。
 */
function configFor(language, commandLine, path) {
  if (!useArgs || language !== "slang" || !commandLine) {
    return null;
  }

  const words = commandLine.split(/\s+/).filter(w => w.length > 0);
  const slang = [];
  const defines = [];
  const includes = [];
  const entries = [];
  const targets = ["spirv", "glsl", "hlsl", "dxil", "metal", "wgsl", "cuda", "ptx", "cpp"];

  for (let i = 0; i < words.length; i++) {
    const word = words[i];
    if (word === "-target" && targets.includes(words[i + 1])) {
      slang.push(`  target: ${words[++i]}`);
    } else if (word === "-experimental-feature") {
      slang.push("  experimental-features: true");
    } else if (word === "-specialize") {
      // -specialize と -entry fragMain<Red> は、エントリポイントの型引数を決める。
      slang.push("  generic-entry-points: true");
    } else if (word === "-entry" && words[i + 1]) {
      // -entry main -stage compute は、設定ファイルの units: の entry と stage に当たる。
      const name = words[++i];
      if (name.includes("<")) {
        slang.push("  generic-entry-points: true");
      }
      entries.push({ name: name.replace(/<.*$/, ""), stage: null });
    } else if (word === "-stage" && words[i + 1]) {
      const stage = words[++i];
      const last = entries.findLast(e => e.stage === null);
      if (last) {
        last.stage = stage;
      }
    } else if (word === "-unscoped-enum") {
      slang.push("  unscoped-enums: true");
    } else if (word === "-D" && words[i + 1]) {
      defines.push(words[++i]);
    } else if (word.startsWith("-D") && word.length > 2) {
      defines.push(word.slice(2));
    } else if ((word === "-I" && words[i + 1]) || (word.startsWith("-I") && word.length > 2)) {
      // 試験の実行は、リポジトリの根 (tests の親) から相対で書く。ファイルの上のフォルダーから、そのパスがあるところを探す。
      const relativePath = word === "-I" ? words[++i] : word.slice(2);
      for (let dir = dirname(resolve(path)); ; dir = dirname(dir)) {
        if (existsSync(join(dir, relativePath))) {
          includes.push(resolve(dir, relativePath).replaceAll("\\", "/"));
          break;
        }
        if (dirname(dir) === dir) break;
      }
    }
  }

  if (slang.length === 0 && defines.length === 0 && includes.length === 0 && entries.length === 0) {
    return null;
  }

  // 同じ項目を 2 度書かない (-target を 2 つ書いた試験は、最初のものを使う)。
  const unique = slang.filter((line, index) => slang.findIndex(other => other.split(":")[0] === line.split(":")[0]) === index);
  const text = [
    ...(unique.length > 0 ? ["slang:", ...unique] : []),
    ...(includes.length > 0 ? ["include-paths:", ...includes.map(d => `  - "${d}"`)] : []),
    ...(defines.length > 0 ? ["defines:", ...defines.map(d => `  - "${d.replaceAll("\\", "\\\\").replaceAll('"', '\\"')}"`)] : []),
    ...(entries.length > 0
      ? ["units:", ...entries.flatMap(e => [
          `  - files: "${relative(configDirectory, resolve(path)).replaceAll("\\", "/")}"`,
          `    entry: ${e.name}`,
          ...(e.stage ? [`    stage: ${e.stage}`] : []),
        ])]
      : []),
  ].join("\n") + "\n";

  if (!configs.has(text)) {
    const file = join(configDirectory, `${configs.size}.shaderlyn.yaml`);
    writeFileSync(file, text);
    configs.set(text, file);
  }

  return configs.get(text);
}

/**
 * 行が記録の仕方で決まり、本当の誤りの行とは限らない誤りか。
 * Godot はプリプロセッサが失敗すると、元のコードのまま読み直して、最初の # の行に「Unknown character #35」を出す (README)。
 */
function anyLine(error) {
  // naga は検証の誤りの一部 (型や入口の誤り) に位置を付けない (行 0)。
  return error.message.startsWith("Tokenizer: Unknown character #35") || error.line === 0;
}

/** 取り込んだファイルの誤り (Godot なら res://...) の、ディスクの上のパス。 */
function includedPath(path, file) {
  if (!file.startsWith("res://")) {
    return resolve(dirname(path), file);
  }
  for (let directory = dirname(path); ; directory = dirname(directory)) {
    if (existsSync(join(directory, "project.godot")) || dirname(directory) === directory) {
      return resolve(directory, file.slice("res://".length));
    }
  }
}
