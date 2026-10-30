using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// サンプラーとイメージの実引数の修飾子を、仮引数と照らし合わせる (GL0313)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>handleFunctionCall</c> の「argument cannot drop memory qualifier when passed to formal parameter」と
/// 「image formats must match」を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// メモリの修飾子 (<c>coherent</c>・<c>volatile</c>・<c>readonly</c>・<c>writeonly</c> など。<c>restrict</c> を除く) を持つ変数は、
/// その修飾子を持たない仮引数に渡せない。<c>coherent</c> 系は、より広い範囲の修飾子 (<c>coherent</c> は <c>devicecoherent</c> も) を持つ仮引数に渡せる。
/// 組み込み関数は、イメージの読み書きの関数だけを見る (<c>imageLoad</c> は <c>writeonly</c>、<c>imageStore</c> は <c>readonly</c>、
/// アトミックの関数はどちらも受け取らない)。
/// </para>
/// <para>
/// 利用者の関数には、イメージの形式 (<c>layout(rgba8)</c> など) が仮引数と同じ変数しか渡せない。
/// <c>writeonly</c> の仮引数には、どちらかの形式が無ければ渡せる。
/// </para>
/// <para>
/// 実引数は、名前 (関数の外の変数と仮引数) とそれを添字で引いたものだけを見る。利用者の関数は、実引数の数の合う宣言が 1 つだけのときに見る。
/// </para>
/// </remarks>
internal sealed partial class GlslArgumentQualifierAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>メモリの修飾子と、それを持つ実引数を受け取れる仮引数の修飾子 (glslang と同じく restrict は見ない)。</summary>
    private static readonly (string Qualifier, string[] Accepted)[] MemoryQualifiers =
    [
        ("volatile", ["volatile"]),
        ("coherent", ["coherent", "devicecoherent"]),
        ("devicecoherent", ["coherent", "devicecoherent"]),
        ("queuefamilycoherent", ["coherent", "devicecoherent", "queuefamilycoherent"]),
        ("workgroupcoherent", ["coherent", "devicecoherent", "queuefamilycoherent", "workgroupcoherent"]),
        ("subgroupcoherent", ["coherent", "devicecoherent", "queuefamilycoherent", "workgroupcoherent", "subgroupcoherent"]),
        ("readonly", ["readonly"]),
        ("writeonly", ["writeonly"]),
        ("nontemporal", ["nontemporal"]),
    ];

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.ArgumentQualifierMismatch];

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
            if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation)
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
            ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];
            ImmutableArray<FunctionDeclarationSyntax> declarations = compilation.GetFunctionDeclarations(callee.Name, program);

            if (declarations.IsEmpty)
            {
                CheckBuiltin(requirement, binder, callee.Name, arguments);
                continue;
            }

            ParameterSyntax[][] fitting =
            [
                .. declarations.Select(d => d.ParameterList.ToArray()).Where(p => p.Length == arguments.Length).DistinctBy(p => string.Join(",", p.Select(x => x.Type.Name))),
            ];

            if (fitting is not [{ } parameters])
            {
                continue;
            }

            for (int i = 0; i < arguments.Length; i++)
            {
                if (Describe(binder, arguments[i]) is not { } argument)
                {
                    continue;
                }

                ImmutableArray<ShaderSyntaxToken> formal = parameters[i].ModifierTokens;
                CheckMemory(requirement, argument, name => formal.Any(t => t.Text == name));

                string? formalFormat = FormatOf(formal);

                if (argument.Format != formalFormat
                    && (!formal.Any(t => t.Text == "writeonly") || (argument.Format is not null && formalFormat is not null)))
                {
                    requirement.Report(
                        GlslRuleDescriptors.ArgumentQualifierMismatch,
                        argument.At,
                        argument.Name,
                        $"イメージの形式 ({argument.Format ?? "無し"}) が、仮引数の形式 ({formalFormat ?? "無し"}) と違います。");
                }
            }
        }
    }

    /// <summary>組み込みのイメージの読み書きの関数。仮引数が持つメモリの修飾子は glslang の宣言 (Initialize.cpp) と同じ。</summary>
    private static void CheckBuiltin(GlslRequirements requirement, ExpressionTypeBinder binder, string name, ExpressionSyntax[] arguments)
    {
        string[]? accepted = name switch
        {
            "imageLoad" => ["readonly", "volatile", "coherent", "nontemporal"],
            "imageStore" => ["writeonly", "volatile", "coherent", "nontemporal"],
            _ when name.StartsWith("imageAtomic", StringComparison.Ordinal) => ["volatile", "coherent", "nontemporal"],
            _ => null,
        };

        if (accepted is null || arguments.Length == 0 || Describe(binder, arguments[0]) is not { } image)
        {
            return;
        }

        CheckMemory(requirement, image, accepted.Contains);
    }

    /// <summary>実引数のメモリの修飾子を、仮引数が受け取れるか。</summary>
    private static void CheckMemory(GlslRequirements requirement, Argument argument, Func<string, bool> formalHas)
    {
        foreach ((string qualifier, string[] accepted) in MemoryQualifiers)
        {
            if (argument.Modifiers.Contains(qualifier) && !accepted.Any(formalHas))
            {
                requirement.Report(
                    GlslRuleDescriptors.ArgumentQualifierMismatch,
                    argument.At,
                    argument.Name,
                    $"{qualifier} を外して仮引数に渡せません。仮引数にも {qualifier} を付けてください。");
            }
        }
    }

    /// <summary>実引数の変数。</summary>
    /// <param name="Name">名前。</param>
    /// <param name="At">報告する位置。</param>
    /// <param name="Modifiers">修飾子 (<c>layout(…)</c> の外)。</param>
    /// <param name="Format">イメージの形式。無ければ <see langword="null"/>。</param>
    private sealed record Argument(string Name, ShaderSyntaxToken At, HashSet<string> Modifiers, string? Format);

    /// <summary>サンプラーかイメージの変数 (とその配列の要素) の実引数を読む。分からなければ <see langword="null"/>。</summary>
    private static Argument? Describe(ExpressionTypeBinder binder, ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;

                case ElementAccessExpressionSyntax element:
                    expression = element.Target;
                    continue;

                case IdentifierExpressionSyntax identifier:
                    (string type, ImmutableArray<ShaderSyntaxToken> modifiers)? declared = binder.ResolveName(identifier) switch
                    {
                        { Kind: DeclaredNameKind.Global, Candidates: [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }] }
                            => (declaration.Type.Name, [.. declaration.ModifierTokens, .. declaration.Type.ModifierTokens]),
                        { Kind: DeclaredNameKind.Local, Candidates: [{ Declarator: ParameterSyntax parameter }] }
                            => (parameter.Type.Name, [.. parameter.ModifierTokens, .. parameter.Type.ModifierTokens]),
                        _ => null,
                    };

                    if (declared is not { } d || !GlslKeywords.IsBuiltInType(d.type) || !Opaque().IsMatch(d.type))
                    {
                        return null;
                    }

                    return new Argument(identifier.Name, identifier.Identifier, [.. TopLevel(d.modifiers)], FormatOf(d.modifiers));

                default:
                    return null;
            }
        }
    }

    /// <summary><c>layout(…)</c> に書いたイメージの形式。</summary>
    private static string? FormatOf(ImmutableArray<ShaderSyntaxToken> modifiers)
        => GlslLayoutQualifier.Read(modifiers).Select(e => e.Name.Text).FirstOrDefault(GlslLayoutAnalyzer.ImageFormats.Contains);

    /// <summary>括弧の外の修飾子の名前。</summary>
    private static IEnumerable<string> TopLevel(ImmutableArray<ShaderSyntaxToken> modifiers)
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

            if (depth == 0)
            {
                yield return modifier.Text;
            }
        }
    }

    /// <summary>サンプラー・イメージ・テクスチャ・サブパスの入力の型 (glslang の EbtSampler)。</summary>
    [GeneratedRegex(@"^(__)?[iu]?(sampler|image|texture|subpassInput)[0-9A-Za-z_]*$")]
    private static partial Regex Opaque();
}
