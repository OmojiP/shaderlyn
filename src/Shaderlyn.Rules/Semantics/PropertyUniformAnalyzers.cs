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
/// 依存関係が解決できず対応検査を行えなかったことを報告する (SL0002)。
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

        // 報告位置はファイルの先頭にする。原因は特定の行ではなくファイル全体の解析状況にある。
        context.ReportDiagnostic(
            SemanticRuleDescriptors.IncompleteAnalysis,
            new Core.Text.TextSpan(0, 0),
            DescribeReason(compilation));
    }

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
    /// 解析対象のファイル自身にある構文エラーは <c>HL0001</c> などとして該当行に出る。
    /// 同じ文面をこちらにも並べると、読み手は 2 件別の問題があると受け取る。
    /// </para>
    /// </remarks>
    private static string DescribeReason(ShaderCompilation compilation)
    {
        if (!compilation.UnresolvedIncludes.IsEmpty)
        {
            IEnumerable<string> listed = compilation.UnresolvedIncludes.Take(MaxListedIncludes);
            int remaining = compilation.UnresolvedIncludes.Length - MaxListedIncludes;

            string suffix = remaining > 0 ? $" ほか {remaining} 件" : string.Empty;
            return "include を解決できないため、uniform の一覧が不完全です。"
                + $"解決できなかった include: {string.Join(", ", listed)}{suffix}";
        }

        if (!compilation.ReportableHlslDiagnostics.IsEmpty)
        {
            Diagnostic reported = compilation.ReportableHlslDiagnostics[0];
            return "このファイルの HLSL を解析できませんでした。"
                + $"{reported.Location.LineSpan.Start} の {reported.Id} を直すと検査されるようになります。";
        }

        ImmutableArray<Diagnostic> hlslDiagnostics = compilation.HlslDiagnostics;

        if (hlslDiagnostics.IsEmpty)
        {
            return "原因を特定できませんでした。";
        }

        // include 先のヘッダの中の問題は、それ自体は報告していない。
        // ここで示さないと、利用者は原因に辿り着く手がかりを一切持てない。
        Diagnostic first = hlslDiagnostics[0];
        return "include したファイルの HLSL を解析できませんでした。"
            + $"{Path.GetFileName(first.Location.FilePath)}({first.Location.LineSpan.Start}): "
            + $"{first.Id} {first.GetMessage()}";
    }
}

