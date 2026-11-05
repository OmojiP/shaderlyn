using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Godot;

namespace Shaderlyn.Tests;

/// <summary>
/// Godot シェーダー言語の構文解析の検証 (GD1)。
/// </summary>
/// <remarks>
/// どの例も Godot 4.7.2 がエラーにしない書き方である (godot-demo-projects と godot-shaders にあるもの)。
/// </remarks>
public sealed class GodotSyntaxTests
{
    private static ShaderSyntaxTree Parse(string code)
        => GodotSyntaxTree.Parse(SourceText.From(code, "a.gdshader"));

    private static string Describe(ShaderSyntaxTree tree)
        => string.Join("\n", tree.Diagnostics.Select(d => $"{d.Location.LineSpan.Start} {d.Id}: {d.GetMessage()}"));

    [Theory]
    [InlineData("shader_type spatial;\nrender_mode unshaded, cull_disabled, blend_add;\nvoid fragment() { ALBEDO = vec3(1.0); }\n")]
    [InlineData("shader_type spatial;\nstencil_mode read, compare_equal, 1;\n")]
    [InlineData("shader_type canvas_item;\nuniform vec4 tint : source_color = vec4(1.0);\nuniform float amount : hint_range(0.0, 1.0, 0.01) = 0.5;\n")]
    [InlineData("shader_type spatial;\nuniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;\n")]
    [InlineData("shader_type spatial;\nuniform int mode : hint_enum(\"A\", \"B\") = 0;\nuniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;\n")]
    [InlineData("shader_type spatial;\ninstance uniform vec4 color : source_color;\nglobal uniform float wind;\n")]
    [InlineData("shader_type spatial;\ngroup_uniforms surface.detail;\nuniform float a;\ngroup_uniforms;\n")]
    [InlineData("shader_type spatial;\nvarying flat int id;\nvarying smooth vec3 world;\nvarying highp vec2 uv2;\n")]
    [InlineData("shader_type spatial;\nconst float K[3] = float[3](1.0, 2.0, 3.0);\nconst vec3 C = vec3(1.0);\n")]
    [InlineData("shader_type spatial;\nstruct S { float a; vec3 b; };\nvoid f(in float x, out float y, inout S s) { y = x; }\n")]
    [InlineData("shader_type spatial;\nvoid fragment() {\n  float[3] a = float[](1.0, 2.0, 3.0);\n  lowp float b = 1.0f;\n  uint c = 1u;\n  int d = 0xFF;\n}\n")]
    [InlineData("shader_type particles;\nvoid start() {\n}\nvoid process() {\n  if (RESTART) { discard; }\n}\n")]
    [InlineData("shader_type spatial;\n/* 閉じていないコメントは、Godot ではファイルの終わりまでのコメントになる\n")]
    public void Godotが通す書き方を構文エラーにしない(string code)
    {
        ShaderSyntaxTree tree = Parse(code);

        Assert.True(tree.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error), Describe(tree));
    }

    [Fact]
    public void uniformのヒントの並びを1つの宣言として読む()
    {
        ShaderSyntaxTree tree = Parse("shader_type spatial;\nuniform sampler2D t : source_color, filter_linear, repeat_enable;\n");

        VariableDeclarationSyntax declaration = Assert.Single(tree.Root.Declarations.OfType<VariableDeclarationSyntax>());
        VariableDeclaratorSyntax declarator = Assert.Single(declaration.Variables);
        Assert.Equal(["source_color", "filter_linear", "repeat_enable"], declarator.Semantics.Select(s => s.Name));
    }

    [Theory]
    [InlineData("shader_type spatial;\nvoid fragment() {\n  float a = (1.0 + 2.0;\n}\n", 3)]
    [InlineData("shader_type spatial;\nvoid fragment() {\n  if true {\n  }\n}\n", 3)]
    public void 構文の誤りを報告する(string code, int line)
    {
        ShaderSyntaxTree tree = Parse(code);

        Assert.Contains(tree.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Location.LineSpan.Start.Line + 1 == line);
    }
}
