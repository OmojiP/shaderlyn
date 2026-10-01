using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cookbook;

/// <summary>コードブロック単位で報告するときに使う共通の判断。</summary>
/// <remarks>
/// <b>コードブロックには「指すべき 1 行」が無い。</b>
/// 「このブロックに #pragma が無い」はブロック全体に対する指摘である。
/// <c>.compute</c> のような HLSL 単体ファイルには囲む <c>HLSLPROGRAM</c> が無いので、
/// その場合はファイルの先頭を指す。
/// </remarks>
internal static class CookbookProgramSpan
{
    /// <summary>コードブロックを指す範囲を返す。</summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <returns>報告に使う範囲。</returns>
    public static TextSpan Of(AnalyzedProgram program)
        => program.Block?.Span ?? new TextSpan(0, 0);

    /// <summary>コードブロックを利用者に示す呼び名を返す。</summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <returns>Pass 名、カーネル名、いずれも無ければ既定の呼び名。</returns>
    public static string Describe(AnalyzedProgram program)
        => program.PassName ?? program.KernelName ?? "このコードブロック";
}
