using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// WGSL の型システム。
/// </summary>
/// <remarks>
/// <para>
/// <b>型の名前は、短い名前にそろえる</b> (構文解析が <c>vec3&lt;f32&gt;</c> を <c>vec3f</c> にする)。
/// 短い名前の無い型は、型引数を付けて書く (<c>vec3&lt;bool&gt;</c>、<c>vec2&lt;AbstractInt&gt;</c>)。
/// 行列 <c>matCxR</c> (C 列 R 行) の形は、GLSL と同じく <c>Rows = C</c>、<c>Columns = R</c> として持つ。積は行列の積で、<c>m[i]</c> は R 成分の列になる。
/// </para>
/// <para>
/// <b>暗黙の変換は、抽象的な数 (AbstractInt・AbstractFloat) を具体的な型にするものだけである。</b>
/// 型を書かない数のリテラル (<c>1</c>、<c>1.0</c>) が抽象的な数で、<c>i32</c> と <c>f32</c> の間などは変換しない。
/// <c>let</c> / <c>var</c> / <c>override</c> は、型を書かなければ初期値の型を具体的にしたもの (AbstractInt は <c>i32</c>、AbstractFloat は <c>f32</c>) になる。
/// <c>const</c> は抽象的なまま残る。
/// </para>
/// <para>
/// 組み込み関数の名前は naga 30 (<c>front/wgsl</c> の <c>conv.rs</c> と <c>lower/mod.rs</c>) から集めた。
/// 結果の型は、引数から決まるものだけを答える (テクスチャの要素の型・アトミックの中身・構造体を返すものは答えない)。
/// </para>
/// </remarks>
internal sealed class WgslTypeSystem : ShaderTypeSystem
{
    /// <summary>型を書かない整数のリテラルの型。</summary>
    public const string AbstractInt = "AbstractInt";

    /// <summary>型を書かない浮動小数点数のリテラルの型。</summary>
    public const string AbstractFloat = "AbstractFloat";

    /// <summary>共有の型システム。下の表を作ってから作るよう、最初に使うときに作る。</summary>
    public static WgslTypeSystem Instance => Shared.Value;

    private static readonly Lazy<WgslTypeSystem> Shared = new(() => new WgslTypeSystem());

    private static readonly FrozenSet<string> Scalars = FrozenSet.ToFrozenSet(
        ["bool", "i32", "u32", "f32", "f16", "i64", "u64", "f64", "i16", "u16", AbstractInt, AbstractFloat], StringComparer.Ordinal);

