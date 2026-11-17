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
// 標準のモジュールにだけある名前は、3 つ目の列にそのモジュール (slang/neural など。, で区切る) を書く。import したときだけ見える。
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
// 名前ごとの、それを宣言した標準のモジュール。標準ライブラリ (*.meta.slang) にもある名前は、いつも見える (core)。
const origins = new Map();
let origin = "core";
const add = (set, name) => {
  if (name && /^[A-Za-z_]\w*$/.test(name) && !keywords.has(name)) {
    set.add(name);
    const key = `${set === types ? "T" : set === functions ? "F" : "V"}\t${name}`;
    if (!origins.has(key)) {
      origins.set(key, new Set());
    }
    origins.get(key).add(origin);
  }
};

/** 標準のモジュールのファイルの、モジュールの名前 (slang/neural、experimental/x)。 */
const moduleOf = path => {
  const parts = relative(modulesRoot, path).split(sep);
  return parts[0] === "experimental" ? `experimental/${parts.at(-1).replace(/\.slang$/, "")}` : `slang/${parts[0]}`;
};

for (const file of files) {
  let text = readFileSync(file, "utf8");
  origin = file.startsWith(modulesRoot) ? moduleOf(file) : "core";

  // C++ のコードで名前を組み立てる関数 (WaveMulti$(opName)) は、名前の前半 (5 文字以上) を前方一致として覚える。
  for (const m of text.matchAll(/\b([A-Za-z_]\w{4,})\$\(/g)) {
    prefixes.add(m[1]);
  }

  // C++ の部分の表に、文字列で書いた資源の型の名前 ({ kIROp_HLSLRWByteAddressBufferType, "RWByteAddressBuffer" }、"RasterizerOrderedStructuredBuffer<T, L>")。
  for (const m of text.matchAll(/"([A-Z][A-Za-z0-9]*(?:Buffer|State))(?:<[^"]*>)?"/g)) {
    add(types, m[1]);
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

  // インターフェイスの associatedtype (IDifferentiable の Differential)。適合する型の中では、修飾せずに型として書ける。
  for (const m of full.matchAll(/\bassociatedtype\s+([A-Za-z_]\w*)/g)) {
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

// C++ のコードで名前を組み立てる型 (hlsl.meta.slang と glsl.meta.slang の繰り返し)。組み合わせを多めに作る
// (作らない組み合わせ、たとえば RWTextureCube を足しても、宣言されていない型を見落とすだけで、誤報告にはならない)。
origin = "core";
{
  const shapes = ["1D", "2D", "3D", "Cube"];
  for (const shape of shapes) {
    for (const ms of ["", "MS"]) {
      for (const array of ["", "Array"]) {
        // typealias $(accessPrefix[access])$(textureTypeName)$(shapeTypeNames[shape])$(msPostFix[isMS])$(arrayPostFix[isArray])
        for (const access of ["", "RW", "W", "RasterizerOrdered", "Feedback"]) {
          for (const kind of ["Texture", "Sampler"]) {
            add(types, `${access}${kind}${shape}${ms}${array}`);
          }
        }
        // typealias DepthTexture… と Sampler…Shadow
        add(types, `DepthTexture${shape}${ms}${array}`);
        add(types, `Sampler${shape}${ms}${array}Shadow`);
      }
    }
  }
  // kBaseBufferAccessLevels[aa].name << "Buffer"
  for (const access of ["", "RW", "RasterizerOrdered"]) {
    add(types, `${access}Buffer`);
  }
  // GLSL の gimageDim (targetType.prefix << "image" << targetShape.suffix)
  for (const prefix of ["", "u", "i", "u64", "i64"]) {
    for (const suffix of ["1D", "2D", "3D", "Cube", "2DRect", "1DArray", "2DArray", "CubeArray", "Buffer", "2DMS", "2DMSArray"]) {
      add(types, `${prefix}image${suffix}`);
    }
  }
  // GLSL の subpassInput (kSubpassLoadEntries の prefix)
  for (const prefix of ["", "u", "i"]) {
    add(types, `${prefix}subpassInput`);
    add(types, `${prefix}subpassInputMS`);
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

/** 標準のモジュールにだけある名前には、モジュールの列を足す。 */
function withOrigin(line) {
  const from = origins.get(line);
  return !from || from.has("core") ? line : `${line}\t${[...from].sort().join(",")}`;
}

const lines = [
  "# 元のデータ: slang 2026.19 の source/slang/*.meta.slang と source/standard-modules (https://github.com/shader-slang/slang)。ライセンスは THIRD-PARTY-NOTICES.txt にある。",
  "# tools/slang/stdlib.mjs が作る。手で書き換えない。",
  ...[...types].sort().map(n => withOrigin(`T\t${n}`)),
  ...[...functions].sort().map(n => withOrigin(`F\t${n}`)),
  ...[...values].sort().map(n => withOrigin(`V\t${n}`)),
  ...[...modules].sort().map(n => `M\t${n}`),
  ...[...prefixes].sort().map(n => `P	${n}`),
];
const out = join("src", "Shaderlyn.Semantics", "Symbols", "SlangStdlib.g.txt");
writeFileSync(out, lines.join("\n") + "\n");
console.log(`${out}: 型 ${types.size}、関数 ${functions.size}、値 ${values.size}、モジュール ${modules.size}`);

// ---- 型の適合の表 (SlangConformance.g.txt) ----
// 1 行に 1 つ。
// 列の数は種類ごとに決まっていて、無い値は空にする。最後の列は、標準のモジュールにだけあるもののモジュール (標準ライブラリなら空)。
//   I  インターフェイス<TAB>継承したインターフェイス (, で区切る)<TAB>モジュール
//   C  型<TAB>インターフェイス<TAB>条件<TAB>モジュール  型がインターフェイスに適合する。条件は、型の最初の型引数が適合しなければならないインターフェイス (無ければ空)
//                                         (__generic<T:__BuiltinFloatingPointType, let N : int> extension vector<T,N> : IFloat なら、vector と IFloat と __BuiltinFloatingPointType)
//   X  制約<TAB>インターフェイス<TAB>モジュール  制約に適合する型は、どれもインターフェイスに適合する (extension<T : 制約> T : インターフェイス)
//   K  型<TAB>モジュール  適合を全部 C の行に書いた型 (struct・class で宣言し、C++ の部分で形を作る extension の対象でないものと、Buffer・RWBuffer・RasterizerOrderedBuffer)。
//                         C の行 (条件を問わない) とインターフェイスの継承・X の行で届かないインターフェイスには、適合しない
//   G  型<TAB>位置<TAB>型の仮引数<TAB>制約<TAB>モジュール  ジェネリックの型の、型の仮引数の制約 (struct DescriptorHandle<T:IOpaqueDescriptor> なら DescriptorHandle 0 T IOpaqueDescriptor)
//   U  インターフェイス<TAB>モジュール  コンパイラが自分で適合を足すインターフェイス (__magic_type・KnownBuiltin)。K の型でも、適合しないとは言えない
// 組み込みの数の型の、__Builtin… の継承の並びは、標準ライブラリの C++ の部分 (core.meta.slang の kBaseTypes の繰り返しの switch) が作るので、ここに書き写す。
const conformance = new Set();
// C の行は条件の列を必ず持つ (無ければ空)。どの行も、最後にモジュールの列を持つ (標準ライブラリなら空)。
const addConformance = line => {
  const padded = line.startsWith("C\t") && line.split("\t").length === 3 ? `${line}\t` : line;
  conformance.add(`${padded}\t${origin === "core" ? "" : origin}`);
};
const splitTopLevel = list => {
  const parts = [];
  let depth = 0;
  let current = "";
  for (const c of list) {
    if (c === "<") depth++;
    if (c === ">") depth--;
    if (c === "," && depth === 0) {
      parts.push(current);
      current = "";
    } else {
      current += c;
    }
  }
  parts.push(current);
  return parts.map(p => p.trim().replace(/<.*$/s, "").trim()).filter(p => /^[A-Za-z_]\w*$/.test(p));
};
// 型の仮引数の並び (T:I, let N : int) から、型の仮引数の名前と制約 (名前 1 つだけのもの)。
const readGeneric = list => (list ?? "").split(",").map(p => p.trim()).filter(p => p && !p.startsWith("let ")).map(p => {
  const [name, constraint] = p.split(":").map(s => s?.trim());
  return { name, constraint: constraint && /^[A-Za-z_]\w*$/.test(constraint) ? constraint : null };
});

// 全部の適合を表に書いた型 (K) と、コンパイラが自分で適合を足すインターフェイス (U) を決めるための名前。
const plainTypes = new Map();      // struct・class で宣言した型 → モジュール
const aliasNames = new Set();      // typealias・typedef の名前 (別名は元の型の適合を持つので、閉じた型にしない)
const templatedTargets = new Set(); // C++ の部分で形を作る extension の対象 (_Texture など)
const magicInterfaces = new Map(); // __magic_type・KnownBuiltin のインターフェイス → モジュール
const bufferAliases = [];          // Buffer・RWBuffer など (_Texture の別名)
const generics = new Set();        // G の行 (標準ライブラリのジェネリックの型の、型の仮引数の制約)
let bufferReadOnlyArray = false;   // 読むだけの Buffer が IArray に適合する ($(isReadOnly?": IArray<T>":""))

for (const file of files) {
  let text = readFileSync(file, "utf8");
  origin = file.startsWith(modulesRoot) ? moduleOf(file) : "core";
  const raw = text.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/\/\/[^\n]*/g, " ");

  // C++ の部分が作る適合。読めない形の適合があれば止める (表が黙って欠けないように)。
  for (const m of raw.matchAll(/\bextension\s*(?:<[^>{]*>)?\s*([^{;]*?)\s*\{/g)) {
    const header = m[1];
    if (!header.includes("$")) continue;
    const target = header.match(/^([A-Za-z_]\w*)/)?.[1];
    if (target) templatedTargets.add(target);
    if (header.includes(`$(isReadOnly?": IArray<T>":"")`) && target === "_Texture") {
      bufferReadOnlyArray = true;
      continue;
    }
    if (header.startsWith("$(typeName)")) continue; // kDynamicResourceCastableTypes (下で読む)
    // 対象の名前を C++ の部分で作るものは、どの型に当たるか分からない。_Texture の適合は Buffer の別名に写すので、読めない形があっては困る。
    if (/^\$/.test(header) || (target === "_Texture" && /:\s*[A-Za-z_$]/.test(header.replace(/\bwhere\b[^]*$/, "").replace(/^[^<]*<[^]*>/, "")))) {
      throw new Error(`C++ の部分で形を作る extension の適合を読めません: ${file}: extension ${header}`);
    }
  }
  for (const m of raw.matchAll(/kDynamicResourceCastableTypes\[\]\s*=\s*\{([\s\S]*?)\};/g)) {
    for (const e of m[1].matchAll(/\{\s*"([A-Za-z_]\w*)(?:<[^"]*>)?"/g)) {
      addConformance(`C\t${e[1]}\t__IDynamicResourceCastable`);
    }
  }
  for (const m of raw.matchAll(/kBaseBufferAccessLevels\[\]\s*=\s*\{([\s\S]*?)\};/g)) {
    for (const e of m[1].matchAll(/\{\s*"([A-Za-z]*)"/g)) bufferAliases.push(`${e[1]}Buffer`);
  }
  for (const m of raw.matchAll(/((?:\s*(?:__magic_type\([^)]*\)|\[[^\]]*\]))*)\s*\binterface\s+([A-Za-z_]\w*)/g)) {
    if (/__magic_type|KnownBuiltin/.test(m[1])) magicInterfaces.set(m[2], origin);
  }

  text = text.replace(/\$\{\{\{\{[\s\S]*?\}\}\}\}/g, " ").replace(/\$\{[\s\S]*?\$\}/g, " ").replace(/\$\([^)]*\)/g, " ").replace(/\$\w+/g, " ");
  text = text.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/\/\/[^\n]*/g, " ");
  for (const m of text.matchAll(/\b(?:struct|class)\s+([A-Za-z_]\w*)\s*[<:{]/g)) {
    if (!plainTypes.has(m[1])) plainTypes.set(m[1], origin);
  }
  // ジェネリックの型の、型の仮引数の制約 (struct DescriptorHandle<T:IOpaqueDescriptor>・__generic<T, L : IBufferDataLayout> struct StructuredBuffer)。
  for (const m of text.matchAll(/(?:__generic\s*<([^>{;]*(?:<[^>{;]*>[^>{;]*)*)>\s*(?:(?:__\w+\([^)]*\)|\[[^\]]*\])\s*)*)?\b(?:struct|class)\s+([A-Za-z_]\w*)\s*(?:<([^>{;]*(?:<[^>{;]*>[^>{;]*)*)>)?\s*[:{]/g)) {
    const list = m[3] ?? m[1];
    if (!list || (m[1] && m[3])) continue;
    const parts = list.split(/,(?![^<]*>)/).map(p => p.trim());
    if (parts.some(p => /^each\b|\beach\s/.test(p))) continue;
    parts.forEach((p, index) => {
      const c = p.match(/^(?:let\s+)?([A-Za-z_]\w*)\s*:\s*([A-Za-z_]\w*)\s*(?:=.*)?$/);
      if (c && !p.startsWith("let ")) generics.add(`G\t${m[2]}\t${index}\t${c[1]}\t${c[2]}\t${origin === "core" ? "" : origin}`);
    });
  }
  for (const m of text.matchAll(/\btypealias\s+([A-Za-z_]\w*)|\btypedef\s+[^;{}]*?\b([A-Za-z_]\w*)\s*;/g)) aliasNames.add(m[1] ?? m[2]);

  for (const m of text.matchAll(/\binterface\s+([A-Za-z_]\w*)\s*(?:<[^>{]*>)?\s*(?::\s*([^{;]+))?\{/g)) {
    const bases = m[2] ? splitTopLevel(m[2]) : [];
    if (bases.length > 0) addConformance(`I\t${m[1]}\t${bases.join(",")}`);
  }
  for (const m of text.matchAll(/\b(?:struct|class)\s+([A-Za-z_]\w*)\s*(?:<[^>{]*>)?\s*:\s*([^{;]+)\{/g)) {
    for (const base of splitTopLevel(m[2])) addConformance(`C\t${m[1]}\t${base}`);
  }
  for (const m of text.matchAll(/(?:__generic\s*<([^>]*)>\s*)?\bextension\s*(?:<([^>]*)>)?\s*([A-Za-z_]\w*)\s*(?:<[^>{]*>)?\s*:\s*([^{;]+)\{/g)) {
    const parameters = readGeneric(m[2] ?? m[1]);
    const target = parameters.find(p => p.name === m[3]);
    for (const iface of splitTopLevel(m[4])) {
      if (target) {
        if (target.constraint) addConformance(`X\t${target.constraint}\t${iface}`);
      } else {
        const condition = parameters[0]?.constraint;
        addConformance(condition ? `C\t${m[3]}\t${iface}\t${condition}` : `C\t${m[3]}\t${iface}`);
      }
    }
  }
}

// Buffer・RWBuffer・RasterizerOrderedBuffer は _Texture の別名 (C++ の部分が typealias を作る)。_Texture の適合を写し、読むだけの Buffer に IArray を足す。
// _Texture の extension の適合は、上で読めることを確かめたもの (読むだけの Buffer の IArray) だけなので、この 3 つは全部の適合を書いた型にできる。
origin = "core";
if (bufferAliases.length === 0 || !bufferReadOnlyArray) throw new Error("Buffer の型の作り方 (kBaseBufferAccessLevels・isReadOnly の IArray) を読めません");
for (const line of [...conformance].filter(l => l.startsWith("C\t_Texture\t"))) {
  const [, , iface, condition] = line.split("\t");
  for (const alias of bufferAliases) addConformance(`C\t${alias}\t${iface}\t${condition}`);
}
addConformance(`C\t${bufferAliases[0]}\tIArray`);
const closedTypes = new Map(bufferAliases.map(a => [a, "core"]));
for (const [name, module] of plainTypes) {
  if (!templatedTargets.has(name) && !aliasNames.has(name)) closedTypes.set(name, module);
}
for (const [name, module] of closedTypes) conformance.add(`K\t${name}\t${module === "core" ? "" : module}`);
for (const [name, module] of magicInterfaces) conformance.add(`U\t${name}\t${module === "core" ? "" : module}`);
for (const line of generics) conformance.add(line);

origin = "core";
{
  const floating = ["half", "float", "double"];
  const signed = ["int8_t", "int16_t", "int", "int64_t", "intptr_t"];
  const unsigned = ["uint8_t", "uint16_t", "uint", "uint64_t", "uintptr_t"];
  for (const t of [...floating, ...signed, ...unsigned, "bool", "void"]) addConformance(`C\t${t}\t__BuiltinType`);
  for (const t of floating) for (const i of ["__BuiltinFloatingPointType", "__BuiltinRealType", "__BuiltinSignedArithmeticType", "__BuiltinArithmeticType"]) addConformance(`C\t${t}\t${i}`);
  for (const t of signed) addConformance(`C\t${t}\t__BuiltinSignedArithmeticType`);
  for (const t of [...signed, ...unsigned]) for (const i of ["__BuiltinArithmeticType", "__BuiltinIntegerType", "__BuiltinLogicalType"]) addConformance(`C\t${t}\t${i}`);
  for (const t of ["int", "uint"]) addConformance(`C\t${t}\t__BuiltinInt32Type`);
  for (const t of ["int64_t", "uint64_t"]) addConformance(`C\t${t}\t__BuiltinInt64Type`);
  addConformance("C\tbool\t__BuiltinLogicalType");
}

const conformanceOut = join("src", "Shaderlyn.Semantics", "Symbols", "SlangConformance.g.txt");
writeFileSync(conformanceOut, [
  "# 元のデータ: slang 2026.19 の source/slang/*.meta.slang と source/standard-modules (https://github.com/shader-slang/slang)。ライセンスは THIRD-PARTY-NOTICES.txt にある。",
  "# tools/slang/stdlib.mjs が作る。手で書き換えない。",
  ...[...conformance].sort(),
].join("\n") + "\n");
console.log(`${conformanceOut}: ${conformance.size} 行`);
