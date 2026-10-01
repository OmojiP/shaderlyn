using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Shaderlyn.Cli;
using Shaderlyn.Cookbook;
using Shaderlyn.LanguageServer;
using Shaderlyn.LanguageServer.Protocol;

namespace Shaderlyn.Tests;

/// <summary>
/// エディタに常駐して診断を返すサーバの検証。
/// </summary>
/// <remarks>
/// <para>
/// <b>やり取りの枠が壊れると、以降のすべてが読めなくなる。</b>
/// <c>Content-Length</c> の数え方を 1 バイト誤るだけで、
/// エディタ側は何の知らせもなく表示をやめる。ここは実際に往復させて確かめる。
/// </para>
/// <para>
/// 解析そのものの正しさは他のテストが見ている。
/// ここで見るのは、要求を受けて応答が返り、指摘が正しい位置で届くことである。
/// </para>
/// </remarks>
public sealed class LanguageServerTests
{
    /// <summary>応答を待つ上限。</summary>
    /// <remarks>解析が終わらない不具合をテストの停止で表すため、無限には待たない。</remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string BrokenShader = """
        Shader "Company/Broken"
        {
            SubShader
            {
                Pass
                {
                    Cull Sideways
                }
            }
        }
        """;

    [Fact]
    public async Task 開いたスクリプトの指摘を返す()
    {
        await using LanguageServerHarness harness = new();

        await harness.SendAsync(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
        JsonDocument initialize = await harness.ReceiveAsync();

        // 全文で同期する (1 = Full) と申告する。差分で受け取っても解析は毎回全文を見る。
        Assert.Equal(
            1,
            initialize.RootElement.GetProperty("result").GetProperty("capabilities")
                .GetProperty("textDocumentSync").GetInt32());

        await harness.OpenAsync(BrokenShader);
        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        JsonElement first = diagnostics[0];
        Assert.Equal("SL1021", first.GetProperty("code").GetString());

        // LSP の行と桁は 0 始まり。Cull Sideways は 7 行目にある。
        Assert.Equal(6, first.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        // ルールの説明へ辿れること。理由が読めない指摘は理解されないまま抑制される。
        Assert.Contains(
            "SL1021",
            first.GetProperty("codeDescription").GetProperty("href").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 編集のたびにShaderLab単体の指摘を返す()
    {
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(BrokenShader);
        await harness.ReceiveDiagnosticsAsync();

        await harness.ChangeAsync(BrokenShader.Replace("Sideways", "Back", StringComparison.Ordinal));
        JsonElement afterFix = await harness.ReceiveDiagnosticsAsync();

        // 直した指摘は消える。空で送ることが「指摘が無い」を意味する。
        Assert.Equal(0, afterFix.GetArrayLength());
    }

    /// <summary>
    /// 自作ルールを足したサーバが、そのルールの指摘を返すことを検証する。
    /// </summary>
    /// <remarks>
    /// <b>エディタと CLI で同じルールが働くことを保つための検証である。</b>
    /// サーバが組み込みルールを固定していると、CI では落ちるのに編集中は何も出ない状態になる。
    /// 利用者から見れば「ルールが効いていない」としか見えない。
    /// </remarks>
    [Fact]
    public async Task 自作ルールを足したサーバがその指摘を返す()
    {
        await using LanguageServerHarness harness = new(
            analyzers: [.. BuiltInAnalyzers.All, new PropertyNamePrefixAnalyzer("_")]);

        await harness.InitializeAsync();
        await harness.OpenAsync("""
            Shader "Company/Prefix"
            {
                Properties
                {
                    BaseColor ("Base Color", Color) = (1,1,1,1)
                }
            }
            """);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        Assert.Contains(
            diagnostics.EnumerateArray(),
            d => d.GetProperty("code").GetString() == "COOK0008");
    }

    /// <summary>HLSL の断片を単体で開いたときの URI。</summary>
    private static string HlslDocumentUri { get; } =
        DocumentUri.FromFilePath(Path.Combine(Path.GetTempPath(), "shaderlyn-lsp-test", "Common.hlsl"));

    /// <summary>シンボルの条件の中にだけ誤りを置いた HLSL の断片。</summary>
    private const string SymbolFragment = """
        #ifdef _FOO
        float3 InFoo(float2 uv) { return uv; }
        #endif

        float4 Always() { return 0; }
        """;

    /// <summary>
    /// HLSL の断片を単体で開いても解析し、断片では判断できない指摘を出さないことを検証する。
    /// </summary>
    /// <remarks>
    /// Unity では <c>.hlsl</c> だけを編集することも多い。
    /// ステージの <c>#pragma</c> やシンボルの宣言は取り込む側の <c>.shader</c> にあるので、
    /// 断片を開くたびにそれが「無い」と並ぶと、ほかの指摘が読めなくなる。
    /// </remarks>
    [Fact]
    public async Task HLSLの断片を単体で解析する()
    {
        await using LanguageServerHarness harness = new(documentUri: HlslDocumentUri);

        await harness.InitializeAsync();
        await harness.OpenAsync("""
            #ifdef _FOO
            float4 InFoo() { return 0; }
            #endif

