using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Rules;

/// <summary>
/// 同じ SubShader 内での Pass 名の重複を検出する (SL1041)。
/// </summary>
internal sealed class PassNameAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderLabRuleDescriptors.DuplicatePassName];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<SubShaderSyntax>(AnalyzeSubShader);

    /// <summary>
    /// SubShader 内の Pass 名を集めて重複を報告する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <remarks>
    /// <para>
    /// 重複の判定は SubShader 単位で行う。別の SubShader に同名の Pass があるのは正常であり、
    /// 品質レベルごとに同じ役割の Pass を用意する通常の書き方だからである。
    /// </para>
    /// <para>
    /// <b>比較は大文字小文字を区別しない。</b>
    /// Unity は Pass 名を大文字へ正規化して保持しており、
    /// <c>UsePass "Shader/FORWARD"</c> のような参照もその正規化後の名前で解決される。
    /// したがって <c>Forward</c> と <c>FORWARD</c> は Unity にとって同じ名前である。
    /// </para>
    /// </remarks>
    private static void AnalyzeSubShader(NodeAnalysisContext<SubShaderSyntax> context)
    {
        Dictionary<string, SyntaxToken> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (PassSyntax pass in context.Node.Passes)
        {
            SyntaxToken? nameToken = FindPassNameToken(pass);
            if (nameToken is null || nameToken.IsMissing)
            {
                continue;
            }

            string name = nameToken.ValueText;
            if (name.Length == 0)
            {
                continue;
            }

            if (seen.ContainsKey(name))
            {
                context.ReportDiagnostic(
                    ShaderLabRuleDescriptors.DuplicatePassName, nameToken.Span, name);
            }
            else
            {
                seen[name] = nameToken;
            }
        }
    }

    /// <summary>
    /// Pass の <c>Name</c> 命令から名前のトークンを取り出す。
    /// </summary>
    /// <param name="pass">対象の Pass。</param>
    /// <returns>名前のトークン。<c>Name</c> 命令が無い場合は <see langword="null"/>。</returns>
    private static SyntaxToken? FindPassNameToken(PassSyntax pass)
    {
        foreach (CommandSyntax command in pass.Body.Statements.OfType<CommandSyntax>())
        {
            if (!command.NameIs("Name"))
            {
                continue;
            }

            if (command.ValueArguments.FirstOrDefault() is LiteralArgumentSyntax literal)
            {
                return literal.Token;
            }
        }

        return null;
    }
}
