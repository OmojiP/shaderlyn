using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL のコードを対象にするルールの検証。
/// </summary>
public sealed class HlslRuleTests
{
    private const string UrpCorePath = "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl";

    /// <summary>埋め込みコードの中身を指定してシェーダーを組み立てる。</summary>
    /// <param name="codeLines">HLSLPROGRAM の中に置く行。</param>
    /// <returns>組み立てたシェーダーのソース。</returns>
    private static string Shader(params string[] codeLines)
        => "Shader \"Test/Hlsl\"\n"
           + "{\n"
           + "    SubShader\n"
           + "    {\n"
           + "        Pass\n"
           + "        {\n"
           + "            HLSLPROGRAM\n"
           + "            #include \"" + UrpCorePath + "\"\n"
           + string.Join("\n", codeLines.Select(line => "            " + line))
           + "\n            ENDHLSL\n"
           + "        }\n"
           + "    }\n"
           + "}\n";

    /// <summary>シェーダーを解析して診断を得る。</summary>
    /// <param name="source">解析するシェーダーのソース。</param>
    /// <param name="urpCoreContent">URP のヘッダの中身として与える内容。</param>
    /// <returns>検出した診断。</returns>
    private static ImmutableArray<Diagnostic> Analyze(string source, string urpCoreContent = "")
    {
        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = urpCoreContent }),
        };

        AnalysisTarget unit = ShaderCompilation.Create(text, tree, options).CreateAnalysisTarget();

        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(unit);
    }

    private static string Describe(ImmutableArray<Diagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"  {d.Location.LineSpan.Start} {d.Id}: {d.GetMessage()}"));

    /// <summary>正しく書かれた最小のシェーダー。</summary>
    private static string[] ValidProgram =>
    [
        "#pragma vertex vert",
        "#pragma fragment frag",
        "struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };",
        "Varyings vert(float4 positionOS : POSITION) { Varyings o; o.positionCS = positionOS; o.uv = 0; return o; }",
        "half4 frag(Varyings i) : SV_Target { return i.uv.xyxy; }",
    ];

    [Fact]
    public void 正しく書かれたシェーダーには指摘を出さない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(ValidProgram));
        Assert.True(diagnostics.IsEmpty, Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0210: セマンティクスの重複
    // ------------------------------------------------------------------

    [Fact]
    public void 構造体内のセマンティクスの重複を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "struct Varyings",
            "{",
            "    float4 positionCS : SV_POSITION;",
            "    float2 uv : TEXCOORD0;",
            "    float2 uv2 : TEXCOORD0;",
            "};",
            "Varyings vert(float4 p : POSITION) { Varyings o; o.positionCS = p; o.uv = 0; o.uv2 = 0; return o; }",
            "half4 frag(Varyings i) : SV_Target { return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0210");
        Assert.Contains("TEXCOORD0", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("Varyings", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 別々の構造体が同じセマンティクスを使うのは報告しない()
    {
        // 頂点入力と頂点出力で同じ TEXCOORD0 を使うのはむしろ普通である。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };",
            "struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };",
            "Varyings vert(Attributes v) { Varyings o; o.positionCS = v.positionOS; o.uv = v.uv; return o; }",
            "half4 frag(Varyings i) : SV_Target { return 0; }"));

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0210");
    }

    [Fact]
    public void レジスタ指定は重複の検査から外す()
    {
        // : register(t0) は構文上セマンティクスと同じ形をしているが意味が違う。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "struct Data { float4 a : packoffset(c0); float4 b : packoffset(c0); };",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0210");
    }

    // ------------------------------------------------------------------
    // HL0301 / HL0302: エントリポイント
    // ------------------------------------------------------------------

    [Fact]
    public void 存在しないエントリポイントを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment fragmnet",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "half4 frag() : SV_Target { return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0301");
        Assert.Contains("fragmnet", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void 共通コード片で定義されたエントリポイントは見つかる()
    {
        // HLSLINCLUDE の中身は各 Pass の先頭へ差し込まれる。
        // これを再現できていないと、正しいシェーダーを誤りとして報告する。
        const string source = """
            Shader "Test/Shared"
            {
                SubShader
                {
                    HLSLINCLUDE
                    float4 SharedVert(float4 p : POSITION) : SV_POSITION { return p; }
                    ENDHLSL

                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex SharedVert
                        #pragma fragment frag
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0301");
    }

    [Fact]
    public void 非活性領域で定義されたエントリポイントは報告しない()
    {
        // 単一構成での展開では、別の構成でのみ定義される関数が見えない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "#ifdef SOME_VARIANT",
            "half4 frag() : SV_Target { return 0; }",
            "#endif"));

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0301");
    }

    [Fact]
    public void バリアントで調べた領域に名前があるだけのエントリポイントを報告する()
    {
        // #define を含むのでまとめられず、_A のバリアントとして別に解析される。
        // そのバリアントにも frag の定義は無い。名前が現れていても見逃す理由にならない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "#pragma shader_feature_local _A",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "#ifdef _A",
            "#define USE_A 1",
            "half4 g() { return frag; }",
            "#endif"));

        Assert.Single(diagnostics, d => d.Id == "HL0301");
    }

    [Fact]
    public void どの構成でも調べない領域で定義されたエントリポイントは報告しない()
    {
        // SHADER_API_* は環境の条件なので、バリアントを作らない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "#if defined(SHADER_API_GLES)",
            "half4 frag() : SV_Target { return 0; }",
            "#endif"));

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0301");
    }

    [Fact]
    public void ステージの指定が無いブロックを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { return 0; }"));

        Assert.Single(diagnostics, d => d.Id == "HL0302");
    }

    [Fact]
    public void サーフェスシェーダーはステージ指定として認める()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma surface surf Standard",
            "struct Input { float2 uv_MainTex; };",
            "void surf(Input IN, inout SurfaceOutputStandard o) { o.Albedo = 1; }"));

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0302");
        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0301");
    }

    // ------------------------------------------------------------------
    // HL0330 / HL0331: シェーダーのシンボル
    // ------------------------------------------------------------------

    /// <summary>シンボルの宣言と使用を並べたシェーダー。</summary>
    private const string SymbolShader = """
        Shader "Test/Keyword"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma shader_feature_local _NORMALMAP
                    #pragma multi_compile _ _SHADOWS_SOFT
                    #pragma shader_feature _NEVER_USED

                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

                    half4 frag() : SV_Target
                    {
                        #ifdef _NORMALMAP
                            return 1;
                        #endif
                        #ifdef _SHADOWS_SOFTT
                            return 2;
                        #endif
                        #if defined(_SHADOWS_SOFT)
                            return 3;
                        #endif
                        #ifdef SHADER_API_D3D11
                            return 4;
                        #endif
                        return 0;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // ------------------------------------------------------------------
    // HL0332: どの構成でも成り立たない条件
    // ------------------------------------------------------------------

    /// <summary>
    /// どの構成でも成り立たない条件だけを報告することを検証する。
    /// </summary>
    /// <param name="pragmas">シンボルの宣言。行は <c>|</c> で区切る。</param>
    /// <param name="condition">分岐の条件。</param>
    /// <param name="expected">報告するはずなら <see langword="true"/>。</param>
    /// <remarks>
    /// <para>
    /// Unity は有効なシンボルを値 1 で定義する。1 以外と比べる条件は、有効にしても無効にしても通らない。
    /// 同じ行のシンボルは同時に有効にならず、<c>_</c> の無い <c>multi_compile</c> の行はどれか 1 つが必ず有効である。
    /// </para>
    /// <para>
    /// 環境のマクロ (<c>SHADER_API_D3D11</c>) を含む条件は報告しない。1 通りの値を仮に選んでいるだけで、
    /// 別のプラットフォームでは真になりうる。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("#pragma multi_compile _ _A", "#if _A == 2", true)]
    [InlineData("#pragma multi_compile _ _A", "#if _A > 1", true)]
    [InlineData("#pragma multi_compile _ _A", "#if defined(_A) && !defined(_A)", true)]
    [InlineData("#pragma multi_compile _ _X _Y", "#if defined(_X) && defined(_Y)", true)]
    [InlineData("#pragma multi_compile MODE_A MODE_B", "#if !defined(MODE_A) && !defined(MODE_B)", true)]
    [InlineData("#pragma multi_compile _ _A", "#if _A == 1", false)]
    [InlineData("#pragma multi_compile _ _A", "#if _A != 0", false)]
    [InlineData("#pragma multi_compile _ _X|#pragma multi_compile _ _Y", "#if defined(_X) && defined(_Y)", false)]
    [InlineData("#pragma shader_feature MODE_A MODE_B", "#if !defined(MODE_A) && !defined(MODE_B)", false)]
    [InlineData("#pragma multi_compile _ _A", "#if _A == 2 || SHADER_API_D3D11", false)]
    [InlineData("#pragma multi_compile _ _A", "#if _UNDECLARED == 2", false)]
    public void どの構成でも成り立たない条件を報告する(string pragmas, string condition, bool expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            [.. pragmas.Split('|'), condition, "float _Never;", "#endif", .. ValidProgram]));

        Diagnostic[] reported = [.. diagnostics.Where(d => d.Id == "HL0332")];

        Assert.True(expected == (reported.Length == 1), Describe(diagnostics));
    }

    [Fact]
    public void どの構成でも成り立たないelifを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            ["#pragma multi_compile _ _A", "#if defined(_A)", "float _First;", "#elif _A == 2", "float _Second;", "#endif", .. ValidProgram]));

        Diagnostic diagnostic = Assert.Single(diagnostics.Where(d => d.Id == "HL0332"));
        Assert.Contains("_A == 2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 取り込んだヘッダの成り立たない条件は報告しない()
    {
        // ヘッダの条件はヘッダの都合で書かれている。このファイルの利用者に直しようが無い。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(["#pragma multi_compile _ _A", .. ValidProgram]),
            "#if _A == 2\nfloat _InHeader;\n#endif\n");

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0332");
    }

    [Fact]
    public void 宣言されていないシンボルを報告する()
    {
        // シンボルは宣言して初めて切り替えられる。
        // 綴りを 1 文字誤っただけで、その機能が丸ごと入らないまま出荷されうる。
        Diagnostic diagnostic = Assert.Single(
            Analyze(SymbolShader).Where(d => d.Id == "HL0330"));

        Assert.Contains("_SHADOWS_SOFTT", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 宣言されているシンボルは報告しない()
    {
        string[] reported = [.. Analyze(SymbolShader).Where(d => d.Id == "HL0330").Select(d => d.GetMessage())];

        Assert.DoesNotContain(reported, m => m.Contains("_NORMALMAP", StringComparison.Ordinal));
        Assert.DoesNotContain(reported, m => m.Contains("_SHADOWS_SOFT'", StringComparison.Ordinal));
    }

    [Fact]
    public void プラットフォームの判定を宣言漏れとして報告しない()
    {
        // 条件にはコンパイラが定義する名前も現れる。
        // それらは宣言されていなくて当然である。
        Assert.DoesNotContain(
            Analyze(SymbolShader).Where(d => d.Id == "HL0330"),
            d => d.GetMessage().Contains("SHADER_API_D3D11", StringComparison.Ordinal));
    }

    [Fact]
    public void 宣言したのに使われていないシンボルを報告する()
    {
        // シンボルは 1 つ増えるごとにバリアントの数を倍にする。
        Diagnostic diagnostic = Assert.Single(
            Analyze(SymbolShader).Where(d => d.Id == "HL0331"));

        Assert.Contains("_NEVER_USED", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 下線だけの名前をシンボルとして数えないことを検証する。
    /// </summary>
    /// <remarks>
    /// <c>multi_compile</c> の「シンボル無しの構成」は <c>_</c> とも <c>__</c> とも書ける。
    /// Unity 同梱のシェーダーは <c>_</c> を 1,323 回、<c>__</c> を 28 回使っている。
    /// <c>_</c> だけを除いていたため、<c>__</c> が
    /// 「宣言したのに使っていない」として報告されていた。
    /// </remarks>
    [Theory]
    [InlineData("_")]
    [InlineData("__")]
    [InlineData("___")]
    public void 下線だけの名前はシンボルとして数えない(string placeholder)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze($$"""
            Shader "Test/Placeholder"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #pragma multi_compile {{placeholder}} _SHADOWS_SOFT

                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

                        half4 frag() : SV_Target
                        {
                            #ifdef _SHADOWS_SOFT
                                return 1;
                            #endif
                            return 0;
                        }
                        ENDHLSL
                    }
                }
            }
            """);

        Assert.Empty(diagnostics.Where(d => d.Id is "HL0330" or "HL0331"));
    }

    /// <summary>
    /// <c>#if _A</c> の形もシンボルとして扱うことを検証する。
    /// </summary>
    /// <param name="directive">条件の書き方。</param>
    /// <remarks>
    /// Unity は有効なシンボルを値 1 のマクロとして定義するため、
    /// URP 自身も <c>#if _ALPHATEST_ON</c> の形を使っている。
    /// 記号として扱わないと、参照しているのに「使われていない」(HL0331) と報告され、
    /// その分岐のコードも検査されない。
    /// </remarks>
    [Theory]
    [InlineData("#ifdef _NORMALMAP")]
    [InlineData("#if _NORMALMAP")]
    [InlineData("#if defined(_NORMALMAP)")]
    public void 条件の書き方によらずシンボルとして扱う(string directive)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma shader_feature_local _NORMALMAP",
            "half4 frag() : SV_Target",
            "{",
            directive,
            "    return Missing();",
            "#endif",
            "    return 0;",
            "}"));

        // 宣言を参照しているので「使われていない」にはならない。
        Assert.False(Has(diagnostics, "HL0331"), Describe(diagnostics));

        // 分岐の中のコードも検査される。
        Assert.Contains(diagnostics, d => d.Id == "HL0310");
    }

    /// <summary>HLSLINCLUDE を複数のパスが共有するシェーダー。</summary>
    /// <remarks>
    /// HLSLINCLUDE の中身は SubShader のすべてのパスに入る。
    /// パスごとに判定すると、同じ 1 行がパスの数だけ報告される。
    /// </remarks>
    private const string SharedSymbolShader = """
        Shader "Test/SharedKeyword"
        {
            SubShader
            {
                HLSLINCLUDE
                #pragma shader_feature_local _NORMALMAP
                #pragma shader_feature_local _DETAIL

                #ifdef _MISSPELED
                    #define BROKEN 1
                #endif
                ENDHLSL

                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                    half4 frag() : SV_Target
                    {
                        #ifdef _NORMALMAP
                            return 1;
                        #endif
                        return 0;
                    }
                    ENDHLSL
                }

                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                    half4 frag() : SV_Target
                    {
                        #ifdef _DETAIL
                            return 1;
                        #endif
                        return 0;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public void 共有されたシンボルの誤りをパスの数だけ報告しない()
    {
        // HLSLINCLUDE の 1 行を 2 つのパスが共有している。
        // 同じ場所の同じ誤りを 2 回並べても、読み手には 2 件の問題に見えるだけである。
        Diagnostic diagnostic = Assert.Single(
            Analyze(SharedSymbolShader).Where(d => d.Id == "HL0330"));

        Assert.Contains("_MISSPELED", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 別のパスで使われているシンボルを未使用と言わない()
    {
        // _NORMALMAP は 1 つ目のパス、_DETAIL は 2 つ目のパスでしか使われていない。
        // パスごとに判定すると、どちらも「もう一方のパスでは未使用」として報告されてしまう。
        Assert.Empty(Analyze(SharedSymbolShader).Where(d => d.Id == "HL0331"));
    }

    [Fact]
    public void 依存関係が解決できない場合はエントリポイントを検査しない()
    {
        // 取り込めなかったヘッダに定義されている可能性がある以上、
        // 「見つからない」ことを根拠にはできない。
        const string source = """
            Shader "Test/Missing"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "MissingHeader.hlsl"
                        #pragma vertex vert
                        #pragma fragment frag
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Assert.True(Has(diagnostics, "SL0002"), Describe(diagnostics));
        Assert.False(Has(diagnostics, "HL0301"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0001: 埋め込み HLSL の構文エラーの報告
    // ------------------------------------------------------------------

    /// <summary>14 行目の <c>}</c> の位置に HL0001 が出るシェーダー。</summary>
    /// <remarks><c>return 0</c> のセミコロンが無い。</remarks>
    private const string BrokenShader = """
        Shader "Test/Broken"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag

                    float4 vert() : SV_POSITION
                    {
                        return 0
                    }

                    float4 frag() : SV_Target { return 1; }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public void 埋め込みHLSLの構文エラーを実際の位置で報告する()
    {
        // 位置がファイルの先頭に丸められると、利用者は直す場所に辿り着けない。
        // マスクした複製の上で解析していても、
        // 報告する位置は元のファイル上の該当行でなければならない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(BrokenShader);
        Diagnostic error = Assert.Single(diagnostics.Where(d => d.Id == "HL0001"));

        Assert.Equal(14, error.Location.LineSpan.Start.Line + 1);
        Assert.Equal("}", error.Location.GetLineText().Trim());
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void 構文エラーで検査を見送ったときはincludeのせいにしない()
    {
        // 「依存関係を解決できないため」と言われた利用者は --unity-project を疑う。
        // 既に指定してある場合、それは何時間でも溶かせる誤誘導になる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(BrokenShader);
        string message = diagnostics.First(d => d.Id == "SL0002").GetMessage();

        Assert.DoesNotContain("include", message, StringComparison.Ordinal);
        Assert.Contains("HL0001", message, StringComparison.Ordinal);
        Assert.Contains("14:13", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 共通コード片の構文エラーをPassの数だけ繰り返さない()
    {
        // 共通コード片は Pass の数だけ解析し直される。
        // そのまま報告すると、1 か所の誤りが Pass の数だけ並ぶ。
        const string source = """
            Shader "Test/Shared"
            {
                SubShader
                {
                    HLSLINCLUDE
                    float4 Broken()
                    {
                        return 0
                    }
                    ENDHLSL

                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        float4 vert() : SV_POSITION { return 0; }
                        float4 frag() : SV_Target { return 1; }
                        ENDHLSL
                    }

                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        float4 vert() : SV_POSITION { return 0; }
                        float4 frag() : SV_Target { return 1; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Diagnostic error = Assert.Single(diagnostics.Where(d => d.Id == "HL0001"));

        Assert.Equal(9, error.Location.LineSpan.Start.Line + 1);
    }

    [Fact]
    public void ヘッダの宣言の誤りは報告せず検査を見送ったことだけを伝える()
    {
        // ヘッダの中の指摘は利用者に直しようがなく、
        // 環境の条件を 1 通りだけ解く都合による誤りであることも多い。
        // ただし対応検査を見送った事実は伝えなければならない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("float4 frag() : SV_Target { return 1; }"),
            urpCoreContent: "float4 ;\nfloat4 _Ok;\n");

        Assert.False(Has(diagnostics, "HL0001"), Describe(diagnostics));
        Assert.True(Has(diagnostics, "SL0002"), Describe(diagnostics));
        Assert.Contains(
            "Core.hlsl",
            diagnostics.First(d => d.Id == "SL0002").GetMessage(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ヘッダの関数の中身は読まない()
    {
        // 中身はどのルールも見ていないので、構文木にしない。
        // 括弧の対応さえ取れていれば、中の書き方は解析に影響しない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("float4 frag() : SV_Target { return Provided(); }"),
            urpCoreContent: "float4 Provided()\n{\n    return 0\n}\n");

        Assert.False(Has(diagnostics, "HL0001"), Describe(diagnostics));
        Assert.False(Has(diagnostics, "SL0002"), Describe(diagnostics));

        // 中身を読まなくても「実装がある」ことは分かる。
        Assert.False(Has(diagnostics, "HL0311"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0311: 関数の定義がその構成に無い
    // ------------------------------------------------------------------

    [Fact]
    public void 定義が呼ぶ側と別の条件にあるとき報告する()
    {
        // 呼ぶのは !SHADER_DEBUG のとき、定義があるのは SHADER_DEBUG のとき。
        // 2 つは同時に成り立たないので、呼ぶときには必ず定義が無い。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ SHADER_DEBUG",
            "half4 sampleTexture(float2 uv);",
            "half4 frag() : SV_Target",
            "{",
            "#ifdef SHADER_DEBUG",
            "    return 0;",
            "#else",
            "    return sampleTexture(float2(0, 0));",
            "#endif",
            "}",
            "#ifdef SHADER_DEBUG",
            "half4 sampleTexture(float2 uv) { return 1; }",
            "#endif"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0311");

        Assert.Contains("sampleTexture", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("!SHADER_DEBUG", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void 定義がどこにも無い関数を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 sampleTexture(float2 uv);",
            "half4 frag() : SV_Target { return sampleTexture(float2(0, 0)); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0311");

        Assert.Contains("どの構成でも", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 取り込んだヘッダ自身が宣言したシンボルの分岐にしか定義が無い関数を報告することを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 共通の関数を <c>Common.hlsl</c> に切り出し、<c>#pragma multi_compile</c> もそこに置く書き方である。
    /// シェーダー側には宣言が 1 つも無いので、ヘッダを取り込むまでそのシンボルの存在が分からない。
    /// </para>
    /// <para>
    /// ヘッダの宣言を拾わないと、<c>#ifdef</c> の既定の側しか読まれず、
    /// もう一方でしか定義されていない関数は「どこにも無い」ことにされる。
    /// 実際にはヘッダの非活性領域に名前があるため「見えていないだけ」として検査を見送り、
    /// 何も報告されなかった。
    /// </para>
    /// </remarks>
    [Fact]
    public void ヘッダが宣言したシンボルの分岐にしか定義が無い関数を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(
                "half4 frag() : SV_Target { return highQualitySample(float2(0, 0)); }"),
            """
            #pragma multi_compile _ _HEADER_QUALITY

            #ifdef _HEADER_QUALITY
            half4 highQualitySample(float2 uv) { return 1; }
            #endif
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0311");

        Assert.Contains("highQualitySample", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("!_HEADER_QUALITY", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ヘッダが宣言したシンボルで中身が変わるマクロを、構成ごとに展開して検査することを検証する。
    /// </summary>
    /// <remarks>
    /// 分岐ごとに同じマクロを違う中身で定義しているので、両方の分岐を 1 本の木には並べられない。
    /// 構成として作り直さなければ、既定の側しか読まれない。
    /// </remarks>
    [Fact]
    public void ヘッダが宣言したシンボルで型が変わるマクロを構成ごとに検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(
                "half4 frag() : SV_Target",
                "{",
                "    PRECISE_VECTOR v = 0;",
                "    return half4(v, 1);",
                "}"),
            """
            #pragma multi_compile _HIGH_PRECISION _LOW_PRECISION

            #ifdef _HIGH_PRECISION
            #define PRECISE_VECTOR float3
            #else
            #define PRECISE_VECTOR float2
            #endif
            """);

        // 既定の構成は _HIGH_PRECISION で、そこでは 3 + 1 で足りる。
        // _LOW_PRECISION の構成を作らなければ、この誤りは見つからない。
        Assert.Single(diagnostics, d => d.Id == "HL0343");
    }

    [Fact]
    public void 呼ぶ側と同じ条件に定義があるなら報告しない()
    {
        // 条件が揃っている。どの構成でも、呼ぶときには定義がある。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ SHADER_DEBUG",
            "half4 sampleTexture(float2 uv);",
            "half4 frag() : SV_Target",
            "{",
            "#ifdef SHADER_DEBUG",
            "    return sampleTexture(float2(0, 0));",
            "#else",
            "    return 0;",
            "#endif",
            "}",
            "#ifdef SHADER_DEBUG",
            "half4 sampleTexture(float2 uv) { return 1; }",
            "#endif"));

        Assert.False(Has(diagnostics, "HL0311"), Describe(diagnostics));
    }

    [Fact]
    public void 無条件に定義された関数は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 sampleTexture(float2 uv) { return 1; }",
            "half4 frag() : SV_Target { return sampleTexture(float2(0, 0)); }"));

        Assert.False(Has(diagnostics, "HL0311"), Describe(diagnostics));
    }

    [Fact]
    public void このシェーダーが宣言していない関数は報告しない()
    {
        // 組み込み関数はどこにも定義が無い。
        // 対象にすると、あらゆる呼び出しが報告されることになる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { return lerp(0, 1, 0.5); }"));

        Assert.False(Has(diagnostics, "HL0311"), Describe(diagnostics));
    }

    [Fact]
    public void ヘッダに宣言だけがある関数の呼び出しも報告する()
    {
        // 宣言が見えていて実装がどこにも無いなら、その構成ではリンクできない。
        // 宣言をこのファイルに書いたかどうかは、その判断と関係がない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("half4 frag() : SV_Target { return Missing(float2(0, 0)); }"),
            urpCoreContent: "half4 Missing(float2 uv);");

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0311");

        Assert.Contains("Missing", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ヘッダに実装がある関数は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("half4 frag() : SV_Target { return Provided(float2(0, 0)); }"),
            urpCoreContent: "half4 Provided(float2 uv);\n"
                            + "half4 Provided(float2 uv) { return 1; }");

        Assert.False(Has(diagnostics, "HL0311"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0313: 構造体のメンバーがその構成に無い
    // ------------------------------------------------------------------

    /// <summary>条件付きのメンバーを持つ構造体を組み立てる。</summary>
    /// <param name="frag">frag の中身。</param>
    /// <returns>組み立てたシェーダーのソース。</returns>
    private static string ConditionalMemberShader(params string[] frag)
        => Shader([
            "#pragma multi_compile _ SHADER_DEBUG",
            "struct Varyings",
            "{",
            "    float4 positionHCS : SV_POSITION;",
            "    float2 uv : TEXCOORD0;",
            "#ifdef SHADER_DEBUG",
            "    float4 debugColor : COLOR;",
            "#endif",
            "};",
            "half4 frag(Varyings IN) : SV_Target",
            "{",
            .. frag,
            "}"]);

    [Fact]
    public void 条件付きメンバーを条件の外から参照したとき報告する()
    {
        // メンバーがあるのは SHADER_DEBUG のとき、参照するのは !SHADER_DEBUG のとき。
        // 2 つは同時に成り立たないので、参照するときにはメンバーが無い。
        ImmutableArray<Diagnostic> diagnostics = Analyze(ConditionalMemberShader(
            "#ifdef SHADER_DEBUG",
            "    return IN.debugColor;",
            "#else",
            "    return IN.debugColor;",
            "#endif"));

        // 報告するのは #else の側だけである。
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0313");

        Assert.Contains("debugColor", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("Varyings", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("!SHADER_DEBUG", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void 条件付きメンバーを無条件に参照したとき報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(ConditionalMemberShader(
            "    return IN.debugColor;"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0313");

        Assert.Contains("!SHADER_DEBUG", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 同じ条件の中からの参照は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(ConditionalMemberShader(
            "#ifdef SHADER_DEBUG",
            "    return IN.debugColor;",
            "#endif",
            "    return 0;"));

        Assert.False(Has(diagnostics, "HL0313"), Describe(diagnostics));
    }

    [Fact]
    public void 無条件のメンバーの参照は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(ConditionalMemberShader(
            "    return IN.uv.xyxy;"));

        Assert.False(Has(diagnostics, "HL0313"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0340: 実引数を仮引数の型へ変換できない
    // ------------------------------------------------------------------

    /// <summary>条件で仮引数の型が変わる関数を持つシェーダーを組み立てる。</summary>
    /// <param name="otherParameter">#else 側の仮引数の宣言。</param>
    /// <returns>組み立てたシェーダーのソース。</returns>
    private static string OverloadedShader(string otherParameter)
        => Shader(
            "#pragma multi_compile _ SHADER_DEBUG",
            "#ifdef SHADER_DEBUG",
            "half4 sampleTexture(float2 uv) { return uv.x; }",
            "#else",
            "half4 sampleTexture(" + otherParameter + ") { return 0; }",
            "#endif",
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target",
            "{",
            "#ifdef SHADER_DEBUG",
            "    return sampleTexture(uv);",
            "#else",
            "    return sampleTexture(uv);",
            "#endif",
            "}");

    [Fact]
    public void 成分が足りない実引数を報告する()
    {
        // !SHADER_DEBUG のとき、仮引数は float3 で実引数は float2。
        // HLSL は成分を増やす変換を許さない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(OverloadedShader("float3 uvw"));

        // 報告するのは #else の側だけである。
        // #ifdef の側は、その構成にある float2 版が受け付ける。
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0340");

        Assert.Contains("sampleTexture", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("float2", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("float3", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("!SHADER_DEBUG", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void 成分を減らす実引数は報告しない()
    {
        // float2 を float へ渡すのは切り捨てであり、警告は出るが通る。
        ImmutableArray<Diagnostic> diagnostics = Analyze(OverloadedShader("float u"));

        Assert.False(Has(diagnostics, "HL0340"), Describe(diagnostics));
    }

    [Fact]
    public void スカラーを渡すのは報告しない()
    {
        // スカラーは全成分へ複製される。これは許される。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 shade(float3 v) { return v.x; }",
            "half4 frag() : SV_Target { return shade(1.0); }"));

        Assert.False(Has(diagnostics, "HL0340"), Describe(diagnostics));
    }

    [Fact]
    public void 条件が無くても成分が足りなければ報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 shade(float3 v) { return v.x; }",
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { return shade(uv); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0340");

        // 無条件の呼び出しでは、構成の断りを添えない。読み手には雑音でしかない。
        Assert.DoesNotContain("のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 受け付ける宣言が1つでもあれば報告しない()
    {
        // 同じ構成に float2 版と float3 版が並んでいる。float2 版が受け付ける。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 shade(float3 v) { return v.x; }",
            "half4 shade(float2 v) { return v.x; }",
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { return shade(uv); }"));

        Assert.False(Has(diagnostics, "HL0340"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0341: 引数の個数が合わない
    // ------------------------------------------------------------------

    [Fact]
    public void 引数の個数が合わない呼び出しを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 shade(float3 v) { return v.x; }",
            "half4 frag(float3 v : TEXCOORD0) : SV_Target { return shade(v, v); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0341");

        Assert.Contains("shade", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("2 個", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("1 個", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("lerp(v, v)", true)]
    [InlineData("lerp(v, v, 0.5)", false)]
    [InlineData("saturate(v, v)", true)]
    [InlineData("tex2D(_S, v.xy)", false)]
    [InlineData("tex2D(_S, v.xy, v.xy, v.xy)", false)]   // 勾配を渡す形もある
    [InlineData("tex2D(_S, v.xy, v.xy)", true)]
    public void 組み込み関数の引数の個数を検査する(string call, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "sampler2D _S;",
            "half4 frag(float3 v : TEXCOORD0) : SV_Target { return half4(" + call + ".xxx, 1); }"));

        Assert.True(Has(diagnostics, "HL0341") == reported, Describe(diagnostics));
    }

    [Fact]
    public void 組み込み関数の名前で多重定義していれば組み込み関数の個数も受け付ける()
    {
        // max(a, b, c) を足しても、2 個の max は組み込み関数が受け付ける。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float3 max(float3 a, float3 b, float3 c) { return a; }",
            "half4 frag(float3 v : TEXCOORD0) : SV_Target { return max(v, v).x; }"));

        Assert.False(Has(diagnostics, "HL0341"), Describe(diagnostics));
    }

    [Fact]
    public void 依存が揃っていなければ組み込み関数の個数を検査しない()
    {
        // 取り込めなかったヘッダに、同じ名前の関数やマクロがあるかもしれない。
        string source = Shader("half4 frag(float3 v : TEXCOORD0) : SV_Target { return lerp(v, v).x; }")
            .Replace(UrpCorePath, "Packages/does.not.exist/Missing.hlsl", StringComparison.Ordinal);

        Assert.False(Has(Analyze(source), "HL0341"));
    }

    [Fact]
    public void 既定値のある仮引数は省略できる()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 shade(float3 v, float k = 1) { return v.x * k; }",
            "half4 frag(float3 v : TEXCOORD0) : SV_Target { return shade(v); }"));

        Assert.False(Has(diagnostics, "HL0341"), Describe(diagnostics));
    }

    [Fact]
    public void 条件で個数が変わる関数は構成ごとに判断する()
    {
        // !SHADER_DEBUG のときの宣言は 1 個しか受け付けない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ SHADER_DEBUG",
            "#ifdef SHADER_DEBUG",
            "half4 shade(float3 v, float k) { return v.x * k; }",
            "#else",
            "half4 shade(float3 v) { return v.x; }",
            "#endif",
            "half4 frag(float3 v : TEXCOORD0) : SV_Target",
            "{",
            "#ifdef SHADER_DEBUG",
            "    return shade(v, 1);",
            "#else",
            "    return shade(v, 1);",
            "#endif",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0341");

        Assert.Contains("!SHADER_DEBUG", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // HL0312: そのメンバーが存在しない
    // ------------------------------------------------------------------

    /// <summary>
    /// 関数の外の <c>static</c> / <c>groupshared</c> の変数も、型を引いて検査することを検証する。
    /// </summary>
    /// <remarks>
    /// uniform ではないので uniform の一覧には無い。
    /// そこしか見ていなかったため型が分からず、成分の検査を見送っていた。
    /// HDRP の <c>static const float4 KERNEL_WEIGHTS</c> がこの形である。
    /// </remarks>
    [Theory]
    [InlineData("static const float2 WEIGHTS = float2(1, 2);")]
    [InlineData("static float2 WEIGHTS;")]
    [InlineData("groupshared float2 WEIGHTS;")]
    public void 関数の外のstatic変数の成分を検査する(string declaration)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            declaration,
            "half4 frag() : SV_Target { return WEIGHTS.z; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");

        Assert.Contains("float2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 並べた分岐の中を、そのキーワードを有効にした構成の型で検査することを検証する。
    /// </summary>
    /// <remarks>
    /// ヘッダが <c>CTYPE</c> をキーワードで書き分けている。既定の木の <c>CTYPE c</c> は <c>float3</c> だが、
    /// 既定の木に並べた <c>#ifdef ENABLE_ALPHA</c> の中では <c>float4</c> である。
    /// Unity 同梱の HDRP のポストプロセス (PostProcessDefines.hlsl) の形。
    /// </remarks>
    [Fact]
    public void 並べた分岐はそのキーワードを有効にした構成の型で検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(
                "#pragma fragment frag",
                "#pragma multi_compile _ ENABLE_ALPHA",
                "half4 frag() : SV_Target",
                "{",
                "    CTYPE c = 0;",
                "    half4 r = 0;",
                "#ifdef ENABLE_ALPHA",
                "    r.a = c.w;",
                "    r.b = c.q;",
                "#endif",
                "    return r;",
                "}"),
            "#ifndef DEFINES_INCLUDED\n#define DEFINES_INCLUDED\n#if !defined(ENABLE_ALPHA)\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\n#endif\n");

        // c.w は float4 なら読める。c.q はどの型でも読めないので、float4 として報告する。
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");
        Assert.Contains("float4", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'q'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 既定の構成で並べるのをやめたシンボルを、ほかのバリアントでも並べないことを検証する。
    /// </summary>
    /// <remarks>
    /// ヘッダが <c>#if !defined(ENABLE_ALPHA)</c> で <c>CTYPE</c> を書き分けていて、コードで <c>CTYPE</c> を使う。
    /// 既定の構成では、並べた分岐のマクロがコードで使われたので <c>ENABLE_ALPHA</c> を並べずに展開し直す。
    /// <c>DITHER</c> のバリアントでまた並べると、<c>CTYPE</c> の定義が両方実行されて <c>float4</c> が残り、
    /// <c>ENABLE_ALPHA</c> を無効にした構成で <c>float4(c, 1)</c> を成分 5 個と数えてしまう。
    /// Unity 同梱の HDRP の FinalPass.shader の形。
    /// </remarks>
    [Fact]
    public void 既定の構成で並べるのをやめたシンボルはバリアントでも並べない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(
                "#pragma fragment frag",
                "#pragma multi_compile _ ENABLE_ALPHA",
                "#pragma multi_compile _ DITHER",
                "float4 frag() : SV_Target",
                "{",
                "    float d",
                "#ifdef DITHER",
                "        = 1",
                "#else",
                "        = 0",
                "#endif",
                "        ;",
                "    CTYPE c = d;",
                "#if !defined(ENABLE_ALPHA)",
                "    return float4(c, 1);",
                "#else",
                "    return c;",
                "#endif",
                "}"),
            "#ifndef DEFINES_INCLUDED\n#define DEFINES_INCLUDED\n#if !defined(ENABLE_ALPHA)\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\n#endif\n");

        Assert.False(Has(diagnostics, "HL0343"), Describe(diagnostics));
    }

    /// <summary>
    /// 呼び出しを、呼び出しと同じ構成の木の関数の宣言と比べることを検証する。
    /// </summary>
    /// <remarks>
    /// ヘッダの関数の仮引数がキーワードで書き分けたマクロ (<c>inout CTYPE c</c>) だと、構成ごとに型が違う。
    /// 同じ位置の宣言を 1 つにまとめて最初の木のものを使うと、<c>ENABLE_ALPHA</c> の構成の呼び出しを
    /// 既定の構成の <c>float3</c> の仮引数と比べてしまう。Unity 同梱の HDRP の FinalPass.shader (RunFXAA) の形。
    /// </remarks>
    [Fact]
    public void 呼び出しは同じ構成の木の関数の宣言と比べる()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(
                "#pragma fragment frag",
                "#pragma multi_compile _ ENABLE_ALPHA",
                "half4 frag() : SV_Target",
                "{",
                "    CTYPE c = 0;",
                "    Run(c);",
                "    half4 r = 0;",
                "#ifdef ENABLE_ALPHA",
                "    r.a = 1;",
                "#endif",
                "    return r;",
                "}"),
            "#ifndef DEFINES_INCLUDED\n#define DEFINES_INCLUDED\n#if !defined(ENABLE_ALPHA)\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\nvoid Run(inout CTYPE c) { c = 1; }\n#endif\n");

        Assert.False(Has(diagnostics, "HL0340"), Describe(diagnostics));
    }

    /// <summary>
    /// 名前の範囲を波括弧ごとに追って、使っている位置が指す宣言の型で検査することを検証する。
    /// </summary>
    /// <remarks>
    /// 以前は関数の中のすべての宣言を 1 つの表に集め、
    /// 同じ名前が違う型で宣言されていたら、その名前の型を関数全体で不明にしていた。
    /// 分岐ごとに一時変数を宣言する普通の書き方で、成分の検査が軒並み見送られていた。
    /// </remarks>
    [Theory]

    // 内側の波括弧での覆い隠し。外の v は float2 のまま。
    [InlineData("float2 v = 0;\n{ float4 v = 0; v.w = 1; }\nreturn v.w;")]

    // 兄弟の波括弧。それぞれの v は自分の型を持つ。
    [InlineData("if (true) { float4 v = 0; return v.w; }\nelse { float2 v = 0; return v.w; }")]

    // 内側で宣言する前に使えば、外の v を指す。
    [InlineData("float2 v = 0;\n{ v.w = 1; float4 v = 0; }\nreturn 0;")]

    // for の初期化は、その for の中で外の宣言を隠す。
    [InlineData("float4 i = 0;\nfor (float2 i = 0; i.x < 1; i.x++) { i.w = 1; }\nreturn 0;")]
    public void 名前の範囲を波括弧ごとに追って型を決める(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target",
            "{",
            body,
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");

        Assert.Contains("float2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// for の初期化の変数は、ループの後でも囲む波括弧の中で見えることを検証する。
    /// </summary>
    /// <remarks>
    /// Unity (fxc) はこの読み方で通す。同じ波括弧の前の同じ名前より優先する (警告 X3078)。
    /// </remarks>
    [Fact]
    public void ループの初期化の変数はループの後でも見える()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target",
            "{",
            "float4 i = 0;",
            "for (float2 i = 0; i.x < 1; i.x++) { }",
            "return i.w;",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");
        Assert.Contains("float2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 波括弧を挟まずに入れ子になったループの変数は後で型を決めない()
    {
        // 外のループの後で内のループの j を使うと、fxc は内部エラーになる。どちらの j とも決めない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target",
            "{",
            "float2 j = 0;",
            "for (int i = 0; i < 2; i++) for (float4 j = 0; j.w < 1; j.w++) { }",
            "return j.w;",
            "}"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    [Fact]
    public void ループの初期化が漏れる範囲の外では外側の宣言を指す()
    {
        // 内側の波括弧の for は、その波括弧の外へは漏れない (fxc でも DXC でも同じ)。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target",
            "{",
            "float2 i = 0;",
            "{ for (float4 i = 0; i.w < 1; i.w++) { } }",
            "return i.w;",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");
        Assert.Contains("float2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 条件付きの宣言は外側の宣言を確実には隠さない()
    {
        // _A が無い構成では内側の宣言が無いので、外の float2 の v を指す。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "half4 frag() : SV_Target",
            "{",
            "    float2 v = 0;",
            "    {",
            "#ifdef _A",
            "        float4 v = 0;",
            "#endif",
            "        v.w = 1;",
            "    }",
            "    return v.x;",
            "}"));

        Assert.Contains(diagnostics, d => d.Id == "HL0312");
    }

    [Fact]
    public void 範囲に載らない宣言がある名前は型を決めない()
    {
        // 波括弧を書かない if の中の宣言は、どの範囲にも属さない。
        // 位置を決められない宣言があるのに外側の宣言で型を決めると、別の変数の型で判定しかねない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target",
            "{",
            "    float4 v = 0;",
            "    if (true) float2 v = 0;",
            "    return v.w;",
            "}"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    [Fact]
    public void 違う型で宣言したstatic変数は型を決めない()
    {
        // 型が 1 つに決まらないなら、どちらの型でも判定しない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "#ifdef _A",
            "static const float2 WEIGHTS = float2(1, 2);",
            "#else",
            "static const float4 WEIGHTS = float4(1, 2, 3, 4);",
            "#endif",
            "half4 frag() : SV_Target { return WEIGHTS.x; }"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    /// <summary>条件ごとに違う型で <c>color</c> を宣言し、<c>_B</c> のときだけ成分を取り出す関数。</summary>
    /// <param name="member">取り出す成分。</param>
    /// <returns>HLSLPROGRAM の中に置く行。</returns>
    private static string[] ColorByCondition(string member) =>
    [
        "#pragma fragment frag",
        "#pragma multi_compile _ _A",
        "#pragma multi_compile _ _B",
        "half4 frag() : SV_Target",
        "{",
        "#ifdef _A",
        "    float3 color = 1;",
        "#else",
        "    float4 color = 1;",
        "#endif",
        "#ifdef _B",
        $"    color.{member} = 0.5;",
        "#endif",
        "    return color.x;",
        "}",
    ];

    [Fact]
    public void 構成によって型が変わる名前の成分を構成ごとに検査する()
    {
        // _A と _B が同時に有効なとき、color は float3 なので .w は無い。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(ColorByCondition("w")));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");
        Assert.Contains("_A && _B のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("float3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 文の途中で分かれる別々の <c>#if</c> が、同時に有効なときだけ起きる誤りを報告することを検証する。
    /// </summary>
    /// <remarks>
    /// どちらの領域も分岐ごとに文を複製して並べる (条件の巻き上げ)。構成ごとに展開していたときは、
    /// <c>_A</c> だけ・<c>_B</c> だけの構成しか作らず、両方が有効な構成のこの誤りは調べていなかった。
    /// </remarks>
    [Fact]
    public void 文の途中で分かれる別々のifの組み合わせも検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "#pragma multi_compile _ _B",
            "float F()",
            "{",
            "#ifdef _A",
            "    float2",
            "#else",
            "    float4",
            "#endif",
            "    v = 0;",
            "    float r",
            "#ifdef _B",
            "        = v.z",
            "#else",
            "        = 0",
            "#endif",
            "        ;",
            "    return r;",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");
        Assert.Contains("_A && _B のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 構成によって型が変わる名前でもどの型にもある成分は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(ColorByCondition("z")));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    [Fact]
    public void 条件の無い同名の宣言は型を決めない()
    {
        // 内側のブロックで宣言し直している。名前の範囲を追っていないので、どちらの型か分からない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma fragment frag",
            "half4 frag() : SV_Target",
            "{",
            "    float4 color = 1;",
            "    {",
            "        float3 color = 1;",
            "        color.x = 0;",
            "    }",
            "    color.w = 0.5;",
            "    return color.x;",
            "}"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    [Fact]
    public void 構造体に無いメンバーの参照を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };",
            "half4 frag(Varyings IN) : SV_Target { return IN.postionHCS.x; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");

        Assert.Contains("postionHCS", diagnostic.GetMessage(), StringComparison.Ordinal);

        // 正しい名前を並べれば、スペルミスは自分で気づける。
        Assert.Contains("positionHCS", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("uv.z", "z")]
    [InlineData("uv.xg", "xg")]
    [InlineData("uv.xyxyx", "xyxyx")]
    public void 成り立たないスウィズルを報告する(string expression, string member)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { return " + expression + ".x; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");

        Assert.Contains(member, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("uv.xy")]
    [InlineData("uv.yx")]
    [InlineData("uv.rg")]
    [InlineData("uv.xxxx")]
    public void 成り立つスウィズルは報告しない(string expression)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { return " + expression + ".x; }"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    [Fact]
    public void スカラーの複製は報告しない()
    {
        // float は 1 成分だが、.x も .xxxx も成り立つ。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag(float u : TEXCOORD0) : SV_Target { return u.xxxx.x; }"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    [Fact]
    public void 条件付きのメンバーはHL0312では報告しない()
    {
        // メンバー自体は存在する。条件の食い違いは HL0313 の担当である。
        ImmutableArray<Diagnostic> diagnostics = Analyze(ConditionalMemberShader(
            "    return IN.debugColor;"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
        Assert.True(Has(diagnostics, "HL0313"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0342: out の引数に書き戻せない
    // ------------------------------------------------------------------

    [Fact]
    public void outの引数に書き戻せない式を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "void split(float2 uv, out float3 rgb) { rgb = float3(uv, 0); }",
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target",
            "{",
            "    split(uv, float3(0, 0, 0));",
            "    return 0;",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0342");

        Assert.Contains("split", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("out", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void outの引数に変数を渡すのは報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "void split(float2 uv, out float3 rgb) { rgb = float3(uv, 0); }",
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target",
            "{",
            "    float3 got;",
            "    split(uv, got);",
            "    return got.x;",
            "}"));

        Assert.False(Has(diagnostics, "HL0342"), Describe(diagnostics));
        Assert.False(Has(diagnostics, "HL0340"), Describe(diagnostics));
    }

    [Fact]
    public void outの引数は成分を減らす向きも許さない()
    {
        // 書き戻す先が足りない。呼ぶ側の変数は float3 でなければならない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "void split(float2 uv, out float3 rgb) { rgb = float3(uv, 0); }",
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target",
            "{",
            "    float2 small;",
            "    split(uv, small);",
            "    return small.x;",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0340");

        Assert.Contains("float2", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("float3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ベクトルを行列の仮引数へ渡すのを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 shade(float3x3 m) { return m._m00; }",
            "half4 frag(float3 v : TEXCOORD0) : SV_Target { return shade(v); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0340");

        Assert.Contains("float3x3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // SL0004: 条件を追えなかった箇所
    // ------------------------------------------------------------------

    /// <summary>条件を追えない箇所があるシェーダー。</summary>
    /// <remarks>
    /// <para>
    /// 同じ場所が構成によって別の形に解釈される。
    /// <c>if</c> の本体が、一方ではブロック、もう一方では文になり、対応が取れない。
    /// </para>
    /// <para>
    /// 定義が 5 通りあるので、この文は定義ごとに複製されない (巻き上げの対象外)。
    /// <c>if</c> 文自身のトークンは構成で変わらないので、
    /// 選択肢として分ける単位も見つからない (<c>ConditionalMerge.AlignOrChoose</c>)。
    /// </para>
    /// </remarks>
    private const string UntrackableSource =
        "#pragma multi_compile _ _MODE_A _MODE_B _MODE_C _MODE_D\n"
        + "#ifdef _MODE_A\n"
        + "#define BODY { v = 1; }\n"
        + "#elif defined(_MODE_B)\n"
        + "#define BODY v = 2;\n"
        + "#elif defined(_MODE_C)\n"
        + "#define BODY v = 3;\n"
        + "#elif defined(_MODE_D)\n"
        + "#define BODY v = 4;\n"
        + "#else\n"
        + "#define BODY v = 5;\n"
        + "#endif\n"
        + "half4 frag() : SV_Target\n"
        + "{\n"
        + "    half v = 0;\n"
        + "    if (v > 0)\n"
        + "        BODY\n"
        + "    return v;\n"
        + "}";

    [Fact]
    public void 条件を追えなかった箇所を報告する()
    {
        // 報告を見送ったことを、伝えないままにしてはならない。
        // 指摘が出ないことを「問題が無い」と受け取られる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader([.. UntrackableSource.Split('\n')]));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL0004");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void 条件を追えているときはSL0004を報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(ConditionalMemberShader(
            "    return IN.uv.x;"));

        Assert.False(Has(diagnostics, "SL0004"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0350 / HL0351: 代入・初期化・return の型
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("float3 a = uv;", "float3")]
    [InlineData("float3 d; d = uv;", "float3")]
    [InlineData("float3x3 m = uv;", "float3x3")]
    public void 成分が足りない代入を報告する(string body, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { " + body + " return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0350");

        Assert.Contains(expected, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("float2", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("float2 b = float3(1, 2, 3);")]  // 切り捨ては通る
    [InlineData("float3 c = 1;")]                // スカラーは複製される
    [InlineData("float3 d; d.xy = uv;")]         // 代入先はスウィズルで float2
    public void 成り立つ代入は報告しない(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { " + body + " return 0; }"));

        Assert.False(Has(diagnostics, "HL0350"), Describe(diagnostics));
    }

    [Theory]
    [InlineData("float3 widen(float2 uv) { return uv; }", "float2")]
    [InlineData("void bad() { return 1; }", "値を返しています")]
    [InlineData("float3 empty() { return; }", "値を返していません")]
    public void 戻り値と合わないreturnを報告する(string declaration, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            declaration,
            "half4 frag() : SV_Target { return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0351");

        Assert.Contains(expected, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("float2 narrow(float3 v) { return v; }")]  // 切り捨ては通る
    [InlineData("float3 splat() { return 1; }")]           // スカラーは複製される
    [InlineData("void nothing() { return; }")]
    public void 成り立つreturnは報告しない(string declaration)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            declaration,
            "half4 frag() : SV_Target { return 0; }"));

        Assert.False(Has(diagnostics, "HL0351"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0353: 構成によっては成分が切り捨てられる
    // ------------------------------------------------------------------

    /// <summary>条件ごとに違う型で宣言した名前を返す関数。</summary>
    /// <param name="returnType">関数の戻り値の型。</param>
    /// <param name="secondType">2 つ目の宣言の型。</param>
    /// <returns>シェーダーのソース。</returns>
    private static string VaryingTypeShader(string returnType, string secondType)
        => Shader(
            returnType + " Varying()",
            "{",
            "    #pragma multi_compile _A _B",
            "    #if defined(_A)",
            "    float a = 1.0;",
            "    #elif defined(_B)",
            "    " + secondType + " a = (" + secondType + ")1.0;",
            "    #endif",
            "    return a;",
            "}",
            "half4 frag() : SV_Target { return 0; }");

    /// <summary>
    /// 代入先の型が構成によって変わる代入も、構成ごとに検査することを検証する。
    /// </summary>
    /// <remarks>
    /// 以前は代入先の型が決まらないと、代入の検査そのものをやめていた。
    /// <c>_A</c> と <c>_D</c> の構成では float4 を float へ、<c>_B</c> と <c>_D</c> の構成では float3 へ代入しており、
    /// どちらでも成分が落ちるのに何も報告しなかった。
    /// </remarks>
    [Fact]
    public void 代入先の型が構成で変わる代入を構成ごとに検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float ReturnFloat()",
            "{",
            "    #pragma multi_compile _A _B",
            "    #pragma multi_compile _C _D",
            "    #if defined(_A)",
            "    float a = 1.0;",
            "    #elif defined(_B)",
            "    float3 a = float3(1.0, 2.0, 3.0);",
            "    #endif",
            "    #if defined(_C)",
            "    a = 2;",
            "    #elif defined(_D)",
            "    a = float4(1, 1, 1, 1);",
            "    #endif",
            "    return a.x;",
            "}",
            "half4 frag() : SV_Target { return ReturnFloat(); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0353");

        Assert.Contains("float4", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("_D", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 代入先の型が構成で変わっても渡せない構成を報告する()
    {
        // _B のとき a は float3。float2 は float3 へ渡せない (広げる変換は無い)。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float ReturnFloat()",
            "{",
            "    #pragma multi_compile _A _B",
            "    #if defined(_A)",
            "    float a = 1.0;",
            "    #elif defined(_B)",
            "    float3 a = float3(1.0, 2.0, 3.0);",
            "    #endif",
            "    a = float2(1, 1);",
            "    return a.x;",
            "}",
            "half4 frag() : SV_Target { return ReturnFloat(); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0350");

        Assert.Contains("_B", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("float3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 構成によって成分が落ちる受け渡しを報告する()
    {
        // _A のとき a は float、_B のとき float3。float を返す関数なので _B でだけ落ちる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(VaryingTypeShader("float", "float3"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0353");

        Assert.Contains("_B のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("!_A", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'a' は float3 です", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("戻り値の float", diagnostic.GetMessage(), StringComparison.Ordinal);

        // 通るコードなので警告にとどめる。エラーにすると意図した切り捨てがビルドできなくなる。
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void どの構成でも成分が落ちないなら報告しない()
    {
        // float と float の書き分けでは、どちらの構成でも落ちるものが無い。
        ImmutableArray<Diagnostic> diagnostics = Analyze(VaryingTypeShader("float", "float"));

        Assert.False(Has(diagnostics, "HL0353"), Describe(diagnostics));
    }

    [Fact]
    public void 条件によらない切り捨ては報告しない()
    {
        // 意図して書く場面が多い。どこでも報告すると、意図した切り捨てに埋もれて読まれなくなる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float Narrow(float3 v) { return v; }",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.False(Has(diagnostics, "HL0353"), Describe(diagnostics));
    }

    [Fact]
    public void 構成によってスカラーを渡していても報告しない()
    {
        // スカラーは全成分へ複製される。失われる値は無い。
        ImmutableArray<Diagnostic> diagnostics = Analyze(VaryingTypeShader("float3", "float"));

        Assert.False(Has(diagnostics, "HL0353"), Describe(diagnostics));
    }

    [Fact]
    public void 構成によって渡せない戻り値を報告する()
    {
        // _B のとき float2 を float3 へ渡す。成分が増える向きなのでコンパイルに失敗する。
        // 成分が落ちるだけの HL0353 ではなく、HL0351 の領分である。
        ImmutableArray<Diagnostic> diagnostics = Analyze(VaryingTypeShader("float3", "float2"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0351");

        Assert.Contains("float2 を返しています", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("_B のとき、この式は float2 です。", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("!_A", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

        // 渡せない構成は、値が落ちるだけの構成より重い。警告の陰に隠してはならない。
        Assert.False(Has(diagnostics, "HL0353"), Describe(diagnostics));
    }

    [Fact]
    public void 構成によって渡せない代入を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float3 Assign()",
            "{",
            "    #pragma multi_compile _A _B",
            "    #if defined(_A)",
            "    float3 a = float3(1.0, 2.0, 3.0);",
            "    #elif defined(_B)",
            "    float2 a = float2(1.0, 2.0);",
            "    #endif",
            "    float3 result = float3(0.0, 0.0, 0.0);",
            "    result = a;",
            "    return result;",
            "}",
            "half4 frag() : SV_Target { return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0350");

        Assert.Contains("float3 に float2 は入りません", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("_B のとき、この式は float2 です。", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("!_A", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 構成によらない指摘には構成の注記を添えない()
    {
        // 注記は「どの構成でそうなるか」を伝えるためのものである。
        // 構成が 1 つしか無いときに添えると、読み手に無い区別を探させる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float3 widen(float2 uv) { return uv; }",
            "half4 frag() : SV_Target { return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0351");

        Assert.DoesNotContain("のとき、この式は", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.EndsWith("float2 を返しています。", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void dynamic_branchで宣言したシンボルも宣言として数える()
    {
        // #pragma dynamic_branch はバリアントを作らないが、シンボルを宣言する点は同じである。
        // 数えないと、条件で使っているだけで「宣言されていない」と言うことになる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma dynamic_branch_local_fragment _ _HDR_OVERLAY",
            "#ifdef _HDR_OVERLAY",
            "float _Overlay;",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.False(Has(diagnostics, "HL0330"), Describe(diagnostics));
    }

    [Fact]
    public void 実行時の分岐で使っているシンボルは未使用としない()
    {
        // dynamic_branch のシンボルは #ifdef ではなく if の条件として書かれる。
        // 条件だけを数えると「宣言したのに使っていない」と誤って言うことになる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma dynamic_branch_local_fragment _ _HDR_OVERLAY",
            "half4 frag() : SV_Target { if (_HDR_OVERLAY) { return 1; } return 0; }"));

        Assert.False(Has(diagnostics, "HL0331"), Describe(diagnostics));
    }

    [Fact]
    public void どこにも現れないシンボルは未使用と報告する()
    {
        // #pragma の行に名前が並んでいることを「使用」と数えてはならない。
        // 数えると、どのシンボルも使われていることになり、この規則が死ぬ。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma dynamic_branch_local_fragment _ _HDR_OVERLAY",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.True(Has(diagnostics, "HL0331"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0360 / HL0352 / HL0310: 制御フロー・初期化・未宣言
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("float3 f(float x) { if (x > 0) { return 1; } }")]
    [InlineData("float3 f() { }")]

    // discard はそのピクセルの処理を終わらせるが、
    // コンパイラは値を返す関数に return を求める (Unity 6 / DX11 で実測)。
    [InlineData("float3 f() { discard; }")]
    public void 値を返さずに終わる経路を報告する(string declaration)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            declaration,
            "half4 frag() : SV_Target { return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0360");

        Assert.Contains("float3", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("float3 f(float x) { if (x > 0) { return 1; } else { return 2; } }")]
    [InlineData("float3 f(float x) { if (x > 0) { return 1; } return 2; }")]
    [InlineData("float3 f(float x) { switch ((int)x) { default: return 1; } }")]
    [InlineData("void f() { }")]                        // 値を返さない関数

    // ループの中だけで返す形。コンパイラは境界が定数のループを展開するため、
    // これを誤りとしない。展開できるかをここで判定することはできないので報告しない。
    [InlineData("float3 f() { for (int i = 0; i < 4; i++) { return 1; } }")]
    [InlineData("float3 f(float x) { while (x > 0) { return 1; } }")]

    // 抜けないループ。コンパイラはこれを誤りとするが、
    // 上の形と区別が付かないので見落とす側に倒している。
    [InlineData("float3 f() { while (true) { } }")]
    public void 必ず返る関数は報告しない(string declaration)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            declaration,
            "half4 frag() : SV_Target { return 0; }"));

        Assert.False(Has(diagnostics, "HL0360"), Describe(diagnostics));
    }

    [Theory]
    [InlineData("float3 a = {1, 2};", 2, 3)]
    [InlineData("float3 a = {1, 2, 3, 4};", 4, 3)]
    [InlineData("float2 a[2] = {1, 2, 3};", 3, 4)]
    [InlineData("float2x2 a = {1, 2, 3};", 3, 4)]
    public void 波括弧の要素の個数が合わないとき報告する(string body, int written, int required)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { " + body + " return a[0].x; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0352");

        Assert.Contains($"{written} 個", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains($"{required} 個", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("float3 a = {1, 2, 3};")]
    [InlineData("float3 a = {float2(1, 2), 3};")]   // 平らにして 3 個
    [InlineData("float3 a = {{1, 2}, 3};")]         // 入れ子も平らにする
    [InlineData("float2 a[2] = {1, 2, 3, 4};")]
    public void 個数の合う波括弧は報告しない(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { " + body + " return 0; }"));

        Assert.False(Has(diagnostics, "HL0352"), Describe(diagnostics));
    }

    /// <summary>
    /// 論理積でだけ通る領域も検査することを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>;</c> が領域の外にあるため、この <c>#if</c> は 1 本の木に並べられず、
    /// 構成ごとの展開へ回る。<c>_A</c> だけ・<c>_B</c> だけの構成では、この分岐は通らない。
    /// </para>
    /// <para>
    /// 条件に書かれた論理積をそのまま 1 つの構成として作らないと、
    /// この中のコードはどの構成にも現れず、何も伝えられないまま検査から漏れる。
    /// </para>
    /// </remarks>
    [Fact]
    public void 論理積でだけ通る領域も検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "#pragma multi_compile _ _B",
            "half4 frag() : SV_Target",
            "{",
            "    float4 c;",
            "#if defined(_A) && defined(_B)",
            "    c = float2(1, 2)",
            "#else",
            "    c = 1",
            "#endif",
            "    ;",
            "    return c;",
            "}"));

        Assert.Contains(diagnostics, d => d.Id == "HL0350");
    }

    [Fact]
    public void 論理積で守られていても釣り合っていれば報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "#pragma multi_compile _ _B",
            "half4 frag() : SV_Target",
            "{",
            "    float4 c;",
            "#if defined(_A) && defined(_B)",
            "    c = float4(1, 2, 3, 4)",
            "#else",
            "    c = 1",
            "#endif",
            "    ;",
            "    return c;",
            "}"));

        Assert.False(Has(diagnostics, "HL0350"), Describe(diagnostics));
    }

    /// <summary>
    /// メンバーと要素の両方が条件付きの初期化を報告しないことを検証する。
    /// </summary>
    /// <param name="directive">条件の書き方。</param>
    /// <remarks>
    /// どの構成でも釣り合っているコードである (fxc で両方の構成がコンパイルできる)。
    /// 構成をまたいで平らに数えると食い違って見えるため、数えない。
    /// TextMesh Pro の同梱シェーダーがこの形で、誤検出になっていた。
    /// </remarks>
    [Theory]
    [InlineData("#ifdef _A")]
    [InlineData("#if _A")]
    [InlineData("#if defined(_A)")]
    public void 構成で個数が変わる初期化は報告しない(string directive)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "struct pixel_t {",
            "    float4 a;",
            directive,
            "    float4 b;",
            "#endif",
            "};",
            "half4 frag() : SV_Target",
            "{",
            "    pixel_t o = {",
            "        float4(1, 1, 1, 1),",
            directive,
            "        float4(2, 2, 2, 2),",
            "#endif",
            "    };",
            "    return o.a;",
            "}"));

        Assert.False(Has(diagnostics, "HL0352"), Describe(diagnostics));
    }

    /// <summary>
    /// 条件の付いた要素が書いた側にしか無い初期化も、構成ごとに数えて報告することを検証する。
    /// </summary>
    /// <remarks>
    /// 以前は条件の組ごとに突き合わせ、片側にしか無い条件があると判断しなかった。
    /// <c>!_A</c> の構成では 3 個しか書いていない。
    /// </remarks>
    [Fact]
    public void 要素だけが条件で増える初期化は足りない構成を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "half4 frag() : SV_Target",
            "{",
            "    float4 c = { 1, 2, 3",
            "#ifdef _A",
            "        , 4",
            "#endif",
            "    };",
            "    return c;",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0352");

        Assert.Contains("3 個", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("4 個", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("!_A のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// メンバーだけが条件で増える構造体の初期化を、増える構成で報告することを検証する。
    /// </summary>
    [Fact]
    public void メンバーだけが条件で増える構造体の初期化は足りない構成を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "struct pixel_t {",
            "    float4 a;",
            "#ifdef _A",
            "    float4 b;",
            "#endif",
            "};",
            "half4 frag() : SV_Target",
            "{",
            "    pixel_t o = { float4(1, 1, 1, 1) };",
            "    return o.a;",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0352");

        Assert.Contains("4 個書いていますが、8 個必要です", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("(_A のとき)", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 要素が条件で増えても、どの構成でも釣り合っていれば報告しないことを検証する。
    /// </summary>
    [Theory]
    [InlineData("float4 c = { 1, 2, 3", ", 4", ", 5")]   // _A では 4 個、!_A でも 4 個
    [InlineData("float4 c = { 1, 2", ", float2(3, 4)", ", 3, 4")]
    public void 分岐ごとに釣り合う初期化は報告しない(string head, string enabled, string disabled)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _A",
            "half4 frag() : SV_Target",
            "{",
            "    " + head,
            "#ifdef _A",
            "        " + enabled,
            "#else",
            "        " + disabled,
            "#endif",
            "    };",
            "    return c;",
            "}"));

        Assert.False(Has(diagnostics, "HL0352"), Describe(diagnostics));
    }

    [Theory]
    [InlineData("m._m44", "この行列は 3 行 3 列です")]
    [InlineData("m._m00_11", "混ぜて書くことはできません")]
    [InlineData("m.x", "混ぜて書くことはできません")]
    public void 行列の成分の書き方の誤りを報告する(string expression, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { float3x3 m = 0; return " + expression + "; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");

        Assert.Contains(expected, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("m._m00")]
    [InlineData("m._11")]
    [InlineData("m._m22")]
    [InlineData("m._33")]
    [InlineData("m._m00_m11")]
    public void 成り立つ行列の成分は報告しない(string expression)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { float3x3 m = 0; return " + expression + ".x; }"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    [Fact]
    public void 宣言されていない識別子を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { return undeclaredName; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0310");

        Assert.Contains("undeclaredName", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("return true ? 1 : 0;")]                    // 真偽値のリテラル
    [InlineData("float v = 1; return v;")]                   // 局所変数
    [InlineData("return _KEYWORD_ONLY;")]                    // #ifdef の中にだけある名前
    public void 宣言されている識別子は報告しない(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _ _SOME_KEYWORD",
            "#ifdef _SOME_KEYWORD",
            "float _KEYWORD_ONLY;",
            "#endif",
            "half4 frag() : SV_Target { " + body + " }"));

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    [Fact]
    public void 自分が書いたマクロの本体の誤りも報告する()
    {
        // 展開で生まれたコードでも、そのマクロをこのファイルが書いているなら利用者が直せる。
        // 位置は呼び出し位置ではなく本体を指す。そこが直す場所であり、
        // 同じマクロを何度使っても報告は 1 か所にまとまる。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#define HELPER_DECL half4 helper() { return undeclaredInMacro; }",
            "HELPER_DECL",
            "half4 frag() : SV_Target { return helper(); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0310");

        Assert.Contains("undeclaredInMacro", diagnostic.GetMessage(), StringComparison.Ordinal);

        // #define の行 (Shader が足す 1 行目は #include なので 9 行目)。
        Assert.Equal(9, diagnostic.Location.LineSpan.Start.Line + 1);
    }

    [Fact]
    public void 同じマクロを何度使っても報告は1件にまとまる()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#define USE_MISSING (undeclaredInMacro)",
            "half4 frag() : SV_Target { return USE_MISSING + USE_MISSING + USE_MISSING; }"));

        Assert.Single(diagnostics, d => d.Id == "HL0310");
    }

    [Fact]
    public void ヘッダのマクロの本体は報告しない()
    {
        // 取り込んだヘッダの中身は利用者に直しようがない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("half4 frag() : SV_Target { return HEADER_MACRO; }"),
            "#define HEADER_MACRO (undeclaredInHeaderMacro)\n");

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    /// <summary>
    /// 名前空間の修飾を、宣言されていない識別子として報告しないことを検証する。
    /// </summary>
    /// <remarks>
    /// <c>UnifiedRT::TraceRay(...)</c> の構文の形はメンバーアクセスと同じなので、
    /// 左側の <c>UnifiedRT</c> は識別子の式として現れる。
    /// これは変数でも関数でもないため、宣言を探しても見つからない。
    /// Unity 同梱のレイトレーシング関連のヘッダがこの書き方をしている。
    /// </remarks>
    [Fact]
    public void 名前空間の修飾を未宣言として報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "namespace Helpers",
            "{",
            "    half4 shade(half3 n) { return half4(n, 1); }",
            "}",
            "half4 frag() : SV_Target { return Helpers::shade(half3(0, 0, 1)); }"));

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    /// <summary>
    /// 構造体の外でのメソッドの定義を読めることを検証する。
    /// </summary>
    /// <remarks>
    /// SRP の ThreadingEmuImpl.hlsl が <c>uint Wave::GetIndex() { return indexW; }</c> と書いていて、D3D11 でもコンパイルされる。
    /// 読めなかったため構文の誤りになり、それを取り込む STP などの .compute の検査が止まっていた。
    /// メソッドの中のフィールド (<c>indexW</c>) は、宣言されていない名前として報告しない。
    /// </remarks>
    [Fact]
    public void 構造体の外でのメソッドの定義を読む()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "namespace NS",
            "{",
            "    struct Wave { uint indexW; uint GetIndex(); };",
            "    uint Wave::GetIndex() { return indexW; }",
            "}",
            "half4 frag() : SV_Target { NS::Wave w; w.indexW = 1; return w.GetIndex() + GetIndexTypo(); }"));

        Assert.False(Has(diagnostics, "HL0001"), Describe(diagnostics));
        Assert.False(Has(diagnostics, "SL0002"), Describe(diagnostics));
        Assert.Single(diagnostics, d => d.Id == "HL0310" && d.GetMessage().Contains("GetIndexTypo", StringComparison.Ordinal));
    }

    [Fact]
    public void レイトレーシングの組み込み関数と定数は未宣言と報告しない()
    {
        // Unity 同梱の HWRayTracingMaterial.shader の形。DXR の関数と定数は宣言無しに使える。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "struct Payload { uint id; uint prim; float dist; bool front; float3 origin; };",
            "struct Attributes { float2 barycentrics; };",
            "[shader(\"closesthit\")]",
            "void ClosestHit(inout Payload payload : SV_RayPayload, Attributes attribs : SV_IntersectionAttributes)",
            "{",
            "    payload.id = InstanceID();",
            "    payload.prim = PrimitiveIndex();",
            "    payload.dist = RayTCurrent();",
            "    payload.front = (HitKind() == HIT_KIND_TRIANGLE_FRONT_FACE);",
            "    payload.origin = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();",
            "    uint flags = RAY_FLAG_CULL_BACK_FACING_TRIANGLES | RayFlags();",
            "}"));

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    [Fact]
    public void 別の関数の局所変数だけにある名前は報告する()
    {
        // helper の scale は frag からは見えない。fxc は X3004 (undeclared identifier) を出す。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float helper(float x) { float scale = 2; return x * scale; }",
            "half4 frag() : SV_Target { return helper(1) * scale; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0310");
        Assert.Contains("scale", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 局所変数の名前は、同じ位置が現れるどれかの木で見えれば報告しないことを検証する。
    /// </summary>
    /// <remarks>
    /// 使う側だけが既定の木へ並べられ、宣言の側がバリアントの木にしか無い形。
    /// Unity 同梱の HDRP のシェーダー (DepthOfFieldGather.compute / VolumetricCloudsCombine.shader /
    /// VolumeVoxelization.compute) がこの形で、既定の木だけで判断すると誤検出になっていた。
    /// </remarks>
    [Theory]

    // 関数の頭を #if で切り替え、仮引数を使う側も同じ条件の中にある。
    [InlineData(
        "#if USE_TILES",
        "half4 frag(uint tileId : TEXCOORD0) : SV_Target",
        "#else",
        "half4 frag() : SV_Target",
        "#endif",
        "{",
        "    half4 c = 0;",
        "#if USE_TILES",
        "    c.x = tileId;",
        "#endif",
        "    return c;",
        "}")]

    // 宣言の側はインクルードガードの中で定義したマクロ (FLT_MIN) を使っていて並べられず、
    // バリアントの木にだけ載る。使う側は並べられて、既定の木にも載る。
    [InlineData(
        "half4 frag() : SV_Target",
        "{",
        "    half4 c = 0;",
        "#ifdef USE_TILES",
        "    float waterDistance = FLT_MIN;",
        "#endif",
        "    for (uint i = 0; i < 2; i++)",
        "    {",
        "#if USE_TILES",
        "        if (i < waterDistance) { c.x += 1; }",
        "#endif",
        "    }",
        "    return c;",
        "}")]
    public void 局所変数はどれかの構成の木で見えれば報告しない(params string[] function)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(["#pragma multi_compile _ USE_TILES", .. function]),
            "#ifndef CORE_INCLUDED\n#define CORE_INCLUDED\n#define FLT_MIN 1.175494351e-38\n#endif\n");

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    [Fact]
    public void ループの初期化の名前をループの後で使っても未宣言と報告しない()
    {
        // Unity で通ることを確かめた形。初期化の変数はループの後でも見える。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float ReturnFloat()",
            "{",
            "    int sum = 0;",
            "    for (int i = 0; i < 10; i++) {",
            "        sum += i;",
            "    }",
            "    return i;",
            "}",
            "half4 frag() : SV_Target { return ReturnFloat(); }"));

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    /// <summary>
    /// 戻り値の型がヘッダのマクロでも、利用者が書いた関数を検査することを検証する。
    /// </summary>
    /// <remarks>
    /// <c>HLSLSupport.cginc</c> は D3D11 で <c>#define fixed4 half4</c> と定義する。
    /// 先頭のトークンだけで判定していたため、<c>UnityCG.cginc</c> を取り込んだシェーダーの
    /// <c>fixed4 frag(...)</c> がまるごとヘッダのコードとして扱われ、どのルールにも検査されていなかった。
    /// </remarks>
    [Fact]
    public void 戻り値の型がヘッダのマクロでも関数を検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("fixed4 frag() : SV_Target { return undeclaredName; }"),
            urpCoreContent: "#define fixed4 half4\n");

        Assert.True(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    /// <summary>
    /// マクロの実引数に書いたコードを検査することを検証する。
    /// </summary>
    /// <remarks>
    /// 実引数は利用者が呼び出し位置に書いたコードである。展開の結果としか分からなかったため、
    /// <c>SAMPLE_TEXTURE2D(tex, smp, uvTypo)</c> のような誤りはどのルールにも検査されていなかった。
    /// 報告は実引数を書いた位置に出す。
    /// </remarks>
    [Theory]
    [InlineData("float2 a = ID(undeclaredC);", "HL0310", "undeclaredC")]           // このファイルのマクロ
    [InlineData("float2 a = HEADER_ID(undeclaredD);", "HL0310", "undeclaredD")]    // ヘッダのマクロ
    [InlineData("float a = HEADER_ID(uv.z);", "HL0312", "uv.z")]
    [InlineData("float a = OUTER(uv.z);", "HL0312", "uv.z")]                        // 入れ子のマクロ
    public void マクロの実引数に書いたコードを検査する(string statement, string id, string reportedText)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(
                "#define ID(x) x",
                "half4 frag(float2 uv : TEXCOORD0) : SV_Target { " + statement + " return 0; }"),
            urpCoreContent: "#define HEADER_ID(x) (x)\n#define OUTER(x) HEADER_ID(x)\n");

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == id);
        Assert.Equal(reportedText, diagnostic.Location.Source.ToString(diagnostic.Location.Span));
    }

    /// <summary>
    /// マクロの本体が作った部分は、実引数と組み合わさっていても報告しないことを検証する。
    /// </summary>
    /// <remarks>
    /// <c>UNITY_SETUP_INSTANCE_ID(v)</c> の <c>v.instanceID</c> は、<c>v</c> は利用者が書いたものだが
    /// <c>.instanceID</c> はヘッダのマクロの本体である。利用者に直しようがない。
    /// </remarks>
    [Fact]
    public void マクロの本体が作ったメンバー参照は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader(
                "struct A { float4 p; };",
                "half4 frag() : SV_Target { A a = (A)0; SETUP_ID(a); return 0; }"),
            urpCoreContent: "#define SETUP_ID(v) uint id = v.instanceID;\n");

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    /// <summary>
    /// ヘッダのマクロがまるごと作った宣言は、利用者のコードとして扱わないことを検証する。
    /// </summary>
    /// <remarks>実引数も展開の結果になるので、利用者が書いたトークンは 1 つも残らない。</remarks>
    [Fact]
    public void ヘッダのマクロがまるごと作った宣言は検査しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("MAKE_FUNC(Broken)", "half4 frag() : SV_Target { return Broken(); }"),
            urpCoreContent: "#define MAKE_FUNC(name) half4 name() { return undeclaredInHeader; }\n");

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // ある構成でだけ壊れるコードを、別の構成の情報で隠さない
    // ------------------------------------------------------------------

    /// <summary>
    /// ある構成でだけ宣言が無い名前を、その構成を添えて報告することを検証する。
    /// </summary>
    /// <remarks>
    /// <c>_B</c> のときだけ宣言する名前を常に使えば、<c>!_B</c> の構成で壊れる。
    /// <c>_B</c> の木では見えるので、「どれかの木で見えれば誤りではない」とすると隠れていた。
    /// </remarks>
    [Theory]
    [InlineData("#ifdef _B\n#define EXTRA 1\n#endif", "half4 frag() : SV_Target { return EXTRA; }", "EXTRA")]
    [InlineData("#ifdef _B\n#define EXTRA_FN(x) (x * 2)\n#endif", "half4 frag() : SV_Target { return EXTRA_FN(1); }", "EXTRA_FN")]
    [InlineData("", "half4 frag() : SV_Target {\n#ifdef _B\nfloat4 extra = 1;\n#endif\nreturn extra; }", "extra")]
    public void ある構成でだけ宣言が無い名前を報告する(string declarations, string frag, string name)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "#pragma shader_feature_local _B",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            declarations,
            frag));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0310");
        Assert.Contains(name, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("!_B のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 別の Pass が取り込んだヘッダの宣言を、取り込んでいない Pass では見えないものとすることを検証する。
    /// </summary>
    [Fact]
    public void 別のPassが取り込んだヘッダの宣言は見えない()
    {
        string source = """
            Shader "Test/Hlsl"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        half4 frag() : SV_Target { return _FromHeader + HeaderFunc(); }
                        ENDHLSL
                    }
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        half4 frag() : SV_Target { return _FromHeader + HeaderFunc(); }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(
            source,
            urpCoreContent: "float4 _FromHeader;\nfloat4 HeaderFunc() { return 1; }\n");

        List<Diagnostic> undeclared = [.. diagnostics.Where(d => d.Id == "HL0310")];
        Assert.Equal(2, undeclared.Count);
        // 取り込んでいない 2 つ目の Pass の frag だけを報告する (行は 0 始まり)。
        Assert.All(undeclared, d => Assert.Equal(20, d.Location.LineSpan.Start.Line));
    }

    /// <summary>
    /// ある構成でだけ引数が合わない呼び出しを報告することを検証する。
    /// </summary>
    /// <remarks>
    /// 存在しうる宣言のどれか 1 つが合えば通すと、<c>_B</c> の宣言にだけ合う呼び出しを <c>!_B</c> でも通してしまう。
    /// </remarks>
    [Theory]
    [InlineData(
        "float4 F(float a, float b) { return a; }", "float4 F(float a) { return a; }", "F(1, 2)", "HL0341", "!_B のとき")]
    [InlineData(
        "float4 F(float3 a) { return 1; }", "float4 F(float4 a) { return 1; }", "F(v)", "HL0340", "!_B のとき")]
    [InlineData(
        "void F(out float a) { a = 1; }", "void F(float a) { }", "F(1)", "HL0342", "_B のとき")]
    public void ある構成でだけ引数が合わない呼び出しを報告する(
        string whenB, string otherwise, string call, string id, string condition)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "#pragma shader_feature_local _B",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "#ifdef _B",
            whenB,
            "#else",
            otherwise,
            "#endif",
            "half4 frag() : SV_Target { float3 v = 1; " + call + "; return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == id);
        Assert.Contains(condition, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ある構成でだけ値を返さない関数を報告することを検証する。
    /// </summary>
    [Theory]
    [InlineData("#ifdef _B\n    return 1;\n#endif")]
    [InlineData("#ifdef _B\n    return 1;\n#else\n    if (x > 0) return 2;\n#endif")]
    public void ある構成でだけ値を返さない関数を報告する(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "#pragma shader_feature_local _B",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "float4 F(float x)",
            "{",
            body,
            "}",
            "half4 frag() : SV_Target { return F(1); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0360");
        Assert.Contains("!_B のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// どの構成でも返す、分岐ごとに書き分けた関数は報告しないことを検証する。
    /// </summary>
    [Fact]
    public void 分岐ごとに返す関数は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "#pragma shader_feature_local _B",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "#ifdef _B",
            "float4 F() { return 1; }",
            "#else",
            "float4 F() { return 2; }",
            "#endif",
            "float4 G() {",
            "#ifdef _B",
            "    return 1;",
            "#else",
            "    return 2;",
            "#endif",
            "}",
            "half4 frag() : SV_Target { return F() + G(); }"));

        Assert.False(Has(diagnostics, "HL0360"), Describe(diagnostics));
    }

    /// <summary>
    /// ある構成でだけ入口の関数が無いことを報告することを検証する。
    /// </summary>
    [Fact]
    public void ある構成でだけ入口の関数が無いことを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "#pragma shader_feature_local _B",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "#ifdef _B",
            "half4 frag() : SV_Target { return 1; }",
            "#endif"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0301");
        Assert.Contains("!_B のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 同時には存在しない宣言どうしの同じレジスタは報告しないことを検証する。
    /// </summary>
    [Fact]
    public void 同時には存在しない宣言どうしの同じレジスタは報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "#pragma shader_feature_local _B",
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "#ifdef _B",
            "Texture2D _T1 : register(t0);",
            "#else",
            "Texture2D _T2 : register(t0);",
            "#endif",
            "half4 frag() : SV_Target {",
            "#ifdef _B",
            "    return _T1.Load(int3(0,0,0));",
            "#else",
            "    return _T2.Load(int3(0,0,0));",
            "#endif",
            "}"));

        Assert.False(Has(diagnostics, "HL0211"), Describe(diagnostics));
    }

    [Fact]
    public void 依存が揃っていなければ未宣言の検査をしない()
    {
        // 取り込めなかったヘッダに宣言があるかもしれない。
        // 分からないものを根拠に「宣言されていない」と言ってはならない。
        string source = Shader("half4 frag() : SV_Target { return undeclaredName; }")
            .Replace(UrpCorePath, "Packages/does.not.exist/Missing.hlsl", StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0343 / HL0211 / HL0370: コンストラクタ・レジスタ・添字
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("float3 a = float3(1, 2);", "float3")]
    [InlineData("float2 a = float2(1, 2, 3);", "float2")]
    [InlineData("float3 a = float3(uv);", "float3")]      // uv は float2
    public void コンストラクタの引数の個数が合わないとき報告する(string body, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { " + body + " return a.x; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0343");

        Assert.Contains(expected, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("float3 a = float3(1, 2, 3);")]
    [InlineData("float3 a = float3(uv, 0);")]             // 平らにして 3 個
    [InlineData("float3 a = float3(1);")]                 // スカラーは複製される
    [InlineData("float2 a = float2(float4(1,2,3,4));")]   // 1 つだけなら切り捨てが許される
    public void 成り立つコンストラクタは報告しない(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { " + body + " return a.x; }"));

        Assert.False(Has(diagnostics, "HL0343"), Describe(diagnostics));
    }

    [Fact]
    public void 同じレジスタを2つの宣言が使っているとき報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "Texture2D _A : register(t0);",
            "Texture2D _B : register(t0);",
            "half4 frag() : SV_Target { return _A.Load(int3(0,0,0)) + _B.Load(int3(0,0,0)); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0211");

        Assert.Contains("t0", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("_A", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 使われていないリソースとの同じレジスタは報告しない()
    {
        // コンパイラは使われていないリソースを取り除いてから割り当てる。
        // HDRP の CopyStencilBuffer.shader は 2 つの UAV を u1 に宣言し、Pass ごとに一方だけを使う。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "Texture2D _A : register(t0);",
            "Texture2D _B : register(t0);",
            "half4 frag() : SV_Target { return _A.Load(int3(0,0,0)); }"));

        Assert.False(Has(diagnostics, "HL0211"), Describe(diagnostics));
    }

    [Fact]
    public void 種類の違うレジスタは重複としない()
    {
        // b0 と t0 と s0 は別の空間である。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "Texture2D _A : register(t0);",
            "SamplerState _S : register(s0);",
            "Texture2D _B : register(t1);",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.False(Has(diagnostics, "HL0211"), Describe(diagnostics));
    }

    [Fact]
    public void 配列の範囲を超えた添字を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { float arr[3] = {1, 2, 3}; return arr[5]; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0370");

        Assert.Contains("arr", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 別々の関数に長さの違う同じ名前の配列があっても、その位置で指している配列の長さで判定することを検証する。
    /// </summary>
    /// <remarks>
    /// 以前はシェーダー全体で「名前 → 長さ」の表を作っており、
    /// 同じ名前が違う長さで現れると、その名前をすべて見送っていた。
    /// </remarks>
    [Fact]
    public void 別の関数の同じ名前の配列の長さで判定しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float shortOne() { float arr[2] = {1, 2}; return arr[3]; }",
            "half4 frag() : SV_Target { float arr[8] = {1, 2, 3, 4, 5, 6, 7, 8}; return arr[3] + shortOne(); }"));

        // shortOne の arr[3] だけが範囲を超える。frag の arr は 8 個ある。
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0370");
        Assert.Contains("2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return arr[2];")]                 // 最後の要素
    [InlineData("return arr[(int)uv.x];")]         // 実行時に決まる添字
    public void 範囲に収まる添字は報告しない(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag(float2 uv : TEXCOORD0) : SV_Target { float arr[3] = {1, 2, 3}; " + body + " }"));

        Assert.False(Has(diagnostics, "HL0370"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0344 / HL0352: 配列と構造体
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("float wrong[2]; fill(wrong);", "要素 2 個")]
    [InlineData("float2 wrong[4]; fill(wrong);", "float2[4]")]
    public void 形の合わない配列の引数を報告する(string body, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "void fill(out float dst[4]) { dst[0] = 0; }",
            "half4 frag() : SV_Target { " + body + " return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0344");

        Assert.Contains(expected, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// 別々の関数に長さの違う同じ名前の配列があっても、渡している配列の長さで判定することを検証する。
    /// </summary>
    /// <remarks>
    /// 以前はシェーダー全体で「名前 → 長さ」の表を作っており、
    /// 同じ名前が違う長さで現れると、その名前をすべて見送っていた。
    /// </remarks>
    [Fact]
    public void 別の関数の同じ名前の配列と取り違えない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "void fill(out float dst[4]) { dst[0] = 0; }",
            "half4 wrong() { float arr[2]; fill(arr); return 0; }",
            "half4 frag() : SV_Target { float arr[4]; fill(arr); return arr[0] + wrong(); }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0344");
        Assert.Contains("要素 2 個", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 構造体の配列のフィールドを、配列の仮引数へ渡せることを検証する。
    /// </summary>
    /// <remarks>
    /// HDRP の HairMultipleScatteringPreIntegration.compute は <c>GetAlphaScalesFromAlpha(data.alpha, data.sinAlpha, ...)</c>
    /// のように、構造体の配列のフィールドを渡す。名前でない実引数をすべて「配列ではない」としていた。
    /// </remarks>
    [Theory]
    [InlineData("float v[3];", false)]
    [InlineData("float v[2];", true)]
    [InlineData("float v;", true)]
    public void 構造体の配列のフィールドは宣言の形で判断する(string field, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "struct Data { " + field + " float other; };",
            "void fill(inout float dst[3]) { dst[0] = 0; }",
            "half4 frag() : SV_Target { Data d = (Data)0; float arr[3]; fill(d.v); fill(arr); return 0; }"));

        Assert.True(Has(diagnostics, "HL0344") == reported, Describe(diagnostics));
    }

    /// <summary>
    /// 多次元配列の添字を、次元の数だけ重ねて要素の型にすることを検証する。
    /// </summary>
    /// <remarks>
    /// HDRP の FourierTransform.compute は <c>groupshared float3 pingPongArray[4][N]</c> の
    /// <c>pingPongArray[0][x].xyz</c> を読む。添字 1 つで要素、2 つめはベクトルの成分と読んで <c>float</c> にしていた。
    /// </remarks>
    [Theory]
    [InlineData("a[0][1].xyz", false)]
    [InlineData("a[0][1].w", true)]
    [InlineData("s.m[1][2].xyz", false)]
    public void 多次元配列は次元の数だけ添字を重ねて要素になる(string access, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "struct S { float3 m[2][4]; };",
            "static float3 a[4][8];",
            "half4 frag() : SV_Target { S s = (S)0; float3 v = " + access + "; return half4(v, 1); }"));

        Assert.True(Has(diagnostics, "HL0312") == reported, Describe(diagnostics));
    }

    /// <summary>
    /// 型ごとに多重定義された関数の戻り値を、実引数の型が完全に一致する候補から決めることを検証する。
    /// </summary>
    /// <remarks>
    /// Unity のヘッダは <c>Max3</c> や <c>LinearToSRGB</c> を <c>float</c> / <c>float3</c> などの型ごとに多重定義している。
    /// 候補の戻り値が割れていると型を決めず、その式の検査を見送っていた。
    /// </remarks>
    [Theory]
    [InlineData("Max3(c, c, c).w", true)]
    [InlineData("Max3(c, c, c).z", false)]
    [InlineData("Max3(c.x, c.y, c.z).y", true)]
    public void 多重定義は実引数の型が完全に一致する候補で決める(string expression, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float Max3(float a, float b, float c) { return max(a, max(b, c)); }",
            "float3 Max3(float3 a, float3 b, float3 c) { return max(a, max(b, c)); }",
            "half4 frag() : SV_Target { float3 c = 1; float v = " + expression + "; return v; }"));

        Assert.True(Has(diagnostics, "HL0312") == reported, Describe(diagnostics));
    }

    /// <summary>
    /// 完全に一致する候補が無いとき、成分を切り捨てる候補を除いて戻り値の型を決めることを検証する。
    /// </summary>
    /// <remarks>
    /// SRP の <c>LinearToSRGB</c> は <c>real</c> から <c>real4</c> まで多重定義してあり、D3D11 では <c>real</c> は <c>float</c> になる。
    /// <c>half4</c> を渡すと <c>float4</c> の候補は基底型を変えるだけで済み、ほかの候補は成分を切り捨てる。
    /// </remarks>
    [Theory]
    [InlineData("half4 c = 1;", "ToSRGB(c).w", false)]      // float4 の候補
    [InlineData("half3 c = 1;", "ToSRGB(c).w", true)]       // float3 の候補。float4 へは渡せない
    [InlineData("half c = 1;", "ToSRGB(c).y", false)]       // 複製だけの候補どうしは比べない (型を決めない)
    public void 多重定義は成分を切り捨てる候補を除いて決める(string declaration, string expression, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float ToSRGB(float c) { return c; }",
            "float3 ToSRGB(float3 c) { return c; }",
            "float4 ToSRGB(float4 c) { return c; }",
            "half4 frag() : SV_Target { " + declaration + " float v = " + expression + "; return v; }"));

        Assert.True(Has(diagnostics, "HL0312") == reported, Describe(diagnostics));
    }

    /// <summary>
    /// typedef の別名を、元の型として読むことを検証する。
    /// </summary>
    /// <remarks>typedef は Unity でも使える。別名は分からない型になり、その式の検査を見送っていた。</remarks>
    [Theory]
    [InlineData("V2 b = 0;", "b.y", false)]
    [InlineData("V2 b = 0;", "b.z", true)]
    [InlineData("V2 b = V2(1, 2);", "b.z", true)]
    [InlineData("V2 b = (V2)1;", "b.z", true)]
    [InlineData("W2 b = 0;", "b.z", true)]             // 別名の別名
    [InlineData("float b = MakeV2().z;", "b", true)]   // 戻り値の型
    public void typedefの別名を元の型として読む(string declaration, string expression, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "typedef float2 V2;",
            "typedef V2 W2;",
            "V2 MakeV2() { return 0; }",
            "half4 frag() : SV_Target { " + declaration + " float v = " + expression + "; return v; }"));

        Assert.True(Has(diagnostics, "HL0312") == reported, Describe(diagnostics));
        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
        Assert.False(Has(diagnostics, "HL0001"), Describe(diagnostics));
    }

    [Theory]
    [InlineData("vector<float, 2> b = 0;", "b.y", false)]
    [InlineData("vector<float, 2> b = 0;", "b.z", true)]
    [InlineData("vector b = 0;", "b.w", false)]
    [InlineData("matrix<half, 2, 3> b = 0;", "b._m12", false)]
    [InlineData("matrix<half, 2, 3> b = 0;", "b._m22", true)]
    public void vectorとmatrixの書き方も型として読む(string declaration, string expression, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { " + declaration + " float v = " + expression + "; return v; }"));

        Assert.True(Has(diagnostics, "HL0312") == reported, Describe(diagnostics));
    }

    [Theory]
    [InlineData("(b = 0).y", false)]
    [InlineData("(b = 0).z", true)]
    public void 代入の式は代入先の型になる(string expression, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { float2 b; float v = " + expression + "; return v; }"));

        Assert.True(Has(diagnostics, "HL0312") == reported, Describe(diagnostics));
    }

    /// <summary>
    /// ある引数では切り捨てるが別の引数では良い候補は、除かないことを検証する。
    /// </summary>
    /// <remarks>順位の付け方によっては選ばれうるので、型を決めない。</remarks>
    [Fact]
    public void 別の引数で良い候補は切り捨てでも除かない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "float Mix(float a, half b) { return a; }",
            "float4 Mix(float4 a, float4 b) { return a; }",
            "half4 frag() : SV_Target { float4 a = 1; half b = 1; float v = Mix(a, b).y; return v; }"));

        Assert.False(Has(diagnostics, "HL0312"), Describe(diagnostics));
    }

    [Theory]
    [InlineData("(v >> 1).y", false)]
    [InlineData("(v >> 1).z", true)]
    [InlineData("(v & 1u).z", true)]
    [InlineData("(1u << v).z", true)]
    [InlineData("(f & 1u).x", false)]
    public void ビット演算とシフトは整数どうしなら型が決まる(string expression, bool reported)
    {
        // f は float なので、ビット演算の型は決めない (そもそも誤りだが、型の検査の対象ではない)。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { uint2 v = 3; float f = 1; float r = " + expression + "; return r; }"));

        Assert.True(Has(diagnostics, "HL0312") == reported, Describe(diagnostics));
    }

    [Theory]
    [InlineData("Texture2D _Tex;", "_Tex[uint2(0, 0)].w", false)]
    [InlineData("RWTexture2D<float2> _Tex;", "_Tex[uint2(0, 0)].z", true)]
    [InlineData("Texture2D<float> _Tex;", "_Tex[uint2(0, 0)].y", true)]
    public void テクスチャの添字は要素の型になる(string declaration, string expression, bool reported)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            declaration,
            "half4 frag() : SV_Target { float v = " + expression + "; return v; }"));

        Assert.True(Has(diagnostics, "HL0312") == reported, Describe(diagnostics));
    }

    [Fact]
    public void 形の合う配列の引数は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "void fill(out float dst[4]) { dst[0] = 0; }",
            "half4 frag() : SV_Target { float ok[4]; fill(ok); return ok[0]; }"));

        Assert.False(Has(diagnostics, "HL0344"), Describe(diagnostics));
    }

    [Theory]
    [InlineData("Point b = {1, 2};", 2, 3)]                 // 構造体を平らにして 3 成分
    [InlineData("float n[2][3] = {1, 2, 3};", 3, 6)]        // 多次元は次元の積
    public void 構造体と多次元配列の初期化の個数を数える(string body, int written, int required)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "struct Point { float2 uv; float w; };",
            "half4 frag() : SV_Target { " + body + " return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0352");

        Assert.Contains($"{written} 個", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains($"{required} 個", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Point a = {1, 2, 3};")]
    [InlineData("float m[2][3] = {1, 2, 3, 4, 5, 6};")]
    public void 個数の合う構造体と多次元配列は報告しない(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "struct Point { float2 uv; float w; };",
            "half4 frag() : SV_Target { " + body + " return 0; }"));

        Assert.False(Has(diagnostics, "HL0352"), Describe(diagnostics));
    }

    [Theory]
    [InlineData("float x = clip(1);")]                 // 値を返さない組み込み関数
    [InlineData("float3 x = f16tof32(uint2(1, 2));")]  // uint2 → float2 は float3 に足りない
    public void 値を返さない関数や形の合わない組み込み関数の結果を報告する(string body)
    {
        // 組み込み関数の戻り値の型が分かって初めて言えることである。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { " + body + " return x.x; }"));

        Assert.Single(diagnostics, d => d.Id == "HL0350");
    }

    [Fact]
    public void 値を捨てる呼び出しは報告しない()
    {
        // 文として書く分には正しい。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target { float s, c; sincos(1, s, c); clip(s); return c; }"));

        Assert.False(Has(diagnostics, "HL0350"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // 取り込んだヘッダの関数
    // ------------------------------------------------------------------

    [Fact]
    public void 取り込んだヘッダの関数の呼び出しも検査する()
    {
        // ヘッダの関数を対象外にしていたが、多重定義は候補の突き合わせで既に捌けている。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("half4 frag() : SV_Target { return Shade(1, 2); }"),
            urpCoreContent: "float4 Shade(float a) { return a; }");

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0341");

        Assert.Contains("Shade", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ヘッダの多重定義に合う呼び出しは報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("half4 frag() : SV_Target { return Shade(1, 2); }"),
            urpCoreContent: "float4 Shade(float a) { return a; }\n"
                            + "float4 Shade(float a, float b) { return a + b; }");

        Assert.False(Has(diagnostics, "HL0341"), Describe(diagnostics));
    }

    [Fact]
    public void ヘッダの中の呼び出しは報告しない()
    {
        // 利用者が直せない場所を指しても仕方がない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Shader("half4 frag() : SV_Target { return 0; }"),
            urpCoreContent: "float4 Shade(float a) { return a; }\n"
                            + "float4 Use() { return Shade(1, 2); }");

        Assert.False(Has(diagnostics, "HL0341"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0315: 使っている変数の宣言がその構成に無い
    // ------------------------------------------------------------------

    [Fact]
    public void 条件の外で使っている変数を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma fragment frag",
            "#pragma multi_compile _ _A",
            "#ifdef _A",
            "float4 _Tint;",
            "#endif",
            "half4 frag() : SV_Target { return _Tint; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0315");
        Assert.Contains("_Tint", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("!_A のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 局所変数に隠されているのは、その範囲の中だけであることを検証する。
    /// </summary>
    /// <remarks>
    /// 以前は、どこかの関数で同じ名前の局所変数を宣言していれば、
    /// 別の関数でグローバル変数を使っている箇所まで見送っていた。
    /// </remarks>
    [Fact]
    public void 別の関数の局所変数はグローバル変数を隠さない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma fragment frag",
            "#pragma multi_compile _ _A",
            "#ifdef _A",
            "float4 _Tint;",
            "#endif",
            "half4 shadowed() { float4 _Tint = 1; return _Tint; }",
            "half4 frag() : SV_Target { return _Tint + shadowed(); }"));

        // frag の _Tint (グローバル) だけを報告する。shadowed の _Tint は局所変数なので報告しない。
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0315");
        Assert.Contains("!_A のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)] // 並べる経路
    [InlineData(true)]  // 並べない経路 (#define を含む)
    public void 宣言と同じ条件の中で使っている変数は報告しない(bool defines)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader([
            "#pragma fragment frag",
            "#pragma multi_compile _ _A",
            "#ifdef _A",
            "float4 _Tint;",
            "#endif",
            "#ifdef _A",
            .. defines ? ["#define USE_A 1"] : Array.Empty<string>(),
            "half4 tint() { return _Tint; }",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"]));

        Assert.False(Has(diagnostics, "HL0315"), Describe(diagnostics));
    }

    [Fact]
    public void 範囲を決められない局所変数の宣言があれば見ない()
    {
        // 波括弧の無い if の中の宣言は範囲を決められない。
        // _Tint がグローバル変数を指しているかが分からないので、報告しない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma fragment frag",
            "#pragma multi_compile _ _A",
            "#ifdef _A",
            "float4 _Tint;",
            "#endif",
            "half4 frag() : SV_Target { if (true) float4 _Tint = 1; return _Tint; }"));

        Assert.False(Has(diagnostics, "HL0315"), Describe(diagnostics));
    }

    [Fact]
    public void 無関係なシンボルのバリアントで条件を取り違えない()
    {
        // _A の宣言は並べられ、使う側は並べられない。_B は別に構成として展開される。
        // _B のバリアントが _A の分岐を並べずに展開していると、宣言に !_B が付き、
        // _A && _B のとき宣言が無いことになっていた。Unity 同梱の lightlistbuild.compute の形。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma fragment frag",
            "#pragma multi_compile _ _A",
            "#pragma multi_compile _ _B",
            "#ifdef _A",
            "float4 _Tint;",
            "#endif",
            "#ifdef _B",
            "#define USE_B 1",
            "#endif",
            "#ifdef _A",
            "#define USE_A 1",
            "half4 tint() { return _Tint; }",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.False(Has(diagnostics, "HL0315"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // HL0314: 同じ名前を 2 回宣言している
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("float4 _Color;", "float4 _Color;")]                                // 変数
    [InlineData("struct Point { float x; };", "struct Point { float y; };")]        // 構造体
    [InlineData("float f(float a) { return a; }", "float f(float b) { return b; }")] // 同じ形の関数
    public void 二重宣言を報告する(string first, string second)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            first,
            second,
            "half4 frag() : SV_Target { return 0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0314");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    // 仮引数の型が違えば多重定義であり、誤りではない。
    [InlineData("float f(float a) { return a; }", "float f(float2 a) { return a.x; }")]
    // 宣言だけを先に書く形は正しい。
    [InlineData("float f(float a);", "float f(float a) { return a; }")]
    public void 多重定義と前方宣言は報告しない(string first, string second)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            first,
            second,
            "half4 frag() : SV_Target { return f(1); }"));

        Assert.False(Has(diagnostics, "HL0314"), Describe(diagnostics));
    }

    /// <summary>
    /// 同じ波括弧の直下に並ぶ局所変数の二重宣言を報告することを検証する。
    /// </summary>
    /// <remarks>
    /// 条件で分けた宣言どうしでも、同時に成り立つ構成があれば二重宣言になる。
    /// <c>multi_compile _A _B</c> と <c>multi_compile _C _D</c> は別の行なので、
    /// <c>_A</c> と <c>_D</c> は同時に有効になる。
    /// </remarks>
    [Theory]
    [InlineData("float a = 1;", "float a = 2;")]
    [InlineData("float a = 1;", "#if defined(_D)\nfloat4 a = 2;\n#endif")]
    [InlineData("#if defined(_A)\nfloat a = 1;\n#endif", "#if defined(_D)\nfloat4 a = 2;\n#endif")]
    public void 同じ波括弧の中の二重宣言を報告する(string first, string second)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile _A _B",
            "#pragma multi_compile _C _D",
            "half4 frag() : SV_Target",
            "{",
            first,
            second,
            "    return a.x;",
            "}"));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0314");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// 別の波括弧で宣言された同じ名前を、二重宣言として報告しないことを検証する。
    /// </summary>
    /// <remarks>
    /// 内側の波括弧は覆い隠し (shadowing)、兄弟の波括弧と別の関数は別の範囲である。
    /// いずれも HLSL として正しい。
    /// </remarks>
    [Theory]

    // 内側の波括弧での覆い隠し。
    [InlineData("float a = 1;\n{ float a = 2; return a.xxxx; }\nreturn a.xxxx;")]

    // 兄弟の波括弧。
    [InlineData("if (1 > 0) { float a = 1; return a.xxxx; }\nelse { float a = 2; return a.xxxx; }")]

    // for の本体を 2 つ。
    [InlineData("for (int i = 0; i < 2; i++) { float a = i; }\nfor (int j = 0; j < 2; j++) { float a = j; }\nreturn 0;")]
    public void 別の波括弧の同じ名前は報告しない(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 frag() : SV_Target",
            "{",
            body,
            "}"));

        Assert.False(Has(diagnostics, "HL0314"), Describe(diagnostics));
    }

    [Fact]
    public void 別の関数の同じ名前は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "half4 helper() { float a = 1; return a.xxxx; }",
            "half4 frag() : SV_Target { float a = 2; return a.xxxx + helper(); }"));

        Assert.False(Has(diagnostics, "HL0314"), Describe(diagnostics));
    }

    [Fact]
    public void 同時に成り立たない条件の同名宣言は報告しない()
    {
        // #ifdef の両方の分岐に同じ名前を書くのは普通の書き方である。
        // 1 本の木に並べたからといって二重宣言ではない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma shader_feature_local _NORMALMAP",
            "#ifdef _NORMALMAP",
            "float4 _Color;",
            "#else",
            "float4 _Color;",
            "#endif",
            "half4 frag() : SV_Target { return _Color; }"));

        Assert.False(Has(diagnostics, "HL0314"), Describe(diagnostics));
    }

    [Fact]
    public void 同時に成り立つ条件の同名宣言は報告する()
    {
        // 一方が無条件なら、シンボルが有効な構成では 2 つとも存在する。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma shader_feature_local _NORMALMAP",
            "float4 _Color;",
            "#ifdef _NORMALMAP",
            "float4 _Color;",
            "#endif",
            "half4 frag() : SV_Target { return _Color; }"));

        Assert.Single(diagnostics, d => d.Id == "HL0314");
    }

    /// <summary>同じ宣言に並べた場合と、別々に宣言した場合。</summary>
    public static TheoryData<string, bool> ExclusiveOrIndependent => new()
    {
        { "#pragma multi_compile _ _X _Y", false },                                // 同時には有効にならない
        { "#pragma multi_compile _ _X\n#pragma multi_compile _ _Y", true },       // 同時に有効になりうる
    };

    [Theory]
    [MemberData(nameof(ExclusiveOrIndependent))]
    public void 同時には有効にならないシンボルの入れ子は検査しない(string pragmas, bool expected)
    {
        // 外側はマクロを定義するので並べられず、組の構成として展開される経路である。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader([
            .. pragmas.Split('\n'),
            "#ifdef _X",
            "#define USE_X 1",
            "#ifdef _Y",
            "float4 f() { return neverBoth; }",
            "#endif",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"]));

        Assert.Equal(expected, Has(diagnostics, "HL0310"));
    }

    [Theory]
    [MemberData(nameof(ExclusiveOrIndependent))]
    public void 同時には有効にならないシンボルで並べた入れ子は検査しない(string pragmas, bool expected)
    {
        // 外側も内側も並べられる経路である。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader([
            .. pragmas.Split('\n'),
            "#ifdef _X",
            "#ifdef _Y",
            "float4 f() { return neverBoth; }",
            "#endif",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"]));

        Assert.Equal(expected, Has(diagnostics, "HL0310"));
    }

    [Theory]
    [MemberData(nameof(ExclusiveOrIndependent))]
    public void 同時には有効にならないシンボルの論理積は検査しない(string pragmas, bool expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader([
            .. pragmas.Split('\n'),
            "#if defined(_X) && defined(_Y)",
            "#define USE_XY 1",
            "float4 f() { return neverBoth; }",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"]));

        Assert.Equal(expected, Has(diagnostics, "HL0310"));
    }

    [Theory]
    [MemberData(nameof(ExclusiveOrIndependent))]
    public void 同時には有効にならないシンボルの同名宣言は報告しない(string pragmas, bool expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader([
            .. pragmas.Split('\n'),
            "#ifdef _X",
            "float4 _Tint;",
            "#endif",
            "#ifdef _Y",
            "float4 _Tint;",
            "#endif",
            "half4 frag() : SV_Target { return _Tint; }"]));

        Assert.Equal(expected, Has(diagnostics, "HL0314"));
    }

    [Theory]
    [InlineData("#pragma multi_compile MODE_A MODE_B", false)]   // どちらかが必ず有効
    [InlineData("#pragma multi_compile _ MODE_A MODE_B", true)]  // 「どちらも無い」構成がある
    [InlineData("#pragma shader_feature MODE_A MODE_B", true)]   // shader_feature は「無し」もコンパイルされる
    public void どれか1つが必ず有効な行で全部が無い分岐は検査しない(string pragma, bool expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            pragma,
            "#if defined(MODE_A)",
            "float4 f() { return 1; }",
            "#elif defined(MODE_B)",
            "float4 f() { return 0; }",
            "#else",
            "float4 f() { return neverCompiled; }",
            "#endif",
            "half4 frag() : SV_Target { return f(); }"));

        Assert.Equal(expected, diagnostics.Any(d => d.Id == "HL0310" && d.GetMessage().Contains("neverCompiled", StringComparison.Ordinal)));
    }

    [Fact]
    public void キーワード1つのmulti_compileの外側は検査しない()
    {
        // _ が無いので、ALWAYS_ON はどの構成でも定義されている。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma multi_compile ALWAYS_ON",
            "#ifndef ALWAYS_ON",
            "float4 f() { return neverCompiled; }",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.False(Has(diagnostics, "HL0310"), Describe(diagnostics));
    }

    [Theory]
    [InlineData(false)] // 並べる経路
    [InlineData(true)]  // 並べない経路 (#define を含む)
    public void どれか1つが必ず有効な行の先頭が無い分岐も検査する(bool defines)
    {
        // 既定の構成は MODE_A を定義している。#else は MODE_B の構成で通る。
        // 条件に MODE_B が書かれていなくても、その構成を作って検査する。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader([
            "#pragma multi_compile MODE_A MODE_B",
            "#ifdef MODE_A",
            .. defines ? ["#define USE_A 1"] : Array.Empty<string>(),
            "float4 f() { return missingInA; }",
            "#else",
            "float4 f() { return missingInB; }",
            "#endif",
            "half4 frag() : SV_Target { return f(); }"]));

        Assert.Contains(diagnostics, d => d.Id == "HL0310" && d.GetMessage().Contains("missingInA", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Id == "HL0310" && d.GetMessage().Contains("missingInB", StringComparison.Ordinal));
    }

    [Fact]
    public void 別のPassで独立に宣言したシンボルは排他としない()
    {
        // 出現条件の索引はすべての Pass をまとめて持つ。
        // 1 つの Pass で同じ行に並べていても、別の Pass で独立に宣言していれば、
        // その Pass では同時に有効になりうる。
        string source = """
            Shader "Test/Passes"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma fragment frag
                        #pragma multi_compile _ _X _Y
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma fragment frag
                        #pragma multi_compile _ _X
                        #pragma multi_compile _ _Y
                        #ifdef _X
                        float4 _Tint;
                        #endif
                        #ifdef _Y
                        float4 _Tint;
                        #endif
                        half4 frag() : SV_Target { return _Tint; }
                        ENDHLSL
                    }
                }
            }
            """;

        Assert.Single(Analyze(source), d => d.Id == "HL0314");
    }

    [Fact]
    public void 組の構成で片方しか現れない同名宣言は報告しない()
    {
        // 外側はマクロを定義するので並べられず、_A の構成と _A・_B の構成で別々に展開される。
        // #else 側の宣言は _A の構成でしか見つからないので、条件は「_A」としか分からない。
        // 条件だけを掛け合わせると _A ∧ _B で両方が存在するように見えるが、
        // その構成を展開した木には #else 側が無い。Unity 同梱の OccluderDepthPyramidKernels.compute の形。
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma shader_feature_local _A",
            "#pragma shader_feature_local _B",
            "#ifdef _A",
            "#define USE_A 1",
            "#ifdef _B",
            "float4 _Src;",
            "#else",
            "float3 _Src;",
            "#endif",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.False(Has(diagnostics, "HL0314"), Describe(diagnostics));
    }

    [Fact]
    public void 組の構成で両方が現れる同名宣言は報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma shader_feature_local _A",
            "#pragma shader_feature_local _B",
            "#ifdef _A",
            "#define USE_A 1",
            "float4 _Src;",
            "#ifdef _B",
            "float4 _Src;",
            "#endif",
            "#endif",
            "half4 frag() : SV_Target { return 0; }"));

        Assert.Single(diagnostics, d => d.Id == "HL0314");
    }

    /// <summary>
    /// 関数の中の範囲を、名前の解決と同じ単位で見ることを検証する。
    /// </summary>
    /// <remarks>
    /// fxc で次のとおりであることを確かめた。
    /// 仮引数と同じ名前をいちばん外の波括弧で宣言すると X3036、
    /// switch の別の case で同じ名前を宣言すると X3003 になる。
    /// 内側の波括弧で宣言するのは覆い隠しで、誤りではない。
    /// </remarks>
    [Theory]
    [InlineData("half4 frag(float a : TEXCOORD0) : SV_Target { float a = 1; return a; }", true)]
    [InlineData("half4 frag(float a : TEXCOORD0) : SV_Target { { float a = 1; return a; } }", false)]
    [InlineData("half4 frag(int k : TEXCOORD0) : SV_Target { float r = 0; switch (k) { case 0: float a = 1; r = a; break; case 1: float a = 2; r = a; break; } return r; }", true)]
    [InlineData("half4 frag(int k : TEXCOORD0) : SV_Target { float r = 0; switch (k) { case 0: { float a = 1; r = a; break; } case 1: { float a = 2; r = a; break; } } return r; }", false)]
    public void 仮引数とswitchの本体も同じ範囲として見る(string function, bool expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader("#pragma fragment frag", function));

        Assert.Equal(expected, diagnostics.Count(d => d.Id == "HL0314") == 1);
        Assert.True(diagnostics.Count(d => d.Id == "HL0314") <= 1, Describe(diagnostics));
    }

    /// <summary>
    /// for の初期化の変数を、囲む波括弧の宣言として突き合わせることを検証する。
    /// </summary>
    /// <remarks>
    /// fxc で確かめた。ループの後で同じ名前を宣言すると X3003。
    /// 初期化の変数のほうが後なら、前の宣言より優先する旨の警告 (X3078) で済む。
    /// </remarks>
    [Theory]
    [InlineData("for (int i = 0; i < 2; i++) { } float i = 1; return i;", true)]
    [InlineData("float i = 1; for (int i = 0; i < 2; i++) { } return i;", false)]
    [InlineData("for (int i = 0; i < 2; i++) { } for (int i = 0; i < 3; i++) { } return i;", false)]
    [InlineData("{ for (int i = 0; i < 2; i++) { } } float i = 1; return i;", false)]
    public void ループの初期化の変数も囲む波括弧の宣言として見る(string body, bool expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Shader(
            "#pragma fragment frag",
            "half4 frag() : SV_Target { " + body + " }"));

        Assert.True(Has(diagnostics, "HL0314") == expected, Describe(diagnostics));
    }

    [Fact]
    public void 別のPassの同じ名前は報告しない()
    {
        // Pass ごとに別のコードとして組み立てられる。
        string source = """
            Shader "Test/Passes"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                    Pass
                    {
                        HLSLPROGRAM
                        half4 frag() : SV_Target { return 1; }
                        ENDHLSL
                    }
                }
            }
            """;

        Assert.False(Has(Analyze(source), "HL0314"), Describe(Analyze(source)));
    }

    /// <summary>
    /// 別の Pass にだけある宣言を、この Pass で使っていれば報告することを検証する。
    /// </summary>
    /// <remarks>
    /// Pass ごとに別のコードとして組み立てられるので、別の Pass の宣言は見えない。
    /// 名前だけでシェーダー全体の宣言を集めると、別の Pass にあるだけで「宣言されている」ことになっていた。
    /// </remarks>
    [Theory]
    [InlineData("float4 Helper() { return 1; }", "return Helper();", "HL0311")]
    [InlineData("float4 _OnlyFirst;", "return _OnlyFirst;", "HL0310")]
    public void 別のPassにだけある宣言は見えない(string firstPass, string use, string id)
    {
        string source = $$"""
            Shader "Test/PassScopes"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma fragment frag
                        {{firstPass}}
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma fragment frag
                        float4 Helper();
                        half4 frag() : SV_Target { {{use}} }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Assert.True(Has(diagnostics, id), Describe(diagnostics));
    }

    /// <summary>
    /// 別の Pass の同じ名前の構造体を取り違えないことを検証する。
    /// </summary>
    /// <remarks>
    /// URP の Sprite-Lit-Default.shader は、1 つめの Pass の <c>Varyings</c> では <c>normalWS</c> を
    /// <c>#if defined(DEBUG_DISPLAY)</c> の中に置き、2 つめの Pass の <c>Varyings</c> では常に置く。
    /// 名前だけでまとめると、2 つめの Pass の参照を「DEBUG_DISPLAY が無いときは無い」と報告してしまう。
    /// </remarks>
    [Fact]
    public void 別のPassの同じ名前の構造体を取り違えない()
    {
        string source = """
            Shader "Test/StructPasses"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma fragment frag
                        #pragma multi_compile _ DEBUG_DISPLAY
                        struct Varyings
                        {
                            float4 positionCS : SV_POSITION;
                        #if defined(DEBUG_DISPLAY)
                            half3 normalWS : TEXCOORD0;
                        #endif
                        };
                        half4 frag(Varyings i) : SV_Target
                        {
                            // 文の途中で分けているので、DEBUG_DISPLAY を有効にした構成を別に展開する。
                            half a
                        #if defined(DEBUG_DISPLAY)
                                = 1
                        #else
                                = 0
                        #endif
                                ;
                        #if defined(DEBUG_DISPLAY)
                            return half4(i.normalWS, a);
                        #else
                            return a;
                        #endif
                        }
                        ENDHLSL
                    }
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma fragment frag
                        struct Varyings
                        {
                            float4 positionCS : SV_POSITION;
                            half3 normalWS : TEXCOORD0;
                        };
                        half4 frag(Varyings i) : SV_Target { return half4(i.normalWS, 1); }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Assert.False(Has(diagnostics, "HL0313"), Describe(diagnostics));
    }

    /// <summary>
    /// <c>class</c> / <c>interface</c> を使ったシェーダーで、
    /// 解釈できないことを報告することを検証する。
    /// </summary>
    /// <remarks>
    /// <b>この変更の前は、診断が 1 件も出なかった。</b>
    /// 宣言は「<c>interface</c> 型の変数」として取り込まれ、
    /// 中身のメソッドは存在しないものとして扱われ、
    /// それを呼ぶ式も、何も指摘されずに通っていた。
    /// 指摘が出ないことを「問題が無い」と受け取られてはならない。
    /// </remarks>
    [Fact]
    public void クラスとインターフェイスを解釈できないことを報告する()
    {
        string source = Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "interface ILight { half3 Shade(half3 n); };",
            "class PointLight : ILight { half3 color; half3 Shade(half3 n) { return color * n; } };",
            "struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };",
            "Varyings vert(float4 positionOS : POSITION) { Varyings o; o.positionCS = positionOS; o.uv = 0; return o; }",
            "half4 frag(Varyings i) : SV_Target { PointLight l; return half4(l.Shade(i.uv.xyx), 1); }");

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Assert.Equal(2, diagnostics.Count(d => d.Id == "HL0003"));
    }

    /// <summary>
    /// 解釈できない宣言があるブロックでは、
    /// 「宣言が見つからない」ことを根拠とする検査を見送り、その事実を報告することを検証する。
    /// </summary>
    /// <remarks>
    /// 中身を読んでいない宣言がある以上、uniform の一覧には抜けがある。
    /// その状態で「対応する宣言が無い」と言えば、正しいシェーダーを誤りとして指摘する。
    /// 見送ったこと自体は <c>SL0002</c> が伝える。
    /// </remarks>
    [Fact]
    public void 解釈できない宣言があるブロックでは対応検査を見送る()
    {
        string source = Shader(
            "#pragma vertex vert",
            "#pragma fragment frag",
            "interface ILight { half3 Shade(half3 n); };",
            "struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };",
            "Varyings vert(float4 positionOS : POSITION) { Varyings o; o.positionCS = positionOS; o.uv = 0; return o; }",
            "half4 frag(Varyings i) : SV_Target { return i.uv.xyxy; }");

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Assert.True(Has(diagnostics, "SL0002"), Describe(diagnostics));
    }

    private static bool Has(ImmutableArray<Diagnostic> diagnostics, string ruleId)
        => diagnostics.Any(d => d.Id == ruleId);
}
