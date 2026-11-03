// GLSL のシェーダーを glslang と Shaderlyn の両方に掛け、指摘を行ごとに比べる。
//
// 使い方 (リポジトリのルートで):
//   node tools/oracles/compare-glsl.mjs <ファイルかフォルダー>... [オプション]
//
//   # glslang の Test を、付いている正解 (baseResults) と比べる
//   node tools/oracles/compare-glsl.mjs .work/corpus/glslang/Test --baseline=.work/corpus/glslang/Test/baseResults --gtests=.work/corpus/glslang/gtests
//   # glslang を動かして比べる (OpenGL の意味。--vulkan で Vulkan の意味の SPIR-V を作る)
//   node tools/oracles/compare-glsl.mjs .work/corpus/ogl-samples/data
//   # 拡張子がステージを表さないシェーダー集は、ステージを対応づける
//   node tools/oracles/compare-glsl.mjs .work/corpus/LearnOpenGL/src --stage-map=vs:vert,fs:frag,gs:geom
//
// オプション:
//   --baseline=<フォルダー>  glslang を動かさず、<フォルダー>/<ファイル名>.out の ERROR の行を正解にする (glslang の Test の形)
//   --gtests=<フォルダー>    glslang の gtests (*.FromFile.cpp) から、1 つのファイルをコンパイルする試験だけを選び、
//                            Vulkan と OpenGL のどちらの意味でコンパイルしたかを読む (--baseline と使う)。
//                            渡さなければ、ファイルの名前で試験の種類を見分ける (baselineExclusions)
//   --vulkan                 glslang に -V --target-env vulkan1.3 を渡す。Shaderlyn には glsl: target: vulkan1.3 を渡す
//   --stage-map=<a:b,...>    拡張子 .a のファイルを .b のステージとして読む (作業用のフォルダーに写して読ませる)
//   --details                食い違いを 1 件ずつ出す
//   --json=<パス>            ファイルごとの結果を JSON で書く
//
// 道具の場所は環境変数で渡す。SHADERLYN_GLSLANG (無ければ PATH の glslang)、
// SHADERLYN_CLI (無ければ src/Shaderlyn.Cli.App の Release のビルドの出力)。
//
// glslang は #line で付け替えた行番号で報告する。Shaderlyn の指摘の行番号も、ファイルの #line を読んで同じ番号に直してから比べる (logicalLines)。
//
// 「誤報告」は glslang が誤りにしないファイルに Shaderlyn が出したエラー、
// 「見落とし」は glslang の誤りの行に Shaderlyn のエラーが無いもの。行の無い誤り (リンクなど) は、ファイルにエラーがあればよいとする。
// 終了コードは、食い違いが無ければ 0、あれば 1、使い方の誤りは 2。

import { spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, extname, join, relative, resolve } from "node:path";

/** glslang がステージを決める拡張子。 */
const stages = new Set([
  ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese",
  ".mesh", ".task", ".rgen", ".rint", ".rahit", ".rchit", ".rmiss", ".rcall",
]);

/**
 * glslang の Test のうち、1 つのファイルをコンパイルした結果でないもの。--gtests を渡さないときに、名前の先頭で見分けて比べない。
 */
const baselineExclusions = [
  ["hlsl.", "HLSL として読ませる"],
  ["preprocessor.", "-E (プリプロセスだけ) の出力"],
  ["link", "複数のファイルをつないだ結果"],
  ["vk.relaxed", "複数のファイルをつないだ結果"],
  ["spv.unit", "複数のファイルをつないだ結果"],
  ["glsl.-", "コマンドラインのオプションの試験"],
  ["spv.debuginfo.hlsl.", "HLSL として読ませる"],
  ["spv.register.", "HLSL のレジスタの割り当ての試験"],
  ["spv.glsl.register.", "HLSL のレジスタの割り当ての試験"],
];

/** Shaderlyn が意図して glslang と違う結果を出すファイル (docs/guide/environments.md の「glslang と違う所」)。比べない。 */
const knownDifferences = [
  ["cppMerge.frag", "マクロを通した実引数の、空白を挟んだ字句を ## で連結する glslang の動き。C の規則どおり連結しない"],
];

