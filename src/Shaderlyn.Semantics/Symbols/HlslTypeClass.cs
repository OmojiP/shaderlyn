using System.Collections.Frozen;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// HLSL の型を、プロパティとの対応を判定できる粒度まで丸めた分類。
/// </summary>
/// <remarks>
/// <para>
/// <b>意図的に粗い。</b>
/// <c>float4</c> と <c>half3</c> を区別しても、プロパティとの対応の観点では意味が無い。
/// <c>Color</c> のプロパティを <c>half3</c> で受けるのは Unity のシェーダーでも普通に行われており、
/// 成分数の違いまで報告すると誤検出になる。
/// </para>
/// <para>
/// 逆にスカラーとベクトル、ベクトルとテクスチャの取り違えは、
/// 「マテリアルで設定した値が届かない」という形で必ず不具合になる。
/// 報告する価値があるのはこの粒度の食い違いだけである。
/// </para>
/// </remarks>
public enum HlslTypeClass
{
    /// <summary>
    /// このツールが判別できない型。ユーザー定義の構造体や、未解決のマクロなど。
    /// </summary>
    /// <remarks>
    /// <b>この分類に対して型の不一致を報告してはならない。</b>
    /// 判別できないことは「合っていない」ことの証拠にならない。
    /// </remarks>
    Unknown,

    /// <summary><c>float</c> / <c>half</c> / <c>int</c> など 1 成分の数値型。</summary>
    Scalar,

    /// <summary><c>float4</c> / <c>half3</c> など 2〜4 成分のベクトル型。</summary>
    Vector,

    /// <summary><c>float4x4</c> などの行列型。</summary>
    Matrix,

    /// <summary>2 次元テクスチャ。<c>Texture2D</c> と Cg の <c>sampler2D</c> の両方を含む。</summary>
    Texture2D,

    /// <summary>3 次元テクスチャ。</summary>
    Texture3D,

    /// <summary>キューブマップ。</summary>
    TextureCube,

    /// <summary>2 次元テクスチャ配列。</summary>
    Texture2DArray,

    /// <summary>キューブマップ配列。</summary>
    TextureCubeArray,

    /// <summary><c>SamplerState</c> などのサンプラ。</summary>
    Sampler,

    /// <summary><c>StructuredBuffer</c> などのバッファ。</summary>
    Buffer,
}

/// <summary>
/// 数値型の形。
/// </summary>
public enum HlslNumericKind
{
    /// <summary>数値型ではない。</summary>
    None,

    /// <summary>1 成分。</summary>
    Scalar,

    /// <summary>2 から 4 成分のベクトル。</summary>
    Vector,

    /// <summary>行列。</summary>
    Matrix,
}

/// <summary>
/// 数値型を、基底名と行数・列数に分けて表したもの。
/// </summary>
/// <param name="Kind">形。</param>
/// <param name="BaseName">基底名 (<c>float4x4</c> なら <c>float</c>)。</param>
/// <param name="Rows">行数。スカラーとベクトルは 1。</param>
/// <param name="Columns">列数。スカラーは 1、ベクトルは成分数。</param>
/// <remarks>
/// ベクトルを 1 行として表すことで、<c>mul</c> の規則を
/// スカラー・ベクトル・行列で場合分けせずに書ける。
/// </remarks>
public readonly record struct HlslNumericShape(HlslNumericKind Kind, string BaseName, int Rows, int Columns);

