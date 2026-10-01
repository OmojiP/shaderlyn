using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;
using Shaderlyn.Testing;

namespace Shaderlyn.Tests;

/// <summary>
/// 利用者が自作するルールの経路 (docs/custom-rules/tutorial.md) の検証。
/// </summary>
/// <remarks>
/// <b>これは利用者との契約である。</b>
/// 手順書に書いたとおりに書けば動くこと、
/// 実装を間違えたときに、何も伝えられないまま壊れないことを、ここで確かめる。
/// </remarks>
public sealed class CustomRuleTests
{
    private const string SampleShader = """
        Shader "Test/Custom"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag

                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

                    half4 frag() : SV_Target
                    {
                        return half4(1, 1, 1, 1);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    /// <summary>手順書どおりに書いた、最小のルール。</summary>
    private sealed class HalfIsBannedAnalyzer : HlslRuleAnalyzer
    {
        public static DiagnosticDescriptor Rule { get; } = new(
            id: "TEST0001",
            title: "half を使わない",
            messageFormat: "型 '{0}' は使わないでください。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "このプロジェクトでは half を使わない決まりです。");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        protected override void InitializeHlsl(HlslAnalysisContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.RegisterNodeAction<HlslTypeSyntax>(c =>
            {
                if (c.Node.Name == "half4")
                {
                    c.ReportDiagnostic(Rule, c.Node.Name);
                }
            });
        }
    }

    /// <summary>
    /// バリアントにしか現れないコードも歩くことを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>#ifdef</c> が文の途中で切れているため、この領域は 1 本の木に並べられず、
    /// シンボルを有効にした構成として別に展開される。
    /// その木にしか無いノードは、突き合わせのときに「足すノード」として記録されている。
    /// </para>
    /// <para>
    /// 歩かないと、この分岐の中のコードだけ検査されない。
    /// CI では落ちるのにエディタでは何も出ない、という食い違いになる。
    /// </para>
    /// </remarks>
    [Fact]
    public void バリアントにしか無いコードも検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new HalfIsBannedAnalyzer())
            .Analyze("""
                Shader "Test/Variant"
                {
                    SubShader
                    {
                        Pass
                        {
                            HLSLPROGRAM
                            #pragma vertex vert
                            #pragma fragment frag
                            #pragma multi_compile _ _A

                            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

                            float4 frag() : SV_Target
                            {
                                float4 c;
                            #ifdef _A
                                c = (half4)1
                            #else
                                c = 1
                            #endif
                                ;
                                return c;
                            }
                            ENDHLSL
                        }
                    }
                }
                """);

        Diagnostic diagnostic = Assert.Single(diagnostics);

        Assert.Equal("TEST0001", diagnostic.Id);
        Assert.Contains("half4", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>識別子 <c>v</c> の型を、そのノードが属する構成で求めて報告するルール。</summary>
    private sealed class VariableTypeAnalyzer : HlslRuleAnalyzer
    {
        public static DiagnosticDescriptor Rule { get; } = new(
            id: "TEST0010",
            title: "v の型を報告する",
            messageFormat: "'{0}' は {1} です。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        protected override void InitializeHlsl(HlslAnalysisContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.RegisterNodeAction<IdentifierExpressionSyntax>(c =>
            {
                if (c.Node.Name != "v")
                {
                    return;
                }

                string? type = c.Compilation.GetExpressionTypeBinder(c.Program).GetEvaluatorFor(c.Node).Evaluate(c.Node);
                c.ReportDiagnostic(Rule, c.Node.Name, type ?? "不明");
            });
        }
    }

    /// <summary>
    /// 形は同じでも構成によって意味が変わるコードを、その構成の文脈で渡すことを検証する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ヘッダが分岐ごとに同じマクロを違う中身で定義しているので、両方の分岐を 1 本の木に並べられず、
    /// <c>_LOW_PRECISION</c> の構成として別に展開される。
    /// <c>PRECISE_VECTOR v = 0;</c> は既定の木にもバリアントの木にも同じ形であるので、
    /// 突き合わせは「同じ」と見て既定の木へ足さない。
    /// </para>
    /// <para>
    /// 既定の木だけを歩くと、<c>v</c> は <c>float3</c> にしか見えない。
    /// 組み込みルールはバリアントの木も歩いて <c>float2</c> の構成の誤りを見つけるのに、
    /// 自作ルールだけがそれを見られない、という食い違いになる。
    /// </para>
    /// </remarks>
    [Fact]
    public void バリアントの構成の文脈でも渡す()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new VariableTypeAnalyzer())
            .AddInclude("Precision.hlsl", """
                #pragma multi_compile _HIGH_PRECISION _LOW_PRECISION
                #ifdef _HIGH_PRECISION
                #define PRECISE_VECTOR float3
                #else
                #define PRECISE_VECTOR float2
                #endif
                """)
            .Analyze("""
                Shader "Test/VariantContext"
                {
                    SubShader
                    {
                        Pass
                        {
                            HLSLPROGRAM
                            #pragma vertex vert
                            #pragma fragment frag
                            #include "Precision.hlsl"

                            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

                            half4 frag() : SV_Target
                            {
                                PRECISE_VECTOR v = 0;
                                return half4(v.x, 0, 0, 1);
                            }
                            ENDHLSL
                        }
                    }
                }
                """);

        Assert.Contains(diagnostics, d => d.GetMessage().Contains("float3", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("float2", StringComparison.Ordinal));
    }

    [Fact]
    public void 自作ルールが指摘を出す()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new HalfIsBannedAnalyzer()).Analyze(SampleShader);

        Diagnostic diagnostic = Assert.Single(diagnostics);

        Assert.Equal("TEST0001", diagnostic.Id);
        Assert.Contains("half4", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 自作ルールのテストに組み込みルールが混ざらないことを検証する。
    /// </summary>
    /// <remarks>
    /// 混ざると、自作ルールのテストが組み込みルールの変更で落ちるようになる。
    /// </remarks>
    [Fact]
    public void 渡したアナライザだけが走る()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new HalfIsBannedAnalyzer()).Analyze(SampleShader);

        Assert.All(diagnostics, d => Assert.Equal("TEST0001", d.Id));
    }

    [Fact]
    public void 設定でルールを無効にできる()
    {
        AnalyzerOptions options = new(
            ImmutableDictionary<string, DiagnosticSeverity>.Empty
                .Add("TEST0001", DiagnosticSeverity.None));

        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new HalfIsBannedAnalyzer())
            .WithOptions(options)
            .Analyze(SampleShader);

        Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics));
    }

    /// <summary>
    /// 取り込んだヘッダの中身は歩かないことを検証する。
    /// </summary>
    /// <remarks>
    /// <b>他人のコードを指摘しても直しようがない。</b>
    /// あわせて、テストがディスク上の Unity のインストール状態に依存しないことも確かめる。
    /// </remarks>
    [Fact]
    public void 取り込んだヘッダの中身は指摘しない()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new HalfIsBannedAnalyzer())
            .AddInclude("MyLib.hlsl", "half4 Tint(half4 c) { return c; }")
            .Analyze("""
                Shader "Test/Include"
                {
                    SubShader
                    {
                        Pass
                        {
                            HLSLPROGRAM
                            #pragma vertex vert
                            #pragma fragment frag
                            #include "MyLib.hlsl"

                            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                            float4 frag() : SV_Target { return Tint(1); }
                            ENDHLSL
                        }
                    }
                }
                """);

        Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics));
    }

    /// <summary>
    /// 共通コード片を Pass の数だけ報告しないことを検証する。
    /// </summary>
    /// <remarks>
    /// <c>HLSLINCLUDE</c> のコードは各 Pass の木に現れる。
    /// 素朴に歩くと、同じ 1 行が Pass の数だけ指摘される。
    /// </remarks>
    [Fact]
    public void 共通コード片は一度しか指摘しない()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new HalfIsBannedAnalyzer())
            .Analyze("""
                Shader "Test/Shared"
                {
                    SubShader
                    {
                        HLSLINCLUDE
                        half4 Shared() { return half4(1, 1, 1, 1); }
                        ENDHLSL

                        Pass
                        {
                            HLSLPROGRAM
                            #pragma vertex vert
                            #pragma fragment frag
                            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                            float4 frag() : SV_Target { return Shared(); }
                            ENDHLSL
                        }

                        Pass
                        {
                            HLSLPROGRAM
                            #pragma vertex vert2
                            #pragma fragment frag2
                            float4 vert2(float4 p : POSITION) : SV_POSITION { return p; }
                            float4 frag2() : SV_Target { return Shared(); }
                            ENDHLSL
                        }
                    }
                }
                """);

        // 型の構文としての half4 は、共通コード片の戻り値の 1 か所だけである
        // (half4(1, 1, 1, 1) は型ではなく呼び出しの式)。Pass が 2 つあっても 1 件のままである。
        // 以前は Pass ごとのテキストの実体を鍵にしていたので Pass の数だけ報告しており、このテストはそれを 2 件と数えていた。
        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("6:9", $"{diagnostic.Location.LineSpan.Start.Line + 1}:{diagnostic.Location.LineSpan.Start.Character + 1}");
    }

    [Fact]
    public void computeも解析できる()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new HalfIsBannedAnalyzer())
            .Analyze(
                """
                #pragma kernel CSMain

