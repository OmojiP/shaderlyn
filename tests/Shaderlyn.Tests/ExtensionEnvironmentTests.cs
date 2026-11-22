using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Shaderlyn.Cli;
using Shaderlyn.Cli.Environments;
using Shaderlyn.Testing;

namespace Shaderlyn.Tests;

/// <summary>
/// 拡張子ごとに、どの環境で解析するかの検証 (CLI のフォルダーの指定・ファイルの直接指定、言語サーバー、<see cref="ShaderRuleVerifier"/>)。
/// </summary>
/// <remarks>
/// <para>
/// 4 か所で別々に決めていると、同じファイルがテストと CLI とエディタで違う環境になる
/// (<c>.usf</c> がテストでは Unreal Engine、CLI では Unity だった)。表を 1 つにして、すべてがそれに従うことを確かめる。
/// </para>
/// <para>
/// エディタが言語サーバーに渡さない拡張子 (<c>.mesh</c>・<c>.fx</c>) は、<see cref="EditorExtensionListTests"/> が確かめる。
/// </para>
/// </remarks>
public sealed class ExtensionEnvironmentTests : IDisposable
{
    private const string Unity = "unity";
    private const string Hlsl = "hlsl";
    private const string Unreal = "unreal";
    private const string Glsl = "glsl";
    private const string Godot = "godot";
    private const string Slang = "slang";
    private const string Wgsl = "wgsl";

    /// <summary>各環境がフォルダーから集める拡張子。</summary>
    private static readonly ImmutableDictionary<string, ImmutableArray<string>> CollectedExtensions =
        new Dictionary<string, ImmutableArray<string>>
        {
            [Unity] = [".shader", ".compute"],
            [Hlsl] = [".hlsl"],
            [Unreal] = [".usf"],
            [Glsl] = GlslAnalysisEnvironment.Extensions,
            [Godot] = GodotAnalysisEnvironment.Extensions,
            [Slang] = SlangAnalysisEnvironment.Extensions,
            [Wgsl] = WgslAnalysisEnvironment.Extensions,
        }.ToImmutableDictionary();

    /// <summary>
    /// 環境を決めていない場合の表。
    /// 列は、拡張子・CLI でフォルダーを指定 (<see langword="null"/> は解析しない)・CLI でファイルを直接指定・エディタで開く・<see cref="ShaderRuleVerifier"/>。
    /// </summary>
    public static TheoryData<string, string?, string, string, string> WithoutEnvironment => new()
    {
        // Unity
        { ".shader", Unity, Unity, Unity, Unity },
        { ".compute", Unity, Unity, Unity, Unity },
        { ".hlsl", null, Unity, Unity, Unity },
        { ".hlsli", null, Unity, Unity, Unity },
        { ".cginc", null, Unity, Unity, Unity },
        { ".hlslinc", null, Unity, Unity, Unity },
        { ".fx", null, Unity, Unity, Unity },

        // Unreal Engine
        { ".usf", Unreal, Unreal, Unreal, Unreal },
        { ".ush", null, Unreal, Unreal, Unreal },

        // GLSL・Godot・Slang・WGSL
        { ".vert", Glsl, Glsl, Glsl, Glsl },
        { ".frag", Glsl, Glsl, Glsl, Glsl },
        { ".comp", Glsl, Glsl, Glsl, Glsl },
        { ".geom", Glsl, Glsl, Glsl, Glsl },
        { ".tesc", Glsl, Glsl, Glsl, Glsl },
        { ".tese", Glsl, Glsl, Glsl, Glsl },
        { ".task", Glsl, Glsl, Glsl, Glsl },
        { ".glsl", Glsl, Glsl, Glsl, Glsl },
        { ".rgen", Glsl, Glsl, Glsl, Glsl },
        { ".rint", Glsl, Glsl, Glsl, Glsl },
        { ".rahit", Glsl, Glsl, Glsl, Glsl },
        { ".rchit", Glsl, Glsl, Glsl, Glsl },
        { ".rmiss", Glsl, Glsl, Glsl, Glsl },
        { ".rcall", Glsl, Glsl, Glsl, Glsl },
        { ".mesh", null, Unity, Unity, Unity },
        { ".gdshader", Godot, Godot, Godot, Godot },
        { ".gdshaderinc", Godot, Godot, Godot, Godot },
        { ".slang", Slang, Slang, Slang, Slang },
        { ".wgsl", Wgsl, Wgsl, Wgsl, Wgsl },
    };

