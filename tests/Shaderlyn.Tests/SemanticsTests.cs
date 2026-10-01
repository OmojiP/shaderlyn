using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Profiles;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// 意味解析層とそれを使うルールの検証。
/// </summary>
/// <remarks>
/// <para>
/// テストは Unity のインストールに依存しない。
/// URP のヘッダは <see cref="InMemoryIncludeResolver"/> で空の内容として与え、
/// マクロは同梱の既定マクロ (<see cref="UnityShaderStubs"/>) に解決させる。
/// </para>
/// <para>
/// この構成は現実の CI 環境そのものでもある。
/// GitHub Actions のランナーに Unity は無く、そこで解析が成立しなければ
/// PR 検査の道具として使えない。
/// </para>
/// </remarks>
public sealed class SemanticsTests
{
    /// <summary>URP のヘッダに見せかけるための、内容が空のファイル。</summary>
    /// <remarks>
    /// <para>
    /// 中身は要らない。必要なのは 2 点だけである。
    /// </para>
    /// <list type="number">
    ///   <item><description>include が解決できること (解決できないと対応検査が見送られる)</description></item>
    ///   <item><description>パスに URP の目印が含まれること (プロファイルの適用判定に使われる)</description></item>
    /// </list>
    /// <para>
    /// <c>TEXTURE2D</c> などのマクロは同梱の既定マクロが供給する。
    /// </para>
    /// </remarks>
    private const string UrpCorePath = "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl";

    /// <summary>
    /// URP のヘッダの代わりに置く、最小限の定義。
    /// </summary>
    /// <remarks>
    /// <b>空にしてはならない。</b>
    /// 空だと SAMPLE_TEXTURE2D などのマクロが未定義のまま識別子として残り、
    /// 実物では起きない指摘 (HL0310) が出る。
    /// 実物のヘッダで定義されているものだけを、使う分だけ置く。
    /// </remarks>
    private const string UrpCoreStub = """
        #define TEXTURE2D(name) Texture2D name
        #define SAMPLER(name) SamplerState name
        #define SAMPLE_TEXTURE2D(tex, samp, uv) tex.Sample(samp, uv)
        #define CBUFFER_START(name) cbuffer name {
        #define CBUFFER_END }
        #define TRANSFORM_TEX(uv, name) (uv * name##_ST.xy + name##_ST.zw)
        float4 TransformObjectToHClip(float3 positionOS) { return float4(positionOS, 1); }
        """;

    private static ShaderCompilation Compile(string source, bool provideUrpHeaders = true)
    {
        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        Dictionary<string, string> files = provideUrpHeaders
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = UrpCoreStub }
            : [];

