using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Godot.Parsing;

namespace Shaderlyn.Godot;

/// <summary>
/// Godot シェーダー言語の設定 (<see cref="PreprocessorOptions.Language"/>)。
/// </summary>
internal sealed record GodotLanguageOptions : ShaderLanguageOptions
{
    /// <summary>既定の設定。</summary>
    public static GodotLanguageOptions Default { get; } = new();

    /// <inheritdoc/>
    public override string DescribeUnavailable() => "Godot";

    /// <inheritdoc/>
    public override bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens) => GodotParser.IsCompleteUnits(tokens);
}
