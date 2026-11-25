using System.Text.Json;
using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// システム値のセマンティクスをステージと向きで検査するルール (CF0302) の検証。
/// </summary>
/// <remarks>
/// 報告する例は、FXC (Windows SDK 10.0.26100.0)・DXC 1.8・slangc 2026.19 がエラーにすることを確かめたもの。
/// 報告しない例は、そのコンパイラが通すもの。
/// </remarks>
public sealed class SystemValueSemanticTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-sv-{Guid.NewGuid():N}");

    public SystemValueSemanticTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    private async Task<string> AnalyzeAsync(string fileName, string code, params string[] extraArgs)
    {
        string path = Path.Combine(_workDirectory, fileName);
        File.WriteAllText(path, code);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([path, "--format", "json", .. extraArgs], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return output.ToString();
    }

    /// <summary>そのルール (既定は CF0302) の文を取り出す。</summary>
    private static string[] Reports(string output, string id = "CF0302")
    {
        using JsonDocument document = JsonDocument.Parse(output);
        return
        [
            .. document.RootElement.GetProperty("diagnostics").EnumerateArray()
                .Where(d => d.GetProperty("id").GetString() == id)
                .Select(d => d.GetProperty("message").GetString() ?? string.Empty),
        ];
    }

    [Theory]
    [InlineData("struct V { float4 p : SV_Position; };\n[shader(\"mesh\")]\n[numthreads(1, 1, 1)]\nvoid main(out vertices V v[3], out indices uint3 t[1]) { SetMeshOutputCounts(0, 0); }\n",
        "[outputtopology(\"triangle\")] などの属性がありません (slangc がエラーにします)")]
    [InlineData("struct V { float4 p : SV_Position; };\n[outputtopology(\"triangle\")]\n[numthreads(1, 1, 1)]\nvoid main(out vertices V v, out indices uint3 t[1]) { SetMeshOutputCounts(0, 0); }\n",
        "'v' (out vertices) は配列にしてください")]
    [InlineData("struct V { float4 p : SV_Position; };\n[outputtopology(\"triangle\")]\n[numthreads(1, 1, 1)]\nvoid main(out vertices V v[], out indices uint3 t[1]) { SetMeshOutputCounts(0, 0); }\n",
        "'v' (out vertices) の配列の大きさを書いてください")]
    [InlineData("struct V { float4 p : SV_Position; };\n[shader(\"mesh\")]\n[outputtopology(\"triangle\")]\n[numthreads(1, 1, 1)]\nvoid main(out OutputVertices<V, 3> v) { }\n",
        "頂点の出力 (out vertices / OutputVertices) とインデックスの出力 (out indices / OutputIndices) の両方が必要です")]
    public async Task Slangのメッシュシェーダーに欠けているものを報告する(string code, string expected)
    {
        string output = await AnalyzeAsync("a.slang", code, "--no-config");

        Assert.Contains(Reports(output, "CF0303"), r => r.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[shader(\"vertex\")]\nfloat4 main(Atomic<int> a : A) : SV_Position { return 0; }\n", "仮引数 'a' の型 Atomic は、エントリポイントの入力と出力には使用できません")]
    [InlineData("struct Bad { float a; DifferentialPair<float> d; };\n[shader(\"vertex\")]\nfloat4 main(Bad b : A) : SV_Position { return 0; }\n", "構造体 Bad のフィールド 'd' の型 DifferentialPair")]
    [InlineData("[shader(\"vertex\")]\nAtomic<int> main() { return {}; }\n", "エントリポイント 'main' の戻り値の型 Atomic")]
    [InlineData("struct V { float4 p : SV_Position; };\n[shader(\"vertex\")]\nvoid main(out OutputVertices<V, 3> v) { }\n", "型 OutputVertices は、メッシュシェーダーの出力にだけ使用できます")]
    [InlineData("[shader(\"vertex\")]\nvoid main(LineStream<int> s) { }\n", "型 LineStream は、ジオメトリシェーダーの出力にだけ使用できます")]
    [InlineData("struct V { float4 p : SV_Position; };\n[shader(\"mesh\")]\n[outputtopology(\"triangle\")]\n[numthreads(1, 1, 1)]\nvoid main(OutputVertices<V, 3> v, out OutputIndices<uint4, 1> t) { }\n", "uint・uint2・uint3 にしてください (uint4 は使用できません)")]
    [InlineData("[shader(\"vertex\")]\nfloat4 main(matrix<float, 4, 5> m : A) : SV_Position { return 0; }\n", "行列の行と列は 1 から 4 にしてください")]
    public async Task Slangのエントリポイントで受け渡せない型を報告する(string code, string expected)
    {
        string output = await AnalyzeAsync("a.slang", code, "--no-config");

        Assert.Contains(Reports(output, "SG0407"), r => r.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slangのエントリポイントで受け渡せる型は報告しない()
    {
        string output = await AnalyzeAsync("a.slang", """
            struct V { float4 p : SV_Position; float4x4 m : M; matrix<float, 2, 3> n : N; };
            [shader("vertex")]
            V main(uint id : SV_VertexID, uniform Atomic<int>* counter) { V v = {}; return v; }

            [shader("geometry")]
            [maxvertexcount(3)]
            void gs(triangle V input[3], inout TriangleStream<V> s) { }

            [shader("mesh")]
            [outputtopology("line")]
            [numthreads(1, 1, 1)]
            void ms(OutputVertices<V, 2> v, OutputIndices<uint2, 1> t) { SetMeshOutputCounts(0, 0); }
            """, "--no-config");

        Assert.Empty(Reports(output, "SG0407"));
    }

    [Theory]
    [InlineData("a.slang", "float f(float a, float b = 1.0, no_diff float c) { return a + b + c; }\n", "'c'")]
    [InlineData("a.slang", "struct S { float g(float a = 1.0, float b) { return a + b; } }\n", "'b'")]
    [InlineData("a.hlsl", "float f(float a, float b = 1.0, float c) { return a + b + c; }\n", "'c'")]
    public async Task 既定値の後ろの既定値のない仮引数を報告する(string fileName, string code, string name)
    {
        string output = await AnalyzeAsync(fileName, code, "--no-config");

        string report = Assert.Single(Reports(output, "CF0316"));
        Assert.Contains($"仮引数 {name} に既定値がありません", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.slang", "float f(float a, float b = 1.0, float c = 2.0) { return a + b + c; }\n")]
    [InlineData("a.hlsl", "float f(float a, float b, float c = 2.0) { return a + b + c; }\n")]
    public async Task 既定値が後ろにそろっていれば報告しない(string fileName, string code)
    {
        string output = await AnalyzeAsync(fileName, code, "--no-config");

        Assert.Empty(Reports(output, "CF0316"));
    }

    [Fact]
    public async Task 正しいメッシュシェーダーは報告しない()
    {
        string output = await AnalyzeAsync("a.slang", """
            static const uint MaxVertices = 3;
            struct V { float4 p : SV_Position; };
            [shader("mesh")]
            [outputtopology("triangle")]
            [numthreads(1, 1, 1)]
            void main(out vertices V v[MaxVertices], out indices uint3 t[1]) { SetMeshOutputCounts(0, 0); }

            [shader("mesh")]
            [outputtopology("triangle")]
            [numthreads(1, 1, 1)]
            void main2(OutputVertices<V, 3> v, OutputIndices<uint3, 1> t) { SetMeshOutputCounts(0, 0); }
            """, "--no-config");

        Assert.Empty(Reports(output, "CF0303"));
    }

    [Fact]
    public async Task DXCは頂点とインデックスの出力が無くても誤りにしない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            units:
              - files: "*.hlsl"
                entry: main
                target: ms_6_6
            """);

        string output = await AnalyzeAsync("a.hlsl", """
            struct V { float4 p : SV_Position; };
            [numthreads(1, 1, 1)]
            void main(out vertices V v[3]) { SetMeshOutputCounts(0, 0); }
            """, "--config", Path.Combine(_workDirectory, ".shaderlyn.yaml"));

        string report = Assert.Single(Reports(output, "CF0303"));
        Assert.Contains("[outputtopology(\"triangle\")] などの属性がありません (DXC がエラーにします)", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[shader(\"vertex\")]\nvoid main(out float d : SV_Depth) { d = 0; }\n", "'SV_Depth' は頂点シェーダーの出力には使用できません")]
    [InlineData("[shader(\"compute\")]\n[numthreads(1, 1, 1)]\nvoid main(out uint3 id : SV_DispatchThreadID) { id = 0; }\n", "'SV_DispatchThreadID' はコンピュートシェーダーの出力には使用できません")]
    [InlineData("[shader(\"fragment\")]\nfloat4 main(uint3 g : SV_GroupID) : SV_Target { return 0; }\n", "'SV_GroupID' はピクセルシェーダーの入力には使用できません")]
    // slangc は頂点シェーダーの入力に SV_Position を使用できない (FXC と DXC は使用できる)
    [InlineData("[shader(\"vertex\")]\nfloat4 main(float4 p : SV_Position) : SV_Position { return p; }\n", "'SV_Position' は頂点シェーダーの入力には使用できません")]
    // inout は入力と出力の両方
    [InlineData("[shader(\"fragment\")]\nfloat4 main(inout bool f : SV_IsFrontFace) : SV_Target { return 0; }\n", "'SV_IsFrontFace' はピクセルシェーダーの出力には使用できません")]
    // 構造体のフィールドと、入れ子の構造体
    [InlineData("struct In { float4 p : SV_Position; float d : SV_Depth; };\n[shader(\"fragment\")]\nfloat4 main(In i) : SV_Target { return i.p; }\n", "'SV_Depth' はピクセルシェーダーの入力には使用できません")]
    [InlineData("struct Inner { uint v : SV_VertexID; };\nstruct Out { float4 p : SV_Position; Inner i; };\n[shader(\"vertex\")]\nOut main() { Out o; return o; }\n", "'SV_VertexID' は頂点シェーダーの出力には使用できません")]
    // メッシュシェーダーのプリミティブごとの値。[shader] が無くても [outputtopology] と out vertices でメッシュシェーダーとみなす
    [InlineData("struct V { float4 p : SV_Position; bool c : SV_CullPrimitive; };\n[outputtopology(\"triangle\")]\n[numthreads(1, 1, 1)]\nvoid main(out indices uint3 t[1], out vertices V v[3]) { SetMeshOutputCounts(3, 1); }\n", "out primitives の構造体に置いてください")]
    [InlineData("struct I { uint3 idx; uint p : SV_PrimitiveID; };\nstruct P { uint q : SV_PrimitiveID; };\n[outputtopology(\"triangle\")]\n[numthreads(1, 1, 1)]\nvoid main(out indices I t[1], out primitives P p[1]) { SetMeshOutputCounts(0, 0); }\n", "インデックスの出力 (out indices) には使用できません")]
    [InlineData("struct V { float4 p : SV_Position; float d : SV_Depth; };\n[shader(\"mesh\")]\n[outputtopology(\"triangle\")]\n[numthreads(1, 1, 1)]\nvoid main(OutputVertices<V, 3> v, OutputIndices<uint3, 1> t) { SetMeshOutputCounts(3, 1); }\n", "'SV_Depth' はメッシュシェーダーの頂点の出力 (out vertices) には使用できません")]
    public async Task Slangで使用できないセマンティクスを報告する(string code, string expected)
    {
        string output = await AnalyzeAsync("a.slang", code, "--no-config");

        string report = Assert.Single(Reports(output));
        Assert.Contains(expected, report, StringComparison.Ordinal);
        Assert.Contains("slangc がエラーにします", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[shader(\"vertex\")]\nfloat4 main(uint id : SV_VertexID, uint inst : SV_InstanceID) : SV_Position { return 0; }\n")]
    [InlineData("[shader(\"fragment\")]\nfloat4 main(float4 p : SV_Position, bool f : SV_IsFrontFace, inout uint c : SV_Coverage, out float d : SV_Depth) : SV_Target { d = 0; return p; }\n")]
    [InlineData("[shader(\"compute\")]\n[numthreads(8, 8, 1)]\nvoid main(uint3 id : SV_DispatchThreadID, uint i : SV_GroupIndex) { }\n")]
    // slangc は SV_Target をどこでも受け付ける
    [InlineData("[shader(\"vertex\")]\nfloat4 main(out float4 t : SV_Target) : SV_Position { t = 0; return 0; }\n")]
    // ステージが分からない関数は見ない
    [InlineData("void helper(out float d : SV_Depth) { d = 0; }\n")]
    [InlineData("struct V { float4 p : SV_Position; };\nstruct P { uint id : SV_PrimitiveID; bool c : SV_CullPrimitive; };\n[shader(\"mesh\")]\n[outputtopology(\"triangle\")]\n[numthreads(1, 1, 1)]\nvoid main(uint t : SV_GroupThreadID, out vertices V v[3], out indices uint3 i[1], out primitives P p[1]) { SetMeshOutputCounts(3, 1); }\n")]
    public async Task Slangで使用できるセマンティクスは報告しない(string code)
    {
        string output = await AnalyzeAsync("a.slang", code, "--no-config");

        Assert.Empty(Reports(output));
    }

    [Fact]
    public async Task UnityのFXCで頂点シェーダーの出力のSV_Depthを報告する()
    {
        string output = await AnalyzeAsync("Test.shader", """
            Shader "Test/Depth"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        struct v2f
                        {
                            float4 pos : SV_POSITION;
                            float depth : SV_Depth;
                        };
                        v2f vert(float4 p : POSITION, uint id : SV_VertexID) { v2f o; o.pos = p; o.depth = 0; return o; }
                        half4 frag(float4 pos : SV_POSITION, bool face : SV_IsFrontFace) : SV_Target { return 0; }
                        ENDHLSL
                    }
                }
            }
            """, "--no-config");

        string report = Assert.Single(Reports(output));
        Assert.Contains("'SV_Depth' は頂点シェーダーの出力には使用できません (FXC がエラーにします)", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FXCはシェーダーモデル6のセマンティクスを使用できない()
    {
        string output = await AnalyzeAsync("Test.shader", """
            Shader "Test/ViewId"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        float4 vert(float4 p : POSITION, uint view : SV_ViewID) : SV_POSITION { return p; }
                        half4 frag() : SV_Target { return 0; }
                        ENDHLSL
                    }
                }
            }
            """, "--no-config");

        string report = Assert.Single(Reports(output));
        Assert.Contains("'SV_ViewID' はこのコンパイラでは使用できません。シェーダーモデル 6 以降のセマンティクスです (FXC がエラーにします)", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 設定ファイルのtargetでステージを決める()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            units:
              - files: "*.hlsl"
                entry: MainVS
                target: vs_6_6
              - files: "*.hlsl"
                entry: MainHS
                target: hs_6_6
            """);

        string output = await AnalyzeAsync("a.hlsl", """
            struct C { float4 p : POSITION; };
            struct PC { float e[3] : SV_TessFactor; float i : SV_InsideTessFactor; float d : SV_Depth; };
            PC Patch(InputPatch<C, 3> ip) { PC o = (PC)0; return o; }
            void MainVS(out float d : SV_Depth) { d = 0; }
            [domain("tri")]
            [partitioning("integer")]
            [outputtopology("triangle_cw")]
            [outputcontrolpoints(3)]
            [patchconstantfunc("Patch")]
            C MainHS(InputPatch<C, 3> ip, uint id : SV_OutputControlPointID) { return ip[id]; }
            """, "--config", Path.Combine(_workDirectory, ".shaderlyn.yaml"));

        string[] reports = Reports(output);
        Assert.Equal(2, reports.Length);
        Assert.Contains(reports, r => r.Contains("'SV_Depth' は頂点シェーダーの出力には使用できません (DXC がエラーにします)", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Contains("'SV_Depth' はハルシェーダーのパッチ定数の出力には使用できません (DXC がエラーにします)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 設定ファイルにtargetが無ければ報告しない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            units:
              - files: "*.hlsl"
                entry: MainVS
            """);

        string output = await AnalyzeAsync("a.hlsl", "void MainVS(out float d : SV_Depth) { d = 0; }\n", "--config", Path.Combine(_workDirectory, ".shaderlyn.yaml"));

        Assert.Empty(Reports(output));
    }

    /// <summary>ピクセルシェーダーの深度の出力は 1 つだけ。DXC 1.8 は検証の誤り、slangc 2026.19 は E30705 にする。FXC (ps_5_0) は通す。</summary>
    [Theory]
    [InlineData("ps_6_6", "void main(out float a : SV_DepthGreaterEqual, out float b : SV_Depth) { a = 0; b = 0; }", true)]
    [InlineData("ps_6_6", "struct O { float a : SV_Depth; float b : SV_DepthLessEqual0; float4 c : SV_Target; }; O main() { O o = (O)0; return o; }", true)]
    [InlineData("ps_6_6", "void main(out float a : SV_Depth, out float4 c : SV_Target) { a = 0; c = 0; }", false)]
    [InlineData("ps_5_0", "void main(out float a : SV_DepthGreaterEqual, out float b : SV_Depth) { a = 0; b = 0; }", false)]
    public async Task ピクセルシェーダーの深度の出力は1つだけ(string target, string code, bool reported)
    {
        string compiler = target == "ps_5_0" ? "fxc" : "dxc";
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), $$"""
            version: 1
            environment: hlsl
            hlsl:
              compiler: {{compiler}}
            units:
              - files: "*.hlsl"
                entry: main
                target: {{target}}
            """);

        string output = await AnalyzeAsync("a.hlsl", code, "--config", Path.Combine(_workDirectory, ".shaderlyn.yaml"));

        Assert.Equal(reported, Reports(output).Any(r => r.Contains("深度の出力は 1 つだけです", StringComparison.Ordinal)));
    }

    /// <summary>セマンティクスを受け取れない型の値に付けたもの。型の表は、コンパイラが型の誤りにした型だけを持つ。</summary>
    [Theory]
    [InlineData("[shader(\"vertex\")]\nfloat4 main(float id : SV_ViewID) : SV_Position { return 0; }\n", "'SV_ViewID' は float の値に付けられません")]
    [InlineData("[shader(\"fragment\")]\nfloat4 main(int f : SV_IsFrontFace) : SV_Target { return 0; }\n", "'SV_IsFrontFace' は int の値に付けられません")]
    [InlineData("[shader(\"vertex\")]\nfloat4 main(uint id : SV_ViewID) : SV_Position { return 0; }\n", null)]
    [InlineData("[shader(\"fragment\")]\nfloat4 main(bool f : SV_IsFrontFace) : SV_Target { return 0; }\n", null)]
    public async Task セマンティクスを受け取れない型の値に付けたものを報告する(string code, string? expected)
    {
        string[] reports = [.. Reports(await AnalyzeAsync("a.slang", code, "--no-config")).Where(r => r.Contains("の値に付けられません", StringComparison.Ordinal))];

        if (expected is null)
        {
            Assert.Empty(reports);
        }
        else
        {
            Assert.Contains(reports, r => r.Contains(expected, StringComparison.Ordinal));
        }
    }
}
