using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// 型引数まで含めた WGSL の型 (<c>array&lt;vec3&lt;f32&gt;, 4&gt;</c>)。
/// </summary>
/// <param name="Name">型の名前 (別名は元の型にしてある)。</param>
/// <param name="Arguments">型引数。型でない引数 (配列の大きさ、アドレス空間、形式) は <see cref="Tokens"/> だけを持つ型にする。</param>
/// <param name="Tokens">この型を書いたトークン。</param>
internal sealed record WgslTypeRef(string Name, ImmutableArray<WgslTypeRef> Arguments, ImmutableArray<ShaderSyntaxToken> Tokens)
{
    /// <summary>最初のトークン (報告の位置)。</summary>
    public ShaderSyntaxToken? First => Tokens.IsDefaultOrEmpty ? null : Tokens[0];

    /// <summary>大きさを書かない配列 (<c>array&lt;f32&gt;</c>)。</summary>
    public bool IsRuntimeArray => Name == "array" && Arguments.Length == 1;

    public override string ToString()
        => Arguments.IsDefaultOrEmpty ? Name : $"{Name}<{string.Join(", ", Arguments.Select(a => a.Name.Length > 0 ? a.ToString() : string.Concat(a.Tokens.Select(t => t.Text))))}>";
}

/// <summary>
/// ファイルの構造体と別名から、WGSL の型の形を引く。
/// </summary>
internal sealed class WgslTypes
{
    /// <summary>テクスチャ・サンプラーなど、資源の変数 (アドレス空間を書かない var) にだけ使える型。</summary>
    public static readonly FrozenSet<string> HandleTypes = FrozenSet.ToFrozenSet(
    [
        "sampler", "sampler_comparison",
        "texture_1d", "texture_2d", "texture_2d_array", "texture_3d", "texture_cube", "texture_cube_array", "texture_multisampled_2d",
        "texture_storage_1d", "texture_storage_2d", "texture_storage_2d_array", "texture_storage_3d",
        "texture_depth_2d", "texture_depth_2d_array", "texture_depth_cube", "texture_depth_cube_array", "texture_depth_multisampled_2d",
        "texture_external", "acceleration_structure", "binding_array",
    ], StringComparer.Ordinal);

    /// <summary>標本を取るテクスチャ (型引数が要素の型)。</summary>
    public static readonly FrozenSet<string> SampledTextures = FrozenSet.ToFrozenSet(
        ["texture_1d", "texture_2d", "texture_2d_array", "texture_3d", "texture_cube", "texture_cube_array", "texture_multisampled_2d"], StringComparer.Ordinal);

    /// <summary>ストレージのテクスチャの形式 (naga 30 の <c>map_storage_format</c>)。</summary>
    public static readonly FrozenSet<string> StorageFormats = FrozenSet.ToFrozenSet(
    [
        "r8unorm", "r8snorm", "r8uint", "r8sint", "r16unorm", "r16snorm", "r16uint", "r16sint", "r16float", "rg8unorm", "rg8snorm", "rg8uint", "rg8sint",
        "r32uint", "r32sint", "r32float", "rg16unorm", "rg16snorm", "rg16uint", "rg16sint", "rg16float", "rgba8unorm", "rgba8snorm", "rgba8uint", "rgba8sint",
        "rgb10a2uint", "rgb10a2unorm", "rg11b10ufloat", "r64uint", "rg32uint", "rg32sint", "rg32float", "rgba16unorm", "rgba16snorm", "rgba16uint",
        "rgba16sint", "rgba16float", "rgba32uint", "rgba32sint", "rgba32float", "bgra8unorm",
    ], StringComparer.Ordinal);

    /// <summary>スカラーの型。</summary>
    public static readonly FrozenSet<string> Scalars = FrozenSet.ToFrozenSet(
        ["bool", "i32", "u32", "f32", "f16", "i64", "u64", "f64", "i16", "u16"], StringComparer.Ordinal);

    private readonly Dictionary<string, ImmutableArray<(string Name, WgslTypeRef Type, VariableDeclarationSyntax Declaration)>> _structs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableArray<ShaderSyntaxToken>> _aliases = new(StringComparer.Ordinal);

    /// <summary>ファイルの直下の構造体と別名を集める。</summary>
    public WgslTypes(CompilationUnitSyntax root)
    {
        foreach (DeclarationSyntax declaration in root.Declarations)
        {
            switch (declaration)
            {
                case TypedefDeclarationSyntax { Keyword.Text: "alias", Tokens: [{ } name, { Kind: ShaderSyntaxKind.EqualsToken }, .. var type] }:
                    _aliases.TryAdd(name.Text, [.. type]);
                    break;

                case StructDeclarationSyntax { NameToken: { } structName } structure:
                    _structs.TryAdd(structName.Text, [.. structure.Members.OfType<VariableDeclarationSyntax>()
                        .SelectMany(m => m.Variables.Select(v => (v.Name, Of(m.Type), m)))]);
                    break;
            }
        }
    }

    /// <summary>構造体のメンバー。構造体でなければ <see langword="null"/>。</summary>
    public ImmutableArray<(string Name, WgslTypeRef Type, VariableDeclarationSyntax Declaration)>? MembersOf(string name)
        => _structs.TryGetValue(name, out var members) ? members : null;

