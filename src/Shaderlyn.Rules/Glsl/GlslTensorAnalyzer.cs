using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// テンソル (<c>GL_ARM_tensors</c> の <c>tensorARM&lt;T, rank&gt;</c>) の組み込み関数の実引数を検査する (GL0303 / GL0304 / GL0306)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>tensorReadARM</c>・<c>tensorWriteARM</c>・<c>tensorSizeARM</c> の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// <c>tensorSizeARM(t, dim)</c> の <c>dim</c> は定数で、階数より小さくなければならない。
/// <c>tensorReadARM</c> / <c>tensorWriteARM</c> の座標の配列の要素の数は階数と同じで、データの型は要素の型と同じでなければならない (変換しない)。
/// 範囲の外の値 (<c>gl_TensorOperandsOutOfBoundsValueARM</c>) は読むときだけ書け、続けて要素と同じ型の定数を書く。
/// </para>
/// <para>
/// テンソルの型と階数は、名前の宣言の型から求める。分からなければ見ない。
/// </para>
/// </remarks>
internal sealed class GlslTensorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.ConstantArgumentRequired, GlslRuleDescriptors.ArgumentOutOfRange, GlslRuleDescriptors.VariableArgumentRequired, HlslRuleDescriptors.ArgumentNotConvertible];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.CodeTokens.Any(t => t.Text == "tensorARM"))
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } call)
            {
                continue;
            }

            if (callee.Name is not ("tensorReadARM" or "tensorWriteARM" or "tensorSizeARM"))
            {
                if (compilation.GetFunctionDeclarations(callee.Name, program) is [var function, ..] declarations
                    && declarations.All(d => d.ParameterList.Count() == function.ParameterList.Count())
                    && (requirements.TryGetValue(program, out GlslRequirements? caller) ? caller : requirements[program] = GlslRequirements.Create(context, compilation, program, reported)) is { } callerRequirement)
                {
                    CheckTensorParameters(callerRequirement, compilation.GetExpressionTypeBinder(program), callee, function, [.. call.ArgumentExpressions]);
                }

                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

            if (requirement is null || arguments.Length < 2 || TensorOf(binder, arguments[0]) is not { } tensor)
            {
                continue;
            }

            GlslConstantEvaluator evaluator = new(binder);
            string name = callee.Name;
            ShaderSyntaxToken at = callee.Identifier;

            if (name == "tensorSizeARM")
            {
                if (evaluator.Evaluate(arguments[1]) is not { } dim)
                {
                    requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, "tensorSizeARM の次元の実引数");
                }
                else if (tensor.Rank is { } rank && (dim.Integer < 0 || dim.Integer >= rank))
                {
                    requirement.Report(GlslRuleDescriptors.ArgumentOutOfRange, at, "tensorSizeARM の次元の実引数", $"0 から {rank - 1} (テンソルの階数より小さい数)");
                }

                continue;
            }

            // 書けないテンソル (readonly) には書けず、読めないテンソル (writeonly) からは読めない (glslang の「argument cannot drop memory qualifier」)。
            string dropped = name == "tensorWriteARM" ? "readonly" : "writeonly";

            if (arguments[0] is IdentifierExpressionSyntax target
                && binder.ResolveName(target).Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: CompilationUnitSyntax } declaration } }]
                && declaration.HasModifier(dropped))
            {
                requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 1, $"{dropped} でないテンソルでなければなりません");
            }

            if (tensor.Rank is { } coordinates && CoordinateCount(binder, arguments[1]) is { } count && count != coordinates)
            {
                requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 2, $"テンソルの階数と同じ {coordinates} 個の要素の座標の配列でなければなりません (今は {count} 個)");
            }

            if (arguments.Length >= 3 && TypeOf(binder, arguments[2]) is { } data && tensor.Element.Length > 0 && Canonical(data) != Canonical(tensor.Element))
            {
                requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 3, $"テンソルの要素と同じ型 ({tensor.Element}) でなければなりません (今は {data})");
            }

            // テンソルの演算子の指定 (4 つ目の実引数) は、定数の整数の式でなければならない。
            if (arguments.Length >= 4 && evaluator.IsCertainlyNotConstant(arguments[3]))
            {
                requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, $"{name} のテンソルの演算子の指定 (4 つ目の実引数)");
                continue;
            }

            if (arguments.Length >= 4 && arguments[3] is IdentifierExpressionSyntax { Name: "gl_TensorOperandsOutOfBoundsValueARM" })
            {
                if (name == "tensorWriteARM")
                {
                    requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 4, "範囲の外の値 (gl_TensorOperandsOutOfBoundsValueARM) を書けません。範囲の外の値は tensorReadARM にだけ書けます");
                }
                else if (arguments.Length < 5)
                {
                    requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 5, "範囲の外の値でなければなりません (gl_TensorOperandsOutOfBoundsValueARM の後ろに書きます)");
                }
                else if (evaluator.Evaluate(arguments[4]) is null)
                {
                    requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, "tensorReadARM の範囲の外の値");
                }
                else if (TypeOf(binder, arguments[4]) is { } outType && tensor.Element.Length > 0 && Canonical(outType) != Canonical(tensor.Element))
                {
                    requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 5, $"テンソルの要素と同じ型 ({tensor.Element}) でなければなりません (今は {outType})");
                }
            }
        }
    }

    /// <summary>テンソルの要素の型と階数。</summary>
    private sealed record Tensor(string Element, int? Rank);

    /// <summary>
    /// 利用者の関数のテンソルの仮引数。型の引数を書かない tensorARM はどのテンソルも受け取るが、要素の型を書いた仮引数は、
    /// 同じ要素の型と階数 (書かなければ 1) のテンソルしか受け取らない (glslang の tensorParameterOK)。
    /// </summary>
    private static void CheckTensorParameters(GlslRequirements requirement, ExpressionTypeBinder binder, IdentifierExpressionSyntax callee, FunctionDeclarationSyntax function, ExpressionSyntax[] arguments)
    {
        ParameterSyntax[] parameters = [.. function.ParameterList];

        for (int i = 0; i < Math.Min(parameters.Length, arguments.Length); i++)
        {
            if (FromType(parameters[i].Type) is not { Element.Length: > 0 } formal || TensorOf(binder, arguments[i]) is not { Element.Length: > 0 } actual
                || Canonical(formal.Element) == Canonical(actual.Element) && formal.Rank == actual.Rank)
            {
                continue;
            }

            requirement.Report(HlslRuleDescriptors.ArgumentNotConvertible, callee.Identifier, callee.Name, i + 1,
                $"tensorARM<{actual.Element}, {actual.Rank}>", $"tensorARM<{formal.Element}, {formal.Rank}>", "要素の型か階数を書いた tensorARM の仮引数は、同じ要素の型と階数のテンソルしか受け取れません。");
            return;
        }
    }

    /// <summary>名前の宣言の型から、テンソルを求める。</summary>
    private static Tensor? TensorOf(ExpressionTypeBinder binder, ExpressionSyntax expression)
    {
        TypeSyntax? type = expression is IdentifierExpressionSyntax identifier && binder.ResolveName(identifier).Candidates is [{ Declarator: var declarator }]
            ? declarator switch
            {
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } => declaration.Type,
                ParameterSyntax parameter => parameter.Type,
                _ => null,
            }
            : null;

        return type is null ? null : FromType(type);
    }

    /// <summary>
    /// 型からテンソルを求める。階数を書かないもの (<c>tensorARM</c>、<c>tensorARM&lt;int&gt;</c>) は、glslang と同じく階数 1 とする。
    /// 型の引数を書かない tensorARM の要素の型は空の文字列。
    /// </summary>
    private static Tensor? FromType(TypeSyntax type)
    {
        if (type is not { Name: "tensorARM" })
        {
            return null;
        }

        switch (type.TemplateArguments)
        {
            case [] when type.OpenAngle is null:
                return new Tensor(string.Empty, 1);

            case [{ } only]:
                return new Tensor(only.Text, 1);

            case [{ } element, { Kind: ShaderSyntaxKind.CommaToken }, .. var rest]:
                int? rank = rest is [{ Kind: ShaderSyntaxKind.NumericLiteralToken } literal]
                            && int.TryParse(literal.Text.TrimEnd('u', 'U'), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                    ? value
                    : null;
                return new Tensor(element.Text, rank);

            default:
                return null;
        }
    }

    /// <summary>座標の配列の要素の数 (配列の値の作成 uint[](0, 0) か、大きさの分かる配列の名前)。</summary>
    private static int? CoordinateCount(ExpressionTypeBinder binder, ExpressionSyntax expression) => expression switch
    {
        InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax } constructor => constructor.ArgumentExpressions.Count(),
        IdentifierExpressionSyntax identifier when binder.ResolveName(identifier).TryGetArrayLength(out int length) => length,
        _ => null,
    };

    /// <summary>式の型。分からないか構成で変わるなら <see langword="null"/>。</summary>
    private static string? TypeOf(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => binder.GetTypesByConfiguration(expression) is [{ Type: { } name }] ? name : null;

    private static string Canonical(string type) => type switch
    {
        "int32_t" => "int",
        "uint32_t" => "uint",
        "float32_t" => "float",
        "float64_t" => "double",
        _ => type,
    };
}