    /// <summary>
    /// 環境を決めている場合の表。
    /// 列は、書いた環境・拡張子・CLI でフォルダーを指定 (<see langword="null"/> は解析しない)・CLI でファイルを直接指定・エディタで開く。
    /// </summary>
    public static TheoryData<string, string, string?, string, string> WithEnvironment => new()
    {
        { Unity, ".shader", Unity, Unity, Unity },
        { Unity, ".usf", null, Unity, Unity },
        { Unity, ".ush", null, Unity, Unity },
        { Unity, ".hlsl", null, Unity, Unity },
        { Unity, ".vert", Glsl, Unity, Glsl },
        { Unity, ".wgsl", Wgsl, Unity, Wgsl },
        { Hlsl, ".hlsl", Hlsl, Hlsl, Hlsl },
        { Hlsl, ".usf", null, Hlsl, Hlsl },
        { Hlsl, ".ush", null, Hlsl, Hlsl },
        { Hlsl, ".gdshader", Godot, Hlsl, Godot },
        { Unreal, ".usf", Unreal, Unreal, Unreal },
        { Unreal, ".ush", null, Unreal, Unreal },
        { Unreal, ".hlsl", null, Unreal, Unreal },
        { Unreal, ".slang", Slang, Unreal, Slang },
        { Glsl, ".vert", Glsl, Glsl, Glsl },
        { Glsl, ".mesh", Glsl, Glsl, Glsl },
        { Glsl, ".usf", null, Glsl, Glsl },
        { Glsl, ".wgsl", Wgsl, Glsl, Wgsl },
        { Godot, ".gdshader", Godot, Godot, Godot },
        { Godot, ".usf", null, Godot, Godot },
        { Slang, ".slang", Slang, Slang, Slang },
        { Wgsl, ".wgsl", Wgsl, Wgsl, Wgsl },
        { Wgsl, ".frag", Glsl, Wgsl, Glsl },
    };

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-extension-env-{Guid.NewGuid():N}");

    public ExtensionEnvironmentTests() => Directory.CreateDirectory(_workDirectory);

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

    [Theory]
    [MemberData(nameof(WithoutEnvironment))]
    public void 環境を決めていなければ表のとおりの環境で解析する(
        string extension, string? folder, string explicitFile, string editor, string verifier)
    {
        ImmutableArray<string> unity = CollectedExtensions[Unity];

        Assert.Equal(folder, AnalysisSettings.EnvironmentForFile(extension, Unity, unity, environmentChosen: false, explicitFile: false));
        Assert.Equal(explicitFile, AnalysisSettings.EnvironmentForFile(extension, Unity, unity, environmentChosen: false, explicitFile: true));
        Assert.Equal(editor, AnalysisSettings.EnvironmentForOpenedFile(extension, unity, environmentChosen: false) ?? Unity);
        Assert.Equal(verifier, VerifierEnvironmentOf("Shaders/Test" + extension));
    }

    [Theory]
    [MemberData(nameof(WithEnvironment))]
    public void 環境を決めていれば表のとおりの環境で解析する(
        string environment, string extension, string? folder, string explicitFile, string editor)
    {
        ImmutableArray<string> collected = CollectedExtensions[environment];

        Assert.Equal(folder, AnalysisSettings.EnvironmentForFile(extension, environment, collected, environmentChosen: true, explicitFile: false));
        Assert.Equal(explicitFile, AnalysisSettings.EnvironmentForFile(extension, environment, collected, environmentChosen: true, explicitFile: true));
        Assert.Equal(editor, AnalysisSettings.EnvironmentForOpenedFile(extension, collected, environmentChosen: true) ?? environment);
    }

