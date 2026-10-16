# 0.2.1 から 0.3.0 へ移行する

0.2.1 から 0.3.0 へ上げるときに、何を書き換えるかをまとめたページです。
変更の一覧は [CHANGELOG](../../CHANGELOG.md) にあります。

## 目次

- [0.2.1 から 0.3.0 へ移行する](#021-から-030-へ移行する)
  - [目次](#目次)
  - [書き換えが要るか](#書き換えが要るか)
  - [書き換えの手順](#書き換えの手順)
    - [スクリプトでは置き換えないもの](#スクリプトでは置き換えないもの)
  - [ルールの基底クラス](#ルールの基底クラス)
  - [C 系の言語に共通の型の置き場所](#c-系の言語に共通の型の置き場所)
    - [型の名前](#型の名前)
    - [名前空間](#名前空間)
    - [HLSL の方言の設定](#hlsl-の方言の設定)
  - [環境](#環境)

## 書き換えが要るか

ツールとして使うだけ (CLI・VS Code 拡張・GitHub Action) なら、書き換えは要りません。
ルール ID・設定ファイル・CLI のオプション・出力の形は変わりません。

自作ルール・自作の環境・自作の CLI を書いているなら、下の書き換えが要ります。
構文木のノード (`InvocationExpressionSyntax` など) を使うルールは、`using` を書き換えることになります。

## 書き換えの手順

名前の置き換えは、スクリプト [tools/migrate/0.3.0.sed](../../tools/migrate/0.3.0.sed) でまとめて行えます。
このページの表にある名前の変更を、すべて含んでいます。

1. 自作ルールのプロジェクトをコミットしておく (置き換えはファイルを直接書き換える)
2. パッケージの参照を 0.3.0 に上げる
3. プロジェクトのフォルダーで、スクリプトを流す (Windows では Git Bash で動く)

   ```sh
   find . -name '*.cs' -not -path '*/obj/*' -exec sed -E -i -f path/to/0.3.0.sed {} +
   ```

4. ビルドし、残ったエラーを下の「[スクリプトでは置き換えないもの](#スクリプトでは置き換えないもの)」を見て直す

0.2.1 の実例集 (`samples/Shaderlyn.Cookbook`) をこのスクリプトで書き換えると、残るエラーは `PreprocessorOptions.LanguageVersion` の 1 件だけです。

### スクリプトでは置き換えないもの

- **`PreprocessorOptions` の HLSL の方言の設定** (`Dialects`・`LanguageVersion`・`CompileTargets`・`DialectName`・`Features`)。
  同じ名前のプロパティが `HlslEnvironmentOptions` にもあり (こちらは変わらない)、名前だけでは見分けられません。
  [HLSL の方言の設定](#hlsl-の方言の設定) のとおりに書き換えてください
- **`tree.Hlsl()` の `using`。** スクリプトは `program.Tree.CompileTargets` を `program.Tree.Hlsl().CompileTargets` に置き換えますが、
  `Hlsl()` を使うには `using Shaderlyn.Hlsl.Parsing;` が要ります。「`Hlsl` の定義が含まれていません」のエラーが出たら足してください
- **使わない `using`。** スクリプトは `using Shaderlyn.Hlsl.Syntax;` を残したまま `using Shaderlyn.CFamily.Syntax;` を足します。
  cbuffer と namespace の節 (`ConstantBufferDeclarationSyntax` / `NamespaceDeclarationSyntax`) を使わないファイルでは、`Shaderlyn.Hlsl.Syntax` を消してかまいません

## ルールの基底クラス

自作ルールの基底クラスとその周りの名前から `Hlsl` を外しました。ルールが見るのは C 系の言語に共通の構文木で、HLSL に限らないためです。

| 0.2.1 | 0.3.0 |
| --- | --- |
| `HlslRuleAnalyzer` | `ShaderRuleAnalyzer` |
| `InitializeHlsl(HlslAnalysisContext context)` | `InitializeShader(ShaderAnalysisContext context)` |
| `HlslAnalysisContext` | `ShaderAnalysisContext` |
| `HlslNodeAnalysisContext<TNode>` | `ShaderNodeAnalysisContext<TNode>` |

名前空間 (`Shaderlyn.Semantics.Analysis`) と使い方は変わりません。

```csharp
// 0.2.1
public sealed class NoHalfAnalyzer : HlslRuleAnalyzer
{
    protected override void InitializeHlsl(HlslAnalysisContext context) { ... }
}

// 0.3.0
public sealed class NoHalfAnalyzer : ShaderRuleAnalyzer
{
    protected override void InitializeShader(ShaderAnalysisContext context) { ... }
}
```

## C 系の言語に共通の型の置き場所

字句解析・プリプロセッサ・構文木の大部分 (式・文・関数・構造体・変数) は HLSL に限らない (GLSL などの C 系の言語に共通) ので、
新しいパッケージ `Shaderlyn.CFamily` へ移しました。
`Shaderlyn.Hlsl` が `Shaderlyn.CFamily` を参照しているので、パッケージの参照を足す必要はありません。

### 型の名前

HLSL に限らない型は、名前から `Hlsl` を外しました。

| 0.2.1 | 0.3.0 |
| --- | --- |
| `HlslSyntaxToken` | `ShaderSyntaxToken` |
| `HlslSyntaxKind` | `ShaderSyntaxKind` |
| `HlslSyntaxTrivia` | `ShaderSyntaxTrivia` |
| `HlslLexer` | `ShaderLexer` |
| `HlslIncludeCache` | `IncludeCache` |
| `HlslTokenCache` | `TokenCache` |
| `HlslSyntaxNode` | `ShaderSyntaxNode` |
| `HlslNodeOrToken` | `ShaderNodeOrToken` |
| `HlslNodeOrTokenEntry` | `ShaderNodeOrTokenEntry` |
| `HlslCompilationUnitSyntax` | `CompilationUnitSyntax` |
| `HlslDeclarationSyntax` | `DeclarationSyntax` |
| `HlslExpressionSyntax` | `ExpressionSyntax` |
| `HlslStatementSyntax` | `StatementSyntax` |
| `HlslTypeSyntax` | `TypeSyntax` |
| `HlslAttributeSyntax` | `AttributeSyntax` |
| `HlslLiteral` / `HlslLiteralKind` | `ShaderLiteral` / `ShaderLiteralKind` |
| `HlslSyntaxFacts` | `ShaderSyntaxFacts` |

ほかのノード (`InvocationExpressionSyntax`・`FunctionDeclarationSyntax` など) は、名前は変わらず名前空間だけが変わります。

### 名前空間

`using` を書き換えてください。

| 型 | 0.2.1 | 0.3.0 |
| --- | --- | --- |
| `ShaderSyntaxToken` / `ShaderSyntaxKind` / `ShaderSyntaxTrivia` | `Shaderlyn.Hlsl.Syntax` | `Shaderlyn.CFamily.Syntax` |
| `ShaderLexer` | `Shaderlyn.Hlsl.Parsing` | `Shaderlyn.CFamily.Syntax` |
| 構文木のノード (`ShaderSyntaxNode` とその派生型)・`ShaderLiteral`・`ShaderSyntaxFacts` | `Shaderlyn.Hlsl.Syntax` | `Shaderlyn.CFamily.Syntax` |
| `ConstantBufferDeclarationSyntax` / `NamespaceDeclarationSyntax` (HLSL にだけある節) | `Shaderlyn.Hlsl.Syntax` | 変わらない |
| `Shaderlyn.Hlsl.Preprocessing` のすべての型 (`PreprocessorOptions`・`PragmaDirective`・`IIncludeResolver` など) | `Shaderlyn.Hlsl.Preprocessing` | `Shaderlyn.CFamily.Preprocessing` |

解析済みのコードの型は `ShaderSyntaxTree` (`Shaderlyn.CFamily.Syntax`) になりました。
`HlslSyntaxTree` は HLSL のコードを読む入口 (`HlslSyntaxTree.Parse`) だけを持つ静的クラスになり、`ShaderSyntaxTree` を返します。
木の HLSL に固有の値は、`tree.Hlsl()` (`Shaderlyn.Hlsl.Parsing`) から読みます。

| 0.2.1 | 0.3.0 |
| --- | --- |
| `HlslSyntaxTree tree = HlslSyntaxTree.Parse(...)` | `ShaderSyntaxTree tree = HlslSyntaxTree.Parse(...)` |
| `program.Tree.CompileTargets` | `program.Tree.Hlsl().CompileTargets` |
| `program.Tree.Dialects` / `LanguageVersion` / `Features` | `program.Tree.Hlsl().Dialects` / `LanguageVersion` / `Features` |

`Root`・`PreprocessResult`・`Pragmas`・`Diagnostics`・`Text`・`UnanalyzedIdentifiers` は、これまでどおり木から読めます。

多くのルールは、`using Shaderlyn.Hlsl.Syntax;` を `using Shaderlyn.CFamily.Syntax;` に置き換えれば済みます
(cbuffer か namespace の節を使うなら、両方を書きます)。

### HLSL の方言の設定

`PreprocessorOptions` の HLSL の方言の設定 (`Dialects`・`LanguageVersion`・`CompileTargets`・`DialectName`・`Features`) を、
`HlslLanguageOptions` (`Shaderlyn.Hlsl.Parsing`) へ移しました。`PreprocessorOptions.Language` に入れて渡します。

```csharp
// 0.2.1
PreprocessorOptions options = new() { LanguageVersion = HlslLanguageVersion.Hlsl2021 };
options = options with { Dialects = [HlslDialect.Fxc] };
HlslLanguageVersion version = options.LanguageVersion;

// 0.3.0
PreprocessorOptions options = new() { Language = new HlslLanguageOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 } };
options = options.WithHlsl(h => h with { Dialects = [HlslDialect.Fxc] });
HlslLanguageVersion version = options.Hlsl().LanguageVersion;
```

`options.Hlsl()` は、設定が無ければ既定 (FXC) を返します。
自作の環境 (`IHlslEnvironment`) で方言を変えるときは、受け取った設定を `WithHlsl` で変えると、ほかの環境が入れた設定 (`DialectName` など) を残せます。

## 環境

自作の環境 (独自エンジン) を書いている人向けです。ルールを書くだけなら関係ありません。

環境の口を、言語に依らない `IShaderEnvironment` と、HLSL を使う環境の `IHlslEnvironment` に分けました。
`IHlslEnvironment` は `IShaderEnvironment` に「HLSL として読む」ことを足したもので、これまでどおり実装できます。
メンバーは変わりません (足した `Parse` は `IHlslEnvironment` が実装しているので、書かなくてかまいません)。

名前の変わった型:

| 0.2.1 | 0.3.0 |
| --- | --- |
| `HlslCompilationUnit` | `ShaderCompilationUnit` |
| `HlslEntryPoint` | `ShaderEntryPoint` |

受け取る側の型も変わりました。

| 0.2.1 | 0.3.0 |
| --- | --- |
| `compilation.Environment` (`IHlslEnvironment`) | `compilation.Environment` (`IShaderEnvironment`) |
| `HostEnvironment` の `Func<IHlslEnvironment>` | `Func<IShaderEnvironment>`。`IHlslEnvironment` を返すラムダはそのまま渡せる |
