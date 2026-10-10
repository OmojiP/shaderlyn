using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules;

/// <summary>
/// コードが届くコンパイル先 (API と方言) のうち、その構文や型を使えないものがあれば報告する (HL0003)。
/// </summary>
/// <remarks>
/// <para>
/// <b>1 つのパスは、API ごとに別の方言でコンパイルされることがある。</b>
/// Unity で <c>#pragma use_dxc</c> を書いたパスは、DX12 は DXC、DX11 は FXC でコンパイルされうる
/// (<see cref="HlslCompileTarget"/>)。パーサーはどれかの方言で使える構文を読むので、
/// 片方でしか使えない構文 (<c>enum</c> など) は、ここで API を添えて報告する。
/// どの方言でも使えない構文は、パーサーが HL0003 として報告済みである。
/// </para>
/// <para>
/// <b>構成ごとに、そのノードに届くコンパイル先だけを見る。</b>
/// <c>#ifdef UNITY_DEVICE_SUPPORTS_NATIVE_16BIT</c> の中の <c>float16_t</c> は、
/// キーワードが有効な構成 (DXC) でしかコンパイルされない。
/// </para>
/// <para>使えないコンパイル先 (<see cref="HlslCompileTarget.IsUsable"/> が偽) は見ない。そこでのエラーは利用者に見えない。</para>
/// </remarks>
internal sealed class CompileTargetAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslDescriptors.UnsupportedDeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 型 (int64_t など) はパーサーが見ないので、方言が 1 つだけでも見る。コンパイル先が分からなければ見ない。
        if (compilation.Programs.All(p => p.Tree.CompileTargets.IsDefaultOrEmpty))
        {
            return;
        }

        // 位置ごとに、使えないコンパイル先を集めてから 1 度だけ報告する。同じコードが構成ごとに何度も渡る。
        Dictionary<int, (HlslSyntaxToken Token, string Construct, SortedSet<string> Subjects)> found = [];

        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (Describe(node) is not { } construct || !compilation.IsReportable(node))
            {
                continue;
            }

            foreach (HlslCompileTarget target in CompileTargets.Reaching(compilation, node, program))
            {
                if (construct.IsAvailable(target.Dialect.Features))
                {
                    continue;
                }

                int position = construct.Token.Span.Start;

                if (!found.TryGetValue(position, out var entry))
                {
                    entry = (construct.Token, construct.Name, new SortedSet<string>(StringComparer.Ordinal));
                    found[position] = entry;
                }

                entry.Subjects.Add(target.Subject);
            }
        }

        foreach ((HlslSyntaxToken token, string name, SortedSet<string> subjects) in found.Values)
        {
            if (token.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    HlslDescriptors.UnsupportedDeclaration, location, name, string.Join("・", subjects) + " "));
            }
        }
    }

    /// <summary>方言で使えるかが変わる構文と型を見分ける。</summary>
    /// <param name="node">ノード。</param>
    /// <returns>見分けた構文。当てはまらなければ <see langword="null"/>。</returns>
    private static Construct? Describe(HlslSyntaxNode node) => node switch
    {
        EnumDeclarationSyntax enumeration => new Construct(enumeration.NameToken ?? enumeration.Keyword, "enum", static f => f.Enums),
        HlslTypeSyntax type => DescribeType(type),
        _ => null,
    };

    /// <summary>型名を見分ける (<c>int64_t</c>、<c>float32_t</c>、<c>float16_t3</c> など)。</summary>
    /// <param name="type">型。</param>
    /// <returns>見分けた型。当てはまらなければ <see langword="null"/>。</returns>
    private static Construct? DescribeType(HlslTypeSyntax type)
    {
        // vector<uint32_t, 2> は要素の型を見る。
        HlslSyntaxToken token = type.NameToken.Text is "vector" or "matrix" && !type.TemplateArguments.IsDefaultOrEmpty
            ? type.TemplateArguments[0]
            : type.NameToken;
        string written = token.Text;
        string scalar = StripShape(written);

        return scalar switch
        {
            "int64_t" or "uint64_t" => new Construct(token, $"型 {written}", static f => f.Int64Types),
            "float64_t" or "float32_t" or "int32_t" or "uint32_t" => new Construct(token, $"型 {written}", static f => f.ExplicitWidthTypes),
            "float16_t" or "int16_t" or "uint16_t" => new Construct(token, $"型 {written}", static f => f.SixteenBitTypes),
            _ => null,
        };
    }

    /// <summary>ベクトルと行列の形 (<c>3</c> / <c>4x4</c>) を外す。</summary>
    /// <param name="name">型名。</param>
    /// <returns>スカラーの型名。</returns>
    private static string StripShape(string name)
    {
        int end = name.Length;

        if (end >= 3 && name[end - 2] == 'x' && name[end - 1] is >= '1' and <= '4' && name[end - 3] is >= '1' and <= '4')
        {
            end -= 3;
        }
        else if (end >= 1 && name[end - 1] is >= '1' and <= '4' && end >= 2 && name[end - 2] == 't')
        {
            end -= 1;
        }

        return name[..end];
    }

    /// <summary>見分けた構文。</summary>
    /// <param name="Token">報告する位置。</param>
    /// <param name="Name">文に入れる名前。</param>
    /// <param name="IsAvailable">方言で使えるか。</param>
    private sealed record Construct(HlslSyntaxToken Token, string Name, Func<HlslLanguageFeatures, bool> IsAvailable);
}

/// <summary>
/// ノードに届くコンパイル先を求める。
/// </summary>
internal static class CompileTargets
{
    /// <summary>
    /// そのノードが、どの構成でどのコンパイル先に届くかを求める。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="node">ノード。</param>
    /// <param name="program">ノードが属する木。</param>
    /// <returns>届く、使えるコンパイル先。コンパイル先が分からなければ空。</returns>
    /// <remarks>
    /// ノードの出現条件と、コンパイル先の構成の条件が両立するなら届く。
    /// 出現条件が分からなければ、木を解析した構成で決める。
    /// </remarks>
    public static IEnumerable<HlslCompileTarget> Reaching(ShaderCompilation compilation, HlslSyntaxNode node, AnalyzedProgram program)
    {
        ImmutableArray<HlslCompileTarget> targets = program.Tree.CompileTargets;

        if (targets.IsDefaultOrEmpty)
        {
            yield break;
        }

        SymbolCondition condition = compilation.GetEffectiveCondition(node, program);
        Semantics.Conditional.ConditionMap? map = null;

        foreach (HlslCompileTarget target in targets)
        {
            if (!target.IsUsable)
            {
                continue;
            }

            if (condition.IsUnknown)
            {
                if (target.AppliesTo([.. program.EnabledSymbols]))
                {
                    yield return target;
                }

                continue;
            }

            map ??= compilation.GetConditionMap();

            if (map.IsPossible(condition.And(target.Condition)))
            {
                yield return target;
            }
        }
    }
}
