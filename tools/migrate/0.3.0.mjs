// Shaderlyn 0.2.1 → 0.3.0 の、自作ルール・自作の環境の書き換え。
//
// 使い方 (書き換えるプロジェクトのフォルダーで):
//   node <このファイル>              # カレントフォルダーの下の .cs を書き換える
//   node <このファイル> <フォルダー>   # そのフォルダーの下の .cs を書き換える
//
// 名前の置き換えだけを行い、書き換えたファイルを並べる。bin / obj / .git の下は見ない。
// 置き換えたあとにビルドし、残ったエラーは docs/guide/migration-0.3.0.md の「スクリプトでは置き換えないもの」を見て直す。
// ファイルを直接書き換えるので、先にコミットしておくこと。

import { readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

/** 置き換え。上から順に当てる。 */
const replacements = [
  // 名前空間
  [/\bShaderlyn\.Hlsl\.Preprocessing\b/g, "Shaderlyn.CFamily.Preprocessing"],
  // ノードの大部分は CFamily へ移った。cbuffer と namespace の節は Hlsl.Syntax に残るので、両方を書く (使わないほうは消してよい)。
  [/^([ \t]*)using Shaderlyn\.Hlsl\.Syntax;(\r?\n)/gm, "$1using Shaderlyn.CFamily.Syntax;$2$1using Shaderlyn.Hlsl.Syntax;$2"],

  // 字句・プリプロセッサ
  [/\bHlslSyntaxToken\b/g, "ShaderSyntaxToken"],
  [/\bHlslSyntaxKind\b/g, "ShaderSyntaxKind"],
  [/\bHlslSyntaxTrivia\b/g, "ShaderSyntaxTrivia"],
  [/\bHlslLexer\b/g, "ShaderLexer"],
  [/\bHlslIncludeCache\b/g, "IncludeCache"],
  [/\bHlslTokenCache\b/g, "TokenCache"],

  // 構文木
  [/\bHlslSyntaxNode\b/g, "ShaderSyntaxNode"],
  [/\bHlslNodeOrTokenEntry\b/g, "ShaderNodeOrTokenEntry"],
  [/\bHlslNodeOrToken\b/g, "ShaderNodeOrToken"],
  [/\bHlslCompilationUnitSyntax\b/g, "CompilationUnitSyntax"],
  [/\bHlslDeclarationSyntax\b/g, "DeclarationSyntax"],
  [/\bHlslExpressionSyntax\b/g, "ExpressionSyntax"],
  [/\bHlslStatementSyntax\b/g, "StatementSyntax"],
  [/\bHlslTypeSyntax\b/g, "TypeSyntax"],
  [/\bHlslAttributeSyntax\b/g, "AttributeSyntax"],
  [/\bHlslLiteralKind\b/g, "ShaderLiteralKind"],
  [/\bHlslLiteral\b/g, "ShaderLiteral"],
  [/\bHlslSyntaxFacts\b/g, "ShaderSyntaxFacts"],
  [/\.Tree\.(CompileTargets|Dialects|LanguageVersion|Features)\b/g, ".Tree.Hlsl().$1"],
  // 解析済みのコードの型。HlslSyntaxTree.Parse は残る。
  [/\bHlslSyntaxTree\b(?!\.)/g, "ShaderSyntaxTree"],

  // ルールの基底クラス
  [/\bHlslRuleAnalyzer\b/g, "ShaderRuleAnalyzer"],
  [/\bHlslAnalysisContext\b/g, "ShaderAnalysisContext"],
  [/\bHlslNodeAnalysisContext\b/g, "ShaderNodeAnalysisContext"],
  [/\bInitializeHlsl\b/g, "InitializeShader"],

  // 環境
  [/\bHlslCompilationUnit\b/g, "ShaderCompilationUnit"],
  [/\bHlslEntryPoint\b/g, "ShaderEntryPoint"],
];

const skipped = new Set(["bin", "obj", ".git"]);

/** フォルダーの下の .cs を集める。 */
function collect(directory) {
  const files = [];

  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);

    if (entry.isDirectory()) {
      if (!skipped.has(entry.name)) {
        files.push(...collect(path));
      }
    } else if (entry.name.endsWith(".cs")) {
      files.push(path);
    }
  }

  return files;
}

const root = resolve(process.argv[2] ?? ".");
let changed = 0;

for (const file of collect(root)) {
  const original = readFileSync(file, "utf8");
  const rewritten = replacements.reduce((text, [pattern, replacement]) => text.replace(pattern, replacement), original);

  if (rewritten !== original) {
    writeFileSync(file, rewritten, "utf8");
    console.log(file);
    changed++;
  }
}

console.log(`書き換えたファイル: ${changed} 件`);
