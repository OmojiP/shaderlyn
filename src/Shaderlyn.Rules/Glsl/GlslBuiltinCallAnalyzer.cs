using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 組み込み関数の呼び出しに合う多重定義があるかを検査する (GL0302)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>findFunctionExact</c> / <c>findFunction120</c> / <c>findFunction400</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 実引数の型と同じ仮引数を持つ形が無ければ、暗黙の変換で合う形を探す。
/// 変換できるかは glslang と同じく <c>#version</c> と有効にした拡張で決まる (ES の 3.10 より前と 1.10 では変換しない)。
/// 書き戻す仮引数 (<c>out</c> / <c>inout</c>) は、どちらの向きに変換できても合うものとする (表は <c>out</c> と <c>inout</c> を分けていない)。
/// ただし、アトミックの関数 (<c>atomicStore</c> を除く) の 1 つ目の仮引数は inout なので、両方の向きに変換できなければならない (<c>atomicAdd(intVar, 1.0)</c>)。
/// </para>
/// <para>
/// 実引数の型がどれか 1 つでも分からないか、構成によって変わるか、型の引数を取る型 (<c>coopmat&lt;…&gt;</c>) なら検査しない。
/// 配列 (添字を付けない配列の変数) は、配列の仮引数を持たない形には合わない (<c>interpolateAtCentroid(inArray)</c>、<c>sin(a)</c>)。
/// 配列かどうか分からないか、その位置がどの型にも合う仮引数 (<c>*</c>) の形があれば検査しない。
/// 引数の数が合う形が 1 つも無いときは、引数の数の検査 (CF0402) が報告するならそちらに任せる。
/// CF0402 は解析全体の表で数を見るので、この構成にだけ無い数 (ES の 3.00 の <c>textureSize(s2D)</c>) はこのルールが報告する。利用者が同じ名前の関数を宣言していれば検査しない。
/// </para>
/// </remarks>
internal sealed class GlslBuiltinCallAnalyzer : SemanticRuleAnalyzer
{
    private const string AnyType = "*";

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NoMatchingBuiltin, GlslRuleDescriptors.NotAvailable, GlslRuleDescriptors.NotAvailableInStage];

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
            if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation)
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

            if (state is not { } s || !compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty)
            {
                continue;
            }

            if (s.Types.Builtins.GetFunctions(callee.Name) is not { IsEmpty: false } overloads)
            {
                CheckUnavailableBuiltin(s.Requirement, s.Types, callee);
                continue;
            }

            // 引数の数の検査 (CF0402) は、解析全体の組み込みの表で数を見る。この構成の表で数の合う形が無くても、そちらで合えば報告しない。
            bool countChecked = !(compilation.TypeSystem.TryGetIntrinsicArity(callee.Name, out IntrinsicArity arity)
                                  && arity.Accepts(invocation.ArgumentExpressions.Count()));
            Check(s.Requirement, s.Types, compilation.GetExpressionTypeBinder(program), invocation, callee, overloads, countChecked);
        }
    }

    /// <summary>
    /// 別のバージョン・プロファイル・ステージにはあるが、この構成には無い組み込み関数の呼び出し。glslang は「no matching overloaded function found」にする。
    /// OpenGL 向けの <c>texture2D(…)</c> なども関数の名前である。Vulkan 向けでは型の名前 (値の作成) なので、OpenGL 向けと分かるときだけ見る。
    /// </summary>
    private static void CheckUnavailableBuiltin(GlslRequirements requirement, GlslTypeSystem types, IdentifierExpressionSyntax callee)
    {
        string name = callee.Name;

        if (!types.IsUnavailableBuiltinFunction(name) || (GlslKeywords.IsBuiltInType(name) && requirement.Vulkan != false))
        {
            return;
        }

        if (types.IsBuiltinFunctionOfOtherStage(name) && requirement.Stage is { } stage)
        {
            requirement.Report(GlslRuleDescriptors.NotAvailableInStage, callee.Identifier, $"組み込み関数 '{name}'", GlslRequirements.DescribeStage(stage));
            return;
        }

        requirement.Report(
            GlslRuleDescriptors.NotAvailable,
            callee.Identifier,
            name.StartsWith("texture", StringComparison.Ordinal) && GlslKeywords.IsBuiltInType(name)
                ? $"組み込み関数 '{name}' は、この #version とプロファイルにはありません。texture などを使ってください。"
                : $"組み込み関数 '{name}' は、この #version とプロファイルにはありません。");
    }

    private static void Check(
        GlslRequirements requirement,
        GlslTypeSystem types,
        ExpressionTypeBinder binder,
        InvocationExpressionSyntax invocation,
        IdentifierExpressionSyntax callee,
        ImmutableArray<GlslBuiltinFunction> overloads,
        bool countChecked)
    {
        ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];
        GlslBuiltinFunction[] fitting =
        [
            .. overloads.Where(f => f.IsVariadic ? arguments.Length >= f.FixedParameterCount : f.Parameters.Length == arguments.Length),
        ];

        if (fitting.Length == 0 && countChecked)
        {
            return;
        }

        // 型の引数を取る型 (長いベクトルの vector、coopmat など) の仮引数は、照らし合わせられる型の実引数とは合わない。
        // 実引数の型はどれも照らし合わせられるものに限る (下) ので、その形は候補から除く。sin や abs にも vector の形がある。
        fitting = [.. fitting.Where(f => !f.Parameters.Take(f.FixedParameterCount).Any(GlslKeywords.ParameterizedTypes.Contains))];

        // 表の仮引数の書き方のうち、型として照らし合わせられないもの (unknown、structure{…}、bfloat16_t<2> など) があれば決めない。
        if (fitting.Any(f => f.Parameters.Take(f.FixedParameterCount).Any(t => !IsComparable(types, t))))
        {
            return;
        }

        string[] argumentTypes = new string[arguments.Length];

        for (int i = 0; i < arguments.Length; i++)
        {
            // 文字列のリテラルは、どの型にも合う仮引数 (debugPrintfEXT の書式) にしか渡せない (length("…") は誤り)。
            if (arguments[i] is LiteralExpressionSyntax { Token.Kind: ShaderSyntaxKind.StringLiteralToken })
            {
                if (fitting.Any(f => i < f.FixedParameterCount ? f.Parameters[i] == AnyType : f.IsVariadic))
                {
                    return;
                }

                argumentTypes[i] = "string";
                continue;
            }

            ArrayValue array = GetArrayValue(binder, arguments[i]);

            if (array == ArrayValue.Unknown)
            {
                return;
            }

            if (array.ElementType is { } element)
            {
                // 配列は、表の配列でない仮引数のどれとも合わない (照らし合わせられない配列の仮引数を持つ形は、上で検査を外している)。
                // どの型にも合う仮引数 (*) がその位置にある形があれば決めない。
                if (!IsComparable(types, element) || fitting.Any(f => i < f.FixedParameterCount ? f.Parameters[i] == AnyType : f.IsVariadic))
                {
                    return;
                }

                argumentTypes[i] = element + "[]";
                continue;
            }

            if (binder.GetTypesByConfiguration(arguments[i]) is not [{ Type: { } type }] || !IsComparable(types, type))
            {
                return;
            }

            argumentTypes[i] = type;
        }

        bool inOutFirst = callee.Name.StartsWith("atomic", StringComparison.Ordinal) && callee.Name != "atomicStore";

        if (fitting.Any(f => Matches(types, f, argumentTypes, exact: true, inOutFirst)) || fitting.Any(f => Matches(types, f, argumentTypes, exact: false, inOutFirst)))
        {
            return;
        }

        requirement.Report(
            GlslRuleDescriptors.NoMatchingBuiltin,
            callee.Identifier,
            callee.Name,
            string.Join(", ", argumentTypes));
    }

    /// <summary>多重定義 1 つが実引数の型に合うか。<paramref name="inOutFirst"/> なら 1 つ目の仮引数は inout で、両方の向きに変換できなければならない。</summary>
    private static bool Matches(GlslTypeSystem types, GlslBuiltinFunction function, string[] arguments, bool exact, bool inOutFirst)
    {
        for (int i = 0; i < Math.Min(arguments.Length, function.FixedParameterCount); i++)
        {
            string parameter = function.Parameters[i];

            if (parameter == AnyType || SameType(types, arguments[i], parameter))
            {
                continue;
            }

            if (exact
                || !types.TryDescribeNumeric(arguments[i], out ShaderNumericShape argument)
                || !types.TryDescribeNumeric(parameter, out ShaderNumericShape formal)
                || argument.Kind != formal.Kind || argument.Rows != formal.Rows || argument.Columns != formal.Columns)
            {
                return false;
            }

            bool convertible = i == 0 && inOutFirst && function.WritesBack[i]
                ? types.IsBaseConvertible(argument.BaseName, formal.BaseName) && types.IsBaseConvertible(formal.BaseName, argument.BaseName)
                : types.IsBaseConvertible(argument.BaseName, formal.BaseName)
                  || (function.WritesBack[i] && types.IsBaseConvertible(formal.BaseName, argument.BaseName));

            if (!convertible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>型として照らし合わせられる名前か。数の型、組み込みの型のキーワード (`sampler2D` など)、どの型にも合う `*`。</summary>
    private static bool IsComparable(GlslTypeSystem types, string type)
        => type == AnyType
           || types.TryDescribeNumeric(type, out _)
           || (GlslKeywords.IsBuiltInType(type) && !GlslKeywords.ParameterizedTypes.Contains(type) && type != "void");

    /// <summary>2 つの型の名前が同じ型を指すか (<c>mat3</c> と <c>mat3x3</c>、<c>vec3</c> と <c>f32vec3</c>)。</summary>
    private static bool SameType(GlslTypeSystem types, string left, string right)
        => string.Equals(Alias(left), Alias(right), StringComparison.Ordinal)
           || (types.TryDescribeNumeric(left, out ShaderNumericShape a) && types.TryDescribeNumeric(right, out ShaderNumericShape b) && a == b);

    /// <summary>glslang が同じ型として扱う名前をそろえる (表は accelerationStructureEXT を accelerationStructureNV と書く)。</summary>
    private static string Alias(string type) => type == "accelerationStructureEXT" ? "accelerationStructureNV" : type;

    /// <summary>添字を付けない配列の変数か、配列のメンバーか。</summary>
    /// <returns>
    /// 配列なら要素の型を持つ値。配列でなければ <see cref="ArrayValue.NotArray"/>、分からなければ (候補によって配列かが変わるなど) <see cref="ArrayValue.Unknown"/>。
    /// </returns>
    private static ArrayValue GetArrayValue(ExpressionTypeBinder binder, ExpressionSyntax expression)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return GetArrayValue(binder, parenthesized.Expression);

            case IdentifierExpressionSyntax identifier:
            {
                ImmutableArray<DeclaredName> candidates = binder.ResolveName(identifier).Candidates;

                if (!candidates.Any(c => c.Type.IsArray))
                {
                    return ArrayValue.NotArray;
                }

                return candidates.All(c => c.Type.IsArray) && candidates.Select(c => c.Type.TypeName).Distinct(StringComparer.Ordinal).Count() == 1
                    ? new ArrayValue(Known: true, candidates[0].Type.TypeName)
                    : ArrayValue.Unknown;
            }

            case MemberAccessExpressionSyntax member:
                if (binder.GetTypesByConfiguration(member.Target) is not [{ Type: { } target }])
                {
                    return ArrayValue.Unknown;
                }

                return binder.TryGetStructField(target, member.NameToken.Text, out VariableDeclaratorSyntax? field, out string? fieldType) && field.IsArray
                    ? new ArrayValue(Known: true, fieldType)
                    : ArrayValue.NotArray;

            default:
                return ArrayValue.NotArray;
        }
    }

    /// <summary>実引数が配列か。</summary>
    /// <param name="Known">配列かどうか分かるか。</param>
    /// <param name="ElementType">配列なら要素の型。配列でなければ <see langword="null"/>。</param>
    private readonly record struct ArrayValue(bool Known, string? ElementType)
    {
        public static ArrayValue NotArray { get; } = new(Known: true, ElementType: null);

        public static ArrayValue Unknown { get; } = new(Known: false, ElementType: null);
    }
}
