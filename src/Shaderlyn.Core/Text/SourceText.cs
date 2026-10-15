using System.Collections.Immutable;
using System.Text;

namespace Shaderlyn.Core.Text;

/// <summary>
/// 解析対象となる 1 ファイル分のソーステキストと、その行位置索引を保持する。
/// </summary>
/// <remarks>
/// <para>
/// 全ての層はファイル内容をこの型を通してのみ参照する。
/// 字句解析ツールはオフセットだけを扱い、行・桁への変換が必要になった時点で
/// <see cref="GetLinePosition(int)"/> を呼ぶ。この分離により、
/// トークン 1 個ごとに行番号を持ち回るコストを避けている。
/// </para>
/// <para>
/// 行頭オフセットの表は生成時に一度だけ構築する。ファイルは一度読み込んだら不変であり、
/// 診断の位置解決は解析の最後にまとめて発生するため、遅延構築にする利点が無いためである。
/// </para>
/// </remarks>
public sealed class SourceText
{
    private readonly ImmutableArray<int> _lineStarts;

    private SourceText(string content, string filePath, Encoding encoding)
    {
        Content = content;
        FilePath = filePath;
        Encoding = encoding;
        _lineStarts = ComputeLineStarts(content);
    }

    /// <summary>ファイルの内容全体。</summary>
    public string Content { get; }

    /// <summary>
    /// このテキストの出所を示すパス。
    /// </summary>
    /// <remarks>
    /// 診断の出力とベースラインの照合に使う。メモリ上で生成したテキストの場合は
    /// <c>&lt;memory&gt;</c> のような実在しないパスが入りうるため、
    /// このパスでファイルを開き直す処理を書いてはならない。
    /// </remarks>
    public string FilePath { get; }

    /// <summary>読み込み時に検出されたエンコーディング。</summary>
    public Encoding Encoding { get; }

    /// <summary>テキストの長さ (UTF-16 コードユニット数)。</summary>
    public int Length => Content.Length;

    /// <summary>
    /// テキストの行数。
    /// </summary>
    /// <remarks>
    /// 末尾が改行で終わるファイルの場合、その改行の後ろにある空の行も 1 行として数える。
    /// これはエディタの行番号表示と一致させるためである。
    /// </remarks>
    public int LineCount => _lineStarts.Length;

    /// <summary>指定オフセットの文字を取得する。</summary>
    /// <param name="index">0 始まりのオフセット。</param>
    /// <returns>その位置の文字。</returns>
    public char this[int index] => Content[index];

    /// <summary>
    /// 文字列からソーステキストを生成する。
    /// </summary>
    /// <param name="content">ファイルの内容。</param>
    /// <param name="filePath">出所を示すパス。テストやメモリ上生成では任意の識別子でよい。</param>
    /// <param name="encoding">エンコーディング。省略時は UTF-8 とみなす。</param>
    /// <returns>生成されたソーステキスト。</returns>
    public static SourceText From(string content, string filePath = "<memory>", Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(filePath);

        // 先頭の BOM は取り除く。
        // ファイルから読む経路は StreamReader が取り除くため、
        // ここで残すと同じファイルでも読み込み方によって位置が 1 文字ずれる。
        // Unity が生成する .shader は BOM 付きのことがあり、
        // 「先頭に Shader 以外の記述がある」という誤った構文エラーになる。
        if (content.StartsWith('\uFEFF'))
        {
            content = content[1..];
        }

        return new SourceText(content, filePath, encoding ?? Encoding.UTF8);
    }