        SemanticsOptions options = new() { IncludeResolver = new InMemoryIncludeResolver(files) };
        return ShaderCompilation.Create(text, tree, options);
    }

    private static ImmutableArray<Diagnostic> Analyze(string source, bool provideUrpHeaders = true)
    {
        ShaderCompilation compilation = Compile(source, provideUrpHeaders);

        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }

    private static bool Has(ImmutableArray<Diagnostic> diagnostics, string ruleId)
        => diagnostics.Any(d => d.Id == ruleId);

    private static string Describe(ImmutableArray<Diagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"  {d.Location.LineSpan.Start} {d.Id}: {d.GetMessage()}"));

    /// <summary>正しく書かれた URP シェーダー。誤検出の検証に使う。</summary>
    private const string CorrectUrpShader = """
        Shader "Test/Lit"
        {
            Properties
            {
                _BaseMap ("Base Map", 2D) = "white" {}
                _BaseColor ("Base Color", Color) = (1,1,1,1)
                _Cutoff ("Cutoff", Range(0,1)) = 0.5
            }
            SubShader
            {
                Tags { "RenderPipeline" = "UniversalPipeline" }
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

                    TEXTURE2D(_BaseMap);
                    SAMPLER(sampler_BaseMap);

                    CBUFFER_START(UnityPerMaterial)
                        float4 _BaseMap_ST;
                        half4 _BaseColor;
                        half _Cutoff;
                    CBUFFER_END

                    float4 vert(float4 positionOS : POSITION) : SV_POSITION
                    {
                        return positionOS;
                    }

                    half4 frag() : SV_Target
                    {
                        half4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, float2(0, 0)) * _BaseColor;
                        clip(c.a - _Cutoff);
                        return c;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // ------------------------------------------------------------------
    // 埋め込みコードの取り出し
    // ------------------------------------------------------------------

    [Fact]
    public void 埋め込みコードを取り出しても位置がずれない()
    {
        // 位置がずれたリンタは、指摘が正しくても直す場所を示せない。
        SourceText text = SourceText.From(CorrectUrpShader, "Test.shader");
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        ProgramBlockView extraction = Assert.Single(ProgramBlockExtractor.Extract(tree));

        Assert.Equal(text.Length, extraction.MaskedText.Length);
        Assert.Equal(text.LineCount, extraction.MaskedText.LineCount);
        Assert.Equal(text.FilePath, extraction.MaskedText.FilePath);

        // 解析対象の範囲は 1 文字も変えずに残っている。
        TextSpan body = Assert.IsType<ShaderLab.Syntax.ProgramBlockSyntax>(extraction.Block).BodySpan;
        Assert.Equal(text.ToString(body), extraction.MaskedText.ToString(body));
    }

    [Fact]
    public void 埋め込みコード以外は空白で覆われるが改行は残る()
    {
        SourceText text = SourceText.From(CorrectUrpShader, "Test.shader");
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);
        ProgramBlockView extraction = Assert.Single(ProgramBlockExtractor.Extract(tree));

        // Shader 宣言はコードの外なので覆われている。
        Assert.DoesNotContain("Test/Lit", extraction.MaskedText.Content, StringComparison.Ordinal);

        // 行の対応は保たれている。
        int shaderNameLine = text.GetLinePositionSpan(
            new TextSpan(text.Content.IndexOf("Test/Lit", StringComparison.Ordinal), 0)).Start.Line;
        Assert.Equal(string.Empty, extraction.MaskedText.GetLineText(shaderNameLine).Trim());
    }

    [Fact]
    public void 共通コード片は各Passへ差し込まれる()
    {
        // Unity は HLSLINCLUDE を同じブロック内の各 *PROGRAM の先頭へ差し込む。
        // これを再現できないと、共通コードに宣言をまとめたシェーダーで
        // uniform が 1 つも見つからなくなる。
        const string source = """
            Shader "Test/Shared"
            {
                Properties { _Tint ("Tint", Color) = (1,1,1,1) }
                SubShader
                {
                    HLSLINCLUDE
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                    CBUFFER_START(UnityPerMaterial)
                        half4 _Tint;
                    CBUFFER_END
                    half4 Shade() { return _Tint; }
                    ENDHLSL

                    Pass
                    {
                        HLSLPROGRAM
                        half4 frag() : SV_Target { return Shade(); }
                        ENDHLSL
                    }

                    Pass
                    {
                        HLSLPROGRAM
                        half4 frag() : SV_Target { return Shade() * 0.5; }
                        ENDHLSL
                    }
                }
            }
            """;

        ShaderCompilation compilation = Compile(source);

        Assert.Equal(2, compilation.Programs.Length);
        Assert.All(compilation.Programs, program => Assert.Single(program.IncludeBlocks));

        // 両方の Pass から共通コード片の uniform が見えている。
        Assert.All(compilation.Programs, program => Assert.Contains(program.Uniforms, u => u.Name == "_Tint"));
        Assert.True(compilation.TryGetUniform("_Tint", out _));
        Assert.Empty(compilation.HlslDiagnostics);
    }

    /// <summary>
    /// 組み込みの宣言 (<c>#pragma multi_compile_fog</c>) が作るシンボルの分岐も読むことを検証する。
    /// </summary>
    /// <remarks>
    /// <c>FOG_LINEAR</c> はどこにも書かれていないが、Unity はこれを定義した構成をコンパイルする。
    /// </remarks>
    [Fact]
    public void 組み込みの宣言が作るシンボルの分岐も読む()
    {
        const string source = """
            Shader "Test"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma multi_compile_fog
                        #ifdef FOG_LINEAR
                        half4 _FogTint;
                        #endif
                        ENDHLSL
                    }
                }
            }
            """;

        ShaderCompilation compilation = Compile(source, provideUrpHeaders: false);

        Assert.Contains(
            compilation.Programs.Concat(compilation.SymbolVariants),
            program => program.Uniforms.Any(u => u.Name == "_FogTint"));
    }

    /// <summary>
    /// <c>CGPROGRAM</c> には Unity が自動で取り込むヘッダを取り込むことを検証する。
    /// </summary>
    /// <remarks>
    /// <c>HLSLSupport.cginc</c> と <c>UnityShaderVariables.cginc</c> は書かなくても取り込まれる。
    /// <c>HLSLPROGRAM</c> には取り込まれない。
    /// </remarks>
    [Fact]
    public void CGPROGRAMには自動で取り込まれるヘッダを取り込む()
    {
        const string source = """
            Shader "Test"
            {
                SubShader
                {
                    Pass
                    {
                        CGPROGRAM
                        float4 Use() { return unity_Probe; }
                        ENDCG
                    }

                    Pass
                    {
                        HLSLPROGRAM
                        float4 Use() { return 0; }
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            ["HLSLSupport.cginc"] = "#define fixed half\n",
            ["UnityShaderVariables.cginc"] = "float4 unity_Probe;\n",
        };

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            ShaderLabSyntaxTree.Parse(text),
            new SemanticsOptions { IncludeResolver = new InMemoryIncludeResolver(files) });

        Assert.Contains(compilation.Programs[0].Uniforms, u => u.Name == "unity_Probe");
        Assert.DoesNotContain(compilation.Programs[1].Uniforms, u => u.Name == "unity_Probe");
        Assert.True(compilation.HasCompleteDependencies);
    }

    /// <summary>
    /// サーフェスシェーダーには照明のヘッダも取り込むことを検証する。
    /// </summary>
    /// <remarks>
    /// Unity の雛形は <c>#include</c> を書かずに <c>SurfaceOutputStandard</c> を使う。
    /// <c>UnityPBSLighting.cginc</c> は照明モデルが <c>Standard</c> 系のときだけ取り込む。
    /// </remarks>
    [Theory]
    [InlineData("Standard", true)]
    [InlineData("Lambert", false)]
    public void サーフェスシェーダーには照明のヘッダも取り込む(string lighting, bool expectsPbs)
    {
        string source = $$"""
            Shader "Test"
            {
                SubShader
                {
                    CGPROGRAM
                    #pragma surface surf {{lighting}}
                    ENDCG
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            ["HLSLSupport.cginc"] = "",
            ["UnityShaderVariables.cginc"] = "",
            ["UnityCG.cginc"] = "float4 cg_Uniform;\n",
            ["Lighting.cginc"] = "float4 lighting_Uniform;\n",
            ["UnityPBSLighting.cginc"] = "float4 pbs_Uniform;\n",
        };

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            ShaderLabSyntaxTree.Parse(text),
            new SemanticsOptions { IncludeResolver = new InMemoryIncludeResolver(files) });

        AnalyzedProgram program = Assert.Single(compilation.Programs);
        Assert.Contains(program.Uniforms, u => u.Name == "cg_Uniform");
        Assert.Contains(program.Uniforms, u => u.Name == "lighting_Uniform");
        Assert.Equal(expectsPbs, program.Uniforms.Any(u => u.Name == "pbs_Uniform"));
    }

    /// <summary>
    /// ヘッダが見つからないときは、<c>CGPROGRAM</c> にも何も足さないことを検証する。
    /// </summary>
    /// <remarks>
    /// Unity が無い環境で足すと、書いていない <c>#include</c> が解決できず、検査を見送ることになる。
    /// </remarks>
    [Fact]
    public void 自動で取り込むヘッダが無ければ何も足さない()
    {
        const string source = """
            Shader "Test"
            {
                SubShader
                {
                    Pass
                    {
                        CGPROGRAM
                        float4 _Tint;
                        ENDCG
                    }
                }
            }
            """;

        ShaderCompilation compilation = Compile(source, provideUrpHeaders: false);

        Assert.True(compilation.HasCompleteDependencies);
        Assert.Contains(compilation.Programs[0].Uniforms, u => u.Name == "_Tint");
    }

    /// <summary>
    /// <c>#pragma target</c> から <c>SHADER_TARGET</c> を決めることを検証する。
    /// </summary>
    /// <remarks>
    /// ヘッダは <c>#if SHADER_TARGET &lt; 30</c> で経路を選ぶ。既定の 45 のまま読むと、
    /// <c>#pragma target 2.0</c> の Pass では決して通らない経路を検査する。
    /// 指定の無い Pass は既定のままにする。
    /// </remarks>
    [Fact]
    public void PragmaTargetからSHADER_TARGETを決める()
    {
        const string source = """
            Shader "Test"
            {
                SubShader
                {
                    HLSLINCLUDE
                    #if SHADER_TARGET < 30
                    half4 _Low;
                    #else
                    half4 _High;
                    #endif
                    ENDHLSL

                    Pass
                    {
                        HLSLPROGRAM
                        #pragma target 2.0
                        ENDHLSL
                    }

                    Pass
                    {
                        HLSLPROGRAM
                        ENDHLSL
                    }
                }
            }
            """;

        ShaderCompilation compilation = Compile(source, provideUrpHeaders: false);

        Assert.Equal(2, compilation.Programs.Length);
        Assert.Contains(compilation.Programs[0].Uniforms, u => u.Name == "_Low");
        Assert.DoesNotContain(compilation.Programs[0].Uniforms, u => u.Name == "_High");
        Assert.Contains(compilation.Programs[1].Uniforms, u => u.Name == "_High");
    }

    [Fact]
    public void 読み込み済みのテキストをディスクを読まずに解析できる()
    {
        // エディタに常駐して解析する場合、対象は保存前のテキストになる。
        // パスを受け取る入口しか無いと、保存していない編集を解析できない。
        SourceText text = SourceText.From(
            """
            Shader "Test/Unsaved"
            {
                SubShader { Pass { Cull Sideways } }
            }
            """,
            Path.Combine("Assets", "存在しない.shader"));

        ImmutableArray<Diagnostic> diagnostics =
            new Cli.AnalysisRunner(Cli.BuiltInAnalyzers.All).Analyze(text);

        Assert.Contains(diagnostics, d => d.Id == "SL1021");
    }

    [Fact]
    public void 意味解析を省くとShaderLab単体のルールだけが走る()
    {
        // 入力のたびに走らせる段では、include の展開を伴う検査は間に合わない。
        // 省いた場合に何が出て何が出ないかを固定しておく。
        const string source = """
            Shader "Test/Partial"
            {
                Properties { _Unused ("Unused", Color) = (1,1,1,1) }
                SubShader
                {
                    Tags { "RenderType" = "Opaqu" }
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        half4 frag() : SV_Target { return 1; }
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));

        string[] withoutSemantics =
            [.. new Cli.AnalysisRunner(Cli.BuiltInAnalyzers.All).Analyze(text).Select(d => d.Id)];

        // ShaderLab 単体で判断できるものは出る。
        Assert.Contains("SL1012", withoutSemantics);

        // セマンティックモデルを必要とするものは出ない。検査を見送ったことも報告されない。
        Assert.DoesNotContain("SL1001", withoutSemantics);
        Assert.DoesNotContain("SL0002", withoutSemantics);

        // 同じシェーダーを意味解析つきで見れば、対応検査の結果が加わる。
        Assert.Contains("SL1001", Analyze(source).Select(d => d.Id));
    }

    // ------------------------------------------------------------------
    // シンボルで守られたコード
    // ------------------------------------------------------------------

    /// <summary>シンボルの中にだけ宣言と誤りがあるシェーダー。</summary>
    private const string SymbolGuardedShader = """
        Shader "Test/Keyword"
        {
            Properties
            {
                _BumpMap ("Bump", 2D) = "bump" {}
            }
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma shader_feature_local _NORMALMAP
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

                    #ifdef _NORMALMAP
                        float4 _BumpMap;
                    #endif

                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                    half4 frag() : SV_Target { return 1; }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public void シンボルの中の宣言も見えている()
    {
        // シンボルは C# からも切り替えられる。
        // 有効になっていない分岐の宣言も「いつか存在する」宣言である。
        ShaderCompilation compilation = Compile(SymbolGuardedShader);

Assert.True(compilation.TryGetUniform("_BumpMap", out UniformSymbol? bumpMap));

        // まとめられる #ifdef なので、宣言は 1 本の木の中に条件付きで載っている。
        // どちらの経路で見えているかではなく、条件が付いていることを確かめる。
        Assert.Equal(
            "_NORMALMAP",
            compilation.GetConditionMap().GetCondition(bumpMap!.Declaration).ToString());
    }

    [Fact]
    public void シンボルの中の構文エラーも報告する()
    {
        // 有効になっていない分岐のコードも、いつか通るコードである。
        string source = SymbolGuardedShader.Replace(
            "float4 _BumpMap;", "float4 _BumpMap", StringComparison.Ordinal);

        ShaderCompilation compilation = Compile(source);

        Assert.Contains(compilation.ReportableHlslDiagnostics, d => d.Id == "HL0001");
    }

    [Fact]
    public void 条件で見ていないシンボルは展開しない()
    {
        // そのファイルの条件が見ていないシンボルを有効にしても、
        // そのファイルのコードは 1 行も変わらない。
        string source = SymbolGuardedShader.Replace(
            "#pragma shader_feature_local _NORMALMAP",
            "#pragma shader_feature_local _NORMALMAP _UNUSED_KEYWORD",
            StringComparison.Ordinal);

        ShaderCompilation compilation = Compile(source);

        Assert.DoesNotContain("_UNUSED_KEYWORD", compilation.SymbolVariants.SelectMany(v => v.EnabledSymbols));
    }

    [Fact]
    public void 展開しきれなかったシンボルを記録する()
    {
        // 何も伝えずに一部だけ調べると、調べていない箇所を「問題なし」と受け取られる。
        SourceText text = SourceText.From(SymbolGuardedShader, Path.Combine("Assets", "Test.shader"));
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = UrpCoreStub }),
            MaxSymbolVariants = 0,

            // ここで見るのはバリアントの上限である。
            // 1 本の木にまとめられるとその道を通らないので、明示的に切る。
            BothBranchSymbols = [],
        };

        ShaderCompilation compilation = ShaderCompilation.Create(text, tree, options);

        Assert.Empty(compilation.SymbolVariants);
        Assert.Contains("_NORMALMAP", compilation.UnexploredSymbols);
    }

    /// <summary>
    /// 条件がヘッダにしか無いシンボルを展開しきれなかったとき、宣言した行に報告することを検証する。
    /// </summary>
    /// <remarks>
    /// <c>.shader</c> で宣言して共通の <c>.hlsl</c> の機能だけを切り替える運用では、このファイルに条件が無い。
    /// ファイルの先頭に出すと、どのシンボルの話かを名前から探させることになる。
    /// </remarks>
    [Fact]
    public void 条件がヘッダにしか無いシンボルのSL0003は宣言した行に出す()
    {
        string source = """
            Shader "Test/HeaderOnly"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma fragment frag
                        #pragma multi_compile _ _FOG_ON
                        #include "Fog.hlsl"
                        half4 frag() : SV_Target { return ApplyFog(0); }
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fog.hlsl"] = "#ifndef FOG_INCLUDED\n#define FOG_INCLUDED\n#ifdef _FOG_ON\nhalf4 ApplyFog(half4 c\n#else\nhalf4 ApplyFog(half4 c, float d = 0\n#endif\n) { return c; }\n#endif\n",
            }),
            MaxSymbolVariants = 0,
        };

        ShaderCompilation compilation = ShaderCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), options);
        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Diagnostic reported = Assert.Single(diagnostics.Where(d => d.Id == "SL0003"));
        Assert.Contains("_FOG_ON", reported.GetMessage(), StringComparison.Ordinal);

        // #pragma multi_compile の行 (0 始まりで 8 行目)。
        Assert.Equal(8, reported.Location.LineSpan.Start.Line);
    }

    [Fact]
    public void 展開しきれなかったシンボルをSL0003として報告する()
    {
        // 「調べていない」ことを伝えないままでいると、
        // 指摘が出ないことを「問題が無い」と受け取られる。
        SourceText text = SourceText.From(SymbolGuardedShader, Path.Combine("Assets", "Test.shader"));
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = UrpCoreStub }),
            MaxSymbolVariants = 0,

            // ここで見るのはバリアントの上限である。
            // 1 本の木にまとめられるとその道を通らないので、明示的に切る。
            BothBranchSymbols = [],
        };

        ShaderCompilation compilation = ShaderCompilation.Create(text, tree, options);
        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Diagnostic reported = Assert.Single(diagnostics.Where(d => d.Id == "SL0003"));
        Assert.Contains("_NORMALMAP", reported.GetMessage(), StringComparison.Ordinal);

        // ファイルの先頭ではなく、調べなかった条件の行に出す。
        // どの #ifdef の中が読まれていないのかを、名前から探させない。
        // LinePosition は 0 始まり。
        int line = SymbolGuardedShader.Split('\n')
            .Select((text, index) => (text, index))
            .First(l => l.text.Contains("#ifdef _NORMALMAP", StringComparison.Ordinal)).index;

        Assert.Equal(line, reported.Location.LineSpan.Start.Line);
    }

    /// <summary>外側を並べられない <c>#ifdef _A</c> の中に <c>#ifdef _B</c> があるシェーダー。</summary>
    private const string NestedInDeclinedShader = """
        Shader "Test/Nested"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma shader_feature_local _A
                    #pragma shader_feature_local _B
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

                    #ifdef _A
                    #define USE_A 1
                    static const half a = USE_A;   // 中身を変えて 2 度定義し、コードでも使うので、この分岐は並べられない
                    half4 OnlyA()
                    {
                    #ifdef _B
                        return missingBoth;
                    #endif
                        return 0;
                    }
                    #else
                    #define USE_A 0
                    #endif

                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                    half4 frag() : SV_Target { return 0; }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public void 入れ子の条件は外側と掛け合わせた組として展開する()
    {
        // _A だけの構成でも _B だけの構成でも、内側は通らない。
        ShaderCompilation compilation = Compile(NestedInDeclinedShader);

        Assert.Contains(
            compilation.SymbolVariants,
            v => v.EnabledSymbols.SequenceEqual(["_A", "_B"]));
        Assert.Empty(compilation.UnexploredSymbolCombinations);
    }

    [Fact]
    public void 展開しきれなかった組は組としてSL0003を報告する()
    {
        // 1 つずつの構成は作ったので、_A も _B も「調べていない」わけではない。
        // 調べていないのは、両方を同時に有効にした構成だけである。
        SourceText text = SourceText.From(NestedInDeclinedShader, Path.Combine("Assets", "Test.shader"));
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = UrpCoreStub }),
            MaxSymbolVariants = 2,
        };

        ShaderCompilation compilation = ShaderCompilation.Create(text, tree, options);
        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Assert.Empty(compilation.UnexploredSymbols);

        Diagnostic reported = Assert.Single(diagnostics.Where(d => d.Id == "SL0003"));
        Assert.Equal(
            "シンボル '_A' と '_B' を同時に有効にした構成は調べていません。この組み合わせで分かれる分岐の中は読んでいません。",
            reported.GetMessage());

        // 組でしか通らない分岐を始めた、内側の #ifdef を指す。LinePosition は 0 始まり。
        int line = NestedInDeclinedShader.Split('\n')
            .Select((text, index) => (text, index))
            .First(l => l.text.Contains("#ifdef _B", StringComparison.Ordinal)).index;

        Assert.Equal(line, reported.Location.LineSpan.Start.Line);
    }

    [Fact]
    public void すべて展開できたときはSL0003を報告しない()
    {
        // 上限に達していないなら、調べ残しは無い。報告することも無い。
        Assert.False(Has(Analyze(SymbolGuardedShader), "SL0003"), "調べ残しが無いのに報告している");
    }

    [Fact]
    public void 構造体のメンバーをuniformとして拾わない()
    {
        // 構造体のフィールドはマテリアルから値が入る対象ではなく、
        // 頂点入力や補間の受け渡しに使う名前である。
        // uniform の一覧に混ざると、プロパティとの対応判定が
        // 無関係な名前と突き合わせられることになる。
        const string source = """
            Shader "Test/Struct"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        struct Attributes
                        {
                            float4 positionOS : POSITION;
                            float2 uv : TEXCOORD0;
                        };

                        float4 _Tint;

                        half4 frag() : SV_Target { return (half4)_Tint; }
                        ENDHLSL
                    }
                }
            }
            """;

        ShaderCompilation compilation = Compile(source);

        Assert.True(compilation.TryGetUniform("_Tint", out _));
        Assert.False(compilation.TryGetUniform("positionOS", out _));
        Assert.False(compilation.TryGetUniform("uv", out _));

        // 構造体そのものは、セマンティクスの重複検査 (HL0210) のために保持し続ける。
        Assert.Contains(compilation.Programs[0].Structs, s => s.Name == "Attributes");
    }

    // ------------------------------------------------------------------
    // 依存関係の解決状況
    // ------------------------------------------------------------------

    [Fact]
    public void Unityが無くても既定マクロで宣言を取り出せる()
    {
        // CI のランナーに Unity は無い。そこで uniform を 1 つも取れないようでは
        // PR 検査の道具として使えない。
        ShaderCompilation compilation = Compile(CorrectUrpShader);

        Assert.True(compilation.HasCompleteDependencies, Describe(compilation.HlslDiagnostics));

        Assert.True(compilation.TryGetUniform("_BaseColor", out UniformSymbol? color));
        Assert.Equal("half4", color.TypeName);
        Assert.Equal("UnityPerMaterial", color.ContainingBufferName);

        // TEXTURE2D は既定マクロによって Texture2D の宣言へ展開されている。
        Assert.True(compilation.TryGetUniform("_BaseMap", out UniformSymbol? map));
        Assert.Equal(HlslTypeClass.Texture2D, map.TypeClass);
        Assert.Null(map.ContainingBufferName);
    }

    [Fact]
    public void 依存関係が解決できない場合は検査していないことを報告する()
    {
        const string source = """
            Shader "Test/Missing"
            {
                Properties { _Unused ("Unused", Float) = 0 }
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "SomeProjectHeader.hlsl"
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source, provideUrpHeaders: false);

        // 「検査していない」ことは必ず伝える。何も伝えずに通すと素通りに気づけない。
        Assert.True(Has(diagnostics, "SL0002"), Describe(diagnostics));

        // 判断できない状態で「宣言が無い」と報告してはならない。
        Assert.False(Has(diagnostics, "SL1001"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // SL1001: 対応する宣言が無いプロパティ
    // ------------------------------------------------------------------

    [Fact]
    public void 正しく書かれたURPシェーダーには指摘を出さない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(CorrectUrpShader);
        Assert.True(diagnostics.IsEmpty, Describe(diagnostics));
    }

    [Fact]
    public void 対応する宣言が無いプロパティを報告する()
    {
        string source = CorrectUrpShader.Replace(
            """_Cutoff ("Cutoff", Range(0,1)) = 0.5""",
            """
            _Cutoff ("Cutoff", Range(0,1)) = 0.5
                    _Typo ("Typo", Float) = 0
            """,
            StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL1001");
        Assert.Contains("_Typo", diagnostic.GetMessage(), StringComparison.Ordinal);

        // 指摘は Properties に書かれた場所を指す。
        Assert.Contains("_Typo", diagnostic.Location.GetLineText(), StringComparison.Ordinal);
    }

    [Fact]
    public void 非活性領域に名前が現れるプロパティは報告しない()
    {
        // 単一構成での展開では、別の構成でのみ宣言される uniform が見えない。
        // 「見つからない」ことをそのまま根拠にすると正しいシェーダーを誤りとして報告する。
        const string source = """
            Shader "Test/Variant"
            {
                Properties { _BumpMap ("Normal", 2D) = "bump" {} }
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        #ifdef _NORMALMAP
                        TEXTURE2D(_BumpMap);
                        #endif
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Assert.False(Has(diagnostics, "SL1001"), Describe(diagnostics));
    }

    [Fact]
    public void どの構成でも調べない領域で宣言されたプロパティは報告しない()
    {
        // SHADER_API_* は環境の条件なので、バリアントを作らない。
        // 宣言は誰の索引にも入らないが、その環境では存在する。
        const string source = """
            Shader "Test/Platform"
            {
                Properties { _Foo ("Foo", Float) = 0 }
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        #if defined(SHADER_API_GLES)
                        float _Foo;
                        #endif
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Assert.False(Has(diagnostics, "SL1001"), Describe(diagnostics));
    }

    [Fact]
    public void ShaderLabから参照されるプロパティは報告しない()
    {
        // Cull [_Cull] の形で使うプロパティは、HLSL 側に uniform を持たないのが正常である。
        const string source = """
            Shader "Test/DynamicState"
            {
                Properties { _Cull ("Cull", Float) = 2 }
                SubShader
                {
                    Pass
                    {
                        Cull [_Cull]
                        HLSLPROGRAM
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Assert.False(Has(diagnostics, "SL1001"), Describe(diagnostics));
        Assert.False(Has(diagnostics, "URP0001"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // SL1003: 型の不一致
    // ------------------------------------------------------------------

    [Fact]
    public void 型が食い違うプロパティを報告する()
    {
        string source = CorrectUrpShader.Replace("half4 _BaseColor;", "half _BaseColor;", StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL1003");
        Assert.Contains("_BaseColor", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("half", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 成分数の違いは報告しない()
    {
        // Color を half3 で受けるのは Unity 同梱のシェーダーでも普通に行われている。
        string source = CorrectUrpShader.Replace("half4 _BaseColor;", "half3 _BaseColor;", StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Assert.False(Has(diagnostics, "SL1003"), Describe(diagnostics));
    }

    [Fact]
    public void テクスチャの種類の取り違えを報告する()
    {
        string source = CorrectUrpShader.Replace(
            "TEXTURE2D(_BaseMap);", "TEXTURECUBE(_BaseMap);", StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Assert.True(Has(diagnostics, "SL1003"), Describe(diagnostics));
    }

    [Fact]
    public void 別のPassで型が食い違うプロパティも報告する()
    {
        // 最初の Pass の宣言だけを見ると、2 つ目の Pass の食い違いを見落とす。
        const string source = """
            Shader "Test/PerPassType"
            {
                Properties { _Color ("Color", Color) = (1,1,1,1) }
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        float4 _Color;
                        half4 frag() : SV_Target { return _Color; }
                        ENDHLSL
                    }
                    Pass
                    {
                        HLSLPROGRAM
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        float _Color;
                        half4 frag() : SV_Target { return _Color; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL1003");
        Assert.Contains("float", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 別のPassの読み飛ばした分岐はSRPBatcherの検査を見送る根拠にしない()
    {
        // 1 つ目の Pass の読み飛ばした分岐に _Tint が現れる。2 つ目の Pass は _Tint を定数バッファの外に置く。
        // 2 つ目の Pass の話なので、1 つ目の Pass の読み飛ばしを理由に見送ってはならない。
        const string source = """
            Shader "Test/PerPassSrpBatcher"
            {
                Properties { _Tint ("Tint", Float) = 1 }
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        CBUFFER_START(UnityPerMaterial)
                        half _Tint;
                        CBUFFER_END
                        half4 frag() : SV_Target
                        {
                        #if defined(SHADER_API_GLES)
                            return _Tint * 2;
                        #else
                            return _Tint;
                        #endif
                        }
                        ENDHLSL
                    }
                    Pass
                    {
                        HLSLPROGRAM
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        half _Tint;
                        half4 frag() : SV_Target { return _Tint; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Assert.True(Has(diagnostics, "URP0001"), Describe(diagnostics));
    }

    [Fact]
    public void 型の不一致は依存関係が不完全でも報告する()
    {
        // 「見つかった uniform の型が食い違っている」という判断は、
        // ほかに何が見つかっていないかとは無関係に成り立つ。
        const string source = """
            Shader "Test/Partial"
            {
                Properties { _Color ("Color", Color) = (1,1,1,1) }
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "MissingHeader.hlsl"
                        float _Color;
                        half4 frag() : SV_Target { return _Color; }
                        ENDHLSL
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source, provideUrpHeaders: false);

        Assert.True(Has(diagnostics, "SL0002"), Describe(diagnostics));
        Assert.True(Has(diagnostics, "SL1003"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // SL1004 / SL1002
    // ------------------------------------------------------------------

    [Fact]
    public void 宣言だけで使われていないプロパティを報告する()
    {
        string source = CorrectUrpShader.Replace(
            "clip(c.a - _Cutoff);", string.Empty, StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL1004");
        Assert.Contains("_Cutoff", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>_Foo</c> を <c>_A</c> の <c>#ifdef</c> の中でだけ宣言するシェーダー。
    /// </summary>
    /// <param name="insideLines">条件の中に置く行。</param>
    /// <param name="outsideLines">条件の外に置く行。</param>
    /// <returns>組み立てたシェーダーのソース。</returns>
    /// <remarks>
    /// 中に <c>#define</c> があるのでまとめられず、<c>_A</c> のバリアントとして別に解析される。
    /// </remarks>
    private static string VariantDeclaredShader(string insideLines, string outsideLines) => $$"""
        Shader "Test/Variant"
        {
            Properties { _Foo ("Foo", Float) = 0 }
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma shader_feature_local _A
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                    #ifdef _A
                    #define USE_A 1
                    static const float useA = USE_A;
                    float _Foo;
                    {{insideLines}}
                    #endif
                    {{outsideLines}}
                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                    half4 frag() : SV_Target { return 0; }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public void バリアントでだけ宣言されて使われていないプロパティを報告する()
    {
        // 既定の構成では読み飛ばす領域だが、_A のバリアントで中身まで調べている。
        // 宣言の名前が現れているだけで「どこかで使われているかもしれない」とは言えない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(VariantDeclaredShader(string.Empty, string.Empty));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL1004");
        Assert.Contains("_Foo", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void バリアントの中で使われているプロパティは報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(VariantDeclaredShader(
            "half4 UseFoo() { return _Foo; }", string.Empty));

        Assert.False(Has(diagnostics, "SL1004"), Describe(diagnostics));
    }

    [Fact]
    public void どの構成でも調べない領域で使われているプロパティは報告しない()
    {
        // SHADER_API_* は環境の条件なので、バリアントを作らない。中身は誰も調べていない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(VariantDeclaredShader(
            string.Empty,
            """
            #if defined(SHADER_API_GLES)
            half4 UseFoo() { return _Foo; }
            #endif
            """));

        Assert.False(Has(diagnostics, "SL1004"), Describe(diagnostics));
    }

    [Fact]
    public void 定数バッファの未公開uniformを報告する()
    {
        string source = CorrectUrpShader.Replace(
            "half _Cutoff;", "half _Cutoff;\n                half _Hidden;", StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL1002");
        Assert.Contains("_Hidden", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void テクスチャに付随する自動生成uniformは報告しない()
    {
        // _BaseMap_ST は 2D プロパティに対して Unity が自動的に埋めるもので、
        // Properties に書くのは誤りである。
        ImmutableArray<Diagnostic> diagnostics = Analyze(CorrectUrpShader);
        Assert.False(Has(diagnostics, "SL1002"), Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // URP プロファイル
    // ------------------------------------------------------------------

    [Fact]
    public void 定数バッファに入っていないプロパティを報告する()
    {
        string source = CorrectUrpShader.Replace(
            """
                        CBUFFER_START(UnityPerMaterial)
                            float4 _BaseMap_ST;
                            half4 _BaseColor;
                            half _Cutoff;
                        CBUFFER_END
            """,
            """
                        half _Cutoff;

                        CBUFFER_START(UnityPerMaterial)
                            float4 _BaseMap_ST;
                            half4 _BaseColor;
                        CBUFFER_END
            """,
            StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "URP0001");
        Assert.Contains("_Cutoff", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("UnityPerMaterial", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>_Cutoff</c> を定数バッファの外で宣言し、指定した行を足した URP シェーダー。
    /// </summary>
    /// <param name="extraLines">定数バッファの前に置く行。</param>
    /// <returns>組み立てたシェーダーのソース。</returns>
    private static string CutoffOutsideBufferShader(string extraLines)
        => CorrectUrpShader
            .Replace("half _Cutoff;", string.Empty, StringComparison.Ordinal)
            .Replace(
                "CBUFFER_START(UnityPerMaterial)",
                "half _Cutoff;\n" + extraLines + "\nCBUFFER_START(UnityPerMaterial)",
                StringComparison.Ordinal);

    [Fact]
    public void バリアントで調べた領域に名前があるだけのプロパティも定数バッファの外なら報告する()
    {
        // _A のバリアントの中身は解析している。そこにも定数バッファの中の宣言は無い。
        ImmutableArray<Diagnostic> diagnostics = Analyze(CutoffOutsideBufferShader(
            """
            #pragma shader_feature_local _A
            #ifdef _A
            #define USE_A 1
            half UseCutoff() { return _Cutoff; }
            #endif
            """));

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "URP0001");
        Assert.Contains("_Cutoff", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void どの構成でも調べない領域に名前があるプロパティは定数バッファの外でも報告しない()
    {
        // 環境の条件の中は誰も調べていない。そこで定数バッファに入れているかもしれない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(CutoffOutsideBufferShader(
            """
            #if defined(SHADER_API_GLES)
            half UseCutoff() { return _Cutoff; }
            #endif
            """));

        Assert.False(Has(diagnostics, "URP0001"), Describe(diagnostics));
    }

    [Fact]
    public void 定数バッファに入れたテクスチャを報告する()
    {
        string source = CorrectUrpShader.Replace(
            """
                        TEXTURE2D(_BaseMap);
                        SAMPLER(sampler_BaseMap);
            """,
            string.Empty,
            StringComparison.Ordinal).Replace(
            "float4 _BaseMap_ST;",
            "float4 _BaseMap_ST;\n                TEXTURE2D(_BaseMap);\n                SAMPLER(sampler_BaseMap);",
            StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Assert.True(Has(diagnostics, "URP0002"), Describe(diagnostics));
    }

    [Fact]
    public void BuiltIn向けのAPIを報告する()
    {
        string source = CorrectUrpShader.Replace(
            "return positionOS;", "return UnityObjectToClipPos(positionOS);", StringComparison.Ordinal);

        ImmutableArray<Diagnostic> diagnostics = Analyze(source);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "URP0003");
        Assert.Contains("TransformObjectToHClip", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void SRPのヘッダを取り込んでいないシェーダーにURPルールを適用しない()
    {
        // Built-in 向けのシェーダーにとって UnityObjectToClipPos は正規の書き方である。
        // プロファイルが urp だからといって指摘するのは端的に誤りになる。
        const string source = """
            Shader "Test/Legacy"
            {
                Properties { _Color ("Color", Color) = (1,1,1,1) }
                SubShader
                {
                    Pass
                    {
                        CGPROGRAM
                        #include "UnityCG.cginc"
                        float4 _Color;
                        float4 vert(float4 v : POSITION) : SV_POSITION
                        {
                            return UnityObjectToClipPos(v);
                        }
                        float4 frag() : SV_Target { return _Color; }
                        ENDCG
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = Analyze(source, provideUrpHeaders: false);

        Assert.False(Has(diagnostics, "URP0003"), Describe(diagnostics));
        Assert.False(Has(diagnostics, "URP0001"), Describe(diagnostics));
    }

    [Fact]
    public void セマンティックモデルが無い場合は何も報告しない()
    {
        // モデルが無いのは「問題が無い」のではなく「検査していない」状態である。
        // 例外を投げず、誤った指摘も出さないことを確かめる。
        SourceText text = SourceText.From(CorrectUrpShader, "Test.shader");
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);
        AnalysisTarget unit = new(text, tree.Root, tree.Diagnostics);

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(unit);

        Assert.True(diagnostics.IsEmpty, Describe(diagnostics));
    }
}

/// <summary>
/// レンダーパイプラインプロファイルの検証。
/// </summary>
public sealed class RenderPipelineProfileTests
{
    [Fact]
    public void 既定はURPである() => Assert.Equal("urp", RenderPipelineProfiles.Default.Name);

    [Theory]
    [InlineData("urp")]
    [InlineData("brp")]
    [InlineData("hdrp")]
    [InlineData("URP")]
    public void 名前からプロファイルを引ける(string name)
        => Assert.True(RenderPipelineProfiles.TryGet(name, out _));

    [Fact]
    public void 知らない名前は引けない() => Assert.False(RenderPipelineProfiles.TryGet("legacy", out _));

    [Fact]
    public void BuiltInには固有ルールの知識が無い()
    {
        // 差し替え点が実在することの確認。値が空でもクラスが存在することに意味がある。
        BuiltInPipelineProfile profile = new();
        Assert.Null(profile.MaterialConstantBufferName);
        Assert.Empty(profile.ObsoleteApis);
    }
}

/// <summary>
/// HLSL の型分類の検証。
/// </summary>
/// <remarks>
/// <b>分類できないものを分類したことにしないのが最も重要な性質である。</b>
/// 誤った分類は、そのまま型不一致の誤検出になる。
/// </remarks>
public sealed class HlslTypeClassifierTests
{
    [Theory]
    [InlineData("float", HlslTypeClass.Scalar)]
    [InlineData("half", HlslTypeClass.Scalar)]
    [InlineData("int", HlslTypeClass.Scalar)]
    [InlineData("real", HlslTypeClass.Scalar)]
    [InlineData("float1", HlslTypeClass.Scalar)]
    [InlineData("float2", HlslTypeClass.Vector)]
    [InlineData("half4", HlslTypeClass.Vector)]
    [InlineData("float4x4", HlslTypeClass.Matrix)]
    [InlineData("real3x3", HlslTypeClass.Matrix)]
    [InlineData("Texture2D", HlslTypeClass.Texture2D)]
    [InlineData("sampler2D", HlslTypeClass.Texture2D)]
    [InlineData("TextureCube", HlslTypeClass.TextureCube)]
    [InlineData("Texture2DArray", HlslTypeClass.Texture2DArray)]
    [InlineData("SamplerState", HlslTypeClass.Sampler)]
    [InlineData("StructuredBuffer", HlslTypeClass.Buffer)]
    public void 既知の型名を分類できる(string typeName, HlslTypeClass expected)
        => Assert.Equal(expected, HlslTypeClassifier.Classify(typeName));

    [Theory]
    [InlineData("Varyings")]
    [InlineData("MyStruct")]
    [InlineData("float5")]
    [InlineData("floatX")]
    [InlineData("")]
    [InlineData(null)]
    public void 知らない型名は分類しない(string? typeName)
        => Assert.Equal(HlslTypeClass.Unknown, HlslTypeClassifier.Classify(typeName));

    [Theory]
    [InlineData(ShaderPropertyKind.Color, HlslTypeClass.Vector, true)]
    [InlineData(ShaderPropertyKind.Color, HlslTypeClass.Scalar, false)]
    [InlineData(ShaderPropertyKind.Float, HlslTypeClass.Scalar, true)]
    [InlineData(ShaderPropertyKind.Float, HlslTypeClass.Vector, false)]
    [InlineData(ShaderPropertyKind.Texture2D, HlslTypeClass.Texture2D, true)]
    [InlineData(ShaderPropertyKind.Texture2D, HlslTypeClass.TextureCube, false)]
    [InlineData(ShaderPropertyKind.Any, HlslTypeClass.Matrix, true)]
    public void 型の整合性を判定できる(ShaderPropertyKind kind, HlslTypeClass typeClass, bool expected)
        => Assert.Equal(expected, PropertyTypeCompatibility.IsCompatible(kind, typeClass));

    [Fact]
    public void 分類できない型に対しては不一致と断定しない()
    {
        // 判別できないことは、合っていないことの証拠にならない。
        Assert.True(PropertyTypeCompatibility.IsCompatible(ShaderPropertyKind.Color, HlslTypeClass.Unknown));
    }
}

/// <summary>
/// 字句解析キャッシュの検証。
/// </summary>
public sealed class HlslTokenCacheTests
{
    [Fact]
    public void 同じテキストには同じトークン列を返す()
    {
        HlslTokenCache cache = new();
        SourceText text = SourceText.From("float4 _Color;", "a.hlsl");

        ImmutableArray<Hlsl.Syntax.HlslSyntaxToken> first = cache.GetOrLex(text, out _);
        ImmutableArray<Hlsl.Syntax.HlslSyntaxToken> second = cache.GetOrLex(text, out _);

        // 同じ配列が返る (字句解析をやり直していない)。
        Assert.True(first.Equals(second));
    }

    [Fact]
    public void 内容が同じでも別のテキストは別に扱う()
    {
        // キーは参照であって内容ではない。内容をキーにするとハッシュ計算で全文を走査することになり、
        // 字句解析を省いた意味が薄れる。
        HlslTokenCache cache = new();

        ImmutableArray<Hlsl.Syntax.HlslSyntaxToken> first =
            cache.GetOrLex(SourceText.From("float4 _Color;", "a.hlsl"), out _);
        ImmutableArray<Hlsl.Syntax.HlslSyntaxToken> second =
            cache.GetOrLex(SourceText.From("float4 _Color;", "a.hlsl"), out _);

        Assert.False(first.Equals(second));
        Assert.Equal(first.Length, second.Length);
    }
}