const vulkanMode = { target: "vulkan1.0", defines: [] };
const vulkan11 = { target: "vulkan1.1", defines: [] };
const vulkan11Spirv14 = { target: "vulkan1.1", spirv: "1.4", defines: [] };
const vulkan13 = { target: "vulkan1.3", defines: [] };
const vulkan14 = { target: "vulkan1.4", defines: [] };

/**
 * glslang の gtests の試験の種類と、その試験のファイルをコンパイルする意味 (Spv.FromFile.cpp などの TEST_P の引数)。
 * 載っていない種類 (HLSL、リンク、プリプロセスだけ、設定ファイル、入出力の割り当て、set と binding の上限を緩める既定でない設定) は比べない。
 * target と spirv は設定ファイルの glsl: target / spirv に渡す (vulkan なら Shaderlyn も VULKAN を 100 にする)。
 * defines はほかに定義するマクロ。compileOnly はリンクしない (main が無くてよい)。
 * gtests はほとんどの試験で location と binding をコンパイラに決めさせる (TestFixture.h の automap)。
 * 決めさせない試験 (noAutoMap) のほかは、設定ファイルの glsl: auto-map-locations / auto-map-bindings を true にする。
 */
const gtestFixtures = {
  CompileToAstTest: { target: "opengl", defines: [] },
  CompileToAstTestNV: { target: "opengl", defines: [] },
  CompileOpenGLToSpirvTest: { target: "opengl", spirv: "1.0", defines: [] },
  OpenGLSemantics: { target: "opengl", spirv: "1.0", defines: [], noAutoMap: true },
  CompileVulkanToSpirvTest: vulkanMode,
  CompileVulkanToSpirvTestNoLink: { target: "vulkan1.3", spirv: "1.0", defines: [], compileOnly: true },
  CompileToSpirv14TestNoLink: { target: "vulkan1.3", spirv: "1.4", defines: [], compileOnly: true },
  CompileVulkanToSpirvDeadCodeElimTest: vulkanMode,
  CompileVulkan1_1ToSpirvTest: vulkan11,
  CompileToSpirv14Test: vulkan11Spirv14,
  CompileToSpirv16Test: vulkan13,
  VulkanSemantics: { ...vulkanMode, noAutoMap: true },
  VulkanAstSemantics: vulkanMode,
  CompileVulkanToSpirvTestQCOM: vulkanMode,
  CompileVulkanToSpirv13TestQCOM: vulkan11,
  CompileVulkanToSpirv14TestQCOM: vulkan11Spirv14,
  CompileVulkanToSpirvTestAMD: vulkanMode,
  CompileVulkanToSpirvTestNV: vulkanMode,
  CompileVulkanToSpirv14TestNV: vulkan11Spirv14,
  CompileUpgradeTextureToSampledTextureAndDropSamplersTest: vulkanMode,
  GlslSpirvDebugInfoTest: vulkanMode,
  GlslNonSemanticShaderDebugInfoTest: vulkanMode,
  GlslNonSemanticShaderDebugInfoVulkanLatestTest: vulkan14,
  GlslSplitBarrierEXTTest: vulkan14,
};

/** glslang の出力のうち、誤りではない ERROR の行。 */
const ignoredErrors = [/node is still EOpNull!/, /compilation errors\.\s+No code generated/, /^\d+ compilation errors/];

const args = process.argv.slice(2);
const option = name => args.find(a => a.startsWith(`--${name}=`))?.slice(name.length + 3);
const baseline = option("baseline");
const gtests = option("gtests");
const vulkan = args.includes("--vulkan");
const details = args.includes("--details");
const jsonPath = option("json");
const stageMap = new Map((option("stage-map") ?? "").split(",").filter(Boolean).map(pair => {
  const [from, to] = pair.split(":");
  return [`.${from}`, `.${to}`];
}));
const targets = args.filter(a => !a.startsWith("--"));

if (targets.length === 0) {
  console.error("使い方: node tools/oracles/compare-glsl.mjs <ファイルかフォルダー>... [--baseline=<フォルダー>] [--gtests=<フォルダー>] [--vulkan] [--stage-map=vs:vert,...] [--details] [--json=<パス>]");
  process.exit(2);
}

const glslang = process.env.SHADERLYN_GLSLANG || "glslang";
const cli = process.env.SHADERLYN_CLI
  || resolve("src/Shaderlyn.Cli.App/bin/Release/net10.0", process.platform === "win32" ? "shaderlyn.exe" : "shaderlyn");

