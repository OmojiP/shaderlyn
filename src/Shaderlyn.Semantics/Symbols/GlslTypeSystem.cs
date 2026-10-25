using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Glsl;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// GLSL の型と組み込み関数 (<see cref="ShaderTypeSystem"/>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>型の名前と形。</b> <c>vecN</c> / <c>ivecN</c> / <c>uvecN</c> / <c>bvecN</c> / <c>dvecN</c>、<c>matN</c> / <c>matNxM</c> / <c>dmat…</c> と、
/// 明示的な幅の算術型 (GL_EXT_shader_explicit_arithmetic_types の <c>f16vec3</c> など) を読む。
/// 行列は、GLSL の <c>matCxR</c> (C 列 R 行) を <c>Rows = C</c>、<c>Columns = R</c> として持つ
/// (<see cref="ShaderTypeSystem.MultipliesMatricesAlgebraically"/>)。
/// サンプラー・テクスチャ・イメージは、次元で分類する (<c>sampler2D</c> と <c>texture2D</c> と <c>image2D</c> は <see cref="ShaderTypeClass.Texture2D"/>)。
/// </para>
/// <para>
/// <b>暗黙の変換は HLSL よりずっと狭い。</b> 成分の数が違えば変換できない (切り捨ても、スカラーの複製も無い)。
/// 要素の型の変換は、言語のバージョンで変わる (GLSL 4.60 の 4.1.10 節、GLSL ES 3.20 の 4.1.10 節)。
/// </para>
/// <list type="bullet">
///   <item><description>ES と、デスクトップの 1.10: 暗黙の変換は無い</description></item>
///   <item><description>デスクトップの 1.20 から 3.30: <c>int</c> と <c>uint</c> から <c>float</c></description></item>
///   <item><description>デスクトップの 4.00 以降: <c>int</c> から <c>uint</c>、<c>int</c> と <c>uint</c> から <c>float</c>、<c>int</c> と <c>uint</c> と <c>float</c> から <c>double</c></description></item>
/// </list>
/// <para>
/// バージョンが分からない木 (取り込まれる断片) は、4.00 以降として読む (いちばん多くの変換を通す)。
/// 明示的な幅の算術型の変換は、拡張で決まる規則がまだ無いので、同じ型でなければ通す。
/// </para>
/// <para>
/// 組み込みの関数と変数は、まだ知らない (GL2b で足す)。
/// </para>
/// </remarks>
internal sealed class GlslTypeSystem : ShaderTypeSystem
{
    /// <summary>暗黙の変換の範囲。</summary>
    private enum ConversionLevel
    {
        /// <summary>暗黙の変換は無い (ES、デスクトップの 1.10)。</summary>
        None,

        /// <summary>整数から <c>float</c> (デスクトップの 1.20 から 3.30)。</summary>
        IntegerToFloat,

        /// <summary>4.00 以降の変換 (<c>int</c> → <c>uint</c> → <c>float</c> → <c>double</c>)。</summary>
        Full,
    }

