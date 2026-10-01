using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// ShaderLab の入れ物と宣言の説明。
/// </summary>
internal static partial class HoverBuilder
{
    /// <summary>
    /// <c>Shader</c> の宣言を説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="shader">対象の宣言。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// シェーダー名はマテリアルの選択メニューの階層そのものである。
    /// 最後の区切りより後ろが実際に並ぶ名前になる。
    /// </remarks>
    private static HoverResult DescribeShaderDeclaration(
        ShaderCompilation compilation,
        ShaderDeclarationSyntax shader)
    {
        int separator = shader.Name.LastIndexOf('/');

        string menu = separator > 0
            ? $"メニュー: `{shader.Name[..separator]}` の下に `{shader.Name[(separator + 1)..]}` として並ぶ"
            : "メニューの階層が無い。`分類/名前` の形にすると探しやすくなる";

        return new HoverResult(
            $"```shaderlab\nShader \"{shader.Name}\"\n```\n\n{menu}"
            + $"\n\nレンダーパイプライン: {compilation.Profile.DisplayName}",
            shader.NameToken.Span);
    }

    /// <summary><c>SubShader</c> を説明する。</summary>
    /// <param name="subShader">対象の SubShader。</param>
    /// <returns>説明。</returns>
    /// <remarks>Unity は上から順に、動く最初の SubShader を選ぶ。</remarks>
    private static HoverResult DescribeSubShader(SubShaderSyntax subShader)
        => new(
            $"```shaderlab\nSubShader\n```\n\n{subShader.Passes.Count()} 件の Pass。"
            + "\n\nUnity は上から順に見て、動く最初の SubShader を使う。",
            subShader.Keyword.Span);

