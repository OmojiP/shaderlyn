using System.Text;
using System.Text.Json;
using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// CLI の <c>--fix</c> / <c>--fix-dry-run</c> / <c>--fix-rule</c> と、JSON・SARIF に書き出す修正。
/// </summary>
/// <remarks>
/// <b>書き込んだ後のファイルの中身を、バイト列で確認する。</b>
/// 文字列で比較すると、BOM と改行の書き方が変わったことを見逃す。
/// </remarks>
public sealed class CliFixTests : IDisposable
{
    private const string MissingStatic = "const float k = 1.0;\nfloat f() { return k; }\n";

    private const string MissingOverride =
        "interface IFoo\n{\n    int getVal() { return 0; }\n}\n\nstruct Impl : IFoo\n{\n    int getVal() { return 1; }\n}\n";

    private const string UnityShader =
        "Shader \"A\"\n{\n    Properties { _Color (\"Color\", Color) = (1, 1, 1, 1) }\n    SubShader\n    {\n        Pass\n        {\n"
        + "            HLSLPROGRAM\n            #pragma vertex vert\n            #pragma fragment frag\n            float _Color;\n"
        + "            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }\n            float4 frag() : SV_Target { return _Color; }\n"
        + "            ENDHLSL\n        }\n    }\n}\n";

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-cli-fix-{Guid.NewGuid():N}");

    public CliFixTests() => Directory.CreateDirectory(_workDirectory);

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

