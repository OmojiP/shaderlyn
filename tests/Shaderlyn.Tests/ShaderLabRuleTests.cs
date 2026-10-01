using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Rules;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// ルールを 1 つだけ動かして診断を取り出すための補助。
/// </summary>
internal static class RuleTestHarness
{
    /// <summary>指定したアナライザだけを適用して診断を得る。</summary>
    public static ImmutableArray<Diagnostic> Analyze(DiagnosticAnalyzer analyzer, string content)
    {
        SourceText text = SourceText.From(content, "test.shader");
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);
        AnalysisTarget unit = new(text, tree.Root, []);
        return new AnalyzerDriver([analyzer]).Analyze(unit);
    }

    /// <summary>指定したアナライザを適用し、報告されたルール ID の一覧を返す。</summary>
    public static string[] Ids(DiagnosticAnalyzer analyzer, string content)
        => [.. Analyze(analyzer, content).Select(d => d.Id)];

    /// <summary>構文エラーが混入していないことを確認したうえで診断を得る。</summary>
    public static ImmutableArray<Diagnostic> AnalyzeValid(DiagnosticAnalyzer analyzer, string content)
    {
        SourceText text = SourceText.From(content, "test.shader");
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        Assert.True(
            tree.Diagnostics.IsEmpty,
            $"テスト入力自体に構文エラーがあります:\n{string.Join("\n", tree.Diagnostics.Select(d => d.GetMessage()))}");

        AnalysisTarget unit = new(text, tree.Root, []);
        return new AnalyzerDriver([analyzer]).Analyze(unit);
    }
}

