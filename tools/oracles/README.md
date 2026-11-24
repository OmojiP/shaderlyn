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

## コーパスでの結果 (2026-10-17)

`tools/corpus/fetch.mjs` で取得したコーパスでの結果です。通らなかったファイルには、取り込み先やホスト側で用意する情報が必要なものが含まれます。

| 言語 | コーパス | 通った | 通らない | 通らない理由 |
| --- | --- | --- | --- | --- |
| GLSL | SaschaWillems/Vulkan、Vulkan-Samples | 523 | 0 | — (取り込まれる側の `.glsl` 6 件は検査しない) |
| Slang | SaschaWillems/Vulkan、slang の examples | 216 | 8 | 取り込みの場所 (`-I`) が必要、ホストが特殊化の引数や型の適合を与える前提 |
| WGSL | webgpu-samples、wgpu の examples | 115 | 6 | サンプルが TypeScript で共通のコードを連結してから渡している、まだ naga に存在しない拡張 (`buffer_view`) |
