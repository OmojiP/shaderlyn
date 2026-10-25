using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Hlsl.Parsing;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// HLSL の型と組み込み関数 (<see cref="ShaderTypeSystem"/>)。
/// </summary>
/// <remarks>
/// 中身は HLSL の表 (<see cref="HlslTypeClassifier"/>・<see cref="HlslIntrinsics"/>・<see cref="HlslConversion"/>) を引くだけである。
/// </remarks>
public sealed class HlslTypeSystem : ShaderTypeSystem
{
    private HlslTypeSystem()
    {
    }

    /// <summary>HLSL の型と組み込み関数。状態を持たないので 1 つを共有する。</summary>
    public static HlslTypeSystem Instance { get; } = new();

    /// <inheritdoc/>
    public override ImmutableArray<string> ComponentNameSets { get; } = ["xyzw", "rgba"];

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> FunctionNames => HlslIntrinsics.FunctionNames;

    /// <inheritdoc/>
    internal override bool TryGetIntrinsicArity(string name, out IntrinsicArity arity)
        => HlslIntrinsicArity.Functions.TryGetValue(name, out arity);

    /// <inheritdoc/>
    public override ShaderTypeClass Classify(string? typeName) => HlslTypeClassifier.Classify(typeName);

    /// <inheritdoc/>
    public override bool TryDescribeNumeric(string? typeName, out ShaderNumericShape shape)
        => HlslTypeClassifier.TryDescribeNumeric(typeName, out shape);

    /// <inheritdoc/>
    public override string? Compose(ShaderNumericShape shape) => HlslTypeClassifier.Compose(shape);

    /// <inheritdoc/>
    public override string? ComposeNumeric(string baseName, int components) => HlslTypeClassifier.ComposeNumeric(baseName, components);

    /// <inheritdoc/>
    public override bool TryDecomposeNumeric(string? typeName, out string baseName, out int components)
        => HlslTypeClassifier.TryDecomposeNumeric(typeName, out baseName, out components);

    /// <inheritdoc/>
    public override string? GetWiderBaseName(string left, string right) => HlslTypeClassifier.GetWiderBaseName(left, right);

    /// <inheritdoc/>
    public override bool IsConvertible(string? valueType, string? targetType, bool exact = false)
        => HlslConversion.IsConvertible(valueType, targetType, exact);

    /// <inheritdoc/>
    public override bool IsConvertible(ShaderNumericShape value, ShaderNumericShape target, bool exact)
        => HlslConversion.IsConvertible(value, target, exact);

    /// <inheritdoc/>
    public override bool IsTruncating(string? valueType, string? targetType) => HlslConversion.IsTruncating(valueType, targetType);

    /// <inheritdoc/>
    public override bool IsKnownConstant(string? name) => HlslIntrinsics.IsKnownConstant(name);

    /// <inheritdoc/>
    /// <remarks>使えるかはコンパイラと HLSL のバージョンで変わる (<c>select</c> などは DXC 2021 から)。どれでコンパイルするかは木の言語の設定から読む。</remarks>
    public override bool IsKnownFunction(string? name, ShaderSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return HlslIntrinsics.IsKnownFunction(name, tree.Hlsl().Features);
    }

    /// <inheritdoc/>
    public override TextureMethodReturnType GetTextureMethodReturnType(string? name) => HlslIntrinsics.GetTextureMethodReturnType(name);

    /// <inheritdoc/>
    public override bool IsTextureSamplingFunction(string? name) => HlslIntrinsics.IsTextureSamplingFunction(name);

    /// <inheritdoc/>
    public override IntrinsicResultRule GetResultShape(string? name) => HlslIntrinsics.GetResultShape(name);

    /// <inheritdoc/>
    public override string? GetFixedBaseType(string? name) => HlslIntrinsics.GetFixedBaseType(name);

    /// <inheritdoc/>
    public override string? GetFixedType(string? name) => HlslIntrinsics.GetFixedType(name);
}
