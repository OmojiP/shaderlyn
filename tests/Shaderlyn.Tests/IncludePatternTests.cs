using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Cli;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Tests;

/// <summary>
/// 正しい <c>#include</c> を、どう CLI に渡しても解決できることの検証。
/// </summary>
/// <remarks>
/// <para>
/// Unity では、取り込み元からの相対 (<c>../Lib/Lib.hlsl</c>、<c>..\Lib\Lib.hlsl</c>、<c>./Common.hlsl</c>)、
/// プロジェクトからのパス (<c>Assets/...</c> / <c>Packages/...</c>)、大文字と小文字の違い、空白を含むパスがどれも通る。
/// <c>--unity-project</c> を付ければ、解析対象をフォルダーで渡しても 1 ファイルで渡しても、同じく解決できなければならない。
/// </para>
/// <para>
/// <b>Unity プロジェクトは推定しない。</b> 付けなければ <c>Assets/...</c> などは引けないので、
/// 引けない理由と、解析対象の上で見つけたプロジェクトを CF0021 の文で伝える。
/// </para>
/// </remarks>
[Collection(CurrentDirectoryCollection.Name)]
public sealed class IncludePatternTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shaderlyn-include-" + Guid.NewGuid().ToString("N"));

    /// <summary>Unity プロジェクトの形のフォルダーと、その外のフォルダーを作る。</summary>
    public IncludePatternTests()
    {
        Write("project/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.3.0f1\n");
        Write("project/Packages/manifest.json", "{ \"dependencies\": { } }\n");
        Write("project/Packages/com.example.local/package.json", "{ \"name\": \"com.example.local\", \"version\": \"1.0.0\" }\n");
        Write("project/Packages/com.example.local/ShaderLibrary/Pkg.hlsl", "float PkgFunc(float x) { return x; }\n");
        Write("project/Assets/Shaders/Common.hlsl", "#ifndef COMMON_INCLUDED\n#define COMMON_INCLUDED\nfloat CommonFunc(float x) { return x; }\n#endif\n");
        Write("project/Assets/Shaders/Sub/Inner.hlsl", "#include \"../Common.hlsl\"\n#include \"Deep/D.hlsl\"\nfloat InnerFunc(float x) { return CommonFunc(x) + DeepFunc(x); }\n");
        Write("project/Assets/Shaders/Sub/Deep/D.hlsl", "#include \"../../../Lib/Lib.hlsl\"\nfloat DeepFunc(float x) { return LibFunc(x); }\n");
        Write("project/Assets/Lib/Lib.hlsl", "#ifndef LIB_INCLUDED\n#define LIB_INCLUDED\nfloat LibFunc(float x) { return x; }\n#endif\n");
        Write("project/Assets/My Folder/Space.hlsl", "float SpaceFunc(float x) { return x; }\n");
        Write("project/Assets/Shaders/Main.shader", """
            Shader "Test/Main"
            {
                HLSLINCLUDE
                #include "Common.hlsl"
                ENDHLSL
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #include "./Common.hlsl"
                        #include "Sub/Inner.hlsl"
                        #include "../Lib/Lib.hlsl"
                        #include "Assets/Lib/Lib.hlsl"
                        #include "..\Lib\Lib.hlsl"
                        #include "common.hlsl"
                        #include "../My Folder/Space.hlsl"
                        #include "Packages/com.example.local/ShaderLibrary/Pkg.hlsl"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target { return CommonFunc(1) + InnerFunc(1) + LibFunc(1) + SpaceFunc(1) + PkgFunc(1); }
                        ENDHLSL
                    }
                }
            }
            """);
        Write("project/Assets/Compute/K.compute", """
            #pragma kernel CSMain
            #include "../Shaders/Common.hlsl"
            #include "../Lib/Lib.hlsl"
            RWStructuredBuffer<float> Out;
            [numthreads(8,1,1)]
            void CSMain(uint3 id : SV_DispatchThreadID) { Out[id.x] = CommonFunc(1) + LibFunc(1); }
            """);

        // Unity プロジェクトではないフォルダー。設定ファイルが根を決める。
        Write("secret/s.hlsl", "float Secret() { return 1; }\n");
        Write("plain/common/c.hlsli", "float CommonFunc(float x) { return x; }\n");
        Write("plain/shaders/fx/b.hlsl", "#include \"../../common/c.hlsli\"\nfloat4 main() : SV_Target { return CommonFunc(1); }\n");
        Write("plain/shaders/fx/A.shader", """
            Shader "Plain/A"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #include "../../../secret/s.hlsl"
                        #include "Missing.hlsl"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target { return 1; }
                        ENDHLSL
                    }
                }
            }
            """);
        Write("plain/.shaderlyn.yaml", "version: 1\nenvironment: hlsl\nunits:\n  - files: shaders/**/*.hlsl\n    entry: main\n    target: ps_6_0\n");
    }

    /// <summary>試験用のフォルダーを片付ける。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 消せなくても検証の結果は変わらない。
        }
    }

    /// <summary>
    /// <c>--unity-project</c> を付ければ、どう渡しても正しい include を報告しないことを検証する。
    /// </summary>
    /// <param name="target">プロジェクトからの解析対象。</param>
    [Theory]
    [InlineData("Assets")]
    [InlineData("Assets/Shaders/Main.shader")]
    [InlineData("Assets/Shaders/Sub")]
    [InlineData("Assets/Shaders/Sub/Inner.hlsl")]
    [InlineData("Assets/Compute/K.compute")]
    public async Task 正しいincludeを報告しない(string target)
    {
        string project = Path.Combine(_root, "project");
        string output = await RunAsync(Path.Combine(project, target), "--no-config", "--unity-project", project);

        Assert.DoesNotContain("CF0021", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOL0004", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CF0310", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--unity-project</c> を付けなければプロジェクトを推定せず、引けない理由と見つけたプロジェクトを添えることを検証する。
    /// </summary>
    [Fact]
    public async Task プロジェクトを付けなければ推定せずに理由を添える()
    {
        string project = Path.Combine(_root, "project");
        string output = await RunAsync(Path.Combine(project, "Assets", "Shaders", "Main.shader"), "--no-config", "--format", "json");

        // Assets/... と、プロジェクトの中で解析対象のフォルダーより上を指す ../... は、プロジェクトを指定すれば引けると伝える。
        string hint = $"Unity プロジェクトとして解析する場合は --unity-project \"{project}\" を指定してください。";
        string[] messages = Messages(output);
        Assert.Contains("include 'Assets/Lib/Lib.hlsl' を解決できませんでした。" + hint, messages);
        Assert.Contains("include '../Lib/Lib.hlsl' を解決できませんでした。" + hint, messages);

        // 同じフォルダーと、その下は引ける。
        Assert.DoesNotContain("include './Common.hlsl'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("include 'Sub/Inner.hlsl'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// フォルダーを付けずにファイルを渡しても (作業フォルダーにあるファイル)、同じフォルダーのヘッダを引けることを検証する。
    /// </summary>
    [Fact]
    public async Task フォルダーを付けずに渡したファイルからも引ける()
    {
        string previous = Environment.CurrentDirectory;

        try
        {
            string project = Path.Combine(_root, "project");
            Environment.CurrentDirectory = Path.Combine(project, "Assets", "Shaders");
            string output = await RunAsync("Main.shader", "--no-config", "--unity-project", project);

            Assert.DoesNotContain("CF0021", output, StringComparison.Ordinal);
            Assert.DoesNotContain("TOOL0004", output, StringComparison.Ordinal);

            // プロジェクトを付けなくても、同じフォルダーのヘッダは引ける。
            string withoutProject = await RunAsync("Main.shader", "--no-config");
            Assert.DoesNotContain("include 'Common.hlsl'", withoutProject, StringComparison.Ordinal);
            Assert.DoesNotContain("include './Common.hlsl'", withoutProject, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    /// <summary>範囲の外は読まず、読まなかったことと直し方を伝えることを検証する。無いファイルはそのまま報告する。</summary>
    [Fact]
    public async Task 範囲の外は読まずに理由を添える()
    {
        string output = await RunAsync(Path.Combine(_root, "plain", "shaders", "fx", "A.shader"), "--no-config", "--format", "json");
        string[] messages = Messages(output);

        Assert.Contains(
            "include '../../../secret/s.hlsl' を解決できませんでした。読んでよい範囲の外にあるため読みません。読む場合は --include-path (設定ファイルは include-paths) に加えてください。",
            messages);
        Assert.Contains("include 'Missing.hlsl' を解決できませんでした。", messages);
        Assert.DoesNotContain("TOOL0004", output, StringComparison.Ordinal);
    }

    /// <summary>設定ファイルのフォルダーの中なら、解析対象から上がる include も読むことを検証する。</summary>
    [Fact]
    public async Task 設定ファイルのフォルダーの中なら上がるincludeも読む()
    {
        string plain = Path.Combine(_root, "plain");
        string output = await RunAsync(Path.Combine(plain, "shaders", "fx", "b.hlsl"), "--config", Path.Combine(plain, ".shaderlyn.yaml"));

        Assert.DoesNotContain("CF0021", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOL0004", output, StringComparison.Ordinal);
    }

    /// <summary>プロジェクトを付けていないとき、見つけたプロジェクトと、引けないものを --env に出すことを検証する。</summary>
    [Fact]
    public async Task 見つけたプロジェクトをenvに出す()
    {
        string output = await RunAsync(Path.Combine(_root, "project", "Assets"), "--no-config", "--env");

        Assert.Contains("解析対象は Unity プロジェクトの中にあります", output, StringComparison.Ordinal);
        Assert.Contains("Assets/... と Packages/... の #include", output, StringComparison.Ordinal);
    }

    [Fact]
    public void 範囲の外にあるファイルを答える()
    {
        string assets = Path.Combine(_root, "project", "Assets");
        FileSystemIncludeResolver resolver = new([assets], new IncludeBoundary([assets]));
        string including = Path.Combine(assets, "Shaders", "A.shader");

        Assert.False(resolver.TryResolve("../../../secret/s.hlsl", including, out _));
        Assert.Equal(Path.Combine(_root, "secret", "s.hlsl"), resolver.FindOutsideBoundary("../../../secret/s.hlsl", including));
        Assert.Null(resolver.FindOutsideBoundary("Missing.hlsl", including));
        Assert.Null(new FileSystemIncludeResolver([assets]).FindOutsideBoundary("../../../secret/s.hlsl", including));
    }

    /// <summary>JSON の出力から、指摘の文を取り出す。</summary>
    private static string[] Messages(string json)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("message").GetString() ?? string.Empty)];
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task<string> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync(args, BuiltInAnalyzers.All, output, error);
        return output + error.ToString();
    }
}

/// <summary>作業フォルダーを変えるテストの集まり。ほかのテストと同時には動かさない。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    /// <summary>名前。</summary>
    public const string Name = "作業フォルダーを変えるテスト";
}
