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
    [InlineData("GL0002", "a.vert", "void main() { gl_Position = gl_FragCoord; }")]
    [InlineData("CF0301", "a.frag", "void notMain() { o = vec4(1.0); }")]
    [InlineData("CF0410", "a.frag", "void main() { vec3 c = vec4(1.0); o = vec4(c, 1.0); }")]
    [InlineData("CF0410", "a.frag", "void main() { vec3 c = 1.0; o = vec4(c, 1.0); }")]
    [InlineData("CF0412", "a.frag", "void main() { mat2 m = { 1.0, 0.0, 0.0, 1.0 }; o = vec4(m[0], m[1]); }")]
    [InlineData("CF0411", "a.frag", "vec3 make() { return vec4(1.0); }\nvoid main() { o = vec4(make(), 1.0); }")]
    [InlineData("CF0411", "a.frag", "vec4 none() { }\nvoid main() { o = none(); }")]
    [InlineData("CF0312", "a.frag", "void main() { float f[7]; int n = f.length; o = vec4(n); }")]
    [InlineData("CF0312", "a.frag", "void main() { float f[7]; int a = 1; int n = f.length(a); o = vec4(n); }")]
    [InlineData("CF0411", "a.frag", "layout(binding = 0) uniform atomic_uint counter;\nuint count() { return counter; }\nvoid main() { o = vec4(count()); }")]
    [InlineData("CF0312", "a.frag", "void main() { vec2 v = vec2(1.0); o = vec4(v.z); }")]
    [InlineData("CF0312", "a.frag", "struct Light { vec3 color; };\nvoid main() { Light l; l.color = vec3(1.0); o = vec4(l.colour, 1.0); }")]
    [InlineData("CF0402", "a.frag", "float twice(float x) { return x * 2.0; }\nvoid main() { o = vec4(twice(1.0, 2.0)); }")]
    [InlineData("CF0402", "a.frag", "void main() { o = vec4(clamp(1.0, 0.0)); }")]
    [InlineData("CF0404", "a.frag", "void main() { o = vec4(1.0, 2.0); }")]
    [InlineData("CF0314", "a.frag", "float f(float x) { return x; }\nfloat f(float y) { return y; }\nvoid main() { o = vec4(f(1.0)); }")]
    [InlineData("CF0314", "a.frag", "bool b;\nfloat b(int a);\nvoid main() { o = vec4(1.0); }")]
    [InlineData("CF0410", "a.frag", "float a4[4];\nfloat a5[5];\nvoid main() { a5 = a4; o = vec4(a5[0]); }")]
    [InlineData("CF0410", "a.frag", "void main() { int b[3] = int[](3, 2); o = vec4(b[0]); }")]
    [InlineData("CF0410", "a.frag", "void main() { float c[2] = { 1.0, 2.0, 3.0 }; o = vec4(c[0]); }")]
    [InlineData("CF0314", "a.frag", "uniform U { float f; };\nuniform U { float g; };\nvoid main() { o = vec4(f); }")]
    [InlineData("CF0314", "a.frag", "uniform U { float f; } u;\nuniform V { float g; } U;\nvoid main() { o = vec4(u.f); }")]
    [InlineData("CF0314", "a.frag", "uniform U { float f; };\nfloat f;\nvoid main() { o = vec4(f); }")]
    [InlineData("CF0314", "a.frag", "uniform U { float f; };\nuniform V { float f; };\nvoid main() { o = vec4(f); }")]
    [InlineData("CF0314", "a.frag", "uniform U { float f; } u;\nint U;\nvoid main() { o = vec4(u.f); }")]
    public async Task GLSLの誤りを見つける(string id, string fileName, string body)
    {
        string output = await AnalyzeAsync(fileName, $"#version 450\nlayout(location = 0) out vec4 o;\n{body}\n");
        Assert.Contains($"error {id}", output, StringComparison.Ordinal);
    }

    /// <summary>GLSL の 1.10 だけは、変数と関数の名前の空間が分かれている (glslang と同じ)。</summary>
    [Fact]
    public async Task 変数と関数の同じ名前は110だけ許す()
    {
        const string body = "bool b;\nfloat b(int a) { return 1.0; }\nvoid main() { gl_FragColor = vec4(b(1)); }\n";

        Assert.DoesNotContain("CF0314", await AnalyzeAsync("a.frag", "#version 110\n" + body), StringComparison.Ordinal);
        Assert.Contains("error CF0314", await AnalyzeAsync("a.frag", "#version 120\n" + body), StringComparison.Ordinal);
    }

    /// <summary>ブロックの名前は、記憶域 (in と out など) が違えば同じものを使える (glslang と同じ)。</summary>
    [Fact]
    public async Task 記憶域の違うブロックは同じ名前にできる()
    {
        const string body = "#version 450\nlayout(location = 0) in V { vec4 c; } i;\nlayout(location = 0) out V { vec4 c; };\nvoid main() { c = i.c; }\n";

        Assert.DoesNotContain("CF0314", await AnalyzeAsync("a.vert", body), StringComparison.Ordinal);
    }

    /// <summary>32 ビットに収まる整数のリテラルと、正しい 8 進数は報告しない。int の 0xFFFFFFFF は負の数として読む。</summary>
    [Fact]
    public async Task 収まる整数のリテラル()
    {
        const string body = "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { int a = 4294967295; uint b = 0xFFFFFFFFu; int c = 037777777777; int d = 0; int e = 007; o = vec4(a + c + d + e) + vec4(b); }\n";

        Assert.DoesNotContain("GL0315", await AnalyzeAsync("a.frag", body), StringComparison.Ordinal);
    }

    /// <summary>数の型への演算子と、両方の枝が void の ?: は報告しない。</summary>
    [Fact]
    public async Task 演算子を使える値()
    {
        const string body = "#version 450\nlayout(binding = 0) uniform sampler2D s;\nlayout(location = 0) out vec4 o;\nvoid f() { }\nvoid g() { }\n"
                            + "void main() { bool b = o.x > 0.0; b ? f() : g(); ivec2 n = textureSize(s, 0) + ivec2(1); o = vec4(n, 0.0, 1.0); }\n";

        Assert.DoesNotContain("GL0301", await AnalyzeAsync("a.frag", body), StringComparison.Ordinal);
    }

    /// <summary>整数の添字・switch の条件・case のラベルは報告しない。</summary>
    [Fact]
    public async Task 整数のスカラーの添字と条件()
    {
        const string body = "#version 450\nlayout(location = 0) out vec4 o;\nuniform uint u;\nvoid main() { vec4 v[2] = vec4[2](vec4(0.0), vec4(1.0)); int i = 1; o = v[i] + v[u];\n"
                            + "switch (i) { case 1: break; default: break; } switch (u) { case 2u: break; } }\n";

        Assert.DoesNotContain("GL0316", await AnalyzeAsync("a.frag", body), StringComparison.Ordinal);
    }

    /// <summary>定数の式は報告しない。組み込み関数の値、定数の組み込みの変数、length()、4.20 からの関数の中の const の変数の初期値。</summary>
    [Fact]
    public async Task 定数の式()
    {
        const string body = "#version 450\nlayout(location = 0) out vec4 o;\nuniform int n;\nconst int k = 3;\nconst int m = k * 2;\nfloat g[m];\n"
                            + "void main() { const int c = n; float a[int(sin(0.3)) + k]; float b[gl_MaxDrawBuffers]; float d[a.length()]; o = vec4(a[0] + b[0] + d[0] + g[0] + float(c)); }\n";

        string output = await AnalyzeAsync("a.frag", body);
        Assert.DoesNotContain("GL0317", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GL0001", output, StringComparison.Ordinal);
    }

    /// <summary>初期値のある const の変数、const の仮引数、合った数の実引数で配列を作る式は報告しない。</summary>
    [Fact]
    public async Task 初期値と配列を作る式()
    {
        const string body = "#version 450\nlayout(location = 0) out vec4 o;\nconst struct S { int a; } s = S(1);\n"
                            + "float f(const float x) { return x; }\n"
                            + "void main() { const int c = 2; float a[2] = float[2](1.0, 2.0); float b[] = float[](1.0, 2.0, 3.0); o = vec4(a[0] + b[0] + f(float(c + s.a))); }\n";

        string output = await AnalyzeAsync("a.frag", body);
        Assert.DoesNotContain("GL0317", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GL0318", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GL0319", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GL0101", output, StringComparison.Ordinal);
    }

    /// <summary>セマンティクスの組み合わせが決まりに合っていれば報告しない。</summary>
    [Fact]
    public async Task 正しいメモリのセマンティクス()
    {
        const string body = "#version 450\n#extension GL_KHR_memory_scope_semantics : require\nlayout(local_size_x = 1) in;\nlayout(binding = 0) buffer B { uint x; } b;\n"
                            + "void main() {\n"
                            + "  atomicStore(b.x, 1u, gl_ScopeDevice, gl_StorageSemanticsBuffer, gl_SemanticsRelease | gl_SemanticsMakeAvailable);\n"
                            + "  uint v = atomicLoad(b.x, gl_ScopeDevice, gl_StorageSemanticsBuffer, gl_SemanticsAcquire);\n"
                            + "  atomicAdd(b.x, v, gl_ScopeDevice, gl_StorageSemanticsNone, gl_SemanticsRelaxed);\n"
                            + "  memoryBarrier(gl_ScopeWorkgroup, gl_StorageSemanticsShared, gl_SemanticsAcquireRelease);\n"
                            + "  atomicCompSwap(b.x, 1u, 2u, gl_ScopeDevice, gl_StorageSemanticsBuffer, gl_SemanticsAcquireRelease, gl_StorageSemanticsBuffer, gl_SemanticsAcquire);\n"
                            + "}\n";

        string output = await AnalyzeAsync("a.comp", body);
        Assert.DoesNotContain("GL0314", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GL0303", output, StringComparison.Ordinal);
    }

    /// <summary>修飾子と形式が仮引数と合えば渡せる。writeonly の仮引数には形式の無いイメージも渡せる。</summary>
    [Fact]
    public async Task 修飾子の合うイメージの実引数()
    {
        const string body = "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0, rgba8) uniform coherent restrict writeonly image2D t;\n"
                            + "void store(layout(rgba8) coherent writeonly image2D i) { imageStore(i, ivec2(0), vec4(1.0)); }\n"
                            + "void store2(writeonly image2D i) { imageStore(i, ivec2(0), vec4(1.0)); }\n"
                            + "layout(binding = 1) uniform writeonly image2D u;\n"
                            + "void main() { store(t); store2(u); imageStore(t, ivec2(0), vec4(0.0)); }\n";

        Assert.DoesNotContain("GL0313", await AnalyzeAsync("a.comp", body), StringComparison.Ordinal);
    }

    /// <summary>4.20 からは修飾子の順を問わない。決まった順なら、それより前の版でも良い。</summary>
    [Theory]
    [InlineData("#version 420\nout flat vec4 v;\nvoid main() { v = vec4(1.0); gl_Position = v; }")]
    [InlineData("#version 330\nflat centroid out vec4 v;\ninvariant gl_Position;\nvoid main() { v = vec4(1.0); gl_Position = v; }")]
    [InlineData("#version 300 es\nprecision highp float;\nflat out highp vec4 v;\nvoid main() { v = vec4(1.0); gl_Position = v; }")]
    public async Task 修飾子の順が正しいもの(string code)
    {
        Assert.DoesNotContain("GL0101", await AnalyzeAsync("a.vert", code + "\n"), StringComparison.Ordinal);
    }

    /// <summary>外側の大きさを省いた配列は、初期化の値の大きさになる。同じ大きさなら代入できる。</summary>
    [Fact]
    public async Task 大きさの合う配列の代入と初期化()
    {
        const string body = "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float d[] = float[](1.0, 2.0); float e[2]; e = d; float f[2][3]; float g[][3] = f; o = vec4(e[0] + g[0][0]); }\n";

        Assert.DoesNotContain("CF0410", await AnalyzeAsync("a.frag", body), StringComparison.Ordinal);
    }

    /// <summary>
    /// デスクトップでは組み込み関数を宣言し直せる (GL0405 は ES だけ)。1.10 では変数が関数を隠さない (GL0312)。
    /// ES の 1.00 では、形の違う関数と関数の外の変数に組み込み関数の名前を使える。
    /// </summary>
    [Theory]
    [InlineData("#version 450\nlayout(location = 0) out vec4 o;\nfloat sin(float x) { return x; }\nvoid main() { o = vec4(sin(1.0)); }")]
    [InlineData("#version 110\nvoid main() { float sin = 1.0; gl_FragColor = vec4(sin(sin)); }")]
    [InlineData("#version 100\nprecision mediump float;\nfloat tan;\nbool radians(bool x) { return x; }\nvoid main() { gl_FragColor = vec4(tan); }")]
    [InlineData("#version 450\nlayout(location = 0) out vec4 o;\nstruct S { float x; };\nvoid main() { S S = S(1.0); o = vec4(S.x); }")]
    public async Task 組み込み関数の名前を使えるもの(string code)
    {
        string output = await AnalyzeAsync("a.frag", code + "\n");
        Assert.DoesNotContain("GL0312", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GL0405", output, StringComparison.Ordinal);
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
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float a[2] = float[](1.0, 2.0); a * a; o = vec4(1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(location = 0) flat in int i;\nvoid main() { o = i > 0 ? vec4(1.0) : mat4(1.0); }")]
    [InlineData("GL0302", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D s;\nvoid main() { o = texture(s, vec3(0.5)); }")]
    [InlineData("GL0302", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\nvoid main() { o = vec4(max(1, 2.0)); }")]
    [InlineData("GL0302", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(sin(true)); }")]
    [InlineData("CF0405", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat f(float x) { return x; }\nvoid main() { float a[2]; o = vec4(f(a)); }")]
    [InlineData("CF0312", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { mat4x3 m; o = vec4(m[3][1].length()); }")]
    [InlineData("GL0103", "a.vert", "#version 440\nlayout(xfb_buffer = 1, xfb_offset = 0) out vec4 a;\nlayout(xfb_buffer = 1, xfb_offset = 8) out vec4 b;\nvoid main() { a = b = vec4(0.0); gl_Position = a; }")]
    [InlineData("GL0103", "a.vert", "#version 440\nout Block { layout(xfb_buffer = 2, xfb_offset = 0) vec4 v; } b;\nvoid main() { b.v = vec4(0.0); gl_Position = b.v; }")]
    [InlineData("GL0103", "a.geom", "#version 450\nlayout(points) in;\nlayout(points, max_vertices = 1) out;\nlayout(stream = 1) out Block { layout(stream = 2) vec4 v; } b;\nvoid main() { b.v = vec4(0.0); EmitStreamVertex(1); }")]
    [InlineData("GL0103", "a.frag", "#version 440\nlayout(location = 0) out vec4 o;\nlayout(std140, binding = 0) uniform U { vec4 a; layout(offset = 20) vec4 b; };\nvoid main() { o = a; }")]
    [InlineData("GL0103", "a.frag", "#version 440\nlayout(location = 0) out vec4 o;\nlayout(std140, binding = 0) uniform U { vec4 a; layout(offset = 68) double h; };\nvoid main() { o = a; }")]
    [InlineData("GL0103", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform atomic_uint a;\nlayout(binding = 0) uniform atomic_uint b[2];\nlayout(binding = 0, offset = 4) uniform atomic_uint c;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0103", "a.vert", "#version 440\nlayout(xfb_buffer = 1, xfb_offset = 2) out float a;\nvoid main() { a = 0.0; gl_Position = vec4(a); }")]
    [InlineData("GL0103", "a.vert", "#version 440\nlayout(xfb_buffer = 1, xfb_offset = 4) out double a;\nvoid main() { a = 0.0; gl_Position = vec4(a); }")]
    [InlineData("GL0103", "a.vert", "#version 440\nlayout(xfb_buffer = 1, xfb_stride = 32) out;\nlayout(xfb_buffer = 1, xfb_offset = 0, xfb_stride = 16) out vec4 a;\nvoid main() { a = vec4(0.0); gl_Position = a; }")]
    [InlineData("GL0308", "a.geom", "#version 450\nlayout(points, max_vertices = 1) out;\nvoid f() { int n = gl_in.length(); }\nlayout(triangles) in;\nvoid main() { f(); }")]
    [InlineData("GL0308", "a.tesc", "#version 450\nint a[gl_out.length()];\nlayout(vertices = 3) out;\nvoid main() { }")]
    [InlineData("GL0301", "a.frag", "#version 450\nstruct S { int i; sampler2D s; };\nuniform S a;\nuniform S b;\nlayout(location = 0) out vec4 o;\nvoid main() { if (a == b) o = vec4(1.0); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { atomic_uint c; o = vec4(1.0); }")]
    [InlineData("GL0304", "a.frag", "#version 450\nlayout(binding = 0) uniform sampler2D s;\nlayout(location = 0) out vec4 o;\nvoid main() { const ivec2[2] offsets = ivec2[2](ivec2(1, 2), ivec2(15, 16)); o = textureOffset(s, vec2(0.5), offsets[1]); }")]
    [InlineData("GL0101", "a.frag", "#version 450\n#extension GL_EXT_nonuniform_qualifier : enable\nnonuniformEXT uniform vec4 u;\nlayout(location = 0) out vec4 o;\nvoid main() { o = u; }")]
    [InlineData("GL0101", "a.vert", "#version 120\ncentroid gl_Position;\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0406", "a.frag", "#version 420 core\nlayout(depth_any) out float gl_FragDepth;\nlayout(depth_greater) out float gl_FragDepth;\nvoid main() { gl_FragDepth = 0.5; }")]
    [InlineData("GL0406", "a.frag", "#version 150 core\nin vec4 gl_FragCoord;\nlayout(origin_upper_left) in vec4 gl_FragCoord;\nout vec4 o;\nvoid main() { o = gl_FragCoord; }")]
    [InlineData("GL0406", "a.frag", "#version 130\nflat in vec3 gl_Color;\nvoid main() { gl_FragColor = vec4(1.0); }")]
    [InlineData("GL0406", "a.vert", "#version 450\nout gl_PerVertex { vec4 gl_Position; vec4 t; };\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0406", "a.vert", "#version 450\nout gl_PerVertex { vec4 gl_Position; };\nvoid main() { gl_Position = vec4(0.0); gl_PointSize = 1.0; }")]
    [InlineData("GL0406", "a.geom", "#version 450\nlayout(points) in;\nlayout(points, max_vertices = 1) out;\nin gl_PerVertex { vec4 gl_Position; } myIn[];\nvoid main() { }")]
    [InlineData("GL0001", "a.frag", "#version 100\nprecision mediump float;\nvoid main() { struct S { float a[2]; } s1, s2; s1 = s2; gl_FragColor = vec4(1.0); }")]
    [InlineData("GL0001", "a.frag", "#version 100\nprecision mediump float;\nstruct S { float a[2]; };\nS f() { S s; return s; }\nvoid main() { gl_FragColor = vec4(1.0); }")]
    [InlineData("GL0001", "a.frag", "#version 100\nprecision mediump float;\nvoid main() { float a[2]; gl_FragColor = vec4(a.length()); }")]
    [InlineData("GL0001", "a.vert", "#version 120\nint[2][3] f() { int[2][3] a; return a; }\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0001", "a.vert", "#version 310 es\nuniform B { int a; } b[2][3];\nvoid main() { gl_Position = vec4(b[0][0].a); }")]
    [InlineData("GL0001", "a.vert", "#version 120\n#define M(a, b) a ## b\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0001", "a.vert", "#version 300 es\n#define M(a) #a\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0001", "a.vert", "#version 130\nint \\\n x;\n#extension GL_ARB_shading_language_420pack : enable\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0001", "a.frag", "#version 110\nvoid main() { gl_FragColor = vec4(1.0f); }")]
    [InlineData("GL0001", "a.vert", "#version 100\nvarying struct S { float f; } s;\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0001", "a.vert", "#version 300 es\nout B { vec4 f; };\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0001", "a.vert", "#version 300 es\nvoid main() { int g(); gl_Position = vec4(0.0); }")]
    [InlineData("CF0001", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nvoid foo(int n, int x = 2) { }\nvoid main() { foo(1, 2); }")]
    [InlineData("CF0402", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nvoid foo(int n, int x = 2) { }\nvoid main() { foo(1); }")]
    [InlineData("GL0302", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(length(\"abc\")); }")]
    [InlineData("CF0405", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat f(float x[2]) { return x[0]; }\nvoid main() { float a[]; a[1] = 1.0; o = vec4(f(a)); }")]
    [InlineData("GL0302", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float a[2]; o = vec4(abs(a)); }")]
    [InlineData("GL0302", "a.frag", "#version 450\nlayout(location = 0) in vec3 v[2];\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(interpolateAtCentroid(v), 1.0); }")]
    [InlineData("GL0303", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D s;\nlayout(location = 0) flat in ivec2 shift;\nvoid main() { o = textureOffset(s, vec2(0.5), shift); }")]
    [InlineData("GL0304", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D s;\nvoid main() { o = textureOffset(s, vec2(0.5), ivec2(8, 0)); }")]
    [InlineData("GL0304", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D s;\nvoid main() { o = textureGather(s, vec2(0.5), 4); }")]
    [InlineData("GL0305", "a.tesc", "#version 450\nlayout(vertices = 4) out;\nvoid main() { if (gl_InvocationID == 0) barrier(); }")]
    [InlineData("GL0305", "a.tesc", "#version 450\nlayout(vertices = 4) out;\nvoid sync() { barrier(); }\nvoid main() { sync(); }")]
    [InlineData("GL0305", "a.frag", "#version 450\n#extension GL_ARB_fragment_shader_interlock : enable\nlayout(pixel_interlock_ordered) in;\nvoid main() { beginInvocationInterlockARB(); endInvocationInterlockARB(); endInvocationInterlockARB(); }")]
    [InlineData("GL0306", "a.frag", "#version 450\nlayout(location = 0) in vec4 c;\nlayout(location = 0) out vec4 o;\nvoid main() { vec4 t = c; o = interpolateAtCentroid(t); }")]
    [InlineData("GL0306", "a.frag", "#version 430\nlayout(location = 0) in vec4 c;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(interpolateAtCentroid(c.xy), 0.0, 1.0); }")]
    [InlineData("GL0306", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nint counter;\nvoid main() { atomicAdd(counter, 1); }")]
    [InlineData("GL0306", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0, rgba8ui) uniform uimage2D img;\nvoid main() { imageAtomicAdd(img, ivec2(0), 1u); }")]
    [InlineData("GL0307", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float a[4]; a[0] = 1.0; o = vec4(a[4]); }")]
    [InlineData("GL0307", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { vec3 v = vec3(1.0); o = vec4(v[-1]); }")]
    [InlineData("GL0307", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { mat2x4 m = mat2x4(1.0); o = m[2]; }")]
    [InlineData("GL0307", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { const int c[] = int[](3, 2); o = vec4(c[2]); }")]
    [InlineData("GL0307", "a.frag", "#version 120\nvoid main() { gl_FragData[32] = vec4(1.0); }")]
    [InlineData("GL0307", "a.tesc", "#version 450\nlayout(vertices = 4) out;\nvoid main() { gl_out[4].gl_Position = vec4(1.0); }")]
    [InlineData("GL0308", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0) buffer B { float values[]; uint count; };\nvoid main() { count = uint(values.length()); }")]
    [InlineData("GL0308", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(location = 0) flat in int i;\nfloat weights[];\nvoid main() { weights[1] = 1.0; o = vec4(weights[i]); }")]
    [InlineData("GL0001", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(location = 0) flat in int i;\nlayout(binding = 0) uniform sampler2D textures[];\nvoid main() { o = texture(textures[i], vec2(0.5)); }")]
    [InlineData("GL0402", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat sum(float values[]) { return values[0]; }\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0402", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nstruct S { float values[]; };\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0402", "a.comp", "#version 310 es\nlayout(local_size_x = 1) in;\nshared float values[];\nvoid main() { values[0] = 1.0; }")]
    [InlineData("GL0309", "a.frag", "#version 450\nlayout(location = 0) in vec4 c;\nlayout(location = 0) out vec4 o;\nvoid main() { c.x = 1.0; o = c; }")]
    [InlineData("GL0309", "a.vert", "#version 460\nvoid main() { gl_VertexIndex = 3; gl_Position = vec4(1.0); }")]
    [InlineData("GL0309", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { gl_FragCoord.x = 1.0; o = gl_FragCoord; }")]
    [InlineData("GL0309", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform U { vec4 tint; };\nvoid main() { tint = vec4(1.0); o = tint; }")]
    [InlineData("GL0309", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0) readonly buffer B { float v; };\nvoid main() { v += 1.0; }")]
    [InlineData("GL0310", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0) writeonly buffer B { float v; } b;\nvoid main() { float x = b.v; b.v = x + 1.0; }")]
    [InlineData("GL0311", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(location = 0) flat in int count;\nvoid main() { o = vec4(0.0); if (count) o = vec4(1.0); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform sampler2D t;\nvoid main() { sampler2D s = t; o = texture(s, vec2(0.5)); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { void v; o = vec4(1.0); }")]
    [InlineData("GL0404", "a.tese", "#version 450\nlayout(triangles) in;\nlayout(location = 0) in vec2 uvs[16];\nvoid main() { gl_Position = vec4(uvs[0], 0.0, 1.0); }")]
    [InlineData("GL0101", "a.vert", "#version 330\nout flat vec4 v;\nvoid main() { v = vec4(1.0); gl_Position = v; }")]
    [InlineData("GL0101", "a.vert", "#version 450\nsmooth flat out vec4 v;\nvoid main() { v = vec4(1.0); gl_Position = v; }")]
    [InlineData("GL0101", "a.tese", "#version 450\nlayout(triangles) in;\nflat patch in vec4 p;\nvoid main() { gl_Position = p; }")]
    [InlineData("GL0101", "a.vert", "#version 450\nlayout(location = 0) flat in vec4 p;\nvoid main() { gl_Position = p; }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) flat out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nstruct S { layout(location = 1) float f; };\nvoid main() { S s; s.f = 1.0; o = vec4(s.f); }")]
    [InlineData("GL0101", "a.vert", "#version 450\nflat out Block { vec4 c; } b;\nvoid main() { b.c = vec4(1.0); gl_Position = b.c; }")]
    [InlineData("GL0101", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nvolatile float v;\nvoid main() { v = 1.0; }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) invariant in vec4 c;\nlayout(location = 0) out vec4 o;\nvoid main() { o = c; }")]
    [InlineData("GL0101", "a.frag", "#version 450\nuniform vec4 u;\ninvariant u;\nlayout(location = 0) out vec4 o;\nvoid main() { o = u; }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nstruct S { vec4 u; struct T { int a; } t; };\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform U { sampler2D s; };\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0403", "a.geom", "#version 450\nlayout(points) in;\nlayout(points, max_vertices = 1) out;\nlayout(location = 0) in vec4 c;\nvoid main() { gl_Position = c; EmitVertex(); }")]
    [InlineData("GL0403", "a.tese", "#version 450\nlayout(triangles) in;\nlayout(location = 0) in vec2 uv;\nvoid main() { gl_Position = vec4(uv, 0.0, 1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) in int i;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(i); }")]
    [InlineData("GL0403", "a.frag", "#version 300 es\nprecision lowp uint;\nprecision mediump float;\nout vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0403", "a.frag", "#version 300 es\nprecision mediump vec4;\nprecision mediump float;\nout vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0002", "a.vert", "#version 450\nlayout(location = 0) in VertexIn { vec4 p; } vin;\nvoid main() { gl_Position = vin.p; }")]
    [InlineData("GL0002", "a.frag", "#version 450\nlayout(location = 0) out FragOut { vec4 c; } fout;\nvoid main() { fout.c = vec4(1.0); }")]
    [InlineData("GL0305", "a.frag", "#version 450\nlayout(binding = 0) uniform texture2D t;\nlayout(binding = 1) uniform sampler s;\nlayout(location = 0) out vec4 o;\nvec4 f(sampler2D x) { return texture(x, vec2(0.5)); }\nvoid main() { o = f(sampler2D(t, s)) + texture(sampler2D(t, s), vec2(0.5)); }")]
    [InlineData("GL0315", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { int i = 5000000000; o = vec4(i); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(binding = 0, offset = 0) uniform atomic_uint c;\nlayout(location = 0) out vec4 o;\nvoid main() { c + c; o = vec4(1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(binding = 0) uniform sampler2D s1;\nlayout(binding = 1) uniform sampler2D s2;\nlayout(location = 0) out vec4 o;\nvoid main() { if (s1 == s2) { o = vec4(1.0); } }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid f() { }\nvoid main() { bool b = o.x > 0.0; b ? f() : 4; o = vec4(1.0); }")]
    [InlineData("GL0302", "a.vert", "#version 300 es\nuniform highp sampler2D s;\nvoid main() { ivec2 n = textureSize(s); gl_Position = vec4(n, 0.0, 1.0); }")]
    [InlineData("CF0410", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nint a = true;\nvoid main() { o = vec4(a); }")]
    [InlineData("GL0316", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { vec4 v[2] = vec4[2](vec4(0.0), vec4(1.0)); o = v[1.0]; }")]
    [InlineData("GL0316", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform float f;\nvoid main() { switch (f) { default: break; } o = vec4(1.0); }")]
    [InlineData("GL0316", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int i;\nvoid main() { switch (i) { case 2.0: break; } o = vec4(1.0); }")]
    [InlineData("GL0317", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int n;\nvoid main() { float a[n]; o = vec4(a[0]); }")]
    [InlineData("GL0317", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat a[(2, 3)];\nvoid main() { o = vec4(a[0]); }")]
    [InlineData("GL0317", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int n;\nconst int c = n;\nvoid main() { o = vec4(c); }")]
    [InlineData("GL0317", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int n;\nvoid main() { const int c = n; float a[c]; o = vec4(a[0]); }")]
    [InlineData("GL0001", "a.frag", "#version 330\nlayout(location = 0) out vec4 o;\nuniform int n;\nvoid main() { const int c = n; o = vec4(c); }")]
    [InlineData("GL0001", "a.frag", "#version 100\nprecision mediump float;\nfloat f = gl_FrontFacing ? 1.0 : 0.0;\nvoid main() { gl_FragColor = vec4(f); }")]
    [InlineData("GL0317", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int n;\nconst struct S { int a; } s = S(n);\nvoid main() { o = vec4(s.a); }")]
    [InlineData("GL0317", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int n;\nuniform int i;\nvoid main() { switch (i) { case n: break; } o = vec4(1.0); }")]
    [InlineData("GL0317", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(constant_id = 1) const float k = 0.5;\nlayout(location = 4) uniform float u = k;\nvoid main() { o = vec4(u); }")]
    [InlineData("GL0318", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nconst int a;\nvoid main() { o = vec4(a); }")]
    [InlineData("GL0318", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { const float f; o = vec4(f); }")]
    [InlineData("GL0319", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { int a[] = int[](); o = vec4(a.length()); }")]
    [InlineData("GL0319", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float a[2] = float[2](1.0, 2.0, 3.0); o = vec4(a[0]); }")]
    [InlineData("GL0001", "a.frag", "#version 100\nprecision mediump float;\nvoid main() { float a[2]; a[0] = float[2](1.0, 2.0)[0]; gl_FragColor = vec4(a[0]); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nconst int f() { return 1; }\nvoid main() { o = vec4(f()); }")]
    [InlineData("GL0001", "a.frag", "#version 100\nprecision mediump float;\nuniform Blk { float f; };\nvoid main() { gl_FragColor = vec4(f); }")]
    [InlineData("GL0001", "a.frag", "#version 410\nlayout(location = 0) out vec4 o;\nvoid main() { float a[2] = { 1.0, 2.0 }; o = vec4(a[0]); }")]
    [InlineData("GL0001", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(1.0); }\n;")]
    [InlineData("GL0001", "a.frag", "#version 100\nprecision mediump float;\nvoid main() { float a[2] = float[2](1.0, 2.0); gl_FragColor = vec4(a[0]); }")]
    [InlineData("GL0320", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid g(float a, double b) { }\nvoid g(double a, float b) { }\nvoid main() { float f = 1.0; g(f, f); o = vec4(f); }")]
    [InlineData("GL0321", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int c;\nvoid main() { o = vec4(0.0); switch (c) { case 0: if (o.x > 0.5) { case 1: o.y = 1.0; } break; } }")]
    [InlineData("GL0321", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int c;\nvoid main() { o = vec4(0.0); switch (c) { case 0: break; case 0: break; } }")]
    [InlineData("GL0321", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int c;\nvoid main() { o = vec4(0.0); switch (c) { o.x = 1.0; case 0: break; } }")]
    [InlineData("GL0321", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\nuniform int c;\nvoid main() { o = vec4(0.0); switch (c) { case 0: break; default: int x; } }")]
    [InlineData("GL0321", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(0.0); break; }")]
    [InlineData("GL0001", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(0.0); [[unroll]] for (int i = 0; i < 4; i++) { o.x += 1.0; } }")]
    [InlineData("GL0304", "a.frag", "#version 450\n#extension GL_EXT_control_flow_attributes : enable\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(0.0); [[dependency_length(0)]] for (int i = 0; i < 4; i++) { o.x += 1.0; } }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat f(flat float x) { return x; }\nvoid main() { o = vec4(f(1.0)); }")]
    [InlineData("GL0101", "a.frag", "#version 450\n#extension GL_EXT_nonuniform_qualifier : enable\nlayout(location = 0) out vec4 o;\nlayout(location = 1) in struct S { float a; nonuniformEXT float b; } s;\nvoid main() { o = vec4(s.a); }")]
    [InlineData("CF0310", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { vec5 c = vec4(1.0); o = vec4(1.0); }")]
    [InlineData("GL0315", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { int h = 0x; o = vec4(h); }")]
    [InlineData("GL0101", "a.vert", "#version 450\ninvariant missingName;\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0309", "a.frag", "#version 330 compatibility\nvoid main() { gl_FragColor = vec4(1.0); gl_FragData[1] = vec4(0.0); }")]
    [InlineData("GL0104", "a.tesc", "#version 450\nvoid main() { }")]
    [InlineData("GL0104", "a.geom", "#version 450\nlayout(triangles) in;\nvoid main() { }")]
    [InlineData("CF0310", "a.frag", "#version 140\nuniform sampler2DMS s;\nvoid main() { gl_FragColor = vec4(1.0); }")]
    [InlineData("GL0401", "a.frag", "#version 400\nstruct S { int precise; };\nvoid main() { gl_FragColor = vec4(1.0); }")]
    [InlineData("GL0003", "a.vert", "#version 435\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0324", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\ncoopmat<float16_t, gl_ScopeSubgroup, 8, gl_MatrixUseA> a;\nvoid main() { }")]
    [InlineData("GL0324", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nint rows = 8;\ncoopmat<float16_t, gl_ScopeSubgroup, rows, 8, gl_MatrixUseA> b;\nvoid main() { }")]
    [InlineData("GL0324", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nfloat<16> c;\nvoid main() { }")]
    [InlineData("GL0403", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nshared coopmat<float16_t, gl_ScopeSubgroup, 16, 16, gl_MatrixUseA> s;\nvoid main() { }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nvoid main() { coopmat<float16_t, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> a; coopmat<float16_t, gl_ScopeSubgroup, 8, 8, gl_MatrixUseA> b; coopmat<float, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> f; a + b; }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nvoid main() { coopmat<float16_t, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> a; coopmat<float16_t, gl_ScopeSubgroup, 8, 8, gl_MatrixUseA> b; coopmat<float, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> f; a * 2.0; }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nvoid main() { coopmat<float16_t, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> a; coopmat<float16_t, gl_ScopeSubgroup, 8, 8, gl_MatrixUseA> b; coopmat<float, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> f; a.x; }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nvoid main() { coopmat<float16_t, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> a; coopmat<float16_t, gl_ScopeSubgroup, 8, 8, gl_MatrixUseA> b; coopmat<float, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> f; transpose(a); }")]
    [InlineData("CF0410", "a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nvoid main() { coopmat<float16_t, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> a; coopmat<float16_t, gl_ScopeSubgroup, 8, 8, gl_MatrixUseA> b; coopmat<float, gl_ScopeSubgroup, 16, 8, gl_MatrixUseA> f; f = a; }")]
    [InlineData("GL0302", "a.comp", "#version 450\n#extension GL_EXT_long_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 1) in;\nvoid main() { vector<uint, 5> vu; int i = 1; vu = max(vu, i); }")]
    [InlineData("GL0302", "a.comp", "#version 450\n#extension GL_EXT_long_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 1) in;\nvoid main() { vector<double, 5> vd; vd = sin(vd); }")]
    [InlineData("GL0302", "a.comp", "#version 450\n#extension GL_EXT_long_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 1) in;\nvoid main() { vector<uint, 5> vu; vu = max(vu, vector<uint, 6>(0)); }")]
    [InlineData("GL0307", "a.comp", "#version 450\n#extension GL_EXT_long_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 1) in;\nvoid main() { vector<float, 8> v = vector<float, 8>(1.0); float f = v[8]; }")]
    [InlineData("GL0324", "a.comp", "#version 450\n#extension GL_EXT_long_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 1) in;\nvoid main() { vector<float>(0); }")]
    [InlineData("GL0403", "a.frag", "#version 450\n#extension GL_EXT_bfloat16 : require\nlayout(location = 0) in bfloat16_t x;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0403", "a.comp", "#version 450\n#extension GL_EXT_float_e2m1 : require\nlayout(local_size_x = 1) in;\nshared floate2m1_t s;\nvoid main() { }")]
    [InlineData("GL0303", "a.comp", "#version 450\n#extension GL_EXT_float_e2m1 : require\nlayout(local_size_x = 1) in;\nvoid main() { float f = 1.0; floate2m1_t v = floate2m1_t(f); }")]
    [InlineData("GL0302", "a.comp", "#version 450\n#extension GL_EXT_bfloat16 : require\nlayout(local_size_x = 1) in;\nvoid main() { bfloat16_t b = bfloat16_t(1.0); bool c = bool(b); }")]
    [InlineData("GL0304", "a.comp", "#version 460 core\n#extension GL_ARM_tensors : enable\nlayout(local_size_x = 1) in;\nuniform tensorARM<int, 2> t;\nvoid main() { uint s = tensorSizeARM(t, 2); }")]
    [InlineData("GL0306", "a.comp", "#version 460 core\n#extension GL_ARM_tensors : enable\nlayout(local_size_x = 1) in;\nuniform tensorARM<int, 2> t;\nvoid main() { int i = 1; tensorWriteARM(t, uint[](0), i); }")]
    [InlineData("GL0306", "a.comp", "#version 460 core\n#extension GL_ARM_tensors : enable\nlayout(local_size_x = 1) in;\nuniform tensorARM<int, 2> t;\nvoid main() { float f = 1.0; tensorWriteARM(t, uint[](0, 0), f); }")]
    [InlineData("GL0324", "a.comp", "#version 460 core\n#extension GL_ARM_tensors : enable\nlayout(local_size_x = 1) in;\nuniform tensorARM<int, 2> t;\nuint f(tensorARM x) { return 0u; }\nvoid main() { }")]
    [InlineData("GL0301", "a.frag", "#version 450\n#extension GL_EXT_buffer_reference2 : enable\n#extension GL_EXT_shader_explicit_arithmetic_types_int64 : enable\nlayout(buffer_reference) buffer T1 { int x[]; };\nlayout(buffer_reference) buffer T2 { int x[2]; };\nlayout(location = 0) out vec4 o;\nvoid main() { T2 a; T2 b; bool c = a == b; o = vec4(1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 450\n#extension GL_EXT_buffer_reference2 : enable\n#extension GL_EXT_shader_explicit_arithmetic_types_int64 : enable\nlayout(buffer_reference) buffer T1 { int x[]; };\nlayout(buffer_reference) buffer T2 { int x[2]; };\nlayout(location = 0) out vec4 o;\nvoid main() { T2 a; a += a; o = vec4(1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 450\n#extension GL_EXT_buffer_reference2 : enable\n#extension GL_EXT_shader_explicit_arithmetic_types_int64 : enable\nlayout(buffer_reference) buffer T1 { int x[]; };\nlayout(buffer_reference) buffer T2 { int x[2]; };\nlayout(location = 0) out vec4 o;\nvoid main() { T1 a; T1 b = a + 1; o = vec4(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\n#extension GL_EXT_buffer_reference2 : enable\n#extension GL_EXT_shader_explicit_arithmetic_types_int64 : enable\nlayout(buffer_reference) buffer T1 { int x[]; };\nlayout(buffer_reference) buffer T2 { int x[2]; };\nlayout(location = 0) out vec4 o;\nvoid main() { T2 a; const T2 b = a; o = vec4(1.0); }")]
    [InlineData("GL0001", "a.frag", "#version 450 core\n#extension GL_EXT_shader_16bit_storage : enable\nlayout(std430, binding = 0) buffer B { float16_t r[2]; } b;\nstruct S { float16_t x; float y; };\nlayout(location = 0) out vec4 o;\nvoid main() { b.r = b.r; o = vec4(1.0); }")]
    [InlineData("GL0001", "a.frag", "#version 450 core\n#extension GL_EXT_shader_16bit_storage : enable\nlayout(std430, binding = 0) buffer B { float16_t r[2]; } b;\nstruct S { float16_t x; float y; };\nlayout(location = 0) out vec4 o;\nvoid main() { f16vec2[1](f16vec2(vec2(1.0))); o = vec4(1.0); }")]
    [InlineData("GL0002", "a.rchit", "#version 460\n#extension GL_EXT_ray_tracing : enable\nlayout(binding = 0) uniform accelerationStructureEXT as;\nvoid main() { terminateRayEXT; }")]
    [InlineData("GL0103", "a.rgen", "#version 460\n#extension GL_EXT_ray_tracing : enable\nlayout(binding = 0) uniform accelerationStructureEXT as;\nlayout(location = 2) rayPayloadEXT vec4 p0;\nlayout(location = 2) rayPayloadEXT vec4 p1;\nvoid main() { }")]
    [InlineData("GL0306", "a.rgen", "#version 460\n#extension GL_EXT_ray_tracing : enable\nlayout(binding = 0) uniform accelerationStructureEXT as;\nlayout(location = 1) rayPayloadEXT vec4 p;\nvoid main() { traceRayEXT(as, 0u, 0xFFu, 0u, 0u, 0u, vec3(0.0), 0.0, vec3(1.0), 1.0, 0); }")]
    [InlineData("GL0306", "a.frag", "#version 450\n#extension GL_EXT_debug_printf : enable\nlayout(location = 0) out vec4 o;\nvoid main() { int i = 4; debugPrintfEXT(i); o = vec4(1.0); }")]
    [InlineData("GL0315", "a.frag", "#version 450\n#extension GL_EXT_debug_printf : enable\nlayout(location = 0) out vec4 o;\nvoid main() { debugPrintfEXT(\"\\8\"); o = vec4(1.0); }")]
    [InlineData("GL0302", "a.frag", "#version 450\n#extension GL_EXT_debug_printf : enable\nlayout(location = 0) out vec4 o;\nvoid main() { uint u = uint(\"foo\"); o = vec4(1.0); }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\n#extension GL_EXT_cooperative_matrix_maintenance1 : enable\nlayout(local_size_x = 64) in;\nfloat16_t combine(const in float16_t a, const in float16_t b) { return a + b; }\nvoid main() { coopmat<float16_t, gl_ScopeWorkgroup, 16, 16, gl_MatrixUseA> A; coopmat<float16_t, gl_ScopeWorkgroup, 16, 16, gl_MatrixUseAccumulator> Acc; coopMatTransposeEXT(Acc, A); }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\n#extension GL_EXT_cooperative_matrix_maintenance1 : enable\nlayout(local_size_x = 64) in;\nfloat16_t combine(const in float16_t a, const in float16_t b) { return a + b; }\nvoid main() { coopmat<float16_t, gl_ScopeWorkgroup, 16, 16, gl_MatrixUseA> A; coopmat<float16_t, gl_ScopeWorkgroup, 16, 16, gl_MatrixUseAccumulator> Acc; coopMatReduceEXT(Acc, Acc, 5, combine); }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\n#extension GL_EXT_cooperative_matrix_maintenance1 : enable\nlayout(local_size_x = 64) in;\nfloat16_t combine(const in float16_t a, const in float16_t b) { return a + b; }\nvoid main() { coopmat<float16_t, gl_ScopeWorkgroup, 16, 16, gl_MatrixUseA> A; coopmat<float16_t, gl_ScopeWorkgroup, 16, 16, gl_MatrixUseAccumulator> Acc; int m = 1; coopMatReduceEXT(Acc, Acc, m, combine); }")]
    [InlineData("GL0001", "a.comp", "#version 450 core\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer Block { float x[]; } block;\nvoid main() { uint64_t i = 0; float y = block.x[i]; }")]
    [InlineData("GL0001", "a.comp", "#version 450 core\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer Block { float x[]; } block;\nvoid main() { int64_t n = block.x.length64(); }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\n#extension GL_NV_cooperative_matrix2 : enable\n#extension GL_EXT_buffer_reference : enable\nlayout(local_size_x = 64) in;\nbuffer BufType { float16_t x[]; } Buf;\nlayout(buffer_reference, std430, buffer_reference_align = 2) buffer fp16Buf { float16_t f; };\nfloat16_t decode(in fp16Buf b, const in uint32_t c[2], const in uint32_t d[2]) { return b.f; }\nvoid main() { coopmat<float16_t, gl_ScopeWorkgroup, 64, 32, gl_MatrixUseAccumulator> A; tensorLayoutNV<2> t = createTensorLayoutNV(2); coopMatLoadTensorNV(A, Buf.x, 0, t, decode); }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\n#extension GL_NV_cooperative_matrix2 : enable\n#extension GL_EXT_buffer_reference : enable\nlayout(local_size_x = 64) in;\nbuffer BufType { float16_t x[]; } Buf;\nlayout(buffer_reference, std430, buffer_reference_align = 2) buffer fp16Buf { float16_t f; };\nfloat16_t decode(const in uint32_t b, const in uint32_t c[2], const in uint32_t d[2]) { return float16_t(0); }\nvoid main() { coopmat<float16_t, gl_ScopeWorkgroup, 64, 32, gl_MatrixUseAccumulator> A; tensorLayoutNV<2> t = createTensorLayoutNV(2); coopMatLoadTensorNV(A, Buf.x, 0, t, decode); }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\n#extension GL_NV_cooperative_matrix2 : enable\n#extension GL_EXT_buffer_reference : enable\nlayout(local_size_x = 64) in;\nbuffer BufType { float16_t x[]; } Buf;\nlayout(buffer_reference, std430, buffer_reference_align = 2) buffer fp16Buf { float16_t f; };\nvoid main() { coopmat<float16_t, gl_ScopeWorkgroup, 64, 32, gl_MatrixUseAccumulator> A; coopMatPerElementNV(A, A); }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nvoid main() { coopmat<float16_t, gl_ScopeSubgroup, 3, 4, gl_MatrixUseA> a; coopmat<float16_t, gl_ScopeSubgroup, 3, 4, gl_MatrixUseB> b; coopmat<float16_t, gl_ScopeSubgroup, 3, 3, gl_MatrixUseAccumulator> c; c = coopMatMulAdd(a, b, c); }")]
    [InlineData("GL0325", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nvoid main() { coopmat<float16_t, gl_ScopeSubgroup, 3, 4, gl_MatrixUseB> a; coopmat<float16_t, gl_ScopeSubgroup, 4, 3, gl_MatrixUseB> b; coopmat<float16_t, gl_ScopeSubgroup, 3, 3, gl_MatrixUseAccumulator> c; c = coopMatMulAdd(a, b, c); }")]
    [InlineData("GL0306", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_NV_cooperative_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer MatrixBuf { float16_t matrixData[]; } matrixBuf;\nvoid main() { uint M = 1; coopvecNV<float, 8> a; coopvecNV<float, 8> b; coopVecMatMulNV(b, a, gl_ComponentTypeFloat16NV, matrixBuf.matrixData, 0, gl_ComponentTypeFloat16NV, M, 1, 0, false, 16); }")]
    [InlineData("GL0306", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_NV_cooperative_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer MatrixBuf { float16_t matrixData[]; } matrixBuf;\nvoid main() { float local[4]; coopvecNV<float, 4> v; coopVecLoadNV(v, local, 0); }")]
    [InlineData("GL0307", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_NV_cooperative_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer MatrixBuf { float16_t matrixData[]; } matrixBuf;\nvoid main() { coopvecNV<float, 5> v; v[5] = 0.0; }")]
    [InlineData("CF0312", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_NV_cooperative_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer MatrixBuf { float16_t matrixData[]; } matrixBuf;\nvoid main() { coopvecNV<float, 5> v; v.x; }")]
    [InlineData("GL0302", "a.comp", "#version 450 core\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_NV_cooperative_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nlayout(binding = 0) buffer MatrixBuf { float16_t matrixData[]; } matrixBuf;\nvoid main() { coopvecNV<float16_t, 8> a; coopvecNV<float, 5> b; coopVecOuterProductAccumulateNV(a, b, matrixBuf.matrixData, 0, 0, gl_CooperativeVectorMatrixLayoutTrainingOptimalNV, gl_ComponentTypeFloat16NV); }")]
    [InlineData("GL0302", "a.comp", "#version 450 core\nlayout(local_size_x = 1) in;\nshared int counter;\nvoid main() { float f = atomicAdd(counter, 3.0); }")]
    [InlineData("GL0302", "a.comp", "#version 450 core\n#extension GL_EXT_float_e4m3 : require\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 1) in;\nvoid main() { floate4m3_t b; float16_t h; saturatedConvertEXT(b, b); }")]
    [InlineData("GL0302", "a.comp", "#version 450 core\n#extension GL_EXT_float_e4m3 : require\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 1) in;\nvoid main() { floate4m3_t b; float16_t h; saturatedConvertEXT(b, f16vec2(h)); }")]
    [InlineData("CF0002", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\n#define a b(\n#define b(x)\nb(a)\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0402", "a.mesh.glsl", "#version 450\n#extension GL_NV_mesh_shader : enable\nlayout(local_size_x = 32) in;\nlayout(max_vertices = 81) out;\nlayout(max_primitives = 32) out;\nlayout(triangles) out;\nperviewNV layout(location = 10) out vec4 x[][][];\nvoid main() { }")]
    [InlineData("GL0403", "a.mesh.glsl", "#version 450\n#extension GL_NV_mesh_shader : enable\nlayout(local_size_x = 32) in;\nlayout(max_vertices = 81) out;\nlayout(max_primitives = 32) out;\nlayout(triangles) out;\nperviewNV layout(location = 11) out vec4 x[][gl_MaxMeshViewCountNV - 1];\nvoid main() { }")]
    [InlineData("GL0403", "a.mesh.glsl", "#version 450\n#extension GL_NV_mesh_shader : enable\nlayout(local_size_x = 32) in;\nlayout(max_vertices = 81) out;\nlayout(max_primitives = 32) out;\nlayout(triangles) out;\nperviewNV layout(location = 12) out vec4 x[];\nvoid main() { }")]
    [InlineData("CF0314", "a.frag", "#version 430\nlayout(location = 0) out vec4 o;\nvoid main() { float r[][5][7]; float r[3][4][7]; o = vec4(1.0); }")]
    [InlineData("CF0314", "a.frag", "#version 430\nlayout(location = 0) out vec4 o;\nvoid main() { float r[][5]; int r[3][5]; o = vec4(1.0); }")]
    [InlineData("GL0103", "a.geom", "#version 450\nlayout(triangles) in;\nlayout(points, max_vertices = 1) out;\nin vec4 color[4];\nvoid main() { EmitVertex(); }")]
    [InlineData("GL0103", "a.tesc", "#version 450\nlayout(vertices = 4) out;\nout int outb[5];\nvoid main() { }")]
    [InlineData("GL0104", "a.comp", "#version 450\n#extension GL_KHR_compute_shader_derivatives : require\nlayout(local_size_x = 2, local_size_y = 2) in;\nvoid main() { }\n")]
    [InlineData("GL0104", "a.comp", "#version 450\n#extension GL_KHR_compute_shader_derivatives : require\nlayout(local_size_x = 2, local_size_y = 2) in;\nvoid main() { float x = dFdx(1.0); }\n")]
    [InlineData("GL0104", "a.comp", "#version 450\n#extension GL_KHR_compute_shader_derivatives : require\nlayout(local_size_x = 3, local_size_y = 2) in;\nlayout(derivative_group_quadsKHR) in;\nvoid main() { }\n")]
    [InlineData("GL0001", "a.mesh.glsl", "#version 450\n#extension GL_EXT_mesh_shader : enable\n#extension GL_NV_compute_shader_derivatives : require\nlayout(local_size_x = 4) in;\nlayout(derivative_group_linearNV) in;\nlayout(max_vertices = 3) out;\nlayout(max_primitives = 1) out;\nlayout(triangles) out;\nvoid main() { float x = dFdx(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 460\n#extension GL_EXT_shader_tile_image : require\nlayout(location = 0) out vec4 o;\nlayout(location = 1, set = 0) tileImageEXT highp attachmentEXT c;\nvoid main() { o = colorAttachmentReadEXT(c); }")]
    [InlineData("GL0104", "a.frag", "#version 460\n#extension GL_EXT_shader_tile_image : require\nlayout(location = 0) out vec4 o;\ntileImageEXT highp attachmentEXT c;\nvoid main() { o = colorAttachmentReadEXT(c); }")]
    [InlineData("CF0312", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { vec3 a[5]; float y = a.y; o = vec4(y); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat a[5];\nvoid main() { a--; o = vec4(1.0); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float f[-2]; o = vec4(1.0); }")]
    [InlineData("GL0403", "a.rgen", "#version 460\n#extension GL_EXT_ray_tracing : enable\nlayout(binding = 0) uniform accelerationStructureEXT c;\nvoid main() { accelerationStructureEXT a = c; }")]
    [InlineData("GL0306", "a.comp", "#version 460 core\n#extension GL_ARM_tensors : enable\nlayout(local_size_x = 1) in;\nreadonly layout(set = 0, binding = 1) uniform tensorARM<int, 2> ro;\nvoid main() { int d = 4; tensorWriteARM(ro, uint[](0, 0), d); }")]
    [InlineData("GL0103", "a.vert", "#version 430 core\n#extension GL_ARB_enhanced_layouts : require\nlayout(xfb_offset = 0) out vec4 xfb[];\nvoid main() { xfb[0] = vec4(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(location = 12) in bName2 { float f; layout(location = 13) float g; } bInst2[3];\nvoid main() { o = vec4(bInst2[0].f); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(constant_id = 222) const int arraySize = 4;\nvoid main() { int a1[arraySize]; int a2[arraySize] = a1; o = vec4(1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(constant_id = 222) const int arraySize = 4;\nvoid main() { int a1[arraySize]; int a2[arraySize]; a1 = a2; o = vec4(1.0); }")]
    [InlineData("GL0403", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nvoid main() { float fa[] = { }; }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat gu[];\nvoid main() { float local[] = gu; o = vec4(1.0); }")]
    [InlineData("GL0406", "a.vert", "#version 120\nvarying vec4 gl_TexCoord[35];\nvoid main() { gl_Position = vec4(1.0); }")]
    [InlineData("GL0402", "a.frag", "#version 420\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform atomic_uint a[];\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0403", "a.comp", "#version 460\n#extension GL_EXT_ray_query : enable\nlayout(local_size_x = 1) in;\nvoid main() { rayQueryEXT foo; rayQueryEXT bar = foo; }")]
    [InlineData("GL0323", "a.frag", "#version 450\n#extension GL_EXT_function_control_attributes : enable\nlayout(location = 0) out vec4 o;\n[[inline]] int f(int i);\n[[noinline]] int f(int i) { return i; }\nvoid main() { o = vec4(f(1)); }")]
    [InlineData("GL0001", "a.vert", "#version 300 es\nuniform mediump sampler2DArrayShadow s;\nvoid main() { gl_Position = vec4(textureOffset(s, vec4(0), ivec2(0))); }")]
    [InlineData("CF0410", "a.frag", "#version 430\nlayout(location = 0) out vec4 o;\nvoid main() { float a[5][7]; float r[7]; float[](a[0], a[1], r, a[3]); o = vec4(1.0); }")]
    [InlineData("CF0410", "a.frag", "#version 430\nlayout(location = 0) out vec4 o;\nvoid main() { float a[5][7]; float r[7]; float[4][7][4](a[0], a[1], r, a[3]); o = vec4(1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 430\nlayout(location = 0) out vec4 o;\nfloat g4[4][7];\nfloat g5[5][7];\nvoid main() { if (g4 == g5) { o = vec4(1.0); } }")]
    [InlineData("CF0410", "a.frag", "#version 430\nlayout(location = 0) out vec4 o;\nvoid main() { vec4 bad[3][] = { vec4[2](vec4(4.0), vec4(2.0)), vec4[3](vec4(4.0), vec4(2.0), vec4(5.0)), vec4[2](vec4(4.0), vec4(2.0)) }; o = bad[0][0]; }")]
    [InlineData("GL0103", "a.frag", "#version 460\n#extension GL_EXT_shader_tile_image : require\nprecision highp float;\nlayout(location = 0) tileImageEXT highp attachmentEXT c[2];\nlayout(location = 1) tileImageEXT highp attachmentEXT d;\nlayout(location = 0) out vec4 o;\nvoid main() { o = colorAttachmentReadEXT(d); }")]
    [InlineData("GL0103", "a.frag", "#version 460\n#extension GL_EXT_shader_tile_image : require\nprecision highp float;\nlayout(location = 0) tileImageEXT highp iattachmentEXT c;\nlayout(location = 0) out highp vec4 o;\nvoid main() { o = vec4(colorAttachmentReadEXT(c)); }")]
    [InlineData("GL0306", "a.rgen", "#version 460\n#extension GL_EXT_ray_tracing : enable\n#extension GL_NV_ray_tracing_motion_blur : enable\nlayout(binding = 0, set = 0) uniform accelerationStructureEXT acc;\nlayout(location = 0) rayPayloadEXT vec4 p;\nvoid main() { traceRayMotionNV(acc, 0u, 0u, 0u, 0u, 0u, vec3(0.5), 0.5f, vec3(1.0), 0.75f, 0.5, 1); }")]
    [InlineData("CF0002", "a.vert", "#version 450\n#define P(a, b) a##b\nint a = P(11, 12);\nvoid main() { gl_Position = vec4(1.0); }")]
    [InlineData("CF0002", "a.vert", "#version 450\n#define P(a, b) a##b\nvoid main() { int e = 16; bool f = e P(>, !) 5; gl_Position = vec4(1.0); }")]
    [InlineData("GL0308", "a.frag", "#version 450\n#extension GL_EXT_buffer_reference : enable\nlayout(location = 0) out vec4 o;\nlayout(buffer_reference) buffer T6 { int x[]; };\nvoid main() { T6 b; int n = b.x.length(); o = vec4(n); }")]
    [InlineData("GL0301", "a.frag", "#version 450\n#extension GL_EXT_buffer_reference : enable\nlayout(location = 0) out vec4 o;\nlayout(buffer_reference) buffer T1 { int x; };\nlayout(buffer_reference) buffer T2 { int x; };\nlayout(binding = 0) buffer B { T1 b1; } b4;\nvoid main() { T2 x1 = b4.b1; o = vec4(1.0); }")]
    [InlineData("GL0301", "a.vert", "#version 460\n#extension GL_NV_explicit_typecast : enable\nstruct S { uint dummy; };\nvoid main() { S temp; uint v = 1U; temp = (S) v; gl_Position = vec4(1.0); }")]
    [InlineData("GL0309", "a.mesh.glsl", "#version 460\n#extension GL_EXT_mesh_shader : enable\nlayout(local_size_x = 1) in;\nlayout(max_vertices = 3) out;\nlayout(max_primitives = 1) out;\nlayout(triangles) out;\nstruct T { int a; };\ntaskPayloadSharedEXT T mytask;\nvoid main() { atomicAdd(mytask.a, 1); }")]
    [InlineData("GL0101", "a.frag", "#version 450\n#extension GL_EXT_buffer_reference : enable\nlayout(location = 0) out vec4 o;\nlayout(buffer_reference, std430) buffer T3 { int h; };\nflat in T3 k;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0307", "a.vert", "#version 450\nlayout(constant_id = 1) const uint b = 2;\nlayout(location = 0) out uint o;\nvoid main() { uint arr2[b]; o = arr2[6]; }")]
    [InlineData("GL0406", "a.comp", "#version 460\n#extension GL_EXT_ray_query : enable\n#extension GL_EXT_opacity_micromap_ray_query_mode : enable\nlayout(local_size_x = 1) in;\nlayout(constant_id = 2) const bool gl_EnableOpacityMicromapEXT = false;\nvoid main() { }")]
    [InlineData("GL0406", "a.comp", "#version 460\n#extension GL_EXT_ray_query : enable\n#extension GL_EXT_opacity_micromap_ray_query_mode : enable\nlayout(local_size_x = 1) in;\nbool gl_EnableOpacityMicromapEXT = true;\nvoid main() { }")]
    [InlineData("CF0314", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { struct { float a; int b; } e = { 1.2, 2 }; struct { float a; int b; } e = { 1, 3 }; o = vec4(1.0); }")]
    [InlineData("GL0315", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float f = -1.#INF; o = vec4(f); }")]
    [InlineData("CF0002", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nint a = 1 # 2;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("CF0410", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nstruct { float a; int b; } e3 = { 1.2, 2, 3 };\nvoid main() { o = vec4(1.0); }")]
    [InlineData("CF0410", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvec4 b4[2] = vec4[2](vec4(0.0), mat2x2(1.0));\nvoid main() { o = b4[0]; }")]
    [InlineData("GL0303", "a.comp", "#version 460 core\n#extension GL_ARM_tensors : enable\nlayout(local_size_x = 1) in;\nuniform tensorARM<int, 2> t;\nvoid main() { int ret; uint operands = gl_TensorOperandsNonTemporalARM; tensorReadARM(t, uint[](0, 0), ret, operands, 1); }")]
    [InlineData("CF0002", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\n#define DEF_MAC\n#define DEF_DEFINED defined\n#if DEF_DEFINED DEF_MAC\n#endif\nvoid main() { o = vec4(1.0); }")]
    [InlineData("CF0002", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\n#define F(a, b) a + b\n#if F\n#endif\nvoid main() { o = vec4(1.0); }")]
    [InlineData("CF0002", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(1.0); }\n#if 1\n")]
    [InlineData("GL0322", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main(int a) { o = vec4(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 3) struct S { vec3 a; } s;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(s.a, 1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 320 es\nprecision mediump float;\nlayout(blend_support_multiply) struct T { int i; };\nout vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nint f(int a, void);\nvoid main() { o = vec4(1.0); }")]
    [InlineData("CF0001", "a.frag", "#version 310 es\nprecision mediump float;\nsample in vec4 c;\nout vec4 o;\n#extension GL_OES_shader_multisample_interpolation : enable\nvoid main() { o = c; }")]
    [InlineData("GL0401", "a.frag", "#version 430\nin vec4 gl_Color;\nlayout(location = 0) out vec4 o;\nvoid main() { o = gl_Color; }")]
    [InlineData("GL0001", "a.vert", "#version 430\nint a[gl_MaxTransformFeedbackBuffers];\nvoid main() { gl_Position = vec4(0.0); }")]
    [InlineData("GL0001", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(std430, binding = 0) uniform U { vec4 v; };\nvoid main() { o = v; }")]
    [InlineData("CF0312", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float a[3]; a.method(); o = vec4(1.0); }")]
    [InlineData("GL0101", "a.vert", "#version 300 es\nout vec3 v;\nvoid f() { v = vec3(1.0); }\ninvariant v;\nvoid main() { f(); gl_Position = vec4(0.0); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nstruct S { sampler2D t; } v;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0309", "a.frag", "#version 450\nlayout(early_fragment_tests) in;\nlayout(location = 0) out vec4 o;\nvoid main() { gl_FragDepth = 0.5; o = vec4(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 1) uniform U { layout(binding = 2) float f; } u;\nvoid main() { o = vec4(u.f); }")]
    [InlineData("CF0312", "a.frag", "#version 450\nlayout(binding = 0) uniform sampler2D s;\nlayout(location = 0) out vec4 o;\nvoid main() { vec2 v = s.rr; o = vec4(v, 0.0, 1.0); }")]
    [InlineData("GL0301", "a.frag", "#version 450\n#extension GL_EXT_debug_printf : enable\nlayout(location = 0) out vec4 o;\nvoid main() { \"o\" + \"k\"; o = vec4(1.0); }")]
    [InlineData("GL0002", "a.vert", "#version 450\nvoid main() { gl_Position = vec4(0.0); discard; }")]
    [InlineData("GL0003", "a.frag", "// comment\n#version 300 es\nprecision mediump float;\nout vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nin uniform vec4 v;\nvoid main() { o = v; }")]
    [InlineData("GL0101", "a.vert", "#version 440\nout Block { layout(location = 1, component = 1) mat2 m; } b;\nvoid main() { b.m = mat2(1.0); gl_Position = vec4(0.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(location = 1, depth_less) in float d;\nvoid main() { o = vec4(d); }")]
    [InlineData("GL0403", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\nvoid main() { mediump bvec2 b = bvec2(true); o = vec4(b.x); }")]
    [InlineData("GL0323", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat f(in float a);\nfloat f(out float a) { a = 1.0; return a; }\nvoid main() { float x; o = vec4(f(x)); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform U { in float a; };\nvoid main() { o = vec4(a); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform U { centroid float a; };\nvoid main() { o = vec4(a); }")]
    [InlineData("GL0306", "a.frag", "#version 450\nlayout(binding = 0) uniform texture3D t;\nlayout(binding = 1) uniform sampler s;\nlayout(location = 0) out vec4 o;\nvoid main() { o = texture(sampler2D(t, s), vec2(0.5)); }")]
    [InlineData("GL0306", "a.frag", "#version 450\nlayout(binding = 0) uniform texture2D t;\nlayout(binding = 1) uniform texture2D u;\nlayout(location = 0) out vec4 o;\nvoid main() { o = texture(sampler2D(t, u), vec2(0.5)); }")]
    [InlineData("GL0001", "a.frag", "#version 450\nlayout(binding = 0) uniform texture2D t;\nlayout(location = 0) out vec4 o;\nvoid main() { o = texture(sampler2D(t), vec2(0.5)); }")]
    [InlineData("GL0323", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat f(float a);\nint f(float a) { return 1; }\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0323", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\nfloat f(mediump float a);\nfloat f(highp float a) { return a; }\nvoid main() { o = vec4(f(1.0)); }")]
    [InlineData("GL0322", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nint main() { o = vec4(1.0); return 0; }")]
    [InlineData("GL0101", "a.tesc", "#version 450\nlayout(vertices = 3) out;\npatch in vec4 p;\nvoid main() { }")]
    [InlineData("GL0101", "a.vert", "#version 450\ninvariant out Block { vec4 v; } b;\nvoid main() { b.v = vec4(0.0); gl_Position = vec4(0.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nstruct S { bool b; };\nlayout(location = 0) in S s;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat f(invariant float x) { return x; }\nvoid main() { o = vec4(f(1.0)); }")]
    [InlineData("GL0101", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nbuffer float b;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0101", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nshared vec4 s = vec4(1.0);\nvoid main() { }")]
    [InlineData("GL0002", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nin vec4 a;\nvoid main() { }")]
    [InlineData("GL0104", "a.comp", "#version 450\nvoid f() { uvec3 s = gl_WorkGroupSize; }\nlayout(local_size_x = 64) in;\nvoid main() { f(); }")]
    [InlineData("GL0403", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid f(out sampler2D s) { }\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0403", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nprecision lowp atomic_uint;\nvoid main() { }")]
    [InlineData("GL0321", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nuniform int c;\nvoid main() { o = vec4(0.0); switch (c) { case 0: continue; } }")]
    [InlineData("GL0320", "a.vert", "#version 130\nvoid g(float a, int b) { }\nvoid g(int a, float b) { }\nvoid main() { int i = 1; g(i, i); gl_Position = vec4(0.0); }")]
    [InlineData("GL0003", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\n#if UNDEF\n#endif\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0003", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\n#error not supported\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0003", "a.frag", "#version 450\n#extension all : enable\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0003", "a.frag", "#version 450\n#extension GL_EXT_ray_query : on\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0315", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { int i = 08; o = vec4(i); }")]
    [InlineData("GL0315", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { uint i = 0x1FFFFFFFFu; o = vec4(i); }")]
    [InlineData("GL0314", "a.comp", "#version 450\n#extension GL_KHR_memory_scope_semantics : require\nlayout(local_size_x = 1) in;\nlayout(binding = 0) buffer B { uint x; } b;\nvoid main() { atomicStore(b.x, 1u, gl_ScopeDevice, gl_StorageSemanticsBuffer, gl_SemanticsAcquire); }")]
    [InlineData("GL0314", "a.comp", "#version 450\n#extension GL_KHR_memory_scope_semantics : require\nlayout(local_size_x = 1) in;\nlayout(binding = 0) buffer B { uint x; } b;\nvoid main() { atomicAdd(b.x, 1u, gl_ScopeDevice, gl_StorageSemanticsBuffer, gl_SemanticsRelaxed); }")]
    [InlineData("GL0313", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0, rgba8) uniform writeonly image2D t;\nlayout(binding = 1) buffer B { vec4 v; };\nvoid main() { v = imageLoad(t, ivec2(0)); }")]
    [InlineData("GL0313", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0, rgba8) uniform coherent writeonly image2D t;\nvoid store(writeonly image2D i) { imageStore(i, ivec2(0), vec4(1.0)); }\nvoid main() { store(t); }")]
    [InlineData("GL0313", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0, rgba8) uniform readonly image2D t;\nlayout(binding = 1) buffer B { vec4 v; };\nvec4 load(readonly image2D i) { return imageLoad(i, ivec2(0)); }\nvoid main() { v = load(t); }")]
    [InlineData("GL0312", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float degrees = 90.0; o = vec4(degrees(1.0)); }")]
    [InlineData("GL0405", "a.frag", "#version 100\nprecision mediump float;\nfloat sin(float x) { return x; }\nvoid main() { gl_FragColor = vec4(sin(1.0)); }")]
    [InlineData("GL0405", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\nbool radians(bool x) { return x; }\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0405", "a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\nfloat tan;\nvoid main() { o = vec4(tan); }")]
    [InlineData("GL0401", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { float gl_scale = 1.0; o = vec4(gl_scale); }")]
    [InlineData("GL0401", "a.vert", "#version 140\nout vec4 gl_Position;\nvoid main() { }")]
    [InlineData("GL0401", "a.frag", "#version 100\nprecision mediump float;\nfloat a__b;\nvoid main() { gl_FragColor = vec4(a__b); }")]
    [InlineData("GL0001", "a.comp", "#version 450\nlayout(local_size_x = 1) in;\nlayout(binding = 0) buffer B { float v; };\nvoid main() { atomicAdd(v, 1.0); }")]
    [InlineData("GL0001", "a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform texture2D t;\nvoid main() { o = vec4(textureSize(t, 0), 0.0, 1.0); }")]
    [InlineData("GL0001", "a.comp", "#version 450\n#extension GL_EXT_shader_16bit_storage : enable\nlayout(local_size_x = 1) in;\nlayout(binding = 0) buffer B { float16_t h; float f; };\nvoid main() { float16_t local = h; f = float(local); }")]
    [InlineData("GL0001", "a.frag", "#version 330\nout vec4 o;\nvoid main() { float a[2][3]; o = vec4(a[0][0]); }")]
    [InlineData("GL0001", "a.vert", "#version 120\nin vec4 position;\nvoid main() { gl_Position = position; }")]
    [InlineData("GL0001", "a.vert", "#version 300 es\nin vec4 positions[2];\nvoid main() { gl_Position = positions[0]; }")]
    [InlineData("GL0001", "a.frag", "#version 330\nuniform sampler2D s[4];\nuniform int i;\nout vec4 o;\nvoid main() { o = texture(s[i], vec2(0.5)); }")]
    [InlineData("GL0001", "a.frag", "#version 330\nuniform sampler2D s;\nout vec4 o;\nvoid main() { o = textureGather(s, vec2(0.5)); }")]
    [InlineData("GL0001", "a.frag", "#version 430\nuniform sampler2DMS s;\nout vec4 o;\nvoid main() { o = vec4(textureSamples(s)); }")]
    [InlineData("GL0001", "a.geom", "#version 310 es\n#extension GL_EXT_geometry_shader : enable\nlayout(points) in;\nlayout(points, max_vertices = 1) out;\nvoid main() { gl_PointSize = 1.0; EmitVertex(); }")]
    [InlineData("GL0001", "a.frag", "#version 300 es\n#extension GL_OES_EGL_image_external : enable\nprecision mediump float;\nuniform samplerExternalOES s;\nout vec4 o;\nvoid main() { o = vec4(1.0); }")]
    [InlineData("GL0001", "a.frag", "#version 330\n#define ONE \\\n 1.0\nout vec4 o;\nvoid main() { o = vec4(ONE); }")]
    public async Task 文法の規則の検査(string id, string fileName, string code)
    {
        string output = await AnalyzeAsync(fileName, code + "\n");
        Assert.Contains($"error {id}", output, StringComparison.Ordinal);
    }

    /// <summary>Vulkan 向けでは、ブロックの外の数の型の uniform の変数を GL0101 で伝える。OpenGL 向けでは伝えない。</summary>
    [Theory]
    [InlineData("vulkan1.0", true)]
    [InlineData("opengl", false)]
    public async Task Vulkanのブロックの外のuniform(string target, bool expected)
    {
        string config = Path.Combine(_workDirectory, "shaderlyn.yml");
        File.WriteAllText(config, $"version: 1\nenvironment: glsl\nglsl:\n  target: {target}\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), "#version 450\nlayout(location = 0) out vec4 o;\nlayout(location = 0) uniform vec4 v;\nvoid main() { o = v; }\n");
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.frag"), "--config", config, "--environment", "glsl"], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Equal(expected, output.ToString().Contains("error GL0101", StringComparison.Ordinal));
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

    /// <summary>
    /// その版に無い組み込みの関数と変数は GL0001、同じ版の別のステージにあるものは GL0002 で伝える。宣言されていない名前 (CF0310) とは言わない。
    /// </summary>
    [Theory]
    [InlineData("a.vert", "#version 120\nvoid main() { gl_Position = vec4(trunc(1.5)); }", "GL0001")]
    [InlineData("a.tese", "#version 450\nlayout(triangles) in;\nvoid main() { barrier(); gl_Position = vec4(1.0); }", "GL0002")]
    [InlineData("a.vert", "#version 310 es\nvoid main() { gl_ClipDistance[0] = 1.0; gl_Position = vec4(1.0); }", "GL0001")]
    [InlineData("a.frag", "#version 460\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(float(gl_VertexIndex)); }", "GL0002")]
    [InlineData("a.tesc", "#version 320 es\nlayout(vertices = 3) out;\nvoid main() { float d = gl_in[1].gl_ClipDistance[0]; gl_out[gl_InvocationID].gl_Position = vec4(d); }", "GL0001")]
    [InlineData("a.rint", "#version 460\n#extension GL_EXT_ray_tracing : enable\nvoid main() { float t = gl_HitTEXT; reportIntersectionEXT(t, 0u); }", "GL0001")]
    public async Task その構成に無い組み込みの関数と変数(string fileName, string code, string id)
    {
        string output = await AnalyzeAsync(fileName, code + "\n");
        Assert.Contains($"error {id}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CF0310", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// OpenGL 向けの <c>texture2D(…)</c> は組み込み関数で、ES の 3.00 には無い。Vulkan 向けでは型の名前なので報告しない。
    /// </summary>
    [Theory]
    [InlineData("opengl", true)]
    [InlineData("vulkan1.0", false)]
    public async Task 無くなった組み込み関数のtexture2D(string target, bool expected)
    {
        string config = Path.Combine(_workDirectory, "shaderlyn.yml");
        File.WriteAllText(config, $"version: 1\nenvironment: glsl\nglsl:\n  target: {target}\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), """
            #version 300 es
            precision mediump float;
            uniform sampler2D s;
            out vec4 o;
            void main() { o = texture2D(s, vec2(0.5)); }

            """);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.frag"), "--config", config, "--environment", "glsl"], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Equal(expected, output.ToString().Contains("組み込み関数 'texture2D' は", StringComparison.Ordinal));
    }

    /// <summary>
    /// OpenGL 向けの SPIR-V (<c>glsl: target: opengl</c> と <c>spirv</c>) には <c>gl_DepthRange</c> が無い (glslang の <c>-G</c>)。
    /// SPIR-V を作らない OpenGL にはある。
    /// </summary>
    [Theory]
    [InlineData("  spirv: \"1.0\"\n", true)]
    [InlineData("", false)]
    public async Task OpenGL向けのSPIRVの組み込み(string spirv, bool expected)
    {
        string config = Path.Combine(_workDirectory, "shaderlyn.yml");
        File.WriteAllText(config, $"version: 1\nenvironment: glsl\nglsl:\n  target: opengl\n{spirv}");
        File.WriteAllText(Path.Combine(_workDirectory, "a.vert"), """
            #version 450
            void main() { gl_Position = vec4(gl_DepthRange.near); }

            """);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.vert"), "--config", config, "--environment", "glsl"], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Equal(expected, output.ToString().Contains("組み込みの変数 'gl_DepthRange' は", StringComparison.Ordinal));
    }

    /// <summary>
    /// 宣言されていない名前 (CF0310)。GLSL では、波括弧を書かない制御文の本体の宣言はその文の外で見えず、
    /// インスタンスの名前の無いブロックの名前は式に書けない。拡張で決まる語は、その <c>#extension</c> より前では名前である。
    /// 使えない予約語 (CF0001) があっても検査を続ける。
    /// </summary>
    [Theory]
    [InlineData("a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { int i = 1; if (i == 1) int j = i; else int k = j; o = vec4(1.0); }")]
    [InlineData("a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { while (o.x > 0.0) int j = 1; o = vec4(float(j)); }")]
    [InlineData("a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nlayout(binding = 0) uniform Blk { float f; };\nvoid main() { Blk; o = vec4(f); }")]
    [InlineData("a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid f() { terminateInvocation; }\n#extension GL_EXT_terminate_invocation : enable\nvoid main() { o = vec4(1.0); }")]
    [InlineData("a.frag", "#version 310 es\nprecision mediump float;\nsample out vec4 o;\nvoid main() { o = vec4(missing); }")]
    [InlineData("a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(later); }\nfloat later = 1.0;")]
    public async Task 宣言されていない名前(string fileName, string code)
    {
        Assert.Contains("error CF0310", await AnalyzeAsync(fileName, code + "\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 構文エラーがあっても、最初の構文エラーより前の位置は宣言が無いことを根拠にする検査をする (GLSL は使う前に宣言する)。
    /// 構文エラーより後ろの位置は検査しない。
    /// </summary>
    [Fact]
    public async Task 構文エラーより前の位置()
    {
        const string body = "#version 450\nlayout(location = 0) out vec4 o;\nstruct S { float a; };\nfloat f(S s) { return s.a; }\n"
                            + "void g() { o = vec4(f(1.0) + before); }\nfloat x = ;\nvoid h() { o = vec4(after); }\nvoid main() { g(); h(); }\n";

        string output = await AnalyzeAsync("a.frag", body);
        Assert.Contains("CF0401", output, StringComparison.Ordinal);
        Assert.Contains("'before'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("'after'", output, StringComparison.Ordinal);
        Assert.Contains("より後ろの位置で、宣言が無いことを根拠にする検査", output, StringComparison.Ordinal);
    }

    /// <summary>精度の文、名前を並べた invariant、特殊化の定数の組み込みは、既にある名前に修飾子を足す宣言の誤りにしない。</summary>
    [Fact]
    public async Task 既にある名前に足せる修飾子()
    {
        string output = await AnalyzeAsync(
            "a.vert",
            "#version 450\nprecision mediump float;\ninvariant gl_Position, gl_PointSize;\nlayout(constant_id = 1) gl_MaxImageUnits;\nvoid main() { gl_Position = vec4(0.0); gl_PointSize = 1.0; }\n");

        Assert.DoesNotContain("GL0101", output, StringComparison.Ordinal);
    }

    /// <summary>layout で大きさを決めた後の gl_in と gl_out は、length() と定数でない添字で使える。</summary>
    [Fact]
    public async Task 大きさの決まった入出力の配列()
    {
        string geometry = await AnalyzeAsync("a.geom", "#version 450\nlayout(triangles) in;\nlayout(points, max_vertices = 1) out;\nuniform int i;\nvoid main() { gl_Position = gl_in[i].gl_Position * float(gl_in.length()); EmitVertex(); }\n");
        string control = await AnalyzeAsync("a.tesc", "#version 450\nlayout(vertices = 3) out;\nint a[gl_out.length()];\nvoid main() { gl_out[gl_InvocationID].gl_Position = vec4(a.length()); }\n");

        Assert.DoesNotContain("GL0308", geometry, StringComparison.Ordinal);
        Assert.DoesNotContain("GL0308", control, StringComparison.Ordinal);
    }

    /// <summary>組み込みの変数とブロックを、変えてよいものだけ変えて宣言し直すのは報告しない。</summary>
    [Fact]
    public async Task 正しい組み込みの宣言し直し()
    {
        string fragment = await AnalyzeAsync(
            "a.frag",
            "#version 450\nlayout(origin_upper_left) in vec4 gl_FragCoord;\nlayout(origin_upper_left) in vec4 gl_FragCoord;\n"
            + "layout(depth_greater) out float gl_FragDepth;\nout float gl_FragDepth;\nin float gl_ClipDistance[4];\nlayout(location = 0) out vec4 o;\n"
            + "void main() { o = gl_FragCoord + vec4(gl_ClipDistance[0]); gl_FragDepth = 1.0; }\n");
        string vertex = await AnalyzeAsync("a.vert", "#version 450\nout gl_PerVertex { vec4 gl_Position; float gl_PointSize; };\nvoid main() { gl_Position = vec4(0.0); gl_PointSize = 1.0; }\n");

        Assert.DoesNotContain("GL0406", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("GL0406", vertex, StringComparison.Ordinal);
    }

    /// <summary>GLSL の __FILE__ は文字列の番号 (整数) なので、#line の式に書ける。</summary>
    [Fact]
    public async Task lineの式の__FILE__()
    {
        string output = await AnalyzeAsync("a.vert", "#version 450\n#line __FILE__ + 2\n#line __FILE__ * __LINE__\nvoid main() { gl_Position = vec4(0.0); }\n");

        Assert.DoesNotContain("CF0002", output, StringComparison.Ordinal);
    }

    /// <summary>ES でない GLSL と、短絡で評価しない側の定義されていないマクロは報告しない。</summary>
    [Fact]
    public async Task 書ける条件式の未定義のマクロ()
    {
        Assert.DoesNotContain("GL0003", await AnalyzeAsync("a.frag", "#version 450\nlayout(location = 0) out vec4 o;\n#if UNDEF\n#endif\nvoid main() { o = vec4(1.0); }\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("GL0003", await AnalyzeAsync("a.frag", "#version 300 es\nprecision mediump float;\nout vec4 o;\n#if defined(UNDEF) && UNDEF\n#endif\nvoid main() { o = vec4(1.0); }\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 正しい switch と break / continue は報告しない。glslang がコードを作らないと数える文 (初期値の無い宣言) だけの前の文と、
    /// 最後のラベルの後に文が無いことが警告になるバージョン (4.50) も報告しない。
    /// </summary>
    [Fact]
    public async Task 正しいswitch()
    {
        const string Body = "layout(location = 0) out vec4 o;\nuniform int c;\n"
            + "void main() { o = vec4(0.0); for (int i = 0; i < 4; i++) { switch (c) { int t; case 0: case 1: o.x = 1.0; continue; default: { o.y = 1.0; } break; } if (i > 2) break; } switch (c) { case 1: break; default: } }\n";

        Assert.DoesNotContain("GL0321", await AnalyzeAsync("a.frag", "#version 450\n" + Body), StringComparison.Ordinal);
        Assert.Contains("GL0321", await AnalyzeAsync("a.frag", "#version 460\n" + Body), StringComparison.Ordinal);
    }

    /// <summary>
    /// GLSL の前処理の誤り: 予約された名前の #define / #undef、#pragma optimize / debug の形、2 つ目の #version と最初でない #version、行の頭の ##。
    /// </summary>
    [Fact]
    public async Task GLSLの前処理の誤り()
    {
        string output = await AnalyzeAsync("a.frag", "// comment \\\ncontinued\n#version 450\n#version 450\n#define GL_FOO 1\n#undef defined\n#pragma optimize(on) extra\n#pragma debug(off)\n## x\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(1.0); }\n");

        Assert.Contains("(4:2): error GL0003", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(3:2): error GL0003", output, StringComparison.Ordinal);
        Assert.Contains("(5:9): error GL0003", output, StringComparison.Ordinal);
        Assert.Contains("(6:8): error GL0003", output, StringComparison.Ordinal);
        Assert.Contains("(7:9): error GL0003", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(8:", output, StringComparison.Ordinal);
        Assert.Contains("(9:1): error CF0002", output, StringComparison.Ordinal);
    }

    /// <summary>プロトタイプと定義が同じ修飾子なら、書き方 (const in と const、精度の有無) が違っても GL0323 を報告しない。多重定義も報告しない。</summary>
    [Fact]
    public async Task 食い違わない関数の宣言()
    {
        string output = await AnalyzeAsync("a.frag", "#version 450\nlayout(location = 0) out vec4 o;\nfloat f(const in float a, highp int b);\nfloat f(const float a, int b) { return a; }\nint f(int a) { return a; }\nfloat g(void);\nfloat g() { return 1.0; }\nvoid main() { o = vec4(f(1.0, 1) + float(f(1)) + g()); }\n");

        Assert.DoesNotContain("GL0323", output, StringComparison.Ordinal);
    }

    /// <summary>std140 / std430 の規則どおりの offset と、重ならない atomic_uint の offset は報告しない。</summary>
    [Fact]
    public async Task 正しいoffset()
    {
        string output = await AnalyzeAsync("a.frag", "#version 440\nlayout(location = 0) out vec4 o;\n"
            + "layout(std140, binding = 0) uniform U { vec4 a; layout(offset = 32) vec3 b; layout(offset = 48) vec2 d; layout(align = 16) float e; layout(align = 2) double f; layout(offset = 80) float h; layout(align = 64) dvec3 i; layout(offset = 164, align = 8) float j; };\n"
            + "layout(std430, binding = 1) buffer B { float x; layout(offset = 8) vec2 y; mat2 m; layout(offset = 32) float z[3]; };\n"
            + "layout(binding = 0, offset = 4) uniform atomic_uint;\nlayout(binding = 0) uniform atomic_uint c1;\nlayout(binding = 0) uniform atomic_uint c2[2];\nlayout(binding = 0, offset = 0) uniform atomic_uint c0;\n"
            + "void main() { o = a; }\n");

        Assert.DoesNotContain("GL0103", output, StringComparison.Ordinal);
    }

    /// <summary>1024 文字を越える名前は GL0003 で伝える (glslang の「name too long」)。1024 文字ちょうどは伝えない。</summary>
    [Fact]
    public async Task 長すぎるトークン()
    {
        string ok = new('a', 1024);
        string ng = new('b', 1025);
        string output = await AnalyzeAsync("a.frag", $"#version 450\nlayout(location = 0) out vec4 o;\nfloat {ok} = 1.0;\nfloat {ng} = 2.0;\nvoid main() {{ o = vec4({ok} + {ng}); }}\n");

        Assert.Contains("(4:7): error GL0003", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(3:7): error GL0003", output, StringComparison.Ordinal);
    }

    /// <summary>正しい型の引数 (括弧の中の比べる演算子、constant_id の特殊化の定数を含む) は GL0324 を報告しない。</summary>
    [Fact]
    public async Task 正しい型の引数()
    {
        string output = await AnalyzeAsync("a.comp", "#version 450 core\n#extension GL_KHR_cooperative_matrix : enable\n#extension GL_KHR_memory_scope_semantics : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 64) in;\nlayout(constant_id = 0) const int Y = 8;\nvoid main() { coopmat<float, gl_ScopeSubgroup, 16, (2>1?8:4), gl_MatrixUseAccumulator> m; coopmat<float16_t, gl_ScopeSubgroup, 8, Y, gl_MatrixUseA> n; }\n");

        Assert.DoesNotContain("GL0324", output, StringComparison.Ordinal);
    }

    /// <summary>長いベクトルの正しい組み込み関数の呼び出し (同じ型、リテラルの実引数、mix の bool のベクトル) は報告しない。</summary>
    [Fact]
    public async Task 正しい長いベクトルの組み込み関数()
    {
        string output = await AnalyzeAsync("a.comp", "#version 450\n#extension GL_EXT_long_vector : enable\n#extension GL_EXT_shader_explicit_arithmetic_types : enable\nlayout(local_size_x = 1) in;\nvoid main() { vector<float, 5> a; vector<float, 5> b; vector<bool, 5> m; vector<uint, 5> u; a = max(a, b); a = mix(a, b, m); u = max(u, 1); a = a + b; }\n");

        Assert.DoesNotContain("GL0302", output, StringComparison.Ordinal);
    }

    /// <summary>参照の正しい足し引き (整数との + と -、参照どうしの差) は報告しない。</summary>
    [Fact]
    public async Task 正しい参照の足し引き()
    {
        string output = await AnalyzeAsync("a.frag", "#version 450\n#extension GL_EXT_buffer_reference2 : enable\n#extension GL_EXT_shader_explicit_arithmetic_types_int64 : enable\nlayout(buffer_reference) buffer T1 { int x[]; };\nlayout(buffer_reference) buffer T2 { int x[2]; };\nlayout(location = 0) out vec4 o;\nvoid main() { T2 j; T2 k = j + 6; j += 3; j = 5 + j; int64_t d = k - j; o = vec4(1.0); }\n");

        Assert.DoesNotContain("GL0301", output, StringComparison.Ordinal);
    }

    /// <summary>GLSL で到達した #error は GL0003 (エラー) で伝え、CF0104 (情報) は重ねない。到達しない #error は伝えない。</summary>
    [Fact]
    public async Task 到達したerror()
    {
        string output = await AnalyzeAsync("a.frag", "#version 450\nlayout(location = 0) out vec4 o;\n#if (-2147483648 / -1) != 0\n#error intmin\n#endif\n#ifdef UNDEF\n#error undef\n#endif\n#error reached\nvoid main() { o = vec4(1.0); }\n");

        Assert.Contains("GL0003: #error に到達しました: reached", output, StringComparison.Ordinal);
        Assert.DoesNotContain("intmin", output, StringComparison.Ordinal);
        Assert.DoesNotContain("undef", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CF0104", output, StringComparison.Ordinal);
    }

    /// <summary>型がすべて同じ宣言がある呼び出しと、変換の良さでいちばん良い宣言が決まる呼び出しは、多重定義があいまいとは言わない。</summary>
    [Fact]
    public async Task 決まる多重定義()
    {
        const string body = "#version 450\nlayout(location = 0) out vec4 o;\nvoid g(float a, double b) { }\nvoid g(double a, float b) { }\n"
                            + "void h(double a) { }\nvoid h(int a) { }\n"
                            + "void main() { float f = 1.0; double d = 1.0; g(f, d); g(d, f); h(f); o = vec4(f); }\n";

        Assert.DoesNotContain("GL0320", await AnalyzeAsync("a.frag", body), StringComparison.Ordinal);
    }

    /// <summary>
    /// 波括弧の中の宣言、本体の中で使う宣言、#extension より後ろの terminateInvocation、前で宣言した関数の外の変数、
    /// 後ろで宣言し直した組み込みの変数 (gl_ClipDistance) は報告しない。
    /// </summary>
    [Fact]
    public async Task 見える宣言()
    {
        const string body = "#version 450\n#extension GL_EXT_terminate_invocation : enable\nlayout(location = 0) out vec4 o;\nfloat earlier = 1.0;\n"
                            + "void main() { int i = 1; if (i == 1) { int j = i; o = vec4(float(j)); } for (int k = 0; k < 2; k++) o += vec4(float(k)); if (o.x > 9.0) terminateInvocation; o += vec4(earlier); }\n";

        Assert.DoesNotContain("CF0310", await AnalyzeAsync("a.frag", body), StringComparison.Ordinal);
    }

    /// <summary>マクロを中身を変えて定義し直すこと (CF0004) は、GLSL ではエラーにする。設定で重要度を書けば、それに従う。</summary>
    [Theory]
    [InlineData("", "error CF0004")]
    [InlineData("rules:\n  CF0004: warning\n", "warning CF0004")]
    public async Task マクロの定義し直しはGLSLではエラー(string rules, string expected)
    {
        string config = Path.Combine(_workDirectory, "shaderlyn.yml");
        File.WriteAllText(config, $"version: 1\nenvironment: glsl\n{rules}");
        File.WriteAllText(Path.Combine(_workDirectory, "a.frag"), """
            #version 450
            #define SCALE 1.0
            #define SCALE 2.0
            layout(location = 0) out vec4 o;
            void main() { o = vec4(SCALE); }

            """);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.frag"), "--config", config], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
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
