using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// 意味解析の結果を必要とするアナライザの共通の土台。
/// </summary>
/// <remarks>
/// <para>
/// セマンティックモデルの取り出しと、<b>モデルが無いときに何もしない</b>という規律を
/// 1 か所にまとめている。各アナライザが個別に <see langword="null"/> 判定を書くと、
/// いずれ判定を忘れたアナライザが混ざり、モデルの無い環境で例外を投げる。
/// </para>
/// <para>
/// モデルが用意されるかどうかは呼び出し側の設定次第である。
/// 用意されていない場合、これらのルールは「問題が無い」のではなく「検査していない」。
/// その事実は <see cref="SkippedCheckAnalyzer"/> が別途報告する。
/// </para>
/// </remarks>
internal abstract class SemanticRuleAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSyntaxTreeAction(treeContext =>
        {
            if (treeContext.Unit.GetModel<ShaderCompilation>() is { } compilation)
            {
                Analyze(treeContext, compilation);
            }
        });
    }

    /// <summary>セマンティックモデルを使って検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    protected abstract void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation);
}

/// <summary>
/// 依存関係が解決できず対応検査を行えなかったことを報告する (CF0101)。
/// </summary>
/// <remarks>
/// <b>「検査していない」ことを伝えないままにしてはならない。</b>
/// 指摘が出ないことを「問題が無い」と受け取られると、
/// 検査が素通りしたまま CI が緑になり続ける。
/// これは誤検出より発見が遅れ、結果として高くつく。
/// </remarks>
internal sealed class SkippedCheckAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>メッセージに列挙する未解決 include の最大数。</summary>
    /// <remarks>
    /// すべて並べると 1 行が数千文字になる。原因の見当をつけるには数件で足りる。
    /// </remarks>
    private const int MaxListedIncludes = 3;

    /// <summary>Unreal の環境の名前 (<c>UnrealOptions.EnvironmentName</c>)。</summary>
    private const string UnrealEnvironmentName = "unreal";

    /// <summary>解決できなかった取り込みが、どれもビルドのときにエンジンが生成するファイルかを判定する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>どれもそうなら <see langword="true"/>。解決できなかった取り込みが無ければ <see langword="false"/>。</returns>
    internal static bool IsOnlyGeneratedIncludeMissing(ShaderCompilation compilation)
        => !compilation.UnresolvedIncludes.IsEmpty
           && compilation.UnresolvedIncludes.All(compilation.Environment.IsGeneratedInclude);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SemanticRuleDescriptors.IncompleteAnalysis];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasHlslPrograms || compilation.HasCompleteDependencies)
        {
            return;
        }

        // Unreal でエンジンが生成するファイルだけが欠けているなら、Unreal のルール (UE0001 / UE0002) が直し方を添えて伝える。
        // このアセンブリは Shaderlyn.Unreal を参照できないので、環境は名前で見分ける。
        if (compilation.Environment.Name == UnrealEnvironmentName && IsOnlyGeneratedIncludeMissing(compilation))
        {
            return;
        }

        // 報告位置はファイルの先頭にする。原因は特定の行ではなくファイル全体の解析状況にある。
        context.ReportDiagnostic(
            SemanticRuleDescriptors.IncompleteAnalysis,
            new Core.Text.TextSpan(0, 0),
            DescribeSkipped(compilation),
            DescribeReason(compilation));
    }

    /// <summary>行っていない検査を言う。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>メッセージの主語。</returns>
    /// <remarks>
    /// Unity (ファイル全体の木を持つ環境) では、プロパティと uniform の対応検査が主である。
    /// ファイル全体の木を持たない環境 (汎用の HLSL) には対応検査が無いので、「無いこと」を根拠にする検査を挙げる。
    /// </remarks>
    private static string DescribeSkipped(ShaderCompilation compilation)
        => compilation.Source.Root is null
            ? "宣言が無いことを根拠にする検査 (CF0301 / CF0310 など)"
            : "プロパティと uniform の対応検査";

    /// <summary>
    /// 検査を行えなかった理由を短くまとめる。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>メッセージへ埋め込む説明。</returns>
    /// <remarks>
    /// <para>
    /// <b>「解析できませんでした」だけでは利用者は何もできない。</b>
    /// 未解決の include なのか、展開後のコードの問題なのかで、対処はまったく違う。
    /// 前者はオプションの指定で直り、後者は解析対象そのものに原因がある。
    /// どちらであるかと、具体的にどこかを必ず示す。
    /// </para>
    /// <para>
    /// <b>別に報告されている指摘を、ここで繰り返さない。</b>
    /// 解析対象のファイル自身にある構文エラーは <c>CF0001</c> などとして該当行に出る。
    /// 同じ文面をこちらにも並べると、読み手は 2 件別の問題があると受け取る。
    /// </para>
    /// </remarks>
    private static string DescribeReason(ShaderCompilation compilation)
    {
        // ビルドのときに生成されるファイルだけが無いなら、設定の誤りではない。そう伝えないと、利用者は探索パスを探し回る。
        if (IsOnlyGeneratedIncludeMissing(compilation))
        {
            return "ビルドのときにエンジンが生成するファイルの中身が分からないため、宣言の一覧が不完全です。"
                + $"生成されるファイル: {string.Join(", ", compilation.UnresolvedIncludes.Take(MaxListedIncludes))}"
                + (compilation.UnresolvedIncludes.Length > MaxListedIncludes ? $" ほか {compilation.UnresolvedIncludes.Length - MaxListedIncludes} 件" : string.Empty);
        }

        if (!compilation.UnresolvedIncludes.IsEmpty)
        {
            IEnumerable<string> listed = compilation.UnresolvedIncludes.Take(MaxListedIncludes);
            int remaining = compilation.UnresolvedIncludes.Length - MaxListedIncludes;

            string suffix = remaining > 0 ? $" ほか {remaining} 件" : string.Empty;
            return "include を解決できないため、uniform の一覧が不完全です。"
                + $"解決できなかった include: {string.Join(", ", listed)}{suffix}";
        }

        // 検査を止めるのはエラーだけである (ShaderCompilation.BreaksAnalysis)。警告を原因として示さない。
        if (compilation.ReportableHlslDiagnostics.FirstOrDefault(ShaderCompilation.BreaksAnalysis) is { } reported)
        {
            return "このファイルの HLSL を解析できませんでした。"
                + $"{reported.Location.LineSpan.Start} の {reported.Id} を直すと検査されるようになります。";
        }

        // include 先のヘッダの中の問題は、それ自体は報告していない。
        // ここで示さないと、利用者は原因に辿り着く手がかりを一切持てない。
        if (compilation.HlslDiagnostics.FirstOrDefault(ShaderCompilation.BreaksAnalysis) is not { } first)
        {
            return "原因を特定できませんでした。";
        }

        return "include したファイルの HLSL を解析できませんでした。"
            + $"{Path.GetFileName(first.Location.FilePath)}({first.Location.LineSpan.Start}): "
            + $"{first.Id} {first.GetMessage()}";
    }
}
