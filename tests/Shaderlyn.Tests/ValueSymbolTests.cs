using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
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
        // FXAA_PRESET == 1 の分岐の定義と照合して「定義し直し」(HL0008) と報告していた。
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

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0008");
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
    public void 同じ名前の別の値は単位が違っても同時には成り立たない()
    {
        // Unreal の頂点ファクトリの形。同じファイルの単位ごとに値が違う (交差シェーダーの単位は 4 だけ)。
        // 値を宣言した単位が違うと排他の組にならず、ある単位の木の構成 (== 4) とほかの単位の分岐の条件 (== 1) を掛けた条件を
        // ありうると見て、#error に到達すると報告していた (HL0009)。
        SourceText text = SourceText.From(
            """
            #if QUALITY == 1
            float4 Low() { return 1; }
            #endif
            float4 main() : SV_Target { return 0; }
            """,
            "test.hlsl");

        ShaderCompilationUnit whole = HlslEnvironment.WholeFile(text);

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [
                whole with { Name = "a", Variants = VariantSpace.ForValues("QUALITY", [4]) },
                whole with { Name = "b", Variants = VariantSpace.ForValues("QUALITY", [1, 2]) },
            ],
            text,
            new HlslEnvironment(),
            ShaderSourceInfo.None);

        ConditionMap condition = compilation.GetConditionMap();

        Assert.False(condition.IsPossible(SymbolCondition.Symbol("QUALITY==4").And(SymbolCondition.Symbol("QUALITY==1"))));
        Assert.True(condition.IsPossible(SymbolCondition.Symbol("QUALITY==1")));
    }

    [Fact]
    public void 並べられなかった値の連なりはほかで並べた値でも構成を作る()
    {
        // Unreal の LumenScreenProbeFiltering.usf の形。宣言の側の FORMAT の分岐は並べられるが、
        // 使う側の連なりは並べられない (ここでは外側の条件が読めない)。== 1 をほかで並べたからと == 1 の構成を作らずにいると、
        // 使う側の == 1 の分岐はどの木にも載らず、== 0 の分岐の使用を「== 1 のときは宣言が無い」(HL0315) と報告していた。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #if FORMAT == 0
            RWBuffer<float> Ambient;
            #elif FORMAT == 1
            RWBuffer<float> Irradiance;
            #endif

            #if KIND == 0
            float3
            #else
            float2
            #endif
            Get() { return 1; }

            [numthreads(1, 1, 1)]
            void main()
            {
            #if (KIND == 0) * 1
            #if FORMAT == 0
                Ambient[0] = Get().x;
            #else
                Irradiance[0] = Get().y;
            #endif
            #endif
            }
            """,
            VariantSpace.ForValues("FORMAT", [0, 1]).Union(VariantSpace.ForValues("KIND", [0, 1])));

        Assert.DoesNotContain(diagnostics, d => d.Id is "HL0315" or "HL0310");
    }

    [Fact]
    public void ヘッダで並べられなかった値の連なりもほかの値の構成を作る()
    {
        // Unreal の DistanceFieldLightingShared.ush の形。ヘッダの連なりを並べられず、記録に残った原子は既定の値 (== 0) だった。
        // == 1 の構成を作らずにいると、== 1 の側の宣言はどの木にも載らず、並べた == 0 の側の宣言しか見えないので、
        // 使う名前を「!= 0 のとき宣言されていない」(HL0310) と報告していた。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #include "Brick.hlsl"

            float4 main() : SV_Target { return Sample(1); }
            """,
            VariantSpace.ForValues("OFFSET", [0, 1]),
            new(StringComparer.Ordinal)
            {
                ["Brick.hlsl"] = """
                    float Sample(uint Index)
                    {
                    #if (OFFSET == 0) * 1
                        float Unused = Index;
                    #else
                        float4 BrickOffset = Index;
                    #endif
                    #if OFFSET == 0
                        float3 BrickOffset = Index;
                    #endif
                        return BrickOffset.x;
                    }
                    """,
            });

        Assert.DoesNotContain(diagnostics, d => d.Id is "HL0310" or "HL0315");
    }

    [Fact]
    public void 読めない条件の後ろの分岐の定義を定義し直しと言わない()
    {
        // Unreal の DOFReduce.usf の形。#if CONFIG_DOF_ALPHA は読めないので、#elif DIM_RGB の条件は DIM_RGB だけになる。
        // その広い条件どうしを照らして、1 つ目と 2 つ目の分岐の定義を「定義し直し」(HL0008) と報告していた。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #include "Common.hlsl"

            #if CONFIG_DOF_ALPHA
                #define LAYOUT 0
            #elif DIM_RGB
                #define LAYOUT 1
            #else
                #define LAYOUT 2
            #endif

            float4 main() : SV_Target { return LAYOUT; }
            """,
            VariantSpace.ForValues("DIM_ALPHA", [0, 1]).Union(VariantSpace.ForValues("DIM_RGB", [0, 1])),
            new(StringComparer.Ordinal)
            {
                ["Common.hlsl"] = """
                    #ifdef DIM_ALPHA
                        #define CONFIG_DOF_ALPHA (DIM_ALPHA)
                    #else
                        #define CONFIG_DOF_ALPHA 0
                    #endif
                    """,
            });

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0008");
    }

    [Fact]
    public void 左右を入れ替えた比較も値の条件で読む()
    {
        // Unreal の ByteBuffer.usf の形 (#if STRUCTURED_ELEMENT_SIZE_UINT1 == STRUCTURED_ELEMENT_SIZE)。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #define SIZE_UINT1 (0)
            #define SIZE_UINT2 (1)

            #if PASS == 0
            #if SIZE_UINT1 == ELEMENT_SIZE
                #define ACCESS(Name) Name##1x[0]
            #elif SIZE_UINT2 == ELEMENT_SIZE
                #define ACCESS(Name) Name##2x[0]
            #endif
            #endif

            RWStructuredBuffer<uint> Dst1x;
            RWStructuredBuffer<uint2> Dst2x;

            [numthreads(1, 1, 1)]
            void main()
            {
            #if PASS == 0
                ACCESS(Dst) = 1;
            #endif
            }
            """,
            VariantSpace.ForValues("PASS", [0, 1]).Union(VariantSpace.ForValues("ELEMENT_SIZE", [0, 1])));

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void errorに到達した構成の木は使わない()
    {
        // Unreal の SSDInjest.usf の形。パーミュテーションの値のうち、#else / #error で止めた値の構成はコンパイルできない。
        // その木を使うと、宣言が足りないと報告していた (HL0310)。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #if (PROCESSING == 0) * 1
                #define ARRAY_SIZE 4
            #else
                #error Unknown signal processing.
            #endif

            float4 main() : SV_Target
            {
                float sum = 0;
                for (uint i = 0; i < ARRAY_SIZE; i++) { sum += i; }
                return sum;
            }
            """,
            VariantSpace.ForValues("PROCESSING", [0, 1, 2]),
            out ShaderCompilation compilation);

        Assert.Empty(compilation.SymbolVariants);
        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void errorに到達した構成を情報として伝える()
    {
        // Unreal と汎用の HLSL では、その構成はコンパイルされない (ShouldCompilePermutation が除く)。誤りではないので情報にする (HL0009)。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            """
            #if PROCESSING == 0
                #define ARRAY_SIZE 4
            #elif PROCESSING == 1
                #define ARRAY_SIZE 8
            #else
                #error Unknown signal processing.
            #endif

            float4 main() : SV_Target { return ARRAY_SIZE; }
            """,
            VariantSpace.ForValues("PROCESSING", [0, 1, 2]),
            out ShaderCompilation compilation);

        Assert.NotEmpty(compilation.ErrorConfigurations);
        Diagnostic info = Assert.Single(diagnostics, d => d.Id == "HL0009");
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Contains("PROCESSING", info.GetMessage(), StringComparison.Ordinal);
        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void 名前を付けた値と比べる分岐も値の条件で読む()
    {
        // Unreal の TemporalAA.usf の形。値に名前を付けて比べる。
        // 比べる相手を読めないと、#elif の連なりのすべての分岐を同じ条件で読み、
        // 分岐ごとの定義を「定義し直し」(HL0008) と報告していた。
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
        => Analyze(code, variants, out _, userFiles);

    /// <summary>構成の軸を指定した環境で 1 ファイルを解析する。</summary>
    /// <param name="code">コード。</param>
    /// <param name="variants">構成の軸。</param>
    /// <param name="compilation">解析した結果。</param>
    /// <param name="userFiles">取り込める利用者のヘッダ。</param>
    /// <returns>診断。</returns>
    private ImmutableArray<Diagnostic> Analyze(
        string code,
        VariantSpace variants,
        out ShaderCompilation compilation,
        Dictionary<string, string>? userFiles = null)
    {
        SourceText text = SourceText.From(code, "test.hlsl");

        compilation = ShaderCompilation.Create(
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
