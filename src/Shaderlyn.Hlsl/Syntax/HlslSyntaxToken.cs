using System.Collections.Immutable;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Hlsl.Syntax;

/// <summary>
/// HLSL トークンに付随する空白・コメント・行継続。
/// </summary>
/// <param name="Kind">trivia の種別。</param>
/// <param name="Span">ソーステキスト上の範囲。</param>
public readonly record struct HlslSyntaxTrivia(HlslSyntaxKind Kind, TextSpan Span);

/// <summary>
/// HLSL / Cg の字句単位。
/// </summary>
/// <remarks>
/// <para>
/// ShaderLab のトークンと異なり <see cref="Source"/> を保持する。
/// プリプロセッサが <c>#include</c> を解決すると、
/// 1 つのトークン列に複数のファイル由来のトークンが混ざるためである。
/// これが無いと「どのファイルの何行目か」を復元できない。
/// </para>
/// <para>
/// <see cref="IsAtLineStart"/> はプリプロセッサ指令の検出に使う。
/// <c>#</c> が行頭にあるかどうかで指令か文字列化演算子かが決まる。
/// </para>
/// </remarks>
public sealed class HlslSyntaxToken
{
    /// <summary>
    /// トークンを生成する。
    /// </summary>
    /// <param name="kind">トークンの種別。</param>
    /// <param name="source">このトークンが属するソーステキスト。</param>
    /// <param name="span">trivia を除いたトークン本体の範囲。</param>
    /// <param name="text">トークン本体の文字列。</param>
    /// <param name="isAtLineStart">物理行の最初の非 trivia トークンかどうか。</param>
    /// <param name="leadingTrivia">先行 trivia。</param>
    /// <param name="trailingTrivia">後続 trivia。</param>
    /// <param name="valueText">文字列リテラルの引用符を外した値など。無い場合は <see langword="null"/>。</param>
    /// <param name="isMissing">構文エラーからの回復のために合成されたトークンかどうか。</param>
    /// <param name="isFromMacroExpansion">マクロ展開の結果として現れたトークンかどうか。</param>
    /// <param name="macroDefinitionSpan">
    /// 利用者のファイルに書かれたマクロの本体での位置。それ以外は <see langword="null"/>。
    /// </param>
    /// <param name="macroArgumentSpan">
    /// 利用者のファイルでマクロの実引数として書かれた位置。それ以外は <see langword="null"/>。
    /// </param>
    /// <param name="macroDefinitionSource">
    /// <paramref name="macroDefinitionSpan"/> が指すマクロの本体を書いたファイル。
    /// </param>
    public HlslSyntaxToken(
        HlslSyntaxKind kind,
        SourceText source,
        TextSpan span,
        string text,
        bool isAtLineStart = false,
        ImmutableArray<HlslSyntaxTrivia> leadingTrivia = default,
        ImmutableArray<HlslSyntaxTrivia> trailingTrivia = default,
        string? valueText = null,
        bool isMissing = false,
        bool isFromMacroExpansion = false,
        TextSpan? macroDefinitionSpan = null,
        TextSpan? macroArgumentSpan = null,
        SourceText? macroDefinitionSource = null)
    {
        MacroArgumentSpan = macroArgumentSpan;
        Kind = kind;
        Source = source;
        Span = span;
        Text = text;
        IsAtLineStart = isAtLineStart;
        LeadingTrivia = leadingTrivia.IsDefault ? [] : leadingTrivia;
        TrailingTrivia = trailingTrivia.IsDefault ? [] : trailingTrivia;
        ValueText = valueText ?? text;
        IsMissing = isMissing;
        IsFromMacroExpansion = isFromMacroExpansion;
        MacroDefinitionSpan = macroDefinitionSpan;
        MacroDefinitionSource = macroDefinitionSpan is null ? null : macroDefinitionSource ?? source;
    }

    /// <summary>トークンの種別。</summary>
    public HlslSyntaxKind Kind { get; }

    /// <summary>
    /// このトークンが属するソーステキスト。
    /// </summary>
    /// <remarks>
    /// マクロ展開で生成されたトークンは、展開結果ではなく
    /// <b>マクロを呼び出した位置</b>を指す。診断はコードを書いた場所に出るべきであり、
    /// マクロ定義の中を指しても利用者は直しようがないためである。
    /// </remarks>
    public SourceText Source { get; }

