using System.Collections.Frozen;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// 組み込み関数が結果の型をどう決めるか。
/// </summary>
public enum IntrinsicResultRule
{
    /// <summary>型が決まらない。表に無い関数はこれになる。</summary>
    Unknown,

    /// <summary>引数の型をそのまま返す。引数が複数ある場合は、揃えた結果の型になる。</summary>
    SameAsArguments,

    /// <summary>引数の基底型のスカラーを返す。<c>dot</c> や <c>length</c>。</summary>
    ScalarOfArguments,

    /// <summary>常に <c>bool</c> を返す。<c>all</c> や <c>any</c>。</summary>
    Bool,

    /// <summary>成分ごとに <c>bool</c> を返す。<c>isnan</c> など。</summary>
    BoolPerComponent,

    /// <summary>行列の積。<c>mul</c>。</summary>
    MatrixProduct,

    /// <summary>行列を転置する。<c>transpose</c>。</summary>
    Transpose,

    /// <summary>行列式。<c>determinant</c>。</summary>
    Determinant,

    /// <summary>
    /// 引数と同じ形で、基底型だけが決まっている。
    /// </summary>
    /// <remarks>
    /// <c>asfloat</c> のようにビット列を読み直すものも、
    /// <c>sign(float3)</c> → <c>int3</c> のように型が決まっているものも同じ形である。
    /// 成分の数は引数のまま、基底型だけが関数ごとに決まっている。
    /// </remarks>
    FixedBaseType,

    /// <summary>値を返さない。<c>clip</c> や <c>sincos</c>。</summary>
    /// <remarks>
    /// 戻り値が無いことを知っていれば、値として使っている箇所を報告できる。
    /// 「分からない」と扱うと、そこでは報告できない。
    /// </remarks>
    Void,

    /// <summary>最初の実引数と同じ型を返す。<c>WaveReadLaneAt</c> など。</summary>
    /// <remarks>2 つ目以降の引数は、どこを見るかを指すだけで結果の型に関わらない。</remarks>
    SameAsFirstArgument,

    /// <summary>引数によらず型が決まっている。旧世代の <c>tex2D</c> など。</summary>
    FixedType,
}

/// <summary>
/// テクスチャのメソッドが何を返すか。
/// </summary>
public enum TextureMethodReturnType
{
    /// <summary>型が決まらない。表に無いメソッドはこれになる。</summary>
    Unknown,

    /// <summary>テクセルの型。テンプレート引数があればその型、無ければ <c>float4</c>。</summary>
    Element,

    /// <summary>比較の結果。スカラー。</summary>
    Scalar,
}

/// <summary>
/// HLSL の組み込み関数のうち、結果の型を規則で決められるもの。
/// </summary>
/// <remarks>
/// <para>
/// <b>組み込み関数には宣言が無い。</b>
/// コンパイラが持っているものであり、include したヘッダのどこにも書かれていない。
/// 構文木から戻り値の型を引くことができないため、表として持つしかない。
/// </para>
/// <para>
/// <b>載せるかどうかは、規則で結果の型が決まるかで決める。</b>
/// 行列の規則が絡む <c>mul</c> も、ビット列を読み替える <c>asfloat</c> も、
/// 規則が書けるので載せている。
/// 載っていない関数は「型が分からない」として扱われ、何も報告されない。
/// </para>
/// <para>
/// <b>何が載っていないかは、実物と一覧の両方で数えた。</b>
/// Unity 同梱の 109 件に現れる呼び出し 245,665 か所のうち、
/// 型が決まらなかったのは 11 種 3,722 か所だった。
/// 実物に出ないものも含めて片付けるため、
/// Microsoft のリファレンスの一覧 (135 件) とも突き合わせ、
/// <see cref="FunctionNames"/> に載っている 172 件すべてで型が決まるようにした。
/// </para>
/// <para>
/// <b>「実物に出ないから」は載せない理由にならない。</b>
/// 出ないのは Unity 同梱のシェーダーの話であって、利用者のシェーダーの話ではない。
/// 規則が書けるものは書く。書けないものだけを <see cref="IntrinsicResultRule.Unknown"/> に残す。
/// </para>
/// <para>
/// 利用者が同じ名前の関数を定義していた場合は、そちらの宣言が優先される。
/// この表は宣言が見つからなかったときの最後の手段である。
/// </para>
/// </remarks>
public static class HlslIntrinsics
{
    /// <summary>
    /// 引数と同じ型を返す関数。
    /// </summary>
    /// <remarks>
    /// 成分ごとに計算する関数である。引数が複数ある場合、
    /// 結果は引数どうしを揃えた型になる (<c>lerp(half3, half3, half)</c> は <c>half3</c>)。
    /// </remarks>
    private static readonly string[] SameAsArgumentsNames =
    [
        // 1 引数
        "abs", "saturate", "floor", "ceil", "frac", "round", "trunc",
        "sqrt", "rsqrt", "rcp", "exp", "exp2", "log", "log2", "log10",
        "sin", "cos", "tan", "sinh", "cosh", "tanh", "asin", "acos", "atan",
        "radians", "degrees", "normalize",
        "ddx", "ddy", "ddx_coarse", "ddy_coarse", "ddx_fine", "ddy_fine", "fwidth",

        // 複数引数
        "min", "max", "clamp", "lerp", "mad", "pow", "fmod", "step", "smoothstep",
        "atan2", "reflect", "refract", "cross", "faceforward", "ldexp",

        // 小数部を返す。整数部は out の引数へ書く。
        "modf",

        // 距離ベクトル。両方の引数が同じ形である。
        "dst", "fma",
    ];

