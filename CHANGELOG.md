# 変更履歴

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に従い、
バージョンの付け方は [セマンティックバージョニング](https://semver.org/lang/ja/) に従う。
CLI、VS Code 拡張、NuGet パッケージは同じタグから同じバージョンで出す。
0.x の間は、マイナーバージョンを上げるときに公開 API や CLI のオプションを変えることがある。
そうした変更は「変更」「削除」の項目の先頭に **破壊的変更:** と書いて示す。

利用者から見て何が変わるかを書く。実装の変更は、それが挙動か API を変える場合にだけ載せる。

## [未リリース]

### 追加

- Unreal Engine: `--unreal-generate <フォルダー>` で、エンジンとプロジェクトの C++ から、ビルドのときに生成される取り込み
  (uniform buffer の宣言 `/Engine/Generated/UniformBuffers/…`・`GeneratedUniformBuffers.ush`・`GeneratedInstancedStereo.ush`) を書き出す。
  設定ファイルの `unreal: generated` にそのフォルダーを書くと、`/Engine/Generated` に割り当てて解析する。
  C++ がシェーダーに定義するマクロ (`SetDefine`、`SET_SHADER_DEFINE`、パーミュテーションの次元) の名前も書き出し、HL0310 にしない。
  UE 5.5 のエンジンのシェーダーでは、宣言の検査を行えるシェーダーが 522 個中 8 個から 460 個に増える
  ([C++ から生成されるファイルを作る](docs/guide/environments.md#c-から生成されるファイルを作る))
- Unreal Engine: エディタがプロジェクトの `Intermediate/ShaderAutogen/<プラットフォーム>` に書き出す
  `/Engine/Generated/ShaderAutogen/AutogenShaderHeaders.ush` を読む
- `sizeof` を組み込みの名前として扱う (HL0310 にしない)
- Unreal Engine の環境だけのルール [UE0001](docs/rules/UE0001.md) (エンジンが生成するファイルが無いので宣言の検査を見送った。`--unreal-generate` を案内する) と
  [UE0002](docs/rules/UE0002.md) (マテリアルごとに生成される `Material.ush` / `VertexFactory.ush` の中身が分からないので見送った)。
  UE0002 は利用者には直せないので、要らなければ `rules:` で `UE0002: none` にできる
- [HL0303](docs/rules/HL0303.md) (警告) / [HL0304](docs/rules/HL0304.md) (エラー): cbuffer の中の関数をエントリポイントにしている。
  cbuffer の中に `#include` を書き、取り込んだファイルに関数があるとこうなる。FXC はコンパイルでき、DXC はエントリポイントを見つけられない
  (Unity の DXC は「Lost connection with shader compiler process」で落ちる)。FXC でだけコンパイルされるなら HL0303、DXC でコンパイルされるなら HL0304
- [SL1057](docs/rules/SL1057.md) (警告): `.shader` の先頭に BOM が重なっている。Unity は読めるので、構文エラー (SL0001) ではなくこの警告で伝える

### 変更

- Unreal Engine: 言語のバージョンを、エンジンの設定 (`DataDrivenPlatformInfo.ini` の `EnablesHLSL2021ByDefault`) に合わせてシェーダーごとに決める。
  UE 5.5 の PC は、グローバルシェーダーを HLSL 2021、マテリアルのシェーダー (`Material.ush` を取り込むもの) を 2018 で読む。
  2018 で読むシェーダーでも template などの 2021 の宣言は誤りにしない (C++ が個別に 2021 にすることがある)。
  `hlsl: version` を書けば、今までどおりすべてそのバージョンで読む。エンジンのシェーダーの HL0003 (template) 110 件が無くなる
- Unreal Engine: マテリアルのシェーダーに、エンジンと同じく `IS_MATERIAL_SHADER=1` を定義する
- Unreal Engine: 欠けているのがエンジンが生成するファイルだけのとき、SL0002 ではなく UE0001 / UE0002 で伝える。
  抑制コメントやベースラインに SL0002 を書いていた場合は、UE0001 / UE0002 に書き換える

### 修正

- Unity 6.3.26f1 以降 (6.4 / 6.5 / 6.6 も) の Editor は、同梱のヘッダ (`UnityCG.cginc` など) を `Data/Resources/CGIncludes` に置いている。
  `Data/CGIncludes` しか見ていなかったので、CGPROGRAM のシェーダーが TOOL0004 になり、Editor の推定もできなかった
- `.shader` の先頭のノーブレークスペース (U+00A0) を空白として読んでいた。Unity は構文エラーにするので、SL0001 で報告する
- cbuffer の中の関数をエントリポイントにしていると、関数が見つからない (HL0301) と報告していた
- `--cache` に書けないパスを指定すると、解析が終わってから、読めない理由 (`UnauthorizedAccess_IODenied_Path` など) を出していた。
  解析の前に、権限が無い・フォルダーを指している・ドライブが無いなどの理由を伝える。置き換えに失敗した一時ファイル (`.tmp`) を消し、
  `NUL` などのデバイス名も書けないと伝える。ファイル・設定ファイル・ベースラインを読めないときの理由も同じように伝える
- 同じヘッダを複数の Pass が取り込み、キーワードを宣言する Pass と宣言しない Pass が混ざっていると、
  宣言しない Pass でも、ヘッダの `#if` の分岐にそのキーワードの条件を付けていた。
  「構成：A のとき 'a' は宣言されていません」(HL0310) や「値を返さない経路が存在します」(HL0360) を誤って報告していた
- Unity: ヘッダの中の `#include "..."` を、そのヘッダから引けなければ、解析しているシェーダーからの相対で引き直す (Unity 6.6 と同じ)。
  シェーダーからの相対で書いた取り込みを、解決できない (HL0321) と誤って報告していた
- 取り込みの展開結果を使い回すとき、入れ子で使い回した取り込みの「解決できなかった取り込み」などが、外側の取り込みの記録から抜けていた。
  ファイルを並べて解析すると、解決できない取り込みが消えて宣言が揃っていると判断し、宣言の検査を誤って行うことがあった。
  結果が解析の順番 (並列の実行) で変わり、Unreal Engine のエンジンのシェーダーで HL0310 が 43 件出ていた

## [0.2.0] - 2026-10-14

Unity 以外の環境 (DXC で直接コンパイルする HLSL、Unreal Engine、独自エンジン) を解析できるようにした。
そのために Unity 固有の部分を `Shaderlyn.Unity` パッケージへ分けたので、自作ルールの一部は書き換えが要る (下の「破壊的変更」)。
書き換えの手順は [0.1 から 0.2 へ移行する](docs/guide/migration-0.2.md) にある。
Unity のシェーダーの解析結果は 0.1.1 と変わらない (Unity 同梱のシェーダーと fixtures の全診断を比べて確かめた)。

### 追加

- 設定ファイルの `environment` (コマンドラインは `--environment`) で解析する環境を選ぶ。
  `unity` (既定) / `hlsl` / `unreal` ([Unity 以外の環境で使う](docs/guide/environments.md))
- 汎用の HLSL 環境 (`environment: hlsl`)。コンパイル単位を設定ファイルの `units:` (`files` / `entry` / `target` / `defines` / `symbols`) に、
  構成の軸を `symbols:` (`name` / `values` / `one-of` / `at-most-one`) に書く。
  ターゲットから DXC が定義するマクロ (`__SHADER_TARGET_STAGE` など) を決める。言語のバージョンは `hlsl: version` (既定 2021)
- Unreal Engine の環境 (`environment: unreal`、`Shaderlyn.Unreal` パッケージ)。`.usf` / `.ush` を解析する。
  仮想パス (`/Engine/…` / `/Project/…` / `/Plugin/…`) を設定ファイルの `unreal:` (`engine` / `project` / `virtual-paths`) から解決する。
  ビルドのときに生成される取り込み (`/Engine/Generated/…`) は誤りにせず、宣言が無いことを根拠にする検査を止めて SL0002 で伝える。
  uniform buffer の宣言 (`GeneratedUniformBuffers.ush` / `UniformBuffers/…`) は関数を宣言せず、
  `GeneratedInstancedStereo.ush` が宣言する関数は 3 つに決まっているので、それらだけが欠けているなら呼び出しの引数 (HL0340 / HL0341) は検査する
  (環境の `IHlslEnvironment.GetFunctionsDeclaredIn`、`ShaderCompilation.MayLackOverloadsOf`)
- 値を持つシンボル (`QUALITY ∈ {0, 1, 2}`)。`#if QUALITY == 2` / `#if QUALITY >= 1` / `#if QUALITY` を値ごとに読み分け、
  指摘に `QUALITY == 2 のとき` を添える。1 つを除くすべての値のときは `QUALITY != 0 のとき` と短く書く
- HLSL 2021 の構文 (`template`・`enum class`・演算子の多重定義・メンバー関数・ビットフィールド) を、汎用の HLSL 環境では構文エラーにしない
- シェーダーモデル 6.1〜6.9 の組み込み関数 (メッシュシェーダー、`WaveMatch` / `WaveMultiPrefix*`、`QuadAny` / `QuadAll`、`Barrier` など)、
  ディスクリプタヒープ (`ResourceDescriptorHeap` / `SamplerDescriptorHeap`)、`RayQuery` / `FeedbackTexture2D` / `RasterizerOrdered*` の型、
  メッシュシェーダーの引数の修飾子 (`vertices` / `indices` / `primitives` / `payload`)。`select` / `and` / `or` は HLSL 2021 のときだけ組み込み関数とする
- 独自エンジンの環境を書いて足す口 (`IHlslEnvironment`、`HostEnvironment`、`Program.RunAsync` と `ShaderLanguageServer` の多重定義)。
  実例集に例がある ([独自エンジンの環境](docs/custom-rules/cookbook.md#独自エンジンの環境))
- VS Code 拡張: 呼び出しの案内 (引数の形) に HLSL 2021 の template の関数も出す (`template<T, U> T Mix(T a, U b, float t)`)
- VS Code 拡張: `.hlsli` / `.usf` / `.ush` も開く。構成の選択で、値を持つシンボルは `QUALITY = 2` のように値を選ぶ
- `--env` に環境と、その設定 (汎用の HLSL なら構成の軸とコンパイル単位、Unreal なら仮想パスの割り当て) を出す
- 設定ファイルの `hlsl: compiler` (`dxc` / `fxc`)。`hlsl: version` は `2016` / `2017` も受け付ける。
- SL1056: Unity は UTF-16 のファイルを読めない (BOM があっても。2022.3 / 6.3 / 6.6 で確かめた)。
  UTF-16 で保存した `.shader` はファイルの先頭に、取り込むヘッダは `#include` の行に報告する。
  ファイルは BOM の無い UTF-16 も見分けて読み (`SourceText.Load` / `FromBytes` / `IsUtf16`)、Unity 以外の環境ではそのまま解析する
- 出力する指摘を重要度で絞る `--min-severity` と、設定ファイルの `report: min-severity`。設定ファイルでは出力の形式
  (`text` / `json` / `sarif` / `github`) ごとに変えられる。終了コードは絞る前のすべての指摘で `--error-on` に従って決める
- Unity の環境で、パスごと・API ごとに FXC と DXC (HLSL 2018) のどちらでコンパイルされるかを判定する
  (`#pragma use_dxc` / `never_use_dxc` / DXC が要る `require`、`UNITY_DEVICE_SUPPORTS_*` のキーワード、Unity 6.6 の Shader Compiler Backend)。
  Unity 2022.3〜6.6 の実測に合わせ、バージョンで変わる規則も扱う。DXC になるパスでは `enum` などを HL0003 にしない。
- HL0003 が、パスの届く API のどれかで使えない構文と型を、API とコンパイラを添えて報告する
  (`enum は dx11 (FXC) では使えません` など)。FXC での `int64_t` / `float32_t` / `float16_t` などの型、
  `-enable-16bit-types` の無い DXC での 16 ビットの型も報告する。構成のキーワード (`UNITY_DEVICE_SUPPORTS_NATIVE_16BIT` など) を踏まえる。
  interface 型のグローバル変数 (DXC と、シェーダーモデル 5.0 より前の FXC でエラー)、単体の DXC での `sampler2D` / `tex2D` などの DX9 の書き方、
  DXC でのエントリ関数の `uniform` 引数も報告する。
- HL0354 (情報): `for` の初期化の変数をループの外で使っている (FXC と HLSL 2021 より前は通るが、HLSL 2021 へ移すとエラーになる)。
- HL0349: HLSL 2021 でベクトルや行列に `&&` / `||` / `?:` を使っている (`and()` / `or()` / `select()` を案内する)。
- 設定ファイルの `hlsl: enable-16bit-types` を、`compiler: fxc`・HLSL 2018 より前・シェーダーモデル 6.2 より前の `target` と組み合わせたら TOOL0003 で報告する。
- HL0348: FXC で構造体の値を `++` / `--` している (エラーにならず何もしない)。DXC に届くなら HL0346 で API を添えて報告する。
- SL1055: Unity では効かない `#pragma once` (Unity 2022.3 / 6.3 / 6.6 で、2 度目の取り込みが再定義のエラーになることを確かめた)。
  Unity の環境のプリプロセッサも `#pragma once` を効かせず、実際と同じく 2 度取り込む
- SL1050 / SL1051 / SL1053 / SL1054: `#pragma use_dxc` / `never_use_dxc` / `require` が、対象の API や Unity のバージョンでは意図どおりに効かない。
  直し方の種類で ID を分けた: 無視される指定 (SL1050)、DX11 で使えなくなる指定 (SL1053)、バージョンで結果が変わる組み合わせ (SL1054)、注意 (SL1051)。
  文は「何が起きているか。何でコンパイルされるか。どう直すか。」の 3 文で書く
  (無視される API 名、DX11 で使えなくなる指定、バージョンで結果が逆になる組み合わせ、2023.2 以降で無視される include ファイルの中の指定、6.6 で非推奨の `d3d11` など)。
- SL1052: 16 ビットの型を有効にしたパス (`require native16bit`、`UNITY_DEVICE_SUPPORTS_NATIVE_16BIT` の構成) の定数バッファに `half` などがある
  (Unity が 32 ビットで書き込んだ値を正しく読めない。白が DX12 で (0, 1.875, 0, 1.875) になる)。
- 設定ファイルの `unity: version` / `unity: graphics-apis` / `unity: shader-compiler-backend`。
  無ければ Unity プロジェクトの `ProjectVersion.txt`・`ProjectSettings.asset`・ビルドプロファイルから読み、`--env` に出す。
  使える構文はコンパイラと版で決まる (`HlslDialect` / `HlslLanguageFeatures`。手元の FXC と DXC 1.8 で確かめた表)
- 設定ファイルの `hlsl: enable-16bit-types` (DXC の `-enable-16bit-types`)。`true` で `__HLSL_ENABLE_16_BIT=1` を定義する
- DirectX 12 のシェーダーの書き方を読む: `export` 修飾子、引数の属性とワークグラフの型 (`[MaxRecords(2)] NodeOutput<T>`)、
  文字列の中の行の継続 (ルートシグネチャのマクロ)、`using namespace dx;`、`1.#INF`、括弧の中のカンマ演算子、`struct [raypayload]`
- HLSL 2021 の `enum` / `enum class` を読み、列挙子を宣言された名前として扱う (`Mode m = Opaque;`、`Filter::Linear`)。
  名前空間で修飾した型への型変換 (`(AnalyticPrimitive::Enum) x`) も読む。構文木は `EnumDeclarationSyntax`
- HLSL 2021 の演算子の多重定義 (`Complex operator+(Complex other)`、`operator[]`、`operator()`) を関数として読み、本体も検査する。
  `(a + b).re` のように演算の結果を使う式は、演算子の戻り値の型として読む。構文木は `FunctionDeclarationSyntax` (`IsOperator` / `OperatorTokens`)
- HLSL 2021 の構造体のメソッドの呼び出し (`s.Get()`) を検査する。無いメソッドを HL0312、引数の個数の違いを HL0341 で報告し、
  戻り値の型を読む (`s.Get().q`)。`operator[]` (`s[0]`) と `operator()` (`s(0.5)`) の戻り値の型も読む。継承した基底のメソッドも引く
- HLSL 2021 の `template` の関数と構造体を読み、本体も検査する。型引数 (`T`) を含む型は分からない型として扱い、それを根拠に指摘しない。
  型引数を書いた呼び出し (`Twice<float4>(x)`、`buffer.Load<float4>(i)`) を読む。型引数を書かない呼び出しは、DXC と同じ規則で実引数から型引数を推論する。
  戻り値が型引数そのものなら、書いた型か推論した型に置き換える (`Twice(p).q` を HL0312)
- HLSL 2021 の template の構造体 (`Box<float4> b`) のメンバーを検査する。無いメンバーとメソッドを HL0312 で報告し、
  型引数そのものの型のフィールドやメソッドの戻り値 (`T v;`) を、宣言の型引数 (`float4`) に置き換えて読む。
  型引数を付けた基底 (`struct Derived : Box<T>`) も読む
- 新しいルール HL0346 (構造体に使えない演算子)。演算子を定義していない構造体への演算 (`a + b`、`-a`、`a == b`)、
  右辺だけが構造体の演算 (`2 * a`)、条件演算子で構造体を選ぶ式 (`k > 0 ? a : b`)、複合代入と増減 (`a += b`、`a++`)、
  多重定義できない演算子の定義 (`operator++`、`operator+=`、`operator<<`)、構造体の外の演算子の定義 (DXC 1.8 はエラー) を報告する
- 新しいルール HL0347 (enum の値の型が合わない)。数や別の enum の値を enum へ入れる式 (`Mode m = 1`)、
  enum class の値を数として使う式 (`int x = Mode::A`)、enum class への演算子 (`Mode::A + 1`、`m == 1`)、enum の増減を報告する。
  無い列挙子 (`Mode::C`) は HL0312 で報告する
- HLSL 2021 で、構造体が関わる暗黙の変換を検査する。別の構造体の値 (`P p = q`)、数と構造体の間 (`P p = 0`、`float f = p`) を、
  初期化・代入は HL0350、実引数は HL0340、return は HL0351 で報告する。派生した構造体を基底へ入れるのは正しい
- HLSL 2021 の `class` / `interface` を構造体と同じに読み、メンバー・メソッド・継承を検査する。
  `template` の明示的な特殊化 (`template<> float Twice<float>(float x)`、`template<> struct Box<float>`) も読み、本体を検査する
- HLSL 2021 の構造体のメソッドの中の `this` を読む (`this.x`)
- 新しいルール HL0345 (template の型引数を実引数から決められない)。`MaxOf(k, h)` (`float` と `half`) や `MaxOf(1.0, k)` (接尾辞の無い数と `float`) を報告する。
  template の関数の引数の個数が合わない呼び出しは HL0341 で報告する。
  `vector<T, N>` の仮引数からも推論し、形の合わない実引数 (`float4` を `vector<T, 3>` へ) も報告する。戻り値の `vector<T, N>` も置き換える
  構文木は `TemplateDeclarationSyntax`、`InvocationExpressionSyntax.TypeArgumentTokens`
- 汎用の HLSL 環境で、ライブラリ (`lib_6_x`) とターゲットの分からない単位では、宣言だけの関数 (HL0311) を報告しない。
  定義はリンクするほかのライブラリにありうる (`HlslCompilationUnit.AllowsExternalDefinitions`)
- GitHub Action に入力 `environment` を足した
- 環境ごとの設定ファイルのテンプレートと、試すためのシェーダー ([samples/environments](samples/environments/))。
  汎用の HLSL には HLSL 2021 の構文を使うシェーダーと、DXR のライブラリ (`lib_6_6`) の単位がある

### 変更

- Unreal Engine の環境で、`hlsl: version: 2021` のときに `COMPILER_SUPPORTS_HLSL2021=1` を、常に `HAS_INVERTED_Z_BUFFER=1` を定義する (エンジンと同じ)。
  `--env` の「既定のマクロ」は「定義するマクロ」として、実際に使う値を出す。
- SL1011 の文で、Queue に付けられるオフセットを「('Geometry+1' のようにオフセットを指定可能 [queue name] [offset] )」と書く。
- 指摘の文とタイトルを見直した。「ありません」は「存在しません」、「合いません」は「一致しません」に揃え、構成の条件は「構成：_A のとき」、HL0003 の環境は「環境：dx11 (FXC)」と書く。
  ファイルの終わりで要るものは「ファイルの末尾には '}' が必要です。」と書く。ID と位置は変えていない (ベースラインはそのまま使える)。
- **破壊的変更:** Unity 固有の型を `Shaderlyn.Unity` パッケージ (名前空間 `Shaderlyn.Unity`) へ移した。
  `PropertySymbol` / `ShaderPropertyKind` / `PropertyTypeCompatibility` / `ShaderSymbols` / `ComputeKernels` / `ProgramBlockExtractor` /
  `UnityPackageIncludeResolver` / `UnityShaderStubs`、レンダーパイプラインのプロファイル (`Shaderlyn.Unity.Profiles`)。
  `SymbolGroup` は無くなり、構成の空間 (`VariantSpace` / `VariantGroup`、`Shaderlyn.Core.Syntax`) になった
- **破壊的変更:** `ShaderCompilation.Create(text, tree, options)` と `CreateForHlsl(text, options)` は
  `UnityCompilation.Create` / `UnityCompilation.CreateForHlsl` になった。`ShaderCompilation.Create` はコンパイル単位と環境を受け取る形になった
- **破壊的変更:** `ShaderCompilation` の `Properties` / `ShaderLabTree` / `ShaderName` / `Profile` / `IsStandaloneHlsl` / `IsReferencedFromShaderLab` /
  `IsDeclaredButNeverUsed` / `CountIdentifierOccurrences` / `GetMaterialConstantBuffers` は `compilation.GetUnity()` (`UnityShaderModel`) へ、
  `AnalyzedProgram` の `Block` / `IncludeBlocks` / `Pass` / `PassName` は `program.GetUnityBlock()` (`UnityProgramBlock`) へ移した
- **破壊的変更:** `SemanticsOptions` の `Profile` / `UseFallbackMacros` / `DefaultPredefinedMacros` は `UnityOptions` へ移した。
  `SemanticsOptions.PredefinedMacros` は `null` で「環境の既定」を表す
- **破壊的変更:** 構文木を読む方言を `PreprocessorOptions.Dialects` (`HlslDialect` の並び) で持つ。既定は FXC (以前の既定は HLSL 2018)。
  `LanguageVersion` は DXC 1 つの略記として残した。`HlslIntrinsics.IsKnownFunction` は言語のバージョンの代わりに `HlslLanguageFeatures` を受け取る。
  `HlslLanguageVersion` の値はバージョンの数そのもの (`Hlsl2018 = 2018`) になり、`Hlsl2016` / `Hlsl2017` を足した
- **破壊的変更:** 設定ファイルの `profile` は `unity:` の節に書く。節の外の `profile` も読むが、TOOL0003 で移すよう伝える。
  既定の `--error-on warning` ではこの警告で終了コードが 1 になるので、上げる前に移すこと
- **破壊的変更:** `--profile` / `--unity-project` / `--unity-editor` を Unity 以外の環境で指定するとエラーにする
- 言語サーバー: 設定ファイルを、開いたファイルから上へ辿って探す (CLI と同じ)。ワークスペースのルートにしか設定ファイルを置けなかった。
  設定ファイルを書き換えると、次の解析から反映される (ウィンドウの再読み込みが要らない)
- Unity の環境でも `.hlsli` / `.usf` / `.ush` を HLSL として読む。名前を挙げて解析したときに ShaderLab として読み、SL0001 を出していた
- 言語サーバー: `shaderlyn/conditionSymbols` の各シンボルに `declared` と、値を持つシンボルの `variable` / `value` を足した。
  `declaredByPragma` は同じ値で残してある
- HL0301 の文の `#pragma {0}` を `{0}` にした。Unity では同じ文が出る。汎用の HLSL 環境では `設定ファイルの entry が指定する…` になる
- HL0003 の題を「この環境では使えない構文です」にした。文は Unity では今までどおり「… は Unity のシェーダーでは使えません」、
  汎用の HLSL・Unreal の HLSL 2018 では「… は HLSL 2018 では使えません」になる
- SL0002 の文の主語を環境で変える。ファイル全体の木を持たない環境では「宣言が無いことを根拠にする検査」と言う
- 言語サーバー: `shaderlyn/conditionSymbols` の各シンボルに、属するすべての組の番号 `groups` を、結果に組ごとの `requiresOne` を足した

### 修正

- Linux / macOS で、区切りに `\` を使った `#include` (`..\Lib\Lib.hlsl`) を解決できず HL0321 にしていた。Unity と同じく `/` と同じに読む。
- 利用者が書いたヘッダの関数が構成によって本体を変える (`#if` の中に局所変数がある) と、既定の構成の木に無い局所変数を
  宣言されていない (HL0310) と報告していた (HDRP の SubpixelMorphologicalAntialiasing.hlsl の `leftRight` など)。
- 同じ位置の指摘を重ねないための記録が、ファイルを区別していなかった。取り込んだヘッダと解析しているファイルの同じ文字位置の指摘を
  同じものとみなし、後の方を報告しないことがあった (HL0310 / HL0340 / HL0341 / HL0312 ほか)。
- VS Code 拡張: 取り込んだヘッダを書き換えて保存しても、ヘッダの中の指摘の行が書き換える前のままだった
  (ヘッダの読み込み結果を使い回していた)。保存したときにヘッダを読み直し、開いているシェーダーをすべて解析し直す。
- 定義されていない関数形式のマクロ (`DECLARE_TEX(_Main);` / 引数の並びの `FACE_PARAM(face)`) で、
  「宣言する名前が必要ですが '(' がありました」などの構文エラー (HL0001) が同じ位置に何件も出ていた。
  そのマクロが定義されていないことを伝える 1 件にする。構文エラーは同じ位置で 1 件だけ報告する。
- ファイルの先頭の見えない文字 (ゼロ幅空白など) や、BOM の無い UTF-16 で保存したファイルの SL0001 で、何が書かれているかを伝える。
- `#if` / `#elif` の分岐の中の関数の本体に `#if` があると、その関数に分岐の条件が付かず (「常に」)、
  分岐ごとの同じ名前の関数を重複した宣言 (HL0314) と報告していた。マクロを書き分けた分岐にコードもあり入れ子の `#if` もある領域も、これで並べる。
- 構造体のメソッドから同じ構造体のメソッドを `this.` 無しで呼ぶ書き方と、仮引数の関数オブジェクトを呼ぶ書き方 (`template<typename F> void R(F WritePixel) { WritePixel(...); }`) を、
  同じ名前の大域の関数の呼び出しとして HL0340 / HL0341 と報告していた。
- 取り込みを解決できていないシェーダーで、見えている多重定義だけと照らして HL0340 / HL0341 を報告していた (見えていない多重定義があるかもしれない)。
- 配列の次元 (`float a[4]` と `float a[2][4]`) や型引数 (`V<float, 3>` と `V<float, 1>`) だけが違う多重定義を、重複した宣言 (HL0314) と報告していた。
- `a < b, c > (d)` を、`a` が template として宣言されていなくても型引数付きの呼び出しと読み、引数の個数を誤って報告していた (HLSL 2021)。
- 条件付きで中身を変えて定義し直したマクロの HL0002 が、条件付きの文と条件の無い文で同じ位置に 2 件出ていた。条件付きの 1 件にする。
- `#pragma once` を扱っていなかった。Unreal Engine のヘッダを何度も展開し、宣言の重複 (HL0314)・再定義 (HL0002)・循環 (HL0320) を誤って報告し、
  エンジンのシェーダー 1 件に 5 分かかっていた (BasePassPixelShader.usf が 299 秒から 2 秒、エンジンの 522 件が 2 分半で終わる)。
- 別のファイルの関数の局所変数を、開き波括弧のファイルの中の位置がたまたま同じだと、同じ範囲の重複した宣言 (HL0314) と報告していた。
  別のファイルの宣言と重なるときは、行番号にファイル名を添える (`'X' は Common.ush の 2140 行目にも宣言されています`)。
- 同じマクロの展開から作られた多重定義 (`DEFINE_FOR_ALL(DEFINE_MIN)` が作る `min2(float2x2, …)` と `min2(float3x2, …)`) を 1 つにまとめ、
  ほかの多重定義を呼ぶ正しい呼び出しを HL0340 と報告していた。
- マクロを変えて同じヘッダを何度も取り込み、同じ位置から別の名前の関数を作ると (Unreal Engine の DoubleFloat.ush)、2 つ目からの名前を HL0310 と報告していた。
- `_Static_assert(式, "文")` / `static_assert(...)`、2 進数のリテラル (`0b1010`)、大域の名前 (`::Func(...)`)、型引数を書いた型の静的メンバー
  (`TVec<float, 2>::Const(1.0)`) を構文エラー (HL0001) にしていた。どれも DXC が受け付ける (`GenericNameExpressionSyntax` を足した)。
- 左辺の型が分からない式と構造体の演算を、HL0346 と報告していた (左辺の構造体の演算子が使われるかもしれない)。
- 1 つの文で条件によって中身が変わるマクロを 2 つ以上使うと、1 つだけを複製し、もう一方は構成ごとに展開していた。
  名前と数値のように同じ位置が別の種類の式になると SL0004 になっていた。定義の組み合わせ (8 通りまで) ごとに複製する。
- マクロを書き分けた分岐にコードもある領域を並べず構成ごとに展開していた。`#undef` や入れ子の `#if` が無ければ並べ、マクロを使う文は複製する。
- 利用者が書いて取り込んだヘッダで `#ifdef _A` などで書き分けたマクロの本体やコードの誤りを報告していなかった。
  既定の構成で読み飛ばしたヘッダの分岐の名前を、名前だけで「解析されなかった場所にある名前」としていたため、`_A` の構成で見つけても宣言済みと見なしていた。
  利用者のヘッダも位置で見分け、`'missingA' は 構成：_A のとき宣言されていません` のように報告する。
- 利用者が書いたヘッダの中で条件を追えなかった箇所に SL0004 を出していなかった (Unity やパッケージのヘッダでは今までどおり出さない)。
- 利用者が書いたヘッダで書き分けたマクロ、`#undef` してから定義し直したマクロ、入れ子の `#if` で書き分けたマクロを使う文を、
  定義ごとに複製せず構成ごとに展開していた。文の種類が構成で変わる箇所は SL0004 を出して構成をまたいだ検査をしていなかった。
  `#undef` は、それまでの定義の条件を縮めるものとして扱う。
- `#if … #elif … #else` や、別々の `#ifdef` (`#ifdef _A` / `#ifdef _B` / `#if !defined(_A) && !defined(_B)`) で 3 通りに書き分けたマクロを使う文を、定義ごとに複製せず構成ごとに展開していた。
  `if (v > 0) BODY` の `BODY` が構成によってブロックにも文にもなる形は、SL0004 を出して構成をまたいだ検査をしていなかった。
  `#define` だけが並ぶ領域なら、定義が 4 通りまでは複製して 1 本の木に並べる ([条件で中身が変わるマクロ](docs/guide/conditional-compilation.md#33-条件で中身が変わるマクロ条件の巻き上げ))。
- 条件で中身が変わるマクロで文が終わり、その後ろに `#ifdef` や外側のブロックの `}` が続くと、使う文を複製できず SL0004 を出していた (`if (v > 0) BODY` の次の行が `#ifdef _C`)。
- 名前の無い `#define` / `#undef` / `#ifdef` の HL0002 が、ファイルではなく「<不明>」を指していた。
- カーネルの無い `.compute` に、HL0302 で `#pragma vertex / #pragma fragment` を求めていた (`#pragma kernel` を求める)。
- `sampler2D s = sampler_state { ... };` の `sampler_state` を、宣言されていない名前 (HL0310) と報告していた。
- `_` の無い `multi_compile UNITY_DEVICE_SUPPORTS_WAVE_32` のように常に有効なデバイスのキーワードでも、キーワードが無効な構成 (FXC) があるものとして HL0003 を報告していた。
- `UNITY_DEVICE_SUPPORTS_NATIVE_16BIT` などコンパイラを切り替えるキーワードを、条件に書いていないというだけで HL0331 と報告していた。
- `--unity-project` を付けずに解析すると、`../Lib/Lib.hlsl`・`Assets/...`・`Packages/...` の `#include` を、理由を言わずに HL0321 と報告していた。
  Unity プロジェクトとして解析するなら --unity-project を指定するよう、見つけたプロジェクトを添えて伝える (プロジェクトは推定しない)。範囲の外のファイルは、そのことを伝える。
  設定ファイルを置いたフォルダーは読んでよい範囲に入れた。
- 解析対象をフォルダーを付けずに渡すと (`shaderlyn Main.shader`)、同じフォルダーのヘッダも引けなかった。
- 読んでよい範囲の外にあるファイルを、ただ「解決できない」と報告していた。範囲の外なので読まないことと直し方を HL0321 の文に添える。
- SL1050 が、利用者のヘッダに書いた `#pragma use_dxc` をコードブロックに書いたものとして扱っていた。
- `uint64_t2(...)` / `float16_t4(...)` のような明示のビット幅の型の作成を、宣言されていない関数 (HL0310) と報告していた。
- `select` / `and` / `or` を HLSL 2021 だけの組み込み関数として扱い、DXC の 2018 などで HL0310 を報告していた (DXC 1.8 はどの版でも受け付ける)。
- HLSL 2021 だけでコンパイルするとき、`for` の初期化の変数をループの外で使えば HL0310 で報告する (以前は FXC と同じく見えるものとして扱った)。
- `Texture2D<float3>` の `GatherRed` などの型を、要素の型 (`float3`) と判定していた。成分を 4 つ集めたベクトル (`float4`) である。
  型の食い違い (HL0350) を誤って報告していた
- 構造体と同じ名前の変数 (`ConstantBuffer<Constants> Constants`) を二重宣言 (HL0314) と報告していた。DXC は受け付ける
- VS Code 拡張の構成の選択: 1 つのシンボルが 2 つの組 (`#pragma` の行、設定ファイルの `at-most-one` など) に入っていると、
  2 つの組を 1 つにまとめていた。どちらの組でも一緒にならないシンボル (`[A, B]` と `[B, C]` の `A` と `C`) を同時に選べなかった。
  組の並びが逆だと、同時に有効にならないシンボルを選べた
- 設定ファイルで、値を 1 つだけ書くキー (`environment` / `profile` / `version` / ルールの `severity` など) に並びやキーと値の対を書くと、
  何も伝えずに無視していた。TOOL0003 で報告する
- HLSL 2021 の構造体の継承 (`struct D : B`) で、基底のメンバー (`d.x`) を無いメンバー (HL0312) と報告していた。
  波括弧の初期化 (`D d = { ... }`) も基底のフィールドを数えずに HL0352 と報告していた。基底は `StructDeclarationSyntax.BaseType` / `BaseTypeName`
- `class` / `interface` を HLSL 2018 (Unity を含む) で HL0003 と報告していた。FXC でも DXC のどの版でも使える (Unity でも使える)
- `enum` を DXC の HLSL 2018 (`hlsl: version: 2018`、Unreal の既定) で HL0003 と報告していた。DXC は 2017 から使える
- 構造体の前方宣言 (`struct Light;`、HLSL 2021 の `class ObjModelLoader;`) を構文の誤り (HL0001) と報告していた
- 構造体の宣言に続けて宣言した変数 (`struct Light { ... } g_light;`) を、宣言されていない名前 (HL0310) と報告していた
- `--env` が、Unity 以外の環境でも `Packages/<名前>/... は解決できません` と書いていた
- 山括弧の中にカンマのある型への型変換 (`(vector<float, 3>) x`) を読めず、構文の誤り (HL0001) と報告していた

## [0.1.1] - 2026-10-04

0.1.0 では見逃していた誤りを報告するようになったため、同じコードでも指摘が増えることがある。
CI で `--error-on` を使っている場合は、上げる前に手元で確かめること。

### 追加

- [HL0332](docs/rules/HL0332.md): どの構成でも成り立たない条件 (`#if _DETAIL == 2` など) を報告する
- `--inspect` に「シンボルの扱い」タブ。シンボルごとに、両方の分岐を並べたか構成ごとに展開したかと、その原因の `#if` を示す

### 変更

- 文の途中で分かれる `#if` と、条件で中身が変わるマクロを使う文を、分岐・定義ごとに文を複製して検査する。
  構成ごとの展開が減り、別々の `#if` が同時に有効なときだけ起きる誤り (`_A && _B` のときの [HL0312](docs/rules/HL0312.md) など) も報告する
- 報告とホバーに示す条件を、宣言の制約を使って短くする。
  `#pragma multi_compile _A _B` のもとでは `!_B` を `_A` と、`!_A && _B` を `_B` と示す
- [HL0352](docs/rules/HL0352.md): 構成ごとに要素とメンバーを数えて比べる。
  条件の付いた要素やメンバーが片側にしか無い初期化も報告し、食い違う構成をメッセージに添える
- VS Code 拡張: 補完の候補を、カーソルの位置で書けるものに絞る。
  `#` の後ろでは指令の名前、`#pragma` の後ろでは pragma の名前、`#if` / `#ifdef` ではシンボルとマクロを出し、
  コメント・文字列の中と ShaderLab の部分では HLSL の候補を出さない

### 修正

- 条件で中身が変わるマクロ (`#ifdef _A` / `#define CTYPE float3` / `#else` / `#define CTYPE float4`) を使った宣言で、
  ホバーが片方の構成の型しか示さなかった

## [0.1.0] - 2026-10-02

最初の公開バージョン。

### 追加

- Unity の ShaderLab、埋め込み HLSL、HLSL 単体のファイル (`.compute` / `.hlsl` / `.cginc` / `.hlslinc`) の静的解析
- 52 のルール。ShaderLab、Properties と HLSL の対応、HLSL、URP 固有の検査がある ([ルール一覧](docs/rules/README.md))
- `#ifdef` で分かれるコードは、どちらの分岐も検査する ([条件付きコンパイルの扱い](docs/guide/conditional-compilation.md))
- 利用者が書いて取り込んだヘッダ (共通の `.hlsl` など) も、取り込むシェーダーの文脈で検査する。
  Unity と外部パッケージのヘッダは対象にしない
- 出力形式 `text` / `json` / `sarif` / `github`
- GitHub Action。PR へのインライン注釈と Code Scanning へのアップロード ([CI に組み込む](docs/guide/ci-setup.md))
- 設定ファイル `.shaderlyn.yaml` による重要度の変更、無効化、ルールのオプション ([設定](docs/guide/configuration.md))
- 抑制コメント、ベースライン (`--baseline` / `--write-baseline`)、実行をまたぐキャッシュ (`--cache`)
- 調べるための出力: 解析の中身を HTML に出す `--inspect`、解析が使う環境を表示する `--env`、段ごとの所要時間を出す `--timings`
- VS Code 拡張。言語サーバーを同梱し、VS Code Marketplace で公開する (vsix もリリースに添付する)。
  指摘、ホバー、補完、定義へ移動、キーワードの構成を選んだ表示 ([VS Code 拡張](editors/vscode/README.md))
- C# で自作ルールを書くための NuGet パッケージ (`Shaderlyn.Semantics` / `Shaderlyn.Testing` / `Shaderlyn.Cli` / `Shaderlyn.LanguageServer`) と、
  写して使えるルールの実例集 ([自作ルールの作り方](docs/custom-rules/tutorial.md))
- Native AOT の単一バイナリ (5 プラットフォーム)

[未リリース]: https://github.com/OmojiP/shaderlyn/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/OmojiP/shaderlyn/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/OmojiP/shaderlyn/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/OmojiP/shaderlyn/releases/tag/v0.1.0