/// <summary>
/// HLSL の型名を <see cref="HlslTypeClass"/> へ分類する。
/// </summary>
/// <remarks>
/// <para>
/// 分類は型名の文字列だけで行う。宣言の解決や typedef の追跡は行わない。
/// 知らない名前はすべて <see cref="HlslTypeClass.Unknown"/> に落ち、
/// その場合ルールは何も報告しない。
/// <b>知識が足りないときに報告しないのが正しい振る舞いである。</b>
/// </para>
/// </remarks>
public static class HlslTypeClassifier
{
    /// <summary>
    /// スカラー型の基底名。
    /// </summary>
    /// <remarks>
    /// <c>real</c> は Unity の SRP が定義するマクロで、プラットフォームによって
    /// <c>half</c> か <c>float</c> のどちらかになる。実物のヘッダが取り込まれていれば
    /// 展開されて消えるが、取り込めなかった場合は型名として残るため、ここでも認識する。
    /// </remarks>
    private static readonly FrozenSet<string> ScalarBaseNames = new[]
    {
        "bool", "int", "uint", "dword", "half", "float", "double", "fixed", "real",
        "min16float", "min10float", "min16int", "min12int", "min16uint",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>テクスチャ・サンプラ・バッファの型名から分類への対応表。</summary>
    private static readonly FrozenDictionary<string, HlslTypeClass> ObjectTypeClasses =
        new Dictionary<string, HlslTypeClass>(StringComparer.Ordinal)
        {
            ["Texture2D"] = HlslTypeClass.Texture2D,
            ["Texture2DMS"] = HlslTypeClass.Texture2D,
            ["RWTexture2D"] = HlslTypeClass.Texture2D,
            ["Texture2DArray"] = HlslTypeClass.Texture2DArray,
            ["Texture2DMSArray"] = HlslTypeClass.Texture2DArray,
            ["RWTexture2DArray"] = HlslTypeClass.Texture2DArray,
            ["Texture3D"] = HlslTypeClass.Texture3D,
            ["RWTexture3D"] = HlslTypeClass.Texture3D,
            ["TextureCube"] = HlslTypeClass.TextureCube,
            ["TextureCubeArray"] = HlslTypeClass.TextureCubeArray,

            // Cg 時代の記法。テクスチャとサンプラが 1 つの型に融合しているが、
            // プロパティとの対応の観点ではテクスチャとして扱えばよい。
            ["sampler2D"] = HlslTypeClass.Texture2D,
            ["sampler2D_float"] = HlslTypeClass.Texture2D,
            ["sampler2D_half"] = HlslTypeClass.Texture2D,
            ["sampler2D_shadow"] = HlslTypeClass.Texture2D,
            ["sampler3D"] = HlslTypeClass.Texture3D,
            ["samplerCUBE"] = HlslTypeClass.TextureCube,
            ["samplerCUBE_half"] = HlslTypeClass.TextureCube,
            ["samplerCUBE_shadow"] = HlslTypeClass.TextureCube,

            ["SamplerState"] = HlslTypeClass.Sampler,
            ["SamplerComparisonState"] = HlslTypeClass.Sampler,
            ["sampler"] = HlslTypeClass.Sampler,

            ["Buffer"] = HlslTypeClass.Buffer,
            ["RWBuffer"] = HlslTypeClass.Buffer,
            ["ByteAddressBuffer"] = HlslTypeClass.Buffer,
            ["RWByteAddressBuffer"] = HlslTypeClass.Buffer,
            ["StructuredBuffer"] = HlslTypeClass.Buffer,
            ["RWStructuredBuffer"] = HlslTypeClass.Buffer,
            ["ConstantBuffer"] = HlslTypeClass.Buffer,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 型名を分類する。
    /// </summary>
    /// <param name="typeName">HLSL の型名。名前空間の修飾は含まない。</param>
    /// <returns>対応する分類。判別できない場合は <see cref="HlslTypeClass.Unknown"/>。</returns>
    public static HlslTypeClass Classify(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return HlslTypeClass.Unknown;
        }

        if (ObjectTypeClasses.TryGetValue(typeName, out HlslTypeClass objectClass))
        {
            return objectClass;
        }

        return ClassifyNumeric(typeName);
    }

    /// <summary>
    /// 分類が値型 (テクスチャやサンプラではない) かどうかを判定する。
    /// </summary>
    /// <param name="typeClass">判定する分類。</param>
    /// <returns>スカラー・ベクトル・行列のいずれかであれば <see langword="true"/>。</returns>
    /// <remarks>定数バッファへ入れられるかどうかの判定に使う。</remarks>
    public static bool IsNumeric(this HlslTypeClass typeClass) => typeClass is
        HlslTypeClass.Scalar or HlslTypeClass.Vector or HlslTypeClass.Matrix;

    /// <summary>
    /// 分類がテクスチャまたはサンプラかどうかを判定する。
    /// </summary>
    /// <param name="typeClass">判定する分類。</param>
    /// <returns>テクスチャかサンプラであれば <see langword="true"/>。</returns>
    public static bool IsTextureOrSampler(this HlslTypeClass typeClass)
        => typeClass.IsTexture() || typeClass == HlslTypeClass.Sampler;

    /// <summary>
    /// 分類がテクスチャかどうかを判定する。
    /// </summary>
    /// <param name="typeClass">判定する分類。</param>
    /// <returns>テクスチャであれば <see langword="true"/>。</returns>
    /// <remarks>サンプラは含まない。サンプリングのメソッドを持つのはテクスチャの側である。</remarks>
    public static bool IsTexture(this HlslTypeClass typeClass) => typeClass is
        HlslTypeClass.Texture2D
        or HlslTypeClass.Texture3D
        or HlslTypeClass.TextureCube
        or HlslTypeClass.Texture2DArray
        or HlslTypeClass.TextureCubeArray;

    /// <summary>
    /// 基底型の変換の順位。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 型の違う値を演算すると、順位の高いほうに揃えられる。
    /// <c>half * float</c> は <c>float</c>、<c>int * float</c> も <c>float</c> である。
    /// </para>
    /// <para>
    /// <b>順位の決まらない型はここに載せない。</b>
    /// <c>real</c> は Unity の SRP がプラットフォームごとに <c>half</c> か <c>float</c> へ振り分けるマクロ、
    /// <c>fixed</c> は現代の HLSL に対応物の無い Cg 時代の型、
    /// <c>min16float</c> 系は 16 bit 型を有効にしているかどうかで実際の扱いが変わる。
    /// これらが絡む演算については、結果の型を主張しない。
    /// </para>
    /// </remarks>
    private static readonly FrozenDictionary<string, int> ConversionRanks =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["bool"] = 1,
            ["int"] = 2,
            ["uint"] = 3,
            ["dword"] = 3,
            ["half"] = 4,
            ["float"] = 5,
            ["double"] = 6,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 2 つの基底型のうち、演算の結果が揃えられるほうを返す。
    /// </summary>
    /// <param name="left">一方の基底名。</param>
    /// <param name="right">もう一方の基底名。</param>
    /// <returns>結果の基底名。順位が決まらない型が含まれる場合は <see langword="null"/>。</returns>
    public static string? GetWiderBaseName(string left, string right)
    {
        // 同じ基底型どうしなら順位を知らなくても結果は決まる。
        // real や fixed の演算でも、相手が同じ型なら型は変わらない。
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return left;
        }

        return ConversionRanks.TryGetValue(left, out int leftRank)
               && ConversionRanks.TryGetValue(right, out int rightRank)
            ? (leftRank >= rightRank ? left : right)
            : null;
    }

