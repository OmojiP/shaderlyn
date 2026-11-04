# Unreal Engine

`environment: unreal` で、Unreal Engine の `.usf` / `.ush` を解析します。フォルダーを指定すると `.usf` を探します。エンジン本体が無くても動きます。

ほかの環境と共通の使い方は [CLI の使い方](../cli-usage.md)、設定ファイルのキーは [設定](../configuration.md#unreal) にあります。

## 目次

- [設定ファイル](#設定ファイル)
- [生成ファイルがない場合](#生成ファイルがない場合)
- [解析時の前提](#解析時の前提)
- [C++ から生成されるファイルを作る](#c-から生成されるファイルを作る)

## 設定ファイル

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

- **パーミュテーションは、既定では C++ から読みません。** `SHADER_PERMUTATION_BOOL` / `SHADER_PERMUTATION_INT` に合わせて `symbols:` に書いてください。
  `unreal: permutations: true` にすると、`--unreal-generate` が C++ から読んだパーミュテーションを構成の軸にします ([下記](#c-から生成されるファイルを作る))
- コンパイラと言語のバージョンは、汎用の HLSL と同じ表に従います ([汎用の HLSL「コンパイラと言語のバージョン」](hlsl.md#コンパイラと言語のバージョン))

テンプレートとサンプルが [samples/environments/unreal](../../../samples/environments/unreal/) にあります。

## 生成ファイルがない場合

ビルド時にエンジンが生成するファイル (`/Engine/Generated/…`) は、生成していないと解決できません。該当する `#include` はエラーとして報告しませんが、
uniform buffer などの宣言も読み取れないため、宣言の有無を調べる検査 (CF0301 / CF0310 など) を行いません。この状態は UE0001 (情報) で知らせます。
型の誤りなど、取り込んだ先の宣言を必要としない検査は行います。

関数呼び出しの引数を調べる検査 (CF0401 / CF0402) は、欠けているファイルに関数宣言が含まれないと分かっている場合に行います。たとえば、欠けているのが uniform buffer の宣言 (`GeneratedUniformBuffers.ush` / `UniformBuffers/…`) だけなら、検査します。

- `GeneratedInstancedStereo.ush` は `GetPrimaryView` など 3 つの関数だけを宣言するため、これらの関数呼び出しは検査しません
- `Material.ush` / `VertexFactory.ush` を取り込むシェーダーでは、関数の宣言が欠けている可能性があるため、引数の検査を行いません

uniform buffer の宣言や `Material.ush` / `VertexFactory.ush` の代わりとなるファイルは、エンジンの C++ とシェーダーから作れます ([下記](#c-から生成されるファイルを作る))。

## 解析時の前提
- エディタがプロジェクトの `Intermediate/ShaderAutogen/<プラットフォーム>` に生成する GBuffer の取り込み
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
  C++ から生成されるファイルがあれば、これらの名前は値を特定できない状態でも「宣言されている」ものとして扱います (CF0310 にしません)
- マテリアルから生成される `/Engine/Generated/Material.ush` が定義するマクロ（`INPUT_POSITION_QUALIFIERS`、`OPTIONAL_IsFrontFace` など）は、
  そのままでは分かりません。マテリアルのシェーダー（`BasePassPixelShader.usf` など）では、これらを使う行が構文エラー (CF0001) になります。
  C++ から生成されるファイルを作れば、その `Material.ush` が定義します ([下記](#c-から生成されるファイルを作る))

> 既定のマクロは、PC の Direct3D 12 を想定した値です。プロジェクトのプラットフォームやエンジンの版と合わないところがあれば、
> `defines` で上書きしてください。仮想パスは `virtual-paths` で追加できます。`--env` で、適用中の割り当てとマクロを確認できます。

## C++ から生成されるファイルを作る

エンジンは、C++ に書いた uniform buffer の構造体 (`BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT` など) から、
シェーダーをコンパイルする直前に `/Engine/Generated/` の宣言をメモリ上で作ります。Shaderlyn は同じ C++ を読み、これらの宣言をファイルに出力できます。

```sh
shaderlyn --config .shaderlyn.yaml --unreal-generate Intermediate/Shaderlyn/Generated
```

設定ファイルの `unreal: engine` と `project` で指定した `Source` / `Plugins` 以下の C++ を読みます (UE 5.5 のエンジンで 2 分ほどかかります)。
生成先のフォルダーを `unreal: generated` に指定すると、`/Engine/Generated` に割り当てて解析します。

```yaml
unreal:
  engine: C:/Program Files/Epic Games/UE_5.5/Engine
  project: .
  generated: Intermediate/Shaderlyn/Generated
```

| 生成するファイル | 内容 |
| --- | --- |
| `UniformBuffers/<名前>.ush`、`GeneratedUniformBuffers.ush` | uniform buffer の宣言。`View.ViewSizeAndInvSize` と `View_ViewSizeAndInvSize` のどちらの書き方でも解決できます。`Scene` も含みます |
| `GeneratedInstancedStereo.ush` | `ViewState` と `GetPrimaryView` などの宣言 |
| `ShaderlynEngineDefines.txt` | エンジンの C++ がシェーダーに定義するマクロの名前 (`SetDefine`、`SET_SHADER_DEFINE`、パーミュテーションの次元)。値は分からないので、名前だけを「宣言されている」ものとして扱います |
| `ShaderlynShaderRegistrations.tsv` | シェーダーの登録 (`IMPLEMENT_GLOBAL_SHADER` など。登録を包んだ独自のマクロも展開して読みます)。`.usf` ごとのエントリポイントとステージ |
| `ShaderlynShaderClasses.tsv` | シェーダーのクラスが定義するマクロとパーミュテーション、どの組もコンパイルされないクラス、エンジンがどのシェーダーにも定義するマクロ (`SUBSTRATE_ENABLED` など) |
| `Material.ush`、`UniformBuffers/Material.ush` | マテリアルの代わり。エンジンの `MaterialTemplate.ush` の、マテリアルごとに埋まる穴を、何もしない中身で埋めたもの (下記) |
| `VertexFactory.ush` | 頂点ファクトリ (`IMPLEMENT_VERTEX_FACTORY_TYPE`) の `.ush` を、`SHADERLYN_VERTEX_FACTORY` の値で選んで取り込むもの |
| `ShaderlynVertexFactoryDefines.ush` | 頂点ファクトリの `ModifyCompilationEnvironment` が定義するマクロを、`SHADERLYN_VERTEX_FACTORY` の値で選んで定義するもの |
| `ShaderlynGenerationStamp.tsv` | 読んだ ini の中身のハッシュと、C++ のファイルの一覧・更新日時・大きさ。生成した後に変わったかを確かめるのに使います |

- **プロジェクトの設定 (コンソール変数) を読みます。** エンジンはプロジェクトの設定から、どのシェーダーにもマクロを定義し (`GlobalBeginCompileShader`)、
  シェーダーのクラスもそれを見てコンパイルするかを決めます (`Substrate::IsSubstrateEnabled()` など)。
  値は C++ に書いた既定値 (`TAutoConsoleVariable` の宣言) を、エンジンとプロジェクトの ini (`DefaultEngine.ini` の `[/Script/Engine.RendererSettings]` など) が上書きします。
  プロジェクトの設定を変更した場合は再生成してください
- プラットフォームを問う関数 (`IsMobilePlatform`、`FDataDrivenShaderPlatformInfo::GetSupportsPrimitiveShaders` など) には、PC の Direct3D 12 (SM6) として答えます。
  どのパーミュテーションでもコンパイルされないクラスだけが登録したファイルは解析せず、UE0003 で伝えます
- **マテリアルのシェーダーも、宣言を検査します。** エンジンは `Material.ush` を、マテリアルごとに `MaterialTemplate.ush` の穴 (`%{num_material_texcoords}` など) を埋めて作ります。
  Shaderlyn は、どのマテリアルでもない代わりを作ります。数を表す穴は `0`、値を返す関数は `0` を返すもの、
  `FPixelMaterialInputs` のメンバーは C++ のマテリアルの属性の一覧から埋め、ほかは空にします。
  マテリアルのノードが書くコード (カスタムノードなど) は分からないので、それに関わる誤りは見つけられません。
  作った `Material.ush` の中は、利用者のコードとしては検査しません
- **頂点ファクトリは、シェーダーのクラスに合わせて選びます。** メッシュに描くマテリアルのシェーダー (`FMeshMaterialShader` の派生) は、
  頂点ファクトリごとにコンパイルされます。`ShouldCompilePermutation` を頂点ファクトリごとに実行し、コンパイルされる頂点ファクトリだけを読みます
  (`HairStrandsMaterialVS.usf` は髪の頂点ファクトリでだけ読みます)。頂点ファクトリの `ModifyCompilationEnvironment` が定義するマクロ
  (`PARTICLE_SPRITE_FACTORY` など) も、エンジンのコマンドラインと同じく、どのファイルよりも先に定義します。`unreal: permutations: true` なら頂点ファクトリを構成の軸 (`SHADERLYN_VERTEX_FACTORY`) にし、
  そうでなければ 1 つ (コンパイルされるなら `FLocalVertexFactory`、されなければ、コンパイルされるものの最初) を選んで読みます
- **マテリアルのブレンドモードは、既定のマテリアルと同じ不透明にします** (`MATERIALBLENDING_SOLID=1`)。
  エンジンはマテリアルごとに `MATERIALBLENDING_*` のどれか 1 つを定義します。半透明などのマテリアルでだけ通る分岐は読みません
- 生成ファイルがない場合、マテリアルのシェーダーでは従来どおり宣言の検査を行いません (UE0002。不要な場合は `rules:` で `UE0002: none` に設定します)
- マテリアルの「使用する対象」のフラグ (Used with Geometry Cache など) は C++ から決められないので、`unreal: permutations: true` では、どのフラグも立ちうるものとして頂点ファクトリを選びます。
  UE 5.5 のエンジンでは `VolumetricFogVoxelization.usf` に CF0313 が報告されます。ボリュームのマテリアルに Geometry Cache・Beam/Trail・APEX クロスのフラグが立つと、
  エンジンがコンパイルして失敗する組み合わせで、エンジンのコードにある食い違いです
- 宣言の型は、エンジンと同じ規則で C++ の型から決めます (`FVector4f` → `float4` など)。認識できない型は `float4` として扱い、`--unreal-generate` の出力に記載します
- 生成したファイルは、エンジンかプロジェクトの C++ や ini を変更した場合に再生成してください。バージョン管理に含める必要はありません。
  **再生成が必要な場合は、解析開始時に通知します** (CLI は標準エラー、VS Code 拡張は通知)。
  ini は内容を、C++ は更新日時とファイルサイズを比較します。C++ ファイルの一覧を読み込むため、UE 5.5 のエンジンでは解析時間が 2 秒ほど長くなります
  ```text
  前回の --unreal-generate から変更が検出されました。
  ini が変更されました (DefaultEngine.ini)。
  C++ ファイルが変更されました (変更 3 件、追加 1 件)。
  変更を解析に反映する場合は --unreal-generate をやり直してください。
  ```

UE 5.5 のエンジンのシェーダー (`Engine/Shaders` の `.usf` 522 個) では、宣言の検査を行えるものが 8 個から 460 個に増えます。
