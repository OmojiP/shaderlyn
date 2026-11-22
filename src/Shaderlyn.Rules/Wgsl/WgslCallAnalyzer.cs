using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の組み込み関数と値の作成の実引数の型 (WG0302) と、論理演算子の型 (WG0301) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// 実引数の型が分かるときだけ見る (分からない実引数は、どの形にも合うとする)。抽象的な数は、具体的な型に変換できる。
/// </para>
/// <list type="bullet">
/// <item>スカラーの値の作成 (<c>i32(…)</c>) にベクトルを渡す</item>
/// <item>ベクトルの値の作成 (<c>vec2f(…)</c>) の、要素の型と違う具体的なスカラーやベクトル、成分の数の違う 1 つのベクトル</item>
/// <item>型引数を書いた配列の値の作成 (<c>array&lt;i32, 1&gt;(1.0)</c>) の、要素の型に変換できない実引数</item>
/// <item>行列の値の作成の、数でない実引数 (<c>mat2x2&lt;f32&gt;(array(0, 1), …)</c>)</item>
/// <item><c>cross</c> の 3 成分の浮動小数点数のベクトルでない実引数、<c>dot</c>・<c>distance</c> などの形の違う実引数、浮動小数点数だけの関数の整数の実引数</item>
/// <item><c>select</c> の、スカラーとベクトルでない値 (構造体・配列・ポインター) と、型の合わない 2 つの値</item>
/// <item><c>textureSample</c> などの、テクスチャでない 1 つ目の実引数</item>
/// <item><c>subgroupAnd</c>・<c>subgroupOr</c>・<c>subgroupXor</c> の、i32・u32 でない実引数</item>
/// <item>WG0301: <c>&amp;&amp;</c> と <c>||</c> の、bool のスカラーでない値</item>
/// </list>
/// naga 30 はどれもコンパイルの誤りにする。
/// </remarks>
internal sealed class WgslCallAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>実引数が浮動小数点数でなければならない組み込み関数。</summary>
    private static readonly string[] FloatOnly =
    [
        "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh", "asinh", "acosh", "atanh", "atan2",
        "exp", "exp2", "log", "log2", "pow", "sqrt", "inverseSqrt", "fract", "floor", "ceil", "round", "trunc",
        "length", "distance", "normalize", "cross", "reflect", "refract", "faceForward", "mix", "step", "smoothstep",
        "radians", "degrees", "fma", "ldexp", "determinant", "transpose", "quantizeToF16",
    ];

    /// <summary>すべての実引数が同じ形でなければならない組み込み関数。</summary>
    private static readonly string[] SameShape = ["dot", "distance", "cross", "atan2", "pow", "reflect", "step", "min", "max", "clamp", "fma", "smoothstep"];

    private static readonly string[] ImplicitTextures = ["textureSample", "textureSampleBias", "textureSampleGrad", "textureSampleLevel", "textureSampleCompare", "textureSampleCompareLevel", "textureDimensions", "textureLoad"];

    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [WgslRuleDescriptors.NoMatchingBuiltin, WgslRuleDescriptors.OperatorNotApplicable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxNode at, string message)
            {
                if (compilation.IsReportable(at) && at.GetLocation() is { } location && reported.Add((location.FilePath, location.Span.Start)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                }
            }

            switch (node)
            {
                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken } logical:
                    foreach (ExpressionSyntax operand in (ExpressionSyntax[])[logical.Left, logical.Right])
                    {
                        if (TypeOf(binder, operand) is { } type && binder.TypeSystem.TryDescribeNumeric(type, out ShaderNumericShape shape) && shape.Kind != ShaderNumericKind.Scalar)
                        {
                            Report(WgslRuleDescriptors.OperatorNotApplicable, logical,
                                $"{logical.OperatorToken.Text} は bool のスカラーにだけ使用できますが、{type} に使用しています。成分ごとの論理演算は {logical.OperatorToken.Text[0]} で書きます。");
                            break;
                        }
                    }

                    break;

                case InvocationExpressionSyntax { TargetName: { } name } call when !IsUserFunction(program, name):
                    if (CheckCall(binder, StructNames(program), call, name) is { } message)
                    {
                        Report(WgslRuleDescriptors.NoMatchingBuiltin, call, message);
                    }

                    break;
            }
        }
    }

    private static HashSet<string> StructNames(AnalyzedProgram program)
        => [.. program.Tree.Root.Declarations.OfType<StructDeclarationSyntax>().Select(s => s.NameToken?.Text).OfType<string>()];

    private static bool IsUserFunction(AnalyzedProgram program, string name)
        => program.Tree.Root.Declarations.Any(d => d is FunctionDeclarationSyntax f && f.Name == name || d is StructDeclarationSyntax { NameToken.Text: { } s } && s == name);

    /// <summary>呼び出しの実引数の型を調べ、合わなければその説明を返す。</summary>
    private static string? CheckCall(ExpressionTypeBinder binder, HashSet<string> structs, InvocationExpressionSyntax call, string name)
    {
        ShaderTypeSystem types = binder.TypeSystem;
        ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];
        string?[] argumentTypes = [.. arguments.Select(a => TypeOf(binder, a))];
        ShaderNumericShape?[] shapes = [.. argumentTypes.Select(t => types.TryDescribeNumeric(t, out ShaderNumericShape s) ? s : (ShaderNumericShape?)null)];

        // スカラーの値の作成 (変換): i32(vec2f(…)) は書けない。
        if (name is "i32" or "u32" or "f32" or "f16" or "bool" or "i64" or "u64" && arguments.Length == 1 && shapes[0] is { Kind: not ShaderNumericKind.Scalar })
        {
            return $"{argumentTypes[0]} を {name} に変換できません (スカラーの値の作成にはスカラーを渡します)。";
        }

        // ベクトルの値の作成 (vec2f(…)、vec3<f32>(…) は短い名前にしてある)。
        if (types.TryDescribeNumeric(name, out ShaderNumericShape target) && target.Kind == ShaderNumericKind.Vector && arguments.Length > 0)
        {
            if (arguments.Length == 1 && shapes[0] is { Kind: ShaderNumericKind.Vector } single)
            {
                return single.Columns != target.Columns ? $"{argumentTypes[0]} から {name} は作れません (成分の数が違います)。" : null;
            }

            for (int i = 0; i < arguments.Length; i++)
            {
                if (shapes[i] is { } shape && shape.Kind != ShaderNumericKind.Matrix && !IsAbstract(shape.BaseName) && shape.BaseName != target.BaseName)
                {
                    return $"{name} の {i + 1} 番目の実引数は {target.BaseName} の値でなければなりませんが、{argumentTypes[i]} です (WGSL は暗黙に変換しません)。";
                }
            }

            return null;
        }

        // 行列の値の作成: 数でない実引数 (配列など) は渡せない。
        if ((types.TryDescribeNumeric(name, out ShaderNumericShape matrix) && matrix.Kind == ShaderNumericKind.Matrix)
            || (name.StartsWith("mat", StringComparison.Ordinal) && WgslTypeSystem.IsTypeGenerator(name)))
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                if (shapes[i] is null && (arguments[i] is InvocationExpressionSyntax { TargetName: "array" } || (argumentTypes[i] is { } known && IsKnownNonNumeric(structs, arguments[i], known))))
                {
                    return $"{name} の {i + 1} 番目の実引数に {argumentTypes[i] ?? "配列"} は渡せません (スカラーか列のベクトルを渡します)。";
                }
            }

            return null;
        }

        // 型引数を書いた配列の値の作成 (array<i32, 1>(1.0))。
        if (name == "array" && call.TypeArgumentTokens is [{ Kind: ShaderSyntaxKind.IdentifierToken } elementToken, ..])
        {
            string element = elementToken.Text;

            for (int i = 0; i < arguments.Length; i++)
            {
                if (argumentTypes[i] is { } type && shapes[i] is not null && types.TryDescribeNumeric(element, out _) && !types.IsConvertible(type, element))
                {
                    return $"array<{element}, …> の要素に {type} は渡せません ({element} に変換できません)。";
                }
            }

            return null;
        }

        switch (name)
        {
            case "cross" when shapes.Any(s => s is not null && (s.Value.Kind != ShaderNumericKind.Vector || s.Value.Columns != 3 || !IsFloat(s.Value.BaseName))):
                return "cross の実引数は 3 成分の浮動小数点数のベクトル (vec3<f32> など) でなければなりません。";

            case "select" when arguments.Length == 3:
            {
                for (int i = 0; i < 2; i++)
                {
                    if (arguments[i] is PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AmpersandToken }
                        || (argumentTypes[i] is { } type && shapes[i] is null && IsKnownNonNumeric(structs, arguments[i], type)))
                    {
                        return "select の 1 つ目と 2 つ目の実引数は、スカラーかスカラーのベクトルでなければなりません (構造体・配列・ポインターは選べません)。";
                    }
                }

                if (shapes[0] is { } reject && shapes[1] is { } accept
                    && (reject.Kind != accept.Kind || reject.Columns != accept.Columns || types.GetWiderBaseName(reject.BaseName, accept.BaseName) is null))
                {
                    return $"select の 1 つ目と 2 つ目の実引数の型が合いません ({argumentTypes[0]} と {argumentTypes[1]})。";
                }

                return null;
            }

            case "subgroupAnd" or "subgroupOr" or "subgroupXor" when shapes is [{ } operand] && operand.BaseName is not ("i32" or "u32" or WgslTypeSystem.AbstractInt):
                return $"{name} の実引数は i32 か u32 (のベクトル) でなければなりませんが、{argumentTypes[0]} です。";
        }

        if (ImplicitTextures.Contains(name) && arguments.Length > 0 && shapes[0] is not null)
        {
            return $"{name} の 1 つ目の実引数はテクスチャでなければなりませんが、{argumentTypes[0]} です。";
        }

        if (FloatOnly.Contains(name))
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                // mix の 3 つ目、ldexp の 2 つ目などは型が違ってよいものがあるので、最初の 2 つだけを見る。
                if (i < 2 && name is not ("ldexp" or "refract" or "mix" or "faceForward") && shapes[i] is { } shape && !IsFloat(shape.BaseName) && shape.BaseName != WgslTypeSystem.AbstractInt)
                {
                    return $"{name} の {i + 1} 番目の実引数は浮動小数点数でなければなりませんが、{argumentTypes[i]} です。";
                }
            }
        }

        if (SameShape.Contains(name) && shapes.Length >= 2 && shapes.All(s => s is not null))
        {
            ShaderNumericShape first = shapes[0]!.Value;

            for (int i = 1; i < shapes.Length; i++)
            {
                ShaderNumericShape other = shapes[i]!.Value;

                // step と smoothstep と clamp などは、ベクトルとスカラーの組を受け付けない (WGSL)。形が違えば誤り。
                if (other.Kind != first.Kind || other.Columns != first.Columns || other.Rows != first.Rows
                    || types.GetWiderBaseName(first.BaseName, other.BaseName) is null)
                {
                    return $"{name} の実引数の型が合いません ({argumentTypes[0]} と {argumentTypes[i]})。";
                }
            }
        }

        return null;
    }

    /// <summary>数でないと分かっている値 (配列・構造体の値、配列の値の作成)。</summary>
    private static bool IsKnownNonNumeric(HashSet<string> structs, ExpressionSyntax argument, string type)
        => type is "array" || structs.Contains(type) || argument is InvocationExpressionSyntax { TargetName: "array" };

    private static bool IsAbstract(string baseName) => baseName is WgslTypeSystem.AbstractInt or WgslTypeSystem.AbstractFloat;

    private static bool IsFloat(string baseName) => baseName is "f32" or "f16" or "f64" or WgslTypeSystem.AbstractFloat;

    private static string? TypeOf(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => binder.GetEvaluatorFor(expression).Evaluate(expression);
}