            float3 Always(float2 uv) { return uv; }
            """);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsForAsync("HL0351");

        Assert.DoesNotContain(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "HL0302");
        Assert.DoesNotContain(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "HL0330");
    }

    /// <summary>
    /// 直しは、カーソルの下の指摘に対するものだけを返すことを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>抑制コメントの見出しにはルール ID しか出ない。</b>
    /// 同じ ID の指摘がスクリプトの別の場所にもあると、見出しが同じ候補が並び、
    /// どれがカーソルの行のものか区別できない。選び違えれば関係のない行が書き換わる。
    /// </para>
    /// <para>
    /// 1 行に同じ ID の指摘が複数あるときも、抑制コメントの差し込みは同じ編集になる。
    /// 重なった候補は 1 つにまとめる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task カーソルの下の指摘に対する直しだけを返す()
    {
        await using LanguageServerHarness harness = new(documentUri: HlslDocumentUri);

        await harness.InitializeAsync();

        // Missing1 と Missing2 は同じ行、Missing3 は別の行。いずれも HL0310 になる。
        await harness.OpenAsync("""
            float Frag()
            {
                float value = Missing1 + Missing2;
                return value + Missing3;
            }
            """);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsForAsync("HL0310");

        JsonElement target = diagnostics.EnumerateArray().Single(
            d => d.GetProperty("message").GetString()?.Contains("Missing3", StringComparison.Ordinal) == true);

        JsonElement actions = await harness.RequestCodeActionsAsync(target);

        // スクリプト全体の HL0310 をなぞると 3 件になる。返すのはカーソルの下の 1 件だけである。
        JsonElement action = Assert.Single(actions.EnumerateArray());
        Assert.Equal("この行で HL0310 を抑制する", action.GetProperty("title").GetString());

        // 差し込む先は Missing3 の行 (0 始まりで 3) の先頭である。
        JsonElement edit = action.GetProperty("edit").GetProperty("changes")
            .GetProperty(harness.Uri).EnumerateArray().Single();

        Assert.Equal(3, edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Contains("shaderlyn-disable-next-line HL0310", edit.GetProperty("newText").GetString());
    }

    /// <summary>
    /// 1 行に同じ ID の指摘が複数あっても、抑制の候補を 1 つにまとめることを検証する。
    /// </summary>
    /// <remarks>
    /// 範囲で絞っても、行を選んで直しを求めれば複数の指摘が同時に重なる。
    /// 同じ編集を並べても選びようがない。
    /// </remarks>
    [Fact]
    public async Task 同じ行の同じ指摘に対する抑制を1つにまとめる()
    {
        await using LanguageServerHarness harness = new(documentUri: HlslDocumentUri);

        await harness.InitializeAsync();
        await harness.OpenAsync("""
            float Frag()
            {
                return Missing1 + Missing2;
            }
            """);

        await harness.ReceiveDiagnosticsForAsync("HL0310");

        // 2 件の指摘をまたぐ範囲 (0 始まりで 2 行目の全体) を選んだ状態にあたる。
        JsonElement actions = await harness.RequestCodeActionsAsync(
            startLine: 2, startCharacter: 0, endLine: 2, endCharacter: 30, ruleId: "HL0310");

        JsonElement action = Assert.Single(actions.EnumerateArray());
        Assert.Equal("この行で HL0310 を抑制する", action.GetProperty("title").GetString());
    }

    /// <summary>
    /// 選んだシンボルを定義済みにして解析し直し、条件の中のコードも検査することを検証する。
    /// </summary>
    /// <remarks>
    /// <b>断片の中には、条件のシンボルを定義する場所が無い。</b>
    /// 取り込む側が定義するためである。定義しないまま解析すると、
    /// その分岐の中は一度も検査されない。
    /// </remarks>
    [Fact]
    public async Task 選んだシンボルを定義済みにして解析し直す()
    {
        await using LanguageServerHarness harness = new(documentUri: HlslDocumentUri);

        await harness.InitializeAsync();
        await harness.OpenAsync(SymbolFragment);

        // 定義が無いので、条件の中の誤りは検査されない。
        JsonElement before = await harness.ReceiveDiagnosticsAsync();
        Assert.DoesNotContain(before.EnumerateArray(), d => d.GetProperty("code").GetString() == "HL0351");
        Assert.Contains(("_FOO", false, false), await harness.RequestConditionSymbolsAsync());

        await harness.SetDefinedSymbolsAsync("_FOO");

        JsonElement after = await harness.ReceiveDiagnosticsForAsync("HL0351");
        Assert.Contains(after.EnumerateArray(), d => d.GetProperty("code").GetString() == "HL0351");

        // 選んだものは候補に残り、選ばれていることが分かる。残らないと外す手段が無い。
        Assert.Contains(("_FOO", true, false), await harness.RequestConditionSymbolsAsync());
    }

    /// <summary>
    /// このファイルが宣言したシンボルも候補に含め、定義の無いシンボルと見分けられることを検証する。
    /// </summary>
    /// <remarks>
    /// シンボルを選ぶ意味は「その構成で表示する」ことであり、定義を足して解析し直すこととは違う。
    /// エディタが分けて見せられるよう、サーバが種類と今の表示の仕方を返す。
    /// </remarks>
    [Fact]
    public async Task シンボルを候補に含めて見分けられるようにする()
    {
        await using LanguageServerHarness harness = new(documentUri: HlslDocumentUri);

        await harness.InitializeAsync();
        await harness.OpenAsync("""
            #pragma multi_compile _ _KEYWORD_ON

