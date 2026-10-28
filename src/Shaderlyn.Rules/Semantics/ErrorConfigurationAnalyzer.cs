using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules;

/// <summary>
/// 展開が <c>#error</c> に到達した構成を伝える (CF0104)。
/// </summary>
/// <remarks>
/// <para>
/// Unreal Engine と汎用の HLSL では、その構成はコンパイルされない (C++ の <c>ShouldCompilePermutation</c> や
/// コンパイルの指定が除く)。誤りではないので情報として伝える。その構成のコードは、宣言の有無を見る検査で考えない。
/// </para>
/// <para>
/// Unity では、宣言したキーワードの構成はすべてコンパイルされ、<c>#error</c> に到達すればシェーダーが壊れる。
/// キーワードの構成なら Unity のルール (UN0103、エラー) が伝える。キーワードに関わらない構成 (どの構成でも到達する) は、
/// 解析のマクロの状態が実際と違うためのことが多いので、Unity でもここで情報として伝える。
/// </para>
/// </remarks>
internal sealed class ErrorConfigurationAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SemanticRuleDescriptors.ErrorConfiguration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        bool unity = compilation.Environment.Name == "unity";

        foreach (ErrorConfigurationReport report in Describe(compilation))
        {
            if (unity && !report.Condition.IsAlways)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                SemanticRuleDescriptors.ErrorConfiguration,
                report.Location,
                report.Configuration,
                report.ErrorFile,
                report.ErrorLine));
        }
    }

    /// <summary>報告 1 件分。</summary>
    /// <param name="Condition">構成の条件。</param>
    /// <param name="Configuration">メッセージの頭 (<c>構成：_A は </c>)。どの構成でもなら空。</param>
    /// <param name="ErrorFile">到達した <c>#error</c> のファイルの名前。</param>
    /// <param name="ErrorLine">到達した <c>#error</c> の行 (1 から)。</param>
    /// <param name="Location">報告の位置。</param>
    public readonly record struct ErrorConfigurationReport(
        SymbolCondition Condition,
        string Configuration,
        string ErrorFile,
        int ErrorLine,
        Location Location);

    /// <summary>
    /// <c>#error</c> に到達した構成ごとに、報告の内容を求める。同じ <c>#error</c> と同じ条件は 1 件にする。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>報告の内容。</returns>
    /// <remarks>
    /// 位置は、到達した <c>#error</c> がこのファイルにあればその行、無ければファイルの先頭である。
    /// </remarks>
    public static IEnumerable<ErrorConfigurationReport> Describe(ShaderCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        HashSet<(string File, int Start, string Condition)> seen = [];

        foreach (ErrorConfiguration configuration in compilation.ErrorConfigurations)
        {
            if (configuration.ErrorDirectives.IsDefaultOrEmpty
                || compilation.GetErrorConfigurationCondition(configuration) is not { IsUnknown: false } raw)
            {
                continue;
            }

            SymbolCondition condition = compilation.GetConditionMap().Simplify(raw);

            // 木の構成と分岐の条件が両立しない (既定の木の中の、構成ごとの木が受け持つ分岐)。
            // 同じ名前の別の値 (QUALITY == 1 と QUALITY == 2) は簡約では消えないので、索引の制約でも確かめる。
            // 分岐の条件は、同じファイルのほかの単位の値 (Unreal の頂点ファクトリ) を含むことがある。
            if (condition.IsNever || !compilation.GetConditionMap().IsPossible(condition))
            {
                continue;
            }
            Location error = configuration.ErrorDirectives[0];

            if (!seen.Add((error.FilePath, error.Span.Start, condition.ToString())))
            {
                continue;
            }

            Location location = compilation.IsWrittenHere(error)
                ? error
                : Location.Create(compilation.Text, new Core.Text.TextSpan(0, 0));

            yield return new ErrorConfigurationReport(
                condition,
                condition.IsAlways ? string.Empty : $"構成：{condition} は ",
                Path.GetFileName(error.FilePath),
                error.LineSpan.Start.Line + 1,
                location);
        }
    }
}
