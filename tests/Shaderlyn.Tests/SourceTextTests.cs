using Shaderlyn.Core.Text;

namespace Shaderlyn.Tests;

public sealed class SourceTextTests
{
    [Theory]
    [InlineData("a\nb\nc", 3)]
    [InlineData("a\r\nb\r\nc", 3)]
    [InlineData("a\rb\rc", 3)]
    // 改行で終わるファイルは、その後ろの空行も 1 行として数える (エディタの表示に合わせる)。
    [InlineData("a\n", 2)]
    [InlineData("", 1)]
    // 1 ファイル内で改行コードが混在していても正しく数えられること。
    // Windows と macOS が混在する Unity プロジェクトでは実際に起こる。
    [InlineData("a\nb\r\nc\rd", 4)]
    public void 改行コードの種類によらず行数を数えられる(string content, int expectedLineCount)
    {
        SourceText text = SourceText.From(content);
        Assert.Equal(expectedLineCount, text.LineCount);
    }

    [Fact]
    public void オフセットを行と桁へ変換できる()
    {
        SourceText text = SourceText.From("abc\ndef\nghi");

        Assert.Equal(new LinePosition(0, 0), text.GetLinePosition(0));
        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(2));
        // 改行文字そのものは、その行の末尾として扱う。
        Assert.Equal(new LinePosition(0, 3), text.GetLinePosition(3));
        Assert.Equal(new LinePosition(1, 0), text.GetLinePosition(4));
        Assert.Equal(new LinePosition(2, 2), text.GetLinePosition(10));
    }

    [Fact]
    public void テキスト終端のオフセットも変換できる()
    {
        // 「ファイル末尾で何かが欠けている」という診断はこの位置を指すため、
        // 終端オフセットは範囲外にしてはならない。
        SourceText text = SourceText.From("abc");
        Assert.Equal(new LinePosition(0, 3), text.GetLinePosition(text.Length));
    }

    [Theory]
    [InlineData("abc\ndef", 0, "abc")]
    [InlineData("abc\ndef", 1, "def")]
    [InlineData("abc\r\ndef", 0, "abc")]
    [InlineData("abc\rdef", 0, "abc")]
    public void 行の内容を改行文字を含めずに取り出せる(string content, int lineIndex, string expected)
    {
        SourceText text = SourceText.From(content);
        Assert.Equal(expected, text.GetLineText(lineIndex));
    }

    [Fact]
    public void 範囲外のオフセットは例外になる()
    {
        SourceText text = SourceText.From("abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetLinePosition(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetLinePosition(4));
    }

    [Fact]
    public void 行と桁の文字列表現は一始まりになる()
    {
        // 内部表現は 0 始まりだが、利用者に見せる文字列はエディタの表示に合わせる。
        Assert.Equal("1:1", new LinePosition(0, 0).ToString());
        Assert.Equal("12:5", new LinePosition(11, 4).ToString());
    }
}

public sealed class TextSpanTests
{
    [Fact]
    public void 終端位置は範囲に含まれない()
    {
        TextSpan span = new(5, 3);
        Assert.True(span.Contains(5));
        Assert.True(span.Contains(7));
        Assert.False(span.Contains(8));
        Assert.False(span.Contains(4));
    }

    [Fact]
    public void 空範囲はいかなる位置も含まない()
    {
        TextSpan span = new(5, 0);
        Assert.True(span.IsEmpty);
        Assert.False(span.Contains(5));
    }

    [Fact]
    public void 触れているかの判定は終端位置も含める()
    {
        // 語を打ち終えた直後のカーソルは語の終端にある。そこも「語の上」と答える。
        TextSpan span = new(5, 3);
        Assert.True(span.IntersectsWith(5));
        Assert.True(span.IntersectsWith(8));
        Assert.False(span.IntersectsWith(9));
        Assert.False(span.IntersectsWith(4));

        // 空範囲は、その位置にだけ触れている。
        Assert.True(new TextSpan(5, 0).IntersectsWith(5));
        Assert.False(new TextSpan(5, 0).IntersectsWith(6));
    }

    [Fact]
    public void 接しているだけの範囲は重なりとみなさない()
    {
        Assert.False(new TextSpan(0, 5).OverlapsWith(new TextSpan(5, 5)));
        Assert.True(new TextSpan(0, 6).OverlapsWith(new TextSpan(5, 5)));
    }

    [Fact]
    public void 開始位置と長さの順で比較される()
    {
        Assert.True(new TextSpan(0, 5).CompareTo(new TextSpan(1, 1)) < 0);
        Assert.True(new TextSpan(1, 1).CompareTo(new TextSpan(1, 5)) < 0);
        Assert.Equal(0, new TextSpan(1, 5).CompareTo(new TextSpan(1, 5)));
    }

    [Fact]
    public void 負の値や逆転した境界は例外になる()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextSpan.FromBounds(5, 4));
    }
}
