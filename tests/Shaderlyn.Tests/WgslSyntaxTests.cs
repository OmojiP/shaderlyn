using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Wgsl;

namespace Shaderlyn.Tests;

/// <summary>
/// WGSL の構文解析の検証 (WG1)。
/// </summary>
/// <remarks>
/// 通る例は、naga 30 が誤りにしない書き方 (naga・wgpu の試験、webgpu-samples、Bevy にあるもの)。
/// </remarks>
public sealed class WgslSyntaxTests
{
    private static ShaderSyntaxTree Parse(string code)
        => WgslSyntaxTree.Parse(SourceText.From(code, "a.wgsl"));

    private static string Describe(ShaderSyntaxTree tree)
        => string.Join("\n", tree.Diagnostics.Select(d => $"{d.Location.LineSpan.Start} {d.Id}: {d.GetMessage()}"));

    [Theory]
    // 指令・別名・構造体・資源
    [InlineData("enable f16;\nrequires readonly_and_readwrite_storage_textures;\ndiagnostic(off, derivative_uniformity);\nalias V = vec3<f32>;\nstruct S {\n    @location(0) a: vec4f,\n    b: array<vec3<f32>, 4>,\n}\n@group(0) @binding(0) var<storage, read_write> buf: array<S>;\n@group(0) @binding(1) var t: texture_storage_2d<rgba8unorm, write>;\n")]
    // 関数・属性・戻り値の属性
    [InlineData("@vertex\nfn vs(@builtin(vertex_index) i: u32, @location(0) p: vec3f) -> @builtin(position) vec4<f32> {\n    return vec4<f32>(p, 1.0);\n}\n@compute @workgroup_size(8, 8, 1)\nfn cs(@builtin(global_invocation_id) id: vec3<u32>) { }\n")]
    // 文: let・var・const・for・while・loop・continuing・break if・switch・_ =・const_assert
    [InlineData("fn f(n: i32) -> i32 {\n    let a = 1i;\n    var b: f32 = 2.0;\n    const c = 3u;\n    for (var i = 0; i < n; i++) { b += 1.0; }\n    while b > 0.0 { b -= 1.0; }\n    loop {\n        if a > 2 { break; }\n        continuing {\n            b = b * 2.0;\n            break if b > 10.0;\n        }\n    }\n    switch n {\n        case 0, 1: { return 1; }\n        case 2, default { return 2; }\n    }\n    _ = a;\n    const_assert 1 < 2;\n    return 0;\n}\n")]
    // 式: テンプレートと比較の見分け、ポインター、ビットキャスト、入れ子のコメント
    [InlineData("fn g(p: ptr<function, array<f32, 4>>) -> bool {\n    let x = bitcast<u32>(1.0);\n    let y = (*p)[0];\n    let z = &(*p);\n    /* a /* b */ c */\n    let a = 1 < 2 && 3 > 2;\n    let m = mat2x2<f32>(1.0, 0.0, 0.0, 1.0);\n    return x >> 1u > 0u && y < 2.0;\n}\n")]
    public void nagaが通す書き方を構文エラーにしない(string code)
    {
        ShaderSyntaxTree tree = Parse(code);

        Assert.True(tree.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error), Describe(tree));
    }

    [Theory]
    [InlineData("fn f() {\n    let a = (1.0 + 2.0;\n}\n", 2)]
    [InlineData("struct S {\n    a: f32\n    b: f32,\n}\n", 3)]
    [InlineData("fn f() -> i32 {\n    return 1 +;\n}\n", 2)]
    // キーワード・予約語・__ で始まる語は名前にできない
    [InlineData("fn f() {\n    var default: f32 = 1.0;\n}\n", 2)]
    [InlineData("struct Foo {\n    switch: f32,\n}\n", 2)]
    [InlineData("var<private> __bad: f32;\n", 1)]
    [InlineData("const typedef = 1;\n", 1)]
    // 文として書けない式、continuing の外の break if、型も初期値も無い var、初期値の無い let
    [InlineData("fn f() {\n    3 + 5;\n}\n", 2)]
    [InlineData("fn f() {\n    loop {\n        break if true;\n    }\n}\n", 3)]
    [InlineData("fn f() {\n    var x;\n}\n", 2)]
    [InlineData("fn f() {\n    let x: f32;\n}\n", 2)]
    // アドレス空間に型引数は書けない
    [InlineData("var<private<x>> v: u32;\n", 1)]
    public void 構文の誤りを報告する(string code, int line)
    {
        ShaderSyntaxTree tree = Parse(code);

        Assert.Contains(tree.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Location.LineSpan.Start.Line + 1 == line);
    }

    [Fact]
    public void 書き方をC系のノードにする()
    {
        ShaderSyntaxTree tree = Parse(
            "alias V = vec3<f32>;\nstruct S { a: V, }\nfn f(x: vec3<f32>) -> f32 {\n    var v: array<vec2<f32>, 2>;\n    let w = vec3<f32>(1.0);\n    loop { break; }\n    return x.x;\n}\n");
        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.Root;

        Assert.Equal("vec3f", root.TypeAliases["V"]);

        StructDeclarationSyntax structure = Assert.Single(root.Declarations.OfType<StructDeclarationSyntax>());
        VariableDeclarationSyntax member = Assert.IsType<VariableDeclarationSyntax>(Assert.Single(structure.Members));
        Assert.Equal("vec3f", member.Type.Name);

        FunctionDeclarationSyntax function = Assert.Single(root.Declarations.OfType<FunctionDeclarationSyntax>());
        Assert.Equal("f32", function.ReturnType.Name);
        Assert.Equal("vec3f", Assert.IsType<ParameterSyntax>(function.Parameters[0].Node).Type.Name);

        StatementSyntax[] statements = [.. function.Body!.Statements];
        VariableDeclarationSyntax local = Assert.IsType<VariableDeclarationSyntax>(Assert.IsType<LocalDeclarationStatementSyntax>(statements[0]).Declaration);
        Assert.Equal("array", local.Type.Name);
        Assert.Equal("v", local.Declarators.Single(d => d.Node is not null).Node is VariableDeclaratorSyntax d ? d.Name : null);

        VariableDeclarationSyntax let = Assert.IsType<VariableDeclarationSyntax>(Assert.IsType<LocalDeclarationStatementSyntax>(statements[1]).Declaration);
        Assert.Equal("let", let.Type.Name);
        VariableDeclaratorSyntax declarator = Assert.IsType<VariableDeclaratorSyntax>(let.Declarators[0].Node);
        InvocationExpressionSyntax call = Assert.IsType<InvocationExpressionSyntax>(declarator.Initializer);
        Assert.Equal("vec3f", call.TargetName);

        WhileStatementSyntax loop = Assert.IsType<WhileStatementSyntax>(statements[2]);
        Assert.Equal("true", Assert.IsType<IdentifierExpressionSyntax>(loop.Condition).Name);
    }
}
