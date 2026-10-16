using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Tests;

/// <summary>
/// 利用者が書いて取り込んだヘッダ (共通の <c>.hlsl</c>) の指摘の検証。
/// </summary>
/// <remarks>
/// <para>
/// <b>共通の <c>.hlsl</c> を取り込むのはよくある書き方であり、その中身も利用者が直すコードである。</b>
/// 以前は「取り込んだヘッダは直せない」として一律に報告しておらず、
/// フォルダーを指定した解析では <c>.hlsl</c> も探索しないため、共通のヘッダの誤りはどこからも報告されなかった。
/// </para>
/// <para>
/// ヘッダは取り込む側の文脈で解析する。取り込む側が先に宣言した名前を使うヘッダも、誤りにはならない。
/// 直せないのは Unity と外部パッケージのヘッダ (<c>Library</c> と Unity Editor に同梱のもの) だけである。
/// </para>
/// </remarks>
public sealed partial class UserHeaderTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"shaderlyn-header-{Guid.NewGuid():N}");

    public UserHeaderTests() => Directory.CreateDirectory(Path.Combine(_project, "Assets", "Shaders"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
            // テストの後始末が失敗してもテスト結果には影響させない。
        }
    }

    private string Shaders => Path.Combine(_project, "Assets", "Shaders");

    private string Write(string relativePath, string content)
    {
        string path = Path.Combine(_project, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary><c>#include</c> を並べたシェーダーを書く。</summary>
    private string WriteShader(string name, string body, params string[] includes) => Write(
        Path.Combine("Assets", "Shaders", name + ".shader"),
        $$"""
        Shader "Test/{{name}}"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
        {{string.Join(Environment.NewLine, includes.Select(i => $"            #include \"{i}\""))}}
                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                    {{body}}
                    ENDHLSL
                }
            }
        }
        """);

    private async Task<(ExitCode Code, string Output, string Error)> RunAsync(
        IEnumerable<DiagnosticAnalyzer>? analyzers,
        params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(
            [.. args, "--unity-project", _project, "--no-config"],
            [.. analyzers ?? BuiltInAnalyzers.All],
            output,
            error);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>出力から、ファイル名とルール ID の組を拾う。</summary>
    private static List<(string File, string Id)> Findings(string output)
        => [.. Finding().Matches(output).Select(m => (Path.GetFileName(m.Groups[1].Value), m.Groups[2].Value))];

    [GeneratedRegex(@"^(.+?)\(\d+:\d+\): \w+ (\w+):", RegexOptions.Multiline)]
    private static partial Regex Finding();

    /// <summary>2 つの Pass が同じヘッダを取り込み、キーワード A は 1 つ目の Pass だけが宣言するシェーダーを書く。</summary>
    private void WriteTwoPassShader(string name, string header, string frag)
    {
        string pass(string pragma) => $$"""
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        {{pragma}}
                        #include "{{header}}"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        {{frag}}
                        ENDHLSL
                    }
            """;

        Write(Path.Combine("Assets", "Shaders", name + ".shader"), $$"""
            Shader "Test/{{name}}"
            {
                SubShader
                {
            {{pass("#pragma multi_compile_local _ A")}}
            {{pass(string.Empty)}}
                }
            }
            """);
    }

    /// <summary>
    /// キーワードを宣言していない Pass には、そのキーワードの分岐の条件を付けない。
    /// 以前は、宣言している Pass のヘッダの分岐の条件が、同じヘッダを取り込む別の Pass にも付き、
    /// 「A のとき宣言されていない」(HL0310) や「値を返さない経路がある」(HL0360) と誤って報告していた。
    /// </summary>
    [Fact]
    public async Task キーワードを宣言しないPassにはヘッダの分岐の条件を付けない()
    {
        Write(Path.Combine("Assets", "Shaders", "Branches.hlsl"), """
            #ifndef BRANCHES_INCLUDED
            #define BRANCHES_INCLUDED
            float4 Get()
            {
            #if defined(A)
                return float4(1, 1, 1, 1);
            #else
                return float4(0, 0, 0, 0);
            #endif
            }
            float4 UseA()
            {
            #if defined(A)
                half a = 1;
            #else
                half a = 2;
            #endif
                return a;
            }
            #endif
            """);
        WriteTwoPassShader("TwoPass", "Branches.hlsl", "float4 frag() : SV_Target { return Get() + UseA(); }");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.DoesNotContain("HL0360", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HL0310", output, StringComparison.Ordinal);
    }

    /// <summary>キーワードの分岐で本当に返さない経路は、Pass を分けても報告する。</summary>
    [Fact]
    public async Task キーワードの分岐で返さない経路はPassを分けても報告する()
    {
        Write(Path.Combine("Assets", "Shaders", "Missing.hlsl"), """
            #ifndef MISSING_INCLUDED
            #define MISSING_INCLUDED
            float4 Get()
            {
            #if defined(A)
                return float4(1, 1, 1, 1);
            #endif
            }
            #endif
            """);
        WriteTwoPassShader("TwoPassMissing", "Missing.hlsl", "float4 frag() : SV_Target { return Get(); }");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.Contains(("Missing.hlsl", "HL0360"), Findings(output));
    }

    /// <summary>
    /// ヘッダの中の取り込みは、そのヘッダから引けなければ、解析しているシェーダーから引き直す (Unity 6.6 と同じ)。
    /// </summary>
    [Fact]
    public async Task ヘッダの中の取り込みはシェーダーからの相対でも引く()
    {
        Write(Path.Combine("Assets", "Shaders", "B.hlsl"), "float4 FromB() { return 1; }");

        // ヘッダからの相対では Assets/Shaders/Lib/B.hlsl (無い)、シェーダーからの相対では Assets/Shaders/B.hlsl。
        Write(Path.Combine("Assets", "Shaders", "Lib", "Sub", "H.hlsl"), "#include \"../B.hlsl\"\n");
        Write(Path.Combine("Assets", "Shaders", "Deep", "Pass.shader"), """
            Shader "Test/Deep"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #include "../Lib/Sub/H.hlsl"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target { return FromB(); }
                        ENDHLSL
                    }
                }
            }
            """);

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.DoesNotContain("HL0321", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HL0310", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HL0005", output, StringComparison.Ordinal);

        // 見つけて解析は続けるが、別のフォルダーのシェーダーからは見つからなくなるので、ヘッダの #include の行に警告を出す (UN0102)。
        Assert.Contains(("H.hlsl", "UN0102"), Findings(output));
        Assert.Contains("'../../B.hlsl'", output, StringComparison.Ordinal);
    }

    /// <summary>ヘッダから見つかる取り込みには UN0102 を出さない。</summary>
    [Fact]
    public async Task ヘッダからの相対で見つかる取り込みには警告を出さない()
    {
        Write(Path.Combine("Assets", "Shaders", "B.hlsl"), "float4 FromB() { return 1; }");
        Write(Path.Combine("Assets", "Shaders", "Lib", "Sub", "H.hlsl"), "#include \"../../B.hlsl\"\n");
        WriteShader("Ok", "float4 frag() : SV_Target { return FromB(); }", "Lib/Sub/H.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.DoesNotContain("UN0102", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// ヘッダの指摘に、どのシェーダーのどの Pass から、どの #include をたどって取り込んだかを添える。
    /// 同じ指摘を 2 つのシェーダーから報告したら 1 件にまとめ、経路を並べる。キャッシュから使い回しても経路は残る。
    /// </summary>
    [Fact]
    public async Task ヘッダの指摘に取り込んだ経路を添える()
    {
        Write(Path.Combine("Assets", "Shaders", "Lib", "Inner.hlsl"), "float4 Tint(float4 c) { return c * undefinedTint; }\n");
        Write(Path.Combine("Assets", "Shaders", "Common.hlsl"), "#include \"Lib/Inner.hlsl\"\n");
        WriteShader("A", "float4 frag() : SV_Target { return Tint(1); }", "Common.hlsl");
        WriteShader("B", "float4 frag() : SV_Target { return Tint(1); }", "Common.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.Single(Findings(output), f => f == ("Inner.hlsl", "HL0310"));
        Assert.Matches(@"取り込み元: .*A\.shader\(10:22\) .*→ .*Common\.hlsl\(1:10\)", output);
        Assert.Contains("ほかに 1 本の経路があります", output, StringComparison.Ordinal);

        (_, string all, _) = await RunAsync(null, Shaders, "--include-trace", "all");
        Assert.Equal(2, all.Split('\n').Count(line => line.Contains("取り込み元:", StringComparison.Ordinal)));
        Assert.Contains("B.shader(10:22)", all, StringComparison.Ordinal);

        (_, string none, _) = await RunAsync(null, Shaders, "--include-trace", "none");
        Assert.DoesNotContain("取り込み元", none, StringComparison.Ordinal);

        (_, string json, _) = await RunAsync(null, Shaders, "--format", "json");
        Assert.Contains("\"includedFrom\"", json, StringComparison.Ordinal);

        string cache = Path.Combine(_project, "cache.json");
        await RunAsync(null, Shaders, "--cache", cache);
        (_, string cached, _) = await RunAsync(null, Shaders, "--cache", cache, "--include-trace", "all");
        Assert.Equal(2, cached.Split('\n').Count(line => line.Contains("取り込み元:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 共通のヘッダの誤りをヘッダの位置に報告する()
    {
        Write(Path.Combine("Assets", "Shaders", "Common.hlsl"), """
            float4 Tint(float4 c) { return c * undefinedTint; }
            """);
        WriteShader("A", "float4 frag() : SV_Target { return Tint(1); }", "Common.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.Contains(("Common.hlsl", "HL0310"), Findings(output));
        Assert.Contains("undefinedTint", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 同じヘッダを取り込むシェーダーが複数あっても指摘は1件にまとまる()
    {
        Write(Path.Combine("Assets", "Shaders", "Common.hlsl"), """
            float4 Tint(float4 c) { return c * undefinedTint; }
            """);
        WriteShader("A", "float4 frag() : SV_Target { return Tint(1); }", "Common.hlsl");
        WriteShader("B", "float4 frag() : SV_Target { return Tint(2); }", "Common.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.Single(Findings(output), f => f == ("Common.hlsl", "HL0310"));
    }

    /// <summary>
    /// 構成によって本体が違うヘッダの関数の局所変数を、宣言されていない (HL0310) と報告しない。
    /// </summary>
    /// <remarks>
    /// 既定の構成 (P_LOW) の木には <c>#if !defined(P_LOW)</c> の中身が無い。ヘッダの関数を位置で 1 度だけ見ていたので、
    /// P_HIGH の木にだけある局所変数 <c>r</c> の名前が漏れ、大域の名前として探して見つからなかった
    /// (HDRP の SubpixelMorphologicalAntialiasing.hlsl の <c>leftRight</c> など)。
    /// </remarks>
    [Fact]
    public async Task 構成で本体が変わるヘッダの関数の局所変数を宣言されていないと言わない()
    {
        Write(Path.Combine("Assets", "Shaders", "Lib.hlsl"), """
            #if defined(P_HIGH)
            #define ROUND 25
            #else
            #define ROUND 25
            #endif
            void F(inout float2 w, float2 d)
            {
                float2 lr = step(d.xy, d.yx);
            #if !defined(P_LOW)
                float2 r = (1.0 - ROUND) * lr;
                w *= r;
            #endif
            }
            """);
        WriteShader(
            "A",
            "#pragma multi_compile_fragment P_LOW P_MED P_HIGH\nhalf4 frag() : SV_Target { float2 w = 1; F(w, 0); return w.x; }",
            "Lib.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.DoesNotContain(("Lib.hlsl", "HL0310"), Findings(output));
    }

    /// <summary>
    /// UTF-16 で保存したヘッダは、読んで解析を続けたうえで、取り込む行に UN0001 を出す (Unity は読めない)。
    /// 以前は BOM の無い UTF-16 を UTF-8 として読み、NUL を挟んだ文字列を HL0001 にしていた。BOM 付きは何も言わなかった。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UTF16のヘッダは取り込む行に報告する(bool bom)
    {
        byte[] body = System.Text.Encoding.Unicode.GetBytes("// 共通\nfloat4 Tint(float4 c) { return c * 0.5; }\n");
        string header = Path.Combine(Shaders, "Common.hlsl");
        File.WriteAllBytes(header, bom ? [0xFF, 0xFE, .. body] : body);
        WriteShader("A", "float4 frag() : SV_Target { return Tint(1); }", "Common.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.Contains(("A.shader", "UN0001"), Findings(output));
        Assert.DoesNotContain(Findings(output), f => f.Id is "HL0001" or "HL0310");
    }

    [Fact]
    public async Task ヘッダで定義したマクロの本体の誤りはヘッダの位置に報告する()
    {
        string header = Write(Path.Combine("Assets", "Shaders", "Common.hlsl"), """
            #define SCALE_BY_MISSING(x) ((x) * missingScale)
            """);
        WriteShader("A", "float4 frag() : SV_Target { return SCALE_BY_MISSING(1); }", "Common.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.Contains($"{header}(1:", output, StringComparison.Ordinal);
        Assert.Contains("missingScale", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Libraryの下のヘッダは報告しない()
    {
        // Package Manager が展開したパッケージは利用者に直せない。
        string vendor = Path.Combine("Library", "PackageCache", "com.vendor.lib");
        Write(Path.Combine(vendor, "Vendor.hlsl"), """
            float4 VendorTint(float4 c) { return c * vendorUndefined; }
            """);
        WriteShader("A", "float4 frag() : SV_Target { return VendorTint(1); }", "Vendor.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders, "--include-path", Path.Combine(_project, vendor));

        Assert.DoesNotContain("vendorUndefined", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Findings(output), f => f.File == "Vendor.hlsl");
    }

    [Fact]
    public async Task 取り込む側が先に宣言した名前を使うヘッダを誤りにしない()
    {
        Write(Path.Combine("Assets", "Shaders", "Lighting.hlsl"), """
            float4 ApplyScale(float4 c) { return c * _SharedScale; }
            """);
        Write(Path.Combine("Assets", "Shaders", "A.shader"), """
            Shader "Test/A"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        float _SharedScale;
                        #include "Lighting.hlsl"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target { return ApplyScale(1); }
                        ENDHLSL
                    }
                }
            }
            """);

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.DoesNotContain(Findings(output), f => f.File == "Lighting.hlsl");
    }

    [Fact]
    public async Task キャッシュから復元してもヘッダの指摘が出る()
    {
        Write(Path.Combine("Assets", "Shaders", "Common.hlsl"), """
            float4 Tint(float4 c) { return c * undefinedTint; }
            """);
        WriteShader("A", "float4 frag() : SV_Target { return Tint(1); }", "Common.hlsl");
        string cache = Path.Combine(_project, "cache.json");

        (_, string first, _) = await RunAsync(null, Shaders, "--cache", cache);
        (_, string second, string timings) = await RunAsync(null, Shaders, "--cache", cache, "--timings");

        Assert.Contains(("Common.hlsl", "HL0310"), Findings(first));
        Assert.Equal(first, second);
        Assert.Contains("前回の結果の使い回し 1 件", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 共通のヘッダで使った非推奨のAPIを報告する()
    {
        // URP0003 はマクロ展開前のトークンを見る。解析しているファイルのトークンだけを見ると、
        // 共通のヘッダに書いた UnityObjectToClipPos は報告されない。
        Write(Path.Combine("Assets", "Shaders", "Common.hlsl"), """
            float4 ToClip(float4 p) { return UnityObjectToClipPos(p); }
            """);
        WriteShader(
            "A",
            "float4 frag() : SV_Target { return ToClip(1); }",
            "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl",
            "Common.hlsl");

        (_, string output, _) = await RunAsync(null, Shaders);

        Assert.Contains(("Common.hlsl", "URP0003"), Findings(output));
    }

    [Fact]
    public async Task 自作ルールが範囲で報告したヘッダのノードはヘッダの位置に出る()
    {
        // ShaderRuleAnalyzer の ReportDiagnostic(descriptor, span) は範囲だけを受け取る。
        // ヘッダのノードの範囲を解析しているファイルに付けると、別のファイルの同じ位置を指してしまう。
        string header = Write(Path.Combine("Assets", "Shaders", "Common.hlsl"), """
            float4 BadHelper(float4 c) { return c; }
            """);
        WriteShader("A", "float4 frag() : SV_Target { return BadHelper(1); }", "Common.hlsl");

        (_, string output, _) = await RunAsync([new BadFunctionNameAnalyzer()], Shaders);

        Assert.Contains($"{header}(1:8): warning TEST0001", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 言語サーバーはヘッダの指摘をヘッダのURIへ送る()
    {
        string header = Write(Path.Combine("Assets", "Shaders", "Common.hlsl"), """
            float4 Tint(float4 c) { return c * undefinedTint; }
            """);
        string shader = WriteShader("A", "float4 frag() : SV_Target { return Tint(1); }", "Common.hlsl");
        string headerUri = LanguageServer.DocumentUri.FromFilePath(header);

        await using LanguageServerHarness harness = new(LanguageServer.DocumentUri.FromFilePath(shader));
        await harness.InitializeAsync();
        await harness.OpenAsync(File.ReadAllText(shader));

        JsonElement diagnostics = await harness.ReceiveDiagnosticsForAsync(headerUri, "HL0310");

        JsonElement found = Assert.Single(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "HL0310");
        Assert.Contains("undefinedTint", found.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, found.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    /// <summary>名前が Bad で始まる関数を、名前の範囲で報告する自作ルール。</summary>
    private sealed class BadFunctionNameAnalyzer : ShaderRuleAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TEST0001", "名前が Bad で始まる関数", "関数 '{0}' の名前を変えてください。", "Naming", DiagnosticSeverity.Warning);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        protected override void InitializeShader(ShaderAnalysisContext context)
            => context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
            {
                if (c.Node.Name.StartsWith("Bad", StringComparison.Ordinal))
                {
                    c.ReportDiagnostic(Rule, c.Node.NameToken.Span, c.Node.Name);
                }
            });
    }
}
