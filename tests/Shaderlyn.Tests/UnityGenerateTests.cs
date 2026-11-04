using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// <c>--unity-generate</c> (Unity のプロジェクトの Library と Unity Editor から、解析に要るヘッダを書き出す) と、
/// 書き出したもの (設定ファイルの <c>unity: generated</c>) を読む解析の検証。
/// </summary>
/// <remarks>
/// 偽のプロジェクト (<c>Library/PackageCache</c> にパッケージ 1 つ) と偽の Editor (<c>CGIncludes</c>) を一時フォルダーに作って試す。
/// 確かめの答えは <see cref="Program.ReadConfirmation"/> を差し替えて渡すので、このクラスのテストは並べて動かさない。
/// </remarks>
[Collection(nameof(UnityGenerateTests))]
public sealed class UnityGenerateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"shaderlyn-unity-generate-{Guid.NewGuid():N}");

    private readonly Func<string?> _originalReader = Program.ReadConfirmation;

    public UnityGenerateTests()
    {
        Directory.CreateDirectory(_root);
        CreateProject(Project);
        CreateEditor(Editor);
    }

    private string Project => Path.Combine(_root, "Project");

    private string Editor => Path.Combine(_root, "Editor", "Data");

    private string Output => Path.Combine(_root, "Generated");

    public void Dispose()
    {
        Program.ReadConfirmation = _originalReader;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    [Fact]
    public async Task 一覧を出して確かめてから書き出す()
    {
        Program.ReadConfirmation = () => "y";
        (ExitCode code, string output, _) = await GenerateAsync();

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Unity Companion License (1 個):", output, StringComparison.Ordinal);
        Assert.Contains("com.example.toon", output, StringComparison.Ordinal);
        Assert.Contains("ライセンスが分からない", output, StringComparison.Ordinal);
        Assert.Contains("書き出しますか? [y/n]", output, StringComparison.Ordinal);

        string package = Path.Combine(Output, "Library", "PackageCache", "com.example.toon@abc123");
        Assert.True(File.Exists(Path.Combine(package, "ShaderLibrary", "Toon.hlsl")));
        Assert.True(File.Exists(Path.Combine(package, "LICENSE.md")));
        Assert.True(File.Exists(Path.Combine(package, "package.json")));
        Assert.True(File.Exists(Path.Combine(Output, "Editor", "Data", "Resources", "CGIncludes", "UnityCG.cginc")));
        Assert.True(File.Exists(Path.Combine(Output, "ShaderlynUnityGeneration.tsv")));

        // シェーダーでないもの、Unity がアセットとして扱わないフォルダー (名前が ~ で終わる)、.meta は写さない。
        Assert.False(File.Exists(Path.Combine(package, "Runtime", "Toon.cs")));
        Assert.False(Directory.Exists(Path.Combine(package, "Native~")));
        Assert.False(File.Exists(Path.Combine(package, "LICENSE.md.meta")));

        // 記録にはマシンに依存するパスを書かない。
        string record = File.ReadAllText(Path.Combine(Output, "ShaderlynUnityGeneration.tsv"));
        Assert.DoesNotContain(_root, record, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project-version\t9999.1.0f1", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task nと答えれば何も書かない()
    {
        Program.ReadConfirmation = () => "n";
        (ExitCode code, string output, _) = await GenerateAsync();

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("書き出しませんでした", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task 入力を待てなければyesを付けるよう伝える()
    {
        Program.ReadConfirmation = () => null;
        (ExitCode code, _, string error) = await GenerateAsync();

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains("--yes", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Output));

        (ExitCode withYes, _, _) = await GenerateAsync("--yes");
        Assert.Equal(ExitCode.Success, withYes);
        Assert.True(File.Exists(Path.Combine(Output, "ShaderlynUnityGeneration.tsv")));
    }

    [Fact]
    public async Task 書き出したものでないファイルのあるフォルダーには書かない()
    {
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "keep.txt"), "利用者のファイル");

        (ExitCode code, _, string error) = await GenerateAsync("--yes");

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains("--unity-generate で書き出したものでない", error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Output, "keep.txt")));
    }

    [Fact]
    public async Task 書き出し直すと古いパッケージを消す()
    {
        Assert.Equal(ExitCode.Success, (await GenerateAsync("--yes")).Code);

        // パッケージを上げた (ハッシュが変わった)。
        Directory.Move(
            Path.Combine(Project, "Library", "PackageCache", "com.example.toon@abc123"),
            Path.Combine(Project, "Library", "PackageCache", "com.example.toon@def456"));

        Assert.Equal(ExitCode.Success, (await GenerateAsync("--yes")).Code);
        Assert.False(Directory.Exists(Path.Combine(Output, "Library", "PackageCache", "com.example.toon@abc123")));
        Assert.True(Directory.Exists(Path.Combine(Output, "Library", "PackageCache", "com.example.toon@def456")));
    }

    /// <summary>
    /// Library と Editor の無いマシン (GitHub のホストランナー) を模す。プロジェクトを Library を除いて写し、
    /// 書き出したものだけで、パッケージと Editor のヘッダを取り込むシェーダーを解析できる。
    /// </summary>
    [Fact]
    public async Task 書き出したものでLibraryとEditorの無いプロジェクトを解析する()
    {
        Assert.Equal(ExitCode.Success, (await GenerateAsync("--yes")).Code);

        string ci = Path.Combine(_root, "CI");
        CopyDirectory(Path.Combine(Project, "Assets"), Path.Combine(ci, "Assets"));
        CopyDirectory(Path.Combine(Project, "Packages"), Path.Combine(ci, "Packages"));
        CopyDirectory(Path.Combine(Project, "ProjectSettings"), Path.Combine(ci, "ProjectSettings"));
        CopyDirectory(Output, Path.Combine(ci, ".shaderlyn", "unity"));
        File.WriteAllText(Path.Combine(ci, ".shaderlyn.yaml"), "version: 1\nunity:\n  generated: .shaderlyn/unity\n");

        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync(
            [Path.Combine(ci, "Assets", "Toon.shader"), "--unity-project", ci, "--config", Path.Combine(ci, ".shaderlyn.yaml")],
            BuiltInAnalyzers.All,
            output,
            error);

        string text = output + error.ToString();
        Assert.DoesNotContain("TOOL0004", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CF0021", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CF0101", text, StringComparison.Ordinal);

        // 書き出したもの (Unity のファイルの写し) の中には指摘を出さない。
        Assert.DoesNotContain(".shaderlyn", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--unity-project</c> (<c>unity: project</c>) を指定しなくても、書き出したもののパッケージと Editor のヘッダを読む。
    /// プロジェクトの指定はプロジェクトのファイル (<c>Assets/...</c>) の解決に要るが、書き出したものを読む条件ではない。
    /// </summary>
    [Fact]
    public async Task 書き出したものをプロジェクトの指定無しで読む()
    {
        Assert.Equal(ExitCode.Success, (await GenerateAsync("--yes")).Code);

        string ci = Path.Combine(_root, "CI");
        CopyDirectory(Path.Combine(Project, "Assets"), Path.Combine(ci, "Assets"));
        CopyDirectory(Output, Path.Combine(ci, ".shaderlyn", "unity"));
        File.WriteAllText(Path.Combine(ci, ".shaderlyn.yaml"), "version: 1\nunity:\n  generated: .shaderlyn/unity\n");

        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync(
            [Path.Combine(ci, "Assets", "Toon.shader"), "--config", Path.Combine(ci, ".shaderlyn.yaml")],
            BuiltInAnalyzers.All,
            output,
            error);

        string text = output + error.ToString();
        Assert.DoesNotContain("TOOL0004", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CF0021", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CF0101", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".shaderlyn", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 書き出した後に、Unity のバージョン・packages-lock.json・Library/PackageCache のパッケージが変わったら、解析を始めるときに伝える。
    /// 変わっていなければ何も言わない。
    /// </summary>
    [Fact]
    public async Task 書き出したものがプロジェクトと合わなければ伝える()
    {
        Assert.Equal(ExitCode.Success, (await GenerateAsync("--yes")).Code);
        File.WriteAllText(Path.Combine(Project, ".shaderlyn.yaml"), $"version: 1\nunity:\n  generated: {Output.Replace('\\', '/')}\n");

        Assert.DoesNotContain("書き出し直してください", await AnalyzeProjectAsync(), StringComparison.Ordinal);

        Directory.Move(
            Path.Combine(Project, "Library", "PackageCache", "com.example.toon@abc123"),
            Path.Combine(Project, "Library", "PackageCache", "com.example.toon@def456"));
        string changedPackage = await AnalyzeProjectAsync();
        Assert.Contains("Library/PackageCache のパッケージが変わりました (com.example.toon)", changedPackage, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(Project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 9999.2.0f1\n");
        string changedVersion = await AnalyzeProjectAsync();
        Assert.Contains("Unity のバージョンが変わりました (書き出したとき 9999.1.0f1、今 9999.2.0f1)", changedVersion, StringComparison.Ordinal);
        Assert.Contains("--unity-generate で書き出し直してください", changedVersion, StringComparison.Ordinal);
    }

    /// <summary>プロジェクトの設定ファイルで、プロジェクトのシェーダーを解析する。標準エラーを返す。</summary>
    private async Task<string> AnalyzeProjectAsync()
    {
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync(
            [Path.Combine(Project, "Assets", "Toon.shader"), "--unity-project", Project, "--unity-editor", Editor, "--config", Path.Combine(Project, ".shaderlyn.yaml")],
            BuiltInAnalyzers.All,
            output,
            error);
        return error.ToString();
    }

    private async Task<(ExitCode Code, string Output, string Error)> GenerateAsync(params string[] extra)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(
            ["--unity-project", Project, "--unity-editor", Editor, "--unity-generate", Output, "--no-config", .. extra],
            BuiltInAnalyzers.All,
            output,
            error);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>偽のプロジェクト。レジストリーのパッケージ (com.example.toon) と、ライセンスの分からないパッケージ (com.example.water)。</summary>
    private static void CreateProject(string project)
    {
        Write(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 9999.1.0f1\n");
        Write(Path.Combine(project, "Packages", "manifest.json"), """{ "dependencies": { "com.example.toon": "1.0.0", "com.example.water": "1.0.0" } }""");
        Write(Path.Combine(project, "Packages", "packages-lock.json"), """
            {
              "dependencies": {
                "com.example.toon": { "version": "1.0.0", "depth": 0, "source": "registry", "dependencies": {} },
                "com.example.water": { "version": "1.0.0", "depth": 0, "source": "registry", "dependencies": {} },
                "com.unity.modules.ai": { "version": "1.0.0", "depth": 0, "source": "builtin", "dependencies": {} }
              }
            }
            """);

        string toon = Path.Combine(project, "Library", "PackageCache", "com.example.toon@abc123");
        Write(Path.Combine(toon, "ShaderLibrary", "Toon.hlsl"), "float3 ToonRamp(float x) { return float3(x, x, x); }\n");
        Write(Path.Combine(toon, "Runtime", "Toon.cs"), "class Toon { }\n");
        Write(Path.Combine(toon, "Native~", "Plugin.h"), "int f();\n");
        Write(Path.Combine(toon, "LICENSE.md"), "com.example.toon copyright\n\nLicensed under the Unity Companion License for Unity-dependent projects.\n");
        Write(Path.Combine(toon, "LICENSE.md.meta"), "guid: 0\n");
        Write(Path.Combine(toon, "package.json"), """{ "name": "com.example.toon", "version": "1.0.0" }""");

        string water = Path.Combine(project, "Library", "PackageCache", "com.example.water@fff000");
        Write(Path.Combine(water, "ShaderLibrary", "Water.hlsl"), "float Wave(float x) { return x; }\n");

        Write(Path.Combine(project, "Assets", "Toon.shader"), """
            Shader "Example/Toon"
            {
                SubShader
                {
                    Pass
                    {
                        CGPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #include "UnityCG.cginc"
                        #include "Packages/com.example.toon/ShaderLibrary/Toon.hlsl"

                        float4 vert(float4 position : POSITION) : SV_POSITION { return UnityObjectToClipPos(position); }
                        float4 frag() : SV_Target { return float4(ToonRamp(0.5), 1); }
                        ENDCG
                    }
                }
            }
            """);
    }

    /// <summary>偽の Editor のデータフォルダー (Data/Resources/CGIncludes)。</summary>
    private static void CreateEditor(string editor)
        => Write(Path.Combine(editor, "Resources", "CGIncludes", "UnityCG.cginc"), "float4 UnityObjectToClipPos(float4 v) { return v; }\n");

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            Write(Path.Combine(destination, Path.GetRelativePath(source, file)), File.ReadAllText(file));
        }
    }
}
