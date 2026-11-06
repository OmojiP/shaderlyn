// Slang のファイルを slangc に掛けて、誤りの行を集める (record.mjs から使う)。
//
// slangc は SHADERLYN_SLANGC (無ければ PATH の slangc)。-enable-machine-readable-diagnostics で、誤りをタブ区切りで出させる:
//   E30025<TAB>error<TAB>tests/a.slang<TAB>12<TAB>20<TAB>12<TAB>21<TAB>array size must be non-negative
// 1 つの誤りに span と note の行が続くので、error の行だけを使う。slangc は最初の誤りで止まらず、続けて報告する。
//
// 引数は、slang のテストの指示の行 (//TEST:SIMPLE(…):<引数> と //DIAGNOSTIC_TEST:SIMPLE(…):<引数>) の、最初の止められていないものから読む。
// slangc の引数を書く指示 (SIMPLE / SIMPLE_EX / SIMPLE_LINE / COMPILE / CROSS_COMPILE / REFLECTION) は、その引数をそのまま渡す (-o とその値は除く)。
// 描画のテストの道具の引数を書く指示 (COMPARE_COMPUTE など) は、-vk・-cuda などからコンパイル先を決め、computeMain があればそれをエントリポイントにする。
// tests の中の指示の無いファイル (ほかのテストが import するモジュール) と、//TEST_IGNORE_FILE のあるファイルは読まない (records に入れない)。
// tests の外 (例や Falcor などのコーパス) は、-target spirv に SHADERLYN_SLANG_ARGS (空白で区切る。-I <フォルダー> など) を足して掛ける。
// エントリポイントは [shader("…")] の属性から slangc が見つける。
//
// テストは slang のリポジトリの根からの相対パス (tests/…) で取り込むことがあるので、tests の親のフォルダーで slangc を動かす。

import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { cpus } from "node:os";
import { dirname, join, relative, resolve, sep } from "node:path";

/** slangc の引数を書く指示。 */
const SlangcKinds = new Set(["SIMPLE", "SIMPLE_EX", "SIMPLE_LINE", "COMPILE", "CROSS_COMPILE", "REFLECTION", "CPU_REFLECTION"]);

/** 描画のテストの道具の引数から、slangc のコンパイル先を決める。 */
const RenderTargets = [
  ["-vk", "spirv"], ["-vulkan", "spirv"], ["-cuda", "cuda"], ["-cpu", "cpp"], ["-llvm", "cpp"],
  ["-metal", "metal"], ["-wgpu", "wgsl"], ["-dx12", "hlsl"], ["-d3d12", "hlsl"], ["-dx11", "hlsl"], ["-slang", "hlsl"],
];

/**
 * slangc に掛ける引数を、テストの指示の行から決める。
 * @returns {{ args: string[], directive: string } | undefined} 読まないファイルなら undefined。
 */
export function slangArguments(text) {
  if (/TEST_IGNORE_FILE/.test(text)) {
    return undefined;
  }

  for (const match of text.matchAll(/^[ \t]*\/\/[ \t]*(DIAGNOSTIC_TEST|TEST)(\([^)]*\))?:([A-Z_]+)(\([^)]*\))?:(.*)$/gm)) {
    const kind = match[3];
    const args = split(match[5]);

    if (SlangcKinds.has(kind)) {
      const kept = [];
      for (let i = 0; i < args.length; i++) {
        if (args[i] === "-o") {
          i++;
        } else {
          kept.push(args[i]);
        }
      }
      return { args: kept.filter(a => !/\.slang$/i.test(a) || a.includes("=")), directive: `${match[1]}:${kind}` };
    }

    // slangi で動かすテスト。エントリポイントが無いので、CPU 向けのコードとして掛ける。
    if (kind === "INTERPRET") {
      return { args: ["-target", "cpp"], directive: `${match[1]}:${kind}` };
    }

    if (kind === "LANG_SERVER" || kind === "SPVDB_DEBUGGER" || kind === "EXECUTABLE" || kind === "PERFORMANCE_PROFILE" || kind === "DISPATCHER") {
      return { args: [], directive: `${match[1]}:${kind}` };
    }

    // 描画のテスト (COMPARE_COMPUTE など)。
    const target = RenderTargets.find(([flag]) => args.includes(flag))?.[1] ?? "spirv";
    const entry = /\bcomputeMain\s*\(/.test(text) ? ["-entry", "computeMain", "-stage", "compute"] : [];
    const extra = [];
    for (let i = 0; i < args.length; i++) {
      if (["-capability", "-profile"].includes(args[i]) && args[i + 1] && !args[i + 1].startsWith("-")) {
        extra.push(args[i], args[++i]);
      } else if (args[i] === "-experimental-feature") {
        extra.push(args[i]);
      } else if (args[i] === "-emit-spirv-directly" || args[i] === "-emit-spirv-via-glsl") {
        extra.push(args[i]);
      }
    }
    return { args: ["-target", target, ...entry, ...extra], directive: `${match[1]}:${kind}` };
  }

  return undefined;
}

