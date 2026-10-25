// GLSL の組み込みの関数と変数の表を、glslang が書き出す組み込みの表 (--dump-builtin-symbols) から作る。
//
// 使い方 (リポジトリのルートで):
//   GLSLANG=<glslang の実行ファイル> node tools/glsl/builtins.mjs
//
// GLSLANG を渡さなければ、PATH の glslang を使う。glslang はリポジトリに入れない
// (https://github.com/KhronosGroup/glslang/releases から取る)。
// 構成 (Vulkan か OpenGL、プロファイル、バージョン、ステージ) ごとに空の main を glslang に渡し、書き出した表を集めて
// src/Shaderlyn.Semantics/Symbols/GlslBuiltins.g.txt を書き出す。解析の実行時には glslang を使わない。
//
// 同じ glslang で作り直せば、表は 1 文字も変わらない (並びはすべて決めている)。
// 表は glslang の Initialize.cpp に書かれた組み込みの宣言を元にするので、生成したファイルの先頭に glslang の名前と版を入れる
// (ライセンスは THIRD-PARTY-NOTICES.txt)。

import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const output = join(root, 'src', 'Shaderlyn.Semantics', 'Symbols', 'GlslBuiltins.g.txt');
const glslang = process.env.GLSLANG || 'glslang';

// -dumpfullversion は「11:16.6.0」の形で出る。コロンの後ろがリリースのバージョンである。
const version = execFileSync(glslang, ['-dumpfullversion'], { encoding: 'utf8' }).trim().split(':').pop();

// ---------------------------------------------------------------------------
// 構成
// ---------------------------------------------------------------------------

const Stages = ['vert', 'tesc', 'tese', 'geom', 'frag', 'comp', 'task', 'mesh', 'rgen', 'rint', 'rahit', 'rchit', 'rmiss', 'rcall'];
const DesktopVersions = [110, 120, 130, 140, 150, 330, 400, 410, 420, 430, 440, 450, 460];
const EsVersions = [100, 300, 310, 320];

/** 構成の一覧。並びは、同じ組み込みを持つ構成が続くように、対象・プロファイル・ステージ・バージョンの順にする。 */
const configs = [];

for (const target of ['opengl', 'vulkan']) {
  for (const profile of ['core', 'compatibility', 'es']) {
    // Vulkan は互換プロファイルを持たない。
    if (target === 'vulkan' && profile === 'compatibility') {
      continue;
    }

    for (const stage of Stages) {
      const versions = profile === 'es' ? EsVersions : DesktopVersions;

      for (const v of versions) {
        // Vulkan の GLSL は、デスクトップの 1.40 以降と ES の 3.10 以降 (GL_KHR_vulkan_glsl)。
        if (target === 'vulkan' && (profile === 'es' ? v < 310 : v < 140)) {
          continue;
        }

        // 1.50 より前にはプロファイルが無い。互換プロファイルは 1.50 から書ける。
        if (profile === 'compatibility' && v < 150) {
          continue;
        }

        configs.push({ target, profile, version: v, stage });
      }
    }
  }
}

/** 構成を、#version の行に書く。 */
function versionLine({ profile, version: v }) {
  if (profile === 'es') {
    return v === 100 ? '#version 100' : `#version ${v} es`;
  }

  return profile === 'compatibility' ? `#version ${v} compatibility` : `#version ${v}`;
}

// ---------------------------------------------------------------------------
// glslang の型の書き方を、GLSL の型の名前にする
// ---------------------------------------------------------------------------

/** 型の前に付く修飾子。glslang の getCompleteString が書くもの。 */
const Qualifiers = new Set([
  'global', 'temp', 'const', 'in', 'out', 'inout', 'uniform', 'buffer', 'shared', 'flat', 'smooth', 'noperspective',
  'centroid', 'sample', 'patch', 'highp', 'mediump', 'lowp', 'invariant', 'precise', 'coherent', 'volatile', 'restrict',
  'readonly', 'writeonly', 'nonuniform', 'pervertexEXT', 'pervertexNV', 'perprimitiveEXT', 'perprimitiveNV', 'perviewNV', 'perTaskNV',
  'devicecoherent', 'queuefamilycoherent', 'workgroupcoherent', 'subgroupcoherent', 'nonprivate', 'specialization-constant',
  'hitAttributeNV', 'hitAttributeEXT', 'rayPayloadNV', 'rayPayloadEXT', 'rayPayloadInNV', 'rayPayloadInEXT', 'callableDataNV',
  'callableDataEXT', 'callableDataInNV', 'callableDataInEXT', 'taskPayloadSharedEXT', 'shaderRecordNV', 'shaderRecordEXT',
  'unsized', 'nontemporal', 'fragColor', 'fragDepth',
]);