    /// <summary>スカラーの型の名前。値は、同じ型の別の名前 (<c>float32_t</c> は <c>float</c>) を揃えた名前。</summary>
    private static readonly FrozenDictionary<string, string> Scalars = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bool"] = "bool",
        ["int"] = "int",
        ["uint"] = "uint",
        ["float"] = "float",
        ["double"] = "double",
        ["int8_t"] = "int8_t",
        ["uint8_t"] = "uint8_t",
        ["int16_t"] = "int16_t",
        ["uint16_t"] = "uint16_t",
        ["int32_t"] = "int",
        ["uint32_t"] = "uint",
        ["int64_t"] = "int64_t",
        ["uint64_t"] = "uint64_t",
        ["float16_t"] = "float16_t",
        ["float32_t"] = "float",
        ["float64_t"] = "double",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>ベクトルの名前の接頭辞 (<c>ivec3</c> の <c>i</c>) から要素の型。</summary>
    private static readonly FrozenDictionary<string, string> VectorPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [""] = "float",
        ["b"] = "bool",
        ["i"] = "int",
        ["u"] = "uint",
        ["d"] = "double",
        ["i8"] = "int8_t",
        ["u8"] = "uint8_t",
        ["i16"] = "int16_t",
        ["u16"] = "uint16_t",
        ["i32"] = "int",
        ["u32"] = "uint",
        ["i64"] = "int64_t",
        ["u64"] = "uint64_t",
        ["f16"] = "float16_t",
        ["f32"] = "float",
        ["f64"] = "double",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>行列の名前の接頭辞 (<c>dmat3</c> の <c>d</c>) から要素の型。</summary>
    private static readonly FrozenDictionary<string, string> MatrixPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [""] = "float",
        ["d"] = "double",
        ["f16"] = "float16_t",
        ["f32"] = "float",
        ["f64"] = "double",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>要素の型から、ベクトルの名前の接頭辞 (名前を作るときに使う)。</summary>
    private static readonly FrozenDictionary<string, string> VectorPrefixOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["float"] = "",
        ["bool"] = "b",
        ["int"] = "i",
        ["uint"] = "u",
        ["double"] = "d",
        ["int8_t"] = "i8",
        ["uint8_t"] = "u8",
        ["int16_t"] = "i16",
        ["uint16_t"] = "u16",
        ["int64_t"] = "i64",
        ["uint64_t"] = "u64",
        ["float16_t"] = "f16",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>要素の型から、行列の名前の接頭辞。</summary>
    private static readonly FrozenDictionary<string, string> MatrixPrefixOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["float"] = "",
        ["double"] = "d",
        ["float16_t"] = "f16",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>4.00 以降の変換の順位。低いほうから高いほうへ変換できる (<c>bool</c> は変換できない)。</summary>
    private static readonly FrozenDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["int"] = 1,
        ["uint"] = 2,
        ["float"] = 3,
        ["double"] = 4,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>サンプラー・テクスチャ・イメージの次元 (<c>sampler2DArray</c> の <c>2DArray</c>) から分類。</summary>
    private static readonly FrozenDictionary<string, ShaderTypeClass> Dimensions = new Dictionary<string, ShaderTypeClass>(StringComparer.Ordinal)
    {
        ["2D"] = ShaderTypeClass.Texture2D,
        ["2DRect"] = ShaderTypeClass.Texture2D,
        ["2DMS"] = ShaderTypeClass.Texture2D,
        ["2DShadow"] = ShaderTypeClass.Texture2D,
        ["2DRectShadow"] = ShaderTypeClass.Texture2D,
        ["2DArray"] = ShaderTypeClass.Texture2DArray,
        ["2DMSArray"] = ShaderTypeClass.Texture2DArray,
        ["2DArrayShadow"] = ShaderTypeClass.Texture2DArray,
        ["3D"] = ShaderTypeClass.Texture3D,
        ["Cube"] = ShaderTypeClass.TextureCube,
        ["CubeShadow"] = ShaderTypeClass.TextureCube,
        ["CubeArray"] = ShaderTypeClass.TextureCubeArray,
        ["CubeArrayShadow"] = ShaderTypeClass.TextureCubeArray,
        ["Buffer"] = ShaderTypeClass.Buffer,
        ["ExternalOES"] = ShaderTypeClass.Texture2D,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>変換の範囲ごとの型システム。</summary>
    private static readonly ConcurrentDictionary<ConversionLevel, GlslTypeSystem> ByLevel = new();

    private readonly ConversionLevel _level;

    private GlslTypeSystem(ConversionLevel level) => _level = level;

    /// <summary>言語のバージョンが分からないときの型システム (4.00 以降の変換)。</summary>
    public static GlslTypeSystem Instance { get; } = For(ConversionLevel.Full);

    /// <summary>言語のバージョンとプロファイルの型システムを返す。</summary>
    /// <param name="version">バージョン。分からなければ <see langword="null"/>。</param>
    /// <param name="profile">プロファイル。</param>
    /// <returns>型システム。</returns>
    public static GlslTypeSystem For(int? version, GlslProfile profile)
        => For(version switch
        {
            null => ConversionLevel.Full,
            _ when profile == GlslProfile.Es => ConversionLevel.None,
            < 120 => ConversionLevel.None,
            < 400 => ConversionLevel.IntegerToFloat,
            _ => ConversionLevel.Full,
        });

    private static GlslTypeSystem For(ConversionLevel level) => ByLevel.GetOrAdd(level, static l => new GlslTypeSystem(l));

    /// <inheritdoc/>
    internal override ShaderTypeSystem ForTree(ShaderSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Language is GlslLanguageOptions language ? For(language.Version, language.Profile) : this;
    }

    /// <inheritdoc/>
    internal override bool MultipliesMatricesAlgebraically => true;

    /// <inheritdoc/>
    internal override bool HasLengthMethod => true;

    /// <inheritdoc/>
    internal override bool ComparesWholeValues => true;

    /// <inheritdoc/>
    public override ImmutableArray<string> ComponentNameSets { get; } = ["xyzw", "rgba", "stpq"];

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> FunctionNames { get; } = [];

    // --------------------------------------------------------------------
    // 型の名前と形
    // --------------------------------------------------------------------

    /// <inheritdoc/>
    public override ShaderTypeClass Classify(string? typeName)
    {
        if (TryDescribeNumeric(typeName, out ShaderNumericShape shape))
        {
            return shape.Kind switch
            {
                ShaderNumericKind.Scalar => ShaderTypeClass.Scalar,
                ShaderNumericKind.Vector => ShaderTypeClass.Vector,
                _ => ShaderTypeClass.Matrix,
            };
        }

        if (typeName is "sampler" or "samplerShadow")
        {
            return ShaderTypeClass.Sampler;
        }

        // [i|u](sampler|texture|image)<次元>
        string name = typeName ?? string.Empty;

        foreach (string kind in (ReadOnlySpan<string>)["sampler", "texture", "image"])
        {
            int start = name.StartsWith(kind, StringComparison.Ordinal) ? 0
                : name.Length > 1 && name[0] is 'i' or 'u' && name.AsSpan(1).StartsWith(kind, StringComparison.Ordinal) ? 1
                : -1;

            if (start >= 0 && Dimensions.TryGetValue(name[(start + kind.Length)..], out ShaderTypeClass dimension))
            {
                return dimension;
            }
        }

        return ShaderTypeClass.Unknown;
    }

    /// <inheritdoc/>
    public override bool TryDescribeNumeric(string? typeName, out ShaderNumericShape shape)
    {
        shape = default;

        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }

        if (Scalars.TryGetValue(typeName, out string? scalar))
        {
            shape = new ShaderNumericShape(ShaderNumericKind.Scalar, scalar, 1, 1);
            return true;
        }

        // ivec3 → 接頭辞 i、3 成分。
        int vec = typeName.IndexOf("vec", StringComparison.Ordinal);

        if (vec >= 0
            && typeName.Length == vec + 4
            && typeName[^1] is >= '2' and <= '4'
            && VectorPrefixes.TryGetValue(typeName[..vec], out string? element))
        {
            shape = new ShaderNumericShape(ShaderNumericKind.Vector, element, 1, typeName[^1] - '0');
            return true;
        }

        // mat3 / mat2x4 / dmat3 → C 列 R 行を Rows = C、Columns = R として持つ。
        int mat = typeName.IndexOf("mat", StringComparison.Ordinal);

        if (mat < 0 || !MatrixPrefixes.TryGetValue(typeName[..mat], out string? matrixElement))
        {
            return false;
        }

        ReadOnlySpan<char> size = typeName.AsSpan(mat + 3);

        if (size.Length == 1 && size[0] is >= '2' and <= '4')
        {
            shape = new ShaderNumericShape(ShaderNumericKind.Matrix, matrixElement, size[0] - '0', size[0] - '0');
            return true;
        }

        if (size.Length == 3 && size[0] is >= '2' and <= '4' && size[1] == 'x' && size[2] is >= '2' and <= '4')
        {
            shape = new ShaderNumericShape(ShaderNumericKind.Matrix, matrixElement, size[0] - '0', size[2] - '0');
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public override string? Compose(ShaderNumericShape shape) => shape.Kind switch
    {
        ShaderNumericKind.Scalar => Scalars.ContainsKey(shape.BaseName) ? shape.BaseName : null,
        ShaderNumericKind.Vector => ComposeNumeric(shape.BaseName, shape.Columns),
        ShaderNumericKind.Matrix when shape.Rows == 1 && shape.Columns == 1 => ComposeNumeric(shape.BaseName, 1),
        ShaderNumericKind.Matrix when MatrixPrefixOf.TryGetValue(shape.BaseName, out string? prefix)
                                      && shape.Rows is >= 2 and <= 4 && shape.Columns is >= 2 and <= 4
            => shape.Rows == shape.Columns ? $"{prefix}mat{shape.Rows}" : $"{prefix}mat{shape.Rows}x{shape.Columns}",
        _ => null,
    };

    /// <inheritdoc/>
    public override string? ComposeNumeric(string baseName, int components) => components switch
    {
        1 when Scalars.ContainsKey(baseName) => baseName,
        2 or 3 or 4 when VectorPrefixOf.TryGetValue(baseName, out string? prefix) => $"{prefix}vec{components}",
        _ => null,
    };

    /// <inheritdoc/>
    public override bool TryDecomposeNumeric(string? typeName, out string baseName, out int components)
    {
        if (TryDescribeNumeric(typeName, out ShaderNumericShape shape) && shape.Kind != ShaderNumericKind.Matrix)
        {
            baseName = shape.BaseName;
            components = shape.Columns;
            return true;
        }

        baseName = string.Empty;
        components = 0;
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>片方からもう片方へ暗黙に変換できるときだけ答える。できなければ演算は誤りなので、型を主張しない。</remarks>
    public override string? GetWiderBaseName(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return left;
        }

        return IsBaseConvertible(left, right) ? right
            : IsBaseConvertible(right, left) ? left
            : null;
    }

    // --------------------------------------------------------------------
    // 変換
    // --------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsConvertible(string? valueType, string? targetType, bool exact = false)
        => valueType != "void"
           && (!TryDescribeNumeric(valueType, out ShaderNumericShape value)
               || !TryDescribeNumeric(targetType, out ShaderNumericShape target)
               || IsConvertible(value, target, exact));

    /// <inheritdoc/>
    /// <remarks>
    /// 形 (成分の数) が同じで、要素の型が暗黙に変換できるときだけ通す。
    /// <paramref name="exact"/> (<c>out</c> / <c>inout</c>) では、書き戻す向きの変換も要るので、どちらかの向きに変換できれば通す。
    /// </remarks>
    public override bool IsConvertible(ShaderNumericShape value, ShaderNumericShape target, bool exact)
    {
        if (value.Kind != target.Kind || value.Rows != target.Rows || value.Columns != target.Columns)
        {
            return false;
        }

        // 行列の暗黙の変換は float から double だけである (int の行列は無い)。
        return IsBaseConvertible(value.BaseName, target.BaseName)
               || (exact && IsBaseConvertible(target.BaseName, value.BaseName));
    }

    /// <inheritdoc/>
    /// <remarks>GLSL は成分を減らす変換を誤りにする (<see cref="IsConvertible(string?, string?, bool)"/> が通さない)。</remarks>
    public override bool IsTruncating(string? valueType, string? targetType) => false;

    /// <summary>要素の型を、別の要素の型へ暗黙に変換できるかを判定する。</summary>
    /// <param name="from">値の要素の型。</param>
    /// <param name="to">変換先の要素の型。</param>
    /// <returns>変換できるか、明示的な幅の算術型が絡んで分からなければ <see langword="true"/>。</returns>
    private bool IsBaseConvertible(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return true;
        }

        // 明示的な幅の算術型 (int16_t など) の変換は、拡張の規則をまだ持たない。誤りと言わない。
        if (!Ranks.ContainsKey(from) && from != "bool" || !Ranks.ContainsKey(to) && to != "bool")
        {
            return true;
        }

        return _level switch
        {
            ConversionLevel.Full => Ranks.TryGetValue(from, out int a) && Ranks.TryGetValue(to, out int b) && a < b,
            ConversionLevel.IntegerToFloat => from is "int" or "uint" && to == "float",
            _ => false,
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接尾辞は GLSL の書き方で読む。<c>u</c> は <c>uint</c>、<c>f</c> は <c>float</c>、<c>lf</c> は <c>double</c>。
    /// 明示的な幅の算術型の接尾辞 (<c>hf</c>、<c>l</c>、<c>ul</c>) も読む。
    /// 16 ビットの整数の接尾辞 (<c>s</c>、<c>us</c>) は、共通の字句解析が読まない (1 つのトークンにならない) ので、ここには来ない。
    /// </remarks>
    internal override string? GetLiteralTypeName(ShaderSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.Kind != ShaderSyntaxKind.NumericLiteralToken)
        {
            return null;
        }

        // 16 進では f と h は桁なので、接尾辞にならない。
        string text = token.Text;
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string suffixCharacters = hex ? "uUlL" : "uUlLfFhH";
        int end = text.Length;

        while (end > (hex ? 2 : 0) && suffixCharacters.Contains(text[end - 1], StringComparison.Ordinal))
        {
            end--;
        }

        if (end == 0)
        {
            return null;
        }

        bool floating = !hex && text.AsSpan(0, end).IndexOfAny(".eE") >= 0;

        return text[end..].ToLowerInvariant() switch
        {
            "lf" => "double",
            "hf" => "float16_t",
            "f" => "float",
            "ul" or "lu" => "uint64_t",
            "l" => floating ? "double" : "int64_t",
            "u" => "uint",
            "" => floating ? "float" : "int",
            _ => null,
        };
    }

    // --------------------------------------------------------------------
    // 組み込み (GL2b で足す)
    // --------------------------------------------------------------------

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
