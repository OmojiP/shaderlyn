using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Tests;

public sealed class ShaderLabParserTests
{
    private static ShaderLabSyntaxTree Parse(string content)
        => ShaderLabSyntaxTree.Parse(SourceText.From(content, "test.shader"));

    private static string Describe(ImmutableArray<Diagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"  {d.Location.LineSpan.Start} {d.GetMessage()}"));

    /// <summary>
    /// 正しい ShaderLab を構文エラーなしで解析できることを検証する。
    /// </summary>
    /// <remarks>
    /// ラウンドトリップテストは「トークンを落としていない」ことしか示さない。
    /// 正しい入力を正しく解釈できているかは、構文エラーが 0 件であることで確認する。
    /// この 2 つは別の性質であり、片方だけでは不十分である。
    /// </remarks>
    [Theory]
    [InlineData("UrpLit.shader")]
    [InlineData("BuiltInLegacy.shader")]
    [InlineData("EdgeCases.shader")]
    public void 正しいシェーダーは構文エラーなしで解析できる(string fileName)
    {
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(Fixtures.Load(fileName));

        Assert.True(
            tree.Diagnostics.IsEmpty,
            $"{fileName} で予期しない構文エラーが発生しました:\n{Describe(tree.Diagnostics)}");
    }

    [Fact]
    public void シェーダー名とプロパティを解釈できる()
    {
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(Fixtures.Load("UrpLit.shader"));
        ShaderDeclarationSyntax shader = Assert.IsType<ShaderDeclarationSyntax>(tree.Root.Shader);

        Assert.Equal("Example/UrpLit", shader.Name);

        PropertiesBlockSyntax properties = shader.Body.Statements.OfType<PropertiesBlockSyntax>().Single();
        Assert.Equal(11, properties.Properties.Length);

        PropertyDeclarationSyntax baseMap = properties.Properties[0];
        Assert.Equal("_BaseMap", baseMap.Name);
        Assert.Equal("Base Map", baseMap.DisplayNameToken.ValueText);
        Assert.Equal("2D", baseMap.Type.TypeName);
        Assert.Equal("MainTexture", Assert.Single(baseMap.Attributes).Name);
        Assert.Equal("white", Assert.IsType<TextureDefaultValueSyntax>(baseMap.DefaultValue).Value);
    }

    [Fact]
    public void 数字で始まる型名を分割せずに解釈できる()
    {
        // 2D を数値 2 と識別子 D に分割すると、テクスチャを持つほぼ全てのシェーダーが壊れる。
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { Properties {
                _A ("A", 2D) = "white" {}
                _B ("B", 3D) = "" {}
                _C ("C", 2DArray) = "" {}
                _D ("D", CubeArray) = "" {}
            } }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, Describe(tree.Diagnostics));

        PropertiesBlockSyntax properties = tree.Root.Shader!.Body.Statements.OfType<PropertiesBlockSyntax>().Single();
        Assert.Equal(["2D", "3D", "2DArray", "CubeArray"], properties.Properties.Select(p => p.Type.TypeName));
    }

    [Fact]
    public void 指数表記は数値として解釈される()
    {
        // 2D を識別子にする処理が 1e-5 を壊さないこと。
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { Properties { _T ("T", Float) = 1e-5 } }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, Describe(tree.Diagnostics));

        PropertyDeclarationSyntax property = tree.Root.Shader!.Body.Statements
            .OfType<PropertiesBlockSyntax>().Single().Properties.Single();

        ScalarDefaultValueSyntax value = Assert.IsType<ScalarDefaultValueSyntax>(property.DefaultValue);
        Assert.Equal("1e-5", value.ValueToken.Text);
        Assert.Equal(SyntaxKind.NumericLiteralToken, value.ValueToken.Kind);
    }

    [Fact]
    public void 属性の修飾名に含まれるドットを解釈できる()
    {
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { Properties {
                [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
            } }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, Describe(tree.Diagnostics));

        PropertyAttributeSyntax attribute = tree.Root.Shader!.Body.Statements
            .OfType<PropertiesBlockSyntax>().Single().Properties.Single().Attributes.Single();

        Assert.Equal("Enum", attribute.Name);
        Assert.Contains(attribute.ArgumentTokens, t => t.Kind == SyntaxKind.DotToken);
    }

    [Fact]
    public void パスとタグを解釈できる()
    {
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(Fixtures.Load("UrpLit.shader"));
        SubShaderSyntax subShader = tree.Root.Shader!.Body.Statements.OfType<SubShaderSyntax>().Single();

        Assert.Equal(3, subShader.Passes.Count());

        TagsBlockSyntax tags = subShader.Body.Statements.OfType<TagsBlockSyntax>().Single();
        Assert.Equal(4, tags.Tags.Length);
        Assert.Equal("RenderType", tags.Tags[0].Key);
        Assert.Equal("Opaque", tags.Tags[0].Value);

        // Pass の名前は Name 命令として現れる。
        List<string> passNames = [.. subShader.Passes
            .SelectMany(p => p.Body.Statements.OfType<CommandSyntax>())
            .Where(c => c.NameIs("Name"))
            .Select(c => ((LiteralArgumentSyntax)c.ValueArguments.First()).Token.ValueText)];

        Assert.Equal(["ForwardLit", "ShadowCaster", "DepthOnly"], passNames);
    }

    [Fact]
    public void 埋め込みコードブロックを一つのトークンとして取り込む()
    {
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(Fixtures.Load("UrpLit.shader"));
        SubShaderSyntax subShader = tree.Root.Shader!.Body.Statements.OfType<SubShaderSyntax>().Single();

        ProgramBlockSyntax include = subShader.Body.Statements.OfType<ProgramBlockSyntax>().Single();
        Assert.StartsWith("HLSLINCLUDE", include.Token.Text, StringComparison.Ordinal);
        Assert.EndsWith("ENDHLSL", include.Token.Text, StringComparison.Ordinal);
        Assert.Contains("CBUFFER_START(UnityPerMaterial)", include.Token.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 終了キーワードに似た語がコメントにあってもブロックは切れない()
    {
        // 単純な部分文字列検索で終端を探すと、コメント中の ENDCG に反応して
        // ブロックが途中で切れ、以降の解析がすべて崩れる。
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { SubShader { Pass {
            CGPROGRAM
            // ここに ENDCG_NOT_REALLY と MY_ENDCG がある
            float4 x;
            ENDCG
            } } }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, Describe(tree.Diagnostics));

        ProgramBlockSyntax program = tree.Root.Shader!.Body.Statements
            .OfType<SubShaderSyntax>().Single().Passes.Single()
            .Body.Statements.OfType<ProgramBlockSyntax>().Single();

        Assert.Contains("float4 x;", program.Token.Text, StringComparison.Ordinal);
        Assert.EndsWith("ENDCG", program.Token.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void コメント内の終了キーワードでブロックは終端しない()
    {
        // コメント中の ENDCG で終端したと誤判定すると、そこから先の HLSL が
        // ShaderLab として解釈され、正しいファイルに大量の誤ったエラーが出る。
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { SubShader { Pass {
            CGPROGRAM
            // ENDCG
            /* ENDCG */
            float4 a; // 日本語のコメントに ENDCG があっても同じ
            ENDCG
            } } }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, Describe(tree.Diagnostics));

        ProgramBlockSyntax program = tree.Root.Shader!
            .DescendantNodesAndSelf().OfType<ProgramBlockSyntax>().Single();

        Assert.Contains("float4 a;", program.Token.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 文字列内の終了キーワードでブロックは終端しない()
    {
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { SubShader { Pass {
            HLSLPROGRAM
            #include "SomePath/ENDHLSL/Header.hlsl"
            float4 b;
            ENDHLSL
            } } }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, Describe(tree.Diagnostics));

        ProgramBlockSyntax program = tree.Root.Shader!
            .DescendantNodesAndSelf().OfType<ProgramBlockSyntax>().Single();

        Assert.Contains("float4 b;", program.Token.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ステンシルブロックのPass命令はPassブロックと誤解釈されない()
    {
        // Stencil { Pass Keep } の Pass は命令名であり、Pass ブロックの開始ではない。
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(Fixtures.Load("BuiltInLegacy.shader"));

        StencilBlockSyntax stencil = tree.Root.Shader!
            .DescendantNodesAndSelf().OfType<StencilBlockSyntax>().Single();

        Assert.Equal(5, stencil.Commands.Length);
        Assert.Equal(["Ref", "Comp", "Pass", "Fail", "ZFail"], stencil.Commands.Select(c => c.Name));

        CommandSyntax pass = stencil.Commands[2];
        Assert.Equal("Keep", Assert.IsType<LiteralArgumentSyntax>(Assert.Single(pass.ValueArguments)).Text);
    }

    [Fact]
    public void 命令の引数は行末で終わる()
    {
        // ShaderLab には文の終端記号が無いため、行末を終端とみなす規則を採っている。
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { SubShader {
            Cull Back
            ZWrite On
            } }
            """);

        List<CommandSyntax> commands = [.. tree.Root.Shader!.Body.Statements
            .OfType<SubShaderSyntax>().Single().Body.Statements.OfType<CommandSyntax>()];

        Assert.Equal(2, commands.Count);
        Assert.Equal("Cull", commands[0].Name);
        Assert.Equal("Back", Assert.IsType<LiteralArgumentSyntax>(Assert.Single(commands[0].ValueArguments)).Text);
        Assert.Equal("ZWrite", commands[1].Name);
    }

    [Fact]
    public void 行末のカンマは次の行への継続とみなす()
    {
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { SubShader {
            Blend One Zero,
                  One One
            } }
            """);

        CommandSyntax blend = tree.Root.Shader!.Body.Statements
            .OfType<SubShaderSyntax>().Single().Body.Statements.OfType<CommandSyntax>().Single();

        Assert.Equal("Blend", blend.Name);
        Assert.Equal(["One", "Zero", "One", "One"],
            blend.ValueArguments.Cast<LiteralArgumentSyntax>().Select(a => a.Text));
    }

    [Fact]
    public void レンダーステートのプロパティ参照を解釈できる()
    {
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { SubShader {
            Blend [_SrcBlend] [_DstBlend]
            } }
            """);

        CommandSyntax blend = tree.Root.Shader!.Body.Statements
            .OfType<SubShaderSyntax>().Single().Body.Statements.OfType<CommandSyntax>().Single();

        Assert.Equal(["_SrcBlend", "_DstBlend"],
            blend.ValueArguments.Cast<PropertyReferenceArgumentSyntax>().Select(a => a.Name));
    }

    [Fact]
    public void キーワードは大文字小文字を区別しない()
    {
        // Unity の ShaderLab は命令名の大小を区別しない。区別する実装にすると、
        // 正しく動作しているシェーダーに誤ったエラーを出すことになる。
        ShaderLabSyntaxTree tree = Parse("""
            shader "X" { subshader { pass { } } }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, Describe(tree.Diagnostics));
        Assert.NotNull(tree.Root.Shader);
        Assert.Single(tree.Root.Shader!.Body.Statements.OfType<SubShaderSyntax>().Single().Passes);
    }

    [Fact]
    public void 未知の命令は構文エラーにしない()
    {
        // Unity のバージョンで命令は増える。知らない命令をエラーにすると、
        // ツールが未対応なだけのシェーダーを壊れていると誤判定してしまう。
        ShaderLabSyntaxTree tree = Parse("""
            Shader "X" { SubShader { SomeFutureCommand WithAnArgument } }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, Describe(tree.Diagnostics));
    }
}

public sealed class ShaderLabErrorRecoveryTests
{
    [Fact]
    public void 壊れたプロパティの前後にある正しい宣言は解析される()
    {
        // 1 つの書き損じが以降のプロパティすべてを巻き添えにしてはならない。
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(Fixtures.Load("Broken.shader"));

        PropertiesBlockSyntax properties = tree.Root.Shader!
            .DescendantNodesAndSelf().OfType<PropertiesBlockSyntax>().Single();

        List<string> names = [.. properties.Properties.Select(p => p.Name)];

        Assert.Contains("_Good", names);
        Assert.Contains("_AfterGarbage", names);
        Assert.Contains("_Recovered", names);
    }

    [Fact]
    public void 壊れた記述を次の宣言の型名として食べてしまわない()
    {
        // 型名の位置は任意の識別子を受け入れるため、見切り発車で宣言の解析を始めると
        // 次の宣言の名前を型名として消費してしまう。
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(SourceText.From("""
            Shader "X" { Properties {
                これは プロパティ ではない
                _Survivor ("生き残るべき宣言", Float) = 1
            } }
            """, "x.shader"));

        PropertiesBlockSyntax properties = tree.Root.Shader!
            .DescendantNodesAndSelf().OfType<PropertiesBlockSyntax>().Single();

        PropertyDeclarationSyntax survivor = Assert.Single(properties.Properties);
        Assert.Equal("_Survivor", survivor.Name);
        Assert.Equal("Float", survivor.Type.TypeName);
    }

    [Fact]
    public void 壊れた入力では構文エラーが報告される()
    {
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(Fixtures.Load("Broken.shader"));

        Assert.NotEmpty(tree.Diagnostics);
        Assert.All(tree.Diagnostics, d => Assert.Equal("SL0001", d.Id));
    }

    [Fact]
    public void 欠落トークンは長さ0で位置を持つ()
    {
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(SourceText.From("Shader { }", "x.shader"));

        SyntaxToken name = tree.Root.Shader!.NameToken;
        Assert.True(name.IsMissing);
        Assert.Equal(0, name.Span.Length);
    }

    [Fact]
    public void 閉じ括弧が無くても解析は完了する()
    {
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(
            SourceText.From("Shader \"X\" { SubShader { Pass {", "x.shader"));

        Assert.NotNull(tree.Root.Shader);
        Assert.NotEmpty(tree.Diagnostics);
    }
}