    /// <summary>
    /// 数値型の名前を、基底名と成分数に分解する。
    /// </summary>
    /// <param name="typeName">HLSL の型名。</param>
    /// <param name="baseName">基底名 (<c>float4</c> なら <c>float</c>)。</param>
    /// <param name="components">成分数。スカラーは 1。</param>
    /// <returns>スカラーまたはベクトルとして分解できた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// スウィズルの結果のように、行列があってはならない場所で使う。
    /// 行列も含めて扱う場合は <see cref="TryDescribeNumeric"/> を使うこと。
    /// </remarks>
    public static bool TryDecomposeNumeric(string? typeName, out string baseName, out int components)
    {
        if (TryDescribeNumeric(typeName, out HlslNumericShape shape)
            && shape.Kind is HlslNumericKind.Scalar or HlslNumericKind.Vector)
        {
            baseName = shape.BaseName;
            components = shape.Columns;
            return true;
        }

        baseName = string.Empty;
        components = 0;
        return false;
    }

    /// <summary>
    /// 数値型の名前を、基底名と行数・列数に分解する。
    /// </summary>
    /// <param name="typeName">HLSL の型名。</param>
    /// <param name="shape">分解した形。</param>
    /// <returns>数値型として分解できた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// ベクトルは 1 行として表す。行と列を持たせておくと、
    /// <c>mul</c> の規則をスカラー・ベクトル・行列で分けずに書ける。
    /// </remarks>
    public static bool TryDescribeNumeric(string? typeName, out HlslNumericShape shape)
    {
        shape = default;

        HlslTypeClass typeClass = Classify(typeName);

        if (!typeClass.IsNumeric())
        {
            return false;
        }

        int baseLength = typeName!.Length;
        while (baseLength > 0 && (char.IsAsciiDigit(typeName[baseLength - 1]) || typeName[baseLength - 1] == 'x'))
        {
            baseLength--;
        }

        string baseName = typeName[..baseLength];
        string suffix = typeName[baseLength..];

        shape = suffix.Length switch
        {
            0 => new HlslNumericShape(HlslNumericKind.Scalar, baseName, 1, 1),
            1 => new HlslNumericShape(HlslNumericKind.Vector, baseName, 1, suffix[0] - '0'),
            _ => new HlslNumericShape(HlslNumericKind.Matrix, baseName, suffix[0] - '0', suffix[2] - '0'),
        };

        return true;
    }

