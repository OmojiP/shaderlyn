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
//   --vulkan                 glslang に -V --target-env vulkan1.3 を渡す。Shaderlyn には glsl: target: vulkan を渡す
//   --stage-map=<a:b,...>    拡張子 .a のファイルを .b のステージとして読む (作業用のフォルダーに写して読ませる)
//   --details                食い違いを 1 件ずつ出す
//   --json=<パス>            ファイルごとの結果を JSON で書く
//
// 道具の場所は環境変数で渡す。SHADERLYN_GLSLANG (無ければ PATH の glslang)、
// SHADERLYN_CLI (無ければ src/Shaderlyn.Cli.App の Release のビルドの出力)。
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

const vulkanMode = { target: "vulkan", defines: [] };

/**
 * glslang の gtests の試験の種類と、その試験のファイルをコンパイルする意味。
 * 載っていない種類 (HLSL、リンク、プリプロセスだけ、設定ファイル、入出力の割り当て) は比べない。
 * target は設定ファイルの glsl: target に渡す (vulkan なら Shaderlyn も VULKAN を 100 にする)。
 * defines はほかに glslang が定義するマクロ (-G の GL_SPIRV)。compileOnly はリンクしない (main が無くてよい)。
 */
const gtestFixtures = {
  CompileToAstTest: { target: "opengl", defines: [] },
  CompileToAstTestNV: { target: "opengl", defines: [] },
  CompileOpenGLToSpirvTest: { target: "opengl", defines: ["GL_SPIRV=100"] },
  OpenGLSemantics: { target: "opengl", defines: ["GL_SPIRV=100"] },
  CompileVulkanToSpirvTest: vulkanMode,
  CompileVulkanToSpirvRelaxSetBindingLimitsTest: vulkanMode,
  CompileVulkanToSpirvTestNoLink: { ...vulkanMode, compileOnly: true },
  CompileToSpirv14TestNoLink: { ...vulkanMode, compileOnly: true },
  CompileVulkanToSpirvDeadCodeElimTest: vulkanMode,
  CompileVulkan1_1ToSpirvTest: vulkanMode,
  CompileToSpirv14Test: vulkanMode,
  CompileToSpirv16Test: vulkanMode,
  VulkanSemantics: vulkanMode,
  VulkanAstSemantics: vulkanMode,
  CompileVulkanToSpirvTestQCOM: vulkanMode,
  CompileVulkanToSpirv13TestQCOM: vulkanMode,
  CompileVulkanToSpirv14TestQCOM: vulkanMode,
  CompileVulkanToSpirvTestAMD: vulkanMode,
  CompileVulkanToSpirvTestNV: vulkanMode,
  CompileVulkanToSpirv14TestNV: vulkanMode,
  CompileUpgradeTextureToSampledTextureAndDropSamplersTest: vulkanMode,
  GlslSpirvDebugInfoTest: vulkanMode,
  GlslNonSemanticShaderDebugInfoTest: vulkanMode,
  GlslNonSemanticShaderDebugInfoVulkanLatestTest: vulkanMode,
  GlslSplitBarrierEXTTest: vulkanMode,
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
      const excluded = modes ? !modes.has(name) : baseline !== undefined && baselineExclusions.some(([prefix]) => name.startsWith(prefix));

      if (excluded || (baseline && !existsSync(join(baseline, `${name}.out`)))) {
        continue;
      }

      let analyzed = file;

      if (stageMap.has(extname(file))) {
        analyzed = join(scratch, relative(resolve(target), file) + stageMap.get(extname(file)));
        mkdirSync(dirname(analyzed), { recursive: true });
        copyFileSync(file, analyzed);
      }

      const mode = modes?.get(name) ?? (vulkan ? vulkanMode : { target: "opengl", defines: [] });
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

  for (const [, group] of Map.groupBy(files, entry => `${entry.mode.target} ${entry.mode.defines.join(" ")}`)) {
    for (let i = 0; i < group.length; i += 80) {
      const batch = group.slice(i, i + 80);
      const defines = batch[0].mode.defines.flatMap(define => ["--define", define]);
      const result = spawnSync(cli, ["--config", writeConfig(batch[0].mode.target), "--environment", "glsl", "--format", "json", "--include-trace", "none", "--base-path", scratch, ...defines, ...batch.map(e => e.analyzed)],
        { encoding: "utf8", maxBuffer: 1 << 30 });

      for (const diagnostic of JSON.parse(result.stdout).diagnostics) {
        if (diagnostic.severity === "info") {
          continue;
        }

        // 取り込んだファイルの中の指摘は、取り込んだ側のファイルの名前で付かない。どのファイルのものか分からないので数えない。
        // リンクしない試験 (compileOnly) では、main が無いこと (CF0301) は誤りではない。
        const entry = byPath.get(resolve(scratch, diagnostic.file).toLowerCase());

        if (entry && !(entry.mode.compileOnly && diagnostic.id === "CF0301")) {
          entry.ours.push({ id: diagnostic.id, severity: diagnostic.severity, line: diagnostic.line, message: diagnostic.message });
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
function writeConfig(target) {
  const path = join(scratch, `shaderlyn-${target}.yml`);

  if (!existsSync(path)) {
    writeFileSync(path, `version: 1\nenvironment: glsl\nglsl:\n  target: ${target}\n`);
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
