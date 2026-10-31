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
/// 定数の式でなければならない所を検査する (GL0317 / GL0001)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>arraySizeCheck</c> (「array size must be a constant integer expression」) と、
/// <c>executeInitializer</c> の初期値の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 配列の大きさ、関数の外の <c>const</c> の変数と uniform の変数の初期値は、定数の式でなければならない (GL0317)。
/// 関数の中の <c>const</c> の変数を定数でない式で初期化できるのは、ES でないプロファイルの 4.20 からか <c>GL_ARB_shading_language_420pack</c> から。
/// ES では、関数の外の記憶域の無い変数を定数でない式で初期化するのに <c>GL_EXT_shader_non_constant_global_initializers</c> が要る (GL0001)。
/// </para>
/// <para>
/// 定数でないと言い切れる式 (<see cref="GlslConstantEvaluator.IsCertainlyNotConstant"/>) だけを報告する。
/// 配列の大きさは字句のまま持っているので、名前は関数の中の宣言、関数の外の宣言、組み込みの変数の順に名前で引く。
/// </para>
/// </remarks>
internal sealed class GlslConstantExpressionAnalyzer : SemanticRuleAnalyzer
{
    private static readonly HashSet<string> Storages = new(StringComparer.Ordinal)
    {
        "const", "uniform", "in", "out", "inout", "buffer", "shared", "attribute", "varying", "rayPayloadEXT", "rayPayloadInEXT",
        "hitAttributeEXT", "callableDataEXT", "callableDataInEXT", "taskPayloadSharedEXT",
    };

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.ConstantExpressionRequired, GlslRuleDescriptors.NotAvailable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, Checker?> checkers = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (VariableDeclaratorSyntax or ParameterSyntax))
            {
                continue;
            }

            if (!checkers.TryGetValue(program, out Checker? checker))
            {
                checker = checkers[program] =
                    GlslRequirements.Create(context, compilation, program, reported) is { } requirement
                    && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                        ? new Checker(requirement, program, new GlslConstantEvaluator(compilation.GetExpressionTypeBinder(program), types.Builtins), types.Builtins)
                        : null;
            }

            checker?.Check(node);
        }
    }

    /// <summary>1 つの木の宣言を検査する。</summary>
    private sealed class Checker(GlslRequirements requirement, AnalyzedProgram program, GlslConstantEvaluator evaluator, GlslBuiltinView builtins)
    {
        public void Check(ShaderSyntaxNode node)
        {
            switch (node)
            {
                case ParameterSyntax parameter:
                    CheckArraySizes(parameter, [.. parameter.ArrayRankTokens, .. parameter.Type.ArrayRanks]);
                    break;

                case VariableDeclaratorSyntax declarator:
                    VariableDeclarationSyntax? declaration = declarator.Parent as VariableDeclarationSyntax;
                    CheckArraySizes(declarator, [.. declarator.ArrayRankTokens, .. declaration?.Type.ArrayRanks ?? []]);

                    if (declaration is not null && declarator.Initializer is { } initializer)
                    {
                        CheckInitializer(declaration.ModifierTokens, declaration.Parent is CompilationUnitSyntax, declarator, initializer);
                    }

                    break;

                default:
                    break;
            }
        }

        /// <summary>初期値が定数でなければならないか (glslang の executeInitializer)。</summary>
        private void CheckInitializer(ImmutableArray<ShaderSyntaxToken> modifiers, bool global, VariableDeclaratorSyntax declarator, ExpressionSyntax initializer)
        {
            if (!evaluator.IsCertainlyNotConstant(initializer))
            {
                return;
            }

            ShaderSyntaxToken at = declarator.NameToken;

            if (modifiers.Any(t => t.Text == "uniform"))
            {
                requirement.Report(GlslRuleDescriptors.ConstantExpressionRequired, at, "uniform の変数の初期値");
            }
            else if (modifiers.Any(t => t.Text == "const"))
            {
                if (global)
                {
                    requirement.Report(GlslRuleDescriptors.ConstantExpressionRequired, at, "関数の外の const の変数の初期値");
                    return;
                }

                const string feature = "const の変数を定数でない式で初期化すること";
                requirement.RequireProfile(at, GlslProfileMask.NotEs, feature);
                requirement.ProfileRequires(at, GlslProfileMask.NotEs, 420, ["GL_ARB_shading_language_420pack"], feature);
            }
            else if (global && !modifiers.Any(t => Storages.Contains(t.Text)))
            {
                requirement.ProfileRequires(
                    at,
                    GlslProfileMask.Es,
                    0,
                    ["GL_EXT_shader_non_constant_global_initializers"],
                    "関数の外の変数を定数でない式で初期化すること");
            }
        }

        /// <summary>
        /// 配列の大きさが定数の式か。呼び出しの名前と、後ろに . の付く名前は見ない。
        /// </summary>
        private void CheckArraySizes(ShaderSyntaxNode owner, ImmutableArray<ShaderSyntaxToken> ranks)
        {
            // .length() は配列とベクトルの大きさなので、引く配列が定数でなくても定数になる。
            if (ranks.Any(t => t.Text == "length"))
            {
                return;
            }

            int depth = 0;
            Stack<bool> groups = [];

            for (int i = 0; i < ranks.Length; i++)
            {
                ShaderSyntaxToken token = ranks[i];

                switch (token.Kind)
                {
                    case ShaderSyntaxKind.OpenBracketToken:
                        depth++;
                        break;

                    case ShaderSyntaxKind.CloseBracketToken:
                        depth--;
                        break;

                    // 呼び出しの括弧か、まとめる括弧か。まとめる括弧の中のコンマはコンマの演算子。
                    case ShaderSyntaxKind.OpenParenToken when depth > 0:
                        groups.Push(i == 0 || ranks[i - 1].Kind != ShaderSyntaxKind.IdentifierToken && !GlslKeywords.IsBuiltInType(ranks[i - 1].Text));
                        break;

                    case ShaderSyntaxKind.CloseParenToken when depth > 0 && groups.Count > 0:
                        groups.Pop();
                        break;

                    case ShaderSyntaxKind.CommaToken when depth > 0 && (groups.Count == 0 || groups.Peek()):
                        Report(token);
                        return;

                    case ShaderSyntaxKind.IdentifierToken when depth > 0
                                                               && (i == 0 || ranks[i - 1].Kind != ShaderSyntaxKind.DotToken)
                                                               && (i + 1 >= ranks.Length || ranks[i + 1].Kind is not (ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.DotToken))
                                                               && IsNonConstantName(owner, token):
                        Report(token);
                        return;

                    default:
                        break;
                }
            }

            void Report(ShaderSyntaxToken at) => requirement.Report(GlslRuleDescriptors.ConstantExpressionRequired, at, "配列の大きさ");
        }

        /// <summary>配列の大きさに書いた名前が、定数でない変数を指すと言い切れるか。</summary>
        private bool IsNonConstantName(ShaderSyntaxNode owner, ShaderSyntaxToken name)
        {
            // 関数の中の宣言。前にあって、宣言のある波括弧の中で使っているものを見る。仮引数は定数でない。
            if (owner.FirstAncestorOrDefault<FunctionDeclarationSyntax>() is { } function)
            {
                ShaderSyntaxNode? local = null;

                foreach (ShaderSyntaxNode candidate in function.DescendantNodesAndSelf().OfType<ShaderSyntaxNode>())
                {
                    if (candidate is ParameterSyntax parameter && parameter.Name == name.Text)
                    {
                        local ??= parameter;
                    }
                    else if (candidate is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax } declarator
                             && declarator.Name == name.Text
                             && declarator.NameToken.Span.Start < name.Span.Start
                             && declarator.FirstAncestorOrDefault<BlockStatementSyntax>() is { } block
                             && block.Span.Contains(name.Span)
                             && (local is not VariableDeclaratorSyntax previous || previous.NameToken.Span.Start < declarator.NameToken.Span.Start))
                    {
                        local = declarator;
                    }
                }

                if (local is not null)
                {
                    return IsNonConstantDeclarator(local);
                }
            }

            ImmutableArray<VariableDeclaratorSyntax> globals =
            [
                .. program.Tree.Root.Declarations.OfType<VariableDeclarationSyntax>().SelectMany(d => d.Variables).Where(v => v.Name == name.Text),
            ];

            if (!globals.IsEmpty)
            {
                return globals.All(IsNonConstantDeclarator);
            }

            return name.Text.StartsWith("gl_", StringComparison.Ordinal)
                   && builtins.GetVariables(name.Text) is { IsEmpty: false } variables
                   && variables.All(v => v.Storage != "const");
        }

        /// <summary>宣言した変数が定数でないと言い切れるか。仮引数、const でない変数、定数でない式で初期化した const の変数。</summary>
        private bool IsNonConstantDeclarator(ShaderSyntaxNode declarator) => declarator switch
        {
            ParameterSyntax => true,
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable
                => !declaration.HasModifier("const") || variable.Initializer is { } initializer && evaluator.IsCertainlyNotConstant(initializer),
            _ => false,
        };
    }
}
