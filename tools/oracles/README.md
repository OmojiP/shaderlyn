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
| `glsl` | glslang | Vulkan 1.3 向けに SPIR-V を生成します (`-V --target-env vulkan1.3`)。ステージは拡張子から判断します。ステージを特定できない `.glsl` ファイルは検査しません |
| `slang` | slangc | モジュールとしてコンパイルするため、エントリポイントが存在しないファイルも意味解析できます |
| `wgsl` | naga | WGSL を検証します |
| `godot` | Godot | 起動してバージョンを表示します。シェーダーの検査は `record.mjs` で行います (下記) |

## GLSL を glslang と行ごとに比較する

`compare-glsl.mjs` は GLSL シェーダーを glslang と Shaderlyn で検査し、指摘の行を比較します。
Shaderlyn の CLI は Release 構成でビルドして使用します。

```sh
dotnet build src/Shaderlyn.Cli.App -c Release
```

別の場所にある CLI を使用する場合は、`SHADERLYN_CLI` にそのパスを指定します。

```sh
# glslang の Test を、付属の正解 (baseResults) と比較する (glslang は動かさない)
node tools/oracles/compare-glsl.mjs .work/corpus/glslang/Test --baseline=.work/corpus/glslang/Test/baseResults --gtests=.work/corpus/glslang/gtests
# glslang を動かして比較する (OpenGL の意味。Vulkan の意味なら --vulkan)
SHADERLYN_GLSLANG=<glslang の場所> node tools/oracles/compare-glsl.mjs .work/corpus/ogl-samples/data
# 拡張子がステージを表さないシェーダー集
SHADERLYN_GLSLANG=<glslang の場所> node tools/oracles/compare-glsl.mjs .work/corpus/LearnOpenGL/src --stage-map=vs:vert,fs:frag,gs:geom
```

- **誤報告:** glslang がコンパイルを通すファイルに、Shaderlyn がエラーを報告すること
- **見落とし:** glslang がエラーを報告した行に、Shaderlyn のエラーが存在しないこと。行番号が存在しないエラーは、Shaderlyn がそのファイルにエラーを1つ以上報告していれば一致とします

`--details` を指定すると、一致しない箇所を 1 件ずつ表示します。`--json=<パス>` を指定すると、ファイルごとの結果を JSON で出力します。
glslang の Test のうち、単一ファイルのコンパイル結果ではないもの (`-E` の出力、複数ファイルをつないだ結果、HLSL として解析させた結果) は比較しません。

## Godot・Slang・WGSL をコンパイラと行ごとに比較する

`record.mjs` でコンパイラの誤りの行を JSON に記録し、`compare.mjs` で Shaderlyn の指摘と比較します。
記録した正解はコンパイラのバージョンに固定されるので、コンパイラを上げたら記録し直してください。

```sh
SHADERLYN_GODOT=<Godot の場所> node tools/oracles/record.mjs godot .work/corpus/godot-shaders --out=.work/v0.3.4/oracle/godot/godot-shaders.json
node tools/oracles/compare.mjs .work/v0.3.4/oracle/godot/*.json --details
```

| 言語 | 記録の仕方 |
| --- | --- |
| `godot` | Godot をヘッドレスで起動し、シェーダーのコードを `Shader` に渡して、コンパイラの誤り (`SHADER ERROR`) を集めます。Godot は最初の誤りで止まるので、誤りはファイルごとに 1 つです。元のプロジェクトは開かず、`project.godot` の下のシェーダーと取り込まれる断片だけを作業用のプロジェクトに写して解析します (`res://` の取り込みはそのまま解決できます) |

Godot には誤りの試験の集まりが存在しないので、誤りを報告する所ごとに最小の例を作って使用します (`godot-errors/make.mjs`)。

```sh
node tools/oracles/godot-errors/make.mjs
SHADERLYN_GODOT=<Godot の場所> node tools/oracles/record.mjs godot .work/v0.3.4/godot-errors/cases --out=.work/v0.3.4/oracle/godot/errors.json
```

この記録の仕方では、次のものはエディタと違う結果になります。

- `shader_type` が存在しないか正しくないファイルは、誤りになりません (Godot がコンパイラに掛けないため)
- プリプロセッサの誤りは、その文ではなく「`#` は知らない文字」という誤りになります (Godot がプリプロセッサの失敗を伝えず、元のコードのまま再解析するため)。行は指令の行なので、行ごとに比較することはできます

`compare.mjs` は、コンパイラが誤りにしないファイルに出たエラーと警告を「誤報告」、コンパイラの誤りの行に Shaderlyn のエラーが存在しないものを「見落とし」、
コンパイラの最初の誤りより前の行に出たエラーを「先走り」(誤報告の疑い) として数えます。

