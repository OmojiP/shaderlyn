# 照合用の公式コンパイラ

各言語の公式コンパイラでシェーダーを検査し、その結果を Shaderlyn と照合します。
コンパイラとテスト用コーパスはリポジトリに含めません。各自で用意し、必要に応じてコンパイラの場所を環境変数に指定してください。

## 使い方

```sh
node tools/corpus/fetch.mjs --only=glsl,godot,slang,wgsl   # コーパスを .work/corpus/ に取る
SHADERLYN_GLSLANG=<glslang の場所> node tools/oracles/check.mjs glsl .work/corpus/SaschaWillems-Vulkan/shaders/glsl --failures
```

| 環境変数 | コンパイラ | 環境変数が未設定の場合に探す名前 |
| --- | --- | --- |
| `SHADERLYN_GLSLANG` | glslang | PATH から `glslang` を探す |
| `SHADERLYN_SLANGC` | slangc | PATH から `slangc` を探す |
| `SHADERLYN_NAGA` | naga | PATH から `naga` を探す |
| `SHADERLYN_GODOT` | Godot (コンソール版の実行ファイル) | PATH から `godot` を探す |

`check.mjs` はファイルごとに `ok` / `error` / `skip` のいずれかを1行で表示し、最後に状態ごとの件数を集計します。`--failures` を指定すると、`error` のファイルだけを表示します。

| 言語 | コンパイラ | 検査方法 |
| --- | --- | --- |
| `glsl` | glslang | Vulkan 1.3 向けに SPIR-V を生成します (`-V --target-env vulkan1.3`)。ステージは拡張子から判断します。ステージの分からない `.glsl` ファイルは検査しません |
| `slang` | slangc | モジュールとしてコンパイルするため、エントリポイントのないファイルも意味解析できます |
| `wgsl` | naga | WGSL を検証します |
| `godot` | Godot | 起動してバージョンを表示します。シェーダーの検査は GD4 で行います |

## GLSL を glslang と行ごとに比べる

`compare-glsl.mjs` は GLSL シェーダーを glslang と Shaderlyn で検査し、指摘の行を比べます。
Shaderlyn の CLI は Release 構成でビルドして使います。

```sh
dotnet build src/Shaderlyn.Cli.App -c Release
```

別の場所にある CLI を使う場合は、`SHADERLYN_CLI` にそのパスを指定します。

```sh
# glslang の Test を、付いている正解 (baseResults) と比べる (glslang は動かさない)
node tools/oracles/compare-glsl.mjs .work/corpus/glslang/Test --baseline=.work/corpus/glslang/Test/baseResults --gtests=.work/corpus/glslang/gtests
# glslang を動かして比べる (OpenGL の意味。Vulkan の意味なら --vulkan)
SHADERLYN_GLSLANG=<glslang の場所> node tools/oracles/compare-glsl.mjs .work/corpus/ogl-samples/data
# 拡張子がステージを表さないシェーダー集
SHADERLYN_GLSLANG=<glslang の場所> node tools/oracles/compare-glsl.mjs .work/corpus/LearnOpenGL/src --stage-map=vs:vert,fs:frag,gs:geom
```

- **誤報告:** glslang がコンパイルを通すファイルに、Shaderlyn がエラーを出すこと
- **見落とし:** glslang がエラーを出した行に、Shaderlyn のエラーがないこと。行番号のないエラーは、Shaderlyn がそのファイルにエラーを1つ以上出していれば一致とします

`--details` を指定すると、食い違いを1件ずつ表示します。`--json=<パス>` を指定すると、ファイルごとの結果を JSON で出力します。
glslang の Test のうち、単一ファイルのコンパイル結果ではないもの (`-E` の出力、複数ファイルをつないだ結果、HLSL として読ませた結果) は比較しません。

## 記録した道具 (2026-10-17)

結果を再現できるよう、使ったツールのバージョンを記録します。バージョンを上げたら、ここも更新してください。

| ツール | バージョン | 入手先 |
| --- | --- | --- |
| glslang | 16.6.0 | [KhronosGroup/glslang のリリース](https://github.com/KhronosGroup/glslang/releases/tag/16.6.0) |
| slangc | 2026.19 | [shader-slang/slang のリリース](https://github.com/shader-slang/slang/releases/tag/v2026.19) |
| naga | 30.0.1 | crates.io の naga-cli (`cargo install naga-cli --locked`) |
| Godot | 4.7.2-stable | [godotengine/godot のリリース](https://github.com/godotengine/godot/releases/tag/4.7.2-stable) |

## コーパスでの結果 (2026-10-17)

`tools/corpus/fetch.mjs` で取得したコーパスでの結果です。通らなかったファイルには、取り込み先やホスト側で用意する情報が必要なものが含まれます。

| 言語 | コーパス | 通った | 通らない | 通らない理由 |
| --- | --- | --- | --- | --- |
| GLSL | SaschaWillems/Vulkan、Vulkan-Samples | 523 | 0 | — (取り込まれる側の `.glsl` 6 件は検査しない) |
| Slang | SaschaWillems/Vulkan、slang の examples | 216 | 8 | 取り込みの場所 (`-I`) が要る、ホストが特殊化の引数や型の適合を与える前提 |
| WGSL | webgpu-samples、wgpu の examples | 115 | 6 | サンプルが TypeScript で共通のコードを連結してから渡している、まだ naga に無い拡張 (`buffer_view`) |
