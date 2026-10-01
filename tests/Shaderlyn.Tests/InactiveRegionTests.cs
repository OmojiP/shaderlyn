using System.Text.Json;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;
using LspDocumentUri = Shaderlyn.LanguageServer.DocumentUri;

namespace Shaderlyn.Tests;

/// <summary>
/// 条件が外れて効いていない範囲の検証。
/// </summary>
/// <remarks>
/// エディタはこの範囲を薄く表示する。効いているコードを薄くすると、
/// 出荷されるコードが「直しても意味が無いもの」に見えてしまう。
/// </remarks>
public sealed class InactiveRegionTests
{
    /// <summary>プリプロセスして、非活性の分岐を行の範囲 (始まり, 終わりの次) で返す。</summary>
    private static (int Start, int End)[] InactiveLines(string source)
        => [.. PreprocessorHarness.Run(source).InactiveRegions
            .Select(r => (r.Location.LineSpan.Start.Line + 1, r.Location.LineSpan.End.Line))];

    [Fact]
    public void 定義されていないシンボルの分岐を記録する()
    {
        Assert.Equal([(1, 2)], InactiveLines("""
            #ifdef FOO
            int a;
            #else
            int b;
            #endif
            """));
    }

    [Fact]
    public void 定義されているシンボルではelseの側を記録する()
    {
        Assert.Equal([(4, 5)], InactiveLines("""
            #define FOO
            #ifdef FOO
            int a;
            #else
            int b;
            #endif
            """));
    }

    [Fact]
    public void 外側が外れていれば内側の分岐は記録しない()
    {
        // 外側の範囲が丸ごと含んでいる。重ねて記録すると表示が二重になる。
        Assert.Equal([(1, 5)], InactiveLines("""
            #if 0
            #ifdef BAR
            int c;
            #endif
            int d;
            #endif
            """));
    }

    [Fact]
    public void elifの連なりでは通らなかった分岐をすべて記録する()
    {
        Assert.Equal([(3, 4), (5, 6)], InactiveLines("""
            #if 1
            int a;
            #elif 1
            int b;
            #else
            int c;
            #endif
            """));
    }

    [Fact]
    public void 分岐の条件に書かれた名前を覚える()
    {
        // 展開しなかったシンボルで守られた分岐を、効いていないと言わないために使う。
        InactiveRegion region = Assert.Single(PreprocessorHarness.Run("""
            #if defined(FOO) && BAR > 1
            int a;
            #endif
            """).InactiveRegions);

        Assert.Contains("FOO", region.ConditionSymbols);
        Assert.Contains("BAR", region.ConditionSymbols);
        Assert.DoesNotContain("defined", region.ConditionSymbols);
    }

    // ------------------------------------------------------------------
    // どの構成でも効いていない範囲
    // ------------------------------------------------------------------

    /// <summary>HLSL 単体としてセマンティックモデルを組み、効いていない行の範囲を返す。</summary>
    private static (int Start, int End)[] CollectLines(string source)
        => CollectLines(source, new SemanticsOptions());

    /// <summary>設定を指定して、効いていない行の範囲を返す。</summary>
    private static (int Start, int End)[] CollectLines(string source, SemanticsOptions options)
    {
        SourceText text = SourceText.From(source, Path.Combine("Assets", "Inactive.hlsl"));
        ShaderCompilation compilation = ShaderCompilation.CreateForHlsl(text, options);

        return [.. LanguageServer.InactiveRegionCollector.Collect(compilation).Select(r => (r.StartLine, r.EndLine))];
    }

    /// <summary>外側を並べられない <c>#ifdef _A</c> の中に <c>#ifdef _B</c> がある断片。</summary>
    private const string NestedInDeclinedFragment = """
        #pragma shader_feature_local _A
        #pragma shader_feature_local _B
        #ifdef _A
        #define USE_A 1
        float4 OnlyA()
        {
        #ifdef _B
            return 1;
        #endif
            return 0;
        }
        #endif
        float4 Always() { return 1; }
        """;

    [Theory]
    [InlineData(2)] // 1 つずつの構成だけ作る。組の構成は上限で落ちる
    [InlineData(3)] // 組の構成も作る
    public void 組でしか通らない分岐は効いていないと言わない(int maxSymbolVariants)
    {
        // _A だけの構成でも _B だけの構成でも、内側の分岐は外れる。
        // 組の構成を作らなかったからといって、どの構成でも通らないわけではない。
        (int Start, int End)[] lines = CollectLines(
            NestedInDeclinedFragment,
            new SemanticsOptions { MaxSymbolVariants = maxSymbolVariants });

        int inner = NestedInDeclinedFragment.Split('\n')
            .Select((text, index) => (text, index))
            .First(l => l.text.Contains("return 1;", StringComparison.Ordinal)).index;

        Assert.DoesNotContain(lines, l => l.Start <= inner && inner < l.End);
    }

    [Fact]
    public void どこでも定義されないシンボルの分岐は効いていない()
    {
        Assert.Equal([(1, 2)], CollectLines("""
            #ifdef NEVER_DEFINED
            float4 NeverUsed() { return 0; }
            #endif
            float4 Always() { return 1; }
            """));
    }

    [Theory]
    [InlineData("#if SHADER_API_MOBILE")]
    [InlineData("#ifdef SHADER_STAGE_FRAGMENT")]
    [InlineData("#if UNITY_VERSION < 202200")]
    [InlineData("#if defined(_FOO) && SHADER_API_VULKAN")]
    public void プラットフォームやバージョンで決まる分岐は効いていないと言わない(string condition)
    {
        // 解析は D3D11・Unity 6 の構成を仮に選んでいるだけで、実際のビルドでは別の値になりうる。
        Assert.Empty(CollectLines($$"""
            {{condition}}
            float4 PlatformOnly() { return 0; }
            #endif
            float4 Always() { return 1; }
            """));
    }

