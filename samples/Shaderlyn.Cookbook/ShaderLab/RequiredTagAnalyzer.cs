using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Cookbook;

/// <summary>
/// <c>SubShader</c> に指定したタグがあることを確かめ、値も縛る。
/// </summary>
/// <remarks>
/// <para>
/// <b>「無いこと」を報告するので、集めてから判定する。</b>
/// ノードを 1 つ見るだけでは「どこにも無い」ことは言えない。
/// </para>
/// <para>
/// <b><c>Pass</c> に書かれたタグは数えない。</b>
/// <c>Tags</c> は <c>SubShader</c> にも <c>Pass</c> にも書ける。
/// 間に <c>Pass</c> を挟んで見つけたタグまで数えると、
/// 「1 つの Pass にだけ書いた」シェーダーを「SubShader に書いてある」と誤って認めることになる。
/// </para>
/// <para>
/// <c>allowedValues</c> を渡さなければ、あることだけを確かめる。
/// </para>
/// </remarks>
public sealed class RequiredTagAnalyzer(string tag, params string[] allowedValues) : DiagnosticAnalyzer
{
    private readonly ImmutableHashSet<string> _allowed =
        allowedValues.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CookbookRules.RequiredTag, CookbookRules.TagValueNotAllowed];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterAnalysisStartAction(start =>
        {
            List<SubShaderSyntax> subShaders = [];
            HashSet<SyntaxNode> withTag = [];

            start.RegisterNodeAction<SubShaderSyntax>(c => subShaders.Add(c.Node));

            start.RegisterNodeAction<TagSyntax>(c =>
            {
                if (!string.Equals(c.Node.Key, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (FindSubShader(c.Node) is { } subShader)
                {
                    withTag.Add(subShader);
                }

                if (!_allowed.IsEmpty && !_allowed.Contains(c.Node.Value))
                {
                    c.ReportDiagnostic(
                        CookbookRules.TagValueNotAllowed, c.Node.ValueToken.Span, tag, c.Node.Value);
                }
            });

            start.RegisterAnalysisEndAction(end =>
            {
                foreach (SubShaderSyntax subShader in subShaders.Where(s => !withTag.Contains(s)))
                {
                    end.ReportDiagnostic(CookbookRules.RequiredTag, subShader.Keyword.Span, tag);
                }
            });
        });
    }

    /// <summary>タグを直に囲む <c>SubShader</c> を探す。<c>Pass</c> のタグなら見つからない。</summary>
    /// <param name="tag">対象のタグ。</param>
    /// <returns>囲んでいる <c>SubShader</c>。<c>Pass</c> の中にある場合は <see langword="null"/>。</returns>
    private static SubShaderSyntax? FindSubShader(TagSyntax tag)
    {
        for (SyntaxNode? node = tag.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case SubShaderSyntax subShader:
                    return subShader;
                case PassSyntax:
                    return null;
            }
        }

        return null;
    }
}
