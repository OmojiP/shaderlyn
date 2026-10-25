// Shaderlyn 0.3.0 → 0.3.1 の、ルール ID の書き換え。
//
// 使い方 (書き換えるプロジェクトのフォルダーで):
//   node <このファイル>              # カレントフォルダーの下を書き換える
//   node <このファイル> <フォルダー>   # そのフォルダーの下を書き換える
//
// C 系の言語に共通のルールの ID (HL0310 など) を、新しい ID (CF0310 など) に置き換え、書き換えたファイルを並べる。
// 設定ファイル (.shaderlyn.yaml)、ベースライン (.json)、シェーダーの抑制のコメント、自作ルールのコード (.cs) を見る。
// bin / obj / .git / node_modules / Library の下は見ない。
// 正規表現で書いた重要度 (/HL03.*/: error など) は置き換えられないので、見つけたら場所を伝える。
// ファイルを直接書き換えるので、先にコミットしておくこと。

import { readFileSync, readdirSync, writeFileSync } from "node:fs";
import { extname, join, resolve } from "node:path";

/** 0.3.0 の ID から 0.3.1 の ID。docs/guide/migration-0.3.1.md の表と同じ。 */
const renamed = {
  HL0001: "CF0001", HL0002: "CF0002", HL0004: "CF0003", HL0008: "CF0004",
  HL0320: "CF0020", HL0321: "CF0021",
  HL0005: "CF0101", HL0006: "CF0102", HL0007: "CF0103", HL0009: "CF0104",
  HL0330: "CF0201", HL0331: "CF0202", HL0332: "CF0203",
  HL0301: "CF0301", HL0310: "CF0310", HL0311: "CF0311", HL0312: "CF0312", HL0313: "CF0313", HL0314: "CF0314", HL0315: "CF0315",
  HL0340: "CF0401", HL0341: "CF0402", HL0342: "CF0403", HL0343: "CF0404", HL0344: "CF0405", HL0346: "CF0406",
  HL0350: "CF0410", HL0351: "CF0411", HL0352: "CF0412", HL0353: "CF0413",
  HL0360: "CF0501", HL0370: "CF0510", HL0371: "CF0511",
};

const pattern = new RegExp(`\\b(${Object.keys(renamed).join("|")})\\b`, "g");

/** 見るファイルの拡張子。 */
const extensions = new Set([
  ".yaml", ".yml", ".json", ".cs",
  ".shader", ".compute", ".hlsl", ".hlsli", ".cginc", ".hlslinc", ".usf", ".ush",
  ".glsl", ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese", ".mesh", ".task",
  ".rgen", ".rint", ".rahit", ".rchit", ".rmiss", ".rcall",
]);

const skipped = new Set(["bin", "obj", ".git", "node_modules", "Library"]);

/** フォルダーの下の、見るファイルを集める。 */
function collect(directory) {
  const files = [];

  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);

    if (entry.isDirectory()) {
      if (!skipped.has(entry.name)) {
        files.push(...collect(path));
      }
    } else if (extensions.has(extname(entry.name).toLowerCase())) {
      files.push(path);
    }
  }

  return files;
}

const root = resolve(process.argv[2] ?? ".");
let changed = 0;

for (const file of collect(root)) {
  const original = readFileSync(file, "utf8");
  const rewritten = original.replace(pattern, (id) => renamed[id]);

  if (rewritten !== original) {
    writeFileSync(file, rewritten, "utf8");
    console.log(file);
    changed++;
  }

  // 設定ファイルの、正規表現で書いた重要度 (/HL.../)。どの ID に当たるかが変わるので、書き直してもらう。
  if (/\.ya?ml$/i.test(file)) {
    rewritten.split(/\r?\n/).forEach((line, index) => {
      if (/^\s*["']?\/[^/]*HL[^/]*\/["']?\s*:/.test(line)) {
        console.log(`  正規表現の重要度を確かめてください (${file}:${index + 1}): ${line.trim()}`);
      }
    });
  }
}

console.log(`書き換えたファイル: ${changed} 件`);
