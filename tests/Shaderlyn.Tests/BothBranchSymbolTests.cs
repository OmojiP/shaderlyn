using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// 両方の分岐を残す展開の検証。
/// </summary>
/// <remarks>
/// <b>既定では 1 か所も挙動が変わらないことが前提である。</b>
/// この仕組みは指定したシンボルにだけ効く。
/// 指定しなければ、この仕組みが無かったときと同じ結果になる。
/// </remarks>
public sealed class BothBranchSymbolTests
{
    /// <summary>コードを展開する。</summary>
    /// <param name="code">展開するコード。</param>
    /// <param name="kept">両方の分岐を残すシンボル。</param>
    /// <returns>展開結果。</returns>
    private static PreprocessResult Preprocess(string code, params string[] kept)
        => Preprocess(code, kept, []);

    /// <summary>構成によって定義が変わるシンボルも指定して展開する。</summary>
    /// <param name="code">対象のコード。</param>
    /// <param name="kept">両方の分岐を残すシンボル。</param>
    /// <param name="configuration">
    /// 両方の分岐は残さないが、構成によって定義が変わるシンボル。
    /// 宣言されているのに並べる対象から外れたシンボル (コードで使われたマクロのものなど) にあたる。
    /// </param>
    /// <returns>展開結果。</returns>
    private static PreprocessResult Preprocess(string code, string[] kept, string[] configuration)
    {
        PreprocessorOptions options = new()
        {
            BothBranchSymbols = [.. kept],
            ConfigurationSymbols = [.. configuration],
        };

        return new HlslPreprocessor(options).Preprocess(SourceText.From(code, "test.hlsl"));
    }

    /// <summary>展開結果のトークンを 1 つの文字列にする。</summary>
    /// <param name="result">展開結果。</param>
    /// <returns>連ねた文字列。</returns>
    private static string TextOf(PreprocessResult result)
        => string.Join(" ", result.Tokens.Select(t => t.Text));

    /// <summary>指定した位置のトークンに付いている条件を返す。</summary>
    /// <param name="result">展開結果。</param>
    /// <param name="text">探すトークンのテキスト。</param>
    /// <returns>付いている条件。</returns>
    private static SymbolCondition ConditionOf(PreprocessResult result, string text)
    {
        int index = -1;

        for (int i = 0; i < result.Tokens.Length; i++)
        {
            if (result.Tokens[i].Text == text) { index = i; break; }
        }

        Assert.True(index >= 0, $"'{text}' が出力に無い");

        foreach (ConditionalTokenRange range in result.ConditionalRegions)
        {
            if (index >= range.Start && index < range.Start + range.Length)
            {
                return range.Condition;
            }
        }

        return SymbolCondition.Always;
    }

    private const string BothBranches = """
        #ifdef _NORMALMAP
        float4 _WhenOn;
        #else
        float4 _WhenOff;
        #endif
        """;

    [Fact]
    public void 指定しなければ今までどおり片方だけ残す()
    {
        // この仕組みが無かったときと 1 か所も変わらないこと。
        ConditionResultAssert(Preprocess(BothBranches), expectOn: false, expectOff: true);
        Assert.Empty(Preprocess(BothBranches).ConditionalRegions);
    }

    [Fact]
    public void 指定すると両方の分岐が残る()
    {
        PreprocessResult result = Preprocess(BothBranches, "_NORMALMAP");

        ConditionResultAssert(result, expectOn: true, expectOff: true);
    }

