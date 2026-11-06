using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の添字 (<c>a[i]</c>) を検査する (GD0307)。
/// </summary>
/// <remarks>
/// <para>
/// Godot 4.7.2 の <c>ShaderLanguage::_parse_expression</c> の添字の検査を写した。
/// <list type="bullet">
/// <item>添字は <c>int</c> か <c>uint</c> のスカラー</item>
/// <item>添字を付けられるのは、配列・ベクトル・行列だけ</item>
/// <item>添字がリテラル (<c>-</c> を付けたものを含む) なら、配列の長さ・ベクトルの成分の数・行列の列の数の中</item>
/// </list>
/// 定数の名前 (<c>const int K</c>) の添字は、Godot も範囲を調べない。
/// </para>
/// </remarks>
internal sealed class GodotIndexAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GodotRuleDescriptors.InvalidIndex];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];
        Dictionary<AnalyzedProgram, ExpressionTypeBinder> binders = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not ElementAccessExpressionSyntax element)
            {
                continue;
            }

            if (!binders.TryGetValue(program, out ExpressionTypeBinder? binder))
            {
                binder = binders[program] = compilation.GetExpressionTypeBinder(program);
            }

            if (Check(element, binder) is { } found
                && compilation.IsReportable(found.Token)
                && found.Token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(GodotRuleDescriptors.InvalidIndex, location, found.Message));
            }
        }
    }

    private static (ShaderSyntaxToken Token, string Message)? Check(ElementAccessExpressionSyntax element, ExpressionTypeBinder binder)
    {
        ShaderSyntaxToken at = element.Index.FirstToken ?? element.OpenBracket;
        string? indexType = binder.GetTypesByConfiguration(element.Index) is [{ Type: { } name }] ? name : null;

        if (indexType is not null && (indexType is not ("int" or "uint") || IsArray(element.Index, binder) == true))
        {
            return (at, $"添字は int か uint のスカラーでなければなりません (今は {indexType})。");
        }

        long? constant = ConstantOf(element.Index);

        switch (IsArray(element.Target, binder, out int length))
        {
            case true:
                return constant is { } value && length > 0 && (value < 0 || value >= length)
                    ? (at, $"添字 [{value}] は範囲 [0..{length - 1}] の外です。")
                    : null;

            case false when binder.GetTypesByConfiguration(element.Target) is [{ Type: { } type }]:
                int size = ComponentCount(type);

                if (size == 0)
                {
                    return element.Target.FirstToken is { } first
                        ? (first, $"{type} の値には添字を付けられません。")
                        : null;
                }

                // Godot はベクトルと行列の添字を符号の無い数として比べる (-1 も範囲の外)。
                return constant is { } component && (component < 0 || component >= size)
                    ? (at, $"添字 [{component}] は範囲 [0..{size - 1}] の外です。")
                    : null;

            default:
                return null;
        }
    }

    /// <summary>添字のリテラルの値 (<c>-</c> を付けたものを含む)。</summary>
    private static long? ConstantOf(ExpressionSyntax index) => index switch
    {
        ParenthesizedExpressionSyntax parenthesized => ConstantOf(parenthesized.Expression),
        LiteralExpressionSyntax literal when GodotLiterals.TryParseInteger(literal.Token.Text, out long value) => value,
        PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.MinusToken, Operand: var operand } when ConstantOf(operand) is { } value => -value,
        _ => null,
    };

    private static bool? IsArray(ExpressionSyntax expression, ExpressionTypeBinder binder) => IsArray(expression, binder, out _);

    /// <summary>式が配列の値か。分からなければ <see langword="null"/>。</summary>
    private static bool? IsArray(ExpressionSyntax expression, ExpressionTypeBinder binder, out int length)
    {
        length = 0;

        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return IsArray(parenthesized.Expression, binder, out length);

            case IdentifierExpressionSyntax identifier:
                NameResolution resolution = binder.ResolveName(identifier);

                switch (resolution.Kind)
                {
                    case DeclaredNameKind.NotFound:
                        // 組み込みの変数と定数に配列は無い。
                        return false;

                    case DeclaredNameKind.Local or DeclaredNameKind.Global when !resolution.Candidates.IsEmpty:
                        if (resolution.Candidates.All(c => !c.Type.IsArray))
                        {
                            return false;
                        }

                        if (resolution.Candidates.All(c => c.Type.IsArray))
                        {
                            int[] lengths = [.. resolution.Candidates.Select(c => c.TryGetArrayLength(out int n) ? n : 0).Distinct()];
                            length = lengths is [var single] ? single : 0;
                            return true;
                        }

                        return null;

                    default:
                        return null;
                }

            case MemberAccessExpressionSyntax member:
                if (binder.GetTypesByConfiguration(member.Target) is not [{ Type: { } owner }])
                {
                    return null;
                }

                if (binder.TryGetStructField(owner, member.Name, out VariableDeclaratorSyntax? field, out _))
                {
                    if (field.IsArray)
                    {
                        length = field.TryGetArrayLength(out int n) ? n : 0;
                        return true;
                    }

                    return false;
                }

                // ベクトルのスウィズル。
                return ComponentCount(owner) > 0 && !owner.StartsWith("mat", StringComparison.Ordinal) ? false : null;

            // Godot の配列は 1 次元なので、添字を付けた値は配列でない。
            case ElementAccessExpressionSyntax:
            case LiteralExpressionSyntax:
                return false;

            default:
                return null;
        }
    }

    /// <summary>ベクトルの成分の数か行列の列の数。添字を付けられない型なら 0。</summary>
    private static int ComponentCount(string type) => type switch
    {
        "vec2" or "ivec2" or "uvec2" or "bvec2" or "mat2" => 2,
        "vec3" or "ivec3" or "uvec3" or "bvec3" or "mat3" => 3,
        "vec4" or "ivec4" or "uvec4" or "bvec4" or "mat4" => 4,
        _ => 0,
    };
}
