using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Shaderlyn.Tests;

/// <summary>
/// 実例集 (docs/custom-rules/cookbook.md) に載せた C# のコードが、今の API でコンパイルできることを検証する。
/// </summary>
/// <remarks>
/// <para>
/// <b>ドキュメントのコードは <c>samples/Shaderlyn.Cookbook</c> と一字一句同じではない。</b>
/// 説明のために短くしてあり、診断の定義も <c>Rule</c> という名前だけで済ませている。
/// そのため中身の一致は求めない。確かめるのは、書き写した人がそのまま使える形か、である。
/// </para>
/// <para>
/// <see cref="CookbookTests"/> が動かしているのは <c>samples</c> の側だけである。
/// API を改名したり消したりしたとき、ドキュメントのコードは誰にも気づかれないまま古くなる。
/// </para>
/// </remarks>
public sealed partial class CookbookDocumentTests
{
    private static string DocumentPath
        => Path.Combine(AppContext.BaseDirectory, "docs", "custom-rules", "cookbook.md");

    /// <summary>
    /// ドキュメントのコードが前提にしている <c>using</c>。
    /// </summary>
    /// <remarks>
    /// ドキュメントは読みやすさのために <c>using</c> を省いている。書き写す人が足すものをここに並べる。
    /// </remarks>
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using System.IO;
        using System.Linq;
        using System.Threading.Tasks;
        using Shaderlyn.Cli;
        using Shaderlyn.Core.Analysis;
        using Shaderlyn.Core.Diagnostics;
        using Shaderlyn.Core.Syntax;
        using Shaderlyn.Core.Text;
        using Shaderlyn.Hlsl.Syntax;
        using Shaderlyn.Semantics;
        using Shaderlyn.Semantics.Analysis;
        using Shaderlyn.Semantics.Conditional;
        using Shaderlyn.Semantics.Programs;
        using Shaderlyn.Semantics.Symbols;
        using Shaderlyn.ShaderLab.Syntax;
        using static Shaderlyn.CookbookDocument.Stubs;
        """;

    /// <summary>
    /// ドキュメントのコードが名前だけで参照している診断の定義。
    /// </summary>
    /// <remarks>ドキュメントは冒頭で「診断の定義は省いている」と断っている。</remarks>
    private const string StubSource = """
        namespace Shaderlyn.CookbookDocument;

        internal static class Stubs
        {
            public static Shaderlyn.Core.Diagnostics.DiagnosticDescriptor Rule { get; } = new(
                "DOC0001", "t", "m", "Usage", Shaderlyn.Core.Diagnostics.DiagnosticSeverity.Warning);

            public static Shaderlyn.Core.Diagnostics.DiagnosticDescriptor MissingRule { get; } = new(
                "DOC0002", "t", "m", "Usage", Shaderlyn.Core.Diagnostics.DiagnosticSeverity.Warning);

            public static Shaderlyn.Core.Diagnostics.DiagnosticDescriptor ValueRule { get; } = new(
                "DOC0003", "t", "m", "Usage", Shaderlyn.Core.Diagnostics.DiagnosticSeverity.Warning);
        }
        """;

    [Fact]
    public void 実例集のコードは今のAPIでコンパイルできる()
    {
        List<(int Line, string Code)> blocks = [.. ReadCSharpBlocks(File.ReadAllText(DocumentPath))];

        Assert.NotEmpty(blocks);

        List<SyntaxTree> trees = [CSharpSyntaxTree.ParseText(StubSource)];

        for (int i = 0; i < blocks.Count; i++)
        {
            (int line, string code) = blocks[i];
            trees.Add(CSharpSyntaxTree.ParseText(Wrap(i, code), path: $"cookbook.md:{line}"));
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            "CookbookDocument",
            trees,
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        List<string> errors =
        [
            .. compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Location.SourceTree?.FilePath}: {d.Id} {d.GetMessage()}"),
        ];

        Assert.True(errors.Count == 0, "実例集のコードがコンパイルできません:\n" + string.Join("\n", errors));
    }

    /// <summary>ドキュメントから C# のコードブロックを取り出す。</summary>
    /// <param name="markdown">ドキュメントの中身。</param>
    /// <returns>ブロックの開始行と中身。</returns>
    private static IEnumerable<(int Line, string Code)> ReadCSharpBlocks(string markdown)
    {
        string[] lines = markdown.ReplaceLineEndings("\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "```csharp")
            {
                continue;
            }

            int start = i + 1;
            StringBuilder code = new();

            for (i++; i < lines.Length && lines[i].Trim() != "```"; i++)
            {
                code.AppendLine(lines[i]);
            }

            yield return (start + 1, code.ToString());
        }
    }

    /// <summary>
    /// ブロック 1 つを、単独でコンパイルできる形に包む。
    /// </summary>
    /// <param name="index">ブロックの番号。名前空間を分けるのに使う。</param>
    /// <param name="code">ブロックの中身。</param>
    /// <returns>コンパイルするソース。</returns>
    /// <remarks>
    /// <para>
    /// 型を宣言するブロックは、そのまま名前空間に入れる。
    /// <c>samples</c> と同じ名前の型があるので、ブロックごとに名前空間を分ける。
    /// ほかのブロックの型を参照している場合 (呼び出す側の例) に備えて、すべての名前空間を
    /// <c>using</c> で見えるようにする。
    /// </para>
    /// <para>
    /// 文だけのブロック (自作ルール入りの CLI の <c>Program.cs</c> に書く部分) は、メソッドの本体として包む。
    /// </para>
    /// </remarks>
    private static string Wrap(int index, string code)
    {
        bool declaresType = TypeDeclaration().IsMatch(code);
        string body = declaresType
            ? code
            : $$"""
                internal static class Snippet
                {
                    public static async Task<int> RunAsync(string[] args)
                    {
                {{code}}
                    }
                }
                """;

        return $"{Usings}\n{AllNamespaces()}\nnamespace Shaderlyn.CookbookDocument.Block{index};\n\n{body}";
    }

    /// <summary>すべてのブロックの名前空間を見えるようにする <c>using</c>。</summary>
    /// <returns>並べた <c>using</c>。</returns>
    private static string AllNamespaces()
    {
        int count = ReadCSharpBlocks(File.ReadAllText(DocumentPath)).Count();
        return string.Join("\n", Enumerable.Range(0, count).Select(i => $"using Shaderlyn.CookbookDocument.Block{i};"));
    }

    /// <summary>コンパイルに渡す参照。</summary>
    /// <returns>.NET の標準ライブラリと、このツールのアセンブリ。</returns>
    private static IEnumerable<MetadataReference> References()
    {
        string trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

        return trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    [GeneratedRegex(@"^\s*(public |internal )?(sealed |static |abstract )*(partial )?(class|record|struct) ", RegexOptions.Multiline)]
    private static partial Regex TypeDeclaration();
}
