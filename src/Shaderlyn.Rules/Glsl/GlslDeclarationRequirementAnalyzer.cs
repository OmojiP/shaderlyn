using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 型と修飾子のキーワードを、使える版・拡張・ステージ・場所で検査する (GL0001 / GL0002 / GL0101)。
/// </summary>
/// <remarks>
/// glslang が型と修飾子を読むときの検査 (glslang.y の type_specifier_nonarray と修飾子の規則) を、
/// 宣言・仮引数・値の作成 (<c>uint(x)</c>)・修飾子だけの宣言 (<c>layout(…) in;</c>) の、型と修飾子のトークンに掛ける
/// (<see cref="GlslRequirements.CheckTypeKeyword"/>、<see cref="GlslRequirements.CheckQualifierKeyword"/>)。
/// 修飾子の並びは、ParseHelper.cpp の <c>mergeQualifiers</c> と同じく左から 1 つずつ足して、重複と順を検査する (<see cref="CheckCombination"/>)。
/// </remarks>
internal sealed class GlslDeclarationRequirementAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.NotAvailable, GlslRuleDescriptors.NotAvailableInStage, GlslRuleDescriptors.QualifierNotAllowed];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            bool global = !node.Ancestors().Any(a => a is FunctionDeclarationSyntax);

            switch (node)
            {
                case TypeSyntax type when GlslKeywords.IsBuiltInType(type.NameToken.Text):
                    requirement.CheckTypeKeyword(type.NameToken);
                    break;

                // 値の作成 (uint(x)、double(x))。
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } when GlslKeywords.IsBuiltInType(callee.Name):
                    requirement.CheckTypeKeyword(callee.Identifier);
                    break;

                case VariableDeclarationSyntax variable:
                    CheckQualifiers(requirement, variable.ModifierTokens, global);
                    CheckCombination(requirement, [.. variable.ModifierTokens, .. variable.Type.ModifierTokens], vertexInput: variable.Parent is CompilationUnitSyntax);
                    break;

                // 仮引数の修飾子は、glslang では関数の外 (大域の範囲) で読む。
                case ParameterSyntax parameter:
                    CheckQualifiers(requirement, parameter.ModifierTokens, global: true);
                    CheckCombination(requirement, [.. parameter.ModifierTokens, .. parameter.Type.ModifierTokens], vertexInput: false);
                    requirement.ArrayOfArrayVersionCheck(parameter.NameToken, parameter.ArrayDimensions);
                    break;

                case FunctionDeclarationSyntax function:
                    CheckQualifiers(requirement, function.ModifierTokens, global);
                    break;

                case StructDeclarationSyntax { NameOverride: not null } block:
                    CheckQualifiers(requirement, [.. block.Modifiers, block.Keyword], global);
                    CheckCombination(requirement, [.. block.Modifiers, block.Keyword], vertexInput: false);
                    break;

                case InterfaceBlockDeclarationSyntax block:
                    CheckQualifiers(requirement, block.Modifiers, global);
                    CheckCombination(requirement, block.Modifiers, vertexInput: false);
                    break;

                case VariableDeclaratorSyntax declarator:
                    requirement.ArrayOfArrayVersionCheck(declarator.NameToken, declarator.ArrayDimensions);
                    break;

                // 修飾子だけの宣言 (layout(local_size_x = 8) in;、invariant gl_Position;)。
                case IncompleteDeclarationSyntax incomplete:
                    CheckQualifiers(requirement, incomplete.Tokens, global);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>補助の修飾子 (glslang の isAuxiliary)。</summary>
    private static readonly HashSet<string> Auxiliary = new(StringComparer.Ordinal) { "centroid", "patch", "sample" };

    /// <summary>補間の修飾子 (glslang の isInterpolation)。</summary>
    private static readonly HashSet<string> Interpolation = new(StringComparer.Ordinal)
    {
        "flat", "smooth", "noperspective", "__explicitInterpAMD", "pervertexNV", "pervertexEXT",
    };

    /// <summary>記憶域の修飾子。</summary>
    private static readonly HashSet<string> Storage = new(StringComparer.Ordinal)
    {
        "const", "in", "out", "inout", "uniform", "buffer", "attribute", "varying", "shared",
    };

    /// <summary>メモリの修飾子 (glslang の isMemory)。</summary>
    private static readonly HashSet<string> Memory = new(StringComparer.Ordinal)
    {
        "coherent", "devicecoherent", "queuefamilycoherent", "workgroupcoherent", "subgroupcoherent", "shadercallcoherent",
        "nonprivate", "volatile", "restrict", "readonly", "writeonly", "nontemporal",
    };

    /// <summary>
    /// 修飾子の並びを、左から 1 つずつ足して検査する (glslang の mergeQualifiers)。
    /// 補助の修飾子と補間の修飾子は 1 つずつしか書けない。patch と補間の修飾子は一緒に書けない。
    /// 4.20 (と ES の 3.10) より前は、GL_ARB_shading_language_420pack を有効にしない限り、
    /// precise・invariant・補間・補助・記憶域・精度の順に書かなければならない。
    /// 頂点シェーダーの入力の変数には、補助・補間・メモリの修飾子と invariant を書けない。
    /// </summary>
    private static void CheckCombination(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> tokens, bool vertexInput)
    {
        ShaderSyntaxToken[] qualifiers = [.. TopLevel(tokens).OrderBy(t => t.Span.Start)];

        if (qualifiers.Length == 0)
        {
            return;
        }

        bool ordered = (requirement.IsEs ? requirement.Version < 310 : requirement.Version < 420)
                       && !requirement.IsOn("GL_ARB_shading_language_420pack", qualifiers[0]);
        string? storage = null;
        bool precision = false, invariant = false, interpolation = false, auxiliary = false, memory = false;
        ShaderSyntaxToken? patch = null;

        foreach (ShaderSyntaxToken token in qualifiers)
        {
            string text = token.Text;
            bool isAuxiliary = Auxiliary.Contains(text);
            bool isInterpolation = Interpolation.Contains(text);
            bool isStorage = Storage.Contains(text);
            string? message = null;

            if (isAuxiliary && auxiliary)
            {
                message = "補助の修飾子 (centroid・patch・sample) は 1 つしか書けません。";
            }
            else if (isInterpolation && interpolation)
            {
                message = "補間の修飾子 (flat・smooth・noperspective) は 1 つしか書けません。";
            }
            else if (ordered)
            {
                message = text switch
                {
                    "precise" when invariant || interpolation || auxiliary || storage is not null || precision
                        => "precise は修飾子の先頭に書かなければなりません。",
                    "invariant" when interpolation || auxiliary || storage is not null || precision
                        => "invariant は補間・補助・記憶域・精度の修飾子より前に書かなければなりません。",
                    _ when isInterpolation && (auxiliary || storage is not null || precision)
                        => "補間の修飾子は補助・記憶域・精度の修飾子より前に書かなければなりません。",
                    _ when isAuxiliary && (storage is not null || precision)
                        => "補助の修飾子 (centroid・patch・sample) は記憶域と精度の修飾子より前に書かなければなりません。",
                    _ when isStorage && precision
                        => "精度の修飾子は最後に書かなければなりません。",
                    "const" when storage is "in" or "out"
                        => "const は in / out より前に書かなければなりません。",
                    _ => null,
                };

                if (message is not null)
                {
                    message += " 4.20 (ES は 3.10) より前は、修飾子の順が決まっています。";
                }
            }

            if (message is not null)
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, message);
            }

            storage = isStorage ? text : storage;
            precision |= text is "highp" or "mediump" or "lowp";
            invariant |= text == "invariant";
            interpolation |= isInterpolation;
            auxiliary |= isAuxiliary;
            memory |= Memory.Contains(text);
            patch ??= text == "patch" ? token : null;
        }

        if (patch is not null && interpolation)
        {
            requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, patch, "patch には補間の修飾子 (flat・smooth・noperspective) を一緒に書けません。");
        }

        if (vertexInput && requirement.Stage == GlslShaderStage.Vertex && storage == "in" && (auxiliary || interpolation || memory || invariant))
        {
            requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, qualifiers[0], "頂点シェーダーの入力には、補助・補間・メモリの修飾子と invariant を書けません。");
        }
    }

    /// <summary>括弧の外 (<c>layout(…)</c> の中でない) の修飾子のキーワード。</summary>
    private static IEnumerable<ShaderSyntaxToken> TopLevel(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        int depth = 0;

        foreach (ShaderSyntaxToken token in tokens)
        {
            depth += token.Kind switch
            {
                ShaderSyntaxKind.OpenParenToken => 1,
                ShaderSyntaxKind.CloseParenToken => -1,
                _ => 0,
            };

            if (depth == 0 && token.Kind == ShaderSyntaxKind.IdentifierToken && token.Text != "layout")
            {
                yield return token;
            }
        }
    }

    /// <summary>修飾子のトークンを検査する。括弧の中 (<c>layout(…)</c> の値) は飛ばす。</summary>
    private static void CheckQualifiers(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> tokens, bool global)
    {
        int depth = 0;

        foreach (ShaderSyntaxToken token in tokens)
        {
            switch (token.Kind)
            {
                case ShaderSyntaxKind.OpenParenToken:
                    depth++;
                    break;

                case ShaderSyntaxKind.CloseParenToken:
                    depth--;
                    break;

                case ShaderSyntaxKind.IdentifierToken when depth == 0 && GlslKeywords.Qualifiers.Contains(token.Text):
                    requirement.CheckQualifierKeyword(token, global);
                    break;

                default:
                    break;
            }
        }
    }
}
