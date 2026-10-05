// 外部のシェーダーのコーパスを取ってくる。
//
// 使い方 (リポジトリのルートで):
//   node tools/corpus/fetch.mjs            # .work/corpus/ に取ってくる
//   node tools/corpus/fetch.mjs <フォルダー> # 置き場所を変える
//
// 取ってきたものはリポジトリに入れない (.work/ は .gitignore にある)。ライセンスはそれぞれのリポジトリに従う。
// テストは環境変数 SHADERLYN_HLSL_CORPUS に置き場所を渡したときだけ動く (ExternalCorpusTests)。
//
//   SHADERLYN_HLSL_CORPUS=.work/corpus dotnet test --filter "FullyQualifiedName~ExternalCorpusTests"
//
// 再現できるように、取ってくる版 (コミット) を固定する。版を上げるときはここを書き換える。

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync } from "node:fs";
import { join, resolve } from "node:path";

/** 取ってくるコーパス。patterns はシェーダーのファイルだけを取るための sparse checkout の指定。 */
const corpora = [
  {
    name: "DirectX-Graphics-Samples",
    url: "https://github.com/microsoft/DirectX-Graphics-Samples.git",
    commit: "e5975f9b0744fc5096dd593ed72bf8f5c004164f",
    patterns: ["*.hlsl", "*.hlsli", "*.h"],
  },
];

const root = resolve(process.argv[2] ?? join(".work", "corpus"));
mkdirSync(root, { recursive: true });

for (const corpus of corpora) {
  const directory = join(root, corpus.name);
  const git = (...args) => execFileSync("git", ["-C", directory, ...args], { stdio: "inherit" });

  if (!existsSync(directory)) {
    // 中身 (blob) は要るものだけを取る。履歴も取らない。
    execFileSync("git", ["clone", "--filter=blob:none", "--no-checkout", "--depth", "1", corpus.url, directory], { stdio: "inherit" });
  }

  git("sparse-checkout", "set", "--no-cone", ...corpus.patterns);

  if (corpus.commit.startsWith("COMMIT_")) {
    // 版がまだ固定されていない。既定のブランチの先頭を使い、その版を表示する。
    git("checkout");
    console.log(`${corpus.name}: 版を固定するには、次のコミットを fetch.mjs に書いてください`);
    git("rev-parse", "HEAD");
  } else {
    git("fetch", "--depth", "1", "origin", corpus.commit);
    git("checkout", corpus.commit);
  }
}

console.log(`取ってきた場所: ${root}`);
