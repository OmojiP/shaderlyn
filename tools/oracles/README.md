# 照合用の公式コンパイラ

各言語の公式のコンパイラでシェーダーを検査し、Shaderlyn の結果と照らすためのものです。
道具とコーパスはリポジトリに入れません。道具は各自で入れ、場所を環境変数で渡します。

## 使い方

```sh
node tools/corpus/fetch.mjs --only=glsl,godot,slang,wgsl   # コーパスを .work/corpus/ に取る
SHADERLYN_GLSLANG=<glslang の場所> node tools/oracles/check.mjs glsl .work/corpus/SaschaWillems-Vulkan/shaders/glsl --failures
```

| 環境変数 | 道具 | 渡さなければ |
| --- | --- | --- |
| `SHADERLYN_GLSLANG` | glslang | PATH の `glslang` |
| `SHADERLYN_SLANGC` | slangc | PATH の `slangc` |
| `SHADERLYN_NAGA` | naga | PATH の `naga` |
| `SHADERLYN_GODOT` | Godot (コンソール版の実行ファイル) | PATH の `godot` |

`check.mjs` は 1 ファイル 1 行で `ok` / `error` / `skip` を出し、最後に件数をまとめます。`--failures` を付けると通らないものだけを出します。

| 言語 | 道具 | 何をするか |
| --- | --- | --- |
| `glsl` | glslang | Vulkan の意味で SPIR-V を作る (`-V --target-env vulkan1.3`)。ステージは拡張子で決まる。`.glsl` (取り込まれる側) は検査しない |
| `slang` | slangc | モジュールとしてコンパイルする。エントリポイントを持たないファイルも意味まで検査できる |
| `wgsl` | naga | 検証する |
| `godot` | Godot | 起動して版を出すだけ。シェーダーの検査は GD4 で作る |

## 記録した道具 (2026-10-17)

結果を再現するため、使った版を記録します。版を上げたらここを書き換えます。

| 道具 | 版 | 入手先 |
| --- | --- | --- |
| glslang | 16.6.0 | [KhronosGroup/glslang のリリース](https://github.com/KhronosGroup/glslang/releases/tag/16.6.0) |
| slangc | 2026.19 | [shader-slang/slang のリリース](https://github.com/shader-slang/slang/releases/tag/v2026.19) |
| naga | 30.0.1 | crates.io の naga-cli (`cargo install naga-cli --locked`) |
| Godot | 4.7.2-stable | [godotengine/godot のリリース](https://github.com/godotengine/godot/releases/tag/4.7.2-stable) |

## コーパスでの結果 (2026-10-17)

`tools/corpus/fetch.mjs` で取った版での結果です。通らないものは、単体では読めない (文脈が要る) ものです。

| 言語 | コーパス | 通った | 通らない | 通らない理由 |
| --- | --- | --- | --- | --- |
| GLSL | SaschaWillems/Vulkan、Vulkan-Samples | 523 | 0 | — (取り込まれる側の `.glsl` 6 件は検査しない) |
| Slang | SaschaWillems/Vulkan、slang の examples | 216 | 8 | 取り込みの場所 (`-I`) が要る、ホストが特殊化の引数や型の適合を与える前提 |
| WGSL | webgpu-samples、wgpu の examples | 115 | 6 | サンプルが TypeScript で共通のコードを連結してから渡している、まだ naga に無い拡張 (`buffer_view`) |
