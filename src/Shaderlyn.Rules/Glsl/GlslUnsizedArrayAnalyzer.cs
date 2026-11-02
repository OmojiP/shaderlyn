using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 大きさの決まっていない配列の使い方を検査する (GL0308 / GL0001)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>checkRuntimeSizable</c> と <c>.length()</c> の検査 (handleLengthMethod) を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 大きさを書かずに宣言した配列 (<c>float a[];</c>) は、定数の添字で引くと大きさが暗に決まるが、定数でない添字では引けず、<c>length()</c> も使えない。
/// buffer のブロックの最後のメンバーは、実行時に大きさが決まる配列なので、どちらも使える。
/// サンプラーなどの配列と、uniform と buffer のブロックの配列は、<c>GL_EXT_nonuniform_qualifier</c> を有効にすれば定数でない添字で引ける (GL0001)。
/// uniform のブロックの最後のメンバーは、<c>GL_EXT_uniform_buffer_unsized_array</c> を有効にすれば定数でない添字で引ける。
/// </para>
/// <para>
/// 関数の外の変数、名前の無いブロックのメンバー、名前のあるブロック (とその配列) のメンバーとインスタンスを見る。
/// 入力と出力の配列 (ジオメトリシェーダーの <c>in</c> の配列など) は、glslang が別に大きさを決めるので見ない。
/// 同じ名前を大きさを付けて宣言し直していれば見ない。
/// </para>
/// <para>
/// ジオメトリシェーダーの入力の配列 (<c>gl_in</c> と、大きさを書かない <c>in</c> の配列) は <c>layout(triangles) in;</c> などで、
/// テッセレーションの制御シェーダーの出力の配列 (<c>gl_out</c> と、大きさを書かない <c>out</c> の配列) は <c>layout(vertices = N) out;</c> で大きさが決まる。
/// それより前 (書いていなければどこでも) では、<c>length()</c> を使えず、定数でない添字で引けない
/// (glslang の「array must first be sized by a redeclaration or layout qualifier」)。
/// </para>
/// </remarks>
internal sealed partial class GlslUnsizedArrayAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.UnsizedArrayUse, GlslRuleDescriptors.NotAvailable];

    /// <summary>大きさの決まっていない配列。</summary>
    /// <param name="Name">名前。</param>
    /// <param name="RuntimeLength">buffer のブロックの最後のメンバーか (実行時に大きさが決まる)。</param>
    /// <param name="UniformLast">uniform のブロックの最後のメンバーか。</param>
    /// <param name="NonuniformIndexable">GL_EXT_nonuniform_qualifier で定数でない添字で引けるか (サンプラーなどの配列とブロックの配列)。</param>
    private readonly record struct UnsizedArray(string Name, bool RuntimeLength, bool UniformLast, bool NonuniformIndexable);

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];
        Dictionary<AnalyzedProgram, IoArrays?> ioArrays = [];

        // ステージの layout で大きさが決まる入出力の配列。配列の大きさに書いた length() は式の木に無いので、木ごとに先に見る。
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            GlslRequirements? requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);

            if (requirement is not null && IoArrays.Read(program, requirement.Stage) is { } io)
            {
                ioArrays[program] = io;
                CheckIoArrayInSizes(requirement, io, program);
            }
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (ElementAccessExpressionSyntax or InvocationExpressionSyntax))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            if (ioArrays.GetValueOrDefault(program) is { } io && CheckIoArray(requirement, new GlslConstantEvaluator(binder), io, node))
            {
                continue;
            }

            switch (node)
            {
                case ElementAccessExpressionSyntax element
                    when Describe(binder, program, element.Target) is { RuntimeLength: false } array
                         && new GlslConstantEvaluator(binder).IsCertainlyNotConstant(element.Index):
                    if (array.UniformLast && requirement.IsOn("GL_EXT_uniform_buffer_unsized_array", element.OpenBracket))
                    {
                        break;
                    }

                    if (array.NonuniformIndexable)
                    {
                        requirement.RequireExtensions(element.OpenBracket, ["GL_EXT_nonuniform_qualifier"], "大きさの決まっていない配列を定数でない添字で引くこと");
                    }
                    else
                    {
                        requirement.Report(GlslRuleDescriptors.UnsizedArrayUse, element.OpenBracket, array.Name, "定数でない添字で引けません");
                    }

                    break;

                case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax { NameToken.Text: "length" } method } invocation
                    when !invocation.ArgumentExpressions.Any()
                         && Describe(binder, program, method.Target) is { RuntimeLength: false } array:
                    requirement.Report(GlslRuleDescriptors.UnsizedArrayUse, method.NameToken, array.Name, "length() で長さを求められません");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>式が大きさの決まっていない配列なら、それを表す。分からなければ <see langword="null"/>。</summary>
    private static UnsizedArray? Describe(ExpressionTypeBinder binder, AnalyzedProgram program, ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        switch (expression)
        {
            case IdentifierExpressionSyntax identifier when !identifier.Name.StartsWith("gl_", StringComparison.Ordinal):
            {
                NameResolution resolution = binder.ResolveName(identifier);

                if (resolution.Kind == DeclaredNameKind.NotFound)
                {
                    // 名前のあるブロックの配列 (uniform U { … } u[];)。
                    return FindBlockInstance(program, identifier.Name) is { } instance
                           && IsUnsized(instance.Declarator, null)
                           && IsUniformOrBuffer(instance.Block.Keyword.Text)
                        ? new UnsizedArray(identifier.Name, RuntimeLength: false, UniformLast: false, NonuniformIndexable: true)
                        : null;
                }

                // 大きさを付けて宣言し直せる。使う位置より前の、最後の宣言を見る。
                if (resolution.Kind is not (DeclaredNameKind.Local or DeclaredNameKind.Global)
                    || resolution.Candidates.Any(c => c.Declarator is not VariableDeclaratorSyntax)
                    || resolution.Candidates
                        .Select(c => (VariableDeclaratorSyntax)c.Declarator)
                        .Where(d => d.Span.Start < identifier.Span.Start)
                        .OrderBy(d => d.Span.Start)
                        .LastOrDefault() is not { Parent: VariableDeclarationSyntax declaration } declarator
                    || !IsUnsized(declarator, declaration))
                {
                    return null;
                }

                switch (declaration.Parent)
                {
                    case CompilationUnitSyntax or LocalDeclarationStatementSyntax
                        when !HasStorage(declaration.ModifierTokens, "in", "out", "varying", "attribute", "buffer", "shared"):
                        return new UnsizedArray(identifier.Name, RuntimeLength: false, UniformLast: false, NonuniformIndexable: Opaque().IsMatch(declaration.Type.Name));

                    // 名前の無いブロックのメンバー。
                    case InterfaceBlockDeclarationSyntax block when IsUniformOrBuffer(block.Modifiers):
                        return Member(identifier.Name, IsLast(block.Members, declarator), HasStorage(block.Modifiers, "buffer"));

                    default:
                        return null;
                }
            }

            // 名前のあるブロックのメンバー (b.a、b[1].a)。
            case MemberAccessExpressionSyntax member:
            {
                ExpressionSyntax target = member.Target is ElementAccessExpressionSyntax element ? element.Target : member.Target;

                if (target is not IdentifierExpressionSyntax blockName
                    || binder.ResolveName(blockName).Kind != DeclaredNameKind.NotFound
                    || FindBlockInstance(program, blockName.Name) is not { } instance
                    || !IsUniformOrBuffer(instance.Block.Keyword.Text))
                {
                    return null;
                }

                VariableDeclaratorSyntax? field = instance.Block.Fields
                    .SelectMany(f => f.Variables.Select(v => (Declaration: f, Declarator: v)))
                    .Where(f => f.Declarator.Name == member.NameToken.Text && IsUnsized(f.Declarator, f.Declaration))
                    .Select(f => f.Declarator)
                    .FirstOrDefault();

                return field is null
                    ? null
                    : Member($"{blockName.Name}.{field.Name}", IsLast(instance.Block.Members, field), instance.Block.Keyword.Text == "buffer");
            }

            default:
                return null;
        }
    }

    /// <summary>ブロックのメンバーの配列。</summary>
    private static UnsizedArray Member(string name, bool last, bool buffer)
        => new(name, RuntimeLength: last && buffer, UniformLast: last && !buffer, NonuniformIndexable: false);

    /// <summary>名前のあるブロックのうち、そのインスタンスの名前を持つもの。</summary>
    private static (StructDeclarationSyntax Block, VariableDeclaratorSyntax Declarator)? FindBlockInstance(AnalyzedProgram program, string name)
    {
        foreach (StructDeclarationSyntax block in program.Tree.Root.Declarations.OfType<StructDeclarationSyntax>())
        {
            if (block.Keyword.Text != "struct"
                && block.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>().FirstOrDefault(d => d.Name == name) is { } declarator)
            {
                return (block, declarator);
            }
        }

        return null;
    }

    /// <summary>
    /// ステージの layout で大きさが決まる入出力の配列を、大きさが決まる前に使っているかを検査する。
    /// </summary>
    /// <returns>その配列の使い方だったら <see langword="true"/> (ほかの検査をしない)。</returns>
    private static bool CheckIoArray(GlslRequirements requirement, GlslConstantEvaluator evaluator, IoArrays io, ShaderSyntaxNode node)
    {
        switch (node)
        {
            case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax { NameToken.Text: "length", Target: IdentifierExpressionSyntax target } method } invocation
                when !invocation.ArgumentExpressions.Any() && io.Names.Contains(target.Name):
                if (!io.IsSizedAt(target.Identifier))
                {
                    requirement.Report(GlslRuleDescriptors.UnsizedArrayUse, method.NameToken, target.Name, $"length() で長さを求められません。{io.Sizing} で大きさを決めてから使ってください");
                }

                return true;

            case ElementAccessExpressionSyntax { Target: IdentifierExpressionSyntax target } element when io.Names.Contains(target.Name):
                if (!io.IsSizedAt(target.Identifier) && evaluator.IsCertainlyNotConstant(element.Index))
                {
                    requirement.Report(GlslRuleDescriptors.UnsizedArrayUse, element.OpenBracket, target.Name, $"定数でない添字で引けません。{io.Sizing} で大きさを決めてから引いてください");
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 配列の大きさに書いた <c>gl_out.length()</c> (<c>int a[gl_out.length()];</c>) を見る。配列の大きさは構文木の式にならず、トークンのまま残る。
    /// </summary>
    private static void CheckIoArrayInSizes(GlslRequirements requirement, IoArrays io, AnalyzedProgram program)
    {
        foreach (VariableDeclaratorSyntax declarator in program.Tree.Root.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
        {
            ImmutableArray<ShaderSyntaxToken> ranks = declarator.ArrayRankTokens;

            for (int i = 0; i + 3 < ranks.Length; i++)
            {
                if (ranks[i].Kind == ShaderSyntaxKind.IdentifierToken
                    && io.Names.Contains(ranks[i].Text)
                    && ranks[i + 1].Kind == ShaderSyntaxKind.DotToken
                    && ranks[i + 2].Text == "length"
                    && ranks[i + 3].Kind == ShaderSyntaxKind.OpenParenToken
                    && !io.IsSizedAt(ranks[i]))
                {
                    requirement.Report(GlslRuleDescriptors.UnsizedArrayUse, ranks[i + 2], ranks[i].Text, $"length() で長さを求められません。{io.Sizing} で大きさを決めてから使ってください");
                }
            }
        }
    }

    /// <summary>ステージの layout で大きさが決まる入出力の配列と、大きさを決める宣言の位置。</summary>
    /// <param name="Names">配列の名前 (<c>gl_in</c> / <c>gl_out</c> と、大きさを書かない入出力の配列)。</param>
    /// <param name="SizedFrom">大きさを決める宣言のトークン。無ければ <see langword="null"/>。</param>
    /// <param name="Sizing">大きさを決める宣言の例 (文に入れる)。</param>
    private sealed record IoArrays(HashSet<string> Names, ShaderSyntaxToken? SizedFrom, string Sizing)
    {
        /// <summary>その位置で、大きさが決まっているか。同じファイルで、大きさを決める宣言より後ろにあれば決まっている。</summary>
        public bool IsSizedAt(ShaderSyntaxToken use)
            => SizedFrom is { } sized && (!ReferenceEquals(sized.Source, use.Source) || sized.Span.Start < use.Span.Start);

        /// <summary>木から読む。ジオメトリシェーダーとテッセレーションの制御シェーダーでなければ <see langword="null"/>。</summary>
        public static IoArrays? Read(AnalyzedProgram program, Shaderlyn.Glsl.GlslShaderStage? stage)
        {
            (string builtIn, string storage, string sizing, Func<string, bool> sizes) = stage switch
            {
                Shaderlyn.Glsl.GlslShaderStage.Geometry => ("gl_in", "in", "layout(triangles) in; など",
                    (Func<string, bool>)(id => id is "points" or "lines" or "lines_adjacency" or "triangles" or "triangles_adjacency")),
                Shaderlyn.Glsl.GlslShaderStage.TessellationControl => ("gl_out", "out", "layout(vertices = N) out;", id => id == "vertices"),
                _ => (string.Empty, string.Empty, string.Empty, _ => false),
            };

            if (builtIn.Length == 0)
            {
                return null;
            }

            HashSet<string> names = new(StringComparer.Ordinal) { builtIn };
            ShaderSyntaxToken? sizedFrom = null;

            foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                switch (declaration)
                {
                    // layout(triangles) in; / layout(vertices = 3) out;
                    case IncompleteDeclarationSyntax { Tokens: [_, ..] tokens }
                        when sizedFrom is null && tokens.Any(t => t.Text == storage) && GlslLayoutQualifier.Read(tokens).Any(e => sizes(e.Name.Text.ToLowerInvariant())):
                        sizedFrom = tokens[0];
                        break;

                    // 大きさを書かない入出力の配列 (in vec4 color[];)。patch の出力は見ない。
                    case VariableDeclarationSyntax variable
                        when variable.ModifierTokens.Any(t => t.Text == storage) && !variable.ModifierTokens.Any(t => t.Text == "patch"):
                        foreach (VariableDeclaratorSyntax declarator in variable.Variables)
                        {
                            if (IsUnsized(declarator, variable))
                            {
                                names.Add(declarator.Name);
                            }
                        }

                        break;

                    // 大きさを書いて宣言し直した gl_in / gl_out。
                    case StructDeclarationSyntax block
                        when block.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>().FirstOrDefault(d => d.Name == builtIn) is { } instance
                             && !IsUnsized(instance, null):
                        names.Remove(builtIn);
                        break;

                    default:
                        break;
                }
            }

            return new IoArrays(names, sizedFrom, sizing);
        }
    }

    /// <summary>外側の次元の大きさを書いておらず、初期化子も無いか。</summary>
    private static bool IsUnsized(VariableDeclaratorSyntax declarator, VariableDeclarationSyntax? declaration)
    {
        ImmutableArray<ShaderSyntaxToken> ranks = [.. declarator.ArrayRankTokens, .. declaration?.Type.ArrayRanks ?? []];
        return declarator.Initializer is null
               && ranks.Length >= 2
               && ranks[0].Kind == ShaderSyntaxKind.OpenBracketToken
               && ranks[1].Kind == ShaderSyntaxKind.CloseBracketToken;
    }

    /// <summary>ブロックの最後のメンバーか。</summary>
    private static bool IsLast(ImmutableArray<DeclarationSyntax> members, VariableDeclaratorSyntax declarator)
        => members.OfType<VariableDeclarationSyntax>().LastOrDefault() is { } last && last.Variables.LastOrDefault() == declarator;

    /// <summary>uniform か buffer のブロックか。</summary>
    private static bool IsUniformOrBuffer(string keyword) => keyword is "uniform" or "buffer";

    /// <summary>修飾子に uniform か buffer があるか。</summary>
    private static bool IsUniformOrBuffer(ImmutableArray<ShaderSyntaxToken> modifiers) => HasStorage(modifiers, "uniform", "buffer");

    /// <summary>修飾子 (<c>layout(…)</c> の外) に、どれかの記憶域があるか。</summary>
    private static bool HasStorage(ImmutableArray<ShaderSyntaxToken> modifiers, params string[] storages)
    {
        int depth = 0;

        foreach (ShaderSyntaxToken modifier in modifiers)
        {
            depth += modifier.Kind switch
            {
                ShaderSyntaxKind.OpenParenToken => 1,
                ShaderSyntaxKind.CloseParenToken => -1,
                _ => 0,
            };

            if (depth == 0 && storages.Contains(modifier.Text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>GL_EXT_nonuniform_qualifier で定数でない添字で引ける型 (glslang の EbtSampler・EbtAccStruct・EbtRayQuery・EbtHitObjectNV)。</summary>
    [GeneratedRegex(@"^([iu]?(sampler|image|texture|subpassInput)[0-9A-Za-z_]*|accelerationStructure(EXT|NV)|rayQueryEXT|hitObjectNV)$")]
    private static partial Regex Opaque();
}
