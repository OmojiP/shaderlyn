using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// シンボルの宣言から分かる構成の制約の検証。
/// </summary>
/// <remarks>
/// <c>#pragma multi_compile _ _X _Y</c> の <c>_X</c> と <c>_Y</c> は同時に有効にならない。
/// <c>#pragma multi_compile MODE_A MODE_B</c> はどちらかが必ず有効になる。
/// これを知らないと、決して通らない分岐を検査して誤りを報告する。
/// </remarks>
public sealed class SymbolConstraintsTests
{
    private static readonly SymbolCondition X = SymbolCondition.Symbol("_X");
    private static readonly SymbolCondition Y = SymbolCondition.Symbol("_Y");
    private static readonly SymbolCondition Z = SymbolCondition.Symbol("_Z");

    private static readonly SymbolConstraints XorY = SymbolConstraints.FromSets([["_X", "_Y"]], []);

    [Fact]
    public void 同じ宣言のシンボルを両方求める条件は成り立たない()
    {
        Assert.False(XorY.IsPossible(X.And(Y)));
        Assert.True(XorY.Apply(X.And(Y)).IsNever);
    }

    [Fact]
    public void 片方だけや別の宣言との組は成り立つ()
    {
        Assert.True(XorY.IsPossible(X));
        Assert.True(XorY.IsPossible(X.And(Z)));

        // 「どちらも無い」は既定の構成そのものである。
        Assert.True(XorY.IsPossible(X.Negate().And(Y.Negate())));
    }

    [Fact]
    public void 成り立たない項だけを落とす()
    {
        // (_X ∧ _Y) ∨ _Z は _Z と同じである。
        Assert.Equal(Z, XorY.Apply(X.And(Y).Or(Z)));
    }

    /// <summary>
    /// 制約から決まる部分を省いて短くすることを検証する。
    /// </summary>
    /// <remarks>
    /// <c>#pragma multi_compile _A _B</c> と <c>#pragma multi_compile _C _D</c> は、どちらの行もちょうど 1 つが有効になる。
    /// </remarks>
    [Fact]
    public void 必ずどれか1つが有効になる行の条件を短くする()
    {
        SymbolConstraints constraints = SymbolConstraints.FromSets(
            [["_A", "_B"], ["_C", "_D"]],
            [["_A", "_B"], ["_C", "_D"]]);

        SymbolCondition a = SymbolCondition.Symbol("_A");
        SymbolCondition b = SymbolCondition.Symbol("_B");
        SymbolCondition c = SymbolCondition.Symbol("_C");
        SymbolCondition d = SymbolCondition.Symbol("_D");

        // (_A && !_C && _D) || (!_B && !_C && _D) は _A && _D。
        SymbolCondition written = a.And(c.Negate()).And(d).Or(b.Negate().And(c.Negate()).And(d));
        Assert.Equal("_A && _D", constraints.Reduce(written).ToString());

        // 否定ではなく、有効な側の名前で示す。
        Assert.Equal("_A", constraints.Reduce(b.Negate()).ToString());

        // (_A && _D) || (_B && _D) は _D。
        Assert.Equal("_D", constraints.Reduce(a.And(d).Or(b.And(d))).ToString());

        // _A || _B はどの構成でも成り立つ。
        Assert.True(constraints.Reduce(a.Or(b)).IsAlways);
    }

    [Fact]
    public void 無しを含む行では否定を残す()
    {
        // multi_compile _ _X _Y: !_X は「無し」か _Y なので、_Y とは書けない。
        Assert.Equal("!_X", XorY.Reduce(X.Negate()).ToString());

        // _X が有効なら _Y は無効に決まっているので、!_Y は省く。
        Assert.Equal("_X", XorY.Reduce(X.And(Y.Negate())).ToString());
    }

    [Fact]
    public void 分からない条件はあるものとする()
    {
        Assert.True(XorY.IsPossible(SymbolCondition.Unknown));
        Assert.True(XorY.IsPossible(SymbolCondition.Always));
    }

    [Fact]
    public void 向きを問わない()
    {
        Assert.True(XorY.AreExclusive("_X", "_Y"));
        Assert.True(XorY.AreExclusive("_Y", "_X"));
        Assert.False(XorY.AreExclusive("_X", "_Z"));
    }

    [Fact]
    public void 同じ行に並べたシンボルどうしを排他とする()
    {
        ImmutableArray<HlslSyntaxToken> tokens = new HlslLexer(SourceText.From("""
            #pragma multi_compile_local_fragment _ _X _Y
            #pragma shader_feature _Z
            #pragma multi_compile __ _P _Q _R
            """, "test.hlsl")).Lex(out _);

        SymbolConstraints constraints = ShaderSymbols.CollectConstraintsFromTokens(tokens);

        Assert.True(constraints.AreExclusive("_X", "_Y"));
        Assert.True(constraints.AreExclusive("_P", "_R"));
        Assert.False(constraints.AreExclusive("_X", "_Z"));
        Assert.False(constraints.AreExclusive("_Y", "_P"));
    }

    // ------------------------------------------------------------------
    // どれか 1 つが必ず有効な行
    // ------------------------------------------------------------------