    /// <summary>構文木の型から、型引数まで含めた型を作る。別名は元の型にする。</summary>
    public WgslTypeRef Of(TypeSyntax type)
    {
        ImmutableArray<ShaderSyntaxToken> tokens = type.OpenAngle is { } open && type.CloseAngle is { } close
            ? [type.NameToken, open, .. type.TemplateArguments, close]
            : [type.NameToken];
        return Parse(tokens);
    }

    /// <summary>トークンを型として読む (<c>array &lt; f32 , 4 &gt;</c>)。</summary>
    public WgslTypeRef Parse(ImmutableArray<ShaderSyntaxToken> tokens, int depth = 0)
    {
        if (tokens.IsDefaultOrEmpty || tokens[0].Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return new WgslTypeRef(string.Empty, [], tokens.IsDefault ? [] : tokens);
        }

        ShaderSyntaxToken name = tokens[0];

        // 別名 (alias A = array<f32, 4>;) は元の型にする。入れ子の別名は 16 段まで。
        if (tokens.Length == 1 && depth < 16 && _aliases.TryGetValue(name.Text, out ImmutableArray<ShaderSyntaxToken> aliased))
        {
            return Parse(aliased, depth + 1) with { Tokens = tokens };
        }

        if (tokens.Length < 3 || tokens[1].Kind != ShaderSyntaxKind.LessThanToken || tokens[^1].Kind != ShaderSyntaxKind.GreaterThanToken)
        {
            return new WgslTypeRef(tokens.Length == 1 ? name.Text : string.Empty, [], tokens);
        }

        ImmutableArray<WgslTypeRef>.Builder arguments = ImmutableArray.CreateBuilder<WgslTypeRef>();
        int start = 2;
        int nesting = 0;

        for (int i = 2; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].Kind)
            {
                case ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.OpenParenToken:
                    nesting++;
                    break;
                case ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.CloseParenToken:
                    nesting--;
                    break;
                case ShaderSyntaxKind.CommaToken when nesting == 0:
                    arguments.Add(ParseArgument([.. tokens.Skip(start).Take(i - start)], depth));
                    start = i + 1;
                    break;
            }
        }

        if (start < tokens.Length - 1)
        {
            arguments.Add(ParseArgument([.. tokens.Skip(start).Take(tokens.Length - 1 - start)], depth));
        }

        return new WgslTypeRef(name.Text, arguments.ToImmutable(), tokens);
    }

    /// <summary>型引数を読む。名前 (と型引数) だけの形なら型、それ以外 (数、式) は名前の無い型にする。</summary>
    private WgslTypeRef ParseArgument(ImmutableArray<ShaderSyntaxToken> tokens, int depth)
        => tokens is [{ Kind: ShaderSyntaxKind.IdentifierToken }] or [{ Kind: ShaderSyntaxKind.IdentifierToken }, { Kind: ShaderSyntaxKind.LessThanToken }, ..]
            ? Parse(tokens, depth)
            : new WgslTypeRef(string.Empty, [], tokens);

    /// <summary>大きさを書かない配列を含む (構造体のメンバーを含めて)。</summary>
    public bool ContainsRuntimeArray(WgslTypeRef type, int depth = 0)
        => depth < 32 && (type.IsRuntimeArray
                          || (MembersOf(type.Name) is { } members && members.Any(m => ContainsRuntimeArray(m.Type, depth + 1))));

    /// <summary>アトミックを含む。</summary>
    public bool ContainsAtomic(WgslTypeRef type, int depth = 0)
        => depth < 32 && (type.Name == "atomic"
                          || (type.Name == "array" && type.Arguments.Length > 0 && ContainsAtomic(type.Arguments[0], depth + 1))
                          || (MembersOf(type.Name) is { } members && members.Any(m => ContainsAtomic(m.Type, depth + 1))));

    /// <summary>スカラーの要素の型 (f16・i16・u16 など) を含む。</summary>
    public bool ContainsScalar(WgslTypeRef type, Func<string, bool> scalar, int depth = 0)
    {
        if (depth >= 32)
        {
            return false;
        }

        if (scalar(ElementOfShortName(type.Name) ?? type.Name))
        {
            return true;
        }

        return type.Arguments.Any(a => a.Name.Length > 0 && ContainsScalar(a, scalar, depth + 1))
               || (MembersOf(type.Name) is { } members && members.Any(m => ContainsScalar(m.Type, scalar, depth + 1)));
    }

    /// <summary>短い名前の要素の型 (<c>vec3h</c> → <c>f16</c>)。短い名前でなければ <see langword="null"/>。</summary>
    public static string? ElementOfShortName(string name)
        => (name.StartsWith("vec", StringComparison.Ordinal) && name.Length == 5) || (name.StartsWith("mat", StringComparison.Ordinal) && name.Length == 7)
            ? name[^1] switch
            {
                'f' => "f32",
                'h' => "f16",
                'i' => "i32",
                'u' => "u32",
                _ => null,
            }
            : null;

    /// <summary>データの型 (構造体のメンバー・配列の要素・関数の値にできる型) か。資源・ポインター・光線の照会は違う。</summary>
    public static bool IsDataType(WgslTypeRef type)
        => !HandleTypes.Contains(type.Name) && type.Name is not ("ptr" or "ray_query");
}
