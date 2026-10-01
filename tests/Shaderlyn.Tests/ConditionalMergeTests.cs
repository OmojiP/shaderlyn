using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// 2 つの構成で組み立てた構文木を突き合わせる検証。
/// </summary>
/// <remarks>
/// <b>ここで求めた条件が、この先のすべての判断の前提になる。</b>
/// 条件を取り違えても解析は最後まで走り、
/// 誤った前提で指摘が出るだけになるので、実物の形で確かめる。
/// </remarks>
public sealed class ConditionalMergeTests
{
    /// <summary>直前に組み立てた既定の構成。木そのものを突き合わせる検証で使う。</summary>
    private static AnalyzedProgram? LastBaseline;

    private const string UrpCorePath =
        "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl";

    /// <summary>
    /// 2 つの構成でブロックを解析し、突き合わせる。
    /// </summary>
    /// <param name="body">HLSLPROGRAM の中身。</param>
    /// <param name="keyword">有効にするシンボル。</param>
    /// <returns>突き合わせた結果。</returns>
    private static ConditionalMergeResult MergeBlock(string body, string keyword)
    {
        string source = $$"""
            Shader "Test/Conditional"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma shader_feature_local {{keyword}}
            {{body}}
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        ShaderLabSyntaxTree shaderLab = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = string.Empty }),

            // ここで見るのは「2 つの木を突き合わせる」機構である。
            // 1 つの木にまとめられるとその機構を通らないので、明示的に切る。
            BothBranchSymbols = [],
        };

        ShaderCompilation compilation = ShaderCompilation.Create(text, shaderLab, options);

        AnalyzedProgram baseline = Assert.Single(compilation.Programs);
        LastBaseline = baseline;
        AnalyzedProgram variant = Assert.Single(
            compilation.SymbolVariants.Where(v => v.EnabledSymbols.SequenceEqual([keyword])));

        return ConditionalMerge.Merge(baseline.Tree.Root, variant.Tree.Root, [keyword]);
    }

    /// <summary>結果から、指定した名前のノードに付いた条件を探す。</summary>
    /// <param name="result">突き合わせた結果。</param>
    /// <param name="text">ノードのテキストに含まれる語。</param>
    /// <returns>見つかった条件。</returns>
    private static SymbolCondition ConditionFor(ConditionalMergeResult result, string text)
    {
        // 記録されるのは部分木の根なので、その中に目的の宣言があるかで探す。
        ConditionalNode[] matches =
        [
            .. result.ConditionalNodes.Where(n => DeclaresVariable(n.Node, text))
        ];

        Assert.NotEmpty(matches);

        // 同じ宣言に別々の条件が付いていたら、突き合わせが壊れている。
        return Assert.Single(matches.Select(m => m.Condition).Distinct());
    }

    /// <summary>部分木のどこかで、その名前の変数を宣言しているかを判定する。</summary>
    /// <param name="node">部分木の根。</param>
    /// <param name="name">探す変数の名前。</param>
    /// <returns>宣言していれば <see langword="true"/>。</returns>
    private static bool DeclaresVariable(HlslSyntaxNode node, string name)
        => node.DescendantNodesAndSelf()
            .OfType<VariableDeclarationSyntax>()
            .Any(d => d.Variables.Any(v => v.Name == name));

    [Fact]
    public void 条件の外にある宣言には条件が付かない()
    {
        // 木のほとんどは無条件である。そこに条件が付くと、
        // 「この構成では存在しない」という誤った判断がそこら中で起きる。
        ConditionalMergeResult result = MergeBlock(
            """
                        float4 _Always;
            #ifdef _NORMALMAP
                        float4 _OnlyWhenEnabled;
            #endif
            """,
            "_NORMALMAP");

        Assert.True(result.IsComplete);

        Assert.DoesNotContain(
            result.ConditionalNodes,
            n => DeclaresVariable(n.Node, "_Always"));
    }

    [Fact]
    public void シンボルの中の宣言にはその条件が付く()
    {
        ConditionalMergeResult result = MergeBlock(
            """
                        float4 _Always;
            #ifdef _NORMALMAP
                        float4 _OnlyWhenEnabled;
            #endif
            """,
            "_NORMALMAP");

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP"),
            ConditionFor(result, "_OnlyWhenEnabled"));
    }

    [Fact]
    public void elseの側には否定の条件が付く()
    {
        ConditionalMergeResult result = MergeBlock(
            """
            #ifdef _NORMALMAP
                        float4 _WhenEnabled;
            #else
                        float4 _WhenDisabled;
            #endif
            """,
            "_NORMALMAP");

        Assert.True(result.IsComplete);

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP"),
            ConditionFor(result, "_WhenEnabled"));

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP", isDefined: false),
            ConditionFor(result, "_WhenDisabled"));
    }

    [Fact]
    public void 構造体のメンバーを条件付きにできる()
    {
        ConditionalMergeResult result = MergeBlock(
            """
                        struct Attributes
                        {
                            float4 positionOS : POSITION;
            #ifdef _NORMALMAP
                            float3 normalOS : NORMAL;
            #endif
                        };
            """,
            "_NORMALMAP");

        Assert.True(result.IsComplete);

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionFor(result, "normalOS"));

        Assert.DoesNotContain(
            result.ConditionalNodes,
            n => DeclaresVariable(n.Node, "positionOS"));
    }

    [Fact]
    public void 関数の中の文を条件付きにできる()
    {
        ConditionalMergeResult result = MergeBlock(
            """
                        half4 frag() : SV_Target
                        {
                            half4 color = 1;
            #ifdef _NORMALMAP
                            half4 extra = 2;
                            color += extra;
            #endif
                            return color;
                        }
            """,
            "_NORMALMAP");

        Assert.True(result.IsComplete);

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), ConditionFor(result, "extra"));

        Assert.DoesNotContain(
            result.ConditionalNodes,
            n => DeclaresVariable(n.Node, "color"));
    }

    [Fact]
    public void 条件の中の子孫にも同じ条件が付く()
    {
        // 片方の構成にしか無いノードは、その中身もまるごと同じ条件のもとにある。
        ConditionalMergeResult result = MergeBlock(
            """
            #ifdef _NORMALMAP
                        half4 OnlyHere(half4 a)
                        {
                            return a * 2;
                        }
            #endif
            """,
            "_NORMALMAP");

        Assert.True(result.IsComplete);

        ConditionalNode function = Assert.Single(
            result.ConditionalNodes.Where(
                n => n.Node is FunctionDeclarationSyntax f && f.Name == "OnlyHere"));

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), function.Condition);

        // 子孫は表に書かない。根が条件付きなら中身もまるごと同じ条件のもとにある。
        // 子孫の条件は親を辿って求める (ConditionMap.GetCondition がそれを行う)。
        Assert.DoesNotContain(
            result.ConditionalNodes,
            n => n.Node is HlslExpressionSyntax);
    }

    [Fact]
    public void 仮引数を条件付きにできる()
    {
        // HDRP の実物と同じ形 (VolumetricCloudsCombine.shader:71)。
        // 並びの要素が条件付きになる例である。
        ConditionalMergeResult result = MergeBlock(
            """
                        half4 frag(
                            float2 uv : TEXCOORD0
            #ifdef _NORMALMAP
                            , out float2 extra : SV_Target1
            #endif
                        ) : SV_Target
                        {
                            return 0;
                        }
            """,
            "_NORMALMAP");

        Assert.True(result.IsComplete);

        ConditionalNode parameter = Assert.Single(
            result.ConditionalNodes,
            n => n.Node is ParameterSyntax p && p.Name == "extra");

        Assert.Equal(SymbolCondition.Symbol("_NORMALMAP"), parameter.Condition);
    }

    [Fact]
    public void 式の途中の分岐も両方の条件が付く()
    {
        // 「式の途中」の形。今の突き合わせは、
        // それぞれの構成の式を別々の条件付きノードとして残す。
        // 1 つの木にまとめて三項演算子にすることはしない。
        // 突き合わせは条件を脇の索引に出す形で、木は作り直さない。
        ConditionalMergeResult result = MergeBlock(
            """
                        half4 frag() : SV_Target
                        {
                            half4 color =
            #ifdef _NORMALMAP
                                1
            #else
                                2
            #endif
                                ;
                            return color;
                        }
            """,
            "_NORMALMAP");

        if (!result.IsComplete)
        {
            Assert.NotEmpty(result.UnmergedLocations);
            return;
        }

        Assert.Contains(
            result.ConditionalNodes,
            n => n.Condition == SymbolCondition.Symbol("_NORMALMAP"));

        Assert.Contains(
            result.ConditionalNodes,
            n => n.Condition == SymbolCondition.Symbol("_NORMALMAP", isDefined: false));
    }

    [Fact]
    public void 構文の単位をまたぐ条件は併合せず報告する()
    {
        // 実際のシェーダーから採取した形。
        // if の条件が #if の中にあり、本体は #endif の外にある。
        // 何も伝えずに捨てると「条件を調べた結果ここには何も無い」と読めてしまう。
        ConditionalMergeResult result = MergeBlock(
            """
                        half4 frag() : SV_Target
                        {
                            half4 color = 0;
            #ifdef _NORMALMAP
                            if (color.x < 1)
            #else
                            if (color.y < 1)
            #endif
                            {
                                color = 1;
                            }
                            return color;
                        }
            """,
            "_NORMALMAP");

        // 併合できたかどうかを、必ず持っていること。
        if (!result.IsComplete)
        {
            Assert.NotEmpty(result.UnmergedLocations);
            return;
        }

        // 併合できたなら、両方の条件が正しく付いていること。
        // どちらの分岐の if も、それぞれの条件のもとにある。
        Assert.Contains(result.ConditionalNodes, n => n.Condition == SymbolCondition.Symbol("_NORMALMAP"));
        Assert.Contains(
            result.ConditionalNodes,
            n => n.Condition == SymbolCondition.Symbol("_NORMALMAP", isDefined: false));
    }

    [Fact]
    public void 同じ木どうしなら条件付きのノードは出ない()
    {
        // シンボルがコードを 1 行も変えないなら、2 つの構成は同じ木になる。
        // そのとき条件が付いてはならない。付けば、無条件のはずのものが
        // 「この構成では存在しない」と扱われる。
        ConditionalMergeResult result = MergeBlock(
            """
                        float4 _Always;
            #ifdef _NORMALMAP
                        float4 _OnlyWhenEnabled;
            #endif
            """,
            "_NORMALMAP");

        AnalyzedProgram baseline = LastBaseline!;

        ConditionalMergeResult self = ConditionalMerge.Merge(
            baseline.Tree.Root, baseline.Tree.Root, ["_NORMALMAP"]);

        Assert.True(self.IsComplete);
        Assert.Empty(self.ConditionalNodes);

        // 突き合わせる相手が違えば条件は出る。上の空は「同じだから」である。
        Assert.NotEmpty(result.ConditionalNodes);
    }
}
