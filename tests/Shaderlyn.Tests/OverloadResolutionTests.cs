using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// 「どの関数が呼ばれたか」を求める仕組みの検証。
/// </summary>
/// <remarks>
/// <b>決まらなかったことが、決まらなかったと返ってくることまで確かめる。</b>
/// 当てずっぽうで 1 つ選ぶ実装でも「決まったとき」のテストは通ってしまう。
/// </remarks>
public sealed class OverloadResolutionTests
{
    /// <summary>コードブロックを 1 つ持つシェーダーを組み立てる。</summary>
    /// <param name="body">埋め込む HLSL。</param>
    /// <returns>シェーダーのソース。</returns>
    private static string Shader(string body) =>
        $$"""
        Shader "Test/Overload"
        {
            SubShader
            {
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

    private static ShaderCompilation Compile(string body)
    {
        SourceText text = SourceText.From(Shader(body), Path.Combine("Assets", "Test.shader"));

        return ShaderCompilation.Create(text, ShaderLabSyntaxTree.Parse(text));
    }

    /// <summary>名前で呼び出しを 1 つ探す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">関数の名前。</param>
    /// <returns>見つかった呼び出しと、それが属するコードブロック。</returns>
    private static (AnalyzedProgram Program, InvocationExpressionSyntax Invocation) FindCall(
        ShaderCompilation compilation,
        string name)
    {
        foreach (AnalyzedProgram program in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            foreach (SyntaxNode node in compilation.EnumerateOwnNodes(program))
            {
                if (node is InvocationExpressionSyntax invocation
                    && string.Equals(invocation.TargetName, name, StringComparison.Ordinal))
                {
                    return (program, invocation);
                }
            }
        }

        throw new InvalidOperationException($"呼び出し '{name}' が見つかりません。");
    }

    /// <summary>解決してみる。</summary>
    /// <param name="body">埋め込む HLSL。</param>
    /// <param name="name">呼び出している関数の名前。</param>
    /// <returns>解決結果。</returns>
    private static OverloadResolution Resolve(string body, string name)
    {
        ShaderCompilation compilation = Compile(body);
        (AnalyzedProgram program, InvocationExpressionSyntax invocation) = FindCall(compilation, name);

        return compilation.ResolveCall(invocation, program);
    }

    // ------------------------------------------------------------------

    [Fact]
    public void 宣言が1つなら解決する()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Shade(float3 normal) { return normal.x; }

                            float4 frag() : SV_Target
                            {
                                float3 n = float3(0, 1, 0);
                                return Shade(n);
                            }
            """,
            "Shade");

        Assert.Equal(OverloadResolutionStatus.Resolved, resolution.Status);
        Assert.True(resolution.IsResolved);
        Assert.Equal("Shade", resolution.Declaration!.Name);
        Assert.Equal(["float3"], resolution.ParameterTypes.AsEnumerable());
    }

    [Fact]
    public void 実引数の型に合うオーバーロードへ解決する()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Mix(float a, float b) { return a + b; }
                            float Mix(float a, float2 b) { return a + b.x; }

                            float4 frag() : SV_Target
                            {
                                float value = 1;
                                float2 pair = float2(0, 0);
                                return Mix(value, pair);
                            }
            """,
            "Mix");

        Assert.Equal(OverloadResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(["float", "float2"], resolution.ParameterTypes.AsEnumerable());
    }

    /// <summary>
    /// 暗黙の変換が要る呼び出しでも解決できることの確認。
    /// </summary>
    /// <remarks>
    /// <b>書かれた型をそのまま照合するだけでは、この形は取り違える。</b>
    /// <c>1</c> の型は <c>int</c> であって仮引数の <c>float</c> と一致しないが、
    /// 呼ばれるのは 2 つ目の実引数が厳密に一致するほうである。
    /// </remarks>
    [Fact]
    public void 暗黙の変換が要る呼び出しも解決する()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Mix(float a, float b) { return a + b; }
                            float Mix(float a, float2 b) { return a + b.x; }

                            float4 frag() : SV_Target
                            {
                                float2 pair = float2(0, 0);
                                return Mix(1, pair);
                            }
            """,
            "Mix");

        Assert.Equal(OverloadResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(["float", "float2"], resolution.ParameterTypes.AsEnumerable());
    }

    /// <summary>
    /// 型の分からない引数があっても、そこで候補が分かれなければ解決できることの確認。
    /// </summary>
    /// <remarks>
    /// どちらの候補も 1 つ目の仮引数は <c>float</c> なので、
    /// 1 つ目の型が分からなくても順位は変わらない。
    /// <b>分からない引数があるだけで諦めるのは、絞りすぎである。</b>
    /// </remarks>
    [Fact]
    public void 型の分からない引数が候補を分けなければ解決する()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Mix(float a, float b) { return a + b; }
                            float Mix(float a, float2 b) { return a + b.x; }

                            float4 frag() : SV_Target
                            {
                                float2 pair = float2(0, 0);
                                return Mix(Untyped(), pair);
                            }
            """,
            "Mix");

        Assert.Equal(OverloadResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(["float", "float2"], resolution.ParameterTypes.AsEnumerable());
        Assert.Null(resolution.ArgumentTypes[0]);
    }

    [Fact]
    public void プロトタイプと定義が並んでいても曖昧にしない()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Shade(float3 normal);
                            float Shade(float3 normal) { return normal.x; }

                            float4 frag() : SV_Target
                            {
                                float3 n = float3(0, 1, 0);
                                return Shade(n);
                            }
            """,
            "Shade");

        Assert.Equal(OverloadResolutionStatus.Resolved, resolution.Status);

        // 指すのは本体を持つほうである。
        Assert.True(resolution.Declaration!.IsDefinition);
        Assert.Equal(2, resolution.Candidates.Length);
    }

    /// <summary>
    /// どちらも同じだけ噛み合う呼び出しは、曖昧だと返すことの確認。
    /// </summary>
    /// <remarks>
    /// 実引数の型はどちらも分かっている。
    /// それでも、厳密に一致する位置の数が並ぶので選べない。
    /// </remarks>
    [Fact]
    public void 決め手が無ければ曖昧だと返す()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Pick(float2 a, float3 b) { return a.x + b.x; }
                            float Pick(float3 a, float2 b) { return a.x + b.x; }

                            float4 frag() : SV_Target
                            {
                                float3 v = float3(0, 0, 0);
                                return Pick(v, v);
                            }
            """,
            "Pick");

        Assert.Equal(OverloadResolutionStatus.Ambiguous, resolution.Status);
        Assert.Null(resolution.Declaration);
        Assert.Equal(2, resolution.Candidates.Length);
    }

    [Fact]
    public void 実引数の型が分からなければ判断できないと返す()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Shade(float2 v) { return v.x; }
                            float Shade(float3 v) { return v.x; }

                            float4 frag() : SV_Target
                            {
                                return Shade(Untyped());
                            }
            """,
            "Shade");

        Assert.Equal(OverloadResolutionStatus.Unknown, resolution.Status);
        Assert.Null(resolution.Declaration);
    }

    [Fact]
    public void 引数の数が合わなければそう返す()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Shade(float3 normal) { return normal.x; }

                            float4 frag() : SV_Target
                            {
                                float3 n = float3(0, 1, 0);
                                return Shade(n, n);
                            }
            """,
            "Shade");

        Assert.Equal(OverloadResolutionStatus.ArgumentCountMismatch, resolution.Status);
        Assert.Single(resolution.Candidates);
    }

    [Fact]
    public void 渡せる宣言が無ければそう返す()
    {
        OverloadResolution resolution = Resolve(
            """
                            float Shade(float3 normal) { return normal.x; }

                            float4 frag() : SV_Target
                            {
                                float4x4 m = (float4x4)0;
                                return Shade(m);
                            }
            """,
            "Shade");

        Assert.Equal(OverloadResolutionStatus.NoMatch, resolution.Status);
    }

    [Fact]
    public void 組み込み関数はそうと分かる()
    {
        OverloadResolution resolution = Resolve(
            """
                            float4 frag() : SV_Target
                            {
                                return lerp(0, 1, 0.5);
                            }
            """,
            "lerp");

        Assert.Equal(OverloadResolutionStatus.Intrinsic, resolution.Status);
        Assert.Null(resolution.Declaration);
    }

    [Fact]
    public void 宣言が無ければそう返す()
    {
        OverloadResolution resolution = Resolve(
            """
                            float4 frag() : SV_Target
                            {
                                return NotDeclaredAnywhere(1);
                            }
            """,
            "NotDeclaredAnywhere");

        Assert.Equal(OverloadResolutionStatus.NotDeclared, resolution.Status);
    }

    /// <summary>
    /// 出現条件で候補が絞られることの確認。
    /// </summary>
    /// <remarks>
    /// 2 つの宣言は同時には存在しない。
    /// 条件を見ずに両方を候補にすると、スカラーを渡すこの呼び出しは曖昧になる。
    /// </remarks>
    [Fact]
    public void その構成に存在する宣言だけを候補にする()
    {
        OverloadResolution resolution = Resolve(
            """
                            #pragma multi_compile _ FANCY

                            #ifdef FANCY
                            float Shade(float2 v) { return v.x; }
                            #else
                            float Shade(float3 v) { return v.x; }
                            #endif

                            float4 frag() : SV_Target
                            {
                            #ifdef FANCY
                                return Shade(1);
                            #else
                                return 0;
                            #endif
                            }
            """,
            "Shade");

        Assert.Equal(OverloadResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(["float2"], resolution.ParameterTypes.AsEnumerable());
    }

    /// <summary>メンバー呼び出しを 1 つ探して解決する。</summary>
    /// <param name="body">埋め込む HLSL。</param>
    /// <returns>解決結果。</returns>
    private static OverloadResolution ResolveMemberCall(string body)
    {
        ShaderCompilation compilation = Compile(body);
        AnalyzedProgram program = compilation.Programs[0];

        InvocationExpressionSyntax invocation = compilation.EnumerateOwnNodes(program)
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.Target is MemberAccessExpressionSyntax);

        return compilation.ResolveCall(invocation, program);
    }

    [Fact]
    public void テクスチャのメソッドは組み込みだと分かる()
    {
        OverloadResolution resolution = ResolveMemberCall(
            """
                            Texture2D _Tex;
                            SamplerState sampler_Tex;

                            float4 frag(float2 uv : TEXCOORD0) : SV_Target
                            {
                                return _Tex.Sample(sampler_Tex, uv);
                            }
            """);

        Assert.Equal(OverloadResolutionStatus.Intrinsic, resolution.Status);
        Assert.Null(resolution.Declaration);
    }

    /// <summary>
    /// 知らないメンバー呼び出しは判断できないと返すことの確認。
    /// </summary>
    /// <remarks>
    /// メソッドは宣言として書かれていないため、解決先を探しようがない。
    /// <b>「宣言が無い」ではなく「分からない」を返す。</b>
    /// </remarks>
    [Fact]
    public void 知らないメンバー呼び出しは判断できないと返す()
    {
        OverloadResolution resolution = ResolveMemberCall(
            """
                            struct Helper { float Value; };

                            float4 frag() : SV_Target
                            {
                                Helper h;
                                return h.Compute(1);
                            }
            """);

        Assert.Equal(OverloadResolutionStatus.Unknown, resolution.Status);
    }
}
