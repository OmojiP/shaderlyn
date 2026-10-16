# 0.2.1 から 0.3.0 へ移行する

0.2.1 から 0.3.0 へ上げるときに、何を書き換えるかをまとめたページです。
変更の一覧は [CHANGELOG](../../CHANGELOG.md) にあります。

## 目次

- [0.2.1 から 0.3.0 へ移行する](#021-から-030-へ移行する)
  - [目次](#目次)
  - [書き換えが要るか](#書き換えが要るか)
  - [C 系の言語に共通の型の置き場所](#c-系の言語に共通の型の置き場所)
    - [型の名前](#型の名前)
    - [名前空間](#名前空間)
    - [HLSL の方言の設定](#hlsl-の方言の設定)

## 書き換えが要るか

ツールとして使うだけ (CLI・VS Code 拡張・GitHub Action) なら、書き換えは要りません。
ルール ID・設定ファイル・CLI のオプション・出力の形は変わりません。

自作ルール・自作の環境・自作の CLI を書いているなら、下の書き換えが要ります。
構文木のノード (`InvocationExpressionSyntax` など) を使うルールは、`using` を書き換えることになります。

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

`HlslSyntaxTree` は、これまでどおり `Shaderlyn.Hlsl` にあります。

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
