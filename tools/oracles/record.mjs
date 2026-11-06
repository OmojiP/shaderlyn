// シェーダーをその言語のコンパイラ (答え合わせの道具) に掛け、誤りの行を JSON に記録する。compare.mjs が正解として読む。
//
// 使い方 (リポジトリのルートで):
//   node tools/oracles/record.mjs <言語> <ファイルかフォルダー>... --out=<JSON のパス>
//
//   node tools/oracles/record.mjs godot .work/corpus/godot-demo-projects --out=.work/v0.3.4/oracle/godot/godot-demo-projects.json
//
// 言語と道具:
//   godot  Godot (コンソール版)。SHADERLYN_GODOT (無ければ PATH の godot)。.gdshader を読む (.gdshaderinc は取り込まれる断片なので単体では読まない)
//
// Godot はヘッドレス (--headless) でも、シェーダーのコードを渡すとコンパイラ (ShaderLanguage) に掛ける (dummy の material_storage)。
// ただしヘッドレスでは描画が dummy になり、varying の数の上限 (31) と組み込みの varying の数 (0)、Compatibility の high-end の組み込み関数の扱いが
// エディターと違う。SHADERLYN_GODOT_WINDOW=1 なら、ウィンドウを開いて本物のレンダラーで読む (GPU と画面が要る)。
// レンダラーは、元の project.godot の rendering/renderer/rendering_method を作業用のプロジェクトに写して選ぶ。
// 誤りは「SHADER ERROR: <文>」と「at: (null) (<ファイル>:<行>)」で出る。コンパイラは最初の誤りで止まるので、誤りはファイルごとに 1 つ。
// res:// は、ファイルから上へ辿って最初に見つけた project.godot のフォルダー (無ければファイルのフォルダー) にする。
//
// 出力の形:
//   { "tool": "Godot v4.7.2.stable...", "language": "godot",
//     "files": { "<絶対パス>": { "errors": [ { "line": 4, "message": "...", "file": "<取り込んだファイルの誤りなら、そのパス>" } ] } } }
// 終了コードは、記録できれば 0、使い方の誤りは 2。

import { spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, extname, join, relative, resolve } from "node:path";

const args = process.argv.slice(2);
const language = args[0];
const out = args.find(a => a.startsWith("--out="))?.slice("--out=".length);
const targets = args.slice(1).filter(a => !a.startsWith("--"));

const languages = {
  godot: { extensions: [".gdshader"], record: recordGodot },
};

if (!languages[language] || !out || targets.length === 0) {
  console.error("使い方: node tools/oracles/record.mjs <godot> <ファイルかフォルダー>... --out=<JSON のパス>");
  process.exit(2);
}

const files = collect(targets, languages[language].extensions);
const scratch = mkdtempSync(join(tmpdir(), "shaderlyn-record-"));

try {
  const result = languages[language].record(files);
  mkdirSync(dirname(resolve(out)), { recursive: true });
  writeFileSync(out, JSON.stringify({ language, ...result }, null, 1));
  const failed = Object.values(result.files).filter(f => f.errors.length > 0).length;
  console.log(`${result.tool}: ${files.length} ファイル、誤りのあるファイル ${failed}。${out} に書きました`);
} finally {
  rmSync(scratch, { recursive: true, force: true });
}

/** 指定したファイルとフォルダーから、その言語の拡張子のファイルを集める。 */
function collect(paths, extensions) {
  const found = [];
  const walk = path => {
    if (statSync(path).isDirectory()) {
      for (const name of readdirSync(path)) {
        if (name !== ".git" && name !== ".godot") {
          walk(join(path, name));
        }
      }
    } else if (extensions.includes(extname(path).toLowerCase())) {
      found.push(resolve(path));
    }
  };
  paths.forEach(walk);
  return found.sort();
}

