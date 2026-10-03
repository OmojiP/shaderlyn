using System.Text.Json;
using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// 出力形式の検証。
/// </summary>
/// <remarks>
/// <b>出力形式の誤りは「アップロードは成功したのに何も表示されない」という形で現れる。</b>
/// 絶対パスを出す、行番号を 0 始まりのまま出す、といった誤りは
/// ツール側では何のエラーにもならず、GitHub 側で、何も伝えられずに捨てられる。
/// そのため構造と値を明示的に検証する。
/// </remarks>
public sealed class ReportingTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-report-{Guid.NewGuid():N}");

    public ReportingTests()
    {
        Directory.CreateDirectory(Path.Combine(_workDirectory, "Assets", "Shaders"));

        // 8 行目 18 桁に SL1021 が出るシェーダー。
        File.WriteAllText(
            Path.Combine(_workDirectory, "Assets", "Shaders", "Broken.shader"),
            "Shader \"Company/Broken\"\n"
            + "{\n"
            + "    SubShader\n"
            + "    {\n"
            + "        Pass\n"
            + "        {\n"
            + "            Cull Sideways\n"
            + "        }\n"
            + "    }\n"
            + "}\n");
    }

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

    private async Task<string> RunAsync(params string[] extraArgs)
    {
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync(
            [Path.Combine(_workDirectory, "Assets"), "--no-config", "--base-path", _workDirectory, .. extraArgs],
            BuiltInAnalyzers.All,
            output,
            error);

        return output.ToString();
    }

    // ------------------------------------------------------------------
    // SARIF
    // ------------------------------------------------------------------

    [Fact]
    public async Task SARIFの構造が仕様どおりである()
    {
        using JsonDocument document = JsonDocument.Parse(await RunAsync("--format", "sarif"));
        JsonElement root = document.RootElement;

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());

        JsonElement run = root.GetProperty("runs")[0];
        Assert.Equal("shaderlyn", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());

        JsonElement result = run.GetProperty("results")[0];
        Assert.Equal("SL1021", result.GetProperty("ruleId").GetString());
        Assert.Equal("warning", result.GetProperty("level").GetString());

        JsonElement region = result.GetProperty("locations")[0]
            .GetProperty("physicalLocation").GetProperty("region");

        // SARIF の行・桁は 1 始まり。内部表現の 0 始まりをそのまま出すと 1 行ずれる。
        // 指摘は値 'Sideways' を指す (12 個の空白 + "Cull " の後ろ)。
        Assert.Equal(7, region.GetProperty("startLine").GetInt32());
        Assert.Equal(18, region.GetProperty("startColumn").GetInt32());
        Assert.Equal(26, region.GetProperty("endColumn").GetInt32());
    }

    [Fact]
    public async Task SARIFのパスは基点からの相対になる()
    {
        // 絶対パスのままでは GitHub がリポジトリ内のファイルへ対応づけられず、
        // アップロードは成功したのに何も表示されない。
        using JsonDocument document = JsonDocument.Parse(await RunAsync("--format", "sarif"));

        string uri = document.RootElement
            .GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("locations")[0].GetProperty("physicalLocation")
            .GetProperty("artifactLocation").GetProperty("uri").GetString()!;

        Assert.Equal("Assets/Shaders/Broken.shader", uri);
    }

    [Fact]
    public async Task SARIFに指紋が含まれる()
    {
        // 指紋が無いと、行がずれるたびに GitHub が新しいアラートを立てる。
        using JsonDocument document = JsonDocument.Parse(await RunAsync("--format", "sarif"));

        JsonElement fingerprints = document.RootElement
            .GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("partialFingerprints");

        Assert.False(string.IsNullOrEmpty(fingerprints.GetProperty("shaderlyn/v1").GetString()));
    }

    [Fact]
    public async Task SARIFのルール一覧に解析基盤のルールも含まれる()
    {
        // 構文エラー (SL0001 / HL0001) はどのアナライザにも属さない。
        // rules に無いと、GitHub のアラート画面で説明にも詳細にも辿れなくなる。
        using JsonDocument document = JsonDocument.Parse(await RunAsync("--format", "sarif"));

        HashSet<string> ruleIds =
        [
            .. document.RootElement.GetProperty("runs")[0]
                .GetProperty("tool").GetProperty("driver").GetProperty("rules")
                .EnumerateArray()
                .Select(r => r.GetProperty("id").GetString()!)
        ];

        Assert.Contains("SL0001", ruleIds);
        Assert.Contains("HL0001", ruleIds);
        Assert.Contains("TOOL0002", ruleIds);

        // アナライザの実装の誤りを表すルールも含める。
        // 出力に現れる ID が rules に無いのは不整合である。
        Assert.Contains("TOOL0005", ruleIds);
    }

    [Fact]
    public async Task SARIFのルールにはヘルプへのリンクが付く()
    {
        using JsonDocument document = JsonDocument.Parse(await RunAsync("--format", "sarif"));

        JsonElement rule = document.RootElement.GetProperty("runs")[0]
            .GetProperty("tool").GetProperty("driver").GetProperty("rules")
            .EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == "SL1021");

        Assert.EndsWith("SL1021.md", rule.GetProperty("helpUri").GetString()!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // GitHub ワークフローコマンド
    // ------------------------------------------------------------------

    [Fact]
    public async Task GitHub形式はワークフローコマンドを出力する()
    {
        string output = await RunAsync("--format", "github");

        Assert.Contains("::warning file=Assets/Shaders/Broken.shader,", output, StringComparison.Ordinal);
        Assert.Contains("line=7,", output, StringComparison.Ordinal);
        Assert.Contains("::SL1021:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub形式は属性値のコロンを逃がす()
    {
        // 表題にコロンが含まれると、そこで属性が切れて位置がずれる。
        string output = await RunAsync("--format", "github");

        int titleIndex = output.IndexOf("title=", StringComparison.Ordinal);
        int commandIndex = output.IndexOf("::", titleIndex, StringComparison.Ordinal);
        string title = output[(titleIndex + "title=".Length)..commandIndex];

        Assert.DoesNotContain(':', title);
        Assert.Contains("%3A", title, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // JSON
    // ------------------------------------------------------------------

    [Fact]
    public async Task JSON形式の構造が仕様どおりである()
    {
        using JsonDocument document = JsonDocument.Parse(await RunAsync("--format", "json"));
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("summary").GetProperty("analyzedFiles").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("warnings").GetInt32());

        JsonElement diagnostic = root.GetProperty("diagnostics")[0];
        Assert.Equal("SL1021", diagnostic.GetProperty("id").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal("Assets/Shaders/Broken.shader", diagnostic.GetProperty("file").GetString());
        Assert.Equal(7, diagnostic.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task JSON形式は日本語をそのまま出力する()
    {
        // 既定の符号化器は日本語を \uXXXX へ逃がすため、目で確認できる出力にならない。
        string output = await RunAsync("--format", "json");

        Assert.Contains("不正です", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 共通
    // ------------------------------------------------------------------

    [Fact]
    public async Task 存在しない基点はツールエラーにする()
    {
        // 基点が誤っていると相対化に失敗し、GitHub 上で指摘が対応づかない。
        // 何も伝えずに続けると「アップロードしたのに何も出ない」ことになる。
        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [_workDirectory, "--no-config", "--base-path", Path.Combine(_workDirectory, "missing")],
            BuiltInAnalyzers.All,
            output,
            error);

        Assert.Equal(ExitCode.ToolError, code);
    }

    [Fact]
    public async Task SARIFをファイルへ書きながら注釈も出せる()
    {
        // 形式を変えて 2 回解析させないための仕組み。
        // 依存関係を解決した解析は重く、2 回分の時間は PR ごとの実行で無視できない。
        string sarifPath = Path.Combine(_workDirectory, "out.sarif");
        string annotations = await RunAsync("--format", "sarif", "--output", sarifPath, "--annotate");

        // 標準出力にはワークフローコマンドだけが出る。
        Assert.StartsWith("::warning ", annotations, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$schema\"", annotations, StringComparison.Ordinal);

        // SARIF はファイルへ書かれている。
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
        Assert.Equal("2.1.0", document.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task テキスト形式ではメッセージを1文ずつ改行して出す()
    {
        // 「何が起きたか」「どうすればよいか」を 1 行に詰め込むと、
        // 端末の折り返しに任せることになり、文の切れ目が分からないまま読み飛ばされる。
        string output = await RunAsync("--format", "text");
        string[] lines = [.. output.Split('\n').Select(line => line.TrimEnd('\r'))];

        int headline = Array.FindIndex(lines, line => line.Contains("SL1021", StringComparison.Ordinal));

        Assert.True(headline >= 0, output);

        // 見出し行は「パス(行:桁): 重要度 ID: 1 文目」で終わる。
        // ここを崩すとエディタがファイル位置として認識しなくなる。
        Assert.EndsWith("'Sideways' は不正です。", lines[headline], StringComparison.Ordinal);
        Assert.Equal("    指定できる値: Back, Front, Off", lines[headline + 1]);
    }

    [Fact]
    public async Task 機械可読な形式ではメッセージを1行のまま出す()
    {
        // 改行は人間向けの整形であって、メッセージそのものの一部ではない。
        string message = JsonDocument.Parse(await RunAsync("--format", "json"))
            .RootElement.GetProperty("diagnostics")[0]
            .GetProperty("message").GetString()!;

        Assert.DoesNotContain('\n', message);
        Assert.Contains("指定できる値", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task シンボルの中の宣言もビューアーに出す()
    {
        // #ifdef の中の宣言は既定の構成の木には無く、バリアントの木にしか無い。
        // ビューアーに出さないと「multi_compile を書いたのに構文木に現れない」ことになり、
        // 解析が見落としているのか、見せていないだけなのかを区別できない。
        string shaderPath = Path.Combine(_workDirectory, "Assets", "Shaders", "Keyword.shader");

        await File.WriteAllTextAsync(shaderPath, """
            Shader "Company/Keyword"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma multi_compile _ SHADER_DEBUG

                        struct Attributes
                        {
                            float4 positionOS : POSITION;
                            #ifdef SHADER_DEBUG
                                float4 debugColor : COLOR;
                            #endif
                        };

                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                }
            }
            """);

        string path = Path.Combine(_workDirectory, "keyword.html");

        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [shaderPath, "--no-config", "--inspect", path],
            BuiltInAnalyzers.All,
            output,
            error);

        Assert.Equal(ExitCode.Success, code);

        string html = await File.ReadAllTextAsync(path);

        // どのシンボルを有効にした木かが分かること。
        Assert.Contains("SHADER_DEBUG", html, StringComparison.Ordinal);

        // 構文木のノードとして出ていること。ソースの引用に含まれるだけでは足りない。
        using JsonDocument document = JsonDocument.Parse(ExtractData(html));

        bool found = false;

        foreach (JsonElement program in document.RootElement.GetProperty("programs").EnumerateArray())
        {
            foreach (JsonElement declaration in program.GetProperty("declarations").EnumerateArray())
            {
                if (ContainsNodeText(declaration, "debugColor")) { found = true; }
            }
        }

        Assert.True(found, "debugColor が構文木のノードとして出ていない");
    }

    [Fact]
    public async Task 定義ごとに複製した文にはそれぞれの条件を書き出す()
    {
        // 条件で中身が変わるマクロを使う文は、定義ごとに複製されて同じ位置に並ぶ。
        // 条件が無いと、同じ行が 2 回出る理由も、どちらがどの構成のものかも読み取れない。
        string shaderPath = Path.Combine(_workDirectory, "Assets", "Shaders", "Hoisted.shader");
        await File.WriteAllTextAsync(shaderPath, """
            Shader "Company/Hoisted"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma multi_compile _ _A
                        #ifdef _A
                        #define CTYPE float3
                        #else
                        #define CTYPE float4
                        #endif

                        half4 frag() : SV_Target
                        {
                            CTYPE d = 1.0;
                            return d.x;
                        }
                        ENDHLSL
                    }
                }
            }
            """);

        string path = Path.Combine(_workDirectory, "hoisted.html");

        ExitCode code = await Program.RunAsync(
            [shaderPath, "--no-config", "--inspect", path],
            BuiltInAnalyzers.All,
            new StringWriter(),
            new StringWriter());

        Assert.Equal(ExitCode.Success, code);

        using JsonDocument document = JsonDocument.Parse(ExtractData(await File.ReadAllTextAsync(path)));

        List<(string Text, string Condition)> conditioned = [];

        foreach (JsonElement program in document.RootElement.GetProperty("programs").EnumerateArray())
        {
            foreach (JsonElement declaration in program.GetProperty("declarations").EnumerateArray())
            {
                CollectConditions(declaration, conditioned);
            }
        }

        Assert.Contains(conditioned, c => c.Text.StartsWith("float3 d", StringComparison.Ordinal) && c.Condition == "_A");
        Assert.Contains(conditioned, c => c.Text.StartsWith("float4 d", StringComparison.Ordinal) && c.Condition == "!_A");
    }

    [Fact]
    public async Task シンボルごとに並べたか構成ごとに展開したかと原因の指令を書き出す()
    {
        // 構成ごとの展開は解析を遅くし、上限に届けば SL0003 になる。
        // 書き方を変えれば避けられることが多いが、原因の #if が見えなければ直しようがない。
        string shaderPath = Path.Combine(_workDirectory, "Assets", "Shaders", "Symbols.shader");
        await File.WriteAllTextAsync(shaderPath, """
            Shader "Company/Symbols"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma multi_compile _ _SPLIT
                        #pragma multi_compile _ _CLOSED
                        #pragma multi_compile _ _UNUSED
                        half4 frag() : SV_Target
                        {
                            half m = 0;
                        #ifdef _SPLIT
                            if (m < 1)
                        #else
                            if (m < 2)
                        #endif
                            { m = 1; }
                        #ifdef _CLOSED
                            m += 1;
                        #endif
                            return m;
                        }
                        ENDHLSL
                    }
                }
            }
            """);

        string path = Path.Combine(_workDirectory, "symbols.html");

        ExitCode code = await Program.RunAsync(
            [shaderPath, "--no-config", "--inspect", path],
            BuiltInAnalyzers.All,
            new StringWriter(),
            new StringWriter());

        Assert.Equal(ExitCode.Success, code);

        using JsonDocument document = JsonDocument.Parse(ExtractData(await File.ReadAllTextAsync(path)));
        JsonElement program = document.RootElement.GetProperty("programs")[0];

        Dictionary<string, JsonElement> symbols = program.GetProperty("symbols").EnumerateArray()
            .ToDictionary(s => s.GetProperty("name").GetString()!, s => s);

        Assert.Equal("variant", symbols["_SPLIT"].GetProperty("state").GetString());
        Assert.Equal("merged", symbols["_CLOSED"].GetProperty("state").GetString());
        Assert.Equal("unused", symbols["_UNUSED"].GetProperty("state").GetString());

        // 原因の #ifdef を、このファイルの行で指す (1 始まりで 14 行目)。
        JsonElement reason = Assert.Single(symbols["_SPLIT"].GetProperty("reasons").EnumerateArray());
        Assert.StartsWith("14:", reason.GetProperty("where").GetString(), StringComparison.Ordinal);
        Assert.Contains("閉じていない", reason.GetProperty("label").GetString(), StringComparison.Ordinal);

        Assert.Equal(["_SPLIT"], program.GetProperty("configurations").EnumerateArray().Select(c => c.GetString()));
    }

    /// <summary>条件が書き出されたノードを集める。</summary>
    /// <param name="node">調べる木。</param>
    /// <param name="found">集めた先。</param>
    private static void CollectConditions(JsonElement node, List<(string Text, string Condition)> found)
    {
        if (node.TryGetProperty("condition", out JsonElement condition))
        {
            found.Add((node.GetProperty("text").GetString() ?? string.Empty, condition.GetString() ?? string.Empty));
        }

        if (node.TryGetProperty("children", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                CollectConditions(child, found);
            }
        }
    }

    /// <summary>木のどこかに、その語を含むノードがあるかを調べる。</summary>
    /// <param name="node">調べる木。</param>
    /// <param name="text">探す語。</param>
    /// <returns>見つかれば <see langword="true"/>。</returns>
    private static bool ContainsNodeText(JsonElement node, string text)
    {
        if (node.TryGetProperty("text", out JsonElement own)
            && own.GetString() is { } value
            && value.Contains(text, StringComparison.Ordinal))
        {
            return true;
        }

        if (!node.TryGetProperty("children", out JsonElement children))
        {
            return false;
        }

        foreach (JsonElement child in children.EnumerateArray())
        {
            if (ContainsNodeText(child, text)) { return true; }
        }

        return false;
    }

    [Fact]
    public async Task 解析の中身をHTMLとして書き出せる()
    {
        // 誤検出の原因が構文解析・マクロ展開・型判定のどれかを切り分けるための出力である。
        string path = Path.Combine(_workDirectory, "inspect.html");
        string shaderPath = Path.Combine(_workDirectory, "Assets", "Shaders", "Broken.shader");

        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [shaderPath, "--no-config", "--inspect", path],
            BuiltInAnalyzers.All,
            output,
            error);

        Assert.Equal(ExitCode.Success, code);

        string html = await File.ReadAllTextAsync(path);

        // 外部のファイルを参照すると、調査結果をそのまま添付して共有できなくなる。
        Assert.DoesNotContain("<script src", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link rel=\"stylesheet\"", html, StringComparison.Ordinal);

        Assert.Contains("Company/Broken", html, StringComparison.Ordinal);
        Assert.Contains("SL1021", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 式の型がどう判定されたかを書き出す()
    {
        // ルールが何も報告しないとき、原因のほとんどは
        // 「その式の型を判定できなかった」ことである。
        // 構文木とトークンだけを見せても、そこには辿り着けない。
        string shaderPath = Path.Combine(_workDirectory, "Assets", "Shaders", "Typed.shader");
        await File.WriteAllTextAsync(shaderPath, """
            Shader "Company/Typed"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        half4 frag(float2 uv : TEXCOORD0) : SV_Target
                        {
                            return (half4)uv.x;
                        }
                        ENDHLSL
                    }
                }
            }
            """);

        string path = Path.Combine(_workDirectory, "typed.html");

        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [shaderPath, "--no-config", "--inspect", path],
            BuiltInAnalyzers.All,
            output,
            error);

        Assert.Equal(ExitCode.Success, code);

        using JsonDocument document = JsonDocument.Parse(ExtractData(await File.ReadAllTextAsync(path)));
        JsonElement expressions = document.RootElement.GetProperty("expressions");

        // uv は float2 の仮引数なので uv.x は float、それを half4 へ変換している。
        Assert.Contains(
            expressions.EnumerateArray(),
            e => e.GetProperty("text").GetString() == "uv.x" && e.GetProperty("type").GetString() == "float");

        Assert.Contains(
            expressions.EnumerateArray(),
            e => e.GetProperty("type").GetString() == "half4");
    }

    /// <summary>ビューアーの HTML から、埋め込まれた JSON を取り出す。</summary>
    /// <param name="html">ビューアーの HTML。</param>
    /// <returns>JSON 文字列。</returns>
    /// <remarks>
    /// <b>改行の種類を当てにしない。</b>
    /// 雛形は C# の生文字列リテラルであり、改行はソースファイルのものがそのまま入る。
    /// <c>";\n"</c> を探すと、チェックアウトの改行設定しだいで見つからなくなる。
    /// JSON は 1 行に収まっているので、この行の終わりが値の終わりである。
    /// </remarks>
    private static string ExtractData(string html)
    {
        const string marker = "const DATA = ";
        int start = html.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, "ビューアーの HTML に JSON の埋め込み先が無い");

        start += marker.Length;
        int end = html.AsSpan(start).IndexOfAny('\r', '\n');

        Assert.True(end >= 0, "JSON の行が終わっていない");

        return html[start..(start + end)].TrimEnd(';');
    }

    [Fact]
    public async Task 中身を見る対象が1ファイルでなければ断る()
    {
        // 複数を 1 つの画面に混ぜても、目的の箇所を探しにくくなるだけである。
        await File.WriteAllTextAsync(
            Path.Combine(_workDirectory, "Assets", "Shaders", "Second.shader"),
            "Shader \"Company/Second\" { SubShader { Pass { } } }\n");

        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [_workDirectory, "--no-config", "--inspect", Path.Combine(_workDirectory, "x.html")],
            BuiltInAnalyzers.All,
            output,
            error);

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains("1 つのシェーダー", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub形式に対しては注釈を重ねて出さない()
    {
        string output = await RunAsync("--format", "github", "--annotate");
        Assert.Single(output.Split("::warning ", StringSplitOptions.None)[1..]);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("sarif")]
    [InlineData("github")]
    public async Task どの形式でも指摘があれば終了コード1を返す(string format)
    {
        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [_workDirectory, "--no-config", "--format", format],
            BuiltInAnalyzers.All,
            output,
            error);

        Assert.Equal(ExitCode.DiagnosticsFound, code);
    }
}
