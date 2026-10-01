using System.Collections.Immutable;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// <c>#pragma kernel</c> が宣言する、コンピュートシェーダーの入口 1 つ分。
/// </summary>
/// <param name="Name">カーネルの名前。</param>
/// <param name="Macros">
/// そのカーネルをコンパイルするときにだけ定義されるマクロ。
/// 値を書かない指定は <c>1</c> とみなす。
/// </param>
/// <param name="NameToken">カーネル名のトークン。診断の位置に使う。</param>
internal readonly record struct ComputeKernel(
    string Name,
    ImmutableDictionary<string, string> Macros,
    HlslSyntaxToken NameToken);

/// <summary>
/// <c>#pragma kernel</c> を、展開が始まる前に読み取る。
/// </summary>
/// <remarks>
/// <para>
/// <b>Unity はカーネルごとに別々にコンパイルする。</b>
/// <c>#pragma kernel</c> はカーネル名の後ろにマクロ定義を並べられ、
/// そのカーネルのコンパイル中だけ定義される。
/// </para>
/// <code>
/// #pragma kernel Deferred_Direct_Fptl  SHADE_OPAQUE_ENTRY=Deferred_Direct_Fptl
/// #pragma kernel KSampleCopy4_1_x_8    KERNEL_NAME41=KSampleCopy4_1_x_8  KERNEL_SIZE=8
/// </code>
/// <para>
/// <b>これを読まないと、関数名や配列長がマクロのままになる。</b>
/// 実測では、Unity 同梱の <c>.compute</c> 192 件を
/// マクロ抜きで解析すると 123 件の「エントリポイントが見つかりません」と
/// 77 件の構文エラーが出た。どれも誤検出である。
/// </para>
/// <para>
/// 展開する前に決まっていなければならないので、
/// 字句解析したトークン列から読む。<c>#pragma multi_compile</c> の
/// シンボルを先に拾うのと同じ理由である (<see cref="ShaderSymbols"/>)。
/// </para>
/// </remarks>
internal static class ComputeKernels
{
    /// <summary>
    /// トークン列から <c>#pragma kernel</c> を集める。
    /// </summary>
    /// <param name="tokens">展開前のトークン列。</param>
    /// <returns>現れた順のカーネル。1 つも無ければ空。</returns>
    /// <remarks>
    /// 同じ名前のカーネルが 2 度書かれていても、書かれた分だけ返す。
    /// 重複はそれ自体が誤りであり、ここで何も伝えずに 1 つにまとめると見えなくなる。
    /// </remarks>
    public static ImmutableArray<ComputeKernel> Collect(ImmutableArray<HlslSyntaxToken> tokens)
    {
        ImmutableArray<ComputeKernel>.Builder kernels = ImmutableArray.CreateBuilder<ComputeKernel>();

        for (int i = 0; i < tokens.Length; i++)
        {
            if (!IsKernelPragma(tokens, i) || i + 3 >= tokens.Length)
            {
                continue;
            }

            HlslSyntaxToken nameToken = tokens[i + 3];

            if (nameToken.Kind != HlslSyntaxKind.IdentifierToken || nameToken.IsAtLineStart)
            {
                continue;
            }

            kernels.Add(new ComputeKernel(
                nameToken.Text,
                ReadMacros(tokens, i + 4),
                nameToken));
        }

        return kernels.ToImmutable();
    }

    /// <summary>その位置から <c>#pragma kernel</c> が始まっているかを判定する。</summary>
    /// <param name="tokens">トークン列。</param>
    /// <param name="index">判定する位置。</param>
    /// <returns>始まっていれば <see langword="true"/>。</returns>
    private static bool IsKernelPragma(ImmutableArray<HlslSyntaxToken> tokens, int index)
        => tokens[index].Kind == HlslSyntaxKind.HashToken
           && tokens[index].IsAtLineStart
           && index + 2 < tokens.Length
           && tokens[index + 1].Kind == HlslSyntaxKind.IdentifierToken
           && tokens[index + 1].Text == "pragma"
           && tokens[index + 2].Kind == HlslSyntaxKind.IdentifierToken
           && tokens[index + 2].Text == "kernel";

    /// <summary>
    /// カーネル名の後ろに並んだマクロ定義を読む。
    /// </summary>
    /// <param name="tokens">トークン列。</param>
    /// <param name="start">カーネル名の次の位置。</param>
    /// <returns>読み取ったマクロ。</returns>
    /// <remarks>
    /// <c>NAME=VALUE</c> と <c>NAME</c> の 2 つの形がある。
    /// 値を書かない形は <c>1</c> とみなす。C コンパイラの <c>-D</c> と同じ規則である。
    /// </remarks>
    private static ImmutableDictionary<string, string> ReadMacros(
        ImmutableArray<HlslSyntaxToken> tokens,
        int start)
    {
        ImmutableDictionary<string, string>.Builder macros =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        // 行の終わりまでが引数である。
        for (int i = start; i < tokens.Length && !tokens[i].IsAtLineStart; i++)
        {
            if (tokens[i].Kind != HlslSyntaxKind.IdentifierToken)
            {
                continue;
            }

            string name = tokens[i].Text;

            if (i + 2 < tokens.Length
                && !tokens[i + 1].IsAtLineStart
                && tokens[i + 1].Kind == HlslSyntaxKind.EqualsToken
                && !tokens[i + 2].IsAtLineStart)
            {
                macros[name] = tokens[i + 2].Text;
                i += 2;
                continue;
            }

            macros[name] = "1";
        }

        return macros.ToImmutable();
    }
}