if (!existsSync(cli) && !process.env.SHADERLYN_CLI) {
  console.error(`Shaderlyn の CLI がありません (${cli})。dotnet build src/Shaderlyn.Cli.App -c Release でビルドするか、SHADERLYN_CLI に場所を渡してください。`);
  process.exit(2);
}

const scratch = mkdtempSync(join(tmpdir(), "shaderlyn-compare-"));

try {
  // gtests の試験の一覧から、ファイルごとのコンパイルの意味を読む。
  const modes = gtests ? readGtests(gtests) : undefined;

  // 比べるファイルを集める。stageMap の拡張子のファイルは、作業用のフォルダーにステージの拡張子を足して写す。
  const files = [];

  for (const target of targets) {
    for (const file of collect(resolve(target))) {
      const name = basename(file);
      const excluded = (modes ? !modes.has(name) : baseline !== undefined && baselineExclusions.some(([prefix]) => name.startsWith(prefix)))
        || knownDifferences.some(([known]) => name === known);

      if (excluded || (baseline && !existsSync(join(baseline, `${name}.out`)))) {
        continue;
      }

      let analyzed = file;

      if (stageMap.has(extname(file))) {
        analyzed = join(scratch, relative(resolve(target), file) + stageMap.get(extname(file)));
        mkdirSync(dirname(analyzed), { recursive: true });
        copyFileSync(file, analyzed);
      }

      const mode = modes?.get(name) ?? (vulkan ? vulkan13 : { target: "opengl", defines: [] });
      files.push({ file, analyzed, mode, ours: [], theirs: [] });
    }
  }

  // glslang の誤り。
  for (const entry of files) {
    entry.theirs = baseline
      ? parseGlslang(readFileSync(join(baseline, `${basename(entry.file)}.out`), "utf8"))
      : runGlslang(entry.analyzed);
  }

  // Shaderlyn の指摘。同じ意味のファイルを束ねて CLI に渡す。向けは設定ファイルで渡す。
  const byPath = new Map(files.map(entry => [resolve(entry.analyzed).toLowerCase(), entry]));

  for (const [, group] of Map.groupBy(files, entry => `${entry.mode.target} ${entry.mode.spirv} ${entry.mode.noAutoMap ?? false} ${entry.mode.defines.join(" ")}`)) {
    for (let i = 0; i < group.length; i += 80) {
      const batch = group.slice(i, i + 80);
      const defines = batch[0].mode.defines.flatMap(define => ["--define", define]);
      const result = spawnSync(cli, ["--config", writeConfig(batch[0].mode), "--environment", "glsl", "--format", "json", "--include-trace", "none", "--base-path", scratch, ...defines, ...batch.map(e => e.analyzed)],
        { encoding: "utf8", maxBuffer: 1 << 30 });

      for (const diagnostic of JSON.parse(result.stdout).diagnostics) {
        if (diagnostic.severity === "info") {
          continue;
        }

        // 取り込んだファイルの中の指摘は、取り込んだ側のファイルの名前で付かない。どのファイルのものか分からないので数えない。
        // リンクしない試験 (compileOnly) では、main が無いこと (CF0301) は誤りではない。
        const entry = byPath.get(resolve(scratch, diagnostic.file).toLowerCase());

        if (entry && !(entry.mode.compileOnly && diagnostic.id === "CF0301")) {
          entry.lines ??= logicalLines(readFileSync(entry.file, "utf8"));
          const line = entry.lines?.[diagnostic.line] ?? diagnostic.line;
          entry.ours.push({ id: diagnostic.id, severity: diagnostic.severity, line, message: diagnostic.message });
        }
      }
    }
  }

  // 比べる。
  const summary = { files: files.length, glslangOk: 0, falsePositiveFiles: 0, falsePositives: 0, warningFiles: 0, glslangError: 0, missedFiles: 0, missed: 0, errorLines: 0 };

  for (const entry of files) {
    const errors = entry.ours.filter(d => d.severity === "error");
    const display = relative(process.cwd(), entry.file);

    if (entry.theirs.length === 0) {
      summary.glslangOk++;

      if (errors.length > 0) {
        summary.falsePositiveFiles++;
        summary.falsePositives += errors.length;
        entry.status = "false-positive";
      }

      if (entry.ours.some(d => d.severity === "warning")) {
        summary.warningFiles++;
      }

      if (details) {
        for (const d of entry.ours) {
          console.log(`誤報告\t${display}:${d.line}\t${d.id} ${d.severity}\t${d.message}`);
        }
      }

      continue;
    }

    summary.glslangError++;
    const ourLines = new Set(errors.map(d => d.line));
    const missed = entry.theirs.filter(e => e.line === undefined ? errors.length === 0 : !ourLines.has(e.line));
    summary.errorLines += entry.theirs.length;
    summary.missed += missed.length;

    if (missed.length > 0) {
      summary.missedFiles++;
      entry.status = "missed";

      if (details) {
        for (const e of missed) {
          console.log(`見落とし\t${display}:${e.line ?? "-"}\t${e.message}`);
        }
      }
    }
  }

  if (jsonPath) {
    writeFileSync(jsonPath, JSON.stringify(files.map(({ file, mode, ours, theirs, status }) => ({ file: relative(process.cwd(), file), target: mode.target, status: status ?? "ok", ours, theirs })), null, 1));
  }

  console.log(`ファイル ${summary.files} 件`);
  console.log(`glslang が通す ${summary.glslangOk} 件: 誤報告のあるファイル ${summary.falsePositiveFiles} 件 (エラー ${summary.falsePositives} 件)、警告のあるファイル ${summary.warningFiles} 件`);
  console.log(`glslang が誤りにする ${summary.glslangError} 件 (誤りの行 ${summary.errorLines} 件): 見落としのあるファイル ${summary.missedFiles} 件 (行 ${summary.missed} 件)`);
  process.exitCode = summary.falsePositiveFiles === 0 && summary.missedFiles === 0 ? 0 : 1;
} finally {
  rmSync(scratch, { recursive: true, force: true });
}

