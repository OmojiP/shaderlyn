using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// C 系の言語に共通のルールを GLSL で働かせたときの検証。
/// </summary>
/// <remarks>
/// <para>
/// 誤りの例は、どれも glslang 16.6.0 (<c>--target-env vulkan1.3</c>) がエラーにすることを確かめたものである。
/// エントリポイントが無いこと (CF0301) は、glslang がリンクのときにエラーにする。
/// </para>
/// <para>
/// 値を返さない経路 (CF0501) は、glslang はエラーにしない (値が不定になるだけ)。GLSL では警告として伝える。
/// </para>
/// </remarks>
public sealed class GlslRuleTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-glsl-rules-{Guid.NewGuid():N}");

    public GlslRuleTests() => Directory.CreateDirectory(_workDirectory);

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

    private async Task<string> AnalyzeAsync(string fileName, string code)
    {
        File.WriteAllText(Path.Combine(_workDirectory, fileName), code);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([_workDirectory, "--no-config"], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return output.ToString();
    }

    [Theory]
    [InlineData("CF0310", "a.frag", "void main() { o = vec4(missingValue); }")]
    [InlineData("CF0310", "a.vert", "void main() { gl_Position = gl_FragCoord; }")]
    [InlineData("CF0301", "a.frag", "void notMain() { o = vec4(1.0); }")]
    [InlineData("CF0410", "a.frag", "void main() { vec3 c = vec4(1.0); o = vec4(c, 1.0); }")]
    [InlineData("CF0410", "a.frag", "void main() { vec3 c = 1.0; o = vec4(c, 1.0); }")]
    [InlineData("CF0411", "a.frag", "vec3 make() { return vec4(1.0); }\nvoid main() { o = vec4(make(), 1.0); }")]
    [InlineData("CF0312", "a.frag", "void main() { vec2 v = vec2(1.0); o = vec4(v.z); }")]
    [InlineData("CF0312", "a.frag", "struct Light { vec3 color; };\nvoid main() { Light l; l.color = vec3(1.0); o = vec4(l.colour, 1.0); }")]
    [InlineData("CF0402", "a.frag", "float twice(float x) { return x * 2.0; }\nvoid main() { o = vec4(twice(1.0, 2.0)); }")]
    [InlineData("CF0402", "a.frag", "void main() { o = vec4(clamp(1.0, 0.0)); }")]
    [InlineData("CF0404", "a.frag", "void main() { o = vec4(1.0, 2.0); }")]
    [InlineData("CF0314", "a.frag", "float f(float x) { return x; }\nfloat f(float y) { return y; }\nvoid main() { o = vec4(f(1.0)); }")]
    public async Task GLSLの誤りを見つける(string id, string fileName, string body)
    {
        string output = await AnalyzeAsync(fileName, $"#version 450\nlayout(location = 0) out vec4 o;\n{body}\n");
        Assert.Contains($"error {id}", output, StringComparison.Ordinal);
    }

    /// <summary>ES には暗黙の変換が無いので、<c>int</c> を <c>float</c> に代入できない。デスクトップの 4.50 ではできる。</summary>
    [Fact]
    public async Task ESでは整数を浮動小数に代入できない()
    {
        const string body = "precision mediump float;\nlayout(location = 0) out vec4 o;\nvoid main() { float f = 1; o = vec4(f); }\n";

        Assert.Contains("error CF0410", await AnalyzeAsync("es.frag", "#version 310 es\n" + body), StringComparison.Ordinal);
        Assert.DoesNotContain("CF0410", await AnalyzeAsync("es.frag", "#version 450\n" + body), StringComparison.Ordinal);
    }

    /// <summary>GLSL で正しいが、HLSL の規則で読むと誤りになる書き方。</summary>
    [Theory]
    [InlineData("void main() { vec3 c = texture(tex, uv).rgb; c *= 0.5; o = vec4(c.stp, 1.0); }")]
    [InlineData("void main() { mat4 m = mat4(mat3(1.0)); o = m * vec4(uv, 0.0, 1.0); }")]
    [InlineData("void main() { vec3 v = vec3(uv, uv); o = vec4(v, 1.0); }")]
    [InlineData("void main() { vec3 v = vec3(1.0); v *= mat3(1.0); o = vec4(v, 1.0); }")]
    [InlineData("void main() { float[3] a = float[3](1.0, 2.0, 3.0); o = vec4(a[a.length() - 1]); }")]
    [InlineData("layout(binding = 1) uniform UBO { mat4 model; } ubo;\nvoid main() { o = ubo.model * vec4(uv, 0.0, 1.0); }")]
    [InlineData("layout(binding = 1) uniform Params { float scale; };\nvoid main() { o = vec4(scale); }")]
    [InlineData("void main() { if (uv.x < 0.0) { discard; } o = vec4(gl_FragCoord.xy, 0.0, 1.0); }")]
    [InlineData("void main() { bool a = uv.x > 0.0; bool b = uv.y > 0.0; o = vec4(a ^^ b ? 1.0 : 0.0); }")]
    [InlineData("void main() { vec4(uv, 0.0, 1.0); ivec2[2](ivec2(1), ivec2(2)); o = vec4(1.0); }")]
    [InlineData("struct S { float x; };\nvoid main() { S[2] s; s[0].x = 1.0; o = vec4(s[0].x); }")]
    [InlineData("void main() { int i = 0; while (bool go = i < 3) { i += go ? 1 : 0; } o = vec4(float(i)); }")]
    [InlineData("void main() { float a, b; a = 1.0, b = 2.0; o = vec4(a + b); }")]
    [InlineData("void main() { struct T { float y[2]; } t; t.y[1] = 1.0; o = vec4(t.y[1]); }")]
    [InlineData("void main() { vec2 v = uv; { vec4 v = vec4(v, 0.0, 1.0); o = v; } }")]
    [InlineData("void main() { for (int i = 0; i < 2; i++) { } float i = 5.0; o = vec4(i); }")]
    [InlineData("struct S { float x; };\nvoid main() { S a = S(1.0); S b = a; S c = a == b ? a : b; o = vec4(c.x); }")]
    [InlineData("float u[];\nvoid main() { o = vec4(1.0); }\nfloat u[4];")]
    public async Task GLSLの正しいコードには何も言わない(string body)
    {
        string output = await AnalyzeAsync("ok.frag", $$"""
            #version 450
            layout(location = 0) out vec4 o;
            layout(location = 0) in vec2 uv;
            layout(binding = 0) uniform sampler2D tex;
            {{body}}
            """);

        Assert.DoesNotContain("ok.frag", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 拡張で増える書き方 (明示的な幅の算術型の接尾辞、64 ビットのリテラル、型の引数を取る型) を読む。
    /// </summary>
    [Fact]
    public async Task 拡張の型とリテラルを読む()
    {
        string output = await AnalyzeAsync("ext.comp", """
            #version 460
            #extension GL_EXT_shader_explicit_arithmetic_types : enable
            #extension GL_EXT_long_vector : enable
            layout(local_size_x = 1) in;
            layout(binding = 0) buffer Out { float values[]; };
            void main()
            {
                int16_t a = 3s;
                uint16_t b = 4us;
                int64_t c = 0x1111111111111111l;
                uint64_t d = 18446744073709551615ul;
                float16_t h = 1.0hf;
                vector<float, 5> v = vector<float, 5>(0.0);
                values[0] = float(a) + float(b) + float(c) + float(d) + float(h) + v[0];
            }
            """);

        Assert.DoesNotContain("ext.comp", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 暗黙の変換は、有効にした拡張で増える (glslang の canImplicitlyPromote)。3.30 の int から uint は GL_ARB_gpu_shader5 で通る。
    /// </summary>
    [Fact]
    public async Task 拡張で暗黙の変換が増える()
    {
        const string body = "layout(location = 0) out vec4 o;\nvoid main() { uint u = 1; o = vec4(float(u)); }\n";

        Assert.Contains("error CF0410", await AnalyzeAsync("a.frag", "#version 330\n" + body), StringComparison.Ordinal);
        Assert.DoesNotContain("CF0410", await AnalyzeAsync("a.frag", "#version 330\n#extension GL_ARB_gpu_shader5 : enable\n" + body), StringComparison.Ordinal);
    }

    /// <summary>
    /// 後のバージョンでキーワードになった語は、それより前のバージョンでは名前に使える (LearnOpenGL の <c>vec3 sample[9];</c>)。
    /// 予約語は名前に使えない。
    /// </summary>
    [Fact]
    public async Task バージョンでキーワードが変わる()
    {
        const string body = "out vec4 o;\nvoid main() { vec3 sample[2]; sample[0] = vec3(1.0); o = vec4(sample[0], 1.0); }\n";

        Assert.DoesNotContain("a.frag", await AnalyzeAsync("a.frag", "#version 330 core\n" + body), StringComparison.Ordinal);
        Assert.Contains("error CF0001", await AnalyzeAsync("a.frag", "#version 450\nout vec4 o;\nvoid main() { int class = 1; o = vec4(class); }\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// ジオメトリシェーダーの入力と出力のブロックは、同じ名前でよい (ブロックの名前は型の名前ではない)。
    /// </summary>
    [Fact]
    public async Task 入力と出力のブロックは同じ名前でよい()
    {
        string output = await AnalyzeAsync("a.geom", """
            #version 450
            layout(triangles) in;
            layout(triangle_strip, max_vertices = 3) out;
            layout(location = 0) in block { vec4 color; } In[];
            layout(location = 0) out block { vec4 color; vec2 uv; } Out;
            void main()
            {
                for (int i = 0; i < gl_in.length(); ++i)
                {
                    Out.color = In[i].color;
                    Out.uv = vec2(0.0);
                    gl_Position = gl_in[i].gl_Position;
                    EmitVertex();
                }
            }
            """);

        Assert.DoesNotContain("a.geom", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 拡張で足される組み込みは、その拡張を使う位置より前で有効にしなければならない (GL0001)。
    /// </summary>
    [Theory]
    [InlineData("#extension GL_KHR_shader_subgroup_basic : enable\n", true)]
    [InlineData("#extension GL_KHR_shader_subgroup_arithmetic : enable\n", false)]
    [InlineData("", true)]
    public async Task 組み込みに要る拡張(string extension, bool expected)
    {
        string output = await AnalyzeAsync("a.comp", $$"""
            #version 450
            {{extension}}layout(local_size_x = 64) in;
            layout(binding = 0) buffer Data { float values[]; };
            void main() { values[gl_LocalInvocationIndex] = subgroupAdd(values[gl_LocalInvocationIndex]); }
            """);

        Assert.Equal(expected, output.Contains("error GL0001", StringComparison.Ordinal));
    }

    /// <summary>型・修飾子・演算子・リテラルを、バージョン・ステージ・場所で検査する (glslang の文法の規則の検査)。</summary>
    [Theory]
    [InlineData("GL0001", "a.frag", "#version 120\nvoid main() { uint u = 1u; gl_FragColor = vec4(float(u)); }")]
    [InlineData("GL0001", "a.frag", "#version 120\nvoid main() { int i = 5 % 2; gl_FragColor = vec4(float(i)); }")]
    [InlineData("GL0001", "a.frag", "#version 330\nout vec4 o;\nvoid main() { double d = 1.0lf; o = vec4(float(d)); }")]
    [InlineData("GL0001", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\nvoid main() { double d = 1.0; o = vec4(float(d)); }")]
    [InlineData("GL0002", "a.vert", "#version 450\nshared float s;\nvoid main() { gl_Position = vec4(s); }")]
    [InlineData("GL0201", "a.frag", "#version 300 es\nout vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { in vec4 v; o = v; }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0, offset = 4) out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0101", "a.comp", "#version 450\nlayout(binding = 0) buffer B { layout(local_size_x = 8) float v[]; };\nvoid main() { v[0] = 1.0; }")]
    [InlineData("GL0103", "a.geom", "#version 450\nlayout(triangles) in;\nlayout(points, max_vertices = 1) out;\nlayout(lines) in;\nvoid main() { EmitVertex(); }")]
    [InlineData("GL0102", "a.frag", "#version 450\nlayout(location = 0, colour_space) out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0102", "a.vert", "#version 450\nlayout(local_size_x = 8) in;\nvoid main() { gl_Position = vec4(1.0); }")]
    [InlineData("GL0103", "a.frag", "#version 450\nlayout(location = -1) out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0103", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(std140, binding = 0) uniform U { layout(align = 12) vec4 a; };\nvoid main() { o = a; }")]
    [InlineData("GL0103", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(location = 0, component = 2) out vec2 p;\nvoid main() { o = vec4(1.0); p = vec2(1.0); }")]
    [InlineData("GL0103", "a.frag", "#version 450\nlayout(location = 40) out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0104", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(offset = 0) uniform atomic_uint c;\nvoid main() { o = vec4(float(atomicCounter(c))); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { vec2 a = vec2(1.0); vec3 b = vec3(1.0); o = vec4(a + b, 1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { bool b = true; float f = 1.0 + b; o = vec4(f); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { mat4 m = mat4(1.0); o = vec4(m * vec3(1.0), 1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { vec2 a = vec2(1.0); o = vec4(a < a ? 1.0 : 0.0); }")]
    [InlineData("GL0302", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D s;\nvoid main() { o = texture(s, vec3(0.5)); }")]
    [InlineData("GL0302", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\nvoid main() { o = vec4(max(1, 2.0)); }")]
    [InlineData("GL0303", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D s;\nlayout(location = 0) flat in ivec2 shift;\nvoid main() { o = textureOffset(s, vec2(0.5), shift); }")]
    [InlineData("GL0304", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D s;\nvoid main() { o = textureOffset(s, vec2(0.5), ivec2(8, 0)); }")]
    [InlineData("GL0304", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D s;\nvoid main() { o = textureGather(s, vec2(0.5), 4); }")]
    [InlineData("GL0001", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0) buffer B { float v; };\nvoid main() { atomicAdd(v, 1.0); }")]
    [InlineData("GL0001", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform texture2D t;\nvoid main() { o = vec4(textureSize(t, 0), 0.0, 1.0); }")]
    [InlineData("GL0001", "a.comp", "#version 450\n#extension GL_EXT_shader_16bit_storage : enable\nlayout(local_size_x = 1) in;\nlayout(binding = 0) buffer B { float16_t h; float f; };\nvoid main() { float16_t local = h; f = float(local); }")]
    [InlineData("GL0001", "a.frag", "#version 330\nout vec4 o;\nvoid main() { float a[2][3]; o = vec4(a[0][0]); }")]
    [InlineData("GL0001", "a.frag", "#version 330\n#define ONE \\\n 1.0\nout vec4 o;\nvoid main() { o = vec4(ONE); }")]
    public async Task 文法の規則の検査(string id, string fileName, string code)
    {
        string output = await AnalyzeAsync(fileName, code + "\n");
        Assert.Contains($"error {id}", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// SPIR-V を作るとき、入力と出力の location とブロックの binding が要る。
    /// コンパイラに決めさせる設定 (<c>glsl: auto-map-locations</c> / <c>auto-map-bindings</c>) なら要らない。
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("  auto-map-locations: true\n  auto-map-bindings: true\n", false)]
    public async Task SPIRVを作るときのlocationとbinding(string autoMap, bool expected)
    {
        string config = Path.Combine(_workDirectory, "shaderlyn.yml");
        File.WriteAllText(config, $"version: 1\nenvironment: glsl\nglsl:\n  target: vulkan1.0\n{autoMap}");
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), """
            #version 450
            in vec4 color;
            out vec4 o;
            uniform U { vec4 tint; };
            void main() { o = color * tint; }

            """);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.frag"), "--config", config, "--environment", "glsl"], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Equal(expected, output.ToString().Contains("error GL0104", StringComparison.Ordinal));
    }

    /// <summary>値を返さない経路 (CF0501) は、GLSL では警告にする。設定で重要度を書けば、それに従う。</summary>
    [Theory]
    [InlineData("", "warning CF0501")]
    [InlineData("rules:\n  CF0501: error\n", "error CF0501")]
    public async Task 値を返さない経路はGLSLでは警告(string rules, string expected)
    {
        string config = Path.Combine(_workDirectory, "shaderlyn.yml");
        File.WriteAllText(config, $"version: 1\nenvironment: glsl\n{rules}");
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), """
            #version 450
            layout(location = 0) out vec4 o;
            float pick(bool b) { if (b) { return 1.0; } }
            void main() { o = vec4(pick(true)); }

            """);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.frag"), "--config", config], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    /// <summary><c>#extension</c> は書いた位置より後ろにだけ効く。</summary>
    [Fact]
    public async Task 拡張は書いた位置より後ろに効く()
    {
        string output = await AnalyzeAsync("a.comp", """
            #version 450
            layout(local_size_x = 64) in;
            layout(binding = 0) buffer Data { uint values[]; };
            uint before() { return gl_SubgroupSize; }
            #extension GL_KHR_shader_subgroup_basic : enable
            uint after() { return gl_SubgroupSize; }
            void main() { values[0] = before() + after(); }
            """);

        Assert.Contains("a.comp(4:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a.comp(6:", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// HLSL にしか無い検査 (HL で始まるルール) は、GLSL では働かない。
    /// </summary>
    [Fact]
    public async Task HLSLにしか無いルールは出さない()
    {
        string output = await AnalyzeAsync("a.frag", """
            #version 450
            layout(location = 0) out vec4 o;
            void main() { bvec2 a = bvec2(true); bvec2 b = bvec2(false); o = vec4(a == b ? 1.0 : 0.0); }
            """);

        Assert.DoesNotContain(" HL0", output, StringComparison.Ordinal);
    }
}
