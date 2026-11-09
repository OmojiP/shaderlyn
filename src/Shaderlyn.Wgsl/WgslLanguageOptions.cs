using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Wgsl;

/// <summary>
/// WGSL の設定 (<see cref="PreprocessorOptions.Language"/>)。WGSL にプリプロセッサは無いので、字句を読むためだけに使う。
/// </summary>
internal sealed record WgslLanguageOptions : ShaderLanguageOptions
{
    /// <summary>既定の設定。</summary>
    public static WgslLanguageOptions Default { get; } = new();

    /// <inheritdoc/>
    public override string DescribeUnavailable() => "WGSL";

    /// <inheritdoc/>
    public override bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens) => true;
}