    /// <summary>
    /// 形から数値型の名前を組み立てる。
    /// </summary>
    /// <param name="shape">組み立てる形。</param>
    /// <returns>型名。組み立てられない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 1 成分のベクトルはスカラーとして表す。
    /// 1 行 1 列の行列も同じで、<c>float1x1</c> と <c>float</c> は
    /// 演算の結果としては区別する意味が無い。
    /// </remarks>
    public static string? Compose(HlslNumericShape shape) => shape.Kind switch
    {
        HlslNumericKind.Scalar => shape.BaseName,
        HlslNumericKind.Vector => ComposeNumeric(shape.BaseName, shape.Columns),
        HlslNumericKind.Matrix when shape.Rows == 1 && shape.Columns == 1 => shape.BaseName,
        HlslNumericKind.Matrix when IsComponentCount(shape.Rows) && IsComponentCount(shape.Columns)
            => $"{shape.BaseName}{shape.Rows}x{shape.Columns}",
        _ => null,
    };

    /// <summary>成分数として妥当かどうかを判定する。</summary>
    /// <param name="count">調べる数。</param>
    /// <returns>1 から 4 の場合は <see langword="true"/>。</returns>
    private static bool IsComponentCount(int count) => count is >= 1 and <= 4;

    /// <summary>
    /// 基底名と成分数から数値型の名前を組み立てる。
    /// </summary>
    /// <param name="baseName">基底名。</param>
    /// <param name="components">成分数。1 から 4。</param>
    /// <returns>組み立てた型名。成分数が範囲外の場合は <see langword="null"/>。</returns>
    /// <remarks>1 成分はベクトルではなくスカラーとして表す。<c>float1</c> ではなく <c>float</c>。</remarks>
    public static string? ComposeNumeric(string baseName, int components) => components switch
    {
        1 => baseName,
        2 or 3 or 4 => baseName + (char)('0' + components),
        _ => null,
    };

    /// <summary>
    /// スカラー・ベクトル・行列を、型名の綴りから見分ける。
    /// </summary>
    /// <param name="typeName">HLSL の型名。</param>
    /// <returns>対応する分類。</returns>
    /// <remarks>
    /// <para>
    /// HLSL の数値型は「基底名 + 成分数」あるいは「基底名 + 行数 + <c>x</c> + 列数」という
    /// 規則的な綴りを持つ。<c>float</c> から <c>float4x4</c> まで 273 通りあるため、
    /// 表に列挙するのではなく綴りの規則で判定する。
    /// </para>
    /// <para>
    /// 綴りが規則に合わない名前 (ユーザー定義の構造体など) は分類できない扱いにする。
    /// </para>
    /// </remarks>
    private static HlslTypeClass ClassifyNumeric(string typeName)
    {
        // 末尾から数字と 'x' を取り除いて基底名を得る。
        int baseLength = typeName.Length;
        while (baseLength > 0 && (char.IsAsciiDigit(typeName[baseLength - 1]) || typeName[baseLength - 1] == 'x'))
        {
            baseLength--;
        }

        if (baseLength == 0 || !ScalarBaseNames.Contains(typeName[..baseLength]))
        {
            return HlslTypeClass.Unknown;
        }

        string suffix = typeName[baseLength..];

        if (suffix.Length == 0)
        {
            return HlslTypeClass.Scalar;
        }

        // float4x4 のように 'x' を含むものは行列。
        if (suffix.Contains('x', StringComparison.Ordinal))
        {
            return suffix.Length == 3 && char.IsAsciiDigit(suffix[0]) && suffix[1] == 'x' && char.IsAsciiDigit(suffix[2])
                ? HlslTypeClass.Matrix
                : HlslTypeClass.Unknown;
        }

        if (suffix.Length != 1)
        {
            return HlslTypeClass.Unknown;
        }

        // float1 は 1 成分なのでスカラーと同じ扱いにする。
        // マテリアル側から見れば float と区別がつかない。
        return suffix[0] switch
        {
            '1' => HlslTypeClass.Scalar,
            '2' or '3' or '4' => HlslTypeClass.Vector,
            _ => HlslTypeClass.Unknown,
        };
    }
}
