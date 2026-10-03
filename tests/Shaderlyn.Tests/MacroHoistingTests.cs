using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// 条件によって中身が変わるマクロを、文の単位で複製する展開の検証 (条件の巻き上げ)。
/// </summary>
/// <remarks>
/// <b>並べた分岐と同じ形に収まることが前提である。</b>
/// 複製した文はそれぞれ条件付きの領域になり、構文解析はそのまま通らなければならない
/// (条件の巻き上げ)。
/// </remarks>
public sealed class MacroHoistingTests
{
    /// <summary>コードを展開する。</summary>
    /// <param name="code">展開するコード。</param>
    /// <param name="kept">両方の分岐を残すシンボル。</param>
    /// <returns>展開結果。</returns>
    private static PreprocessResult Preprocess(string code, params string[] kept)
    {
        PreprocessorOptions options = new()
        {
            BothBranchSymbols = [.. kept],
            DeclaredSymbols = [.. kept],
            HoistConditionalMacros = true,
        };

        return new HlslPreprocessor(options).Preprocess(SourceText.From(code, "test.hlsl"));
    }

    /// <summary>展開結果のトークンを 1 つの文字列にする。</summary>
    /// <param name="result">展開結果。</param>
    /// <returns>連ねた文字列。</returns>
    private static string TextOf(PreprocessResult result)
        => string.Join(" ", result.Tokens.Select(t => t.Text));

    /// <summary>そのテキストのトークンに付いている条件を返す。</summary>
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

        Assert.True(index >= 0, $"'{text}' が出力に無い: {TextOf(result)}");

        SymbolCondition condition = SymbolCondition.Always;

        foreach (ConditionalTokenRange range in result.ConditionalRegions)
        {
            if (index >= range.Start && index < range.Start + range.Length)
            {
                condition = condition.And(range.Condition);
            }
        }