            #ifdef _KEYWORD_ON
            float4 SymbolOnly() { return 1; }
            #endif

            #ifdef _FOO
            float4 InFoo() { return 0; }
            #endif
            """);

        await harness.ReceiveDiagnosticsAsync();

        (string Name, bool Defined, bool Keyword)[] before = await harness.RequestConditionSymbolsAsync();
        Assert.Contains(("_KEYWORD_ON", false, true), before);
        Assert.Contains(("_FOO", false, false), before);
        Assert.Equal("allConfigurations", await harness.RequestConditionSymbolsDisplayAsync());

        await harness.SetSelectedConfigurationAsync("_KEYWORD_ON");

        Assert.Contains(("_KEYWORD_ON", true, true), await harness.RequestConditionSymbolsAsync());
        Assert.Equal("selectedConfiguration", await harness.RequestConditionSymbolsDisplayAsync());
    }

    /// <summary>
    /// 同じ <c>#pragma</c> の行で宣言されたシンボルが、まとまりとして分かることを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同じ行のシンボルは同時に有効にならず、<c>_</c> の無い行はどれか 1 つが必ず有効になる。
    /// エディタはこれを見て、実在しない構成を選ばせないようにする。
    /// </para>
    /// <para>
    /// 返さないと、<c>multi_compile _A _B</c> の両方を外した構成を選べてしまう。
    /// そんな構成は無く、解析は先頭のシンボルを補って別の構成を見ることになる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 同じ行で宣言したシンボルをまとまりとして返す()
    {
        await using LanguageServerHarness harness = new(documentUri: HlslDocumentUri);

        await harness.InitializeAsync();
        await harness.OpenAsync("""
            #pragma multi_compile _MODE_A _MODE_B
            #pragma multi_compile _ _EXTRA

            #if defined(_MODE_A) || defined(_MODE_B) || defined(_EXTRA)
            float4 Selected() { return 1; }
            #endif

