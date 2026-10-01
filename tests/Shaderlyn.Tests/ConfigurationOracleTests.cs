using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// すべての構成を 1 つずつ解析した結果を正解として、既定の解析と突き合わせる。
/// </summary>
/// <remarks>
/// <para>
/// <b>既定の解析は、構成の組み合わせを近似している。</b>
/// 両方の分岐を 1 本の木に並べる経路と、構成ごとに展開して突き合わせる経路があり、
/// どちらも「どの構成でその誤りが起きるか」を条件で推し量っている。
/// 近似どうしを比べても (<see cref="BothBranchConsistencyTests"/>)、両方に同じ穴があれば見つからない。
/// </para>
/// <para>
/// <b>構成を 1 つに固定した解析は近似しない。</b>
/// 小さなシェーダーなら、Unity が作る構成をすべて列挙できる。
/// 各構成で見つかった誤りの和が、そのシェーダーの誤りの正解である。
/// 既定の解析にしか無い指摘は誤検出で、正解にしか無い指摘は見逃しである。
/// </para>
/// <para>
/// 構成の列挙は、実装 (<see cref="ShaderSymbols"/>) を使わずにここで独自に行う。
/// 実装と同じ読み方をすると、読み方の誤りが両方に入って見えなくなる。
/// </para>
/// </remarks>
public sealed partial class ConfigurationOracleTests
{
    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>突き合わせる fixture のフォルダー。</summary>
    private static readonly string[] FixtureFolders = ["conditional", "valid", "rules"];

    /// <summary>突き合わせないルール。</summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>
    ///     SL0002 / SL0003 / SL0004 / TOOL0004 は「どこまで調べたか」の報告で、構成を固定すると出方が変わるのが正しい
    ///   </description></item>
    ///   <item><description>
    ///     SL1001 / SL1002 / SL1004 / HL0330 / HL0331 は、構成をまたいで「どこにも無い」ことを判断する。
    ///     1 つの構成で無いことは、どの構成でも無いことを意味しない
    ///   </description></item>
    ///   <item><description>
    ///     HL0353 は、構成をまたいで「型を書き分けている」ことを判断する。
    ///     構成を固定すると、その構成の宣言しか残らない。
    ///     <c>float3</c> を <c>float</c> へ渡す形はただの切り捨てになり、
    ///     それは意図して書く場面が多いので報告しない。
    ///     書き分けたという事実そのものが、構成を固定すると消える
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static readonly HashSet<string> FamilyRules =
        ["SL0002", "SL0003", "SL0004", "TOOL0004", "SL1001", "SL1002", "SL1004", "HL0330", "HL0331", "HL0353"];

    /// <summary>構成を固定すると別の ID で出る、同じ誤り。</summary>
    /// <remarks>
    /// 既定の解析は「その構成には定義が無い」(HL0311 / HL0313 / HL0315) と条件付きで言う。
    /// 構成を固定すれば、それはただの「無い」(HL0310 / HL0312) である。
    /// </remarks>
    private static readonly Dictionary<string, string> SameError = new(StringComparer.Ordinal)
    {
        ["HL0311"] = "HL0310",
        ["HL0315"] = "HL0310",
        ["HL0313"] = "HL0312",
    };

    /// <summary>
    /// 見逃すと分かっているシェーダーと、その理由。
    /// </summary>
    /// <remarks>
    /// ここに入れたシェーダーは、正解との食い違いが<b>まだあること</b>を確かめる。
    /// 直ったら、ここから外す。
    /// </remarks>
    private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
    {
    };

