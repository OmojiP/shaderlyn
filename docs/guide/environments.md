# 環境ごとの使い方

Shaderlyn は、Unity、DXC で直接コンパイルする HLSL (DirectX 12 のエンジンなど)、Unreal Engine、GLSL、独自エンジンのシェーダーを解析します。
どのエンジンと言語のシェーダーとして解析するかを「環境」と呼び、設定ファイルの `environment` で選びます。このページは、環境ごとの設定と違いをまとめたものです。解析ルールと出力方法 (CLI・VS Code 拡張・GitHub Actions) は、どの環境でも共通です。

環境別の設定ファイルのテンプレートと、試すためのサンプルシェーダーが [samples/environments](../../samples/environments/) にあります。
テンプレートをコピーし、環境に合わせて編集すると、すぐに試せます。

## 目次

- [環境の選び方](#環境の選び方)
- [環境ごとのページ](#環境ごとのページ)
- [環境によって変わること](#環境によって変わること)
- [独自エンジン](#独自エンジン)
- [CI で使う](#ci-で使う)

## 環境の選び方

設定ファイル `.shaderlyn.yaml` の `environment` で環境を選びます。コマンドラインで `--environment` を指定すると、その値を優先します。

| `environment` | 対象 | フォルダーを指定したときに探すファイル |
| --- | --- | --- |
| `unity` (既定) | Unity の ShaderLab と HLSL ([Unity](environments/unity.md)) | `.shader` / `.compute` |
| `hlsl` | DXC で直接コンパイルする HLSL ([汎用の HLSL](environments/hlsl.md)) | `.hlsl` |
| `unreal` | Unreal Engine のシェーダー ([Unreal Engine](environments/unreal.md)) | `.usf` |
| `glsl` | GLSL (OpenGL・Vulkan・GLSL ES。[GLSL](environments/glsl.md)) | `.vert` / `.frag` / `.comp` など (ステージの拡張子) と `.glsl` |
| `godot` / `slang` / `wgsl` | Godot / Slang / WGSL (開発中。現在は何も指摘しません) | `.gdshader` / `.slang` / `.wgsl` |
| (自前のホストが足した名前) | 独自エンジン ([下記](#独自エンジン)) | ホストが決める |

取り込まれる断片 (`.hlsli` / `.ush` / `.cginc` など) は、明示的に指定した場合に限り解析します。

HLSL 以外の言語 (GLSL・Godot・Slang・WGSL) は、`environment` に関係なく、拡張子に応じた環境で解析します。
たとえば Unity プロジェクト内の `.wgsl` も WGSL の解析対象です。ただし `.mesh` (GLSL のメッシュシェーダー) は Unity のメッシュアセットと拡張子が重なるため、`environment: glsl` を指定したときだけ読みます。

```sh
shaderlyn Shaders --env      # どの環境で、何を解析するかを表示する (解析はしない)
```

## 環境ごとのページ

| ページ | 内容 |
| --- | --- |
| [Unity](environments/unity.md) | プロジェクトと Unity Editor の指定、Library の無いマシンでの解析 (`--unity-generate`)、レンダーパイプライン、FXC と DXC の選ばれ方 |
| [汎用の HLSL (DXC)](environments/hlsl.md) | コンパイル単位 (`units:`) と構成の軸 (`symbols:`)、コンパイラと言語のバージョンで使える構文 |
| [Unreal Engine](environments/unreal.md) | エンジンとプロジェクトの仮想パス、C++ から生成されるファイル (`--unreal-generate`)、解析の前提 |
| [GLSL](environments/glsl.md) | glslang と同じ検査、`glsl: target` (Vulkan / OpenGL)、ステージと `#version` の決め方 |

## 環境によって変わること

| | Unity | 汎用の HLSL / Unreal |
| --- | --- | --- |
| 構成の軸 | `#pragma multi_compile` / `shader_feature` / `dynamic_branch` | 設定ファイルの `symbols:` |
| エントリポイント | `#pragma vertex` / `fragment` / `kernel` など | 設定ファイルの `units:` の `entry` |
| コンパイラと言語のバージョン | パスと API ごとに判定する。既定は FXC、`#pragma use_dxc` などで DXC の 2018 ([Unity](environments/unity.md#シェーダーのコンパイラ)) | `hlsl: compiler` と `hlsl: version` (汎用の HLSL は DXC の 2021。Unreal はエンジンの設定に合わせてシェーダーごとに決める) |
| 宣言されていないシンボル (CF0201) | 下線で始まる大文字の名前 (`_NORMALMAP`) を報告 | 報告しない (`_DEBUG` などが条件に使われるため) |
| Unity だけのルール | SL / UN / URP | 報告しない |
| Unity だけのオプション | `--profile` / `--unity-project` / `--unity-editor` | 指定するとエラー |
| VS Code の構成の選択 | `#pragma` で宣言したシンボル | `symbols:` のシンボル。値を持つシンボルは `QUALITY = 2` のように値を選ぶ |

GLSL は `#version` と拡張で使える書き方が決まり、構成の軸は `symbols:`、エントリポイントは `main` です ([GLSL](environments/glsl.md))。

## 独自エンジン

独自の書き方 (エンジン独自の `#pragma` で構成を宣言する、など) を持つエンジンでは、環境を C# で実装し、自作ルールと同様に専用の CLI と言語サーバーへ追加します。
作り方は [ルールの実例集「独自エンジンの環境」](../custom-rules/cookbook.md#独自エンジンの環境) にあります。

独自の書き方を持たないエンジン (`-D` を変えて DXC でコンパイルするだけ) は、[汎用の HLSL](environments/hlsl.md) で対応できます。

## CI で使う

GitHub Action は設定ファイルの `environment` に従います。設定ファイルを使わないなら、入力の `environment` で選びます。
Unity 以外の環境では Unity プロジェクトの `Library/` は不要で、GitHub のホストランナーでも実行できます。
Unity の環境でも、`--unity-generate` で解析に必要なヘッダを生成してリポジトリに含めれば、ホストランナーで実行できます
([CI に組み込む「GitHub-hosted runner で実行する」](ci-setup.md#1-github-hosted-runner-で実行する))。

```yaml
- uses: OmojiP/shaderlyn/action@v0.3.3
  with:
    paths: Shaders
    environment: hlsl      # 省略時は設定ファイルの environment
```
