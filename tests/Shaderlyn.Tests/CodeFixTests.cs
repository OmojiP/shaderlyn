using System.Collections.Immutable;
using System.Text.Json;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;

namespace Shaderlyn.Tests;

/// <summary>
/// ルールが添える修正 (<see cref="Core.Diagnostics.DiagnosticFix"/>) を、言語サーバーのコードアクションとして受け取り、当ててみる。
/// </summary>
/// <remarks>
/// <para>
/// <b>当てた後の文と、当てた後にその指摘が消えることの両方を確認する。</b>
/// 文だけを比べると、修正したつもりで別の誤りを作る修正 (差し込む位置のずれなど) を見逃す。
/// </para>
/// <para>
/// 指摘が消えたかは CLI で解析し直して確認する。言語サーバーは構文の結果と意味解析の結果を 2 回に分けて送るので、
/// 「来ない」ことを通知で待つと、意味解析の前の通知を見て通ってしまう。
/// </para>
/// </remarks>
public sealed class CodeFixTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-code-fix-{Guid.NewGuid():N}");

    public CodeFixTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    public static TheoryData<string, string, string, string, string, bool> Fixes { get; } = new()
    {
        {
            "a.slang", "SG0102", "static を付ける",
            "const float k = 1.0;\nfloat f() { return k; }\n",
            "static const float k = 1.0;\nfloat f() { return k; }\n",
            true
        },
        {
            "a.slang", "SG0347", "override を付ける",
            "interface IFoo\n{\n    int getVal() { return 0; }\n}\n\nstruct Impl : IFoo\n{\n    int getVal() { return 1; }\n}\n",
            "interface IFoo\n{\n    int getVal() { return 0; }\n}\n\nstruct Impl : IFoo\n{\n    override int getVal() { return 1; }\n}\n",
            true
        },
        {
            "a.slang", "SG0346", "new を付ける",
            "class Counter\n{\n    int v;\n    __init(int start) { v = start; }\n}\n\nvoid f()\n{\n    Counter a = Counter(4);\n}\n",
            "class Counter\n{\n    int v;\n    __init(int start) { v = start; }\n}\n\nvoid f()\n{\n    Counter a = new Counter(4);\n}\n",
            true
        },
        {
            "a.comp", "GL0001", "'#extension GL_KHR_shader_subgroup_arithmetic : enable' を追加する",
            "#version 450\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer Data { float values[]; };\nvoid main()\n{\n    values[gl_LocalInvocationIndex] = subgroupAdd(values[gl_LocalInvocationIndex]);\n}\n",
            "#version 450\n#extension GL_KHR_shader_subgroup_arithmetic : enable\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer Data { float values[]; };\nvoid main()\n{\n    values[gl_LocalInvocationIndex] = subgroupAdd(values[gl_LocalInvocationIndex]);\n}\n",
            true
        },
        {
            "a.gdshader", "GD0101", "flat を付ける",
            "shader_type spatial;\nvarying int v;\nvoid vertex() { v = 1; }\n",
            "shader_type spatial;\nvarying flat int v;\nvoid vertex() { v = 1; }\n",
            true
        },
        {
            "a.wgsl", "WG0001", "'enable f16;' を追加する",
            "// 先頭のコメント\nvar<private> v: vec3<f16>;\n",
            "// 先頭のコメント\nenable f16;\nvar<private> v: vec3<f16>;\n",
            true
        },
        {
            "a.shader", "SL1003", "型を 'float4' にする",
            "Shader \"A\"\n{\n    Properties { _Color (\"Color\", Color) = (1, 1, 1, 1) }\n    SubShader\n    {\n        Pass\n        {\n            HLSLPROGRAM\n            #pragma vertex vert\n            #pragma fragment frag\n            float _Color;\n            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }\n            float4 frag() : SV_Target { return _Color; }\n            ENDHLSL\n        }\n    }\n}\n",
            "Shader \"A\"\n{\n    Properties { _Color (\"Color\", Color) = (1, 1, 1, 1) }\n    SubShader\n    {\n        Pass\n        {\n            HLSLPROGRAM\n            #pragma vertex vert\n            #pragma fragment frag\n            float4 _Color;\n            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }\n            float4 frag() : SV_Target { return _Color; }\n            ENDHLSL\n        }\n    }\n}\n",
            false
        },
    };

    [Theory]
    [MemberData(nameof(Fixes))]
    public async Task 修正を当てると指摘が消える(string fileName, string ruleId, string title, string source, string expected, bool preferred)
    {
        string path = Path.Combine(_workDirectory, fileName);
        await using LanguageServerHarness harness = new(documentUri: LanguageServer.DocumentUri.FromFilePath(path));

        await harness.InitializeAsync(null, _workDirectory);
        await harness.OpenAsync(source);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsForAsync(ruleId);
        JsonElement target = diagnostics.EnumerateArray().First(d => d.GetProperty("code").GetString() == ruleId);
        JsonElement actions = await harness.RequestCodeActionsAsync(target);
        JsonElement action = actions.EnumerateArray().Single(a => a.GetProperty("title").GetString() == title);

        Assert.Equal(preferred, action.GetProperty("isPreferred").GetBoolean());

        string fixedSource = Apply(source, action.GetProperty("edit").GetProperty("changes").GetProperty(harness.Uri));
        Assert.Equal(expected, fixedSource);

        await File.WriteAllTextAsync(path, fixedSource);
        Assert.DoesNotContain(ruleId, await AnalyzeIdsAsync(path));
    }

    /// <summary>
    /// 重なった BOM を消す修正 (UN0002)。ファイルから読んだときのテキストで確認する。
    /// </summary>
    /// <remarks>
    /// <see cref="SourceText"/> は先頭の U+FEFF を 1 つ取り除くので、残っているのが重なった分である。
    /// 言語サーバーが受け取る文はエディタが BOM を外した後のものだが、そこからもう 1 つ取り除いてしまうため、
    /// 言語サーバーでは UN0002 が出ない (別の不具合。この修正の試験はファイルの側で行う)。
    /// </remarks>
    [Fact]
    public void 重なったBOMを消す修正を添える()
    {
        SourceText text = SourceText.From("﻿﻿﻿Shader \"A\" { SubShader { Pass { } } }\n", Path.Combine("Assets", "A.shader"));
        ShaderCompilation compilation = UnityCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), new SemanticsOptions(), new UnityOptions());
        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        DiagnosticFix fix = Assert.Single(diagnostics.Single(d => d.Id == "UN0002").Fixes);

        Assert.True(fix.IsPreferred);
        TextReplacement replacement = Assert.Single(fix.Replacements);
        Assert.Equal(new TextSpan(0, 2), replacement.Span);
        Assert.Equal(string.Empty, replacement.NewText);
    }

    /// <summary>LSP の編集の並びを、後ろから当てる。</summary>
    private static string Apply(string source, JsonElement edits)
    {
        List<(int Start, int End, string Text)> replacements = [];

        foreach (JsonElement edit in edits.EnumerateArray())
        {
            JsonElement range = edit.GetProperty("range");
            replacements.Add((Offset(source, range.GetProperty("start")), Offset(source, range.GetProperty("end")), edit.GetProperty("newText").GetString()!));
        }

        foreach ((int start, int end, string text) in replacements.OrderByDescending(r => r.Start))
        {
            source = source[..start] + text + source[end..];
        }

        return source;
    }

    /// <summary>行と桁 (UTF-16) から、文字列の中の位置を求める。</summary>
    private static int Offset(string source, JsonElement position)
    {
        int line = position.GetProperty("line").GetInt32();
        int offset = 0;

        for (int i = 0; i < line; i++)
        {
            offset = source.IndexOf('\n', offset) + 1;
        }

        return offset + position.GetProperty("character").GetInt32();
    }

    /// <summary>ファイルを CLI で解析し、指摘の ID を返す。</summary>
    private static async Task<string[]> AnalyzeIdsAsync(string path)
    {
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([path, "--no-config", "--format", "json"], BuiltInAnalyzers.All, output, error);

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        return [.. document.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("id").GetString()!)];
    }
}
