using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 利用者の関数の呼び出しに合う多重定義が 2 つ以上あり、どれを呼ぶか決まらないものを検査する (GL0320)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>findFunction120</c> (「ambiguous function signature match」) と、
/// <c>findFunction400</c> / <c>findFunctionExplicitTypes</c> と ParseContextBase.cpp の <c>selectFunction</c> (「ambiguous best function under implicit type conversion」) を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 型がすべて同じ宣言があれば、それを呼ぶ。無ければ、暗黙の変換で合う宣言を探す。
/// デスクトップの 1.20 から 4.00 より前 (<c>GL_ARB_gpu_shader5</c> などが無いとき) と、ES の <c>GL_EXT_shader_implicit_conversions</c> では、
/// 合う宣言が 2 つ以上あれば誤りである。4.00 からと、明示的な幅の算術型の拡張を有効にしたときは、
/// 仮引数ごとの変換の良さを比べていちばん良い宣言を選び、決まらなければ誤りである
/// (<see cref="GlslTypeSystem.IsBetterConversion"/>)。
/// </para>
/// <para>
/// GLSL では関数を呼ぶ位置より前に宣言しなければならないので、呼び出しより前の宣言だけを候補にする。
/// 仮引数と実引数がどれも数値の型 (配列でない) のときだけ見る。同じ名前の組み込み関数がある名前は見ない。
/// </para>
/// </remarks>
internal sealed class GlslOverloadAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.AmbiguousCall];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.SymbolVariants.IsEmpty)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation
                || compilation.TypeSystem.ForTree(program.Tree) is not GlslTypeSystem types
                || types.FunctionMatching == GlslFunctionMatching.Exact
                || !types.Builtins.GetFunctions(callee.Name).IsEmpty
                || compilation.MayLackOverloadsOf(callee.Name, invocation))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is not null && FindTie(compilation, program, types, invocation, callee.Name) is { } tie)
            {
                requirement.Report(GlslRuleDescriptors.AmbiguousCall, invocation.CloseParen, callee.Name, tie.First, tie.Second);
            }
        }
    }

    /// <summary>仮引数 1 つ。型の名前と形、値を受け取るか、書き戻すか。</summary>
    private readonly record struct Parameter(string TypeName, ShaderNumericShape Shape, bool Input, bool Output);

    /// <summary>どれを呼ぶか決まらない、2 つの宣言の仮引数の型を返す。決まるか、判断できなければ <see langword="null"/>。</summary>
    private static (string First, string Second)? FindTie(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        GlslTypeSystem types,
        InvocationExpressionSyntax invocation,
        string name)
    {
        ImmutableArray<ExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];
        ExpressionTypeEvaluator evaluator = compilation.GetExpressionTypeBinder(program).GetEvaluatorFor(invocation);
        List<ShaderNumericShape> argumentShapes = [];

        foreach (ExpressionSyntax argument in arguments)
        {
            if (evaluator.Evaluate(argument) is not { } type || !types.TryDescribeNumeric(type, out ShaderNumericShape shape))
            {
                return null;
            }

            argumentShapes.Add(shape);
        }

        // 呼び出しより前の宣言。プロトタイプと定義は同じ形なので 1 つにまとめる。
        Dictionary<string, Parameter[]> candidates = new(StringComparer.Ordinal);

        foreach (FunctionDeclarationSyntax declaration in compilation.GetFunctionDeclarations(name, program))
        {
            if (!ReferenceEquals(declaration.FirstToken?.Source, invocation.FirstToken?.Source) || declaration.Span.Start >= invocation.Span.Start)
            {
                continue;
            }

            ImmutableArray<ParameterSyntax> parameters = [.. declaration.ParameterList];

            if (parameters.Length != arguments.Length)
            {
                continue;
            }

            Parameter[] signature = new Parameter[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsArray || !types.TryDescribeNumeric(parameters[i].Type.Name, out ShaderNumericShape shape))
                {
                    return null;
                }

                bool output = parameters[i].ModifierTokens.Any(t => t.Text is "out" or "inout");
                bool input = !parameters[i].ModifierTokens.Any(t => t.Text == "out");
                signature[i] = new Parameter(parameters[i].Type.Name, shape, input, output);
            }

            candidates.TryAdd(Describe(signature), signature);
        }

        // 型がすべて同じ宣言があれば、それを呼ぶ。
        if (candidates.Values.Any(c => c.Select((p, i) => p.Shape == argumentShapes[i]).All(same => same)))
        {
            return null;
        }

        List<Parameter[]> viable = [.. candidates.Values.Where(c => c.Select((p, i) => IsViable(types, argumentShapes[i], p)).All(ok => ok))];

        if (viable.Count < 2)
        {
            return null;
        }

        if (types.FunctionMatching == GlslFunctionMatching.Promotion)
        {
            return (Describe(viable[0]), Describe(viable[1]));
        }

        // selectFunction: 前から順に、良い宣言を残す。残った宣言より良いか同じ良さの宣言があれば、決まらない。
        Parameter[] incumbent = viable[0];

        foreach (Parameter[] candidate in viable.Skip(1))
        {
            if (HasBetterParameter(types, argumentShapes, incumbent, candidate) && !HasBetterParameter(types, argumentShapes, candidate, incumbent))
            {
                incumbent = candidate;
            }
        }

        foreach (Parameter[] candidate in viable)
        {
            if (!ReferenceEquals(candidate, incumbent)
                && (HasBetterParameter(types, argumentShapes, incumbent, candidate) || !HasBetterParameter(types, argumentShapes, candidate, incumbent)))
            {
                return (Describe(incumbent), Describe(candidate));
            }
        }

        return null;
    }

    /// <summary>実引数を仮引数に渡せるか。形が同じで、受け取る向きと書き戻す向きの要素の型の変換があること。</summary>
    private static bool IsViable(GlslTypeSystem types, ShaderNumericShape argument, Parameter parameter)
        => argument.Kind == parameter.Shape.Kind
           && argument.Rows == parameter.Shape.Rows
           && argument.Columns == parameter.Shape.Columns
           && (!parameter.Input || types.IsBaseConvertible(argument.BaseName, parameter.Shape.BaseName))
           && (!parameter.Output || types.IsBaseConvertible(parameter.Shape.BaseName, argument.BaseName));

    /// <summary><paramref name="second"/> のほうが良い変換の仮引数が 1 つでもあるか (selectFunction の betterParam)。</summary>
    private static bool HasBetterParameter(GlslTypeSystem types, List<ShaderNumericShape> arguments, Parameter[] first, Parameter[] second)
        => arguments.Select((a, i) => types.IsBetterConversion(a.BaseName, first[i].Shape.BaseName, second[i].Shape.BaseName)).Any(better => better);

    /// <summary>仮引数の型を並べた文字列 (<c>(float, int)</c>)。</summary>
    private static string Describe(Parameter[] signature)
        => $"({string.Join(", ", signature.Select(p => (p.Output ? p.Input ? "inout " : "out " : string.Empty) + p.TypeName))})";
}
