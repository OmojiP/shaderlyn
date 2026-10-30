using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 関数の名前と変数の名前の重なりを検査する (GL0312 / GL0405)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>handleFunctionCall</c> の「can't use function syntax on variable」と、
/// <c>handleFunctionDeclarator</c> の「redefinition of built-in function」、ES の 3.00 以降の組み込みの名前の宣言し直しを移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 関数の中の変数と仮引数は、同じ名前の関数を隠す。隠された関数を呼ぶと誤りになる (GL0312)。
/// 呼び出しを含む宣言子の変数は、まだ見えないので数えない (<c>S S = S(0);</c>)。
/// デスクトップの 1.10 では関数と変数の名前の空間が分かれているので、検査しない。
/// </para>
/// <para>
/// ES では、組み込み関数と同じ形 (仮引数の型の並び) の関数を宣言できない。
/// ES の 3.00 以降は、組み込み関数と同じ名前の関数 (形が違っても) と、関数の外の変数も宣言できない (GL0405)。
/// glslang は ES の 3.00 以降で、組み込みと利用者の宣言を同じ名前の範囲に置く (setNoBuiltInRedeclarations)。
/// </para>
/// </remarks>
internal sealed class GlslFunctionNameAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.VariableCalled, GlslRuleDescriptors.BuiltinRedeclared];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, (GlslRequirements Requirement, GlslTypeSystem Types)?> states = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (InvocationExpressionSyntax or FunctionDeclarationSyntax or VariableDeclaratorSyntax))
            {
                continue;
            }

            if (!states.TryGetValue(program, out (GlslRequirements Requirement, GlslTypeSystem Types)? state))
            {
                state = states[program] =
                    GlslRequirements.Create(context, compilation, program, reported) is { } requirement
                    && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                        ? (requirement, types)
                        : null;
            }

            if (state is not { } s)
            {
                continue;
            }

            switch (node)
            {
                // 1.10 では、関数と変数の名前の空間が分かれている (glslang の setSeparateNameSpaces)。
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation when s.Requirement.IsEs || s.Requirement.Version != 110:
                    CheckCall(s.Requirement, compilation.GetExpressionTypeBinder(program), invocation, callee);
                    break;

                case FunctionDeclarationSyntax { Parent: CompilationUnitSyntax, NameToken.IsMissing: false } function when s.Requirement.IsEs:
                    CheckFunction(s.Requirement, s.Types, function);
                    break;

                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: CompilationUnitSyntax } } variable
                    when s.Requirement.IsEs && s.Requirement.Version >= 300 && !s.Types.Builtins.GetFunctions(variable.Name).IsEmpty:
                    s.Requirement.Report(
                        GlslRuleDescriptors.BuiltinRedeclared,
                        variable.NameToken,
                        variable.Name,
                        "ES の 3.00 以降では、関数の外の変数に組み込み関数の名前を使えません。");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>関数の中の変数か仮引数に隠された関数を呼んでいないか。</summary>
    private static void CheckCall(GlslRequirements requirement, ExpressionTypeBinder binder, InvocationExpressionSyntax invocation, IdentifierExpressionSyntax callee)
    {
        NameResolution resolution = binder.ResolveName(callee);

        if (resolution.Kind != DeclaredNameKind.Local
            || resolution.Candidates.Any(c => invocation.Ancestors().Contains(c.Declarator)))
        {
            return;
        }

        requirement.Report(GlslRuleDescriptors.VariableCalled, callee.Identifier, callee.Name);
    }

    /// <summary>ES で組み込み関数を宣言し直していないか。</summary>
    private static void CheckFunction(GlslRequirements requirement, GlslTypeSystem types, FunctionDeclarationSyntax function)
    {
        ImmutableArray<GlslBuiltinFunction> overloads = types.Builtins.GetFunctions(function.Name);

        if (overloads.IsEmpty)
        {
            return;
        }

        if (requirement.Version >= 300)
        {
            requirement.Report(
                GlslRuleDescriptors.BuiltinRedeclared,
                function.NameToken,
                function.Name,
                "ES の 3.00 以降では、組み込み関数と同じ名前の関数を宣言できません。");
            return;
        }

        ParameterSyntax[] parameters = [.. function.ParameterList.Where(p => p.Type.Name != "void" || !p.NameToken.IsMissing)];

        if (parameters.Any(p => p.ArrayDimensions > 0)
            || !overloads.Any(f => !f.IsVariadic
                                   && f.Parameters.Length == parameters.Length
                                   && f.Parameters.Zip(parameters).All(pair => SameType(types, pair.First, pair.Second.Type.Name))))
        {
            return;
        }

        requirement.Report(
            GlslRuleDescriptors.BuiltinRedeclared,
            function.NameToken,
            function.Name,
            "ES では、組み込み関数と同じ形の関数を宣言できません。");
    }

    /// <summary>2 つの型の名前が同じ型を指すか (<c>mat3</c> と <c>mat3x3</c> など)。</summary>
    private static bool SameType(GlslTypeSystem types, string left, string right)
        => string.Equals(left, right, StringComparison.Ordinal)
           || (types.TryDescribeNumeric(left, out ShaderNumericShape a) && types.TryDescribeNumeric(right, out ShaderNumericShape b) && a == b);
}