    /// <summary>trivia を除いたトークン本体の範囲。</summary>
    public TextSpan Span { get; }

    /// <summary>trivia を含めた範囲。</summary>
    public TextSpan FullSpan
    {
        get
        {
            int start = LeadingTrivia.Length > 0 ? LeadingTrivia[0].Span.Start : Span.Start;
            int end = TrailingTrivia.Length > 0 ? TrailingTrivia[^1].Span.End : Span.End;
            return TextSpan.FromBounds(start, end);
        }
    }

    /// <summary>トークン本体の文字列。</summary>
    public string Text { get; }

    /// <summary>意味的な値。文字列リテラルなら引用符を外した中身、それ以外は <see cref="Text"/> と同じ。</summary>
    public string ValueText { get; }

    /// <summary>
    /// 物理行の最初の非 trivia トークンかどうか。
    /// </summary>
    /// <remarks>
    /// 行継続 (<c>\</c> + 改行) は行の切れ目として数えない。
    /// 複数行に分けて書かれたマクロ定義が、途中の行で指令として誤解釈されるのを防ぐためである。
    /// </remarks>
    public bool IsAtLineStart { get; }

    /// <summary>先行 trivia。</summary>
    public ImmutableArray<HlslSyntaxTrivia> LeadingTrivia { get; }

    /// <summary>後続 trivia。</summary>
    public ImmutableArray<HlslSyntaxTrivia> TrailingTrivia { get; }

    /// <summary>構文エラーからの回復のために合成されたトークンかどうか。</summary>
    /// <remarks>
    /// <b>ルール側は、欠落トークンに対して診断を出してはならない。</b>
    /// 構文エラーは既に報告されており、重ねて指摘すると出力が読めなくなる。
    /// </remarks>
    public bool IsMissing { get; }

    /// <summary>このトークンの位置を診断用の位置情報として返す。</summary>
    /// <returns>ソーステキストと範囲を組み合わせた位置。</returns>
    /// <remarks>マクロの実引数として書かれたトークンは、書かれた位置 (<see cref="MacroArgumentSpan"/>) を指す。</remarks>
    public Core.Diagnostics.Location GetLocation() => Core.Diagnostics.Location.Create(Source, MacroArgumentSpan ?? Span);

    /// <summary>
    /// トークンのテキストを大文字小文字を区別して比較する。
    /// </summary>
    /// <param name="text">比較する文字列。</param>
    /// <returns>一致する場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// ShaderLab と異なり <b>HLSL は大文字小文字を区別する</b>。
    /// <c>float</c> と <c>Float</c> は別物であり、区別しない比較をすると誤った解釈になる。
    /// </remarks>
    public bool TextIs(string text) => string.Equals(Text, text, StringComparison.Ordinal);

    /// <summary>
    /// マクロ展開の結果として現れたトークンかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>構文木を根拠にするルールは、これを確認しなければならない。</b>
    /// 展開後のトークンは <see cref="Source"/> も <see cref="Span"/> も
    /// マクロの呼び出し位置に揃えられている。診断の位置としてはそれが正しいが、
    /// 「利用者がその式をそこに書いた」ことの証拠にはならない。
    /// ヘッダのマクロが内部で使っている呼び出しを、
    /// 利用者が書いたものとして指摘すると直しようがない。
    /// </para>
    /// <para>
    /// 展開の結果である以上、1 回の展開から生まれたトークンはすべて同じ範囲を持つ。
    /// 位置による絞り込みでは区別できないため、印を付けて区別する。
    /// </para>
    /// </remarks>
    public bool IsFromMacroExpansion { get; }

    /// <summary>
    /// 利用者のファイル (解析しているファイルと、利用者が書いたヘッダ) に書かれたマクロの本体での位置。
    /// それ以外は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>利用者が直せるマクロかどうかを、展開した後も見分けるために要る。</b>
    /// <see cref="Span"/> は呼び出し位置へ移してあるので、
    /// そこからは「どのマクロの、本体のどこから来たか」を復元できない。
    /// </para>
    /// <para>
    /// Unity や外部パッケージのヘッダのマクロには付けない。利用者に直しようがなく、
    /// 覚えても報告に使えないためである。
    /// 入れ子の展開では、いちばん内側 (最初に移したとき) の位置を残す。
    /// </para>
    /// <para>
    /// 本体を書いたファイルは <see cref="MacroDefinitionSource"/> にある。
    /// <see cref="Source"/> は呼び出し位置のファイルなので、ヘッダで定義したマクロを
    /// 別のファイルから呼ぶと、この位置とは別のファイルになる。
    /// </para>
    /// </remarks>
    public TextSpan? MacroDefinitionSpan { get; }

