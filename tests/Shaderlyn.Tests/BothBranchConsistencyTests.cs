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
/// 両方の分岐を 1 本の木に並べても、並べなくても、指摘が変わらないことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// <b>並べるのは速さのためであって、答えを変えるためではない。</b>
/// 並べずに構成ごとに展開する経路 (方法 B) は、並べる経路 (方法 A) より単純である。
/// 両者の指摘が食い違えば、並べ方のどこかが誤っている。
/// ただし並べる経路のほうが多く見つけることはある (<see cref="MergedFindsMore"/>)。
/// 正解との比較は <see cref="ConfigurationOracleTests"/> が受け持つ。
/// </para>
/// <para>
/// 以前はこの突き合わせを CLI の <c>--keep-both-branches ""</c> で手で行っていた。
/// オプションを外したので、テストとして常に走らせる。
/// </para>
/// </remarks>
public sealed partial class BothBranchConsistencyTests
{
    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>
    /// 突き合わせから外す規則。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SL0003</c> は「どこまで展開したか」の報告である。
    /// 並べない経路は展開する構成が増えるので、上限との兼ね合いで出方が変わるのは正しい。
    /// シェーダーの誤りについての答えではないので比べない。
    /// </para>
    /// <para>
    /// <c>SL0004</c> は「どこまで条件を追えたか」の報告である。
    /// 並べる経路は、条件によって中身が変わるマクロを使う文を定義ごとに複製して追えるようにする
    /// (条件の巻き上げ)。追えた箇所が増えれば、この報告は減る。
    /// </para>
    /// </remarks>
    private static readonly string[] ExplorationRules = ["SL0003", "SL0004"];

    /// <summary>
    /// 並べる経路のほうが多く見つけるシェーダーと、その理由。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>並べない経路は正解ではない。</b>構成を 1 つずつ (と、条件に書かれた組を) 展開するだけなので、
    /// 条件に書かれていない組み合わせでしか起きない誤りは見つけられない。
    /// 並べる経路は、同じ木の中で条件を掛け合わせてそれを見つけることがある。
    /// 正解との比較は <see cref="ConfigurationOracleTests"/> が受け持つ。ここでは並べない経路の指摘を含むことだけを確かめる。
    /// </para>
    /// <para>
    /// もう 1 つ、<b>構成をまたいで初めて言えること</b>がある。
    /// 「同じ名前を条件ごとに違う型で宣言している」(HL0353) は、
    /// 構成を固定すると、その構成の宣言しか残らないので観測できない。
    /// 並べる経路にしか出ないのが正しい。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> MergedFindsMore = new(StringComparer.Ordinal)
    {
        [Path.Combine("rules", "HL0353.shader")] =
            "型を書き分けたという事実は構成を固定すると消える。並べた木でしか HL0353 を言えない",

        [Path.Combine("conditional", "MergedTypeByCondition.shader")] =
            "_A と _B が同時に有効なときだけ color が float3 になる。並べない経路はその構成を作らない",
        [Path.Combine("conditional", "TypeByCondition.shader")] =
            "同上。#define は条件の中でしか使われないので並べられる",
        [Path.Combine("conditional", "HoistedMacroType.shader")] =
            "同上。CTYPE を使う文を定義ごとに複製するので、_A と _B の組み合わせが 1 本の木に載る",
        [Path.Combine("conditional", "AssignmentTargetByCondition.shader")] =
            "_B と _D が同時に有効なときだけ float2 を float3 へ代入する。並べない経路はその構成を作らない",
    };

    /// <summary>
    /// 並べない経路のほうが多く見つけるシェーダーと、その理由。
    /// </summary>
    /// <remarks>
    /// <b>並べる経路が見落とす形がまだある。</b>
    /// 構成ごとに展開すれば、その構成での型がそのまま決まる。
    /// 1 本の木では、同じ名前が条件ごとに違う型を持つことになり、型を決められない場合がある。
    /// 正解との比較は <see cref="ConfigurationOracleTests"/> が受け持つ。
    /// </remarks>
    private static readonly Dictionary<string, string> ExpandedFindsMore = new(StringComparer.Ordinal)
    {
    };

    /// <summary>突き合わせに使うシェーダー。</summary>
    public static TheoryData<string> Shaders
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string path in Directory
                         .EnumerateFiles(FixtureRoot, "*.shader", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(FixtureRoot, path));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void 並べても並べなくても指摘は変わらない(string relativePath)
    {
        string path = Path.Combine(FixtureRoot, relativePath);

        string merged = Describe(Analyze(path, bothBranchSymbols: null));
        string expanded = Describe(Analyze(path, bothBranchSymbols: []));

        if (MergedFindsMore.ContainsKey(relativePath))
        {
            string[] mergedLines = merged.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.All(
                expanded.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => Assert.Contains(line, mergedLines));
            Assert.NotEqual(expanded, merged);
            return;
        }

        if (ExpandedFindsMore.ContainsKey(relativePath))
        {
            string[] expandedLines = expanded.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.All(
                merged.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => Assert.Contains(line, expandedLines));
            Assert.NotEqual(expanded, merged);
            return;
        }

        Assert.Equal(expanded, merged);
    }