    /// <summary>
    /// <see cref="ShaderRuleVerifier"/> は、CLI でファイルを直接指定したときと同じ環境で解析する。表に無い拡張子でも同じになる。
    /// </summary>
    [Theory]
    [InlineData(".txt")]
    [InlineData(".USF")]
    [InlineData(".Vert")]
    [InlineData("")]
    public void ShaderRuleVerifierはCLIでファイルを直接指定したときと同じ環境にする(string extension)
    {
        string? cli = AnalysisSettings.EnvironmentForFile(
            extension, Unity, CollectedExtensions[Unity], environmentChosen: false, explicitFile: true);

        Assert.Equal(cli, VerifierEnvironmentOf("Shaders/Test" + extension));
    }

    /// <summary>設定ファイルの環境を作れなかったとき (知らない環境の名前) は、その下の HLSL のファイルを解析しない。</summary>
    [Fact]
    public void 設定ファイルの環境を作れなければusfを解析しない()
    {
        Assert.Null(AnalysisSettings.EnvironmentForFile(".usf", null, [], environmentChosen: false, explicitFile: false));
        Assert.Equal(Glsl, AnalysisSettings.EnvironmentForFile(".vert", null, [], environmentChosen: false, explicitFile: false));
    }

    // 以下は、表の判定が CLI と言語サーバーで実際に使われていることを確かめる。
    // Unity は UNITY_VERSION を、Unreal Engine は COMPILER_HLSL を定義するので、どちらで解析したかが構文エラーの有無で分かる。

    /// <summary>Unity で解析すると構文エラーになり、Unreal Engine では誤りの無いシェーダー。</summary>
    private const string UnrealOnly = """
        #ifdef UNITY_VERSION
        float UnityOnly( {
        #endif
        #ifndef COMPILER_HLSL
        float NotUnreal( {
        #endif
        float4 MainPS() : SV_Target0 { return 0; }
        """;

    [Fact]
    public async Task 設定ファイルが無ければフォルダーのusfをUnrealEngineで解析しushは単体で解析しない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "Lit.shader"), "Shader \"Test\" { SubShader { Pass { } } }");
        File.WriteAllText(Path.Combine(_workDirectory, "Pass.usf"), UnrealOnly);
        File.WriteAllText(Path.Combine(_workDirectory, "Common.ush"), "float Broken( {\n");

        (ExitCode code, string output) = await RunCliAsync(_workDirectory);

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("2 ファイルを解析", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Common.ush", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 環境を決めていればフォルダーのusfを解析しない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "Lit.shader"), "Shader \"Test\" { SubShader { Pass { } } }");
        File.WriteAllText(Path.Combine(_workDirectory, "Pass.usf"), UnrealOnly);

        (_, string output) = await RunCliAsync(_workDirectory, "--environment", Unity);

        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Pass.usf", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 設定ファイルが無ければ直接指定したushをUnrealEngineで解析する()
    {
        string header = Path.Combine(_workDirectory, "Common.ush");
        File.WriteAllText(header, UnrealOnly);

        (ExitCode code, string output) = await RunCliAsync(header);

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 設定ファイルが無ければエディタで開いたusfをUnrealEngineで解析する()
    {
        string uri = LanguageServer.DocumentUri.FromFilePath(Path.Combine(_workDirectory, "Pass.usf"));
        await using LanguageServerHarness harness = new(uri);
        await harness.InitializeAsync(null, _workDirectory);
        await harness.OpenAsync(UnrealOnly);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        Assert.DoesNotContain(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "CF0001");
    }

    [Fact]
    public async Task 設定ファイルでUnityを選べばエディタで開いたusfをUnityで解析する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "environment: unity\n");
        string uri = LanguageServer.DocumentUri.FromFilePath(Path.Combine(_workDirectory, "Pass.usf"));
        await using LanguageServerHarness harness = new(uri);
        await harness.InitializeAsync(null, _workDirectory);
        await harness.OpenAsync(UnrealOnly);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsForAsync(uri, "CF0001");

        Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "CF0001");
    }

    /// <summary>設定ファイルを読まずに CLI を実行する。</summary>
    private static async Task<(ExitCode Code, string Output)> RunCliAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync([.. args, "--no-config"], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return (code, output.ToString());
    }

    /// <summary><see cref="ShaderRuleVerifier"/> がパスの拡張子から決める環境。</summary>
    private static string VerifierEnvironmentOf(string filePath)
        => (string)typeof(ShaderRuleVerifier)
            .GetMethod("EnvironmentOf", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [filePath])!;
}
