using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.LanguageServer;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// 補完の候補が、カーソルの位置で書けるものに限られることの検証。
/// </summary>
/// <remarks>
/// 以前は <c>.</c> の直後かどうかしか見ず、<c>#</c> の後ろやコメントの中でも
/// 組み込み関数や型を出していた。
/// </remarks>
public sealed class CompletionTests
{
    /// <summary>カーソルの位置を表す目印。</summary>
    private const char Cursor = '|';

    [Theory]
    [InlineData("#|")]
    [InlineData("#i|")]
    [InlineData("  # pr|")]
    public void 指令の名前を書く位置では指令の名前だけを出す(string line)
    {
        ImmutableArray<CompletionItem> items = CompleteHlsl(line);

        Assert.Contains(items, i => i.Label == "ifdef");
        Assert.Contains(items, i => i.Label == "pragma");
        Assert.DoesNotContain(items, i => i.Label == "saturate");
        Assert.DoesNotContain(items, i => i.Label == "float4");
    }

    [Theory]
    [InlineData("#pragma |")]
    [InlineData("#pragma multi|")]
    public void pragmaの後ろではpragmaの名前を出す(string line)
    {
        ImmutableArray<CompletionItem> items = CompleteHlsl(line);

        Assert.Contains(items, i => i.Label == "multi_compile_local_fragment");
        Assert.Contains(items, i => i.Label == "vertex" && i.Documentation is not null);
        Assert.DoesNotContain(items, i => i.Label == "saturate");
    }

    [Fact]
    public void エントリポイントの指定ではこのファイルの関数を出す()
    {
        ImmutableArray<CompletionItem> items = CompleteHlsl(
            "float4 vert(float4 p : POSITION) : SV_POSITION { return p; }",
            "#pragma vertex v|");

        Assert.Contains(items, i => i.Label == "vert");
        Assert.DoesNotContain(items, i => i.Label == "saturate");
    }

    [Theory]
    [InlineData("#ifdef |")]
    [InlineData("#if defined(_A) && |")]
    [InlineData("#elif |")]
    public void 条件ではシンボルとマクロを出す(string line)
    {
        ImmutableArray<CompletionItem> items = CompleteHlsl(
            "#pragma multi_compile _ _A",
            "#define MY_MACRO 1",
            line);

        Assert.Contains(items, i => i.Label == "_A" && i.Detail == "シェーダーのシンボル");
        Assert.Contains(items, i => i.Label == "MY_MACRO");
        Assert.DoesNotContain(items, i => i.Label == "saturate");
        Assert.DoesNotContain(items, i => i.Label == "float4");
        Assert.Equal(!line.StartsWith("#ifdef", StringComparison.Ordinal), items.Any(i => i.Label == "defined"));
    }

    [Theory]
    [InlineData("#include \"|")]
    [InlineData("#pragma multi_compile _ |")]
    [InlineData("#pragma vertex vert |")]
    [InlineData("#define |")]
    [InlineData("#define F(|")]
    [InlineData("#ifdef _A |")]
    [InlineData("#endif |")]
    [InlineData("// sat|")]
    [InlineData("/* sat|")]
    [InlineData("float x; // sat|")]
    public void 書ける名前が無い位置では何も出さない(string line)
    {
        Assert.Empty(CompleteHlsl(line));
    }

    [Theory]
    [InlineData("#define SQUARE(x) sat|")]
    [InlineData("#define TWICE x + sat|")]
    [InlineData("float x = sat|")]
    [InlineData("/* c */ float x = sat|")]
    public void コードを書く位置では組み込み関数を出す(string line)
    {
        Assert.Contains(CompleteHlsl(line), i => i.Label == "saturate");
    }

    [Fact]
    public void 前の行の指令はコードの行に影響しない()
    {
        ImmutableArray<CompletionItem> items = CompleteHlsl(
            "#pragma vertex vert",
            "float x = sat|");

        Assert.Contains(items, i => i.Label == "saturate");
    }

    [Fact]
    public void ShaderLabの部分ではHLSLの候補を出さない()
    {
        const string Source = """
            Shader "Test/Completion"
            {
                Properties
                {
                    _Color("Color", Color) = (1, 1, 1, 1)
                    sat|
                }
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        float x = sat|
                        ENDHLSL
                    }
                }
            }
            """;

        int first = Source.IndexOf(Cursor, StringComparison.Ordinal);
        string text = Source.Remove(first, 1);
        int second = text.IndexOf(Cursor, StringComparison.Ordinal);
        text = text.Remove(second, 1);

        SourceText source = SourceText.From(text, Path.Combine("Assets", "Completion.shader"));
        ShaderCompilation compilation = ShaderCompilation.Create(source, ShaderLabSyntaxTree.Parse(source));

        Assert.Empty(CompletionBuilder.Build(compilation, first));
        Assert.Contains(CompletionBuilder.Build(compilation, second), i => i.Label == "saturate");
    }

    /// <summary>HLSL 単体のファイルとして、目印の位置の候補を求める。</summary>
    /// <param name="lines">ファイルの行。どれか 1 つに目印を含める。</param>
    /// <returns>候補。</returns>
    private static ImmutableArray<CompletionItem> CompleteHlsl(params string[] lines)
    {
        string joined = string.Join("\n", lines);
        int offset = joined.IndexOf(Cursor, StringComparison.Ordinal);
        string text = joined.Remove(offset, 1);

        ShaderCompilation compilation = ShaderCompilation.CreateForHlsl(SourceText.From(text, "Completion.hlsl"));

        return CompletionBuilder.Build(compilation, offset);
    }
}
