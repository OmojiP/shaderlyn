using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Analysis;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 呼ばれる先が、禁じた形のオーバーロードである呼び出しを報告する。
/// </summary>
/// <remarks>
/// <para>
/// <b>照合するのは、解決された宣言の仮引数の型である。</b>
/// 実引数に書かれた型ではない。
/// HLSL は暗黙の変換を行うので、<c>Mix(1, uv)</c> のように
/// 書かれた型が仮引数の型と一致しない呼び出しは普通にある。
/// 書かれた型で照合すると、そういう呼び出しを取りこぼす。
/// </para>
/// <para>
/// <b><see cref="ShaderCompilation.ResolveCall"/> が解決を引き受ける。</b>
/// 出現条件での絞り込みと、実引数の型による候補の選択はそちらにある。
/// </para>
/// <para>
/// <b>解決できなかったときは報告しない。</b>
/// 候補が絞れなかった、型が分からなかった、組み込み関数だった、のいずれも
/// 「その形で呼んでいる」ことの証拠にならない。
/// </para>
/// <para>
/// <see langword="null"/> を渡した位置は何の型でもよい。
/// <c>new BannedOverloadAnalyzer("Mix", null, "float2")</c> のように、
/// 気にする位置だけを書ける。
/// </para>
/// </remarks>
public sealed class BannedOverloadAnalyzer(string function, params string?[] parameterTypes) : HlslRuleAnalyzer
{
    private readonly ImmutableArray<string?> _parameters = [.. parameterTypes];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.BannedOverload];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Node.TargetName is not { } name
                || !string.Equals(name, function, StringComparison.Ordinal))
            {
                return;
            }

            OverloadResolution resolution = c.Compilation.ResolveCall(c.Node, c.Program);

            // どれが呼ばれるか決まっていないなら、禁じた形だとは言えない。
            if (!resolution.IsResolved || !Matches(resolution.ParameterTypes))
            {
                return;
            }

            c.ReportDiagnostic(
                CookbookRules.BannedOverload,
                c.Node.Target.Span,
                function,
                string.Join(", ", resolution.ParameterTypes));
        });
    }

    /// <summary>解決先の形が、禁じた形かを判定する。</summary>
    /// <param name="resolved">解決先の仮引数の型。</param>
    /// <returns>禁じた形なら <see langword="true"/>。</returns>
    private bool Matches(ImmutableArray<string> resolved)
    {
        if (resolved.Length != _parameters.Length)
        {
            return false;
        }

        for (int i = 0; i < _parameters.Length; i++)
        {
            if (_parameters[i] is { } expected
                && !string.Equals(resolved[i], expected, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
