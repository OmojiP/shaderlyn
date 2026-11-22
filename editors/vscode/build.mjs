// 拡張を 1 つの JavaScript (out/extension.js) にまとめる。
//
//   node build.mjs               開発用 (ソースマップを付ける)
//   node build.mjs --watch       変更のたびにまとめ直す
//   node build.mjs --production  配布用 (縮める。ソースマップを付けない)
//
// node_modules を vsix に入れると、依存のファイルが数百になり、拡張の読み込みが遅くなる。
// まとめた依存のライセンスは、まとめたファイルの隣 (out/THIRD-PARTY-NOTICES-npm.txt) に書き出す。
// 依存を変えても一覧がずれないよう、esbuild が実際にまとめたパッケージだけから作る。
import * as esbuild from "esbuild";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const watch = process.argv.includes("--watch");
const production = process.argv.includes("--production");

/** @type {esbuild.BuildOptions} */
const options = {
  entryPoints: ["src/extension.ts"],
  bundle: true,
  outfile: "out/extension.js",
  // vscode は VS Code が渡すモジュールなので、まとめない。
  external: ["vscode"],
  format: "cjs",
  platform: "node",
  // package.json の engines (VS Code 1.90) の Electron が持つ Node のバージョン。
  target: "node20",
  sourcemap: !production,
  minify: production,
  metafile: true,
  logLevel: "info",
};

/**
 * まとめたパッケージのライセンスを 1 つのファイルに書き出す。
 * @param {esbuild.Metafile} metafile esbuild が返す、まとめた入力の一覧。
 */
function writeNotices(metafile) {
  const packages = new Set();

  for (const input of Object.keys(metafile.inputs)) {
    const parts = input.split("/");
    const at = parts.lastIndexOf("node_modules");

    if (at >= 0) {
      const length = parts[at + 1].startsWith("@") ? 2 : 1;
      packages.add(parts.slice(0, at + 1 + length).join("/"));
    }
  }

  const sections = [...packages].sort().map((directory) => {
    const manifest = JSON.parse(readFileSync(join(directory, "package.json"), "utf8"));
    const license = readdirSync(directory).find((name) => /^(licen[sc]e|copying)(\.|$)/i.test(name));

    if (license === undefined) {
      throw new Error(`${manifest.name} にライセンスのファイルがありません。`);
    }

    const rule = "-".repeat(78);
    return `${rule}\n${manifest.name} ${manifest.version} (${manifest.license})\n${rule}\n\n`
      + readFileSync(join(directory, license), "utf8").trim() + "\n";
  });

  writeFileSync(
    "out/THIRD-PARTY-NOTICES-npm.txt",
    "Shaderlyn for VS Code - bundled npm packages\n"
      + "============================================\n\n"
      + "out/extension.js includes the following npm packages.\n\n"
      + sections.join("\n"));
}

if (watch) {
  const context = await esbuild.context({
    ...options,
    plugins: [{ name: "notices", setup: (build) => build.onEnd((result) => result.metafile && writeNotices(result.metafile)) }],
  });
  await context.watch();
} else {
  const result = await esbuild.build(options);
  writeNotices(result.metafile);
}
