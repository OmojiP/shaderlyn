using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Analysis;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules;

/// <summary>
/// 使っている変数の宣言が、その構成に存在するかを検査する (CF0315)。
/// </summary>
/// <remarks>
/// <para>
/// <b>関数についての CF0311 と同じことを、グローバル変数について見る。</b>
/// 宣言が <c>#ifdef</c> の中にだけあると、名前はどこかの構成で宣言されているので、
/// 「宣言されていない識別子」(CF0310) にはならない。
/// 食い違いは「使う側の条件」と「宣言がある条件」の組み合わせにしか現れない。
/// </para>
/// <code>
/// #ifdef _A
/// float4 _Tint;               // _A のときだけ
/// #endif
/// half4 frag() : SV_Target { return _Tint; }   // 常に使う → !_A のとき宣言が無い
/// </code>
/// <para>
/// <b>対象はグローバル変数 (定数バッファのメンバーを含む) だけである。</b>
/// 名前が何を指すかは、使っている位置で解決する (<see cref="ExpressionTypeBinder.ResolveName"/>)。
/// その位置で局所変数か仮引数を指す名前は見ない。別の関数に同じ名前の局所変数があっても、
/// ここでグローバル変数を指しているなら検査する。
/// 局所変数が一部の構成でしか宣言されていなくても、局所変数の候補があれば見送る。
/// </para>
/// <para>
/// 次の場合は判断を見送る。
/// </para>
/// <list type="bullet">
///   <item><description>依存関係が解決できていない</description></item>
///   <item><description>どの構成でも解析しなかった領域に名前がある。そこで宣言されているかもしれない</description></item>
///   <item><description>
///     宣言がバリアントの木にしか無く、既定の木のどこへ足すかも分からない。その宣言の条件が分からない
///   </description></item>
///   <item><description>使う側か宣言の側の条件を追えなかった</description></item>
/// </list>
/// </remarks>
internal sealed class VariableDeclarationPresenceAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.VariableDeclarationNotPresent];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasCompleteDependencies)
        {
            return;
        }

        // 同じ位置の報告は、Pass と構成をまたいで条件の和をとって 1 件にする。
        // 木ごとに最初の報告だけを残すと、ほかの木の構成の分が条件から落ちる。
        ConditionalReports reports = new(compilation);

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            // 判断の手順は ShaderCompilation.TryGetIdentifierUndeclaredCondition にある (自作ルールにも公開している)。
            if (node is not IdentifierExpressionSyntax identifier
                || !compilation.IsReportable(identifier)
                || !compilation.TryGetIdentifierUndeclaredCondition(identifier, program, out SymbolCondition missing)
                || identifier.GetLocation() is not { } location)
            {
                continue;
            }

            string name = identifier.Name;
            reports.Add(
                program,
                identifier,
                missing,
                HlslRuleDescriptors.VariableDeclarationNotPresent,
                location,
                name,
                condition => [name, DescribeLeading(condition), DescribeTrailing(condition)]);
        }

        foreach (Diagnostic diagnostic in reports.Build())
        {
            context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>宣言が無い構成を、利用者に読める形にする。</summary>
    /// <param name="missing">宣言が無い構成を表す条件。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeLeading(SymbolCondition missing)
        => missing.IsAlways ? string.Empty : $"構成：{missing} のとき、";

    /// <summary>どの構成でも宣言が無いなら、そのことを文の後ろに添える。</summary>
    /// <param name="missing">宣言が無い構成を表す条件。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeTrailing(SymbolCondition missing)
        => missing.IsAlways ? "どの構成でも" : string.Empty;
}
