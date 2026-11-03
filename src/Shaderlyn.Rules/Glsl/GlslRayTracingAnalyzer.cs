using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// レイトレーシングのシェーダーの検査 (GL0002 / GL0103 / GL0306 / GL0403)。glslang 16.6.0 の ParseHelper.cpp のレイトレーシングの検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// <c>terminateRayEXT</c> と <c>ignoreIntersectionEXT</c> は、任意ヒットのシェーダーにだけ書ける。
/// ペイロード (<c>rayPayloadEXT</c> / <c>rayPayloadInEXT</c>) と呼び出しのデータ (<c>callableDataEXT</c> / <c>callableDataInEXT</c>) の location は、それぞれの中で重なってはならない。
/// <c>traceRayEXT</c> の最後の実引数と <c>executeCallableEXT</c> の 2 つ目の実引数は、その location のペイロードと呼び出しのデータを宣言した定数でなければならない。
/// </para>
/// <para>
/// <c>hitObjectNV</c> / <c>hitObjectEXT</c> は、記憶域の無い関数の外か中の変数にしかできない。それを含む構造体の変数も宣言できない。
/// </para>
/// </remarks>
internal sealed class GlslRayTracingAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.NotAvailableInStage, GlslRuleDescriptors.InvalidLayoutValue, GlslRuleDescriptors.VariableArgumentRequired, GlslRuleDescriptors.TypeNotAllowedHere];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.CodeTokens.Any(t => t.Text.Contains("EXT", StringComparison.Ordinal) || t.Text.Contains("NV", StringComparison.Ordinal)))
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, (GlslRequirements Requirement, Locations Locations)?> checkers = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (JumpStatementSyntax or InvocationExpressionSyntax or VariableDeclarationSyntax))
            {
                continue;
            }

            if (!checkers.TryGetValue(program, out var checker))
            {
                checker = checkers[program] = GlslRequirements.Create(context, compilation, program, reported) is { } created
                    ? (created, CollectLocations(created, program))
                    : null;
            }

            if (checker is not { } found)
            {
                continue;
            }

            (GlslRequirements requirement, Locations locations) = found;

            switch (node)
            {
                case JumpStatementSyntax { Keyword.Text: "terminateRayEXT" or "ignoreIntersectionEXT" } jump:
                    requirement.RequireStage(jump.Keyword, [GlslShaderStage.AnyHit], $"'{jump.Keyword.Text}'");
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } call
                    when compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty:
                    CheckCall(requirement, compilation.GetExpressionTypeBinder(program), locations, callee, [.. call.ArgumentExpressions]);
                    break;

                // レイクエリは rayQueryInitializeEXT でしか初期化できない (glslang の「ray queries can only be initialized by using the rayQueryInitializeEXT intrinsic」)。
                case VariableDeclarationSyntax { Type.Name: "rayQueryEXT" } query when query.Variables.FirstOrDefault(v => v.Initializer is not null) is { } initialized:
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, initialized.NameToken, initialized.Name, "レイクエリ (rayQueryEXT) の変数には初期値を書けません。rayQueryInitializeEXT で初期化してください。");
                    break;

                // hitObject を含む構造体の変数 (glslang の「struct is not allowed to contain hitObjectEXT」)。
                case VariableDeclarationSyntax declaration when declaration.Parent is not (StructDeclarationSyntax or InterfaceBlockDeclarationSyntax)
                                                              && program.Structs.FirstOrDefault(s => s.Name == declaration.Type.Name && s.Keyword.Text == "struct") is { } structure
                                                              && structure.Fields.FirstOrDefault(f => f.Type.Name is "hitObjectNV" or "hitObjectEXT") is { } hitField
                                                              && declaration.Variables.FirstOrDefault() is { } holder:
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, holder.NameToken, holder.Name, $"{hitField.Type.Name} を含む構造体の変数は宣言できません。");
                    break;

                case VariableDeclarationSyntax declaration when declaration.Type.Name is "hitObjectNV" or "hitObjectEXT" && declaration.Variables.FirstOrDefault() is { } first:
                    if (declaration.ModifierTokens.Any(t => t.Text is "uniform" or "in" or "out" or "buffer" or "shared" or "inout"))
                    {
                        requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, first.NameToken, first.Name, $"{declaration.Type.Name} は、記憶域の修飾子の無い変数にしかできません。");
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>ペイロードと呼び出しのデータの location。</summary>
    private sealed record Locations(HashSet<long> Payloads, HashSet<long> Callables);

    /// <summary>traceRayEXT と executeCallableEXT の location の実引数。</summary>
    private static void CheckCall(GlslRequirements requirement, ExpressionTypeBinder binder, Locations locations, IdentifierExpressionSyntax callee, ExpressionSyntax[] arguments)
    {
        (int index, HashSet<long> declared, string kind) = callee.Name switch
        {
            "traceRayEXT" or "traceRayNV" when arguments.Length == 11 => (10, locations.Payloads, "rayPayloadEXT / rayPayloadInEXT"),
            "executeCallableEXT" or "executeCallableNV" when arguments.Length == 2 => (1, locations.Callables, "callableDataEXT / callableDataInEXT"),
            _ => (-1, locations.Payloads, string.Empty),
        };

        if (index < 0 || new GlslConstantEvaluator(binder).Evaluate(arguments[index]) is not { } value || declared.Contains(value.Integer))
        {
            return;
        }

        requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, callee.Identifier, callee.Name, index + 1,
            $"宣言した {kind} の location でなければなりません (location {value.Integer.ToString(CultureInfo.InvariantCulture)} の宣言がありません)");
    }

    /// <summary>ペイロードと呼び出しのデータの location を集め、重なりを報告する。</summary>
    private static Locations CollectLocations(GlslRequirements requirement, AnalyzedProgram program)
    {
        Locations locations = new([], []);

        foreach (ShaderSyntaxNode node in program.Tree.Root.ChildNodes())
        {
            (ImmutableArray<ShaderSyntaxToken> modifiers, ShaderSyntaxToken? at) = node switch
            {
                VariableDeclarationSyntax variable => (variable.ModifierTokens, variable.Variables.FirstOrDefault()?.NameToken),
                InterfaceBlockDeclarationSyntax block => (block.Modifiers, block.NameToken),
                StructDeclarationSyntax { Keyword.Text: not "struct" } block => ([.. block.Modifiers, block.Keyword], block.Keyword),
                _ => ([], null),
            };

            if (at is null || GlslLayoutQualifier.Read(modifiers).Where(e => e.Name.Text == "location").Select(e => (GlslLayoutEntry?)e).FirstOrDefault() is not { } entry
                || GlslLayoutQualifier.Evaluate(entry.Value, _ => null) is not { } location)
            {
                continue;
            }

            HashSet<long>? set = modifiers.Any(t => t.Text is "rayPayloadEXT" or "rayPayloadInEXT" or "rayPayloadNV" or "rayPayloadInNV") ? locations.Payloads
                : modifiers.Any(t => t.Text is "callableDataEXT" or "callableDataInEXT" or "callableDataNV" or "callableDataInNV") ? locations.Callables
                : null;

            if (set is not null && !set.Add(location.Integer) && ReferenceEquals(at.Source, program.Tree.Text))
            {
                requirement.Report(GlslRuleDescriptors.InvalidLayoutValue, at, $"location {location.Integer.ToString(CultureInfo.InvariantCulture)} は、ほかの宣言の location と重なります。");
            }
        }

        return locations;
    }
}
