using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Slang.Parsing;

namespace Shaderlyn.Slang;

/// <summary>
/// Slang の設定 (<see cref="PreprocessorOptions.Language"/>)。
/// </summary>
internal sealed record SlangLanguageOptions : ShaderLanguageOptions
{
    /// <summary>既定の設定。</summary>
    public static SlangLanguageOptions Default { get; } = new();

    /// <inheritdoc/>
    public override string DescribeUnavailable() => "Slang";

    /// <inheritdoc/>
    public override bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens) => SlangParser.IsCompleteUnits(tokens);

    /// <inheritdoc/>
    /// <remarks>Slang の <c>#language slang 2026</c> は、言語のバージョンを選ぶ。</remarks>
    public override bool IsLanguageDirective(string name) => name is "language" or "lang";
}
