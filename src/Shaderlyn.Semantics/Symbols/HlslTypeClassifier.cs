using System.Collections.Frozen;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// HLSL の型名を <see cref="ShaderTypeClass"/> へ分類する。
/// </summary>
/// <remarks>
/// <para>
/// 分類は型名の文字列だけで行う。宣言の解決や typedef の追跡は行わない。
/// 知らない名前はすべて <see cref="ShaderTypeClass.Unknown"/> に落ち、
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
        "int16_t", "uint16_t", "float16_t", "int32_t", "uint32_t", "float32_t", "int64_t", "uint64_t", "float64_t",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>テクスチャ・サンプラ・バッファの型名から分類への対応表。</summary>
    private static readonly FrozenDictionary<string, ShaderTypeClass> ObjectTypeClasses =
        new Dictionary<string, ShaderTypeClass>(StringComparer.Ordinal)
        {
            ["Texture2D"] = ShaderTypeClass.Texture2D,
            ["Texture2DMS"] = ShaderTypeClass.Texture2D,
            ["RWTexture2D"] = ShaderTypeClass.Texture2D,
            ["Texture2DArray"] = ShaderTypeClass.Texture2DArray,
            ["Texture2DMSArray"] = ShaderTypeClass.Texture2DArray,
            ["RWTexture2DArray"] = ShaderTypeClass.Texture2DArray,
            ["Texture3D"] = ShaderTypeClass.Texture3D,
            ["RWTexture3D"] = ShaderTypeClass.Texture3D,
            ["TextureCube"] = ShaderTypeClass.TextureCube,
            ["TextureCubeArray"] = ShaderTypeClass.TextureCubeArray,

            // Cg 時代の記法。テクスチャとサンプラが 1 つの型に融合しているが、
            // プロパティとの対応の観点ではテクスチャとして扱えばよい。
            ["sampler2D"] = ShaderTypeClass.Texture2D,
            ["sampler2D_float"] = ShaderTypeClass.Texture2D,
            ["sampler2D_half"] = ShaderTypeClass.Texture2D,
            ["sampler2D_shadow"] = ShaderTypeClass.Texture2D,
            ["sampler3D"] = ShaderTypeClass.Texture3D,
            ["samplerCUBE"] = ShaderTypeClass.TextureCube,
            ["samplerCUBE_half"] = ShaderTypeClass.TextureCube,
            ["samplerCUBE_shadow"] = ShaderTypeClass.TextureCube,

            ["SamplerState"] = ShaderTypeClass.Sampler,
            ["SamplerComparisonState"] = ShaderTypeClass.Sampler,
            ["sampler"] = ShaderTypeClass.Sampler,

            ["Buffer"] = ShaderTypeClass.Buffer,
            ["RWBuffer"] = ShaderTypeClass.Buffer,
            ["ByteAddressBuffer"] = ShaderTypeClass.Buffer,
            ["RWByteAddressBuffer"] = ShaderTypeClass.Buffer,
            ["StructuredBuffer"] = ShaderTypeClass.Buffer,
            ["RWStructuredBuffer"] = ShaderTypeClass.Buffer,
            ["ConstantBuffer"] = ShaderTypeClass.Buffer,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 型名を分類する。
    /// </summary>
    /// <param name="typeName">HLSL の型名。名前空間の修飾は含まない。</param>
    /// <returns>対応する分類。判別できない場合は <see cref="ShaderTypeClass.Unknown"/>。</returns>
    public static ShaderTypeClass Classify(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return ShaderTypeClass.Unknown;
        }

        if (ObjectTypeClasses.TryGetValue(typeName, out ShaderTypeClass objectClass))
        {
            return objectClass;
        }

        return ClassifyNumeric(typeName);
    }

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
        if (TryDescribeNumeric(typeName, out ShaderNumericShape shape)
            && shape.Kind is ShaderNumericKind.Scalar or ShaderNumericKind.Vector)
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
    public static bool TryDescribeNumeric(string? typeName, out ShaderNumericShape shape)
    {
        shape = default;

        ShaderTypeClass typeClass = Classify(typeName);

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
            0 => new ShaderNumericShape(ShaderNumericKind.Scalar, baseName, 1, 1),
            1 => new ShaderNumericShape(ShaderNumericKind.Vector, baseName, 1, suffix[0] - '0'),
            _ => new ShaderNumericShape(ShaderNumericKind.Matrix, baseName, suffix[0] - '0', suffix[2] - '0'),
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
    public static string? Compose(ShaderNumericShape shape) => shape.Kind switch
    {
        ShaderNumericKind.Scalar => shape.BaseName,
        ShaderNumericKind.Vector => ComposeNumeric(shape.BaseName, shape.Columns),
        ShaderNumericKind.Matrix when shape.Rows == 1 && shape.Columns == 1 => shape.BaseName,
        ShaderNumericKind.Matrix when IsComponentCount(shape.Rows) && IsComponentCount(shape.Columns)
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
    private static ShaderTypeClass ClassifyNumeric(string typeName)
    {
        // 末尾から数字と 'x' を取り除いて基底名を得る。
        int baseLength = typeName.Length;
        while (baseLength > 0 && (char.IsAsciiDigit(typeName[baseLength - 1]) || typeName[baseLength - 1] == 'x'))
        {
            baseLength--;
        }

        if (baseLength == 0 || !ScalarBaseNames.Contains(typeName[..baseLength]))
        {
            return ShaderTypeClass.Unknown;
        }

        string suffix = typeName[baseLength..];

        if (suffix.Length == 0)
        {
            return ShaderTypeClass.Scalar;
        }

        // float4x4 のように 'x' を含むものは行列。
        if (suffix.Contains('x', StringComparison.Ordinal))
        {
            return suffix.Length == 3 && char.IsAsciiDigit(suffix[0]) && suffix[1] == 'x' && char.IsAsciiDigit(suffix[2])
                ? ShaderTypeClass.Matrix
                : ShaderTypeClass.Unknown;
        }

        if (suffix.Length != 1)
        {
            return ShaderTypeClass.Unknown;
        }

        // float1 は 1 成分なのでスカラーと同じ扱いにする。
        // マテリアル側から見れば float と区別がつかない。
        return suffix[0] switch
        {
            '1' => ShaderTypeClass.Scalar,
            '2' or '3' or '4' => ShaderTypeClass.Vector,
            _ => ShaderTypeClass.Unknown,
        };
    }
}
