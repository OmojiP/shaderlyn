using Shaderlyn.Core.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// 出現条件の検証。
/// </summary>
/// <remarks>
/// <b>ここを間違えても、何も壊れない。</b>
/// 条件を取り違えても解析は最後まで走り、
/// 「その構成では存在しない」という誤った前提で指摘が出るだけになる。
/// 論理の性質は網羅的に確かめる。
/// </remarks>
public sealed class SymbolConditionTests
{
    private static readonly SymbolCondition A = SymbolCondition.Symbol("A");
    private static readonly SymbolCondition B = SymbolCondition.Symbol("B");
    private static readonly SymbolCondition NotA = SymbolCondition.Symbol("A", isDefined: false);

    [Fact]
    public void 値が決まったシンボルを置き換える()
    {
        // (A ∧ B) ∨ ¬A で A が偽なら常に、A が真なら B。
        SymbolCondition condition = A.And(B).Or(NotA);

        Assert.True(condition.Assume(s => s == "A" ? false : null).IsAlways);
        Assert.Equal(B, condition.Assume(s => s == "A" ? true : null));
        Assert.True(A.And(B).Assume(s => s == "B" ? false : null).IsNever);
        Assert.Equal(condition, condition.Assume(_ => null));
    }

    [Fact]
    public void 既定値は常に成り立つ()
    {
        // 構文木のほとんどのノードは無条件である。
        // 既定値がそれを表すからこそ、何も持たせずに済む。
        SymbolCondition none = default;

        Assert.True(none.IsAlways);
        Assert.Equal(SymbolCondition.Always, none);
    }

    [Fact]
    public void 特別な値は互いに違う()
    {
        Assert.NotEqual(SymbolCondition.Always, SymbolCondition.Never);
        Assert.NotEqual(SymbolCondition.Always, SymbolCondition.Unknown);
        Assert.NotEqual(SymbolCondition.Never, SymbolCondition.Unknown);

        Assert.True(SymbolCondition.Never.IsNever);
        Assert.True(SymbolCondition.Unknown.IsUnknown);
        Assert.False(SymbolCondition.Never.IsAlways);
        Assert.False(SymbolCondition.Unknown.IsAlways);
    }

    // ------------------------------------------------------------------
    // 論理積 (#if の入れ子)
    // ------------------------------------------------------------------

    [Fact]
    public void 無条件との論理積は相手をそのまま返す()
    {
        Assert.Equal(A, A.And(SymbolCondition.Always));
        Assert.Equal(A, SymbolCondition.Always.And(A));
    }

    [Fact]
    public void 同じシンボルの真と偽は同居できない()
    {
        // #ifdef A の中の #ifndef A は、決して通らない。
        Assert.True(A.And(NotA).IsNever);
    }

    [Fact]
    public void 同じ条件を重ねても増えない()
    {
        Assert.Equal(A, A.And(A));
        Assert.Equal("A", A.And(A).ToString());
    }

    [Fact]
    public void 論理積は順序によらない()
    {
        Assert.Equal(A.And(B), B.And(A));
    }

    // ------------------------------------------------------------------
    // 論理和 (併合)
    // ------------------------------------------------------------------

    [Fact]
    public void 無条件との論理和は無条件になる()
    {
        Assert.True(A.Or(SymbolCondition.Always).IsAlways);
        Assert.True(SymbolCondition.Always.Or(A).IsAlways);
    }

    [Fact]
    public void 決して成り立たない条件との論理和は相手をそのまま返す()
    {
        Assert.Equal(A, A.Or(SymbolCondition.Never));
        Assert.Equal(A, SymbolCondition.Never.Or(A));
    }

    [Fact]
    public void 論理和は順序によらない()
    {
        Assert.Equal(A.Or(B), B.Or(A));
    }

    [Fact]
    public void 短い項が長い項を吸収する()
    {
        // A ∨ (A ∧ B) は A と同じ。
        // #if の中と外で同じ形が見つかったときに、この形が素直に出てくる。
        Assert.Equal(A, A.Or(A.And(B)));
    }

    [Fact]
    public void 真と偽の両方を集めると無条件になる()
    {
        // #if A の側と #else の側の両方に同じ形があれば、それは常にある。
        Assert.True(A.Or(NotA).IsAlways);
    }

    // ------------------------------------------------------------------
    // 否定 (#else)
    // ------------------------------------------------------------------

    [Fact]
    public void 単一シンボルの否定は真偽が入れ替わる()
    {
        Assert.Equal(NotA, A.Negate());
        Assert.Equal(A, NotA.Negate());
    }

    [Fact]
    public void 二重の否定は元に戻る()
    {
        foreach (SymbolCondition condition in new[] { A, A.And(B), A.Or(B), A.And(B.Negate()) })
        {
            Assert.Equal(condition, condition.Negate().Negate());
        }
    }

    [Fact]
    public void 特別な値の否定()
    {
        Assert.True(SymbolCondition.Always.Negate().IsNever);
        Assert.True(SymbolCondition.Never.Negate().IsAlways);
        Assert.True(SymbolCondition.Unknown.Negate().IsUnknown);
    }

