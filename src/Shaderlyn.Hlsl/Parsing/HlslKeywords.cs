using System.Collections.Frozen;

namespace Shaderlyn.Hlsl.Parsing;

/// <summary>
/// HLSL / Cg のキーワードと組み込み型の一覧。
/// </summary>
/// <remarks>
/// <para>
/// 構文解析で「宣言か式か」を判断するために使う。
/// <c>float4 x;</c> と <c>foo(x);</c> はどちらも識別子で始まるため、
/// 先頭が型名かどうかを知らないと区別できない。
/// </para>
/// <para>
/// <b>ここに無い型名を誤りとして扱ってはならない。</b>
/// 利用者が <c>struct</c> や <c>typedef</c> で定義した型も宣言の先頭に来る。
/// この表は「確実に型である」ことを判定するためのものであり、
/// 型でないことの判定には使えない。
/// </para>
/// </remarks>
internal static class HlslKeywords
{
    /// <summary>スカラー型の基底名。</summary>
    private static readonly string[] ScalarBaseTypes =
    [
        "bool", "int", "uint", "dword", "half", "float", "double", "fixed",
        "min16float", "min10float", "min16int", "min12int", "min16uint",
    ];

    /// <summary>
    /// 組み込み型の名前。
    /// </summary>
    /// <remarks>
    /// スカラー型から、ベクトル型 (<c>float4</c>) と行列型 (<c>float4x4</c>) を機械的に生成する。
    /// 手で列挙すると 13 × 21 = 273 個になり、書き漏らしが必ず起きる。
    /// </remarks>
    public static FrozenSet<string> BuiltInTypes { get; } = BuildBuiltInTypes();

    /// <summary>
    /// テクスチャやバッファなど、テンプレート引数を取りうるオブジェクト型。
    /// </summary>
    public static FrozenSet<string> ObjectTypes { get; } = new[]
    {
        "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray",
        "Texture2DMS", "Texture2DMSArray", "Texture3D", "TextureCube", "TextureCubeArray",
        "RWTexture1D", "RWTexture1DArray", "RWTexture2D", "RWTexture2DArray", "RWTexture3D",
        "Buffer", "RWBuffer", "ByteAddressBuffer", "RWByteAddressBuffer",
        "StructuredBuffer", "RWStructuredBuffer", "AppendStructuredBuffer", "ConsumeStructuredBuffer",
        "ConstantBuffer", "SamplerState", "SamplerComparisonState",
        "sampler", "sampler1D", "sampler2D", "sampler3D", "samplerCUBE", "sampler2D_float",
        "sampler2D_half", "samplerCUBE_half", "sampler2D_shadow", "samplerCUBE_shadow",
        "PointStream", "LineStream", "TriangleStream",
        "InputPatch", "OutputPatch",
        "RaytracingAccelerationStructure", "RayDesc", "BuiltInTriangleIntersectionAttributes",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// 型名の直前に置ける修飾子。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>(const int) x</c> や <c>(unsigned int) x</c> のように、
    /// <b>型変換の括弧の中にも現れる</b>。
    /// HDRP の計算ユーティリティがこの書き方を使っている。
    /// </para>
    /// <para>
    /// <b><see cref="DeclarationModifiers"/> をそのまま使ってはならない。</b>
    /// あちらには <c>point</c> や <c>line</c> が入っている。
    /// これらは変数名としても普通に使われるので、
    /// <c>(point) - b</c> という減算を型変換と読み違える。
    /// 型の一部と言い切れるものだけをここに置く。
    /// </para>
    /// </remarks>
    public static FrozenSet<string> TypePrefixModifiers { get; } = new[]
    {
        "const", "unsigned", "signed", "volatile", "precise",
        "row_major", "column_major", "snorm", "unorm",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// 宣言に付けられる修飾子。
    /// </summary>
    public static FrozenSet<string> DeclarationModifiers { get; } = new[]
    {
        "static", "const", "uniform", "extern", "shared", "groupshared", "volatile",
        "precise", "inline", "row_major", "column_major", "globallycoherent",
        "nointerpolation", "linear", "centroid", "noperspective", "sample",
        "in", "out", "inout", "unsigned", "snorm", "unorm",

        // ジオメトリシェーダーの入力プリミティブ種別。
        // 引数の型の前に置かれるため、修飾子として扱わないと
        // triangle VertexToGeometry input[3] のような宣言が解析できない。
        "point", "line", "triangle", "lineadj", "triangleadj",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>指定した名前が確実に型かどうかを判定する。</summary>
    /// <param name="name">判定する名前。</param>
    /// <returns>組み込み型またはオブジェクト型の場合は <see langword="true"/>。</returns>
    public static bool IsKnownTypeName(string name)
        => BuiltInTypes.Contains(name) || ObjectTypes.Contains(name);

    /// <summary>スカラー・ベクトル・行列の組み込み型名を生成する。</summary>
    /// <returns>組み込み型名の集合。</returns>
    private static FrozenSet<string> BuildBuiltInTypes()
    {
        HashSet<string> types = new(StringComparer.Ordinal) { "void", "string" };

        foreach (string baseType in ScalarBaseTypes)
        {
            types.Add(baseType);

            for (int columns = 1; columns <= 4; columns++)
            {
                types.Add($"{baseType}{columns}");

                for (int rows = 1; rows <= 4; rows++)
                {
                    types.Add($"{baseType}{columns}x{rows}");
                }
            }
        }

        return types.ToFrozenSet(StringComparer.Ordinal);
    }
}
