// Slang の標準ライブラリ (slang のソースの source/slang/*.meta.slang) と標準のモジュール (source/standard-modules) から、
// 宣言した名前を集めて表にする。
//
//   node tools/slang/stdlib.mjs [<slang のリポジトリ>]   (既定は .work/corpus/slang-tests)
//
// 出力: src/Shaderlyn.Semantics/Symbols/SlangStdlib.g.txt。1 行に 1 つ、「種類<TAB>名前」。
//   T  型 (struct・class・interface・enum・typealias・typedef・associatedtype)
//   F  関数 (メソッドを含む。メソッドの名前も関数として見せる)
//   V  値 (関数の外の static const・let・変数、enum の値)
//   M  標準のモジュールの名前 (import slang.neural; の slang/neural)。slangc に入っているので、ファイルを探さない
//   P  名前の前半。C++ のコードで名前を組み立てる関数 (WaveMulti$(opName) の WaveMulti)。この前半で始まる名前を関数として見せる
//
// 標準ライブラリは Slang で書かれていて、C++ のコードで形を作る部分 (${{{{ … }}}} と $(…)) を含む。その部分は読まない。
// 名前は多めに集める (宣言として読めない形も、名前として拾う)。名前が無いことを根拠にする検査を誤らせないためである。
// 標準のモジュールの名前は、slangc が入れる場所 (slang/<フォルダー>、slang/<フォルダー>/<ファイル>、experimental/<ファイル>) から決める。
import { readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join, relative, resolve, sep } from "node:path";

const root = resolve(process.argv[2] ?? join(".work", "corpus", "slang-tests"));
const source = join(root, "source", "slang");
const modulesRoot = join(root, "source", "standard-modules");

const moduleFiles = [];
const modules = new Set();
const prefixes = new Set();
const walk = dir => {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory() && entry.name !== "unit-test") {
      walk(path);
    } else if (entry.name.endsWith(".slang")) {
      moduleFiles.push(path);
    }
  }
};
walk(modulesRoot);

for (const path of moduleFiles) {
  const parts = relative(modulesRoot, path).split(sep);
  const top = parts[0];
  const stem = parts.at(-1).replace(/\.slang$/, "");
  if (top === "experimental") {
    modules.add(`experimental/${stem}`);
  } else {
    modules.add(`slang/${top}`);
    modules.add(`slang/${top}/${stem}`);
    modules.add(`slang/${top}/${stem.replaceAll("-", "_")}`);
  }
}

const files = [
  ...readdirSync(source).filter(n => n.endsWith(".meta.slang")).sort().map(n => join(source, n)),
  ...moduleFiles.sort(),
];

const types = new Set();
const functions = new Set();
const values = new Set();
const keywords = new Set([
  "if", "else", "for", "while", "do", "switch", "case", "default", "return", "break", "continue", "discard", "static", "const", "in", "out",
  "inout", "let", "var", "func", "struct", "class", "interface", "enum", "typedef", "typealias", "extension", "property", "get", "set",
  "this", "This", "true", "false", "sizeof", "alignof", "public", "internal", "private", "__init", "__subscript", "__generic", "where",
]);
const add = (set, name) => {
  if (name && /^[A-Za-z_]\w*$/.test(name) && !keywords.has(name)) {
    set.add(name);
  }
};

