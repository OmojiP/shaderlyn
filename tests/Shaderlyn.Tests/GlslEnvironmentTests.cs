using Shaderlyn.Cli;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Tests;

/// <summary>
/// GLSL の環境 (<see cref="GlslEnvironment"/>) の検証。意味解析のモデルを作り、ステージ・バージョン・定義済みマクロを決める。
/// </summary>
/// <remarks>
/// 定義済みマクロの値は、glslang 16.6.0 で <c>#ifndef</c> と <c>#error</c> を使って確かめたものである。
/// </remarks>
public sealed class GlslEnvironmentTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-glsl-env-{Guid.NewGuid():N}");

    public GlslEnvironmentTests() => Directory.CreateDirectory(_workDirectory);

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

    /// <summary>GLSL のコードからモデルを作る。</summary>
    /// <param name="fileName">ファイルの名前。ステージは拡張子で決まる。</param>
    /// <param name="code">コード。</param>
    /// <param name="semantics">解析の設定。</param>
    /// <returns>モデル。</returns>
    private static ShaderCompilation Compile(string fileName, string code, SemanticsOptions? semantics = null)
    {
        SourceText text = SourceText.From(code, Path.Combine(Path.GetTempPath(), fileName));
        return ShaderCompilation.Create(text, [GlslEnvironment.WholeFile(text)], text, new GlslEnvironment(), ShaderSourceInfo.None, semantics);
    }

    /// <summary>条件が成り立つときだけ通る分岐に構文の誤りを置いたコード。誤りが出れば条件が成り立っている。</summary>
    /// <param name="version">先頭の <c>#version</c> の行。無ければ空。</param>
    /// <param name="condition"><c>#if</c> の条件。</param>
    /// <returns>コード。</returns>
    private static string BrokenWhen(string version, string condition) => $$"""
        {{version}}
        #if {{condition}}
        void broken() { int x = ; }
        #endif
        void main() { }
        """;

    private static bool IsReached(ShaderCompilation compilation)
        => compilation.ReportableHlslDiagnostics.Any(d => d.Id == "CF0001");

    [Theory]
    [InlineData("#version 450", "a.frag", "__VERSION__ == 450 && GL_core_profile == 1 && GL_FRAGMENT_PRECISION_HIGH == 1 && !defined(GL_ES) && !defined(GL_compatibility_profile)")]
    [InlineData("#version 450 core", "a.vert", "__VERSION__ == 450 && defined(GL_core_profile) && !defined(GL_compatibility_profile)")]
    [InlineData("#version 450 compatibility", "a.frag", "defined(GL_core_profile) && GL_compatibility_profile == 1")]
    [InlineData("#version 140", "a.frag", "__VERSION__ == 140 && !defined(GL_core_profile) && defined(GL_FRAGMENT_PRECISION_HIGH)")]
    [InlineData("#version 300 es", "a.frag", "__VERSION__ == 300 && GL_ES == 1 && !defined(GL_core_profile) && !defined(GL_es_profile)")]
    [InlineData("#version 310 es", "a.comp", "__VERSION__ == 310 && GL_ES == 1")]
    [InlineData("#version 100", "a.frag", "__VERSION__ == 100 && GL_ES == 1")]
    [InlineData("", "a.vert", "__VERSION__ == 100 && GL_ES == 1")]
    public void 定義済みのマクロをglslangと同じく定義する(string version, string fileName, string condition)
    {
        Assert.True(IsReached(Compile(fileName, BrokenWhen(version, condition))));
        Assert.False(IsReached(Compile(fileName, BrokenWhen(version, $"!({condition})"))));
    }

    [Fact]
    public void VULKANはコンパイルの指定で決まるので定義しない()
        => Assert.False(IsReached(Compile("a.frag", BrokenWhen("#version 450", "defined(VULKAN) || defined(GL_SPIRV)"))));

    [Fact]
    public void バージョンの無い取り込まれる断片はバージョンを決めない()
        => Assert.False(IsReached(Compile("common.glsl", BrokenWhen(string.Empty, "defined(__VERSION__) || defined(GL_ES)"))));

    [Fact]
    public void 設定で与えたマクロがコンパイラのマクロに勝つ()
    {
        SemanticsOptions semantics = SemanticsOptions.Default with
        {
            PredefinedMacros = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add("GL_FRAGMENT_PRECISION_HIGH", "0")
                .Add("VULKAN", "100"),
        };

        Assert.True(IsReached(Compile("a.frag", BrokenWhen("#version 450", "GL_FRAGMENT_PRECISION_HIGH == 0 && VULKAN == 100"), semantics)));
    }

    [Theory]
    [InlineData("a.vert", "Vertex")]
    [InlineData("a.FRAG", "Fragment")]
    [InlineData("a.comp", "Compute")]
    [InlineData("a.tesc", "TessellationControl")]
    [InlineData("a.rchit", "ClosestHit")]
    [InlineData("shadow.vert.glsl", "Vertex")]
    [InlineData("common.glsl", null)]
    [InlineData("a.txt", null)]
    public void ステージをファイルの名前で決める(string fileName, string? stage)
        => Assert.Equal(stage, GlslLanguageOptions.StageFromFileName(fileName)?.ToString());

    [Fact]
    public void 木に言語のバージョンとステージを残す()
    {
        ShaderCompilation compilation = Compile("a.frag", "#version 310 es\nvoid main() { }\n");

        AnalyzedProgram program = Assert.Single(compilation.Programs);
        Assert.Equal(new GlslLanguageOptions(310, GlslProfile.Es, GlslShaderStage.Fragment), program.Tree.Language);
        Assert.Empty(compilation.ReportableHlslDiagnostics);
    }

    [Fact]
    public void ステージのファイルのエントリポイントはmain()
    {
        AnalyzedProgram stage = Assert.Single(Compile("a.frag", "#version 450\nvoid main() { }\n").Programs);
        AnalyzedProgram fragment = Assert.Single(Compile("common.glsl", "float f() { return 1.0; }\n").Programs);

        Assert.Equal("main", Assert.Single(stage.EntryPoints).Name);
        Assert.Empty(fragment.EntryPoints);
    }

    /// <summary>
    /// 型と組み込みの表が無いうちは、ルールを働かせない。働かせると、<c>vec4</c> や <c>gl_FragColor</c> を知らないために誤検出が出る。
    /// エントリポイントが無いこと (CF0301) も、GLSL で確かめるまでは伝えない。
    /// </summary>
    [Fact]
    public async Task GLSLではまだルールが働かない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), """
            #version 450
            layout(location = 0) out vec4 outColor;
            void notMain() { outColor = vec4(undefinedThing, gl_FragCoord.x); }
            """);

        (ExitCode code, string output) = await RunAsync("--no-config");

        Assert.Equal(ExitCode.Success, code);
        Assert.DoesNotContain("CF0", output, StringComparison.Ordinal);
    }

    /// <summary>設定ファイルの <c>symbols:</c> を構成の軸にし、両方の分岐を読む。</summary>
    [Fact]
    public async Task 設定のシンボルで両方の分岐を読む()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: glsl
            symbols:
              - name: USE_FOG
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), """
            #version 450
            #ifdef USE_FOG
            void fog() { float x = ; }
            #endif
            void main() { }
            """);

        (ExitCode code, string output) = await RunAsync();

        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("a.frag(3:", output, StringComparison.Ordinal);
        Assert.Contains("CF0001", output, StringComparison.Ordinal);
    }

    private async Task<(ExitCode Code, string Output)> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync([_workDirectory, .. args], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return (code, output.ToString());
    }
}
