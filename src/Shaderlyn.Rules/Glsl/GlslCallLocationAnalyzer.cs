using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 呼べる場所の決まった組み込み関数を、その場所で検査する (GL0305)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>checkLocation</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// テッセレーションの制御シェーダーの <c>barrier()</c> と、<c>beginInvocationInterlockARB()</c> / <c>endInvocationInterlockARB()</c> は、
/// main の中で、流れを分ける文 (glslang の controlFlowNestingLevel が増える所) の外に、<c>return;</c> より前に書く。
/// interlock の 2 つは、ファイルの中でそれぞれ 1 回だけ、begin を end より前に呼ぶ。
/// </para>
/// <para>
/// 流れを分ける文は、if の枝、while・do・for の全体 (条件を含む)、switch の本体、<c>?:</c> の 2 つの枝。
/// if と switch の条件、<c>?:</c> の条件は含まない。glslang の文法の動作 (glslang.y) に合わせている。
/// </para>
/// <para>
/// テクスチャとサンプラーから作るサンプラーの値 (<c>sampler2D(t, s)</c>) は、使う場所 (組み込み関数の実引数など) に直接書く。
/// 利用者の関数の実引数、<c>?:</c> の枝、カンマの演算子の項には書けない (glslang の <c>samplerConstructorLocationCheck</c>)。
/// </para>
/// </remarks>
internal sealed class GlslCallLocationAnalyzer : SemanticRuleAnalyzer
{
    private const string Barrier = "barrier";
    private const string BeginInterlock = "beginInvocationInterlockARB";
    private const string EndInterlock = "endInvocationInterlockARB";

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.CallNotAllowedHere];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, State?> states = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation)
            {
                continue;
            }

            if (!states.TryGetValue(program, out State? state))
            {
                state = states[program] =
                    GlslRequirements.Create(context, compilation, program, reported) is { } requirement
                    && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                        ? new State(requirement, types)
                        : null;
            }

            if (state is not null && IsSamplerConstructor(invocation, callee))
            {
                CheckSamplerConstructor(compilation, program, state, invocation, callee);
                continue;
            }

            if (callee.Name is not (Barrier or BeginInterlock or EndInterlock))
            {
                continue;
            }

            if (state is null
                || state.Types.Builtins.GetFunctions(callee.Name).IsEmpty
                || !compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty)
            {
                continue;
            }

            if (callee.Name == Barrier && state.Requirement.Stage != GlslShaderStage.TessellationControl)
            {
                continue;
            }

            if (Problem(callee, state) is { } problem)
            {
                state.Requirement.Report(GlslRuleDescriptors.CallNotAllowedHere, callee.Identifier, callee.Name, problem);
            }
        }
    }

    /// <summary>テクスチャとサンプラーから作るサンプラーの値 (<c>sampler2D(t, s)</c>) か。</summary>
    private static bool IsSamplerConstructor(InvocationExpressionSyntax invocation, IdentifierExpressionSyntax callee)
        => callee.Name.TrimStart('i', 'u').StartsWith("sampler", StringComparison.Ordinal)
           && GlslKeywords.IsBuiltInType(callee.Name)
           && invocation.ArgumentExpressions.Count() == 2;

    /// <summary>サンプラーの値の作成を、使う場所に直接書いているか。</summary>
    private static void CheckSamplerConstructor(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        State state,
        InvocationExpressionSyntax invocation,
        IdentifierExpressionSyntax callee)
    {
        ShaderSyntaxNode node = invocation;

        while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
        {
            node = parenthesized;
        }

        string? place = node.Parent switch
        {
            InvocationExpressionSyntax { Target: IdentifierExpressionSyntax function } call
                when !ReferenceEquals(call.Target, node) && !compilation.GetFunctionDeclarations(function.Name, program).IsEmpty
                => "利用者の関数の実引数",
            ConditionalExpressionSyntax conditional when !ReferenceEquals(conditional.Condition, node) => "?: の枝",
            BinaryExpressionSyntax { OperatorToken.Text: "," } => "カンマの演算子の項",
            _ => null,
        };

        if (place is not null)
        {
            state.Requirement.Report(
                GlslRuleDescriptors.CallNotAllowedHere,
                callee.Identifier,
                callee.Name,
                $"使う場所 (組み込み関数の実引数など) に直接書かなければなりません。{place}には書けません");
        }
    }

    /// <summary>呼んだ場所の問題。glslang の checkLocation が最初に出す誤りに当たるもの。</summary>
    private static string? Problem(IdentifierExpressionSyntax callee, State state)
    {
        FunctionDeclarationSyntax? function = callee.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault();
        bool inMain = function is { Name: "main", Body: not null };
        bool nested = IsInFlowControl(callee);
        bool afterReturn = inMain && function!.Body!.DescendantNodesAndSelf()
            .OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression is null && r.Span.End <= callee.Span.Start);

        string? problem = callee.Name == Barrier && nested ? "流れを分ける文 (if・ループ・switch・?:) の中では呼べません"
            : !inMain ? "main の中でしか呼べません"
            : afterReturn ? "main の return の後では呼べません"
            : nested ? "流れを分ける文 (if・ループ・switch・?:) の中では呼べません"
            : null;

        // 呼んだ回数は、場所の誤りがあっても数える (glslang と同じ)。
        switch (callee.Name)
        {
            case BeginInterlock:
                problem ??= state.BeginCount > 0 ? "1 回しか呼べません"
                    : state.EndCount > 0 ? "endInvocationInterlockARB() より前に呼ばなければなりません"
                    : null;
                state.BeginCount++;
                break;

            case EndInterlock:
                problem ??= state.EndCount > 0 ? "1 回しか呼べません"
                    : state.BeginCount == 0 ? "beginInvocationInterlockARB() の後で呼ばなければなりません"
                    : null;
                state.EndCount++;
                break;

            default:
                break;
        }

        return problem;
    }

    /// <summary>流れを分ける文の中か (glslang の controlFlowNestingLevel が 0 より大きいか)。関数の本体までを見る。</summary>
    private static bool IsInFlowControl(ShaderSyntaxNode node)
    {
        for (SyntaxNode child = node; child.Parent is { } parent && parent is not FunctionDeclarationSyntax; child = parent)
        {
            bool nested = parent switch
            {
                IfStatementSyntax statement => child != statement.Condition,
                SwitchStatementSyntax statement => child != statement.Expression,
                ConditionalExpressionSyntax expression => child != expression.Condition,
                WhileStatementSyntax or DoWhileStatementSyntax or ForStatementSyntax => true,
                _ => false,
            };

            if (nested)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>1 つの木の状態。interlock の関数を呼んだ回数を数える。</summary>
    private sealed class State(GlslRequirements requirement, GlslTypeSystem types)
    {
        public GlslRequirements Requirement => requirement;

        public GlslTypeSystem Types => types;

        public int BeginCount { get; set; }

        public int EndCount { get; set; }
    }
}
