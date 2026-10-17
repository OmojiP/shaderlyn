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

    /// <summary>
    /// GLSL の取り込みも、解析対象の外のファイルは読まない (HLSL と同じ <c>IncludeBoundary</c>)。中のファイルは読む。
    /// </summary>
    [Fact]
    public async Task GLSLの取り込みは解析対象の外を読まない()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"shaderlyn-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);

        try
        {
            // 外のファイルと中のファイルは、どちらも構文エラーを含む。読めば指摘が出る。
            string secret = Path.Combine(outside, "secret.glsl");
            File.WriteAllText(secret, "float leaked( {\n");
            File.WriteAllText(Path.Combine(_workDirectory, "inside.glsl"), "float inside( {\n");
            File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), $$"""
                #version 450
                #include "inside.glsl"
                #include "{{secret.Replace('\\', '/')}}"
                void main() {}
                """);

            (_, string output) = await RunAsync("--environment", "glsl", Path.Combine(_workDirectory, "a.frag"));

            Assert.Contains("inside.glsl", output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret.glsl", output, StringComparison.Ordinal);
            Assert.DoesNotContain("leaked", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>
    /// 解析設定 (設定ファイルと環境) をまたいでも、指摘はファイルの順に並ぶ。
    /// 同じヘッダを 2 つの解析設定が取り込んでも、ヘッダの指摘は 1 件にまとまる。
    /// </summary>
    [Fact]
    public async Task 解析設定をまたいでも並び順と重複のまとめ方は変わらない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "version: 1\nenvironment: hlsl\n");
        File.WriteAllText(Path.Combine(_workDirectory, "Common.hlsli"), "float Lib() { return missing; }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "z.hlsl"), "#include \"Common.hlsli\"\nfloat4 main() : SV_Target { return Lib() + alsoMissing; }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), "#version 450\nvoid main() { x = ; }\n");
        Directory.CreateDirectory(Path.Combine(_workDirectory, "sub"));
        File.WriteAllText(Path.Combine(_workDirectory, "sub", ".shaderlyn.yaml"), "version: 1\nenvironment: hlsl\nrules:\n  HL0301: none\n");
        File.WriteAllText(Path.Combine(_workDirectory, "sub", "b.hlsl"), "#include \"../Common.hlsli\"\nfloat4 main() : SV_Target { return Lib(); }\n");

        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([_workDirectory], BuiltInAnalyzers.All, output, error);
        string text = output.ToString();

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Contains("3 ファイルを解析", text, StringComparison.Ordinal);

        // GLSL の解析設定 (a.frag) は既定の解析設定 (z.hlsl) より後に解析するが、出力はファイルの順に並ぶ。
        int frag = text.IndexOf("a.frag(", StringComparison.Ordinal);
        int z = text.IndexOf("z.hlsl(2", StringComparison.Ordinal);
        Assert.True(frag >= 0 && z > frag, text);

        // ヘッダの指摘は、2 つの解析設定から取り込まれても 1 件。
        Assert.Single(text.Split('\n'), line => line.Contains("Common.hlsli(", StringComparison.Ordinal) && line.Contains("HL0310", StringComparison.Ordinal));
    }

    /// <summary>入れ子の設定ファイルに知らない環境を書いたら、黙って飛ばさず、書いた行に報告する。</summary>
    [Fact]
    public async Task 入れ子の設定ファイルの知らない環境は報告する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "version: 1\nenvironment: hlsl\n");
        File.WriteAllText(Path.Combine(_workDirectory, "root.hlsl"), "float4 main() : SV_Target { return 1; }\n");
        Directory.CreateDirectory(Path.Combine(_workDirectory, "sub"));
        string nested = Path.Combine(_workDirectory, "sub", ".shaderlyn.yaml");
        File.WriteAllText(nested, "version: 1\nenvironment: unreel\n");
        File.WriteAllText(Path.Combine(_workDirectory, "sub", "b.hlsl"), "float4 main() : SV_Target { return 1; }\n");

        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([_workDirectory], BuiltInAnalyzers.All, output, error);
        string text = output.ToString();

        Assert.Contains("環境 'unreel' はありません", text, StringComparison.Ordinal);
        Assert.Contains(".shaderlyn.yaml(2", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 解析設定が複数あっても、変わっていなければキャッシュを使い回す。入れ子の設定ファイルを変えたら使い回さない。
    /// </summary>
    [Fact]
    public async Task 解析設定が複数あっても設定ファイルを変えたらキャッシュを使い回さない()
    {
        string cache = Path.Combine(_workDirectory, "cache.json");
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "version: 1\nenvironment: hlsl\n");
        File.WriteAllText(Path.Combine(_workDirectory, "root.hlsl"), "float4 main() : SV_Target { return 1; }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), "#version 450\nvoid main() {}\n");
        Directory.CreateDirectory(Path.Combine(_workDirectory, "sub"));
        string nested = Path.Combine(_workDirectory, "sub", ".shaderlyn.yaml");
        File.WriteAllText(nested, "version: 1\nenvironment: hlsl\n");
        File.WriteAllText(Path.Combine(_workDirectory, "sub", "b.hlsl"), "float4 main() : SV_Target { return 1; }\n");

        async Task<string> RunWithCacheAsync()
        {
            StringWriter output = new();
            StringWriter error = new();
            await Program.RunAsync([_workDirectory, "--cache", cache, "--timings"], BuiltInAnalyzers.All, output, error);
            return error.ToString();
        }

        await RunWithCacheAsync();
        Assert.Contains("前回の結果の使い回し 3 件 / 解析 0 件", await RunWithCacheAsync(), StringComparison.Ordinal);

        File.WriteAllText(nested, "version: 1\nenvironment: hlsl\nrules:\n  HL0301: none\n");
        Assert.Contains("前回の結果の使い回し 0 件 / 解析 3 件", await RunWithCacheAsync(), StringComparison.Ordinal);
    }

    /// <summary>名前を挙げたファイルは、拡張子に関わらず設定の環境で読む (今までどおり)。</summary>
    [Fact]
    public async Task 名前を挙げたファイルは拡張子に関わらず設定の環境で読む()
    {
        string file = Path.Combine(_workDirectory, "Lib.fx");
        File.WriteAllText(file, "float Lib( {\n");

        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([file, "--no-config", "--environment", "hlsl"], BuiltInAnalyzers.All, output, error);

        Assert.Contains("1 ファイルを解析", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("HL0001", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary><c>--env</c> は、解析設定が複数あれば見出しを 1 回だけ書き、解析設定ごとに区切る。</summary>
    [Fact]
    public async Task 環境の表示は解析設定ごとに区切り見出しは1回()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "Lit.shader"), "Shader \"Test\" { SubShader { Pass { } } }");
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), "#version 450\nvoid main() {}\n");

        (_, string output) = await RunAsync("--env");

        Assert.Single(output.Split('\n'), line => line.Contains("の解析環境", StringComparison.Ordinal));
        Assert.Contains("解析設定 1/2", output, StringComparison.Ordinal);
        Assert.Contains("解析設定 2/2: 環境 GLSL", output, StringComparison.Ordinal);
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
