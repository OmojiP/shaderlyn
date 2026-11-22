using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang のエントリポイントの入力と出力 (仮引数と戻り値) に、受け渡せない型を使用していないかを検査する (SG0407)。
/// </summary>
/// <remarks>
/// <para>
/// slangc 2026.19 が E38050 にするもののうち、宣言から決まるものを見る。
/// </para>
/// <list type="bullet">
/// <item><c>DifferentialPair</c>・<c>DifferentialPtrPair</c>・<c>Atomic</c> は、どのステージの入力と出力にも使用できない (構造体のフィールドに含めても同じ)。</item>
/// <item><c>CoopVec</c>・<c>CoopMat</c> は、頂点シェーダーとピクセルシェーダーの入力と出力に使用できない。</item>
/// <item><c>OutputVertices</c>・<c>OutputIndices</c>・<c>OutputPrimitives</c> はメッシュシェーダーに、ストリーム (<c>TriangleStream</c> など) はジオメトリシェーダーにだけ使用できる。</item>
/// <item><c>OutputIndices</c> の要素は <c>uint</c>・<c>uint2</c>・<c>uint3</c> にだけできる。</item>
/// <item>行列 (<c>matrix&lt;float, 4, 5&gt;</c>) の行と列は 1 から 4 にだけできる。</item>
/// </list>
/// <para>
/// slangc と同じく、構造体のフィールドの型が誤りでも、その構造体を受け渡す仮引数か関数に報告する。
/// <c>uniform</c> の仮引数は入力と出力ではないので見ない。ステージの決め方は CF0302 と同じ (<see cref="SystemValueSemanticAnalyzer.EntryFunctions"/>)。
/// </para>
/// </remarks>
internal sealed class SlangEntryPointVaryingAnalyzer : SemanticRuleAnalyzer
{
    private static readonly ImmutableHashSet<string> NeverVarying = ["DifferentialPair", "DifferentialPtrPair", "Atomic"];

    private static readonly ImmutableHashSet<string> Cooperative = ["CoopVec", "CoopMat"];

    private static readonly ImmutableHashSet<string> MeshOutputs = ["OutputVertices", "OutputIndices", "OutputPrimitives"];

    private static readonly ImmutableHashSet<string> Streams = ["PointStream", "LineStream", "TriangleStream"];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.InvalidDeclarationType];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<FunctionDeclarationSyntax> functions = [.. EntryPointAnalyzer.EnumerateFunctions(program.Tree.Root.Declarations).Where(f => f.IsDefinition)];

            foreach ((FunctionDeclarationSyntax function, HlslShaderStage stage) in SystemValueSemanticAnalyzer.EntryFunctions(program, functions))
            {
                foreach (ParameterSyntax parameter in function.ParameterList)
                {
                    if (parameter.ModifierTokens.Concat(parameter.Type.ModifierTokens).Any(t => t.Text == "uniform"))
                    {
                        continue;
                    }

                    if (Problem(program, parameter.Type, stage, 0) is { } problem)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.InvalidDeclarationType, parameter.NameToken.GetLocation(),
                            $"仮引数 '{parameter.Name}' の{problem}"));
                    }
                }

                if (Problem(program, function.ReturnType, stage, 0) is { } returned)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.InvalidDeclarationType, function.NameToken.GetLocation(),
                        $"エントリポイント '{function.Name}' の戻り値の{returned}"));
                }
            }
        }
    }

    /// <summary>入力と出力に受け渡せない型なら、その説明を返す。</summary>
    /// <param name="program">コードブロック。</param>
    /// <param name="type">型。</param>
    /// <param name="stage">ステージ。</param>
    /// <param name="depth">構造体の入れ子の深さ。</param>
    /// <returns>「型 X は…」で始まる説明。受け渡せれば <see langword="null"/>。</returns>
    private static string? Problem(AnalyzedProgram program, TypeSyntax type, HlslShaderStage stage, int depth)
    {
        string name = type.NameToken.Text;

        if (NeverVarying.Contains(name))
        {
            return $"型 {name} は、エントリポイントの入力と出力には使用できません。";
        }

        if (Cooperative.Contains(name) && stage is HlslShaderStage.Vertex or HlslShaderStage.Pixel)
        {
            return $"型 {name} は、頂点シェーダーとピクセルシェーダーの入力と出力には使用できません。";
        }

        if (MeshOutputs.Contains(name) && stage != HlslShaderStage.Mesh)
        {
            return $"型 {name} は、メッシュシェーダーの出力にだけ使用できます。";
        }

        if (Streams.Contains(name) && stage != HlslShaderStage.Geometry)
        {
            return $"型 {name} は、ジオメトリシェーダーの出力にだけ使用できます。";
        }

        if (name == "OutputIndices"
            && type.TemplateArguments.FirstOrDefault() is { Kind: ShaderSyntaxKind.IdentifierToken } element
            && type.TemplateArguments.Length > 1 && type.TemplateArguments[1].Text == ","
            && element.Text is not ("uint" or "uint2" or "uint3"))
        {
            return $"型 OutputIndices の要素は uint・uint2・uint3 にしてください ({element.Text} は使用できません)。";
        }

        if (name == "matrix"
            && type.TemplateArguments is [_, { Text: "," }, { Kind: ShaderSyntaxKind.NumericLiteralToken } rows, { Text: "," }, { Kind: ShaderSyntaxKind.NumericLiteralToken } columns]
            && (OutOfRange(rows.Text) || OutOfRange(columns.Text)))
        {
            return $"行列の行と列は 1 から 4 にしてください (matrix<…, {rows.Text}, {columns.Text}> は入力と出力に使用できません)。";
        }

        if (depth > 8 || program.Structs.FirstOrDefault(s => s.Keyword.Text == "struct" && s.Name == name) is not { } declaration)
        {
            return null;
        }

        foreach (VariableDeclarationSyntax field in declaration.Fields)
        {
            if (field.HasModifier("static") || Problem(program, field.Type, stage, depth + 1) is not { } inner)
            {
                continue;
            }

            string fieldName = field.Variables.FirstOrDefault()?.Name ?? "?";
            return $"構造体 {name} のフィールド '{fieldName}' の{inner}";
        }

        return null;
    }

    private static bool OutOfRange(string text) => !int.TryParse(text, out int value) || value is < 1 or > 4;
}
