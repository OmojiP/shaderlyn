using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>型のメモリの配置 (大きさと揃え)。</summary>
/// <param name="Size">大きさ (バイト)。大きさを決めない配列は、要素 1 つ分。</param>
/// <param name="Align">揃え (バイト)。</param>
internal readonly record struct WgslTypeLayout(long Size, long Align);

/// <summary>
/// WGSL の型のメモリの配置を、仕様の規則 (Alignment and Size) で求める。
/// </summary>
/// <remarks>
/// スカラーは 4 バイト (f16 / i16 / u16 は 2、64 ビットの型は 8)。vec2 は要素の 2 倍、vec3 と vec4 は 4 倍の揃えで、vec3 の大きさは 3 倍。
/// 行列は列のベクトルの配列。配列の要素の間隔は、要素の大きさを揃えに切り上げたもの。構造体は、メンバーを揃えに合わせて並べ、全体を最大の揃えに切り上げる
/// (<c>@align</c> と <c>@size</c> も入れる)。求められない型 (資源、大きさが定数でない配列) は <see langword="null"/> にする。
/// </remarks>
internal sealed class WgslLayout(WgslTypes types, WgslConstants constants)
{
    /// <summary>naga が受け付ける型の大きさの上限 (バイト)。</summary>
    public const long MaxSize = int.MaxValue;

    private readonly Dictionary<string, WgslTypeLayout?> _structs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _computing = new(StringComparer.Ordinal);

    /// <summary>型の配置を求める。</summary>
    public WgslTypeLayout? Of(WgslTypeRef type)
    {
        if (ScalarSize(type.Name) is { } scalar)
        {
            return new WgslTypeLayout(scalar, scalar);
        }

        // 短い名前 (vec3f・mat4x4h)。
        if (WgslTypes.ElementOfShortName(type.Name) is { } shortElement && ScalarSize(shortElement) is { } shortSize)
        {
            return type.Name.StartsWith("vec", StringComparison.Ordinal)
                ? Vector(type.Name[3] - '0', shortSize)
                : Matrix(type.Name[3] - '0', type.Name[5] - '0', shortSize);
        }

        switch (type.Name)
        {
            case "vec2" or "vec3" or "vec4" when type.Arguments is [{ } element] && ScalarSize(element.Name) is { } size:
                return Vector(type.Name[3] - '0', size);

            case ['m', 'a', 't', _, 'x', _] when type.Arguments is [{ } element] && ScalarSize(element.Name) is { } size:
                return Matrix(type.Name[3] - '0', type.Name[5] - '0', size);

            case "atomic":
                return new WgslTypeLayout(4, 4);

            case "array" when type.Arguments.Length >= 1 && Of(type.Arguments[0]) is { } element:
            {
                long stride = RoundUp(element.Align, element.Size);

                if (type.Arguments.Length == 1)
                {
                    return new WgslTypeLayout(stride, element.Align);
                }

                return constants.EvaluateTokens(type.Arguments[1].Tokens) is { Kind: WgslValueKind.Integer } count && count.Integer > 0
                    ? new WgslTypeLayout(SaturatingMultiply(stride, count.Integer), element.Align)
                    : null;
            }
        }

        return types.MembersOf(type.Name) is { } members ? Struct(type.Name, members) : null;
    }

    /// <summary>配列の要素の間隔。</summary>
    public long? StrideOf(WgslTypeRef array)
        => array is { Name: "array", Arguments.Length: >= 1 } && Of(array.Arguments[0]) is { } element ? RoundUp(element.Align, element.Size) : null;

    /// <summary>構造体のメンバーの、<c>@align</c> と <c>@size</c> を入れない配置。</summary>
    public WgslTypeLayout? MemberLayout(VariableDeclarationSyntax member) => Of(types.Of(member.Type));

    private WgslTypeLayout? Struct(string name, ImmutableArray<(string Name, WgslTypeRef Type, VariableDeclarationSyntax Declaration)> members)
    {
        if (_structs.TryGetValue(name, out WgslTypeLayout? cached))
        {
            return cached;
        }

        if (!_computing.Add(name))
        {
            return null;
        }

        try
        {
            long offset = 0;
            long align = 1;

            foreach ((_, WgslTypeRef type, VariableDeclarationSyntax declaration) in members)
            {
                if (Of(type) is not { } member)
                {
                    return _structs[name] = null;
                }

                ImmutableArray<WgslAttribute> attributes = WgslAttribute.Read(declaration.ModifierTokens);
                long memberAlign = Number(attributes, "align") ?? member.Align;
                long memberSize = Number(attributes, "size") ?? member.Size;

                offset = RoundUp(memberAlign, offset);
                offset = SaturatingAdd(offset, memberSize);
                align = Math.Max(align, memberAlign);
            }

            return _structs[name] = new WgslTypeLayout(RoundUp(align, offset), align);
        }
        finally
        {
            _computing.Remove(name);
        }
    }

    /// <summary>属性の数の値 (<c>@align(16)</c> の 16)。</summary>
    public long? Number(ImmutableArray<WgslAttribute> attributes, string name)
        => attributes.FirstOrDefault(a => a.Name.Text == name) is { Name: not null } attribute
           && constants.EvaluateTokens(attribute.Arguments) is { Kind: WgslValueKind.Integer } value
           && value.Integer >= 0 && value.Integer <= long.MaxValue
            ? (long)value.Integer
            : null;

    private static long? ScalarSize(string name) => name switch
    {
        "bool" or "i32" or "u32" or "f32" => 4,
        "f16" or "i16" or "u16" => 2,
        "i64" or "u64" or "f64" => 8,
        _ => null,
    };

    private static WgslTypeLayout Vector(int components, long scalar)
        => new(components * scalar, (components == 2 ? 2 : 4) * scalar);

    private static WgslTypeLayout Matrix(int columns, int rows, long scalar)
    {
        WgslTypeLayout column = Vector(rows, scalar);
        return new WgslTypeLayout(columns * RoundUp(column.Align, column.Size), column.Align);
    }

    public static long RoundUp(long align, long value) => align <= 0 ? value : (value + align - 1) / align * align;

    private static long SaturatingMultiply(long a, Int128 b)
    {
        Int128 product = a * b;
        return product > long.MaxValue ? long.MaxValue : (long)product;
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}