for (const file of files) {
  let text = readFileSync(file, "utf8");

  // C++ のコードで名前を組み立てる関数 (WaveMulti$(opName)) は、名前の前半 (5 文字以上) を前方一致として覚える。
  for (const m of text.matchAll(/\b([A-Za-z_]\w{4,})\$\(/g)) {
    prefixes.add(m[1]);
  }

  // C++ の部分 (${{{{ … }}}})、$(…) の差し込み、コメントを除く。
  text = text.replace(/\$\{\{\{\{[\s\S]*?\}\}\}\}/g, " ").replace(/\$\([^)]*\)/g, " ").replace(/\$\w+/g, " ");
  text = text.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/\/\/[^\n]*/g, " ");

  // 名前で呼べるのは、関数の外 (名前空間の中を含む) の宣言だけである。関数・構造体・extension の本体の中は、名前を集めない
  // (本体の中の局所変数を拾うと、利用者の書いた宣言されていない名前を見落とす)。enum の値だけは、本体から集める。
  const full = text;
  text = globalText(text);

  // 型
  for (const m of text.matchAll(/\b(?:struct|class|interface|enum|typealias|associatedtype)\s+(?:class\s+)?([A-Za-z_]\w*)/g)) {
    add(types, m[1]);
  }
  for (const m of text.matchAll(/\btypedef\s+[^;{}]*?\b([A-Za-z_]\w*)\s*;/g)) {
    add(types, m[1]);
  }

  // enum の値
  for (const m of full.matchAll(/\benum\s+(?:class\s+)?[A-Za-z_]\w*[^{;]*\{([^}]*)\}/g)) {
    for (const item of m[1].split(",")) {
      add(values, item.trim().split(/[\s=]/)[0]);
    }
  }

  // 関数とメソッド: 型らしいもの (名前・> ・] ・*) の後ろの、名前 ( の形。呼び出し (return f(…) など) も拾うが、標準ライブラリの名前なので害は無い。
  for (const m of text.matchAll(/(?:[A-Za-z_]\w*|>|\]|\*)\s+([A-Za-z_]\w*)\s*(?:<[^()<>]*(?:<[^()<>]*>[^()<>]*)*>)?\s*\(/g)) {
    add(functions, m[1]);
  }

  // 関数の宣言は、本体の中 (extension・__generic のブロック・構造体) でも、行頭の宣言の形 (型 名前<…>(…)) のものは拾う。
  // 文 (return f(…) など) は拾わない。メソッドの名前も入るが、多めに入るだけで誤報告にはならない。
  for (const m of full.matchAll(/^[ \t]*(?:(?:public|static|internal|inline|__generic<[^>\n]*>|\[[^\]\n]*\]|[A-Za-z_]\w*\([^)\n]*\))\s+)*([A-Za-z_][\w:.]*(?:<[^()\n]*>)?\*?)\s+([A-Za-z_]\w*)\s*(?:<[^()\n]*>)?\s*\(/gm)) {
    if (!["return", "else", "case", "throw", "if", "while", "for", "switch", "new", "goto", "defer"].includes(m[1])) {
      add(functions, m[2]);
    }
  }

  // 関数の外の値: static const T NAME = / let NAME = / T NAME; (行頭の宣言)
  for (const m of text.matchAll(/^\s*(?:(?:public|static|const|let|var|__glsl_extension\([^)]*\)|layout\([^)]*\)|in|out|uniform)\s+)+(?:[A-Za-z_][\w<>,\s]*?\s+)?([A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*(?:=|;|:)/gm)) {
    add(values, m[1]);
  }
}

/** 名前空間でない波括弧の中を空白にした文字列 (関数の外の宣言だけが残る)。 */
function globalText(text) {
  const out = [...text];
  const stack = [];
  for (let i = 0; i < out.length; i++) {
    const c = text[i];
    if (c === "{") {
      const head = text.slice(Math.max(0, i - 200), i);
      stack.push(/\bnamespace\s+[\w.:]+\s*$/.test(head) ? "namespace" : "body");
    } else if (c === "}") {
      stack.pop();
    } else if (stack.includes("body") && c !== "\n") {
      out[i] = " ";
    }
  }
  return out.join("");
}

const lines = [
  "# 元のデータ: slang 2026.19 の source/slang/*.meta.slang と source/standard-modules (https://github.com/shader-slang/slang)。ライセンスは THIRD-PARTY-NOTICES.txt にある。",
  "# tools/slang/stdlib.mjs が作る。手で書き換えない。",
  ...[...types].sort().map(n => `T\t${n}`),
  ...[...functions].sort().map(n => `F\t${n}`),
  ...[...values].sort().map(n => `V\t${n}`),
  ...[...modules].sort().map(n => `M\t${n}`),
  ...[...prefixes].sort().map(n => `P	${n}`),
];
const out = join("src", "Shaderlyn.Semantics", "Symbols", "SlangStdlib.g.txt");
writeFileSync(out, lines.join("\n") + "\n");
console.log(`${out}: 型 ${types.size}、関数 ${functions.size}、値 ${values.size}、モジュール ${modules.size}`);
