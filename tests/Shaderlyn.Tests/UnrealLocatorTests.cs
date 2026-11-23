using System.Text.Json;
using Shaderlyn.Cli;
using Shaderlyn.Unreal;

namespace Shaderlyn.Tests;

/// <summary>
/// 設定ファイルに書いていない Unreal Engine のエンジンとプロジェクトを、解析するファイルから見つけること (<see cref="UnrealLocator"/>) の検証。
/// </summary>
/// <remarks>
/// エンジン本体は使わず、印のファイル (Build/Build.version と Shaders/Public/Platform.ush) を置いた同じ形のフォルダーを作る。
/// .uproject の EngineAssociation は空にする (プロジェクトを含むエンジンの木を上へ探す)。インストールしたエンジンの登録は PC で変わるので試さない。
/// </remarks>
public sealed class UnrealLocatorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "shaderlyn-unreal-locator-" + Guid.NewGuid().ToString("N"));

    public UnrealLocatorTests()
    {
        Directory.CreateDirectory(_root);
        Write("Engine/Build/Build.version", "{}");
        Write("Engine/Shaders/Public/Platform.ush", "// platform");
        Write("Engine/Shaders/Private/EngineLib.ush", "float EngineLib() { return 1; }\n");
        Write("Game/Game.uproject", "{ \"FileVersion\": 3, \"EngineAssociation\": \"\" }");
        Write("Game/Shaders/Private/GameLib.ush", "float GameLib() { return 2; }\n");
        Write("Game/Shaders/Private/Main.usf", Shader);
    }

    private const string Shader = """
        #include "/Engine/Private/EngineLib.ush"
        #include "/Project/Private/GameLib.ush"
        void MainPS(out float4 o : SV_Target0) { o = EngineLib() + GameLib() + missingName; }
        """;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    private string PathOf(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, string content)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void エンジンの中のファイルならエンジンを見つけ_プロジェクトは探さない()
    {
        // エンジンの上に .uproject があっても、エンジンのファイルのプロジェクトではない。
        Write("Stray.uproject", "{}");

        UnrealLocations found = UnrealLocator.Find(PathOf("Engine/Shaders/Private/EngineLib.ush"), stopAt: null);

        Assert.Equal(PathOf("Engine"), found.EngineDirectory);
        Assert.Null(found.ProjectDirectory);
    }

    [Fact]
    public void 印のファイルがないEngineのShadersはエンジンとみなさない()
    {
        Write("Fake/Engine/Shaders/Private/X.usf", "");

        Assert.Null(UnrealLocator.Find(PathOf("Fake/Engine/Shaders/Private/X.usf"), stopAt: PathOf("Fake")).EngineDirectory);
    }

    [Fact]
    public void uprojectからプロジェクトとエンジンを見つける()
    {
        UnrealLocations found = UnrealLocator.Find(PathOf("Game/Shaders/Private/Main.usf"), stopAt: null);

        Assert.Equal(PathOf("Game"), found.ProjectDirectory);
        Assert.Equal(PathOf("Engine"), found.EngineDirectory);
    }

    [Fact]
    public void 止める場所より上のuprojectは見ない()
    {
        UnrealLocations found = UnrealLocator.Find(PathOf("Game/Shaders/Private/Main.usf"), stopAt: PathOf("Game/Shaders"));

        Assert.Null(found.ProjectDirectory);
        Assert.Null(found.EngineDirectory);
    }

    [Fact]
    public void 同じフォルダーにuprojectが複数あれば使わない()
    {
        Write("Game/Other.uproject", "{}");

        UnrealLocations found = UnrealLocator.Find(PathOf("Game/Shaders/Private/Main.usf"), stopAt: null);

        Assert.Null(found.ProjectDirectory);
        Assert.Contains("2 個", found.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CLIは設定ファイルなしで取り込みを解決する()
    {
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync([PathOf("Game/Shaders/Private/Main.usf"), "--no-config"], BuiltInAnalyzers.All, output, error);

        // エンジンとプロジェクトの関数が見え、未定義の名前だけを報告する。
        Assert.Contains("'missingName'", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("'EngineLib'", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("'GameLib'", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 設定ファイルの指定を優先する()
    {
        Write("Game/.shaderlyn.yaml", "version: 1\nenvironment: unreal\nunreal:\n  engine: ../Elsewhere/Engine\n");
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync([PathOf("Game/Shaders/Private/Main.usf"), "--env"], BuiltInAnalyzers.All, output, error);

        Assert.Contains("/Engine → " + PathOf("Elsewhere/Engine/Shaders"), output.ToString(), StringComparison.Ordinal);
        Assert.Contains("/Project → " + PathOf("Game/Shaders"), output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Game", true)]
    [InlineData("Game/Shaders", false)]
    public async Task 言語サーバーはワークスペースのルートより上のuprojectを見ない(string workspace, bool resolves)
    {
        await using LanguageServerHarness harness = new(LanguageServer.DocumentUri.FromFilePath(PathOf("Game/Shaders/Private/Main.usf")));
        await harness.InitializeAsync(null, PathOf(workspace));
        await harness.OpenAsync(Shader);

        // 解決できれば未定義の名前 (CF0310) だけ、できなければ取り込みを 1 つも解決できない (TOOL0004)。
        JsonElement diagnostics = await harness.ReceiveDiagnosticsForAsync(
            harness.Uri,
            d => d.EnumerateArray().Any(x => x.GetProperty("code").GetString() is "CF0310" or "TOOL0004"));
        string[] codes = [.. diagnostics.EnumerateArray().Select(d => d.GetProperty("code").GetString() ?? string.Empty)];

        Assert.Equal(resolves, codes.Contains("CF0310"));
        Assert.Equal(!resolves, codes.Contains("TOOL0004"));
    }
}
