# Shaderlyn 0.2.1 → 0.3.0 の、自作ルール・自作の環境の書き換え (sed -E で使う)。
# 名前の置き換えだけを行う。置き換えたあとにビルドし、残ったエラーは docs/guide/migration-0.3.0.md を見て直す。

# 名前空間
s/\busing Shaderlyn\.Hlsl\.Preprocessing;/using Shaderlyn.CFamily.Preprocessing;/g
s/\bShaderlyn\.Hlsl\.Preprocessing\b/Shaderlyn.CFamily.Preprocessing/g
# ノードの大部分は CFamily へ移った。cbuffer と namespace の節は Hlsl.Syntax に残るので、両方を書く (使わないほうは消してよい)。
s/^([ \t]*)using Shaderlyn\.Hlsl\.Syntax;/\1using Shaderlyn.CFamily.Syntax;\n\1using Shaderlyn.Hlsl.Syntax;/

# 字句・プリプロセッサ
s/\bHlslSyntaxToken\b/ShaderSyntaxToken/g
s/\bHlslSyntaxKind\b/ShaderSyntaxKind/g
s/\bHlslSyntaxTrivia\b/ShaderSyntaxTrivia/g
s/\bHlslLexer\b/ShaderLexer/g
s/\bHlslIncludeCache\b/IncludeCache/g
s/\bHlslTokenCache\b/TokenCache/g

# 構文木
s/\bHlslSyntaxNode\b/ShaderSyntaxNode/g
s/\bHlslNodeOrTokenEntry\b/ShaderNodeOrTokenEntry/g
s/\bHlslNodeOrToken\b/ShaderNodeOrToken/g
s/\bHlslCompilationUnitSyntax\b/CompilationUnitSyntax/g
s/\bHlslDeclarationSyntax\b/DeclarationSyntax/g
s/\bHlslExpressionSyntax\b/ExpressionSyntax/g
s/\bHlslStatementSyntax\b/StatementSyntax/g
s/\bHlslTypeSyntax\b/TypeSyntax/g
s/\bHlslAttributeSyntax\b/AttributeSyntax/g
s/\bHlslLiteralKind\b/ShaderLiteralKind/g
s/\bHlslLiteral\b/ShaderLiteral/g
s/\bHlslSyntaxFacts\b/ShaderSyntaxFacts/g
s/\.Tree\.(CompileTargets|Dialects|LanguageVersion|Features)\b/.Tree.Hlsl().\1/g
# 解析済みのコードの型。HlslSyntaxTree.Parse は残る。
s/\bHlslSyntaxTree\b([^.]|$)/ShaderSyntaxTree\1/g

# ルールの基底クラス
s/\bHlslRuleAnalyzer\b/ShaderRuleAnalyzer/g
s/\bHlslAnalysisContext\b/ShaderAnalysisContext/g
s/\bHlslNodeAnalysisContext\b/ShaderNodeAnalysisContext/g
s/\bInitializeHlsl\b/InitializeShader/g

# 環境
s/\bHlslCompilationUnit\b/ShaderCompilationUnit/g
s/\bHlslEntryPoint\b/ShaderEntryPoint/g
