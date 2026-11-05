using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の組み込み関数と値の作成に、実引数の型に合う形があるかを検査する (GD0302)。
/// </summary>
/// <remarks>
/// <para>
/// 形の表は Godot 4.7.2 の <c>builtin_func_defs</c> とステージ関数の組み込み関数 (<see cref="GodotTypeSystem.Functions"/>)。<c>vec2(…)</c> などの値の作成もこの表にある。
/// Godot には暗黙の変換が無いので、実引数の型は仮引数とちょうど同じでなければならない。
/// 例外は、実引数に書いた整数のリテラルで、<c>float</c> / <c>uint</c> の仮引数へ変換される (<see cref="ShaderTypeSystem.ConvertsLiteralArgument"/>)。
/// </para>
/// <para>
/// 実引数の数が合う形が無いものは CF0402 が報告するので、ここでは見ない。型の分からない実引数がある呼び出しと、利用者が同じ名前の関数を宣言したものも見ない。
/// </para>
/// </remarks>
internal sealed class GodotBuiltinCallAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GodotRuleDescriptors.NoMatchingBuiltin];

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
            if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation
                || compilation.TypeSystem.ForTree(program.Tree) is not GodotTypeSystem godot
                || !godot.Functions.TryGetValue(callee.Name, out ImmutableArray<GodotBuiltinFunction> overloads)
                || !compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty)
            {
                continue;
            }

            if (!binders.TryGetValue(program, out ExpressionTypeBinder? binder))
            {
                binder = binders[program] = compilation.GetExpressionTypeBinder(program);
            }

            ImmutableArray<ExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];

            // 実引数の数が合う形が無ければ CF0402 が報告する。
            if (!overloads.Any(f => f.Parameters.Length == arguments.Length))
            {
                continue;
            }

            string[] types = new string[arguments.Length];

            for (int i = 0; i < arguments.Length; i++)
            {
                if (binder.GetTypesByConfiguration(arguments[i]) is not [{ Type: { } type }])
                {
                    types = [];
                    break;
                }

                types[i] = type;
            }

            if (types.Length != arguments.Length
                || overloads.Any(f => Matches(f, arguments, types, binder.TypeSystem))
                || !compilation.IsReportable(callee.Identifier)
                || callee.Identifier.GetLocation() is not { } location
                || !reported.Add((location.FilePath, location.Span.Start)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                GodotRuleDescriptors.NoMatchingBuiltin, location, callee.Name, string.Join(", ", types)));
        }
    }

    /// <summary>形が実引数に合うか。型がちょうど同じか、整数のリテラルを変換して合うもの。</summary>
    private static bool Matches(GodotBuiltinFunction function, ImmutableArray<ExpressionSyntax> arguments, string[] types, ShaderTypeSystem typeSystem)
    {
        if (function.Parameters.Length != arguments.Length)
        {
            return false;
        }

        for (int i = 0; i < arguments.Length; i++)
        {
            if (types[i] != function.Parameters[i] && !typeSystem.ConvertsLiteralArgument(arguments[i], function.Parameters[i]))
            {
                return false;
            }
        }

        return true;
    }
}
