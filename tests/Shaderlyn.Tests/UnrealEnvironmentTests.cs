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
    /// </summary>
    [Fact]
    public void CPlusPlusのシェーダーの登録を読む()
    {
        string output = Path.Combine(_workDirectory, "Generated");
        UnrealUniformBufferGeneration generation = UnrealUniformBufferGenerator.GenerateFromTexts(
        [
            ("Pass.cpp", """
                // IMPLEMENT_GLOBAL_SHADER(FCommented, "/Engine/Private/X.usf", "MainPS", SF_Pixel);
                #define IMPLEMENT_MY_SHADER(Name) IMPLEMENT_GLOBAL_SHADER(Name, "/Engine/Private/Y.usf", "Main", SF_Pixel)
                IMPLEMENT_GLOBAL_SHADER(FPassPS, "/Engine/Private/Pass.usf", "MainPS", SF_Pixel);
                IMPLEMENT_SHADER_TYPE(template<>, TPassCS<1>, TEXT( "/Engine/Private/Pass.usf" ), TEXT("MainCS"), SF_Compute);
                IMPLEMENT_MATERIAL_SHADER_TYPE(, FMaterialVS, TEXT("/Engine/Private/Material.usf"), TEXT("MainVS"), SF_Vertex);
                IMPLEMENT_GLOBAL_SHADER(FHitGroup, "/Engine/Private/RayTracing.usf", "closesthit=MainCHS anyhit=MainAHS", SF_RayHitGroup);
                IMPLEMENT_MY_SHADER(FFirst);
                IMPLEMENT_MY_SHADER(FSecond);
                IMPLEMENT_SHADERPIPELINE_TYPE_VSPS(FPipeline, FMaterialVS, FPassPS, true);
                """),
        ],
        output);

        Assert.Equal(4, generation.ShaderRegistrationCount);
        Assert.Equal(2, Assert.Single(generation.UnreadShaderRegistrations, p => p.Key == "IMPLEMENT_MY_SHADER").Value);
        Assert.Single(generation.UnreadShaderRegistrations);

        ImmutableArray<UnrealShaderRegistration> read = UnrealShaderRegistrations.Read(output);
        Assert.Contains(read, r => r is { VirtualPath: "/Engine/Private/Pass.usf", EntryPoint: "MainCS", Frequency: "Compute", Macro: "IMPLEMENT_SHADER_TYPE" });
        Assert.Contains(read, r => r is { VirtualPath: "/Engine/Private/Material.usf", EntryPoint: "MainVS", Macro: "IMPLEMENT_MATERIAL_SHADER_TYPE" });
        Assert.Equal(["MainCHS", "MainAHS"], UnrealShaderRegistrations.EntryFunctions(Assert.Single(read, r => r.Frequency == "RayHitGroup").EntryPoint).ToArray());
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
                    }
                };

                IMPLEMENT_GLOBAL_SHADER(FPlainPS, "/Engine/Private/Plain.usf", "MainPS", SF_Pixel);
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
        Assert.Empty(plain.Permutations);
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

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_workDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task<(ExitCode Code, string Output, string Error)> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(args, BuiltInAnalyzers.All, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
