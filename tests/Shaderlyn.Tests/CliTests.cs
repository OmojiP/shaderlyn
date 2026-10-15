using System.Collections.Immutable;
using System.Reflection;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Unity.Rules;

namespace Shaderlyn.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void 引数が無い場合はヘルプ表示になる()
    {
        Assert.True(CommandLineOptions.TryParse([], out CommandLineOptions? options, out _));
        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void 対象パスとオプションを解析できる()
    {
        Assert.True(CommandLineOptions.TryParse(
            ["Assets/Shaders", "--format", "sarif", "--error-on", "error", "-o", "out.sarif"],
            out CommandLineOptions? options,
            out _));

        Assert.Equal(["Assets/Shaders"], options.InputPaths.ToArray());
        Assert.Equal(OutputFormat.Sarif, options.Format);
        Assert.Equal(DiagnosticSeverity.Error, options.ErrorOn);
        Assert.Equal("out.sarif", options.OutputPath);
    }

    [Fact]
    public void 既定の閾値は警告になる()
    {
        Assert.True(CommandLineOptions.TryParse(["x.shader"], out CommandLineOptions? options, out _));
        Assert.Equal(DiagnosticSeverity.Warning, options.ErrorOn);
        Assert.Equal(OutputFormat.Text, options.Format);
    }

    [Theory]
    [InlineData("--format")]
    [InlineData("--error-on")]
    [InlineData("--output")]
    public void 値を伴うオプションに値が無ければエラーになる(string option)
    {
        Assert.False(CommandLineOptions.TryParse([option], out _, out string? error));
        Assert.Contains(option, error, StringComparison.Ordinal);
    }

    [Fact]
    public void 不正な形式名はエラーになる()
    {
        Assert.False(CommandLineOptions.TryParse(["x", "--format", "xml"], out _, out string? error));
        Assert.Contains("xml", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 重要度にnoneは指定できない()
    {
        // none は「ルールを無効化する」ための値であり、終了コードの閾値としては意味を成さない。
        Assert.False(CommandLineOptions.TryParse(["x", "--error-on", "none"], out _, out _));
    }

    [Fact]
    public void 不明なオプションはエラーになる()
    {
        Assert.False(CommandLineOptions.TryParse(["--nonexistent"], out _, out string? error));
        Assert.Contains("--nonexistent", error, StringComparison.Ordinal);
    }

    [Fact]
    public void シンボル展開の上限を指定できる()
    {
        Assert.True(CommandLineOptions.TryParse(
            ["x", "--max-symbol-variants", "16"], out CommandLineOptions? options, out _));

        Assert.Equal(16, options!.MaxSymbolVariants);
    }

    [Fact]
    public void シンボル展開の上限を省略すると既定値のままになる()
    {
        // 省略と 0 の指定は違う。0 は「1 つも展開しない」という指定である。
        Assert.True(CommandLineOptions.TryParse(["x"], out CommandLineOptions? options, out _));

        Assert.Null(options!.MaxSymbolVariants);
    }

    [Fact]
    public void ヘルプに載せたオプションはすべて受け付ける()
    {
        // オプションは表で引いている。表に足し忘れると、ヘルプにあるのに「不明なオプション」になる。
        string[] documented =
        [
            .. System.Text.RegularExpressions.Regex
                .Matches(CommandLineOptions.GetUsage(), @"(?<=^|[\s,])(--[a-z-]+|-[a-z])\b", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Value)
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.Contains("--max-symbol-variants", documented);

        foreach (string option in documented)
        {
            CommandLineOptions.TryParse(["x", option], out _, out string? error);
            Assert.DoesNotContain("不明なオプション", error ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 廃止したkeep_both_branchesは不明なオプションになる()
    {
        // 切り分け用だったオプションを CLI から外した。
        // 残っているスクリプトが、何も伝えられないまま別の挙動にならないよう、不明なオプションとして断る。
        Assert.False(CommandLineOptions.TryParse(
            ["x", "--keep-both-branches", "_A"], out _, out string? error));

        Assert.Contains("--keep-both-branches", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("many")]
    public void シンボル展開の上限に整数以外は指定できない(string value)
    {
        // 負の数は「上限なし」に見えるが、そうは動かない。
        // 意図と挙動がずれる指定は、何も伝えずに丸めることはせず断る。
        Assert.False(CommandLineOptions.TryParse(
            ["x", "--max-symbol-variants", value], out _, out string? error));

        Assert.Contains(value, error, StringComparison.Ordinal);
    }

    [Fact]
    public void 対象パスが無ければエラーになる()
    {
        Assert.False(CommandLineOptions.TryParse(["--format", "json"], out _, out string? error));
        Assert.Contains("パス", error, StringComparison.Ordinal);
    }
}

public sealed class ProgramTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-test-{Guid.NewGuid():N}");

    public ProgramTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // テストの後始末が失敗してもテスト結果には影響させない。
        }
    }

    /// <summary>
    /// 利用者が書いたヘッダで書き分けたマクロの誤りを、その構成のときとして報告することを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前は、既定の構成で読み飛ばしたヘッダの分岐の名前を、名前だけで「解析されなかった場所にある名前」としていた。
    /// 誤った名前そのものがその分岐にあるので、<c>_MODE_A</c> の木で見つけても報告されなかった。
    /// SL0004 もヘッダの位置では出しておらず、検査を見送ったことも伝わらなかった。
    /// </para>
    /// <para>
    /// 今は利用者のヘッダの定義も条件付きで覚え、使う文を定義ごとに複製する (条件の巻き上げ)。
    /// 文の種類が構成で変わっても SL0004 にならない。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("#ifdef _MODE_A\n#define BODY { v = missingA; }\n#else\n#define BODY v = 3;\n#endif", "_MODE_A")]
    [InlineData("#if defined(_MODE_B)\n#define BODY v = 2;\n#elif defined(_MODE_A)\n#define BODY { v = missingA; }\n#else\n#define BODY v = 3;\n#endif", "_MODE_A")]
    [InlineData("#ifdef _MODE_A\nfloat Helper() { return missingA; }\n#else\nfloat Helper() { return 0; }\n#endif\n#define BODY v = Helper();", "_MODE_A")]
    public async Task 利用者のヘッダで書き分けたマクロの誤りを構成付きで報告する(string header, string symbol)
    {
        File.WriteAllText(Path.Combine(_workDirectory, "Body.hlsl"), header.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        CreateShaderFile(
            "Test.shader",
            """
            Shader "Test"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #pragma multi_compile _ _MODE_A _MODE_B
                        #include "Body.hlsl"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target
                        {
                            float v = 0;
                            if (v > 0)
                                BODY
                            return v;
                        }
                        ENDHLSL
                    }
                }
            }
            """);

        (_, string output, _) = await RunAsync(Path.Combine(_workDirectory, "Test.shader"), "--no-config");

        Assert.Contains($"Body.hlsl", output, StringComparison.Ordinal);
        Assert.Contains($"'missingA' は 構成：{symbol} のとき宣言されていません", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SL0004", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 利用者が書いたヘッダの中で条件を追えなかった箇所も、SL0004 で伝えることを検証する。
    /// </summary>
    /// <remarks>
    /// 定義が 5 通りあるので複製しない。Unity やパッケージのヘッダと違い、利用者が直せる。
    /// </remarks>
    [Fact]
    public async Task 利用者のヘッダで条件を追えなかった箇所を報告する()
    {
        File.WriteAllText(
            Path.Combine(_workDirectory, "Body.hlsl"),
            string.Join(
                Environment.NewLine,
                "#if defined(_MODE_A)",
                "#define BODY { v = 1; }",
                "#elif defined(_MODE_B)",
                "#define BODY v = 2;",
                "#elif defined(_MODE_C)",
                "#define BODY v = 3;",
                "#elif defined(_MODE_D)",
                "#define BODY v = 4;",
                "#else",
                "#define BODY v = 5;",
                "#endif"));
        CreateShaderFile(
            "Test.shader",
            """
            Shader "Test"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #pragma multi_compile _ _MODE_A _MODE_B _MODE_C _MODE_D
                        #include "Body.hlsl"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target
                        {
                            float v = 0;
                            if (v > 0)
                                BODY
                            return v;
                        }
                        ENDHLSL
                    }
                }
            }
            """);

        (_, string output, _) = await RunAsync(Path.Combine(_workDirectory, "Test.shader"), "--no-config");

        Assert.Contains("Body.hlsl", output, StringComparison.Ordinal);
        Assert.Contains("SL0004", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 別々のファイルの局所変数を、同じ範囲の重複した宣言 (HL0314) と報告しないことを検証する。
    /// </summary>
    /// <remarks>
    /// 範囲を開き波括弧のファイルの中の位置だけで見分けていたので、
    /// 別のファイルの関数の開き波括弧がたまたま同じ位置にあると、両方の局所変数を突き合わせていた。
    /// Unreal Engine のエンジンのシェーダー (SobolRandom.ush の Result など) で起きていた。
    /// </remarks>
    [Fact]
    public async Task 別のファイルの局所変数を重複した宣言と報告しない()
    {
        // どちらのファイルでも、関数の開き波括弧と局所変数が同じ位置に来るようにする。
        File.WriteAllText(Path.Combine(_workDirectory, "A.hlsl"), "float A()\n{\n    float r = 1;\n    return r;\n}\n");
        File.WriteAllText(Path.Combine(_workDirectory, "B.hlsl"), "float B()\n{\n    float r = 2;\n    return r;\n}\n");
        CreateShaderFile(
            "Test.hlsl",
            "#include \"A.hlsl\"\n#include \"B.hlsl\"\nfloat4 main() : SV_Target { return A() + B(); }\n");
        File.WriteAllText(
            Path.Combine(_workDirectory, ".shaderlyn.yaml"),
            "version: 1\nenvironment: hlsl\nunits:\n  - files: Test.hlsl\n    entry: main\n    target: ps_6_0\n");

        (_, string output, _) = await RunAsync(Path.Combine(_workDirectory, "Test.hlsl"));

        Assert.DoesNotContain("HL0314", output, StringComparison.Ordinal);
    }

    /// <summary>警告 (SL1021: Cull の不正な値) を 1 件出すシェーダー。</summary>
    private const string ShaderWithWarning = """
        Shader "Test/Warning"
        {
            SubShader
            {
                Tags { "Queue" = "Geometry" }
                Pass { Cull Sideways }
            }
        }
        """;

    /// <summary>
    /// <c>--min-severity</c> は出力だけを絞り、終了コードは絞る前のすべての指摘で決める。
    /// 表示を絞ったことで CI の合否が変わってはならない。
    /// </summary>
    [Fact]
    public async Task 重要度で出力を絞っても終了コードはすべての指摘で決める()
    {
        CreateShaderFile("A.shader", ShaderWithWarning);

        (ExitCode code, string output, _) = await RunAsync(_workDirectory, "--no-config", "--min-severity", "error");

        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.DoesNotContain("SL1021", output, StringComparison.Ordinal);
        Assert.Contains("ほかに 1 件を出力していません", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 設定ファイルの <c>report: min-severity</c> は形式ごとに書ける。書かなかった形式はすべての指摘を出す。
    /// 設定ファイルの問題は、絞っても隠さない (綴りの誤りに気付けなくなる)。
    /// </summary>
    [Fact]
    public async Task 設定ファイルで出力の形式ごとに重要度を絞る()
    {
        CreateShaderFile("A.shader", ShaderWithWarning);
        File.WriteAllText(
            Path.Combine(_workDirectory, ".shaderlyn.yaml"),
            "report:\n  min-severity:\n    text: error\n    jsn: info\n");

        (_, string text, _) = await RunAsync(Path.Combine(_workDirectory, "A.shader"));
        (_, string json, _) = await RunAsync(Path.Combine(_workDirectory, "A.shader"), "--format", "json");
        (_, string commandLine, _) = await RunAsync(Path.Combine(_workDirectory, "A.shader"), "--min-severity", "info");

        Assert.DoesNotContain("SL1021", text, StringComparison.Ordinal);
        Assert.Contains("'jsn'", text, StringComparison.Ordinal);
        Assert.Contains("SL1021", json, StringComparison.Ordinal);
        Assert.Contains("SL1021", commandLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("hidden")]
    public void 出力を絞る重要度にはinfoからerrorを指定する(string value)
    {
        Assert.False(CommandLineOptions.TryParse(["x", "--min-severity", value], out _, out string? error));
        Assert.Contains("--min-severity", error, StringComparison.Ordinal);
    }

    private string CreateShaderFile(string name, string content = "Shader \"Test\" { }")
    {
        string path = Path.Combine(_workDirectory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(ExitCode Code, string Output, string Error)> RunAsync(
        params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(args, BuiltInAnalyzers.All, output, error);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>
    /// ファイルを並列に解析しても、出力が実行のたびに変わらないことを検証する。
    /// </summary>
    /// <remarks>
    /// <b>出力が安定しないリンタは、差分での比較もベースラインの照合も成り立たない。</b>
    /// 解析は複数のファイルを並列に処理するため、結果の整列に漏れがあると
    /// 実行のたびに順序が入れ替わる。ファイル数を多めにして、
    /// 処理順の揺らぎが出力に現れないことを確かめる。
    /// </remarks>
    [Fact]
    public async Task 並列に解析しても出力の順序が安定する()
    {
        for (int i = 0; i < 24; i++)
        {
            // 名前順と内容が対応しないようにして、処理順への依存があれば現れるようにする。
            CreateShaderFile(
                $"Shader{i:D2}.shader",
                $$"""
                Shader "Test/Shader{{23 - i}}"
                {
                    SubShader
                    {
                        Tags { "Quue" = "Transparent" }
                        Pass { Cull Sideways }
                    }
                }
                """);
        }

        (ExitCode firstCode, string firstOutput, _) = await RunAsync(_workDirectory, "--error-on", "error");
        (ExitCode secondCode, string secondOutput, _) = await RunAsync(_workDirectory, "--error-on", "error");

        Assert.Equal(firstCode, secondCode);
        Assert.Equal(firstOutput, secondOutput);

        // 実際に指摘が出ていなければ順序の検証にならない。
        Assert.Contains("SL1021", firstOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ヘルプは成功終了する()
    {
        (ExitCode code, string output, _) = await RunAsync("--help");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("shaderlyn", output, StringComparison.Ordinal);
        Assert.Contains("--error-on", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 存在しないパスはツールエラーになる()
    {
        // 「指摘が無かった」ではなく「ツールが失敗した」と区別できることが重要。
        // CI で設定を間違えたまま検査が素通りするのを防ぐ。
        (ExitCode code, _, string error) = await RunAsync(Path.Combine(_workDirectory, "nope.shader"));

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains("存在しません", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 引数の誤りはツールエラーになる()
    {
        (ExitCode code, _, string error) = await RunAsync("--format", "invalid", "x");

        Assert.Equal(ExitCode.ToolError, code);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task 指摘が無ければ成功終了する()
    {
        CreateShaderFile("Test.shader");

        (ExitCode code, string output, _) = await RunAsync(_workDirectory);

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task フォルダーを再帰的に探索する()
    {
        CreateShaderFile("Test.shader");
        CreateShaderFile(Path.Combine("Nested", "Deep", "Other.shader"));
        CreateShaderFile("NotAShader.txt");

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("2 ファイルを解析", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解析の対象外のファイルを見つけたことを伝える()
    {
        // Shader Graph とレイトレーシングシェーダーは解析しない。黙って飛ばすと検査したつもりになる。
        CreateShaderFile("Test.shader");
        CreateShaderFile("Graph.shadergraph", "{}");
        CreateShaderFile("Sub.shadersubgraph", "{}");
        CreateShaderFile("Rays.raytrace", "");

        (ExitCode code, string output, string error) = await RunAsync(_workDirectory);

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
        Assert.Contains("解析の対象外のファイルがあります", error, StringComparison.Ordinal);
        Assert.Contains(".shadergraph 1 件", error, StringComparison.Ordinal);
        Assert.Contains(".raytrace 1 件", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 直接指定した対象外のファイルは解析しない()
    {
        CreateShaderFile("Graph.shadergraph", "{}");

        (ExitCode code, string output, string error) = await RunAsync(Path.Combine(_workDirectory, "Graph.shadergraph"));

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("0 ファイルを解析", output, StringComparison.Ordinal);
        Assert.Contains("解析の対象外です", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GLSLPROGRAMは解析の対象外と伝える()
    {
        CreateShaderFile("Glsl.shader", """
            Shader "Test/Glsl"
            {
                SubShader
                {
                    Pass
                    {
                        GLSLPROGRAM
                        void main() { }
                        ENDGLSL
                    }
                }
            }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("SL0005", output, StringComparison.Ordinal);
        Assert.Contains("GLSLPROGRAM", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unityプロジェクト直下のライブラリは解析の起点から除外される()
    {
        // Library/PackageCache には URP など Unity 公式のシェーダーが大量にあり、
        // 利用者が直せないうえ解析時間だけが膨れ上がる。
        // 兄弟の Assets の存在をもって Unity プロジェクトルートと判定するため、
        // 実際のプロジェクト構造を再現する。
        CreateShaderFile(Path.Combine("Assets", "Test.shader"));
        CreateShaderFile(Path.Combine("Library", "PackageCache", "Lit.shader"));

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unityプロジェクトでない場所のライブラリという名前のフォルダーは除外されない()
    {
        // 名前だけで除外すると、利用者が Library という名前のフォルダーに置いた
        // シェーダーが、何も伝えられずにスキップされる。「検査したつもりで何も検査していない」状態は
        // リンタとして最悪の失敗なので、この挙動は明示的に固定しておく。
        CreateShaderFile(Path.Combine("Library", "Custom.shader"));

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 一時フォルダー配下でも解析される()
    {
        // 作業フォルダー自体がシステムの一時領域 (パスに Temp を含む) の下にある。
        // パス中の語句で除外する実装だとここで全ファイルが消える。
        CreateShaderFile("Test.shader");

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 出力先ファイルを指定できる()
    {
        CreateShaderFile("Test.shader");
        string outputPath = Path.Combine(_workDirectory, "report", "out.txt");

        (ExitCode code, string consoleOutput, _) = await RunAsync(_workDirectory, "-o", outputPath);

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(consoleOutput);
        Assert.True(File.Exists(outputPath));
        Assert.Contains("1 ファイルを解析", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解析の内訳を標準エラーへ出せる()
    {
        // 速さを直す前に測るためのものである。
        // 標準出力へ混ぜると JSON や SARIF が壊れるので、標準エラーへ出す。
        CreateShaderFile("Test.shader");

        (ExitCode code, string output, string error) =
            await RunAsync(_workDirectory, "--timings");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("解析の内訳", error, StringComparison.Ordinal);
        Assert.Contains("展開とセマンティックモデル", error, StringComparison.Ordinal);
        Assert.DoesNotContain("解析の内訳", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 環境を表示するときは解析しない()
    {
        // 環境の誤りは指摘が出ないことでしか現れない。解析の前に、見つけた設定と解決できない #include を見せる。
        CreateShaderFile(
            Path.Combine("Assets", "Test.shader"),
            "Shader \"Test\" { SubShader { Pass { HLSLPROGRAM\n#include \"Missing.hlsl\"\nENDHLSL } } }");
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "unity:\n  profile: hdrp\n");
        Directory.CreateDirectory(Path.Combine(_workDirectory, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(_workDirectory, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 1.0.0f1\n");

        (ExitCode code, string output, _) = await RunAsync(Path.Combine(_workDirectory, "Assets"), "--env");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("解析はしていません", output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_workDirectory, ".shaderlyn.yaml"), output, StringComparison.Ordinal);
        Assert.Contains("プロファイル: hdrp (設定ファイル)", output, StringComparison.Ordinal);
        Assert.Contains("解決できた: 0 / 1", output, StringComparison.Ordinal);
        Assert.Contains("\"Missing.hlsl\"", output, StringComparison.Ordinal);
        Assert.Contains("解析対象は Unity プロジェクトの中にあります", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ファイルを解析", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 知らない環境を指定するとエラーにする()
    {
        CreateShaderFile(Path.Combine("Assets", "Test.shader"), "Shader \"Test\" { SubShader { Pass { } } }");

        (ExitCode code, _, string error) = await RunAsync(Path.Combine(_workDirectory, "Assets"), "--environment", "godot");

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains("環境 'godot' はありません", error, StringComparison.Ordinal);
        Assert.Contains("unity", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 設定ファイルの知らない環境は書いた行に報告する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "version: 1\nenvironment: unreel\n");

        Assert.True(CommandLineOptions.TryParse([_workDirectory], out CommandLineOptions? options, out _));
        EffectiveSettings settings = EffectiveSettings.Create(options);

        Diagnostic diagnostic = Assert.Single(settings.ConfigurationDiagnostics);
        Assert.Equal("TOOL0003", diagnostic.Id);
        Assert.Equal(1, diagnostic.Location.LineSpan.Start.Line);
        Assert.Contains("unreel", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 環境の表示に環境を出す()
    {
        CreateShaderFile(Path.Combine("Assets", "Test.shader"), "Shader \"Test\" { SubShader { Pass { } } }");

        (ExitCode code, string output, _) = await RunAsync(Path.Combine(_workDirectory, "Assets"), "--env", "--environment", "unity");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("環境: Unity (コマンドライン)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 設定ファイルの unity: project を、--unity-project の代わりに使うことを検証する。
    /// </summary>
    [Fact]
    public async Task 設定ファイルのUnityプロジェクトを使う()
    {
        CreateShaderFile(
            Path.Combine("Assets", "Test.shader"),
            "Shader \"Test\" { SubShader { Pass { HLSLPROGRAM\nENDHLSL } } }");
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "unity:\n  project: .\n");
        Directory.CreateDirectory(Path.Combine(_workDirectory, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(_workDirectory, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 1.0.0f1\n");

        (ExitCode code, string output, _) = await RunAsync(Path.Combine(_workDirectory, "Assets"), "--env");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains($"--unity-project: {Path.GetFullPath(_workDirectory)}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("解析対象の上に Unity プロジェクトがあります", output, StringComparison.Ordinal);
    }

    [Fact]
    public void 環境の表示は対象のパスが無くてもよい()
    {
        Assert.True(CommandLineOptions.TryParse(["--env"], out CommandLineOptions? options, out _));
        Assert.True(options.ShowEnvironment);
    }

    [Fact]
    public async Task ルール一覧を表示できる()
    {
        (ExitCode code, string output, _) = await RunAsync("--list-rules");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("ルールが実装されています", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 閾値以上の指摘があれば終了コード1を返す()
    {
        CreateShaderFile("Test.shader");

        // ファイル全体に対して必ず警告を出すアナライザを注入して、終了コードの分岐を検証する。
        ImmutableArray<DiagnosticAnalyzer> analyzers = [new AlwaysWarnAnalyzer()];
        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync([_workDirectory], analyzers, output, error);

        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("TEST9001", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 閾値を上げれば警告では終了コード1にならない()
    {
        CreateShaderFile("Test.shader");

        ImmutableArray<DiagnosticAnalyzer> analyzers = [new AlwaysWarnAnalyzer()];
        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [_workDirectory, "--error-on", "error"], analyzers, output, error);

        // 指摘自体は出力されるが、終了コードは成功のままであること。
        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("TEST9001", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>パッケージのヘッダを取り込むシェーダー。</summary>
    private const string PackageIncludingShader = """
        Shader "Test/Lit"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                    float4 vert(float4 position : POSITION) : SV_POSITION { return position; }
                    half4 frag() : SV_Target { return TEST_COLOR; }
                    ENDHLSL
                }
            }
        }
        """;

    /// <summary>
    /// ヘッダを 1 つも解決できない環境では、解析を続けずエラーにすることを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>これは終了コードを決める仕様である。</b>
    /// 前提が揃っていない CI で「指摘 0 件」の緑が出続ける状態は、
    /// 誤検出よりも発見が遅れる。
    /// 何も伝えずに警告へ緩んでも、利用者からは正常な結果と区別が付かないため、
    /// ここで固定する。
    /// </para>
    /// <para>
    /// <b>Unity が入っている機械でも同じ結果になるようにする。</b>
    /// 指定が無ければインストール済みの Editor から同梱のヘッダを引けてしまうので、
    /// <c>--unity-editor</c> に存在しないパスを渡して「Unity の無い CI」を再現する。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ヘッダを1つも解決できなければTOOL0004でエラーになる()
    {
        CreateShaderFile("Lit.shader", PackageIncludingShader);

        (ExitCode code, string output, _) = await RunAsync(
            _workDirectory, "--unity-editor", Path.Combine(_workDirectory, "no-unity"));

        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("error TOOL0004", output, StringComparison.Ordinal);

        // 直し方まで伝わっていること。原因は環境にあり、シェーダーを読んでも直せない。
        Assert.Contains("--unity-project", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unity プロジェクトの指定が無くても、Editor 同梱のパッケージから解決できることを検証する。
    /// </summary>
    /// <remarks>
    /// <c>Library/PackageCache</c> が無くても、Editor に同梱された URP のヘッダは引ける。
    /// この経路が壊れると、Unity の入った機械でも TOOL0004 で落ちるようになる。
    /// </remarks>
    [Fact]
    public async Task Editor同梱のパッケージからヘッダを解決できる()
    {
        CreateShaderFile("Lit.shader", PackageIncludingShader);
        string editorData = CreateBuiltInPackageHeader();

        (ExitCode code, string output, _) = await RunAsync(_workDirectory, "--unity-editor", editorData);

        Assert.Equal(ExitCode.Success, code);
        Assert.DoesNotContain("TOOL0004", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>#include</c> を書いていないシェーダーは、ヘッダを引けない環境でも検査されることを検証する。
    /// </summary>
    /// <remarks>
    /// 取り込むものが無いファイルに「1 つも解決できません」と言っても意味がなく、
    /// ヘッダに依存しない検査まで巻き添えで止まるのは行き過ぎである。
    /// </remarks>
    [Fact]
    public async Task includeを書いていないシェーダーはTOOL0004にならない()
    {
        CreateShaderFile(
            "Plain.shader",
            """
            Shader "Test/Plain"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        float4 vert(float4 position : POSITION) : SV_POSITION { return position; }
                        half4 frag() : SV_Target { return half4(1, 1, 1, 1); }
                        ENDHLSL
                    }
                }
            }
            """);

        (ExitCode code, string output, _) = await RunAsync(
            _workDirectory, "--unity-editor", Path.Combine(_workDirectory, "no-unity"));

        Assert.Equal(ExitCode.Success, code);
        Assert.DoesNotContain("TOOL0004", output, StringComparison.Ordinal);
    }

    /// <summary>Editor 同梱パッケージのヘッダを用意し、Editor のデータフォルダーを返す。</summary>
    /// <returns>作成した Editor のデータフォルダー。</returns>
    private string CreateBuiltInPackageHeader()
    {
        string editorData = Path.Combine(_workDirectory, "EditorData");
        string headerDirectory = Path.Combine(
            editorData,
            "Resources",
            "PackageManager",
            "BuiltInPackages",
            "com.unity.render-pipelines.universal",
            "ShaderLibrary");

        Directory.CreateDirectory(headerDirectory);
        File.WriteAllText(
            Path.Combine(headerDirectory, "Core.hlsl"),
            "#define TEST_COLOR half4(1, 1, 1, 1)");

        return editorData;
    }

    private sealed class AlwaysWarnAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Descriptor = new(
            "TEST9001", "常に警告", "常に警告を出します", "Test", DiagnosticSeverity.Warning);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Descriptor];

        public override void Initialize(AnalysisContext context)
            => context.RegisterSyntaxTreeAction(c => c.ReportDiagnostic(Descriptor, new Core.Text.TextSpan(0, 0)));
    }
}

public sealed class BuiltInAnalyzerRegistrationTests
{
    /// <summary>
    /// Native AOT ではリフレクションによるアナライザの自動収集ができないため、
    /// <see cref="BuiltInAnalyzers.All"/> への登録は手作業になる。
    /// その登録漏れをテスト側のリフレクションで機械的に検出する。
    /// テストは AOT の制約を受けないのでリフレクションを使ってよい。
    /// </summary>
    /// <remarks>
    /// <b>走査するのはルールが実装されているアセンブリである。</b>
    /// <see cref="BuiltInAnalyzers"/> 自身が属する CLI のアセンブリを走査しても、
    /// そこにアナライザは 1 つも無いので何も検出しない。
    /// 「検査しているように見えて何も検査していない」テストになる。
    /// </remarks>
    [Fact]
    public void アセンブリ内の全アナライザがBuiltInAnalyzersに登録されている()
    {
        // 組み込みのルールは、汎用の HLSL のルールと、Unity の環境のルール、Unreal の環境のルールの 3 つのアセンブリにある。
        IEnumerable<Type> declaredAnalyzers = new[]
            {
                typeof(Rules.CallSignatureAnalyzer).Assembly,
                typeof(TagAnalyzer).Assembly,
                typeof(Unreal.Rules.GeneratedIncludeAnalyzer).Assembly,
            }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(t => t is { IsAbstract: false, IsNested: false } && t.IsSubclassOf(typeof(DiagnosticAnalyzer)));

        HashSet<Type> registered = [.. BuiltInAnalyzers.All.Select(a => a.GetType())];

        List<string> missing = [.. declaredAnalyzers.Where(t => !registered.Contains(t)).Select(t => t.FullName!)];

        // 走査対象を間違えて「何も見つからない」状態になっていないことを確かめる。
        Assert.NotEmpty(declaredAnalyzers);

        Assert.True(
            missing.Count == 0,
            $"次のアナライザが BuiltInAnalyzers.All に登録されていません: {string.Join(", ", missing)}");
    }

    [Fact]
    public void 登録されたアナライザのルールIDが重複していない()
    {
        List<string> duplicates = [.. BuiltInAnalyzers.All
            .SelectMany(a => a.SupportedDiagnostics)
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .Where(g => g.Select(d => d).Distinct().Count() > 1)
            .Select(g => g.Key)];

        Assert.True(duplicates.Count == 0, $"ルール ID が重複しています: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void 組み込みルールには必ず説明が付いている()
    {
        // 「なぜ問題なのか」が読めない指摘は無視されるか、誤って抑制される。
        List<string> withoutDescription = [.. BuiltInAnalyzers.All
            .SelectMany(a => a.SupportedDiagnostics)
            .Where(d => string.IsNullOrWhiteSpace(d.Description))
            .Select(d => d.Id)];

        Assert.True(
            withoutDescription.Count == 0,
            $"次のルールに Description がありません: {string.Join(", ", withoutDescription)}");
    }
}

public sealed class DiagnosticDescriptorTests
{
    [Theory]
    [InlineData("SL1003")]
    [InlineData("HL0101")]
    [InlineData("URP0001")]
    [InlineData("USER0001")]
    public void 規定の書式のルールIDは受け入れられる(string id)
    {
        DiagnosticDescriptor descriptor = new(id, "表題", "メッセージ", "Test", DiagnosticSeverity.Warning);
        Assert.Equal(id, descriptor.Id);
    }

    [Theory]
    [InlineData("sl1003")]   // 小文字
    [InlineData("SL103")]    // 桁数不足
    [InlineData("SL10033")]  // 桁数超過
    [InlineData("1003")]     // 接頭辞なし
    [InlineData("SL-1003")]  // 記号混入
    public void 不正な書式のルールIDは拒否される(string id)
    {
        Assert.Throws<ArgumentException>(
            () => new DiagnosticDescriptor(id, "表題", "メッセージ", "Test", DiagnosticSeverity.Warning));
    }

    [Fact]
    public void 既定の重要度にnoneは指定できない()
    {
        // ルールの無効化は設定ファイルの責務であり、ルール定義側で行うことではない。
        Assert.Throws<ArgumentException>(
            () => new DiagnosticDescriptor("SL1003", "表題", "メッセージ", "Test", DiagnosticSeverity.None));
    }

    [Fact]
    public void ルールIDの接頭辞を取り出せる()
    {
        DiagnosticDescriptor descriptor = new("URP0001", "表題", "メッセージ", "Test", DiagnosticSeverity.Warning);
        Assert.Equal("URP", descriptor.IdPrefix);
    }

    [Fact]
    public void 書式と引数が食い違ってもメッセージ生成は失敗しない()
    {
        // ルール実装の記述ミスでツール全体が停止するのは、リンタとして望ましくない。
        DiagnosticDescriptor descriptor = new(
            "SL1003", "表題", "{0} と {1}", "Test", DiagnosticSeverity.Warning);

        Diagnostic diagnostic = Diagnostic.Create(
            descriptor,
            Location.Create(Core.Text.SourceText.From("x"), new Core.Text.TextSpan(0, 1)),
            "引数が1つだけ");

        Assert.Equal("{0} と {1}", diagnostic.GetMessage());
    }
}
