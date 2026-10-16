using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules;

/// <summary>
/// <c>for</c> の初期化で宣言した変数を、ループの外で使っている箇所を伝える (HL0354)。
/// </summary>
/// <remarks>
/// <para>
/// FXC と DXC 2021 より前は、<c>for (int i = 0; ...)</c> の <c>i</c> を囲む波括弧の範囲へ漏らすので、ループの後でも使える。
/// HLSL 2021 は C++ と同じくループの中に閉じ込め、外で使うとエラーになる (<c>use of undeclared identifier</c>)。
/// </para>
/// <para>
/// コードをコンパイルする方言がどれも 2021 なら、名前は見つからず HL0310 になる。
/// ここで伝えるのは、今は通るが 2021 へ移すとエラーになる書き方である (情報)。
/// </para>
/// </remarks>
internal sealed class LoopVariableScopeAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.LoopVariableOutsideLoop];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ReportedLocations reported = new();

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not IdentifierExpressionSyntax identifier
                || !program.Tree.Hlsl().Features.LoopVariableOutsideLoop
                || !compilation.IsReportable(identifier.Identifier))
            {
                continue;
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            // 候補のどれもが、この名前を囲まない for 文の初期化の変数なら、ループの外で使っている。
            if (binder.LookupLocal(identifier) is not { IsEmpty: false } declarations
                || !declarations.All(d => LoopOf(d.Declarator) is { } loop && !identifier.Ancestors().Contains(loop))
                || identifier.Identifier.GetLocation() is not { } location
                || !reported.Add(location))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.LoopVariableOutsideLoop, location, identifier.Name));
        }
    }

    /// <summary>宣言子が for 文の初期化にあれば、その for 文を返す。</summary>
    /// <param name="declarator">宣言子。</param>
    /// <returns>for 文。初期化に無ければ <see langword="null"/>。</returns>
    private static ForStatementSyntax? LoopOf(ShaderSyntaxNode declarator)
        => declarator.Parent is VariableDeclarationSyntax declaration
           && declaration.Parent is ForStatementSyntax loop
           && ReferenceEquals(loop.Initializer, declaration)
            ? loop
            : null;
}