    /// <summary>
    /// <c>Pass</c> を説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="pass">対象の Pass。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// <c>LightMode</c> はこの Pass がいつ描かれるかを決める。
    /// 書かれていないと既定の扱いになり、意図した段階で描かれないことがある。
    /// </remarks>
    private static HoverResult DescribePass(ShaderCompilation compilation, PassSyntax pass)
    {
        string name = pass.Body.Statements
            .OfType<CommandSyntax>()
            .Where(c => string.Equals(c.Name, "Name", StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Arguments.OfType<LiteralArgumentSyntax>())
            .Select(a => a.Token.ValueText)
            .FirstOrDefault() ?? "(名前なし)";

        string lightMode = pass.Body.Statements
            .OfType<TagsBlockSyntax>()
            .SelectMany(t => t.Tags)
            .Where(t => string.Equals(t.Key, "LightMode", StringComparison.OrdinalIgnoreCase))
            .Select(t => $"`{t.Value}`")
            .FirstOrDefault() ?? "指定なし";

        return new HoverResult(
            $"```shaderlab\nPass  // {name}\n```\n\nLightMode: {lightMode}"
            + $"\n\nレンダーパイプライン: {compilation.Profile.DisplayName}",
            pass.Keyword.Span);
    }

    /// <summary>
    /// プロパティの型を説明する。
    /// </summary>
    /// <param name="type">対象の型。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// <b>HLSL 側で受ける型が分からないと対応が取れない。</b>
    /// <c>Color</c> を <c>float</c> で受けるといった食い違いは、
    /// 実行して初めて「値が届かない」形で現れる。
    /// </remarks>
    private static HoverResult DescribePropertyType(PropertyTypeSyntax type)
    {
        string hlsl = type.TypeName.ToUpperInvariant() switch
        {
            "COLOR" or "VECTOR" => "HLSL 側は `float4` / `half4` で受ける",
            "FLOAT" or "RANGE" => "HLSL 側は `float` / `half` で受ける",
            "INT" or "INTEGER" => "HLSL 側は `int` で受ける",
            "2D" => "HLSL 側は `TEXTURE2D` と `SAMPLER`。`_ST` を使うなら `float4` も要る",
            "3D" => "HLSL 側は `TEXTURE3D`",
            "CUBE" => "HLSL 側は `TEXTURECUBE`",
            "2DARRAY" => "HLSL 側は `TEXTURE2D_ARRAY`",
            _ => "HLSL 側で受ける型は不明",
        };

        string arguments = type.ArgumentTokens.IsEmpty
            ? string.Empty
            : $" ({string.Join("", type.ArgumentTokens.Select(t => t.Text))})";

        return new HoverResult(
            $"```shaderlab\n{type.TypeName}{arguments}\n```\n\nプロパティの型。{hlsl}",
            type.TypeToken.Span);
    }

    /// <summary>
    /// プロパティ属性を説明する。
    /// </summary>
    /// <param name="attribute">対象の属性。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// 属性は <c>MaterialPropertyDrawer</c> で自由に足せるため、
    /// 知らない名前を誤りとして扱ってはならない。
    /// </remarks>
    private static HoverResult DescribeAttribute(PropertyAttributeSyntax attribute)
    {
        string meaning = attribute.Name switch
        {
            "MainColor" => "このシェーダーの主色。`Material.color` から読み書きされる",
            "MainTexture" => "このシェーダーの主テクスチャ。`Material.mainTexture` から読み書きされる",
            "HideInInspector" => "インスペクタに出さない。値は保存される",
            "NoScaleOffset" => "テクスチャの Tiling / Offset を出さない。`_ST` も要らなくなる",
            "Normal" => "法線マップとして扱う。設定を誤ると警告が出る",
            "HDR" => "HDR の色として扱う。1 を超える値を入れられる",
            "PerRendererData" => "マテリアルではなく描画側から与える。インスペクタでは編集できない",
            "Toggle" or "ToggleOff" => "チェックボックスとして出し、対応するシンボルを切り替える",
            "KeywordEnum" => "選択肢として出し、対応するシンボルを切り替える",
            "Enum" => "選択肢として出す",
            "Space" or "Header" => "インスペクタの見た目を整える",
            _ => "このツールが知らない属性。`MaterialPropertyDrawer` で足したものかもしれない",
        };

        return new HoverResult(
            $"```shaderlab\n[{attribute.Name}]\n```\n\n{meaning}",
            attribute.NameToken.Span);
    }

    /// <summary>
    /// 埋め込みコードブロックを説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="block">対象のブロック。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// <c>HLSLINCLUDE</c> は同じブロック内の各 <c>*PROGRAM</c> の先頭へ差し込まれる。
    /// この挙動を知らないと、なぜ他の Pass にも影響するのかが分からない。
    /// </remarks>
    private static HoverResult DescribeProgramBlock(
        ShaderCompilation compilation,
        ProgramBlockSyntax block,
        int offset)
    {
        string keyword = block.Delimiter?.StartKeyword ?? "PROGRAM";

        AnalyzedProgramInfo info = Summarize(compilation, block);

        string note = keyword.EndsWith("INCLUDE", StringComparison.Ordinal)
            ? "同じブロック内の各 PROGRAM の先頭へ差し込まれる共通コード片。"
            : $"{info.Uniforms} 件の uniform が見えている。";

        return new HoverResult(
            $"```shaderlab\n{keyword}\n```\n\n{note}"
            + (info.Unresolved > 0
                ? $"\n\n**解決できない include が {info.Unresolved} 件ある。**"
                  + "この状態では対応検査を見送る (SL0002)。"
                : string.Empty),
            GetProgramKeywordSpan(block, offset));
    }

    /// <summary>
    /// カーソルが指しているほうのキーワードの範囲を求める。
    /// </summary>
    /// <param name="block">対象のブロック。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>キーワードの範囲。</returns>
    /// <remarks>
    /// ブロックのトークンは中身を丸ごと含んでいる。
    /// そのまま返すと、エディタは <c>*PROGRAM</c> から <c>END*</c> までを
    /// 説明の対象として塗る。指したのはキーワード 1 語である。
    /// </remarks>
    private static TextSpan GetProgramKeywordSpan(ProgramBlockSyntax block, int offset)
    {
        TextSpan span = block.Token.Span;

        if (block.Delimiter is not { } delimiter)
        {
            return span;
        }

        TextSpan start = new(span.Start, Math.Min(delimiter.StartKeyword.Length, span.Length));

        return offset <= start.End
            ? start
            : TextSpan.FromBounds(Math.Max(span.Start, span.End - delimiter.EndKeyword.Length), span.End);
    }

    /// <summary>ブロックの概要。</summary>
    /// <param name="Uniforms">見えている uniform の数。</param>
    /// <param name="Unresolved">解決できなかった include の数。</param>
    private readonly record struct AnalyzedProgramInfo(int Uniforms, int Unresolved);

    /// <summary>ブロックに対応する解析結果を要約する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="block">対象のブロック。</param>
    /// <returns>要約。対応する解析結果が無い場合は 0 件。</returns>
    private static AnalyzedProgramInfo Summarize(ShaderCompilation compilation, ProgramBlockSyntax block)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (ReferenceEquals(program.Block, block) || program.IncludeBlocks.Contains(block))
            {
                return new AnalyzedProgramInfo(program.Uniforms.Length, program.UnresolvedIncludes.Length);
            }
        }

        return default;
    }
}
