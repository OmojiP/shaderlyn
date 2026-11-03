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
                    CheckStorageCount(requirement, [.. variable.ModifierTokens, .. variable.Type.ModifierTokens]);

                    // ブロックと構造体のメンバーには nonuniformEXT を書けない (glslang の「not allowed on block or structure members」)。
                    if (variable.Parent is StructDeclarationSyntax or InterfaceBlockDeclarationSyntax
                        && TopLevel([.. variable.ModifierTokens, .. variable.Type.ModifierTokens]).FirstOrDefault(t => t.Text == "nonuniformEXT") is { } memberNonuniform)
                    {
                        requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, memberNonuniform, "ブロックと構造体のメンバーには nonuniformEXT を書けません。");
                    }
                    CheckCombination(requirement, [.. variable.ModifierTokens, .. variable.Type.ModifierTokens], vertexInput: variable.Parent is CompilationUnitSyntax);

                    if (variable.Parent is CompilationUnitSyntax)
                    {
                        CheckGlobalStorage(requirement, variable);
                    }

                    if (variable.Parent is CompilationUnitSyntax && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types)
                    {
                        CheckInterfaceVariable(requirement, types, program, variable);
                        CheckMemoryQualifiers(requirement, program, variable);
                        CheckInvariant(requirement, program, [.. variable.ModifierTokens, .. variable.Type.ModifierTokens]);
                    }

                    if (variable.Parent is StructDeclarationSyntax { Keyword.Text: "struct" })
                    {
                        CheckStructMember(requirement, variable);
                    }

                    break;

                // 仮引数の修飾子は、glslang では関数の外 (大域の範囲) で読む。
                case ParameterSyntax parameter:
                    CheckQualifiers(requirement, parameter.ModifierTokens, global: true);
                    CheckParameterQualifiers(requirement, [.. parameter.ModifierTokens, .. parameter.Type.ModifierTokens]);
                    CheckStorageCount(requirement, [.. parameter.ModifierTokens, .. parameter.Type.ModifierTokens]);
                    CheckCombination(requirement, [.. parameter.ModifierTokens, .. parameter.Type.ModifierTokens], vertexInput: false);
                    requirement.ArrayOfArrayVersionCheck(parameter.NameToken, parameter.ArrayDimensions);
                    break;

                case FunctionDeclarationSyntax function:
                    CheckQualifiers(requirement, function.ModifierTokens, global);

                    // 関数の中の関数の宣言 (glslang の「local function declaration」。ES では書けない)。
                    if (function.Parent is LocalDeclarationStatementSyntax)
                    {
                        requirement.RequireProfile(function.NameToken, GlslProfileMask.NotEs, "'local function declaration'");
                    }

                    // 配列の配列を返す関数 (int[2][3] f())。
                    requirement.ArrayOfArrayVersionCheck(function.NameToken, ShaderSyntaxFacts.CountArrayDimensions(function.ReturnType.ArrayRanks));

                    // 配列か、配列を含む構造体を返す関数 (glslang の handleFunctionDeclarator の containsArray と arrayObjectCheck)。
                    if (!function.ReturnType.ArrayRanks.IsEmpty || GlslExpressionRequirementAnalyzer.StructContainsArray(program, function.ReturnType.Name))
                    {
                        requirement.ArrayObjectCheck(function.NameToken, "array in function return type");
                    }

                    // 戻り値の型に記憶域の修飾子は書けない (glslang の function_header の「no qualifiers allowed for function return」)。
                    if (TopLevel([.. function.ModifierTokens, .. function.ReturnType.ModifierTokens]).FirstOrDefault(t => Storage.Contains(t.Text)) is { } storage)
                    {
                        requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, storage, $"関数の戻り値には '{storage.Text}' を書けません。");
                    }

                    break;

                case StructDeclarationSyntax { NameOverride: not null } block:
                    CheckQualifiers(requirement, [.. block.Modifiers, block.Keyword], global);
                    CheckCombination(requirement, [.. block.Modifiers, block.Keyword], vertexInput: false);
                    CheckBlockStage(requirement, [.. block.Modifiers, block.Keyword], block.Name);
                    CheckBlockQualifiers(requirement, [.. block.Modifiers, block.Keyword]);
                    CheckBlockMembers(requirement, block.Keyword.Text, block.Fields);
                    CheckUniformBlock(requirement, [.. block.Modifiers, block.Keyword]);

                    // ES では、ブロックのインスタンスを配列の配列にできない (glslang の declareBlock の「array-of-array of block」)。
                    if (block.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>().Any(d => d.ArrayDimensions > 1))
                    {
                        requirement.RequireProfile(block.Keyword, GlslProfileMask.NotEs, "'array-of-array of block'");
                    }

                    break;

                case InterfaceBlockDeclarationSyntax block:
                    CheckQualifiers(requirement, block.Modifiers, global);
                    CheckCombination(requirement, block.Modifiers, vertexInput: false);
                    CheckBlockStage(requirement, block.Modifiers, block.Name);
                    CheckBlockQualifiers(requirement, block.Modifiers);
                    CheckBlockMembers(requirement, TopLevel(block.Modifiers).LastOrDefault(t => t.Text is "in" or "out" or "uniform" or "buffer")?.Text, block.Members.OfType<VariableDeclarationSyntax>());
                    CheckUniformBlock(requirement, block.Modifiers);
                    break;

                case VariableDeclaratorSyntax declarator:
                    requirement.ArrayOfArrayVersionCheck(declarator.NameToken, declarator.ArrayDimensions);

                    // 仮引数でない変数の nonuniformEXT は、in か記憶域の無いものにだけ書ける (glslang の nonUniformCheck)。宣言の最初の名前で 1 度だけ見る。
                    if (declarator.Parent is VariableDeclarationSyntax { Parent: not (StructDeclarationSyntax or InterfaceBlockDeclarationSyntax) } owner
                        && owner.Variables.FirstOrDefault() == declarator
                        && TopLevel([.. owner.ModifierTokens, .. owner.Type.ModifierTokens]).ToArray() is var top
                        && top.FirstOrDefault(t => t.Text == "nonuniformEXT") is { } nonuniform
                        && top.FirstOrDefault(t => Storage.Contains(t.Text) && t.Text != "in") is { } badStorage)
                    {
                        requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, nonuniform, $"nonuniformEXT は、仮引数でない変数では in か記憶域の無いものにだけ書けます ('{badStorage.Text}')。");
                    }

                    // 初期値のある、配列か配列を含む構造体の変数 (glslang の declareVariable と executeInitializer の containsArray と arrayObjectCheck)。
                    if (declarator is { Initializer: not null, Parent: VariableDeclarationSyntax declaration }
                        && (!declarator.ArrayRankTokens.IsEmpty || !declaration.Type.ArrayRanks.IsEmpty
                            || GlslExpressionRequirementAnalyzer.StructContainsArray(program, declaration.Type.Name, declarator)))
                    {
                        requirement.ArrayObjectCheck(declarator.NameToken, "array initializer");
                    }

                    break;

                // 関数の外の余分な ; (glslang.y の external_declaration の SEMICOLON)。
                // 前の宣言が ; か } で終わっているものだけを見る。構文の誤りから立ち直るときにも ; だけの宣言ができる。
                case IncompleteDeclarationSyntax { Tokens: [{ Kind: ShaderSyntaxKind.SemicolonToken } semicolon], Parent: CompilationUnitSyntax unit } extra
                    when (compilation.HasCompleteDependencies || compilation.IsBeforeFirstBreak(extra)) && FollowsCompleteDeclaration(unit, extra):
                    requirement.RequireProfile(semicolon, GlslProfileMask.NotEs, "'extraneous semicolon'");
                    requirement.ProfileRequires(semicolon, GlslProfileMask.NotEs, 460, [], "'extraneous semicolon'");
                    break;

                // 波括弧の初期化子 (glslang.y の initializer)。空の {} は GL_EXT_null_initializer が要る。
                case InitializerListExpressionSyntax { Elements.IsEmpty: true } empty:
                    requirement.ProfileRequires(empty.OpenBrace, GlslProfileMask.Es, 0, ["GL_EXT_null_initializer"], "'empty { } initializer'");
                    requirement.ProfileRequires(empty.OpenBrace, GlslProfileMask.NotEs, 0, ["GL_EXT_null_initializer"], "'empty { } initializer'");
                    break;

                case InitializerListExpressionSyntax list:
                    requirement.RequireProfile(list.OpenBrace, GlslProfileMask.NotEs, "'{ } style initializers'");
                    requirement.ProfileRequires(list.OpenBrace, GlslProfileMask.NotEs, 420, ["GL_ARB_shading_language_420pack"], "'{ } style initializers'");
                    break;

                // 修飾子だけの宣言 (layout(local_size_x = 8) in;、invariant gl_Position;)。
                case IncompleteDeclarationSyntax incomplete:
                    CheckQualifiers(requirement, incomplete.Tokens, global);
                    CheckQualifierToExisting(requirement, incomplete.Tokens);

                    if (global)
                    {
                        CheckInvariant(requirement, program, incomplete.Tokens);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>宣言が、; か } で終わる宣言の直後 (かファイルの先頭) にあるか。</summary>
    private static bool FollowsCompleteDeclaration(CompilationUnitSyntax unit, DeclarationSyntax declaration)
    {
        int index = unit.Declarations.IndexOf(declaration);
        return index <= 0 || unit.Declarations[index - 1].DescendantTokens().LastOrDefault() is { Kind: ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken };
    }

    /// <summary>
    /// uniform のブロックに要るバージョンと拡張 (ES の 3.00、デスクトップの 1.40 か GL_ARB_uniform_buffer_object)。glslang の blockStageIoCheck。
    /// </summary>
    private static void CheckUniformBlock(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> qualifiers)
    {
        if (TopLevel(qualifiers).LastOrDefault(t => t.Text == "uniform") is not { } keyword)
        {
            return;
        }

        requirement.ProfileRequires(keyword, GlslProfileMask.Es, 300, [], "'uniform block'");
        requirement.ProfileRequires(keyword, GlslProfileMask.None, 140, ["GL_ARB_uniform_buffer_object"], "'uniform block'");
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
    /// 既にある名前に修飾子を足す宣言 (<c>invariant gl_Position;</c>) には、<c>invariant</c> と <c>precise</c> しか書けない
    /// (glslang の addQualifierToExisting)。<c>layout(buffer_reference)</c> の前方宣言は除く。
    /// </summary>
    private static void CheckQualifierToExisting(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> tokens)
    {
        // 精度の文 (precision mediump float;)、buffer_reference の前方宣言、特殊化の定数の組み込み (layout(constant_id = 1) gl_MaxImageUnits;) は除く。
        if (tokens.IsEmpty
            || tokens[0].Text == "precision"
            || tokens.Any(t => t.Text is "buffer_reference" or "constant_id")
            || tokens.Length >= 2 && GlslKeywords.IsBuiltInType(tokens[^2].Text))
        {
            return;
        }

        // 修飾子は、括弧の外で最初の名前 (修飾子のキーワードでない識別子) より前。その後ろは名前とカンマ (invariant gl_Position, gl_PointSize;)。
        int depth = 0;
        int nameIndex = -1;

        for (int i = 0; i < tokens.Length; i++)
        {
            depth += tokens[i].Kind switch
            {
                ShaderSyntaxKind.OpenParenToken => 1,
                ShaderSyntaxKind.CloseParenToken => -1,
                _ => 0,
            };

            if (depth == 0 && tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && !GlslKeywords.Qualifiers.Contains(tokens[i].Text) && tokens[i].Text != "layout")
            {
                nameIndex = i;
                break;
            }
        }

        if (nameIndex <= 0
            || tokens[nameIndex..].Any(t => t.Kind is not (ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.SemicolonToken)))
        {
            return;
        }

        ShaderSyntaxToken name = tokens[nameIndex];
        ImmutableArray<ShaderSyntaxToken> qualifiers = tokens[..nameIndex];

        if ((qualifiers.FirstOrDefault(t => t.Text == "layout") ?? TopLevel(qualifiers).FirstOrDefault(t => t.Text is not ("invariant" or "precise"))) is { } other)
        {
            requirement.Report(
                GlslRuleDescriptors.QualifierNotAllowed,
                other,
                $"既にある名前 '{name.Text}' に足せる修飾子は invariant と precise だけです ('{other.Text}')。");
        }
    }

    /// <summary>
    /// 入力のブロックは頂点シェーダーとコンピュートシェーダーに、出力のブロックはフラグメントシェーダーとコンピュートシェーダーに書けない
    /// (glslang の「input block」「output block」の requireStage)。
    /// 入出力のブロックに要る版も見る (glslang の blockStageIoCheck の profileRequires)。デスクトップは 1.50 から、
    /// ES の頂点シェーダーの出力とフラグメントシェーダーの入力は 3.20 か GL_EXT_shader_io_blocks から。組み込みのブロックの宣言し直し (gl_PerVertex) は見ない。
    /// </summary>
    private static void CheckBlockStage(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> modifiers, string name)
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

        // 組み込みのブロックの宣言し直し (glslang の redeclareBuiltinBlock の profileRequires)。
        if (name.StartsWith("gl_", StringComparison.Ordinal))
        {
            requirement.ProfileRequires(storage, GlslProfileMask.Es, 320, ["GL_EXT_shader_io_blocks", "GL_OES_shader_io_blocks"], "'built-in block redeclaration'");
            requirement.ProfileRequires(storage, GlslProfileMask.NotEs, 410, ["GL_ARB_separate_shader_objects"], "'built-in block redeclaration'");
            return;
        }

        requirement.ProfileRequires(storage, GlslProfileMask.NotEs, 150, ["GL_ARB_separate_shader_objects"], input ? "'input block'" : "'output block'");

        if (input && stage == GlslShaderStage.Fragment)
        {
            requirement.ProfileRequires(storage, GlslProfileMask.Es, 320, ["GL_EXT_shader_io_blocks", "GL_OES_shader_io_blocks"], "'fragment input block'");
        }
        else if (!input && stage == GlslShaderStage.Vertex)
        {
            requirement.ProfileRequires(storage, GlslProfileMask.Es, 320, ["GL_EXT_shader_io_blocks", "GL_OES_shader_io_blocks"], "'vertex output block'");
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

            // glslang の blockQualifierCheck の「cannot use invariant qualifier on an interface block」。
            if (token.Text == "invariant")
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, "ブロックには invariant を書けません。メンバーに書いてください。");
            }
        }
    }

    /// <summary>
    /// ブロックのメンバーの修飾子 (glslang の addBlock)。メンバーの記憶域はブロックと同じでなければならず、
    /// uniform と buffer のブロックのメンバーには補助・補間の修飾子を書けない。
    /// </summary>
    private static void CheckBlockMembers(GlslRequirements requirement, string? storage, IEnumerable<VariableDeclarationSyntax> fields)
    {
        if (storage is not ("in" or "out" or "uniform" or "buffer"))
        {
            return;
        }

        foreach (VariableDeclarationSyntax member in fields)
        {
            ShaderSyntaxToken[] qualifiers = [.. TopLevel([.. member.ModifierTokens, .. member.Type.ModifierTokens])];

            if (member.Variables.FirstOrDefault() is not { } first)
            {
                continue;
            }

            if (qualifiers.FirstOrDefault(t => t.Text is "in" or "out" or "inout" or "uniform" or "buffer" or "shared" or "const" or "attribute" or "varying") is { } own
                && own.Text != storage)
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, first.NameToken, $"ブロックのメンバー '{first.Name}' の記憶域 '{own.Text}' は、ブロックの記憶域 '{storage}' と違います。");
            }

            if (storage is "uniform" or "buffer" && qualifiers.FirstOrDefault(t => Auxiliary.Contains(t.Text) || Interpolation.Contains(t.Text)) is { } interpolation)
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, first.NameToken, $"{storage} のブロックのメンバー '{first.Name}' には、補助・補間の修飾子 '{interpolation.Text}' を書けません。");
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

    /// <summary>
    /// invariant は出力にだけ付けられる (glslang の invariantCheck)。ES の 3.00 より前とデスクトップの 4.20 より前は、頂点シェーダーでない入力にも付けられる。
    /// 宣言し直し (<c>invariant name;</c>) は、関数の外で宣言した変数の記憶域で決める。組み込みの変数は見ない。
    /// </summary>
    private static void CheckInvariant(GlslRequirements requirement, AnalyzedProgram program, ImmutableArray<ShaderSyntaxToken> tokens)
    {
        ShaderSyntaxToken[] qualifiers = [.. TopLevel(tokens)];

        if (qualifiers.FirstOrDefault(t => t.Text == "invariant") is not { } invariant || requirement.Stage is not { } stage)
        {
            return;
        }

        string? storage = qualifiers.LastOrDefault(t => Storage.Contains(t.Text))?.Text;

        // invariant name; は、名前の宣言の記憶域で決める。名前を並べたもの (invariant gl_Position, gl_PointSize;) は見ない。
        if (storage is null)
        {
            if (qualifiers is not [_, { } name])
            {
                return;
            }

            if (name.Text.StartsWith("gl_", StringComparison.Ordinal)
                || program.Tree.Root.Declarations.OfType<VariableDeclarationSyntax>().FirstOrDefault(d => d.Variables.Any(v => v.Name == name.Text)) is not { } declared)
            {
                return;
            }

            storage = TopLevel([.. declared.ModifierTokens, .. declared.Type.ModifierTokens]).LastOrDefault(t => Storage.Contains(t.Text))?.Text;
        }

        bool output = storage is "out" || (storage is "varying" && stage != GlslShaderStage.Fragment);
        bool input = storage is "in" or "attribute" || (storage is "varying" && stage == GlslShaderStage.Fragment);
        bool strict = requirement.IsEs ? requirement.Version >= 300 : requirement.Version >= 420;

        if (strict ? !output : (stage == GlslShaderStage.Vertex && input) || (!output && !input))
        {
            requirement.Report(
                GlslRuleDescriptors.QualifierNotAllowed,
                invariant,
                strict ? "invariant は出力にだけ付けられます。" : "invariant は出力と、頂点シェーダーでない入力にだけ付けられます。");
        }
    }

    /// <summary>
    /// 記憶域の修飾子は 1 つだけ書ける (glslang の mergeQualifiers の「too many storage qualifiers」)。
    /// <c>in</c> と <c>out</c> (<c>inout</c> になる)、<c>const</c> と <c>in</c> の組は書ける。
    /// </summary>
    private static void CheckStorageCount(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> tokens)
    {
        string? storage = null;

        foreach (ShaderSyntaxToken token in TopLevel(tokens))
        {
            if (!Storage.Contains(token.Text))
            {
                continue;
            }

            storage = (storage, token.Text) switch
            {
                (null, string text) => text,
                ("in", "out") or ("out", "in") => "inout",
                ("in", "const") or ("const", "in") => "const in",
                _ => null,
            };

            if (storage is null)
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, $"記憶域の修飾子は 1 つしか書けません ('{token.Text}')。");
                return;
            }
        }
    }

    /// <summary>
    /// 仮引数に書けない修飾子 (glslang の <c>paramCheckFix</c>)。補助・補間の修飾子、layout、invariant は書けない。
    /// </summary>
    /// <param name="requirement">バージョンと拡張。</param>
    /// <param name="tokens">仮引数とその型の修飾子。</param>
    private static void CheckParameterQualifiers(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> tokens)
    {
        if (tokens.FirstOrDefault(t => t.Text == "layout") is { } layout)
        {
            requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, layout, "仮引数には layout を書けません。");
        }

        foreach (ShaderSyntaxToken token in TopLevel(tokens))
        {
            if (Auxiliary.Contains(token.Text) || Interpolation.Contains(token.Text))
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, $"仮引数には補助・補間の修飾子 '{token.Text}' を書けません。");
            }
            else if (token.Text == "invariant")
            {
                requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, "仮引数には invariant を書けません。");
            }
        }
    }

    /// <summary>
    /// 関数の外の変数の記憶域を見る (glslang の <c>declareVariable</c> と <c>globalQualifierTypeCheck</c>)。
    /// </summary>
    /// <param name="requirement">バージョンと拡張。</param>
    /// <param name="variable">関数の外の変数の宣言。</param>
    /// <remarks>
    /// <c>buffer</c> はブロックにしか書けない。計算シェーダーには <c>in</c> / <c>out</c> の変数を宣言できない。
    /// <c>shared</c> の変数の初期値には <c>{}</c> しか書けない (<c>GL_EXT_null_initializer</c>)。
    /// </remarks>
    private static void CheckGlobalStorage(GlslRequirements requirement, VariableDeclarationSyntax variable)
    {
        foreach (ShaderSyntaxToken token in TopLevel([.. variable.ModifierTokens, .. variable.Type.ModifierTokens]))
        {
            switch (token.Text)
            {
                case "buffer":
                    requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, "buffer はブロックにしか書けません。変数には書けません。");
                    break;

                // テッセレーションの制御シェーダーの patch は出力だけ、評価シェーダーの patch は入力だけ (glslang の globalQualifierTypeCheck)。
                case "in" when requirement.Stage == GlslShaderStage.TessellationControl && TopLevel([.. variable.ModifierTokens]).Any(t => t.Text == "patch"):
                    requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, "テッセレーションの制御シェーダーでは、patch は出力にだけ付けられます。");
                    break;

                case "out" when requirement.Stage == GlslShaderStage.TessellationEvaluation && TopLevel([.. variable.ModifierTokens]).Any(t => t.Text == "patch"):
                    requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, "テッセレーションの評価シェーダーでは、patch は入力にだけ付けられます。");
                    break;

                // Vulkan では、数の型の uniform の変数はブロックの外に置けない (glslang の vulkanRemoved の「non-opaque uniforms outside a block」)。
                case "uniform" when requirement.Vulkan == true && IsNumericTypeName(variable.Type.Name):
                    requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, $"Vulkan 向けの GLSL では、{variable.Type.Name} の uniform の変数はブロックの外に置けません。uniform のブロックのメンバーにしてください。");
                    break;

                case "in" or "out" when requirement.Stage == GlslShaderStage.Compute:
                    requirement.Report(GlslRuleDescriptors.NotAvailableInStage, token, $"関数の外の '{token.Text}' の変数", GlslRequirements.DescribeStage(GlslShaderStage.Compute));
                    break;

                case "shared" when variable.Variables.FirstOrDefault(v => v.Initializer is not null and not InitializerListExpressionSyntax { Elements.IsEmpty: true }) is { } initialized:
                    requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, token, $"shared の変数 '{initialized.Name}' の初期値には {{}} しか書けません。");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>スカラー・ベクトル・行列の組み込みの型の名前か。</summary>
    private static bool IsNumericTypeName(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name,
            "^(bool|int|uint|float|double|[biud]?vec[234]|d?mat[234](x[234])?)$");

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
