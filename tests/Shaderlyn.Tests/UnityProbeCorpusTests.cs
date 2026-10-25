using System.Collections.Immutable;
using System.Text;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// Unity のコンパイラの判定 (<see cref="UnityCompilerSelection"/>) を、Unity で実測した結果と照らし合わせる。
/// </summary>
/// <remarks>
/// <para>
/// <b>実測の結果はリポジトリに入れない。</b> ShaderProbePack を入れた Unity プロジェクトを並べたフォルダー
/// (<c>p20223</c> / <c>p63</c> / <c>p66_dxc</c> など) を、環境変数 <c>SHADERLYN_UNITY_PROBE</c> に渡したときだけ動く。
/// </para>
/// <para>
/// 各プロジェクトの <c>ShaderProbeCompileMatrix_&lt;版&gt;_&lt;構成&gt;.md</c> は、パスと構成ごとに、
/// Unity がどのコンパイラでコンパイルしたか (<c>FXC</c> / <c>DXC</c> / <c>error</c>) を記録している。
/// 構成 <c>dx11</c> の表の D3D の列を DX11 の判定と、<c>dx12</c> の表の D3D の列と Vulkan の列を DX12 と Vulkan の判定と比べる。
/// Metal の列は、6.5 以前の 16 ビットの型のエラー (SPIRV-Cross) が混じるので比べない。
/// 表は <c>enum</c> のエラーの有無で FXC と DXC を見分けているので、<c>enum</c> を使うシェーダーだけを比べる。
/// </para>
/// <para>違いの一覧は、フォルダーの隣に <c>report-unity-probe.txt</c> として書き出す。</para>
/// </remarks>
public sealed class UnityProbeCorpusTests(ITestOutputHelper output)
{
    /// <summary>実測の結果の置き場所を渡す環境変数。</summary>
    private const string ProbeVariable = "SHADERLYN_UNITY_PROBE";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Unityで実測したコンパイラと判定が一致する()
    {
        string? root = Environment.GetEnvironmentVariable(ProbeVariable);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _output.WriteLine($"{ProbeVariable} が無いため飛ばします。");
            return;
        }

        List<string> mismatches = [];
        int compared = 0;

        foreach (string project in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            UnityProjectSettings settings = UnityProjectSettings.Read(project);

            if (settings.EditorVersion is not { } version)
            {
                continue;
            }

            foreach (string config in (string[])["dx11", "dx12"])
            {
                string matrix = Path.Combine(project, $"ShaderProbeCompileMatrix_{version}_{config}.md");

                if (File.Exists(matrix))
                {
                    compared += Compare(project, version, config, matrix, settings, mismatches);
                }
            }
        }

        StringBuilder report = new();
        report.AppendLine($"比べた数: {compared}、違い: {mismatches.Count}");
        mismatches.ForEach(m => report.AppendLine(m));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\', '/'))!, "report-unity-probe.txt"), report.ToString());
        _output.WriteLine(report.ToString());