    /// <summary>短い名前の接尾辞と要素の型。</summary>
    private static readonly FrozenDictionary<char, string> Suffixes = new Dictionary<char, string>
    {
        ['f'] = "f32",
        ['h'] = "f16",
        ['i'] = "i32",
        ['u'] = "u32",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, char> SuffixOf = new Dictionary<string, char>(StringComparer.Ordinal)
    {
        ["f32"] = 'f',
        ["f16"] = 'h',
        ["i32"] = 'i',
        ["u32"] = 'u',
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>あらかじめ宣言された、型引数を取らない型と、型を作る名前 (<c>vec3</c>、<c>array</c>、<c>texture_2d</c> など)。</summary>
    private static readonly FrozenSet<string> PredeclaredTypes = FrozenSet.ToFrozenSet(
    [
        "bool", "i32", "u32", "f32", "f16", "i64", "u64", "f64", "i16", "u16",
        "vec2", "vec3", "vec4",
        "mat2x2", "mat2x3", "mat2x4", "mat3x2", "mat3x3", "mat3x4", "mat4x2", "mat4x3", "mat4x4",
        "array", "atomic", "ptr", "binding_array",
        "sampler", "sampler_comparison",
        "texture_1d", "texture_2d", "texture_2d_array", "texture_3d", "texture_cube", "texture_cube_array", "texture_multisampled_2d",
        "texture_storage_1d", "texture_storage_2d", "texture_storage_2d_array", "texture_storage_3d",
        "texture_depth_2d", "texture_depth_2d_array", "texture_depth_cube", "texture_depth_cube_array", "texture_depth_multisampled_2d",
        "texture_external",
        "acceleration_structure", "ray_query", "RayDesc", "RayIntersection",
        "coop_mat8x8", "coop_mat16x16",
    ], StringComparer.Ordinal);

    /// <summary>引数と同じ型を返す組み込み関数 (成分ごとの演算)。</summary>
    private static readonly FrozenSet<string> SameAsArgument = FrozenSet.ToFrozenSet(
    [
        "abs", "min", "max", "clamp", "saturate",
        "cos", "cosh", "sin", "sinh", "tan", "tanh", "acos", "acosh", "asin", "asinh", "atan", "atanh", "atan2", "radians", "degrees",
        "ceil", "floor", "round", "fract", "trunc", "ldexp",
        "exp", "exp2", "log", "log2", "pow",
        "normalize", "faceForward", "reflect", "refract", "cross",
        "sign", "fma", "mix", "step", "smoothstep", "sqrt", "inverseSqrt", "quantizeToF16",
        "countTrailingZeros", "countLeadingZeros", "countOneBits", "reverseBits", "extractBits", "insertBits", "firstTrailingBit", "firstLeadingBit",
        "dpdx", "dpdy", "fwidth", "dpdxCoarse", "dpdyCoarse", "fwidthCoarse", "dpdxFine", "dpdyFine", "fwidthFine",
        "subgroupAdd", "subgroupMul", "subgroupMin", "subgroupMax", "subgroupAnd", "subgroupOr", "subgroupXor",
        "subgroupExclusiveAdd", "subgroupExclusiveMul", "subgroupInclusiveAdd", "subgroupInclusiveMul",
        "subgroupBroadcast", "subgroupBroadcastFirst", "subgroupShuffle", "subgroupShuffleDown", "subgroupShuffleUp", "subgroupShuffleXor",
        "quadBroadcast", "quadSwapDiagonal", "quadSwapX", "quadSwapY",
    ], StringComparer.Ordinal);

    /// <summary>引数によらず決まった型を返す組み込み関数。</summary>
    private static readonly FrozenDictionary<string, string> FixedResults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["all"] = "bool",
        ["any"] = "bool",
        ["subgroupAll"] = "bool",
        ["subgroupAny"] = "bool",
        ["arrayLength"] = "u32",
        ["pack4x8snorm"] = "u32",
        ["pack4x8unorm"] = "u32",
        ["pack2x16snorm"] = "u32",
        ["pack2x16unorm"] = "u32",
        ["pack2x16float"] = "u32",
        ["pack4xI8"] = "u32",
        ["pack4xU8"] = "u32",
        ["pack4xI8Clamp"] = "u32",
        ["pack4xU8Clamp"] = "u32",
        ["unpack4x8snorm"] = "vec4f",
        ["unpack4x8unorm"] = "vec4f",
        ["unpack2x16snorm"] = "vec2f",
        ["unpack2x16unorm"] = "vec2f",
        ["unpack2x16float"] = "vec2f",
        ["unpack4xI8"] = "vec4i",
        ["unpack4xU8"] = "vec4u",
        ["dot4I8Packed"] = "i32",
        ["dot4U8Packed"] = "u32",
        ["subgroupBallot"] = "vec4u",
        ["textureNumLayers"] = "u32",
        ["textureNumLevels"] = "u32",
        ["textureNumSamples"] = "u32",
        ["textureSampleCompare"] = "f32",
        ["textureSampleCompareLevel"] = "f32",
        ["textureSampleBaseClampToEdge"] = "vec4f",
        ["workgroupBarrier"] = "void",
        ["storageBarrier"] = "void",
        ["textureBarrier"] = "void",
        ["subgroupBarrier"] = "void",
        ["textureStore"] = "void",
        ["atomicStore"] = "void",
        ["rayQueryInitialize"] = "void",
        ["rayQueryTerminate"] = "void",
        ["rayQueryGenerateIntersection"] = "void",
        ["rayQueryConfirmIntersection"] = "void",
        ["rayQueryProceed"] = "bool",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>結果の型を答えない組み込み関数 (要素の型・中身・構造体が引数の型からは分からないもの)。</summary>
    private static readonly FrozenSet<string> UnknownResults = FrozenSet.ToFrozenSet(
    [
        "modf", "frexp", "select", "bitcast", "workgroupUniformLoad",
        "atomicAdd", "atomicSub", "atomicMax", "atomicMin", "atomicAnd", "atomicOr", "atomicXor", "atomicExchange", "atomicLoad", "atomicCompareExchangeWeak",
        "textureAtomicAdd", "textureAtomicAnd", "textureAtomicMax", "textureAtomicMin", "textureAtomicOr", "textureAtomicXor",
        "textureGather", "textureGatherCompare", "textureLoad",
        "rayQueryGetCandidateIntersection", "rayQueryGetCommittedIntersection", "getCandidateHitVertexPositions", "getCommittedHitVertexPositions", "traceRay",
        "coopLoad", "coopLoadT", "coopMultiplyAdd", "coopStore", "coopStoreT",
    ], StringComparer.Ordinal);

    /// <summary>組み込みの値 (<c>true</c>、<c>false</c>、光線の照会の定数)。</summary>
    private static readonly FrozenDictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["true"] = "bool",
        ["false"] = "bool",
        ["RAY_FLAG_NONE"] = "u32",
        ["RAY_FLAG_FORCE_OPAQUE"] = "u32",
        ["RAY_FLAG_FORCE_NO_OPAQUE"] = "u32",
        ["RAY_FLAG_TERMINATE_ON_FIRST_HIT"] = "u32",
        ["RAY_FLAG_SKIP_CLOSEST_HIT_SHADER"] = "u32",
        ["RAY_FLAG_CULL_BACK_FACING"] = "u32",
        ["RAY_FLAG_CULL_FRONT_FACING"] = "u32",
        ["RAY_FLAG_CULL_OPAQUE"] = "u32",
        ["RAY_FLAG_CULL_NO_OPAQUE"] = "u32",
        ["RAY_FLAG_SKIP_TRIANGLES"] = "u32",
        ["RAY_FLAG_SKIP_AABBS"] = "u32",
        ["RAY_QUERY_INTERSECTION_NONE"] = "u32",
        ["RAY_QUERY_INTERSECTION_TRIANGLE"] = "u32",
        ["RAY_QUERY_INTERSECTION_GENERATED"] = "u32",
        ["RAY_QUERY_INTERSECTION_AABB"] = "u32",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>型を作る名前 (型引数を書かずに値を作ると、要素の型は引数から決まる)。</summary>
    private static readonly FrozenSet<string> Generators = FrozenSet.ToFrozenSet(
    [
        "vec2", "vec3", "vec4",
        "mat2x2", "mat2x3", "mat2x4", "mat3x2", "mat3x3", "mat3x4", "mat4x2", "mat4x3", "mat4x4",
        "array",
    ], StringComparer.Ordinal);

    private readonly FrozenSet<string> _functions;

    private WgslTypeSystem()
    {
        HashSet<string> functions = new(SameAsArgument, StringComparer.Ordinal);
        functions.UnionWith(FixedResults.Keys);
        functions.UnionWith(UnknownResults);
        functions.UnionWith(Generators);
        functions.UnionWith(["length", "distance", "dot", "determinant", "transpose", "textureDimensions",
            "textureSample", "textureSampleBias", "textureSampleGrad", "textureSampleLevel"]);
        _functions = functions.ToFrozenSet(StringComparer.Ordinal);
        FunctionNames = [.. _functions.Order(StringComparer.Ordinal)];
    }

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

        return ShaderTypeClass.Unknown;
    }

    /// <inheritdoc/>
    public override bool IsBuiltInTypeName(string? name)
        => name is not null && (PredeclaredTypes.Contains(name) || TryDescribeNumeric(name, out _));

    /// <inheritdoc/>
    public override bool TryDescribeNumeric(string? typeName, out ShaderNumericShape shape)
    {
        shape = default;

        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }

        if (Scalars.Contains(typeName))
        {
            shape = new ShaderNumericShape(ShaderNumericKind.Scalar, typeName, 1, 1);
            return true;
        }

        // vec3f / vec3<bool>
        if (typeName.StartsWith("vec", StringComparison.Ordinal)
            && typeName.Length >= 5
            && typeName[3] is >= '2' and <= '4'
            && ElementOf(typeName.AsSpan(4)) is { } element)
        {
            shape = new ShaderNumericShape(ShaderNumericKind.Vector, element, 1, typeName[3] - '0');
            return true;
        }

        // mat2x3f / mat2x3<AbstractFloat>。C 列 R 行を Rows = C、Columns = R として持つ。
        if (typeName.StartsWith("mat", StringComparison.Ordinal)
            && typeName.Length >= 7
            && typeName[3] is >= '2' and <= '4'
            && typeName[4] == 'x'
            && typeName[5] is >= '2' and <= '4'
            && ElementOf(typeName.AsSpan(6)) is { } matrixElement
            && matrixElement is "f32" or "f16" or AbstractFloat)
        {
            shape = new ShaderNumericShape(ShaderNumericKind.Matrix, matrixElement, typeName[3] - '0', typeName[5] - '0');
            return true;
        }

        return false;
    }

