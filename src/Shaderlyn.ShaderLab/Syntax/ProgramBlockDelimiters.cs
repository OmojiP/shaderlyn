using System.Collections.Immutable;

namespace Shaderlyn.ShaderLab.Syntax;

/// <summary>
/// 埋め込みコードブロックに書かれている言語。
/// </summary>
public enum ProgramBlockLanguage
{
    /// <summary>
    /// HLSL 系。<c>CGPROGRAM</c> と <c>HLSLPROGRAM</c> の両方がこれにあたる。
    /// </summary>
    /// <remarks>
    /// Cg と HLSL を区別していない。Unity の Cg は実際には HLSL のサブセットとして扱われており、
    /// 構文上の差は解析の観点では無視できる。区別してもルールを書き分ける必要が生じないため、
    /// 同じフロントエンドで処理する。
    /// </remarks>
    Hlsl,

    /// <summary>GLSL。現時点では解析対象外。</summary>
    Glsl,
}

/// <summary>
/// 埋め込みコードブロック 1 種類の定義。
/// </summary>
/// <param name="StartKeyword">開始キーワード (<c>CGPROGRAM</c> など)。</param>
/// <param name="EndKeyword">対応する終了キーワード (<c>ENDCG</c> など)。</param>
/// <param name="Language">中に書かれている言語。</param>
/// <param name="IsIncludeBlock">
/// 各 Pass へ差し込まれる共通コード片 (<c>CGINCLUDE</c> / <c>HLSLINCLUDE</c>) かどうか。
/// </param>
public readonly record struct ProgramBlockDelimiter(
    string StartKeyword,
    string EndKeyword,
    ProgramBlockLanguage Language,
    bool IsIncludeBlock);

/// <summary>
/// 埋め込みコードブロックの開始・終了キーワードの対応表。
/// </summary>
/// <remarks>
/// <para>
/// <b>終了キーワードは開始キーワードごとに固定であり、対応関係を取り違えてはならない。</b>
/// 特に <c>HLSLINCLUDE</c> の終端が <c>ENDHLSL</c> であって <c>ENDINCLUDE</c> ではない点に注意。
/// </para>
/// <para>
/// この表を字句解析ツールの中ではなく独立した公開型に置いているのは、
/// 意味解析層が「ブロックのどこからどこまでが中身のコードか」を
/// 同じ知識に基づいて求められるようにするためである。
/// 両者が別々に判定すると、片方だけを直したときにずれる。
/// </para>
/// </remarks>
internal static class ProgramBlockDelimiters
{
    /// <summary>認識するブロックの全一覧。</summary>
    public static ImmutableArray<ProgramBlockDelimiter> All { get; } =
    [
        new("CGPROGRAM", "ENDCG", ProgramBlockLanguage.Hlsl, IsIncludeBlock: false),
        new("CGINCLUDE", "ENDCG", ProgramBlockLanguage.Hlsl, IsIncludeBlock: true),
        new("HLSLPROGRAM", "ENDHLSL", ProgramBlockLanguage.Hlsl, IsIncludeBlock: false),
        new("HLSLINCLUDE", "ENDHLSL", ProgramBlockLanguage.Hlsl, IsIncludeBlock: true),
        new("GLSLPROGRAM", "ENDGLSL", ProgramBlockLanguage.Glsl, IsIncludeBlock: false),
        new("GLSLINCLUDE", "ENDGLSL", ProgramBlockLanguage.Glsl, IsIncludeBlock: true),
    ];

    /// <summary>
    /// 開始キーワードから定義を引く。
    /// </summary>
    /// <param name="keyword">開始キーワード。</param>
    /// <param name="delimiter">見つかった定義。</param>
    /// <returns>見つかった場合は <see langword="true"/>。</returns>
    /// <remarks>ShaderLab のキーワードは大文字小文字を区別しない。</remarks>
    public static bool TryGetByStartKeyword(ReadOnlySpan<char> keyword, out ProgramBlockDelimiter delimiter)
    {
        foreach (ProgramBlockDelimiter candidate in All)
        {
            if (keyword.Equals(candidate.StartKeyword, StringComparison.OrdinalIgnoreCase))
            {
                delimiter = candidate;
                return true;
            }
        }

        delimiter = default;
        return false;
    }

    /// <summary>
    /// ブロック全体のテキストから、その定義を引く。
    /// </summary>
    /// <param name="blockText">ブロック全体のテキスト。開始キーワードから始まる。</param>
    /// <param name="delimiter">見つかった定義。</param>
    /// <returns>見つかった場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 開始キーワードどうしは互いに接頭辞になっていないため、
    /// 先頭一致の判定だけで一意に決まる。
    /// </remarks>
    public static bool TryGetByBlockText(string blockText, out ProgramBlockDelimiter delimiter)
    {
        ArgumentNullException.ThrowIfNull(blockText);

        foreach (ProgramBlockDelimiter candidate in All)
        {
            if (blockText.StartsWith(candidate.StartKeyword, StringComparison.OrdinalIgnoreCase))
            {
                delimiter = candidate;
                return true;
            }
        }

        delimiter = default;
        return false;
    }
}
