using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Godot.Parsing;

namespace Shaderlyn.Godot;

/// <summary>
/// Godot シェーダー言語の設定 (<see cref="PreprocessorOptions.Language"/>)。
/// </summary>
/// <param name="ShaderType">シェーダーの種類 (<c>shader_type spatial;</c> の <c>spatial</c>)。分からなければ <see langword="null"/> (取り込まれる断片など)。</param>
internal sealed record GodotLanguageOptions(string? ShaderType = null) : ShaderLanguageOptions
{
    /// <summary>既定の設定 (シェーダーの種類が分からない)。</summary>
    public static GodotLanguageOptions Default { get; } = new();

    /// <inheritdoc/>
    public override string DescribeUnavailable() => "Godot";

    /// <inheritdoc/>
    public override bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens) => GodotParser.IsCompleteUnits(tokens);

    /// <summary>字句の並びから、最初の <c>shader_type</c> の種類を読む。</summary>
    /// <param name="tokens">展開する前の字句。</param>
    /// <returns>シェーダーの種類。無ければ <see langword="null"/>。</returns>
    public static string? ReadShaderType(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text == "shader_type" && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                return tokens[i + 1].Text;
            }
        }

        return null;
    }
}
