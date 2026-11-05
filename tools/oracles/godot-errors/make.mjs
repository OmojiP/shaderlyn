// Godot のシェーダーのコンパイラ (servers/rendering/shader_language.cpp と shader_preprocessor.cpp) の誤りごとに、最小の例を作る。
// Godot には誤りの試験の集まりが無いので、glslang の Test の代わりにこれを使う。
//
//   node tools/oracles/godot-errors/make.mjs [<出力のフォルダー>]   (既定は .work/v0.3.4/godot-errors/cases)
//   SHADERLYN_GODOT=<Godot の場所> node tools/oracles/record.mjs godot .work/v0.3.4/godot-errors/cases --out=.work/v0.3.4/oracle/godot/errors.json
//
// 名前の先頭の数は、誤りを出す行 (Godot 4.7.2 の shader_language.cpp。e は _set_expected_error、p は shader_preprocessor.cpp)。
// s で始まるものは、シェーダーの種類とステージ、varying、名前の重複など。c で始まるものは型の変換、t はステージ関数とステージごとの組み込み (-ok- は Godot が通すもの)。
// 狙った誤りと違う誤りになるものもあるが、正解は Godot に読ませた結果なので、そのまま試験に使える。
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { cases as batch1 } from "./batch1.mjs";
import { cases as batch2 } from "./batch2.mjs";
import { cases as batch3 } from "./batch3.mjs";
import { cases as batch4 } from "./batch4.mjs";

const out = resolve(process.argv[2] ?? join(".work", "v0.3.4", "godot-errors", "cases"));
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const all = { ...batch1, ...batch2, ...batch3, ...batch4 };
for (const [name, code] of Object.entries(all)) {
  writeFileSync(join(out, `${name}.gdshader`), code.replace(/^\n/, ""));
}
console.log(`${Object.keys(all).length} 個の例を ${out} に書きました`);
