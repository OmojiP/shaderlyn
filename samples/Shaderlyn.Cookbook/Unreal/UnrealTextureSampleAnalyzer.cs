using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// Unreal Engine のシェーダーで、<c>Texture2D</c> の <c>Sample</c> メソッドを直接呼んでいる箇所を報告し、
/// <c>Common.ush</c> の <c>Texture2DSample</c> を使用するよう促す。
/// </summary>
/// <remarks>
/// <para>
/// <b>Unreal Engine の環境だけに当てる例である。</b> Unreal の <c>.usf</c>・<c>.ush</c> は HLSL なので、<see cref="Languages"/> は既定の
/// <see cref="ShaderLanguages.Hlsl"/> のままにし、環境の名前 (<see cref="Semantics.Environments.IShaderEnvironment.Name"/>) で絞る。
/// 汎用の HLSL や Unity のファイルには当たらない。
/// </para>
/// <para>
/// <c>Texture2DSample</c> は <c>/Engine/Private/Common.ush</c> が定義する。取り込んでいないファイルでは置き換えられないので、
/// 関数の名前 (<see cref="Semantics.AnalyzedProgram.FunctionNames"/>) に無ければ報告しない。<c>Texture2DSample</c> の定義の中の呼び出しも報告しない。
/// </para>
/// <para>
/// 型は <see cref="Semantics.Programs.ExpressionTypeBinder"/> で求める。求まらない場合は報告しない。
/// </para>
/// </remarks>
public sealed class UnrealTextureSampleAnalyzer : ShaderRuleAnalyzer
{
    /// <summary>Unreal Engine の環境の名前 (設定ファイルの <c>environment: unreal</c>)。</summary>
    private const string UnrealEnvironment = "unreal";

    /// <summary>置き換え先の関数。</summary>
    private const string Helper = "Texture2DSample";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.UnrealTextureSample];

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Compilation.Environment.Name != UnrealEnvironment
                || c.Node.Target is not MemberAccessExpressionSyntax { Name: "Sample" } member
                || !c.Program.FunctionNames.Contains(Helper)
                || c.Node.FirstAncestorOrDefault<FunctionDeclarationSyntax>()?.Name == Helper
                || !c.Compilation.IsReportable(member.NameToken))
            {
                return;
            }

            string? type = c.Compilation.GetExpressionTypeBinder(c.Program)
                .GetEvaluatorFor(member.Target)
                .Evaluate(member.Target);

            if (type is "Texture2D" or "Texture2D<float4>")
            {
                c.ReportDiagnostic(CookbookRules.UnrealTextureSample, member.NameToken.Span, Helper);
            }
        });
    }
}
