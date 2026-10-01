using System.Collections.Immutable;
using System.Diagnostics;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Preprocessing;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// Unity が同梱している実際のシェーダーコードに対する堅牢性の検証。
/// </summary>
/// <remarks>
/// <para>
/// 自分で書いた fixture は、無意識のうちに自分の実装が扱える範囲に収まってしまう。
/// 他人が書いた大量の実コードに当てることでしか見つからない不具合があるため、
/// Unity のインストール先にある数百ファイルを解析対象にする。
/// </para>
/// <para>
/// <b>Unity が見つからない環境ではテストを飛ばす。</b>
/// CI の Linux ランナーには Unity が無く、
/// そこで失敗させると本質的でない理由でビルドが赤くなる。
/// </para>
/// </remarks>
public sealed class UnityCorpusTests(ITestOutputHelper output)
{
    /// <summary>解析に許容する 1 ファイルあたりの時間。</summary>
    private const int PerFileTimeoutMilliseconds = 5000;

    /// <summary>検証に使うファイル数の上限。</summary>
    /// <remarks>
    /// 全件を対象にするとテストが長くなりすぎる。
    /// 不具合の検出には十分な数を、決定的な順序で選ぶ。
    /// </remarks>
    private const int MaxFiles = 400;

    /// <summary>
    /// 埋め込みコードの検証に使う .shader の数の上限。
    /// </summary>
    /// <remarks>
    /// 埋め込みブロックごとに include の連鎖をすべて展開し直すため、
    /// 1 件あたりの処理が重い。全件を対象にするとテストだけで数分かかる。
    /// </remarks>
    private const int MaxEmbeddedBlockShaders = 60;

    /// <summary>Pass やカーネルに分けて比べるファイルの数の上限。</summary>
    /// <remarks>分けた数だけ解析し直すので、全件 (187 本) では 7 分かかる。</remarks>
    private const int MaxSplitFiles = 100;

