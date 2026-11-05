using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Glsl;
using Shaderlyn.Godot;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// Godot シェーダー言語の型システム。
/// </summary>
/// <remarks>
/// <para>
/// Godot シェーダー言語の型・演算・組み込み関数は GLSL ES 3.0 と同じで、暗黙の変換も無い (<c>float a = 1;</c> は誤り)。
/// それらは GLSL の型システム (ES の 3.20 のフラグメントシェーダー) に任せる。Godot が持たない ES の組み込み関数もあるが、それは見落としになるだけで誤報告にはならない。
/// </para>
/// <para>
/// 組み込みの変数と定数 (<c>ALBEDO</c>、<c>TIME</c>、<c>PI</c>) だけは Godot の表 (<see cref="GodotBuiltins"/>) から引く。GLSL の <c>gl_</c> の変数は無い。
/// どの関数で使えるかは関数ごとに違うが、ここでは、そのシェーダーの種類のどの関数のものも使えるとする (使う位置の検査は GD のルールが行う)。
/// </para>
/// </remarks>
internal sealed class GodotTypeSystem : ShaderTypeSystem
{
    private static readonly ConcurrentDictionary<string, GodotTypeSystem> ByShaderType = new(StringComparer.Ordinal);

    /// <summary>型・演算・組み込み関数を任せる GLSL の型システム。</summary>
    private readonly GlslTypeSystem _glsl = GlslTypeSystem.For(320, GlslProfile.Es, GlslShaderStage.Fragment);

    /// <summary>組み込みの変数と定数。同じ名前が関数ごとにあれば、最初のものの型を使う。</summary>
    private readonly FrozenDictionary<string, GodotBuiltinVariable> _variables;

    private GodotTypeSystem(string? shaderType)
    {
        ShaderType = shaderType;
        IEnumerable<GodotBuiltinVariable> variables = shaderType is null
            ? GodotBuiltins.Instance.ShaderTypes.SelectMany(t => GodotBuiltins.Instance.GetVariables(t))
            : GodotBuiltins.Instance.GetVariables(shaderType);
        _variables = variables
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>シェーダーの種類が分からないときの型システム (どの種類の組み込みも使えるとする)。</summary>
    public static GodotTypeSystem Instance { get; } = new(null);

    /// <summary>シェーダーの種類。分からなければ <see langword="null"/>。</summary>
    public string? ShaderType { get; }

    /// <summary>シェーダーの種類の型システムを返す。</summary>
    /// <param name="shaderType">シェーダーの種類 (<c>spatial</c> など)。分からなければ <see langword="null"/>。</param>
    /// <returns>型システム。</returns>
    public static GodotTypeSystem For(string? shaderType)
        => shaderType is null ? Instance : ByShaderType.GetOrAdd(shaderType, static t => new GodotTypeSystem(t));

    /// <inheritdoc/>
    internal override ShaderTypeSystem ForTree(ShaderSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Language is GodotLanguageOptions language ? For(language.ShaderType) : this;
    }

    /// <inheritdoc/>
    public override ShaderTypeClass Classify(string? typeName) => _glsl.Classify(typeName);

    /// <inheritdoc/>
    public override bool IsBuiltInTypeName(string? name) => name is not null && Godot.Parsing.GodotKeywords.BuiltInTypes.Contains(name);

    /// <inheritdoc/>
    public override bool TryDescribeNumeric(string? typeName, out ShaderNumericShape shape) => _glsl.TryDescribeNumeric(typeName, out shape);

    /// <inheritdoc/>
    public override string? Compose(ShaderNumericShape shape) => _glsl.Compose(shape);

    /// <inheritdoc/>
    public override string? ComposeNumeric(string baseName, int components) => _glsl.ComposeNumeric(baseName, components);

    /// <inheritdoc/>
    public override bool TryDecomposeNumeric(string? typeName, out string baseName, out int components)
        => _glsl.TryDecomposeNumeric(typeName, out baseName, out components);

    /// <inheritdoc/>
    public override string? GetWiderBaseName(string left, string right) => _glsl.GetWiderBaseName(left, right);

    /// <inheritdoc/>
    public override ImmutableArray<string> ComponentNameSets => _glsl.ComponentNameSets;

    /// <inheritdoc/>
    internal override string? GetLiteralTypeName(ShaderSyntaxToken token) => _glsl.GetLiteralTypeName(token);

    /// <inheritdoc/>
    internal override bool MultipliesMatricesAlgebraically => true;

    /// <inheritdoc/>
    internal override bool ComparesWholeValues => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Godot は暗黙の変換をしないが、関数の呼び出し (利用者の関数・組み込み関数・値の作成) の実引数に書いた整数のリテラルは、
    /// 仮引数のスカラーの型 (<c>float</c> / <c>uint</c>) へ変換して渡す (<c>f(0)</c>、<c>f(-1)</c>、<c>vec2(1, 2)</c>。ShaderLanguage の convert_constant)。
    /// 定数の名前 (<c>const int K</c>)・式 (<c>1 + 2</c>)・変数・初期化・代入・演算は変換しない。Godot 4.7.2 で確かめた。
    /// </remarks>
    internal override bool ConvertsLiteralArgument(ExpressionSyntax argument, string? parameterType)
    {
        ExpressionSyntax value = argument is PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.MinusToken } negated
            ? negated.Operand
            : argument;

        return value is LiteralExpressionSyntax literal
               && _glsl.GetLiteralTypeName(literal.Token) is "int"
               && parameterType is "float" or "uint" or "int";
    }