        Assert.True(compared > 0, "比べる行がありませんでした。");
        Assert.Empty(mismatches);
    }

    /// <summary>1 つの表を比べる。</summary>
    /// <returns>比べた数。</returns>
    private static int Compare(
        string project,
        string version,
        string config,
        string matrixPath,
        UnityProjectSettings settings,
        List<string> mismatches)
    {
        string[] lines = File.ReadAllLines(matrixPath);
        string? header = lines.FirstOrDefault(l => l.StartsWith("| Shader |", StringComparison.Ordinal));

        if (header is null)
        {
            return 0;
        }

        string[] columns = Cells(header);
        int d3d = Array.FindIndex(columns, c => c == (config == "dx11" ? "D3D11" : "D3D12"));
        d3d = d3d >= 0 ? d3d : Array.IndexOf(columns, "D3D");
        int vulkan = Array.IndexOf(columns, "Vulkan");

        UnityOptions options = new()
        {
            EditorVersion = version,
            GraphicsApis = [UnityGraphicsApi.Direct3D11, UnityGraphicsApi.Direct3D12, UnityGraphicsApi.Vulkan],
            DxcBackendApis = settings.DxcBackendApis,
        };

        Dictionary<string, (ImmutableArray<ImmutableArray<HlslCompileTarget>> Passes, List<string> Labels)> shaders = new(StringComparer.Ordinal);
        int compared = 0;

        foreach (string line in lines.Where(l => l.StartsWith("| `", StringComparison.Ordinal)))
        {
            string[] cells = Cells(line);
            string shader = cells[0].Trim('`');
            string pass = cells[1];
            ImmutableHashSet<string> keywords = cells[2] == "(none)" ? [] : [.. cells[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)];

            if (!shaders.TryGetValue(shader, out var entry))
            {
                string path = Path.Combine(project, "Assets", "ShaderProbePack", "ShaderProbePack", shader.Replace('/', Path.DirectorySeparatorChar));

                // 表は enum のエラーの有無で FXC と DXC を見分ける。enum を使わないシェーダーは、通れば DXC と記録される。
                if (!File.Exists(path) || !File.ReadAllText(path).Contains("enum ", StringComparison.Ordinal))
                {
                    continue;
                }

                entry = (Analyze(path, options), []);
                shaders[shader] = entry;
            }

            if (!entry.Labels.Contains(pass))
            {
                entry.Labels.Add(pass);
            }

            int index = entry.Labels.IndexOf(pass);

            if (index >= entry.Passes.Length)
            {
                continue;
            }

            ImmutableArray<HlslCompileTarget> targets = entry.Passes[index];
            string where = $"{Path.GetFileName(project)} {config} {shader} {pass} [{cells[2]}]";

            compared += Check(where, config == "dx11" ? "dx11" : "dx12", cells[d3d], targets, keywords, mismatches);

            if (config == "dx12" && vulkan >= 0)
            {
                compared += Check(where, "vulkan", cells[vulkan], targets, keywords, mismatches);
            }
        }

        return compared;
    }

    /// <summary>1 つの欄を比べる。</summary>
    /// <returns>比べたら 1。</returns>
    private static int Check(
        string where,
        string platform,
        string actual,
        ImmutableArray<HlslCompileTarget> targets,
        ImmutableHashSet<string> keywords,
        List<string> mismatches)
    {
        HlslCompileTarget[] applied = [.. targets.Where(t => t.Platform == platform && t.AppliesTo(keywords))];
        string expected = applied.Length != 1
            ? $"({applied.Length} 個)"
            : !applied[0].IsUsable
                ? "error"
                : applied[0].Dialect.Compiler == HlslCompiler.Dxc ? "DXC" : "FXC";

        string observed = actual.StartsWith("DXC", StringComparison.Ordinal) ? "DXC"
            : actual.StartsWith("FXC", StringComparison.Ordinal) ? "FXC"
            : "error";

        // 使えない API のコンパイル先は、コンパイルを省くか、作っても使わない (表では error か DXC)。
        if (expected != observed && !(expected == "error" && observed == "DXC"))
        {
            mismatches.Add($"{where} {platform}: 実測 {actual} / 判定 {expected} ({string.Join("; ", applied.Select(t => t.Describe()))})");
        }

        return 1;
    }

    /// <summary>シェーダーをパスごとのコンパイル先にする。</summary>
    private static ImmutableArray<ImmutableArray<HlslCompileTarget>> Analyze(string path, UnityOptions options)
    {
        SourceText text = SourceText.Load(path);
        ShaderCompilation compilation = UnityCompilation.Create(text, ShaderLabSyntaxTree.Parse(text),
            new SemanticsOptions { IncludeResolver = new CFamily.Preprocessing.FileSystemIncludeResolver([]) },
            options);

        return [.. compilation.Programs.Select(p => p.Tree.Hlsl().CompileTargets)];
    }

    /// <summary>表の 1 行を欄に分ける。</summary>
    private static string[] Cells(string line)
        => [.. line.Trim().Trim('|').Split('|').Select(c => c.Trim())];
}
