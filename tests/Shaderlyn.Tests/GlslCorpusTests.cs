using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// GLSL のコーパスを、共通の構文木と、GLSL に固有の構文の解析で読めるかの検証。
/// </summary>
/// <remarks>
/// <para>
/// 環境変数 <c>SHADERLYN_GLSL_CORPUS</c> にコーパスの置き場所 (<c>node tools/corpus/fetch.mjs --only=glsl</c> で取ったもの) を
/// 渡したときだけ動く。コーパスはリポジトリに入れない。
/// </para>
/// <para>
/// GLSL は構文エラーが 0 件であることを確かめる (0.3.0 で GLSL の構文エラーの指摘を出すため)。
/// 読んだ節の数と、エラーの例も出す。Godot と Slang は数えて出すだけで、失敗にはしない。
/// </para>
/// </remarks>
public sealed class GlslCorpusTests(ITestOutputHelper output)
{
    private const string CorpusVariable = "SHADERLYN_GLSL_CORPUS";

    /// <summary>ステージを表す拡張子。<c>.glsl</c> は取り込まれる側なので単体では読まない。</summary>
    private static readonly HashSet<string> StageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese",
        ".mesh", ".task", ".rgen", ".rint", ".rahit", ".rchit", ".rmiss", ".rcall",
    };

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void GLSLのコーパスに構文エラーが無い()
    {
        string? root = Environment.GetEnvironmentVariable(CorpusVariable);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _output.WriteLine($"{CorpusVariable} が無いため飛ばします。");
            return;
        }

        int files = 0;
        int failedFiles = 0;
        Dictionary<string, List<string>> byMessage = new(StringComparer.Ordinal);
        Dictionary<string, int> nodeCounts = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(p => StageExtensions.Contains(Path.GetExtension(p)) && !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            files++;
            SourceText text = SourceText.From(File.ReadAllText(path), path);
            ShaderSyntaxTree tree = GlslSyntaxTree.Parse(text, new PreprocessorOptions
            {
                IncludeResolver = new FileSystemIncludeResolver([]),
            });

            // 誤り無く読めても、誤った形で読んでいるかもしれない。木の中身も数える。
            foreach (Core.Syntax.SyntaxNode node in tree.Root.DescendantNodesAndSelf())
            {
                string kind = node switch
                {
                    IncompleteDeclarationSyntax incomplete => $"読み飛ばした宣言 ({incomplete.Tokens.FirstOrDefault()?.Text})",
                    IncompleteStatementSyntax => "読み飛ばした文",
                    IncompleteExpressionSyntax => "読み飛ばした式",
                    _ => node.GetType().Name,
                };

                nodeCounts[kind] = nodeCounts.GetValueOrDefault(kind) + 1;
            }

            Diagnostic[] errors = [.. tree.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];

            if (errors.Length == 0)
            {
                continue;
            }

            failedFiles++;
            Diagnostic first = errors[0];
            string key = $"{first.Id}: {Normalize(first.GetMessage())}";

            if (!byMessage.TryGetValue(key, out List<string>? examples))
            {
                byMessage[key] = examples = [];
            }

            examples.Add($"{Path.GetRelativePath(root, path)}({first.Location.LineSpan.Start.Line + 1})");
        }

        _output.WriteLine($"ファイル {files} 件、構文エラーのあるファイル {failedFiles} 件");
        _output.WriteLine("節の数:");

        foreach ((string kind, int count) in nodeCounts.OrderByDescending(p => p.Value))
        {
            _output.WriteLine($"  {count,7} {kind}");
        }

        foreach ((string message, List<string> examples) in byMessage.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            _output.WriteLine($"{examples.Count,4} {message}");

            foreach (string example in examples.Take(3))
            {
                _output.WriteLine($"       {example}");
            }
        }

        Assert.True(files > 0, $"{CorpusVariable} の下に GLSL のファイルがありません。");
        Assert.True(failedFiles == 0, $"構文エラーのあるファイルが {failedFiles} 件あります: {string.Join(", ", byMessage.Keys.Take(5))}");
    }

    /// <summary>
    /// Godot と Slang を、いちばん近い言語 (Godot は GLSL、Slang は HLSL 2021) として読んだときに、何が足りないかを数える。
    /// </summary>
    /// <remarks>
    /// D1 (C 系の言語は構文木を共有する) を決める材料。足りないものが構文の追加で済むか、木の形から違うかを見る。
    /// </remarks>
    [Fact]
    public void GodotとSlangを近い言語として読んだときの構文エラーを数える()
    {
        string? root = Environment.GetEnvironmentVariable(CorpusVariable);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _output.WriteLine($"{CorpusVariable} が無いため飛ばします。");
            return;
        }

        Measure(root, [".gdshader"], "Godot (GLSL として読む)", text => GlslSyntaxTree.Parse(text));
        Measure(root, [".slang"], "Slang (HLSL 2021 で読む)", text => Hlsl.HlslSyntaxTree.Parse(text, new PreprocessorOptions
        {
            Language = new Hlsl.Parsing.HlslLanguageOptions { LanguageVersion = Hlsl.Parsing.HlslLanguageVersion.Hlsl2021 },
        }));
    }

    private void Measure(string root, string[] extensions, string title, Func<SourceText, ShaderSyntaxTree> parse)
    {
        int files = 0;
        int failedFiles = 0;
        Dictionary<string, List<string>> byMessage = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(p => extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            files++;
            ShaderSyntaxTree tree = parse(SourceText.From(File.ReadAllText(path), path));
            Diagnostic? first = tree.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);

            if (first is null)
            {
                continue;
            }

            failedFiles++;
            string key = $"{first.Id}: {Normalize(first.GetMessage())} [{first.Location.GetLineText().Trim()}]";
            key = key.Length > 160 ? key[..160] : key;

            if (!byMessage.TryGetValue(key, out List<string>? examples))
            {
                byMessage[key] = examples = [];
            }

            examples.Add($"{Path.GetRelativePath(root, path)}({first.Location.LineSpan.Start.Line + 1})");
        }

        _output.WriteLine($"{title}: ファイル {files} 件、構文エラーのあるファイル {failedFiles} 件");

        foreach ((string message, List<string> examples) in byMessage.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal).Take(40))
        {
            _output.WriteLine($"{examples.Count,4} {message}");
        }
    }

    /// <summary>メッセージの中の名前を伏せ、同じ種類の誤りを 1 つにまとめる。</summary>
    private static string Normalize(string message)
        => System.Text.RegularExpressions.Regex.Replace(message, "'[^']*'", "'…'");
}
