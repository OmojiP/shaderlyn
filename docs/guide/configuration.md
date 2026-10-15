# 設定

`.shaderlyn.yaml` に、プロジェクト全体の解析の設定（重要度の変更、探索パス、定義済みマクロなど）を書くためのページです。

設定ファイル以外の設定も後ろにまとめています。

# 設定ファイル `.shaderlyn.yaml`

解析対象のパスから上のフォルダーへ辿って設定ファイルを探します。VS Code 拡張は、開いたファイルから上へ辿って探します。

`--config <パス>` で明示的に指定することも、`--no-config` で探索を止めることもできます。

## 例

Unity の例です。汎用の HLSL・Unreal の例は、環境ごとのテンプレート ([samples/environments](../../samples/environments/)) にあります。

```yaml
version: 1

unity:
  profile: urp
  project: .         # Unity プロジェクトのルート。--unity-project と同じ

include-paths:
  - Assets/ShaderLibrary

defines:
  - _NORMALMAP
  - SHADER_API_D3D11=1

rules:
  SL1001: error      # 対応する宣言が無いプロパティはエラー扱いにする
  SL1004: none       # 未使用プロパティの指摘は使わない
  COOK0030:
    severity: warning
    options: { maxParameters: 4 }   # options を読む自作ルールへの設定
```

## キーの一覧

