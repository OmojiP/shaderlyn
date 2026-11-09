// WGSL のファイルを naga に掛けて、誤りの行を集める (record.mjs から使う)。
//
// naga は SHADERLYN_NAGA (無ければ PATH の naga)。naga <ファイル> で読みと検証をする。誤りは最初の 1 つで止まり、
//   error: <文>
//     ┌─ <パス>:<行>:<桁>
// の形で標準エラーに出る (検証の誤りは色付きなので、色の符号を除いて読む)。
//
// Bevy のシェーダーの多くは naga_oil の #import / #define_import_path / #ifdef を使う。Shaderlyn は読まない (0.3.0 の決定) ので、
// # で始まる行のあるファイルは記録しない。

import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { cpus } from "node:os";

/** 1 つのファイルを naga に掛ける。 */
function run(naga, path) {
  return new Promise(done => {
    const child = spawn(naga, [path]);
    let stderr = "";
    child.stdout.resume();
    child.stderr.on("data", chunk => { stderr += chunk; });
    const timer = setTimeout(() => child.kill(), 60000);
    child.on("close", code => {
      clearTimeout(timer);
      done({ code, stderr });
    });
    child.on("error", error => {
      clearTimeout(timer);
      done({ code: -1, stderr: String(error) });
    });
  });
}

/** naga の出力から誤りを読む。 */
export function parseNaga(stderr) {
  const lines = stderr.replace(/\x1b\[[0-9;]*m/g, "").split(/\r?\n/);
  const errors = [];

  for (let i = 0; i < lines.length; i++) {
    const head = lines[i].match(/^error: (.*)$/);
    if (!head) {
      continue;
    }

    // ┌─ <パス>:<行>:<桁> (パスにドライブの : を含むことがあるので、末尾から読む)
    let line = 0;
    for (let j = i + 1; j < Math.min(lines.length, i + 4); j++) {
      const at = lines[j].match(/┌─ .*:(\d+):(\d+)\s*$/);
      if (at) {
        line = Number(at[1]);
        break;
      }
    }

    errors.push({ line, message: head[1] });
    break;
  }

  return errors;
}

/** WGSL のファイルを naga に掛け、誤りを集める。 */
export async function recordNaga(paths) {
  const naga = process.env.SHADERLYN_NAGA || "naga";
  const files = {};
  const queue = paths.filter(path => !/^\s*#/m.test(readFileSync(path, "utf8")));

  const version = await new Promise(done => {
    const child = spawn(naga, ["--version"]);
    let text = "";
    child.stdout.on("data", c => { text += c; });
    child.on("close", () => done(text.trim()));
    child.on("error", () => done(""));
  });

  let next = 0;
  const workers = Array.from({ length: Math.max(1, cpus().length - 2) }, async () => {
    while (next < queue.length) {
      const path = queue[next++];
      const { code, stderr } = await run(naga, path);
      const errors = parseNaga(stderr);
      if (errors.length === 0 && code !== 0) {
        errors.push({ line: 0, message: `naga が失敗しました (終了コード ${code}): ${stderr.trim().split(/\r?\n/)[0] ?? ""}` });
      }
      files[path] = { errors };
    }
  });
  await Promise.all(workers);

  return { tool: `naga ${version}`, files };
}