/** Godot に掛ける。res:// の根 (project.godot のフォルダー) ごとに 1 回起動する。 */
function recordGodot(paths) {
  const godot = process.env.SHADERLYN_GODOT || "godot";

  // コードを Shader に渡すと、ヘッドレスでもコンパイラに掛かる。ファイルの区切りを「=== 」の行で、誤りと同じ標準エラーに出す (順に並ぶ)。
  const script = join(scratch, "record.gd");
  writeFileSync(script, [
    "extends SceneTree",
    "func _init():",
    "\tfor path in OS.get_cmdline_user_args():",
    "\t\tprinterr(\"=== \", path)",
    "\t\tvar shader = Shader.new()",
    "\t\tshader.code = FileAccess.get_file_as_string(path)",
    "\t\tshader.get_shader_uniform_list()",
    "\tprinterr(\"=== end\")",
    "\tquit()",
    "",
  ].join("\n"));

  // 元のプロジェクトは開かない (XR の起動などプロジェクトの設定が効いて止まることがある)。
  // 根ごとに作業用の空のプロジェクトを作り、根の下のシェーダーと取り込まれる断片だけを同じ相対パスで写す。res:// はそのまま解決できる。
  const groups = new Map();
  for (const path of paths) {
    const root = projectRoot(path) ?? dirname(path);
    if (!groups.has(root)) {
      groups.set(root, []);
    }
    groups.get(root).push(path);
  }

  const files = {};
  let tool = "";
  let index = 0;

  for (const [root, group] of groups) {
    const project = join(scratch, `project${index++}`);
    mkdirSync(project);
    writeFileSync(join(project, "project.godot"), `config_version=5\n${renderingMethod(root)}`);
    const copies = new Map();
    for (const source of collect([root], [".gdshader", ".gdshaderinc"])) {
      const copy = join(project, relative(root, source));
      mkdirSync(dirname(copy), { recursive: true });
      copyFileSync(source, copy);
      copies.set(resolve(copy).toLowerCase(), source);
    }

    for (let i = 0; i < group.length; i += 100) {
      const batch = group.slice(i, i + 100).map(path => join(project, relative(root, path)));
      const window = process.env.SHADERLYN_GODOT_WINDOW === "1";
      const run = spawnSync(godot, [...(window ? [] : ["--headless"]), "--path", project, "--script", script, "--", ...batch],
        { encoding: "utf8", maxBuffer: 1 << 30, timeout: 600000 });
      const output = run.stderr;
      tool ||= run.stdout.match(/^Godot Engine (v\S+)/m)?.[1] ?? "";

      if (!output.includes("=== end")) {
        throw new Error(`Godot が最後まで動きませんでした (${root})。\n${output.slice(-2000)}`);
      }

      let current;
      let message;
      for (const line of output.split(/\r?\n/)) {
        if (line.startsWith("=== ")) {
          current = line.slice(4) === "end" ? undefined : copies.get(resolve(line.slice(4)).toLowerCase());
          if (current) {
            files[current] = { errors: [] };
          }
          continue;
        }

        const error = line.match(/^SHADER ERROR: (.*)$/);
        if (error) {
          message = error[1];
          continue;
        }

        // at: (null) (:4)、取り込んだファイルなら at: (null) (res://inc.gdshaderinc:2)
        const at = line.match(/^\s+at: \(null\) \((.*):(\d+)\)$/);
        if (at && message !== undefined && current) {
          files[current].errors.push({ line: Number(at[2]), message, ...(at[1] ? { file: at[1] } : {}) });
          message = undefined;
        }
      }
    }
  }

  return { tool: `Godot ${tool}`, files };
}

/** 元のプロジェクトのレンダラーの設定を、作業用の project.godot に書く形で返す。無ければ空 (Godot の既定の Forward+)。 */
function renderingMethod(root) {
  const path = join(root, "project.godot");
  if (!existsSync(path)) {
    return "";
  }

  let section = "";
  for (const raw of readFileSync(path, "utf8").split(/\r?\n/)) {
    const line = raw.trim();
    const header = line.match(/^\[(.*)\]$/);
    if (header) {
      section = header[1];
    } else if (section === "rendering" && /^renderer\/rendering_method\s*=/.test(line)) {
      return `[rendering]\n${line}\n`;
    }
  }

  return "";
}

/** ファイルから上へ辿って、project.godot のあるフォルダーを探す。 */
function projectRoot(path) {
  for (let directory = dirname(path); ; directory = dirname(directory)) {
    if (existsSync(join(directory, "project.godot"))) {
      return directory;
    }
    if (dirname(directory) === directory) {
      return undefined;
    }
  }
}
