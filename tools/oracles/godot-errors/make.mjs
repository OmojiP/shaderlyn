// Godot のシェーダーのコンパイラ (servers/rendering/shader_language.cpp と shader_preprocessor.cpp) の誤りごとに、最小の例を作る。
// Godot には誤りの試験の集まりが無いので、glslang の Test の代わりにこれを使う。
//
//   node tools/oracles/godot-errors/make.mjs [<出力のフォルダー>]   (既定は .work/v0.3.4/godot-errors/cases)
//   SHADERLYN_GODOT=<Godot の場所> node tools/oracles/record.mjs godot .work/v0.3.4/godot-errors/cases --out=.work/v0.3.4/oracle/godot/errors.json
//
// 名前の先頭の数は、誤りを出す行 (Godot 4.7.2 の shader_language.cpp。e は _set_expected_error、p は shader_preprocessor.cpp)。
// s で始まるものは、シェーダーの種類とステージ、varying、名前の重複など。c で始まるものは型の変換、t はステージ関数とステージごとの組み込み、u は関数の外の宣言 (uniform のヒントと既定値、render_mode、varying)、w は文 (switch・break・continue・discard・for)、a は書き込みと out の実引数、i は添字、n は名前の宣言し直し、v は varying のステージ、r は配列・定数の式・0 での割り算・再帰、p はサンプラーの仮引数、g はレンダラーで変わるもの (-ok- は Godot が通すもの)。
// g の例はレンダラーごとのフォルダーに置き、そのフォルダーに project.godot を書く。varying の数の上限などは本物のレンダラーで決まるので、
// SHADERLYN_GODOT_WINDOW=1 を付けて記録する (record.mjs)。
// 狙った誤りと違う誤りになるものもあるが、正解は Godot に読ませた結果なので、そのまま試験に使える。
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { cases as batch1 } from "./batch1.mjs";
import { cases as batch2 } from "./batch2.mjs";
import { cases as batch3 } from "./batch3.mjs";
import { cases as batch4 } from "./batch4.mjs";
import { cases as batch5 } from "./batch5.mjs";
import { cases as batch6 } from "./batch6.mjs";
import { cases as batch7 } from "./batch7.mjs";
import { cases as batch8 } from "./batch8.mjs";
import { cases as batch9 } from "./batch9.mjs";
import { cases as batch10 } from "./batch10.mjs";
import { cases as batch11 } from "./batch11.mjs";
import { cases as batch12 } from "./batch12.mjs";
import { cases as batch13, projects } from "./batch13.mjs";

const out = resolve(process.argv[2] ?? join(".work", "v0.3.4", "godot-errors", "cases"));
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const all = { ...batch1, ...batch2, ...batch3, ...batch4, ...batch5, ...batch6, ...batch7, ...batch8, ...batch9, ...batch10, ...batch11, ...batch12, ...batch13 };
for (const [folder, method] of Object.entries(projects)) {
  mkdirSync(join(out, folder), { recursive: true });
  writeFileSync(join(out, folder, "project.godot"), `config_version=5

[rendering]

renderer/rendering_method="${method}"
`);
}

for (const [name, code] of Object.entries(all)) {
  mkdirSync(dirname(join(out, `${name}.gdshader`)), { recursive: true });
  writeFileSync(join(out, `${name}.gdshader`), code.replace(/^\n/, ""));
}
console.log(`${Object.keys(all).length} 個の例を ${out} に書きました`);