    /// <summary>
    /// ファイルを読み込んでソーステキストを生成する。
    /// </summary>
    /// <param name="filePath">読み込むファイルのパス。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>生成されたソーステキスト。</returns>
    /// <exception cref="IOException">ファイルの読み込みに失敗した場合。</exception>
    /// <remarks>
    /// BOM があればそれに従い、無ければ UTF-8 として読む。
    /// Unity が生成する .shader ファイルは BOM 付き UTF-8 のことも BOM 無しのこともあるため、
    /// どちらでも読めるようにしておく必要がある。
    /// </remarks>
    public static async Task<SourceText> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return FromBytes(bytes, filePath);
    }

    /// <summary>
    /// ファイルを読み込んでソーステキストを生成する (同期版)。
    /// </summary>
    /// <param name="filePath">読み込むファイルのパス。</param>
    /// <returns>生成されたソーステキスト。</returns>
    /// <exception cref="IOException">ファイルの読み込みに失敗した場合。</exception>
    /// <remarks>エンコーディングの決め方は <see cref="FromBytes"/> と同じ。取り込んだヘッダを読むのに使う。</remarks>
    public static SourceText Load(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return FromBytes(File.ReadAllBytes(filePath), filePath);
    }

    /// <summary>
    /// ファイルの中身のバイト列からソーステキストを生成する。
    /// </summary>
    /// <param name="bytes">ファイルの中身。</param>
    /// <param name="filePath">出所を示すパス。</param>
    /// <returns>生成されたソーステキスト。読んだエンコーディングは <see cref="Encoding"/> に入る。</returns>
    /// <remarks>
    /// <para>
    /// BOM があればそれに従う (UTF-8 / UTF-16 LE / UTF-16 BE)。無ければ UTF-8 として読む。
    /// </para>
    /// <para>
    /// <b>BOM の無い UTF-16 も、NUL の並び方から見分けて読む。</b>
    /// ASCII の多いテキストを UTF-16 で書くと、1 バイトおきに NUL が並ぶ。
    /// UTF-8 として読むと NUL を挟んだ文字列になり、どの構文も読めない。
    /// DXC は UTF-16 のファイルを読めるので、汎用の HLSL の環境ではそのまま解析できなければならない。
    /// Unity は UTF-16 を読めない (BOM の有無によらない) ので、Unity の環境ではそれを規則で伝える (UN1056)。
    /// </para>
    /// </remarks>
    public static SourceText FromBytes(byte[] bytes, string filePath)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(filePath);

        Encoding encoding = DetectEncoding(bytes, out int preamble);
        string content = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        return new SourceText(content, filePath, encoding);
    }

    /// <summary>BOM の無い UTF-16 と見なすのに要る、見るバイト数の上限。</summary>
    private const int Utf16SniffLength = 512;

    /// <summary>バイト列のエンコーディングを決める。</summary>
    /// <param name="bytes">ファイルの中身。</param>
    /// <param name="preamble">BOM のバイト数。</param>
    /// <returns>エンコーディング。</returns>
    private static Encoding DetectEncoding(byte[] bytes, out int preamble)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preamble = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preamble = 2;
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preamble = 2;
            return Encoding.BigEndianUnicode;
        }

        preamble = 0;

        // 1 バイトおきの NUL を数える。偶数番目にだけ多ければ BE、奇数番目にだけ多ければ LE。
        // UTF-8 のテキストに NUL はまず現れないので、半分を超える偏りがあれば UTF-16 である。
        int length = Math.Min(bytes.Length, Utf16SniffLength) & ~1;

        if (length >= 4)
        {
            int evenNuls = 0;
            int oddNuls = 0;

            for (int i = 0; i < length; i += 2)
            {
                evenNuls += bytes[i] == 0 ? 1 : 0;
                oddNuls += bytes[i + 1] == 0 ? 1 : 0;
            }

            int pairs = length / 2;

            if (oddNuls * 2 > pairs && evenNuls * 10 < pairs)
            {
                return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            }

            if (evenNuls * 2 > pairs && oddNuls * 10 < pairs)
            {
                return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
            }
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    /// <summary>UTF-16 (LE / BE、BOM の有無によらない) で読んだかどうか。</summary>
    public bool IsUtf16 => Encoding.CodePage is 1200 or 1201;

    /// <summary>
    /// 行・桁の位置をオフセットへ変換する。
    /// </summary>
    /// <param name="position">0 始まりの行・桁。</param>
    /// <returns>対応するオフセット。</returns>
    /// <remarks>
    /// <para>
    /// エディタは行・桁で位置を伝えてくるため、内部の表現へ直す必要がある。
    /// </para>
    /// <para>
    /// <b>範囲の外は端へ丸める。</b>
    /// 編集の途中でエディタが送ってくる位置は、
    /// こちらが持っているテキストより後ろを指していることがある。
    /// 例外にすると、その 1 回のために応答が返らなくなる。
    /// </para>
    /// </remarks>
    public int GetOffset(LinePosition position)
    {
        int line = Math.Clamp(position.Line, 0, _lineStarts.Length - 1);
        int lineStart = _lineStarts[line];
        int lineEnd = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Content.Length;

        return Math.Clamp(lineStart + position.Character, lineStart, lineEnd);
    }

    /// <summary>
    /// オフセットを行・桁の位置へ変換する。
    /// </summary>
    /// <param name="position">0 始まりのオフセット。テキスト長と等しい値 (終端) も許容する。</param>
    /// <returns>対応する行・桁の位置。</returns>
    /// <exception cref="ArgumentOutOfRangeException">オフセットが負、またはテキスト長を超える場合。</exception>
    public LinePosition GetLinePosition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Content.Length);

        int line = FindLineIndex(position);
        return new LinePosition(line, position - _lineStarts[line]);
    }

    /// <summary>
    /// 文字範囲を行・桁の範囲へ変換する。
    /// </summary>
    /// <param name="span">変換する文字範囲。</param>
    /// <returns>対応する行・桁の範囲。</returns>
    public LinePositionSpan GetLinePositionSpan(TextSpan span)
        => new(GetLinePosition(span.Start), GetLinePosition(span.End));

    /// <summary>
    /// 指定行の範囲を、行末の改行文字を含めずに返す。
    /// </summary>
    /// <param name="lineIndex">0 始まりの行番号。</param>
    /// <returns>その行の文字範囲。</returns>
    /// <exception cref="ArgumentOutOfRangeException">行番号が範囲外の場合。</exception>
    public TextSpan GetLineSpan(int lineIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lineIndex, _lineStarts.Length);

        int start = _lineStarts[lineIndex];
        int endIncludingBreak = lineIndex + 1 < _lineStarts.Length ? _lineStarts[lineIndex + 1] : Content.Length;

        // 改行文字を落とす。CRLF・LF・CR のいずれにも対応する必要がある。
        int end = endIncludingBreak;
        if (end > start && Content[end - 1] == '\n')
        {
            end--;
        }

        if (end > start && Content[end - 1] == '\r')
        {
            end--;
        }

        return TextSpan.FromBounds(start, end);
    }

    /// <summary>
    /// 指定行の内容を、行末の改行文字を含めずに返す。
    /// </summary>
    /// <param name="lineIndex">0 始まりの行番号。</param>
    /// <returns>その行の文字列。</returns>
    public string GetLineText(int lineIndex) => ToString(GetLineSpan(lineIndex));

    /// <summary>指定範囲の部分文字列を返す。</summary>
    /// <param name="span">取り出す範囲。</param>
    /// <returns>その範囲の文字列。</returns>
    /// <exception cref="ArgumentOutOfRangeException">範囲がテキストの外にはみ出す場合。</exception>
    public string ToString(TextSpan span)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(span.End, Content.Length);
        return Content.Substring(span.Start, span.Length);
    }

    /// <summary>指定範囲を割り当てなしで参照する。</summary>
    /// <param name="span">取り出す範囲。</param>
    /// <returns>その範囲を指すスパン。</returns>
    /// <remarks>字句解析のようにトークンごとの比較が高頻度で発生する箇所で使う。</remarks>
    public ReadOnlySpan<char> AsSpan(TextSpan span) => Content.AsSpan(span.Start, span.Length);

    /// <summary>テキスト全体を返す。</summary>
    /// <returns><see cref="Content"/> と同じ文字列。</returns>
    public override string ToString() => Content;

    /// <summary>
    /// オフセットを含む行の番号を二分探索で求める。
    /// </summary>
    /// <param name="position">検索するオフセット。</param>
    /// <returns>0 始まりの行番号。</returns>
    private int FindLineIndex(int position)
    {
        // BinarySearch はキーが見つからない場合に「挿入位置の補数」を返す。
        // 行頭ちょうどなら一致するのでその行、そうでなければ挿入位置の 1 つ手前が該当行になる。
        int index = _lineStarts.BinarySearch(position);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>
    /// 各行の先頭オフセットを列挙した表を構築する。
    /// </summary>
    /// <param name="content">対象のテキスト。</param>
    /// <returns>行頭オフセットの昇順配列。先頭要素は常に 0。</returns>
    /// <remarks>
    /// 改行として LF・CRLF・CR の 3 種類を認識する。
    /// ShaderLab のファイルは Windows と macOS の混在環境で編集されることが多く、
    /// 1 ファイル内で改行コードが混在している例も珍しくないため、3 種類すべてを扱う。
    /// </remarks>
    private static ImmutableArray<int> ComputeLineStarts(string content)
    {
        ImmutableArray<int>.Builder builder = ImmutableArray.CreateBuilder<int>();
        builder.Add(0);

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];

            if (c == '\r')
            {
                // CRLF は 2 文字で 1 つの改行として扱う。
                if (i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }

                builder.Add(i + 1);
            }
            else if (c == '\n')
            {
                builder.Add(i + 1);
            }
        }

        return builder.ToImmutable();
    }
}