/**
 * glslang の gtests (*.FromFile.cpp) の INSTANTIATE_TEST_SUITE_P から、ファイルの名前ごとのコンパイルの意味を読む。
 * gtestFixtures に無い種類の試験のファイルは載せない。
 */
function readGtests(directory) {
  const modes = new Map();

  for (const name of readdirSync(directory).filter(n => n.endsWith(".FromFile.cpp"))) {
    const source = readFileSync(join(directory, name), "utf8");

    for (const suite of source.matchAll(/INSTANTIATE_TEST_SUITE_P\(\s*\w+\s*,\s*(\w+)\s*,([\s\S]*?)\n\);/g)) {
      const mode = gtestFixtures[suite[1]];

      if (mode) {
        for (const file of suite[2].matchAll(/"([^"]+)"/g)) {
          modes.set(file[1], mode);
        }
      }
    }
  }

  return modes;
}

/** その向けの設定ファイルを作業用のフォルダーに書き、パスを返す。 */
function writeConfig({ target, spirv, noAutoMap }) {
  const autoMap = gtests !== undefined && !noAutoMap;
  const path = join(scratch, `shaderlyn-${target}-${spirv ?? "default"}${autoMap ? "-automap" : ""}.yml`);

  if (!existsSync(path)) {
    const autoMapKeys = autoMap ? "  auto-map-locations: true\n  auto-map-bindings: true\n" : "";
    writeFileSync(path, `version: 1\nenvironment: glsl\nglsl:\n  target: ${target}\n${spirv ? `  spirv: "${spirv}"\n` : ""}${autoMapKeys}`);
  }

  return path;
}

/**
 * glslang を動かし、誤りを返す。
 * -l でリンクまで行う。Shaderlyn はステージのファイルに main が無いこと (CF0301) と、関数の本体が無いこと (CF0311) を誤りにする。
 * glslang ではどちらもリンクの誤りなので、リンクしないと比べられない。
 */
function runGlslang(file) {
  const options = vulkan ? ["-V", "--target-env", "vulkan1.3", "-o", join(scratch, "out.spv")] : [];
  const result = spawnSync(glslang, [...options, "-l", `-I${dirname(file)}`, file], { encoding: "utf8" });

  if (result.error) {
    console.error(`glslang を起動できません (${glslang})。環境変数 SHADERLYN_GLSLANG に場所を渡すか、PATH に置いてください。`);
    process.exit(2);
  }

  return parseGlslang(`${result.stdout}\n${result.stderr}`);
}

