// naga の誤りの試験 (naga/tests/naga/wgsl_errors.rs) から、WGSL の断片を取り出してファイルにする。
//
//   node tools/oracles/wgsl-errors/extract.mjs [<naga のコーパス>] [<出力のフォルダー>]
//   (既定は .work/corpus/naga と .work/v0.3.4/wgsl-errors/cases)
//
// 試験の関数 (check(…) / check_error_matches(…) / check_success(…) / check_validation! など) に渡す文字列のリテラルのうち、
// WGSL の断片 (期待する誤りの文でないもの) を取り出す。Rust の字句 (コメント、文字のリテラル、生の文字列 r#"…"#) を順に読む。
// ファイルの名前は wgsl_errors-<試験の関数の名前>-<番号>.wgsl。正解は naga を流して記録する (record.mjs wgsl)。
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const corpus = resolve(process.argv[2] ?? join(".work", "corpus", "naga"));
const out = resolve(process.argv[3] ?? join(".work", "v0.3.4", "wgsl-errors", "cases"));
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const text = readFileSync(join(corpus, "naga", "tests", "naga", "wgsl_errors.rs"), "utf8");
const counts = new Map();
let fn = "top";
let written = 0;
let i = 0;

while (i < text.length) {
  const c = text[i];

  // コメント
  if (c === "/" && text[i + 1] === "/") {
    i = text.indexOf("\n", i);
    i = i < 0 ? text.length : i;
    continue;
  }
  if (c === "/" && text[i + 1] === "*") {
    i = text.indexOf("*/", i + 2) + 2;
    continue;
  }

  // 生の文字列 r"…" / r#"…"#
  if (c === "r" && !isIdent(text[i - 1]) && /^r#*"/.test(text.slice(i, i + 12))) {
    const hashes = text.slice(i + 1).match(/^#*/)[0];
    const start = i + 2 + hashes.length;
    const end = text.indexOf(`"${hashes}`, start);
    emit(text.slice(start, end));
    i = end + 1 + hashes.length;
    continue;
  }

  // 文字列 "…"
  if (c === '"') {
    let j = i + 1;
    let value = "";
    while (j < text.length && text[j] !== '"') {
      if (text[j] === "\\") {
        const next = text[j + 1];
        if (next === "\n" || next === "\r") {
          j += 2;
          while (/\s/.test(text[j])) j++;
          continue;
        }
        value += { n: "\n", t: "\t", r: "\r", "0": "\0" }[next] ?? next;
        j += 2;
        continue;
      }
      value += text[j++];
    }
    emit(value);
    i = j + 1;
    continue;
  }

  // 文字のリテラル ('"'、'\'') とライフタイム ('a) は飛ばす
  if (c === "'") {
    const m = text.slice(i, i + 5).match(/^'(\\.|[^\\'])'/);
    i += m ? m[0].length : 1;
    continue;
  }

  // fn 名前
  if (c === "f" && !isIdent(text[i - 1]) && /^fn\s+[a-z_0-9]+/.test(text.slice(i, i + 80))) {
    const m = text.slice(i).match(/^fn\s+([a-z_0-9]+)/);
    fn = m[1];
    i += m[0].length;
    continue;
  }

  i++;
}

console.log(`${written} 個の断片を ${out} に書きました`);

function isIdent(ch) {
  return ch !== undefined && /[A-Za-z0-9_]/.test(ch);
}

function emit(value) {
  // 期待する誤りの文 (error: … や ┌─ を含むもの)、試験の道具の文字列、WGSL らしくない短いものは除く。
  if (/^\s*(error|warning)\b|┌─|\{\}|\{[a-z_]+\}/.test(value) || !/[;{}]/.test(value)) {
    return;
  }

  const index = (counts.get(fn) ?? 0) + 1;
  counts.set(fn, index);
  writeFileSync(join(out, `wgsl_errors-${fn}-${index}.wgsl`), value.replace(/^\r?\n/, ""));
  written++;
}