/** 要素の型の名前 (glslang の書き方) から、ベクトルと行列の名前の接頭辞。 */
const VectorPrefix = {
  float: '', bool: 'b', int: 'i', uint: 'u', double: 'd',
  int8_t: 'i8', uint8_t: 'u8', int16_t: 'i16', uint16_t: 'u16', int64_t: 'i64', uint64_t: 'u64', float16_t: 'f16',
};
const MatrixPrefix = { float: '', double: 'd', float16_t: 'f16' };

/**
 * 修飾子を除いた型の書き方 (words の先頭から) を読む。
 * @returns {{ type: string, rest: string[] } | null}
 */
function readType(words) {
  let i = 0;
  const dims = [];

  for (;;) {
    const array = /^(\d+)-element$/.exec(words[i] ?? '');

    if (array && words[i + 1] === 'array' && words[i + 2] === 'of') {
      dims.push(array[1]);
      i += 3;
      continue;
    }

    break;
  }

  // 精度は配列の後に書かれる (2-element array of highp 4-component vector of float)。
  while (Qualifiers.has(words[i] ?? '')) {
    i += 1;
  }

  let type;
  const vector = /^(\d)-component$/.exec(words[i] ?? '');
  const matrix = /^(\d)X(\d)$/.exec(words[i] ?? '');

  if (vector && words[i + 1] === 'vector' && words[i + 2] === 'of') {
    const element = words[i + 3];
    type = element in VectorPrefix ? `${VectorPrefix[element]}vec${vector[1]}` : `${element}<${vector[1]}>`;
    i += 4;
  } else if (matrix && words[i + 1] === 'matrix' && words[i + 2] === 'of') {
    const element = words[i + 3];
    const size = matrix[1] === matrix[2] ? matrix[1] : `${matrix[1]}x${matrix[2]}`;
    type = element in MatrixPrefix ? `${MatrixPrefix[element]}mat${size}` : `${element}<${matrix[1]}x${matrix[2]}>`;
    i += 4;
  } else if (words[i] !== undefined) {
    type = words[i];
    i += 1;
  } else {
    return null;
  }

  // 大きさの無い配列 (unsized 1-element array) は [] にする。
  return { type: type + dims.map(d => (d === '1' && unsized ? '[]' : `[${d}]`)).join(''), rest: words.slice(i) };
}

let unsized = false;

/** 修飾子を落とし、書き戻す向き (out / inout) を返す。 */
function stripQualifiers(words) {
  let direction = '';
  let i = 0;
  unsized = false;

  while (i < words.length && (Qualifiers.has(words[i]) || words[i].startsWith('gl_'))) {
    if (words[i] === 'out' || words[i] === 'inout') {
      direction = words[i];
    }

    if (words[i] === 'unsized') {
      unsized = true;
    }

    i += 1;
  }

  return { direction, words: words.slice(i) };
}

/** 括弧の外のコンマで分ける。 */
function splitTopLevel(text) {
  const parts = [];
  let depth = 0;
  let start = 0;

  for (let i = 0; i < text.length; i++) {
    if ('{(<'.includes(text[i])) {
      depth++;
    } else if ('})>'.includes(text[i])) {
      depth--;
    } else if (text[i] === ',' && depth === 0) {
      parts.push(text.slice(start, i));
      start = i + 1;
    }
  }

  parts.push(text.slice(start));
  return parts.map(p => p.trim()).filter(p => p.length > 0);
}

/** 構造体とブロックの中身 ({...}) を、1 つの語にまとめる。 */
function words(text) {
  const result = [];
  let current = '';
  let depth = 0;

  for (const ch of text) {
    if (ch === '{') {
      depth++;
    } else if (ch === '}') {
      depth--;
    }

    if (ch === ' ' && depth === 0) {
      if (current.length > 0) {
        result.push(current);
      }

      current = '';
    } else {
      current += ch;
    }
  }

  if (current.length > 0) {
    result.push(current);
  }

  return result;
}

