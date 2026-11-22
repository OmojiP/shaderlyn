using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.CFamily.Preprocessing;

/// <summary>
/// 字句解析の結果をソーステキスト単位で覚えておく。
/// </summary>
/// <remarks>
/// <para>
/// <b>これは解析時間を大きく左右する。</b>
/// 1 つの <c>.shader</c> には複数の Pass があり、Pass ごとに埋め込みコードを展開し直す。
/// どの Pass も同じ URP のヘッダ群を取り込むため、
/// キャッシュが無いと同じ数万行のヘッダを Pass の数だけ字句解析することになる。
/// プロジェクト全体では同じヘッダを数百回読み直す形になり、解析時間の大半がここに消える。
/// </para>
/// <para>
/// キーはソーステキストの<b>参照</b>である。内容の比較ではない。
/// include の解決器は同じパスに対して同じインスタンスを返すよう作られており、
/// 参照が一致することがそのまま「同じファイル」を意味する。
/// </para>
/// <para>
/// <b>内容やパスをキーにしてはならない。</b>
/// Pass ごとに作るマスクしたテキストは、
/// 元のファイルと同じパス・同じ長さを持ちながら中身が違う。
/// パスと長さで引くと、別の Pass のトークン列を返してしまう。
/// </para>
/// <para>
/// <b>キーは弱参照で持つ。</b>
/// マスクしたテキストは解析 1 回分の寿命しかなく、
/// 強参照で持つと解析のたびに項目が積み上がる。
/// 取り込んだヘッダのテキストは解決器が保持し続けるため、
/// 弱参照にしてもキャッシュから落ちない。
/// </para>
/// </remarks>
public sealed class TokenCache
{
    /// <summary>
    /// ソーステキストごとの字句解析の結果。値は <see cref="Lazy{T}"/> で包む。
    /// </summary>
    /// <remarks>
    /// <c>ConditionalWeakTable.GetValue</c> は、複数のスレッドが同じキーを同時に初めて引くと、値を作る処理を全員が実行する
    /// (表に残るのは 1 つだけ)。ファイルを並列に解析すると、同じヘッダを取り込む解析が同時に始まり、同じ字句解析を重ねていた。
    /// <c>Lazy</c> で包むと、表に残った 1 つの <c>Lazy</c> だけが評価され、ほかのスレッドはその結果を待つ。
    /// </remarks>
    private readonly ConditionalWeakTable<SourceText, Lazy<Entry>> _entries = [];

    /// <summary>字句解析の結果 1 件分。</summary>
    /// <param name="Tokens">トークン列。</param>
    /// <param name="Diagnostics">字句解析が検出した問題。</param>
    /// <remarks>
    /// 弱参照の表は参照型しか値にできないため、構造体ではなくクラスにしている。
    /// </remarks>
    private sealed record Entry(
        ImmutableArray<ShaderSyntaxToken> Tokens,
        ImmutableArray<ShaderLexer.LexerDiagnostic> Diagnostics);

    /// <summary>
    /// ソーステキストのトークン列を取得する。未解析であればここで字句解析する。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <param name="diagnostics">字句解析が検出した問題。</param>
    /// <returns>トークン列。</returns>
    public ImmutableArray<ShaderSyntaxToken> GetOrLex(
        SourceText text,
        out ImmutableArray<ShaderLexer.LexerDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);

        Entry entry = _entries.GetValue(text, static source => new Lazy<Entry>(
            () =>
            {
                ShaderLexer lexer = new(source);
                ImmutableArray<ShaderSyntaxToken> tokens = lexer.Lex(out ImmutableArray<ShaderLexer.LexerDiagnostic> lexed);
                return new Entry(tokens, lexed);
            },
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        diagnostics = entry.Diagnostics;
        return entry.Tokens;
    }
}
