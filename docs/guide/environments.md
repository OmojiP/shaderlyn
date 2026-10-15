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
- [コンパイラと言語のバージョン](#コンパイラと言語のバージョン)

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
  `select` / `and` / `or` は DXC の組み込み関数です (DXC 1.8 はどの版でも使えます。1.6 は 2021 だけ)

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
- **ビルドのときにエンジンが生成するファイル (`/Engine/Generated/…`) は、そのままでは解決できません。** その `#include` は誤りとして報告しませんが、
  中身 (uniform buffer などの宣言) が分からないので、宣言が無いことを根拠にする検査 (HL0301 / HL0310 など) は行いません。
  その旨を UE0001 (情報) で伝えます。型の誤りなど、取り込んだ先を見なくてよい検査は行います。
  呼び出しの引数の検査 (HL0340 / HL0341) は、欠けているのが関数を宣言しないと分かっているファイル
  (uniform buffer の宣言 `GeneratedUniformBuffers.ush` / `UniformBuffers/…`) だけなら行います。
  `GeneratedInstancedStereo.ush` は `GetPrimaryView` などの 3 つの関数だけを宣言するので、それらの呼び出しだけを見ません。
  `Material.ush` / `VertexFactory.ush` を取り込むシェーダーでは、関数が欠けているかもしれないので行いません。
  uniform buffer の宣言は、エンジンの C++ から作れます ([下記](#c-から生成されるファイルを作る))
- エディタがプロジェクトの `Intermediate/ShaderAutogen/<プラットフォーム>` に書き出す GBuffer の取り込み
  (`/Engine/Generated/ShaderAutogen/AutogenShaderHeaders.ush`) は、そのフォルダーがあれば読みます。エディタで一度プロジェクトを開くと作られます
- マテリアルのシェーダーには、エンジンと同じく `IS_MATERIAL_SHADER=1` を定義します
- シェーダーコンパイラのマクロは、PC の Direct3D 12 を想定して `COMPILER_HLSL=1` / `COMPILER_DXC=1` / `SM6_PROFILE=1` を定義します。
  ほかのプラットフォームに合わせるときは `defines` で上書きしてください
- **言語のバージョンは、エンジンの設定に合わせてシェーダーごとに決めます。** エンジンはプラットフォームの設定
  (`Engine/Config/<プラットフォーム>/DataDrivenPlatformInfo.ini` の `EnablesHLSL2021ByDefault`) で決めており、
  Shaderlyn もそれを読みます (既定は PC の `PCD3D_SM6`)。UE 5.5 の PC は `1` で、次のようになります。

  | シェーダー | 言語のバージョン |
  | --- | --- |
  | グローバルシェーダー | HLSL 2021 (`COMPILER_SUPPORTS_HLSL2021=1` を定義する) |
  | マテリアルのシェーダー (`/Engine/Generated/Material.ush` を、ヘッダを経由しても取り込むもの) | HLSL 2018 |

  シェーダーの C++ が `CFLAG_HLSL2021` で個別に 2021 にすることがある (Nanite・Lumen・パストレーシングなど) ので、
  2018 で読むシェーダーでも、template・演算子の多重定義・ビットフィールドは誤りにしません。
  `hlsl: version` を書くと、すべてのシェーダーをそのバージョンで読みます。エンジンのフォルダーが無いときの既定は 2018 です。
  `--env` に、どう決めたかを表示します
- `#pragma once` を扱います。エンジンのヘッダは、インクルードガードではなく `#pragma once` で二重の取り込みを防いでいます
- エンジンの C++ がコンパイルのときに定義するマクロのうち、PC の Direct3D で値の決まる `HAS_INVERTED_Z_BUFFER=1` は既定で定義します。
  プロジェクトの設定で値の変わるもの（`UE_LWC_RENDER_TILE_SIZE_*`、作業色空間の行列 `WORKING_COLOR_SPACE_*` など）と、
  パーミュテーションの `DIM_*` は定義しません。使っている場合は `defines` / `symbols:` に書いてください。
  C++ から生成されるファイルを作っていれば、こうした名前は値が分からないまま「宣言されている」ものとして扱います (HL0310 にしません)
- マテリアルから生成される `/Engine/Generated/Material.ush` が定義するマクロ（`INPUT_POSITION_QUALIFIERS`、`OPTIONAL_IsFrontFace` など）は分かりません。
  マテリアルのシェーダー（`BasePassPixelShader.usf` など）では、これらを使う行が構文エラー (HL0001) になります。`defines` で空に定義すると読めます

> 既定のマクロは、PC の Direct3D 12 を想定した値です。プロジェクトのプラットフォームやエンジンの版と合わないところがあれば、
> `defines` で上書きしてください。仮想パスは `virtual-paths` で足せます。`--env` で、使っている割り当てとマクロを確かめられます。

### C++ から生成されるファイルを作る

エンジンは、C++ に書いた uniform buffer の構造体 (`BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT` など) から、
シェーダーをコンパイルする直前に `/Engine/Generated/` の宣言をメモリ上で作ります。Shaderlyn は同じ C++ を読んで、これをファイルに書き出せます。

```sh
shaderlyn --config .shaderlyn.yaml --unreal-generate Intermediate/Shaderlyn/Generated
```

設定ファイルの `unreal: engine` と `project` の `Source` / `Plugins` の下の C++ を読みます (UE 5.5 のエンジンで 2 分ほどかかります)。
書き出したフォルダーを `unreal: generated` に書くと、`/Engine/Generated` に割り当てて解析します。

```yaml
unreal:
  engine: C:/Program Files/Epic Games/UE_5.5/Engine
  project: .
  generated: Intermediate/Shaderlyn/Generated
```

| 書き出すもの | 中身 |
| --- | --- |
| `UniformBuffers/<名前>.ush`、`GeneratedUniformBuffers.ush` | uniform buffer の宣言。`View.ViewSizeAndInvSize` と `View_ViewSizeAndInvSize` のどちらの書き方でも引けます。`Scene` も含みます |
| `GeneratedInstancedStereo.ush` | `ViewState` と `GetPrimaryView` などの宣言 |
| `ShaderlynEngineDefines.txt` | エンジンの C++ がシェーダーに定義するマクロの名前 (`SetDefine`、`SET_SHADER_DEFINE`、パーミュテーションの次元)。値は分からないので、名前だけを「宣言されている」ものとして扱います |

- **マテリアルから作られる `Material.ush` / `VertexFactory.ush` は作れません。** これらを取り込むシェーダーでは、今までどおり宣言の検査を行いません (UE0002。要らなければ `rules:` で `UE0002: none` にします)
- 宣言の型は、エンジンと同じ規則で C++ の型から決めます (`FVector4f` → `float4` など)。知らない型は `float4` として書き、`--unreal-generate` の出力に挙げます
- 作ったファイルは、エンジンかプロジェクトの C++ を変えたら作り直してください。バージョン管理には入れなくてかまいません

UE 5.5 のエンジンのシェーダー (`Engine/Shaders` の `.usf` 522 個) では、宣言の検査を行えるものが 8 個から 460 個に増えます。

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
| コンパイラと言語のバージョン | パスと API ごとに判定する。既定は FXC、`#pragma use_dxc` などで DXC の 2018 ([下記](#コンパイラと言語のバージョン)) | `hlsl: compiler` と `hlsl: version` (汎用の HLSL は DXC の 2021。Unreal はエンジンの設定に合わせてシェーダーごとに決める) |
| 宣言し忘れたシンボル (HL0330) | 下線で始まる大文字の名前 (`_NORMALMAP`) を報告 | 報告しない (`_DEBUG` などが普通に条件に出るため) |
| Unity だけのルール | SL / UN / URP | 出ない |
| Unity だけのオプション | `--profile` / `--unity-project` / `--unity-editor` | 指定するとエラー |
| VS Code の構成の選択 | `#pragma` で宣言したシンボル | `symbols:` のシンボル。値を持つシンボルは `QUALITY = 2` のように値を選ぶ |

## コンパイラと言語のバージョン

使える構文と型は、コードをコンパイルするコンパイラ (FXC / DXC) と、DXC の言語のバージョン (`-HV`) で決まります。
Shaderlyn はこの組 (方言) ごとの表を持ち、使えない構文を [HL0003](../rules/HL0003.md) などで報告します。
表は手元の FXC (Windows SDK 10.0.26100) と DXC 1.6 / 1.8 で確かめたものです。

| | FXC | DXC 2016 | 2017 | 2018 | 2021 |
| --- | --- | --- | --- | --- | --- |
| `template` / 演算子の多重定義 / ビットフィールド | × | × | × | × | ○ |
| `enum` / `enum class` | × | × | ○ | ○ | ○ |
| `int64_t` | × | ○ | ○ | ○ | ○ |
| `float32_t` などの明示のビット幅の型 | × | × | × | ○ | ○ |
| `float16_t` など (`-enable-16bit-types`) | × | × | × | ○ | ○ |
| ベクトルへの `&&` / `?:` | ○ | ○ | ○ | ○ | × ([HL0349](../rules/HL0349.md)) |
| `for` の変数をループの外で使う | ○ | ○ | ○ | ○ | × |
| `and()` / `or()` / `select()` | × | ○ | ○ | ○ | ○ |
| 構造体の `++` | 何もしない ([HL0348](../rules/HL0348.md)) | × | × | × | × |
| `sampler2D` / `tex2D` (DX9 の書き方) | ○ | × | × | × | × |

### Unity

Unity は既定で FXC を使い、パス (コンピュートシェーダーはカーネル) ごと・API ごとに DXC (HLSL 2018) へ切り替えます。
Unity の DXC は `sampler2D` / `tex2D` を受け付けます。Shaderlyn はこの判定を Unity のバージョンごとに持っています。
Unity は `#pragma once` を効かせません (2022.3 / 6.3 / 6.6 で確かめた)。Shaderlyn も Unity の環境では効かせず、書いてあれば [UN0101](../rules/UN0101.md) で伝えます。

| | 2022.3 | 2023.2〜6.3 | 6.4〜6.5 | 6.6〜 |
| --- | --- | --- | --- | --- |
| include ファイルの中の `use_dxc` / `require` | 効く | 無視 | 無視 | 無視 |
| `use_dxc` (API なし) | DX11 も DXC (DX11 で使えない) | 同左 | 同左 | DX12 / Vulkan / Metal が DXC、DX11 は FXC |
| `use_dxc d3d11` | DX12 と DX11 が DXC | 同左 | 同左 | DX12 が DXC (非推奨) |
| `use_dxc dx12` | 無視 | 無視 | 無視 | DX12 が DXC |
| `use_dxc d3d12` | 無視 | 無視 | 無視 | 無視 |
| DXC が要る `require` (12 個) | DXC (DX11 で使えない) | 同左 | 同左 | 同左 |
| `require inlineraytracing` | 知らない値 (FXC) | コンパイルされない | DXC | DXC |
| `never_use_dxc` と DXC が要る `require` | FXC | FXC | FXC | DXC |
| `UNITY_DEVICE_SUPPORTS_NATIVE_16BIT` が有効な構成 | DXC | DXC | DXC | DXC |
| `UNITY_DEVICE_SUPPORTS_INLINE_RAY_TRACING` が有効な構成 | FXC | DXC | DXC | DXC |
| `UNITY_DEVICE_SUPPORTS_WAVE_*` が有効な構成 | コンピュートシェーダーだけ DXC | 同左 | 同左 | 同左 |
| Shader Compiler Backend を DXC | ― | ― | ― | その API は DXC (`never_use_dxc` が勝つ) |

- Unity のバージョン・対象の API・Shader Compiler Backend は、`--unity-project` のプロジェクトから読みます。設定ファイルで上書きできます
  ([設定](configuration.md#unity))。読んだものは `--env` に出ます
- バージョンが分からないときは、どのバージョンの結果も並べます。バージョンで変わる指摘 (UN0201 / UN0203 の一部と UN0202) は出しません
- DXC でコンパイルされるパスは DX11 では使えません。使えない API でのエラーは利用者に見えないので、そこでは構文を報告しません
- この判定は、ShaderProbePack で実測した 2022.3〜6.6 (10 プロジェクト、DX11 / DX12 / Vulkan) の 5980 の組み合わせと一致することを確かめています
