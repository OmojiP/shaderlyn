using System.Collections.Frozen;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>enum の型 1 つ分 (HLSL 2021)。</summary>
/// <param name="Name">型の名前。</param>
/// <param name="IsScoped"><c>enum class</c> / <c>enum struct</c> なら <see langword="true"/>。</param>
/// <param name="Members">列挙子の名前。</param>
/// <param name="BaseType">基になる型 (<c>enum E : uint</c> の <c>uint</c>)。書かなければ <see langword="null"/>。</param>
/// <remarks>
/// DXC で確かめた規則: enum の型へ入れられるのは同じ enum の値 (か型変換したもの) だけである。
/// <c>enum class</c> の値は数へ暗黙に変換されず、<c>==</c> / <c>!=</c> (同じ enum どうし) と代入のほかに演算子を使えない。
/// ふつうの enum の値は数へ変換される。<c>++</c> / <c>--</c> は使えない。
/// </remarks>
internal sealed record EnumInfo(string Name, bool IsScoped, FrozenSet<string> Members, string? BaseType = null)
{
    /// <summary>列挙子に型の名前の修飾が要るか (<c>enum class</c> のほか、Slang の [UnscopedEnum] の無い enum)。名前の見え方だけに使う。</summary>
    public bool MembersNeedQualification { get; init; } = IsScoped;
}
