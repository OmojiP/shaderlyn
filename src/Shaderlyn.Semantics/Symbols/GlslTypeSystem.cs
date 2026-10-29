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
/// 要素の型の変換は、言語のバージョンと、有効にした拡張で変わる (<see cref="GlslConversions"/>。glslang と同じ)。
/// ES の 3.10 より前と、デスクトップの 1.10 には暗黙の変換が無い。
/// </para>
/// <para>
/// バージョンが分からない木 (取り込まれる断片) は、4.60 ですべての拡張を有効にしたものとして読む (いちばん多くの変換を通す)。
/// </para>
/// <para>
/// 組み込みの関数と変数は、glslang から作った表 (<see cref="GlslBuiltins"/>) を、木のバージョン・プロファイル・ステージで絞って引く。
/// </para>
/// </remarks>
internal sealed class GlslTypeSystem : ShaderTypeSystem
{
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

        // bfloat16 (GL_EXT_bfloat16) と 8 ビットの浮動小数点数 (GL_EXT_float_e5m2 / GL_EXT_float_e4m3 など)
        ["bfloat16_t"] = "bfloat16_t",
        ["floate5m2_t"] = "floate5m2_t",
        ["floate4m3_t"] = "floate4m3_t",
        ["floate2m1_t"] = "floate2m1_t",
        ["floate3m2_t"] = "floate3m2_t",
        ["floate2m3_t"] = "floate2m3_t",
        ["floatue8m0_t"] = "floatue8m0_t",
        ["floatmxint8_t"] = "floatmxint8_t",
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
        ["bf16"] = "bfloat16_t",
        ["fe5m2"] = "floate5m2_t",
        ["fe4m3"] = "floate4m3_t",
        ["fe2m1"] = "floate2m1_t",
        ["fe3m2"] = "floate3m2_t",
        ["fe2m3"] = "floate2m3_t",
        ["fue8m0"] = "floatue8m0_t",
        ["fmxint8"] = "floatmxint8_t",
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
        ["bfloat16_t"] = "bf16",
        ["floate5m2_t"] = "fe5m2",
        ["floate4m3_t"] = "fe4m3",
        ["floate2m1_t"] = "fe2m1",
        ["floate3m2_t"] = "fe3m2",
        ["floate2m3_t"] = "fe2m3",
        ["floatue8m0_t"] = "fue8m0",
        ["floatmxint8_t"] = "fmxint8",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>要素の型から、行列の名前の接頭辞。</summary>
    private static readonly FrozenDictionary<string, string> MatrixPrefixOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["float"] = "",
        ["double"] = "d",
        ["float16_t"] = "f16",
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

    /// <summary>バージョン・プロファイル・ステージ・数値の機能ごとの型システム。</summary>
    private static readonly ConcurrentDictionary<(int? Version, GlslProfile Profile, GlslShaderStage? Stage, GlslNumericFeatures Features, bool? Vulkan), GlslTypeSystem> ByConfiguration = new();

    /// <summary>組み込みの表で、どの型にも合う仮引数と、実引数と同じ型の戻り値を表す名前 (<c>nonuniformEXT</c>)。</summary>
    private const string AnyType = "*";

    private readonly GlslNumericFeatures _features;
    private readonly int? _version;
    private readonly GlslProfile _profile;
    private readonly GlslShaderStage? _stage;
    private readonly bool? _vulkan;
    private GlslBuiltinView? _builtins;

    private GlslTypeSystem(int? version, GlslProfile profile, GlslShaderStage? stage, GlslNumericFeatures features, bool? vulkan)
    {
        _version = version;
        _profile = profile;
        _stage = stage;
        _vulkan = vulkan;
        _features = version is null ? GlslNumericFeatures.All : features;
    }

    /// <summary>言語のバージョンもステージも分からないときの型システム (4.00 以降の変換、すべての組み込み)。</summary>
    public static GlslTypeSystem Instance { get; } = For(null, GlslProfile.Core);

