using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// Slang の型システム。型・演算・HLSL の組み込み関数は HLSL 2021 の型システムに任せ、Slang の標準ライブラリの名前を足す。
/// </summary>
/// <remarks>
/// <para>
/// 標準ライブラリの名前は、slang のソースの <c>*.meta.slang</c> から <c>tools/slang/stdlib.mjs</c> が作った表 (<c>SlangStdlib.g.txt</c>) から引く。
/// 名前は多めに集めてあるので、「宣言されていない名前」を誤って報告しない。
/// </para>
/// <para>
/// 標準ライブラリの関数は、HLSL の同じ名前の関数より多くの形を持つことがある (ジェネリック、<c>printf</c> の可変長の引数など)。
/// その関数の引数の数は主張しない (<see cref="TryGetIntrinsicArity"/>)。
/// </para>
/// </remarks>
internal sealed class SlangTypeSystem : ShaderTypeSystem
{
    private const string ResourceName = "Shaderlyn.Semantics.SlangStdlib.txt";

    private static readonly Lazy<SlangTypeSystem> Shared = new(() => new SlangTypeSystem([]));

    /// <summary>import した標準のモジュールの組ごとの型システム。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SlangTypeSystem> ByImports = new(StringComparer.Ordinal);

    private readonly HlslTypeSystem _hlsl = HlslTypeSystem.Instance;
    private readonly FrozenSet<string> _types;
    private readonly FrozenSet<string> _functions;
    private readonly FrozenSet<string> _values;
    private readonly FrozenSet<string> _modules;
    private readonly ImmutableArray<string> _functionPrefixes;