    [Fact]
    public async Task 差分の表示ではファイルを書き換えずに差分を出す()
    {
        string path = Write("a.slang", MissingStatic);

        (ExitCode code, string output, string error) = await RunAsync(path, "--fix-dry-run");

        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Equal(
            "--- a/a.slang\n+++ b/a.slang\n@@ -1,2 +1,2 @@\n-const float k = 1.0;\n+static const float k = 1.0;\n float f() { return k; }\n",
            output);
        Assert.Contains("適用する修正: 1 件 (1 ファイル。SG0102: 1)", error, StringComparison.Ordinal);
        Assert.Equal(MissingStatic, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task 適用する修正が無ければ差分の表示は0を返す()
    {
        string path = Write("a.slang", "static const float k = 1.0;\nfloat f() { return k; }\n");

        (ExitCode code, string output, string error) = await RunAsync(path, "--fix-dry-run");

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(output);
        Assert.Contains("適用する修正: 0 件", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 修正を書き込み書き込んだ後のファイルを報告する()
    {
        string path = Write("a.slang", MissingStatic);

        (ExitCode code, string output, string error) = await RunAsync(path, "--fix", "--format", "json");

        Assert.Equal("static const float k = 1.0;\nfloat f() { return k; }\n", await File.ReadAllTextAsync(path));
        Assert.Contains("修正を適用しました: 1 件 (1 ファイル。SG0102: 1)", error, StringComparison.Ordinal);
        Assert.DoesNotContain("SG0102", Ids(output));
        Assert.Equal(ExitCode.Success, code);
    }

    [Fact]
    public async Task 指定したルールの修正だけを適用する()
    {
        string slang = Write("a.slang", MissingStatic + "\n" + MissingOverride);

        (_, string output, string error) = await RunAsync(slang, "--fix", "--fix-rule", "sg0347", "--format", "json");

        string fixedText = await File.ReadAllTextAsync(slang);
        Assert.Contains("override int getVal() { return 1; }", fixedText, StringComparison.Ordinal);
        Assert.StartsWith("const float k", fixedText, StringComparison.Ordinal);
        Assert.Contains("SG0347: 1", error, StringComparison.Ordinal);
        Assert.Contains("SG0102", Ids(output));
    }

    [Fact]
    public async Task 既定の候補でない修正はルールを指定したときだけ適用する()
    {
        string path = Write("A.shader", UnityShader);

        await RunAsync(path, "--fix");
        Assert.Equal(UnityShader, await File.ReadAllTextAsync(path));

        (_, _, string error) = await RunAsync(path, "--fix", "--fix-rule", "SL1003");

        Assert.Contains("float4 _Color;", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Contains("SL1003: 1", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BOMと改行の書き方を保つ()
    {
        string path = Path.Combine(_workDirectory, "a.wgsl");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        await File.WriteAllBytesAsync(path, [.. bom, .. Encoding.UTF8.GetBytes("// コメント\r\nvar<private> v: vec3<f16>;\r\n")]);

        await RunAsync(path, "--fix");

        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(bom, bytes[..3]);
        Assert.Equal("// コメント\r\nenable f16;\r\nvar<private> v: vec3<f16>;\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task JSONに修正を書き出す()
    {
        string path = Write("a.slang", MissingStatic);

        (_, string output, _) = await RunAsync(path, "--format", "json");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement fix = document.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Single(d => d.GetProperty("id").GetString() == "SG0102")
            .GetProperty("fixes").EnumerateArray().Single();

        Assert.Equal("static を付ける", fix.GetProperty("title").GetString());
        Assert.True(fix.GetProperty("preferred").GetBoolean());

        JsonElement edit = fix.GetProperty("edits").EnumerateArray().Single();
        Assert.Equal(1, edit.GetProperty("line").GetInt32());
        Assert.Equal(1, edit.GetProperty("column").GetInt32());
        Assert.Equal(1, edit.GetProperty("endLine").GetInt32());
        Assert.Equal(1, edit.GetProperty("endColumn").GetInt32());
        Assert.Equal("static ", edit.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SARIFに修正を書き出す()
    {
        string path = Write("a.slang", MissingStatic);

        (_, string output, _) = await RunAsync(path, "--format", "sarif");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement fix = document.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("ruleId").GetString() == "SG0102")
            .GetProperty("fixes").EnumerateArray().Single();

        Assert.Equal("static を付ける", fix.GetProperty("description").GetProperty("text").GetString());

        JsonElement change = fix.GetProperty("artifactChanges").EnumerateArray().Single();
        Assert.Equal("a.slang", change.GetProperty("artifactLocation").GetProperty("uri").GetString());

        JsonElement replacement = change.GetProperty("replacements").EnumerateArray().Single();
        JsonElement region = replacement.GetProperty("deletedRegion");
        Assert.Equal(1, region.GetProperty("startLine").GetInt32());
        Assert.Equal(1, region.GetProperty("startColumn").GetInt32());
        Assert.Equal(1, region.GetProperty("endColumn").GetInt32());
        Assert.Equal("static ", replacement.GetProperty("insertedContent").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(new[] { "--fix", "--fix-dry-run" }, "--fix と --fix-dry-run は同時に指定できません。")]
    [InlineData(new[] { "--fix-rule", "SG0102" }, "--fix-rule は --fix か --fix-dry-run と一緒に指定してください。")]
    [InlineData(new[] { "--fix", "--write-baseline", "b.json" }, "--fix は --write-baseline と同時に指定できません。")]
    [InlineData(new[] { "--fix-dry-run", "--env" }, "--fix-dry-run は --env と同時に指定できません。")]
    [InlineData(new[] { "--fix", "--fix-rule", "XX9999" }, "--fix-rule のルール ID 'XX9999' は存在しません。")]
    public async Task 修正のオプションの誤りを伝える(string[] options, string message)
    {
        string path = Write("a.slang", MissingStatic);

        (ExitCode code, _, string error) = await RunAsync(path, options);

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains(message, error, StringComparison.Ordinal);
        Assert.Equal(MissingStatic, await File.ReadAllTextAsync(path));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_workDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<(ExitCode Code, string Output, string Error)> RunAsync(string path, params string[] options)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(
            [path, "--no-config", "--base-path", _workDirectory, .. options], BuiltInAnalyzers.All, output, error);

        return (code, output.ToString(), error.ToString());
    }

    private static string[] Ids(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("id").GetString()!)];
    }
}