    [Fact]
    public void ドモルガンの法則が成り立つ()
    {
        // ¬(A ∧ B) = ¬A ∨ ¬B
        Assert.Equal(A.Negate().Or(B.Negate()), A.And(B).Negate());

        // ¬(A ∨ B) = ¬A ∧ ¬B
        Assert.Equal(A.Negate().And(B.Negate()), A.Or(B).Negate());
    }

    [Fact]
    public void 排中律と矛盾律が成り立つ()
    {
        Assert.True(A.Or(A.Negate()).IsAlways);
        Assert.True(A.And(A.Negate()).IsNever);
    }

    [Fact]
    public void 相補する項は共通部分だけになる()
    {
        // (A && B) || (!A && B) は、A がどちらでも B なら成り立つので B。
        SymbolCondition merged = A.And(B).Or(A.Negate().And(B));

        Assert.Equal(B, merged);
    }

    [Fact]
    public void 併合で最も多い形が無条件になる()
    {
        // 突き合わせで最も多く通る道:
        // 既定の構成 (シンボル無効) と、シンボルを有効にした構成の
        // 両方に同じ形があれば、それは条件なしで存在する。
        SymbolCondition keyword = SymbolCondition.Symbol("_NORMALMAP");

        Assert.True(keyword.Or(keyword.Negate()).IsAlways);
    }

    [Fact]
    public void 片方の構成にしか無い形は条件が残る()
    {
        // シンボルを有効にした構成にだけあるなら、その条件のもとでだけ存在する。
        SymbolCondition keyword = SymbolCondition.Symbol("_NORMALMAP");

        Assert.Equal("_NORMALMAP", keyword.ToString());
        Assert.False(keyword.IsAlways);
        Assert.False(keyword.IsNever);
    }

    [Fact]
    public void 入れ子の条件をまとめられる()
    {
        // #ifdef A の中の #ifdef B と、その #else を集めると A に戻る。
        SymbolCondition inner = A.And(B);
        SymbolCondition innerElse = A.And(B.Negate());

        Assert.Equal(A, inner.Or(innerElse));
    }

    // ------------------------------------------------------------------
    // 表しきれない条件
    // ------------------------------------------------------------------

    [Fact]
    public void 不明はどの演算でも不明のまま伝わる()
    {
        // 表せないことを表せていないと、誤った条件で判断してしまう。
        SymbolCondition unknown = SymbolCondition.Unknown;

        Assert.True(unknown.And(A).IsUnknown);
        Assert.True(A.And(unknown).IsUnknown);
        Assert.True(unknown.Or(A).IsUnknown);
        Assert.True(A.Or(unknown).IsUnknown);
        Assert.True(unknown.Negate().IsUnknown);
    }

    [Fact]
    public void 項が増えすぎたら不明になる()
    {
        // 近い条件で代用すると「この構成では存在しない」という誤りを生む。
        SymbolCondition wide = SymbolCondition.Never;

        for (int i = 0; i < SymbolCondition.MaxTerms + 8; i++)
        {
            wide = wide.Or(SymbolCondition.Symbol($"S{i}"));
        }

        Assert.True(wide.IsUnknown);
    }

    // ------------------------------------------------------------------
    // 等値と表示
    // ------------------------------------------------------------------

    [Fact]
    public void 同じ意味の条件は同じ値になる()
    {
        // 併合はこの等値で行うため、作り方が違っても一致しなければならない。
        Assert.Equal(A.And(B), B.And(A));
        Assert.Equal(A.And(B).GetHashCode(), B.And(A).GetHashCode());

        Assert.Equal(A.Or(B), B.Or(A));
        Assert.Equal(A.Or(B).GetHashCode(), B.Or(A).GetHashCode());
    }

    [Fact]
    public void 辞書の鍵として使える()
    {
        Dictionary<SymbolCondition, int> counts = [];

        counts[A.And(B)] = 1;
        counts[B.And(A)] = 2;

        Assert.Single(counts);
        Assert.Equal(2, counts[A.And(B)]);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("!A")]
    public void 単一の条件を読める形で表示する(string expected)
    {
        SymbolCondition condition = expected.StartsWith('!') ? NotA : A;
        Assert.Equal(expected, condition.ToString());
    }

    [Fact]
    public void 複数の条件を読める形で表示する()
    {
        Assert.Equal("A && B", A.And(B).ToString());
        Assert.Equal("常に", SymbolCondition.Always.ToString());
        Assert.Equal("不明", SymbolCondition.Unknown.ToString());
    }

    [Fact]
    public void 依存しているシンボルを列挙できる()
    {
        // 「どのシンボルに依存しているか」を利用者へ示すのに使う。
        Assert.Equal(["A", "B"], A.And(B).EnumerateSymbols().Order(StringComparer.Ordinal));
        Assert.Empty(SymbolCondition.Always.EnumerateSymbols());
        Assert.Empty(SymbolCondition.Unknown.EnumerateSymbols());
    }
}
