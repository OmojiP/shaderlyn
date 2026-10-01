using System.Collections.Immutable;
using Shaderlyn.Cookbook;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Testing;

namespace Shaderlyn.Tests;

/// <summary>
/// docs/custom-rules/cookbook.md に載せたルールが動くことの検証。
/// </summary>
/// <remarks>
/// <b>「出ること」と「正しいコードに出ないこと」を対で確かめる。</b>
/// 手引きを写した人が最初に踏むのは誤検出であり、それは例のほうの責任である。
/// </remarks>
public sealed class CookbookTests
{
    /// <summary>コードブロックを 1 つ持つシェーダーを組み立てる。</summary>
    /// <param name="body">埋め込む HLSL。</param>
    /// <returns>シェーダーのソース。</returns>
    private static string Shader(string body) =>
        $$"""
        Shader "Test/Cookbook"
        {
            SubShader
            {
                Tags { "RenderType" = "Opaque" }

                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag

                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

        {{body}}
                    ENDHLSL
                }
            }
        }
        """;

    private static ImmutableArray<Diagnostic> Analyze(DiagnosticAnalyzer analyzer, string source)
        => new ShaderRuleVerifier(analyzer).Analyze(source);

    private static void AssertNone(DiagnosticAnalyzer analyzer, string source, AnalyzerOptions? options = null)
    {
        ImmutableArray<Diagnostic> diagnostics = options is null
            ? Analyze(analyzer, source)
            : new ShaderRuleVerifier(analyzer).WithOptions(options).Analyze(source);

        Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics));
    }

    // ------------------------------------------------------------------

    [Fact]
    public void 使ってはいけないマクロを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedMacroAnalyzer("UNITY_MATRIX_MVP"),
            Shader("""
                            #define UNITY_MATRIX_MVP float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1)
                            float4 frag() : SV_Target { return mul(UNITY_MATRIX_MVP, float4(0,0,0,1)); }
                """));

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal("COOK0001", d.Id));
    }

    [Fact]
    public void 使っていないマクロは報告しない()
        => AssertNone(
            new BannedMacroAnalyzer("UNITY_MATRIX_MVP"),
            Shader("                float4 frag() : SV_Target { return 1; }"));

    [Fact]
    public void 使ってはいけない関数を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedFunctionAnalyzer("tex2D"),
            Shader("""
                            float4 tex2D(float2 uv) { return 1; }
                            float4 frag() : SV_Target { return tex2D(float2(0, 0)); }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0002", diagnostic.Id);
        Assert.Contains("tex2D", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 呼んでいない関数は報告しない()
        => AssertNone(
            new BannedFunctionAnalyzer("tex2D"),
            Shader("                float4 frag() : SV_Target { return saturate(1); }"));

    [Fact]
    public void ifを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new NoBranchAnalyzer(),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                if (c.a > 0.5) { return 1; }
                                return 0;
                            }
                """));

        Assert.Equal("COOK0003", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void ifが無ければ報告しない()
        => AssertNone(
            new NoBranchAnalyzer(),
            Shader("                float4 frag() : SV_Target { return 1; }"));

    [Fact]
    public void elseが多すぎる連なりを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new TooManyBranchesAnalyzer(maxElseCount: 2),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                if (c.r > 0.8) { return 4; }
                                else if (c.r > 0.6) { return 3; }
                                else if (c.r > 0.4) { return 2; }
                                else { return 1; }
                            }
                """));

        // 連なりの先頭でだけ数える。3 連なりに対して 1 件だけ出る。
        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0004", diagnostic.Id);
        Assert.Contains("3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 上限までのelseは報告しない()
        => AssertNone(
            new TooManyBranchesAnalyzer(maxElseCount: 2),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                if (c.r > 0.6) { return 3; }
                                else if (c.r > 0.4) { return 2; }
                                else { return 1; }
                            }
                """));

    /// <summary>
    /// シンボルが定義された構成でだけ禁じられる関数を報告する。
    /// </summary>
    /// <remarks>
    /// 出現条件を使うので、#ifdef の外に書いた同じ呼び出しは報告されない。
    /// これが「条件付きの禁止」の意味である。
    /// </remarks>
    [Fact]
    public void 特定の構成でだけ禁じた関数を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedUnderSymbolAnalyzer("_LOW_QUALITY", "ExpensiveCall"),
            Shader("""
                            #pragma multi_compile _ _LOW_QUALITY
                            float4 ExpensiveCall() { return 1; }
                            float4 frag() : SV_Target
                            {
                            #ifdef _LOW_QUALITY
                                return ExpensiveCall();
                            #else
                                return 0;
                            #endif
                            }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0005", diagnostic.Id);
        Assert.Contains("_LOW_QUALITY", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 条件の外の呼び出しは報告しない()
        => AssertNone(
            new BannedUnderSymbolAnalyzer("_LOW_QUALITY", "ExpensiveCall"),
            Shader("""
                            #pragma multi_compile _ _LOW_QUALITY
                            float4 ExpensiveCall() { return 1; }
                            float4 frag() : SV_Target { return ExpensiveCall(); }
                """));

    [Fact]
    public void 引数の型が違えば報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new ArgumentTypeAnalyzer("Blend", parameter: 0, expectedType: "float4"),
            Shader("""
                            float4 Blend(float4 a, float4 b) { return a * b; }
                            float4 frag() : SV_Target
                            {
                                float2 wrong = float2(1, 1);
                                return Blend(wrong, float4(1, 1, 1, 1));
                            }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0006", diagnostic.Id);
        Assert.Contains("float2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 引数の型が合っていれば報告しない()
        => AssertNone(
            new ArgumentTypeAnalyzer("Blend", parameter: 0, expectedType: "float4"),
            Shader("""
                            float4 Blend(float4 a, float4 b) { return a * b; }
                            float4 frag() : SV_Target
                            {
                                float4 ok = float4(1, 1, 1, 1);
                                return Blend(ok, ok);
                            }
                """));

    [Fact]
    public void 禁じた形のオーバーロードを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedOverloadAnalyzer("Mix", "float", "float2"),
            Shader("""
                            float Mix(float a, float b) { return a + b; }
                            float Mix(float a, float2 b) { return a + b.x; }

                            float4 frag() : SV_Target
                            {
                                float value = 1;
                                float2 pair = float2(0, 0);
                                return Mix(value, pair);
                            }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0029", diagnostic.Id);
        Assert.Contains("float, float2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 別のオーバーロードは報告しない()
        => AssertNone(
            new BannedOverloadAnalyzer("Mix", "float", "float2"),
            Shader("""
                            float Mix(float a, float b) { return a + b; }
                            float Mix(float a, float2 b) { return a + b.x; }

                            float4 frag() : SV_Target
                            {
                                float value = 1;
                                float other = 2;
                                return Mix(value, other);
                            }
                """));

    /// <summary>
    /// 書かれた型が仮引数の型と違っても、解決先が禁じた形なら報告することの確認。
    /// </summary>
    /// <remarks>
    /// <b>実引数に書かれた型で照合していたら、この呼び出しは素通りする。</b>
    /// <c>1</c> の型は <c>int</c> であって <c>float</c> ではないが、
    /// 呼ばれるのは <c>Mix(float, float2)</c> のほうである。
    /// </remarks>
    [Fact]
    public void 暗黙の変換で禁じた形に落ちる呼び出しも報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedOverloadAnalyzer("Mix", "float", "float2"),
            Shader("""
                            float Mix(float a, float b) { return a + b; }
                            float Mix(float a, float2 b) { return a + b.x; }

                            float4 frag() : SV_Target
                            {
                                float2 pair = float2(0, 0);
                                return Mix(1, pair);
                            }
                """));

        Assert.Equal("COOK0029", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void 引数の数が違えば別のオーバーロードとして扱う()
        => AssertNone(
            new BannedOverloadAnalyzer("Mix", "float", "float2"),
            Shader("""
                            float Mix(float a, float2 b) { return a + b.x; }
                            float Mix(float a, float2 b, float t) { return lerp(a, b.x, t); }

                            float4 frag() : SV_Target
                            {
                                float value = 1;
                                float2 pair = float2(0, 0);
                                return Mix(value, pair, 0.5);
                            }
                """));

    /// <summary>
    /// どれが呼ばれるか決まらない呼び出しは見送ることの確認。
    /// </summary>
    /// <remarks>
    /// 型の分からない引数が、そのまま候補を分けている。
    /// <b>決まっていないものを「その形で呼んでいる」とは言えない。</b>
    /// </remarks>
    [Fact]
    public void 呼ばれる先が決まらなければ見送る()
        => AssertNone(
            new BannedOverloadAnalyzer("Mix", "float2"),
            Shader("""
                            float Mix(float2 a) { return a.x; }
                            float Mix(float3 a) { return a.x; }

                            float4 frag() : SV_Target
                            {
                                return Mix(Untyped());
                            }
                """));

    /// <summary>
    /// メンバー呼び出しは報告しないことの確認。
    /// </summary>
    /// <remarks>
    /// テクスチャのメソッドには宣言が無く、解決先を指せない。
    /// <see cref="OverloadResolutionStatus.Intrinsic"/> が返るので、この例は報告しない。
    /// </remarks>
    [Fact]
    public void メンバー呼び出しは報告しない()
        => AssertNone(
            new BannedOverloadAnalyzer("SampleLevel", null, null, "float"),
            Shader("""
                            Texture2D _Tex;
                            SamplerState sampler_Tex;

                            float4 frag(float2 uv : TEXCOORD0) : SV_Target
                            {
                                float lod = 0;
                                return _Tex.SampleLevel(sampler_Tex, uv, lod);
                            }
                """));

    [Fact]
    public void 気にしない位置はnullで飛ばせる()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedOverloadAnalyzer("Mix", null, "float2"),
            Shader("""
                            float Mix(float a, float b) { return a + b; }
                            float Mix(float a, float2 b) { return a + b.x; }

                            float4 frag() : SV_Target
                            {
                                float2 pair = float2(0, 0);
                                return Mix(1, pair);
                            }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0029", diagnostic.Id);
        Assert.Contains("float, float2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 大きすぎるリテラルを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new LiteralLimitAnalyzer(maximum: 64),
            Shader("                float4 frag() : SV_Target { return 128.0; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0007", diagnostic.Id);
        Assert.Contains("128.0", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 閾値以下のリテラルは報告しない()
        => AssertNone(
            new LiteralLimitAnalyzer(maximum: 64),
            Shader("                float4 frag() : SV_Target { return 32.0; }"));

    // ------------------------------------------------------------------
    // ShaderLab
    // ------------------------------------------------------------------

    [Fact]
    public void プロパティ名の接頭辞を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new PropertyNamePrefixAnalyzer("_"))
            .Analyze("""
                Shader "Test/Properties"
                {
                    Properties
                    {
                        _BaseColor ("Base Color", Color) = (1,1,1,1)
                        Amount ("Amount", Float) = 1
                    }
                    SubShader { Pass { } }
                }
                """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0008", diagnostic.Id);
        Assert.Contains("Amount", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTypeが無いSubShaderを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new RequiredTagAnalyzer("RenderType"))
            .Analyze("""
                Shader "Test/Tags"
                {
                    SubShader
                    {
                        Tags { "Queue" = "Geometry" }
                        Pass { }
                    }
                }
                """);

        Assert.Equal("COOK0009", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void RenderTypeがあれば報告しない()
        => AssertNone(
            new RequiredTagAnalyzer("RenderType"),
            """
            Shader "Test/Tags"
            {
                SubShader
                {
                    Tags { "RenderType" = "Opaque" }
                    Pass { }
                }
            }
            """);

    [Fact]
    public void Passにだけ書いたタグはSubShaderのものとして数えない()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new RequiredTagAnalyzer("RenderType"))
            .Analyze("""
                Shader "Test/Tags"
                {
                    SubShader
                    {
                        Pass { Tags { "RenderType" = "Opaque" } }
                    }
                }
                """);

        Assert.Equal("COOK0009", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void 許していないタグの値を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new RequiredTagAnalyzer("RenderType", "Opaque", "TransparentCutout"))
                .Analyze("""
                    Shader "Test/Tags"
                    {
                        SubShader
                        {
                            Tags { "RenderType" = "Transparent" }
                            Pass { }
                        }
                    }
                    """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0014", diagnostic.Id);
        Assert.Contains("Transparent", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 許しているタグの値は報告しない()
        => AssertNone(
            new RequiredTagAnalyzer("RenderType", "Opaque", "TransparentCutout"),
            """
            Shader "Test/Tags"
            {
                SubShader
                {
                    Tags { "RenderType" = "Opaque" }
                    Pass { }
                }
            }
            """);

    [Fact]
    public void 許していない命令の値を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new CommandValueAnalyzer("Cull", "Back", "Front"))
                .Analyze("""
                    Shader "Test/Commands"
                    {
                        SubShader
                        {
                            Pass { Cull Off }
                        }
                    }
                    """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0015", diagnostic.Id);
        Assert.Contains("Off", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void プロパティで決まる命令の値は見送る()
        => AssertNone(
            new CommandValueAnalyzer("Cull", "Back", "Front"),
            """
            Shader "Test/Commands"
            {
                Properties { _Cull ("Cull", Float) = 2 }
                SubShader
                {
                    Pass { Cull [_Cull] }
                }
            }
            """);

    [Fact]
    public void Passが多すぎるSubShaderを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new PassCountLimitAnalyzer(maximum: 2))
            .Analyze("""
                Shader "Test/Passes"
                {
                    SubShader
                    {
                        Pass { }
                        Pass { }
                        Pass { }
                    }
                }
                """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0010", diagnostic.Id);
        Assert.Contains("3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // コンピュートシェーダー
    // ------------------------------------------------------------------

    private const string ComputePath = "Assets/Test.compute";

    [Fact]
    public void スレッド数が倍数でないカーネルを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new ThreadGroupSizeAnalyzer(multipleOf: 64))
            .Analyze(
                """
                #pragma kernel CSMain

                RWStructuredBuffer<float4> Result;

                [numthreads(7, 1, 1)]
                void CSMain(uint3 id : SV_DispatchThreadID) { Result[id.x] = 1; }
                """,
                ComputePath);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0011", diagnostic.Id);
        Assert.Contains("CSMain", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void スレッド数が倍数なら報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new ThreadGroupSizeAnalyzer(multipleOf: 64))
            .Analyze(
                """
                #pragma kernel CSMain

                RWStructuredBuffer<float4> Result;

                [numthreads(8, 8, 1)]
                void CSMain(uint3 id : SV_DispatchThreadID) { Result[id.x] = 1; }
                """,
                ComputePath);

        Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics));
    }

    [Fact]
    public void カーネル名の接頭辞を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new KernelNamePrefixAnalyzer("CS"))
            .Analyze(
                """
                #pragma kernel CSMain
                #pragma kernel Helper

                RWStructuredBuffer<float4> Result;

                [numthreads(8, 1, 1)]
                void CSMain(uint3 id : SV_DispatchThreadID) { Result[id.x] = 1; }

                [numthreads(8, 1, 1)]
                void Helper(uint3 id : SV_DispatchThreadID) { Result[id.x] = 2; }
                """,
                ComputePath);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0012", diagnostic.Id);
        Assert.Contains("Helper", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 書き込めるバッファの名前を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new WritableBufferNamingAnalyzer("Out"))
                .Analyze(
                    """
                    #pragma kernel CSMain

                    StructuredBuffer<float4> Input;
                    RWStructuredBuffer<float4> Result;

                    [numthreads(8, 1, 1)]
                    void CSMain(uint3 id : SV_DispatchThreadID) { Result[id.x] = Input[id.x]; }
                    """,
                    ComputePath);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0013", diagnostic.Id);
        Assert.Contains("Result", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 読み取り専用のバッファは対象にしない()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new WritableBufferNamingAnalyzer("Out"))
                .Analyze(
                    """
                    #pragma kernel CSMain

                    StructuredBuffer<float4> Input;
                    RWStructuredBuffer<float4> OutResult;

                    [numthreads(8, 1, 1)]
                    void CSMain(uint3 id : SV_DispatchThreadID) { OutResult[id.x] = Input[id.x]; }
                    """,
                    ComputePath);

        Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // 型・演算子・セマンティクスを禁じる
    // ------------------------------------------------------------------

    [Fact]
    public void 使ってはいけない型を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedTypeAnalyzer("half"),
            Shader("                half4 frag() : SV_Target { return 1; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0016", diagnostic.Id);
        Assert.Contains("half4", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 禁じていない型は報告しない()
        => AssertNone(
            new BannedTypeAnalyzer("half"),
            Shader("""
                            Texture2D _Tex;
                            float4 frag() : SV_Target { return 1; }
                """));

    [Fact]
    public void 使ってはいけない演算子を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedOperatorAnalyzer("%"),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                int x = (int)c.r % 2;
                                x %= 3;
                                return x;
                            }
                """));

        // 二項演算と複合代入の両方で出る。
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("COOK0017", d.Id));
    }

    [Fact]
    public void 比較演算子や他の複合代入は巻き込まない()
        => AssertNone(
            new BannedOperatorAnalyzer("%"),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                float x = c.r;
                                x /= 2;
                                return c.r <= 0.5 ? x : 1;
                            }
                """));

    [Fact]
    public void 使ってはいけないセマンティクスを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new BannedSemanticAnalyzer("VFACE"),
            Shader("                float4 frag(float face : VFACE) : SV_Target { return face; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0018", diagnostic.Id);
        Assert.Contains("VFACE", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void レジスタ指定はセマンティクスとして扱わない()
        => AssertNone(
            new BannedSemanticAnalyzer("VFACE", "register"),
            Shader("""
                            Texture2D _Tex : register(t0);
                            float4 frag() : SV_Target { return 1; }
                """));

    // ------------------------------------------------------------------
    // #pragma
    // ------------------------------------------------------------------

    [Fact]
    public void 必須のpragmaが無いコードブロックを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new RequiredPragmaAnalyzer("multi_compile_instancing"),
            Shader("                float4 frag() : SV_Target { return 1; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0019", diagnostic.Id);
        Assert.Contains("multi_compile_instancing", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 必須のpragmaがあれば報告しない()
        => AssertNone(
            new RequiredPragmaAnalyzer("multi_compile_instancing"),
            Shader("""
                            #pragma multi_compile_instancing
                            float4 frag() : SV_Target { return 1; }
                """));

    [Fact]
    public void シェーダーモデルの指定が無いコードブロックを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new ShaderModelMinimumAnalyzer("4.5"),
            Shader("                float4 frag() : SV_Target { return 1; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0020", diagnostic.Id);
        Assert.Contains("指定なし", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 低いシェーダーモデルを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new ShaderModelMinimumAnalyzer("4.5"),
            Shader("""
                            #pragma target 3.0
                            float4 frag() : SV_Target { return 1; }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0020", diagnostic.Id);
        Assert.Contains("3.0", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 下限を満たすシェーダーモデルは報告しない()
        => AssertNone(
            new ShaderModelMinimumAnalyzer("4.5"),
            Shader("""
                            #pragma target 4.5
                            float4 frag() : SV_Target { return 1; }
                """));

    [Fact]
    public void 数として読めないシェーダーモデルは見送る()
        => AssertNone(
            new ShaderModelMinimumAnalyzer("4.5"),
            Shader("""
                            #pragma target es3.1
                            float4 frag() : SV_Target { return 1; }
                """));

    [Fact]
    public void シンボルが多すぎるコードブロックを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new SymbolBudgetAnalyzer(maxSymbols: 2),
            Shader("""
                            #pragma multi_compile _ FEATURE_A FEATURE_B
                            #pragma shader_feature_local FEATURE_C
                            float4 frag() : SV_Target { return 1; }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0021", diagnostic.Id);
        Assert.Contains("3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 上限までのシンボルは報告しない()
        => AssertNone(
            new SymbolBudgetAnalyzer(maxSymbols: 2),
            Shader("""
                            #pragma multi_compile _ FEATURE_A
                            float4 frag() : SV_Target { return 1; }
                """));

    // ------------------------------------------------------------------
    // #include
    // ------------------------------------------------------------------

    [Fact]
    public void 取り込んではいけないヘッダを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new BannedIncludeAnalyzer("UnityCG.cginc"))
                .AddInclude("UnityCG.cginc", "#define UNITY_CG_INCLUDED 1")
                .Analyze(Shader("""
                                #include "UnityCG.cginc"
                                float4 frag() : SV_Target { return 1; }
                    """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0022", diagnostic.Id);
        Assert.Contains("UnityCG.cginc", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 禁じていないヘッダは報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new BannedIncludeAnalyzer("UnityCG.cginc"))
                .AddInclude("Common.hlsl", "#define COMMON_INCLUDED 1")
                .Analyze(Shader("""
                                #include "Common.hlsl"
                                float4 frag() : SV_Target { return 1; }
                    """));

        Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics));
    }

    [Fact]
    public void 必須のヘッダが無いコードブロックを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new RequiredIncludeAnalyzer("Common.hlsl"),
            Shader("                float4 frag() : SV_Target { return 1; }"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0023", diagnostic.Id);
        Assert.Contains("Common.hlsl", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 必須のヘッダがあれば報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new RequiredIncludeAnalyzer("Common.hlsl"))
                .AddInclude("Common.hlsl", "#define COMMON_INCLUDED 1")
                .Analyze(Shader("""
                                #include "Common.hlsl"
                                float4 frag() : SV_Target { return 1; }
                    """));

        Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics));
    }

    // ------------------------------------------------------------------
    // 予算と複雑さ
    // ------------------------------------------------------------------

    [Fact]
    public void サンプリングが多すぎるコードブロックを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new SampleBudgetAnalyzer(maxSamples: 2),
            Shader("""
                            sampler2D _MainTex;
                            Texture2D _Extra;
                            SamplerState sampler_Extra;

                            float4 frag(float2 uv : TEXCOORD0) : SV_Target
                            {
                                float4 a = tex2D(_MainTex, uv);
                                float4 b = tex2D(_MainTex, uv + 0.1);
                                float4 c = _Extra.Sample(sampler_Extra, uv);
                                return a + b + c;
                            }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0024", diagnostic.Id);
        Assert.Contains("3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 予算内のサンプリングは報告しない()
        => AssertNone(
            new SampleBudgetAnalyzer(maxSamples: 2),
            Shader("""
                            sampler2D _MainTex;

                            float4 frag(float2 uv : TEXCOORD0) : SV_Target
                            {
                                return tex2D(_MainTex, uv) + tex2D(_MainTex, uv + 0.1);
                            }
                """));

    /// <summary>キーワードで書き分けたサンプリング。木には 4 回あるが、どの構成でも 3 回である。</summary>
    private const string SamplesByKeyword = """
                            #pragma multi_compile _ _A
                            sampler2D _MainTex;

                            float4 frag(float2 uv : TEXCOORD0) : SV_Target
                            {
                                float4 a = tex2D(_MainTex, uv);
                                float4 b = tex2D(_MainTex, uv + 0.1);
                            #ifdef _A
                                float4 c = tex2D(_MainTex, uv + 0.2);
                            #else
                                float4 c = tex2D(_MainTex, uv + 0.3);
                            #endif
                                return a + b + c;
                            }
        """;

    [Fact]
    public void 同時には存在しないサンプリングは足し合わせない()
        => AssertNone(new SampleBudgetAnalyzer(maxSamples: 3), Shader(SamplesByKeyword));

    [Fact]
    public void 構成ごとに数えたサンプリングが予算を超えれば報告する()
    {
        Diagnostic diagnostic = Assert.Single(Analyze(new SampleBudgetAnalyzer(maxSamples: 2), Shader(SamplesByKeyword)));

        Assert.Contains("3", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 同時には存在しない文は関数の長さに足し合わせない()
        => AssertNone(
            new FunctionLengthLimitAnalyzer(maxStatements: 3),
            Shader("""
                            #pragma multi_compile _ _A
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                            #ifdef _A
                                float a = c.r;
                                float b = c.g;
                            #else
                                float a = c.b;
                                float b = c.a;
                            #endif
                                return a + b;
                            }
                """));

    [Fact]
    public void 同時には存在しないメンバーは定数バッファの大きさに足し合わせない()
        => AssertNone(
            new ConstantBufferSizeLimitAnalyzer(maxBytes: 48),
            Shader("""
                            #pragma multi_compile _ _A
                            cbuffer UnityPerMaterial
                            {
                            #ifdef _A
                                float4 _ColorA;
                                float4 _TintA;
                            #else
                                float4 _ColorB;
                                float4 _TintB;
                            #endif
                                float4 _Common;
                            };
                            float4 frag() : SV_Target { return _Common; }
                """));

    [Fact]
    public void 入れ子が深すぎる箇所を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new NestingDepthLimitAnalyzer(maxDepth: 3),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                for (int i = 0; i < 4; i++)
                                {
                                    if (c.r > 0.5)
                                    {
                                        while (c.g > 0.5)
                                        {
                                            if (c.b > 0.5) { return 1; }
                                        }
                                    }
                                }
                                return 0;
                            }
                """));

        // 上限を超えた最初の段でだけ出る。
        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0025", diagnostic.Id);
        Assert.Contains("4", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void elseifの連なりは段を増やさない()
        => AssertNone(
            new NestingDepthLimitAnalyzer(maxDepth: 3),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                for (int i = 0; i < 4; i++)
                                {
                                    if (c.r > 0.8) { return 4; }
                                    else if (c.r > 0.6) { return 3; }
                                    else if (c.r > 0.4) { return 2; }
                                }
                                return 0;
                            }
                """));

    [Fact]
    public void 長すぎる関数を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new FunctionLengthLimitAnalyzer(maxStatements: 3),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                float a = c.r;
                                float b = c.g;
                                float d = c.b;
                                float e = a + b;
                                return e + d;
                            }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0026", diagnostic.Id);
        Assert.Contains("frag", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 上限までの長さの関数は報告しない()
        => AssertNone(
            new FunctionLengthLimitAnalyzer(maxStatements: 3),
            Shader("""
                            float4 frag(float4 c : COLOR) : SV_Target
                            {
                                float a = c.r;
                                return a;
                            }
                """));

    /// <summary>仮引数が 4 個の関数。上限を変えると結果が変わる。</summary>
    private const string FourParameterFunction = """
                            float4 mix4(float a, float b, float c, float d) { return a + b + c + d; }
                            float4 frag() : SV_Target { return mix4(1, 2, 3, 4); }
        """;

    /// <summary>設定ファイルの options を再現する。</summary>
    /// <param name="ruleId">ルール ID。</param>
    /// <param name="name">オプション名。</param>
    /// <param name="value">値。</param>
    /// <returns>そのオプションだけを持つ実行時設定。</returns>
    private static AnalyzerOptions RuleOption(string ruleId, string name, string value)
        => new(ruleOptions: ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
            .Add(ruleId, ImmutableDictionary<string, string>.Empty.Add(name, value)));

    [Fact]
    public void 設定が無ければコンストラクタの既定値で判定する()
        => AssertNone(new ParameterCountLimitAnalyzer(defaultMaxParameters: 8), Shader(FourParameterFunction));

    [Fact]
    public void 設定ファイルのoptionsで上限を下げられる()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(
                new ParameterCountLimitAnalyzer(defaultMaxParameters: 8))
            .WithOptions(RuleOption("COOK0030", "maxParameters", "2"))
            .Analyze(Shader(FourParameterFunction));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0030", diagnostic.Id);
        Assert.Contains("mix4", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 数値として読めない値でも解析が止まらないことを検証する。
    /// </summary>
    /// <remarks>
    /// 書き間違いは設定の読み込み側が <c>TOOL0003</c> で報告する。
    /// ルールの側は既定値へ倒し、解析そのものは続ける。
    /// </remarks>
    [Fact]
    public void 読めない値のoptionsは既定値へ倒す()
        => AssertNone(
            new ParameterCountLimitAnalyzer(defaultMaxParameters: 8),
            Shader(FourParameterFunction),
            RuleOption("COOK0030", "maxParameters", "たくさん"));

    [Fact]
    public void 回数が多すぎるループを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new LoopBoundAnalyzer(maxIterations: 64),
            Shader("""
                            float4 frag() : SV_Target
                            {
                                float s = 0;
                                for (int i = 0; i < 256; i++) { s += i; }
                                return s;
                            }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0027", diagnostic.Id);
        Assert.Contains("256", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 回数が定数で決まらないループは見送る()
        => AssertNone(
            new LoopBoundAnalyzer(maxIterations: 64),
            Shader("""
                            int _Count;

                            float4 frag() : SV_Target
                            {
                                float s = 0;
                                for (int i = 0; i < _Count; i++) { s += i; }
                                return s;
                            }
                """));

    [Fact]
    public void 大きすぎる定数バッファを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            new ConstantBufferSizeLimitAnalyzer(maxBytes: 64),
            Shader("""
                            cbuffer UnityPerMaterial
                            {
                                float4 _Colors[8];
                                float _Cutoff;
                            };

                            float4 frag() : SV_Target { return _Colors[0] * _Cutoff; }
                """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COOK0028", diagnostic.Id);
        Assert.Contains("UnityPerMaterial", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 収まっている定数バッファは報告しない()
        => AssertNone(
            new ConstantBufferSizeLimitAnalyzer(maxBytes: 64),
            Shader("""
                            cbuffer UnityPerMaterial
                            {
                                float4 _Color;
                                float _Cutoff;
                            };

                            float4 frag() : SV_Target { return _Color * _Cutoff; }
                """));

    [Fact]
    public void 大きさの分からない型があれば定数バッファを見送る()
        => AssertNone(
            new ConstantBufferSizeLimitAnalyzer(maxBytes: 16),
            Shader("""
                            struct Payload { float4 a; float4 b; };

                            cbuffer UnityPerMaterial
                            {
                                Payload _Payload;
                                float4 _Color;
                            };

                            float4 frag() : SV_Target { return _Color; }
                """));
}