    /// <summary>突き合わせるシェーダー。</summary>
    public static TheoryData<string> Shaders
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string path in FixtureFolders
                         .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(FixtureRoot, folder), "*.shader"))
                         .Order(StringComparer.Ordinal))
            {
                // シンボルを宣言していなければ、構成は 1 つしか無い。突き合わせる意味が無い。
                if (DeclaringPragma().IsMatch(WithIncludedSources(path, File.ReadAllText(path))))
                {
                    data.Add(Path.GetRelativePath(FixtureRoot, path).Replace(Path.DirectorySeparatorChar, '/'));
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void すべての構成を1つずつ解析した結果と一致する(string fileName)
    {
        string path = Path.Combine(FixtureRoot, fileName);
        string source = File.ReadAllText(path);

        SortedSet<string> expected = new(StringComparer.Ordinal);

        foreach (ImmutableArray<string> configuration in EnumerateConfigurations(WithIncludedSources(path, source)))
        {
            expected.UnionWith(Describe(Analyze(path, source, configuration)));
        }

        SortedSet<string> actual = new(Describe(Analyze(path, source, configuration: null)), StringComparer.Ordinal);

        string difference = string.Join(
            "\n",
            [
                .. actual.Except(expected).Select(d => "誤検出: " + d),
                .. expected.Except(actual).Select(d => "見逃し: " + d),
            ]);

        if (KnownGaps.TryGetValue(fileName, out string? reason))
        {
            Assert.False(
                difference.Length == 0,
                $"{fileName} は既知の見逃し ({reason}) として登録されていますが、正解と一致しました。KnownGaps から外してください。");
            return;
        }

        Assert.True(difference.Length == 0, $"{fileName} が正解と食い違っています:\n{difference}");
    }

    [Fact]
    public void 既知の見逃しには理由がある()
        => Assert.All(KnownGaps, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));

    [Fact]
    public void 構成は宣言の仕様どおりに列挙する()
    {
        // _ の無い multi_compile はどれか 1 つ、_ のある行と shader_feature は「無し」も含む。
        ImmutableArray<ImmutableArray<string>> configurations = [.. EnumerateConfigurations("""
            #pragma multi_compile MODE_A MODE_B
            #pragma multi_compile_local _ _X
            #pragma shader_feature_local _F
            """)];

        Assert.Equal(2 * 2 * 2, configurations.Length);
        Assert.All(configurations, c => Assert.True(c.Contains("MODE_A") ^ c.Contains("MODE_B")));
        Assert.Contains(configurations, c => !c.Contains("_X") && !c.Contains("_F"));
    }

    /// <summary>
    /// シェーダーのソースに、同じフォルダーから取り込むヘッダのソースをつなげる。
    /// </summary>
    /// <param name="path">対象のシェーダー。</param>
    /// <param name="source">シェーダーのソース。</param>
    /// <returns>つなげたソース。</returns>
    /// <remarks>
    /// <para>
    /// <b>ヘッダも <c>#pragma multi_compile</c> を書ける。</b>
    /// 共通の関数を切り出したヘッダに宣言を置く書き方では、シェーダー側に宣言が 1 つも無い。
    /// シェーダーのソースだけを読むと、そのシェーダーの構成は 1 つしか無いことになり、
    /// ヘッダの分岐を検査できているかを確かめられない。
    /// </para>
    /// <para>
    /// 解決できる取り込みだけを読む。Unity のヘッダ (<c>Packages/...</c>) は
    /// 宣言を持たないうえ、ここで読むとスタブと食い違う。
    /// 実装の取り込み解決は使わない。同じ読み方をすると、読み方の誤りが両方に入って見えなくなる。
    /// </para>
    /// </remarks>
    private static string WithIncludedSources(string path, string source)
    {
        string folder = Path.GetDirectoryName(path)!;
        List<string> sources = [source];

        foreach (Match match in IncludeDirective().Matches(source))
        {
            string included = Path.Combine(folder, match.Groups["path"].Value);

            if (File.Exists(included))
            {
                sources.Add(File.ReadAllText(included));
            }
        }

        return string.Join("\n", sources);
    }

    /// <summary>Unity が作る構成をすべて列挙する。</summary>
    /// <param name="source">シェーダーのソース。</param>
    /// <returns>構成ごとの、定義するシンボル。</returns>
    /// <remarks>
    /// <c>#pragma multi_compile</c> は書いた数だけの構成を作り、「無し」は <c>_</c> を書いたときだけ作る。
    /// <c>#pragma shader_feature</c> は「無し」も必ず作る。
    /// </remarks>
    private static IEnumerable<ImmutableArray<string>> EnumerateConfigurations(string source)
    {
        List<List<string?>> choices = [];

        foreach (Match match in DeclaringPragma().Matches(source))
        {
            string[] arguments = match.Groups["args"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool placeholder = arguments.Any(a => a.All(c => c == '_'));
            bool multiCompile = match.Groups["kind"].Value == "multi_compile";

            List<string?> options = [.. arguments.Where(a => !a.All(c => c == '_'))];

            if (placeholder || !multiCompile)
            {
                options.Add(null);
            }

            choices.Add(options);
        }

        IEnumerable<ImmutableArray<string>> configurations = [[]];

        foreach (List<string?> options in choices)
        {
            configurations = [.. configurations.SelectMany(c => options.Select(o => o is null ? c : c.Add(o)))];
        }

        return configurations;
    }

    /// <summary>シェーダーを解析する。</summary>
    /// <param name="path">対象のシェーダー。</param>
    /// <param name="source">シェーダーのソース。</param>
    /// <param name="configuration">
    /// 定義するシンボル。<see langword="null"/> なら既定の解析 (構成を固定しない)。
    /// </param>
    /// <returns>検出した診断。</returns>
    private static ImmutableArray<Diagnostic> Analyze(string path, string source, ImmutableArray<string>? configuration)
    {
        SourceText text = SourceText.From(source, path);
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            IncludeResolver = new CompositeIncludeResolver(
            [
                new RuleFixtureTests.UnityHeaderStubResolver(),
                new FileSystemIncludeResolver([Path.GetDirectoryName(path)!]),
            ]),

            // 上限で落ちた構成を比べても意味が無い。
            MaxSymbolVariants = 64,
        };

        if (configuration is { } symbols)
        {
            // エディタの「選んだ構成で表示」と同じ形で、構成を 1 つに固定する。
            options = options with
            {
                PredefinedMacros = symbols.Aggregate(
                    SemanticsOptions.DefaultPredefinedMacros,
                    (macros, symbol) => macros.SetItem(symbol, "1")),
                BothBranchSymbols = [],
                MaxSymbolVariants = 0,
                FixedSymbolConfiguration = true,
            };
        }

        ShaderCompilation compilation = ShaderCompilation.Create(text, tree, options);

        return new AnalyzerDriver(BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }

    /// <summary>比べるために、診断を ID と位置の文字列にする。</summary>
    /// <param name="diagnostics">対象の診断。</param>
    /// <returns>突き合わせる診断。</returns>
    /// <remarks>
    /// メッセージは比べない。既定の解析は「(_A &amp;&amp; _B のとき)」のように構成を添えるためである。
    /// </remarks>
    private static IEnumerable<string> Describe(ImmutableArray<Diagnostic> diagnostics)
        => diagnostics
            .Where(d => !FamilyRules.Contains(d.Id))
            .Select(d => $"{SameError.GetValueOrDefault(d.Id, d.Id)} {d.Location.LineSpan.Start}");

    [GeneratedRegex(@"^\s*#\s*pragma\s+(?<kind>multi_compile|shader_feature)\w*[ \t]+(?<args>[^\r\n]*)", RegexOptions.Multiline)]
    private static partial Regex DeclaringPragma();

    [GeneratedRegex(@"^\s*#\s*include\s+""(?<path>[^""\r\n]+)""", RegexOptions.Multiline)]
    private static partial Regex IncludeDirective();
}
