using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// プリプロセッサの検証を短く書くための補助。
/// </summary>
internal static class PreprocessorHarness
{
    /// <summary>プリプロセスして結果を返す。</summary>
    public static PreprocessResult Run(string content, PreprocessorOptions? options = null)
        => new HlslPreprocessor(options).Preprocess(SourceText.From(content, "test.hlsl"));

    /// <summary>プリプロセス結果のトークンを空白区切りの文字列にする。</summary>
    public static string Text(string content, PreprocessorOptions? options = null)
        => Join(Run(content, options).Tokens);

    /// <summary>トークン列を空白区切りの文字列にする。</summary>
    public static string Join(ImmutableArray<HlslSyntaxToken> tokens)
        => string.Join(" ", tokens.Where(t => t.Kind != HlslSyntaxKind.EndOfFileToken).Select(t => t.Text));

    /// <summary>診断メッセージを連結する。デバッグ表示用。</summary>
    public static string Describe(PreprocessResult result)
        => string.Join("\n", result.Diagnostics.Select(d => $"  {d.Id}: {d.GetMessage()}"));

    /// <summary>診断が 1 件も無いことを確認したうえでトークンを文字列にする。</summary>
    public static string TextWithoutDiagnostics(string content, PreprocessorOptions? options = null)
    {
        PreprocessResult result = Run(content, options);
        Assert.True(result.Diagnostics.IsEmpty, $"予期しない診断:\n{Describe(result)}");
        return Join(result.Tokens);
    }
}

public sealed class MacroExpansionTests
{
    [Fact]
    public void オブジェクト形式マクロを展開する()
    {
        Assert.Equal("1", PreprocessorHarness.TextWithoutDiagnostics("""
            #define ONE 1
            ONE
            """));
    }

    [Fact]
    public void 関数形式マクロを展開する()
    {
        // Unity のシェーダーライブラリが依存する最も基本的な形。
        Assert.Equal("Texture2D _BaseMap ;", PreprocessorHarness.TextWithoutDiagnostics("""
            #define TEXTURE2D(name) Texture2D name
            TEXTURE2D(_BaseMap);
            """));
    }

    [Fact]
    public void 複数行にわたるマクロ定義を扱える()
    {
        // 行継続を改行として扱うと本体が 1 行目で切れる。
        // Unity のシェーダーライブラリは長いマクロを多用するため、ここを誤ると大半が壊れる。
        Assert.Equal("struct Foo { float4 a ; float4 b ; } ;", PreprocessorHarness.TextWithoutDiagnostics("""
            #define DECLARE(name) \
                struct name       \
                {                 \
                    float4 a;     \
                    float4 b;     \
                }
            DECLARE(Foo);
            """));
    }

    [Fact]
    public void 入れ子の括弧を含む実引数を一つの引数として扱う()
    {
        // FOO(float2(1, 2)) の引数は 1 つである。深さを数えないと 2 つに割れる。
        Assert.Equal("float2 ( 1 , 2 )", PreprocessorHarness.TextWithoutDiagnostics("""
            #define IDENTITY(x) x
            IDENTITY(float2(1, 2))
            """));
    }

    [Fact]
    public void マクロを入れ子に展開する()
    {
        Assert.Equal("1", PreprocessorHarness.TextWithoutDiagnostics("""
            #define A B
            #define B C
            #define C 1
            A
            """));
    }

    [Fact]
    public void 自己再帰するマクロで無限展開しない()
    {
        // 展開中のマクロを再度展開しないことで止める。
        Assert.Equal("A", PreprocessorHarness.TextWithoutDiagnostics("""
            #define A A
            A
            """));
    }

    [Fact]
    public void 相互再帰するマクロで無限展開しない()
    {
        PreprocessResult result = PreprocessorHarness.Run("""
            #define A B
            #define B A
            A
            """);

        // 停止することが要件であり、結果の値は問わない。
        Assert.NotEmpty(result.Tokens);
    }

    [Fact]
    public void 関数形式マクロは開き括弧が無ければ展開しない()
    {
        // C の規則。マクロ名を単なる識別子として参照するコードを壊さないために必要。
        Assert.Equal("FOO ;", PreprocessorHarness.TextWithoutDiagnostics("""
            #define FOO(x) x
            FOO;
            """));
    }

    [Fact]
    public void 実引数はマクロ展開してから埋め込む()
    {
        Assert.Equal("1", PreprocessorHarness.TextWithoutDiagnostics("""
            #define ONE 1
            #define IDENTITY(x) x
            IDENTITY(ONE)
            """));
    }

