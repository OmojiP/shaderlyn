using System.Text.RegularExpressions;
using Shaderlyn.Cli.Environments;
using Shaderlyn.Semantics;

namespace Shaderlyn.Tests;

/// <summary>
/// エディタの拡張機能が対象にする拡張子が、言語サーバーの解析する拡張子と揃っていることの試験。
/// </summary>
/// <remarks>
/// 拡張子の一覧は、サーバー (各環境の Extensions と ShaderSourceKinds)、VS Code (documentSelector)、
/// Visual Studio (ShaderContentType)、Rider (shaderExtensions) の 4 か所に別々に書いてある。
/// 1 か所だけ増やし忘れると、そのエディタでだけ指摘が出なくなり、気づきにくい。
/// </remarks>
public partial class EditorExtensionListTests
{
    /// <summary>
    /// サーバーは解析するが、エディタでは対象にしない拡張子。
    /// </summary>
    /// <remarks>
    /// <c>.mesh</c> (GLSL のメッシュシェーダー) は Unity のメッシュのアセットと同じ拡張子なので、エディタでは選ばない
    /// (<c>environment: glsl</c> を指定した CLI でだけ解析する。docs/guide/environments/glsl.md)。
    /// </remarks>
    private static readonly string[] EditorExcluded = [".mesh"];

    [Fact]
    public void VSCodeとVisualStudioとRiderの対象の拡張子が揃っている()
    {
        string[] vscode = VSCodeExtensions();
        string[] visualStudio = VisualStudioExtensions();
        string[] rider = RiderExtensions();

        Assert.NotEmpty(vscode);
        Assert.Equal(vscode, visualStudio);
        Assert.Equal(vscode, rider);
    }

    [Fact]
    public void エディタの対象の拡張子が言語サーバーの解析する拡張子と揃っている()
    {
        string[] editor = VSCodeExtensions();

        // HLSL として解析する拡張子は ShaderSourceKinds が決める。一覧を持たないので、エディタの側の拡張子で問い合わせる。
        IEnumerable<string> hlsl = editor.Where(e => ShaderSourceKinds.FromPath("a" + e) == ShaderSourceKind.Hlsl);

        string[] server =
        [
            .. new[] { ".shader" }
                .Concat(hlsl)
                .Concat(GlslAnalysisEnvironment.Extensions)
                .Concat(GodotAnalysisEnvironment.Extensions)
                .Concat(SlangAnalysisEnvironment.Extensions)
                .Concat(WgslAnalysisEnvironment.Extensions)
                .Except(EditorExcluded, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(server, editor);

        // HLSL として解析する拡張子は、エディタの側に欠けていると上の比較で見落とすので、代表を直接確かめる。
        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal) { ".hlsl", ".hlsli", ".cginc", ".hlslinc", ".compute", ".usf", ".ush" },
            editor.ToHashSet(StringComparer.Ordinal));
    }

    private static string[] VSCodeExtensions()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot(), "editors", "vscode", "src", "extension.ts"));

        return Normalize(VSCodePattern().Matches(source).SelectMany(m => m.Groups[1].Success
            ? m.Groups[1].Value.Split(',')
            : [m.Groups[2].Value]));
    }

    private static string[] VisualStudioExtensions()
    {
        string source = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot(), "editors", "visualstudio", "Shaderlyn.VisualStudio", "ShaderContentType.cs"));

        return Normalize(VisualStudioPattern().Matches(source).Select(m => m.Groups[1].Value));
    }

    private static string[] RiderExtensions()
    {
        string source = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot(), "editors", "rider", "src", "main", "kotlin", "dev", "shaderlyn", "rider", "ShaderlynLspIntegrationProvider.kt"));
        Match set = RiderSetPattern().Match(source);
        Assert.True(set.Success, "Rider の shaderExtensions が見つかりません。");

        return Normalize(RiderItemPattern().Matches(set.Groups[1].Value).Select(m => m.Groups[1].Value));
    }

    private static string[] Normalize(IEnumerable<string> extensions)
        => [.. extensions.Select(e => "." + e.Trim().TrimStart('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>documentSelector の <c>pattern: "**/*.shader"</c> と <c>pattern: "**/*.{a,b}"</c>。</summary>
    [GeneratedRegex(""""pattern:\s*"\*\*/\*\.(?:\{([^}]*)\}|([A-Za-z0-9]+))"""")]
    private static partial Regex VSCodePattern();

    /// <summary><c>[FileExtension(".shader")]</c>。</summary>
    [GeneratedRegex("""\[FileExtension\("([^"]+)"\)\]""")]
    private static partial Regex VisualStudioPattern();

    /// <summary><c>private val shaderExtensions = setOf(...)</c> の中身。</summary>
    [GeneratedRegex("""shaderExtensions\s*=\s*setOf\(([^)]*)\)""")]
    private static partial Regex RiderSetPattern();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex RiderItemPattern();
}