    [Fact]
    public void 残した分岐にそれぞれの条件が付く()
    {
        PreprocessResult result = Preprocess(BothBranches, "_NORMALMAP");

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionOf(result, "_WhenOn"));

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP", isDefined: false),
            ConditionOf(result, "_WhenOff"));
    }

    [Fact]
    public void 条件の外には条件が付かない()
    {
        PreprocessResult result = Preprocess(
            $"float4 _Always;\n{BothBranches}\nfloat4 _AlsoAlways;",
            "_NORMALMAP");

        Assert.True(ConditionOf(result, "_Always").IsAlways);
        Assert.True(ConditionOf(result, "_AlsoAlways").IsAlways);
    }

    [Fact]
    public void 指定していないシンボルは今までどおり()
    {
        // 別のシンボルを指定しても、このシンボルの分岐はまとめられる。
        ConditionResultAssert(
            Preprocess(BothBranches, "_OTHER"), expectOn: false, expectOff: true);
    }

    [Fact]
    public void マクロを切り替える領域は両方残さない()
    {
        // その先の展開結果そのものが変わるため、並べても意味を成さない。
        PreprocessResult result = Preprocess(
            """
            #ifdef _NORMALMAP
            #define USE_NORMALS 1
            #endif
            float4 _Value;
            """,
            "_NORMALMAP");

        Assert.Empty(result.ConditionalRegions);
    }

    [Fact]
    public void 構文の単位で閉じていない領域は両方残さない()
    {
        // 並べると "x = 1 x = 2 ;" になり、構文として通らない。
        PreprocessResult result = Preprocess(
            """
            void f()
            {
                int x;
            #ifdef _NORMALMAP
                x = 1
            #else
                x = 2
            #endif
                ;
            }
            """,
            "_NORMALMAP");

        Assert.Empty(result.ConditionalRegions);
    }

    /// <summary>入れ子の条件を持つコード。前後にも同じ外側の条件のコードがある。</summary>
    private const string NestedBranches = """
        #ifdef _NORMALMAP
        float4 _Before;
        #ifdef _DETAIL
        float4 _Both;
        #else
        float4 _OnlyNormal;
        #endif
        float4 _After;
        #else
        float4 _Neither;
        #endif
        """;

    [Fact]
    public void 入れ子の条件は掛け合わせる()
    {
        // 内側にあるコードが現れるのは、外側と内側の両方が成り立つときだけである。
        PreprocessResult result = Preprocess(NestedBranches, "_NORMALMAP", "_DETAIL");

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP").And(SymbolCondition.Symbol("_DETAIL")),
            ConditionOf(result, "_Both"));

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP")
                .And(SymbolCondition.Symbol("_DETAIL", isDefined: false)),
            ConditionOf(result, "_OnlyNormal"));
    }

    [Fact]
    public void 入れ子の前後は外側の条件のままである()
    {
        // 入れ子で区切り忘れると、手前に書かれたコードが条件を失う。
        PreprocessResult result = Preprocess(NestedBranches, "_NORMALMAP", "_DETAIL");

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionOf(result, "_Before"));
        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionOf(result, "_After"));

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP", isDefined: false),
            ConditionOf(result, "_Neither"));
    }

    [Fact]
    public void 入れ子でも四つの分岐がすべて残る()
    {
        string text = TextOf(Preprocess(NestedBranches, "_NORMALMAP", "_DETAIL"));

        foreach (string name in (string[])["_Before", "_Both", "_OnlyNormal", "_After", "_Neither"])
        {
            Assert.Contains(name, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 外側が片方だけを残すなら内側も残さない()
    {
        // 外側で捨てられた分岐の中にあったのかどうかが、付けた条件から読み取れない。
        // _NORMALMAP は構成によって変わるが並べないので、内側の _DETAIL も残さない。
        PreprocessResult result = Preprocess(NestedBranches, ["_DETAIL"], ["_NORMALMAP"]);

        Assert.Empty(result.ConditionalRegions);
        Assert.Contains("_DETAIL", result.DeclinedBothBranchSymbols);
    }

    /// <summary>
    /// 構成によらない外側の条件は、内側のシンボルの条件を並べる妨げにならないことを検証する。
    /// </summary>
    /// <remarks>
    /// インクルードガードや <c>#if SHADER_TARGET &gt;= 30</c> は、単一構成で決めた分岐がどの構成でも通る。
    /// 以前はこれも「外側が並べていない」と数え、中のシンボルを構成ごとに展開し直していた。
    /// ヘッダはほぼすべてインクルードガードで囲まれているので、ヘッダの中のシンボルの条件は並べられなかった。
    /// </remarks>
    [Theory]
    [InlineData("#ifndef GUARD_INCLUDED\n#define GUARD_INCLUDED")]
    [InlineData("#if !defined(SHADER_API_GLES2) && 1")]
    [InlineData("#if defined(SHADER_API_D3D11) || !defined(SHADER_API_GLES)")]
    public void 構成によらない外側の条件の中でも並べる(string opening)
    {
        PreprocessResult result = Preprocess(
            opening + "\nfloat4 _Before;\n#ifdef _NORMALMAP\nfloat4 _WhenOn;\n#else\nfloat4 _WhenOff;\n#endif\n#endif",
            ["_NORMALMAP"],
            []);

        Assert.DoesNotContain("_NORMALMAP", result.DeclinedBothBranchSymbols);
        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionOf(result, "_WhenOn"));
        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP", false), ConditionOf(result, "_WhenOff"));
        Assert.Equal(SymbolCondition.Always, ConditionOf(result, "_Before"));
    }

    [Fact]
    public void 構成によらない条件で読み飛ばした分岐の中は並べられなかったと数えない()
    {
        // 別のプラットフォーム向けのコードは、どの構成でも読まれない。構成ごとに展開し直しても読めない。
        PreprocessResult result = Preprocess(
            "#if 0\n#ifdef _NORMALMAP\nfloat4 _WhenOn;\n#endif\n#endif\n#ifdef _NORMALMAP\nfloat4 _Other;\n#endif",
            ["_NORMALMAP"],
            []);

        Assert.DoesNotContain("_NORMALMAP", result.DeclinedBothBranchSymbols);
        Assert.DoesNotContain("_WhenOn", TextOf(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// キーワード以外を値に解いたらキーワードが残らなかった条件を、並べられなかったと数えないことを検証する。
    /// </summary>
    /// <remarks>
    /// <c>#if defined(_A) &amp;&amp; (SHADER_TARGET &gt;= 45)</c> は、<c>SHADER_TARGET</c> が 35 の構成ではどの構成でも偽である。
    /// <c>_A</c> を有効にして展開し直しても、読めるコードは増えない。
    /// </remarks>
    [Fact]
    public void キーワードが残らない条件は並べられなかったと数えない()
    {
        PreprocessResult result = Preprocess(
            "#if defined(_A) && (TARGET >= 45)\n#ifdef _B\nfloat4 _Never;\n#endif\n#endif\n#ifdef _A\nfloat4 _WhenA;\n#endif\n#ifdef _B\nfloat4 _WhenB;\n#endif",
            ["_A", "_B"],
            []);

        Assert.DoesNotContain("_A", result.DeclinedBothBranchSymbols);
        Assert.DoesNotContain("_B", result.DeclinedBothBranchSymbols);
        Assert.DoesNotContain("_Never", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public void 構成によって変わるマクロを値に解いた条件は並べられなかったと数える()
    {
        // LEVEL はこの構成では 0 だが、_Q を有効にした構成では 2 になる。
        PreprocessResult result = Preprocess(
            "#ifdef _Q\n#define LEVEL 2\n#else\n#define LEVEL 0\n#endif\n#if defined(_A) && (LEVEL >= 1)\nfloat4 _WhenBoth;\n#endif",
            ["_A"],
            ["_Q"]);

        Assert.Contains("_A", result.DeclinedBothBranchSymbols);
    }

    [Fact]
    public void 並べた分岐の間に構成によらない条件が挟まっても条件を失わない()
    {
        // _A の中の固定の条件の中に _B がある。_B より手前の _InA は _A の条件を持ち続ける。
        PreprocessResult result = Preprocess(
            "#ifdef _A\nfloat4 _InA;\n#if !defined(SHADER_API_GLES2)\nfloat4 _InStatic;\n#ifdef _B\nfloat4 _InB;\n#endif\nfloat4 _AfterB;\n#endif\n#endif",
            ["_A", "_B"],
            []);

        Assert.Equal(SymbolCondition.Symbol("_A"), ConditionOf(result, "_InA"));
        Assert.Equal(SymbolCondition.Symbol("_A"), ConditionOf(result, "_InStatic"));
        Assert.Equal(SymbolCondition.Symbol("_A").And(SymbolCondition.Symbol("_B")), ConditionOf(result, "_InB"));
        Assert.Equal(SymbolCondition.Symbol("_A"), ConditionOf(result, "_AfterB"));
    }

    /// <summary>分岐が 3 つある領域。</summary>
    private const string ThreeBranches = """
        #ifdef _NORMALMAP
        float4 _WhenOn;
        #elif defined(_DETAIL)
        float4 _WhenDetail;
        #else
        float4 _WhenOff;
        #endif
        """;

    [Fact]
    public void 分岐が三つ以上でもすべて残る()
    {
        string text = TextOf(Preprocess(ThreeBranches, "_NORMALMAP", "_DETAIL"));

        foreach (string name in (string[])["_WhenOn", "_WhenDetail", "_WhenOff"])
        {
            Assert.Contains(name, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 後ろの分岐にはそれまでの否定が掛かる()
    {
        // #elif が通るのは「それより前の分岐がすべて外れたとき」である。
        // ここを掛け忘れると、#ifdef の側と同時に成り立つ条件になってしまう。
        PreprocessResult result = Preprocess(ThreeBranches, "_NORMALMAP", "_DETAIL");

        SymbolCondition normal = SymbolCondition.Symbol("_NORMALMAP");
        SymbolCondition detail = SymbolCondition.Symbol("_DETAIL");

        Assert.Equal(normal, ConditionOf(result, "_WhenOn"));
        Assert.Equal(normal.Negate().And(detail), ConditionOf(result, "_WhenDetail"));
        Assert.Equal(normal.Negate().And(detail.Negate()), ConditionOf(result, "_WhenOff"));
    }

    [Fact]
    public void 記号として扱えない条件は値を解いてまとめる()
    {
        // #elif の条件が記号 1 つでなければ、単一構成での値は決まっている。
        // 偽で確定した分岐はどの構成でも通らないので、条件を付けずに読み飛ばす。
        PreprocessResult result = Preprocess(
            """
            #ifdef _NORMALMAP
            float4 _WhenOn;
            #elif SOMETHING_UNDEFINED
            float4 _Unreachable;
            #else
            float4 _WhenOff;
            #endif
            """,
            "_NORMALMAP");

        string text = TextOf(result);

        Assert.DoesNotContain("_Unreachable", text, StringComparison.Ordinal);
        Assert.Contains("_WhenOn", text, StringComparison.Ordinal);

        // 偽の分岐は条件に何も足さない。#else は #ifdef の裏返しのままである。
        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP", isDefined: false),
            ConditionOf(result, "_WhenOff"));
    }

    [Fact]
    public void 記号として扱えない条件が真なら後ろの分岐は残らない()
    {
        PreprocessResult result = Preprocess(
            """
            #define TRUTHY 1
            #ifdef _NORMALMAP
            float4 _WhenOn;
            #elif TRUTHY
            float4 _WhenTruthy;
            #else
            float4 _Unreachable;
            #endif
            """,
            "_NORMALMAP");

        string text = TextOf(result);

        Assert.DoesNotContain("_Unreachable", text, StringComparison.Ordinal);

        // #elif が通るのは _NORMALMAP が無いときだけである。
        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP", isDefined: false),
            ConditionOf(result, "_WhenTruthy"));
    }

    [Fact]
    public void ifとdefinedで書いてもまとめる()
    {
        // #if defined(X) は #ifdef X と同じ形である。
        // 式であることそのものは、並べられない理由にならない。
        PreprocessResult result = Preprocess(
            """
            #if defined(_NORMALMAP)
            float4 _WhenOn;
            #else
            float4 _WhenOff;
            #endif
            """,
            "_NORMALMAP");

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionOf(result, "_WhenOn"));

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP", isDefined: false),
            ConditionOf(result, "_WhenOff"));
    }

    [Fact]
    public void 論理演算で結んだ条件もまとめる()
    {
        // defined() だけでできた式は、そのまま出現条件として書ける。
        PreprocessResult result = Preprocess(
            """
            #if defined(_NORMALMAP) || defined(_DETAIL)
            float4 _Either;
            #else
            float4 _Neither;
            #endif
            """,
            "_NORMALMAP",
            "_DETAIL");

        SymbolCondition either =
            SymbolCondition.Symbol("_NORMALMAP").Or(SymbolCondition.Symbol("_DETAIL"));

        Assert.Equal(either, ConditionOf(result, "_Either"));
        Assert.Equal(either.Negate(), ConditionOf(result, "_Neither"));
    }

    [Fact]
    public void シンボルでない名前はその場で値を解く()
    {
        // 単一構成での展開では、シンボルでない名前の定義の有無は決まっている。
        // 解いたうえで、シンボルだけを記号として残す。
        PreprocessResult result = Preprocess(
            """
            #define PLATFORM_SUPPORTS 1
            #if defined(PLATFORM_SUPPORTS) && defined(_NORMALMAP)
            float4 _WhenOn;
            #else
            float4 _WhenOff;
            #endif
            """,
            "_NORMALMAP");

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionOf(result, "_WhenOn"));
    }

    [Fact]
    public void 構成によって定義が変わるマクロを呼ぶ領域はまとめない()
    {
        // 定義を切り替えている領域を並べないのと同じ理由が、使う側にもある。
        // 並べると、有効な定義に対して引数の数が合わない呼び出しが現れ、
        // そこから先の展開が丸ごと崩れる (実物の URP の SAMPLE_GI がこの形)。
        PreprocessResult result = Preprocess(
            """
            #ifdef USE_LIGHTMAP
            #define SAMPLE_GI(lm, sh, normal) Lightmap(lm, normal)
            #else
            #define SAMPLE_GI(sh, normal) Probe(sh, normal)
            #endif

            #ifdef _NORMALMAP
            float4 a = SAMPLE_GI(uv, sh, n);
            #else
            float4 b = SAMPLE_GI(sh, n);
            #endif
            """,
            ["_NORMALMAP"],
            ["USE_LIGHTMAP"]);

        Assert.Empty(result.ConditionalRegions);
        Assert.Contains("_NORMALMAP", result.DeclinedBothBranchSymbols);
    }

    /// <summary>
    /// 構成によらない条件の中で定義したマクロは、呼んでも並べる妨げにならないことを検証する。
    /// </summary>
    /// <remarks>
    /// インクルードガードや <c>SHADER_API_*</c> の中の定義は、どの構成でも同じである。
    /// 以前は条件の中の定義をすべて「構成によって変わる」と数えていたため、
    /// ヘッダのマクロ (<c>FLT_MIN</c>、<c>SAMPLE_TEXTURE2D</c> など) を呼ぶだけで並べられなかった。
    /// </remarks>
    [Fact]
    public void 構成によらない条件の中で定義したマクロを呼ぶ領域はまとめる()
    {
        PreprocessResult result = Preprocess(
            """
            #ifndef MACROS_INCLUDED
            #define MACROS_INCLUDED
            #define FLT_MIN 1.175494351e-38
            #endif

            #ifdef _NORMALMAP
            float a = FLT_MIN;
            #else
            float b = FLT_MIN;
            #endif
            """,
            ["_NORMALMAP"],
            []);

        Assert.DoesNotContain("_NORMALMAP", result.DeclinedBothBranchSymbols);
        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionOf(result, "a"));
    }

    /// <summary>
    /// 構成によって変わる条件のために読み飛ばした定義も、構成によって変わるマクロとして数えることを検証する。
    /// </summary>
    /// <remarks>
    /// URP のヘッダは <c>DOTS_INSTANCING_ON</c> から <c>UNITY_DOTS_INSTANCING_ENABLED</c> を導き、
    /// それを見て <c>SAMPLE_TEXTURE2D_LIGHTMAP</c> の中身 (引数の個数) を切り替える。
    /// 既定の構成では導く側が読み飛ばされるので、定義されたものだけを数えると
    /// 切り替える側の条件を構成によらないものと取り違え、その中のマクロを呼ぶ領域を並べてしまう。
    /// </remarks>
    [Fact]
    public void 読み飛ばした分岐の定義も構成によって変わるマクロとして数える()
    {
        PreprocessResult result = Preprocess(
            """
            #ifdef DOTS_INSTANCING_ON
            #define DOTS_ENABLED
            #endif

            #if defined(DOTS_ENABLED)
            #define SAMPLE_LM(t, s, uv, slice) SampleArray(t, s, uv, slice)
            #else
            #define SAMPLE_LM(t, s, uv) Sample(t, s, uv)
            #endif

            #ifdef _NORMALMAP
            float4 a = SAMPLE_LM(t, s, uv);
            #endif
            """,
            ["_NORMALMAP"],
            ["DOTS_INSTANCING_ON"]);

        Assert.Contains("_NORMALMAP", result.DeclinedBothBranchSymbols);
    }

    [Fact]
    public void 本体が構成によって変わる名前を参照するマクロは構成によって変わる()
    {
        // 条件の外で定義していても、本体が構成によって変わるマクロを使えば、展開の結果は構成で変わる。
        PreprocessResult result = Preprocess(
            """
            #ifdef USE_LIGHTMAP
            #define GI_SOURCE Lightmap
            #else
            #define GI_SOURCE Probe
            #endif
            #define SAMPLE_GI(x) GI_SOURCE(x)

            #ifdef _NORMALMAP
            float4 a = SAMPLE_GI(n);
            #endif
            """,
            ["_NORMALMAP"],
            ["USE_LIGHTMAP"]);

        Assert.Contains("_NORMALMAP", result.DeclinedBothBranchSymbols);
    }

    [Fact]
    public void 並べない領域の分岐の記号は残せなかったと記録する()
    {
        // #elif に書かれたシンボルも、並べられなければ構成ごとの展開が要る。
        // ここを落とすと、その分岐だけがどちらの経路からも見えなくなる。
        PreprocessResult result = Preprocess(
            """
            #ifdef _NORMALMAP
            #include "other.hlsl"
            #elif defined(_DETAIL)
            float4 _WhenDetail;
            #endif
            """,
            "_NORMALMAP",
            "_DETAIL");

        // 取り込みを切り替えているので並べられない。
        Assert.Empty(result.ConditionalRegions);

        Assert.Contains("_NORMALMAP", result.DeclinedBothBranchSymbols);
        Assert.Contains("_DETAIL", result.DeclinedBothBranchSymbols);
    }

    [Fact]
    public void ifndefの側にも正しい条件が付く()
    {
        PreprocessResult result = Preprocess(
            """
            #ifndef _NORMALMAP
            float4 _WhenOff;
            #else
            float4 _WhenOn;
            #endif
            """,
            "_NORMALMAP");

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP", isDefined: false),
            ConditionOf(result, "_WhenOff"));

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionOf(result, "_WhenOn"));
    }

    [Fact]
    public void 非活性領域の識別子は両方残したら記録しない()
    {
        // 残しているのだから「非活性領域にしか無い名前」ではない。
        // ここを直し忘れると、誤検出を避けるための仕組みが余計に働く。
        PreprocessResult result = Preprocess(BothBranches, "_NORMALMAP");

        Assert.DoesNotContain("_WhenOn", result.SkippedIdentifiers);
    }

    [Fact]
    public void 一部しか残せなかったシンボルは残せなかったと記録する()
    {
        // 1 つのシンボルが複数の領域を守っていることがある。
        // 片方が並べられても、もう片方が並べられなければ、
        // そのシンボルはまだ構成ごとの展開が要る。
        // ここを取り違えると、並べられなかった側の宣言が丸ごと見えなくなる。
        PreprocessResult result = Preprocess(
            """
            #ifdef _NORMALMAP
            #include "other.hlsl"
            #endif

            #ifdef _NORMALMAP
            float4 _Guarded;
            #endif
            """,
            "_NORMALMAP");

        // 片方は並べられた。
        Assert.NotEmpty(result.ConditionalRegions);

        // それでも「残せなかった」に入っていること。
        Assert.Contains("_NORMALMAP", result.DeclinedBothBranchSymbols);
    }

    [Fact]
    public void すべて残せたシンボルは残せなかったに入らない()
    {
        PreprocessResult result = Preprocess(
            """
            #ifdef _NORMALMAP
            float4 _One;
            #endif

            #ifdef _NORMALMAP
            float4 _Two;
            #endif
            """,
            "_NORMALMAP");

        Assert.NotEmpty(result.ConditionalRegions);
        Assert.Empty(result.DeclinedBothBranchSymbols);
    }

    /// <summary>出力にどちらの分岐が含まれるかを確かめる。</summary>
    /// <param name="result">展開結果。</param>
    /// <param name="expectOn">有効側が含まれることを期待するか。</param>
    /// <param name="expectOff">無効側が含まれることを期待するか。</param>
    private static void ConditionResultAssert(PreprocessResult result, bool expectOn, bool expectOff)
    {
        string text = TextOf(result);

        Assert.Equal(expectOn, text.Contains("_WhenOn", StringComparison.Ordinal));
        Assert.Equal(expectOff, text.Contains("_WhenOff", StringComparison.Ordinal));
    }
}