    [Fact]
    public void 文字列化演算子を扱える()
    {
        Assert.Equal("\"abc\"", PreprocessorHarness.TextWithoutDiagnostics("""
            #define STRINGIZE(x) #x
            STRINGIZE(abc)
            """));
    }

    [Fact]
    public void トークン連結演算子を扱える()
    {
        // a ## b が識別子 ab になるのは、融合結果を字句解析し直しているため。
        Assert.Equal("ab", PreprocessorHarness.TextWithoutDiagnostics("""
            #define PASTE(x, y) x ## y
            PASTE(a, b)
            """));
    }

    [Fact]
    public void 連なったトークン連結演算子を扱える()
    {
        // a ## b ## c のように連結が連なる形。1 回分だけ処理して抜けると、
        // 2 つ目の ## が演算子のまま出力へ漏れ、展開結果が構文として成立しなくなる。
        //
        // Unity の Common.hlsl に
        // #define FRAMEBUFFER_INPUT_FLOAT(idx) ... float4 _UnityFBInput##idx##_TexelSize
        // という定義が実在し、これを扱えないと該当のシェーダーが丸ごと解析できない。
        Assert.Equal("_Tex0_TexelSize", PreprocessorHarness.TextWithoutDiagnostics("""
            #define DECLARE(idx) _Tex##idx##_TexelSize
            DECLARE(0)
            """));
    }

    [Fact]
    public void 実引数の中の同名マクロは再帰とみなさない()
    {
        // C の規則では、マクロ名の展開が抑止されるのは自分自身の置換結果の中だけである。
        // 実引数は置換より前に展開されるため、そこに同じマクロが現れても再帰ではない。
        //
        // これを再帰とみなすと、遅延連結の定番の書き方が成立しなくなる。
        // Unity の HDRP には
        // CALL_MERGE_NAME(CALL_MERGE_NAME(sampler, name), 0)
        // という入れ子の呼び出しが実在する。
        Assert.Equal("sampler_BaseMap0", PreprocessorHarness.TextWithoutDiagnostics("""
            #define MERGE_NAME(X, Y) X##Y
            #define CALL_MERGE_NAME(X, Y) MERGE_NAME(X, Y)
            CALL_MERGE_NAME(CALL_MERGE_NAME(sampler, _BaseMap), 0)
            """));
    }

    [Fact]
    public void 自分自身を含むマクロは無限展開しない()
    {
        // 実引数の扱いを緩めた結果、置換結果の中での抑止まで効かなくなっていないことを確かめる。
        Assert.Equal("SELF", PreprocessorHarness.TextWithoutDiagnostics("""
            #define SELF SELF
            SELF
            """));
    }

    [Fact]
    public void 可変長引数マクロを扱える()
    {
        Assert.Equal("f ( 1 , 2 , 3 )", PreprocessorHarness.TextWithoutDiagnostics("""
            #define CALL(fn, ...) fn(__VA_ARGS__)
            CALL(f, 1, 2, 3)
            """));
    }

