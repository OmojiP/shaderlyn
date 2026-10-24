using System.Collections.Immutable;
using Shaderlyn.Cli;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Unreal;

namespace Shaderlyn.Tests;

/// <summary>
/// Unreal Engine の環境の検証。エンジン本体は使わず、同じ形のフォルダーを作って確かめる。
/// </summary>
public sealed class UnrealEnvironmentTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "shaderlyn-unreal-" + Guid.NewGuid().ToString("N"));

    public UnrealEnvironmentTests() => Directory.CreateDirectory(_workDirectory);

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

    [Fact]
    public void 仮想パスを割り当てたフォルダーから引く()
    {
        Write("Engine/Shaders/Private/Common.ush", "// engine");
        Write("Plugins/A/Shaders/Private/A.ush", "// plugin A");
        Write("Plugins/A/Sub/Shaders/B.ush", "// plugin A sub");

        UnrealVirtualPathResolver resolver = new(new Dictionary<string, string>
        {
            ["/Engine"] = Path.Combine(_workDirectory, "Engine", "Shaders"),
            ["/Plugin/A"] = Path.Combine(_workDirectory, "Plugins", "A", "Shaders"),
            ["/Plugin/A/Sub/"] = Path.Combine(_workDirectory, "Plugins", "A", "Sub", "Shaders"),
        });

        Assert.True(resolver.TryResolve("/Engine/Private/Common.ush", "x.usf", out SourceText? common));
        Assert.Equal("// engine", common.ToString());

        Assert.True(resolver.TryResolve("/Plugin/A/Private/A.ush", "x.usf", out SourceText? a));
        Assert.Equal("// plugin A", a.ToString());

        // 長い割り当てが勝つ。
        Assert.True(resolver.TryResolve("/Plugin/A/Sub/B.ush", "x.usf", out SourceText? b));
        Assert.Equal("// plugin A sub", b.ToString());

        // 仮想パスでないものと、割り当ての無いものは引かない。
        Assert.False(resolver.TryResolve("Private/Common.ush", "x.usf", out _));
        Assert.False(resolver.TryResolve("/Engine/Generated/GeneratedUniformBuffers.ush", "x.usf", out _));
        Assert.False(resolver.TryResolve("/Plugin/Other/X.ush", "x.usf", out _));
    }

    [Fact]
    public async Task パーミュテーションごとにUnrealのシェーダーを解析する()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
            symbols:
              - name: PERMUTATION_HIGH_QUALITY   # SHADER_PERMUTATION_BOOL は 0 か 1 で定義される
                values: [0, 1]
            units:
              - files: Shaders/**/*.usf
                entry: MainPS
                target: ps_6_6
            """);

        // エンジンの共通ヘッダは、ビルドのときに生成される uniform buffer の宣言を取り込む。
        Write("Engine/Shaders/Private/Common.ush", """
            #pragma once
            #include "/Engine/Generated/GeneratedUniformBuffers.ush"
            #if COMPILER_HLSL && SM6_PROFILE
            float Square(float x) { return x * x; }
            #endif
            """);
        Write("Shaders/Private/MyCommon.ush", """
            #pragma once
            float3 Tint(float3 c) { return c * 0.5; }
            """);
        Write("Shaders/Private/MyPass.usf", """
            #include "/Engine/Private/Common.ush"
            #include "/Project/Private/MyCommon.ush"

            float4 MainPS(float4 SvPosition : SV_Position) : SV_Target0
            {
            #if PERMUTATION_HIGH_QUALITY
                float3 color = Tint(Square(SvPosition.x));
            #else
                float4 color = Square(SvPosition.x);
            #endif
                return color.a;
            }
            """);

        (ExitCode code, string output, string error) = await RunAsync(_workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.Equal(ExitCode.DiagnosticsFound, code);

        // パーミュテーションの値ごとに読み分け、値を添えて報告する。
        Assert.Contains("HL0312", output, StringComparison.Ordinal);
        Assert.Contains("PERMUTATION_HIGH_QUALITY == 1", output, StringComparison.Ordinal);

        // 生成される取り込みは誤りとして報告しない。中身を知らないことは HL0005 で伝える。
        Assert.DoesNotContain("HL0321", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOL0004", output, StringComparison.Ordinal);
        Assert.Contains("エンジンが生成するファイル", output, StringComparison.Ordinal);
        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 宣言する関数が決まっている生成される取り込み (uniform buffer の宣言、GeneratedInstancedStereo.ush) だけが欠けているなら、
    /// ほかの名前の呼び出しは多重定義と照らす (HL0341)。以前はエンジンのシェーダーのほとんどで検査を止めていた。
    /// </summary>
    [Fact]
    public async Task 宣言する関数が決まっている生成ファイルだけが欠けていれば呼び出しを検査する()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
            """);
        Write("Engine/Shaders/Private/Common.ush", """
            #pragma once
            #include "/Engine/Generated/GeneratedUniformBuffers.ush"
            #include "/Engine/Generated/UniformBuffers/View.ush"
            #include "/Engine/Generated/GeneratedInstancedStereo.ush"
            float Square(float x) { return x * x; }
            """);
        Write("Shaders/Private/MyPass.usf", """
            #include "/Engine/Private/Common.ush"

            float4 MainPS(float4 SvPosition : SV_Position) : SV_Target0
            {
                ViewState V = GetPrimaryView(1);
                return Square(SvPosition.x, 2);
            }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("関数 'Square' に引数を 2 個", output, StringComparison.Ordinal);

        // GeneratedInstancedStereo.ush が宣言する関数は、見えない多重定義があるかもしれないので照らさない。
        Assert.DoesNotContain("'GetPrimaryView'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>hlsl: version</c> を書かなければ、エンジンの設定 (<c>EnablesHLSL2021ByDefault=1</c>) に合わせて、
    /// グローバルシェーダーは HLSL 2021、マテリアルのシェーダー (ヘッダを経由して Material.ush を取り込むもの) は 2018 で読む。
    /// </summary>
    /// <remarks>
    /// どちらで読んだかは <c>COMPILER_SUPPORTS_HLSL2021</c> の分岐で見分ける (分岐の中にわざと構文エラーを置く)。
    /// マテリアルのシェーダーでも template は誤りにしない。シェーダーの C++ が個別に 2021 にすることがある (Nanite など)。
    /// </remarks>
    [Fact]
    public async Task エンジンの設定に合わせてシェーダーごとに言語のバージョンを決める()
    {
        WriteEngineDefault(1);
        Write("Engine/Shaders/Private/MaterialCommon.ush", """
            #pragma once
            #include "/Engine/Generated/Material.ush"
            """);
        Write("Shaders/Private/GlobalPass.usf", """
            template<typename T> T Twice(T v) { return v + v; }
            #if COMPILER_SUPPORTS_HLSL2021
            float Marker2021 = ;
            #else
            float Marker2018 = ;
            #endif
            float4 MainPS() : SV_Target0 { return Twice(1.0); }
            """);
        Write("Shaders/Private/MaterialPass.usf", """
            #include "/Engine/Private/MaterialCommon.ush"
            template<typename T> T Twice(T v) { return v + v; }
            #if COMPILER_SUPPORTS_HLSL2021
            float Marker2021 = ;
            #else
            float Marker2018 = ;
            #endif
            float4 MainPS() : SV_Target0 { return Twice(1.0); }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("GlobalPass.usf(3:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalPass.usf(5:", output, StringComparison.Ordinal);
        Assert.Contains("MaterialPass.usf(6:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MaterialPass.usf(4:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HL0003", output, StringComparison.Ordinal);
    }

    /// <summary><c>hlsl: version</c> を書けば、エンジンの設定によらず、すべてのシェーダーをそのバージョンで厳しく読む。</summary>
    [Fact]
    public async Task 言語のバージョンを書けばエンジンの設定より優先する()
    {
        WriteEngineDefault(1, hlslVersion: 2018);
        Write("Shaders/Private/GlobalPass.usf", """
            template<typename T> T Twice(T v) { return v + v; }
            float4 MainPS() : SV_Target0 { return Twice(1.0); }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("HL0003", output, StringComparison.Ordinal);
    }

    /// <summary>エンジンの設定が無ければ、今までどおり HLSL 2018 で読む。</summary>
    [Fact]
    public async Task エンジンの設定が無ければHLSL2018で読む()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
            """);
        Write("Engine/Shaders/Private/Common.ush", "#pragma once\n");
        Write("Shaders/Private/GlobalPass.usf", """
            template<typename T> T Twice(T v) { return v + v; }
            float4 MainPS() : SV_Target0 { return Twice(1.0); }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("HL0003", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// プラットフォームの設定から作るマクロで、節に書いていないキーはエンジンの既定値にする。
    /// <c>bSupportsVertexShaderSRVs</c> と <c>bSupportsClipDistance</c> は既定が true なので 1、ほかは 0。
    /// </summary>
    [Fact]
    public void プラットフォームの設定に無いキーはエンジンの既定値にする()
    {
        ImmutableDictionary<string, string> macros = UnrealOptions.CreateDataDrivenMacros(
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("bSupportsMeshShadersTier1", "true")
                .Add("bSupportsClipDistance", "false"));

        Assert.Equal("1", macros["PLATFORM_SUPPORTS_VERTEX_SHADER_SRVS"]);
        Assert.Equal("0", macros["PLATFORM_SUPPORTS_CLIP_DISTANCE"]);
        Assert.Equal("1", macros["PLATFORM_SUPPORTS_MESH_SHADERS_TIER1"]);
        Assert.Equal("0", macros["PLATFORM_SUPPORTS_MESH_SHADERS_TIER0"]);
    }

    /// <summary>エンジンのプラットフォームの設定と、設定ファイルを書く。</summary>
    /// <param name="enablesHlsl2021ByDefault"><c>PCD3D_SM6</c> の <c>EnablesHLSL2021ByDefault</c>。</param>
    /// <param name="hlslVersion">設定ファイルの <c>hlsl: version</c>。書かなければ <see langword="null"/>。</param>
    private void WriteEngineDefault(int enablesHlsl2021ByDefault, int? hlslVersion = null)
    {
        Write(".shaderlyn.yaml", $"""
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
            {(hlslVersion is { } version ? $"hlsl:\n  version: {version}\n" : string.Empty)}
            """);
        Write("Engine/Config/Windows/DataDrivenPlatformInfo.ini", $"""
            [ShaderPlatform PCD3D_SM5]
            Language=HLSL

            [ShaderPlatform PCD3D_SM6]
            Language=HLSL
            EnablesHLSL2021ByDefault={enablesHlsl2021ByDefault}
            """);
    }

    /// <summary>関数を含みうる生成される取り込み (Material.ush) が欠けていれば、呼び出しは照らさない。</summary>
    [Fact]
    public async Task 関数を含みうる生成ファイルが欠けていれば呼び出しを検査しない()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
            """);
        Write("Engine/Shaders/Private/Common.ush", """
            #pragma once
            #include "/Engine/Generated/Material.ush"
            float Square(float x) { return x * x; }
            """);
        Write("Shaders/Private/MyPass.usf", """
            #include "/Engine/Private/Common.ush"
            float4 MainPS(float4 SvPosition : SV_Position) : SV_Target0 { return Square(SvPosition.x, 2); }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.DoesNotContain("HL0341", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task エンジンの場所が無ければUnrealの直し方を案内する()
    {
        Write(".shaderlyn.yaml", "environment: unreal\n");
        Write("Shaders/MyPass.usf", """
            #include "/Engine/Private/Common.ush"
            float4 MainPS() : SV_Target0 { return 1; }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("TOOL0004", output, StringComparison.Ordinal);
        Assert.Contains("Unreal の仮想パス", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 環境の表示に仮想パスを出す()
    {
        Write(".shaderlyn.yaml", """
            environment: unreal
            unreal:
              engine: Engine
              virtual-paths:
                /Plugin/MyPlugin: Plugins/MyPlugin/Shaders
            """);
        Directory.CreateDirectory(Path.Combine(_workDirectory, "Engine", "Shaders"));

        (ExitCode code, string output, _) = await RunAsync(_workDirectory, "--env");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("環境: Unreal Engine", output, StringComparison.Ordinal);
        Assert.Contains("/Engine → ", output, StringComparison.Ordinal);
        Assert.Contains("/Plugin/MyPlugin → ", output, StringComparison.Ordinal);
        Assert.Contains("(ありません)", output, StringComparison.Ordinal);
        Assert.Contains("言語のバージョン: エンジンの設定を読めないので 2018 (既定)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--unreal-generate</c> で C++ から uniform buffer の宣言とエンジンが定義するマクロの名前を書き出し、
    /// <c>unreal: generated</c> に書けば、宣言が揃ったものとして宣言の検査 (HL0310) を行う。
    /// </summary>
    /// <summary>
    /// エンジンと同じく、単位が名前で参照する uniform buffer だけを取り込む。
    /// すべてを取り込むと、シェーダーが自分で宣言した同じ名前 (uniform buffer のリソースを平らにした名前) と重なり、
    /// 二重の宣言 (HL0314) と報告していた (Unreal の HairStrandsTangent.usf の HairStrandsVF_CullingIndexBuffer)。
    /// </summary>
    [Fact]
    public async Task 名前で参照するuniform_bufferだけを取り込む()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
              generated: Generated
            """);
        Write("Engine/Source/Runtime/Renderer/Private/Params.h", """
            #pragma once
            BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(FMyViewParameters, RENDERER_API)
                SHADER_PARAMETER(FVector4f, ScreenSize)
            END_GLOBAL_SHADER_PARAMETER_STRUCT()
            BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(FHairParameters, RENDERER_API)
                SHADER_PARAMETER_SRV(Buffer<uint>, CullingIndexBuffer)
            END_GLOBAL_SHADER_PARAMETER_STRUCT()
            """);
        Write("Engine/Source/Runtime/Renderer/Private/Params.cpp", """
            #include "Params.h"
            IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT(FMyViewParameters, "MyView");
            IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT(FHairParameters, "HairVF");
            """);
        Write("Engine/Shaders/Private/Common.ush", """
            #pragma once
            #include "/Engine/Generated/GeneratedUniformBuffers.ush"
            float2 ScreenScale() { return MyView.ScreenSize.zw; }
            """);

        // HairVF を名前で参照しないので、HairVF は取り込まれない。自分で宣言した同じ名前は二重の宣言ではない。
        Write("Shaders/Private/Own.usf", """
            #include "/Engine/Private/Common.ush"
            Buffer<uint> HairVF_CullingIndexBuffer;
            [numthreads(64, 1, 1)]
            void MainCS(uint3 Id : SV_DispatchThreadID)
            {
                float x = HairVF_CullingIndexBuffer[Id.x] * ScreenScale().x;
            }
            """);

        // HairVF を名前で参照するので取り込まれる。同じ名前を宣言すれば二重の宣言である。
        Write("Shaders/Private/Both.usf", """
            #include "/Engine/Private/Common.ush"
            Buffer<uint> HairVF_CullingIndexBuffer;
            [numthreads(64, 1, 1)]
            void MainCS(uint3 Id : SV_DispatchThreadID)
            {
                float x = HairVF.CullingIndexBuffer[Id.x] * ScreenScale().x;
            }
            """);

        string config = Path.Combine(_workDirectory, ".shaderlyn.yaml");
        (ExitCode generateCode, _, string generateError) =
            await RunAsync("--config", config, "--unreal-generate", Path.Combine(_workDirectory, "Generated"));

        Assert.True(generateError.Length == 0, generateError);
        Assert.Equal(ExitCode.Success, generateCode);

        (_, string output, string error) = await RunAsync(Path.Combine(_workDirectory, "Shaders"));

        Assert.True(error.Length == 0, error);
        Assert.DoesNotContain("HL0310", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Own.usf(2:", output, StringComparison.Ordinal);
        Assert.Single(output.Split('\n'), line => line.Contains("Both.usf(2:", StringComparison.Ordinal) && line.Contains("HL0314", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CPlusPlusから作った宣言で宣言の検査を行う()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
              generated: Generated
            """);
        Write("Engine/Source/Runtime/Renderer/Private/MyView.h", """
            #pragma once
            BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(FMyViewParameters, RENDERER_API)
                SHADER_PARAMETER(FVector4f, ScreenSize)
                SHADER_PARAMETER_TEXTURE(Texture2D, MyTexture)
                SHADER_PARAMETER_SAMPLER(SamplerState, MySampler)
            END_GLOBAL_SHADER_PARAMETER_STRUCT()
            """);
        Write("Engine/Source/Runtime/Renderer/Private/MyView.cpp", """
            #include "MyView.h"
            IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT(FMyViewParameters, "MyView");

            BEGIN_SHADER_PARAMETER_STRUCT(FGPUSceneResourceParameters, )
                SHADER_PARAMETER(uint32, FrameNumber)
            END_SHADER_PARAMETER_STRUCT()
            IMPLEMENT_SCENE_UB_STRUCT(FGPUSceneResourceParameters, GPUScene, ConstructDefault);

            class FMyCS : public FGlobalShader
            {
                class FFastDim : SHADER_PERMUTATION_BOOL("DIM_FAST");
                static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, FShaderCompilerEnvironment& OutEnvironment)
                {
                    OutEnvironment.SetDefine(TEXT("THREADGROUP_SIZE"), 64);
                    SET_SHADER_DEFINE(OutEnvironment, MY_SCALE, 2.0f);
                }
            };
            """);
        Write("Engine/Shaders/Private/Common.ush", """
            #pragma once
            #include "/Engine/Generated/GeneratedUniformBuffers.ush"
            """);
        Write("Shaders/Private/MyPass.usf", """
            #include "/Engine/Private/Common.ush"

            [numthreads(THREADGROUP_SIZE, 1, 1)]
            void MainCS(uint3 Id : SV_DispatchThreadID)
            {
                float2 uv = Id.xy * MyView.ScreenSize.zw * MY_SCALE;
                float4 c = MyView.MyTexture.SampleLevel(MyView.MySampler, uv, 0);
                uint frame = Scene.GPUScene.FrameNumber + DIM_FAST;
                float x = c.x + frame + Undeclared;
            }
            """);

        string config = Path.Combine(_workDirectory, ".shaderlyn.yaml");
        (ExitCode generateCode, string generateOutput, string generateError) =
            await RunAsync("--config", config, "--unreal-generate", Path.Combine(_workDirectory, "Generated"));

        Assert.True(generateError.Length == 0, generateError);
        Assert.Equal(ExitCode.Success, generateCode);
        Assert.Contains("uniform buffer 2 個", generateOutput, StringComparison.Ordinal);
        Assert.Contains("マクロ 3 個", generateOutput, StringComparison.Ordinal);

        (_, string output, string error) = await RunAsync(Path.Combine(_workDirectory, "Shaders"));

        Assert.True(error.Length == 0, error);
        Assert.DoesNotContain("HL0005", output, StringComparison.Ordinal);
        Assert.Contains("'Undeclared'", output, StringComparison.Ordinal);
        Assert.Single(output.Split('\n'), line => line.Contains("HL0310", StringComparison.Ordinal));
    }

    /// <summary>
    /// 欠けているのが生成されるファイルだけなら、HL0005 ではなく Unreal のルールで伝える。
    /// C++ から作れるものが欠けていれば UE0001、マテリアルごとに作られるものだけなら UE0002。
    /// </summary>
    [Fact]
    public async Task 生成されるファイルが欠けていればUnrealのルールで伝える()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
            """);
        Write("Engine/Shaders/Private/Common.ush", """
            #pragma once
            #include "/Engine/Generated/GeneratedUniformBuffers.ush"
            """);
        Write("Shaders/Private/GlobalPass.usf", """
            #include "/Engine/Private/Common.ush"
            #include "/Engine/Generated/Material.ush"
            float4 MainPS() : SV_Target0 { return 1; }
            """);
        Write("Shaders/Private/MaterialPass.usf", """
            #include "/Engine/Generated/Material.ush"
            #include "/Engine/Generated/VertexFactory.ush"
            float4 MainPS() : SV_Target0 { return 1; }
            """);
        Write("Shaders/Private/BrokenPass.usf", """
            #include "/Engine/Private/Missing.ush"
            #include "/Engine/Generated/Material.ush"
            float4 MainPS() : SV_Target0 { return 1; }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("GlobalPass.usf(1:1): info UE0001", output, StringComparison.Ordinal);
        Assert.Contains("--unreal-generate", output, StringComparison.Ordinal);
        Assert.Contains("MaterialPass.usf(1:1): info UE0002", output, StringComparison.Ordinal);

        // 生成されないファイルも欠けていれば、設定の誤りの見込みが高いので HL0005 で伝える。
        Assert.Contains("BrokenPass.usf(1:1): info HL0005", output, StringComparison.Ordinal);
        Assert.DoesNotContain("BrokenPass.usf(1:1): info UE", output, StringComparison.Ordinal);
        Assert.Single(output.Split('\n'), line => line.Contains(": info HL0005", StringComparison.Ordinal));
    }

    [Fact]
    public void CPlusPlusの構造体からuniform_bufferの宣言を作る()
    {
        string output = Path.Combine(_workDirectory, "Generated");
        UnrealUniformBufferGeneration generation = UnrealUniformBufferGenerator.GenerateFromTexts(
        [
            ("Params.h", """
                #define MAX_ITEMS 4
                BEGIN_SHADER_PARAMETER_STRUCT(FInner, )
                    SHADER_PARAMETER(FIntPoint, Size)
                END_SHADER_PARAMETER_STRUCT()
                BEGIN_SHADER_PARAMETER_STRUCT(FShared, )
                    SHADER_PARAMETER(float, Shared)
                END_SHADER_PARAMETER_STRUCT()
                BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(FOuter, )
                    SHADER_PARAMETER_ARRAY(FMatrix44f, Matrices, [MAX_ITEMS * 2])
                    SHADER_PARAMETER_SCALAR_ARRAY(uint32, Flags, [6])
                    SHADER_PARAMETER_STRUCT(FInner, Inner)
                    SHADER_PARAMETER_STRUCT_INCLUDE(FShared, SharedParameters)
                    SHADER_PARAMETER_RDG_BUFFER_SRV(StructuredBuffer<float4>, Items)
                    RDG_BUFFER_ACCESS(Indirect, ERHIAccess::IndirectArgs)
                END_GLOBAL_SHADER_PARAMETER_STRUCT()
                """),
            ("Params.cpp", """
                // IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT(FOuter, "Commented");
                IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT(FOuter, "Outer");
                IMPLEMENT_SCENE_UB_STRUCT(FInner, Zeta, ConstructDefault);
                IMPLEMENT_SCENE_UB_STRUCT(FShared, Alpha, ConstructDefault);
                """),
        ],
        output);

        Assert.Equal(2, generation.UniformBufferCount);
        Assert.Empty(generation.Incomplete);
        Assert.Empty(generation.UnknownTypes);

        string outer = File.ReadAllText(Path.Combine(output, "UniformBuffers", "Outer.ush"));
        Assert.Contains("float4x4 Outer_Matrices[8];", outer, StringComparison.Ordinal);
        Assert.Contains("uint4 Outer_Flags[2];", outer, StringComparison.Ordinal);
        Assert.Contains("int2 Outer_Inner_Size;", outer, StringComparison.Ordinal);
        Assert.Contains("float Outer_Shared;", outer, StringComparison.Ordinal);
        Assert.Contains("StructuredBuffer<float4> Outer_Items;", outer, StringComparison.Ordinal);
        Assert.Contains("FShaderlynUniformBuffer_Outer_Inner Inner;", outer, StringComparison.Ordinal);
        Assert.Contains("static const FShaderlynUniformBuffer_Outer Outer;", outer, StringComparison.Ordinal);
        Assert.DoesNotContain("Indirect", outer, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "UniformBuffers", "Commented.ush")));

        // Scene はメンバーの名前の順に並べる。
        string scene = File.ReadAllText(Path.Combine(output, "UniformBuffers", "Scene.ush"));
        Assert.True(
            scene.IndexOf("Scene_Alpha_Shared", StringComparison.Ordinal) < scene.IndexOf("Scene_Zeta_Size", StringComparison.Ordinal),
            scene);

        Assert.Contains("#include \"/Engine/Generated/UniformBuffers/Outer.ush\"", File.ReadAllText(Path.Combine(output, "GeneratedUniformBuffers.ush")), StringComparison.Ordinal);
    }

    /// <summary>マテリアルのシェーダーには、エンジンと同じく <c>IS_MATERIAL_SHADER=1</c> を定義する。</summary>
    [Fact]
    public async Task マテリアルのシェーダーにはマテリアルの印を定義する()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
            """);
        Write("Engine/Shaders/Private/MaterialCommon.ush", """
            #pragma once
            #include "/Engine/Generated/Material.ush"
            """);
        Write("Shaders/Private/MaterialPass.usf", """
            #include "/Engine/Private/MaterialCommon.ush"
            #if IS_MATERIAL_SHADER
            float MarkerMaterial = ;
            #else
            float MarkerGlobal = ;
            #endif
            """);
        Write("Shaders/Private/GlobalPass.usf", """
            #if IS_MATERIAL_SHADER
            float MarkerMaterial = ;
            #else
            float MarkerGlobal = ;
            #endif
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("MaterialPass.usf(3:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MaterialPass.usf(5:", output, StringComparison.Ordinal);
        Assert.Contains("GlobalPass.usf(4:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalPass.usf(2:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void エディタが書き出したGBufferの取り込みと生成した取り込みを割り当てる()
    {
        string autogen = Path.Combine(_workDirectory, "Intermediate", "ShaderAutogen", "PCD3D_SM6");
        Directory.CreateDirectory(autogen);

        UnrealOptions options = new()
        {
            ProjectDirectory = _workDirectory,
            GeneratedDirectory = Path.Combine(_workDirectory, "Generated"),
        };

        IReadOnlyDictionary<string, string> mappings = options.GetVirtualPathMappings();
        Assert.Equal(autogen, mappings["/Engine/Generated/ShaderAutogen"]);
        Assert.Equal(Path.Combine(_workDirectory, "Generated"), mappings["/Engine/Generated"]);
    }

    /// <summary>
    /// C++ のシェーダーの登録 (3 つの形とヒットグループ) を読み、読めなかった独自のマクロを数える。
    /// コメントとマクロの定義の中は読まない。シェーダーのパイプラインは登録ではないので数えない。
    /// 登録を包んだ独自のマクロは、定義が見つかれば呼び出しを展開して読む (マクロを呼ぶマクロも、<c>#</c> と <c>##</c> も)。
    /// 展開した中の <c>typedef</c> の名前で登録したクラスは、テンプレートの引数を書いたクラスにする。
    /// </summary>
    [Fact]
    public void CPlusPlusのシェーダーの登録を読む()
    {
        string output = Path.Combine(_workDirectory, "Generated");
        UnrealUniformBufferGeneration generation = UnrealUniformBufferGenerator.GenerateFromTexts(
        [
            ("Macros.h", """
                #define IMPLEMENT_MY_SHADER(Name) IMPLEMENT_GLOBAL_SHADER(Name, "/Engine/Private/Y.usf", "Main", SF_Pixel)
                #define IMPLEMENT_RESOLVE_SHADER(Width, MSAA) \
                	typedef TResolvePS<Width, MSAA> FResolve##Width##_##MSAA##PS; \
                	IMPLEMENT_SHADER_TYPE(template<>, FResolve##Width##_##MSAA##PS, TEXT("/Engine/Private/Resolve.usf"), TEXT("ResolvePS"), SF_Pixel)
                #define IMPLEMENT_RESOLVE_SHADERS(Width) \
                	IMPLEMENT_RESOLVE_SHADER(Width, 0) \
                	IMPLEMENT_RESOLVE_SHADER(Width, 1)
                """),
            ("Pass.cpp", """
                // IMPLEMENT_GLOBAL_SHADER(FCommented, "/Engine/Private/X.usf", "MainPS", SF_Pixel);
                IMPLEMENT_GLOBAL_SHADER(FPassPS, "/Engine/Private/Pass.usf", "MainPS", SF_Pixel);
                IMPLEMENT_SHADER_TYPE(template<>, TPassCS<1>, TEXT( "/Engine/Private/Pass.usf" ), TEXT("MainCS"), SF_Compute);
                IMPLEMENT_MATERIAL_SHADER_TYPE(, FMaterialVS, TEXT("/Engine/Private/Material.usf"), TEXT("MainVS"), SF_Vertex);
                IMPLEMENT_GLOBAL_SHADER(FHitGroup, "/Engine/Private/RayTracing.usf", "closesthit=MainCHS anyhit=MainAHS", SF_RayHitGroup);
                IMPLEMENT_MY_SHADER(FFirst);
                IMPLEMENT_MY_SHADER(FSecond);
                IMPLEMENT_RESOLVE_SHADERS(2);
                IMPLEMENT_UNKNOWN_SHADER(FThird);
                IMPLEMENT_SHADERPIPELINE_TYPE_VSPS(FPipeline, FMaterialVS, FPassPS, true);
                """),
        ],
        output);

        Assert.Equal(8, generation.ShaderRegistrationCount);
        Assert.Equal(1, Assert.Single(generation.UnreadShaderRegistrations).Value);
        Assert.Contains("IMPLEMENT_UNKNOWN_SHADER", generation.UnreadShaderRegistrations.Keys);

        ImmutableArray<UnrealShaderRegistration> read = UnrealShaderRegistrations.Read(output);
        Assert.Contains(read, r => r is { VirtualPath: "/Engine/Private/Pass.usf", EntryPoint: "MainCS", Frequency: "Compute", Macro: "IMPLEMENT_SHADER_TYPE" });
        Assert.Contains(read, r => r is { VirtualPath: "/Engine/Private/Material.usf", EntryPoint: "MainVS", Macro: "IMPLEMENT_MATERIAL_SHADER_TYPE" });
        Assert.Equal(["MainCHS", "MainAHS"], UnrealShaderRegistrations.EntryFunctions(Assert.Single(read, r => r.Frequency == "RayHitGroup").EntryPoint).ToArray());
        Assert.Contains(read, r => r is { VirtualPath: "/Engine/Private/Y.usf", ShaderClass: "FSecond", Macro: "IMPLEMENT_MY_SHADER" });
        Assert.Equal(
            ["TResolvePS<2,0>", "TResolvePS<2,1>"],
            read.Where(r => r.VirtualPath == "/Engine/Private/Resolve.usf").Select(r => r.ShaderClass).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// C++ のシェーダーのクラスから、定義するマクロとパーミュテーションを読む。
    /// 継承をたどり、ほかのクラスの <c>ModifyCompilationEnvironment</c> には引数を渡して追う。
    /// 引数と定数で決まる <c>if</c> は評価し、決まらないものの中の定義は値を決めない。
    /// クラスを作るマクロは、ほかのファイルで定義されていても、マクロを呼ぶマクロを通しても展開する。
    /// </summary>
    [Fact]
    public void CPlusPlusのシェーダーのクラスを読む()
    {
        string output = Path.Combine(_workDirectory, "Generated");
        UnrealUniformBufferGenerator.GenerateFromTexts(
        [
            ("Common.h", """
                enum class EDispatch : uint8 { Inline, Pipeline, MAX };
                enum class EQuality { Low, Medium, High };

                class FTracingBase : public FGlobalShader
                {
                public:
                    static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, EDispatch Dispatch, FShaderCompilerEnvironment& OutEnvironment)
                    {
                        OutEnvironment.SetDefine(TEXT("TRACING"), 1);
                        const bool bInline = Dispatch == EDispatch::Inline;
                        if (bInline)
                        {
                            OutEnvironment.SetDefine(TEXT("INLINE_TRACING"), 1);
                            OutEnvironment.CompilerFlags.Add(CFLAG_InlineRayTracing);
                        }
                        if (CVarSomething.GetValueOnAnyThread() > 0)
                        {
                            OutEnvironment.SetDefine(TEXT("FROM_CVAR"), 1);
                        }
                    }
                };

                #define IMPLEMENT_INLINE_SHADER(ShaderClass) \
                    class ShaderClass##CS : public ShaderClass \
                    { \
                        static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, FShaderCompilerEnvironment& OutEnvironment) \
                        { \
                            ShaderClass::ModifyCompilationEnvironment(Parameters, EDispatch::Inline, OutEnvironment); \
                        } \
                    };
                #define IMPLEMENT_PIPELINE_SHADER(ShaderClass) \
                    class ShaderClass##RGS : public ShaderClass \
                    { \
                        static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, FShaderCompilerEnvironment& OutEnvironment) \
                        { \
                            ShaderClass::ModifyCompilationEnvironment(Parameters, EDispatch::Pipeline, OutEnvironment); \
                        } \
                    };
                #define IMPLEMENT_BOTH_SHADERS(ShaderClass) IMPLEMENT_INLINE_SHADER(ShaderClass) IMPLEMENT_PIPELINE_SHADER(ShaderClass)
                """),
            ("Pass.cpp", """
                static constexpr int32 MaxSamples = 3;

                class FReflection : public FTracingBase
                {
                    class FQuality : SHADER_PERMUTATION_ENUM_CLASS("DIM_QUALITY", EQuality);
                    class FSamples : SHADER_PERMUTATION_INT("DIM_SAMPLES", MaxSamples);
                    class FSparse : SHADER_PERMUTATION_SPARSE_INT("DIM_SPARSE", 1, 4, 8);
                    class FFlag : SHADER_PERMUTATION_BOOL("DIM_FLAG");
                    using FCommon = TShaderPermutationDomain<FQuality, FSamples>;
                public:
                    using FPermutationDomain = TShaderPermutationDomain<FCommon, FSparse, FFlag>;
                };

                IMPLEMENT_BOTH_SHADERS(FReflection)
                IMPLEMENT_GLOBAL_SHADER(FReflectionCS, "/Engine/Private/Reflection.usf", "ReflectionCS", SF_Compute);
                IMPLEMENT_GLOBAL_SHADER(FReflectionRGS, "/Engine/Private/Reflection.usf", "ReflectionRGS", SF_RayGen);

                class FPlainPS : public FGlobalShader
                {
                    static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, FShaderCompilerEnvironment& OutEnvironment)
                    {
                        FGlobalShader::ModifyCompilationEnvironment(Parameters, OutEnvironment);
                        OutEnvironment.SetDefine(TEXT("THREADGROUP_SIZE"), 64);
                        OutEnvironment.SetDefine(TEXT("USE_TEXT"), TEXT("2"));
                        OutEnvironment.SetDefine(TEXT("ENABLED"), true);
                        OutEnvironment.SetDefine(TEXT("FROM_FUNCTION"), GetSize());
                        if (true) { OutEnvironment.SetDefine(TEXT("ALWAYS"), 1); } else { OutEnvironment.SetDefine(TEXT("NEVER"), 1); }
                        OutEnvironment.SetDefine(TEXT("BIN_PASS"), BIN_RESERVE);
                    }
                };

                IMPLEMENT_GLOBAL_SHADER(FPlainPS, "/Engine/Private/Plain.usf", "MainPS", SF_Pixel);
                """),

            // C++ とシェーダーが共有するヘッダ (Shaders/Shared) の整数のマクロも、値として読む。
            ("Shared/Definitions.h", """
                #define BIN_INIT 0
                #define BIN_RESERVE (2) // reserve
                """),
        ],
        output);

        ImmutableDictionary<string, UnrealShaderClass> classes = UnrealShaderClasses.Read(output);

        UnrealShaderClass compute = classes["FReflectionCS"];
        Assert.Equal("1", compute.Defines["TRACING"]);
        Assert.Equal("1", compute.Defines["INLINE_TRACING"]);
        Assert.Equal("1", compute.CompilerFlags["CFLAG_InlineRayTracing"]);
        Assert.Null(compute.Defines["FROM_CVAR"]);
        Assert.Equal([0, 1, 2], compute.Permutations["DIM_QUALITY"]!.Value.ToArray());
        Assert.Equal([0, 1, 2], compute.Permutations["DIM_SAMPLES"]!.Value.ToArray());
        Assert.Equal([1, 4, 8], compute.Permutations["DIM_SPARSE"]!.Value.ToArray());
        Assert.Equal([0, 1], compute.Permutations["DIM_FLAG"]!.Value.ToArray());

        UnrealShaderClass rayGen = classes["FReflectionRGS"];
        Assert.Equal("1", rayGen.Defines["TRACING"]);
        Assert.False(rayGen.Defines.ContainsKey("INLINE_TRACING"));
        Assert.False(rayGen.CompilerFlags.ContainsKey("CFLAG_InlineRayTracing"));

        UnrealShaderClass plain = classes["FPlainPS"];
        Assert.Equal("64", plain.Defines["THREADGROUP_SIZE"]);
        Assert.Equal("2", plain.Defines["USE_TEXT"]);
        Assert.Equal("1", plain.Defines["ENABLED"]);
        Assert.Null(plain.Defines["FROM_FUNCTION"]);
        Assert.Equal("1", plain.Defines["ALWAYS"]);
        Assert.False(plain.Defines.ContainsKey("NEVER"));
        Assert.Equal("2", plain.Defines["BIN_PASS"]);
        Assert.Empty(plain.Permutations);
    }

    /// <summary>
    /// パーミュテーションの値の組ごとに、エンジンと同じく <c>ShouldCompilePermutation</c> と <c>ModifyCompilationEnvironment</c> を実行する。
    /// コンパイルされない組の値は除き、組で値の変わる定義は軸にして、同時には現れない値の組を書き出す。
    /// テンプレートのクラスは、登録に書かれた引数ごとに読む。
    /// </summary>
    [Fact]
    public void CPlusPlusのクラスをパーミュテーションの組ごとに読む()
    {
        string output = Path.Combine(_workDirectory, "Generated");
        UnrealUniformBufferGenerator.GenerateFromTexts(
        [
            ("Pass.cpp", """
                enum class ELightSourceMode
                {
                    // シェーダーと合わせる
                    Punctual = 0,
                    FromReceiver = 2,
                };

                enum class EShadowType : uint8 { Directional, Point, Receiver, MAX };

                enum ECollisionMode { PCM_None, PCM_DepthBuffer };

                class FShadowCS : public FGlobalShader
                {
                    static constexpr int32 MaxBatch = 2;
                    class FShadowType : SHADER_PERMUTATION_ENUM_CLASS("SHADOW_TYPE", EShadowType);
                    class FInline : SHADER_PERMUTATION_BOOL("DIM_INLINE");
                    class FBatch : SHADER_PERMUTATION_RANGE_INT("BATCH_SIZE", 1, FShadowCS::MaxBatch);
                    using FPermutationDomain = TShaderPermutationDomain<FShadowType, FInline, FBatch>;

                    static FPermutationDomain RemapPermutation(FPermutationDomain PermutationVector)
                    {
                        if (PermutationVector.Get<FShadowType>() == EShadowType::Directional)
                        {
                            PermutationVector.Set<FBatch>(1);
                        }
                        return PermutationVector;
                    }

                    static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& Parameters)
                    {
                        FPermutationDomain PermutationVector(Parameters.PermutationId);
                        if (RemapPermutation(PermutationVector) != PermutationVector)
                        {
                            return false;
                        }
                        if (PermutationVector.Get<FInline>() && PermutationVector.Get<FShadowType>() == EShadowType::Receiver)
                        {
                            return false;
                        }
                        return !IsMobilePlatform(Parameters.Platform);
                    }

                    static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, FShaderCompilerEnvironment& OutEnvironment)
                    {
                        FPermutationDomain PermutationVector(Parameters.PermutationId);
                        EShadowType ShadowType = PermutationVector.Get<FShadowType>();
                        ELightSourceMode Mode = (ELightSourceMode)0;
                        if (ShadowType == EShadowType::Receiver)
                        {
                            Mode = ELightSourceMode::FromReceiver;
                        }
                        OutEnvironment.SetDefine(TEXT("LIGHT_SOURCE_MODE"), (uint32)Mode);
                        OutEnvironment.SetDefine(TEXT("TILE_VERTEX_SHADER"), 1.0f);
                        if (PermutationVector.Get<FInline>())
                        {
                            OutEnvironment.CompilerFlags.Add(CFLAG_AllowRealTypes);
                        }
                    }
                };

                template <ECollisionMode CollisionMode>
                class TSimulationPS : public FGlobalShader
                {
                    static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, FShaderCompilerEnvironment& OutEnvironment)
                    {
                        OutEnvironment.SetDefine(TEXT("DEPTH_BUFFER_COLLISION"), CollisionMode == PCM_DepthBuffer);
                    }
                };

                class FRasterizeVS : public FGlobalShader
                {
                    class FPrimShaderDim : SHADER_PERMUTATION_BOOL("NANITE_PRIM_SHADER");
                    class FMeshShaderDim : SHADER_PERMUTATION_BOOL("NANITE_MESH_SHADER");
                    using FPermutationDomain = TShaderPermutationDomain<FPrimShaderDim, FMeshShaderDim>;

                    static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& Parameters)
                    {
                        FPermutationDomain PermutationVector(Parameters.PermutationId);
                        if (PermutationVector.Get<FPrimShaderDim>() && !FDataDrivenShaderPlatformInfo::GetSupportsPrimitiveShaders(Parameters.Platform))
                        {
                            return false;
                        }
                        return !PermutationVector.Get<FMeshShaderDim>() || FDataDrivenShaderPlatformInfo::GetSupportsMeshShadersTier1(Parameters.Platform);
                    }
                };

                IMPLEMENT_GLOBAL_SHADER(FShadowCS, "/Engine/Private/Shadow.usf", "MainCS", SF_Compute);
                IMPLEMENT_GLOBAL_SHADER(FRasterizeVS, "/Engine/Private/Rasterize.usf", "MainVS", SF_Vertex);
                IMPLEMENT_SHADER_TYPE(template<>, TSimulationPS<PCM_DepthBuffer>, TEXT("/Engine/Private/Simulation.usf"), TEXT("MainPS"), SF_Pixel);
                IMPLEMENT_SHADER_TYPE(template<>, TSimulationPS<PCM_None>, TEXT("/Engine/Private/Simulation.usf"), TEXT("MainPS"), SF_Pixel);
                """),
        ],
        output,
        ImmutableDictionary<string, string>.Empty.Add("bSupportsMeshShadersTier1", "true"));

        ImmutableDictionary<string, UnrealShaderClass> classes = UnrealShaderClasses.Read(output);

        UnrealShaderClass shadow = classes["FShadowCS"];
        Assert.Equal([0, 1, 2], shadow.Permutations["SHADOW_TYPE"]!.Value.ToArray());
        Assert.Equal([1, 2], shadow.Permutations["BATCH_SIZE"]!.Value.ToArray());
        Assert.Equal([0, 2], shadow.Varying["LIGHT_SOURCE_MODE"].ToArray());
        Assert.Equal([0, 1], shadow.Varying["CFLAG_AllowRealTypes"].ToArray());
        Assert.Equal("1.000000", shadow.Defines["TILE_VERTEX_SHADER"]);

        // ShouldCompilePermutation が除く組 (Inline の Receiver) と、RemapPermutation が書き換える組 (Directional の BATCH_SIZE 2)。
        Assert.Contains(new KeyValuePair<string, string>("DIM_INLINE==1", "SHADOW_TYPE==2"), shadow.Exclusions);
        Assert.Contains(new KeyValuePair<string, string>("BATCH_SIZE==2", "SHADOW_TYPE==0"), shadow.Exclusions);

        // 組から決まる定義どうし、組と定義の組み合わせ。
        Assert.Contains(new KeyValuePair<string, string>("LIGHT_SOURCE_MODE==0", "SHADOW_TYPE==2"), shadow.Exclusions);
        Assert.Contains(new KeyValuePair<string, string>("CFLAG_AllowRealTypes==0", "DIM_INLINE==1"), shadow.Exclusions);

        Assert.Equal("1", classes["TSimulationPS<PCM_DepthBuffer>"].Defines["DEPTH_BUFFER_COLLISION"]);
        Assert.Equal("0", classes["TSimulationPS<PCM_None>"].Defines["DEPTH_BUFFER_COLLISION"]);

        // シェーダープラットフォームを問う関数には、プラットフォームの設定で答える。節に無い設定は既定値 (false) である。
        Assert.Equal([0], classes["FRasterizeVS"].Permutations["NANITE_PRIM_SHADER"]!.Value.ToArray());
        Assert.Equal([0, 1], classes["FRasterizeVS"].Permutations["NANITE_MESH_SHADER"]!.Value.ToArray());

        // 解析では、同時には現れない値の組を、同時には有効にならない組にする。フラグはエンジンが定義するマクロにする。
        (_, VariantSpace variants) = UnrealOptions.MergeClasses([shadow]);
        Assert.True(variants.Constraints.AreExclusive("DIM_INLINE==1", "SHADOW_TYPE==2"));
        Assert.True(variants.Constraints.AreExclusive("DIM_INLINE==1", "PLATFORM_SUPPORTS_REAL_TYPES==0"));
        Assert.Equal([0, 2], variants.ValueSymbols["LIGHT_SOURCE_MODE"].ToArray());

        // 1 つを除いて「無い」とした値は、残りの値である。DIM_INLINE == 1 と SHADOW_TYPE が 0 でも 1 でもないことは、同時には成り立たない。
        SymbolCondition receiverInline = SymbolCondition.Symbol("DIM_INLINE==1")
            .And(SymbolCondition.Symbol("SHADOW_TYPE==0", isDefined: false))
            .And(SymbolCondition.Symbol("SHADOW_TYPE==1", isDefined: false));
        Assert.False(variants.Constraints.IsPossible(receiverInline));
    }

    /// <summary>
    /// 同じ単位を登録したクラスの定義を合わせる。どのクラスでも同じ値なら定義し、違えば構成の軸にする。
    /// </summary>
    [Fact]
    public void 同じ単位のクラスの定義を合わせる()
    {
        UnrealShaderClass first = new(
            "FFirst",
            ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [new KeyValuePair<string, string?>("SAME", "1"), new("DIFFERENT", "1"), new("UNKNOWN", null)]),
            ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [new KeyValuePair<string, ImmutableArray<int>?>("DIM", [0, 1])]),
            ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [new KeyValuePair<string, string?>("CFLAG_InlineRayTracing", "1")]));
        UnrealShaderClass second = new(
            "FSecond",
            ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [new KeyValuePair<string, string?>("SAME", "1"), new("DIFFERENT", "2")]),
            ImmutableSortedDictionary<string, ImmutableArray<int>?>.Empty.WithComparers(StringComparer.Ordinal),
            ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal));

        (ImmutableDictionary<string, string> macros, VariantSpace variants) = UnrealOptions.MergeClasses([first, second]);

        Assert.Equal("1", macros["SAME"]);
        Assert.Equal("0", macros["PLATFORM_SUPPORTS_REAL_TYPES"]);
        Assert.False(macros.ContainsKey("DIFFERENT"));
        Assert.Equal([1, 2], variants.ValueSymbols["DIFFERENT"].ToArray());
        Assert.Equal([0, 1], variants.ValueSymbols["DIM"].ToArray());
        Assert.Equal([0, 1], variants.ValueSymbols["PLATFORM_SUPPORTS_INLINE_RAY_TRACING"].ToArray());
        Assert.Contains("UNKNOWN", variants.Symbols);

        // 値の分からないマクロも、シェーダーが整数と比べていれば、その整数を値にする。
        SourceText shader = SourceText.From(
            """
            #if UNKNOWN == 0
            #elif UNKNOWN == 3 || UNKNOWN != 5
            #endif
            #if UNKNOWNER == 9
            #endif
            int x = UNKNOWN == 7;
            """,
            "Pass.usf");
        Assert.Equal([0, 3, 5], UnrealOptions.ComparedValues(shader, "UNKNOWN").ToArray());

        // 比べた値のどれでもないときの分岐 (#else) も読めるよう、0 も値にする。
        Assert.Equal([0, 9], UnrealOptions.ComparedValues(shader, "UNKNOWNER").ToArray());
        Assert.Empty(UnrealOptions.ComparedValues(shader, "NOT_COMPARED"));

        // 比べた値のほかを #error で止めていれば、0 は入れない。
        SourceText guarded = SourceText.From(
            """
            #if MAX_FIELDS != 4 && MAX_FIELDS != 1

            #error This must match MAX_FIELDS in C++ land
            #endif
            #if MAX_FIELDS > 1
            #endif
            """,
            "Particles.usf");
        Assert.Equal([1, 4], UnrealOptions.ComparedValues(guarded, "MAX_FIELDS").ToArray());

        // 比べる相手は、ファイルで整数に定義したマクロでもよく、左右を入れ替えて書いてもよい。
        SourceText named = SourceText.From(
            """
            #define SIZE_UINT1 (0)
            #define SIZE_UINT4 2 // comment
            #if SIZE_UINT1 == ELEMENT_SIZE
            #elif ELEMENT_SIZE == SIZE_UINT4
            #endif
            """,
            "ByteBuffer.usf");
        Assert.Equal([0, 2], UnrealOptions.ComparedValues(named, "ELEMENT_SIZE").ToArray());

        (_, VariantSpace compared) = UnrealOptions.MergeClasses([first, second], name => UnrealOptions.ComparedValues(shader, name));
        Assert.Equal([0, 3, 5], compared.ValueSymbols["UNKNOWN"].ToArray());

        // クラスが見つからなければ、値の決まったマクロも構成の軸にする。
        (ImmutableDictionary<string, string> withMissing, VariantSpace missingVariants) = UnrealOptions.MergeClasses([first, null]);
        Assert.False(withMissing.ContainsKey("SAME"));
        Assert.Contains("SAME", missingVariants.Symbols);
    }

    /// <summary>
    /// 設定の <c>units:</c> に書かなくても、C++ の登録からエントリポイントとステージの単位を作って解析する。
    /// 単位ごとに、ステージのマクロ (<c>PIXELSHADER=1</c> など、ほかは 0) を定義する。
    /// </summary>
    [Fact]
    public async Task CPlusPlusの登録から単位を作って解析する()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              generated: Generated
            """);

        // どちらの単位でもコンパイルされない分岐 (PIXELSHADER も COMPUTESHADER も 0) は読まない。
        // ステージのマクロを定義しなければ、値が分からないので両方の分岐を読み、宣言されていない名前を報告してしまう。
        Write("Engine/Shaders/Private/Pass.usf", """
            #if PIXELSHADER
            float4 MainPS() : SV_Target0 { return 1; }
            #endif

            #if COMPUTESHADER
            [numthreads(8, 8, 1)]
            void MainCS() {}
            #endif

            #if !PIXELSHADER && !COMPUTESHADER
            float Unused() { return undeclaredInOtherStages; }
            #endif
            """);
        Write("Engine/Shaders/Private/Missing.usf", "float Helper() { return 1; }\n");
        Write("Generated/" + UnrealShaderRegistrations.FileName, """
            # 仮想パス	エントリポイント	ステージ	クラス	マクロ	C++ のファイル
            /Engine/Private/Pass.usf	MainPS	Pixel	FPassPS	IMPLEMENT_GLOBAL_SHADER	Pass.cpp
            /Engine/Private/Pass.usf	MainCS	Compute	FPassCS	IMPLEMENT_GLOBAL_SHADER	Pass.cpp
            /Engine/Private/Missing.usf	MainPS	Pixel	FMissingPS	IMPLEMENT_GLOBAL_SHADER	Missing.cpp
            """);

        WriteGenerationStamp();
        (_, string output, string error) = await RunAsync(Path.Combine(_workDirectory, "Engine", "Shaders"));

        Assert.True(error.Length == 0, error);
        Assert.Contains("2 ファイルを解析", output, StringComparison.Ordinal);

        Assert.DoesNotContain("undeclaredInOtherStages", output, StringComparison.Ordinal);

        // 単位ごとに、そのステージのエントリポイントがある (MainPS は PIXELSHADER が 1 の単位、MainCS は COMPUTESHADER が 1 の単位)。
        Assert.DoesNotContain("Pass.usf(", output, StringComparison.Ordinal);

        // 登録したエントリポイントが無ければ、どの C++ の登録からかを添えて報告する。
        Assert.Contains("Missing.usf", output, StringComparison.Ordinal);
        Assert.Contains("HL0301", output, StringComparison.Ordinal);
        Assert.Contains("IMPLEMENT_GLOBAL_SHADER", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同じエントリポイントとステージでも、定義するものの違うクラスの登録は別の単位にする。
    /// まとめると、片方のクラスのマクロともう片方のパーミュテーションの、実在しない組み合わせを読んでしまう。
    /// </summary>
    [Fact]
    public async Task 定義するものの違うクラスは別の単位にする()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              generated: Generated
              permutations: true
            """);

        // FWide は MODE=2 だけを定義し、DIM_X を持たない。DIM_X を持つのは MODE=1 の FNarrow だけである。
        Write("Engine/Shaders/Private/Shared.usf", """
            #if MODE == 2 && DIM_X
            float Cross() { return onlyAcrossClasses; }
            #endif

            float4 MainPS() : SV_Target0 { return MODE; }
            """);

        UnrealUniformBufferGenerator.GenerateFromTexts(
        [
            ("Shared.cpp", """
                class FNarrowPS : public FGlobalShader
                {
                    class FX : SHADER_PERMUTATION_BOOL("DIM_X");
                public:
                    using FPermutationDomain = TShaderPermutationDomain<FX>;
                    static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, FShaderCompilerEnvironment& OutEnvironment)
                    {
                        OutEnvironment.SetDefine(TEXT("MODE"), 1);
                    }
                };

                class FWidePS : public FGlobalShader
                {
                    static void ModifyCompilationEnvironment(const FGlobalShaderPermutationParameters& Parameters, FShaderCompilerEnvironment& OutEnvironment)
                    {
                        OutEnvironment.SetDefine(TEXT("MODE"), 2);
                    }
                };

                IMPLEMENT_GLOBAL_SHADER(FNarrowPS, "/Engine/Private/Shared.usf", "MainPS", SF_Pixel);
                IMPLEMENT_GLOBAL_SHADER(FWidePS, "/Engine/Private/Shared.usf", "MainPS", SF_Pixel);
                """),
        ],
        Path.Combine(_workDirectory, "Generated"));

        WriteGenerationStamp();
        (_, string output, string error) = await RunAsync(Path.Combine(_workDirectory, "Engine", "Shaders"));

        Assert.True(error.Length == 0, error);
        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
        Assert.DoesNotContain("onlyAcrossClasses", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 登録したクラスがどれも PC ではコンパイルされないファイルは解析せず、そのことを伝える (UE0003)。
    /// プラットフォームを問う小さな関数 (RHI のヘッダの <c>RHISupportsRenderTargetWriteMask</c>) は、
    /// 本体の式を読んでプラットフォームの設定で答える。PC でもコンパイルされるクラスがあれば、そのクラスで解析する。
    /// </summary>
    [Fact]
    public async Task PCでコンパイルされないシェーダーは解析しない()
    {
        UnrealUniformBufferGenerator.GenerateFromTexts(
        [
            ("RHI.h", """
                inline bool RHISupportsRenderTargetWriteMask(const FStaticShaderPlatform Platform)
                {
                	// コメントがあってもよい
                	return FDataDrivenShaderPlatformInfo::GetSupportsRenderTargetWriteMask(Platform);
                }
                """),
            ("Pass.cpp", """
                class FDecodeCS : public FGlobalShader
                {
                    static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& Parameters)
                    {
                        return RHISupportsRenderTargetWriteMask(Parameters.Platform);
                    }
                };

                class FMobilePS : public FGlobalShader
                {
                    static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& Parameters)
                    {
                        return IsMobilePlatform(Parameters.Platform);
                    }
                };

                class FDesktopPS : public FGlobalShader
                {
                };

                IMPLEMENT_GLOBAL_SHADER(FDecodeCS, "/Engine/Private/Decode.usf", "DecodeCS", SF_Compute);
                IMPLEMENT_GLOBAL_SHADER(FMobilePS, "/Engine/Private/Feedback.usf", "MainPS", SF_Pixel);
                IMPLEMENT_GLOBAL_SHADER(FMobilePS, "/Engine/Private/Both.usf", "MobilePS", SF_Pixel);
                IMPLEMENT_GLOBAL_SHADER(FDesktopPS, "/Engine/Private/Both.usf", "DesktopPS", SF_Pixel);
                """),
        ],
        Path.Combine(_workDirectory, "Generated"),
        ImmutableDictionary<string, string>.Empty.Add("bIsPC", "true"));

        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              generated: Generated
            """);

        // エントリポイントはコンソールのヘッダにある。PC で読むと HL0301 になっていた。
        Write("Engine/Shaders/Private/Decode.usf", "float Helper() { return 1; }\n");

        // モバイルでない構成では宣言されない名前を使う。
        Write("Engine/Shaders/Private/Feedback.usf", """
            #if SHADING_PATH_MOBILE
            float4 Buffer;
            #endif
            float4 MainPS() : SV_Target0 { return Buffer; }
            """);

        // PC でもコンパイルされるクラスがあれば、そのクラスの登録だけで解析する (MobilePS は無くてよい)。
        Write("Engine/Shaders/Private/Both.usf", "float4 DesktopPS() : SV_Target0 { return undeclaredOnDesktop; }\n");

        WriteGenerationStamp();
        (_, string output, string error) = await RunAsync(Path.Combine(_workDirectory, "Engine", "Shaders"));

        Assert.True(error.Length == 0, error);
        Assert.Contains("Decode.usf(1:1): info UE0003", output, StringComparison.Ordinal);
        Assert.Contains("FDecodeCS", output, StringComparison.Ordinal);
        Assert.Contains("Feedback.usf(1:1): info UE0003", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HL0301", output, StringComparison.Ordinal);
        Assert.DoesNotContain("'Buffer'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Both.usf(1:1): info UE0003", output, StringComparison.Ordinal);
        Assert.Contains("'undeclaredOnDesktop'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// コンソール変数の値 (C++ の既定値と、ini の値) で、<c>GlobalBeginCompileShader</c> がどのシェーダーにも定義するマクロを求め、
    /// <c>ShouldCompilePermutation</c> にも答える。引数の無い 1 段の関数 (<c>Substrate::IsSubstrateEnabled()</c>) と、
    /// クラスの <c>bool</c> を返す関数 (<c>IsSupported</c>) も実行する。答えられない呼び出しがあっても、式の残りは読む。
    /// </summary>
    [Fact]
    public void コンソール変数の値でエンジンが定義するマクロとコンパイルされる組を求める()
    {
        (string Path, string Text)[] sources =
        [
            ("RenderUtils.cpp", """
                static TAutoConsoleVariable<int32> CVarSubstrate(
                	TEXT("r.Substrate"),
                	0,
                	TEXT("Enable Substrate materials (Beta)."),
                	ECVF_ReadOnly);
                static int32 GHairStrips = 1;
                static FAutoConsoleVariableRef CVarHairStrips(TEXT("r.HairStrands.Strips"), GHairStrips, TEXT(""));

                namespace Substrate
                {
                	bool IsSubstrateEnabled()
                	{
                		return CVarSubstrate.GetValueOnAnyThread() > 0;
                	}
                }
                """),
            ("ShaderCompiler.cpp", """
                void GlobalBeginCompileShader(const FString& DebugGroupName, FShaderTarget Target, FShaderCompilerInput& Input)
                {
                	const bool bSubstrate = Substrate::IsSubstrateEnabled() && IsSubstrateSupportForShaderPipeline(Input);
                	{
                		SET_SHADER_DEFINE(Input.Environment, SUBSTRATE_ENABLED, bSubstrate ? 1 : 0);
                	}
                	{
                		static const auto CVar = IConsoleManager::Get().FindTConsoleVariableDataInt(TEXT("r.HairStrands.Strips"));
                		SET_SHADER_DEFINE(Input.Environment, USE_HAIR_STRIPS, CVar ? (CVar->GetValueOnAnyThread() != 0) : 0);
                	}
                	SET_SHADER_DEFINE(Input.Environment, PIXELSHADER, Target.Frequency == SF_Pixel);
                }
                """),
            ("Pass.cpp", """
                static bool IsVisualizeSupported(EShaderPlatform InPlatform)
                {
                	return Substrate::IsSubstrateEnabled() && GetMaxSupportedFeatureLevel(InPlatform) >= ERHIFeatureLevel::SM5;
                }

                class FVisualizeCS : public FGlobalShader
                {
                	static bool IsSupported(EShaderPlatform InPlatform)
                	{
                		return IsVisualizeSupported(InPlatform);
                	}

                	static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& Parameters)
                	{
                		return IsSupported(Parameters.Platform);
                	}
                };

                IMPLEMENT_GLOBAL_SHADER(FVisualizeCS, "/Engine/Private/Visualize.usf", "MainCS", SF_Compute);
                """),
        ];

        string output = Path.Combine(_workDirectory, "Generated");
        UnrealUniformBufferGenerator.GenerateFromTexts(sources, output);

        ImmutableDictionary<string, string> defines = UnrealShaderClasses.ReadGlobalDefines(output);
        Assert.Equal("0", defines["SUBSTRATE_ENABLED"]);
        Assert.Equal("1", defines["USE_HAIR_STRIPS"]);

        // ステージで変わるものは、値が決まらないので書かない。
        Assert.False(defines.ContainsKey("PIXELSHADER"));
        Assert.True(UnrealShaderClasses.Read(output)["FVisualizeCS"].NeverCompiled);

        // ini の値が C++ の既定値より優先する。Substrate を有効にすると、SUBSTRATE_ENABLED は決まらない
        // (IsSubstrateSupportForShaderPipeline に答えられない)。クラスはコンパイルされうる。
        string enabled = Path.Combine(_workDirectory, "Enabled");
        UnrealUniformBufferGenerator.GenerateFromTexts(
            sources,
            enabled,
            consoleVariableOverrides: ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase).Add("r.Substrate", "True"));

        Assert.False(UnrealShaderClasses.ReadGlobalDefines(enabled).ContainsKey("SUBSTRATE_ENABLED"));
        Assert.False(UnrealShaderClasses.Read(enabled)["FVisualizeCS"].NeverCompiled);
    }

    /// <summary>
    /// ini のコンソール変数を、エンジンの ini、プロジェクトの ini の順に読み、後のものを優先する。
    /// コンソール変数の節だけを読み、名前に点の無い設定の項目と配列の書き方は読まない。
    /// </summary>
    [Fact]
    public void iniのコンソール変数を読む()
    {
        Write("Engine/Config/BaseEngine.ini", """
            [/Script/Engine.RendererSettings]
            r.Substrate=False
            r.Lumen.Enabled=1
            MaxSkinBones=(Default=65536)
            +r.Array=1

            [/Script/Engine.OtherSettings]
            r.Other=1
            """);
        Write("Project/Config/DefaultEngine.ini", """
            [/Script/Engine.RendererSettings]
            r.Substrate=True
            [SystemSettings]
            r.HairStrands.Strips="0"
            """);

        ImmutableDictionary<string, string> values = UnrealConsoleVariables.ReadOverrides(
            Path.Combine(_workDirectory, "Engine"), Path.Combine(_workDirectory, "Project"));

        Assert.Equal("True", values["r.substrate"]);
        Assert.Equal("1", values["r.Lumen.Enabled"]);
        Assert.Equal("0", values["r.HairStrands.Strips"]);
        Assert.False(values.ContainsKey("MaxSkinBones"));
        Assert.False(values.ContainsKey("r.Array"));
        Assert.False(values.ContainsKey("r.Other"));
    }

    /// <summary>
    /// 生成した、エンジンがどのシェーダーにも定義するマクロ (SUBSTRATE_ENABLED など) を、登録から作る単位に定義する。
    /// 定義しないと、Substrate.ush の <c>#ifndef SUBSTRATE_ENABLED</c> の見張りで 1 にされ、<c>#error</c> に到達したまま読んでいた。
    /// </summary>
    [Fact]
    public async Task エンジンがどのシェーダーにも定義するマクロを単位に定義する()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              generated: Generated
            """);
        Write("Engine/Shaders/Private/Pass.usf", """
            #ifndef SUBSTRATE_ENABLED
            	#define SUBSTRATE_ENABLED 1
            	#error SUBSTRATE_ENABLED needs to be defined
            #endif
            #if SUBSTRATE_ENABLED
            float4 Value() { return undeclaredWithSubstrate; }
            #endif
            [numthreads(8, 8, 1)]
            void MainCS() {}
            """);
        Write("Generated/" + UnrealShaderRegistrations.FileName, """
            # 仮想パス	エントリポイント	ステージ	クラス	マクロ	C++ のファイル
            /Engine/Private/Pass.usf	MainCS	Compute	FPassCS	IMPLEMENT_GLOBAL_SHADER	Pass.cpp
            """);
        Write("Generated/" + UnrealShaderClasses.FileName, "*\tglobal\tSUBSTRATE_ENABLED\t0\nFPassCS\tclass\t\t\n");

        WriteGenerationStamp();
        (_, string output, string error) = await RunAsync(Path.Combine(_workDirectory, "Engine", "Shaders"));

        Assert.True(error.Length == 0, error);
        Assert.DoesNotContain("undeclaredWithSubstrate", output, StringComparison.Ordinal);
    }

    [Fact]
    public void 生成した後にiniとCppが変わったことを伝える()
    {
        Write("Engine/Config/BaseEngine.ini", "[/Script/Engine.RendererSettings]\nr.Substrate=False\n");
        Write("Engine/Source/Runtime/A.h", "// a");
        Write("Engine/Source/Runtime/B.cpp", "// b");
        Write("Engine/Source/Runtime/Readme.txt", "C++ ではないので数えない");
        string engine = Path.Combine(_workDirectory, "Engine");
        string generated = Path.Combine(_workDirectory, "Generated");
        Directory.CreateDirectory(generated);

        UnrealGenerationStamp.Write(generated, engine, projectDirectory: null, [Path.Combine(engine, "Source")]);

        UnrealGenerationStaleness fresh = UnrealGenerationStamp.Check(generated)!;
        Assert.False(fresh.IsStale);
        Assert.Null(fresh.Describe());

        Write("Engine/Config/BaseEngine.ini", "[/Script/Engine.RendererSettings]\nr.Substrate=True\n");
        Write("Engine/Source/Runtime/C.h", "// c");
        File.Delete(Path.Combine(engine, "Source", "Runtime", "B.cpp"));
        File.SetLastWriteTimeUtc(Path.Combine(engine, "Source", "Runtime", "A.h"), DateTime.UtcNow.AddMinutes(1));
        Write("Engine/Source/Runtime/Readme.txt", "変えても数えない");

        UnrealGenerationStaleness stale = UnrealGenerationStamp.Check(generated)!;
        Assert.True(stale.IsStale);
        Assert.Equal("BaseEngine.ini", Path.GetFileName(Assert.Single(stale.ChangedIniFiles)));
        Assert.Equal(1, stale.ChangedSourceFiles);
        Assert.Equal(1, stale.AddedSourceFiles);
        Assert.Equal(1, stale.RemovedSourceFiles);
        Assert.Equal(
            string.Join(Environment.NewLine, "前回の --unreal-generate から変更が検出されました。", "ini が変更されました (BaseEngine.ini)。", "C++ ファイルが変更されました (変更 1 件、追加 1 件、削除 1 件)。", "変更を解析に反映する場合は --unreal-generate をやり直してください。"),
            stale.Describe());
    }

    [Fact]
    public void 記録の無い生成物は記録が無いことを伝える()
    {
        string generated = Path.Combine(_workDirectory, "Generated");
        Directory.CreateDirectory(generated);

        UnrealGenerationStaleness result = UnrealGenerationStamp.Check(generated)!;

        Assert.False(result.HasRecord);
        Assert.Contains("記録がありません", result.Describe(), StringComparison.Ordinal);
        Assert.Null(UnrealGenerationStamp.Check(Path.Combine(_workDirectory, "Missing")));
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_workDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>手で作った生成物のフォルダーに、--unreal-generate が書く記録を足す (無いと、記録が無いことを標準エラーに出す)。</summary>
    private void WriteGenerationStamp()
        => UnrealGenerationStamp.Write(
            Path.Combine(_workDirectory, "Generated"),
            Path.Combine(_workDirectory, "Engine"),
            projectDirectory: null,
            [Path.Combine(_workDirectory, "Engine", "Source")]);

    private static async Task<(ExitCode Code, string Output, string Error)> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(args, BuiltInAnalyzers.All, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
