using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、取り除かれた宣言 (<c>[RemovedSince(版, "説明")]</c>) の使用を検査する (SG0341)。
/// </summary>
/// <remarks>
/// <para>
/// 版が、ファイルの言語のバージョン (<c>#language slang 2026</c>。書かなければ slangc 2026.19 の既定の 2025) 以下なら取り除かれている (slangc の E31207)。
/// <c>-1</c> はいつも取り除かれている。
/// </para>
/// <para>
/// 属性は木に残らない宣言 (構造体・フィールド) もあるので、トークンの並びから、属性の後ろの宣言の名前を読む。
/// その名前の識別子のうち、宣言そのもの以外のすべてを使用とする。
/// </para>
/// </remarks>
internal sealed class SlangRemovedDeclarationAnalyzer : SemanticRuleAnalyzer
{
    private const int DefaultLanguageVersion = 2025;

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.RemovedDeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            // 構造体の属性は木に残らないので、プリプロセスの後のトークンの並びから読む。
            ShaderSyntaxToken[] tokens = [.. program.Tree.PreprocessResult.Tokens.Where(t => !t.IsMissing)];
            int version = LanguageVersion(program.Tree.PreprocessResult.Tokens) ?? DefaultLanguageVersion;
            Dictionary<string, (ShaderSyntaxToken Declaration, int Since, string? Reason)> removed = new(StringComparer.Ordinal);

            for (int i = 0; i + 3 < tokens.Length; i++)
            {
                if (tokens[i] is not { Kind: ShaderSyntaxKind.OpenBracketToken }
                    || tokens[i + 1] is not { Text: "RemovedSince" }
                    || tokens[i + 2].Kind != ShaderSyntaxKind.OpenParenToken
                    || Since(tokens, i + 3) is not { } since
                    || since.Version > version)
                {
                    continue;
                }

                int close = Array.FindIndex(tokens, i, t => t.Kind == ShaderSyntaxKind.CloseBracketToken);

                if (close >= 0 && DeclaredName(tokens, close + 1) is { } name)
                {
                    removed.TryAdd(name.Text, (name, since.Version, since.Reason));
                }
            }

            // 多重定義の一部だけが取り除かれた関数は、どれを呼ぶか決められないので見ない。
            foreach (FunctionDeclarationSyntax function in program.Tree.Root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
            {
                if (removed.ContainsKey(function.Name) && !function.Attributes.Any(a => a.NameToken.Text == "RemovedSince"))
                {
                    removed.Remove(function.Name);
                }
            }

            if (removed.Count == 0)
            {
                continue;
            }

            foreach (ShaderSyntaxToken token in tokens)
            {
                if (token.Kind != ShaderSyntaxKind.IdentifierToken
                    || !removed.TryGetValue(token.Text, out (ShaderSyntaxToken Declaration, int Since, string? Reason) entry)
                    || ReferenceEquals(token, entry.Declaration)
                    || !compilation.IsReportable(token)
                    || token.GetLocation() is not { } location)
                {
                    continue;
                }

                string since = entry.Since < 0 ? "取り除かれています" : $"言語のバージョン {entry.Since} から取り除かれています";
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.RemovedDeclaration, location,
                    $"'{token.Text}' は{since}{(entry.Reason is { Length: > 0 } reason ? $": {reason}" : "")}。"));
            }
        }
    }

    /// <summary><c>RemovedSince(</c> の後ろの、版と説明。</summary>
    private static (int Version, string? Reason)? Since(ShaderSyntaxToken[] tokens, int start)
    {
        bool negative = tokens[start].Kind == ShaderSyntaxKind.MinusToken;
        int at = negative ? start + 1 : start;

        if (at >= tokens.Length || !int.TryParse(tokens[at].Text, NumberStyles.None, CultureInfo.InvariantCulture, out int version))
        {
            return null;
        }

        string? reason = at + 2 < tokens.Length && tokens[at + 1].Kind == ShaderSyntaxKind.CommaToken && tokens[at + 2].Kind == ShaderSyntaxKind.StringLiteralToken
            ? tokens[at + 2].ValueText
            : null;
        return (negative ? -version : version, reason);
    }

    /// <summary>属性の後ろの宣言の名前 (struct S・interface I・enum E・typedef … N・型 名前 ( / ; / =)。</summary>
    private static ShaderSyntaxToken? DeclaredName(ShaderSyntaxToken[] tokens, int start)
    {
        int i = start;

        // ほかの属性を飛ばす。
        while (i < tokens.Length && tokens[i].Kind == ShaderSyntaxKind.OpenBracketToken)
        {
            int close = Array.FindIndex(tokens, i, t => t.Kind == ShaderSyntaxKind.CloseBracketToken);

            if (close < 0)
            {
                return null;
            }

            i = close + 1;
        }

        if (i + 1 < tokens.Length && tokens[i].Text is "struct" or "class" or "interface" or "enum" or "typealias")
        {
            return tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken ? tokens[i + 1] : null;
        }

        // 型 名前 ( … / 型 名前 ; / typedef 型 名前 ; — 区切りの手前の名前。
        for (int j = i; j < tokens.Length && j < i + 16; j++)
        {
            if (tokens[j].Kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.EqualsToken
                or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.ColonToken)
            {
                return j > i && tokens[j - 1].Kind == ShaderSyntaxKind.IdentifierToken ? tokens[j - 1] : null;
            }
        }

        return null;
    }

    /// <summary>ファイルの <c>#language slang 2026</c> の版。プリプロセス後のトークンには残らないので、テキストから読む。</summary>
    private static int? LanguageVersion(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        if (tokens.IsDefaultOrEmpty || tokens[0].Source is not { } source)
        {
            return null;
        }

        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            source.ToString(), @"^\s*#\s*(?:language|lang)\s+slang\s+(\d{4})", System.Text.RegularExpressions.RegexOptions.Multiline);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}