/** SHADERLYN_SLANG_ARGS の引数。-I の後ろのフォルダーは、作業フォルダーからの絶対パスにする (slangc はファイルのフォルダーで動かす)。 */
function extraArguments() {
  const args = split(process.env.SHADERLYN_SLANG_ARGS ?? "");
  return args.map((a, i) => (args[i - 1] === "-I" ? resolve(a) : a));
}

/** 空白で区切る (引用符の中は区切らない)。 */
function split(text) {
  return [...text.trim().matchAll(/"([^"]*)"|(\S+)/g)].map(m => m[1] ?? m[2]);
}

/** tests のフォルダーの親 (slang のリポジトリの根)。無ければファイルのフォルダー。 */
function workingDirectory(path) {
  const parts = resolve(path).split(sep);
  const index = parts.lastIndexOf("tests");
  return index > 0 ? parts.slice(0, index).join(sep) : dirname(path);
}

/** 1 つのファイルを slangc に掛ける。 */
function run(slangc, path, args) {
  const cwd = workingDirectory(path);
  const input = relative(cwd, path).split(sep).join("/");
  return new Promise(done => {
    const child = spawn(slangc, [input, ...args, "-enable-machine-readable-diagnostics", "-diagnostic-color", "never"], { cwd });
    let stderr = "";
    child.stdout.resume();
    child.stderr.on("data", chunk => { stderr += chunk; });
    const timer = setTimeout(() => child.kill(), 120000);
    child.on("close", code => {
      clearTimeout(timer);
      done({ code, stderr, cwd });
    });
    child.on("error", error => {
      clearTimeout(timer);
      done({ code: -1, stderr: String(error), cwd });
    });
  });
}

/** slangc の出力から誤りを読む。 */
function parse(stderr, cwd, path) {
  const errors = [];
  const seen = new Set();
  for (const line of stderr.split(/\r?\n/)) {
    const fields = line.split("\t");
    // E57004 (SPIR-V にエントリポイントが 1 つも無い) は、エントリポイントの無いモジュールを掛けたときに出る。コードの誤りではないので除く。
    if (fields.length < 8 || !/^(error|fatal)$/.test(fields[1]) || fields[0] === "E57004") {
      continue;
    }

    const file = resolve(cwd, fields[2]);
    const lineNumber = Number(fields[3]);
    const message = fields.slice(7).join("\t");
    const key = `${file}:${lineNumber}:${fields[0]}`;
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    errors.push({ line: lineNumber, message: `${fields[0]}: ${message}`, ...(file.toLowerCase() === path.toLowerCase() ? {} : { file }) });
  }
  return errors;
}

/** Slang のファイルを slangc に掛け、誤りを集める。 */
export async function recordSlang(paths) {
  const slangc = process.env.SHADERLYN_SLANGC || "slangc";
  const files = {};
  const queue = [];

  for (const path of paths) {
    const text = readFileSync(path, "utf8");
    const settings = slangArguments(text)
      ?? (resolve(path).split(sep).includes("tests") || /TEST_IGNORE_FILE/.test(text)
        ? undefined
        : { args: ["-target", "spirv", ...extraArguments()], directive: "none" });
    if (settings) {
      queue.push({ path, ...settings });
    }
  }

  let tool = "";
  const version = await new Promise(done => {
    const child = spawn(slangc, ["-version"]);
    let text = "";
    child.stdout.on("data", c => { text += c; });
    child.stderr.on("data", c => { text += c; });
    child.on("close", () => done(text.trim()));
    child.on("error", () => done(""));
  });
  tool = `slangc ${version}`;

  const skipped = [];
  let next = 0;
  const workers = Array.from({ length: Math.max(1, cpus().length - 2) }, async () => {
    while (next < queue.length) {
      const item = queue[next++];
      // この slangc が知らない引数 (E00017) は外して掛け直す。
      let args = item.args;
      let result = await run(slangc, item.path, args);
      for (let retry = 0; retry < 5; retry++) {
        const unknown = result.stderr.match(/unknown command-line option '([^']+)'/)?.[1];
        if (!unknown || !args.includes(unknown)) {
          break;
        }
        args = args.filter(a => a !== unknown);
        result = await run(slangc, item.path, args);
      }

      const { stderr, cwd, code } = result;

      // 後段のコンパイラ (dxc・metal など) が無くて失敗したものは、正解にならないので記録しない。
      if (/\t(E00100|E52002)\t|failed to load downstream compiler|pass-through compiler not found/.test(stderr)) {
        skipped.push(item.path);
        continue;
      }

      const errors = parse(stderr, cwd, item.path);
      // 誤りの行が無いのに失敗したもの (後段のコンパイラの失敗など) は、行 0 の誤りとして残す。
      if (errors.length === 0 && code !== 0 && !/^E57004	/m.test(stderr)) {
        errors.push({ line: 0, message: `slangc が失敗しました (終了コード ${code}): ${stderr.trim().split(/\r?\n/)[0] ?? ""}` });
      }
      files[item.path] = { errors, args: args.join(" "), directive: item.directive };
    }
  });
  await Promise.all(workers);

  if (skipped.length > 0) {
    console.log(`後段のコンパイラが無いので記録しなかったもの: ${skipped.length} ファイル`);
  }

  return { tool, files };
}
