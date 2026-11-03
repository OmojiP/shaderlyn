using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 組み込みの変数とブロックの宣言し直しを検査する (GL0406)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>redeclareBuiltinVariable</c>・<c>redeclareBuiltinBlock</c> と、<c>declareVariable</c> の型と配列の大きさの検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 関数の外の変数の宣言のうち、組み込みの変数を宣言し直せるもの (<see cref="GlslReservedNameAnalyzer.MayRedeclare"/>) を、書いた順に見る。
/// glslang と同じく、前の宣言し直しで決まったこと (<c>gl_FragCoord</c> の layout、<c>gl_FragDepth</c> の depth、配列の大きさ) と比べる。
/// </para>
/// <list type="bullet">
///   <item><description>型 (要素の型) は変えられない</description></item>
///   <item><description><c>gl_FrontColor</c> などの色は、layout を書けず、記憶域・メモリ・補助の修飾子を変えられない</description></item>
///   <item><description><c>gl_TexCoord</c>・<c>gl_ClipDistance</c>・<c>gl_CullDistance</c> は、layout・メモリ・補助・<c>flat</c>・<c>noperspective</c>・記憶域を変えられない。大きさを書いた後は、違う大きさにできない</description></item>
///   <item><description><c>gl_FragCoord</c> は、最初の宣言し直しを使った後に書けない。2 つ目からは、それまでと同じ layout (<c>origin_upper_left</c>・<c>pixel_center_integer</c>) でなければならない</description></item>
///   <item><description><c>gl_FragDepth</c> は、depth の layout を書くなら使う前で、それまでと同じ depth でなければならない</description></item>
///   <item><description>1.40 以前で <c>GL_ARB_separate_shader_objects</c> を有効にした <c>gl_Position</c> などは、使う前に、そのステージの向きの記憶域で、補間を変えずに書く</description></item>
/// </list>
/// <para>
/// ブロック (<c>out gl_PerVertex { … };</c>、<c>in gl_PerVertex { … } gl_in[];</c>) は、宣言し直せる名前か、インスタンスの名前が組み込みのものか、
/// 1 度だけか、組み込みのメンバーだけか、メンバーの型と配列かどうかを変えていないかを見る。
/// 名前の無い出力のブロックを宣言し直した後は、含めなかったメンバー (<c>gl_PointSize</c> など) を使えない。
/// </para>
/// </remarks>
internal sealed class GlslBuiltinRedeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>宣言し直せるブロック (redeclareBuiltinBlock)。</summary>
    private static readonly HashSet<string> RedeclarableBlocks =
        ["gl_PerVertex", "gl_PerFragment", "gl_MeshPerVertexNV", "gl_MeshPerPrimitiveNV", "gl_MeshPerVertexEXT", "gl_MeshPerPrimitiveEXT"];

    /// <summary>名前の無い gl_PerVertex のメンバーになりうる組み込みの変数 (表の gl_in と gl_out のメンバー)。</summary>
    private static readonly HashSet<string> PerVertexMembers =
    [
        "gl_Position", "gl_PointSize", "gl_ClipDistance", "gl_CullDistance", "gl_ClipVertex", "gl_FrontColor", "gl_BackColor",
        "gl_FrontSecondaryColor", "gl_BackSecondaryColor", "gl_TexCoord", "gl_FogFragCoord", "gl_ViewportMask",
        "gl_SecondaryPositionNV", "gl_SecondaryViewportMaskNV", "gl_PositionPerViewNV", "gl_ViewportMaskPerViewNV",
    ];

    /// <summary>色の組み込みの変数。補間の修飾子だけを変えられる。</summary>
    private static readonly HashSet<string> Colors =
        ["gl_FrontColor", "gl_BackColor", "gl_FrontSecondaryColor", "gl_BackSecondaryColor", "gl_SecondaryColor", "gl_Color"];

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.BuiltinVariableRedeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (GlslRequirements.Create(context, compilation, program, reported) is not { Stage: { } stage } requirement
                || compilation.TypeSystem.ForTree(program.Tree) is not GlslTypeSystem types)
            {
                continue;
            }

            // 名前ごとの、使った位置 (いちばん前のもの)。
            Dictionary<string, ShaderSyntaxToken> firstUse = new(StringComparer.Ordinal);

            foreach (IdentifierExpressionSyntax use in program.Tree.Root.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
            {
                if (use.Name.StartsWith("gl_", StringComparison.Ordinal)
                    && (!firstUse.TryGetValue(use.Name, out ShaderSyntaxToken? known) || IsBefore(use.Identifier, known)))
                {
                    firstUse[use.Name] = use.Identifier;
                }
            }

            State state = new();

            foreach (VariableDeclarationSyntax declaration in program.Tree.Root.Declarations.OfType<VariableDeclarationSyntax>())
            {
                foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                {
                    string name = declarator.Name;

                    if (!name.StartsWith("gl_", StringComparison.Ordinal)
                        || !compilation.IsReportable(declarator.NameToken)
                        || !GlslReservedNameAnalyzer.MayRedeclare(requirement, name, declarator.NameToken)
                        || types.Builtins.GetVariables(name) is not { IsEmpty: false } builtins)
                    {
                        continue;
                    }

                    Qualifiers qualifiers = Qualifiers.Read([.. declaration.ModifierTokens, .. declaration.Type.ModifierTokens], stage);
                    bool usedBefore = firstUse.TryGetValue(name, out ShaderSyntaxToken? use) && IsBefore(use, declarator.NameToken);

                    foreach (string reason in Check(requirement, stage, state, name, declaration, declarator, qualifiers, builtins, usedBefore))
                    {
                        requirement.Report(GlslRuleDescriptors.BuiltinVariableRedeclaration, declarator.NameToken, name, reason);
                    }
                }
            }

            CheckBlocks(compilation, program, requirement, types);
        }
    }

    /// <summary>組み込みのブロックの宣言し直しを検査する (redeclareBuiltinBlock)。</summary>
    private static void CheckBlocks(ShaderCompilation compilation, AnalyzedProgram program, GlslRequirements requirement, GlslTypeSystem types)
    {
        HashSet<string> redeclared = new(StringComparer.Ordinal);

        foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
        {            (string name, ShaderSyntaxToken at, string storage, IEnumerable<VariableDeclarationSyntax> members, VariableDeclaratorSyntax? instance) = declaration switch
            {
                InterfaceBlockDeclarationSyntax block => (block.Name, block.Modifiers.FirstOrDefault() ?? block.NameToken, Storage(block.Modifiers), block.Members.OfType<VariableDeclarationSyntax>(), null),
                // 名前のあるブロックの Name は記憶域を含む (in gl_PerVertex)。名前のトークンから取る。
                StructDeclarationSyntax { Keyword.Text: not "struct", NameToken.Text: { } blockName } block => (
                    blockName,
                    block.Modifiers.FirstOrDefault() ?? block.Keyword,
                    block.Keyword.Text,
                    block.Fields,
                    block.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>().FirstOrDefault()),
                _ => (string.Empty, null!, string.Empty, Enumerable.Empty<VariableDeclarationSyntax>(), (VariableDeclaratorSyntax?)null),
            };

            if (!name.StartsWith("gl_", StringComparison.Ordinal) || !compilation.IsReportable(at))
            {
                continue;
            }

            if (!RedeclarableBlocks.Contains(name))
            {
                Report(requirement, at, name, "宣言し直せるブロックは gl_PerVertex などの決まったものだけです。");
                continue;
            }

            if (instance is not null && !instance.Name.StartsWith("gl_", StringComparison.Ordinal))
            {
                Report(requirement, at, name, $"インスタンスの名前 '{instance.Name}' は組み込みのもの (gl_in など) にしてください。");
                continue;
            }

            VariableDeclaratorSyntax[] declared = [.. members.SelectMany(m => m.Variables)];

            if (declared.Length == 0)
            {
                continue;
            }

            if (instance is not null && types.Builtins.GetVariables(instance.Name).IsEmpty)
            {
                Report(requirement, at, name, $"この版とステージには、宣言し直す組み込みのブロック '{instance.Name}' がありません。");
                continue;
            }

            if (instance is null)
            {
                if (types.Builtins.GetVariables(declared[0].Name).IsEmpty)
                {
                    Report(requirement, at, name, $"この版とステージには、メンバー '{declared[0].Name}' を持つ組み込みのブロックがありません。");
                    continue;
                }

                if (name == "gl_PerVertex" && !PerVertexMembers.Contains(declared[0].Name))
                {
                    Report(requirement, at, name, $"'{declared[0].Name}' はブロックのメンバーではありません。");
                    continue;
                }
            }

            if (!redeclared.Add(instance?.Name ?? $"{storage} {name}"))
            {
                Report(requirement, at, name, "組み込みのブロックは 1 度しか宣言し直せません。");
                continue;
            }

            // ジオメトリシェーダーの gl_in は、使った後には宣言し直せない。glslang は使うと配列の大きさを決めるために写しを作り、2 度目と同じに扱う。
            // テッセレーションのシェーダーの gl_in は初めから大きさが決まっているので、使っても写しを作らない。
            if (instance is { Name: "gl_in" } && requirement.Stage == GlslShaderStage.Geometry
                && program.Tree.Root.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>().Any(u => u.Name == instance.Name && IsBefore(u.Identifier, at)))
            {
                Report(requirement, at, name, $"'{instance.Name}' を使った後には宣言し直せません。");
                continue;
            }

            if (name != "gl_PerVertex")
            {
                continue;
            }

            // メンバー: 組み込みのものだけで、型と配列かどうかを変えない。
            foreach (VariableDeclaratorSyntax member in declared)
            {
                string? builtinType = instance is not null
                    ? types.Builtins.GetMember(instance.Name, member.Name)?.Type
                    : PerVertexMembers.Contains(member.Name)
                        ? types.Builtins.GetVariables(member.Name).FirstOrDefault(v => v.Storage == storage)?.Type
                          ?? types.Builtins.GetMember(storage == "in" ? "gl_in" : "gl_out", member.Name)?.Type
                        : null;

                if (builtinType is null)
                {
                    Report(requirement, at, name, $"組み込みのブロックに無いメンバー '{member.Name}' があります。");
                    continue;
                }

                if (member.Parent is VariableDeclarationSyntax memberDeclaration && ElementType(builtinType) != memberDeclaration.Type.Name)
                {
                    Report(requirement, member.NameToken, member.Name, $"メンバーの型は {ElementType(builtinType)} のままにしてください。");
                }

                if (builtinType.Contains('[', StringComparison.Ordinal) != member.IsArray)
                {
                    Report(requirement, member.NameToken, member.Name, "メンバーを配列にするかどうかは変えられません。");
                }
            }

            // 名前の無い出力のブロックで含めなかったメンバーは、後ろで使えない。
            if (instance is null && storage == "out")
            {
                HashSet<string> kept = [.. declared.Select(d => d.Name)];

                foreach (IdentifierExpressionSyntax use in program.Tree.Root.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
                {
                    if (PerVertexMembers.Contains(use.Name)
                        && !kept.Contains(use.Name)
                        && IsBefore(at, use.Identifier)
                        && compilation.IsReportable(use.Identifier)
                        && types.Builtins.GetVariables(use.Name).Any(v => v.Storage == "out"))
                    {
                        Report(requirement, use.Identifier, use.Name, "宣言し直した gl_PerVertex に含めなかったメンバーは使えません。");
                    }
                }
            }
        }
    }

    /// <summary>GL0406 を報告する。</summary>
    private static void Report(GlslRequirements requirement, ShaderSyntaxToken at, string name, string reason)
        => requirement.Report(GlslRuleDescriptors.BuiltinVariableRedeclaration, at, name, reason);

    /// <summary>ブロックの修飾子の記憶域 (in / out など)。</summary>
    private static string Storage(ImmutableArray<ShaderSyntaxToken> modifiers)
        => modifiers.LastOrDefault(t => t.Text is "in" or "out" or "uniform" or "buffer")?.Text ?? string.Empty;

    /// <summary>宣言し直し 1 つを検査する。</summary>
    private static IEnumerable<string> Check(
        GlslRequirements requirement,
        GlslShaderStage stage,
        State state,
        string name,
        VariableDeclarationSyntax declaration,
        VariableDeclaratorSyntax declarator,
        Qualifiers qualifiers,
        ImmutableArray<GlslBuiltinVariable> builtins,
        bool usedBefore)
    {
        // 型 (要素の型) は変えられない (declareVariable)。
        string declaredType = declaration.Type.Name;

        if (!builtins.Any(b => ElementType(b.Type) == declaredType))
        {
            yield return $"型は {ElementType(builtins[0].Type)} のままにしてください。";
        }

        bool firstRedeclaration = state.Redeclared.Add(name);
        bool separateShaderObjects = !requirement.IsEs && requirement.Version <= 140 && requirement.IsOn("GL_ARB_separate_shader_objects", declarator.NameToken)
            && name is "gl_Position" or "gl_PointSize" or "gl_ClipVertex" or "gl_FogFragCoord";

        if (separateShaderObjects)
        {
            bool expectedStorage = stage == GlslShaderStage.Vertex ? qualifiers.IsOutput : stage != GlslShaderStage.Fragment || qualifiers.IsInput;

            if (usedBefore)
            {
                yield return "使った後には宣言し直せません。";
            }

            if (qualifiers.HasLayout)
            {
                yield return "layout を書けません。";
            }

            if (qualifiers.HasMemoryOrAuxiliary || !expectedStorage)
            {
                yield return "記憶域・メモリ・補助の修飾子を変えられません。";
            }

            if (qualifiers.Flat || qualifiers.NoPerspective || !expectedStorage)
            {
                yield return "補間の修飾子を変えられません。";
            }
        }
        else if (Colors.Contains(name))
        {
            if (declarator.IsArray)
            {
                yield return "配列にできません。";
            }

            if (qualifiers.HasLayout)
            {
                yield return "layout を書けません。";
            }

            if (qualifiers.HasMemoryOrAuxiliary || !builtins.Any(b => qualifiers.Storage == b.Storage))
            {
                yield return "記憶域・メモリ・補助の修飾子を変えられません。補間の修飾子だけを変えられます。";
            }
        }
        else if (name is "gl_TexCoord" or "gl_ClipDistance" or "gl_CullDistance")
        {
            if (qualifiers.HasLayout || qualifiers.HasMemoryOrAuxiliary || qualifiers.Flat || qualifiers.NoPerspective
                || !builtins.Any(b => qualifiers.Storage == b.Storage))
            {
                yield return "修飾子を変えられません。";
            }

            // 大きさを書いた後は、違う大きさにできない (declareVariable の「redeclaration of array with size」)。
            if (declarator.TryGetArrayLength(out int length))
            {
                // 大きさの上限 (glslang の arrayLimitCheck。上限は glslang の既定の資源の値)。
                (string limitName, int limit) = name == "gl_TexCoord" ? ("gl_MaxTextureCoords", 32) : name == "gl_ClipDistance" ? ("gl_MaxClipDistances", 8) : ("gl_MaxCullDistances", 8);

                if (length > limit)
                {
                    yield return $"大きさは {limitName} ({limit}) 以下でなければなりません。";
                }

                if (state.Sizes.TryGetValue(name, out int previous) && previous != length)
                {
                    yield return $"前の宣言し直しで大きさを {previous} にしています。";
                }

                state.Sizes.TryAdd(name, length);
            }
        }
        else if (name == "gl_FragCoord")
        {
            if (firstRedeclaration && usedBefore)
            {
                yield return "使った後には宣言し直せません。";
            }

            if (qualifiers.Flat || qualifiers.NoPerspective || qualifiers.HasMemoryOrAuxiliary)
            {
                yield return "layout だけを変えられます。";
            }

            if (!qualifiers.IsInput)
            {
                yield return "記憶域は in のままにしてください。";
            }

            bool upperLeft = qualifiers.Layout.Contains("origin_upper_left");
            bool pixelCenter = qualifiers.Layout.Contains("pixel_center_integer");

            if (!firstRedeclaration && (upperLeft != state.OriginUpperLeft || pixelCenter != state.PixelCenterInteger))
            {
                yield return "前の宣言し直しと同じ layout (origin_upper_left・pixel_center_integer) にしてください。";
            }

            state.OriginUpperLeft |= upperLeft;
            state.PixelCenterInteger |= pixelCenter;
        }
        else if (name == "gl_FragDepth")
        {
            if (qualifiers.Flat || qualifiers.NoPerspective || qualifiers.HasMemoryOrAuxiliary)
            {
                yield return "layout だけを変えられます。";
            }

            if (!qualifiers.IsOutput)
            {
                yield return "記憶域は out のままにしてください。";
            }

            if (qualifiers.Layout.FirstOrDefault(l => l.StartsWith("depth_", StringComparison.Ordinal)) is { } depth)
            {
                if (usedBefore)
                {
                    yield return "depth の layout を書くなら、使う前に宣言し直してください。";
                }

                if (state.Depth is { } previous && previous != depth)
                {
                    yield return $"前の宣言し直しの depth ({previous}) と同じにしてください。";
                }

                state.Depth ??= depth;
            }
        }
    }

    /// <summary>表の型の、配列を除いた型 (<c>float[]</c> → <c>float</c>)。</summary>
    private static string ElementType(string type) => type.IndexOf('[', StringComparison.Ordinal) is var bracket and >= 0 ? type[..bracket] : type;

    /// <summary>同じファイルで、<paramref name="left"/> が <paramref name="right"/> より前にあるか。</summary>
    private static bool IsBefore(ShaderSyntaxToken left, ShaderSyntaxToken right)
        => ReferenceEquals(left.Source, right.Source) && left.Span.Start < right.Span.Start;

    /// <summary>それまでの宣言し直しで決まったこと。</summary>
    private sealed class State
    {
        public HashSet<string> Redeclared { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Sizes { get; } = new(StringComparer.Ordinal);

        public bool OriginUpperLeft { get; set; }

        public bool PixelCenterInteger { get; set; }

        public string? Depth { get; set; }
    }

    /// <summary>宣言の修飾子。</summary>
    private sealed record Qualifiers(string Storage, bool Flat, bool NoPerspective, bool HasMemoryOrAuxiliary, bool HasLayout, ImmutableHashSet<string> Layout)
    {
        /// <summary>ステージの入力か (<c>in</c>、フラグメントシェーダーの <c>varying</c>)。</summary>
        public bool IsInput => Storage == "in";

        /// <summary>ステージの出力か (<c>out</c>、フラグメントシェーダーでない <c>varying</c>)。</summary>
        public bool IsOutput => Storage == "out";

        /// <summary>修飾子のトークンから読む。記憶域は表と同じ書き方 (<c>in</c> / <c>out</c> など) にそろえる。</summary>
        public static Qualifiers Read(ImmutableArray<ShaderSyntaxToken> tokens, GlslShaderStage stage)
        {
            string storage = "global";
            bool flat = false;
            bool noPerspective = false;
            bool memoryOrAuxiliary = false;
            bool layout = false;
            ImmutableHashSet<string>.Builder layoutNames = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            int depth = 0;

            foreach (ShaderSyntaxToken token in tokens)
            {
                if (token.Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    depth++;
                    continue;
                }

                if (token.Kind == ShaderSyntaxKind.CloseParenToken)
                {
                    depth--;
                    continue;
                }

                if (depth > 0)
                {
                    if (token.Kind == ShaderSyntaxKind.IdentifierToken)
                    {
                        layoutNames.Add(token.Text);
                    }

                    continue;
                }

                switch (token.Text)
                {
                    case "layout":
                        layout = true;
                        break;
                    case "in" or "attribute":
                        storage = "in";
                        break;
                    case "out":
                        storage = "out";
                        break;
                    case "varying":
                        storage = stage == GlslShaderStage.Fragment ? "in" : "out";
                        break;
                    case "uniform" or "buffer" or "shared" or "const":
                        storage = token.Text;
                        break;
                    case "flat":
                        flat = true;
                        break;
                    case "noperspective":
                        noPerspective = true;
                        break;
                    case "coherent" or "volatile" or "restrict" or "readonly" or "writeonly" or "centroid" or "sample" or "patch":
                        memoryOrAuxiliary = true;
                        break;
                    default:
                        break;
                }
            }

            return new Qualifiers(storage, flat, noPerspective, memoryOrAuxiliary, layout, layoutNames.ToImmutable());
        }
    }
}
