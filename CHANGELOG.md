# 変更履歴

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に従い、
バージョンの付け方は [セマンティックバージョニング](https://semver.org/lang/ja/) に従う。
CLI、VS Code 拡張、NuGet パッケージは同じタグから同じバージョンで出す。
0.x の間は、マイナーバージョンを上げるときに公開 API や CLI のオプションを変えることがある。
そうした変更は「変更」「削除」の項目の先頭に **破壊的変更:** と書いて示す。

利用者から見て何が変わるかを書く。実装の変更は、それが挙動か API を変える場合にだけ載せる。

## [未リリース]

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
  ビルドのときに生成される取り込み (`/Engine/Generated/…`) は誤りにせず、宣言が無いことを根拠にする検査を止めて SL0002 で伝える
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
  多重定義できない演算子の定義 (`operator++`、`operator+=`、`operator<<`) を報告する
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

[未リリース]: https://github.com/OmojiP/shaderlyn/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/OmojiP/shaderlyn/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/OmojiP/shaderlyn/releases/tag/v0.1.0
