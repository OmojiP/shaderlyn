# Unity 以外の環境で使う

Shaderlyn は Unity のほか、DXC で直接コンパイルする HLSL (DirectX 12 のエンジンなど)、Unreal Engine、独自エンジンのシェーダーも解析できます。
環境ごとの違いは設定ファイルに書き、ルールと出力 (CLI・VS Code 拡張・GitHub Actions) はどの環境でも同じです。

環境ごとの設定ファイルのテンプレートと、試すためのシェーダーが [samples/environments](../../samples/environments/) にあります。
写して書き換えると早く始められます。

## 目次

- [環境の選び方](#環境の選び方)
- [汎用の HLSL (DXC)](#汎用の-hlsl-dxc)
- [Unreal Engine](#unreal-engine)
- [独自エンジン](#独自エンジン)
- [CI で使う](#ci-で使う)
- [環境によって変わること](#環境によって変わること)

## 環境の選び方

設定ファイル `.shaderlyn.yaml` の `environment` で選びます。コマンドラインの `--environment` が勝ちます。

| `environment` | 対象 | フォルダーを指定したときに探すファイル |
| --- | --- | --- |
| `unity` (既定) | Unity の ShaderLab と HLSL | `.shader` / `.compute` |
| `hlsl` | DXC で直接コンパイルする HLSL | `.hlsl` |
| `unreal` | Unreal Engine のシェーダー | `.usf` |
| (自前のホストが足した名前) | 独自エンジン | ホストが決める |

取り込まれる断片 (`.hlsli` / `.ush` / `.cginc` など) は、名前を挙げたときだけ解析します。

```sh
shaderlyn Shaders --env      # どの環境で、何を解析するかを表示する (解析はしない)
```

## 汎用の HLSL (DXC)

コンパイル単位 (どのファイルを、どのエントリポイント・ターゲットでコンパイルするか) と、構成の軸 (`-D` の組み合わせ) を設定ファイルに書きます。

```yaml
version: 1
environment: hlsl

hlsl:
  version: 2021            # DXC の -HV。既定は 2021

symbols:
  - name: QUALITY          # -D QUALITY=0 / 1 / 2 でコンパイルし分ける
    values: [0, 1, 2]
  - name: USE_FOG          # 定義するか・しないか

units:
  - files: Shaders/**/*.hlsl
    entry: MainVS
    target: vs_6_6
  - files: Shaders/**/*.hlsl
    entry: MainPS
    target: ps_6_6
```

- 1 つのファイルに複数の単位を当てられます。上の例では、頂点シェーダーとピクセルシェーダーを 1 つのファイルに書き、2 回解析します
- `entry` の関数が無ければ HL0301 になります。報告はファイルの先頭に出ます
- `target` から、DXC が定義するマクロ (`__SHADER_TARGET_STAGE` / `__SHADER_TARGET_MAJOR` / `__SHADER_TARGET_MINOR`) を決めます。
  `__HLSL_VERSION` と `__SHADER_STAGE_*` はどの単位でも定義します
- 値を持つシンボルは `#if QUALITY == 2` / `#if QUALITY >= 1` / `#if QUALITY` を値ごとに読み分け、指摘に `QUALITY == 2 のとき` を添えます
- シェーダーモデル 6.x の組み込み関数 (メッシュシェーダー、wave、`Barrier` など) と、ディスクリプタヒープ (`ResourceDescriptorHeap`) を知っています。
  `select` / `and` / `or` は HLSL 2021 のときだけ組み込み関数です

キーの一覧は [設定](configuration.md#hlsl) にあります。

## Unreal Engine

`.usf` / `.ush` を解析します。エンジン本体が無くても動きます。

```yaml
version: 1
environment: unreal

unreal:
  engine: C:/Program Files/Epic Games/UE_5.4/Engine   # /Engine を Engine/Shaders に割り当てる
  project: .                                          # /Project を Shaders に割り当てる
  virtual-paths:
    /Plugin/MyPlugin: Plugins/MyPlugin/Shaders

symbols:
  - name: PERMUTATION_HIGH_QUALITY    # SHADER_PERMUTATION_BOOL は 0 か 1 で定義される
    values: [0, 1]

units:
  - files: Shaders/**/*.usf
    entry: MainPS
    target: ps_6_6
```

- **パーミュテーションは C++ から読みません。** `SHADER_PERMUTATION_BOOL` / `SHADER_PERMUTATION_INT` に合わせて `symbols:` に書いてください
- **ビルドのときにエンジンが生成するファイル (`/Engine/Generated/…`) は解決できません。** その `#include` は誤りとして報告しませんが、
  中身 (uniform buffer などの宣言) が分からないので、宣言が無いことを根拠にする検査 (HL0301 / HL0310 など) は行いません。
  その旨を SL0002 (情報) で伝えます。型の誤りなど、取り込んだ先を見なくてよい検査は行います
- シェーダーコンパイラのマクロは、PC の Direct3D 12 を想定して `COMPILER_HLSL=1` / `COMPILER_DXC=1` / `SM6_PROFILE=1` を定義します。
  ほかのプラットフォームに合わせるときは `defines` で上書きしてください
- 言語のバージョンの既定は 2018 です。HLSL 2021 で書いたシェーダーは `hlsl: version: 2021` にしてください

> 既定のマクロと仮想パスの割り当ては、エンジンのソースの公開情報に合わせたもので、エンジン本体では確かめていません。
> 合わないところがあれば `defines` と `virtual-paths` で直せます。

## 独自エンジン

独自の書き方 (エンジン独自の `#pragma` で構成を宣言する、など) を持つエンジンは、環境を C# で書き、自作ルールと同じく自前の CLI と言語サーバーに渡して足します。
作り方は [ルールの実例集「独自エンジンの環境」](../custom-rules/cookbook.md#独自エンジンの環境) にあります。

独自の書き方を持たないエンジン (`-D` を変えて DXC でコンパイルするだけ) は、[汎用の HLSL](#汎用の-hlsl-dxc) で足ります。

## CI で使う

GitHub Action は設定ファイルの `environment` に従います。設定ファイルを使わないなら、入力の `environment` で選びます。
Unity 以外の環境では Unity のプロジェクト (`Library/`) が要らないので、GitHub のホストランナーで動かせます。

```yaml
- uses: OmojiP/shaderlyn/action@v0.2.0
  with:
    paths: Shaders
    environment: hlsl      # 省略時は設定ファイルの environment
```

## 環境によって変わること

| | Unity | 汎用の HLSL / Unreal |
| --- | --- | --- |
| 構成の軸 | `#pragma multi_compile` / `shader_feature` / `dynamic_branch` | 設定ファイルの `symbols:` |
| エントリポイント | `#pragma vertex` / `fragment` / `kernel` など | 設定ファイルの `units:` の `entry` |
| HLSL の言語のバージョン | 2018 | `hlsl: version` (汎用の HLSL は 2021、Unreal は 2018 が既定) |
| 宣言し忘れたシンボル (HL0330) | 下線で始まる大文字の名前 (`_NORMALMAP`) を報告 | 報告しない (`_DEBUG` などが普通に条件に出るため) |
| Unity だけのルール | SL / URP / HL0302 | 出ない |
| Unity だけのオプション | `--profile` / `--unity-project` / `--unity-editor` | 指定するとエラー |
| VS Code の構成の選択 | `#pragma` で宣言したシンボル | `symbols:` のシンボル。値を持つシンボルは `QUALITY = 2` のように値を選ぶ |