    // ------------------------------------------------------------------
    // エディタへの通知
    // ------------------------------------------------------------------

    /// <summary>シンボルで守られた分岐と、どの構成でも通らない分岐を持つシェーダー。</summary>
    private const string SymbolShader = """
        Shader "Company/Inactive"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma multi_compile _ _KEYWORD_ON

                    #ifdef _KEYWORD_ON
                    float4 SymbolOnly() { return 1; }
                    #endif

                    #ifdef NEVER_DEFINED
                    float4 NeverUsed() { return 0; }
                    #endif

                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                    half4 frag() : SV_Target { return 0; }
                    ENDHLSL
                }
            }
        }
        """;

    /// <summary>その文字列が書かれた行の番号 (0 始まり)。</summary>
    private static int LineOf(string source, string marker)
        => source[..source.IndexOf(marker, StringComparison.Ordinal)].Count(c => c == '\n');

    /// <summary>通知された範囲のどれかが、その行を含むかを判定する。</summary>
    private static bool Covers(JsonElement notification, int line)
        => notification.GetProperty("regions").EnumerateArray().Any(
            r => r.GetProperty("start").GetProperty("line").GetInt32() <= line
                 && line < r.GetProperty("end").GetProperty("line").GetInt32());

    [Fact]
    public async Task どの構成でも通らない分岐だけを効いていないと伝える()
    {
        // multi_compile のシンボルで守られた分岐は、既定の構成で外れていても別のバリアントで通る。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync(new { inactiveRegions = true });
        await harness.OpenAsync(SymbolShader);

        JsonElement notification = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");

        Assert.True(Covers(notification, LineOf(SymbolShader, "NeverUsed")));
        Assert.False(Covers(notification, LineOf(SymbolShader, "SymbolOnly")));
        Assert.False(Covers(notification, LineOf(SymbolShader, "#ifdef NEVER_DEFINED")));
        Assert.False(Covers(notification, LineOf(SymbolShader, "float4 vert")));
    }

    [Fact]
    public async Task シンボルを定義済みにすると効いている側に変わる()
    {
        const string fragment = """
            #ifdef _FOO
            float4 InFoo() { return 1; }
            #endif

            float4 Always() { return 0; }
            """;

        string uri = LspDocumentUri.FromFilePath(
            Path.Combine(Path.GetTempPath(), "shaderlyn-lsp-test", "Inactive.hlsl"));

        await using LanguageServerHarness harness = new(uri);
        await harness.InitializeAsync(new { inactiveRegions = true });
        await harness.OpenAsync(fragment);

        JsonElement before = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.True(Covers(before, LineOf(fragment, "InFoo")));

        await harness.SendAsync(new
        {
            jsonrpc = "2.0",
            id = 50,
            method = "shaderlyn/setDefinedSymbols",
            @params = new { textDocument = new { uri }, symbols = new[] { "_FOO" } },
        });

        JsonElement after = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.False(Covers(after, LineOf(fragment, "InFoo")));
    }

    [Fact]
    public async Task 選んだ構成で表示するとシンボルで分かれる分岐も薄くする()
    {
        // すべての構成を合わせると、シンボルで守られた分岐はどれかのバリアントで通るので薄くならない。
        // 利用者が構成を選んだら、選ばなかったシンボルは無効である。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync(new { inactiveRegions = true });
        await harness.OpenAsync(SymbolShader);
        await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");

        await harness.SetSelectedConfigurationAsync();

        JsonElement off = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.True(Covers(off, LineOf(SymbolShader, "SymbolOnly")));
        Assert.True(Covers(off, LineOf(SymbolShader, "NeverUsed")));
        Assert.False(Covers(off, LineOf(SymbolShader, "float4 vert")));

        await harness.SetSelectedConfigurationAsync("_KEYWORD_ON");

        JsonElement on = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.False(Covers(on, LineOf(SymbolShader, "SymbolOnly")));
        Assert.True(Covers(on, LineOf(SymbolShader, "NeverUsed")));
    }

    [Fact]
    public async Task 取り込まれる断片でもシンボルを外した構成を表示できる()
    {
        // tests/Shaderlyn.Tests/fixtures/valid/UrpLightingHelpers.hlsl と同じ形。
        // シンボルの分岐がヘッダを取り込むので、両方の分岐を 1 つの木に並べられない。
        const string fragment = """
            #pragma multi_compile _ _FEATURE_ON

            #ifdef _FEATURE_ON
            #include "Packages/com.example.missing/Feature.hlsl"
            float4 FeatureOnly() { return 1; }
            #endif

            float4 Always() { return 0; }
            """;

        string uri = LspDocumentUri.FromFilePath(
            Path.Combine(Path.GetTempPath(), "shaderlyn-lsp-test", "SymbolFragment.hlsl"));

        await using LanguageServerHarness harness = new(uri);
        await harness.InitializeAsync(new { inactiveRegions = true });
        await harness.OpenAsync(fragment);

        JsonElement all = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.False(Covers(all, LineOf(fragment, "FeatureOnly")));

        await harness.SetSelectedConfigurationAsync();

        JsonElement off = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.True(Covers(off, LineOf(fragment, "FeatureOnly")));
        Assert.False(Covers(off, LineOf(fragment, "float4 Always")));

        await harness.SetSelectedConfigurationAsync("_FEATURE_ON");

        JsonElement on = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.False(Covers(on, LineOf(fragment, "FeatureOnly")));
    }
}
