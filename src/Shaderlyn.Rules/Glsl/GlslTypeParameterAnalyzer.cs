using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 型の引数 (<c>coopmat&lt;float16_t, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA&gt;</c> など) を検査する (GL0324)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>typeParametersCheck</c> と <c>declareVariable</c> の型の引数の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 型の引数を取るのは、協調行列 (<c>coopmat</c>、<c>fcoopmatNV</c> など)、協調ベクトル (<c>coopvecNV</c>)、長いベクトル (<c>vector</c>)、
/// テンソル (<c>tensorARM</c>、<c>tensorLayoutNV</c>、<c>tensorViewNV</c>) だけである。ほかの型 (<c>float&lt;16&gt;</c>) には書けない。
/// 引数の数、要素の型、数の引数が定数の整数の式であることを見る。
/// </para>
/// <para>
/// 協調行列は、shared の変数とブロックのメンバーにできない (GL0403)。
/// </para>
/// </remarks>
internal sealed class GlslTypeParameterAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>KHR の協調行列の要素に使える型。</summary>
    private static readonly HashSet<string> CoopmatElements = new(StringComparer.Ordinal)
    {
        "float", "float32_t", "float16_t", "bfloat16_t", "floate5m2_t", "floate4m3_t",
        "int", "int32_t", "int8_t", "int16_t", "uint", "uint32_t", "uint8_t", "uint16_t",
    };

    /// <summary>テンソルの要素に使える型。</summary>
    private static readonly HashSet<string> TensorElements = new(StringComparer.Ordinal)
    {
        "bool", "int8_t", "int16_t", "int", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint", "uint32_t", "uint64_t",
        "float16_t", "float", "float32_t", "double", "float64_t", "bfloat16_t", "floate5m2_t", "floate4m3_t",
    };

    /// <summary>協調行列の型の名前。</summary>
    private static readonly HashSet<string> CooperativeMatrices = new(StringComparer.Ordinal) { "coopmat", "fcoopmatNV", "icoopmatNV", "ucoopmatNV" };

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.InvalidTypeParameters, GlslRuleDescriptors.TypeNotAllowedHere, GlslRuleDescriptors.NotAvailable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, (GlslRequirements Requirement, HashSet<string> Constants)?> checkers = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            // 値の作成 (vector<float, 5>(0)、coopmat<…>(x))。名前が型の引数を取る型で、山括弧を書いたか、vector() のように書いたもの。
            if (node is InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "vector" or "coopmat" or "coopvecNV" or "tensorARM" } callee } constructor)
            {
                if (!checkers.TryGetValue(program, out var constructorChecker))
                {
                    constructorChecker = checkers[program] = GlslRequirements.Create(context, compilation, program, reported) is { } created
                        ? (created, CollectConstants(program))
                        : null;
                }

                // 利用者の関数 vector(…) や、拡張を有効にしていない名前は見ない。
                if (constructorChecker is { } c
                    && compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty
                    && (callee.Name != "vector" || c.Requirement.IsOn("GL_EXT_long_vector", callee.Identifier))
                    && DescribeError(callee.Name, constructor.OpenAngle is not null, constructor.TypeArgumentTokens, c.Constants) is { } constructorError)
                {
                    c.Requirement.Report(GlslRuleDescriptors.InvalidTypeParameters, callee.Identifier, callee.Name, constructorError);
                }

                continue;
            }

            // 仮引数のテンソル (void f(tensorARM t) と void f(tensorARM<int> t))。
            if (node is ParameterSyntax { Type: { Name: "tensorARM" } parameterType } parameter)
            {
                if (!checkers.TryGetValue(program, out var parameterChecker))
                {
                    parameterChecker = checkers[program] = GlslRequirements.Create(context, compilation, program, reported) is { } created
                        ? (created, CollectConstants(program))
                        : null;
                }

                string? parameterError = parameterType.OpenAngle is null
                    ? "tensorARM には型の引数 (要素の型・階数) が要ります。"
                    : parameterChecker is { } p ? DescribeError(parameterType, p.Constants) : null;

                if (parameterChecker is { } pc && parameterError is not null)
                {
                    pc.Requirement.Report(GlslRuleDescriptors.InvalidTypeParameters, parameterType.NameToken, "tensorARM", parameterError);
                }

                continue;
            }

            if (node is not VariableDeclarationSyntax declaration || declaration.Variables.FirstOrDefault() is not { } first)
            {
                continue;
            }

            if (!checkers.TryGetValue(program, out var checker))
            {
                checker = checkers[program] = GlslRequirements.Create(context, compilation, program, reported) is { } created
                    ? (created, CollectConstants(program))
                    : null;
            }

            if (checker is not { } found)
            {
                continue;
            }

            (GlslRequirements requirement, HashSet<string> constants) = found;
            TypeSyntax type = declaration.Type;

            if (DescribeError(type, constants) is { } error)
            {
                requirement.Report(GlslRuleDescriptors.InvalidTypeParameters, first.NameToken, type.Name, error);
            }

            // テンソルの要素の bfloat16_t と 8 ビットの浮動小数の型は、それぞれの拡張が要る (glslang の declareVariable)。
            if (type is { Name: "tensorARM", TemplateArguments: [{ } tensorElement, ..] }
                && tensorElement.Text switch { "bfloat16_t" => "GL_ARM_tensors_bfloat16", "floate5m2_t" => "GL_ARM_tensors_float_e5m2", "floate4m3_t" => "GL_ARM_tensors_float_e4m3", _ => null } is { } tensorExtension)
            {
                requirement.RequireExtensions(tensorElement, [tensorExtension], $"'tensorARM<{tensorElement.Text}, …>'");
            }

            if (CooperativeMatrices.Contains(type.Name))
            {
                if (declaration.ModifierTokens.Any(t => t.Text == "shared"))
                {
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, first.NameToken, first.Name, "協調行列は shared の変数にできません。");
                }

                if (declaration.Parent is InterfaceBlockDeclarationSyntax or StructDeclarationSyntax { Keyword.Text: not "struct" })
                {
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, first.NameToken, first.Name, "ブロックのメンバーは協調行列にできません。");
                }
            }
        }
    }

    /// <summary>const の変数の名前 (constant_id の特殊化の定数も)。型の引数に書ける。</summary>
    private static HashSet<string> CollectConstants(AnalyzedProgram program)
        => [.. program.Tree.Root.DescendantNodesAndSelf().OfType<VariableDeclarationSyntax>()
            .Where(d => d.HasModifier("const") || d.ModifierTokens.Any(t => t.Text == "constant_id"))
            .SelectMany(d => d.Variables.Select(v => v.Name))];

    /// <summary>型の引数の誤りを述べる。誤りが無ければ <see langword="null"/>。</summary>
    private static string? DescribeError(TypeSyntax type, HashSet<string> constants)
        => DescribeError(type.Name, type.OpenAngle is not null, type.TemplateArguments, constants);

    /// <summary>型の名前と型の引数のトークンから、型の引数の誤りを述べる。誤りが無ければ <see langword="null"/>。</summary>
    private static string? DescribeError(string name, bool hasArguments, ImmutableArray<ShaderSyntaxToken> tokens, HashSet<string> constants)
    {
        List<ImmutableArray<ShaderSyntaxToken>> arguments = Split(tokens);
        TypeName type = new(name);

        switch (type.Name)
        {
            case "coopmat":
                if (!hasArguments)
                {
                    return "coopmat には型の引数 (要素の型・スコープ・行の数・列の数・用途) が要ります。";
                }

                if (arguments.Count != 5)
                {
                    return $"coopmat の型の引数は 5 つ (要素の型・スコープ・行の数・列の数・用途) です (今は {arguments.Count} つ)。";
                }

                if (arguments[0] is [{ Text: "spirv_type" }, ..])
                {
                    return DescribeNonConstant(arguments.Skip(1), constants);
                }

                if (arguments[0] is not [{ } element] || !CoopmatElements.Contains(element.Text))
                {
                    return $"coopmat の要素の型には、8・16・32 ビットの整数か、16・32 ビットの浮動小数の型しか書けません (今は {Describe(arguments[0])})。";
                }

                return DescribeNonConstant(arguments.Skip(1), constants);

            case "fcoopmatNV" or "icoopmatNV" or "ucoopmatNV":
                if (arguments.Count != 4)
                {
                    return $"{type.Name} の型の引数は 4 つ (ビットの数・スコープ・行の数・列の数) です (今は {arguments.Count} つ)。";
                }

                if (arguments[0] is [{ Kind: ShaderSyntaxKind.NumericLiteralToken } bits])
                {
                    bool floating = type.Name == "fcoopmatNV";

                    if (floating ? bits.Text is not ("16" or "32" or "64") : bits.Text is not ("8" or "16" or "32"))
                    {
                        return floating ? "fcoopmatNV のビットの数は 16・32・64 のどれかです。" : $"{type.Name} のビットの数は 8・16・32 のどれかです。";
                    }
                }

                return DescribeNonConstant(arguments, constants);

            case "coopvecNV" or "vector":
                if (arguments.Count != 2)
                {
                    return $"{type.Name} の型の引数は 2 つ (要素の型・要素の数) です (今は {arguments.Count} つ)。";
                }

                if (arguments[0] is not [{ Kind: ShaderSyntaxKind.IdentifierToken, Text: not "void" }])
                {
                    return $"{type.Name} の要素の型には、void でない型を書かなければなりません (今は {Describe(arguments[0])})。";
                }

                if (IsNonPositive(arguments[1]))
                {
                    return $"{type.Name} の要素の数は正の数でなければなりません。";
                }

                return DescribeNonConstant(arguments.Skip(1), constants);

            case "tensorARM":
                if (arguments.Count != 2)
                {
                    return $"tensorARM の型の引数は 2 つ (要素の型・階数) です (今は {arguments.Count} つ)。";
                }

                if (arguments[0] is not [{ } tensorElement] || !TensorElements.Contains(tensorElement.Text))
                {
                    return $"tensorARM の要素の型には、bool・整数・浮動小数の型しか書けません (今は {Describe(arguments[0])})。";
                }

                if (IsNonPositive(arguments[1]))
                {
                    return "tensorARM の階数は 1 以上でなければなりません。";
                }

                return DescribeNonConstant(arguments.Skip(1), constants);

            case "tensorLayoutNV" when arguments.Count > 2:
                return $"tensorLayoutNV の型の引数は 2 つまでです (今は {arguments.Count} つ)。";

            case "tensorViewNV" when arguments.Count > 7:
                return $"tensorViewNV の型の引数は 7 つまでです (今は {arguments.Count} つ)。";

            case "tensorLayoutNV" or "tensorViewNV" or "spirv_type":
                return null;

            default:
                return hasArguments && arguments.Count > 0 ? $"型 '{type.Name}' には型の引数を書けません。" : null;
        }
    }

    /// <summary>型の名前 (switch の型を TypeSyntax と同じ形で書くため)。</summary>
    private readonly record struct TypeName(string Name);

    /// <summary>数の引数が、定数の整数の式でないものを述べる。</summary>
    private static string? DescribeNonConstant(IEnumerable<ImmutableArray<ShaderSyntaxToken>> arguments, HashSet<string> constants)
    {
        foreach (ImmutableArray<ShaderSyntaxToken> argument in arguments)
        {
            if (argument.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.IdentifierToken
                                            && !t.Text.StartsWith("gl_", StringComparison.Ordinal)
                                            && !constants.Contains(t.Text)) is { } variable)
            {
                return $"型の引数は定数の整数の式でなければなりません ('{variable.Text}' は定数ではありません)。";
            }
        }

        return null;
    }

    /// <summary>0 か負の数のリテラルか。</summary>
    private static bool IsNonPositive(ImmutableArray<ShaderSyntaxToken> argument)
        => argument is [{ Kind: ShaderSyntaxKind.MinusToken }, { Kind: ShaderSyntaxKind.NumericLiteralToken }]
           || argument is [{ Kind: ShaderSyntaxKind.NumericLiteralToken } literal]
              && long.TryParse(literal.Text.TrimEnd('u', 'U'), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value <= 0;

    /// <summary>引数のトークンを文字列にする。</summary>
    private static string Describe(ImmutableArray<ShaderSyntaxToken> argument) => string.Join(" ", argument.Select(t => t.Text));

    /// <summary>型の引数のトークンを、括弧の外のカンマで分ける。</summary>
    private static List<ImmutableArray<ShaderSyntaxToken>> Split(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        List<ImmutableArray<ShaderSyntaxToken>> arguments = [];

        if (tokens.IsDefaultOrEmpty)
        {
            return arguments;
        }

        ImmutableArray<ShaderSyntaxToken>.Builder current = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
        int parens = 0;
        int angles = 0;

        foreach (ShaderSyntaxToken token in tokens)
        {
            // 括弧の中の < と > は比べる演算子 ((2 > 1 ? 8 : 4))。括弧の外の山括弧だけを入れ子として数える。
            switch (token.Kind)
            {
                case ShaderSyntaxKind.OpenParenToken:
                    parens++;
                    break;

                case ShaderSyntaxKind.CloseParenToken:
                    parens--;
                    break;

                case ShaderSyntaxKind.LessThanToken when parens == 0:
                    angles++;
                    break;

                case ShaderSyntaxKind.GreaterThanToken when parens == 0:
                    angles--;
                    break;

                default:
                    break;
            }

            if (parens == 0 && angles == 0 && token.Kind == ShaderSyntaxKind.CommaToken)
            {
                arguments.Add(current.ToImmutable());
                current.Clear();
                continue;
            }

            current.Add(token);
        }

        arguments.Add(current.ToImmutable());
        return arguments;
    }
}
