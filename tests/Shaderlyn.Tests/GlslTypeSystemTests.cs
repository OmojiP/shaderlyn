using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Tests;

/// <summary>
/// GLSL の型システム (<see cref="GlslTypeSystem"/>) と、GLSL の式の型の検証。
/// </summary>
/// <remarks>
/// 暗黙の変換の規則は GLSL 4.60 と GLSL ES 3.20 の仕様書の 4.1.10 節による。
/// </remarks>
public sealed class GlslTypeSystemTests
{
    /// <summary>
    /// 宣言の後に置いた <c>r</c> の初期化の式の型を求める。
    /// </summary>
    /// <param name="declarations">関数の中の宣言。</param>
    /// <param name="expression">型を求める式。</param>
    /// <param name="version"><c>#version</c> の行。</param>
    /// <returns>型の名前。</returns>
    private static string? TypeOf(string declarations, string expression, string version = "#version 450")
    {
        string code = $$"""
            {{version}}
            struct Light { vec3 color; float range[4]; };
            void main()
            {
                {{declarations}}
                auto_ r = {{expression}};
            }
            """.Replace("auto_", "float", StringComparison.Ordinal);
        SourceText text = SourceText.From(code, Path.Combine(Path.GetTempPath(), "t.frag"));
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [GlslEnvironment.WholeFile(text)], text, new GlslEnvironment(), ShaderSourceInfo.None);

        AnalyzedProgram program = Assert.Single(compilation.Programs);
        Assert.Empty(compilation.ReportableHlslDiagnostics);

