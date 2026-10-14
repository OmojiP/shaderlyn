using Shaderlyn.Cli;
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

        // 生成される取り込みは誤りとして報告しない。中身を知らないことは SL0002 で伝える。
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