            #ifdef _FOO
            float4 InFoo() { return 0; }
            #endif
            """);

        await harness.ReceiveDiagnosticsAsync();

        (string Name, int Group, bool RequiresOne)[] groups = await harness.RequestConditionSymbolGroupsAsync();

        (string Name, int Group, bool RequiresOne) modeA = Assert.Single(groups, s => s.Name == "_MODE_A");
        (string Name, int Group, bool RequiresOne) modeB = Assert.Single(groups, s => s.Name == "_MODE_B");
        (string Name, int Group, bool RequiresOne) extra = Assert.Single(groups, s => s.Name == "_EXTRA");
        (string Name, int Group, bool RequiresOne) foo = Assert.Single(groups, s => s.Name == "_FOO");

        // 同じ行の 2 つは同じまとまりで、どちらかが必ず有効になる。
        Assert.Equal(modeA.Group, modeB.Group);
        Assert.True(modeA.RequiresOne);
        Assert.True(modeB.RequiresOne);

        // `_` のある行は別のまとまりで、外したままにできる。
        Assert.NotEqual(modeA.Group, extra.Group);
        Assert.False(extra.RequiresOne);

        // 宣言されていないシンボルはどのまとまりにも入らない。
        Assert.Equal(-1, foo.Group);
    }

    [Fact]
    public async Task 閉じたスクリプトの指摘を消す()
    {
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(BrokenShader);
        await harness.ReceiveDiagnosticsAsync();

        await harness.SendAsync(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didClose",
            @params = new { textDocument = new { uri = LanguageServerHarness.DocumentUri } },
        });

        // 開いていないファイルの指摘が一覧に居座り続けてはならない。
        Assert.Equal(0, (await harness.ReceiveDiagnosticsAsync()).GetArrayLength());
    }

    [Fact]
    public async Task BOM付きのスクリプトを構文エラーにしない()
    {
        // ファイルから読む経路は StreamReader が BOM を取り除く。
        // エディタから受け取る経路で残すと、位置が 1 文字ずれて
        // 「先頭に Shader 以外の記述がある」という誤った指摘になる。
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync("\uFEFF" + BrokenShader);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        Assert.DoesNotContain(
            diagnostics.EnumerateArray(),
            d => d.GetProperty("code").GetString() == "SL0001");
    }

    /// <summary>型を辿れる式と辿れない式を並べたシェーダー。</summary>
    private const string TypedShader = """
        Shader "Company/Typed"
        {
            Properties { _Amount ("Amount", Float) = 1 }
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    float _Amount;
                    half4 frag(float2 uv : TEXCOORD0) : SV_Target
                    {
                        return (half4)(uv.x * _Amount);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    /// <summary>条件付きの宣言を持つシェーダー。</summary>
    private const string ConditionalShader = """
        Shader "Company/Conditional"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma multi_compile _ SHADER_DEBUG
                    #ifdef SHADER_DEBUG
                    float _DebugAmount;
                    #endif
                    float _Amount;
                    half4 frag() : SV_Target { return _Amount; }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public async Task 条件付きの宣言にはその条件を添える()
    {
        // #ifdef で囲まれた宣言は 1 本の木の中にある。
        // 何も断らないと、無条件の宣言と区別が付かない。
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(ConditionalShader);
        await harness.ReceiveDiagnosticsAsync();

        string markdown = await harness.HoverAsync(ConditionalShader, "_DebugAmount");

        Assert.Contains("SHADER_DEBUG", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 無条件の宣言には条件を添えない()
    {
        // 断りを付けると、条件付きであるかのように読める。読み手には雑音でしかない。
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(ConditionalShader);
        await harness.ReceiveDiagnosticsAsync();

        string markdown = await harness.HoverAsync(ConditionalShader, "_Amount;");

        Assert.DoesNotContain("この構成でだけ存在します", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task カーソルの下の式の型を答える()
    {
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(TypedShader);
        await harness.ReceiveDiagnosticsAsync();

        // 12 行目の uv.x。uv は float2 の仮引数なので float。
        string markdown = await harness.HoverAsync(TypedShader, "uv.x");

        Assert.Contains("uv.x", markdown, StringComparison.Ordinal);
        Assert.Contains("float", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// 条件ごとに違う型で宣言した名前について、構成ごとの型を答えることを検証する。
    /// </summary>
    /// <remarks>
    /// <b>1 本の木で型が決まらないことは、型が分からないことではない。</b>
    /// 診断は構成ごとに型を求めて判断している。
    /// ホバーだけが「判定できません」と答えると、
    /// 指摘が出ない理由を調べる利用者を誤った方向へ導く。
    /// </remarks>
    [Fact]
    public async Task 条件ごとに型が変わる名前は構成ごとの型を答える()
    {
        await using LanguageServerHarness harness = new(documentUri: HlslDocumentUri);

        await harness.InitializeAsync();
        await harness.OpenAsync(VaryingTypeFragment);
        await harness.ReceiveDiagnosticsAsync();

        // a は _A のとき float、_B のとき float3。目印の中心が a に来るように空白を含める。
        string varying = await harness.HoverAsync(VaryingTypeFragment, " a;");

        Assert.Contains("型は構成によって変わります", varying, StringComparison.Ordinal);
        Assert.Contains("`_A` のとき **`float`**", varying, StringComparison.Ordinal);

        // #elif は「前の条件が成り立たず、かつ自分の条件が成り立つ」である。そのまま出す。
        Assert.Contains("`!_A && _B` のとき **`float3`**", varying, StringComparison.Ordinal);
        Assert.DoesNotContain("型を判定できません", varying, StringComparison.Ordinal);

        // a.x は float でも float3 でも float。構成を並べる必要は無い。
        string swizzle = await harness.HoverAsync(VaryingTypeFragment, "a.x");

        Assert.Contains("型: **`float`**", swizzle, StringComparison.Ordinal);
        Assert.DoesNotContain("型を判定できません", swizzle, StringComparison.Ordinal);
        Assert.DoesNotContain("型は構成によって変わります", swizzle, StringComparison.Ordinal);
    }

    /// <summary>同じ名前を条件ごとに違う型で宣言した HLSL の断片。</summary>
    private const string VaryingTypeFragment = """
        float ReturnFloat()
        {
            #pragma multi_compile _A _B

            #if defined(_A)
            float a = 1.0;
            #elif defined(_B)
            float3 a = float3(1.0, 2.0, 3.0);
            #endif

            float taken = a.x;
            return a;
        }
        """;

    [Theory]
    [InlineData("half4", "ベクトル")]           // 型名。式ではない
    [InlineData("Varyings", "struct")]          // 構造体の宣言
    [InlineData("_Amount", "uniform")]          // 変数の宣言
    [InlineData("frag", "関数")]                // 関数の宣言
    [InlineData("texcoord", "仮引数")]          // 仮引数の宣言
    public async Task 宣言と型名にも答える(string target, string expected)
    {
        // 「ホバーしても何も出ない」語が多いと、拡張が動いていないように見える。
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(DeclarationShader);
        await harness.ReceiveDiagnosticsAsync();

        string markdown = await harness.HoverAsync(DeclarationShader, target);

        Assert.Contains(expected, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 型名を型が不明な式として扱わない()
    {
        // half4 は型であって、型が不明な式ではない。
        // 型名の上で「型を判定できません」と答えると、
        // 解析が壊れているという印象だけを与える。
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(DeclarationShader);
        await harness.ReceiveDiagnosticsAsync();

        string markdown = await harness.HoverAsync(DeclarationShader, "half4");

        Assert.DoesNotContain("判定できません", markdown, StringComparison.Ordinal);
    }

    /// <summary>宣言をひととおり並べたシェーダー。</summary>
    private const string DeclarationShader = """
        Shader "Company/Declarations"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    struct Varyings { float2 uv; };
                    float _Amount;

                    half4 frag(float2 texcoord : TEXCOORD0) : SV_Target
                    {
                        return (half4)(texcoord.x * _Amount);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public async Task レンダーステートに指定できる値を答える()
    {
        // Unity は不正な値をエラーにせず既定値へ倒す。
        // 指定できる値をその場で見られると、スペルミスに気づける。
        const string source = """
            Shader "Company/State"
            {
                SubShader
                {
                    Pass { Cull Off }
                }
            }
            """;

        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(source);
        await harness.ReceiveDiagnosticsAsync();

        string markdown = await harness.HoverAsync(source, "Cull");

        Assert.Contains("Back, Front, Off", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 型を判定できないことも答える()
    {
        // 指摘が出ないという結果からは、「問題が無い」のか
        // 「判断できていない」のか区別が付かない。そこを見えるようにするのが要点である。
        //
        // 宣言の無い関数の戻り値は、規則で型を決めようがない。
        // 数値リテラルの型は HlslLiteral が答えるので、ここでは使えない。
        string source = TypedShader.Replace(
            "uv.x * _Amount", "Untyped() * Untyped()", StringComparison.Ordinal);

        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(source);
        await harness.ReceiveDiagnosticsAsync();

        // 演算子の上を指す。両辺の型が分からないので、積の型も分からない。
        string markdown = await harness.HoverAsync(source, "Untyped() * Untyped()");

        Assert.Contains("判定できません", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task プロパティの上ではHLSL側の宣言を答える()
    {
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(TypedShader);
        await harness.ReceiveDiagnosticsAsync();

        string markdown = await harness.HoverAsync(TypedShader, "_Amount");

        Assert.Contains("_Amount", markdown, StringComparison.Ordinal);
        Assert.Contains("float", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 答えるものが無くても応答は返す()
    {
        // 要求に id がある以上、空でも返さなければエディタは待ち続ける。
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(TypedShader);
        await harness.ReceiveDiagnosticsAsync();

        // Shader 宣言の行。式もプロパティも無い。
        JsonDocument response = await harness.RequestHoverAsync(0, 3);
        Assert.True(response.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task 処理が例外で終わったら出力へ記録して応答は返す()
    {
        // 常駐は続けるが、何も伝えずに捨てると利用者には「ホバーが出ない」としか見えない。
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(TypedShader);
        await harness.ReceiveDiagnosticsAsync();

        // 行番号に文字列を渡すと、位置を読むところで例外になる。
        await harness.SendAsync(new
        {
            jsonrpc = "2.0",
            id = 101,
            method = "textDocument/hover",
            @params = new
            {
                textDocument = new { uri = harness.Uri },
                position = new { line = "x", character = 0 },
            },
        });

        bool logged = false;
        bool responded = false;

        while (!responded)
        {
            using JsonDocument message = await harness.ReceiveAsync();
            JsonElement root = message.RootElement;

            if (root.TryGetProperty("method", out JsonElement method) && method.GetString() == "window/logMessage")
            {
                JsonElement parameters = root.GetProperty("params");
                Assert.Equal(1, parameters.GetProperty("type").GetInt32());
                Assert.Contains("ホバーの組み立て", parameters.GetProperty("message").GetString(), StringComparison.Ordinal);
                logged = true;
            }
            else if (root.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 101)
            {
                Assert.True(root.TryGetProperty("result", out _));
                responded = true;
            }
        }

        Assert.True(logged, "応答より前に記録が届いていない");
    }

    [Fact]
    public async Task スペルミスを直す操作を提示する()
    {
        // 正しい綴りを知っているのはルールの側だけである。
        // メッセージの文面から読み取らせると、文面を変えるたびに直しが壊れる。
        const string source = """
            Shader "Company/Tag"
            {
                SubShader
                {
                    Tags { "RenderType" = "Opaqu" }
                    Pass { }
                }
            }
            """;

        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(source);
        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        JsonElement misspelling = diagnostics.EnumerateArray()
            .First(d => d.GetProperty("code").GetString() == "SL1012");

        JsonElement actions = await harness.RequestCodeActionsAsync(misspelling);
        JsonElement fix = actions[0];

        Assert.Contains("Opaque", fix.GetProperty("title").GetString()!, StringComparison.Ordinal);
        Assert.True(fix.GetProperty("isPreferred").GetBoolean());

        // 引用符を含む範囲を指しているので、引用符ごと置き換えられなければならない。
        JsonElement edit = fix.GetProperty("edit").GetProperty("changes")
            .GetProperty(LanguageServerHarness.DocumentUri)[0];

        Assert.Equal("\"Opaque\"", edit.GetProperty("newText").GetString());
    }

    [Fact]
    public async Task 抑制コメントを差し込む操作を提示する()
    {
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(BrokenShader);
        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        JsonElement actions = await harness.RequestCodeActionsAsync(diagnostics[0]);

        JsonElement suppression = actions.EnumerateArray()
            .First(a => a.GetProperty("title").GetString()!.Contains("抑制", StringComparison.Ordinal));

        string newText = suppression.GetProperty("edit").GetProperty("changes")
            .GetProperty(LanguageServerHarness.DocumentUri)[0]
            .GetProperty("newText").GetString()!;

        // ID を省くとその行のすべての指摘が抑制される。まだ見ぬ指摘まで巻き添えにしてはならない。
        Assert.Contains("shaderlyn-disable-next-line SL1021", newText, StringComparison.Ordinal);

        // 字下げを合わせる。崩れたコメントは機械が書いたものとして軽く扱われる。
        Assert.StartsWith("            ", newText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解析の中身をHTMLで返す()
    {
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(BrokenShader);
        await harness.ReceiveDiagnosticsAsync();

        string html = await harness.RequestInspectAsync();

        Assert.Contains("Company/Broken", html, StringComparison.Ordinal);

        // 外部を参照しない 1 枚の HTML であること。
        Assert.DoesNotContain("<script src", html, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // ホバー: 答えるものが無い場所では何も返さない
    // ------------------------------------------------------------------

    /// <summary>ホバーと定義の検証に使うシェーダー。</summary>
    private const string NavigableShader = """
        Shader "Company/Navigable"
        {
            Properties { _BaseColor ("Base", Color) = (1,1,1,1) }
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag

                    float4 _BaseColor;

                    float4 Helper(float2 uv, half scale)
                    {
                        float2 scaled = uv * scale;
                        return float4(scaled, 0, 1);
                    }

                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

                    half4 frag(float2 uv : TEXCOORD0) : SV_Target
                    {
                        // 説明することが何も無い行
                        float4 v = Helper(uv, 2);
                        return (half4)(v * _BaseColor);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    [Theory]
    [InlineData("// 説明することが何も無い行")]
    [InlineData("return p; }")]
    public async Task 説明することが無い場所では何も返さない(string target)
    {
        // コードブロックのトークンは中身を丸ごと含んでいる。
        // 範囲に入っているかだけで判定すると、
        // コメントや return の上でも「HLSLPROGRAM」の説明が出ることになる。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(NavigableShader);
        await harness.ReceiveDiagnosticsAsync();

        Assert.Equal(string.Empty, await harness.HoverAsync(NavigableShader, target));
    }

    [Fact]
    public async Task コードブロックの開始シンボルは説明する()
    {
        // ブロック自身を指したときは、そのブロックの話をしてよい。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(NavigableShader);
        await harness.ReceiveDiagnosticsAsync();

        Assert.Contains(
            "HLSLPROGRAM",
            await harness.HoverAsync(NavigableShader, "HLSLPROGRAM"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task セマンティクスの意味を説明する()
    {
        // SV_Target と SV_POSITION は綴りが似ているだけで役割がまったく違う。
        // 取り違えても Unity はエラーを出さない。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(NavigableShader);
        await harness.ReceiveDiagnosticsAsync();

        string hover = await harness.HoverAsync(NavigableShader, "SV_Target");

        Assert.Contains("SV_Target", hover, StringComparison.Ordinal);
        Assert.Contains("出力先", hover, StringComparison.Ordinal);
    }

    /// <summary><c>#pragma</c> の説明の検証に使うシェーダー。</summary>
    private const string PragmaShader = """
        Shader "Company/Pragmas"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex VertMain
                    #pragma fragment FragMain
                    #pragma multi_compile_fog
                    #pragma shader_feature_local_fragment _ _USE_NOISE
                    #pragma future_pragma_name

                    float4 VertMain(float4 p : POSITION) : SV_POSITION { return p; }
                    half4 FragMain() : SV_Target { return 0; }
                    ENDHLSL
                }
            }
        }
        """;

    [Theory]
    [InlineData("vertex", "頂点シェーダーのエントリポイントを指定する")]
    [InlineData("multi_compile_fog", "霧のモード")]
    [InlineData("shader_feature_local_fragment", "実行時にスクリプトから有効にしても効かない")]
    [InlineData("shader_feature_local_fragment", "`_local`")]
    [InlineData("shader_feature_local_fragment", "フラグメントシェーダーにだけ作られ")]
    public async Task pragmaの名前の意味を説明する(string target, string expected)
    {
        // shader_feature と multi_compile は並べて書かれるが、ビルドでの扱いがまったく違う。
        // 取り違えても Unity はエラーを出さない。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(PragmaShader);
        await harness.ReceiveDiagnosticsAsync();

        string hover = await harness.HoverAsync(PragmaShader, target);

        Assert.Contains($"#pragma {target}", hover, StringComparison.Ordinal);
        Assert.Contains(expected, hover, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VertMain", "頂点シェーダーのエントリポイントとして指定された関数")]
    [InlineData("_USE_NOISE", "シェーダーのシンボル")]
    [InlineData(" _ ", "プレースホルダー")]
    public async Task pragmaの引数の役割を説明する(string target, string expected)
    {
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(PragmaShader);
        await harness.ReceiveDiagnosticsAsync();

        Assert.Contains(expected, await harness.HoverAsync(PragmaShader, target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 知らないpragmaを間違いとは言わない()
    {
        // #pragma は Unity のバージョンごとに増える。知らないことは知らないと言うにとどめる。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(PragmaShader);
        await harness.ReceiveDiagnosticsAsync();

        string hover = await harness.HoverAsync(PragmaShader, "future_pragma_name");

        Assert.Contains("説明を持たない", hover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 呼び出しの説明に仮引数を並べる()
    {
        // 「引数 2 個」では、何を渡せばよいのかが分からない。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(NavigableShader);
        await harness.ReceiveDiagnosticsAsync();

        string hover = await harness.HoverAsync(NavigableShader, "Helper(");

        Assert.Contains("float4 Helper(float2 uv, half scale)", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("引数 2 個", hover, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 定義へ飛ぶ
    // ------------------------------------------------------------------

    [Fact]
    public async Task このファイルの関数の宣言へ飛べる()
    {
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(NavigableShader);
        await harness.ReceiveDiagnosticsAsync();

        JsonElement targets = await harness.DefinitionAsync(NavigableShader, "Helper(");

        JsonElement first = Assert.Single(targets.EnumerateArray());
        Assert.Equal(LanguageServerHarness.DocumentUri, first.GetProperty("uri").GetString());
        Assert.Equal(13, first.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task pragmaに書いた入口の関数へ飛べる()
    {
        // #pragma vertex vert の vert は、この関数を指している。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(NavigableShader);
        await harness.ReceiveDiagnosticsAsync();

        // 改行をまたぐ目印は、チェックアウトの改行設定しだいで見つからなくなる。
        // "#pragma vertex vert" の 2 つ目の vert を、行の中だけで指す。
        JsonElement targets = await harness.DefinitionAsync(NavigableShader, "x vert");

        JsonElement first = Assert.Single(targets.EnumerateArray());
        Assert.Equal(19, first.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task 実在しないファイルを定義として返さない()
    {
        // 定義済みマクロの出所は <定義済みマクロ> であって、ファイル名ではない。
        // そのまま返すと、エディタは「ファイルが見つかりませんでした」と書いたタブを開く。
        // 飛べない先を返すくらいなら、何も返さないほうがよい。
        string source = NavigableShader.Replace(
            "float4 _BaseColor;",
            "float4 _BaseColor;\n                    static int api = SHADER_API_D3D11;",
            StringComparison.Ordinal);

        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(source);
        await harness.ReceiveDiagnosticsAsync();

        JsonElement targets = await harness.DefinitionAsync(source, "SHADER_API_D3D11;");

        Assert.Empty(targets.EnumerateArray());
    }

    [Fact]
    public async Task includeの取り込み先へ飛べる()
    {
        // 書かれたパスへは飛べない。実際に読んだファイルを返す必要がある。
        string header = await WriteHeaderBesideDocumentAsync("Included.hlsl", "#define INCLUDED_MARKER 1\n");

        string source = NavigableShader.Replace(
            "#pragma fragment frag",
            "#pragma fragment frag\n                    #include \"Included.hlsl\"",
            StringComparison.Ordinal);

        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(source);
        await harness.ReceiveDiagnosticsAsync();

        JsonElement targets = await harness.DefinitionAsync(source, "\"Included.hlsl\"");

        JsonElement first = Assert.Single(targets.EnumerateArray());
        Assert.Equal(DocumentUri.FromFilePath(header), first.GetProperty("uri").GetString());
    }

    [Fact]
    public async Task 解決できないincludeをその行で報告する()
    {
        // SL0002 は「解決できなかった」ことをファイルの先頭で伝えるが、
        // どの行のパスが誤っているかは伝えない。
        await WriteHeaderBesideDocumentAsync("Included.hlsl", "#define M 1\n");

        string source = NavigableShader.Replace(
            "#pragma fragment frag",
            "#pragma fragment frag\n                    #include \"Included.hlsl\""
            + "\n                    #include \"Typoed.hlsl\"",
            StringComparison.Ordinal);

        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(source);

        JsonElement reported = Assert.Single(
            (await harness.ReceiveDiagnosticsForAsync("HL0321")).EnumerateArray(),
            d => d.GetProperty("code").GetString() == "HL0321");

        Assert.Contains("Typoed.hlsl", reported.GetProperty("message").GetString()!, StringComparison.Ordinal);

        // スペルミスは 1 行に決まる。その行を指していなければ直しようがない。
        Assert.Equal(11, reported.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    /// <summary>開いているスクリプトの隣にヘッダを置く。</summary>
    /// <param name="fileName">ヘッダのファイル名。</param>
    /// <param name="content">ヘッダの中身。</param>
    /// <returns>置いたファイルのパス。</returns>
    /// <remarks>
    /// <c>#include</c> はまず、書いているファイルからの相対で解決される。
    /// 実際にファイルを置かないと、解決も、飛び先の検証もできない。
    /// </remarks>
    private static async Task<string> WriteHeaderBesideDocumentAsync(string fileName, string content)
    {
        string directory = Path.GetDirectoryName(
            DocumentUri.ToFilePath(LanguageServerHarness.DocumentUri))!;

        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, content);

        return path;
    }


    [WindowsOnlyFact]
    public async Task 受け取ったURIの書き方のまま定義を返す()
    {
        // VS Code は Windows のドライブ文字のコロンを符号化して送ってくる。
        // パスから組み立て直すと符号化されていない形になり、
        // エディタはそれを別のスクリプトとして扱う。
        // 同じファイルなのに新しいタブが開き、
        // 「ファイルが見つからなかったため、エディターを開くことができませんでした」と出る。
        string encoded = LanguageServerHarness.DocumentUri
            .Replace("file:///C:", "file:///C%3A", StringComparison.OrdinalIgnoreCase);

        Assert.Contains("%3A", encoded, StringComparison.Ordinal);

        await using LanguageServerHarness harness = new(encoded);
        await harness.InitializeAsync();
        await harness.OpenAsync(NavigableShader);
        await harness.ReceiveDiagnosticsAsync();

        JsonElement targets = await harness.DefinitionAsync(NavigableShader, "Helper(");

        JsonElement first = Assert.Single(targets.EnumerateArray());

        // 受け取ったものと 1 文字も違わないこと。
        Assert.Equal(encoded, first.GetProperty("uri").GetString());
    }

    [Fact]
    public async Task 符号化されたURIでもファイルの中身を見失わない()
    {
        // ドライブ文字の解釈を誤ると、解析対象のパスが C:\c:\... になる。
        // 開いているスクリプトのパスと一致しなくなり、
        // 「このファイルに書かれたものだけを見る」判定がすべて外れる。
        string encoded = LanguageServerHarness.DocumentUri
            .Replace("file:///C:", "file:///C%3A", StringComparison.OrdinalIgnoreCase);

        await using LanguageServerHarness harness = new(encoded);
        await harness.InitializeAsync();
        await harness.OpenAsync(NavigableShader);
        await harness.ReceiveDiagnosticsAsync();

        Assert.Contains(
            "float4 Helper(float2 uv, half scale)",
            await harness.HoverAsync(NavigableShader, "Helper("),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 終了の要求に応えて終わる()
    {
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.SendAsync(new { jsonrpc = "2.0", id = 9, method = "shutdown" });

        JsonDocument response = await harness.ReceiveAsync();
        Assert.Equal(9, response.RootElement.GetProperty("id").GetInt32());

        await harness.SendAsync(new { jsonrpc = "2.0", method = "exit" });
        Assert.Equal(0, await harness.WaitForExitAsync());
    }
}