/**
 * #line で付け替えた後の行番号を、ファイルの行ごとに求める。#line が無ければ null。
 * glslang と同じく、ES か #version 330 以降では #line N の次の行が N、それより前のバージョンでは N + 1 になる。
 * 行番号の式は、整数を本体にする #define を展開して評価する (#line L1 + L2)。評価できなければ、そこから後ろは直さない。
 * #if で読み飛ばす分岐は見ない (glslang の Test では #line を分岐の中に書かない)。
 */
function logicalLines(text) {
  const physical = text.split(/\r?\n/);

  if (!physical.some(line => /^\s*#\s*line\b/.test(line))) {
    return null;
  }

  const version = text.match(/^\s*#\s*version\s+(\d+)\s*(\w*)/m);
  const nextLineIsN = !version || version[2] === "es" || Number(version[1]) >= 330 || Number(version[1]) === 100;
  const macros = new Map();
  const mapped = [0];
  let offset = 0;
  let known = true;

  for (let i = 0; i < physical.length; i++) {
    const number = i + 1;
    mapped[number] = known ? number + offset : undefined;
    const define = physical[i].match(/^\s*#\s*define\s+(\w+)\s+(\d+)\s*$/);

    if (define) {
      macros.set(define[1], define[2]);
    }

    const directive = physical[i].match(/^\s*#\s*line\s+(.*?)\s*(\/\/.*)?$/);

    if (!directive || !known) {
      continue;
    }

    const value = evaluateLine(directive[1], macros);

    if (value === undefined) {
      known = false;
      continue;
    }

    offset = value - (number + 1) + (nextLineIsN ? 0 : 1);

    // #line の行そのものの誤り (後ろの余分な記述など) は、付け替えた後の番号で、次の行の 1 つ前として報告される。
    // 式の 0 での割り算は付け替える前に報告されるので、前の番号のままにする。
    if (!/[/%]\s*0\b/.test(directive[1])) {
      mapped[number] = number + offset;
    }
  }

  return mapped;
}

/** #line の行番号 (最初の式) を評価する。後ろの文字列の番号は読まない。評価できなければ undefined。 */
function evaluateLine(expression, macros) {
  // 識別子は整数のマクロなら展開し、それ以外 (中身の無いマクロなど) は消す。
  const tokens = expression.replace(/[A-Za-z_]\w*/g, name => ` ${macros.get(name) ?? ""} `).match(/\d+|[-+*/%()]|\S/g) ?? [];
  const first = [];

  // 項 (数か閉じ括弧) の後ろに項の始まり (数か開き括弧) が来たら、そこで最初の式が終わる (後ろは文字列の番号)。
  for (const token of tokens) {
    const previous = first.at(-1);

    if (previous !== undefined && /^(\d+|\))$/.test(previous) && /^(\d+|\()$/.test(token)) {
      break;
    }

    first.push(token);
  }

  try {
    const value = first.length > 0 ? Function(`"use strict"; return (${first.join(" ")});`)() : undefined;
    // 0 で割ると、glslang は誤りにして 0 とする (#line 200 % 0)。
    return Number.isInteger(value) ? value : Number.isNaN(value) || value === Infinity || value === -Infinity ? 0 : undefined;
  } catch {
    return undefined;
  }
}

/** glslang の出力から ERROR の行を取り出す。行番号は「<文字列の番号かパス>:<行>:」の形。 */
function parseGlslang(output) {
  const errors = [];

  for (const match of output.matchAll(/^ERROR: (.*)$/gm)) {
    const text = match[1];

    if (ignoredErrors.some(pattern => pattern.test(text))) {
      continue;
    }

    const located = text.match(/^(.*):(\d+): (.*)$/);
    errors.push(located ? { line: Number(located[2]), message: located[3].trim() } : { message: text.trim() });
  }

  return errors;
}

/** フォルダーの下から、ステージの拡張子 (と stageMap の拡張子) のファイルを名前順に集める。 */
function collect(path) {
  if (statSync(path).isFile()) {
    return [path];
  }

  const files = [];

  for (const entry of readdirSync(path, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    // glslang の Test は、下のフォルダーに取り込まれる側や別の試験がある。正解と比べるときは直下だけを見る。
    if (entry.name === ".git" || (baseline && entry.isDirectory())) {
      continue;
    }

    const child = join(path, entry.name);

    if (entry.isDirectory()) {
      files.push(...collect(child));
    } else if (stages.has(extname(entry.name)) || stageMap.has(extname(entry.name))) {
      files.push(child);
    }
  }

  return files;
}
