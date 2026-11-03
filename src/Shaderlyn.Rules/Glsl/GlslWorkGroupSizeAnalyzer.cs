using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 計算シェーダーで、ワークグループの大きさを決める前に <c>gl_WorkGroupSize</c> を読んでいないかを検査する (GL0104)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>rValueErrorCheck</c> の
/// 「can't read from gl_WorkGroupSize before a fixed workgroup size has been declared」を移したもの。
/// </summary>
/// <remarks>
/// 大きさは <c>layout(local_size_x = …) in;</c> (<c>local_size_x_id</c> などの特殊化の定数も) で決まる。
/// その宣言より前 (宣言が無ければどこでも) の <c>gl_WorkGroupSize</c> を報告する。
/// </remarks>
internal sealed class GlslWorkGroupSizeAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.LayoutRequired, GlslRuleDescriptors.InvalidLayoutValue];

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
            if (GlslRequirements.Create(context, compilation, program, reported) is not { } requirement)
            {
                continue;
            }

            if (requirement.Stage is GlslShaderStage.TessellationControl or GlslShaderStage.TessellationEvaluation or GlslShaderStage.Geometry)
            {
                CheckStageLayouts(requirement, program);
                CheckArrayedInterface(requirement, program);
                continue;
            }

            if (requirement.Stage != GlslShaderStage.Compute)
            {
                continue;
            }

            IncompleteDeclarationSyntax[] declarations =
            [
                .. program.Tree.Root.DescendantNodesAndSelf()
                    .OfType<IncompleteDeclarationSyntax>()
                    .Where(d => d.Tokens.Any(t => t.Text.StartsWith("local_size_", StringComparison.Ordinal)) && d.Tokens.Any(t => t.Text == "in")),
            ];

            // 取り込んだファイルの宣言は、書いた位置を比べられない。
            if (declarations.Any(d => !ReferenceEquals(d.Tokens[0].Source, program.Tree.Text)))
            {
                continue;
            }

            int? declared = declarations.Length == 0 ? null : declarations.Min(d => d.Tokens[0].Span.Start);

            foreach (IdentifierExpressionSyntax use in program.Tree.Root.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
            {
                if (use.Name == "gl_WorkGroupSize"
                    && compilation.IsReportable(use.Identifier)
                    && ReferenceEquals(use.Identifier.Source, program.Tree.Text)
                    && (declared is not { } at || use.Identifier.Span.Start < at))
                {
                    requirement.Report(GlslRuleDescriptors.LayoutRequired, use.Identifier,
                        "gl_WorkGroupSize を読む前に、ワークグループの大きさ (layout(local_size_x = …) in;) を宣言してください。");
                }
            }
        }
    }

    /// <summary>
    /// ジオメトリシェーダーの入力の配列は入力のプリミティブの頂点の数と、テッセレーションの制御シェーダーの出力の配列 (patch でないもの) は
    /// layout(vertices = …) の数と、大きさが同じでなければならない (glslang の ParseHelper.cpp の checkIoArraysConsistency)。
    /// 配列が layout より前にあれば layout の位置で、後にあれば配列の名前の位置で報告する。大きさが整数のリテラルの配列だけを見る。
    /// </summary>
    private static void CheckArrayedInterface(GlslRequirements requirement, AnalyzedProgram program)
    {
        (string storage, Func<ImmutableArray<ShaderSyntaxToken>, (ShaderSyntaxToken At, long Size)?> sizeOf, string what) = requirement.Stage switch
        {
            GlslShaderStage.Geometry => ("in", (Func<ImmutableArray<ShaderSyntaxToken>, (ShaderSyntaxToken, long)?>)PrimitiveSize, "入力のプリミティブの頂点の数"),
            GlslShaderStage.TessellationControl => ("out", OutputVertices, "出力のパッチの頂点の数 (vertices)"),
            _ => ("", _ => null, ""),
        };

        if (storage.Length == 0)
        {
            return;
        }

        (ShaderSyntaxToken At, long Size)? layout = program.Tree.Root.ChildNodes().OfType<IncompleteDeclarationSyntax>()
            .Where(d => d.Tokens.Any(t => t.Text == storage))
            .Select(d => sizeOf(d.Tokens))
            .FirstOrDefault(found => found is not null);

        if (layout is not { } declared || !ReferenceEquals(declared.At.Source, program.Tree.Text))
        {
            return;
        }

        foreach (ShaderSyntaxNode node in program.Tree.Root.ChildNodes())
        {
            (ImmutableArray<ShaderSyntaxToken> modifiers, IEnumerable<VariableDeclaratorSyntax> declarators) = node switch
            {
                VariableDeclarationSyntax variable => (variable.ModifierTokens, variable.Variables),
                StructDeclarationSyntax { Keyword.Text: not "struct" } block => ([.. block.Modifiers, block.Keyword], block.ChildNodes().OfType<VariableDeclaratorSyntax>()),
                InterfaceBlockDeclarationSyntax block => (block.Modifiers, block.ChildNodes().OfType<VariableDeclaratorSyntax>()),
                _ => ([], []),
            };

            if (!modifiers.Any(t => t.Text == storage) || modifiers.Any(t => t.Text == "patch"))
            {
                continue;
            }

            foreach (VariableDeclaratorSyntax declarator in declarators)
            {
                if (declarator.ArrayRankTokens is not [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.NumericLiteralToken } size, { Kind: ShaderSyntaxKind.CloseBracketToken }, ..]
                    || !long.TryParse(size.Text.TrimEnd('u', 'U'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long length)
                    || length == declared.Size
                    || !ReferenceEquals(declarator.NameToken.Source, program.Tree.Text))
                {
                    continue;
                }

                // ブロックは、glslang と同じくブロックの頭で報告する。
                ShaderSyntaxToken own = node is VariableDeclarationSyntax ? declarator.NameToken : modifiers[0];
                ShaderSyntaxToken at = declarator.NameToken.Span.Start < declared.At.Span.Start ? declared.At : own;
                requirement.Report(GlslRuleDescriptors.InvalidLayoutValue, at,
                    $"'{declarator.Name}' の配列の大きさ ({length}) が、{what} ({declared.Size}) と違います。");
            }
        }
    }

    /// <summary>入力のプリミティブ (layout(triangles) in; など) の頂点の数。</summary>
    private static (ShaderSyntaxToken At, long Size)? PrimitiveSize(ImmutableArray<ShaderSyntaxToken> tokens)
        => tokens.FirstOrDefault(t => t.Text is "points" or "lines" or "lines_adjacency" or "triangles" or "triangles_adjacency") is { } primitive
            ? (primitive, primitive.Text switch
            {
                "points" => 1,
                "lines" => 2,
                "lines_adjacency" => 4,
                "triangles" => 3,
                _ => 6,
            })
            : null;

    /// <summary>layout(vertices = N) out; の N。</summary>
    private static (ShaderSyntaxToken At, long Size)? OutputVertices(ImmutableArray<ShaderSyntaxToken> tokens)
        => GlslLayoutQualifier.Read(tokens).Where(e => e.Name.Text == "vertices").Select(e => (GlslLayoutEntry?)e).FirstOrDefault() is { } entry
           && GlslLayoutQualifier.Evaluate(entry.Value, _ => null) is { } value
            ? (entry.Name, (long)value.Value)
            : null;

    /// <summary>
    /// テッセレーションとジオメトリのシェーダーに要る、型の無い宣言の layout (glslang の linkValidate.cpp の finalCheck)。
    /// 制御シェーダーは layout(vertices = …) out;、評価シェーダーは入力のプリミティブ、ジオメトリシェーダーは入力と出力のプリミティブと max_vertices が要る。
    /// main を定義したファイルだけを見て、main の名前の位置で報告する。
    /// </summary>
    private static void CheckStageLayouts(GlslRequirements requirement, AnalyzedProgram program)
    {
        if (program.Tree.Root.ChildNodes().OfType<FunctionDeclarationSyntax>().FirstOrDefault(f => f.Name == "main" && f.Body is not null) is not { } main
            || !ReferenceEquals(main.NameToken.Source, program.Tree.Text))
        {
            return;
        }

        string[][] declarations =
        [
            .. program.Tree.Root.DescendantNodesAndSelf().OfType<IncompleteDeclarationSyntax>().Select(d => d.Tokens.Select(t => t.Text).ToArray()),
        ];

        bool Declares(string storage, params string[] names)
            => declarations.Any(tokens => tokens.Contains(storage) && tokens.Any(names.Contains));

        void Require(string message) => requirement.Report(GlslRuleDescriptors.LayoutRequired, main.NameToken, message);

        switch (requirement.Stage)
        {
            case GlslShaderStage.TessellationControl when !Declares("out", "vertices"):
                Require("テッセレーションの制御シェーダーには、出力のパッチの頂点の数 (layout(vertices = …) out;) が要ります。");
                break;

            case GlslShaderStage.TessellationEvaluation when !Declares("in", "triangles", "quads", "isolines"):
                Require("テッセレーションの評価シェーダーには、入力のプリミティブ (layout(triangles) in; など) が要ります。");
                break;

            case GlslShaderStage.Geometry:
                if (!Declares("in", "points", "lines", "lines_adjacency", "triangles", "triangles_adjacency"))
                {
                    Require("ジオメトリシェーダーには、入力のプリミティブ (layout(triangles) in; など) が要ります。");
                }

                // passthrough の入力 (GL_NV_geometry_shader_passthrough) があれば、出力のプリミティブと max_vertices は決まる。
                if (declarations.Any(tokens => tokens.Contains("passthrough"))
                    || program.Tree.Root.DescendantNodesAndSelf().OfType<InterfaceBlockDeclarationSyntax>().Any(b => b.Modifiers.Any(t => t.Text == "passthrough"))
                    || program.Tree.Root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>().Any(b => b.Modifiers.Any(t => t.Text == "passthrough"))
                    || program.Tree.Root.DescendantNodesAndSelf().OfType<VariableDeclarationSyntax>().Any(v => v.ModifierTokens.Any(t => t.Text == "passthrough")))
                {
                    break;
                }

                if (!Declares("out", "points", "line_strip", "triangle_strip"))
                {
                    Require("ジオメトリシェーダーには、出力のプリミティブ (layout(triangle_strip) out; など) が要ります。");
                }

                if (!Declares("out", "max_vertices"))
                {
                    Require("ジオメトリシェーダーには、layout(max_vertices = …) out; が要ります。");
                }

                break;

            default:
                break;
        }
    }
}