    /// <summary>
    /// 成分ごとに <c>bool</c> を返す関数。
    /// </summary>
    /// <remarks>成分数は引数のまま。<c>isnan(float3)</c> は <c>bool3</c> である。</remarks>
    private static readonly string[] BoolPerComponentNames = ["isnan", "isinf", "isfinite"];

    /// <summary>引数の基底型のスカラーを返す関数。</summary>
    private static readonly string[] ScalarOfArgumentsNames = ["dot", "length", "distance"];

    /// <summary>
    /// 引数と同じ形で、基底型が決まっている関数。
    /// </summary>
    /// <remarks>
    /// <b>実物に多い。</b> Unity 同梱の 109 件を数えると、
    /// <c>f32tof16</c> と <c>f16tof32</c> だけで 1,819 か所ある。
    /// 表に載せないと、その戻り値を使った式はすべて「型が分からない」になる。
    /// </remarks>
    private static readonly (string Name, string BaseType)[] FixedBaseTypeNames =
    [
        // ビット列をそのまま別の型として読み直す。
        ("asfloat", "float"),
        ("asint", "int"),
        ("asuint", "uint"),

        // 半精度との相互変換。ビット列を uint に詰める。
        ("f32tof16", "uint"),
        ("f16tof32", "float"),

        // ビット演算。符号なし整数で答える。
        ("countbits", "uint"),
        ("reversebits", "uint"),
        ("firstbithigh", "uint"),
        ("firstbitlow", "uint"),

        // -1 / 0 / 1 を返す。float を渡しても int である。
        ("sign", "int"),

        // 32 ビット 2 つを 64 ビットとして読み直す。成分数は引数のまま。
        ("asdouble", "double"),
    ];

    /// <summary>
    /// 最初の実引数と同じ型を返す関数。
    /// </summary>
    /// <remarks>
    /// 2 つ目以降の引数は、レーンの番号や標本の位置といった「どこを見るか」であり、
    /// 結果の型には関わらない。
    /// <see cref="SameAsArgumentsNames"/> のように引数どうしを揃えると、
    /// <c>WaveReadLaneAt(float3, uint)</c> のような組み合わせで型を落とす。
    /// </remarks>
    private static readonly string[] SameAsFirstArgumentNames =
    [
        // 仮数部を返す。指数部は out の引数へ書く。
        "frexp",

        // 添字が一様でないことを伝えるだけで、値も型も変えない。
        "NonUniformResourceIndex",

        // 標本の位置を変えて属性を読み直す。
        "EvaluateAttributeCentroid", "EvaluateAttributeAtSample", "EvaluateAttributeSnapped",

        // 波内の値を配る・集約する。結果は元の式の型のままである。
        "WaveReadLaneAt", "WaveReadLaneFirst",
        "WaveActiveBitAnd", "WaveActiveBitOr", "WaveActiveBitXor",
        "WaveActiveMax", "WaveActiveMin", "WaveActiveProduct", "WaveActiveSum",
        "WavePrefixProduct", "WavePrefixSum",
        "QuadReadAcrossDiagonal", "QuadReadAcrossX", "QuadReadAcrossY", "QuadReadLaneAt",
    ];

