# Unity

既定の環境です。設定ファイルが無いか、`environment` を書いていなければ、Unity の環境で解析します。
ShaderLab (`.shader`) とコンピュートシェーダー (`.compute`) を、プロジェクトのパッケージと Unity Editor のヘッダを読んで解析します。

ほかの環境と共通の使い方 (出力・終了コード・ベースラインなど) は [CLI の使い方](../cli-usage.md)、設定ファイルのキーは [設定](../configuration.md#unity) にあります。

## 目次

- [解析するファイル](#解析するファイル)
- [プロジェクトを指定する](#プロジェクトを指定する)
- [Unity Editor の場所](#unity-editor-の場所)
- [Library と Unity Editor の無いマシンで解析する](#library-と-unity-editor-の無いマシンで解析する)
- [`#include` の解決](#include-の解決)
- [レンダーパイプライン](#レンダーパイプライン)
- [シェーダーのコンパイラ](#シェーダーのコンパイラ)
- [Unity だけのルール](#unity-だけのルール)
- [設定ファイルの例](#設定ファイルの例)

## 解析するファイル

フォルダーを指定すると、`.shader` と `.compute` を探します。

```bash
shaderlyn Assets/Shaders --unity-project .
```

`.hlsl` や `.cginc` は単体では成立しない断片であることがあるため、フォルダー指定では解析しません。
それらのファイルは、取り込んでいるシェーダーの解析の中で、取り込む側の文脈で検査されます。指摘はヘッダの位置に報告されます。
同じヘッダを取り込むシェーダーが複数あっても 1 件にまとまります。
Unity と外部パッケージのヘッダ (`Library/PackageCache` と Unity Editor に同梱のもの) は、直せないので報告しません。

### 解析対象は自分のシェーダーに絞る

Unityプロジェクト全体を解析する場合、Assets 以下を指定してください。

プロジェクトルートを指定すると Unity 同梱のシェーダーやパッケージを解析してしまいます。

```bash
# Good
shaderlyn Assets/Shaders --unity-project .

# Bad（Unity のパッケージまで解析してしまう）
shaderlyn . --unity-project .
```

## プロジェクトを指定する

Unity が用意する関数などのヘッダーを探すときに、Unity プロジェクトのルートを `--unity-project` で指定します (設定ファイルなら `unity: project`)。

```bash
shaderlyn Assets --unity-project .
```

指定しない場合は、そのマシンにインストール済みの Unity Editor からヘッダを探します。プロジェクトは推定しません。
そのため `Assets/...` と `Packages/...` の `#include`、解析対象のフォルダーより上を指す `../...` の `#include` は解決できません。
これらは [CF0021](../../rules/CF0021.md) のメッセージで、解決できない理由と、解析対象の上位で見つけた Unity プロジェクトを添えて報告します。
`--env` には、見つけたプロジェクトと解決できない項目も表示されます。

見つからない場合、次のエラーが発生します。

```
Assets/Shaders/Lit.shader(9:9): error TOOL0004: 取り込むヘッダを 1 つも解決できません ('Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl')。
    Unity プロジェクトとして解析する場合は --unity-project "C:\Work\MyGame" を指定してください。
```

## Unity Editor の場所

通常は指定不要です。プロジェクトの `ProjectSettings/ProjectVersion.txt` のバージョンを、Unity Hub の既定のインストール先
(Windows は `C:\Program Files\Unity\Hub\Editor`) から探します。

Unity Hub でインストール先を変えた場合 (別のドライブなど) は見つからないので、`--unity-editor` で Editor のデータフォルダーを指定してください
(設定ファイルなら `unity: editor`)。見つからないと、`UnityCG.cginc` などの同梱ヘッダを解決できず、
[CF0021](../../rules/CF0021.md) か [TOOL0004](../../rules/TOOL0004.md) に `--unity-editor` を指定するよう添えます。
Unity 6.3.26f1 以降の Editor は、同梱のヘッダを `Editor/Data/Resources/CGIncludes` に置いています。指定するのはどちらでも `Editor/Data` です。

```bash
# Windows
shaderlyn Assets --unity-project . --unity-editor "C:\Program Files\Unity\Hub\Editor\6000.0.30f1\Editor\Data"

# macOS
shaderlyn Assets --unity-project . --unity-editor "/Applications/Unity/Hub/Editor/6000.0.30f1/Unity.app/Contents"

# Linux
shaderlyn Assets --unity-project . --unity-editor "$HOME/Unity/Hub/Editor/6000.0.30f1/Editor/Data"
```

## Library と Unity Editor の無いマシンで解析する

GitHub のホストランナーのように、プロジェクトの `Library` も Unity Editor も無いマシンでは、パッケージのヘッダ
(`Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl` など) と `UnityCG.cginc` を読めません。
`--unity-generate` で、解析に必要なヘッダだけをあらかじめ生成してリポジトリに含めると、そうしたマシンでも解析できます。

```bash
shaderlyn --unity-project . --unity-generate .shaderlyn/unity
```

```text
次のパッケージのシェーダーのファイルを書き出します。
  Unity Companion License (5 個):
    com.unity.render-pipelines.core
    com.unity.render-pipelines.universal
    …
  Unity Editor のヘッダ (CGIncludes。C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Data)
合わせてファイル 475 個 (3.9 MB)。
書き出したものをリポジトリに入れるときは、各パッケージのライセンスを確かめてください。公開のリポジトリには入れないでください。
書き出しますか? [y/n]
```

- 生成するのは、パッケージの取り込まれうるファイル (`.hlsl` / `.cginc` / `.hlsli` など)・`package.json`・ライセンスの表示 (`LICENSE.md` など) と、Unity Editor のヘッダだけです。
  URP のプロジェクトで 4 MB ほどです (`Library/PackageCache` そのものは 1 GB を超えます)
- 生成前にパッケージとライセンスの一覧を表示して確認します。`n` を入力すると何も書き込みません。入力を受け付けられない環境 (CI など) では `--yes` を指定してください。
- プロジェクトと同じバージョンの Unity Editor から写します。見つからなければ止まるので、そのバージョンを入れるか `--unity-editor` で指定してください
- 生成先には、空のフォルダーか、以前 `--unity-generate` で生成したフォルダーを指定します。既存のファイルは削除してから再生成します。

生成したフォルダーを、設定ファイルの `unity: generated` に指定します。

```yaml
version: 1
unity:
  generated: .shaderlyn/unity
```

- `Library/PackageCache` と、プロジェクトと同じバージョンの Unity Editor があれば、これまでどおりそちらを読みます。無いときに、書き出したものを読みます
- 生成したファイル (Unity のファイルの写し) については指摘を報告しません
- どちらを参照しているかは `--env` の「Unity Editor」に表示されます
- **Unity のバージョンかパッケージを上げたら、書き出し直してください。** 書き出した後に `ProjectVersion.txt`・`packages-lock.json`・`Library/PackageCache` のパッケージが変わると、
  解析を始めるときに標準エラーに伝えます (VS Code 拡張では通知)。解析は止めません。
  この確かめには、比べる相手のプロジェクトの指定 (`--unity-project` か `unity: project`) が要ります。指定が無くても書き出したものは読みますが、合っているかは確かめません
  ```text
  --unity-generate で書き出したもの (…/.shaderlyn/unity) が、プロジェクトと合っていません。Unity のバージョンが変わりました (書き出したとき 6000.3.26f1、今 6000.3.30f1)。--unity-generate で書き出し直してください。
  ```

> **ライセンスについて:** 書き出したものは Unity のパッケージと Unity Editor のファイルの写しです。
> Unity のパッケージの多くは Unity Companion License で、Unity のコンテンツを作るための利用に限って写しと配布を認め、写しにライセンスの表示を添えることを求めます
> (`--unity-generate` は `LICENSE.md` などを一緒に写します)。Asset Store などから入れたパッケージは、それぞれのライセンスに従います。
> 書き出したものを入れるのは private のリポジトリにし、各パッケージのライセンスを確かめてください。

## `#include` の解決

`#include` は、Unity と同じ規則で解決します。`#include` を書いたファイルからの相対パス、ヘッダの中ではシェーダーからの相対パス ([UN0102](../../rules/UN0102.md) の警告付き)、`Assets/...` と `Packages/...` の形式、Editor の同梱ヘッダです。

この規則で解決できない場所にヘッダを置いている場合は、`--include-path` を指定します ([CLI の使い方「`#include` の追加探索パス」](../cli-usage.md#include-の追加探索パス))。
多くの場合、`--unity-project` だけで解決できるため、指定は不要です。

```bash
shaderlyn Assets --unity-project . --include-path ../SharedShaderLibrary
```

Unity は `#pragma once` を効かせません (2022.3 / 6.3 / 6.6 で確かめた)。Shaderlyn も Unity の環境では効かせず、書いてあれば [UN0101](../../rules/UN0101.md) で伝えます。

## レンダーパイプライン

既定は `urp` で、`urp` / `brp` / `hdrp` から選べます。

対応の度合いはパイプラインで違います。

| プロファイル | 対応 |
| --- | --- |
| `urp` | すべてのルール (URP0001〜URP0003 を含む) |
| `brp` | 共通のルールだけ。BRP だけのルールは足しません (BRP は Unity 6.5 で非推奨になり、6.7 まで使えます) |
| `hdrp` | 共通のルールと SRP Batcher の検査 (URP0001 / URP0002) だけ |

どのプロファイルでも、誤検出の報告は直します。

```bash
shaderlyn Assets --unity-project . --profile brp
```

## シェーダーのコンパイラ

Unity は既定で FXC を使い、パス (コンピュートシェーダーはカーネル) ごと・API ごとに DXC (HLSL 2018) へ切り替えます。
コンパイラと言語のバージョンで使える構文は [汎用の HLSL「コンパイラと言語のバージョン」](hlsl.md#コンパイラと言語のバージョン) の表のとおりです。
Unity の DXC は `sampler2D` / `tex2D` を受け付けます。Shaderlyn の判定条件は Unity のバージョンごとに異なります。

| | 2022.3 | 2023.2〜6.3 | 6.4〜6.5 | 6.6〜 |
| --- | --- | --- | --- | --- |
| include ファイルの中の `use_dxc` / `require` | 有効 | 無視 | 無視 | 無視 |
| `use_dxc` (API なし) | DX11 も DXC (DX11 で使えない) | 同左 | 同左 | DX12 / Vulkan / Metal が DXC、DX11 は FXC |
| `use_dxc d3d11` | DX12 と DX11 が DXC | 同左 | 同左 | DX12 が DXC (非推奨) |
| `use_dxc dx12` | 無視 | 無視 | 無視 | DX12 が DXC |
| `use_dxc d3d12` | 無視 | 無視 | 無視 | 無視 |
| DXC が必要な `require` (12 個) | DXC (DX11 で使えない) | 同左 | 同左 | 同左 |
| `require inlineraytracing` | 知らない値 (FXC) | コンパイルされない | DXC | DXC |
| `never_use_dxc` と DXC が必要な `require` | FXC | FXC | FXC | DXC |
| `UNITY_DEVICE_SUPPORTS_NATIVE_16BIT` が有効な構成 | DXC | DXC | DXC | DXC |
| `UNITY_DEVICE_SUPPORTS_INLINE_RAY_TRACING` が有効な構成 | FXC | DXC | DXC | DXC |
| `UNITY_DEVICE_SUPPORTS_WAVE_*` が有効な構成 | コンピュートシェーダーだけ DXC | 同左 | 同左 | 同左 |
| Shader Compiler Backend を DXC | ― | ― | ― | その API は DXC (`never_use_dxc` が優先される) |

- Unity のバージョン・対象の API・Shader Compiler Backend は、`--unity-project` のプロジェクトから読みます。設定ファイルで上書きできます
  ([設定](../configuration.md#unity))。読み込んだ内容は `--env` に表示されます
- バージョンが分からないときは、どのバージョンの結果も並べます。バージョンで変わる指摘 (UN0201 / UN0203 の一部と UN0202) は出しません
- DXC でコンパイルされるパスは DX11 では使えません。使えない API でのエラーは利用者に見えないので、そこでは構文を報告しません
- この判定は、ShaderProbePack で実測した 2022.3〜6.6 (10 プロジェクト、DX11 / DX12 / Vulkan) の 5980 の組み合わせと一致することを確かめています

## Unity だけのルール

ShaderLab のルール (`SL`)、Unity のルール (`UN`)、URP のルール (`URP`) は、Unity の環境でだけ報告します ([ルールの一覧](../../rules/README.md))。
宣言されていないシンボル ([CF0201](../../rules/CF0201.md)) は、下線で始まる大文字の名前 (`_NORMALMAP`) を報告します。

構成の軸は `#pragma multi_compile` / `shader_feature` / `dynamic_branch` から、エントリポイントは `#pragma vertex` / `fragment` / `kernel` などから読みます。
設定ファイルの `symbols:` と `units:` は使いません。

## 設定ファイルの例

```yaml
version: 1
environment: unity        # 省略してもよい
unity:
  profile: urp
  project: .
  # generated: .shaderlyn/unity   # Library と Unity Editor の無いマシンで解析するとき
```

テンプレートが [samples/environments/unity](../../../samples/environments/unity/) にあります。
