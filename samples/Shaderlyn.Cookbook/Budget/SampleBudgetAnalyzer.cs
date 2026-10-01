using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 1 つのコードブロックでのテクスチャサンプリングの回数を制限する。
/// </summary>
/// <remarks>
/// <para>
/// <b>サンプリングは 2 つの形で現れる。</b>
/// <c>tex2D(...)</c> のような関数の呼び出しと、
/// <c>SAMPLE_TEXTURE2D</c> のようなマクロが展開されてできる
/// <c>_MainTex.Sample(...)</c> の形である。どちらも数えないと数が合わない。
/// </para>
/// <para>
/// <b>Pass ごとに数える。</b>
/// 予算はコンパイルされる 1 つのシェーダーに対するものであり、
/// <c>HLSLINCLUDE</c> のコードは Pass ごとに入る。
/// </para>
/// <para>
/// <b>報告は予算を超えた最初の呼び出しを指す。</b>
/// ブロック全体を指しても、どれを削ればよいかの手がかりにならない。
/// </para>
/// <para>
/// <b>構成ごとに数える。</b>
/// キーワードを有効にした構成にしか無い呼び出しも数えるために、既定の木へ足したノードまで見る
/// (<see cref="ShaderCompilation.EnumerateOwnNodes"/>)。そのうえで、同時には存在しない呼び出しを足し合わせないように、
/// 構成ごとに数えた最大を予算と比べる (<see cref="ShaderCompilation.TryEnumerateConfigurations"/>)。
/// <c>#ifdef _A</c> と <c>#else</c> に 1 回ずつ書いたサンプリングは、どの構成でも 1 回である。
/// </para>
/// </remarks>
public sealed class SampleBudgetAnalyzer(int maxSamples, params string[] functions) : DiagnosticAnalyzer
{
    private readonly ImmutableHashSet<string> _functions = [.. functions];

    /// <summary>その名前を数えるかどうかを判定する。</summary>
    /// <param name="name">関数またはメソッドの名前。</param>
    /// <returns>数えるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>名前を渡さなければ、ツールが知っているサンプリング関数を数える。</b>
    /// 一覧を自分で持つと、新しい読み方が増えたときに古いままになる。
    /// </remarks>
    private bool Counts(string name)
        => _functions.IsEmpty ? HlslIntrinsics.IsTextureSamplingFunction(name) : _functions.Contains(name);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.TooManySamples];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSyntaxTreeAction(c =>
        {
            if (c.Unit.GetModel<ShaderCompilation>() is not { } compilation)
            {
                return;
            }

            foreach (AnalyzedProgram program in compilation.Programs)
            {
                List<(InvocationExpressionSyntax Call, HlslSyntaxToken Name)> samples =
                [
                    .. compilation.EnumerateOwnNodes(program)
                        .OfType<InvocationExpressionSyntax>()
                        .Select(call => (Call: call, Name: NameToken(call)))
                        .Where(sample => sample.Name is { } name && Counts(name.Text))
                        .Select(sample => (sample.Call, sample.Name!)),
                ];

                // 条件が分からない呼び出しがあれば数えない。分からないことを誤りにしてはならない。
                if (samples.Count <= maxSamples
                    || !compilation.TryEnumerateConfigurations(samples.Select(s => s.Call), program, out var configurations))
                {
                    continue;
                }

                // いちばん多い構成で数え、その構成で予算を超えた最初の呼び出しを指す。
                List<HlslSyntaxToken> worst = configurations
                    .Select(configuration => samples.Where(s => configuration.Contains(s.Call)).Select(s => s.Name).ToList())
                    .MaxBy(present => present.Count) ?? [];

                if (worst.Count > maxSamples)
                {
                    c.ReportDiagnostic(
                        CookbookRules.TooManySamples,
                        worst[maxSamples].Span,
                        CookbookProgramSpan.Describe(program),
                        worst.Count,
                        maxSamples);
                }
            }
        });
    }

    /// <summary>呼び出しの名前のトークンを返す。</summary>
    /// <param name="invocation">対象の呼び出し。</param>
    /// <returns>名前のトークン。名前で呼んでいない場合は <see langword="null"/>。</returns>
    private static HlslSyntaxToken? NameToken(InvocationExpressionSyntax invocation) => invocation.Target switch
    {
        IdentifierExpressionSyntax identifier => identifier.Identifier,
        MemberAccessExpressionSyntax member => member.NameToken,
        _ => null,
    };
}