    /// <summary>値を返さない関数。</summary>
    /// <remarks>
    /// <c>sincos</c> や <c>InterlockedAdd</c> は結果を <c>out</c> の引数へ書く。
    /// <c>clip</c> は値を捨てて描画そのものを止める。
    /// </remarks>
    private static readonly string[] VoidNames =
    [
        "clip", "sincos", "abort", "errorf", "printf",

        "AllMemoryBarrier", "AllMemoryBarrierWithGroupSync",
        "DeviceMemoryBarrier", "DeviceMemoryBarrierWithGroupSync",
        "GroupMemoryBarrier", "GroupMemoryBarrierWithGroupSync",

        // 元の値は out の引数へ返る。
        "InterlockedAdd", "InterlockedAnd", "InterlockedCompareExchange",
        "InterlockedCompareStore", "InterlockedExchange", "InterlockedMax",
        "InterlockedMin", "InterlockedOr", "InterlockedXor",

        // 補正した分割数は out の引数へ返る。
        "Process2DQuadTessFactorsAvg", "Process2DQuadTessFactorsMax", "Process2DQuadTessFactorsMin",
        "ProcessIsolineTessFactors",
        "ProcessQuadTessFactorsAvg", "ProcessQuadTessFactorsMax", "ProcessQuadTessFactorsMin",
        "ProcessTriTessFactorsAvg", "ProcessTriTessFactorsMax", "ProcessTriTessFactorsMin",

        // レイトレーシング (DXR、シェーダーモデル 6.3)。
        "TraceRay", "CallShader", "IgnoreHit", "AcceptHitAndEndSearch",
    ];

    /// <summary>
    /// 引数によらず型が決まっている関数。
    /// </summary>
    /// <remarks>
    /// 型は Microsoft のリファレンス (dx-graphics-hlsl-intrinsic-functions) の
    /// 各関数のページに書かれている戻り値をそのまま写した。
    /// </remarks>
    private static readonly (string Name, string Type)[] FixedTypeNames =
    [
        // 旧世代のテクスチャ取得。サンプラの型に関わらず float4 を返す。
        ("tex1D", "float4"), ("tex1Dbias", "float4"), ("tex1Dgrad", "float4"),
        ("tex1Dlod", "float4"), ("tex1Dproj", "float4"),
        ("tex2D", "float4"), ("tex2Dbias", "float4"), ("tex2Dgrad", "float4"),
        ("tex2Dlod", "float4"), ("tex2Dproj", "float4"),
        ("tex3D", "float4"), ("tex3Dbias", "float4"), ("tex3Dgrad", "float4"),
        ("tex3Dlod", "float4"), ("tex3Dproj", "float4"),
        ("texCUBE", "float4"), ("texCUBEbias", "float4"), ("texCUBEgrad", "float4"),
        ("texCUBElod", "float4"), ("texCUBEproj", "float4"),

        // 旧世代の固定機能まわり。
        ("lit", "float4"),
        ("D3DCOLORtoUBYTE4", "int4"),
        ("noise", "float"),
        ("msad4", "uint4"),

        // 描画先の標本。
        ("GetRenderTargetSampleCount", "uint"),
        ("GetRenderTargetSamplePosition", "float2"),
        ("CheckAccessFullyMapped", "bool"),

        // 波とレーン。
        ("WaveGetLaneCount", "uint"), ("WaveGetLaneIndex", "uint"),
        ("WaveIsFirstLane", "bool"), ("IsHelperLane", "bool"),
        ("WaveActiveAllTrue", "bool"), ("WaveActiveAnyTrue", "bool"),
        ("WaveActiveAllEqual", "bool"),
        ("WaveActiveBallot", "uint4"),
        ("WaveActiveCountBits", "uint"), ("WavePrefixCountBits", "uint"),

        // 詰めた 8 ビット整数の積和 (シェーダーモデル 6.4)。
        ("dot2add", "float"),
        ("dot4add_i8packed", "int"),
        ("dot4add_u8packed", "uint"),

        // 8 ビット整数の詰め替え (シェーダーモデル 6.6)。
        ("pack_s8", "int8_t4_packed"), ("pack_clamp_s8", "int8_t4_packed"),
        ("pack_u8", "uint8_t4_packed"), ("pack_clamp_u8", "uint8_t4_packed"),
        ("unpack_s8s16", "int16_t4"), ("unpack_s8s32", "int32_t4"),
        ("unpack_u8u16", "uint16_t4"), ("unpack_u8u32", "uint32_t4"),

        // レイトレーシングのシステム値 (DXR、シェーダーモデル 6.3)。
        ("DispatchRaysIndex", "uint3"), ("DispatchRaysDimensions", "uint3"),
        ("WorldRayOrigin", "float3"), ("WorldRayDirection", "float3"),
        ("ObjectRayOrigin", "float3"), ("ObjectRayDirection", "float3"),
        ("RayTMin", "float"), ("RayTCurrent", "float"), ("RayFlags", "uint"),
        ("InstanceIndex", "uint"), ("InstanceID", "uint"), ("GeometryIndex", "uint"),
        ("PrimitiveIndex", "uint"), ("HitKind", "uint"),
        ("ObjectToWorld3x4", "float3x4"), ("ObjectToWorld4x3", "float4x3"),
        ("WorldToObject3x4", "float3x4"), ("WorldToObject4x3", "float4x3"),
        ("ObjectToWorld", "float3x4"), ("WorldToObject", "float3x4"),
        ("ReportHit", "bool"),
    ];