    /// <summary>1 つのルールについて出力する実例の最大数。</summary>
    /// <remarks>
    /// 分布だけでは誤検出かどうかを判断できない。
    /// 1 件だけだと、それを調べて納得した時点で残りを見落とす。
    /// </remarks>
    private const int MaxExamplesPerRule = 5;

    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// 診断メッセージを、種類を数えるための短い形へ丸める。
    /// </summary>
    /// <param name="message">元のメッセージ。</param>
    /// <returns>丸めたメッセージ。</returns>
    /// <remarks>
    /// メッセージには具体的な識別子が埋め込まれているため、
    /// そのまま数えるとすべてが別種になって分布が見えない。
    /// </remarks>
    private static string Summarize(string message)
    {
        System.Text.StringBuilder builder = new();
        bool insideQuotes = false;

        foreach (char c in message)
        {
            if (c == '\'')
            {
                if (!insideQuotes)
                {
                    builder.Append("'…'");
                }

                insideQuotes = !insideQuotes;
                continue;
            }

            if (!insideQuotes)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Unity のインストール先を探す。</summary>
    /// <returns>見つかった Editor のデータフォルダー。無ければ <see langword="null"/>。</returns>
    private static string? FindUnityEditorData()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");

        if (!Directory.Exists(root))
        {
            return null;
        }

        // 複数バージョンがある場合は新しいものを使う。
        foreach (string version in Directory.EnumerateDirectories(root).OrderDescending(StringComparer.Ordinal))
        {
            string data = Path.Combine(version, "Editor", "Data");
            if (Directory.Exists(Path.Combine(data, "CGIncludes")))
            {
                return data;
            }
        }

        return null;
    }

    /// <summary>解析対象のファイルを集める。</summary>
    /// <param name="editorData">Editor のデータフォルダー。</param>
    /// <returns>対象ファイルのパス。</returns>
    private static ImmutableArray<string> CollectShaderSources(string editorData)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

        return
        [
            .. Directory.EnumerateFiles(editorData, "*.hlsl", options)
                .Concat(Directory.EnumerateFiles(editorData, "*.cginc", options))

                // ShaderGraph のテンプレートは $SurfaceDescription.BaseColor: のような
                // 置換用の記述を含んでおり、HLSL として妥当ではない。
                // Unity 自身も、置換を済ませてからでなければコンパイルしない。
                .Where(p => !p.EndsWith(".template.hlsl", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(MaxFiles)
        ];
    }

    /// <summary>Unity の標準的なインクルードパスを組み立てる。</summary>
    /// <param name="editorData">Editor のデータフォルダー。</param>
    /// <returns>include の解決に使う設定。</returns>
    private static PreprocessorOptions CreateOptions(string editorData)
    {
        List<string> searchPaths =
        [
            Path.Combine(editorData, "CGIncludes"),
            Path.Combine(editorData, "Resources", "PackageManager", "BuiltInPackages"),
        ];

        return new PreprocessorOptions
        {
            // Unity のシェーダーは他パッケージのヘッダを Packages/<パッケージ名>/... で参照する。
            // これを解決できないとマクロが未定義のまま残り、構文エラーの山になる。
            IncludeResolver = new CompositeIncludeResolver(
                new FileSystemIncludeResolver(searchPaths),
                UnityPackageIncludeResolver.ForProject(editorData, editorData)),

            // Unity が D3D11 向けにコンパイルする際の代表的な構成を再現する。
            // 単一構成での展開なので、どれか 1 つを選ぶ必要がある。
            PredefinedMacros = ImmutableDictionary<string, string>.Empty
                .Add("SHADER_API_D3D11", "1")
                .Add("SHADER_TARGET", "45")
                .Add("UNITY_VERSION", "600000"),
        };
    }

    [Fact]
    public void Unity同梱のシェーダーコードを例外なく解析できる()
    {
        string? editorData = FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため検証を飛ばします。");
            return;
        }

        ImmutableArray<string> files = CollectShaderSources(editorData);
        _output.WriteLine($"{files.Length} 件のファイルを検証します ({editorData})");

        PreprocessorOptions options = CreateOptions(editorData);

        List<string> failures = [];
        List<string> slowFiles = [];
        int cleanFiles = 0;
        int totalDiagnostics = 0;

        // どの種類の失敗が多いかを可視化する。
        // 「診断が出た」だけでは、パーサの不備なのか解析対象の性質なのか区別できない。
        Dictionary<string, int> diagnosticKinds = new(StringComparer.Ordinal);
        Dictionary<string, string> examples = new(StringComparer.Ordinal);

        // 解決できない include はマクロを未定義のまま残し、
        // その結果として構文エラーを大量に生む。原因の切り分けに必要な数字。
        Dictionary<string, int> unresolvedIncludes = new(StringComparer.OrdinalIgnoreCase);
        int filesWithUnresolvedIncludes = 0;
        Stopwatch totalTime = Stopwatch.StartNew();

        foreach (string file in files)
        {
            SourceText text;
            try
            {
                text = SourceText.From(File.ReadAllText(file), file);
            }
            catch (IOException)
            {
                continue;
            }

            Stopwatch fileTime = Stopwatch.StartNew();

            try
            {
                HlslSyntaxTree tree = HlslSyntaxTree.Parse(text, options);
                fileTime.Stop();

                totalDiagnostics += tree.Diagnostics.Length;

                if (!tree.PreprocessResult.UnresolvedIncludes.IsEmpty)
                {
                    filesWithUnresolvedIncludes++;

                    foreach (string include in tree.PreprocessResult.UnresolvedIncludes)
                    {
                        unresolvedIncludes[include] = unresolvedIncludes.GetValueOrDefault(include) + 1;
                    }
                }

                if (tree.Diagnostics.IsEmpty)
                {
                    cleanFiles++;
                }
                else
                {
                    // 最初の 1 件だけを数える。1 か所の失敗が連鎖して出す後続の診断は、
                    // 原因の分布を見るうえでは雑音にしかならない。
                    Diagnostic first = tree.Diagnostics[0];
                    string kind = $"{first.Id}: {Summarize(first.GetMessage())}";
                    diagnosticKinds[kind] = diagnosticKinds.GetValueOrDefault(kind) + 1;

                    // 種類ごとに実例を 1 つ残す。分布だけでは原因の調査ができない。
                    if (!examples.ContainsKey(kind))
                    {
                        examples[kind] =
                            $"{first.Location.FilePath}({first.Location.LineSpan.Start})\n"
                            + $"      {first.Location.GetLineText().Trim()}";
                    }
                }

                if (fileTime.ElapsedMilliseconds > PerFileTimeoutMilliseconds)
                {
                    slowFiles.Add($"{Path.GetFileName(file)} ({fileTime.ElapsedMilliseconds}ms)");
                }
            }
            catch (Exception ex)
            {
                // 例外は許容しない。リンタは壊れた入力を与えられる前提で書く必要がある。
                failures.Add($"{file}\n    {ex.GetType().Name}: {ex.Message}");
            }
        }

        totalTime.Stop();

        _output.WriteLine($"解析時間: {totalTime.ElapsedMilliseconds}ms");
        _output.WriteLine($"診断なしで解析できたファイル: {cleanFiles}/{files.Length}");
        _output.WriteLine($"診断の総数: {totalDiagnostics}");
        _output.WriteLine($"include を解決できなかったファイル: {filesWithUnresolvedIncludes}/{files.Length}");
        _output.WriteLine("最初の診断の種類 (上位 15 件):");

        foreach ((string kind, int count) in diagnosticKinds.OrderByDescending(p => p.Value).Take(15))
        {
            _output.WriteLine($"  {count,4} {kind}");
            _output.WriteLine($"      {examples.GetValueOrDefault(kind, "")}");
        }

        _output.WriteLine("解決できなかった include (上位 15 件):");

        foreach ((string include, int count) in unresolvedIncludes.OrderByDescending(p => p.Value).Take(15))
        {
            _output.WriteLine($"  {count,4} {include}");
        }

        if (slowFiles.Count > 0)
        {
            _output.WriteLine($"時間のかかったファイル: {string.Join(", ", slowFiles)}");
        }

        Assert.True(
            failures.Count == 0,
            $"次のファイルの解析で例外が発生しました:\n{string.Join("\n", failures.Take(10))}");

        Assert.Empty(slowFiles);
    }

    [Fact]
    public void Unity同梱のShaderLabを例外なく解析できる()
    {
        string? editorData = FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため検証を飛ばします。");
            return;
        }

        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        ImmutableArray<string> files =
        [
            .. Directory.EnumerateFiles(editorData, "*.shader", options)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(MaxFiles)
        ];

        _output.WriteLine($"{files.Length} 件の .shader を検証します。");

        List<string> failures = [];
        List<string> roundTripFailures = [];
        int cleanFiles = 0;

        foreach (string file in files)
        {
            SourceText text;
            try
            {
                text = SourceText.From(File.ReadAllText(file), file);
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                ShaderLab.ShaderLabSyntaxTree tree = ShaderLab.ShaderLabSyntaxTree.Parse(text);

                // 実コードに対してもラウンドトリップが成立しなければならない。
                if (tree.ToFullString() != text.Content)
                {
                    roundTripFailures.Add(file);
                }

                if (tree.Diagnostics.IsEmpty)
                {
                    cleanFiles++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{file}\n    {ex.GetType().Name}: {ex.Message}");
            }
        }

        _output.WriteLine($"構文エラーなしで解析できたファイル: {cleanFiles}/{files.Length}");

        Assert.True(
            failures.Count == 0,
            $"次のファイルの解析で例外が発生しました:\n{string.Join("\n", failures.Take(10))}");

        Assert.True(
            roundTripFailures.Count == 0,
            $"次のファイルでラウンドトリップが成立しませんでした:\n{string.Join("\n", roundTripFailures.Take(10))}");
    }

    /// <summary>
    /// 実際の .shader に埋め込まれた HLSL を、依存関係を解決したうえで解析できるかを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>これが M2 の完了条件そのものである。</b>
    /// ヘッダファイルを単体で解析する検証とは意味が違う。
    /// <c>.cginc</c> や <c>.hlsl</c> の多くは、あらかじめ別のヘッダが
    /// 取り込まれていることを前提に書かれており、単体では成立しない。
    /// Unity 自身もそれらを単体でコンパイルすることはできない。
    /// </para>
    /// <para>
    /// 一方 <c>.shader</c> に埋め込まれた HLSL は、自分に必要な include を自分で書いている。
    /// 実際の利用場面と一致するのはこちらであり、
    /// 解析できるかどうかを問う意味があるのもこちらである。
    /// </para>
    /// </remarks>
    [Fact]
    public void 実シェーダーに埋め込まれたHLSLを依存関係ごと解析できる()
    {
        string? editorData = FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため検証を飛ばします。");
            return;
        }

        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

        // 1 ブロックごとに巨大な include の連鎖を展開し直すため、全件だと数分かかる。
        // 不具合の検出には十分な数を、決定的な順序で選ぶ。
        ImmutableArray<string> files =
        [
            .. Directory.EnumerateFiles(editorData, "*.shader", enumeration)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(MaxEmbeddedBlockShaders)
        ];

        PreprocessorOptions options = CreateOptions(editorData);

        int programBlocks = 0;
        int cleanBlocks = 0;
        List<string> failures = [];
        Dictionary<string, int> diagnosticKinds = new(StringComparer.Ordinal);
        Dictionary<string, string> examples = new(StringComparer.Ordinal);

        foreach (string file in files)
        {
            SourceText shaderText;
            try
            {
                shaderText = SourceText.From(File.ReadAllText(file), file);
            }
            catch (IOException)
            {
                continue;
            }

            ShaderLab.ShaderLabSyntaxTree shaderTree = ShaderLab.ShaderLabSyntaxTree.Parse(shaderText);

            foreach (ShaderLab.Syntax.ProgramBlockSyntax block in
                shaderTree.Root.DescendantNodesAndSelf().OfType<ShaderLab.Syntax.ProgramBlockSyntax>())
            {
                string? code = ExtractProgramBody(block.Token.Text);
                if (code is null)
                {
                    continue;
                }

                programBlocks++;

                // 埋め込みコードの include は .shader からの相対で解決される。
                SourceText codeText = SourceText.From(code, file);

                try
                {
                    HlslSyntaxTree tree = HlslSyntaxTree.Parse(codeText, options);

                    if (tree.Diagnostics.IsEmpty)
                    {
                        cleanBlocks++;
                    }
                    else
                    {
                        Diagnostic first = tree.Diagnostics[0];
                        string kind = $"{first.Id}: {Summarize(first.GetMessage())}";
                        diagnosticKinds[kind] = diagnosticKinds.GetValueOrDefault(kind) + 1;
                        examples.TryAdd(kind, $"{file}\n      {first.Location.GetLineText().Trim()}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{file}\n    {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        _output.WriteLine($"{files.Length} 件の .shader から {programBlocks} 個の埋め込みコードブロックを検証しました。");
        _output.WriteLine($"診断なしで解析できたブロック: {cleanBlocks}/{programBlocks}");

        foreach ((string kind, int count) in diagnosticKinds.OrderByDescending(p => p.Value).Take(10))
        {
            _output.WriteLine($"  {count,4} {kind}");
            _output.WriteLine($"      {examples.GetValueOrDefault(kind, string.Empty)}");
        }

        Assert.True(
            failures.Count == 0,
            $"次のファイルの解析で例外が発生しました:\n{string.Join("\n", failures.Take(10))}");
    }

    /// <summary>
    /// 埋め込みコードブロックのトークンから、開始・終了シンボルを除いた中身を取り出す。
    /// </summary>
    /// <param name="blockText">ブロック全体のテキスト。</param>
    /// <returns>中身のコード。取り出せない場合は <see langword="null"/>。</returns>
    private static string? ExtractProgramBody(string blockText)
    {
        int start = blockText.IndexOf('\n', StringComparison.Ordinal);
        int end = blockText.LastIndexOf('\n');

        return start < 0 || end <= start ? null : blockText[(start + 1)..end];
    }

    [Fact]
    public void Unity同梱のShaderLabに組み込みルールを適用して誤検出を測る()
    {
        // Unity 公式のシェーダーは正しく書かれている前提で、
        // どのルールがどれだけ指摘を出すかを可視化する。
        // 誤検出が多いルールはここで見つかる。
        string? editorData = FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため検証を飛ばします。");
            return;
        }

        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        ImmutableArray<string> files =
        [
            .. Directory.EnumerateFiles(editorData, "*.shader", enumeration)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(MaxFiles)
        ];

        Core.Analysis.AnalyzerDriver driver = new(Cli.BuiltInAnalyzers.All);
        Dictionary<string, int> countsByRule = new(StringComparer.Ordinal);

        foreach (string file in files)
        {
            SourceText text;
            try
            {
                text = SourceText.From(File.ReadAllText(file), file);
            }
            catch (IOException)
            {
                continue;
            }

            ShaderLab.ShaderLabSyntaxTree tree = ShaderLab.ShaderLabSyntaxTree.Parse(text);
            Core.Analysis.AnalysisTarget unit = new(text, tree.Root, tree.Diagnostics);

            foreach (Diagnostic diagnostic in driver.Analyze(unit))
            {
                countsByRule[diagnostic.Id] = countsByRule.GetValueOrDefault(diagnostic.Id) + 1;
            }
        }

        _output.WriteLine($"{files.Length} 件の .shader に対するルール別の指摘数:");
        foreach ((string id, int count) in countsByRule.OrderByDescending(p => p.Value))
        {
            _output.WriteLine($"  {id}: {count}");
        }

        // 件数そのものは断定できないため、ここでは可視化にとどめる。
        // 例外を投げないことだけを保証する。
        Assert.True(true);
    }

    /// <summary>
    /// 意味解析まで含めた解析を Unity 公式のシェーダーへ適用し、誤検出を測る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>意味解析のルールは、実コードに当てて初めて誤検出が見える。</b>
    /// 自分で書いた fixture は無意識のうちに自分の実装が扱える範囲へ収まるため、
    /// 「単一構成での展開では見えない宣言」や「マクロで組み立てられる宣言」といった
    /// 現実の書き方に当たらない。
    /// </para>
    /// <para>
    /// Unity 公式のシェーダーは正しく書かれている前提なので、
    /// <b>ここで出る指摘は原則としてすべて誤検出である</b>。
    /// 件数を出力し、増えたときに気づけるようにする。
    /// </para>
    /// </remarks>
    [Fact]
    public void Unity同梱のShaderLabに意味解析ルールを適用して誤検出を測る()
    {
        string? editorData = FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため検証を飛ばします。");
            return;
        }

        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        ImmutableArray<string> files =
        [
            .. Directory.EnumerateFiles(editorData, "*.shader", enumeration)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(MaxFiles)
        ];

        PreprocessorOptions preprocessor = CreateOptions(editorData);
        Semantics.SemanticsOptions options = new()
        {
            IncludeResolver = preprocessor.IncludeResolver,
            PredefinedMacros = preprocessor.PredefinedMacros,
            TokenCache = new HlslTokenCache(),
        };

        Core.Analysis.AnalyzerDriver driver = new(Cli.BuiltInAnalyzers.All);
        Dictionary<string, int> countsByRule = new(StringComparer.Ordinal);

        // 分布だけでは誤検出かどうかを判断できない。
        // 実例を複数残しておかないと、1 件目を調べて納得した時点で残りを見落とす。
        Dictionary<string, List<string>> examples = new(StringComparer.Ordinal);
        List<string> failures = [];
        int analyzedShaders = 0;
        int shadersWithCompleteDependencies = 0;
        int identifiersChecked = 0;

        Stopwatch elapsed = Stopwatch.StartNew();

        foreach (string file in files)
        {
            SourceText text;
            try
            {
                text = SourceText.From(File.ReadAllText(file), file);
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                ShaderLab.ShaderLabSyntaxTree tree = ShaderLab.ShaderLabSyntaxTree.Parse(text);
                Semantics.ShaderCompilation compilation = Semantics.ShaderCompilation.Create(text, tree, options);

                analyzedShaders++;
                if (compilation.HasCompleteDependencies)
                {
                    shadersWithCompleteDependencies++;

                    // HL0310 が調べる識別子の数。同じ位置は構成の数だけ現れるので 1 度に数える。
                    identifiersChecked += compilation.EnumerateRuleNodes()
                        .Select(pair => pair.Node)
                        .OfType<Hlsl.Syntax.IdentifierExpressionSyntax>()
                        .Where(compilation.IsReportable)
                        .Select(identifier => identifier.GetLocation()?.Span.Start ?? -1)
                        .Distinct()
                        .Count();
                }

                foreach (Diagnostic diagnostic in driver.Analyze(compilation.CreateAnalysisTarget()))
                {
                    countsByRule[diagnostic.Id] = countsByRule.GetValueOrDefault(diagnostic.Id) + 1;

                    List<string> forRule = examples.TryGetValue(diagnostic.Id, out List<string>? existing)
                        ? existing
                        : examples[diagnostic.Id] = [];

                    if (forRule.Count < MaxExamplesPerRule)
                    {
                        forRule.Add(
                            $"{Path.GetFileName(file)}({diagnostic.Location.LineSpan.Start}) {diagnostic.GetMessage()}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{file}\n    {ex.GetType().Name}: {ex.Message}");
            }
        }

        elapsed.Stop();

        _output.WriteLine($"{analyzedShaders} 件の .shader を意味解析しました ({elapsed.ElapsedMilliseconds}ms)");
        _output.WriteLine($"依存関係を完全に解決できたファイル: {shadersWithCompleteDependencies}/{analyzedShaders}");
        _output.WriteLine($"依存関係が揃ったファイルの、未宣言を調べた識別子: {identifiersChecked}");
        _output.WriteLine("ルール別の指摘数:");

        foreach ((string id, int count) in countsByRule.OrderByDescending(p => p.Value))
        {
            _output.WriteLine($"  {id}: {count}");

            foreach (string example in examples.GetValueOrDefault(id, []))
            {
                _output.WriteLine($"      {example}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"次のファイルの解析で例外が発生しました:\n{string.Join("\n", failures.Take(10))}");
    }

    /// <summary>
    /// 条件によって中身が変わるマクロの巻き上げが、実コードで構文木を壊さないことを確かめる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>自分で書いた fixture では、単位の取り違えは現れない。</b>
    /// 扱える形しか書かないためである。他人が書いた実コードに当てるしかない
    /// (条件の巻き上げ)。
    /// </para>
    /// <para>
    /// 見るのは <c>HL0001</c> (構文エラー) が増えていないことである。
    /// 複製する単位を取り違えると、文にならない断片が並び、そこから先の構文解析が総崩れになる。
    /// </para>
    /// <para>
    /// 併せて、複製した回数・切れ目を読み直した回数・諦めた回数を出す。
    /// 指摘が変わらないことは「働いている」ことを示さないので、数えて確かめる。
    /// </para>
    /// </remarks>
    [Fact]
    public void 巻き上げは実シェーダーの構文木を壊さない()
    {
        string? editorData = FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため検証を飛ばします。");
            return;
        }

        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

        // 巻き上げが起きるのは、条件でマクロの中身を切り替えているシェーダーだけである。
        // どれがそうかは展開してみるまで分からないので、全件に当てる。
        ImmutableArray<string> files =
        [
            .. Directory.EnumerateFiles(editorData, "*.shader", enumeration)
                .Concat(Directory.EnumerateFiles(editorData, "*.compute", enumeration))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];

        PreprocessorOptions preprocessor = CreateOptions(editorData);
        Semantics.SemanticsOptions shared = new()
        {
            IncludeResolver = preprocessor.IncludeResolver,
            PredefinedMacros = preprocessor.PredefinedMacros,
            TokenCache = new HlslTokenCache(),
            IncludeCache = new HlslIncludeCache(),

            // 巻き上げは既定の構成を展開するときに起きる。
            // 構成ごとの展開は、この検証には要らない。
            MaxSymbolVariants = 0,
        };

        int hoisted = 0;
        int retried = 0;
        int gaveUp = 0;
        int hoistingShaders = 0;
        List<string> broken = [];

        foreach (string file in files)
        {
            SourceText text;
            try
            {
                text = SourceText.From(File.ReadAllText(file), file);
            }
            catch (IOException)
            {
                continue;
            }

            int withHoisting = CountSyntaxErrors(
                text, shared with { HoistConditionalMacros = true }, out ImmutableArray<PreprocessResult> results);

            int units = results.Sum(r => r.HoistedUnits);

            hoisted += units;
            retried += results.Sum(r => r.HoistRetries);
            gaveUp += results.Sum(r => r.HoistGiveUps);

            if (units > 0)
            {
                hoistingShaders++;
            }

            // 構文エラーが 1 つも無ければ、巻き上げが壊していないことは明らかである。
            // 比べるために展開し直すのは、出ているファイルだけでよい。
            if (withHoisting == 0)
            {
                continue;
            }

            int withoutHoisting = CountSyntaxErrors(text, shared with { HoistConditionalMacros = false }, out _);

            if (withHoisting > withoutHoisting)
            {
                broken.Add($"{Path.GetFileName(file)}: {withoutHoisting} → {withHoisting}");
            }
        }

        _output.WriteLine($"{files.Length} 件で確かめました。巻き上げが起きたファイル: {hoistingShaders}");
        _output.WriteLine($"複製した文: {hoisted}、切れ目を読み直した: {retried}、諦めた: {gaveUp}");

        Assert.True(
            broken.Count == 0,
            $"巻き上げで構文エラーが増えました:\n{string.Join("\n", broken.Take(10))}");

        Assert.True(hoisted > 0, "巻き上げが 1 度も起きていません。この検証は何も確かめていません。");
    }

    /// <summary>
    /// 構成ごとの展開 (バリアント) を、なぜ作ったのかを数える。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>両方の分岐を並べられなかったシンボルは、有効にして展開し直す。</b>
    /// 並べなかった理由は展開の結果に残っている (<see cref="PreprocessResult.BothBranchDeclines"/>)。
    /// 1 つのシンボルが何か所かで並べられなかったなら、バリアントを要らなくするにはそのすべてを直す必要がある。
    /// そこで理由ごとに「関わっているバリアント」と「その理由だけで作ったバリアント」を数える。
    /// </para>
    /// <para>
    /// 理由の記録が無いバリアントは、記録の漏れか、まだ分かっていない理由である。例を出す。
    /// </para>
    /// </remarks>
    [Fact]
    public void バリアントを作った理由を測る()
    {
        string? editorData = FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため検証を飛ばします。");
            return;
        }

        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        ImmutableArray<string> files =
        [
            .. Directory.EnumerateFiles(editorData, "*.shader", enumeration)
                .Concat(Directory.EnumerateFiles(editorData, "*.compute", enumeration))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];

        PreprocessorOptions preprocessor = CreateOptions(editorData);
        Semantics.SemanticsOptions options = new()
        {
            IncludeResolver = preprocessor.IncludeResolver,
            PredefinedMacros = preprocessor.PredefinedMacros,
            TokenCache = new HlslTokenCache(),
            IncludeCache = new HlslIncludeCache(),
        };

        const string MacroUsedInCode = "MacroUsedInCode";

        int compilations = 0;
        int withVariants = 0;
        int singleVariants = 0;
        int combinationVariants = 0;
        int unexplored = 0;
        Dictionary<string, int> involved = new(StringComparer.Ordinal);
        Dictionary<string, int> sole = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> examples = new(StringComparer.Ordinal);
        List<string> unexplained = [];

        // 並べられず、構成も作らず、上限で落としたとも言っていないシンボル。その分岐の片側はどの木にも載っていない。
        List<string> unread = [];

        Stopwatch elapsed = Stopwatch.StartNew();

        foreach (string file in files)
        {
            SourceText text;
            try
            {
                text = SourceText.From(File.ReadAllText(file), file);
            }
            catch (IOException)
            {
                continue;
            }

            Semantics.ShaderCompilation compilation =
                file.EndsWith(".compute", StringComparison.OrdinalIgnoreCase)
                    ? Semantics.ShaderCompilation.CreateForHlsl(text, options)
                    : Semantics.ShaderCompilation.Create(text, ShaderLab.ShaderLabSyntaxTree.Parse(text), options);

            compilations++;
            unexplored += compilation.UnexploredSymbols.Length + compilation.UnexploredSymbolCombinations.Length;

            HashSet<string> expanded = [.. compilation.SymbolVariants.SelectMany(v => v.EnabledSymbols), .. compilation.UnexploredSymbols];

            foreach (Semantics.AnalyzedProgram program in compilation.Programs)
            {
                foreach (string symbol in program.Tree.PreprocessResult.DeclinedBothBranchSymbols)
                {
                    // 既定の構成で定義するシンボル (_ の無い行の先頭) は、有効な側を既定の木で読んでいる。
                    if (!expanded.Contains(symbol)
                        && !program.Tree.PreprocessResult.Macros.ContainsKey(symbol)
                        && !compilation.UnexploredSymbolCombinations.Any(c => c.Symbols.Contains(symbol)))
                    {
                        unread.Add($"{Path.GetFileName(file)}:{symbol}");
                    }
                }
            }

            if (compilation.SymbolVariants.IsEmpty)
            {
                continue;
            }

            withVariants++;

            // 理由はシンボルごとに、書かれた場所 (このファイルか、取り込んだヘッダか) と合わせて集める。
            Dictionary<string, HashSet<string>> reasons = new(StringComparer.Ordinal);

            void Add(string symbol, string reason)
            {
                if (!reasons.TryGetValue(symbol, out HashSet<string>? set))
                {
                    reasons[symbol] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(reason);
            }

            foreach (Semantics.AnalyzedProgram program in compilation.Programs)
            {
                ImmutableArray<BothBranchDecline> declines = program.Tree.PreprocessResult.BothBranchDeclines;

                foreach (BothBranchDecline decline in declines.IsDefault ? [] : declines)
                {
                    string where = string.Equals(decline.FilePath, file, StringComparison.OrdinalIgnoreCase)
                        ? "このファイル"
                        : "ヘッダ";
                    Add(decline.Symbol, $"{decline.Reason} ({where})");
                }

                foreach (string symbol in program.MacroConflictSymbols)
                {
                    Add(symbol, MacroUsedInCode);
                }
            }

            foreach (Semantics.AnalyzedProgram variant in compilation.SymbolVariants)
            {
                if (variant.EnabledSymbols.Length != 1)
                {
                    combinationVariants++;
                    continue;
                }

                singleVariants++;
                string symbol = variant.EnabledSymbols[0];
                string example = $"{Path.GetFileName(file)}:{symbol}";

                if (!reasons.TryGetValue(symbol, out HashSet<string>? found) || found.Count == 0)
                {
                    unexplained.Add(example);
                    continue;
                }

                foreach (string reason in found)
                {
                    involved[reason] = involved.GetValueOrDefault(reason) + 1;

                    List<string> forReason = examples.TryGetValue(reason, out List<string>? existing)
                        ? existing
                        : examples[reason] = [];

                    if (forReason.Count < MaxExamplesPerRule)
                    {
                        forReason.Add(example);
                    }
                }

                if (found.Count == 1)
                {
                    string only = found.First();
                    sole[only] = sole.GetValueOrDefault(only) + 1;
                }
            }
        }

        elapsed.Stop();

        _output.WriteLine($"{compilations} 件を解析しました ({elapsed.ElapsedMilliseconds}ms)");
        _output.WriteLine($"バリアントを作ったファイル: {withVariants}");
        _output.WriteLine($"バリアント: 1 つずつ {singleVariants}、組 {combinationVariants}、上限で調べなかった構成 {unexplored}");
        _output.WriteLine("理由 (関わっているバリアント / その理由だけのバリアント):");

        foreach ((string reason, int count) in involved.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            _output.WriteLine($"  {reason}: {count} / {sole.GetValueOrDefault(reason)}");

            foreach (string example in examples.GetValueOrDefault(reason, []))
            {
                _output.WriteLine($"      {example}");
            }
        }

        _output.WriteLine($"理由の記録が無いバリアント: {unexplained.Count}");

        foreach (string example in unexplained.Take(MaxExamplesPerRule * 2))
        {
            _output.WriteLine($"      {example}");
        }

        List<string> distinctUnread = [.. unread.Distinct(StringComparer.Ordinal)];
        _output.WriteLine($"並べられず構成も作らなかったシンボル: {distinctUnread.Count}");

        foreach (string example in distinctUnread.Take(MaxExamplesPerRule * 4))
        {
            _output.WriteLine($"      {example}");
        }
    }

    /// <summary>構文エラーの数を数える。</summary>
    /// <param name="text">シェーダーのテキスト。</param>
    /// <param name="options">実行時設定。</param>
    /// <param name="results">コードブロックごとの展開結果。</param>
    /// <returns><c>HL0001</c> の数。</returns>
    private static int CountSyntaxErrors(
        SourceText text,
        Semantics.SemanticsOptions options,
        out ImmutableArray<PreprocessResult> results)
    {
        Semantics.ShaderCompilation compilation =
            text.FilePath.EndsWith(".compute", StringComparison.OrdinalIgnoreCase)
                ? Semantics.ShaderCompilation.CreateForHlsl(text, options)
                : Semantics.ShaderCompilation.Create(text, ShaderLab.ShaderLabSyntaxTree.Parse(text), options);

        results = [.. compilation.Programs.Select(p => p.Tree.PreprocessResult)];

        return compilation.Programs
            .SelectMany(p => p.Diagnostics)
            .Count(d => d.Id == "HL0001");
    }

    /// <summary>
    /// シェーダー全体で判定するルール。Pass やカーネルに分けると答えが変わるのが正しいので、分割の比較から外す。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description><c>SL1001</c> / <c>SL1004</c>: プロパティがどれかの Pass で宣言・使用されているか</description></item>
    ///   <item><description><c>HL0330</c> / <c>HL0331</c>: キーワードがどれかの Pass で宣言・使用されているか</description></item>
    ///   <item><description><c>SL0002</c> / <c>SL0003</c>: ファイルとして何を調べなかったか</description></item>
    ///   <item><description><c>SL1041</c>: 同じ SubShader の Pass 名の重複。Pass が 2 つ以上なければ起きない</description></item>
    /// </list>
    /// </remarks>
    private static readonly ImmutableHashSet<string> ShaderWideRules =
        ["SL1001", "SL1004", "HL0330", "HL0331", "SL0002", "SL0003", "SL1041"];

    /// <summary>
    /// Pass やカーネルを 1 つずつに分けて解析しても、Pass ごとに判定するルールの指摘が変わらないことを確かめる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>別々にコンパイルされるコードを同じものと見なしていないかを測る。</b>
    /// 1 つの <c>.shader</c> の Pass、1 つの <c>.compute</c> のカーネルは別々にコンパイルされる。
    /// 解析がそれらの宣言を混ぜると、ほかの Pass にしか無い宣言で誤りを見落としたり (分けると増える指摘)、
    /// ほかの Pass の型で誤りを報告したり (分けると消える指摘) する。
    /// </para>
    /// <para>
    /// 分け方は、ほかの Pass (カーネルでは <c>#pragma kernel</c> の行) を空白で塗りつぶす。
    /// 位置が変わらないので、元の指摘とそのまま比べられる。
    /// シェーダー全体で判定するルールは比べない (<see cref="ShaderWideRules"/>)。
    /// </para>
    /// </remarks>
    [Fact]
    public void Passやカーネルに分けて解析しても指摘は変わらない()
    {
        string? editorData = FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため検証を飛ばします。");
            return;
        }

        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        ImmutableArray<string> files =
        [
            .. Directory.EnumerateFiles(editorData, "*.shader", enumeration)
                .Concat(Directory.EnumerateFiles(editorData, "*.compute", enumeration))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(MaxFiles)
        ];

        PreprocessorOptions preprocessor = CreateOptions(editorData);
        Semantics.SemanticsOptions options = new()
        {
            IncludeResolver = preprocessor.IncludeResolver,
            PredefinedMacros = preprocessor.PredefinedMacros,
            TokenCache = new HlslTokenCache(),
            IncludeCache = new HlslIncludeCache(),
        };

        Core.Analysis.AnalyzerDriver driver = new(Cli.BuiltInAnalyzers.All);
        List<string> onlyWhole = [];
        List<string> onlySplit = [];
        int splitFiles = 0;

        foreach (string file in files)
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            // 1 ファイルを Pass の数だけ解析し直すので重い。全件 (187 本) で 7 分かかった。
            if (splitFiles >= MaxSplitFiles)
            {
                break;
            }

            ImmutableArray<string> pieces = SplitIntoBlocks(file, content, options);

            if (pieces.Length < 2)
            {
                continue;
            }

            splitFiles++;

            HashSet<string> whole = Describe(file, content);
            HashSet<string> split = [.. pieces.SelectMany(piece => Describe(file, piece))];

            onlyWhole.AddRange(whole.Except(split).Select(d => $"{Path.GetFileName(file)} {d}"));
            onlySplit.AddRange(split.Except(whole).Select(d => $"{Path.GetFileName(file)} {d}"));
        }

        _output.WriteLine($"分けて解析したファイル: {splitFiles}");
        _output.WriteLine($"まとめて解析したときだけ出る指摘: {onlyWhole.Count}");
        onlyWhole.Take(MaxExamplesPerRule * 4).ToList().ForEach(d => _output.WriteLine($"      {d}"));
        _output.WriteLine($"分けて解析したときだけ出る指摘: {onlySplit.Count}");
        onlySplit.Take(MaxExamplesPerRule * 4).ToList().ForEach(d => _output.WriteLine($"      {d}"));

        Assert.True(
            onlyWhole.Count == 0 && onlySplit.Count == 0,
            $"Pass やカーネルに分けると指摘が変わります。\n{string.Join("\n", onlyWhole.Concat(onlySplit).Take(10))}");

        // Pass ごとに判定するルールの指摘を、位置とメッセージで表す。
        HashSet<string> Describe(string path, string source)
        {
            SourceText text = SourceText.From(source, path);
            Semantics.ShaderCompilation compilation = path.EndsWith(".compute", StringComparison.OrdinalIgnoreCase)
                ? Semantics.ShaderCompilation.CreateForHlsl(text, options)
                : Semantics.ShaderCompilation.Create(text, ShaderLab.ShaderLabSyntaxTree.Parse(text), options);

            return
            [
                .. driver.Analyze(compilation.CreateAnalysisTarget())
                    .Where(d => !ShaderWideRules.Contains(d.Id))
                    .Select(d => $"{d.Id}({d.Location.LineSpan.Start}) {d.GetMessage()}"),
            ];
        }
    }

    /// <summary>
    /// シェーダーを、Pass (カーネル) を 1 つだけ残したものに分ける。ほかは空白で塗りつぶし、位置を保つ。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <param name="content">ファイルの中身。</param>
    /// <param name="options">解析の設定。カーネルを見つけるのに使う。</param>
    /// <returns>分けたもの。分ける必要が無ければ空。</returns>
    /// <remarks>
    /// カーネルは、解析が読んだ <c>#pragma kernel</c> の行で分ける。
    /// コメントの中や、読み飛ばした分岐の中の行を数えると、カーネルの無いものを作ってしまう。
    /// </remarks>
    private static ImmutableArray<string> SplitIntoBlocks(string path, string content, Semantics.SemanticsOptions options)
    {
        List<TextSpan> blocks = [];

        if (path.EndsWith(".compute", StringComparison.OrdinalIgnoreCase))
        {
            SourceText text = SourceText.From(content, path);
            Semantics.ShaderCompilation compilation = Semantics.ShaderCompilation.CreateForHlsl(text, options);

            blocks.AddRange(compilation.Programs
                .SelectMany(program => program.Tree.PreprocessResult.Pragmas)
                .Where(pragma => pragma.Name == "kernel"
                                 && !pragma.NameToken.IsFromMacroExpansion
                                 && string.Equals(pragma.NameToken.Source.FilePath, path, StringComparison.Ordinal))
                .Select(pragma => text.GetLineSpan(text.GetLinePosition(pragma.NameToken.Span.Start).Line))
                .Distinct());
        }
        else
        {
            SourceText text = SourceText.From(content, path);

            blocks.AddRange(ShaderLab.ShaderLabSyntaxTree.Parse(text).Root
                .DescendantNodesAndSelf()
                .OfType<ShaderLab.Syntax.PassSyntax>()
                .Select(pass => pass.Span));
        }

        if (blocks.Count < 2)
        {
            return [];
        }

        return
        [
            .. blocks.Select(kept =>
            {
                char[] chars = content.ToCharArray();

                foreach (TextSpan other in blocks.Where(b => b != kept))
                {
                    for (int i = other.Start; i < other.End; i++)
                    {
                        if (chars[i] is not ('\r' or '\n'))
                        {
                            chars[i] = ' ';
                        }
                    }
                }

                return new string(chars);
            }),
        ];
    }
}