/// <summary>
/// Properties と HLSL の uniform の対応を検査する (SL1001 / SL1003)。
/// </summary>
/// <remarks>
/// <para>
/// <b>このルールが M3 の中心である。</b>
/// プロパティと uniform の食い違いは、コンパイルも通り描画も走るのに
/// 「設定した値が反映されない」という形でしか現れない。
/// 実行して初めて気づく種類の不具合であり、静的解析が最も価値を出せる対象である。
/// </para>
/// <para>
/// 誤検出を避けるための条件が多い。単一構成での展開では
/// 別の構成でのみ宣言される uniform が見えないため、
/// 「見つからない」ことをそのまま根拠にすると、正しいシェーダーを誤りとして報告してしまう。
/// </para>
/// </remarks>
internal sealed class PropertyUniformAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        SemanticRuleDescriptors.MissingUniformDeclaration,
        SemanticRuleDescriptors.PropertyTypeMismatch,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasHlslPrograms)
        {
            return;
        }

        foreach (PropertySymbol property in compilation.Properties)
        {
            IReadOnlyList<UniformSymbol> uniforms = compilation.GetUniforms(property.Name);

            if (uniforms.Count > 0)
            {
                // Pass ごとに違う型で宣言していることがある。最初の 1 つだけを見ると、ほかの Pass の食い違いを見落とす。
                // 同じ型の宣言は Pass と構成の数だけあるので、型ごとに 1 度だけ見る。
                foreach (UniformSymbol uniform in uniforms.DistinctBy(u => u.TypeName, StringComparer.Ordinal))
                {
                    if (CheckType(context, property, uniform))
                    {
                        break;
                    }
                }

                continue;
            }

            CheckMissingDeclaration(context, compilation, property);
        }
    }

    /// <summary>
    /// プロパティの型と uniform の型の食い違いを検査する (SL1003)。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="property">対象のプロパティ。</param>
    /// <param name="uniform">対応する uniform。</param>
    /// <returns>報告した場合は <see langword="true"/>。1 つのプロパティについて 1 度だけ報告する。</returns>
    /// <remarks>
    /// <b>このルールは依存関係が不完全でも実行してよい。</b>
    /// 「見つかった uniform の型が食い違っている」という判断は、
    /// ほかに何が見つかっていないかとは無関係に成り立つ。
    /// 見つからなかったものを根拠にする SL1001 とは、この点で性質が違う。
    /// </remarks>
    private static bool CheckType(
        SyntaxTreeAnalysisContext context,
        PropertySymbol property,
        UniformSymbol uniform)
    {
        if (PropertyTypeCompatibility.IsCompatible(property.Kind, uniform.TypeClass))
        {
            return false;
        }

        context.ReportDiagnostic(
            SemanticRuleDescriptors.PropertyTypeMismatch,
            property.Declaration.NameToken.Span,
            property.Name,
            property.Kind.ToDisplayName(),
            uniform.TypeName,
            PropertyTypeCompatibility.DescribeExpectedType(property.Kind));

        return true;
    }

    /// <summary>
    /// プロパティに対応する宣言がまったく無いことを検査する (SL1001)。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="property">対象のプロパティ。</param>
    /// <remarks>
    /// <para>
    /// 「無いこと」を根拠にする以上、<b>見えていないだけの可能性をすべて潰してから</b>報告する。
    /// </para>
    /// <list type="number">
    ///   <item><description>依存関係が完全に解決できていること。include が読めていなければ判断できない</description></item>
    ///   <item><description>インスペクタに表示されるプロパティであること</description></item>
    ///   <item><description>ShaderLab 側から参照されていないこと (<c>Cull [_Cull]</c> の形)</description></item>
    ///   <item><description>非活性領域にも名前が現れないこと。別の構成では宣言されているかもしれない</description></item>
    ///   <item><description>展開後のコードのどこにも名前が現れないこと</description></item>
    /// </list>
    /// <para>
    /// 最後の条件は、このツールが宣言として認識できなかった書き方への保険である。
    /// 名前がどこかに現れているなら、認識できていないだけで使われている可能性がある。
    /// </para>
    /// </remarks>
    private static void CheckMissingDeclaration(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        PropertySymbol property)
    {
        if (!compilation.HasCompleteDependencies)
        {
            return;
        }

        // [HideInInspector] のプロパティは人が設定するものではない。
        // マテリアルの状態を保持したり、エディタ拡張がシンボルを切り替えるために
        // 読み書きしたりする用途で使われる。
        // このルールが伝えたいのは「設定しても効果が無い」ことなので、
        // そもそも人が設定しないプロパティには当てはまらない。
        if (property.HasAttribute("HideInInspector"))
        {
            return;
        }

        if (compilation.IsReferencedFromShaderLab(property.Name))
        {
            return;
        }

        if (compilation.AppearsOutsideAnalyzedCode(property.Name))
        {
            return;
        }

        if (compilation.CountIdentifierOccurrences(property.Name) > 0)
        {
            return;
        }

        context.ReportDiagnostic(
            SemanticRuleDescriptors.MissingUniformDeclaration,
            property.Declaration.NameToken.Span,
            property.Name);
    }
}

/// <summary>
/// 宣言されているだけで使われていないプロパティを検査する (SL1004)。
/// </summary>
internal sealed class UnusedPropertyAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SemanticRuleDescriptors.UnusedProperty];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasHlslPrograms || !compilation.HasCompleteDependencies)
        {
            return;
        }

        foreach (PropertySymbol property in compilation.Properties)
        {
            if (compilation.IsReferencedFromShaderLab(property.Name))
            {
                continue;
            }

            if (!compilation.IsDeclaredButNeverUsed(property.Name))
            {
                continue;
            }

            context.ReportDiagnostic(
                SemanticRuleDescriptors.UnusedProperty,
                property.Declaration.NameToken.Span,
                property.Name);
        }
    }
}