    [Theory]
    [InlineData("Nested.shader", "missingDetail", true)]
    [InlineData("Nested.shader", "missingDetail", false)]
    [InlineData("Conjunction.shader", "missingInConjunction", true)]
    [InlineData("Conjunction.shader", "missingInConjunction", false)]
    [InlineData("NestedInDeclined.shader", "missingBaseOnly", true)]
    [InlineData("NestedInDeclined.shader", "missingBaseAndDetail", true)]
    [InlineData("NestedInDeclined.shader", "missingBaseAndDetail", false)]
    [InlineData("NestedInDeclined.shader", "missingAllThree", true)]
    [InlineData("NestedInDeclined.shader", "missingAllThree", false)]
    [InlineData("NestedInDeclined.shader", "missingDetailWithoutBase", true)]
    [InlineData("DeclinedInMerged.shader", "missingInner", true)]
    [InlineData("DeclinedInMerged.shader", "missingInner", false)]
    public void 組み合わせで守られた領域の誤りはどちらの経路も報告する(string fileName, string undeclared, bool merge)
    {
        // どちらか一方だけを有効にした構成では通らない領域である。
        // 外側を読み飛ばしても入れ子の条件を読み、組を 1 つの構成として展開する。
        string path = Path.Combine(FixtureRoot, "conditional", fileName);

        ImmutableArray<Diagnostic> diagnostics = Analyze(path, bothBranchSymbols: merge ? null : []);

        Assert.Contains(
            diagnostics,
            d => d.Id == "HL0310" && d.GetMessage().Contains($"'{undeclared}'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SwitchesInclude.shader", "missingWithoutHelpers")]
    [InlineData("Nested.shader", "missingNeither")]
    [InlineData("ElifChain.shader", "missingDefault")]
    public void 別の構成で読み飛ばされた名前も既定の構成で使っていれば報告する(string fileName, string undeclared)
    {
        // どれも既定の構成で実際にコンパイルされるコードの誤りである。
        // 以前は、別の構成 (バリアント) がその行を読み飛ばしたことで「宣言済み」と扱われ、報告していなかった。
        string path = Path.Combine(FixtureRoot, "conditional", fileName);

        Assert.Contains(
            Analyze(path, bothBranchSymbols: null),
            d => d.Id == "HL0310" && d.GetMessage().Contains($"'{undeclared}'", StringComparison.Ordinal));
    }

    /// <summary>シェーダーを解析する。</summary>
    /// <param name="path">対象のシェーダー。</param>
    /// <param name="bothBranchSymbols">並べる対象。<see langword="null"/> なら既定 (自動で拾う)。</param>
    /// <returns>検出した診断。</returns>
    private static ImmutableArray<Diagnostic> Analyze(string path, ImmutableHashSet<string>? bothBranchSymbols)
    {
        SourceText text = SourceText.From(File.ReadAllText(path), path);
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            IncludeResolver = new CompositeIncludeResolver(
            [
                new RuleFixtureTests.UnityHeaderStubResolver(),
                new FileSystemIncludeResolver([Path.GetDirectoryName(path)!]),
            ]),

            // 並べない経路は展開する構成が増える。上限で落ちた分を比べても意味が無い。
            MaxSymbolVariants = 64,
            BothBranchSymbols = bothBranchSymbols,

            // 並べる機構だけを比べる。キーワードをまとめると、並べない経路も _A と _B を同時に有効にした構成を作り、
            // 並べる経路が同じ木で見つける誤りを見つけるようになる。
            PackIndependentSymbols = false,
        };

        ShaderCompilation compilation = ShaderCompilation.Create(text, tree, options);

        return new AnalyzerDriver(BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }

    /// <summary>比べるために、診断を並べた文字列にする。</summary>
    /// <param name="diagnostics">対象の診断。</param>
    /// <returns>ID と位置を 1 行ずつ並べたもの。</returns>
    private static string Describe(ImmutableArray<Diagnostic> diagnostics)
        => string.Join(
            "\n",
            diagnostics
                .Where(d => !ExplorationRules.Contains(d.Id))
                .Select(d => $"{d.Id} {d.Location.LineSpan.Start} {WithoutAssumptionNote(d.GetMessage())}")
                .Order(StringComparer.Ordinal));

    /// <summary>
    /// 「どの構成でそうなるか」の注記を外す。
    /// </summary>
    /// <param name="message">診断のメッセージ。</param>
    /// <returns>注記を外したメッセージ。</returns>
    /// <remarks>
    /// <b>この注記は、並べる経路にしか付かない。</b>
    /// 構成ごとに展開する経路では、その構成の型がそのまま決まるので添える構成が無い。
    /// 同じ誤りを見つけているかを比べるのが目的なので、注記の有無は問わない。
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>HL0310 の「どの構成で宣言が無いか」も外す。</b>
    /// これは木ごとの結果を突き合わせて求めるので、どの組み合わせを 1 本の木で解析したかによって精度が変わる。
    /// 並べる経路は <c>_A &amp;&amp; _B</c> を同じ木で見るが、構成ごとに展開する経路は見ない。
    /// どちらも解析した構成についてだけ述べているので、書き方が違っても誤りの見つけ方は同じである。
    /// </para>
    /// </remarks>
    private static string WithoutAssumptionNote(string message)
        => UndeclaredCondition().Replace(AssumptionNote().Replace(message, string.Empty), "は(構成)宣言されていません。");

    [GeneratedRegex(@"[^。]*のとき、この式は[^。]*です。")]
    private static partial Regex AssumptionNote();

    [GeneratedRegex(@"は(どこにも| .+ のとき)宣言されていません。")]
    private static partial Regex UndeclaredCondition();
}
