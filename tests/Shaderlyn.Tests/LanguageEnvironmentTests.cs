using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL 以外の言語の環境 (GLSL・Godot・Slang・WGSL) の検証。
/// </summary>
/// <remarks>
/// 今は構文だけを読む (単位 P3)。GLSL は構文エラーを指摘し、まだ読めない言語は何も指摘しない。
/// 意味解析とルールは働かないので、HLSL のルールの誤検出 (<c>vec3</c> を宣言されていない名前と読むなど) も出ない。
/// </remarks>
public sealed class LanguageEnvironmentTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-language-env-{Guid.NewGuid():N}");

    public LanguageEnvironmentTests() => Directory.CreateDirectory(_workDirectory);

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

    private async Task<(ExitCode Code, string Output)> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync([_workDirectory, "--no-config", .. args], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return (code, output.ToString());
    }

    [Fact]
    public async Task GLSLは構文エラーを指摘し_HLSLのルールは働かない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "ok.frag"), """
            #version 450
            layout(binding = 0) uniform UBO { mat4 model; } ubo;
            layout(location = 0) in vec3 inColor;
            layout(location = 0) out vec4 outColor;
            void main() { outColor = vec4(inColor, 1.0); }
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "broken.vert"), """
            #version 450
            void main() { gl_Position = vec4(1.0, ; }
            """);

        (ExitCode code, string output) = await RunAsync("--environment", "glsl");

        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("2 ファイルを解析", output, StringComparison.Ordinal);
        Assert.Contains("broken.vert", output, StringComparison.Ordinal);
        Assert.Contains("HL0001", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ok.frag", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HL0310", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unity のプロジェクトに GLSL のファイルが混ざっていれば、拡張子で GLSL として読む。
    /// Unity の環境で取り込まれる側の <c>.hlsl</c> は、今までどおり単体では読まない。
    /// </summary>
    [Fact]
    public async Task HLSL以外の言語のファイルは拡張子の環境で読む()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "Lit.shader"), "Shader \"Test\" { SubShader { Pass { } } }");
        File.WriteAllText(Path.Combine(_workDirectory, "Common.hlsl"), "float Broken( {");
        Directory.CreateDirectory(Path.Combine(_workDirectory, "web"));
        File.WriteAllText(Path.Combine(_workDirectory, "web", "broken.frag"), "#version 450\nvoid main() { x = ; }\n");

        (ExitCode code, string output) = await RunAsync();

        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("2 ファイルを解析", output, StringComparison.Ordinal);
        Assert.Contains("broken.frag", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Common.hlsl", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>.mesh</c> は Unity のメッシュのアセット (YAML) と重なるので、拡張子だけでは GLSL として読まない。
    /// GLSL の環境を指定したときだけ読む。
    /// </summary>
    [Fact]
    public async Task meshは拡張子だけではGLSLとして読まない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "Lit.shader"), "Shader \"Test\" { SubShader { Pass { } } }");
        File.WriteAllText(Path.Combine(_workDirectory, "Cube.mesh"), "%YAML 1.1\n--- !u!43 &4300000\nMesh:\n  m_Name: Cube\n");

        (ExitCode code, string output) = await RunAsync();

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);

        File.Delete(Path.Combine(_workDirectory, "Cube.mesh"));
        File.WriteAllText(Path.Combine(_workDirectory, "meshlet.mesh"), "#version 460\nvoid main() { x = ; }\n");

        (_, string glsl) = await RunAsync("--environment", "glsl");

        Assert.Contains("meshlet.mesh", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    /// ファイルごとに一番近い設定ファイルを使う。入れ子の設定ファイルは、その下のファイルにだけ効く。
    /// </summary>
    [Fact]
    public async Task 入れ子の設定ファイルはその下のファイルにだけ効く()
    {
        const string Undeclared = "float4 main() : SV_Target { return missing; }\n";
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "version: 1\nenvironment: hlsl\n");
        File.WriteAllText(Path.Combine(_workDirectory, "root.hlsl"), Undeclared);
        Directory.CreateDirectory(Path.Combine(_workDirectory, "legacy"));
        File.WriteAllText(Path.Combine(_workDirectory, "legacy", ".shaderlyn.yaml"), "version: 1\nenvironment: hlsl\nrules:\n  HL0310: none\n");
        File.WriteAllText(Path.Combine(_workDirectory, "legacy", "old.hlsl"), Undeclared);

        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([_workDirectory], BuiltInAnalyzers.All, output, error);
        string text = output.ToString();

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Contains("2 ファイルを解析", text, StringComparison.Ordinal);
        Assert.Contains("root.hlsl", text, StringComparison.Ordinal);
        Assert.DoesNotContain("old.hlsl", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("godot", "a.gdshader", "shader_type spatial;\nvoid fragment() { ALBEDO = vec3(1.0); }\n")]
    [InlineData("slang", "a.slang", "import utils;\n[shader(\"compute\")] void main<T : IFoo>() {}\n")]
    [InlineData("wgsl", "a.wgsl", "@fragment fn main() -> @location(0) vec4<f32> { return vec4<f32>(1.0); }\n")]
    public async Task まだ読めない言語はファイルを集めるが何も指摘しない(string environment, string file, string code)
    {
        File.WriteAllText(Path.Combine(_workDirectory, file), code);

        (ExitCode exit, string output) = await RunAsync("--environment", environment);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 0 件, 情報 0 件", output, StringComparison.Ordinal);
    }
}
