using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 大きさを書かない配列の宣言し直しを検査する (CF0314)。glslang 16.6.0 の ParseHelper.cpp の <c>arrayLimitCheck</c> の後の宣言し直しの検査
/// (「redeclaration of array with a different element type」「… with a different array dimensions or sizes」) を移したもの。
/// </summary>
/// <remarks>
/// GLSL では、一番外の次元の大きさを書かない配列 (<c>float a[][5];</c>) を、同じ範囲で大きさを書いて宣言し直せる。
/// 宣言し直すときは、要素の型と、内側の次元の数と大きさが同じでなければならない。
/// 内側の次元の大きさは、どちらも整数のリテラルのときだけ比べる。
/// </remarks>
internal sealed class GlslArrayRedeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.Redeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not VariableDeclarationSyntax { Type.ArrayRanks.IsEmpty: true } declaration
                || !declaration.Variables.Any(IsOuterUnsized))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is not null)
            {
                Check(requirement, declaration);
            }
        }
    }

    /// <summary>大きさを書かない配列のそれぞれを、同じ範囲の後ろの同じ名前の宣言と比べる。</summary>
    private static void Check(GlslRequirements requirement, VariableDeclarationSyntax declaration)
    {
        IEnumerable<VariableDeclarationSyntax> siblings = declaration.Parent switch
        {
            CompilationUnitSyntax unit => unit.ChildNodes().OfType<VariableDeclarationSyntax>(),
            LocalDeclarationStatementSyntax { Parent: { } block } => block.ChildNodes().OfType<LocalDeclarationStatementSyntax>().Select(s => s.Declaration).OfType<VariableDeclarationSyntax>(),
            _ => [],
        };

        VariableDeclarationSyntax[] later = [.. siblings.SkipWhile(d => d != declaration).Skip(1)];

        foreach (VariableDeclaratorSyntax unsized in declaration.Variables.Where(IsOuterUnsized))
        {
            if (later.SelectMany(d => d.Variables.Select(v => (Declaration: d, Declarator: v))).FirstOrDefault(p => p.Declarator.Name == unsized.Name) is not { Declarator: { } redeclared } pair)
            {
                continue;
            }

            if (pair.Declaration.Type.Name == declaration.Type.Name && pair.Declaration.Type.ArrayRanks.IsEmpty
                && SameInnerDimensions(InnerDimensions(unsized), InnerDimensions(redeclared)) != false)
            {
                continue;
            }

            int line = unsized.NameToken.GetLocation().LineSpan.Start.Line + 1;
            requirement.Report(HlslRuleDescriptors.Redeclaration, redeclared.NameToken, redeclared.Name, line.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>一番外の次元の大きさを書かない配列か (<c>a[]</c>、<c>a[][5]</c>)。</summary>
    private static bool IsOuterUnsized(VariableDeclaratorSyntax declarator)
        => declarator.ArrayRankTokens is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }, ..];

    /// <summary>一番外を除く次元の、角括弧の中のトークンの文字列。配列でなければ <see langword="null"/>。</summary>
    private static string[]? InnerDimensions(VariableDeclaratorSyntax declarator)
    {
        List<string> dimensions = [];
        List<string> current = [];
        int depth = 0;

        foreach (ShaderSyntaxToken token in declarator.ArrayRankTokens)
        {
            if (token.Kind == ShaderSyntaxKind.OpenBracketToken && depth++ == 0)
            {
                current.Clear();
            }
            else if (token.Kind == ShaderSyntaxKind.CloseBracketToken && --depth == 0)
            {
                dimensions.Add(string.Concat(current));
            }
            else
            {
                current.Add(token.Text);
            }
        }

        return dimensions.Count == 0 ? null : [.. dimensions.Skip(1)];
    }

    /// <summary>内側の次元が同じか。分からなければ <see langword="null"/>。</summary>
    private static bool? SameInnerDimensions(string[]? left, string[]? right)
    {
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        bool? same = true;

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] == right[i])
            {
                continue;
            }

            if (long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out long a)
                && long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out long b))
            {
                if (a != b)
                {
                    return false;
                }
            }
            else
            {
                same = null;
            }
        }

        return same;
    }
}