// ---------------------------------------------------------------------------
// glslang の表を読む
// ---------------------------------------------------------------------------

/** 関数の多重定義 (鍵は名前・戻り値・仮引数・拡張) から、使える構成の番号。 */
const functions = new Map();

/** 変数 (鍵は名前・型・向き・拡張) から、使える構成の番号。 */
const variables = new Map();

/** ブロックの変数 (gl_in など) のメンバー (鍵は変数・メンバー・型) から、使える構成の番号。 */
const members = new Map();

/** 読めなかった行 (表に入れなかったもの)。 */
const skipped = new Map();

function add(map, key, index) {
  if (!map.has(key)) {
    map.set(key, new Set());
  }

  map.get(key).add(index);
}

/** 拡張の印 (<GL_EXT_a,GL_EXT_b,>) を取り出す。 */
function splitExtensions(text) {
  const match = /\s*<([A-Za-z0-9_,]+)>\s*$/.exec(text);
  return match
    ? { text: text.slice(0, match.index), extensions: match[1].split(',').filter(e => e.length > 0).join(',') }
    : { text, extensions: '' };
}

function readBlockMembers(body) {
  // block{ in 4-component vector of float Position gl_Position,  in float PointSize gl_PointSize, ...}
  return splitTopLevel(body.slice(body.indexOf('{') + 1, body.lastIndexOf('}'))).map(member => {
    const all = words(member);
    const name = all[all.length - 1];
    const { words: rest } = stripQualifiers(all.slice(0, -1));
    const type = readType(rest);
    return { name, type: type?.type ?? '?' };
  });
}

/**
 * 名前の無いブロックのメンバーの名前の候補 (メンバーの順)。glslang の表はメンバーの名前を書き出さない。
 * gl_PerVertex は、同じ対象・プロファイル・バージョンの gl_in のメンバー (ジオメトリシェーダーなどの表に名前が出る) を先に試す。
 * 互換プロファイルのフラグメントシェーダーの gl_PerFragment は、どの表にも名前が出ないので、GLSL 4.60 の 7.1 節の順で書く。
 */
const FallbackBlocks = [
  [['gl_Position', 'vec4'], ['gl_PointSize', 'float'], ['gl_ClipDistance', 'float[]'], ['gl_CullDistance', 'float[]']],
  [['gl_FogFragCoord', 'float'], ['gl_TexCoord', 'vec4[]'], ['gl_Color', 'vec4'], ['gl_SecondaryColor', 'vec4']],
];

/** 対象・プロファイル・バージョンごとの gl_in のメンバー ([名前, 型] の並び)。 */
const inputBlocks = new Map();

/** 構成ごとの、名前の無いブロックのメンバー。すべての構成を読んでから名前を決める。 */
const pendingAnonymous = [];

const work = mkdtempSync(join(tmpdir(), 'shaderlyn-glsl-'));
const usable = [];