public sealed class TagAnalyzerTests
{
    [Fact]
    public void 既知のタグに近いスペルミスを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = RuleTestHarness.AnalyzeValid(new TagAnalyzer(), """
            Shader "X" { SubShader { Tags { "RendrType" = "Opaque" } } }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SL1010", diagnostic.Id);
        Assert.Contains("RendrType", diagnostic.GetMessage(), StringComparison.Ordinal);
        // 正しい候補を提示すること。
        Assert.Contains("RenderType", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TerrainCompatible")]
    [InlineData("MyCustomPipelineTag")]
    [InlineData("SomethingEntirelyDifferent")]
    [InlineData("Size")]
    public void 既知のタグから遠い名前は報告しない(string tagKey)
    {
        // タグの名前空間は開かれており、各機能が独自のタグを自由に追加する。
        // 「一覧に無いから誤り」とする実装では、Unity 同梱のシェーダーに対してだけでも
        // 56 件の誤検出が出ていた。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TagAnalyzer(), $$"""
            Shader "X" { SubShader { Tags { "{{tagKey}}" = "Value" } } }
            """));
    }

    [Theory]
    [InlineData("LightMod", "LightMode")]
    [InlineData("RenderPipline", "RenderPipeline")]
    [InlineData("DisableBatchng", "DisableBatching")]
    public void よくあるスペルミスを検出して候補を示す(string wrong, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = RuleTestHarness.AnalyzeValid(new TagAnalyzer(), $$"""
            Shader "X" { SubShader { Tags { "{{wrong}}" = "Value" } } }
            """);

        Assert.Contains(expected, Assert.Single(diagnostics).GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 既知のタグ名は報告しない()
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TagAnalyzer(), """
            Shader "X" { SubShader { Tags {
                "RenderType" = "Opaque"
                "Queue" = "Geometry"
                "RenderPipeline" = "UniversalPipeline"
                "IgnoreProjector" = "True"
                "DisableBatching" = "LODFading"
                "PreviewType" = "Plane"
            } } }
            """));
    }

    [Theory]
    [InlineData("Geometry")]
    [InlineData("Transparent")]
    [InlineData("Transparent+100")]
    [InlineData("Geometry-1")]
    [InlineData("2500")]
    public void 妥当な描画キューは報告しない(string queue)
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TagAnalyzer(), $$"""
            Shader "X" { SubShader { Tags { "Queue" = "{{queue}}" } } }
            """));
    }

    [Theory]
    [InlineData("Transparnt")]
    [InlineData("")]
    [InlineData("Transparent+abc")]
    public void 不正な描画キューを報告する(string queue)
    {
        string[] ids = RuleTestHarness.Ids(new TagAnalyzer(), $$"""
            Shader "X" { SubShader { Tags { "Queue" = "{{queue}}" } } }
            """);

        Assert.Equal(["SL1011"], ids);
    }

    [Fact]
    public void 真偽値タグに真偽値以外を指定すると報告する()
    {
        Assert.Equal(["SL1011"], RuleTestHarness.Ids(new TagAnalyzer(), """
            Shader "X" { SubShader { Tags { "IgnoreProjector" = "Yes" } } }
            """));
    }

    [Fact]
    public void RenderTypeのスペルミスを報告する()
    {
        // Unity はスペルミスを報告しないため、
        // RenderType を見る仕組み (シェーダー置き換えなど) から漏れる。
        string[] ids = RuleTestHarness.Ids(new TagAnalyzer(), """
            Shader "X" { SubShader { Tags { "RenderType" = "Opaqu" } } }
            """);

        Assert.Equal(["SL1012"], ids);
    }

    [Fact]
    public void RenderTypeのスペルミスには正しい候補を示す()
    {
        Diagnostic diagnostic = Assert.Single(RuleTestHarness.Analyze(new TagAnalyzer(), """
            Shader "X" { SubShader { Tags { "RenderType" = "Transparnt" } } }
            """));

        Assert.Equal("SL1012", diagnostic.Id);
        Assert.Contains("Transparent", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Opaque")]
    [InlineData("TransparentCutout")]
    [InlineData("GrassBillBoard")]
    [InlineData("HDLitShader")]
    [InlineData("HDUnlitShader")]
    public void 既知のRenderTypeは報告しない(string renderType)
    {
        // HDRP の 2 つは互いに編集距離 2 である。
        // 片方だけを既知としていると、もう片方がスペルミスとして報告される。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TagAnalyzer(), $$"""
            Shader "X" { SubShader { Tags { "RenderType" = "{{renderType}}" } } }
            """));
    }

    [Theory]
    [InlineData("MyCustomType")]
    [InlineData("Foliage")]
    [InlineData("UI")]
    public void 既知の値から遠いRenderTypeは報告しない(string renderType)
    {
        // RenderType は Camera.RenderWithShader による置き換え用に
        // プロジェクトが自由な値を使える。「一覧に無いから誤り」とはできない。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TagAnalyzer(), $$"""
            Shader "X" { SubShader { Tags { "RenderType" = "{{renderType}}" } } }
            """));
    }
}

public sealed class PassNameAnalyzerTests
{
    [Fact]
    public void 同じSubShader内の重複したPass名を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = RuleTestHarness.AnalyzeValid(new PassNameAnalyzer(), """
            Shader "X" { SubShader {
                Pass { Name "Forward" }
                Pass { Name "Forward" }
            } }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SL1041", diagnostic.Id);
        // 2 つ目の Pass の位置に報告されること。
        Assert.Equal(2, diagnostic.Location.LineSpan.Start.Line);
    }

    [Fact]
    public void 大文字小文字だけが異なるPass名も重複とみなす()
    {
        // Unity は Pass 名を大文字へ正規化して保持するため、両者は同じ名前になる。
        Assert.Equal(["SL1041"], RuleTestHarness.Ids(new PassNameAnalyzer(), """
            Shader "X" { SubShader {
                Pass { Name "Forward" }
                Pass { Name "FORWARD" }
            } }
            """));
    }

