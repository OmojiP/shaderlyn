// 各言語の公式の道具 (答え) でシェーダーを検査し、通ったか・誤りの最初の 1 行を並べる。
//
// 使い方 (リポジトリのルートで):
//   node tools/oracles/check.mjs <言語> <ファイルかフォルダー>...
//   node tools/oracles/check.mjs glsl .work/corpus/SaschaWillems-Vulkan/shaders/glsl
//   node tools/oracles/check.mjs wgsl .work/corpus/webgpu-samples --failures   # 通らなかったものだけ出す
//
// 言語: glsl (glslang、Vulkan の意味で SPIR-V を作る) / slang (slangc、モジュールとしてコンパイルする) / wgsl (naga) / godot (版の確認だけ。シェーダーの検査は GD4 で作る)
//
// 道具は環境変数で渡す (SHADERLYN_GLSLANG / SHADERLYN_SLANGC / SHADERLYN_NAGA / SHADERLYN_GODOT)。
// 無ければ PATH の道具の名前 (glslang / slangc / naga / godot) を使う。
// 使った道具の版は tools/oracles/README.md に記録する。
//
// 出力は 1 ファイル 1 行 (「ok」「error」「skip」とパス、誤りなら最初のメッセージ)。最後に件数をまとめる。
// 終了コードは、すべて通れば 0、通らないものがあれば 1、使い方の誤りは 2。

import { spawnSync } from "node:child_process";
import { mkdtempSync, readdirSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, extname, join, relative, resolve } from "node:path";

/** GLSL のステージを表す拡張子。glslang は拡張子でステージを決める。 */
const glslStages = new Set([
  ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese",
  ".mesh", ".task", ".rgen", ".rint", ".rahit", ".rchit", ".rmiss", ".rcall",
]);

const scratch = mkdtempSync(join(tmpdir(), "shaderlyn-oracle-"));

/**
 * 言語ごとの検査。
 * extensions は検査するファイル、check はそのファイルを道具に掛けて { status, message } を返す。
 */
const languages = {
  glsl: {
    tool: "glslang",
    extensions: [...glslStages, ".glsl"],
    check(tool, file) {
      // 拡張子でステージが決まらないファイル (.glsl) は取り込まれる側なので、単体では検査しない。
      if (!glslStages.has(extname(file))) {
        return { status: "skip", message: "取り込まれるファイル (ステージが無い)" };
      }

      return run(tool, ["-V", "--target-env", "vulkan1.3", `-I${dirname(file)}`, "-o", join(scratch, "out.spv"), file], /^ERROR: /m);
    },
  },
  slang: {
    tool: "slangc",
    extensions: [".slang"],
    check(tool, file) {
      // SPIR-V ではなくモジュールにする。エントリポイントを持たず import されるだけのファイルも、意味まで検査できる。
      return run(tool, [file, "-o", join(scratch, "out.slang-module")], /^[^\n]*\berror\b[^\n]*$/m);
    },
  },
  wgsl: {
    tool: "naga",
    extensions: [".wgsl"],
    check(tool, file) {
      return run(tool, [file], /^error[^\n]*$/m);
    },
  },
  godot: {
    tool: "godot",
    extensions: [],
    check() {
      return { status: "skip", message: "Godot のシェーダーの検査は GD4 で作る" };
    },
  },
};

/** 道具を動かし、通ったか・最初の誤りを返す。 */
function run(tool, args, errorLine) {
  const result = spawnSync(tool, args, { encoding: "utf8" });

  if (result.error) {
    return { status: "error", message: `道具を起動できません: ${result.error.message}` };
  }

  if (result.status === 0) {
    return { status: "ok", message: "" };
  }

  const output = `${result.stdout ?? ""}\n${result.stderr ?? ""}`;
  const first = output.match(errorLine)?.[0] ?? output.split("\n").find(line => line.trim() !== "") ?? `終了コード ${result.status}`;
  return { status: "error", message: first.trim() };
}

/** フォルダーの下から、拡張子の合うファイルを名前順に集める。 */
function collect(path, extensions) {
  if (statSync(path).isFile()) {
    return [path];
  }

  const files = [];

  for (const entry of readdirSync(path, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    if (entry.name === ".git") {
      continue;
    }

    const child = join(path, entry.name);

    if (entry.isDirectory()) {
      files.push(...collect(child, extensions));
    } else if (extensions.includes(extname(entry.name))) {
      files.push(child);
    }
  }

  return files;
}

const args = process.argv.slice(2);
const failuresOnly = args.includes("--failures");
const [languageName, ...targets] = args.filter(a => !a.startsWith("--"));
const language = languages[languageName];

if (!language || (targets.length === 0 && languageName !== "godot")) {
  console.error("使い方: node tools/oracles/check.mjs <glsl|slang|wgsl|godot> <ファイルかフォルダー>... [--failures]");
  process.exit(2);
}

const variable = `SHADERLYN_${language.tool.toUpperCase()}`;
const tool = process.env[variable] || language.tool;
const probe = spawnSync(tool, ["--version"], { encoding: "utf8" });

if (probe.error) {
  console.error(`${language.tool} を起動できません (${tool})。環境変数 ${variable} に場所を渡すか、PATH に置いてください。`);
  process.exit(2);
}

if (languageName === "godot") {
  // 版を出して、起動できることだけを確かめる。
  const version = spawnSync(tool, ["--headless", "--version"], { encoding: "utf8" });
  console.log(version.stdout.trim().split("\n").at(-1));
  rmSync(scratch, { recursive: true, force: true });
  process.exit(version.status === 0 ? 0 : 1);
}

const counts = { ok: 0, error: 0, skip: 0 };

try {
  for (const target of targets) {
    for (const file of collect(resolve(target), language.extensions)) {
      const { status, message } = language.check(tool, file);
      counts[status]++;

      if (!failuresOnly || status === "error") {
        console.log(`${status}\t${relative(process.cwd(), file)}${message ? `\t${message}` : ""}`);
      }
    }
  }
} finally {
  rmSync(scratch, { recursive: true, force: true });
}

console.log(`通った ${counts.ok} 件 / 通らない ${counts.error} 件 / 検査しない ${counts.skip} 件`);
process.exit(counts.error === 0 ? 0 : 1);
