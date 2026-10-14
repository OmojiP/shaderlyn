using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Unity;

namespace Shaderlyn.Tests;

/// <summary>
/// 構成の空間 (<see cref="VariantSpace"/>) と、Unity の <c>#pragma</c> からそれを作る読み取りの検証。
/// </summary>
/// <remarks>
/// 読み取りは、これまでの <see cref="ShaderSymbols"/> の各関数と同じ答えを出さなければならない。
/// 解析と規則をこの形へ移していくとき、答えが変わらないことの裏付けになる。
/// </remarks>
public sealed class VariantSpaceTests
{
    /// <summary>宣言の書き方を一通り含むコード。</summary>
    private const string Declarations = """
        #pragma multi_compile_local_fragment _ _X _Y
        #pragma shader_feature _Z
        #pragma multi_compile __ _P _Q _R
        #pragma multi_compile MODE_A MODE_B
        #pragma dynamic_branch _HDR_OVERLAY
        #pragma multi_compile_fog
        #pragma multi_compile_fwdbase nolightmap
        #pragma vertex vert
        #pragma shader_feature _Z
        """;

    [Fact]
    public void 展開前のトークンから読んだ空間はこれまでの関数と同じ答えを出す()
    {
        ImmutableArray<HlslSyntaxToken> tokens = Lex(Declarations);
        VariantSpace space = ShaderSymbols.ReadVariantSpaceFromTokens(tokens);

        Assert.Equal(ShaderSymbols.CollectDeclaredFromTokens(tokens).Order(), space.Symbols.Order());
        AssertSameConstraints(ShaderSymbols.CollectConstraintsFromTokens(tokens), space.Constraints);
    }

    [Fact]
    public void 展開後のpragmaから読んだ空間はこれまでの関数と同じ答えを出す()
    {
        ImmutableArray<PragmaDirective> pragmas = Preprocess(Declarations);
        VariantSpace space = ShaderSymbols.ReadVariantSpace(pragmas);

        Assert.Equal(ShaderSymbols.CollectDeclared(pragmas).Order(), space.Symbols.Order());
        AssertSameConstraints(ShaderSymbols.CollectConstraints(pragmas), space.Constraints);

        // 行の単位も同じ。
        Assert.Equal(
            ShaderSymbols.CollectGroups(pragmas).Select(g => new VariantGroup(g.Symbols, g.RequiresOne)),
            space.Groups);

        // 名前が書かれた宣言は、名前のトークンの位置を持つ。同じ名前の宣言は宣言の数だけ並ぶ。
        Assert.Equal(
            ShaderSymbols.EnumerateDeclared(pragmas).Select(t => (t.Text, t.Span.Start)),
            space.Declarations.Where(d => !d.IsImplicit).Select(d => (d.Name, d.Location!.Span.Start)));

        // 組み込みの宣言が作るシンボルは、宣言の名前の位置を持つ。
        Assert.Equal(
            ShaderSymbols.EnumerateBuiltInDeclared(pragmas).Select(p => (p.Symbol, p.Declaration.Span.Start)),
            space.Declarations.Where(d => d.IsImplicit).Select(d => (d.Name, d.Location!.Span.Start)));
    }

    /// <summary>
    /// Unity 同梱のシェーダーすべてで、これまでの関数と同じ答えを出すことを検証する。
    /// </summary>
    /// <remarks>字句解析だけで足りるので、全件を見る。Unity が無ければ飛ばす。</remarks>
    [Fact]
    public void Unity同梱のシェーダーでもこれまでの関数と同じ答えを出す()
    {
        if (UnityCorpusTests.FindUnityEditorData() is not { } editorData)
        {
            return;
        }

        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        int checkedFiles = 0;

        foreach (string path in Directory.EnumerateFiles(editorData, "*.shader", enumeration)
                     .Concat(Directory.EnumerateFiles(editorData, "*.hlsl", enumeration)))
        {
            ImmutableArray<HlslSyntaxToken> tokens = Lex(File.ReadAllText(path));
            VariantSpace space = ShaderSymbols.ReadVariantSpaceFromTokens(tokens);

            Assert.True(
                ShaderSymbols.CollectDeclaredFromTokens(tokens).SetEquals(space.Symbols),
                path);
            AssertSameConstraints(ShaderSymbols.CollectConstraintsFromTokens(tokens), space.Constraints);
            checkedFiles++;
        }

        Assert.True(checkedFiles > 0);
    }

    [Fact]
    public void dynamic_branchの宣言は実行時の分岐として持つ()
    {
        VariantSpace space = ShaderSymbols.ReadVariantSpace(Preprocess(Declarations));

        Assert.Equal(VariantSymbolKind.Runtime, Assert.Single(space.Declarations, d => d.Name == "_HDR_OVERLAY").Kind);
        Assert.All(space.Declarations.Where(d => d.Name != "_HDR_OVERLAY"), d => Assert.Equal(VariantSymbolKind.Compiled, d.Kind));
    }

    [Fact]
    public void 組み込みの宣言は書かれていない名前も宣言し除く指定を守る()
    {
        VariantSpace space = ShaderSymbols.ReadVariantSpace(Preprocess(Declarations));

        Assert.True(space.IsDeclared("FOG_EXP2"));
        Assert.True(space.IsDeclared("DIRECTIONAL"));

        // nolightmap は LIGHTMAP_ON を作らせない。
        Assert.False(space.IsDeclared("LIGHTMAP_ON"));
        Assert.Contains(new VariantGroup(["FOG_LINEAR", "FOG_EXP", "FOG_EXP2"], RequiresOne: false), space.Groups);
    }

    [Fact]
    public void 空の空間は何も宣言しない()
    {
        Assert.True(VariantSpace.Empty.IsEmpty);
        Assert.True(VariantSpace.Empty.Constraints.IsEmpty);
        Assert.False(VariantSpace.Empty.IsDeclared("_X"));
    }

    /// <summary>2 つの制約が同じ組を持つことを確かめる。</summary>
    /// <param name="expected">これまでの関数が作った制約。</param>
    /// <param name="actual">構成の空間が作った制約。</param>
    private static void AssertSameConstraints(SymbolConstraints expected, SymbolConstraints actual)
    {
        Assert.Equal(expected.ExclusivePairs.Order(), actual.ExclusivePairs.Order());
        Assert.Equal(
            expected.RequiredGroups.Select(g => string.Join(",", g)),
            actual.RequiredGroups.Select(g => string.Join(",", g)));
    }

    /// <summary>字句解析する。</summary>
    /// <param name="code">コード。</param>
    /// <returns>トークン。</returns>
    private static ImmutableArray<HlslSyntaxToken> Lex(string code)
        => new HlslLexer(SourceText.From(code, "test.hlsl")).Lex(out _);

    /// <summary>展開して <c>#pragma</c> を集める。</summary>
    /// <param name="code">コード。</param>
    /// <returns>集めた <c>#pragma</c>。</returns>
    private static ImmutableArray<PragmaDirective> Preprocess(string code)
        => HlslSyntaxTree.Parse(SourceText.From(code, "test.hlsl")).Pragmas;
}
