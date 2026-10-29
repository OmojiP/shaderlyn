using Shaderlyn.Glsl;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// GLSL の要素の型の暗黙の変換。glslang 16.6.0 の <c>TIntermediate::canImplicitlyPromote</c> (Intermediate.cpp) を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 変換できるかは、言語のバージョン・ES か・有効にした拡張 (<see cref="GlslNumericFeatures"/>) で決まる。
/// ES の 3.10 より前と、デスクトップの 1.10 には暗黙の変換が無い。
/// </para>
/// <para>
/// 型は要素の型の名前 (<c>int</c>、<c>float16_t</c> など) で表す。<c>int32_t</c> などの別名は、呼ぶ側で揃えておく。
/// </para>
/// </remarks>
internal static class GlslConversions
{
    /// <summary>要素の型を、別の要素の型へ暗黙に変換できるかを判定する。</summary>
    /// <param name="from">値の要素の型。</param>
    /// <param name="to">変換先の要素の型。</param>
    /// <param name="version">言語のバージョン。</param>
    /// <param name="es">ES か。</param>
    /// <param name="features">有効にした拡張で決まる数値の機能。</param>
    /// <returns>変換できれば <see langword="true"/>。</returns>
    public static bool CanImplicitlyPromote(string from, string to, int version, bool es, GlslNumericFeatures features)
    {
        // glslang は同じ型どうしでは変換を問わない。ES の 3.10 より前でも同じ型は通る。
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return true;
        }

        if ((es && version < 310) || version == 110)
        {
            return false;
        }

        if ((IsIntegralPromotion(from, to) || IsFPPromotion(from, to) || IsIntegralConversion(from, to, version)
             || IsFPConversion(from, to) || IsFPIntegralConversion(from, to))
            && features.HasFlag(GlslNumericFeatures.ExplicitArithmeticTypes))
        {
            return true;
        }

        bool has(GlslNumericFeatures feature) => features.HasFlag(feature);
        bool nv5 = has(GlslNumericFeatures.NvGpuShader5Types);
        bool int16 = has(GlslNumericFeatures.GpuShaderInt16);

        if (es)
        {
            return to switch
            {
                "float" => from is "int" or "uint" && has(GlslNumericFeatures.ImplicitConversions),
                "uint" => from == "int" && has(GlslNumericFeatures.ImplicitConversions),
                _ => false,
            };
        }

        bool fp64 = version >= 400 || has(GlslNumericFeatures.GpuShaderFp64);