    /// <summary>短い名前の接尾辞 (<c>f</c>) か型引数 (<c>&lt;bool&gt;</c>) から、要素の型を読む。</summary>
    private static string? ElementOf(ReadOnlySpan<char> rest)
    {
        if (rest.Length == 1)
        {
            return Suffixes.TryGetValue(rest[0], out string? element) ? element : null;
        }

        if (rest.Length > 2 && rest[0] == '<' && rest[^1] == '>')
        {
            string inner = rest[1..^1].ToString();
            return Scalars.Contains(inner) ? inner : null;
        }

        return null;
    }

    /// <summary>要素の型を書く形 (<c>f</c> か <c>&lt;bool&gt;</c>)。</summary>
    private static string ElementSuffix(string baseName)
        => SuffixOf.TryGetValue(baseName, out char suffix) ? suffix.ToString() : $"<{baseName}>";

    /// <inheritdoc/>
    public override string? Compose(ShaderNumericShape shape) => shape.Kind switch
    {
        ShaderNumericKind.Scalar => Scalars.Contains(shape.BaseName) ? shape.BaseName : null,
        ShaderNumericKind.Vector => ComposeNumeric(shape.BaseName, shape.Columns),
        ShaderNumericKind.Matrix when shape.Rows == 1 && shape.Columns == 1 => ComposeNumeric(shape.BaseName, 1),
        ShaderNumericKind.Matrix when shape.BaseName is "f32" or "f16" or AbstractFloat
                                      && shape.Rows is >= 2 and <= 4 && shape.Columns is >= 2 and <= 4
            => $"mat{shape.Rows}x{shape.Columns}{ElementSuffix(shape.BaseName)}",
        _ => null,
    };

