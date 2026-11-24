using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// <c>GetAttributeAtVertex(value, i)</c> の value が、補間しない頂点の入力でないものを報告する (HLSL は HL0355、Slang は SG0364)。
/// </summary>
/// <remarks>
/// <para>
/// 誤りになる条件は、DXC 1.8 (ps_6_1) と slangc 2026.19 (-target spirv) にコンパイルさせて決めた (2026-11-24)。コンパイラで条件が違うので、規則を分けている。
/// </para>
/// <list type="table">
/// <item><term>nointerpolation のない入力の仮引数・構造体のフィールド</term><description>DXC は型によらず誤り。slangc はスカラーの値だけ誤り (ベクトルは通す)</description></item>
/// <item><term>入力を入れた局所変数</term><description>DXC は通す。slangc は誤り (E39027)</description></item>
/// <item><term>グローバル変数</term><description>どちらも誤り</description></item>
/// <item><term>入力のベクトルの成分 (<c>c.x</c>)</term><description>slangc は誤り。DXC は見ない</description></item>
/// </list>
/// <para>
/// ピクセルシェーダーのエントリポイントの本体に直接書いた呼び出しだけを見る。ほかの関数の仮引数を渡す呼び出しは、
/// DXC は展開してから判断し、slangc は落ちる (E99997) ので見ない。FXC はこの関数を持たないので、HLSL は DXC でコンパイルする場合だけ見る。
/// Slang の <c>pervertex</c> は <c>nointerpolation</c> と同じに扱う。
/// </para>
/// </remarks>
internal sealed class AttributeAtVertexAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>スカラーの型。</summary>
    private static readonly ImmutableHashSet<string> Scalars =
        ["float", "half", "double", "int", "uint", "bool", "int16_t", "uint16_t", "int64_t", "uint64_t", "float16_t", "float64_t", "min16float", "min16int", "min16uint"];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.AttributeAtVertexInput, Slang.SlangRuleDescriptors.AttributeAtVertexInput];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        HashSet<Location> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<FunctionDeclarationSyntax> functions = [.. EntryPointAnalyzer.EnumerateFunctions(program.Tree.Root.Declarations).Where(f => f.IsDefinition)];

            if (functions.Any(f => f.Name == "GetAttributeAtVertex"))
            {
                continue;
            }

            ExpressionTypeBinder? binder = null;

            foreach ((FunctionDeclarationSyntax entry, HlslShaderStage stage) in SystemValueSemanticAnalyzer.EntryFunctions(program, functions))
            {
                if (stage != HlslShaderStage.Pixel
                    || entry.Body is null
                    || (!compilation.IsSlang && !SystemValueSemanticAnalyzer.CompilersOf(compilation, entry, program).Contains(SystemValueCompiler.Dxc)))
                {
                    continue;
                }

                foreach (InvocationExpressionSyntax call in entry.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    if (call.TargetName != "GetAttributeAtVertex"
                        || call.ArgumentExpressions.FirstOrDefault() is not { } value
                        || !compilation.IsReportable(value)
                        || value.GetLocation() is not { } location
                        || Problem(binder ??= compilation.GetExpressionTypeBinder(program), program, entry, value, compilation.IsSlang) is not { } problem
                        || !reported.Add(location))
                    {
                        continue;
                    }

                    context.ReportDiagnostic(compilation.IsSlang
                        ? Diagnostic.Create(Slang.SlangRuleDescriptors.AttributeAtVertexInput, location,
                            $"GetAttributeAtVertex には、nointerpolation か pervertex を付けたエントリポイントの入力を直接渡してください ({problem})。")
                        : Diagnostic.Create(HlslRuleDescriptors.AttributeAtVertexInput, location,
                            $"GetAttributeAtVertex には、nointerpolation を付けたエントリポイントの入力を直接渡してください ({problem}。DXC がコンパイルの誤りにします)。"));
                }
            }
        }
    }

    /// <summary>渡した値の問題。問題がなければ (か判断できなければ) <see langword="null"/>。</summary>
    private static string? Problem(ExpressionTypeBinder binder, AnalyzedProgram program, FunctionDeclarationSyntax entry, ExpressionSyntax value, bool slang)
    {
        // s.a.b のメンバーの並びを、根の名前から順に集める。
        List<string> members = [];
        ExpressionSyntax root = value;

        while (root is MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access)
        {
            members.Insert(0, access.Name);
            root = access.Target;
        }

        if (root is not IdentifierExpressionSyntax identifier || binder.ResolveName(identifier).Candidates is not [var candidate])
        {
            return null;
        }

        switch (candidate.Declarator)
        {
            case ParameterSyntax parameter when entry.ParameterList.Contains(parameter):
                return InputProblem(program, parameter, members, slang);

            case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable:
                return declaration.Parent switch
                {
                    LocalDeclarationStatementSyntax when slang => $"局所変数 '{variable.Name}' を渡しています",
                    CompilationUnitSyntax or NamespaceDeclarationSyntax or ConstantBufferDeclarationSyntax => $"グローバル変数 '{variable.Name}' を渡しています",
                    _ => null,
                };

            default:
                return null;
        }
    }

    /// <summary>エントリポイントの仮引数 (とそのフィールド) の問題。</summary>
    private static string? InputProblem(AnalyzedProgram program, ParameterSyntax parameter, List<string> members, bool slang)
    {
        bool interpolationFree = IsNotInterpolated(parameter.ModifierTokens.Concat(parameter.Type.ModifierTokens));
        string type = parameter.Type.Name;
        string name = parameter.Name;

        foreach (string member in members)
        {
            if (program.Structs.FirstOrDefault(s => s.Keyword.Text is "struct" && s.Name == type) is not { } structure)
            {
                // ベクトルの成分 (c.x)。slangc は誤りにする。
                return slang ? $"入力 '{name}' の成分 '{member}' を渡しています" : null;
            }

            if (structure.Fields.FirstOrDefault(f => f.Variables.Any(v => v.Name == member)) is not { } field)
            {
                return null;
            }

            interpolationFree |= IsNotInterpolated(field.ModifierTokens.Concat(field.Type.ModifierTokens));
            type = field.Type.Name;
            name = $"{name}.{member}";
        }

        if (interpolationFree || (slang && !Scalars.Contains(type)) || program.Structs.Any(s => s.Name == type))
        {
            return null;
        }

        return $"入力 '{name}' に nointerpolation がありません";
    }

    private static bool IsNotInterpolated(IEnumerable<ShaderSyntaxToken> modifiers) => modifiers.Any(t => t.Text is "nointerpolation" or "pervertex");
}
