using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// GLSL の型と組み込み関数 (<see cref="ShaderTypeSystem"/>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>まだ型を何も知らない。</b> 型の名前は分類せず (<see cref="ShaderTypeClass.Unknown"/>)、どの変換も通す。
/// 型と変換は GL2a、組み込みの関数と変数は GL2b で足す。
/// </para>
/// <para>
/// 分からないことを「誤り」にしない答え方にしているので、意味解析はこれで組み立てられる。
/// ただし、知らない名前を宣言されていないと読むルールがあるので、GLSL ではまだルールを働かせない
/// (<see cref="Analysis.ShaderRuleAnalyzer.AnalyzesGlsl"/>)。
/// </para>
/// </remarks>
internal sealed class GlslTypeSystem : ShaderTypeSystem
{
    private GlslTypeSystem()
    {
    }

    /// <summary>GLSL の型と組み込み関数。状態を持たないので 1 つを共有する。</summary>
    public static GlslTypeSystem Instance { get; } = new();

    /// <inheritdoc/>
    public override ImmutableArray<string> ComponentNameSets { get; } = ["xyzw", "rgba", "stpq"];

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> FunctionNames { get; } = [];

    /// <inheritdoc/>
    public override ShaderTypeClass Classify(string? typeName) => ShaderTypeClass.Unknown;

    /// <inheritdoc/>
    public override bool TryDescribeNumeric(string? typeName, out ShaderNumericShape shape)
    {
        shape = default;
        return false;
    }

    /// <inheritdoc/>
    public override string? Compose(ShaderNumericShape shape) => null;

    /// <inheritdoc/>
    public override string? ComposeNumeric(string baseName, int components) => null;

    /// <inheritdoc/>
    public override bool TryDecomposeNumeric(string? typeName, out string baseName, out int components)
    {
        baseName = string.Empty;
        components = 0;
        return false;
    }

    /// <inheritdoc/>
    public override string? GetWiderBaseName(string left, string right) => null;

    /// <inheritdoc/>
    public override bool IsConvertible(string? valueType, string? targetType, bool exact = false) => true;

    /// <inheritdoc/>
    public override bool IsConvertible(ShaderNumericShape value, ShaderNumericShape target, bool exact) => true;

    /// <inheritdoc/>
    public override bool IsTruncating(string? valueType, string? targetType) => false;

    /// <inheritdoc/>
    public override bool IsKnownConstant(string? name) => false;

    /// <inheritdoc/>
    public override bool IsKnownFunction(string? name, ShaderSyntaxTree tree) => false;

    /// <inheritdoc/>
    public override TextureMethodReturnType GetTextureMethodReturnType(string? name) => TextureMethodReturnType.Unknown;

    /// <inheritdoc/>
    public override bool IsTextureSamplingFunction(string? name) => false;

    /// <inheritdoc/>
    public override IntrinsicResultRule GetResultShape(string? name) => IntrinsicResultRule.Unknown;

    /// <inheritdoc/>
    public override string? GetFixedBaseType(string? name) => null;

    /// <inheritdoc/>
    public override string? GetFixedType(string? name) => null;
}