    [Fact]
    public void 別のSubShaderにある同名のPassは報告しない()
    {
        // 品質レベルごとに同じ役割の Pass を用意するのは通常の書き方である。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new PassNameAnalyzer(), """
            Shader "X" {
                SubShader { Pass { Name "Forward" } }
                SubShader { Pass { Name "Forward" } }
            }
            """));
    }

    [Fact]
    public void 名前の無いPassは報告しない()
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new PassNameAnalyzer(), """
            Shader "X" { SubShader { Pass { } Pass { } } }
            """));
    }
}

public sealed class TransparencyAnalyzerTests
{
    [Fact]
    public void 半透明でZWriteを指定し忘れると報告する()
    {
        // ZWrite の既定値は On であるため、書き忘れがそのまま不具合になる。
        ImmutableArray<Diagnostic> diagnostics = RuleTestHarness.AnalyzeValid(new TransparencyAnalyzer(), """
            Shader "X" { SubShader {
                Tags { "Queue" = "Transparent" }
                Blend SrcAlpha OneMinusSrcAlpha
                Pass { }
            } }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SL1020", diagnostic.Id);
        Assert.Contains("Transparent", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 半透明でZWriteをOnと明示していると報告する()
    {
        Assert.Equal(["SL1020"], RuleTestHarness.Ids(new TransparencyAnalyzer(), """
            Shader "X" { SubShader {
                Tags { "Queue" = "Transparent" }
                Blend SrcAlpha OneMinusSrcAlpha
                Pass { ZWrite On }
            } }
            """));
    }

    [Fact]
    public void 半透明でZWriteをOffにしていれば報告しない()
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TransparencyAnalyzer(), """
            Shader "X" { SubShader {
                Tags { "Queue" = "Transparent" }
                Blend SrcAlpha OneMinusSrcAlpha
                ZWrite Off
                Pass { }
            } }
            """));
    }

    [Fact]
    public void Pass側のZWriteOffがSubShader側のOnを上書きする()
    {
        // 内側の指定が外側を上書きするという ShaderLab の規則に従う必要がある。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TransparencyAnalyzer(), """
            Shader "X" { SubShader {
                Tags { "Queue" = "Transparent" }
                Blend SrcAlpha OneMinusSrcAlpha
                ZWrite On
                Pass { ZWrite Off }
            } }
            """));
    }

    [Fact]
    public void Category階層のレンダーステートも考慮する()
    {
        // 旧来のシェーダーは共通のレンダーステートを Category 階層に置く。
        // これを見落とすと、正しく設定されているシェーダーを誤検出することになる。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TransparencyAnalyzer(), """
            Shader "X" { Category {
                Tags { "Queue" = "Transparent" }
                Blend SrcAlpha OneMinusSrcAlpha
                ZWrite Off
                SubShader { Pass { } }
            } }
            """));
    }

    [Fact]
    public void 不透明キューでは報告しない()
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TransparencyAnalyzer(), """
            Shader "X" { SubShader {
                Tags { "Queue" = "Geometry" }
                Blend SrcAlpha OneMinusSrcAlpha
                Pass { }
            } }
            """));
    }

    [Fact]
    public void ブレンドが無効なら報告しない()
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TransparencyAnalyzer(), """
            Shader "X" { SubShader {
                Tags { "Queue" = "Transparent" }
                Blend Off
                Pass { }
            } }
            """));
    }

    [Fact]
    public void ZWriteをプロパティで切り替えている場合は報告しない()
    {
        // 実際の値はマテリアル側で決まるため静的には判断できない。
        // 判断できないものを誤りとして報告してはならない。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new TransparencyAnalyzer(), """
            Shader "X" { SubShader {
                Tags { "Queue" = "Transparent" }
                Blend SrcAlpha OneMinusSrcAlpha
                Pass { ZWrite [_ZWrite] }
            } }
            """));
    }

    [Fact]
    public void 数値指定の描画キューも半透明として扱う()
    {
        Assert.Equal(["SL1020"], RuleTestHarness.Ids(new TransparencyAnalyzer(), """
            Shader "X" { SubShader {
                Tags { "Queue" = "3100" }
                Blend SrcAlpha OneMinusSrcAlpha
                Pass { }
            } }
            """));
    }
}

public sealed class RenderStateValueAnalyzerTests
{
    [Theory]
    [InlineData("Cull Backk")]
    [InlineData("ZWrite Onn")]
    [InlineData("ZTest LEqul")]
    [InlineData("Blend SrcAlpha OneMinusSrcAlpa")]
    [InlineData("BlendOp Addd")]
    public void 不正なレンダーステートの値を報告する(string command)
    {
        Assert.Equal(["SL1021"], RuleTestHarness.Ids(new RenderStateValueAnalyzer(), $$"""
            Shader "X" { SubShader { Pass { {{command}} } } }
            """));
    }

    [Theory]
    [InlineData("Cull Off")]
    [InlineData("Cull front")]
    [InlineData("ZWrite Off")]
    [InlineData("ZTest LEqual")]
    [InlineData("Blend Off")]
    [InlineData("Blend SrcAlpha OneMinusSrcAlpha")]
    [InlineData("Blend One Zero, One One")]
    [InlineData("BlendOp Add, Max")]
    public void 妥当なレンダーステートの値は報告しない(string command)
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new RenderStateValueAnalyzer(), $$"""
            Shader "X" { SubShader { Pass { {{command}} } } }
            """));
    }

    [Fact]
    public void プロパティ参照の値は検証しない()
    {
        // マテリアル側で決まる値を静的に判断することはできない。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new RenderStateValueAnalyzer(), """
            Shader "X" { SubShader { Pass { Cull [_Cull] ZWrite [_ZWrite] } } }
            """));
    }

    [Fact]
    public void 数値の引数は検証しない()
    {
        // Blend の先頭の数値はレンダーターゲット番号であり、係数名ではない。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new RenderStateValueAnalyzer(), """
            Shader "X" { SubShader { Pass { Blend 1 SrcAlpha OneMinusSrcAlpha } } }
            """));
    }

    [Fact]
    public void 未知の命令は検証しない()
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new RenderStateValueAnalyzer(), """
            Shader "X" { SubShader { Pass { SomeFutureCommand AnyValue } } }
            """));
    }

    [Fact]
    public void ステンシルの操作値を検証する()
    {
        Assert.Equal(["SL1021"], RuleTestHarness.Ids(new RenderStateValueAnalyzer(), """
            Shader "X" { SubShader { Pass { Stencil { Ref 2 Comp Equal Pass Kep } } } }
            """));
    }

    [Fact]
    public void 一行に複数の命令を書いても正しく切り分けられる()
    {
        // 行末だけを命令の終端とみなすと、Cull が後続の ZWrite Off まで
        // 引数として飲み込み、'ZWrite' が Cull の不正な値として誤報告される。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new RenderStateValueAnalyzer(), """
            Shader "X" { SubShader { Pass { Cull Off ZWrite Off ZTest LEqual } } }
            """));
    }

    [Fact]
    public void ステンシルの妥当な設定は報告しない()
    {
        // Stencil の Pass は操作名を取り、Pass ブロックのレンダーステートとは別物である。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new RenderStateValueAnalyzer(), """
            Shader "X" { SubShader { Pass { Stencil {
                Ref 2
                Comp Equal
                Pass Keep
                Fail Keep
                ZFail DecrSat
            } } } }
            """));
    }
}

public sealed class PropertyRuleTests
{
    [Fact]
    public void アンダースコアで始まらないプロパティ名を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = RuleTestHarness.AnalyzeValid(new PropertyNamingAnalyzer(), """
            Shader "X" { Properties { MainTex ("Main", 2D) = "white" {} } }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SL1030", diagnostic.Id);
        Assert.Contains("MainTex", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void アンダースコアで始まるプロパティ名は報告しない()
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new PropertyNamingAnalyzer(), """
            Shader "X" { Properties { _MainTex ("Main", 2D) = "white" {} } }
            """));
    }

    [Theory]
    [InlineData("[KeywordEnum] _P (\"P\", Float) = 0")]
    [InlineData("[PowerSlider] _P (\"P\", Range(0, 1)) = 0")]
    [InlineData("[Header] _P (\"P\", Float) = 0")]
    [InlineData("[MainColor(Extra)] _P (\"P\", Color) = (1,1,1,1)")]
    [InlineData("[Enum(A, 0, B)] _P (\"P\", Float) = 0")]
    public void 属性の引数の個数が不正なら報告する(string declaration)
    {
        Assert.Equal(["SL1031"], RuleTestHarness.Ids(new PropertyAttributeAnalyzer(), $$"""
            Shader "X" { Properties { {{declaration}} } }
            """));
    }

    [Theory]
    [InlineData("[MainTexture] _P (\"P\", 2D) = \"white\" {}")]
    [InlineData("[Toggle] _P (\"P\", Float) = 0")]
    [InlineData("[Toggle(_KEYWORD)] _P (\"P\", Float) = 0")]
    [InlineData("[KeywordEnum(None, Add, Multiply)] _P (\"P\", Float) = 0")]
    [InlineData("[Enum(UnityEngine.Rendering.CullMode)] _P (\"P\", Float) = 2")]
    [InlineData("[Enum(Off, 0, On, 1)] _P (\"P\", Float) = 0")]
    [InlineData("[PowerSlider(3.0)] _P (\"P\", Range(0, 1)) = 0")]
    public void 属性の引数が妥当なら報告しない(string declaration)
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new PropertyAttributeAnalyzer(), $$"""
            Shader "X" { Properties { {{declaration}} } }
            """));
    }

    [Fact]
    public void 未知の属性名は報告しない()
    {
        // MaterialPropertyDrawer を継承すれば属性はプロジェクトごとに追加できる。
        // 未知の名前を報告すると、独自ドロワーを使うプロジェクトで大量の誤検出が出る。
        Assert.Empty(RuleTestHarness.AnalyzeValid(new PropertyAttributeAnalyzer(), """
            Shader "X" { Properties { [MyCustomDrawer(1, 2, 3)] _P ("P", Float) = 0 } }
            """));
    }
}

public sealed class ShaderNameAnalyzerTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" Leading")]
    [InlineData("Trailing ")]
    [InlineData("/Leading")]
    [InlineData("Trailing/")]
    [InlineData("A//B")]
    [InlineData("A/ B")]
    public void 構造が不正なシェーダー名を報告する(string name)
    {
        Assert.Equal(["SL1040"], RuleTestHarness.Ids(new ShaderNameAnalyzer(), $$"""
            Shader "{{name}}" { }
            """));
    }

    [Theory]
    [InlineData("Simple")]
    [InlineData("Company/Category/Name")]
    [InlineData("Universal Render Pipeline/Lit")]
    public void 妥当なシェーダー名は報告しない(string name)
    {
        Assert.Empty(RuleTestHarness.AnalyzeValid(new ShaderNameAnalyzer(), $$"""
            Shader "{{name}}" { }
            """));
    }
}

public sealed class RealisticShaderRuleTests
{
    /// <summary>
    /// 現実的で正しく書かれたシェーダーに対して、指摘が 1 件も出ないことを検証する。
    /// </summary>
    /// <remarks>
    /// リンタにとって最も重要な性質は「正しいコードを誤りと言わないこと」である。
    /// 誤検出が出るルールは、そのルールだけでなくツール全体が信用されなくなり、
    /// 最終的に丸ごと無効化される。
    /// </remarks>
    [Theory]
    [InlineData("UrpLit.shader")]
    [InlineData("BuiltInLegacy.shader")]
    public void 正しく書かれたシェーダーには指摘を出さない(string fileName)
    {
        SourceText text = Fixtures.Load(fileName);
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);
        AnalysisTarget unit = new(text, tree.Root, tree.Diagnostics);

        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(unit);

        Assert.True(
            diagnostics.IsEmpty,
            $"{fileName} で誤検出が発生しました:\n"
            + string.Join("\n", diagnostics.Select(d => $"  {d.Location.LineSpan.Start} {d.Id}: {d.GetMessage()}")));
    }
}
