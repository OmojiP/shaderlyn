using System.Collections.Frozen;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// ShaderLab 側の要素の説明。
/// </summary>
internal static partial class HoverBuilder
{
    /// <summary>
    /// ShaderLab の要素について答える。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。答えるものが無い場合は <see langword="null"/>。</returns>
    private static HoverResult? BuildForProperty(ShaderCompilation compilation, int offset)
    {
        foreach (SyntaxNode node in compilation.ShaderLabTree.Root.DescendantNodesAndSelf())
        {
            if (node.Span.Length == 0 || offset < node.Span.Start || offset > node.Span.End)
            {
                continue;
            }

            if (DescribeShaderLab(compilation, node, offset) is { } described)
            {
                return described;
            }
        }

        return null;
    }

    /// <summary>ShaderLab のノード 1 つを説明する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="node">説明するノード。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。答えられない場合は <see langword="null"/>。</returns>
    private static HoverResult? DescribeShaderLab(ShaderCompilation compilation, SyntaxNode node, int offset)
        => node switch
        {
            PropertyDeclarationSyntax property when Touches(property.NameToken, offset)
                => DescribeProperty(compilation, property),

            TagSyntax tag => DescribeTag(tag, offset),

            CommandSyntax command => DescribeCommand(command, offset),

            PropertyTypeSyntax type => DescribePropertyType(type),

            PropertyAttributeSyntax attribute when Touches(attribute.NameToken, offset)
                => DescribeAttribute(attribute),

            ShaderDeclarationSyntax shader when Touches(shader.ShaderKeyword, offset)
                                                || Touches(shader.NameToken, offset)
                => DescribeShaderDeclaration(compilation, shader),

            SubShaderSyntax subShader when Touches(subShader.Keyword, offset)
                => DescribeSubShader(subShader),

            PassSyntax pass when Touches(pass.Keyword, offset)
                => DescribePass(compilation, pass),

            PropertiesBlockSyntax properties when Touches(properties.Keyword, offset)
                => new HoverResult(
                    $"```shaderlab\nProperties\n```\n\nマテリアルのインスペクタに出す値の宣言"
                    + $" ({properties.Properties.Length} 件)。"
                    + "\n\nここに書いただけでは効かない。HLSL 側にも同じ名前の uniform が要る。",
                    properties.Keyword.Span),

            TagsBlockSyntax tags when Touches(tags.Keyword, offset)
                => new HoverResult(
                    $"```shaderlab\nTags {{ {string.Join(" ", tags.Tags.Select(t => $"\"{t.Key}\" = \"{t.Value}\""))} }}\n```"
                    + "\n\nUnity はタグのスペルミスをエラーにしない。",
                    tags.Keyword.Span),

            // ブロックの中身は HLSL であり、ShaderLab として説明できるものは何も無い。
            // 開始と終了のキーワードを指したときだけ、そのブロック自身の話をする。
            ProgramBlockSyntax program when TouchesProgramKeyword(program, offset)
                => DescribeProgramBlock(compilation, program, offset),

            _ => null,
        };

    /// <summary>
    /// カーソルが <c>*PROGRAM</c> ブロックの開始・終了キーワードの上にあるかを判定する。
    /// </summary>
    /// <param name="block">対象のブロック。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>キーワードの上にあれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>ブロックのトークンは中身を丸ごと含んでいる。</b>
    /// <c>HLSLPROGRAM</c> から <c>ENDHLSL</c> までが 1 つのトークンであり、
    /// 範囲に入っているかだけで判定すると、
    /// コードのどこを指しても「HLSLPROGRAM」と答えることになる。
    /// コメントや <c>{</c> の上でブロックの説明が出るのは、利用者にとって何の意味も無い。
    /// </remarks>
    private static bool TouchesProgramKeyword(ProgramBlockSyntax block, int offset)
    {
        if (block.Delimiter is not { } delimiter)
        {
            return false;
        }

        TextSpan span = block.Token.Span;

        bool onStart = offset >= span.Start && offset <= span.Start + delimiter.StartKeyword.Length;

        // 終了キーワードが書かれていない (閉じ忘れ) 場合、末尾は中身の途中を指している。
        bool onEnd = span.Length >= delimiter.EndKeyword.Length
                     && block.Token.Text.EndsWith(delimiter.EndKeyword, StringComparison.OrdinalIgnoreCase)
                     && offset >= span.End - delimiter.EndKeyword.Length
                     && offset <= span.End;

        return onStart || onEnd;
    }

    /// <summary>
    /// タグを説明する。
    /// </summary>
    /// <param name="tag">対象のタグ。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。タグの上に無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>Unity はタグのスペルミスをエラーにしない。</b>
    /// 知っているタグかどうか、指定できる値は何かを出す。
    /// </remarks>
    private static HoverResult? DescribeTag(TagSyntax tag, int offset)
    {
        if (Touches(tag.KeyToken, offset))
        {
            string known = ShaderLabKnownValues.TagKeys.Contains(tag.Key)
                ? "既知のタグ"
                : "このツールが知らないタグ。独自のタグであれば問題ない";

            return new HoverResult($"```shaderlab\n\"{tag.Key}\"\n```\n\n{known}", tag.KeyToken.Span);
        }

        if (!Touches(tag.ValueToken, offset))
        {
            return null;
        }

        string values = DescribeAllowedTagValues(tag.Key);

        return new HoverResult(
            $"```shaderlab\n\"{tag.Key}\" = \"{tag.Value}\"\n```{values}", tag.ValueToken.Span);
    }

    /// <summary>タグに指定できる値を説明する。</summary>
    /// <param name="key">タグの名前。</param>
    /// <returns>説明。分からない場合は空。</returns>
    private static string DescribeAllowedTagValues(string key)
    {
        if (string.Equals(key, "RenderType", StringComparison.OrdinalIgnoreCase))
        {
            return "\n\nよく使われる値: " + Join(ShaderLabKnownValues.RenderTypes)
                + "\n\n置き換え用に独自の値を付けてもよい。";
        }

        if (string.Equals(key, "Queue", StringComparison.OrdinalIgnoreCase))
        {
            return "\n\n指定できる値: " + Join(ShaderLabKnownValues.RenderQueues)
                + " (数値のオフセットを付けられる)";
        }

        if (ShaderLabKnownValues.BooleanTagKeys.Contains(key))
        {
            return "\n\n指定できる値: True, False";
        }

        return string.Empty;
    }

    /// <summary>
    /// レンダーステート命令を説明する。
    /// </summary>
    /// <param name="command">対象の命令。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。命令の上に無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>Unity は不正な値をエラーにせず既定値へ倒す。</b>
    /// 指定できる値をその場で見られると、スペルミスに気づける。
    /// </remarks>
    private static HoverResult? DescribeCommand(CommandSyntax command, int offset)
    {
        if (!Touches(command.NameToken, offset)
            && !command.Arguments.OfType<LiteralArgumentSyntax>().Any(a => Touches(a.Token, offset)))
        {
            return null;
        }

        string values = DescribeAllowedCommandValues(command.Name);
        string arguments = string.Join(
            " ", command.Arguments.OfType<LiteralArgumentSyntax>().Select(a => a.Token.Text));

        return new HoverResult(
            $"```shaderlab\n{command.Name} {arguments}\n```\n\nレンダーステート命令{values}",
            command.NameToken.Span);
    }

    /// <summary>命令に指定できる値を説明する。</summary>
    /// <param name="name">命令の名前。</param>
    /// <returns>説明。分からない場合は空。</returns>
    private static string DescribeAllowedCommandValues(string name)
    {
        FrozenSet<string>? allowed = name.ToUpperInvariant() switch
        {
            "CULL" => ShaderLabKnownValues.CullModes,
            "ZWRITE" or "ALPHATOMASK" or "CONSERVATIVE" or "ZCLIP" or "LIGHTING"
                => ShaderLabKnownValues.OnOff,
            "ZTEST" => ShaderLabKnownValues.DepthTestFunctions,
            "BLENDOP" => ShaderLabKnownValues.BlendOperations,
            "BLEND" => ShaderLabKnownValues.BlendFactors,
            _ => null,
        };

        return allowed is null ? string.Empty : "\n\n指定できる値: " + Join(allowed);
    }

    /// <summary>値の一覧を読みやすく並べる。</summary>
    /// <param name="values">並べる値。</param>
    /// <returns>組み立てた文字列。</returns>
    private static string Join(FrozenSet<string> values)
        => string.Join(", ", values.Order(StringComparer.Ordinal));

    /// <summary>ShaderLab のトークンがカーソルの下にあるかを判定する。</summary>
    /// <param name="token">対象のトークン。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>下にある場合は <see langword="true"/>。</returns>
    private static bool Touches(SyntaxToken token, int offset)
        => CursorTarget.Touches(token.Span, offset);

    /// <summary>
    /// プロパティの宣言を説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="property">対象のプロパティ。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// プロパティと uniform の対応が取れているかは目で追いにくい。
    /// HLSL 側にどう見えているかをここで出す。
    /// </remarks>
    private static HoverResult DescribeProperty(
        ShaderCompilation compilation,
        PropertyDeclarationSyntax property)
    {
        string name = property.NameToken.ValueText;

        string uniform = compilation.TryGetUniform(name, out UniformSymbol? found)
            ? $"HLSL 側の宣言: **`{found.TypeName} {found.Name}`**"
              + (found.ContainingBufferName is { } buffer ? $" (`{buffer}` の中)" : " (定数バッファの外)")
            : "**HLSL 側に対応する宣言が見つかりません。**";

        PropertySymbol? symbol = compilation.Properties.FirstOrDefault(p => p.Name == name);
        string kind = symbol is null ? string.Empty : $"プロパティの型: **`{symbol.Kind}`**\n\n";

        return new HoverResult(
            $"```shaderlab\n{name}\n```\n\n{kind}{uniform}", property.NameToken.Span);
    }
}