        return to switch
        {
            "double" => from switch
            {
                "int" or "uint" or "int64_t" or "uint64_t" or "float" => fp64,
                "int16_t" or "uint16_t" => fp64 && (nv5 || int16),
                "float16_t" => fp64 && (nv5 || has(GlslNumericFeatures.GpuShaderHalfFloat)),
                _ when IsSmallFloat(from) => true,
                "int8_t" or "uint8_t" => nv5,
                _ => false,
            },
            "float" => from switch
            {
                "int" or "uint" => true,
                "int16_t" or "uint16_t" => int16 || nv5,
                "float16_t" => has(GlslNumericFeatures.GpuShaderHalfFloat) || nv5,
                _ when IsSmallFloat(from) => true,
                "int8_t" or "uint8_t" => nv5,
                _ => false,
            },
            "uint" => from switch
            {
                "int" => version >= 400 || has(GlslNumericFeatures.ArbGpuShader5) || nv5,
                "int16_t" or "uint16_t" => int16 || nv5,
                "int8_t" or "uint8_t" => nv5,
                _ => false,
            },
            "int" => from switch
            {
                "int16_t" => int16 || nv5,
                "int8_t" => nv5,
                _ => false,
            },
            "uint64_t" => from switch
            {
                "int" or "uint" or "int64_t" => true,
                "int16_t" or "uint16_t" => int16 || nv5,
                "int8_t" or "uint8_t" => nv5,
                _ => false,
            },
            "int64_t" => from switch
            {
                "int" => true,
                "int8_t" => nv5,
                "int16_t" => int16 || nv5,
                _ => false,
            },
            "float16_t" => from switch
            {
                "int16_t" or "uint16_t" => int16,
                _ => IsEightBitFloat(from),
            },
            "bfloat16_t" => IsEightBitFloat(from),
            "floate4m3_t" or "floate5m2_t" => from == "floate2m1_t",
            "uint16_t" => from == "int16_t" && int16,
            _ => false,
        };
    }

    /// <summary>bfloat16 と 8 ビットの浮動小数点数か (float と double へ変換できる)。</summary>
    private static bool IsSmallFloat(string type) => type == "bfloat16_t" || IsEightBitFloat(type);

    /// <summary>8 ビットの浮動小数点数か。</summary>
    private static bool IsEightBitFloat(string type)
        => type is "floate5m2_t" or "floate4m3_t" or "floate2m1_t" or "floate3m2_t" or "floate2m3_t" or "floatmxint8_t";

    /// <summary>
    /// 二項の演算子で、両辺の要素の型をそろえる先を求める (glslang の <c>getConversionDestinationType</c>)。
    /// </summary>
    /// <param name="left">左の要素の型。</param>
    /// <param name="right">右の要素の型。</param>
    /// <param name="version">言語のバージョン。</param>
    /// <param name="es">ES か。</param>
    /// <param name="features">有効にした拡張で決まる数値の機能。</param>
    /// <returns>そろえる先の要素の型。そろえられなければ <see langword="null"/>。</returns>
    public static string? GetConversionDestination(string left, string right, int version, bool es, GlslNumericFeatures features)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return left;
        }

        if ((es && (version < 310 || !features.HasFlag(GlslNumericFeatures.ImplicitConversions))) || version == 110)
        {
            return null;
        }

        bool promote(string from, string to) => CanImplicitlyPromote(from, to, version, es, features);

        foreach (string floating in (string[])["double", "float", "float16_t", "bfloat16_t"])
        {
            if ((left == floating && promote(right, floating)) || (right == floating && promote(left, floating)))
            {
                return floating;
            }
        }

        if (IntegerRank(left) is not { } leftRank || IntegerRank(right) is not { } rightRank || !(promote(left, right) || promote(right, left)))
        {
            return null;
        }

        bool leftUnsigned = left.StartsWith('u');
        bool rightUnsigned = right.StartsWith('u');

        if (leftUnsigned == rightUnsigned)
        {
            return leftRank < rightRank ? right : left;
        }

        if (leftUnsigned && leftRank > rightRank)
        {
            return left;
        }

        if (rightUnsigned && rightRank > leftRank)
        {
            return right;
        }

        // 符号付きの型が、符号無しの型の値をすべて表せるなら符号付きの型、表せなければそれに当たる符号無しの型。
        (string signed, int signedRank, int unsignedRank) = leftUnsigned ? (right, rightRank, leftRank) : (left, leftRank, rightRank);
        return signedRank > unsignedRank ? signed : "u" + signed;
    }

    /// <summary>整数の型の大きさの順位 (glslang の <c>getTypeRank</c>)。整数でなければ <see langword="null"/>。</summary>
    /// <param name="type">要素の型。</param>
    /// <returns>8 ビットが 0、16 ビットが 1、32 ビットが 2、64 ビットが 3。</returns>
    public static int? IntegerRank(string type) => type switch
    {
        "int8_t" or "uint8_t" => 0,
        "int16_t" or "uint16_t" => 1,
        "int" or "uint" => 2,
        "int64_t" or "uint64_t" => 3,
        _ => null,
    };


    private static bool IsIntegralPromotion(string from, string to)
        => to == "int" && from is "int8_t" or "int16_t" or "uint8_t" or "uint16_t";

    private static bool IsFPPromotion(string from, string to)
        => to == "double" && (from is "float16_t" or "float" || IsSmallFloat(from));

    private static bool IsIntegralConversion(string from, string to, int version) => from switch
    {
        "int" => to switch
        {
            "uint" => version >= 400,
            "int64_t" or "uint64_t" => true,
            _ => false,
        },
        "uint" => to is "int64_t" or "uint64_t",
        "int8_t" => to is "uint8_t" or "int16_t" or "uint16_t" or "uint" or "int64_t" or "uint64_t",
        "uint8_t" => to is "int16_t" or "uint16_t" or "uint" or "int64_t" or "uint64_t",
        "int16_t" => to is "uint16_t" or "uint" or "int64_t" or "uint64_t",
        "uint16_t" => to is "uint" or "int64_t" or "uint64_t",
        "int64_t" => to == "uint64_t",
        _ => false,
    };

    private static bool IsFPConversion(string from, string to)
        => to == "float" && (from == "float16_t" || IsSmallFloat(from));

    private static bool IsFPIntegralConversion(string from, string to) => from switch
    {
        "int" or "uint" => to is "float" or "double",
        "int8_t" or "uint8_t" or "int16_t" or "uint16_t" => to is "float16_t" or "float" or "double",
        "int64_t" or "uint64_t" => to == "double",
        _ => false,
    };
}