    /// <summary>言語のバージョン・プロファイル・ステージの型システムを返す。</summary>
    /// <param name="version">バージョン。分からなければ <see langword="null"/>。</param>
    /// <param name="profile">プロファイル。</param>
    /// <param name="stage">ステージ。分からなければ <see langword="null"/>。</param>
    /// <param name="features">有効にした拡張で決まる数値の機能。</param>
    /// <param name="vulkan">Vulkan 向けか。分からなければ <see langword="null"/> (組み込みは Vulkan と OpenGL を合わせる)。</param>
    /// <returns>型システム。</returns>
    public static GlslTypeSystem For(
        int? version,
        GlslProfile profile,
        GlslShaderStage? stage = null,
        GlslNumericFeatures features = GlslNumericFeatures.None,
        bool? vulkan = null)
        => ByConfiguration.GetOrAdd(
            (version, profile, stage, features, vulkan),
            static key => new GlslTypeSystem(key.Version, key.Profile, key.Stage, key.Features, key.Vulkan));

    /// <summary>この構成で使える組み込みの関数と変数。表は初めて使うときに読む。</summary>
    internal GlslBuiltinView Builtins => _builtins ??= GlslBuiltins.Instance.For(_version, _profile, _stage, _vulkan);

    /// <inheritdoc/>
    internal override ShaderTypeSystem ForTree(ShaderSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Language is GlslLanguageOptions language
            ? For(language.Version, language.Profile, language.Stage, language.NumericFeatures, language.Vulkan)
            : this;
    }

    /// <inheritdoc/>
    internal override bool MultipliesMatricesAlgebraically => true;

    /// <inheritdoc/>
    internal override bool HasLengthMethod => true;

    /// <inheritdoc/>
    internal override bool ComparesWholeValues => true;

    /// <inheritdoc/>
    internal override bool ConstructorsDropExtraComponents => true;

    /// <inheritdoc/>
    public override ImmutableArray<string> ComponentNameSets { get; } = ["xyzw", "rgba", "stpq"];

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> FunctionNames => Builtins.FunctionNames;

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

        // [i|u|f16](sampler|texture|image)<次元>
        string name = typeName ?? string.Empty;

        foreach (string kind in (ReadOnlySpan<string>)["sampler", "texture", "image"])
        {
            int start = name.StartsWith(kind, StringComparison.Ordinal) ? 0
                : name.Length > 1 && name[0] is 'i' or 'u' && name.AsSpan(1).StartsWith(kind, StringComparison.Ordinal) ? 1
                : name.StartsWith("f16", StringComparison.Ordinal) && name.AsSpan(3).StartsWith(kind, StringComparison.Ordinal) ? 3
                : -1;

            if (start >= 0 && Dimensions.TryGetValue(name[(start + kind.Length)..], out ShaderTypeClass dimension))
            {
                return dimension;
            }
        }