    /// <summary>型システムを作る。</summary>
    /// <param name="imports">import した標準のモジュール (slang/neural など)。そのモジュールにだけある名前を見せる。</param>
    private SlangTypeSystem(ImmutableArray<string> imports)
    {
        Imports = imports;

        // 構文解析が型として読む名前 (uint8_t・Ptr など) も型にする。
        HashSet<string> types = new(Slang.Parsing.SlangKeywords.ExtraTypes, StringComparer.Ordinal);

        // 自動微分の演算子 (bwd_diff(f)(…)) と、型を引数に取る演算子 (countof・alignof)。関数のように呼ぶ。
        HashSet<string> functions = new(StringComparer.Ordinal) { "bwd_diff", "fwd_diff", "__bwd_diff", "__fwd_diff", "countof", "alignof", "sizeof" };

        // GLSL の組み込み関数 (Slang は GLSL の組み込みも読む。atomicAdd・imageAtomicAdd など)。
        functions.UnionWith(GlslBuiltins.Instance.Functions.Keys);

        // C++ のコードで名前を組み立てる関数の、名前の前半 (WaveMulti$(opName) の WaveMulti)。
        List<string> prefixes = [];

        // 言語の値 (none は空の Optional、This は自分の型) と、GLSL の組み込みの変数 (Slang は GLSL の組み込みも読む)。GLSL の文として書く ignoreIntersectionEXT と terminateRayEXT も値の名前として読む。
        HashSet<string> values = new(GlslBuiltins.Instance.Variables.Keys, StringComparer.Ordinal) { "none", "nullptr", "This", "ignoreIntersectionEXT", "terminateRayEXT" };

        // 言語に組み込みのモジュール。
        HashSet<string> modules = new(StringComparer.Ordinal) { "glsl", "hlsl", "core", "diff", "gfx", "slang" };

        using Stream stream = typeof(SlangTypeSystem).Assembly.GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException($"埋め込みのリソース {ResourceName} がありません。");
        using StreamReader reader = new(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#' || line.Split('\t') is not [var kind, var name, .. var rest])
            {
                continue;
            }

            // 標準のモジュールにだけある名前 (3 つ目の列がモジュール) は、そのモジュールを import したときだけ見せる。
            if (rest is [var from] && !from.Split(',').Any(IsImported))
            {
                continue;
            }

            (kind switch
            {
                "T" => types,
                "F" => functions,
                "M" => modules,
                "P" => null,
                _ => values,
            })?.Add(name);

            if (kind == "P")
            {
                prefixes.Add(name);
            }
        }

        _types = types.ToFrozenSet(StringComparer.Ordinal);
        _functions = functions.ToFrozenSet(StringComparer.Ordinal);
        _values = values.ToFrozenSet(StringComparer.Ordinal);
        _modules = modules.ToFrozenSet(StringComparer.Ordinal);
        _functionPrefixes = [.. prefixes];
        FunctionNames = [.. _hlsl.FunctionNames.Concat(_functions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>型システム (標準のモジュールを import していないファイルのもの)。</summary>
    public static SlangTypeSystem Instance => Shared.Value;

    /// <summary>import した標準のモジュール。</summary>
    internal ImmutableArray<string> Imports { get; }

    /// <summary>そのモジュール (slang/neural など) を import したか。モジュールの中のファイル (slang/neural/x) を import しても、そのモジュールの名前を見せる。</summary>
    private bool IsImported(string module)
        => Imports.Any(i => i == module || i.StartsWith(module + "/", StringComparison.Ordinal) || module.StartsWith(i + "/", StringComparison.Ordinal));

    /// <inheritdoc/>
    /// <remarks>ファイルが import した標準のモジュール (import slang.neural;) の名前を見せる型システムを返す。</remarks>
    internal override ShaderTypeSystem ForTree(ShaderSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ImmutableArray<string> imports = StandardImports(tree.PreprocessResult.Tokens);
        return imports.IsEmpty ? Instance : ByImports.GetOrAdd(string.Join(";", imports), _ => new SlangTypeSystem(imports));
    }

    /// <summary>import した標準のモジュールの名前 (区切りは /)。</summary>
    private static ImmutableArray<string> StandardImports(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        SortedSet<string> imports = new(StringComparer.Ordinal);

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i] is not { Kind: ShaderSyntaxKind.IdentifierToken, Text: "import" or "__import" })
            {
                continue;
            }

            List<string> parts = [];

            for (int j = i + 1; j < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken; j += 2)
            {
                parts.Add(tokens[j].Text);

                if (j + 1 >= tokens.Length || tokens[j + 1].Kind != ShaderSyntaxKind.DotToken)
                {
                    break;
                }
            }

            string name = string.Join("/", parts);

            if (parts.Count > 0 && Instance._modules.Contains(name))
            {
                imports.Add(name);
            }
        }

        return [.. imports];
    }

    /// <summary>slangc に入っている標準のモジュールか (<c>import slang.neural;</c> の <c>slang/neural</c>)。</summary>
    /// <param name="name">モジュールの名前 (区切りは <c>/</c>)。</param>
    /// <returns>標準のモジュールなら <see langword="true"/>。</returns>
    internal bool IsStandardModule(string name) => _modules.Contains(name);

    /// <summary>標準ライブラリの型の名前 (補完のため)。</summary>
    internal IEnumerable<string> StandardLibraryTypeNames => _types;

    /// <summary>コンパイラが組み込みで持つ名前 (<c>__</c> で始まるもの。<c>__floatAsInt</c>、<c>__first</c> など)。</summary>
    private static bool IsCompilerIntrinsic(string? name) => name is not null && name.StartsWith("__", StringComparison.Ordinal);