        ExpressionSyntax initializer = program.Tree.Root.DescendantNodesAndSelf()
            .OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Name == "r")
            .Initializer!;

        return compilation.GetExpressionTypeBinder(program).GetEvaluatorFor(initializer).Evaluate(initializer);
    }

    [Theory]
    [InlineData("vec4 v;", "v.xy", "vec2")]
    [InlineData("vec4 v;", "v.st", "vec2")]
    [InlineData("vec4 v;", "v.bgr", "vec3")]
    [InlineData("ivec3 v;", "v.x", "int")]
    [InlineData("vec4 v;", "v[2]", "float")]
    [InlineData("dvec2 v;", "v.yx", "dvec2")]
    [InlineData("bvec4 v;", "v.xyz", "bvec3")]
    [InlineData("u16vec2 v;", "v.x", "uint16_t")]
    public void 成分の参照(string declarations, string expression, string expected)
        => Assert.Equal(expected, TypeOf(declarations, expression));

    /// <summary>
    /// GLSL の <c>*</c> は、行列があれば行列の積である。<c>matCxR</c> は C 列 R 行。
    /// <c>m[i]</c> は列で、R 成分のベクトルになる。
    /// </summary>
    [Theory]
    [InlineData("mat4x3 m; vec4 v;", "m * v", "vec3")]
    [InlineData("mat4x3 m; vec3 v;", "v * m", "vec4")]
    [InlineData("mat3 a; mat3 b;", "a * b", "mat3")]
    [InlineData("mat2x3 a; mat4x2 b;", "a * b", "mat4x3")]
    [InlineData("mat4 m; vec4 v;", "m * v", "vec4")]
    [InlineData("dmat3 m; dvec3 v;", "m * v", "dvec3")]
    [InlineData("mat4x3 m;", "m[1]", "vec3")]
    [InlineData("mat4x3 m;", "m[1][2]", "float")]
    [InlineData("mat3 m;", "m * 2.0", "mat3")]
    [InlineData("mat3 a; mat3 b;", "a + b", "mat3")]
    public void 行列の積と添字(string declarations, string expression, string expected)
        => Assert.Equal(expected, TypeOf(declarations, expression));

    [Theory]
    [InlineData("vec3 a; vec3 b;", "a * b", "vec3")]
    [InlineData("vec3 a;", "a * 2.0", "vec3")]
    [InlineData("ivec2 a;", "a + 1", "ivec2")]
    [InlineData("vec3 a;", "-a", "vec3")]
    [InlineData("int i; float f;", "i + f", "float")]
    [InlineData("uint u; int i;", "u + i", "uint")]
    [InlineData("float f; double d;", "f * d", "double")]
    [InlineData("ivec3 a; ivec3 b;", "a & b", "ivec3")]
    public void 算術の演算(string declarations, string expression, string expected)
        => Assert.Equal(expected, TypeOf(declarations, expression));

    [Theory]
    [InlineData("vec3 a; vec3 b;", "a == b")]
    [InlineData("vec3 a; vec3 b;", "a != b")]
    [InlineData("float a; float b;", "a < b")]
    [InlineData("bool a; bool b;", "a && b")]
    public void 比較の結果はいつもbool(string declarations, string expression)
        => Assert.Equal("bool", TypeOf(declarations, expression));

    /// <summary>ES には暗黙の変換が無いので、<c>int</c> と <c>float</c> を混ぜた演算の型は決まらない (誤りである)。</summary>
    [Fact]
    public void ESでは整数と浮動小数を混ぜた演算の型を決めない()
        => Assert.Null(TypeOf("int i; float f;", "i + f", "#version 310 es"));

    [Theory]
    [InlineData("vec3(1.0)", "vec3")]
    [InlineData("mat4(1.0)", "mat4")]
    [InlineData("1", "int")]
    [InlineData("1u", "uint")]
    [InlineData("0x1Fu", "uint")]
    [InlineData("0xFF", "int")]
    [InlineData("1.0", "float")]
    [InlineData("1.0f", "float")]
    [InlineData("1e3", "float")]
    [InlineData("1.0lf", "double")]
    [InlineData("1.5hf", "float16_t")]
    [InlineData("3l", "int64_t")]
    [InlineData("3ul", "uint64_t")]
    public void コンストラクタとリテラル(string expression, string expected)
        => Assert.Equal(expected, TypeOf(string.Empty, expression));

    [Theory]
    [InlineData("float a[4];", "a.length()", "int")]
    [InlineData("vec3 v;", "v.length()", "int")]
    [InlineData("Light l;", "l.color", "vec3")]
    [InlineData("Light l;", "l.range[1]", "float")]
    [InlineData("vec3 a[2];", "a[1]", "vec3")]
    [InlineData("Light l;", "l.range.length()", "int")]
    public void 配列と構造体(string declarations, string expression, string expected)
        => Assert.Equal(expected, TypeOf(declarations, expression));

    [Theory]
    [InlineData(450, "int", "float", true)]
    [InlineData(450, "int", "uint", true)]
    [InlineData(450, "uint", "double", true)]
    [InlineData(450, "float", "int", false)]
    [InlineData(450, "uint", "int", false)]
    [InlineData(450, "bool", "int", false)]
    [InlineData(450, "int", "bool", false)]
    [InlineData(450, "ivec3", "vec3", true)]
    [InlineData(450, "vec3", "dvec3", true)]
    [InlineData(450, "mat3", "dmat3", true)]
    [InlineData(450, "mat3x3", "mat3", true)]
    [InlineData(450, "vec4", "vec3", false)]
    [InlineData(450, "vec3", "vec4", false)]
    [InlineData(450, "float", "vec3", false)]
    [InlineData(450, "mat4", "mat3", false)]
    [InlineData(450, "Light", "vec3", true)]
    [InlineData(450, "void", "float", false)]
    [InlineData(330, "int", "float", true)]
    [InlineData(330, "uint", "float", true)]
    [InlineData(330, "int", "uint", false)]
    [InlineData(330, "float", "double", false)]
    [InlineData(110, "int", "float", false)]
    [InlineData(120, "int", "float", true)]
    [InlineData(null, "int", "double", true)]
    [InlineData(450, "int16_t", "float", true)]
    public void デスクトップの暗黙の変換(int? version, string value, string target, bool expected)
        => Assert.Equal(expected, GlslTypeSystem.For(version, GlslProfile.Core).IsConvertible(value, target));

    [Theory]
    [InlineData("int", "float")]
    [InlineData("int", "uint")]
    [InlineData("ivec2", "vec2")]
    public void ESには暗黙の変換が無い(string value, string target)
    {
        Assert.False(GlslTypeSystem.For(310, GlslProfile.Es).IsConvertible(value, target));
        Assert.True(GlslTypeSystem.For(310, GlslProfile.Es).IsConvertible(value, value));
    }

    /// <summary><c>out</c> / <c>inout</c> の引数は、どちらかの向きに変換できれば通す (誤りと言い切れる場合だけ通さない)。</summary>
    [Fact]
    public void 書き戻す引数は形が同じならどちらかの向きの変換で通す()
    {
        GlslTypeSystem types = GlslTypeSystem.For(450, GlslProfile.Core);

        Assert.True(types.IsConvertible("float", "int", exact: true));
        Assert.False(types.IsConvertible("vec3", "vec4", exact: true));
        Assert.False(types.IsConvertible("bool", "float", exact: true));
    }

    [Theory]
    [InlineData("float", "Scalar")]
    [InlineData("uint", "Scalar")]
    [InlineData("vec3", "Vector")]
    [InlineData("f16vec4", "Vector")]
    [InlineData("mat4", "Matrix")]
    [InlineData("dmat2x3", "Matrix")]
    [InlineData("sampler2D", "Texture2D")]
    [InlineData("isampler2DMS", "Texture2D")]
    [InlineData("sampler2DShadow", "Texture2D")]
    [InlineData("texture2D", "Texture2D")]
    [InlineData("image2D", "Texture2D")]
    [InlineData("usampler2DArray", "Texture2DArray")]
    [InlineData("image3D", "Texture3D")]
    [InlineData("samplerCubeShadow", "TextureCube")]
    [InlineData("samplerCubeArray", "TextureCubeArray")]
    [InlineData("samplerBuffer", "Buffer")]
    [InlineData("sampler", "Sampler")]
    [InlineData("samplerShadow", "Sampler")]
    [InlineData("atomic_uint", "Unknown")]
    [InlineData("float4", "Unknown")]
    [InlineData("vec5", "Unknown")]
    [InlineData("mat5", "Unknown")]
    [InlineData("Light", "Unknown")]
    public void 型を分類する(string typeName, string expected)
        => Assert.Equal(expected, GlslTypeSystem.Instance.Classify(typeName).ToString());

    [Fact]
    public void 形から名前を作る()
    {
        GlslTypeSystem types = GlslTypeSystem.Instance;

        Assert.Equal("mat2x3", types.Compose(new ShaderNumericShape(ShaderNumericKind.Matrix, "float", 2, 3)));
        Assert.Equal("mat3", types.Compose(new ShaderNumericShape(ShaderNumericKind.Matrix, "float", 3, 3)));
        Assert.Equal("dmat4", types.Compose(new ShaderNumericShape(ShaderNumericKind.Matrix, "double", 4, 4)));
        Assert.Null(types.Compose(new ShaderNumericShape(ShaderNumericKind.Matrix, "int", 3, 3)));
        Assert.Equal("uvec2", types.ComposeNumeric("uint", 2));
        Assert.Equal("i16vec3", types.ComposeNumeric("int16_t", 3));
        Assert.Equal("float", types.ComposeNumeric("float", 1));
        Assert.Null(types.ComposeNumeric("float", 5));
        Assert.Equal("vec3", types.Compose(types.TryDescribeNumeric("f32vec3", out ShaderNumericShape shape) ? shape : default));
    }
}
