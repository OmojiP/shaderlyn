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
/// 値を返さない経路 (CF0501) は、glslang はエラーにしない (値が不定になるだけ)。HLSL と同じく誤りとして伝える。
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
    [InlineData("CF0501", "a.frag", "float pick(bool b) { if (b) { return 1.0; } }\nvoid main() { o = vec4(pick(true)); }")]
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
