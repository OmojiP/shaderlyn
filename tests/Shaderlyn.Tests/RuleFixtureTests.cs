using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.Cli;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// 規則ごとに、実際に違反するシェーダーを置いて報告されることを確かめる。
/// </summary>
/// <remarks>
/// <para>
/// <b>規則を足したら、それを破るシェーダーも足す。</b>
/// 単体テストは中の作りを知ったうえで書くため、
/// 「その作りのまま壊れていない」ことしか確かめられない。
/// ここでは利用者が書くのと同じ形のシェーダーを 1 本置き、
/// 端から端まで通して報告されることを見る。
/// </para>
/// <para>
/// <see cref="すべての規則に違反するシェーダーがある"/> が、
/// 置き忘れを見つける。規則を足して違反シェーダーを置かなければ落ちる。
/// </para>
/// </remarks>
public sealed class RuleFixtureTests
{
    /// <summary>違反するシェーダーを置いてある場所。</summary>
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "rules");

    /// <summary>
    /// 違反するシェーダーを置けない規則と、その理由。
    /// </summary>
    /// <remarks>
    /// <b>ここに足すときは理由を書くこと。</b>
    /// 理由を書けないなら、それは置けないのではなく置いていないだけである。
    /// </remarks>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["TOOL0001"] = "アナライザが例外を投げたときの報告。シェーダーの内容では起こせない",
        ["TOOL0002"] = "ファイルを読めなかったときの報告。シェーダーの内容では起こせない",
        ["TOOL0003"] = "設定ファイルの誤りの報告。シェーダーの内容では起こせない",

        // 循環は取り込み先で起きるため、報告は取り込んだファイルの側に出る。
        // .shader の診断としては SL0002 のメッセージの中に現れる。
        // これは HL0320_の循環はSL0002の中に現れる で確かめている。
        ["HL0320"] = "循環は取り込み先で起きるため、報告が SL0002 のメッセージの中に入る",
    };

    /// <summary>違反するシェーダーを置いてある規則。</summary>
    public static TheoryData<string> Rules
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string id in FixtureIds())
            {
                // 置いてはあるが、別の形でしか報告されない規則は theory から外す。
                // その形は専用の検証で確かめる。
                if (!Exempt.ContainsKey(id))
                {
                    data.Add(id);
                }
            }

            return data;
        }
    }

    /// <summary>置いてある違反シェーダーの規則を列挙する。</summary>
    /// <returns>規則の名前。</returns>
    private static IEnumerable<string> FixtureIds()
        => Directory.EnumerateFiles(FixtureDirectory, "*.shader")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>();

    [Theory]
    [MemberData(nameof(Rules))]
    public void 違反するシェーダーでその規則を報告する(string ruleId)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(ruleId);

        Assert.True(
            diagnostics.Any(d => d.Id == ruleId),
            $"{ruleId} を報告するはずが、報告したのは "
            + $"[{string.Join(", ", diagnostics.Select(d => d.Id).Distinct().Order(StringComparer.Ordinal))}] だった");
    }

    [Fact]
    public void すべての規則に違反するシェーダーがある()
    {
        HashSet<string> covered = [.. FixtureIds()];

        List<string> missing =
        [
            .. RuleCatalog.GetShaderRules(BuiltInAnalyzers.All)
                .Select(d => d.Id)
                .Where(id => !covered.Contains(id) && !Exempt.ContainsKey(id))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            missing.Count == 0,
            $"違反するシェーダーが無い規則: {string.Join(", ", missing)}。"
            + $"fixtures/rules/<ID>.shader を足すか、理由を添えて Exempt へ入れること");
    }

    [Fact]
    public void 置けない理由を書いていない除外がない()
    {
        // 理由の無い除外は「置けない」ではなく「置いていない」である。
        Assert.All(Exempt, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
    }

    [Fact]
    public void HL0320_の循環はSL0002の中に現れる()
    {
        // include の循環は取り込み先で起きるため、.shader 自身の診断にはならない。
        // 見送った事実は SL0002 が伝える。
        ImmutableArray<Diagnostic> diagnostics = Analyze("HL0320");

        Diagnostic reported = Assert.Single(diagnostics, d => d.Id == "SL0002");

        Assert.Contains("HL0320", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void TOOL0004_はパッケージでないパスにUnityの直し方を案内しない()
    {
        // 違反シェーダーが引けないのは definitely/missing.hlsl で、Unity とは関係が無い。
        // ここで --unity-project や PackageCache を案内すると、効果の無い設定を試させることになる。
        Diagnostic reported = Assert.Single(Analyze("TOOL0004"), d => d.Id == "TOOL0004");
        string message = reported.GetMessage();

        Assert.DoesNotContain("PackageCache", message, StringComparison.Ordinal);
        Assert.DoesNotContain("--unity-project", message, StringComparison.Ordinal);
        Assert.Contains("--include-path", message, StringComparison.Ordinal);
    }

    /// <summary>違反するシェーダーを解析して診断を得る。</summary>
    /// <param name="ruleId">対象の規則。</param>
    /// <returns>検出した診断。</returns>
    private static ImmutableArray<Diagnostic> Analyze(string ruleId)
    {
        string path = Path.Combine(FixtureDirectory, $"{ruleId}.shader");
        SourceText text = SourceText.From(File.ReadAllText(path), path);
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            // 取り込みは 2 つの経路で解く。
            // Unity のヘッダは中身の要らない空のファイルとして与え、
            // 違反シェーダーの隣に置いた .hlsl は実際に読む。
            IncludeResolver = new CompositeIncludeResolver(
            [
                new UnityHeaderStubResolver(),
                new FileSystemIncludeResolver([FixtureDirectory]),
            ]),
        };

        ShaderCompilation compilation = ShaderCompilation.Create(text, tree, options);

        return new AnalyzerDriver(BuiltInAnalyzers.All)
            .Analyze(compilation.CreateAnalysisTarget());
    }

    /// <summary>
    /// Unity のヘッダを、中身の無いファイルとして返す。
    /// </summary>
    /// <remarks>
    /// <b>Unity がインストールされていなくても検証できなければならない。</b>
    /// ここで見たいのは規則が働くかどうかであって、
    /// Unity のヘッダの中身ではない。
    /// 解決できたことにしないと、依存が揃っていない扱いになり
    /// プロパティと uniform の対応検査が丸ごと見送られる。
    /// </remarks>
    internal sealed class UnityHeaderStubResolver : IIncludeResolver
    {
        /// <inheritdoc/>
        public bool TryResolve(
            string path,
            string includingFilePath,
            [NotNullWhen(true)] out SourceText? resolved)
        {
            resolved = path.StartsWith("Packages/", StringComparison.Ordinal)
                ? SourceText.From(string.Empty, path)
                : null;

            return resolved is not null;
        }
    }
}