        return ShaderTypeClass.Unknown;
    }

    /// <inheritdoc/>
    /// <remarks>構文解析の型の表 (glslang 16.6.0 の文法の型) と同じ。</remarks>
    public override bool IsBuiltInTypeName(string? name) => name is not null && Glsl.Parsing.GlslKeywords.IsBuiltInType(name);

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
    /// <returns>変換できれば <see langword="true"/>。</returns>
    internal bool IsBaseConvertible(string from, string to)
        => GlslConversions.CanImplicitlyPromote(from, to, _version ?? 460, _version is not null && _profile == GlslProfile.Es, _features);

    /// <summary>二項の演算子で、両辺の要素の型をそろえる先を求める (glslang の <c>getConversionDestinationType</c>)。</summary>
    /// <param name="left">左の要素の型。</param>
    /// <param name="right">右の要素の型。</param>
    /// <returns>そろえる先の要素の型。そろえられなければ <see langword="null"/>。</returns>
    internal string? GetConversionDestination(string left, string right)
        => GlslConversions.GetConversionDestination(left, right, _version ?? 460, _version is not null && _profile == GlslProfile.Es, _features);

    /// <inheritdoc/>
    /// <remarks>
    /// 接尾辞は GLSL の書き方で読む (glslang 16.6.0 の字句解析)。<c>u</c> は <c>uint</c>、<c>f</c> は <c>float</c>、<c>lf</c> は <c>double</c>。
    /// 明示的な幅の算術型の接尾辞 (<c>hf</c>、<c>l</c>、<c>ul</c>、<c>s</c>、<c>us</c>) と、8 ビットの浮動小数点数の接尾辞 (<c>fe2m1</c> など) も読む。
    /// <c>l</c> と <c>s</c> を両方持つもの (<c>uls</c>) は、glslang と同じく 64 ビットとする。
    /// </remarks>
    internal override string? GetLiteralTypeName(ShaderSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.Kind != ShaderSyntaxKind.NumericLiteralToken || ShaderLiteral.GetDigits(token).Length == 0)
        {
            return null;
        }

        bool floating = ShaderLiteral.GetKind(token) == ShaderLiteralKind.Floating;
        string suffix = ShaderLiteral.GetSuffix(token);

        return suffix switch
        {
            "fe2m1" => "floate2m1_t",
            "fe3m2" => "floate3m2_t",
            "fe2m3" => "floate2m3_t",
            "fue8m0" => "floatue8m0_t",
            "fmxint8" => "floatmxint8_t",
            _ => suffix.ToLowerInvariant() switch
            {
                "lf" => "double",
                "hf" => "float16_t",
                "f" => "float",
                "ul" or "uls" => "uint64_t",
                "l" => floating ? "double" : "int64_t",
                "us" => "uint16_t",
                "s" => "int16_t",
                "u" => "uint",
                "" => floating ? "float" : "int",
                _ => null,
            },
        };
    }

    // --------------------------------------------------------------------
    // 組み込み
    // --------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>組み込み変数 (<c>gl_FragCoord</c>、<c>gl_MaxDrawBuffers</c> など) を含む。この型システムのステージとバージョンで使えるものだけ。</remarks>
    public override bool IsKnownConstant(string? name) => name is not null && Builtins.GetVariable(name) is not null;

    /// <inheritdoc/>
    public override bool IsKnownFunction(string? name, ShaderSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return name is not null && !((GlslTypeSystem)ForTree(tree)).Builtins.GetFunctions(name).IsEmpty;
    }

    /// <inheritdoc/>
    internal override bool TryGetIntrinsicArity(string name, out IntrinsicArity arity)
    {
        ImmutableArray<GlslBuiltinFunction> overloads = Builtins.GetFunctions(name);
        int[] variadic = [.. overloads.Where(f => f.IsVariadic).Select(f => f.FixedParameterCount)];
        arity = new IntrinsicArity(
            [.. overloads.Where(f => !f.IsVariadic).Select(f => f.Parameters.Length).Distinct().Order()],
            variadic.Length == 0 ? -1 : variadic.Min());
        return !overloads.IsEmpty;
    }

    /// <inheritdoc/>
    internal override bool ResolvesIntrinsicsByOverloads => true;

    /// <inheritdoc/>
    internal override ResolvedTypeName? GetBuiltinVariable(string name)
    {
        if (Builtins.GetVariable(name) is not { } variable)
        {
            return null;
        }

        // vec4[] / float[32] / block[32]
        int bracket = variable.Type.IndexOf('[', StringComparison.Ordinal);
        return bracket < 0
            ? new ResolvedTypeName(variable.Type, IsArray: false)
            : new ResolvedTypeName(variable.Type[..bracket], IsArray: true, ArrayDimensions: variable.Type.Count(c => c == '['));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 引数の数が合う多重定義のうち、実引数の型と同じ仮引数を持つもの、無ければ暗黙に変換できるものを選ぶ (GLSL 4.60 の 6.1 節)。
    /// 型の分からない実引数はどの仮引数にも合うものとする。選んだ多重定義の戻り値の型が 1 つに決まるときだけ答える。
    /// </remarks>
    internal override bool TryResolveIntrinsic(string name, ImmutableArray<string?> argumentTypes, out string? resultType)
    {
        resultType = null;
        ImmutableArray<GlslBuiltinFunction> overloads = Builtins.GetFunctions(name);

        if (overloads.IsEmpty)
        {
            return false;
        }

        // 型の引数を取る型 (vector<float16_t, 5>、coopmat<…>) の値を渡すと、戻り値の型は型の引数で決まる。
        // 表には型の引数の無い形 (float length(vector)) しか無いので、型を決めない。
        if (argumentTypes.Any(t => t is not null && Glsl.Parsing.GlslKeywords.ParameterizedTypes.Contains(t)))
        {
            return true;
        }

        GlslBuiltinFunction[] fitting =
            [.. overloads.Where(f => f.IsVariadic ? argumentTypes.Length >= f.FixedParameterCount : f.Parameters.Length == argumentTypes.Length)];
        GlslBuiltinFunction[] exact = [.. fitting.Where(f => Matches(f, exact: true))];
        GlslBuiltinFunction[] chosen = exact.Length > 0 ? exact : [.. fitting.Where(f => Matches(f, exact: false))];
        string?[] results = [.. chosen.Select(ReturnTypeOf).Distinct(StringComparer.Ordinal)];

        resultType = results.Length == 1 ? results[0] : null;
        return true;

        // * (nonuniformEXT) は実引数と同じ型を返す。
        string? ReturnTypeOf(GlslBuiltinFunction function)
            => function.ReturnType == AnyType
                ? function.Parameters.IndexOf(AnyType) is >= 0 and int index ? argumentTypes[index] : null
                : function.ReturnType;

        bool Matches(GlslBuiltinFunction function, bool exact)
        {
            for (int i = 0; i < Math.Min(argumentTypes.Length, function.FixedParameterCount); i++)
            {
                if (argumentTypes[i] is not { } argument)
                {
                    continue;
                }

                string parameter = function.Parameters[i];

                if (parameter == AnyType)
                {
                    continue;
                }

                if (exact ? !SameType(argument, parameter) : !IsConvertible(argument, parameter, function.WritesBack[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>2 つの型の名前が同じ型を指すか (<c>mat3</c> と <c>mat3x3</c>、<c>vec3</c> と <c>f32vec3</c>)。</summary>
    /// <param name="left">一方の型の名前。</param>
    /// <param name="right">もう一方の型の名前。</param>
    /// <returns>同じ型なら <see langword="true"/>。</returns>
    private bool SameType(string left, string right)
        => string.Equals(left, right, StringComparison.Ordinal)
           || (TryDescribeNumeric(left, out ShaderNumericShape a) && TryDescribeNumeric(right, out ShaderNumericShape b) && a == b);

    /// <inheritdoc/>
    public override TextureMethodReturnType GetTextureMethodReturnType(string? name) => TextureMethodReturnType.Unknown;

    /// <inheritdoc/>
    /// <remarks>テクスチャを読む組み込み関数 (<c>texture</c>、<c>textureLod</c>、<c>texelFetch</c> など)。</remarks>
    public override bool IsTextureSamplingFunction(string? name)
        => name is not null
           && (name.StartsWith("texture", StringComparison.Ordinal) || name.StartsWith("texel", StringComparison.Ordinal))
           && !Builtins.GetFunctions(name).IsEmpty;

    /// <inheritdoc/>
    public override IntrinsicResultRule GetResultShape(string? name) => IntrinsicResultRule.Unknown;

    /// <inheritdoc/>
    public override string? GetFixedBaseType(string? name) => null;

    /// <inheritdoc/>
    public override string? GetFixedType(string? name) => null;
}