    [Fact]
    public void 引数の個数が合わないと報告する()
    {
        PreprocessResult result = PreprocessorHarness.Run("""
            #define TWO(a, b) a b
            TWO(1)
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "HL0002");
    }

    [Fact]
    public void オブジェクト形式と関数形式は括弧の直前の空白で区別される()
    {
        // #define A(x) x は関数形式、#define A (x) x はオブジェクト形式で本体が (x) x になる。
        Assert.Equal("( x ) x", PreprocessorHarness.TextWithoutDiagnostics("""
            #define A (x) x
            A
            """));
    }

    [Fact]
    public void undefでマクロを解除できる()
    {
        Assert.Equal("A", PreprocessorHarness.TextWithoutDiagnostics("""
            #define A 1
            #undef A
            A
            """));
    }

    [Fact]
    public void 設定で与えた定義済みマクロが効く()
    {
        PreprocessorOptions options = new()
        {
            PredefinedMacros = ImmutableDictionary<string, string>.Empty.Add("SHADER_API_D3D11", "1"),
        };

        Assert.Equal("yes", PreprocessorHarness.TextWithoutDiagnostics("""
            #if defined(SHADER_API_D3D11)
            yes
            #else
            no
            #endif
            """, options));
    }

    [Fact]
    public void マクロ展開後のトークンは呼び出し位置を指す()
    {
        // 診断はコードを書いた場所に出るべきである。
        // マクロ定義の中を指しても利用者は直しようがない。
        PreprocessResult result = PreprocessorHarness.Run("""
            #define BAD_VALUE 42
            BAD_VALUE
            """);

        HlslSyntaxToken token = Assert.Single(
            result.Tokens.Where(t => t.Kind == HlslSyntaxKind.NumericLiteralToken));

        // 2 行目 (0 始まりで 1) にある使用箇所を指していること。
        Assert.Equal(1, token.Source.GetLinePosition(token.Span.Start).Line);
    }
}

public sealed class ConditionalCompilationTests
{
    [Theory]
    [InlineData("#if 1", "yes")]
    [InlineData("#if 0", "no")]
    [InlineData("#if 1 + 1 == 2", "yes")]
    [InlineData("#if 1 > 2", "no")]
    [InlineData("#if (1 && 0) || 1", "yes")]
    [InlineData("#if !0", "yes")]
    [InlineData("#if 0x10 == 16", "yes")]
    [InlineData("#if 1 ? 1 : 0", "yes")]
    [InlineData("#if UNDEFINED_MACRO", "no")]
    public void 条件式を評価できる(string directive, string expected)
    {
        Assert.Equal(expected, PreprocessorHarness.TextWithoutDiagnostics($"""
            {directive}
            yes
            #else
            no
            #endif
            """));
    }

    [Fact]
    public void 未定義のマクロは0として扱う()
    {
        // C の規則。未定義を誤りとすると条件付きコンパイルを使うコードが解析できない。
        Assert.Equal("no", PreprocessorHarness.TextWithoutDiagnostics("""
            #if SOME_UNDEFINED_THING
            yes
            #else
            no
            #endif
            """));
    }

    [Fact]
    public void definedはマクロ展開より先に処理される()
    {
        // 先に展開すると X が値へ置き換わり、defined が判定できなくなる。
        Assert.Equal("yes", PreprocessorHarness.TextWithoutDiagnostics("""
            #define X 0
            #if defined(X)
            yes
            #else
            no
            #endif
            """));
    }

    [Fact]
    public void ifdefとifndefを扱える()
    {
        Assert.Equal("defined notdefined", PreprocessorHarness.TextWithoutDiagnostics("""
            #define A
            #ifdef A
            defined
            #endif
            #ifndef B
            notdefined
            #endif
            """));
    }

    [Fact]
    public void elifの連鎖で最初に成立した分岐だけを採用する()
    {
        Assert.Equal("second", PreprocessorHarness.TextWithoutDiagnostics("""
            #if 0
            first
            #elif 1
            second
            #elif 1
            third
            #else
            fourth
            #endif
            """));
    }

    [Fact]
    public void 入れ子の条件分岐を扱える()
    {
        Assert.Equal("inner", PreprocessorHarness.TextWithoutDiagnostics("""
            #if 1
            #if 0
            outer
            #else
            inner
            #endif
            #endif
            """));
    }

    [Fact]
    public void 非活性な親の中の分岐は採用されない()
    {
        // 親が偽なら、子の条件が真でも出力してはならない。
        Assert.Equal("", PreprocessorHarness.TextWithoutDiagnostics("""
            #if 0
            #if 1
            should_not_appear
            #endif
            #endif
            """));
    }

    [Fact]
    public void 非活性領域のdefineは実行されない()
    {
        Assert.Equal("A", PreprocessorHarness.TextWithoutDiagnostics("""
            #if 0
            #define A 1
            #endif
            A
            """));
    }

    [Fact]
    public void 非活性領域の入れ子も正しく対応付ける()
    {
        // 非活性領域でも #if の入れ子を追わないと #endif の対応を見失う。
        Assert.Equal("after", PreprocessorHarness.TextWithoutDiagnostics("""
            #if 0
            #ifdef SOMETHING
            a
            #else
            b
            #endif
            #endif
            after
            """));
    }

    [Fact]
    public void 閉じられていない条件分岐を報告する()
    {
        PreprocessResult result = PreprocessorHarness.Run("""
            #if 1
            code
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "HL0002");
    }

    [Fact]
    public void 対応するifのないendifを報告する()
    {
        PreprocessResult result = PreprocessorHarness.Run("#endif");
        Assert.Contains(result.Diagnostics, d => d.Id == "HL0002");
    }

