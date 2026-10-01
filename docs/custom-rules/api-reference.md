# ルール public API リファレンス

## すべてのアナライザが守ること

- 報告するルールを `SupportedDiagnostics` に載せてください。漏れると [TOOL0005](../rules/TOOL0005.md) が出ます
- メッセージの穴の数と、渡す引数の数を合わせてください。合わないと [TOOL0006](../rules/TOOL0006.md) が出ます
- アナライザに状態を持たせないでください。インスタンスは全ファイルで共有され、複数のファイルが並列に解析されます。
  ファイル単位の状態は [`RegisterAnalysisStartAction`](#analysiscontext) で作ります

## 目次

- [アナライザ](#アナライザ)
- [アクションを登録する](#アクションを登録する)
- [アクションが受け取る値](#アクションが受け取る値)
- [診断](#診断)
- [構文木](#構文木)
  - [ShaderLab のノード型](#shaderlab-のノード型)
  - [HLSL のノード型](#hlsl-のノード型)
  - [構文の上の事実を引く](#構文の上の事実を引く)
- [セマンティックモデル](#セマンティックモデル)
- [テスト](#テスト)

### 型の索引

ノード型 (`PassSyntax` など) は [ShaderLab のノード型](#shaderlab-のノード型) と [HLSL のノード型](#hlsl-のノード型) の表にあります。

| 型・メンバー | 節 |
| --- | --- |
| [`AnalysisContext`](#analysiscontext) | アクションを登録する |
| [`AnalysisStartContext`](#analysisstartcontext) | アクションを登録する |
| [`AnalysisTarget`](#analysistarget) | セマンティックモデル |
| [`AnalyzedProgram`](#analyzedprogram) | セマンティックモデル |
| [`AnalyzerOptions`](#analyzeroptions) | セマンティックモデル |
| [`CallCompatibility`](#callcompatibility-shaderlynsemanticsprograms) | セマンティックモデル |
| [`ConditionMap`](#conditionmap-shaderlynsemanticsconditional) | セマンティックモデル |
| [`Diagnostic`](#diagnostic) | 診断 |
| [`DiagnosticAnalyzer`](#diagnosticanalyzer-shaderlyncoreanalysis) | アナライザ |
| [`DiagnosticDescriptor`](#diagnosticdescriptor-shaderlyncorediagnostics) | 診断 |
| [`DiagnosticSeverity`](#diagnosticseverity) | 診断 |
| [`HlslAnalysisContext`](#hlslanalysiscontext) | アクションを登録する |
| [`HlslIntrinsics`](#hlslintrinsics--組み込み関数を知る) | 構文木 |
| [`HlslLiteral`](#hlslliteral--リテラルの値と型) | 構文木 |
| [`HlslRuleAnalyzer`](#hlslruleanalyzer-shaderlynsemanticsanalysis) | アナライザ |
| [`HlslSyntaxFacts`](#hlslsyntaxfacts--構文の規則) | 構文木 |
| [`HlslTypeClassifier`](#hlsltypeclassifier--型名を読み解く) | 構文木 |
| [`Location`](#location-と-textspan) | 診断 |
| [`ResolveCall`](#resolvecall) | セマンティックモデル |
| [`ResolveName`](#resolvename) | セマンティックモデル |
| [`ShaderCompilation`](#shadercompilation-shaderlynsemantics) | セマンティックモデル |
| [`ShaderRuleVerifier`](#shaderruleverifier-shaderlyntesting) | テスト |
| [`SymbolCondition`](#symbolcondition-shaderlyncoresyntax) | セマンティックモデル |
| [`TextSpan`](#location-と-textspan) | 診断 |

---

## アナライザ

### `DiagnosticAnalyzer` (`Shaderlyn.Core.Analysis`)

すべてのルールの基底クラスです。

| メンバー                                                             |                                                 |
| -------------------------------------------------------------------- | ----------------------------------------------- |
| `abstract ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics` | 報告しうるルールの一覧                          |
| `abstract void Initialize(AnalysisContext context)`                  | 関心の登録。ドライバの生成時に 1 回だけ呼ばれる |

`Initialize` で登録したアクションが、以降すべてのファイルに適用されます。

### `HlslRuleAnalyzer` (`Shaderlyn.Semantics.Analysis`)

埋め込み HLSL のノードを見るアナライザの基底クラスです。

| メンバー                                                    |                           |
| ----------------------------------------------------------- | ------------------------- |
| `abstract void InitializeHlsl(HlslAnalysisContext context)` | HLSL ノードへの関心の登録 |
| `sealed override void Initialize(...)`                      | 上書き不可                |

この基底クラスが引き受けることは次のとおりです。

- セマンティックモデルが無い場合は何も報告しない
- 利用者が書いたコードだけを歩きます。そのシェーダーと、利用者が書いて取り込んだヘッダ (共通の `.hlsl` など) です。
  Unity や外部パッケージのヘッダの中身は歩きません ([報告してよいノードか](#報告してよいノードか))
- **既定の構成の木に加えて、シンボルを有効にした構成 (バリアント) の木も歩きます。**
  ノードは、それが属する木の文脈で渡ります (`c.Program`)。
  形は同じでも構成によって型が変わるコード (マクロの中身を `#ifdef` で切り替えている場合など) も、
  その構成の型で検査できます
- 同じ位置のコードは、`Pass` と構成の数だけ渡ります (`HLSLINCLUDE` のコードは各 `Pass` の木に現れる)。
  **同じルール・同じ範囲・同じ引数の報告は 1 度にまとめます。** 文面が変わる報告はそれぞれ残ります。
  渡ってきた回数を数えて集計する場合は、重複を自分で除いてください

そのノードが存在する条件は `c.Condition` で分かります (その木を解析した構成も掛け合わせてあります)。

---

## アクションを登録する

### `AnalysisContext`

名前空間は `Shaderlyn.Core.Analysis` です。`Initialize` の引数です。

| メソッド                                                        |                          |
| --------------------------------------------------------------- | ------------------------ |
| `RegisterNodeAction<TNode>(Action<NodeAnalysisContext<TNode>>)` | ノード型ごとのアクション |
| `RegisterSyntaxTreeAction(Action<SyntaxTreeAnalysisContext>)`   | ファイル全体に 1 回      |
| `RegisterAnalysisStartAction(Action<AnalysisStartContext>)`     | ファイル単位の状態を作る |

`TNode` には [ShaderLab のノード型](#shaderlab-のノード型)を指定します。
基底型を指定すると、その派生型すべてで発火します。

アクションを何個登録しても、構文木の走査は 1 回のままです。

### `HlslAnalysisContext`

名前空間は `Shaderlyn.Semantics.Analysis` です。`InitializeHlsl` の引数です。

| メソッド                                                            |                          |
| ------------------------------------------------------------------- | ------------------------ |
| `RegisterNodeAction<TNode>(Action<HlslNodeAnalysisContext<TNode>>)` | ノード型ごとのアクション |

`TNode` には [HLSL のノード型](#hlsl-のノード型)を指定します。

### `AnalysisStartContext`

ファイル 1 つ分のスコープです。ここで作った変数は、そのファイルの解析中だけ生きています。

| メンバー                                                |                                                    |
| ------------------------------------------------------- | -------------------------------------------------- |
| `Unit` / `Options` / `CancellationToken`                | 解析中のファイルと設定                             |
| `RegisterNodeAction<TNode>(...)`                        | このファイルにのみ有効なノードアクション           |
| `RegisterAnalysisEndAction(Action<AnalysisEndContext>)` | 走査完了後に呼ばれる。集めた情報で報告するのはここ |

---

## アクションが受け取る値

登録したアクションには、対象のノードと、報告するためのメソッドを持つ値が渡ります。
型はアクションの種類ごとに違いますが (`NodeAnalysisContext<TNode>` /
`SyntaxTreeAnalysisContext` / `AnalysisEndContext` / `HlslNodeAnalysisContext<TNode>`)、
次のメンバーは共通です。

| メンバー                                      |                                                       |
| --------------------------------------------- | ----------------------------------------------------- |
| `Node`                                        | 検査対象のノード (ノードアクションのみ)               |
| `Unit`                                        | 解析中のファイル。[`AnalysisTarget`](#analysistarget) |
| `Options`                                     | 実行時設定。[`AnalyzerOptions`](#analyzeroptions)     |
| `CancellationToken`                           | キャンセル用トークン                                  |
| `ReportDiagnostic(descriptor, args...)`       | ノード全体を指して報告 (ノードアクションのみ)         |
| `ReportDiagnostic(descriptor, span, args...)` | 範囲を指定して報告                                    |
| `ReportDiagnostic(diagnostic)`                | 作り込んだ診断をそのまま報告                          |

`HlslNodeAnalysisContext<TNode>` はこれに加えて次を持ちます。

| メンバー      |                                                                                                                    |
| ------------- | ------------------------------------------------------------------------------------------------------------------ |
| `Compilation` | 対象シェーダーのセマンティックモデル。[`ShaderCompilation`](#shadercompilation-shaderlynsemantics)。`null` にはならない |
| `Program`     | そのノードが属するコードブロック。[`AnalyzedProgram`](#analyzedprogram)。既定の構成の木か、バリアントの木のどちらか。型を求めるときは `Compilation.GetExpressionTypeBinder(Program)` を使う |
| `Condition`   | そのノードが存在する条件 (`SymbolCondition`)。`Compilation.GetEffectiveCondition(Node, Program)` と同じ。`IsUnknown` なら報告の根拠にしない |

---

## 診断

### `DiagnosticDescriptor` (`Shaderlyn.Core.Diagnostics`)

```csharp
new DiagnosticDescriptor(
    id: "MY0001",
    title: "短い表題",
    messageFormat: "'{0}' は使えません。",
    category: "Usage",
    defaultSeverity: DiagnosticSeverity.Warning,
    description: "なぜ駄目で、どう直すか。",
    helpLinkUri: "https://example.com/MY0001");
```

| 引数              |                                                     |
| ----------------- | --------------------------------------------------- |
| `id`              | 英大文字の接頭辞 + 4 桁。この書式でないと例外になる |
| `title`           | 一覧表示に使う短い説明                              |
| `messageFormat`   | `string.Format` の書式                              |
| `category`        | `Correctness` / `Usage` / `Naming` など             |
| `defaultSeverity` | `Info` / `Warning` / `Error`。`None` は指定できない |
| `description`     | 指摘を受けた人が読む説明                            |
| `helpLinkUri`     | 詳細ページの URL                                    |

| プロパティ                     |                                           |
| ------------------------------ | ----------------------------------------- |
| `IdPrefix`                     | ID の英字部分                             |
| `RequiredMessageArgumentCount` | 書式が必要とする引数の数。`{{` は数えない |

### `DiagnosticSeverity`

値は `None` / `Info` / `Warning` / `Error` です。
`None` は「無効」を表し、設定ファイルでの無効化に使います。

### `Diagnostic`

| メンバー                                      |                                                       |
| --------------------------------------------- | ----------------------------------------------------- |
| `Id` / `Descriptor` / `Severity` / `Location` | 基本                                                  |
| `GetMessage()`                                | 引数を埋めた最終的なメッセージ                        |
| `MessageArgumentCount`                        | 渡された引数の数                                      |
| `WithSuggestedReplacement(text)`              | 置き換え候補を添える。エディタが 1 回の操作で適用する |
| `WithSeverity(...)` / `WithLocation(...)`     | 差し替えた複製を返す                                  |

`Diagnostic.Create(descriptor, location, args...)` でも作れます。

### `Location` と `TextSpan`

`Location.Create(sourceText, span)` で作ります。
`Location.LineSpan` で行と桁が引けます。

`TextSpan.Contains(position)` は終端位置を含まず、`TextSpan.IntersectsWith(position)` は含みます。
エディタのカーソルのように、語の直後の位置も「語の上」と扱いたいときは `IntersectsWith` を使います。

---

## 構文木

### 共通 (`Shaderlyn.Core.Syntax`)

`SyntaxNode` に共通のメンバーです。

| メンバー                   |                         |
| -------------------------- | ----------------------- |
| `Span`                     | ファイル上の範囲        |
| `Parent`                   | 親ノード。根では `null` |
| `ChildNodes()`             | 子ノード                |
| `DescendantNodesAndSelf()` | 自分を含む子孫すべて    |

HLSL のノードは加えて `Source` を持ち、そのノードがどのファイル由来かを返します。
取り込んだヘッダのノードでは、そのヘッダのテキストになります。

どの型のノードがどこに現れるかは、`--inspect` で構文木を見ると確かめられます。

```bash
shaderlyn Assets/Shaders/Lit.shader --unity-project . --inspect tree.html
```

### ShaderLab のノード型

名前空間は `Shaderlyn.ShaderLab.Syntax` です。すべて `ShaderLabSyntaxNode` の派生型です。

`ShaderLabSyntaxNode` の直下:

| 型                               | 何を指すか                           |
| -------------------------------- | ------------------------------------ |
| `ShaderLabCompilationUnitSyntax` | ファイルの根                         |
| `ShaderDeclarationSyntax`        | `Shader "名前" { ... }`              |
| `BlockSyntax`                    | `{ ... }`                            |
| `PropertyDeclarationSyntax`      | `Properties` の中の 1 件             |
| `PropertyTypeSyntax`             | プロパティの型。`2D` / `Range(0, 1)` |
| `PropertyAttributeSyntax`        | プロパティに付く `[HDR]` などの属性  |
| `TagSyntax`                      | `"RenderType" = "Opaque"` の 1 件    |
| `ShaderLabStatementSyntax`       | ブロックの中に並ぶものの基底 (抽象)  |
| `CommandArgumentSyntax`          | 命令の引数の基底 (抽象)              |
| `PropertyDefaultValueSyntax`     | プロパティの既定値の基底 (抽象)      |

`ShaderLabStatementSyntax` の派生:

| 型                      | 何を指すか                                             |
| ----------------------- | ------------------------------------------------------ |
| `SubShaderSyntax`       | `SubShader { ... }`                                    |
| `PassSyntax`            | `Pass { ... }`                                         |
| `CategorySyntax`        | `Category { ... }`                                     |
| `PropertiesBlockSyntax` | `Properties { ... }`                                   |
| `TagsBlockSyntax`       | `Tags { ... }`                                         |
| `StencilBlockSyntax`    | `Stencil { ... }`                                      |
| `CommandSyntax`         | `Cull Back` のような命令                               |
| `GrabPassSyntax`        | `GrabPass { ... }`                                     |
| `ProgramBlockSyntax`    | `HLSLPROGRAM` / `CGPROGRAM` / `HLSLINCLUDE` のブロック |
| `SkippedTokensSyntax`   | 構文解析が読み飛ばしたトークン                         |

`CommandArgumentSyntax` の派生:

| 型                                | 何を指すか                 |
| --------------------------------- | -------------------------- |
| `LiteralArgumentSyntax`           | そのまま書かれた値。`Back` |
| `PropertyReferenceArgumentSyntax` | プロパティ参照。`[_Cull]`  |
| `ArgumentSeparatorSyntax`         | 引数の区切り               |

`PropertyDefaultValueSyntax` の派生:

| 型                          | 何を指すか               |
| --------------------------- | ------------------------ |
| `ScalarDefaultValueSyntax`  | 数値。`0.5`              |
| `VectorDefaultValueSyntax`  | ベクトル。`(1, 1, 1, 1)` |
| `TextureDefaultValueSyntax` | テクスチャ。`"white" {}` |

### HLSL のノード型

名前空間は `Shaderlyn.Hlsl.Syntax` です。すべて `HlslSyntaxNode` の派生型です。

`HlslSyntaxNode` の直下:

| 型                          | 何を指すか                                                |
| --------------------------- | --------------------------------------------------------- |
| `HlslCompilationUnitSyntax` | コードブロックの根                                        |
| `HlslTypeSyntax`            | 型の記述。`Name` / `ModifierTokens` / `TemplateArguments` |
| `ParameterSyntax`           | 仮引数                                                    |
| `VariableDeclaratorSyntax`  | 宣言された変数 1 つ分の名前と初期化子                     |
| `SemanticSyntax`            | `: SV_Target` のセマンティクス                            |
| `HlslAttributeSyntax`       | `[loop]` `[branch]` などの属性                            |
| `HlslDeclarationSyntax`     | 宣言の基底 (抽象)                                         |
| `HlslExpressionSyntax`      | 式の基底 (抽象)                                           |
| `HlslStatementSyntax`       | 文の基底 (抽象)                                           |

`HlslDeclarationSyntax` の派生:

| 型                                | 何を指すか                                                           |
| --------------------------------- | -------------------------------------------------------------------- |
| `FunctionDeclarationSyntax`       | 関数。`Name` / `ReturnType` / `Parameters` / `Body` / `IsDefinition` |
| `VariableDeclarationSyntax`       | 変数・uniform。`Type` / `Variables`                                  |
| `StructDeclarationSyntax`         | 構造体                                                               |
| `ConstantBufferDeclarationSyntax` | `cbuffer`。`CBUFFER_START` の展開結果もこの形                        |
| `NamespaceDeclarationSyntax`      | `namespace X { ... }`                                                |
| `TypedefDeclarationSyntax`        | `typedef`                                                            |
| `IncompleteDeclarationSyntax`     | 宣言として解析しきれなかったトークン                                 |

`HlslExpressionSyntax` の派生:

| 型                                | 何を指すか                         |
| --------------------------------- | ---------------------------------- |
| `InvocationExpressionSyntax`      | 呼び出し。`Target` / `Arguments`   |
| `IdentifierExpressionSyntax`      | 名前の参照。`Name`                 |
| `MemberAccessExpressionSyntax`    | `a.b`                              |
| `ElementAccessExpressionSyntax`   | `a[i]`                             |
| `LiteralExpressionSyntax`         | リテラル                           |
| `BinaryExpressionSyntax`          | `a + b`                            |
| `AssignmentExpressionSyntax`      | `a = b` / `a += b`                 |
| `PrefixUnaryExpressionSyntax`     | `-a` / `++a`                       |
| `PostfixUnaryExpressionSyntax`    | `a++`                              |
| `ConditionalExpressionSyntax`     | `a ? b : c`                        |
| `CastExpressionSyntax`            | `(float)a`                         |
| `ParenthesizedExpressionSyntax`   | `( a )`                            |
| `InitializerListExpressionSyntax` | `{ 1, 2 }`                         |
| `IncompleteExpressionSyntax`      | 式として解析しきれなかったトークン |

`HlslStatementSyntax` の派生:

| 型                                | 何を指すか                         |
| --------------------------------- | ---------------------------------- |
| `BlockStatementSyntax`            | `{ ... }`                          |
| `ExpressionStatementSyntax`       | 式を評価する文                     |
| `LocalDeclarationStatementSyntax` | 局所変数の宣言                     |
| `IfStatementSyntax`               | `if`                               |
| `ForStatementSyntax`              | `for`                              |
| `WhileStatementSyntax`            | `while`                            |
| `DoWhileStatementSyntax`          | `do ... while`                     |
| `SwitchStatementSyntax`           | `switch`                           |
| `SwitchLabelStatementSyntax`      | `case` / `default`                 |
| `ReturnStatementSyntax`           | `return`                           |
| `JumpStatementSyntax`             | `break` / `continue` / `discard`   |
| `EmptyStatementSyntax`            | `;` だけの文                       |
| `IncompleteStatementSyntax`       | 文として解析しきれなかったトークン |

### 構文の上の事実を引く

#### `HlslLiteral` — リテラルの値と型

名前空間は `Shaderlyn.Hlsl.Syntax` です。16 進・8 進・接尾辞を読み分けます。

| メンバー                         |                                                           |
| -------------------------------- | --------------------------------------------------------- |
| `GetTypeName(token)`             | `int` / `uint` / `float` / `half` / `double`              |
| `TryGetInt64(token, out value)`  | 整数としての値                                            |
| `TryGetDouble(token, out value)` | 数としての値。閾値との比較はこちら                        |
| `GetKind(token)`                 | `Decimal` / `Hexadecimal` / `Octal` / `Floating` / `None` |
| `GetDigits(token)`               | 接尾辞を除いた表記                                        |

```csharp
if (HlslLiteral.TryGetInt64(literal.Token, out long index)) { ... }
```

#### `HlslSyntaxFacts` — 構文の規則

名前空間は `Shaderlyn.Hlsl.Syntax` です。

| メンバー                                                    |                                          |
| ----------------------------------------------------------- | ---------------------------------------- |
| `TryGetArrayLength(rankTokens, out length)`                 | 角括弧の並びが表す要素の総数。多次元は積 |
| `IsAssignmentOperator(kind)` / `IsCompoundAssignment(kind)` | 代入演算子かどうか                       |
| `GetCompoundAssignmentOperand(kind)`                        | `%=` が行う演算 (`%`)                    |
| `GetKeyword(statement)`                                     | その文を始めるキーワードのトークン       |

配列の長さは宣言子と仮引数から直接も引けます。

```csharp
if (variable.TryGetArrayLength(out int length)) { ... }   // VariableDeclaratorSyntax
if (parameter.TryGetArrayLength(out int length)) { ... }  // ParameterSyntax
```

#### `HlslTypeClassifier` — 型名を読み解く

名前空間は `Shaderlyn.Semantics.Symbols` です。

| メンバー                                                  |                          |
| --------------------------------------------------------- | ------------------------ |
| `Classify(typeName)`                                      | `HlslTypeClass` への分類 |
| `TryDescribeNumeric(typeName, out shape)`                 | 基底名・行・列への分解   |
| `Compose(shape)` / `ComposeNumeric(baseName, components)` | 分解の逆                 |
| `GetWiderBaseName(left, right)`                           | 広いほうの基底型         |

```csharp
// float4x4 → BaseName "float", Rows 4, Columns 4
HlslTypeClassifier.TryDescribeNumeric(type.Name, out HlslNumericShape shape);
```

`HlslConversion.IsConvertible(valueType, targetType, exact)` が、
組み込みルールと同じ判定で暗黙の変換の可否を答えます。

#### `HlslIntrinsics` — 組み込み関数を知る

名前空間は `Shaderlyn.Semantics.Symbols` です。

| メンバー                           |                                        |
| ---------------------------------- | -------------------------------------- |
| `IsKnownFunction(name)`            | 組み込み関数かどうか                   |
| `IsTextureSamplingFunction(name)`  | テクスチャを読む関数・メソッドかどうか |
| `GetTextureMethodReturnType(name)` | `Sample` などが何を返すか              |

---

## セマンティックモデル

### `AnalysisTarget`

名前空間は `Shaderlyn.Core.Analysis` です。解析中のファイル 1 つを表します。

| メンバー             |                                               |
| -------------------- | --------------------------------------------- |
| `Text`               | 対象ファイルのソーステキスト                  |
| `Root`               | 構文木の根。パースに至らなかった場合は `null` |
| `FilePath`           | 対象ファイルのパス                            |
| `GetModel<TModel>()` | 添えられたモデル。無ければ `null`             |

### `ShaderCompilation` (`Shaderlyn.Semantics`)

`GetModel<ShaderCompilation>()` で取り出せる、シェーダー 1 つ分のセマンティックモデルです。

| メンバー                           |                                             |
| ---------------------------------- | ------------------------------------------- |
| `Properties`                       | `Properties` ブロックの宣言                 |
| `Programs`                         | コードブロックごとの解析結果                |
| `SymbolVariants`                  | シンボルを 1 つ有効にした構成での解析結果 |
| `TryGetUniform(name, out _)`       | uniform を引く                              |
| `HasCompleteDependencies`          | `#include` をすべて解決できたか             |
| `AppearsOutsideAnalyzedCode(name)` | 名前が、どの構成でも解析されなかった領域かマクロ定義の本体に現れるか (シェーダー全体) |
| `AppearsOutsideAnalyzedCode(name, program)` | 同じことを、そのコードブロック (Pass・カーネル) の中だけで判定する。**Pass ごとに判定する検査はこちらを使う。** 別の Pass の読み飛ばしで、この Pass の検査を見送らない |
| `UnanalyzedIdentifiers`            | どの構成でも解析されなかった領域に現れた名前 |
| `AllDeclaredNames`                 | すべての木 (取り込んだヘッダを含む) で宣言された変数・仮引数・関数・構造体・定数バッファの名前 |
| `LocalOnlyDeclaredNames`           | そのうち、関数の中の変数と仮引数としてしか宣言されていない名前。位置によって見えたり見えなかったりするので、`ResolveName` で確かめる |
| `HeaderDeclaredNames`              | そのうち、取り込んだヘッダの宣言に現れた名前。このファイルに書いた宣言は Pass ごとに違うので、`AnalyzedProgram.BlockKey` ごとに集めること |
| `EnumerateRuleNodes()`             | **検査のために歩くノード（これを使う）。** 既定の構成と各バリアントの木にある、そのシェーダーが自分で書いたノードを、属する木とともに返す |
| `EnumerateRuleDeclarations()`      | 同じく、トップレベルの宣言を属する木とともに返す |
| `GetEffectiveCondition(node, program)` | そのノードが存在する条件。その木を解析した構成も掛け合わせる |
| `TryEnumerateConfigurations(nodes, program, out configurations)` | ノードの条件に現れるシンボルの実在する組み合わせと、それぞれで存在するノード。**数えたり合計したりするルールはこれで構成ごとに数える**（[下の節](#数えるルールは構成ごとに数える)） |
| `GetTreeConfiguration(program)` | その木が実際に表す構成のうち、木の中の出現条件では表されない部分。既定の木なら「バリアントになったシンボルはどれも無効」。**複数の木の結果を条件で突き合わせるルールは、ノードの出現条件にこれを掛ける。** 掛けないと、どの木も解析していない組み合わせについてまで結論を出す |
| `GetHeaderDeclaredNames(block)` | そのコードブロック (Pass・カーネル) が取り込んだヘッダの宣言に現れた名前。別の Pass が取り込んだヘッダの名前は含まない |
| `EnumerateOwnNodes(program)`       | 1 本の木として見せるためのノード。バリアントにしか無いノードを既定の木の子として返す（検査には使わない） |
| `IsReportable(node)`               | **そのノードを報告してよいか（これを使う）** |
| `IsReportable(token)`              | そのトークンを報告してよいか。ノードの一部だけを報告するときに使う |
| `IsUserFile(path)`                 | そのファイルが利用者のファイル (このファイルと、利用者が書いて取り込んだヘッダ) か |
| `IsWrittenHere(node)`              | そのノードが、このファイルに直接書かれているか。エディタの操作のための判定で、報告の判定には使わない |
| `IsFromOwnMacro(node)`             | そのノードが、利用者のファイルが書いたマクロの本体から来たか |
| `CodeTokens`                       | このファイルのコードの、マクロ展開前のトークン |
| `UserIncludeCodeTokens`            | 利用者が書いて取り込んだヘッダの、マクロ展開前のトークン。位置はヘッダを指す |
| `GetConditionMap()`                | ノードがどのシンボル構成で存在するか      |
| `IsStandaloneHlsl`                 | `.compute` などの単体ファイルか             |
| `ResolveCall(invocation, program)` | その呼び出しでどの関数が呼ばれるか          |
| `GetFunctionDeclarations(name)`    | その名前で宣言されている関数すべて          |
| `HasFunctionDeclarations`          | 関数の宣言が 1 つでもあるか                 |
| `GetExpressionTypeBinder(program)` | 式の型を求める `ExpressionTypeBinder`       |

#### 報告してよいノードか

**指摘を出す前に `IsReportable(node)` を確かめてください。** Unity や外部パッケージのヘッダのコードや、
それらのマクロが作ったコードを指摘しても、利用者には直しようがありません。

```csharp
if (!context.Compilation.IsReportable(node))
{
    return;
}
```

`IsReportable` は次の 2 つを通します。「利用者のファイル」は、このファイルと、利用者が書いて取り込んだヘッダ (共通の `.hlsl` など) です (`IsUserFile`)。
CLI と言語サーバーでは、Unity と外部パッケージのヘッダ (プロジェクトの `Library` の下と Unity Editor に同梱のもの) 以外が利用者のファイルです。

| 通すもの | 報告の位置 |
| --- | --- |
| 利用者のファイルに直接書かれたコード | そのコードの位置。ヘッダのコードならヘッダを指す |
| 利用者のファイルが書いたマクロの本体から来たコード（`IsFromOwnMacro`） | **マクロの本体の位置** |

1 つ目には、次のものも入ります。

- **マクロの実引数に書いたコード。** `SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv)` の `uv` や
  `HEADER_ID(uv.z)` の `uv.z` は、利用者が呼び出し位置に書いたものです。報告は実引数を書いた位置に出ます
  (`HlslSyntaxToken.MacroArgumentSpan`)
- **先頭だけがヘッダのマクロのノード。** `fixed4 frag(v2f i) : SV_Target { ... }` の `fixed4` は `HLSLSupport.cginc` のマクロですが、
  `frag` 以降は利用者が書いたコードです

ヘッダのマクロの本体が作った部分は入りません。実引数を包んでいても同じです。
`#define HEADER_ID(x) (x)` の括弧や、`UNITY_SETUP_INSTANCE_ID(v)` が作る `v.instanceID` の `.instanceID` がそうです。
ノードの一部だけを報告するルールは、報告する部分 (メンバー名のトークンなど) についても `IsReportable(token)` を確かめてください。

**利用者のヘッダのコードは、取り込むシェーダーの文脈で渡ってきます。**
`HlslRuleAnalyzer` の `ReportDiagnostic(descriptor, span, ...)` は範囲をノードのファイルに付けるので、ヘッダのノードならヘッダの位置に出ます。
`DiagnosticAnalyzer` を直接継承したルールで範囲だけを渡すと、解析しているファイルの位置になります。
ヘッダのノードを報告するときは `Diagnostic.Create(descriptor, node.GetLocation(), ...)` で位置ごと渡してください。
`IsWrittenHere` はエディタの操作 (ホバーや定義へ移動) のための判定で、利用者のヘッダを含みません。報告の判定には使わないでください。

2 つ目は `node.GetLocation()` が自動でマクロの本体を指すので、ルール側で何かする必要はありません。
同じマクロを何度使っても、報告は本体の 1 か所にまとまります。

```hlsl
#define HELPER_DECL half4 helper() { return undeclared; }
                                  // ^^^^^^^^^^ ここに報告される
HELPER_DECL
```

実引数として渡したトークンには本体の印を付けません。そちらは呼び出し位置に書かれたものだからです。

#### 検査のために歩く

**検査のためにノードを歩くなら `EnumerateRuleNodes()` を使ってください。**
既定の構成の木とバリアントの木を、それぞれの文脈で歩きます。組み込みルールもこの形です。

```csharp
HashSet<(string FilePath, int Start)> reported = [];

foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
{
    if (node is not InvocationExpressionSyntax invocation || !compilation.IsReportable(invocation))
    {
        continue;
    }

    // 型はそのノードが属する木から求める。構成によって型が変わるコードも、その構成の型になる。
    ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

    // 条件にはその木を解析した構成を掛け合わせてある。
    SymbolCondition present = compilation.GetEffectiveCondition(invocation, program);

    // 同じ位置は Pass と構成の数だけ現れるので、報告はファイルと位置で 1 度にまとめる。
    // 利用者のヘッダのコードも歩くので、位置だけでは別のファイルの同じ位置とぶつかる。
    ...
}
```

並べた分岐のうち、その木では有効にしていないシンボルを要するものは、
そのシンボルを有効にしたバリアントの木があれば、そちらでだけ渡ります。
既定の木の宣言は既定の構成のマクロで展開してあるので、
`#if !defined(ENABLE_ALPHA)` で書き分けた `CTYPE color` は既定の木では `float3` です。
`#ifdef ENABLE_ALPHA` の中の `color.w` は、`ENABLE_ALPHA` を有効にした木で `float4` として検査されます。

`EnumerateOwnNodes(program)` は、バリアントにしか無いノードを既定の木の子として返します。
そのノードの属さない構成の文脈で型を求めることになるので、検査には使いません。

**バリアントの木の中で、条件が付いていないことは「どの構成でも」を意味しません。**
そのシンボルを有効にした構成の中での話です。
`#pragma multi_compile_fragment _ _A _B` のように同時に有効にならないシンボルがあると、
`_A` を有効にした木では `#ifndef _B` の領域が条件無しで現れます。
`GetEffectiveCondition` はこれを踏まえて、その木を解析した構成 (`AnalyzedCondition`) を掛け合わせます。

#### 数えるルールは構成ごとに数える

両方の分岐を並べた木には、同時には存在しないノードが並んでいます。
`#ifdef _A` と `#else` にサンプリングを 1 回ずつ書けば木には 2 回ありますが、どの構成でも 1 回です。
**ノードを数えたり合計したりするルール（回数・文の数・大きさの上限など）は、木のノードをそのまま数えないでください。**
`TryEnumerateConfigurations` で構成ごとに存在するノードを絞り、その最大を上限と比べます。

```csharp
List<HlslStatementSyntax> statements = [.. body.DescendantNodesAndSelf().OfType<HlslStatementSyntax>()];

// 条件が分からないノードがある、シンボルが多すぎる (MaxConfigurationSymbols を超える) なら数えない
if (!compilation.TryEnumerateConfigurations(statements, program, out var configurations) || configurations.IsEmpty)
{
    return;
}

int count = configurations.Max(configuration => statements.Count(configuration.Contains));
```

実在しない組み合わせ（同じ `#pragma` 行のシンボルを同時に有効にするなど）は含まれません。
クックブックの `SampleBudgetAnalyzer`・`FunctionLengthLimitAnalyzer`・`ConstantBufferSizeLimitAnalyzer` がこの形です。

#### 構成ごとの結論を出すときの注意

「ある構成で宣言が無い」「ある構成で return しない」のように、**構成を名指しして報告するルール**は次の 2 点に気を付けてください。

- **ノードの出現条件は、既定の木を基準にしたものです。** シンボルを有効にしたバリアントの木 (シンボルをまとめて有効にした木を含む) の中では、
  その木に実際にあるノードにも合わない条件が付くことがあります。構成ごとの判定は既定の木で行い、
  バリアントの木はその木を 1 つの構成として扱ってください (組み込みの HL0310 / HL0340〜0342 / HL0360 がこの形です)
- **その木が表す構成を掛けてください** (`GetTreeConfiguration(program)`)。既定の木の `AnalyzedCondition` は「常に」ですが、
  実際にはバリアントになったシンボルをどれも無効にした構成です。掛けないと、どの木も解析していない組み合わせについてまで報告します

| `AnalyzedProgram` のメンバー |                                  |
| --------------------------- | -------------------------------- |
| `EnabledSymbols`            | その木で有効にしたシンボル       |
| `AnalyzedCondition`         | それを条件として表したもの。既定の構成なら「常に」 |
| `BlockKey`                  | 別々に組み立てられるコード (Pass、コンピュートシェーダーのカーネル) を見分ける鍵。既定の構成とそのバリアントは同じ鍵を持つ。宣言を集めて突き合わせるなら、この鍵ごとに分ける |

式の型は `GetExpressionTypeBinder(program)` が返すものから求めます。
`ExpressionTypeBinder` を自分で `new` した場合、宣言と構造体のフィールドを毎回集め直します。

| `ExpressionTypeBinder` のメンバー |                                                                   |
| --------------------------------- | ----------------------------------------------------------------- |
| `GetEvaluatorFor(node)`           | そのノードを囲む関数の中で式の型を求める `ExpressionTypeEvaluator` |
| `CountComponents(expression)`     | 式の数値の成分の個数 (`float3` なら 3)。型が分からなければ `null` |
| `ResolveName(identifier)`         | 識別子がその位置で指す宣言 (下の「`ResolveName`」)                 |

名前は、使っている位置から波括弧ごとに外側へたどって引きます。
同じ関数の中でも、内側の波括弧で宣言し直した名前はその中と外で別の変数として扱われます。
関数の外では uniform を引き、無ければ `static` / `groupshared` の変数を引きます。

### `ResolveName`

識別子がその位置で指す変数の宣言を引きます。型を求める仕組みと同じ解決です。
名前だけで宣言を探すと、別の関数の同じ名前の局所変数と取り違えます。

```csharp
if (c.Node is IdentifierExpressionSyntax identifier)
{
    NameResolution resolution = c.Compilation.GetExpressionTypeBinder(c.Program).ResolveName(identifier);

    if (resolution.Kind == DeclaredNameKind.Global && resolution.TryGetArrayLength(out int length))
    {
        // 関数の外の配列。候補がどれも同じ長さだった
    }
}
```

| `NameResolution.Kind` |                                                             |
| --------------------- | ----------------------------------------------------------- |
| `Local`               | 関数の中の変数か仮引数                                      |
| `Global`              | 関数の外の変数 (uniform、`static` / `groupshared`、定数バッファのメンバー) |
| `NotFound`            | どちらにも宣言が無い。別の構成でだけ宣言されている名前もこれになる |
| `Undecidable`         | 決められない。報告の根拠にしない                            |

`Candidates` は指しうる宣言 (`DeclaredName`) の並びで、条件によって別の宣言を指すなら複数になります。
`DeclaredName` は宣言子 (`Declarator`)、型 (`Type`)、その位置から見える条件 (`Condition`) を持ちます。
次の場合は `Undecidable` になります。

- 関数の中に、波括弧の無い `if (x) float a;` のような範囲の決まらない宣言がある
- 前の文の中で、波括弧を挟まずに入れ子になった `for` の初期化が同じ名前を宣言している
  (`for (...) for (int j = 0; ...) ...;` の後の `j`)。fxc が内部エラーになる形です

`for` の初期化の変数は、囲む波括弧の中ではループの後でも見えます (Unity で通る読み方)。

### `ResolveCall`

その呼び出しでどの関数が呼ばれるかを引きます。
多重定義された関数では、名前だけでは呼ばれる宣言が決まりません。
`ResolveCall` は出現条件で候補を絞り、実引数の型で 1 つを選びます。

```csharp
OverloadResolution resolution = c.Compilation.ResolveCall(c.Node, c.Program);

if (resolution.IsResolved)
{
    // resolution.Declaration     … 呼ばれる関数の宣言
    // resolution.ParameterTypes  … その仮引数の型名
}
```

| メンバー         |                                           |
| ---------------- | ----------------------------------------- |
| `Status`         | どこまで解決できたか (下表)               |
| `Declaration`    | 呼ばれる関数の宣言。決まらなければ `null` |
| `ParameterTypes` | 解決先の仮引数の型名                      |
| `Candidates`     | 名前と出現条件で絞った候補                |
| `ArgumentTypes`  | 実引数の型。分からなかったものは `null`   |
| `IsResolved`     | 1 つに決まったかどうか                    |

| `Status`                | 意味                                   |
| ----------------------- | -------------------------------------- |
| `Resolved`              | 宣言が 1 つに決まった                  |
| `Intrinsic`             | 組み込み関数だった。宣言は無い         |
| `NotDeclared`           | その構成に、その名前の宣言が無い       |
| `ArgumentCountMismatch` | その個数を受け付ける宣言が無い         |
| `NoMatch`               | 個数は合うが、実引数を渡せる宣言が無い |
| `Ambiguous`             | 渡せる宣言が複数あり、決め手が無い     |
| `Unknown`               | 判断できない                           |

これは HLSL のオーバーロード解決の近似です。
実際のコンパイラは変換の費用で候補を順位づけますが、ここでは
渡せる候補のうち実引数の型と厳密に一致する位置が最も多いものを選び、
並んだ場合は `Ambiguous` を返します。

`obj.Method(...)` の形は解決しません。メソッドは宣言として書かれていないためです。
テクスチャのメソッド (`Sample` など) は `Intrinsic`、それ以外は `Unknown` になります。

### `CallCompatibility` (`Shaderlyn.Semantics.Programs`)

呼び出しが宣言に合うかを、自分で 1 つずつ確かめるための判定です。
`ResolveCall` と組み込みルール (HL0340〜HL0342) も同じ判定を使っています。

| メンバー                                   |                                                              |
| ------------------------------------------ | ------------------------------------------------------------ |
| `AcceptsArgumentCount(parameters, count)`  | 実引数の個数が仮引数に収まるか。既定値のある仮引数は省ける   |
| `IsWriteback(parameter)`                   | `out` / `inout` の仮引数か                                   |
| `IsWritebackModifier(modifier)`            | 語が `out` / `inout` か                                      |
| `IsAssignable(expression)`                 | 書き戻せる式か。リテラルや演算の結果などは `false`           |
| `IsPresentWith(conditions, declaration, call)` | 呼び出しの構成にその宣言が存在しうるか。条件が分からなければ `true` |

### `ConditionMap` (`Shaderlyn.Semantics.Conditional`)

`GetConditionMap()` が返す、ノードがどのシンボル構成で存在するかの索引です。
`#ifdef _NORMALMAP` の中に書いたメンバーは、木から消えるのではなく「`_NORMALMAP` のときだけ存在する」ノードとして木に載ります。

| メンバー                       |                                                                          |
| ------------------------------ | ------------------------------------------------------------------------ |
| `IsAlwaysPresent(node)`        | どの構成でも存在するか (`GetCondition(node).IsAlways` と同じ)           |
| `GetCondition(node)`           | そのノードが存在する条件 (`SymbolCondition`)。入れ子の条件は掛け合わさる |
| `IsPossible(condition)`        | その条件が成り立つ構成があるか。同じ `#pragma` 行のシンボルは同時に有効にならないこと、`_` の無い `multi_compile` の行はどれか 1 つが必ず有効なことも考える |
| `EnumerateConditional()`       | 条件が付いているノードの一覧                                             |
| `UnmergedLocations`            | 条件を追えなかった箇所。`SL0004` として報告される                        |
| `IsComplete`                   | 条件を追えなかった箇所が 1 つも無いか                                    |

**構成によって起きる誤りは、起きる構成を添えて報告してください。** 組み込みルールはこの形で書いています。
シンボルの構成はどれもいつか通るコードなので、1 つの構成でしか起きない誤りも直すべき誤りです。
黙っていると、そのシンボルを有効にしたときだけ現れる不具合になります。

判定は条件の演算で書きます。たとえば「呼んでいる関数の定義が無い構成があるか」(HL0311) は、
「呼ぶ位置の条件 ∧ ¬定義がある条件」が成り立ちうるかです。

```csharp
SymbolCondition call = conditions.GetCondition(invocation);
SymbolCondition defined = conditions.GetCondition(definition);

if (call.IsUnknown || defined.IsUnknown)
{
    return;   // 追えなかった条件を根拠にしない
}

SymbolCondition missing = call.And(defined.Negate());

if (conditions.IsPossible(missing))
{
    // メッセージに構成を添える。ToString() は "!_NORMALMAP" のような形を返す
    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, name, missing.IsAlways ? "どの構成でも" : $"{missing} のとき"));
}
```

どの構成でも存在するものだけを相手にしたい場合 (「無いこと」を根拠にする判断で、条件付きの宣言を数えたくないときなど) は
`IsAlwaysPresent` が使えます。

### `SymbolCondition` (`Shaderlyn.Core.Syntax`)

シンボルの真偽の組み合わせで表した条件です。

| メンバー                          |                                                               |
| --------------------------------- | ------------------------------------------------------------- |
| `Always` / `Never` / `Unknown`    | 常に成り立つ / 決して成り立たない / 追えなかった              |
| `IsAlways` / `IsNever` / `IsUnknown` | 上のどれにあたるか                                         |
| `Symbol(name, isDefined)`         | シンボル 1 つの条件 (`isDefined: false` で `!name`)           |
| `And(other)` / `Or(other)` / `Negate()` | 組み合わせる                                            |
| `EnumerateSymbols()`              | 条件に出てくるシンボルの名前                                  |

「この 2 つは同時に成り立つか」は `ConditionMap.IsPossible(a.And(b))` で判定します。
`a.And(b).IsNever` では足りません。条件の代数は、`#pragma multi_compile _ _X _Y` の `_X` と `_Y` が
同時に有効にならないことを知らないため、`_X && _Y` を成り立つ条件として扱います。

**`Unknown` を見たら報告しないでください。**
条件を追えなかったものを「常に」や「決して」と取り違えると、
成り立たない構成についての指摘を出すことになります。
実例は [実例集の「コンパイル条件分岐に応じた関数制限」](cookbook.md#5-コンパイル条件分岐シンボルに応じた関数制限) にあります。

### `AnalyzedProgram`

コードブロック 1 つ分の解析結果です。

| メンバー                                                        |                                  |
| --------------------------------------------------------------- | -------------------------------- |
| `Tree`                                                          | そのブロックの HLSL 構文木       |
| `Uniforms` / `Structs` / `FunctionNames` / `FunctionSignatures` | そのブロックから見える宣言       |
| `PassName`                                                      | 囲む `Pass` の名前               |
| `KernelName`                                                    | `.compute` のカーネル名          |
| `Keyword`                                                       | どのシンボルを有効にした構成か |

### `AnalyzerOptions`

| メンバー                                    |                                      |
| ------------------------------------------- | ------------------------------------ |
| `GetEffectiveSeverity(descriptor)`          | 設定を適用した実効重要度             |
| `IsEnabled(descriptor)`                     | 有効かどうか                         |
| `TryGetOption(ruleId, name, out value)`     | 設定ファイルのルールごとの設定を引く |
| `GetIntOption(ruleId, name, defaultValue)`  | 同上。数値として引く                 |
| `WithExtension<T>(x)` / `GetExtension<T>()` | 型をキーにした任意の設定の受け渡し   |

重要度の上書きはドライバが一律に適用します。ルール側で設定を読む必要はありません。

---

## テスト

### `ShaderRuleVerifier` (`Shaderlyn.Testing`)

| メンバー                                   |                                                                |
| ------------------------------------------ | -------------------------------------------------------------- |
| `new ShaderRuleVerifier(params analyzers)` | 検査するアナライザ。渡したものだけが走る                       |
| `AddInclude(path, content)`                | `#include` で引けるファイル。ディスクを読まない                |
| `WithOptions(options)`                     | 設定ファイル相当の設定                                         |
| `WithPredefinedMacros(macros)`             | 定義済みマクロ                                                 |
| `Analyze(source, filePath)`                | 解析して指摘を返す。拡張子で ShaderLab / HLSL 単体を切り替える |
| `static Describe(diagnostics)`             | 落ちたときに何が出ていたかを見せる                             |

返るのは抑制コメントを適用したあとの指摘です。利用者の目に入るものと同じです。

---

## 関連

- [自作ルールの作り方](tutorial.md) — 手順と、書くときの原則
- [ルールの実例集](cookbook.md) — 写して直せば動く例
- [条件付きコンパイルの扱い](../guide/conditional-compilation.md) — 出現条件がどう作られるか
- [設定ファイル](../guide/configuration.md)
