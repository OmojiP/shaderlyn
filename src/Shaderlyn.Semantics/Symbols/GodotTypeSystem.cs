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
/// Godot シェーダー言語の型と演算は GLSL ES 3.0 と同じで、暗黙の変換も無い (<c>float a = 1;</c> は誤り)。
/// 型の名前・形・変換は GLSL の型システム (ES の 3.20 のフラグメントシェーダー) に任せる。
/// </para>
/// <para>
/// 組み込みの変数と定数 (<c>ALBEDO</c>、<c>TIME</c>、<c>PI</c>) と組み込み関数は Godot の表 (<see cref="GodotBuiltins"/>) から引く。GLSL の <c>gl_</c> の変数は無い。
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

    /// <summary>組み込みの変数と定数の名前ごとの、使える関数 (<c>global</c> と <c>constants</c> を含む)。</summary>
    private readonly FrozenDictionary<string, FrozenSet<string>> _functionsByVariable;

    /// <summary>このシェーダーの種類のステージ関数 (<c>vertex</c> / <c>fragment</c> など)。</summary>
    private readonly FrozenSet<string> _stageFunctions;

    /// <summary>
    /// 組み込み関数 (値の作成を含む) と、このシェーダーの種類のステージ関数の中でだけ使える組み込み関数 (<c>texture_sdf</c> など)。
    /// ステージ関数のものは、どのステージ関数のものも使えるとする (使う位置の検査は GD のルールが行う)。
    /// </summary>
    internal FrozenDictionary<string, ImmutableArray<GodotBuiltinFunction>> Functions { get; }

    private GodotTypeSystem(string? shaderType)
    {
        ShaderType = shaderType;
        IEnumerable<GodotBuiltinVariable> variables = shaderType is null
            ? GodotBuiltins.Instance.ShaderTypes.SelectMany(t => GodotBuiltins.Instance.GetVariables(t))
            : GodotBuiltins.Instance.GetVariables(shaderType);
        GodotBuiltinVariable[] all = [.. variables];
        _variables = all
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _functionsByVariable = all
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.Select(v => v.Function).ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);
        _stageFunctions = all
            .Select(v => v.Function)
            .Where(f => f is not ("global" or "constants"))
            .ToFrozenSet(StringComparer.Ordinal);

        Dictionary<string, ImmutableArray<GodotBuiltinFunction>> functions = new(GodotBuiltins.Instance.Functions, StringComparer.Ordinal);

        foreach (IGrouping<string, GodotStageFunction> group in GodotBuiltins.Instance.StageFunctions
                     .Where(f => shaderType is null || f.ShaderType == shaderType)
                     .GroupBy(f => f.Function.Name, StringComparer.Ordinal))
        {
            functions[group.Key] = [.. functions.GetValueOrDefault(group.Key, []), .. group.Select(f => f.Function).Distinct()];
        }

        Functions = functions.ToFrozenDictionary(StringComparer.Ordinal);
        FunctionNames = [.. Functions.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// 組み込みの変数か定数を、その位置で使えるか。
    /// </summary>
    /// <param name="name">名前。</param>
    /// <param name="function">囲んでいる関数の名前。関数の外なら <see langword="null"/>。</param>
    /// <returns>使えるなら <see langword="true"/>。組み込みでない名前は <see langword="false"/>。</returns>
    /// <remarks>
    /// ステージ関数 (<c>fragment</c> など) の中では、その関数の組み込みと、どの関数でも使える組み込み (<c>TIME</c>)・定数 (<c>PI</c>) が使える。
    /// 利用者の関数の中では、どこから呼ばれていても、どの関数でも使える組み込みと定数だけが使える (<c>FRAGCOORD</c> は「Unknown identifier」)。
    /// 関数の外 (uniform の既定値など) では定数だけが使える。Godot 4.7.2 で確かめた。
    /// シェーダーの種類が分からないときは、どこでも使えるとする。
    /// </remarks>
    internal bool IsBuiltinVariableAvailable(string name, string? function)
    {
        if (!_functionsByVariable.TryGetValue(name, out FrozenSet<string>? functions))
        {
            return false;
        }

        if (ShaderType is null || functions.Contains("constants"))
        {
            return true;
        }

        return function is not null
               && (functions.Contains("global") || (_stageFunctions.Contains(function) && functions.Contains(function)));
    }

    /// <summary>
    /// 組み込みの変数か定数が、その関数の中で書き込めないものか。
    /// </summary>
    /// <param name="name">名前。</param>
    /// <param name="function">囲んでいる関数の名前。</param>
    /// <returns>書き込めないと分かれば <see langword="true"/>。シェーダーの種類が分からないとき、その関数で使えないときは <see langword="false"/>。</returns>
    /// <remarks>
    /// 同じ名前が関数ごとに書き込めたり書き込めなかったりする (<c>spatial</c> の <c>VERTEX</c> は <c>vertex</c> では書き込めて、<c>fragment</c> では書き込めない)。
    /// その関数のもの、どの関数でも使えるもの (<c>TIME</c>)、定数 (<c>PI</c>) の順に引く。
    /// </remarks>
    internal bool IsBuiltinVariableReadOnly(string name, string function)
    {
        if (ShaderType is null)
        {
            return false;
        }

        ImmutableArray<GodotBuiltinVariable> variables = GodotBuiltins.Instance.GetVariables(ShaderType);
        GodotBuiltinVariable? found =
            (_stageFunctions.Contains(function) ? variables.FirstOrDefault(v => v.Name == name && v.Function == function) : null)
            ?? variables.FirstOrDefault(v => v.Name == name && v.Function == "global")
            ?? variables.FirstOrDefault(v => v.Name == name && v.Function == "constants");

        return found is { IsConstant: true };
    }

    /// <summary>このシェーダーの種類のステージ関数か (<c>vertex</c> / <c>fragment</c> / <c>light</c> / <c>start</c> / <c>process</c> / <c>sky</c> / <c>fog</c> など)。</summary>
    /// <param name="name">関数の名前。</param>
    /// <returns>ステージ関数なら <see langword="true"/>。</returns>
    internal bool IsStageFunction(string name) => _stageFunctions.Contains(name);

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
    /// <remarks>
    /// 実引数の数が合う多重定義のうち、型がちょうど合うもの (型の分からない実引数はどれにも合う)、無ければ <c>int</c> の実引数を
    /// <c>float</c> / <c>uint</c> の仮引数にも合わせたもの (実引数が整数のリテラルなら Godot は変換する) を選ぶ。戻り値の型が 1 つに決まるときだけ答える。
    /// </remarks>
    internal override bool TryResolveIntrinsic(string name, ImmutableArray<string?> argumentTypes, out string? resultType)
    {
        resultType = null;

        if (!Functions.TryGetValue(name, out ImmutableArray<GodotBuiltinFunction> overloads))
        {
            return false;
        }

        foreach (bool literal in (bool[])[false, true])
        {
            string[] results = [.. overloads
                .Where(f => Matches(f, argumentTypes, literal))
                .Select(f => f.ReturnType)
                .Distinct(StringComparer.Ordinal)];

            if (results.Length > 0)
            {
                resultType = results.Length == 1 ? results[0] : null;
                return true;
            }
        }

        return true;
    }

    /// <summary>多重定義が実引数の型に合うか。</summary>
    /// <param name="function">多重定義。</param>
    /// <param name="argumentTypes">実引数の型。分からないものは <see langword="null"/>。</param>
    /// <param name="literal"><c>int</c> の実引数を <c>float</c> / <c>uint</c> の仮引数にも合わせるか (整数のリテラルの変換)。</param>
    /// <returns>合えば <see langword="true"/>。</returns>
    internal static bool Matches(GodotBuiltinFunction function, ImmutableArray<string?> argumentTypes, bool literal)
    {
        if (function.Parameters.Length != argumentTypes.Length)
        {
            return false;
        }

        for (int i = 0; i < argumentTypes.Length; i++)
        {
            string? argument = argumentTypes[i];
            string parameter = function.Parameters[i];

            if (argument is not null && argument != parameter && !(literal && argument == "int" && parameter is "float" or "uint"))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool IsConvertible(string? valueType, string? targetType, bool exact = false) => _glsl.IsConvertible(valueType, targetType, exact);

    /// <inheritdoc/>
    public override bool IsConvertible(ShaderNumericShape value, ShaderNumericShape target, bool exact) => _glsl.IsConvertible(value, target, exact);

    /// <inheritdoc/>
    public override bool IsTruncating(string? valueType, string? targetType) => _glsl.IsTruncating(valueType, targetType);

    /// <inheritdoc/>
    public override bool IsKnownConstant(string? name) => name is not null && _variables.ContainsKey(name);

    /// <inheritdoc/>
    public override bool IsKnownFunction(string? name, ShaderSyntaxTree tree) => name is not null && Functions.ContainsKey(name);

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> FunctionNames { get; }

    /// <inheritdoc/>
    internal override bool TryGetIntrinsicArity(string name, out IntrinsicArity arity)
    {
        if (!Functions.TryGetValue(name, out ImmutableArray<GodotBuiltinFunction> overloads))
        {
            arity = default;
            return false;
        }

        arity = new IntrinsicArity([.. overloads.Select(f => f.Parameters.Length).Distinct().Order()], -1);
        return true;
    }

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