    /// <inheritdoc/>
    public override string? ComposeNumeric(string baseName, int components) => components switch
    {
        1 when Scalars.Contains(baseName) => baseName,
        2 or 3 or 4 when Scalars.Contains(baseName) => $"vec{components}{ElementSuffix(baseName)}",
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
    /// <remarks>抽象的な数は、混ぜた相手の具体的な型になる。具体的な型どうしは同じでなければ決めない (演算は誤り)。</remarks>
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

    /// <inheritdoc/>
    public override ImmutableArray<string> ComponentNameSets { get; } = ["xyzw", "rgba"];

    /// <inheritdoc/>
    /// <remarks>
    /// 接尾辞の <c>i</c> は <c>i32</c>、<c>u</c> は <c>u32</c>、<c>f</c> は <c>f32</c>、<c>h</c> は <c>f16</c>。
    /// 接尾辞が無ければ、小数点か指数があれば AbstractFloat、無ければ AbstractInt。16 進数の <c>f</c> は数字なので、指数 (<c>p</c>) の後ろの <c>f</c> だけを接尾辞とする。
    /// </remarks>
    internal override string? GetLiteralTypeName(ShaderSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        string text = token.Text;

        if (text.Length == 0 || !char.IsAsciiDigit(text[0]) && text[0] != '.')
        {
            return null;
        }

        bool hex = text.Length > 1 && text[0] == '0' && text[1] is 'x' or 'X';
        char last = text[^1];

        if (hex)
        {
            int exponent = text.IndexOfAny(['p', 'P']);
            bool floating = exponent >= 0 || text.Contains('.', StringComparison.Ordinal);

            return last switch
            {
                'i' when text[^2] == 'l' => "i64",
                'u' when text[^2] == 'l' => "u64",
                'i' => "i32",
                'u' => "u32",
                'f' when exponent >= 0 => "f32",
                'h' when exponent >= 0 => "f16",
                _ => floating ? AbstractFloat : AbstractInt,
            };
        }

        // 64 ビットの接尾辞 (lf・li・lu。naga の拡張)。
        if (text.Length > 2 && text[^2] == 'l')
        {
            return last switch
            {
                'f' => "f64",
                'i' => "i64",
                'u' => "u64",
                _ => null,
            };
        }

        return last switch
        {
            'i' => "i32",
            'u' => "u32",
            'f' => "f32",
            'h' => "f16",
            _ => text.AsSpan().IndexOfAny(['.', 'e', 'E']) >= 0 ? AbstractFloat : AbstractInt,
        };
    }

    /// <inheritdoc/>
    internal override bool MultipliesMatricesAlgebraically => true;

    /// <summary>型を書かない宣言の、型の名前の位置のキーワード (<c>let</c> / <c>var</c> / <c>const</c> / <c>override</c>)。</summary>
    /// <param name="name">型の名前。</param>
    /// <returns>キーワードなら <see langword="true"/>。</returns>
    internal static bool IsDeclarationKeyword(string name) => name is "let" or "var" or "const" or "override";

    /// <summary>型を書かない宣言の型を、初期値の型から決める。<c>const</c> 以外は抽象的な数を具体的な型にする。</summary>
    /// <param name="keyword">宣言のキーワード。</param>
    /// <param name="initializerType">初期値の型。</param>
    /// <returns>宣言の型。</returns>
    internal string Concretize(string keyword, string initializerType)
    {
        if (keyword == "const" || !TryDescribeNumeric(initializerType, out ShaderNumericShape shape))
        {
            return initializerType;
        }

        string concrete = shape.BaseName switch
        {
            AbstractInt => "i32",
            AbstractFloat => "f32",
            _ => shape.BaseName,
        };

        return concrete == shape.BaseName ? initializerType : Compose(shape with { BaseName = concrete }) ?? initializerType;
    }

    // --------------------------------------------------------------------
    // 変換
    // --------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// 利用者の型 (構造体) は、同じ名前の型にだけ変換できる (<c>let x: Bar = Foo(1);</c> は誤り)。数の型と利用者の型の間も変換しない。
    /// あらかじめ宣言された型 (<c>array</c>・<c>ptr</c>・テクスチャなど) は型引数を名前に含めないので、比べない。
    /// </remarks>
    public override bool IsConvertible(string? valueType, string? targetType, bool exact = false)
    {
        if (valueType == "void")
        {
            return false;
        }

        bool valueNumeric = TryDescribeNumeric(valueType, out ShaderNumericShape value);
        bool targetNumeric = TryDescribeNumeric(targetType, out ShaderNumericShape target);

        if (valueNumeric && targetNumeric)
        {
            return IsConvertible(value, target, exact);
        }

        bool valueUser = IsUserTypeName(valueType);
        bool targetUser = IsUserTypeName(targetType);

        return !((valueUser && targetUser && valueType != targetType)
                 || (valueUser && targetNumeric)
                 || (valueNumeric && targetUser));
    }

    /// <summary>利用者が宣言した型 (構造体) の名前か。あらかじめ宣言された型と、型を書かない宣言のキーワードは除く。</summary>
    private bool IsUserTypeName(string? name)
        => !string.IsNullOrEmpty(name)
           && (char.IsAsciiLetter(name[0]) || name[0] == '_')
           && !IsBuiltInTypeName(name)
           && !IsDeclarationKeyword(name)
           && name is not ("void" or "bool");

    /// <inheritdoc/>
    public override bool IsConvertible(ShaderNumericShape value, ShaderNumericShape target, bool exact)
        => value.Kind == target.Kind
           && value.Rows == target.Rows
           && value.Columns == target.Columns
           && (value.BaseName == target.BaseName || (!exact && IsBaseConvertible(value.BaseName, target.BaseName)));

    /// <summary>要素の型を暗黙に変換できるか (抽象的な数から具体的な型へだけ)。</summary>
    private static bool IsBaseConvertible(string value, string target)
        => value == target
           || (value == AbstractInt && target is "i32" or "u32" or "f32" or "f16" or "i64" or "u64" or "f64" or "i16" or "u16" or AbstractFloat)
           || (value == AbstractFloat && target is "f32" or "f16" or "f64");

    /// <inheritdoc/>
    public override bool IsTruncating(string? valueType, string? targetType) => false;

    // --------------------------------------------------------------------
    // 組み込み
    // --------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsKnownConstant(string? name) => name is not null && Values.ContainsKey(name);

    /// <inheritdoc/>
    internal override ResolvedTypeName? GetBuiltinVariable(string name)
        => Values.TryGetValue(name, out string? type) ? new ResolvedTypeName(type, IsArray: false) : null;

    /// <inheritdoc/>
    public override bool IsKnownFunction(string? name, ShaderSyntaxTree tree) => name is not null && _functions.Contains(name);

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> FunctionNames { get; }

    /// <summary>補完に出す型の名前 (あらかじめ宣言された型と、ベクトル・行列の短い名前)。</summary>
    internal IEnumerable<string> CompletionTypeNames
        => PredeclaredTypes
            .Concat(from n in "234" from s in "fihu" select $"vec{n}{s}")
            .Concat(from c in "234" from r in "234" from s in "fh" select $"mat{c}x{r}{s}")
            .Order(StringComparer.Ordinal);

    /// <summary>組み込みの値の名前と型 (<c>true</c>、光線の照会の定数)。</summary>
    internal IEnumerable<KeyValuePair<string, string>> BuiltinValues => Values.OrderBy(v => v.Key, StringComparer.Ordinal);

    /// <summary>引数の数が決まっている組み込み関数 (WGSL の仕様の組み込み関数の表)。</summary>
    private static readonly FrozenDictionary<string, int> Arities = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["abs"] = 1, ["acos"] = 1, ["acosh"] = 1, ["asin"] = 1, ["asinh"] = 1, ["atan"] = 1, ["atanh"] = 1, ["ceil"] = 1, ["cos"] = 1, ["cosh"] = 1,
        ["countLeadingZeros"] = 1, ["countOneBits"] = 1, ["countTrailingZeros"] = 1, ["degrees"] = 1, ["determinant"] = 1, ["exp"] = 1, ["exp2"] = 1,
        ["firstLeadingBit"] = 1, ["firstTrailingBit"] = 1, ["floor"] = 1, ["fract"] = 1, ["frexp"] = 1, ["inverseSqrt"] = 1, ["length"] = 1,
        ["log"] = 1, ["log2"] = 1, ["modf"] = 1, ["normalize"] = 1, ["quantizeToF16"] = 1, ["radians"] = 1, ["reverseBits"] = 1, ["round"] = 1,
        ["saturate"] = 1, ["sign"] = 1, ["sin"] = 1, ["sinh"] = 1, ["sqrt"] = 1, ["tan"] = 1, ["tanh"] = 1, ["transpose"] = 1, ["trunc"] = 1,
        ["all"] = 1, ["any"] = 1, ["arrayLength"] = 1,
        ["dpdx"] = 1, ["dpdy"] = 1, ["fwidth"] = 1, ["dpdxCoarse"] = 1, ["dpdyCoarse"] = 1, ["fwidthCoarse"] = 1, ["dpdxFine"] = 1, ["dpdyFine"] = 1, ["fwidthFine"] = 1,
        ["pack4x8snorm"] = 1, ["pack4x8unorm"] = 1, ["pack2x16snorm"] = 1, ["pack2x16unorm"] = 1, ["pack2x16float"] = 1,
        ["pack4xI8"] = 1, ["pack4xU8"] = 1, ["pack4xI8Clamp"] = 1, ["pack4xU8Clamp"] = 1,
        ["unpack4x8snorm"] = 1, ["unpack4x8unorm"] = 1, ["unpack2x16snorm"] = 1, ["unpack2x16unorm"] = 1, ["unpack2x16float"] = 1, ["unpack4xI8"] = 1, ["unpack4xU8"] = 1,
        ["atan2"] = 2, ["cross"] = 2, ["distance"] = 2, ["dot"] = 2, ["ldexp"] = 2, ["max"] = 2, ["min"] = 2, ["pow"] = 2, ["reflect"] = 2, ["step"] = 2,
        ["dot4I8Packed"] = 2, ["dot4U8Packed"] = 2,
        ["clamp"] = 3, ["faceForward"] = 3, ["fma"] = 3, ["mix"] = 3, ["refract"] = 3, ["smoothstep"] = 3, ["select"] = 3, ["extractBits"] = 3,
        ["insertBits"] = 4,
        ["workgroupBarrier"] = 0, ["storageBarrier"] = 0, ["textureBarrier"] = 0, ["subgroupBarrier"] = 0,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <inheritdoc/>
    internal override bool TryGetIntrinsicArity(string name, out IntrinsicArity arity)
    {
        if (Arities.TryGetValue(name, out int count))
        {
            arity = new IntrinsicArity([count], -1);
            return true;
        }

        arity = default;
        return false;
    }

    /// <inheritdoc/>
    internal override bool ResolvesIntrinsicsByOverloads => true;

    /// <inheritdoc/>
    internal override bool TryResolveIntrinsic(string name, ImmutableArray<string?> argumentTypes, out string? resultType)
    {
        resultType = null;

        if (!_functions.Contains(name))
        {
            return false;
        }

        if (FixedResults.TryGetValue(name, out string? fixedType))
        {
            resultType = fixedType;
            return true;
        }

        if (UnknownResults.Contains(name) || argumentTypes.IsDefaultOrEmpty)
        {
            return true;
        }

        string? first = argumentTypes[0];

        resultType = name switch
        {
            _ when SameAsArgument.Contains(name) => SameShape(argumentTypes),
            "length" or "distance" or "dot" or "determinant"
                => TryDescribeNumeric(first, out ShaderNumericShape shape) ? ComposeNumeric(Widest(argumentTypes) ?? shape.BaseName, 1) : null,
            "transpose"
                => TryDescribeNumeric(first, out ShaderNumericShape matrix) && matrix.Kind == ShaderNumericKind.Matrix
                    ? Compose(matrix with { Rows = matrix.Columns, Columns = matrix.Rows })
                    : null,
            "textureDimensions" => TextureDimensions(first),
            "textureSample" or "textureSampleBias" or "textureSampleGrad" or "textureSampleLevel"
                => first is null ? null : first.StartsWith("texture_depth", StringComparison.Ordinal) ? "f32" : "vec4f",
            "vec2" or "vec3" or "vec4" => Construct(name[3] - '0', argumentTypes),
            _ when name.StartsWith("mat", StringComparison.Ordinal)
                => Widest(argumentTypes) is { } element && element is "f32" or "f16" or AbstractFloat or AbstractInt
                    ? Compose(new ShaderNumericShape(ShaderNumericKind.Matrix, element == AbstractInt ? AbstractFloat : element, name[3] - '0', name[5] - '0'))
                    : null,
            _ => null,
        };

        return true;
    }

    /// <summary>
    /// 成分ごとの演算の結果の型。引数の中で形が最初の引数と同じもののうち、具体的な型があればそれを、無ければ最初の引数の型を返す。
    /// </summary>
    private string? SameShape(ImmutableArray<string?> argumentTypes)
    {
        if (!TryDescribeNumeric(argumentTypes[0], out ShaderNumericShape first))
        {
            return null;
        }

        string baseName = first.BaseName;

        foreach (string? argument in argumentTypes.Skip(1))
        {
            if (TryDescribeNumeric(argument, out ShaderNumericShape other)
                && other.Kind == first.Kind && other.Rows == first.Rows && other.Columns == first.Columns
                && GetWiderBaseName(baseName, other.BaseName) is { } wider)
            {
                baseName = wider;
            }
        }

        return Compose(first with { BaseName = baseName });
    }

    /// <summary>引数の要素の型を混ぜたもの。分からない引数があれば <see langword="null"/>。</summary>
    private string? Widest(ImmutableArray<string?> argumentTypes)
    {
        string? widest = null;

        foreach (string? argument in argumentTypes)
        {
            if (!TryDescribeNumeric(argument, out ShaderNumericShape shape))
            {
                return null;
            }

            widest = widest is null ? shape.BaseName : GetWiderBaseName(widest, shape.BaseName);

            if (widest is null)
            {
                return null;
            }
        }

        return widest;
    }

    /// <summary>型引数を書かない <c>vecN(…)</c> の型。要素の型は引数から決まる。</summary>
    private string? Construct(int components, ImmutableArray<string?> argumentTypes)
        => Widest(argumentTypes) is { } element ? ComposeNumeric(element, components) : null;

    /// <summary><c>textureDimensions</c> の結果の型。テクスチャの次元で決まる。</summary>
    private static string? TextureDimensions(string? texture) => texture switch
    {
        "texture_1d" or "texture_storage_1d" => "u32",
        "texture_3d" or "texture_storage_3d" => "vec3u",
        null => null,
        _ when texture.StartsWith("texture_", StringComparison.Ordinal) => "vec2u",
        _ => null,
    };

    /// <inheritdoc/>
    public override TextureMethodReturnType GetTextureMethodReturnType(string? name) => TextureMethodReturnType.Unknown;

    /// <inheritdoc/>
    public override bool IsTextureSamplingFunction(string? name) => name is "textureSample" or "textureSampleBias" or "textureSampleCompare";

    /// <inheritdoc/>
    public override IntrinsicResultRule GetResultShape(string? name) => IntrinsicResultRule.Unknown;

    /// <inheritdoc/>
    public override string? GetFixedBaseType(string? name) => null;

    /// <inheritdoc/>
    public override string? GetFixedType(string? name) => null;

    /// <summary>数のリテラルの値を整数として読む (定数式の評価のため)。</summary>
    /// <param name="text">リテラル。</param>
    /// <param name="value">値。</param>
    /// <returns>整数のリテラルなら <see langword="true"/>。</returns>
    internal static bool TryParseInteger(string text, out long value)
    {
        string digits = text.TrimEnd('i', 'u');
        bool hex = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return hex
            ? long.TryParse(digits.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