    [Fact]
    public void definedと開き括弧の間に空白があっても解釈できる()
    {
        // Unity のシェーダーライブラリは #if defined (SHADER_API_GAMECORE) のように
        // 空白を挟んで書いている箇所がある。
        Assert.Equal("yes", PreprocessorHarness.TextWithoutDiagnostics("""
            #define A 1
            #if defined (A)
            yes
            #else
            no
            #endif
            """));
    }

    [Fact]
    public void 長いelif連鎖から正しい分岐を選ぶ()
    {
        // Unity の Common.hlsl はグラフィックス API ごとの分岐を
        // 10 段以上の #elif で書いており、ここを誤ると
        // TEXTURE2D をはじめとするマクロがすべて未定義になる。
        PreprocessorOptions options = new()
        {
            PredefinedMacros = ImmutableDictionary<string, string>.Empty.Add("SHADER_API_D3D11", "1"),
        };

        Assert.Equal("d3d11", PreprocessorHarness.TextWithoutDiagnostics("""
            #if defined (SHADER_API_GAMECORE)
            gamecore
            #elif defined(SHADER_API_XBOXONE)
            xboxone
            #elif defined(SHADER_API_PS4)
            ps4
            #elif defined(SHADER_API_PS5)
            ps5
            #elif defined(SHADER_API_D3D11)
            d3d11
            #elif defined(SHADER_API_METAL)
            metal
            #else
            #error unsupported shader api
            #endif
            """, options));
    }

    [Fact]
    public void 分岐の中のdefineが呼び出し側に効く()
    {
        PreprocessorOptions options = new()
        {
            PredefinedMacros = ImmutableDictionary<string, string>.Empty.Add("SHADER_API_D3D11", "1"),
        };

        Assert.Equal("Texture2D _Tex ;", PreprocessorHarness.TextWithoutDiagnostics("""
            #if defined(SHADER_API_METAL)
            #define TEXTURE2D(name) MetalTexture name
            #elif defined(SHADER_API_D3D11)
            #define TEXTURE2D(name) Texture2D name
            #endif
            TEXTURE2D(_Tex);
            """, options));
    }

    [Fact]
    public void 条件式の0除算で例外を投げない()
    {
        PreprocessResult result = PreprocessorHarness.Run("""
            #if 1 / 0
            yes
            #endif
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "HL0002");
    }

    [Fact]
    public void ifdefの名前の後ろの記述を報告しdefinedの式への直しを添える()
    {
        // #ifdef A0 || A1 は #ifdef A0 と同じ意味で、A1 はどこにも効かない。
        PreprocessResult result = PreprocessorHarness.Run("""
            #ifdef A0 || A1
            yes
            #endif
            """);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "HL0004");

        Assert.Contains("'#if defined(A0) || defined(A1)'", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal("if defined(A0) || defined(A1)", diagnostic.SuggestedReplacement);

        // 直しは指令名から行の終わりまでを置き換える。
        Assert.Equal("ifdef A0 || A1".Length, diagnostic.Location.Span.Length);
    }

    [Fact]
    public void ifdefの後ろの記述は評価に使わない()
    {
        // コンパイラと同じく名前 1 つだけで決める。A1 だけが定義されていても分岐は外れる。
        PreprocessorOptions options = new()
        {
            PredefinedMacros = ImmutableDictionary<string, string>.Empty.Add("A1", "1"),
        };

        Assert.DoesNotContain(
            PreprocessorHarness.Run("""
                #ifdef A0 || A1
                yes
                #endif
                """, options).Tokens,
            t => t.Text == "yes");
    }

    [Theory]
    [InlineData("#ifndef A0 || A1", "!defined(A)")]
    [InlineData("#ifdef A0 A1", "defined(A) || defined(B)")]
    [InlineData("#ifdef A0 || 1", "defined(A) || defined(B)")]
    public void 意図を一意に読めない形には直しを添えず書き方の例だけを示す(string directive, string example)
    {
        // #ifndef A || B は「どちらも無い」のか「どちらかが無い」のかが読み取れない。
        PreprocessResult result = PreprocessorHarness.Run($"{directive}\nyes\n#endif");

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "HL0004");

        Assert.Null(diagnostic.SuggestedReplacement);
        Assert.Contains(example, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ifdefの名前が1つだけなら報告しない()
    {
        PreprocessResult result = PreprocessorHarness.Run("""
            #ifdef A0 // コメントは記述に数えない
            yes
            #endif
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "HL0004");
    }
}