    /// <summary>常に <c>bool</c> を返す関数。</summary>
    /// <remarks>成分数によらず 1 つの真偽値へまとめる。</remarks>
    private static readonly string[] BoolNames = ["all", "any"];

    /// <summary>行列を扱う関数。</summary>
    /// <remarks>
    /// 成分ごとの演算ではないため、他とは別の規則で結果の形が決まる。
    /// <c>mul</c> は行列の積であり、<c>*</c> (成分ごとの積) とは結果が違う。
    /// </remarks>
    private static readonly (string Name, IntrinsicResultRule Shape)[] MatrixNames =
    [
        ("mul", IntrinsicResultRule.MatrixProduct),
        ("transpose", IntrinsicResultRule.Transpose),
        ("determinant", IntrinsicResultRule.Determinant),
    ];

    private static readonly FrozenDictionary<string, IntrinsicResultRule> Shapes = BuildShapes();

    private static readonly FrozenDictionary<string, string> FixedBaseTypes =
        FixedBaseTypeNames.ToFrozenDictionary(
            entry => entry.Name, entry => entry.BaseType, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> FixedTypes =
        FixedTypeNames.ToFrozenDictionary(
            entry => entry.Name, entry => entry.Type, StringComparer.Ordinal);

    /// <summary>
    /// テクスチャのメソッドの名前から、返すものへの対応。
    /// </summary>
    /// <remarks>
    /// <c>GetDimensions</c> は出力引数へ書き込むだけで値を返さないため載せない。
    /// </remarks>
    private static readonly FrozenDictionary<string, TextureMethodReturnType> TextureMethods =
        new Dictionary<string, TextureMethodReturnType>(StringComparer.Ordinal)
        {
            ["Sample"] = TextureMethodReturnType.Element,
            ["SampleLevel"] = TextureMethodReturnType.Element,
            ["SampleBias"] = TextureMethodReturnType.Element,
            ["SampleGrad"] = TextureMethodReturnType.Element,
            ["Load"] = TextureMethodReturnType.Element,
            ["Gather"] = TextureMethodReturnType.Element,
            ["GatherRed"] = TextureMethodReturnType.Element,
            ["GatherGreen"] = TextureMethodReturnType.Element,
            ["GatherBlue"] = TextureMethodReturnType.Element,
            ["GatherAlpha"] = TextureMethodReturnType.Element,

            // 比較の結果はスカラーになる。
            ["SampleCmp"] = TextureMethodReturnType.Scalar,
            ["SampleCmpLevelZero"] = TextureMethodReturnType.Scalar,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// HLSL の組み込み関数の名前。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>これは「名前を知っているか」だけの表である。</b>
    /// 引数や戻り値の型は分からなくてよい。
    /// 「宣言されていない」と言ってよいかを決めるためだけに使う (HL0310)。
    /// </para>
    /// <para>
    /// 元は Microsoft の
    /// <see href="https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-intrinsic-functions">
    /// Intrinsic Functions</see> の一覧である。
    /// あの表はシェーダーモデル 5 までしか載っていないので、
    /// 6 以降で入った波動関数と量子化の関数を足してある。
    /// </para>
    /// <para>
    /// <b>足りなければ報告しないほうへ倒す。</b>
    /// 表に無い名前を「宣言されていない」と言うと、
    /// 正しく書かれたコードを誤りとして指摘することになる。
    /// 実測で見つかったものは足していく。
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> IntrinsicNames = new[]
    {
        // シェーダーモデル 5 まで (Microsoft の一覧より)
        "abort", "abs", "acos", "all", "AllMemoryBarrier", "AllMemoryBarrierWithGroupSync",
        "any", "asdouble", "asfloat", "asin", "asint", "asuint", "atan", "atan2",
        "ceil", "CheckAccessFullyMapped", "clamp", "clip", "cos", "cosh", "countbits", "cross",
        "D3DCOLORtoUBYTE4", "ddx", "ddx_coarse", "ddx_fine", "ddy", "ddy_coarse", "ddy_fine",
        "degrees", "determinant", "DeviceMemoryBarrier", "DeviceMemoryBarrierWithGroupSync",
        "distance", "dot", "dst", "errorf",
        "EvaluateAttributeCentroid", "EvaluateAttributeAtSample", "EvaluateAttributeSnapped",
        "exp", "exp2", "f16tof32", "f32tof16", "faceforward", "firstbithigh", "firstbitlow",
        "floor", "fma", "fmod", "frac", "frexp", "fwidth",
        "GetRenderTargetSampleCount", "GetRenderTargetSamplePosition",
        "GroupMemoryBarrier", "GroupMemoryBarrierWithGroupSync",
        "InterlockedAdd", "InterlockedAnd", "InterlockedCompareExchange", "InterlockedCompareStore",
        "InterlockedExchange", "InterlockedMax", "InterlockedMin", "InterlockedOr", "InterlockedXor",
        "isfinite", "isinf", "isnan", "ldexp", "length", "lerp", "lit", "log", "log10", "log2",
        "mad", "max", "min", "modf", "msad4", "mul", "noise", "normalize", "pow", "printf",
        "Process2DQuadTessFactorsAvg", "Process2DQuadTessFactorsMax", "Process2DQuadTessFactorsMin",
        "ProcessIsolineTessFactors",
        "ProcessQuadTessFactorsAvg", "ProcessQuadTessFactorsMax", "ProcessQuadTessFactorsMin",
        "ProcessTriTessFactorsAvg", "ProcessTriTessFactorsMax", "ProcessTriTessFactorsMin",
        "radians", "rcp", "reflect", "refract", "reversebits", "round", "rsqrt",
        "saturate", "sign", "sin", "sincos", "sinh", "smoothstep", "sqrt", "step",
        "tan", "tanh", "transpose", "trunc",

        // DirectX 9 時代のテクスチャ関数。Unity のシェーダーにはまだ残っている
        "tex1D", "tex1Dbias", "tex1Dgrad", "tex1Dlod", "tex1Dproj",
        "tex2D", "tex2Dbias", "tex2Dgrad", "tex2Dlod", "tex2Dproj",
        "tex3D", "tex3Dbias", "tex3Dgrad", "tex3Dlod", "tex3Dproj",
        "texCUBE", "texCUBEbias", "texCUBEgrad", "texCUBElod", "texCUBEproj",

        // シェーダーモデル 6 以降。上の一覧には載っていない
        "WaveIsFirstLane", "WaveGetLaneCount", "WaveGetLaneIndex",
        "WaveActiveAnyTrue", "WaveActiveAllTrue", "WaveActiveBallot",
        "WaveReadLaneAt", "WaveReadLaneFirst",
        "WaveActiveAllEqual", "WaveActiveBitAnd", "WaveActiveBitOr", "WaveActiveBitXor",
        "WaveActiveCountBits", "WaveActiveMax", "WaveActiveMin", "WaveActiveProduct",
        "WaveActiveSum", "WavePrefixCountBits", "WavePrefixProduct", "WavePrefixSum",
        "QuadReadAcrossDiagonal", "QuadReadAcrossX", "QuadReadAcrossY", "QuadReadLaneAt",
        "NonUniformResourceIndex", "IsHelperLane",
        "dot4add_u8packed", "dot4add_i8packed", "dot2add",
        "pack_s8", "pack_u8", "pack_clamp_s8", "pack_clamp_u8",
        "unpack_s8s16", "unpack_u8u16", "unpack_s8s32", "unpack_u8u32",

        // レイトレーシング (DXR、シェーダーモデル 6.3)。上の一覧には載っていない
        "TraceRay", "ReportHit", "CallShader", "IgnoreHit", "AcceptHitAndEndSearch",
        "DispatchRaysIndex", "DispatchRaysDimensions",
        "WorldRayOrigin", "WorldRayDirection", "ObjectRayOrigin", "ObjectRayDirection",
        "RayTMin", "RayTCurrent", "RayFlags",
        "InstanceIndex", "InstanceID", "GeometryIndex", "PrimitiveIndex", "HitKind",
        "ObjectToWorld3x4", "ObjectToWorld4x3", "WorldToObject3x4", "WorldToObject4x3",
        "ObjectToWorld", "WorldToObject",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// コンパイラがあらかじめ定義している定数の名前。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DXR の <c>HIT_KIND_*</c> と <c>RAY_FLAG_*</c> は、宣言もマクロも無しに使える。
    /// 関数ではないので <see cref="IntrinsicNames"/> とは分けて持つ (補完で関数として出さない)。
    /// </para>
    /// <para>
    /// 名前は DirectX Raytracing (DXR) Functional Spec の定義による。
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> ConstantNames = new[]
    {
        "HIT_KIND_TRIANGLE_FRONT_FACE", "HIT_KIND_TRIANGLE_BACK_FACE",
        "RAY_FLAG_NONE", "RAY_FLAG_FORCE_OPAQUE", "RAY_FLAG_FORCE_NON_OPAQUE",
        "RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH", "RAY_FLAG_SKIP_CLOSEST_HIT_SHADER",
        "RAY_FLAG_CULL_BACK_FACING_TRIANGLES", "RAY_FLAG_CULL_FRONT_FACING_TRIANGLES",
        "RAY_FLAG_CULL_OPAQUE", "RAY_FLAG_CULL_NON_OPAQUE",
        "RAY_FLAG_SKIP_TRIANGLES", "RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// その名前が、コンパイラがあらかじめ定義している定数かどうかを判定する。
    /// </summary>
    /// <param name="name">判定する名前。</param>
    /// <returns>定義済みの定数であれば <see langword="true"/>。</returns>
    public static bool IsKnownConstant(string? name)
        => name is not null && ConstantNames.Contains(name);

    /// <summary>
    /// その名前が組み込み関数かどうかを判定する。
    /// </summary>
    /// <param name="name">判定する名前。</param>
    /// <returns>組み込み関数であれば <see langword="true"/>。</returns>
    public static bool IsKnownFunction(string? name)
        => name is not null && IntrinsicNames.Contains(name);

    /// <summary>組み込み関数の名前を列挙する。</summary>
    /// <remarks>補完の候補に出すために使う。</remarks>
    public static IReadOnlyCollection<string> FunctionNames => IntrinsicNames;

    /// <summary>
    /// テクスチャのメソッドが何を返すかを求める。
    /// </summary>
    /// <param name="name">メソッドの名前。</param>
    /// <returns>返すもの。表に無い場合は <see cref="TextureMethodReturnType.Unknown"/>。</returns>
    public static TextureMethodReturnType GetTextureMethodReturnType(string? name)
        => name is not null && TextureMethods.TryGetValue(name, out TextureMethodReturnType result)
            ? result
            : TextureMethodReturnType.Unknown;

    /// <summary>
    /// テクスチャを読む関数またはメソッドかどうかを判定する。
    /// </summary>
    /// <param name="name">関数またはメソッドの名前。</param>
    /// <returns>テクスチャを読むものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>読み方は 2 通りある。</b>
    /// <c>tex2D(...)</c> のような DirectX 9 時代の関数と、
    /// <c>tex.Sample(...)</c> のようなメソッドの形である。
    /// <c>SAMPLE_TEXTURE2D</c> のようなマクロは展開されて後者になる。
    /// </para>
    /// <para>
    /// <b>サンプリングの回数を数えるルールが、この一覧を書き写さずに済むようにしている。</b>
    /// 書き写した一覧は、新しい読み方が増えたときに古いままになる。
    /// </para>
    /// </remarks>
    public static bool IsTextureSamplingFunction(string? name)
        => name is not null
           && (TextureMethods.ContainsKey(name)
               || (name.StartsWith("tex", StringComparison.Ordinal) && IntrinsicNames.Contains(name)));

    /// <summary>
    /// 組み込み関数の結果の型の決め方を返す。
    /// </summary>
    /// <param name="name">関数の名前。</param>
    /// <returns>決め方。表に無い場合は <see cref="IntrinsicResultRule.Unknown"/>。</returns>
    public static IntrinsicResultRule GetResultShape(string? name)
        => name is not null && Shapes.TryGetValue(name, out IntrinsicResultRule shape)
            ? shape
            : IntrinsicResultRule.Unknown;

    /// <summary>
    /// 引数と同じ形で返すときの、基底型を返す。
    /// </summary>
    /// <param name="name">関数の名前。</param>
    /// <returns>基底型。表に無い場合は <see langword="null"/>。</returns>
    public static string? GetFixedBaseType(string? name)
        => name is not null && FixedBaseTypes.TryGetValue(name, out string? baseType) ? baseType : null;

    /// <summary>
    /// 引数によらず決まっている結果の型を返す。
    /// </summary>
    /// <param name="name">関数の名前。</param>
    /// <returns>型。表に無い場合は <see langword="null"/>。</returns>
    public static string? GetFixedType(string? name)
        => name is not null && FixedTypes.TryGetValue(name, out string? type) ? type : null;

    /// <summary>表を組み立てる。</summary>
    /// <returns>関数名から決め方への対応。</returns>
    private static FrozenDictionary<string, IntrinsicResultRule> BuildShapes()
    {
        Dictionary<string, IntrinsicResultRule> shapes = new(StringComparer.Ordinal);

        foreach (string name in SameAsArgumentsNames)
        {
            shapes[name] = IntrinsicResultRule.SameAsArguments;
        }

        foreach (string name in ScalarOfArgumentsNames)
        {
            shapes[name] = IntrinsicResultRule.ScalarOfArguments;
        }

        foreach (string name in BoolNames)
        {
            shapes[name] = IntrinsicResultRule.Bool;
        }

        foreach (string name in BoolPerComponentNames)
        {
            shapes[name] = IntrinsicResultRule.BoolPerComponent;
        }

        foreach ((string name, string _) in FixedBaseTypeNames)
        {
            shapes[name] = IntrinsicResultRule.FixedBaseType;
        }

        foreach (string name in VoidNames)
        {
            shapes[name] = IntrinsicResultRule.Void;
        }

        foreach (string name in SameAsFirstArgumentNames)
        {
            shapes[name] = IntrinsicResultRule.SameAsFirstArgument;
        }

        foreach ((string name, string _) in FixedTypeNames)
        {
            shapes[name] = IntrinsicResultRule.FixedType;
        }

        foreach ((string name, IntrinsicResultRule shape) in MatrixNames)
        {
            shapes[name] = shape;
        }

        return shapes.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
