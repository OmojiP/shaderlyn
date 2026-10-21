using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
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
        // (QUALITY == 1) || (QUALITY == 2) ではなく、0 以外と短く書く。
        Assert.Contains("QUALITY != 0 のとき", diagnostic.GetMessage(), StringComparison.Ordinal);
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
    public void 値の構成の木はほかの値を持つシンボルを既定の値で読んだ構成である()
    {
        // SIZE の分岐は並べられないので、SIZE == 16 の木を作る。その木で FORMAT は既定の値 (0) で読む。
        // 木の構成を「SIZE == 16」だけにすると、その木の宣言や使用を FORMAT == 1 のものとも見て、
        // 「そのとき宣言が無い」(HL0315) と報告していた (Unreal の LumenScreenProbeFiltering.usf)。
        SourceText text = SourceText.From(
            """
            #if SIZE >= 16
            float3
            #else
            float4
            #endif
            Get() { return 1; }
            #if FORMAT == 0
            RWBuffer<float> Ambient;
            #endif
            [numthreads(1, 1, 1)] void main() { Ambient[0] = Get().x; }
            """,
            "test.hlsl");

        HlslEnvironment environment = new(new HlslEnvironmentOptions
        {
            Variants = VariantSpace.ForValues("FORMAT", [0, 1]).Union(VariantSpace.ForValues("SIZE", [8, 16])),
        });

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [new ShaderCompilationUnit(text, new TextSpan(0, text.Length))],
            text,
            environment,
            ShaderSourceInfo.None);

        AnalyzedProgram size16 = Assert.Single(compilation.SymbolVariants, v => v.EnabledSymbols.Contains("SIZE==16"));
        string condition = size16.AnalyzedCondition.ToString();

        Assert.True(size16.AnalyzedCondition.IsSatisfiedBy(s => s is "SIZE==16" or "FORMAT==0"), condition);
        Assert.False(size16.AnalyzedCondition.IsSatisfiedBy(s => s is "SIZE==16" or "FORMAT==1"), condition);
    }

    [Fact]
    public void 原子を並べた値の名前は値の構成の木でも今の値に決めない()
    {
        // #if DEBUG は DEBUG==1 だけを並べ、DEBUG==0 は並べた記録に残らない。
        // KIND == 2 の木で DEBUG==0 を「成り立つ」と決めると、その木にある DEBUG==1 の側の宣言を使う文を
        // 「DEBUG == 0 のときは無い」と報告していた (Unreal の HairStrandsEnvironmentLighting.usf で HL0313)。
        SourceText text = SourceText.From(
            """
            #if DEBUG
            struct FDebug { float3 Color; };
            void WriteDebug() { FDebug o; o.Color = 1; }
            #endif

            #if KIND == 0
            float3
            #elif KIND == 1
            float2
            #else
            float
            #endif
            Get() { return 1; }

            float4 main() : SV_Target { return Get().x; }
            """,
            "test.hlsl");

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [HlslEnvironment.WholeFile(text)],
            text,
            new HlslEnvironment(new HlslEnvironmentOptions
            {
                Variants = VariantSpace.ForValues("DEBUG", [0, 1]).Union(VariantSpace.ForValues("KIND", [0, 1, 2])),
            }),
            ShaderSourceInfo.None);

        AnalyzedProgram kind2 = Assert.Single(compilation.SymbolVariants, v => v.EnabledSymbols.Contains("KIND==2"));
        Assert.True(kind2.AnalyzedCondition.IsSatisfiedBy(s => s is "KIND==2" or "DEBUG==1"), kind2.AnalyzedCondition.ToString());

        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0313");
    }

    [Fact]
    public void ヘッダのifndefで与える既定の値は定義し直しにならない()
    {
        // Unreal の FXAAShader.usf と Fxaa3_11.ush の形。ファイルが値ごとに FXAA_PC を定義し、
        // ヘッダは #ifndef FXAA_PC で既定の値を与える。FXAA_PRESET == 0 の構成で読んだヘッダの定義を、
        // FXAA_PRESET == 1 の分岐の定義と照合して「定義し直し」(HL0002) と報告していた。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #if FXAA_PRESET == 0
                #define FXAA_QUALITY__PRESET 10
                #define FXAA_PC_CONSOLE 1
            #elif FXAA_PRESET == 1
                #define FXAA_QUALITY__PRESET 10
                #define FXAA_PC 1
            #elif FXAA_PRESET == 2
                #define FXAA_QUALITY__PRESET 13
                #define FXAA_PC 1
            #endif

            #include "Fxaa.hlsl"

            float4 main() : SV_Target { return FXAA_PC + FXAA_PC_CONSOLE + FXAA_QUALITY__PRESET; }
            """,
            VariantSpace.ForValues("FXAA_PRESET", [0, 1, 2]),
            new(StringComparer.Ordinal)
            {
                ["Fxaa.hlsl"] = """
                    #ifndef FXAA_PC
                        #define FXAA_PC 0
                    #endif
                    #if !defined(FXAA_PC_CONSOLE)
                        #define FXAA_PC_CONSOLE 0
                    #endif
                    """,
            });

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0002");
    }

    [Fact]
    public void ほかの単位にだけある値のときの宣言を求めない()
    {
        // Unreal の SSRTReflections.usf の形。QUALITY が 1 から 4 の単位と、1 から 3 の単位がある。
        // 1 から 3 の単位の木は 4 の #else を読まないので、そこの宣言が無い。
        // その木を 4 の構成についても見ていることにして、「4 のとき宣言されていない」(HL0310) と報告していた。
        SourceText text = SourceText.From(
            """
            float4 main() : SV_Target
            {
            #if QUALITY == 1
                uint NumRays = 1;
            #elif QUALITY == 2
                uint NumRays = 2;
            #elif QUALITY == 3
                uint NumRays = 4;
            #else
                uint NumRays = 12;
            #endif
                return NumRays;
            }
            """,
            "test.hlsl");

        ShaderCompilationUnit whole = HlslEnvironment.WholeFile(text);

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [
                whole with { Name = "all", Variants = VariantSpace.ForValues("QUALITY", [1, 2, 3, 4]) },
                whole with { Name = "stencil", Variants = VariantSpace.ForValues("QUALITY", [1, 2, 3]) },
            ],
            text,
            new HlslEnvironment(),
            ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        foreach (Diagnostic diagnostic in diagnostics)
        {
            _output.WriteLine($"{diagnostic.Location.LineSpan.Start} {diagnostic.Id} {diagnostic.Severity}: {diagnostic.GetMessage()}");
        }

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0310");
    }

    [Fact]
    public void 名前を付けた値と比べる分岐も値の条件で読む()
    {
        // Unreal の TemporalAA.usf の形。値に名前を付けて比べる。
        // 比べる相手を読めないと、#elif の連なりのすべての分岐を同じ条件で読み、
        // 分岐ごとの定義を「定義し直し」(HL0002) と報告していた。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #define QUALITY_LOW 0
            #define QUALITY_MEDIUM (1)
            #define QUALITY_HIGH 2

            #if PASS == 0
                #if QUALITY == QUALITY_LOW
                    #define FILTERED 0
                #elif QUALITY == QUALITY_MEDIUM
                    #define FILTERED 1
                #elif QUALITY == QUALITY_HIGH
                    #define FILTERED 2
                #endif
            #else
                #define FILTERED 3
            #endif

            float4 main() : SV_Target { return FILTERED; }
            """,
            VariantSpace.ForValues("PASS", [0, 1]).Union(VariantSpace.ForValues("QUALITY", [0, 1, 2])));

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void 値の構成の木では有効にした値の分岐の定義を使う()
    {
        // QUALITY == 1 の木で、PASS の分岐ごとの定義を複製する (条件の巻き上げ)。
        // 有効にした値 (QUALITY == 1) の原子を「成り立たない」と読み、その分岐の定義を外して
        // 名前の残った複製を作り、「宣言されていない」(HL0310) と報告していた。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #if PASS == 0
                #if QUALITY == 0
                    #define FILTERED 0
                #elif QUALITY == 1
                    #define FILTERED 1
                #elif QUALITY == 2
                    #define FILTERED 2
                #endif
            #elif PASS == 1
                #define FILTERED 3
            #else
                #define FILTERED 4
            #endif

            float4 main() : SV_Target { return FILTERED; }
            """,
            VariantSpace.ForValues("PASS", [0, 1, 2]).Union(VariantSpace.ForValues("QUALITY", [0, 1, 2])));

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
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
    public void 一つを除くすべての値の論理和は否定として表示する()
    {
        VariantSpace space = VariantSpace.ForValues("QUALITY", [0, 1, 2])
            .Union(new VariantSpace(
                [
                    new VariantDeclaration("_A", VariantSymbolKind.Compiled, null),
                    new VariantDeclaration("_B", VariantSymbolKind.Compiled, null),
                    new VariantDeclaration("_C", VariantSymbolKind.Compiled, null),
                ],
                [new VariantGroup(["_A", "_B", "_C"], RequiresOne: true)]));

        SymbolCondition one = SymbolCondition.Symbol(VariantValues.Atom("QUALITY", 1));
        SymbolCondition two = SymbolCondition.Symbol(VariantValues.Atom("QUALITY", 2));
        SymbolCondition fog = SymbolCondition.Symbol("USE_FOG");

        Assert.Equal("QUALITY != 0", space.Constraints.Reduce(one.Or(two)).ToString());

        // ほかの条件と掛け合わせた項も、残りの部分が同じならまとめる。
        Assert.Equal("QUALITY != 0 && USE_FOG", space.Constraints.Reduce(one.And(fog).Or(two.And(fog))).ToString());

        // 2 つだけなら短くならないので、そのまま書く。
        Assert.Equal("QUALITY == 1", space.Constraints.Reduce(one).ToString());

        // 値を持たない組 (Unity の multi_compile _A _B _C) は、否定にまとめない。
        SymbolCondition ab = SymbolCondition.Symbol("_A").Or(SymbolCondition.Symbol("_B"));
        Assert.DoesNotContain("!", space.Constraints.Reduce(ab).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 原子は値の比較として表示する()
    {
        SymbolCondition two = SymbolCondition.Symbol(VariantValues.Atom("QUALITY", 2));

        Assert.Equal("QUALITY == 2", two.ToString());
        Assert.Equal("QUALITY != 2", two.Negate().ToString());
    }

    /// <summary>構成の軸を指定した環境で 1 ファイルを解析する。</summary>
    /// <param name="code">コード。</param>
    /// <param name="variants">構成の軸。</param>
    /// <param name="userFiles">取り込める利用者のヘッダ。</param>
    /// <returns>診断。</returns>
    private ImmutableArray<Diagnostic> Analyze(string code, VariantSpace variants, Dictionary<string, string>? userFiles = null)
    {
        SourceText text = SourceText.From(code, "test.hlsl");

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [HlslEnvironment.WholeFile(text)],
            text,
            new HlslEnvironment(new HlslEnvironmentOptions { Variants = variants }),
            ShaderSourceInfo.None,
            new SemanticsOptions
            {
                IncludeResolver = new InMemoryIncludeResolver(userFiles ?? []),
                IsUserInclude = path => userFiles?.ContainsKey(path) == true,
            });

        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        foreach (Diagnostic diagnostic in diagnostics)
        {
            _output.WriteLine($"{diagnostic.Location.LineSpan.Start} {diagnostic.Id} {diagnostic.Severity}: {diagnostic.GetMessage()}");
        }

        return diagnostics;
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
            [new ShaderCompilationUnit(text, new TextSpan(0, text.Length))],
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
            => options.WithHlsl(h => h with { LanguageVersion = HlslLanguageVersion.Hlsl2021 });

        public PreprocessorOptions PrepareUnit(
            ShaderCompilationUnit unit,
            ImmutableArray<ShaderSyntaxToken> tokens,
            PreprocessorOptions options,
            string filePath)
            => options;

        public VariantSpace ReadDeclaredVariants(ShaderCompilationUnit unit, ImmutableArray<ShaderSyntaxToken> tokens) => Space;

        public VariantSpace ReadVariants(ShaderCompilationUnit unit, PreprocessResult result) => Space;

        public ImmutableArray<ShaderEntryPoint> ReadEntryPoints(ShaderCompilationUnit unit, PreprocessResult result) => [];

        public bool LooksLikeVariantSymbol(string name) => false;

        public bool IsGeneratedInclude(string path) => false;
    }
}
