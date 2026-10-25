// 外部のシェーダーのコーパスを取ってくる。
//
// 使い方 (リポジトリのルートで):
//   node tools/corpus/fetch.mjs                     # すべてを .work/corpus/ に取ってくる
//   node tools/corpus/fetch.mjs --only=glsl,godot   # 言語で絞る (hlsl / glsl / godot / slang / wgsl)
//   node tools/corpus/fetch.mjs <フォルダー>          # 置き場所を変える
//
// 取ってきたものはリポジトリに入れない (.work/ は .gitignore にある)。ライセンスはそれぞれのリポジトリに従う
// (下の license は取ってきた時点で確かめたもの)。
// HLSL のテストは環境変数 SHADERLYN_HLSL_CORPUS に置き場所を渡したときだけ動く (ExternalCorpusTests)。
//
//   SHADERLYN_HLSL_CORPUS=.work/corpus dotnet test --filter "FullyQualifiedName~ExternalCorpusTests"
//
// 再現できるように、取ってくる版 (コミット) を固定する。版を上げるときはここを書き換える。

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * 取ってくるコーパス。
 * languages はそのコーパスで確かめる言語、patterns はシェーダーのファイルだけを取るための sparse checkout の指定 (--no-cone)。
 */
const corpora = [
  {
    name: "DirectX-Graphics-Samples",
    url: "https://github.com/microsoft/DirectX-Graphics-Samples.git",
    commit: "e5975f9b0744fc5096dd593ed72bf8f5c004164f",
    license: "MIT",
    languages: ["hlsl"],
    patterns: ["*.hlsl", "*.hlsli", "*.h"],
  },
  {
    // GLSL と Slang の両方がある (同じサンプルを書き分けている)。
    name: "SaschaWillems-Vulkan",
    url: "https://github.com/SaschaWillems/Vulkan.git",
    commit: "4ee2501c8280cf71d1f9ce231cd8ebd47b58adde",
    license: "MIT",
    languages: ["glsl", "slang"],
    patterns: ["/shaders/glsl/**", "/shaders/slang/**", "!*.spv", "/LICENSE.md"],
  },
  {
    name: "Vulkan-Samples",
    url: "https://github.com/KhronosGroup/Vulkan-Samples.git",
    commit: "177edebf0cd7d4f669667e49f052cfb56b17e004",
    license: "Apache-2.0",
    languages: ["glsl"],
    patterns: ["/shaders/**/glsl/**", "!*.spv", "/LICENSE"],
  },
  {
    // res:// の根を決めるため、project.godot も取る。
    name: "godot-demo-projects",
    url: "https://github.com/godotengine/godot-demo-projects.git",
    commit: "b761b4cd5718a7118f2c95eb55f181d0a5f004f1",
    license: "MIT",
    languages: ["godot"],
    patterns: ["*.gdshader", "*.gdshaderinc", "project.godot", "/LICENSE.md"],
  },
  {
    // テスト (tests/) は誤りを含む入力が多いので、例 (examples/) だけを取る。
    name: "slang",
    url: "https://github.com/shader-slang/slang.git",
    commit: "3a93b38989da758cd2ff5dabb326fdbc3f2f26a1",
    license: "Apache-2.0 WITH LLVM-exception",
    languages: ["slang"],
    patterns: ["/examples/**/*.slang", "/LICENSE"],
  },
  {
    name: "webgpu-samples",
    url: "https://github.com/webgpu/webgpu-samples.git",
    commit: "e040ec1a20dbbe01afed30c812e825b2639cfe5b",
    license: "BSD-3-Clause",
    languages: ["wgsl"],
    patterns: ["*.wgsl", "/LICENSE.txt"],
  },
  {
    name: "wgpu",
    url: "https://github.com/gfx-rs/wgpu.git",
    commit: "a4b0f2257a4e1d9744dafeb1e6a0a6436f744914",
    license: "MIT OR Apache-2.0",
    languages: ["wgsl"],
    patterns: ["/examples/**/*.wgsl", "/LICENSE.MIT", "/LICENSE.APACHE"],
  },
];

const options = process.argv.slice(2);
const only = options.find(o => o.startsWith("--only="))?.slice("--only=".length).split(",").filter(Boolean);
const positional = options.filter(o => !o.startsWith("--"));

const root = resolve(positional[0] ?? join(".work", "corpus"));
mkdirSync(root, { recursive: true });

const selected = only ? corpora.filter(c => c.languages.some(l => only.includes(l))) : corpora;

if (selected.length === 0) {
  console.error(`--only に当てはまるコーパスがありません: ${only.join(",")}`);
  process.exit(1);
}

for (const corpus of selected) {
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

  console.log(`${corpus.name} (${corpus.languages.join(" / ")}, ${corpus.license})`);
}

console.log(`取ってきた場所: ${root}`);
