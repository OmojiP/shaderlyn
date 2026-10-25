using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Analysis;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Testing;

namespace Shaderlyn.Tests;

/// <summary>
/// 自作ルール向けの、構成を扱う API の検証 (docs/custom-rules/api-reference.md)。
/// </summary>
public sealed class CustomRuleApiTests
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "TEST0100",
        title: "Banned を呼ばない",
        messageFormat: "{0}関数 '{1}' は使えません。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "検証用。");

    /// <summary>テストのシェーダー。<paramref name="body"/> を Pass のコードにする。</summary>
    private static string Shader(string body) => $$"""
        Shader "Test/Api"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
        {{body}}
                    ENDHLSL
                }
            }
        }
        """;

    /// <summary><c>Banned()</c> の呼び出しを報告するルール。指定とどの報告を使うかを外から決める。</summary>
    private sealed class BannedCallAnalyzer(NodeActionOptions? options, SymbolCondition? when) : ShaderRuleAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        protected override void InitializeShader(ShaderAnalysisContext context)
            => context.RegisterNodeAction<InvocationExpressionSyntax>(
                c =>
                {
                    if (c.Node.TargetName == "Banned")
                    {
                        c.ReportWhen(when ?? SymbolCondition.Always, Rule, c.Node.Target.Span, "Banned");
                    }
                },
                options);
    }

    [Fact]
    public void 条件付きの報告は木をまたいで条件の和をとって1件にする()
    {
        // _A と _B の分岐で、Banned を呼ぶ文の形が変わる (_B の構成は別の木になる)。どちらの構成の報告も 1 件にまとめる。
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new BannedCallAnalyzer(null, null)).Analyze(Shader("""
            #pragma multi_compile _ _A _B
            float Banned() { return 1; }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target
            {
            #if defined(_A) || defined(_B)
                return Banned();
            #else
                return 0;
            #endif
            }
            """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.StartsWith("構成：", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("_A", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("_B", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.EndsWith("関数 'Banned' は使えません。", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void どの構成でもなら条件を付けない()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new BannedCallAnalyzer(null, null)).Analyze(Shader("""
            float Banned() { return 1; }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return Banned(); }
            """));

        Assert.Equal("関数 'Banned' は使えません。", Assert.Single(diagnostics).GetMessage());
    }

    [Theory]
    [InlineData("defined(_A)")]
    [InlineData("_A && !defined(_B)")]
    public void 指定した構成で存在しうるノードだけで呼ぶ(string when)
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(
            new BannedCallAnalyzer(new NodeActionOptions { When = when }, null)).Analyze(Shader("""
            #pragma multi_compile _ _A _B
            float Banned() { return 1; }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target
            {
                float x = 0;
            #if defined(_B)
                x = Banned();
            #endif
            #if defined(_A)
                x += Banned();
            #endif
                return x;
            }
            """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("構成：_A のとき、関数 'Banned' は使えません。", diagnostic.GetMessage());
    }

    [Fact]
    public void エントリポイントから届く関数の中だけで呼ぶ()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(
            new BannedCallAnalyzer(new NodeActionOptions { ReachableFromEntryPoints = true }, null)).Analyze(Shader("""
            float Banned() { return 1; }
            float Used() { return Banned(); }
            float Unused() { return Banned() * 2; }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return Used(); }
            """));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(11, diagnostic.Location.LineSpan.Start.Line + 1);
    }

    [Fact]
    public void 条件の書き方の誤りは登録のときに伝える()
    {
        Assert.Throws<FormatException>(() => NodeCondition.Parse("defined(_A) &&"));
        Assert.Throws<FormatException>(() => NodeCondition.Parse("(_A"));
        Assert.Throws<FormatException>(() => NodeCondition.Parse("_A == x"));
    }

    [Fact]
    public void 値を持つシンボルとの比較は値の一覧で読む()
    {
        ImmutableDictionary<string, ImmutableArray<int>> values =
            ImmutableDictionary<string, ImmutableArray<int>>.Empty.Add("QUALITY", [0, 1, 2, 3]);

        SymbolCondition condition = NodeCondition.Parse("QUALITY >= 2 && defined(_A)").Resolve(values);

        Assert.True(condition.IsSatisfiedBy(name => name is "QUALITY==3" or "_A"));
        Assert.False(condition.IsSatisfiedBy(name => name is "QUALITY==1" or "_A"));
        Assert.False(condition.IsSatisfiedBy(name => name is "QUALITY==2"));
        Assert.Throws<FormatException>(() => NodeCondition.Parse("_A > 1").Resolve(values));
    }

    /// <summary>エントリポイントから API を呼び、結果を覚えるルール。</summary>
    private sealed class ProbeAnalyzer(Action<ShaderNodeAnalysisContext<FunctionDeclarationSyntax>> probe) : ShaderRuleAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        protected override void InitializeShader(ShaderAnalysisContext context)
            => context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
            {
                if (c.Node.Name == "frag")
                {
                    probe(c);
                }
            });
    }

    [Fact]
    public void 呼ばれる関数を深さと構成付きでたどる()
    {
        List<(string Name, int Depth, string When)> found = [];

        new ShaderRuleVerifier(new ProbeAnalyzer(c =>
        {
            foreach (CalledFunction callee in c.Compilation.EnumerateCallees(c.Node, c.Program))
            {
                found.Add((callee.Function.Name, callee.Depth, callee.When.ToString()));
            }
        })).Analyze(Shader("""
            #pragma multi_compile _ _A
            float Leaf() { return 1; }
            float Recursive();
            float Helper() { return Leaf() + Leaf(); }
            float Optional() { return 2; }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target
            {
                float x = Helper();
            #ifdef _A
                x += Optional();
            #endif
                return x;
            }
            """));

        Assert.Contains(("Helper", 1, "常に"), found);
        Assert.Contains(("Leaf", 2, "常に"), found);
        Assert.Contains(("Optional", 1, "_A"), found);
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void 式の型を構成ごとに求める()
    {
        List<string> types = [];

        new ShaderRuleVerifier(new ProbeAnalyzer(c =>
        {
            ReturnStatementSyntax returned = c.Node.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Single();
            ExpressionTypeBinder binder = c.Compilation.GetExpressionTypeBinder(c.Program);

            foreach (ConditionalType type in binder.GetTypesByConfiguration(returned.Expression!))
            {
                types.Add($"{type.When} {type.Type}");
            }
        })).Analyze(Shader("""
            #pragma multi_compile _ _A
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target
            {
            #ifdef _A
                float3 color = 1;
            #else
                float4 color = 1;
            #endif
                return color;
            }
            """));

        Assert.Equal(["!_A float4", "_A float3"], types.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void 識別子が宣言されていない構成を求める()
    {
        List<string> missing = [];

        new ShaderRuleVerifier(new ProbeAnalyzer(c =>
        {
            foreach (IdentifierExpressionSyntax identifier in c.Node.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
            {
                missing.Add(c.Compilation.TryGetIdentifierUndeclaredCondition(identifier, c.Program, out SymbolCondition undeclared)
                    ? $"{identifier.Name}: {undeclared}"
                    : $"{identifier.Name}: なし");
            }
        })).Analyze(Shader("""
            #pragma multi_compile _ _A
            #ifdef _A
            float4 _Tint;
            #endif
            float4 _Always;
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target
            {
                float4 local = _Always;
                return _Tint + local;
            }
            """));

        Assert.Contains("_Tint: !_A", missing);
        Assert.Contains("_Always: なし", missing);
        Assert.Contains("local: なし", missing);
    }
}