| キー            | 内容                                                            |
| --------------- | --------------------------------------------------------------- |
| `version`       | 設定ファイルのバージョン。現在は `1`                                    |
| `environment`   | 解析する環境。`unity` (既定) / `hlsl` (DXC で直接コンパイルする汎用の HLSL) / `unreal` |
| `unity`         | Unity の環境の設定 ([下記](#unity))                             |
| `unreal`        | Unreal Engine の環境の設定 ([下記](#unreal))                    |
| `hlsl`          | 汎用の HLSL 環境の設定 ([下記](#hlsl))                          |
| `symbols`       | 構成の軸になるシンボル。汎用の HLSL 環境で使う ([下記](#symbols)) |
| `units`         | コンパイル単位。汎用の HLSL 環境で使う ([下記](#units))         |
| `include-paths` | `#include` の追加探索パス。設定ファイルからの相対で解決されます。ここに書いたパスと、設定ファイルを置いたフォルダーは、`#include` が読んでよい範囲に入ります ([`#include` が読む範囲](cli-usage.md#include-が読む範囲)) |
| `defines`       | 定義済みマクロ。`NAME` または `NAME=VALUE`                      |
| `rules`         | ルール ID ごとの重要度とオプション                              |
| `report`        | 出力に出す指摘の重要度。出力の形式ごとに変えられる ([下記](#report)) |

## unity

Unity の環境だけで使う設定です。

| キー      | 内容                                                                                          |
| --------- | --------------------------------------------------------------------------------------------- |
| `profile` | レンダーパイプライン: `urp` / `brp` / `hdrp`。`--profile` の指定が勝ちます                    |
| `project` | Unity プロジェクトのルート。設定ファイルからの相対で解決されます。`--unity-project` の指定が勝ちます |
| `editor`  | Unity Editor のデータフォルダー。`--unity-editor` の指定が勝ちます                            |
| `version` | Unity のバージョン (`6.3` / `6000.3.0f1` / `2022.3` など)。無ければプロジェクトの `ProjectSettings/ProjectVersion.txt` から読みます |
| `graphics-apis` | シェーダーをコンパイルするグラフィックス API の並び: `dx11` / `dx12` / `vulkan` / `metal` / `gles3` / `glcore`。無ければプロジェクトの Windows 向けの設定から読み、それも無ければ `[dx11, dx12]` |
| `shader-compiler-backend` | Shader Compiler Backend (Unity 6.6 から): `fxc` / `dxc`。`dxc` は DX11 以外の API を DXC にします。無ければプロジェクトのビルドプロファイルから読みます |

### シェーダーのコンパイラ

Unity は既定で FXC を使い、次の場合にパス (コンピュートシェーダーはカーネル) を DXC (HLSL 2018) でコンパイルします。
Shaderlyn はパスごと・API ごとにどちらでコンパイルされるかを決め、使える構文をそれで判断します。

- `#pragma use_dxc` (API を書けばその API だけ)
- DXC が要る `#pragma require` (`int64` / `native16bit` / ウェーブの命令など 12 個。`inlineraytracing` は 6.4 から)
- `multi_compile` の `UNITY_DEVICE_SUPPORTS_NATIVE_16BIT` / `UNITY_DEVICE_SUPPORTS_INLINE_RAY_TRACING` が有効な構成
  (コンピュートシェーダーでは `UNITY_DEVICE_SUPPORTS_WAVE_*` も)
- Unity 6.6 で Shader Compiler Backend を DXC にした API

判定は Unity のバージョンで変わります (`use_dxc dx12` は 6.6 から効く、6.5 以前の `use_dxc` は DX11 も DXC にするなど)。
バージョンが分からないときは、どのバージョンの結果も並べます。
読んだ設定は `--env` の「シェーダーのコンパイラ」に出ます。

`#pragma` はコードブロックと `HLSLINCLUDE` に直接書いたものを読みます。
include ファイルの中の `use_dxc` / `require` は Unity 2022.3 だけで効くので、2022.3 (またはバージョンが分からないとき) だけ読みます。
その場合 `#if` は評価せず、書かれた `#include` をすべてたどります。

ShaderProbePack で実測した 2022.3〜6.6 (10 プロジェクト、DX11 / DX12 / Vulkan) の 5980 の組み合わせと判定が一致することを確かめています。

節の外に書いた `profile` (v0.1 の書き方) も読みますが、`unity:` の下へ移すよう警告 (`TOOL0003`) が出ます。
既定の `--error-on warning` では、この警告でも終了コードが `1` になります（[0.1 から 0.2 へ移行する](migration-0.2.md#設定ファイル)）。
両方に書いた場合は `unity:` の下を使います。

ほかの環境では `unity:` の節を読みません。`--profile` / `--unity-project` / `--unity-editor` をほかの環境で指定するとエラーになります。

## hlsl

`environment: hlsl` で使う設定です。

| キー      | 内容                                                                 |
| --------- | -------------------------------------------------------------------- |
| `compiler` | コンパイラ: `dxc` (既定) / `fxc`。`fxc` のときは `version` を使わず、DXC のマクロも定義しません |
| `version` | HLSL の言語のバージョン (DXC の `-HV`): `2016` / `2017` / `2018` / `2021` (既定)。DXC 1.8 は `2015` をコンパイルできないので受け付けません |
| `enable-16bit-types` | 16 ビットの型を使う (DXC の `-enable-16bit-types`)。`true` で `__HLSL_ENABLE_16_BIT=1` を定義します。既定は `false`。`compiler: fxc`、`version` が `2018` より前、`units:` の `target` がシェーダーモデル 6.2 より前のときは使えず、`TOOL0003` を報告します (DXC が受け付けないため) |

使える構文は方言で決まります ([HL0003 の表](../rules/HL0003.md#環境による違い))。

汎用の HLSL 環境は、DXC が定義するマクロ (`__HLSL_VERSION`、`__SHADER_TARGET_STAGE`、`__SHADER_TARGET_MAJOR`、
`__SHADER_TARGET_MINOR`、`__SHADER_STAGE_PIXEL` などのステージの番号、`__hlsl_dx_compiler`) を定義します。
ターゲットで決まるものは `units:` の `target` から決めます。`defines` で同じ名前を定義すると、そちらが勝ちます。

フォルダーを指定したときは `.hlsl` を探します。`.hlsli` は取り込まれる断片なので、名前を挙げたときだけ解析します。

## unreal

`environment: unreal` で使う設定です。コンパイル単位と構成の軸 (パーミュテーション) は、汎用の HLSL と同じく `units:` と `symbols:` に書きます。
コンパイラと言語のバージョンは `hlsl: compiler` / `hlsl: version` で変えられます (Unreal の既定は DXC の `2018`。Unreal Engine の実際のコンパイラは確かめていません)。

| キー            | 内容                                                                                   |
| --------------- | -------------------------------------------------------------------------------------- |
| `engine`        | エンジンのフォルダー (`…/UE_5.4/Engine`)。`/Engine` を `Shaders` の下に割り当てます       |
| `project`       | プロジェクトのフォルダー。`/Project` を `Shaders` の下に割り当てます                     |
| `generated`     | `--unreal-generate` で C++ から作ったフォルダー。`/Engine/Generated` に割り当てます ([Unity 以外の環境で使う](environments.md#c-から生成されるファイルを作る)) |
| `virtual-paths` | そのほかの仮想パスの割り当て (`/Plugin/MyPlugin: Plugins/MyPlugin/Shaders`)            |

```yaml
environment: unreal
unreal:
  engine: C:/Program Files/Epic Games/UE_5.4/Engine
  project: .
  virtual-paths:
    /Plugin/MyPlugin: Plugins/MyPlugin/Shaders
symbols:
  - name: PERMUTATION_HIGH_QUALITY   # SHADER_PERMUTATION_BOOL は 0 か 1 で定義される
    values: [0, 1]
units:
  - files: Shaders/**/*.usf
    entry: MainPS
    target: ps_6_6
```

- フォルダーを指定したときは `.usf` を探します。`.ush` は名前を挙げたときだけ解析します
- ビルドのときにエンジンが生成するファイル (`/Engine/Generated/…`) は、`generated` が無ければ解決できません。その `#include` は誤りとして報告しませんが、
  中身 (uniform buffer などの宣言) が分からないので、宣言が無いことを根拠にする検査 (HL0301 / HL0310 など) は行いません (UE0001 / UE0002 で伝えます)。
  欠けているのが uniform buffer の宣言だけなら、呼び出しの引数の検査 (HL0340 / HL0341) は行います ([Unity 以外の環境で使う](environments.md))
- シェーダーコンパイラが定義するマクロは、PC の Direct3D 12 を想定して `COMPILER_HLSL=1` / `COMPILER_DXC=1` / `SM6_PROFILE=1` を定義します。
  プロジェクトのプラットフォームと合わないときは `defines` で上書きしてください
- パーミュテーションは C++ から読みません。`SHADER_PERMUTATION_BOOL` / `SHADER_PERMUTATION_INT` に合わせて `symbols:` に書いてください

## symbols

構成の軸になるシンボルを宣言します。Unity の `#pragma multi_compile` に当たるもので、宣言したシンボルの構成ごとにコードを解析します。
Unity の環境では使いません (`#pragma` から読みます)。

```yaml
symbols:
  - name: QUALITY
    values: [0, 1, 2]          # 値を持つシンボル。先頭が既定の値
  - name: USE_FOG              # values を省略すると「定義されているか・いないか」
  - one-of: [MODE_A, MODE_B]   # 同時には有効にならず、どれか 1 つは有効
  - at-most-one: [FX_A, FX_B]  # 同時には有効にならない
```

値を持つシンボルは、`#if QUALITY == 2` / `#if QUALITY >= 1` / `#if QUALITY` を値ごとに読み分けます。
指摘には `QUALITY == 2 のとき` のように、その値を添えます。

## units

コンパイル単位を書きます。1 つのファイルに複数の単位を当てられます (頂点シェーダーとピクセルシェーダーを 1 つのファイルに書く場合など)。
どの単位にも当てはまらないファイルは、ファイル全体を 1 つの単位にし、`symbols:` のすべてを構成の軸にして解析します。

```yaml
units:
  - files: Shaders/**/*.hlsl   # 設定ファイルからの相対の glob
    entry: MainPS              # この名前の関数が無ければ HL0301
    target: ps_6_6             # DXC の -T と同じ書き方
    defines: [USE_FOO=1]       # この単位でだけ定義するマクロ
    symbols: [QUALITY]         # この単位で使う構成の軸。省略するとすべて
```

## rules

2 通りの書き方があります。重要度は `none` / `info` / `warning` / `error` のいずれかで、
`none` はそのルールを無効にします。

```yaml
rules:
  SL1003: error

  COOK0030:
    severity: warning
    options: { maxParameters: 4 }
```

### options

`options` はルールごとの設定値で、ルールの閾値変更などに等に利用できます。

オプションを自作ルールで使用する方法：[ルールの実例集](../custom-rules/cookbook.md#設定ファイルの-options-から閾値を受け取る)

> 現在、組み込みルールにはオプションを使用するものはありません。

どのオプション名を読むかはルールの説明に書かれています。

## report

`min-severity` に書いた重要度以上の指摘だけを出力します。`info` / `warning` / `error` のいずれかです。
すべての形式に同じ値を書くことも、形式 (`text` / `json` / `sarif` / `github`) ごとに書くこともできます。
形式ごとに書いたとき、書かなかった形式はすべての指摘を出します。

```yaml
report:
  min-severity: warning      # すべての形式

report:
  min-severity:              # 形式ごと
    text: error              # 手元ではエラーだけ見る
    sarif: info              # Code Scanning にはすべて送る
```

- 絞るのは出力だけです。終了コードは、出力しなかった指摘も含めて `--error-on` で決めます。
  表示を絞ったことで CI の合否は変わりません
- テキストの最後の行に、出力しなかった件数を「(重要度で絞り、ほかに 3 件を出力していません)」のように添えます
- 設定ファイルの誤り (`TOOL0003` など) は絞りません
- `--annotate` で書く注釈には `github` の値を使います
- ベースラインの書き出し (`--write-baseline`) は、絞らずにすべての指摘を書きます
- コマンドラインの `--min-severity` は、どの形式でもこの設定より優先します

`include-trace` は、共通のヘッダの中の指摘に、どのシェーダーのどの Pass から取り込んだかの経路をどこまで添えるかです。
`first` (既定。最初の 1 本と、ほかの本数) / `all` / `none` のいずれかです
([ヘッダの指摘がどこから来たか](cli-usage.md#ヘッダの指摘がどこから来たか--include-trace))。
コマンドラインの `--include-trace` が優先します。

```yaml
report:
  include-trace: all
```

## 設定の誤りの報告

設定ファイルに書かれた認識できないキーは `TOOL0003` として報告されます。

```
.shaderlyn.yaml(8:1): warning TOOL0003: 設定に指定できないキー 'banned-symbol' があります。
```

次のものも報告されます。

- 字下げにタブを使っている
- 同じキーが 2 回書かれている
- 閉じられていない括弧や引用符
- 解釈できない重要度の綴り
- このツールより新しい `version`
- `symbols:` / `units:` の書き誤り (読めない `target`、宣言していないシンボルの名前など)
- Unity の環境で `symbols:` / `units:` を書いている

問題があっても読み込みは失敗せず、読めた範囲は適用されます。

## コマンドラインとの優先順位

コマンドラインの指定は設定ファイルより優先されます。

# 設定ファイル以外の設定

## 抑制コメント

設定ファイルではなくコードに書く抑制もあります。意図してそう書いている箇所だけを抑制するときに使います。

| 書き方                                                 | 効果                         |
| ------------------------------------------------------ | ---------------------------- |
| `// shaderlyn-disable-next-line ID`                    | 次の 1 行だけ                |
| `// shaderlyn-disable-line ID`                         | その行だけ（行末に書く）     |
| `// shaderlyn-disable ID` … `// shaderlyn-enable ID`   | 挟んだ範囲                   |
| `// shaderlyn-disable-file ID`                         | ファイル全体（どこでもよい） |

```hlsl
// shaderlyn-disable-next-line SL1003 移行中のため (#1234)
half _Color;

// shaderlyn-disable-line SL1021
Cull Sideways

// shaderlyn-disable SL1020
...
// shaderlyn-enable SL1020

// shaderlyn-disable-file HL0302
```

ルール ID を省略するとすべてのルールが対象になります。ID はカンマまたは空白で区切って複数書けます。
ID の後ろのコメントは解析されません。

抑制は ShaderLab、埋め込み HLSL、`#include` したヘッダのいずれでも同じように書けます。

## ベースライン

既存プロジェクトへ導入するときに、今ある指摘を凍結して新しく混入したものだけを報告させる仕組みです。
設定ファイルではなくコマンドラインで指定します。手順は [CLI の使い方 6. ベースライン](cli-usage.md#6-ベースライン) にあります。

---

## 関連

- [CLI の使い方](cli-usage.md) — コマンドラインでの指定
- [ルール](../rules/README.md) — 重要度を変えるルールの ID
- [トラブルシューティング「設定ファイル」](troubleshooting.md#設定ファイル)
