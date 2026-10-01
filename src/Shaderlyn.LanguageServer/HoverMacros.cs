using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// マクロの説明。
/// </summary>
internal static partial class HoverBuilder
{
    /// <summary>
    /// マクロについて答える。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。マクロの上に無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>構文木にはマクロの呼び出しが残っていない。</b>
    /// 展開されて消えているため、構文木を辿っても
    /// <c>CBUFFER_START</c> や <c>TEXTURE2D</c> の上では何も見つからない。
    /// Unity のシェーダーはこれらを多用するので、
    /// 答えられないままにすると「ほとんどの語で何も出ない」ことになる。
    /// </para>
    /// <para>
    /// 展開前のトークン列を見て、名前がマクロとして定義されていれば
    /// その定義を出す。マクロが何に化けるかは、
    /// 指摘の理由を追うときに最も知りたいことの 1 つである。
    /// </para>
    /// </remarks>
    private static HoverResult? BuildForMacro(ShaderCompilation compilation, int offset)
    {
        HlslSyntaxToken? token = CursorTarget.FindIdentifier(compilation, offset);

        if (token is null)
        {
            return null;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            // 定数バッファの名前はマクロの引数として書かれる。
            // 展開後の宣言は呼び出し位置へ移されるため、構文木からは辿れない。
            //
            // ConstantBufferSymbol は構造体なので、FirstOrDefault と is { } を
            // 組み合わせてはならない。見つからなくても既定値が返り、
            // パターンが必ず一致して、中身の無い値を使ってしまう。
            foreach (ConstantBufferSymbol candidate in program.ConstantBuffers)
            {
                if (string.Equals(candidate.Name, token.Text, StringComparison.Ordinal))
                {
                    return DescribeConstantBufferSymbol(compilation, candidate, token.Span);
                }
            }

            if (!program.Tree.PreprocessResult.Macros.TryGetValue(token.Text, out MacroDefinition? macro))
            {
                continue;
            }

            string parameters = macro.IsFunctionLike ? $"({string.Join(", ", macro.Parameters)})" : string.Empty;
            string body = string.Join(" ", macro.Body.Select(t => t.Text));
            string shown = body.Length <= 200 ? body : body[..200] + "…";

            return new HoverResult(
                $"```hlsl\n#define {macro.Name}{parameters} {shown}\n```\n\n"
                + (macro.IsFunctionLike ? "関数形式マクロ" : "マクロ")
                + "。展開後のコードに名前は残らない。",
                token.Span);
        }

        return null;
    }

    /// <summary>
    /// 定数バッファを説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="buffer">対象の定数バッファ。</param>
    /// <param name="span">対応する範囲。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// マテリアル用の定数バッファかどうかを出す。
    /// SRP Batcher が効くかどうかはここに入っているかで決まる。
    /// </remarks>
    private static HoverResult DescribeConstantBufferSymbol(
        ShaderCompilation compilation,
        ConstantBufferSymbol buffer,
        TextSpan span)
    {
        string members = string.Join(
            "\n", buffer.Members.Select(m => $"    {m.TypeName} {m.Name};"));

        string note = compilation.Profile.MaterialConstantBufferName is { } material
                      && string.Equals(buffer.Name, material, StringComparison.Ordinal)
            ? $"\n\n{compilation.Profile.DisplayName} がマテリアルの値に使う定数バッファ。"
              + "ここに入っていない uniform があると SRP Batcher が無効になる。"
            : string.Empty;

        return new HoverResult(
            $"```hlsl\nCBUFFER_START({buffer.Name})\n{members}\nCBUFFER_END\n```\n\n"
            + $"定数バッファ ({buffer.Members.Length} 件の uniform){note}",
            span);
    }
}
