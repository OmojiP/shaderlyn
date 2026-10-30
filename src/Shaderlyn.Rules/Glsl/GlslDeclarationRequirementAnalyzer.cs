using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

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

                    if (variable.Parent is CompilationUnitSyntax && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types)
                    {
                        CheckInterfaceVariable(requirement, types, program, variable);
                        CheckMemoryQualifiers(requirement, program, variable);
                    }

                    if (variable.Parent is StructDeclarationSyntax { Keyword.Text: "struct" })
                    {
                        CheckStructMember(requirement, variable);
                    }

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
                    CheckBlockStage(requirement, [.. block.Modifiers, block.Keyword]);
                    CheckBlockQualifiers(requirement, [.. block.Modifiers, block.Keyword]);
                    break;

                case InterfaceBlockDeclarationSyntax block:
                    CheckQualifiers(requirement, block.Modifiers, global);
                    CheckCombination(requirement, block.Modifiers, vertexInput: false);
                    CheckBlockStage(requirement, block.Modifiers);
                    CheckBlockQualifiers(requirement, block.Modifiers);
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

    /// <summary>
    /// 入力のブロックは頂点シェーダーとコンピュートシェーダーに、出力のブロックはフラグメントシェーダーとコンピュートシェーダーに書けない
    /// (glslang の「input block」「output block」の requireStage)。
    /// </summary>
    private static void CheckBlockStage(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> modifiers)
    {
        if (requirement.Stage is not { } stage || TopLevel(modifiers).LastOrDefault(t => t.Text is "in" or "out") is not { } storage)
        {
            return;
        }

        bool input = storage.Text == "in";

        if (stage == GlslShaderStage.Compute
            || (input && stage == GlslShaderStage.Vertex)
            || (!input && stage == GlslShaderStage.Fragment))
        {
            requirement.Report(GlslRuleDescriptors.NotAvailableInStage, storage, input ? "入力のブロック" : "出力のブロック", GlslRequirements.DescribeStage(stage));
        }
    }

    /// <summary>
    /// フラグメントシェーダーの出力と、整数の入出力の修飾子と型 (glslang の globalQualifierTypeCheck)。
    /// フラグメントシェーダーの出力には補助・補間の修飾子を書けず、行列・構造体・double・64 ビットの整数にできない。
    /// 整数と double の、フラグメントシェーダーの入力 (と ES の 3.00 の頂点シェーダーの出力) には flat が要る。
    /// </summary>
    private static void CheckInterfaceVariable(GlslRequirements requirement, GlslTypeSystem types, AnalyzedProgram program, VariableDeclarationSyntax declaration)
    {
        ShaderSyntaxToken[] qualifiers = [.. TopLevel([.. declaration.ModifierTokens, .. declaration.Type.ModifierTokens])];

        if (requirement.Stage is not { } stage
            || qualifiers.LastOrDefault(t => t.Text is "in" or "out" or "varying") is not { } storage)
        {
            return;
        }

        bool input = storage.Text == "in" || (storage.Text == "varying" && stage == GlslShaderStage.Fragment);
        string type = declaration.Type.Name;
        bool numeric = types.TryDescribeNumeric(type, out ShaderNumericShape shape);

        if (stage == GlslShaderStage.Fragment && !input)
        {
            string? problem =
                qualifiers.Any(t => Auxiliary.Contains(t.Text)) ? "補助の修飾子 (centroid・sample・patch) を書けません"
                : qualifiers.Any(t => Interpolation.Contains(t.Text)) ? "補間の修飾子 (flat・smooth・noperspective) を書けません"
                : numeric && shape.Kind == ShaderNumericKind.Matrix ? "行列にできません"
                : program.Structs.Any(s => s.Name == type && s.Keyword.Text == "struct") ? "構造体にできません"
                : numeric && shape.BaseName is "double" or "int64_t" or "uint64_t" ? "double・int64_t・uint64_t にできません"
                : null;

            if (problem is not null)
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, storage, $"フラグメントシェーダーの出力は{problem}。");
            }

            return;
        }

        bool needsFlat = (input && stage == GlslShaderStage.Fragment)
                         || (!input && stage == GlslShaderStage.Vertex && requirement.IsEs && requirement.Version == 300);

        if (needsFlat
            && numeric
            && shape.BaseName is "int" or "uint" or "double" or "int8_t" or "uint8_t" or "int16_t" or "uint16_t" or "int64_t" or "uint64_t"
            && !qualifiers.Any(t => t.Text is "flat" or "__explicitInterpAMD" or "pervertexNV" or "pervertexEXT"))
        {
            requirement.Report(
                GlslRuleDescriptors.QualifierNotAllowed,
                storage,
                $"整数と double の{(input ? "フラグメントシェーダーの入力" : "頂点シェーダーの出力")}には flat を付けなければなりません ('{type}')。");
        }
    }

    /// <summary>構造体のメンバーには、layout・記憶域・補助・補間・メモリの修飾子と invariant を書けない (glslang の addStructure の検査)。</summary>
    private static void CheckStructMember(GlslRequirements requirement, VariableDeclarationSyntax member)
    {
        ImmutableArray<ShaderSyntaxToken> modifiers = [.. member.ModifierTokens, .. member.Type.ModifierTokens];
        ShaderSyntaxToken[] qualifiers = [.. TopLevel(modifiers)];
        ShaderSyntaxToken at = member.Variables.FirstOrDefault()?.NameToken ?? member.Type.NameToken;

        string? problem =
            modifiers.Any(t => t.Text == "layout") ? "layout"
            : qualifiers.Any(t => Auxiliary.Contains(t.Text) || Interpolation.Contains(t.Text) || (Storage.Contains(t.Text) && t.Text != "const")) ? "記憶域・補助・補間の修飾子"
            : qualifiers.Any(t => Memory.Contains(t.Text)) ? "メモリの修飾子"
            : qualifiers.Any(t => t.Text == "invariant") ? "invariant"
            : null;

        if (problem is not null)
        {
            requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, at, $"構造体のメンバーには{problem}を書けません。");
        }
    }

    /// <summary>ブロックには補間の修飾子 (flat・smooth・noperspective) と centroid を書けない (glslang の blockQualifierCheck)。pervertexEXT などは書ける。</summary>
    private static void CheckBlockQualifiers(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> modifiers)
    {
        foreach (ShaderSyntaxToken token in TopLevel(modifiers))
        {
            if (token.Text is "flat" or "smooth" or "noperspective" or "__explicitInterpAMD" or "centroid")
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, $"ブロックには '{token.Text}' を書けません。メンバーに書いてください。");
            }
        }
    }

    /// <summary>
    /// メモリの修飾子は、イメージ (とサンプラー) の変数と、uniform・buffer の宣言にだけ書ける (glslang の globalQualifierTypeCheck)。
    /// 利用者の型 (buffer_reference の型など) は見ない。
    /// </summary>
    private static void CheckMemoryQualifiers(GlslRequirements requirement, AnalyzedProgram program, VariableDeclarationSyntax declaration)
    {
        ShaderSyntaxToken[] qualifiers = [.. TopLevel([.. declaration.ModifierTokens, .. declaration.Type.ModifierTokens])];
        string type = declaration.Type.Name;

        if (qualifiers.FirstOrDefault(t => Memory.Contains(t.Text) || t.Text == "restrict") is not { } memory
            || qualifiers.Any(t => t.Text is "uniform" or "buffer")
            || !GlslKeywords.IsBuiltInType(type)
            || type.Contains("image", StringComparison.Ordinal)
            || type.Contains("sampler", StringComparison.Ordinal)
            || type.Contains("texture", StringComparison.Ordinal)
            || type.StartsWith("tensor", StringComparison.Ordinal))
        {
            return;
        }

        requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, memory, $"'{memory.Text}' は、イメージの変数と uniform・buffer の宣言にだけ書けます。");
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