try {
  for (const [index, config] of configs.entries()) {
    const file = join(work, `main.${config.stage}`);
    writeFileSync(file, `${versionLine(config)}\nvoid main() { }\n`);

    // Vulkan (SPIR-V を作る指定) では、表は標準エラーに出る。SPIR-V は作業用のフォルダーに書かせる。
    // レイトレーシングは SPIR-V 1.4 から使えるので、Vulkan 1.3 を対象にする (-V は Vulkan 1.0)。
    const run = spawnSync(
      glslang,
      [...(config.target === 'vulkan' ? ['--target-env', 'vulkan1.3', '-o', join(work, 'main.spv')] : []), '--dump-builtin-symbols', file],
      { encoding: 'utf8', cwd: work, maxBuffer: 1 << 28 });
    const text = `${run.stdout ?? ''}${run.stderr ?? ''}`;

    // そのバージョンで使えないステージ (1.10 のコンピュートシェーダーなど) は、glslang が組み込みを読めずに止まる。
    if (text.includes('Unable to parse built-ins') || !text.includes('BuiltinSymbolTable {')) {
      usable.push(false);
      continue;
    }

    usable.push(true);
    const lines = text.slice(text.indexOf('BuiltinSymbolTable {')).split(/\r?\n/);
    const anonymous = [];
    let previous = '';

    for (const line of lines) {
      // 表の終わり。後ろはコンパイルの結果 (ERROR: Linking … など) である。
      if (line === '}') {
        break;
      }

      // 表は LEVEL ごとに名前の順に並ぶ。
      if (line.startsWith('LEVEL ')) {
        previous = '';
        continue;
      }

      const anon = /^anonymous member (\d+) of (anon@\d+)$/.exec(line);

      if (anon) {
        anonymous.push({ member: Number(anon[1]), block: anon[2], after: previous, before: undefined });
        continue;
      }

      const entry = /^([A-Za-z_][A-Za-z0-9_]*): +(.*)$/.exec(line);

      if (!entry) {
        continue;
      }

      const name = entry[1];
      previous = name;

      for (const waiting of anonymous.filter(a => a.before === undefined)) {
        waiting.before = name;
      }

      // ジオメトリシェーダーの出力は layout( stream=0) が前に付く。
      const { text: description, extensions } = splitExtensions(entry[2].replace(/^layout\([^)]*\)\s*/, ''));
      const open = description.indexOf(`${name}(`);

      if (open >= 0) {
        // global 4-component vector of float texture( in sampler2D , in 2-component vector of float )
        const { words: head } = stripQualifiers(words(description.slice(0, open).trim()));
        const result = readType(head);
        const params = splitTopLevel(description.slice(open + name.length + 1, description.lastIndexOf(')'))).map(p => {
          const { direction, words: rest } = stripQualifiers(words(p));
          const type = readType(rest);
          return (direction ? `${direction} ` : '') + (type?.type ?? '?');
        });

        if (!result || params.includes('?')) {
          add(skipped, `${name}: ${description}`, index);
          continue;
        }

        add(functions, [name, result.type, params.join(';'), extensions].join('\t'), index);
        continue;
      }

      // gl_PerVertex:  out block (ブロックの型。変数ではない)
      if (/ block$/.test(description)) {
        continue;
      }

      const { direction, words: rest } = stripQualifiers(words(description));
      const qualifier = /^(const|uniform|in|out|flat in|smooth in|smooth out|flat out)/.exec(description)?.[1] ?? '';
      const type = readType(rest);

      if (!type) {
        add(skipped, `${name}: ${description}`, index);
        continue;
      }

      const storage = qualifier.includes('const') ? 'const' : qualifier.includes('uniform') ? 'uniform'
        : direction === 'out' || qualifier.endsWith('out') ? 'out' : 'in';

      if (type.type.startsWith('block{')) {
        const dims = type.type.slice(type.type.lastIndexOf('}') + 1);

        const blockMembers = readBlockMembers(type.type);

        for (const member of blockMembers) {
          add(members, [name, member.name, member.type, extensions].join('\t'), index);
        }

        const key = `${config.target}/${config.profile}/${config.version}`;

        if (name === 'gl_in' && !inputBlocks.has(key)) {
          inputBlocks.set(key, blockMembers.map(m => [m.name, m.type]));
        }

        add(variables, [name, `block${dims}`, storage, extensions].join('\t'), index);
        continue;
      }

      add(variables, [name, type.type.startsWith('structure{') ? 'structure' : type.type, storage, extensions].join('\t'), index);
    }

    if (anonymous.length > 0) {
      pendingAnonymous.push({ index, config, anonymous });
    }
  }
} finally {
  rmSync(work, { recursive: true, force: true });
}

// 名前の無いブロック (頂点シェーダーの out gl_PerVertex など) のメンバーは、名前が書き出されない。
// 表は名前の順に並ぶので、候補のメンバーの並びのうち、どのメンバーも並びの位置 (前後の名前の間) に収まるものを選ぶ。
for (const { index, config, anonymous } of pendingAnonymous) {
  const candidates = [inputBlocks.get(`${config.target}/${config.profile}/${config.version}`), ...FallbackBlocks].filter(c => c);

  for (const block of new Set(anonymous.map(a => a.block))) {
    const entries = anonymous.filter(a => a.block === block);
    const fits = candidates.find(list => entries.every(({ member, after, before }) =>
      list[member] !== undefined && after < list[member][0] && (before === undefined || list[member][0] < before)));

    if (!fits) {
      throw new Error(`名前の無いブロック ${block} のメンバーの名前を決められません: ${JSON.stringify(config)}`);
    }

    const storage = config.stage === 'frag' ? 'in' : 'out';

    for (const { member } of entries) {
      add(variables, [fits[member][0], fits[member][1], storage, ''].join('\t'), index);
    }
  }
}