public sealed class IncludeHandlingTests
{
    private static PreprocessorOptions WithFiles(params (string Path, string Content)[] files)
        => new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)),
        };

    [Fact]
    public void includeの内容を取り込む()
    {
        Assert.Equal("float4 a ;", PreprocessorHarness.TextWithoutDiagnostics(
            """
            #include "Common.hlsl"
            """,
            WithFiles(("Common.hlsl", "float4 a;"))));
    }

    [Fact]
    public void include先で定義したマクロが呼び出し側で使える()
    {
        Assert.Equal("Texture2D _BaseMap ;", PreprocessorHarness.TextWithoutDiagnostics(
            """
            #include "Macros.hlsl"
            TEXTURE2D(_BaseMap);
            """,
            WithFiles(("Macros.hlsl", "#define TEXTURE2D(name) Texture2D name"))));
    }

    [Fact]
    public void 山括弧のincludeを扱える()
    {
        Assert.Equal("int x ;", PreprocessorHarness.TextWithoutDiagnostics(
            "#include <Sub/Header.hlsl>",
            WithFiles(("Sub/Header.hlsl", "int x;"))));
    }

    [Fact]
    public void 入れ子のincludeを扱える()
    {
        Assert.Equal("deep", PreprocessorHarness.TextWithoutDiagnostics(
            """
            #include "A.hlsl"
            """,
            WithFiles(
                ("A.hlsl", "#include \"B.hlsl\""),
                ("B.hlsl", "deep"))));
    }

    [Fact]
    public void 循環includeを検出して停止する()
    {
        PreprocessResult result = new HlslPreprocessor(WithFiles(
                ("A.hlsl", "#include \"B.hlsl\""),
                ("B.hlsl", "#include \"A.hlsl\"")))
            .Preprocess(SourceText.From("#include \"A.hlsl\"", "test.hlsl"));

        Assert.Contains(result.Diagnostics, d => d.Id == "HL0320");
    }

    [Fact]
    public void インクルードガードがあれば二重取り込みしない()
    {
        Assert.Equal("once", PreprocessorHarness.TextWithoutDiagnostics(
            """
            #include "Guarded.hlsl"
            #include "Guarded.hlsl"
            """,
            WithFiles(("Guarded.hlsl", """
                #ifndef GUARDED_INCLUDED
                #define GUARDED_INCLUDED
                once
                #endif
                """))));
    }

    [Fact]
    public void 解決できないincludeは既定では報告しない()
    {
        // Unity 未インストールの環境ではパッケージ配下が軒並み解決できず、
        // 報告すると出力が埋まって他の指摘が読めなくなる。
        PreprocessResult result = PreprocessorHarness.Run(
            "#include \"Missing.hlsl\"", WithFiles());

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["Missing.hlsl"], result.UnresolvedIncludes.ToArray());
    }

    [Fact]
    public void 解決できないincludeを設定で報告できる()
    {
        PreprocessorOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(new Dictionary<string, string>()),
            ReportUnresolvedIncludes = true,
        };

        PreprocessResult result = PreprocessorHarness.Run("#include \"Missing.hlsl\"", options);
        Assert.Contains(result.Diagnostics, d => d.Id == "HL0321");
    }

    [Fact]
    public void 解決できたincludeを記録する()
    {
        PreprocessResult result = PreprocessorHarness.Run(
            "#include \"Common.hlsl\"",
            WithFiles(("Common.hlsl", "int x;")));

        Assert.Single(result.ResolvedIncludes);
    }
}

public sealed class PragmaHandlingTests
{
    [Fact]
    public void pragmaを記録して出力からは取り除く()
    {
        // #pragma vertex はルールが必要とするため捨ててはならないが、
        // 構文解析の入力に混ざってもいけない。
        PreprocessResult result = PreprocessorHarness.Run("""
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            int x;
            """);

        Assert.Equal("int x ;", PreprocessorHarness.Join(result.Tokens));
        Assert.Equal(["vertex", "fragment", "target"], result.Pragmas.Select(p => p.Name));
        Assert.Equal("vert", Assert.Single(result.Pragmas[0].Arguments).Text);
    }

    [Fact]
    public void 非活性領域のpragmaは記録しない()
    {
        PreprocessResult result = PreprocessorHarness.Run("""
            #if 0
            #pragma vertex notUsed
            #endif
            #pragma vertex used
            """);

        PragmaDirective pragma = Assert.Single(result.Pragmas);
        Assert.Equal("used", Assert.Single(pragma.Arguments).Text);
    }
}