    /// <inheritdoc/>
    internal override bool ConstructorsDropExtraComponents => true;

    /// <inheritdoc/>
    internal override bool HasLengthMethod => true;

    /// <inheritdoc/>
    internal override bool ResolvesIntrinsicsByOverloads => true;

    /// <inheritdoc/>
    internal override ResolvedTypeName? GetBuiltinVariable(string name)
        => _variables.TryGetValue(name, out GodotBuiltinVariable? variable) ? new ResolvedTypeName(variable.Type, IsArray: false) : null;

    /// <inheritdoc/>
    internal override bool TryResolveIntrinsic(string name, ImmutableArray<string?> argumentTypes, out string? resultType)
        => _glsl.TryResolveIntrinsic(name, argumentTypes, out resultType);

    /// <inheritdoc/>
    public override bool IsConvertible(string? valueType, string? targetType, bool exact = false) => _glsl.IsConvertible(valueType, targetType, exact);

    /// <inheritdoc/>
    public override bool IsConvertible(ShaderNumericShape value, ShaderNumericShape target, bool exact) => _glsl.IsConvertible(value, target, exact);

    /// <inheritdoc/>
    public override bool IsTruncating(string? valueType, string? targetType) => _glsl.IsTruncating(valueType, targetType);

    /// <inheritdoc/>
    public override bool IsKnownConstant(string? name) => name is not null && _variables.ContainsKey(name);

    /// <inheritdoc/>
    public override bool IsKnownFunction(string? name, ShaderSyntaxTree tree) => name is not null && !_glsl.Builtins.GetFunctions(name).IsEmpty;

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> FunctionNames => _glsl.FunctionNames;

    /// <inheritdoc/>
    internal override bool TryGetIntrinsicArity(string name, out IntrinsicArity arity) => _glsl.TryGetIntrinsicArity(name, out arity);

    /// <inheritdoc/>
    public override TextureMethodReturnType GetTextureMethodReturnType(string? name) => _glsl.GetTextureMethodReturnType(name);

    /// <inheritdoc/>
    public override bool IsTextureSamplingFunction(string? name) => _glsl.IsTextureSamplingFunction(name);

    /// <inheritdoc/>
    public override IntrinsicResultRule GetResultShape(string? name) => _glsl.GetResultShape(name);

    /// <inheritdoc/>
    public override string? GetFixedBaseType(string? name) => _glsl.GetFixedBaseType(name);

    /// <inheritdoc/>
    public override string? GetFixedType(string? name) => _glsl.GetFixedType(name);
}