// ---------------------------------------------------------------------------
// 書き出す
// ---------------------------------------------------------------------------

/** 構成の番号の集まりを、範囲の並び (0-12,15) にする。同じ集まりは同じ番号の行を使う。 */
const sets = new Map();

function setOf(indices) {
  const sorted = [...indices].sort((a, b) => a - b);
  const ranges = [];

  for (let i = 0; i < sorted.length;) {
    let j = i;

    while (j + 1 < sorted.length && sorted[j + 1] === sorted[j] + 1) {
      j++;
    }

    ranges.push(i === j ? `${sorted[i]}` : `${sorted[i]}-${sorted[j]}`);
    i = j + 1;
  }

  const key = ranges.join(',');

  if (!sets.has(key)) {
    sets.set(key, sets.size);
  }

  return sets.get(key);
}

// glslang が受け付けるが、表に書き出さないもの。
// - gl_HitTEXT / gl_HitTNV は gl_RayTmaxEXT / gl_RayTmaxNV の別名 (GL_EXT_ray_tracing / GL_NV_ray_tracing)。使える構成も同じにする
// - nonuniformEXT (GL_EXT_nonuniform_qualifier) は、実引数と同じ型の値を返す。* はどの型にも合い、戻り値は実引数の型になる。
//   どの構成で使えるかは表から分からないので、すべての構成で使えるものとする
for (const [alias, original] of [['gl_HitTEXT', 'gl_RayTmaxEXT'], ['gl_HitTNV', 'gl_RayTmaxNV']]) {
  for (const [key, indices] of [...variables].filter(([k]) => k.startsWith(`${original}\t`))) {
    for (const index of indices) {
      add(variables, [alias, ...key.split('\t').slice(1)].join('\t'), index);
    }
  }
}

for (const [index, ok] of usable.entries()) {
  if (ok) {
    add(functions, ['nonuniformEXT', '*', '*', 'GL_EXT_nonuniform_qualifier'].join('\t'), index);
  }
}

const byKey = (a, b) =>(a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0);
const functionLines = [...functions].sort(byKey).map(([key, indices]) => `F\t${key}\t${setOf(indices)}`);
const variableLines = [...variables].sort(byKey).map(([key, indices]) => `V\t${key}\t${setOf(indices)}`);
const memberLines = [...members].sort(byKey).map(([key, indices]) => `M\t${key}\t${setOf(indices)}`);

const out = [
  '# <auto-generated>',
  '# tools/glsl/builtins.mjs が生成した。手で書き換えないこと。',
  `# 元のデータ: glslang ${version} の --dump-builtin-symbols (https://github.com/KhronosGroup/glslang)。ライセンスは THIRD-PARTY-NOTICES.txt にある。`,
  '#',
  '# C 構成の番号 対象 プロファイル バージョン ステージ (glslang が組み込みを読めなかった構成は入れない)',
  '# S 構成の集まりの番号 構成の番号の範囲',
  '# F 名前 戻り値の型 仮引数の型 (; で区切る。out / inout は書き戻す引数) 要る拡張 (, で区切る) 構成の集まり',
  '# V 名前 型 (block はブロックの配列、structure は構造体) 記憶域 (in / out / uniform / const) 要る拡張 構成の集まり',
  '# M ブロックの変数の名前 メンバーの名前 型 要る拡張 構成の集まり',
  '# </auto-generated>',
  ...configs.map((c, i) => (usable[i] ? `C\t${i}\t${c.target}\t${c.profile}\t${c.version}\t${c.stage}` : null)).filter(l => l !== null),
  ...[...sets].map(([ranges, id]) => `S\t${id}\t${ranges}`),
  ...functionLines,
  ...variableLines,
  ...memberLines,
  '',
].join('\n');

writeFileSync(output, out);

console.log(`構成 ${usable.filter(u => u).length} / ${configs.length}、関数 ${functions.size}、変数 ${variables.size}、メンバー ${members.size}、集まり ${sets.size}`);

if (skipped.size > 0) {
  console.log(`読めなかった行 ${skipped.size} (表に入れていない):`);

  for (const line of [...skipped.keys()].slice(0, 20)) {
    console.log(`  ${line}`);
  }
}
