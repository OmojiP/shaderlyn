using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// 取り込みの展開結果を使い回す仕組みの検証。
/// </summary>
/// <remarks>
/// <b>誤って使い回すと、症状は「違う展開結果が返る」ことだけになる。</b>
/// 構文エラーにも例外にもならず、指摘だけが変わる。
/// だからここでは「使い回した結果」と「使い回さない結果」を
/// 突き合わせる形で確かめる。
/// </remarks>
public sealed class HlslIncludeCacheTests
{
    /// <summary>ヘッダを 1 つ持つ解決器を作る。</summary>
    /// <param name="files">パスと中身。</param>
    /// <returns>解決器。</returns>
    private static InMemoryIncludeResolver Resolver(params (string Path, string Content)[] files)
        => new(files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal));

    /// <summary>展開する。</summary>
    /// <param name="code">展開するコード。</param>
    /// <param name="resolver">取り込みの解決器。</param>
    /// <param name="cache">使い回しのキャッシュ。</param>
    /// <param name="defines">定義済みマクロ。</param>
    /// <returns>展開結果。</returns>
    private static PreprocessResult Preprocess(
        string code,
        InMemoryIncludeResolver resolver,
        HlslIncludeCache? cache,
        params (string Name, string Value)[] defines)
    {
        PreprocessorOptions options = new()
        {
            IncludeResolver = resolver,
            IncludeCache = cache,
            PredefinedMacros = defines.ToImmutableDictionary(
                d => d.Name, d => d.Value, StringComparer.Ordinal),
        };

        return new HlslPreprocessor(options).Preprocess(SourceText.From(code, "test.hlsl"));
    }

    /// <summary>展開結果をトークンの並びとして表す。</summary>
    /// <param name="result">展開結果。</param>
    /// <returns>連ねた文字列。</returns>
    private static string TextOf(PreprocessResult result)
        => string.Join(" ", result.Tokens.Select(t => t.Text));

    [Fact]
    public void 同じ状態で取り込んだら使い回す()
    {
        HlslIncludeCache cache = new();
        InMemoryIncludeResolver resolver = Resolver(("h.hlsl", "float4 _FromHeader;"));

        const string Code = """
            #include "h.hlsl"
            float4 _Own;
            """;

        string first = TextOf(Preprocess(Code, resolver, cache));
        string second = TextOf(Preprocess(Code, resolver, cache));

        Assert.Equal(first, second);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void 使い回しても展開結果は同じである()
    {
        InMemoryIncludeResolver resolver = Resolver(
            ("outer.hlsl", "#include \"inner.hlsl\"\n#define FROM_OUTER 1\nfloat4 _Outer;"),
            ("inner.hlsl", "#pragma multi_compile _ _KEYWORD\nfloat4 _Inner;"));

        const string Code = """
            #include "outer.hlsl"
            #ifdef FROM_OUTER
            float4 _Guarded;
            #endif
            """;

        HlslIncludeCache cache = new();

        PreprocessResult withoutCache = Preprocess(Code, resolver, cache: null);
        _ = Preprocess(Code, resolver, cache);            // 覚えさせる
        PreprocessResult reused = Preprocess(Code, resolver, cache);

        Assert.Equal(TextOf(withoutCache), TextOf(reused));
        Assert.Equal(withoutCache.Pragmas.Length, reused.Pragmas.Length);
        Assert.Equal(withoutCache.Macros.Count, reused.Macros.Count);
        Assert.Equal(
            string.Join(",", withoutCache.ResolvedIncludes.Select(p => "[" + p + "]")),
            string.Join(",", reused.ResolvedIncludes.Select(p => "[" + p + "]")));
    }

    [Fact]
    public void マクロの状態が違えば使い回さない()
    {
        // ヘッダが呼び出し側のマクロを見ている。状態が違えば結果も違う。
        HlslIncludeCache cache = new();
        InMemoryIncludeResolver resolver = Resolver(
            ("h.hlsl", "#ifdef ON\nfloat4 _WhenOn;\n#else\nfloat4 _WhenOff;\n#endif"));

        const string Code = "#include \"h.hlsl\"";

        string off = TextOf(Preprocess(Code, resolver, cache));
        string on = TextOf(Preprocess(Code, resolver, cache, ("ON", "1")));

        Assert.Contains("_WhenOff", off, StringComparison.Ordinal);
        Assert.Contains("_WhenOn", on, StringComparison.Ordinal);
        Assert.Equal(2, cache.Misses);
    }

    /// <summary>
    /// ヘッダが読まない名前だけが違うなら、展開結果を使い回すことを確かめる。
    /// </summary>
    /// <remarks>
    /// バリアントごとの展開は <c>#define _KEYWORD 1</c> だけが違う。
    /// そのキーワードを見ないヘッダまで展開し直していたのが、展開の大半だった。
    /// </remarks>
    [Fact]
    public void ヘッダが読まない名前だけが違うなら使い回す()
    {
        HlslIncludeCache cache = new();
        InMemoryIncludeResolver resolver = Resolver(
            ("h.hlsl", "#ifdef ON\nfloat4 _WhenOn;\n#else\nfloat4 _WhenOff;\n#endif"));

        const string Code = "#include \"h.hlsl\"";

        _ = Preprocess(Code, resolver, cache, ("ON", "1"));
        PreprocessResult reused = Preprocess(Code, resolver, cache, ("ON", "1"), ("_UNRELATED", "1"));
        PreprocessResult fresh = Preprocess(Code, resolver, cache: null, ("ON", "1"), ("_UNRELATED", "1"));

        Assert.Equal(1, cache.ReadHits);
        Assert.Equal(TextOf(fresh), TextOf(reused));
    }

    /// <summary>
    /// 使い回した内側のヘッダが読んだ名前も、外側のヘッダの鍵に入ることを確かめる。
    /// </summary>
    /// <remarks>
    /// 使い回した中身は展開し直さないので、読みは表から知らされない。
    /// 足し忘れると、外側は <c>ON</c> を読んでいないことになり、
    /// <c>ON</c> だけが違う状態で外側ごと使い回して <c>_WhenOff</c> を返す。
    /// </remarks>
    [Fact]
    public void 使い回した内側が読んだ名前は外側の鍵にも入る()
    {
        HlslIncludeCache cache = new();
        InMemoryIncludeResolver resolver = Resolver(
            ("inner.hlsl", "#ifdef ON\nfloat4 _WhenOn;\n#else\nfloat4 _WhenOff;\n#endif"),
            ("outer.hlsl", "#include \"inner.hlsl\"\nfloat4 _Outer;"));

        // 内側を先に覚えさせ、外側の展開の中で使い回させる。
        _ = Preprocess("#include \"inner.hlsl\"\n#include \"outer.hlsl\"", resolver, cache);

        PreprocessResult reused = Preprocess("#include \"outer.hlsl\"", resolver, cache, ("ON", "1"));

        Assert.Contains("_WhenOn", TextOf(reused), StringComparison.Ordinal);
        Assert.DoesNotContain("_WhenOff", TextOf(reused), StringComparison.Ordinal);
    }

    /// <summary>
    /// ヘッダが自分で定義してから読んだ名前は、外の状態に左右されないことを確かめる。
    /// </summary>
    [Fact]
    public void ヘッダが定義してから読んだ名前の外の状態は問わない()
    {
        HlslIncludeCache cache = new();
        InMemoryIncludeResolver resolver = Resolver(
            ("h.hlsl", "#undef MODE\n#define MODE 1\n#if MODE\nfloat4 _Mode;\n#endif"));

        const string Code = "#include \"h.hlsl\"\nfloat4 _X = MODE;";

        _ = Preprocess(Code, resolver, cache);
        PreprocessResult reused = Preprocess(Code, resolver, cache, ("MODE", "0"));
        PreprocessResult fresh = Preprocess(Code, resolver, cache: null, ("MODE", "0"));

        Assert.Equal(TextOf(fresh), TextOf(reused));
    }

    [Fact]
    public void ヘッダが定義したマクロは使い回した側にも効く()
    {
        HlslIncludeCache cache = new();
        InMemoryIncludeResolver resolver = Resolver(("h.hlsl", "#define VALUE 42"));

        const string Code = """
            #include "h.hlsl"
            float4 _X = VALUE;
            """;

        _ = Preprocess(Code, resolver, cache);
        PreprocessResult reused = Preprocess(Code, resolver, cache);

        Assert.Equal(1, cache.Hits);
        Assert.Contains("42", TextOf(reused), StringComparison.Ordinal);
    }

    [Fact]
    public void 取り込みの中の診断も使い回した側に出る()
    {
        HlslIncludeCache cache = new();
        InMemoryIncludeResolver resolver = Resolver(("h.hlsl", "#unknown-directive"));

        const string Code = "#include \"h.hlsl\"";

        int first = Preprocess(Code, resolver, cache).Diagnostics.Length;
        int second = Preprocess(Code, resolver, cache).Diagnostics.Length;

        Assert.True(first > 0, "取り込んだヘッダの誤りが報告されていない");
        Assert.Equal(first, second);
    }

    /// <summary>
    /// 記録の中身が、そのヘッダより前に何を見たかで変わらないことを確かめる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>これを取り違えると、実行するたびに指摘が変わる。</b>
    /// 記録を「全体の集合に新しいときだけ」書き足すと、
    /// 記録の中身が手前の経路に依存する。取り込みの手前の状態は鍵に入っていないので、
    /// 同じ鍵で中身の違う記録ができ、どちらが先に記録されたかで結果が変わる。
    /// 並列に解析すると、その順序は実行のたびに変わる。
    /// </para>
    /// <para>
    /// Unity 同梱の <c>RestirSpatial.compute</c> で実際に起きていた。
    /// <c>RingBuffer</c> が「どこにも宣言されていません」(HL0310) と、
    /// 出たり出なかったりしていた。
    /// </para>
    /// </remarks>
    [Fact]
    public void 記録の中身は手前に何を見たかで変わらない()
    {
        HlslIncludeCache cache = new();
        InMemoryIncludeResolver resolver = Resolver(
            ("h.hlsl", "#ifndef H\n#define H\nfloat _InsideHeader;\n#endif\n"));

        // 手前の非活性領域で同じ名前を見ている経路。
        // 2 度目の取り込みではヘッダの中身が飛ばされ、その名前が記録される。
        const string Seen = """
            #if 0
            float _InsideHeader;
            #endif
            #include "h.hlsl"
            #include "h.hlsl"
            """;

        // 手前で見ていない経路。2 度目の取り込みの鍵は上と同じになる。
        const string Unseen = """
            #include "h.hlsl"
            #include "h.hlsl"
            """;

        _ = Preprocess(Seen, resolver, cache);
        PreprocessResult unseen = Preprocess(Unseen, resolver, cache);

        Assert.Contains("_InsideHeader", unseen.SkippedIdentifiers);
        AssertSameAsFresh(resolver, WarmUp: Seen, Target: Unseen);
    }

    /// <summary>
    /// 取り込みの解決状況が、手前に何を取り込んだかで変わらないことを確かめる。
    /// </summary>
    /// <remarks>
    /// <b>これは <c>SkippedIdentifiers</c> より影響が広い。</b>
    /// 解決できた取り込みの一覧は、実行のあいだで結果を持ち越す鍵
    /// (<c>--cache</c>) にも入る。取りこぼすと、ヘッダを直しても
    /// 古い結果が返ってくることになる。
    /// </remarks>
    [Fact]
    public void 解決できた取り込みは手前に何を取り込んだかで変わらない()
    {
        // inner.hlsl はマクロを定義しない。
        // 先に取り込んでもマクロ表は変わらず、outer.hlsl の鍵は同じになる。
        InMemoryIncludeResolver resolver = Resolver(
            ("inner.hlsl", "float _Inner;"),
            ("outer.hlsl", "#include \"inner.hlsl\"\nfloat _Outer;"));

        AssertSameAsFresh(
            resolver,
            WarmUp: "#include \"inner.hlsl\"\n#include \"outer.hlsl\"",
            Target: "#include \"outer.hlsl\"");
    }

    /// <summary>
    /// 解決できなかった取り込みが、手前の経路で消えないことを確かめる。
    /// </summary>
    /// <remarks>
    /// <b>取りこぼすと「依存が揃っている」と誤って判断する。</b>
    /// 揃っていない前提で報告しないはずのルールが動いてしまう。
    /// </remarks>
    [Fact]
    public void 解決できなかった取り込みは手前の経路で消えない()
    {
        InMemoryIncludeResolver resolver = Resolver(
            ("outer.hlsl", "#include \"missing.hlsl\"\nfloat _Outer;"));

        AssertSameAsFresh(
            resolver,
            WarmUp: "#include \"missing.hlsl\"\n#include \"outer.hlsl\"",
            Target: "#include \"outer.hlsl\"");
    }

    /// <summary>
    /// 取り込む順序を変えても、使い回した結果が変わらないことを確かめる。
    /// </summary>
    /// <remarks>
    /// <b>並列に解析すると、どちらが先にキャッシュへ入るかは実行のたびに変わる。</b>
    /// 順序で結果が変わるなら、それは実行のたびに結果が変わるということである。
    /// </remarks>
    [Fact]
    public void 覚える順序を変えても結果が変わらない()
    {
        // inner.hlsl はマクロを定義しないので、先に取り込んでも
        // outer.hlsl を取り込む時点のマクロ表は変わらない。鍵は同じになる。
        InMemoryIncludeResolver resolver = Resolver(
            ("inner.hlsl", "float _Inner;"),
            ("outer.hlsl", "#include \"inner.hlsl\"\nfloat _Outer;"));

        const string WithInnerFirst = "#include \"inner.hlsl\"\n#include \"outer.hlsl\"";
        const string OuterOnly = "#include \"outer.hlsl\"";

        // 先に「inner を取り込み済みの経路」で覚えた場合。
        HlslIncludeCache innerFirst = new();
        _ = Preprocess(WithInnerFirst, resolver, innerFirst);
        PreprocessResult afterInnerFirst = Preprocess(OuterOnly, resolver, innerFirst);

        // 先に「outer だけの経路」で覚えた場合。
        HlslIncludeCache outerFirst = new();
        _ = Preprocess(OuterOnly, resolver, outerFirst);
        PreprocessResult afterOuterFirst = Preprocess(OuterOnly, resolver, outerFirst);

        Assert.True(innerFirst.Hits > 0 && outerFirst.Hits > 0, "どちらも使い回していない");

        Assert.Equal(TextOf(afterOuterFirst), TextOf(afterInnerFirst));
        Assert.Equal(
            afterOuterFirst.ResolvedIncludes.Order().ToArray(),
            afterInnerFirst.ResolvedIncludes.Order().ToArray());
        Assert.Equal(
            afterOuterFirst.SkippedIdentifiers.Order().ToArray(),
            afterInnerFirst.SkippedIdentifiers.Order().ToArray());
    }

    /// <summary>
    /// 使い回した結果が、使い回さない結果と一致することを確かめる。
    /// </summary>
    /// <param name="resolver">取り込みの解決器。</param>
    /// <param name="WarmUp">先にキャッシュを埋める側のコード。</param>
    /// <param name="Target">使い回す側のコード。</param>
    /// <remarks>
    /// <b>使い回さない結果を正解とする。</b>
    /// 記録の中身がずれていても、構文エラーにも例外にもならない。
    /// 突き合わせる相手を用意しないと気づけない。
    /// </remarks>
    private static void AssertSameAsFresh(
        InMemoryIncludeResolver resolver,
        string WarmUp,
        string Target)
    {
        HlslIncludeCache cache = new();

        _ = Preprocess(WarmUp, resolver, cache);

        PreprocessResult reused = Preprocess(Target, resolver, cache);
        PreprocessResult fresh = Preprocess(Target, resolver, cache: null);

        Assert.True(cache.Hits > 0, "使い回していないので、この検証は何も確かめていない");

        Assert.Equal(TextOf(fresh), TextOf(reused));

        Assert.Equal(
            fresh.SkippedIdentifiers.Order().ToArray(),
            reused.SkippedIdentifiers.Order().ToArray());

        Assert.Equal(
            fresh.ResolvedIncludes.Order().ToArray(),
            reused.ResolvedIncludes.Order().ToArray());

        Assert.Equal(
            fresh.UnresolvedIncludes.Order().ToArray(),
            reused.UnresolvedIncludes.Order().ToArray());

        Assert.Equal(
            fresh.DeclinedBothBranchSymbols.Order().ToArray(),
            reused.DeclinedBothBranchSymbols.Order().ToArray());

        Assert.Equal(
            fresh.Macros.Keys.Order().ToArray(),
            reused.Macros.Keys.Order().ToArray());

        Assert.Equal(fresh.Diagnostics.Length, reused.Diagnostics.Length);
    }
}
