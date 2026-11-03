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
/// 長いベクトル (<c>GL_EXT_long_vector</c> の <c>vector&lt;T, N&gt;</c>) の組み込み関数の呼び出しと添字を検査する (GL0302 / GL0307)。
/// glslang 16.6.0 の ParseHelper.cpp の長いベクトルの多重定義の解決 (「can't resolve types (ambiguous long vector overload)」) と添字の範囲の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 長いベクトルを渡す組み込み関数は、長いベクトルを同じ要素の型の 4 成分のベクトル (<c>vector&lt;uint, 5&gt;</c> なら <c>uvec4</c>) に置き換えて、
/// 型がすべて同じ多重定義があるかを見る。長いベクトルは暗黙に変換しないので、変換の要るものは無いとする。
/// 長いベクトルどうしは、要素の数 (書いた式のまま) も同じでなければならない。
/// </para>
/// <para>
/// 定数の添字が要素の数以上か負なら、範囲の外である。
/// </para>
/// </remarks>
internal sealed class GlslLongVectorAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>要素の型から、同じ要素の型の 4 成分のベクトルへ。</summary>
    private static readonly Dictionary<string, string> Vector4 = new(StringComparer.Ordinal)
    {
        ["float"] = "vec4", ["float32_t"] = "vec4", ["double"] = "dvec4", ["float64_t"] = "dvec4", ["float16_t"] = "f16vec4",
        ["int"] = "ivec4", ["int32_t"] = "ivec4", ["uint"] = "uvec4", ["uint32_t"] = "uvec4", ["bool"] = "bvec4",
        ["int8_t"] = "i8vec4", ["uint8_t"] = "u8vec4", ["int16_t"] = "i16vec4", ["uint16_t"] = "u16vec4", ["int64_t"] = "i64vec4", ["uint64_t"] = "u64vec4",
    };

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NoMatchingBuiltin, GlslRuleDescriptors.IndexOutOfRange];

    /// <summary>長いベクトルの型。</summary>
    /// <param name="Element">要素の型。</param>
    /// <param name="Count">要素の数 (書いた式のまま)。</param>
    private sealed record LongVector(string Element, string Count);

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.CodeTokens.Any(t => t.Text == "vector"))
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (InvocationExpressionSyntax or ElementAccessExpressionSyntax))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null || compilation.TypeSystem.ForTree(program.Tree) is not GlslTypeSystem types)
            {
                continue;
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            switch (node)
            {
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee, OpenAngle: null } call
                    when !types.Builtins.GetFunctions(callee.Name).IsEmpty && compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty:
                    CheckCall(requirement, types, binder, callee, [.. call.ArgumentExpressions]);
                    break;

                case ElementAccessExpressionSyntax element when TypeOf(binder, element.Target) is { } vector
                                                              && int.TryParse(vector.Count, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                                                              && IndexOf(element.Index) is { } index
                                                              && (index < 0 || index >= count):
                    requirement.Report(GlslRuleDescriptors.IndexOutOfRange, element.OpenBracket, index.ToString(CultureInfo.InvariantCulture), $"vector<{vector.Element}, {count}> の要素は 0 から {count - 1}");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>長いベクトルを渡す組み込み関数の呼び出し。</summary>
    private static void CheckCall(GlslRequirements requirement, GlslTypeSystem types, ExpressionTypeBinder binder, IdentifierExpressionSyntax callee, ExpressionSyntax[] arguments)
    {
        LongVector?[] vectors = [.. arguments.Select(a => TypeOf(binder, a))];

        if (vectors.All(v => v is null))
        {
            return;
        }

        // 長いベクトルどうしは暗黙に変換しない。要素の型の違うもの (max(vector<uint, 5>, vector<uint16_t, 5>)) は、
        // その関数のどの多重定義も、ベクトルの位置に同じ基の型を取るとき (mix の bvec や ldexp の ivec のような形が無いとき) だけ報告する。
        int[] vectorPositions = [.. Enumerable.Range(0, arguments.Length).Where(i => vectors[i] is not null)];

        if (vectors.OfType<LongVector>().Select(v => Canonical(v.Element)).Distinct(StringComparer.Ordinal).Count() > 1
            && types.Builtins.GetFunctions(callee.Name).Where(o => o.Parameters.Length == arguments.Length && !o.Parameters.Contains("vector"))
                .All(o => vectorPositions.Select(i => BaseOf(Canonical(o.Parameters[i]))).Distinct(StringComparer.Ordinal).Count() <= 1))
        {
            requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, callee.Identifier, callee.Name,
                string.Join(", ", vectors.Select(v => v is null ? "…" : $"vector<{v.Element}, {v.Count}>")));
            return;
        }

        string[] mapped = new string[arguments.Length];

        for (int i = 0; i < arguments.Length; i++)
        {
            if (vectors[i] is { } vector)
            {
                // 8・16・64 ビットの整数の長いベクトルの多重定義は、組み込みの表に無い (glslang は長いベクトルのために足す)。
                if (vector.Element is "int8_t" or "uint8_t" or "int16_t" or "uint16_t" or "int64_t" or "uint64_t"
                    || !Vector4.TryGetValue(vector.Element, out string? shortVector))
                {
                    return;
                }

                mapped[i] = shortVector;
            }
            else if (binder.GetTypesByConfiguration(arguments[i]) is [{ Type: { } name }] && types.TryDescribeNumeric(name, out ShaderNumericShape shape) && shape.Kind == ShaderNumericKind.Scalar)
            {
                mapped[i] = name;
            }
            else
            {
                return;
            }
        }

        string described = string.Join(", ", arguments.Select((_, i) => vectors[i] is { } v ? $"vector<{v.Element}, {v.Count}>" : mapped[i]));

        if (vectors.OfType<LongVector>().Select(v => v.Count).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, callee.Identifier, callee.Name, described);
            return;
        }

        // 長いベクトルの要素の型 (の基の型)。スカラーの仮引数がこれと同じ基の型 (max(uvec4, uint) の uint) なら、変換せずに同じ型でなければならない。
        HashSet<string> elementBases = [.. vectors.OfType<LongVector>().Select(v => BaseOf(Canonical(v.Element)))];

        bool exact = types.Builtins.GetFunctions(callee.Name).Any(o => o.Parameters.Length == mapped.Length
            && o.Parameters.Select((p, i) => Canonical(p) == Canonical(mapped[i])
                                             || vectors[i] is null && types.TryDescribeNumeric(p, out ShaderNumericShape parameter) && parameter.Kind == ShaderNumericKind.Scalar
                                                && (arguments[i] is LiteralExpressionSyntax || !elementBases.Contains(BaseOf(Canonical(p)))))
                .All(same => same));

        if (!exact)
        {
            requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, callee.Identifier, callee.Name, described);
        }
    }

    /// <summary>スカラーの型の基 (float・int・uint など)。ベクトルなら要素の型。</summary>
    private static string BaseOf(string type)
    {
        // 型の体系は、有効にしていない拡張の型 (i8vec4 など) を数の型と読まないので、名前から求める。
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(type, "^(|d|i|u|b|f16|f32|f64|i8|u8|i16|u16|i32|u32|i64|u64)vec[234]$");

        return !match.Success ? Canonical(type) : match.Groups[1].Value switch
        {
            "" or "f32" => "float",
            "d" or "f64" => "double",
            "i" or "i32" => "int",
            "u" or "u32" => "uint",
            "b" => "bool",
            "f16" => "float16_t",
            var prefix => (prefix[0] == 'i' ? "int" : "uint") + prefix[1..] + "_t",
        };
    }

    /// <summary>同じ型の別の名前をそろえる (float32_t と float など)。</summary>
    private static string Canonical(string type) => type switch
    {
        "float32_t" => "float",
        "float64_t" => "double",
        "int32_t" => "int",
        "uint32_t" => "uint",
        _ => type,
    };

    /// <summary>式の長いベクトルの型。長いベクトルでないか分からなければ <see langword="null"/>。</summary>
    private static LongVector? TypeOf(ExpressionTypeBinder binder, ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => TypeOf(binder, parenthesized.Expression),
        IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates is [{ Declarator: var declarator }] ? FromDeclarator(declarator) : null,
        InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "vector" }, OpenAngle: not null } constructor => FromArguments(constructor.TypeArgumentTokens),
        _ => null,
    };

    private static LongVector? FromDeclarator(ShaderSyntaxNode declarator) => declarator switch
    {
        VariableDeclaratorSyntax { IsArray: false, Parent: VariableDeclarationSyntax { Type: { Name: "vector", OpenAngle: not null } type } } => FromArguments(type.TemplateArguments),
        ParameterSyntax { IsArray: false, Type: { Name: "vector", OpenAngle: not null } type } => FromArguments(type.TemplateArguments),
        _ => null,
    };

    /// <summary>型の引数 (要素の型、要素の数) から作る。</summary>
    private static LongVector? FromArguments(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        int comma = tokens.IndexOf(tokens.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.CommaToken)!);

        return comma == 1 && tokens.Skip(comma + 1).All(t => t.Kind != ShaderSyntaxKind.CommaToken)
            ? new LongVector(tokens[0].Text, string.Concat(tokens.Skip(comma + 1).Select(t => t.Text)))
            : null;
    }

    /// <summary>整数のリテラル (と負の数) の添字。</summary>
    private static long? IndexOf(ExpressionSyntax index) => index switch
    {
        LiteralExpressionSyntax { Token: { Kind: ShaderSyntaxKind.NumericLiteralToken } token }
            when long.TryParse(token.Text.TrimEnd('u', 'U'), NumberStyles.None, CultureInfo.InvariantCulture, out long value) => value,
        PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.MinusToken, Operand: var operand } when IndexOf(operand) is { } positive => -positive,
        _ => null,
    };
}