                RWStructuredBuffer<float4> outputData;

                [numthreads(8, 1, 1)]
                void CSMain(uint3 id : SV_DispatchThreadID)
                {
                    half4 value = half4(1, 1, 1, 1);
                    outputData[id.x] = value;
                }
                """,
                "Assets/Test.compute");

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal("TEST0001", d.Id));
    }

    // ------------------------------------------------------------------
    // 実装を間違えたときに、何も伝えられないまま壊れないこと
    // ------------------------------------------------------------------

    /// <summary>SupportedDiagnostics に申告し忘れたルール。</summary>
    private sealed class UndeclaredRuleAnalyzer : DiagnosticAnalyzer
    {
        public static DiagnosticDescriptor Declared { get; } = new(
            id: "TEST0002",
            title: "申告しているルール",
            messageFormat: "申告しているルールです。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        public static DiagnosticDescriptor Forgotten { get; } = new(
            id: "TEST0003",
            title: "申告し忘れたルール",
            messageFormat: "申告し忘れたルールです。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Declared];

        public override void Initialize(AnalysisContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.RegisterSyntaxTreeAction(c => c.ReportDiagnostic(Forgotten, new Core.Text.TextSpan(0, 0)));
        }
    }

    /// <summary>
    /// 申告漏れを、何も伝えずに通さないことを検証する。
    /// </summary>
    /// <remarks>
    /// <b>申告漏れのルールは設定ファイルから無効にできない。</b>
    /// 利用者から見ると「消し方の分からない指摘」になる。
    /// Roslyn では実行時に壊れても何も知らせない種類の誤りなので、ここでは必ず表に出す。
    /// </remarks>
    [Fact]
    public void 申告していないルールを報告したら知らせる()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new UndeclaredRuleAnalyzer()).Analyze(SampleShader);

        // 指摘そのものは通す。中身は正しいかもしれない。
        Assert.Contains(diagnostics, d => d.Id == "TEST0003");

        Diagnostic reported = Assert.Single(diagnostics.Where(d => d.Id == "TOOL0005"));
        Assert.Contains("UndeclaredRuleAnalyzer", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("TEST0003", reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>メッセージの引数を渡し忘れたルール。</summary>
    private sealed class MissingArgumentAnalyzer : DiagnosticAnalyzer
    {
        public static DiagnosticDescriptor Rule { get; } = new(
            id: "TEST0004",
            title: "引数を必要とするルール",
            messageFormat: "'{0}' と '{1}' が食い違っています。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            // 穴が 2 つあるのに 1 つしか渡していない。
            context.RegisterSyntaxTreeAction(c => c.ReportDiagnostic(Rule, new Core.Text.TextSpan(0, 0), "A"));
        }
    }

    /// <summary>
    /// 書式の穴が埋まらないまま表示されるのを知らせることを検証する。
    /// </summary>
    /// <remarks>
    /// 出力としては成立してしまうので、これを見ていないと
    /// 「'{1}' が食い違っています」という文面が利用者に届く。
    /// </remarks>
    [Fact]
    public void メッセージの引数が足りなければ知らせる()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new MissingArgumentAnalyzer()).Analyze(SampleShader);

        Diagnostic reported = Assert.Single(diagnostics.Where(d => d.Id == "TOOL0006"));

        Assert.Contains("TEST0004", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("2", reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 同じ誤りを何度も報告しないことを検証する。
    /// </summary>
    /// <remarks>
    /// ノードごとに報告するルールで誤ると、出力が同じ行で埋まって元の指摘が読めなくなる。
    /// </remarks>
    [Fact]
    public void 同じ実装の誤りは一度だけ知らせる()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new NoisyUndeclaredAnalyzer()).Analyze(SampleShader);

        Assert.Single(diagnostics.Where(d => d.Id == "TOOL0005"));
        Assert.True(diagnostics.Count(d => d.Id == "TEST0005") > 1, ShaderRuleVerifier.Describe(diagnostics));
    }

    /// <summary>申告漏れのルールを、ノードごとに何度も報告するアナライザ。</summary>
    private sealed class NoisyUndeclaredAnalyzer : HlslRuleAnalyzer
    {
        public static DiagnosticDescriptor Declared { get; } = new(
            id: "TEST0006",
            title: "申告しているルール",
            messageFormat: "申告しているルールです。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        public static DiagnosticDescriptor Forgotten { get; } = new(
            id: "TEST0005",
            title: "申告し忘れたルール",
            messageFormat: "申告し忘れたルールです。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Declared];

        protected override void InitializeHlsl(HlslAnalysisContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.RegisterNodeAction<HlslTypeSyntax>(c => c.ReportDiagnostic(Forgotten));
        }
    }

    /// <summary>
    /// 手順書に載せた ShaderLab のルールが、そのまま動くことを検証する。
    /// </summary>
    /// <remarks>
    /// <b>手順書のコードが動かないのは、書いていないのより悪い。</b>
    /// 載せた形はここで固定する。
    /// </remarks>
    [Fact]
    public void 手順書のShaderLabのルールが動く()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new PassCountAnalyzer()).Analyze(SampleShader);

        Assert.Equal("TEST0008", Assert.Single(diagnostics).Id);
    }

    /// <summary>ShaderLab のノードを見るルール。手順書の 6 章と同じ形。</summary>
    private sealed class PassCountAnalyzer : DiagnosticAnalyzer
    {
        public static DiagnosticDescriptor Rule { get; } = new(
            id: "TEST0008",
            title: "Pass を数える",
            messageFormat: "Pass があります。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.RegisterNodeAction<ShaderLab.Syntax.PassSyntax>(c => c.ReportDiagnostic(Rule));
        }
    }

    /// <summary>
    /// 手順書に載せた「集めてから判定する」形が動くことを検証する。
    /// </summary>
    [Fact]
    public void 手順書の集計するルールが動く()
    {
        ImmutableArray<Diagnostic> diagnostics =
            new ShaderRuleVerifier(new CountingAnalyzer()).Analyze(SampleShader);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Contains("1", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>ファイル単位の状態を持つルール。手順書の 7 章と同じ形。</summary>
    private sealed class CountingAnalyzer : DiagnosticAnalyzer
    {
        public static DiagnosticDescriptor Rule { get; } = new(
            id: "TEST0009",
            title: "Pass の数を報告する",
            messageFormat: "Pass が {0} 個あります。",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.RegisterAnalysisStartAction(start =>
            {
                List<ShaderLab.Syntax.PassSyntax> passes = [];

                start.RegisterNodeAction<ShaderLab.Syntax.PassSyntax>(c => passes.Add(c.Node));
                start.RegisterAnalysisEndAction(end =>
                    end.ReportDiagnostic(Rule, new Core.Text.TextSpan(0, 0), passes.Count));
            });
        }
    }

    [Theory]
    [InlineData("穴なし", 0)]
    [InlineData("{0} だけ", 1)]
    [InlineData("{0} と {1}", 2)]
    [InlineData("{1} だけでも 2 つ必要", 2)]
    [InlineData("{{0}} は文字としての波括弧", 0)]
    [InlineData("{0,-10} のような書式指定も穴である", 1)]
    public void 書式が必要とする引数の数を数えられる(string format, int expected)
    {
        DiagnosticDescriptor descriptor = new(
            id: "TEST0007",
            title: "テスト用",
            messageFormat: format,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            description: "テスト用。");

        Assert.Equal(expected, descriptor.RequiredMessageArgumentCount);
    }
}
