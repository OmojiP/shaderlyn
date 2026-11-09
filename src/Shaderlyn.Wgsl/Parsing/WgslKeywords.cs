using System.Collections.Frozen;

namespace Shaderlyn.Wgsl.Parsing;

/// <summary>
/// WGSL のキーワードと予約語。名前 (変数・関数・構造体・メンバー・仮引数) にできない。
/// </summary>
/// <remarks>
/// WGSL の仕様の表 (Keyword Summary。naga 30 の <c>keywords/wgsl.rs</c> の RESERVED と同じもの) から写した。
/// <c>__</c> で始まる名前も予約されている。
/// </remarks>
internal static class WgslKeywords
{
    /// <summary>キーワード。</summary>
    public static FrozenSet<string> Keywords { get; } = FrozenSet.ToFrozenSet(
    [
        "alias", "break", "case", "const", "const_assert", "continue", "continuing", "default", "diagnostic", "discard", "else", "enable", "false",
        "fn", "for", "if", "let", "loop", "override", "requires", "return", "struct", "switch", "true", "var", "while",
    ], StringComparer.Ordinal);

    /// <summary>予約語 (今は使っていないが、将来の言語のために名前にできない語)。</summary>
    public static FrozenSet<string> Reserved { get; } = FrozenSet.ToFrozenSet(
    [
        "NULL", "Self", "abstract", "active", "alignas", "alignof", "as", "asm", "asm_fragment", "async", "attribute", "auto", "await", "become",
        "cast", "catch", "class", "co_await", "co_return", "co_yield", "coherent", "column_major", "common", "compile", "compile_fragment", "concept",
        "const_cast", "consteval", "constexpr", "constinit", "crate", "debugger", "decltype", "delete", "demote", "demote_to_helper", "do",
        "dynamic_cast", "enum", "explicit", "export", "extends", "extern", "external", "fallthrough", "filter", "final", "finally", "friend", "from",
        "fxgroup", "get", "goto", "groupshared", "highp", "impl", "implements", "import", "inline", "instanceof", "interface", "layout", "lowp",
        "macro", "macro_rules", "match", "mediump", "meta", "mod", "module", "move", "mut", "mutable", "namespace", "new", "nil", "noexcept",
        "noinline", "nointerpolation", "non_coherent", "noncoherent", "noperspective", "null", "nullptr", "of", "operator", "package", "packoffset",
        "partition", "pass", "patch", "pixelfragment", "precise", "precision", "premerge", "priv", "protected", "pub", "public", "readonly", "ref",
        "regardless", "register", "reinterpret_cast", "require", "resource", "restrict", "self", "set", "shared", "sizeof", "smooth", "snorm",
        "static", "static_assert", "static_cast", "std", "subroutine", "super", "target", "template", "this", "thread_local", "throw", "trait", "try",
        "type", "typedef", "typeid", "typename", "typeof", "union", "unless", "unorm", "unsafe", "unsized", "use", "using", "varying", "virtual",
        "volatile", "wgsl", "where", "with", "writeonly", "yield",
    ], StringComparer.Ordinal);

    /// <summary>名前にできない語か (キーワード、予約語、__ で始まる語)。</summary>
    /// <param name="name">名前。</param>
    /// <returns>名前にできなければ <see langword="true"/>。</returns>
    public static bool IsReserved(string name)
        => Keywords.Contains(name) || Reserved.Contains(name) || name.StartsWith("__", StringComparison.Ordinal);
}