    private static readonly SymbolCondition ModeA = SymbolCondition.Symbol("MODE_A");
    private static readonly SymbolCondition ModeB = SymbolCondition.Symbol("MODE_B");

    private static readonly SymbolConstraints OneOfModes =
        SymbolConstraints.FromSets([["MODE_A", "MODE_B"]], [["MODE_A", "MODE_B"]]);

    [Fact]
    public void どれか1つが必ず有効な行で全部が無い条件は成り立たない()
    {
        Assert.False(OneOfModes.IsPossible(ModeA.Negate().And(ModeB.Negate())));
        Assert.True(OneOfModes.IsPossible(ModeA.Negate()));
        Assert.False(OneOfModes.IsPossible(ModeA.And(ModeB)));
    }

    [Fact]
    public void 先頭が無いことを求める分岐には同じ行の別のシンボルを足す()
    {
        // 既定の構成は MODE_A を定義している。#ifdef MODE_A の #else は MODE_B の構成でしか通らない。
        Assert.Equal([["MODE_B"]], OneOfModes.EnumerateRequiredCombinations(ModeA.Negate()));
        Assert.Equal([["MODE_B", "_X"]], OneOfModes.EnumerateRequiredCombinations(ModeA.Negate().And(X)));
    }

    [Fact]
    public void 先頭を求める分岐には何も足さない()
    {
        // 既定の構成で通るので、1 つずつの構成も要らない。
        Assert.Empty(OneOfModes.EnumerateRequiredCombinations(ModeA));
        Assert.Equal([["MODE_A", "_X"]], OneOfModes.EnumerateRequiredCombinations(ModeA.And(X)));
    }

    [Fact]
    public void 下線の無いmulti_compileだけをどれか1つが必ず有効な行とする()
    {
        ImmutableArray<HlslSyntaxToken> tokens = new HlslLexer(SourceText.From("""
            #pragma multi_compile MODE_A MODE_B
            #pragma multi_compile_local ALWAYS_ON
            #pragma multi_compile _ _X
            #pragma shader_feature FEATURE_A FEATURE_B
            #pragma shader_feature_local _ _P _Q
            """, "test.hlsl")).Lex(out _);

        SymbolConstraints constraints = ShaderSymbols.CollectConstraintsFromTokens(tokens);

        Assert.Equal(
            ["MODE_A,MODE_B", "ALWAYS_ON"],
            constraints.RequiredGroups.Select(g => string.Join(",", g)));

        // shader_feature は「どれも無い」構成もコンパイルされる。
        Assert.True(constraints.IsPossible(
            SymbolCondition.Symbol("FEATURE_A", isDefined: false).And(SymbolCondition.Symbol("FEATURE_B", isDefined: false))));
    }

    // ------------------------------------------------------------------
    // 組み込みの宣言
    // ------------------------------------------------------------------

    [Fact]
    public void 組み込みの宣言が作るシンボルも宣言されている()
    {
        ImmutableArray<HlslSyntaxToken> tokens = new HlslLexer(SourceText.From("""
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile_shadowcaster
            """, "test.hlsl")).Lex(out _);

        HashSet<string> declared = ShaderSymbols.CollectDeclaredFromTokens(tokens);
        SymbolConstraints constraints = ShaderSymbols.CollectConstraintsFromTokens(tokens);

        Assert.Superset(
            new HashSet<string> { "FOG_LINEAR", "FOG_EXP", "FOG_EXP2", "INSTANCING_ON", "SHADOWS_DEPTH", "SHADOWS_CUBE" },
            declared);

        // 霧のモードは 1 つしか選ばれない。どれも無い構成はある。
        Assert.True(constraints.AreExclusive("FOG_LINEAR", "FOG_EXP2"));
        Assert.True(constraints.IsPossible(SymbolCondition.Symbol("FOG_LINEAR", isDefined: false)
            .And(SymbolCondition.Symbol("FOG_EXP", isDefined: false))
            .And(SymbolCondition.Symbol("FOG_EXP2", isDefined: false))));

        // 影を落とすパスは、どちらかの種類で必ずコンパイルされる。
        Assert.Contains(constraints.RequiredGroups, g => g.SequenceEqual(["SHADOWS_DEPTH", "SHADOWS_CUBE"]));
        Assert.False(constraints.AreExclusive("FOG_LINEAR", "INSTANCING_ON"));
    }

    [Fact]
    public void 組み込みの宣言の引数はシンボルではない()
    {
        ImmutableArray<HlslSyntaxToken> tokens = new HlslLexer(SourceText.From("""
            #pragma multi_compile_fwdbase nolightmap nodynlightmap
            """, "test.hlsl")).Lex(out _);

        HashSet<string> declared = ShaderSymbols.CollectDeclaredFromTokens(tokens);

        Assert.Contains("DIRECTIONAL", declared);
        Assert.Contains("LIGHTPROBE_SH", declared);
        Assert.DoesNotContain("LIGHTMAP_ON", declared);
        Assert.DoesNotContain("DYNAMICLIGHTMAP_ON", declared);
        Assert.DoesNotContain("nolightmap", declared);
    }
}
