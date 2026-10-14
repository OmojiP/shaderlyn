using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.Unity;
using Shaderlyn.Rules;

namespace Shaderlyn.Unity.Rules;

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
        UnityRuleDescriptors.MissingUniformDeclaration,
        UnityRuleDescriptors.PropertyTypeMismatch,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasHlslPrograms || compilation.GetUnity() is not { } unity)
        {
            return;
        }

        foreach (PropertySymbol property in unity.Properties)
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
            UnityRuleDescriptors.PropertyTypeMismatch,
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

        if (compilation.GetUnity()!.IsReferencedFromShaderLab(property.Name))
        {
            return;
        }

        if (compilation.AppearsOutsideAnalyzedCode(property.Name))
        {
            return;
        }

        if (compilation.GetUnity()!.CountIdentifierOccurrences(property.Name) > 0)
        {
            return;
        }

        context.ReportDiagnostic(
            UnityRuleDescriptors.MissingUniformDeclaration,
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
        [UnityRuleDescriptors.UnusedProperty];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasHlslPrograms || !compilation.HasCompleteDependencies || compilation.GetUnity() is not { } unity)
        {
            return;
        }

        foreach (PropertySymbol property in unity.Properties)
        {
            if (unity.IsReferencedFromShaderLab(property.Name))
            {
                continue;
            }

            if (!unity.IsDeclaredButNeverUsed(property.Name))
            {
                continue;
            }

            context.ReportDiagnostic(
                UnityRuleDescriptors.UnusedProperty,
                property.Declaration.NameToken.Span,
                property.Name);
        }
    }
}