`--use-args` を付けると、正解に記録したコンパイラの引数のうち設定ファイルに当たるもの (Slang の `-target`・`-experimental-feature`・`-specialize` と `-entry f<T>`・`-unscoped-enum`・`-D`・`-I`) を、
ファイルごとの一時の設定ファイルにして Shaderlyn に渡します。コマンドラインの指定によって出る誤りも、同じ指定で比較できます。付けなければ、どのファイルも `--no-config` で解析します。

```sh
node tools/oracles/compare.mjs .work/v0.3.4/oracle/slang/tests.json --use-args
```

> 手元の .NET SDK が 10.0.401 のままだと、Native AOT の脆弱性の勧告 (NU1903) で Release のビルドが止まります。
> SDK を 10.0.403 以降に上げるか、`dotnet build src/Shaderlyn.Cli.App -c Release -p:PublishAot=false` でビルドしてください。

## 記録した道具 (2026-10-17)

結果を再現できるよう、使用したツールのバージョンを記録します。バージョンを上げたら、ここも更新してください。

| ツール | バージョン | 入手先 |
| --- | --- | --- |
| glslang | 16.6.0 | [KhronosGroup/glslang のリリース](https://github.com/KhronosGroup/glslang/releases/tag/16.6.0) |
| slangc | 2026.19 | [shader-slang/slang のリリース](https://github.com/shader-slang/slang/releases/tag/v2026.19) |
| naga | 30.0.1 | crates.io の naga-cli (`cargo install naga-cli --locked`) |
| Godot | 4.7.2-stable | [godotengine/godot のリリース](https://github.com/godotengine/godot/releases/tag/4.7.2-stable) |

## コーパスでの結果 (2026-11-25)

`tools/corpus/fetch.mjs` で取得したコーパスを、Release の CLI でコンパイラと比較した結果です。
誤報告・見落とし・先走りの数え方は上の各節のとおりです。GLSL の見落としは行の数、ほかはファイルの誤りの行の数です。

| 言語 | コンパイラ | コーパス | ファイル | コンパイラの誤り | 誤報告 | 見落とし | 先走り |
| --- | --- | --- | --- | --- | --- | --- | --- |
| GLSL | glslang 16.6.0 | glslang の Test (baseResults) | 1,056 | 301 ファイル | 0 | 25 行 (11 ファイル) | — |
| GLSL | glslang 16.6.0 | SaschaWillems/Vulkan・Vulkan-Samples (`--vulkan`) | 523 | 0 | 0 | 0 | — |
| GLSL | glslang 16.6.0 | ogl-samples | 749 | 101 ファイル | 0 | 42 行 (21 ファイル) | — |
| GLSL | glslang 16.6.0 | opengl-es-sdk-for-android・LearnOpenGL | 393 | 0 | 0 | 0 | — |
| WGSL | naga 30.0.1 | 誤りの試験・naga・wgpu・webgpu-samples・Bevy | 565 | 248 | 0 | 0 | 14 |
| Godot | Godot 4.7.2 | 誤りの例・godot-demo-projects・godot-shaders | 618 | 414 | 0 | 0 | 0 |
| Slang | slangc 2026.19 | slang の tests (`--use-args`) | 4,453 | 965 | 4 | 699 | 59 |
| Slang | slangc 2026.19 | slang の tests/diagnostics (`--use-args`) | 569 | 465 | 3 | 256 | 12 |
| Slang | slangc 2026.19 | slang の examples (`--use-args`) | 48 | 7 | 2 | 6 | 0 |
| Slang | slangc 2026.19 | Falcor (`--use-args`) | 446 | 192 | 623 | 551 | 289 |

残っているものの理由:

- GLSL の glslang の Test の見落としは、前の行の誤りの巻き添えと、前処理の誤りを報告する行の違いです (docs/guide/environments/glsl.md の「glslang と違う所」)。
  通るファイルの警告 2 件は、値を返さない経路 (CF0501) です
- GLSL の ogl-samples の見落としは、拡張 (`GL_GOOGLE_include_directive` / `GL_ARB_shading_language_include`) を有効にしていない `#include` です。
  ogl-samples はアプリケーションが取り込みを解決する前提で書かれています
- WGSL の先走りは、naga が最初の誤りで止まった後ろの、本当の誤りです
- Slang の Falcor は、Falcor のビルドと同じ取り込みの場所 (`-I`) と定義を与えていないため、取り込みが見つからない (CF0021) ことと、その巻き添えの構文の誤り (CF0001) が大半です
