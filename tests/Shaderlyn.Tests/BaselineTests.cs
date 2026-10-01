using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// ベースラインの検証。
/// </summary>
/// <remarks>
/// <b>既存プロジェクトへ導入する経路そのものなので、CLI を通しで検証する。</b>
/// 「既存の指摘を凍結する」「新規の混入だけを報告する」という
/// 2 つの振る舞いが両方成り立たないと導入に使えない。
/// </remarks>
public sealed class BaselineTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-baseline-{Guid.NewGuid():N}");

    public BaselineTests() => Directory.CreateDirectory(_workDirectory);

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

    private string BaselinePath => Path.Combine(_workDirectory, ".shaderlyn-baseline.json");

    private void WriteShader(string name, params string[] passLines)
    {
        string content = "Shader \"Test/Baseline\"\n"
            + "{\n"
            + "    SubShader\n"
            + "    {\n"
            + "        Pass\n"
            + "        {\n"
            + string.Join("\n", passLines.Select(line => "            " + line))
            + "\n        }\n"
            + "    }\n"
            + "}\n";

        File.WriteAllText(Path.Combine(_workDirectory, name), content);
    }

    private async Task<(ExitCode Code, string Output)> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [.. args, "--no-config"], BuiltInAnalyzers.All, output, error);

        return (code, output.ToString() + error.ToString());
    }

    [Fact]
    public async Task ベースラインを書き出して既存の指摘を凍結できる()
    {
        WriteShader("A.shader", "Cull Sideways", "ZWrite Perhaps");

        // 何も凍結していない状態では指摘が出る。
        (ExitCode before, _) = await RunAsync(_workDirectory);
        Assert.Equal(ExitCode.DiagnosticsFound, before);

        // 書き出しは終了コード 0 で終わる。導入時の操作を CI で失敗させない。
        (ExitCode write, string writeOutput) = await RunAsync(
            _workDirectory, "--write-baseline", BaselinePath);

        Assert.Equal(ExitCode.Success, write);
        Assert.Contains("ベースライン", writeOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(BaselinePath));

        // 凍結後は指摘が出ない。
        (ExitCode after, _) = await RunAsync(_workDirectory, "--baseline", BaselinePath);
        Assert.Equal(ExitCode.Success, after);
    }

    [Fact]
    public async Task ベースライン後に混入した指摘は報告する()
    {
        WriteShader("A.shader", "Cull Sideways");
        await RunAsync(_workDirectory, "--write-baseline", BaselinePath);

        // 新しい問題を足す。
        WriteShader("B.shader", "ZWrite Perhaps");

        (ExitCode code, string output) = await RunAsync(_workDirectory, "--baseline", BaselinePath);

        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("B.shader", output, StringComparison.Ordinal);
        Assert.DoesNotContain("A.shader", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 行がずれてもベースラインは外れない()
    {
        // 無関係な行を足しただけでベースラインが総崩れになるようでは、
        // 既存プロジェクトへの導入という目的を果たせない。
        WriteShader("A.shader", "Cull Sideways");
        await RunAsync(_workDirectory, "--write-baseline", BaselinePath);

        WriteShader("A.shader", "// 説明を足した", "ZTest LEqual", "Cull Sideways");

        (ExitCode code, _) = await RunAsync(_workDirectory, "--baseline", BaselinePath);
        Assert.Equal(ExitCode.Success, code);
    }

    [Fact]
    public async Task 同じ内容の指摘が増えたら報告する()
    {
        // 同じ行の内容が複数あると指紋が一致する。
        // 件数を持たないと、増えた分を見逃す。
        WriteShader("A.shader", "Cull Sideways");
        await RunAsync(_workDirectory, "--write-baseline", BaselinePath);

        WriteShader("A.shader", "Cull Sideways", "Cull Sideways");

        (ExitCode code, _) = await RunAsync(_workDirectory, "--baseline", BaselinePath);
        Assert.Equal(ExitCode.DiagnosticsFound, code);
    }

    [Fact]
    public async Task ベースラインの内容は実行のたびに変わらない()
    {
        // 実行のたびに差分が出るファイルはバージョン管理に置けない。
        WriteShader("A.shader", "Cull Sideways");
        WriteShader("B.shader", "ZWrite Perhaps");

        await RunAsync(_workDirectory, "--write-baseline", BaselinePath);
        string first = await File.ReadAllTextAsync(BaselinePath);

        await RunAsync(_workDirectory, "--write-baseline", BaselinePath);
        string second = await File.ReadAllTextAsync(BaselinePath);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ベースラインには相対パスを記録する()
    {
        // 絶対パスを記録すると、別のマシンやランナーで一切照合できない。
        WriteShader("A.shader", "Cull Sideways");
        await RunAsync(_workDirectory, "--write-baseline", BaselinePath);

        string content = await File.ReadAllTextAsync(BaselinePath);

        Assert.Contains("A.shader", content, StringComparison.Ordinal);
        Assert.DoesNotContain(_workDirectory, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 読み込めないベースラインはツールエラーにする()
    {
        // ベースラインが効いていない状態で解析を続けると、
        // 「新規の指摘だけを見ている」と誤解したまま運用されることになる。
        WriteShader("A.shader", "Cull Sideways");
        await File.WriteAllTextAsync(BaselinePath, "{ 壊れた JSON");

        (ExitCode code, string output) = await RunAsync(_workDirectory, "--baseline", BaselinePath);

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains("ベースライン", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 存在しないベースラインはツールエラーにする()
    {
        WriteShader("A.shader", "Cull Sideways");

        (ExitCode code, _) = await RunAsync(
            _workDirectory, "--baseline", Path.Combine(_workDirectory, "missing.json"));

        Assert.Equal(ExitCode.ToolError, code);
    }
}