        return condition;
    }

    [Fact]
    public void 条件で中身が変わるマクロを使う文を定義ごとに複製する()
    {
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define CTYPE float3
            #else
            #define CTYPE float4
            #endif

            CTYPE color;
            """,
            "_A");

        string text = TextOf(result);

        Assert.Contains("float3 color", text, StringComparison.Ordinal);
        Assert.Contains("float4 color", text, StringComparison.Ordinal);

        Assert.Equal(SymbolCondition.Symbol("_A", true), ConditionOf(result, "float3"));
        Assert.Equal(SymbolCondition.Symbol("_A", false), ConditionOf(result, "float4"));
    }

    [Fact]
    public void 関数形式マクロも引数ごと複製する()
    {
        // 引数は文の単位の中にある。分岐ごとに展開し直せば、その定義の引数として読まれる。
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define PACK(x, y) float2(x, y)
            #else
            #define PACK(x, y) float4(x, y, 0, 0)
            #endif

            float4 packed = PACK(1, 2);
            """,
            "_A");

        string text = TextOf(result);

        Assert.Contains("float2 ( 1 , 2 )", text, StringComparison.Ordinal);
        Assert.Contains("float4 ( 1 , 2 , 0 , 0 )", text, StringComparison.Ordinal);

        Assert.Equal(SymbolCondition.Symbol("_A", true), ConditionOf(result, "float2"));
    }

    [Fact]
    public void 引数の数が違う関数形式マクロも複製する()
    {
        // 同じ呼び出しが、定義によって別の数の引数として読まれることはない。
        // 引数の読み取りごと分岐しているので、それぞれの定義の数で読まれる。
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define SAMPLE(tex, uv) tex.Sample(uv)
            #else
            #define SAMPLE(tex, uv) tex.Load(uv)
            #endif

            float4 c = SAMPLE(_MainTex, uv);
            """,
            "_A");

        string text = TextOf(result);

        Assert.Contains("_MainTex . Sample ( uv )", text, StringComparison.Ordinal);
        Assert.Contains("_MainTex . Load ( uv )", text, StringComparison.Ordinal);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void 呼び出しの形でなければ関数形式マクロは複製しない()
    {
        // 関数形式マクロは ( が続かなければ展開されない。
        // どの分岐も同じトークン列になるので、複製すると同じ文が並ぶだけになる。
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define PACK(x) float2(x, 0)
            #else
            #define PACK(x) float4(x, 0, 0, 0)
            #endif

            float PACK;
            """,
            "_A");

        Assert.Equal("float PACK ;", TextOf(result).Trim());
    }

    [Fact]
    public void マクロが波括弧を開く形も切れ目を読み直して複製する()
    {
        // 本体が波括弧を開いたまま終わるマクロでは、展開前のトークンを数えても切れ目が合わない。
        // 開く側はマクロの中にあり、閉じる側だけが数に入るためである。
        // 最初の候補で文にならなければ、次の候補で読み直す。
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define BEGIN struct Packed {
            #else
            #define BEGIN struct Packed { float pad;
            #endif

            BEGIN
                float4 value;
            };
            """,
            "_A");

        string text = TextOf(result);

        Assert.Contains("struct Packed { float4 value ; } ;", text, StringComparison.Ordinal);
        Assert.Contains("struct Packed { float pad ; float4 value ; } ;", text, StringComparison.Ordinal);

        Assert.True(
            result.Tokens.Count(t => t.Kind == HlslSyntaxKind.OpenBraceToken)
                == result.Tokens.Count(t => t.Kind == HlslSyntaxKind.CloseBraceToken),
            text);

        Assert.Equal(SymbolCondition.Symbol("_A", false), ConditionOf(result, "pad"));
    }

    [Fact]
    public void 括弧が釣り合っていても文にならない複製は取り消す()
    {
        // 片方の定義が空だと float v = ; になる。
        // 括弧は釣り合い、末尾も ; なので、記号を数えるだけでは通ってしまう。
        // 通すと 1 本の木に文にならない断片が並び、そこから先の構文解析が総崩れになる。
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define VAL
            #else
            #define VAL 1
            #endif

            float v = VAL;
            """,
            "_A");

        Assert.Equal("float v = 1 ;", TextOf(result).Trim());
        Assert.Empty(result.ConditionalRegions);
    }

    [Fact]
    public void 間に指令がある文は複製しない()
    {
        // 複製すると指令を 2 度処理することになる。
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define CTYPE float3
            #else
            #define CTYPE float4
            #endif

            CTYPE color = CTYPE(
            #ifdef _B
                1, 2, 3
            #else
                0, 0, 0
            #endif
            );
            """,
            "_A");

        string text = TextOf(result);

        // 1 通りでしか展開していないこと。複製していれば float3 の分岐も出る。
        Assert.DoesNotContain("float3", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("color", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void 括弧の中の区切りは文の切れ目にしない()
    {
        // for (uint i = 0; i < N; i++) の ; を文の切れ目と見ると、複製した断片が文にならない。
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define COUNT 4
            #else
            #define COUNT 8
            #endif

            void main()
            {
                for (uint i = 0; i < COUNT; i++)
                {
                    acc += i;
                }
            }
            """,
            "_A");

        string text = TextOf(result);

        Assert.Contains("i < 4 ;", text, StringComparison.Ordinal);
        Assert.Contains("i < 8 ;", text, StringComparison.Ordinal);

        Assert.True(
            result.Tokens.Count(t => t.Kind == HlslSyntaxKind.OpenBraceToken)
                == result.Tokens.Count(t => t.Kind == HlslSyntaxKind.CloseBraceToken),
            text);
    }

    [Fact]
    public void 別のマクロの本体を通して使っていても定義ごとに複製する()
    {
        // APPLY 自体は条件で中身が変わらないが、展開すると BODY になる。
        // APPLY の位置で BODY の定義ごとに複製しなければ、1 つの構成の値でしか展開されない。
        PreprocessResult result = Preprocess(
            """
            #ifdef _A
            #define BODY { v = 1; }
            #else
            #define BODY v = 2;
            #endif
            #define APPLY BODY

            void F(inout float v)
            {
                if (v > 0)
                    APPLY
                v = 3;
            }
            """,
            "_A");

        string text = TextOf(result);

        Assert.Contains("if ( v > 0 ) { v = 1 ; }", text, StringComparison.Ordinal);
        Assert.Contains("if ( v > 0 ) v = 2 ;", text, StringComparison.Ordinal);
        Assert.Contains("_A", result.MergedSymbols);
        Assert.DoesNotContain("_A", result.DeclinedBothBranchSymbols);
    }

    [Fact]
    public void 読み飛ばした分岐でundefしてから定義し直しても再定義と報告しない()
    {
        // _A の分岐は既定の構成では読み飛ばすが、#undef も #define と同じく別の構成では効いている。
        // #undef を見ずに #define だけを覚えると、「#undef してから定義してください」と誤って報告する。
        PreprocessResult result = Preprocess(
            """
            #define CTYPE float4
            #ifdef _A
            #undef CTYPE
            #define CTYPE float3
            #endif

            CTYPE color;
            """,
            "_A");

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "HL0002");
    }

    [Fact]
    public void 無効にすれば1通りでしか展開しない()
    {
        PreprocessorOptions options = new()
        {
            BothBranchSymbols = ["_A"],
            DeclaredSymbols = ["_A"],
            HoistConditionalMacros = false,
        };

        PreprocessResult result = new HlslPreprocessor(options).Preprocess(
            SourceText.From(
                """
                #ifdef _A
                #define CTYPE float3
                #else
                #define CTYPE float4
                #endif

                CTYPE color;
                """,
                "test.hlsl"));

        Assert.DoesNotContain("float3", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public void 取り込みを切り替える領域も条件ごとに処理する()
    {
        PreprocessorOptions options = new()
        {
            BothBranchSymbols = ["_A"],
            DeclaredSymbols = ["_A"],
            MergeSwitchedIncludes = true,
            IncludeResolver = new DictionaryIncludeResolver(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a.hlsl"] = "float4 fromA;",
                ["b.hlsl"] = "float4 fromB;",
            }),
        };

        PreprocessResult result = new HlslPreprocessor(options).Preprocess(
            SourceText.From(
                """
                #ifdef _A
                #include "a.hlsl"
                #else
                #include "b.hlsl"
                #endif
                """,
                "test.hlsl"));

        Assert.Contains("fromA", TextOf(result), StringComparison.Ordinal);
        Assert.Contains("fromB", TextOf(result), StringComparison.Ordinal);

        Assert.Equal(SymbolCondition.Symbol("_A", true), ConditionOf(result, "fromA"));
        Assert.Equal(SymbolCondition.Symbol("_A", false), ConditionOf(result, "fromB"));
    }

    [Fact]
    public void 取り込みの切り替えを並べなければ片方しか残らない()
    {
        PreprocessorOptions options = new()
        {
            BothBranchSymbols = ["_A"],
            DeclaredSymbols = ["_A"],
            MergeSwitchedIncludes = false,
            IncludeResolver = new DictionaryIncludeResolver(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a.hlsl"] = "float4 fromA;",
                ["b.hlsl"] = "float4 fromB;",
            }),
        };

        PreprocessResult result = new HlslPreprocessor(options).Preprocess(
            SourceText.From(
                """
                #ifdef _A
                #include "a.hlsl"
                #else
                #include "b.hlsl"
                #endif
                """,
                "test.hlsl"));

        Assert.DoesNotContain("fromA", TextOf(result), StringComparison.Ordinal);
        Assert.Contains("_A", result.DeclinedBothBranchSymbols);
    }

    [Fact]
    public void 切り替えたヘッダが定義したマクロをコードで使えば並べ直しを求める()
    {
        // ヘッダの中身は、並べるかを決める時点では読んでいない。
        // 定義したマクロがコードとして展開されたら、そのシンボルは並べずに展開し直す。
        PreprocessorOptions options = new()
        {
            BothBranchSymbols = ["_A"],
            DeclaredSymbols = ["_A"],
            MergeSwitchedIncludes = true,
            IncludeResolver = new DictionaryIncludeResolver(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a.hlsl"] = "#define VALUE_TYPE float3",
                ["b.hlsl"] = "#define VALUE_TYPE float4",
            }),
        };

        PreprocessResult result = new HlslPreprocessor(options).Preprocess(
            SourceText.From(
                """
                #ifdef _A
                #include "a.hlsl"
                #else
                #include "b.hlsl"
                #endif

                VALUE_TYPE value;
                """,
                "test.hlsl"));

        Assert.Contains("_A", result.MergedMacroConflicts);
    }

    /// <summary>辞書から取り込みを解決する。</summary>
    /// <param name="files">ファイル名と中身。</param>
    private sealed class DictionaryIncludeResolver(Dictionary<string, string> files) : IIncludeResolver
    {
        /// <inheritdoc />
        public bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved)
        {
            if (files.TryGetValue(path, out string? content))
            {
                resolved = SourceText.From(content, path);
                return true;
            }

            resolved = null;
            return false;
        }
    }
}
