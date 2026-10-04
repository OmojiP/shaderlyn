using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// 値を持つシンボル (<c>QUALITY ∈ {0, 1, 2}</c>) の検証。
/// </summary>
/// <remarks>
/// Unreal のパーミュテーション (<c>SHADER_PERMUTATION_INT</c>) や、独自エンジンが <c>-D QUALITY=2</c> の組を変えて
/// コンパイルする構成である。値ごとの原子 (<c>QUALITY==2</c>) の「ちょうど 1 つ」の組として扱う。
/// </remarks>
public sealed class ValueSymbolTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void 値の比較で分かれる分岐を値の条件で報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            float4 main() : SV_Target
            {
            #if QUALITY == 2
                float3 c = 1;
            #else
                float4 c = 1;
            #endif
                return c.w;
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");
        Assert.Contains("QUALITY == 2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 値だけを書いた条件は0以外の値で成り立つ()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            float4 main() : SV_Target
            {
            #if QUALITY
                float3 c = 1;
            #else
                float4 c = 1;
            #endif
                return c.w;
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0312");
        Assert.DoesNotContain("QUALITY == 0", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("QUALITY", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 並べられない分岐は値ごとの構成で解析する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #if QUALITY >= 1
            float3
            #else
            float4
            #endif
            Get() { return 1; }

            float4 main() : SV_Target { return Get().w; }
            """,
            out ShaderCompilation compilation);

        // 既定の構成は QUALITY == 0 (先頭の値)。残りの値を 1 つずつ有効にした構成を作る。
        Assert.Equal(
            ["QUALITY==1", "QUALITY==2"],
            compilation.SymbolVariants.SelectMany(v => v.EnabledSymbols).Order(StringComparer.Ordinal));

        // 構成の木の中では型が float3 に決まるので、条件を添えずに報告する。
        Assert.Single(diagnostics, d => d.Id == "HL0312");
    }

    [Fact]
    public void 値を持つシンボルはどの構成でもマクロとして定義されている()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            float4 main() : SV_Target
            {
                float q = QUALITY;
            #ifdef QUALITY
                return q;
            #else
                return undefinedName;
            #endif
            }
            """);

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void 原子は値の比較として表示する()
    {
        SymbolCondition two = SymbolCondition.Symbol(VariantValues.Atom("QUALITY", 2));

        Assert.Equal("QUALITY == 2", two.ToString());
        Assert.Equal("QUALITY != 2", two.Negate().ToString());
    }

    /// <summary>QUALITY を宣言した環境で 1 ファイルを解析する。</summary>
    /// <param name="code">コード。</param>
    /// <returns>診断。</returns>
    private ImmutableArray<Diagnostic> Analyze(string code) => Analyze(code, out _);

    /// <summary>QUALITY を宣言した環境で 1 ファイルを解析する。</summary>
    /// <param name="code">コード。</param>
    /// <param name="compilation">解析した結果。</param>
    /// <returns>診断。</returns>
    private ImmutableArray<Diagnostic> Analyze(string code, out ShaderCompilation compilation)
    {
        SourceText text = SourceText.From(code, "test.hlsl");

        compilation = ShaderCompilation.Create(
            text,
            [new HlslCompilationUnit(text, new TextSpan(0, text.Length))],
            text,
            new QualityEnvironment(),
            ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        foreach (Diagnostic diagnostic in diagnostics)
        {
            _output.WriteLine($"{diagnostic.Location.LineSpan.Start} {diagnostic.Id} {diagnostic.Severity}: {diagnostic.GetMessage()}");
        }

        _output.WriteLine($"variants: {string.Join(" / ", compilation.SymbolVariants.Select(v => string.Join("+", v.EnabledSymbols)))}");
        return diagnostics;
    }

    /// <summary><c>QUALITY</c> が 0 / 1 / 2 をとる環境。</summary>
    private sealed class QualityEnvironment : IHlslEnvironment
    {
        private static readonly VariantSpace Space = VariantSpace.ForValues("QUALITY", [0, 1, 2]);

        public string Name => "test-quality";

        public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
            => options with { LanguageVersion = HlslLanguageVersion.Hlsl2021 };

        public PreprocessorOptions PrepareUnit(
            HlslCompilationUnit unit,
            ImmutableArray<HlslSyntaxToken> tokens,
            PreprocessorOptions options,
            string filePath)
            => options;

        public VariantSpace ReadDeclaredVariants(HlslCompilationUnit unit, ImmutableArray<HlslSyntaxToken> tokens) => Space;

        public VariantSpace ReadVariants(HlslCompilationUnit unit, PreprocessResult result) => Space;

        public ImmutableArray<HlslEntryPoint> ReadEntryPoints(HlslCompilationUnit unit, PreprocessResult result) => [];

        public bool LooksLikeVariantSymbol(string name) => false;
    }
}
