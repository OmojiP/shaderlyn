using System.Collections.Immutable;
using Shaderlyn.Cli;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL 単体ファイル (<c>.compute</c> など) の解析の検証。
/// </summary>
public sealed class ComputeShaderTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-compute-test-{Guid.NewGuid():N}");

    public ComputeShaderTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // テストの後始末が失敗してもテスト結果には影響させない。
        }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_workDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(string Output, string Error)> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync(args, BuiltInAnalyzers.All, output, error);
        return (output.ToString(), error.ToString());
    }

    /// <summary>
    /// <c>.compute</c> を ShaderLab として読まないことを検証する。
    /// </summary>
    /// <remarks>
    /// <b>これがこの機能を作った理由である。</b>
    /// 以前は正しい <c>.compute</c> に「ファイルの先頭に 'Shader' 以外の記述があります」
    /// という <c>SL0001</c> が出ていた。指摘が出ないより、間違った指摘のほうが悪い。
    /// </remarks>
    [Fact]
    public async Task computeをShaderLabとして読まない()
    {
        string path = WriteFile(
            "Valid.compute",
            """
            #pragma kernel CSMain

            RWStructuredBuffer<int> outputData;

            [numthreads(8, 1, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID)
            {
                outputData[id.x] = (int) id.x;
            }
            """);

        (string output, _) = await RunAsync(path);

        Assert.DoesNotContain("SL0001", output, StringComparison.Ordinal);
        Assert.DoesNotContain("error", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task フォルダー走査でcomputeも対象になる()
    {
        WriteFile("A.compute", "#pragma kernel K\n[numthreads(1,1,1)]\nvoid K() { }\n");

        (string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>#pragma kernel</c> が指す関数が無ければ報告することを検証する。
    /// </summary>
    /// <remarks>
    /// 埋め込み HLSL と同じ道を通っているので、HLSL のルールがそのまま働く。
    /// </remarks>
    [Fact]
    public async Task 存在しないカーネルを報告する()
    {
        string path = WriteFile(
            "Missing.compute",
            """
            #pragma kernel CSMain

            [numthreads(8, 1, 1)]
            void SomethingElse(uint3 id : SV_DispatchThreadID)
            {
            }
            """);

        (string output, _) = await RunAsync(path);

        Assert.Contains("HL0301", output, StringComparison.Ordinal);
        Assert.Contains("CSMain", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>#pragma kernel</c> が並べたマクロが効くことを検証する。
    /// </summary>
    /// <remarks>
    /// <b>これを読まないと、Unity 同梱の <c>.compute</c> が誤検出だらけになる。</b>
    /// 実測では 192 件に対して 123 件の「エントリポイントが見つかりません」が出た。
    /// 関数名そのものがマクロで、カーネルごとに違う名前になるためである。
    /// </remarks>
    [Fact]
    public async Task kernelが並べたマクロが効く()
    {
        string path = WriteFile(
            "Macros.compute",
            """
            #pragma kernel CopySmall  KERNEL_NAME=CopySmall  KERNEL_SIZE=8
            #pragma kernel CopyLarge  KERNEL_NAME=CopyLarge  KERNEL_SIZE=16

            RWStructuredBuffer<int> outputData;

            [numthreads(KERNEL_SIZE, KERNEL_SIZE, 1)]
            void KERNEL_NAME(uint3 id : SV_DispatchThreadID)
            {
                outputData[id.x] = KERNEL_SIZE;
            }
            """);

        (string output, _) = await RunAsync(path);

        Assert.DoesNotContain("HL0301", output, StringComparison.Ordinal);
        Assert.DoesNotContain("error", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// シンボルのバリアントにも、カーネルが並べたマクロが効くことを検証する。
    /// </summary>
    /// <remarks>
    /// 落とすと、このカーネルでは決して通らない <c>#ifndef USE_X</c> の中を
    /// <c>_A</c> のバリアントが読み、そこの誤りを報告する。
    /// </remarks>
    [Fact]
    public async Task シンボルのバリアントにもkernelのマクロが効く()
    {
        string path = WriteFile(
            "VariantMacros.compute",
            """
            #pragma kernel KMain USE_X
            #pragma multi_compile _ _A

            RWStructuredBuffer<float4> Result;

            [numthreads(1, 1, 1)]
            void KMain(uint id : SV_DispatchThreadID)
            {
            #ifdef _A
            #define USE_A 1
            #ifndef USE_X
                Result[id] = onlyWithoutX;
            #endif
            #endif
                Result[id] = 1;
            }
            """);

        (string output, _) = await RunAsync(path);

        Assert.DoesNotContain("onlyWithoutX", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// カーネルごとに別の解析単位になることを検証する。
    /// </summary>
    /// <remarks>
    /// <c>VARIANT=0</c> と <c>VARIANT=1</c> は両立しないので、
    /// 1 つの構成にまとめることはできない。
    /// </remarks>
    [Fact]
    public async Task カーネルごとに別の構成として解析する()
    {
        SourceText text = SourceText.From(
            """
            #pragma kernel First   ENTRY=First   VARIANT=0
            #pragma kernel Second  ENTRY=Second  VARIANT=1

            RWStructuredBuffer<int> outputData;

            [numthreads(1, 1, 1)]
            void ENTRY(uint3 id : SV_DispatchThreadID)
            {
                outputData[id.x] = VARIANT;
            }
            """,
            Path.Combine(_workDirectory, "Variants.compute"));

        ShaderCompilation compilation = ShaderCompilation.CreateForHlsl(text);

        Assert.Equal(2, compilation.Programs.Length);
        Assert.Equal(["First", "Second"], compilation.Programs.Select(p => p.KernelName));

        // それぞれの構成で、そのカーネルの関数だけが見えている。
        Assert.Contains("First", compilation.Programs[0].FunctionNames);
        Assert.DoesNotContain("Second", compilation.Programs[0].FunctionNames);
        Assert.Contains("Second", compilation.Programs[1].FunctionNames);

        await Task.CompletedTask;
    }

    /// <summary>
    /// 別のカーネルの宣言どうしを、同じ範囲の二重宣言と取り違えないことを検証する。
    /// </summary>
    /// <remarks>
    /// カーネルごとの解析単位は、どれも同じコードブロックの位置から始まる。
    /// 位置だけで見分けると、<c>#if DIRECTIONAL_LIGHT</c> の両側 (それぞれ別のカーネルでしか通らない) が
    /// 1 つの範囲に並んで見える。Unity 同梱の DiffuseShadowDenoiser.compute の形。
    /// </remarks>
    [Fact]
    public async Task 別のカーネルの同じ名前は二重宣言にしない()
    {
        string path = WriteFile(
            "KernelRedeclaration.compute",
            """
            #pragma kernel KDirectional DIRECTIONAL_LIGHT
            #pragma kernel KPoint
            #pragma multi_compile _ DISTANCE_BASED

            RWStructuredBuffer<float> Result;

            [numthreads(1, 1, 1)]
            void KDirectional(uint id : SV_DispatchThreadID) { Result[id] = 0; }

            [numthreads(1, 1, 1)]
            void KPoint(uint id : SV_DispatchThreadID)
            {
            #if defined(DISTANCE_BASED)
            #if DIRECTIONAL_LIGHT
                float angle = 1;
            #else
                float angle = 2;
            #endif
                Result[id] = angle;
            #endif
            }
            """);

        (string output, _) = await RunAsync(path);

        Assert.DoesNotContain("HL0314", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// カーネルごとに型の違う uniform を、そのカーネルの宣言で型付けすることを検証する。
    /// </summary>
    /// <remarks>
    /// HDRP の DepthOfFieldMip.compute は <c>CTYPE=float3</c> / <c>CTYPE=float4</c> をカーネルごとに指定し、
    /// <c>RWTexture2D&lt;CTYPE&gt;</c> を宣言する。シェーダー全体で最初の宣言を使うと、別のカーネルの型で判定する。
    /// </remarks>
    [Fact]
    public async Task カーネルごとに型の違うuniformはそのカーネルの宣言で型付けする()
    {
        string path = WriteFile(
            "KernelTypes.compute",
            """
            #pragma kernel KColor      MAIN=KColor      CTYPE=float3
            #pragma kernel KColorAlpha MAIN=KColorAlpha CTYPE=float4

            RWTexture2D<CTYPE> _Input;
            RWTexture2D<CTYPE> _Output;

            [numthreads(8, 8, 1)]
            void MAIN(uint2 id : SV_DispatchThreadID)
            {
                CTYPE color = _Input[id];
                color += _Input[id + uint2(1, 0)];
                _Output[id] = color;
            }
            """);

        (string output, _) = await RunAsync(path);

        Assert.DoesNotContain("HL0350", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// カーネルのコンパイルで Unity が定義する <c>SHADER_STAGE_COMPUTE</c> を定義することを検証する。
    /// </summary>
    /// <remarks>
    /// Unity の TextureXR.hlsl は、これが定義されているときだけ <c>UNITY_XR_ASSIGN_VIEW_INDEX</c> を定義する。
    /// 定義しないと、後ろに <c>;</c> を書かない呼び出しが構文エラーになる (HDRP の Exposure.compute)。
    /// </remarks>
    [Fact]
    public async Task カーネルではSHADER_STAGE_COMPUTEを定義する()
    {
        string path = WriteFile(
            "Stage.compute",
            """
            #pragma kernel KMain

            #if defined(SHADER_STAGE_COMPUTE)
            #define ASSIGN_VIEW(x)
            #endif

            RWStructuredBuffer<float> Result;

            [numthreads(1, 1, 1)]
            void KMain(uint id : SV_DispatchThreadID)
            {
                ASSIGN_VIEW(id)
                Result[id] = 1;
            }
            """);

        (string output, _) = await RunAsync(path);

        Assert.DoesNotContain("HL0001", output, StringComparison.Ordinal);
    }

    [Fact]
    public void kernelを宣言していなければ構成は1つ()
    {
        SourceText text = SourceText.From(
            "float3 Tint(float3 c) { return c * 0.5; }\n",
            Path.Combine(_workDirectory, "Fragment.hlsl"));

        ShaderCompilation compilation = ShaderCompilation.CreateForHlsl(text);

        Assert.Single(compilation.Programs);
        Assert.Null(compilation.Programs[0].KernelName);
        Assert.True(compilation.IsStandaloneHlsl);
    }

    /// <summary>
    /// ダミーの ShaderLab の木から診断が漏れないことを検証する。
    /// </summary>
    /// <remarks>
    /// 全部を空白でマスクしたテキストを ShaderLab として解析するので、
    /// そのままでは「'Shader' 宣言が見つかりません」が出る。
    /// それはダミーについての話であって、このファイルの事実ではない。
    /// </remarks>
    [Fact]
    public void ダミーのShaderLabの診断は報告しない()
    {
        SourceText text = SourceText.From(
            "#pragma kernel K\n[numthreads(1,1,1)]\nvoid K() { }\n",
            Path.Combine(_workDirectory, "Blank.compute"));

        ShaderCompilation compilation = ShaderCompilation.CreateForHlsl(text);

        ImmutableArray<Diagnostic> diagnostics = compilation.CreateAnalysisTarget().SyntaxDiagnostics;

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("SL", StringComparison.Ordinal));
    }

    /// <summary>
    /// マスクしたテキストが元のファイルと同じ長さであることを検証する。
    /// </summary>
    /// <remarks>
    /// 長さが食い違うと、位置を扱う処理が「たまたま今は通る」状態になる。
    /// </remarks>
    [Fact]
    public void ダミーのテキストは元と同じ長さになる()
    {
        SourceText text = SourceText.From(
            "#pragma kernel K\r\n\r\nvoid K() { }\r\n",
            Path.Combine(_workDirectory, "Length.compute"));

        ShaderCompilation compilation = ShaderCompilation.CreateForHlsl(text);

        Assert.Equal(text.Length, compilation.ShaderLabTree.Text.Length);
        Assert.Equal(text.LineCount, compilation.ShaderLabTree.Text.LineCount);
    }

    [Theory]
    [InlineData("A.compute", ShaderSourceKind.Hlsl)]
    [InlineData("A.hlsl", ShaderSourceKind.Hlsl)]
    [InlineData("A.cginc", ShaderSourceKind.Hlsl)]
    [InlineData("A.COMPUTE", ShaderSourceKind.Hlsl)]
    [InlineData("A.shader", ShaderSourceKind.ShaderLab)]
    [InlineData("A.txt", ShaderSourceKind.ShaderLab)]
    [InlineData("<memory>", ShaderSourceKind.ShaderLab)]
    public void 拡張子から解析する言語を決める(string path, object expected)
        => Assert.Equal((ShaderSourceKind)expected, ShaderSourceKinds.FromPath(path));

    [Fact]
    public void 走査の対象はshaderとcomputeだけ()
    {
        // .hlsl と .cginc は断片であることが多く、走査対象にすると雑音になる。
        Assert.Equal([".shader", ".compute"], CommandLineOptions.DefaultExtensions.ToArray());
    }

    [Fact]
    public void kernelの宣言を展開の前に読み取れる()
    {
        SourceText text = SourceText.From(
            """
            #pragma kernel Plain
            #pragma kernel WithMacros  ENTRY=WithMacros  SIZE=8  DEBUG_ON
            """,
            "K.compute");

        ImmutableArray<ComputeKernel> kernels =
            ComputeKernels.Collect(new HlslLexer(text).Lex(out _));

        Assert.Equal(2, kernels.Length);

        Assert.Equal("Plain", kernels[0].Name);
        Assert.Empty(kernels[0].Macros);

        Assert.Equal("WithMacros", kernels[1].Name);
        Assert.Equal("WithMacros", kernels[1].Macros["ENTRY"]);
        Assert.Equal("8", kernels[1].Macros["SIZE"]);

        // 値を書かない指定は 1 とみなす。C コンパイラの -D と同じ規則である。
        Assert.Equal("1", kernels[1].Macros["DEBUG_ON"]);
    }
}