    /// <summary>大きさを書いた数の型 (int8_t2・uint16_t4x4・float64_t3 など) と、ポインターの大きさの整数 (intptr_t・uintptr_t)。</summary>
    private static readonly System.Text.RegularExpressions.Regex SizedNumericType = new(
        "^(?:u?int(?:8|16|32|64)_t|float(?:16|32|64)_t|u?intptr_t)(?:[1-4](?:x[1-4])?)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Slang の標準ライブラリの型の名前か。</summary>
    /// <param name="name">名前。</param>
    /// <returns>標準ライブラリの型なら <see langword="true"/>。</returns>
    internal bool IsStandardLibraryType(string? name)
        => name is not null && (_types.Contains(name) || SizedNumericType.IsMatch(name));

    /// <inheritdoc/>
    public override ShaderTypeClass Classify(string? typeName) => _hlsl.Classify(typeName);

    /// <inheritdoc/>
    public override bool IsBuiltInTypeName(string? name) => _hlsl.IsBuiltInTypeName(name) || IsStandardLibraryType(name);

    /// <inheritdoc/>
    public override bool TryDescribeNumeric(string? typeName, out ShaderNumericShape shape) => _hlsl.TryDescribeNumeric(typeName, out shape);

    /// <inheritdoc/>
    public override string? Compose(ShaderNumericShape shape) => _hlsl.Compose(shape);

    /// <inheritdoc/>
    public override string? ComposeNumeric(string baseName, int components) => _hlsl.ComposeNumeric(baseName, components);

    /// <inheritdoc/>
    public override bool TryDecomposeNumeric(string? typeName, out string baseName, out int components)
        => _hlsl.TryDecomposeNumeric(typeName, out baseName, out components);

    /// <inheritdoc/>
    public override string? GetWiderBaseName(string left, string right) => _hlsl.GetWiderBaseName(left, right);

    /// <inheritdoc/>
    public override ImmutableArray<string> ComponentNameSets => _hlsl.ComponentNameSets;

    /// <inheritdoc/>
    internal override string? GetLiteralTypeName(ShaderSyntaxToken token) => _hlsl.GetLiteralTypeName(token);

    /// <inheritdoc/>
    public override bool IsConvertible(string? valueType, string? targetType, bool exact = false) => _hlsl.IsConvertible(valueType, targetType, exact);

    /// <inheritdoc/>
    public override bool IsConvertible(ShaderNumericShape value, ShaderNumericShape target, bool exact) => _hlsl.IsConvertible(value, target, exact);

    /// <inheritdoc/>
    public override bool IsTruncating(string? valueType, string? targetType) => _hlsl.IsTruncating(valueType, targetType);

    /// <inheritdoc/>
    /// <remarks>型の名前も値の位置に書ける (sizeof(T)・alignof(void)・countof(T))。</remarks>
    public override bool IsKnownConstant(string? name)
        => _hlsl.IsKnownConstant(name) || (name is not null && _values.Contains(name)) || IsCompilerIntrinsic(name) || IsBuiltInTypeName(name) || name == "void";

    /// <inheritdoc/>
    public override bool IsKnownFunction(string? name, ShaderSyntaxTree tree)
        => ForTree(tree) is SlangTypeSystem imported && !ReferenceEquals(imported, this)
            ? imported.IsKnownFunction(name, tree)
            : _hlsl.IsKnownFunction(name, tree)
           || (name is not null && (_functions.Contains(name) || _functionPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))))
           || IsStandardLibraryType(name)
           || IsCompilerIntrinsic(name);

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> FunctionNames { get; }

    /// <inheritdoc/>
    /// <remarks>標準ライブラリにある名前の関数は、HLSL より多くの形を持ちうるので、引数の数を主張しない。</remarks>
    internal override bool TryGetIntrinsicArity(string name, out IntrinsicArity arity)
    {
        if (_functions.Contains(name))
        {
            arity = default;
            return false;
        }

        return _hlsl.TryGetIntrinsicArity(name, out arity);
    }

    /// <inheritdoc/>
    public override TextureMethodReturnType GetTextureMethodReturnType(string? name) => _hlsl.GetTextureMethodReturnType(name);

    /// <inheritdoc/>
    public override bool IsTextureSamplingFunction(string? name) => _hlsl.IsTextureSamplingFunction(name);

    /// <inheritdoc/>
    public override IntrinsicResultRule GetResultShape(string? name) => _hlsl.GetResultShape(name);

    /// <inheritdoc/>
    public override string? GetFixedBaseType(string? name) => _hlsl.GetFixedBaseType(name);

    /// <inheritdoc/>
    public override string? GetFixedType(string? name) => _hlsl.GetFixedType(name);
}
