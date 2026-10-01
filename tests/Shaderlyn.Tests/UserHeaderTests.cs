using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
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
        // HlslRuleAnalyzer の ReportDiagnostic(descriptor, span) は範囲だけを受け取る。
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
    private sealed class BadFunctionNameAnalyzer : HlslRuleAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TEST0001", "名前が Bad で始まる関数", "関数 '{0}' の名前を変えてください。", "Naming", DiagnosticSeverity.Warning);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        protected override void InitializeHlsl(HlslAnalysisContext context)
            => context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
            {
                if (c.Node.Name.StartsWith("Bad", StringComparison.Ordinal))
                {
                    c.ReportDiagnostic(Rule, c.Node.NameToken.Span, c.Node.Name);
                }
            });
    }
}