    /// <summary>
    /// <see cref="MacroDefinitionSpan"/> が指すマクロの本体を書いたファイル。印が無ければ <see langword="null"/>。
    /// </summary>
    public SourceText? MacroDefinitionSource { get; }

    /// <summary>
    /// 利用者のファイルで、マクロの実引数として書かれた位置。それ以外は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>実引数は、利用者が呼び出し位置に書いたコードである。</b>
    /// <c>SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv)</c> の <c>uv</c> も、
    /// <c>HEADER_ID(uv.z)</c> の <c>uv.z</c> も、その場所に書かれている。
    /// 展開の結果としか分からなかったため、実引数の中の誤りはどのルールにも検査されていなかった。
    /// </para>
    /// <para>
    /// <see cref="Span"/> は本体のトークンと同じく呼び出し位置へ移す。
    /// 構成ごとの木の突き合わせは位置を鍵にしているので、実引数だけ元の位置に残すと対応が崩れる。
    /// 書かれた位置はここに別に持ち、書かれたものかの判定と、報告の位置 (<see cref="GetLocation"/>) に使う。
    /// </para>
    /// </remarks>
    public TextSpan? MacroArgumentSpan { get; }

    /// <summary>
    /// マクロの呼び出し位置へ移した複製を返す。
    /// </summary>
    /// <param name="source">呼び出し位置のソーステキスト。</param>
    /// <param name="span">呼び出し位置の範囲。</param>
    /// <param name="keepDefinition">
    /// マクロの本体での位置 (<see cref="MacroDefinitionSpan"/>) を引き継ぐかどうか。
    /// 呼び出し位置が利用者のファイルの外なら引き継がない。そこは報告しない場所である。
    /// </param>
    /// <param name="argumentSpan">
    /// 実引数として埋め込むなら、利用者のファイルで書かれた位置 (<see cref="MacroArgumentSpan"/>)。
    /// 本体のトークンなら <see langword="null"/>。
    /// </param>
    /// <returns>複製されたトークン。</returns>
    /// <remarks>
    /// <b>診断は利用者がコードを書いた場所に出さなければならない。</b>
    /// マクロ定義の中を指しても、利用者はそこを直せないし直すべきでもない。
    /// あわせて <see cref="IsFromMacroExpansion"/> を立て、
    /// 位置だけでは失われる「展開の結果である」という事実を残す。
    /// </remarks>
    public HlslSyntaxToken RelocateToMacroCallSite(
        SourceText source,
        TextSpan span,
        bool keepDefinition = false,
        TextSpan? argumentSpan = null)
        => new(Kind, source, span, Text, isAtLineStart: false, [], [], ValueText, IsMissing,
            isFromMacroExpansion: true,
            macroDefinitionSpan: keepDefinition ? MacroDefinitionSpan : null,
            macroArgumentSpan: argumentSpan,
            macroDefinitionSource: keepDefinition ? MacroDefinitionSource : null);

    /// <summary>
    /// マクロの本体での位置を付けた複製を返す。
    /// </summary>
    /// <param name="span">本体での位置。</param>
    /// <param name="source">本体を書いたファイル。</param>
    /// <returns>複製されたトークン。</returns>
    /// <remarks>
    /// <b>本体のトークンにだけ付ける。</b>
    /// 実引数として埋め込まれたトークンは、利用者が呼び出し位置に書いたものであり、
    /// そちらの位置で報告しなければならない。
    /// </remarks>
    public HlslSyntaxToken WithMacroDefinitionSpan(TextSpan span, SourceText source)
        => new(Kind, Source, Span, Text, IsAtLineStart, LeadingTrivia, TrailingTrivia, ValueText, IsMissing,
            IsFromMacroExpansion, span, MacroArgumentSpan, source);

    /// <summary>トークンの種別とテキストを並べた文字列を返す。</summary>
    /// <returns>デバッグ用の文字列表現。</returns>
    public override string ToString() => IsMissing ? $"{Kind}(欠落)" : $"{Kind}('{Text}')";
}
