using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// メッシュシェーダーのエントリポイントに、必要な属性と出力があるかを検査する (CF0303)。
/// </summary>
/// <remarks>
/// <para>
/// 次の 3 つを見る。DXC 1.8 と slangc 2026.19 で確かめた。
/// </para>
/// <list type="bullet">
/// <item><c>[outputtopology]</c> が無い。DXC と slangc がエラーにする。</item>
/// <item><c>out vertices</c> / <c>out indices</c> / <c>out primitives</c> が配列でないか、配列の大きさを書いていない (<c>verts[]</c>)。DXC と slangc がエラーにする。</item>
/// <item>頂点の出力かインデックスの出力が無い (大きさを書いた配列でないものは、無いものとする)。slangc だけがエラーにする。</item>
/// </list>
/// <para>
/// エントリポイントのステージは CF0302 と同じく決める (<see cref="SystemValueSemanticAnalyzer.EntryFunctions"/>)。
/// 配列の大きさが名前 (<c>verts[MAX_VERTS]</c>) でも、大きさを書いたものとする。
/// </para>
/// </remarks>
internal sealed class MeshShaderSignatureAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.MeshShaderSignature];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        HashSet<(string, int, string)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<FunctionDeclarationSyntax> functions = [.. EntryPointAnalyzer.EnumerateFunctions(program.Tree.Root.Declarations).Where(f => f.IsDefinition)];

            foreach ((FunctionDeclarationSyntax function, HlslShaderStage stage) in SystemValueSemanticAnalyzer.EntryFunctions(program, functions))
            {
                if (stage != HlslShaderStage.Mesh)
                {
                    continue;
                }

                ImmutableArray<SystemValueCompiler> compilers = SystemValueSemanticAnalyzer.CompilersOf(compilation, function, program);

                if (compilers.IsEmpty)
                {
                    continue;
                }

                Check(function, compilers, (token, message, rejecting) =>
                {
                    Location location = token.GetLocation();

                    if (reported.Add((location.FilePath, location.Span.Start, message)))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.MeshShaderSignature, location, message, rejecting));
                    }
                });
            }
        }
    }

    private static void Check(FunctionDeclarationSyntax function, ImmutableArray<SystemValueCompiler> compilers, Action<ShaderSyntaxToken, string, string> report)
    {
        // DXC と slangc が誤りにするもの。FXC はメッシュシェーダーをコンパイルできない。
        string both = string.Join("・", compilers.Where(c => c != SystemValueCompiler.Fxc).Select(c => c == SystemValueCompiler.Dxc ? "DXC" : "slangc"));
        bool slang = compilers.Contains(SystemValueCompiler.Slang);

        if (both.Length == 0)
        {
            return;
        }

        if (!function.Attributes.Any(a => a.Name == "outputtopology"))
        {
            report(function.NameToken, $"メッシュシェーダー '{function.Name}' に [outputtopology(\"triangle\")] などの属性がありません", both);
        }

        bool vertices = false;
        bool indices = false;

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            string? kind = parameter.ModifierTokens.Concat(parameter.Type.ModifierTokens).Select(t => t.Text).FirstOrDefault(t => t is "vertices" or "indices" or "primitives");

            if (kind is null)
            {
                // Slang の書き方 (OutputVertices<V, 3>) は、型が大きさを持つ。
                vertices |= parameter.Type.Name == "OutputVertices";
                indices |= parameter.Type.Name == "OutputIndices";
                continue;
            }

            if (!parameter.IsArray)
            {
                report(parameter.NameToken, $"'{parameter.Name}' (out {kind}) は配列にしてください ({parameter.Name}[3] など)", both);
                continue;
            }

            if (HasEmptyRank(parameter))
            {
                report(parameter.NameToken, $"'{parameter.Name}' (out {kind}) の配列の大きさを書いてください", both);
                continue;
            }

            vertices |= kind == "vertices";
            indices |= kind == "indices";
        }

        if (slang && !(vertices && indices))
        {
            report(function.NameToken, $"メッシュシェーダー '{function.Name}' には、頂点の出力 (out vertices / OutputVertices) とインデックスの出力 (out indices / OutputIndices) の両方が必要です", "slangc");
        }
    }

    /// <summary>大きさを書いていない次元 (<c>[]</c>) があるか。</summary>
    private static bool HasEmptyRank(ParameterSyntax parameter)
    {
        ImmutableArray<ShaderSyntaxToken> ranks = [.. parameter.ArrayRankTokens, .. parameter.Type.ArrayRanks];

        for (int i = 0; i + 1 < ranks.Length; i++)
        {
            if (ranks[i].Kind == ShaderSyntaxKind.OpenBracketToken && ranks[i + 1].Kind == ShaderSyntaxKind.CloseBracketToken)
            {
                return true;
            }
        }

        return false;
    }
}
