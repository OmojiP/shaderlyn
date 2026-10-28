using System.Diagnostics;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.Semantics.Symbols;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// GLSL の組み込みの関数と変数の表 (<see cref="GlslBuiltins"/>) の検証。
/// </summary>
/// <remarks>
/// <para>
/// 表は glslang の <c>--dump-builtin-symbols</c> から作る (<c>tools/glsl/builtins.mjs</c>)。
/// 環境変数 <c>SHADERLYN_GLSLANG</c> に glslang の実行ファイルを渡すと、表を作り直して、リポジトリの表と同じかを確かめる。
/// </para>
/// <para>
/// 環境変数 <c>SHADERLYN_GLSL_CORPUS</c> にコーパスの置き場所を渡すと、コーパスの関数の呼び出しと <c>gl_</c> の名前がすべて解決することを確かめる。
/// </para>
/// </remarks>
public sealed class GlslBuiltinsTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// 関数の中の宣言の後に置いた <c>r</c> の初期化の式の型を求める。
    /// </summary>
    private static string? TypeOf(string fileName, string declarations, string expression, string version = "#version 450")
    {
        string code = $$"""
            {{version}}
            void main()
            {
                {{declarations}}
                float r = {{expression}};
            }
            """;
        (ShaderCompilation compilation, AnalyzedProgram program) = Compile(fileName, code);

        ExpressionSyntax initializer = program.Tree.Root.DescendantNodesAndSelf()
            .OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Name == "r")
            .Initializer!;

        return compilation.GetExpressionTypeBinder(program).GetEvaluatorFor(initializer).Evaluate(initializer);
    }

    private static (ShaderCompilation Compilation, AnalyzedProgram Program) Compile(string fileName, string code, SemanticsOptions? options = null)
    {
        SourceText text = SourceText.From(code, Path.Combine(Path.GetTempPath(), fileName));
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [GlslEnvironment.WholeFile(text)], text, new GlslEnvironment(), ShaderSourceInfo.None, options);
        return (compilation, Assert.Single(compilation.Programs));
    }

    [Theory]
    [InlineData("uniform sampler2D s; vec2 uv;", "texture(s, uv)", "vec4")]
    [InlineData("uniform usampler2D s; vec2 uv;", "texture(s, uv)", "uvec4")]
    [InlineData("uniform sampler2DShadow s; vec3 uv;", "texture(s, uv)", "float")]
    [InlineData("uniform sampler2D s; ivec2 p;", "texelFetch(s, p, 0)", "vec4")]
    [InlineData("uniform sampler2D s;", "textureSize(s, 0)", "ivec2")]
    [InlineData("vec3 a; vec3 b;", "dot(a, b)", "float")]
    [InlineData("vec3 a;", "normalize(a)", "vec3")]
    [InlineData("vec3 a;", "length(a)", "float")]
    [InlineData("vec3 a;", "max(a, 0.0)", "vec3")]
    [InlineData("vec3 a; vec3 b;", "mix(a, b, 0.5)", "vec3")]
    [InlineData("ivec2 a;", "abs(a)", "ivec2")]
    [InlineData("mat4x3 m;", "transpose(m)", "mat3x4")]
    [InlineData("vec2 c; vec3 d;", "outerProduct(c, d)", "mat3x2")]
    [InlineData("vec3 a; vec3 b;", "lessThan(a, b)", "bvec3")]
    [InlineData("uint u;", "bitCount(u)", "int")]
    [InlineData("float f;", "floatBitsToUint(f)", "uint")]
    public void 組み込み関数の戻り値の型(string declarations, string expression, string expected)
        => Assert.Equal(expected, TypeOf("a.frag", declarations, expression));

    [Theory]
    [InlineData("a.frag", "gl_FragCoord.xy", "vec2")]
    [InlineData("a.frag", "gl_FrontFacing", "bool")]
    [InlineData("a.vert", "gl_VertexIndex", "int")]
    [InlineData("a.vert", "gl_VertexID", "int")]
    [InlineData("a.comp", "gl_GlobalInvocationID.x", "uint")]
    [InlineData("a.comp", "gl_WorkGroupSize", "uvec3")]
    [InlineData("a.vert", "gl_Position", "vec4")]
    [InlineData("a.tesc", "gl_InvocationID", "int")]
    public void 組み込み変数の型(string fileName, string expression, string expected)
        => Assert.Equal(expected, TypeOf(fileName, string.Empty, expression));

    [Fact]
    public void 組み込み変数はそのステージでだけ使える()
    {
        GlslTypeSystem fragment = GlslTypeSystem.For(450, GlslProfile.Core, GlslShaderStage.Fragment);
        GlslTypeSystem vertex = GlslTypeSystem.For(450, GlslProfile.Core, GlslShaderStage.Vertex);
        GlslTypeSystem unknown = GlslTypeSystem.For(450, GlslProfile.Core);

        Assert.True(fragment.IsKnownConstant("gl_FragCoord"));
        Assert.False(vertex.IsKnownConstant("gl_FragCoord"));
        Assert.True(vertex.IsKnownConstant("gl_VertexIndex"));
        Assert.False(fragment.IsKnownConstant("gl_VertexIndex"));

        // ステージの分からない断片は、すべてのステージのものを合わせる。
        Assert.True(unknown.IsKnownConstant("gl_FragCoord"));
        Assert.True(unknown.IsKnownConstant("gl_VertexIndex"));
    }

    [Fact]
    public void 組み込みはバージョンとプロファイルで変わる()
    {
        // gl_FragColor は ES 100 と互換プロファイルにあり、コアの 4.50 には無い。
        Assert.True(GlslTypeSystem.For(100, GlslProfile.Es, GlslShaderStage.Fragment).IsKnownConstant("gl_FragColor"));
        Assert.True(GlslTypeSystem.For(450, GlslProfile.Compatibility, GlslShaderStage.Fragment).IsKnownConstant("gl_FragColor"));
        Assert.False(GlslTypeSystem.For(450, GlslProfile.Core, GlslShaderStage.Fragment).IsKnownConstant("gl_FragColor"));

        // texture2D は ES 100 にあり、ES 300 には無い。
        Assert.True(GlslTypeSystem.For(100, GlslProfile.Es, GlslShaderStage.Fragment).TryGetIntrinsicArity("texture2D", out _));
        Assert.False(GlslTypeSystem.For(300, GlslProfile.Es, GlslShaderStage.Fragment).TryGetIntrinsicArity("texture2D", out _));

        // 表に無いバージョンは、表にある、それより前でいちばん新しいバージョンとして読む。
        Assert.True(GlslTypeSystem.For(470, GlslProfile.Core, GlslShaderStage.Fragment).IsKnownConstant("gl_FragCoord"));
    }

    [Fact]
    public void 組み込み関数の引数の数()
    {
        Assert.True(GlslTypeSystem.Instance.TryGetIntrinsicArity("clamp", out IntrinsicArity clamp));
        Assert.True(clamp.Accepts(3));
        Assert.False(clamp.Accepts(2));
        Assert.False(GlslTypeSystem.Instance.TryGetIntrinsicArity("saturate", out _));
    }

    [Fact]
    public void 木の言語で組み込み関数を判定する()
    {
        (ShaderCompilation compilation, AnalyzedProgram program) = Compile("a.frag", "#version 100\nvoid main() { }\n");

        Assert.True(compilation.TypeSystem.IsKnownFunction("texture2D", program.Tree));
        Assert.False(compilation.TypeSystem.IsKnownFunction("textureLod", program.Tree));
        Assert.False(compilation.TypeSystem.IsKnownFunction("lerp", program.Tree));
    }

    /// <summary>表を作り直しても、リポジトリの表から 1 文字も変わらない。</summary>
    [Fact]
    public void 表を作り直しても変わらない()
    {
        string? glslang = Environment.GetEnvironmentVariable("SHADERLYN_GLSLANG");

        if (string.IsNullOrWhiteSpace(glslang) || !File.Exists(glslang))
        {
            _output.WriteLine("SHADERLYN_GLSLANG が無いため飛ばします。");
            return;
        }

        string root = RepositoryRoot();
        string table = Path.Combine(root, "src", "Shaderlyn.Semantics", "Symbols", "GlslBuiltins.g.txt");
        string before = File.ReadAllText(table);

        ProcessStartInfo start = new("node", [Path.Combine(root, "tools", "glsl", "builtins.mjs")])
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["GLSLANG"] = glslang;

        using Process process = Process.Start(start)!;
        _output.WriteLine(process.StandardOutput.ReadToEnd());
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        string after = File.ReadAllText(table);

        if (after != before)
        {
            File.WriteAllText(table, before);
        }

        Assert.True(process.ExitCode == 0, error);
        Assert.True(after == before, "作り直した表がリポジトリの表と違います。");
    }

    /// <summary>コーパスの関数の呼び出しと <c>gl_</c> の名前が、すべて組み込みか宣言に解決する。</summary>
    [Fact]
    public void コーパスの関数の呼び出しがすべて解決する()
    {
        string? root = Environment.GetEnvironmentVariable("SHADERLYN_GLSL_CORPUS");

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _output.WriteLine("SHADERLYN_GLSL_CORPUS が無いため飛ばします。");
            return;
        }

        SemanticsOptions options = SemanticsOptions.Default with { IncludeResolver = new FileSystemIncludeResolver([]) };
        Dictionary<string, List<string>> unresolved = new(StringComparer.Ordinal);
        int files = 0;
        int calls = 0;

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(p => GlslLanguageOptions.StageFromFileName(p) is not null
                                 && !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            files++;
            SourceText text = SourceText.From(File.ReadAllText(path), path);
            ShaderCompilation compilation = ShaderCompilation.Create(
                text, [GlslEnvironment.WholeFile(text)], text, new GlslEnvironment(), ShaderSourceInfo.None, options);

            foreach (AnalyzedProgram program in compilation.Programs)
            {
                ShaderTypeSystem types = compilation.GetExpressionTypeBinder(program).TypeSystem;
                HashSet<string> structs = [.. program.Structs.Select(s => s.Name)];

                foreach (Core.Syntax.SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
                {
                    string? name = node switch
                    {
                        InvocationExpressionSyntax { TargetName: { } target } => target,
                        IdentifierExpressionSyntax identifier when identifier.Name.StartsWith("gl_", StringComparison.Ordinal)
                            && identifier.Parent is not InvocationExpressionSyntax => identifier.Name,
                        _ => null,
                    };

                    if (name is null)
                    {
                        continue;
                    }

                    calls++;

                    bool resolved = node is InvocationExpressionSyntax
                        ? !compilation.GetFunctionDeclarations(name, program).IsEmpty
                          || types.Classify(name) != ShaderTypeClass.Unknown
                          || structs.Contains(name)
                          || types.IsKnownFunction(name, program.Tree)
                        : types.IsKnownConstant(name);

                    if (!resolved)
                    {
                        if (!unresolved.TryGetValue(name, out List<string>? examples))
                        {
                            unresolved[name] = examples = [];
                        }

                        examples.Add($"{Path.GetRelativePath(root, path)}({(node as ShaderSyntaxNode)?.GetLocation()?.LineSpan.Start.Line + 1})");
                    }
                }
            }
        }

        _output.WriteLine($"ファイル {files} 件、関数の呼び出しと gl_ の名前 {calls} 件、解決しなかった名前 {unresolved.Count} 種類");

        foreach ((string name, List<string> examples) in unresolved.OrderByDescending(p => p.Value.Count))
        {
            _output.WriteLine($"{examples.Count,5} {name}: {string.Join(", ", examples.Take(3))}");
        }

        Assert.True(files > 0, "SHADERLYN_GLSL_CORPUS の下に GLSL のファイルがありません。");
        Assert.True(unresolved.Count == 0, $"解決しなかった名前: {string.Join(", ", unresolved.Keys.Take(10))}");
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;

        while (directory is not null && !File.Exists(Path.Combine(directory, "Shaderlyn.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("リポジトリのルートが見つかりません。");
    }
}
