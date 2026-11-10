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

    /// <summary>
    /// どの列挙型の列挙子も、型の名前で修飾せずに書けるか (slangc の <c>-unscoped-enum</c>、設定ファイルの <c>slang: unscoped-enums</c>)。
    /// 既定は <see langword="false"/> (<c>[UnscopedEnum]</c> を付けた列挙型だけ)。
    /// </summary>
    public bool UnscopedEnums { get; init; }

    /// <inheritdoc/>
    public override string DescribeUnavailable() => "Slang";

    /// <inheritdoc/>
    public override bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens) => SlangParser.IsCompleteUnits(tokens);

    /// <inheritdoc/>
    /// <remarks>Slang の <c>#language slang 2026</c> は、言語のバージョンを選ぶ。</remarks>
    public override bool IsLanguageDirective(string name) => name is "language" or "lang";
}
