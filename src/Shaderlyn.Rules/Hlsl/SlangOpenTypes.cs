using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules;

/// <summary>
/// Slang で、メンバーを書いた構造体の宣言だけからは決められない型 (メンバーの検査をしない型) を集める。
/// </summary>
/// <remarks>
/// <para>
/// Slang の型のメンバーは、構造体の宣言のほかに、<c>extension T { … }</c> と <c>property</c> / <c>__subscript</c> からも来る。
/// 構文解析はそれらを読み飛ばしているので、それに当たる型のメンバーを「無い」とは言えない。
/// </para>
/// <list type="bullet">
///   <item><description><c>extension</c> の対象の型。対象がジェネリックの仮引数 (<c>extension&lt;T : IFoo&gt; T</c>) なら、どの型にもメンバーが足されうるので、すべての型</description></item>
///   <item><description>読み飛ばしたメンバー (<c>property</c>・<c>__subscript</c>・<c>associatedtype</c> など) を持つ構造体とインターフェイス</description></item>
///   <item><description>基底がメンバーを決められない型である構造体。インターフェイス (か、構造体として見つからない型) の <c>extension</c> があれば、基底を持つ構造体すべて
///   (2 つ目からの基底 <c>struct S : IA, IB</c> は構文木に残していないので、どの基底から足されるかを決められない)</description></item>
/// </list>
/// <para>取り込んだモジュールの宣言も見る (モジュールのトークンは木に入っている)。</para>
/// </remarks>
internal sealed class SlangOpenTypes : IReadOnlySet<string>
{
    private static readonly ConditionalWeakTable<ShaderCompilation, SlangOpenTypes> Cache = [];

    private readonly bool _all;
    private readonly FrozenSet<string> _names;

    private SlangOpenTypes(bool all, IEnumerable<string> names)
    {
        _all = all;
        _names = names.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>モデルの、メンバーを決められない型。</summary>
    /// <param name="compilation">対象のモデル。</param>
    /// <returns>型の集まり。</returns>
    public static SlangOpenTypes Of(ShaderCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return Cache.GetValue(compilation, Collect);
    }

    private static SlangOpenTypes Collect(ShaderCompilation compilation)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        bool all = false;
        List<StructDeclarationSyntax> structs = [];
        HashSet<string> extended = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case IncompleteDeclarationSyntax { Tokens: [{ Text: "extension" }, ..] } extension:
                        if (ExtendedType(extension) is { } target)
                        {
                            extended.Add(target);
                            names.Add(target);
                        }
                        else
                        {
                            all = true;
                        }

                        break;

                    case StructDeclarationSyntax structure:
                        structs.Add(structure);

                        if (structure.Members.Any(m => m is IncompleteDeclarationSyntax { Tokens: [{ Kind: ShaderSyntaxKind.IdentifierToken }, ..] }))
                        {
                            names.Add(structure.Name);
                        }

                        break;
                }
            }
        }

        // インターフェイスか、構造体として見つからない型への extension は、それを基底に持つどの構造体にもメンバーを足しうる。
        HashSet<string> concrete = new(structs.Where(s => s.Keyword.Text is "struct" or "class").Select(s => s.Name), StringComparer.Ordinal);
        bool extendsInterfaces = extended.Any(n => !concrete.Contains(n));

        // 基底がメンバーを決められない型なら、その構造体も決められない (継承をたどる)。
        bool changed = true;

        while (changed)
        {
            changed = false;

            foreach (StructDeclarationSyntax structure in structs)
            {
                if (structure.BaseTypeName is { } baseName
                    && (extendsInterfaces || names.Contains(baseName))
                    && names.Add(structure.Name))
                {
                    changed = true;
                }
            }
        }

        return new SlangOpenTypes(all, names);
    }

    /// <summary><c>extension</c> の対象の型の名前。ジェネリックの仮引数なら <see langword="null"/> (どの型でもありうる)。</summary>
    private static string? ExtendedType(IncompleteDeclarationSyntax extension)
    {
        int i = 1;
        HashSet<string> parameters = new(StringComparer.Ordinal);

        // extension<T : IFoo> T : IBar { … } の <…> を読み飛ばし、仮引数の名前を覚える。
        if (i < extension.Tokens.Length && extension.Tokens[i].Kind == ShaderSyntaxKind.LessThanToken)
        {
            int depth = 0;

            for (; i < extension.Tokens.Length; i++)
            {
                ShaderSyntaxToken token = extension.Tokens[i];
                depth += token.Kind switch
                {
                    ShaderSyntaxKind.LessThanToken => 1,
                    ShaderSyntaxKind.GreaterThanToken => -1,
                    ShaderSyntaxKind.GreaterThanGreaterThanToken => -2,
                    _ => 0,
                };

                if (token.Kind == ShaderSyntaxKind.IdentifierToken && extension.Tokens[i - 1].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.CommaToken)
                {
                    parameters.Add(token.Text is "let" or "each" && i + 1 < extension.Tokens.Length ? extension.Tokens[i + 1].Text : token.Text);
                }

                if (depth <= 0)
                {
                    i++;
                    break;
                }
            }
        }

        return i < extension.Tokens.Length
               && extension.Tokens[i].Kind == ShaderSyntaxKind.IdentifierToken
               && !parameters.Contains(extension.Tokens[i].Text)
            ? extension.Tokens[i].Text
            : null;
    }

    /// <inheritdoc/>
    public bool Contains(string item) => _all || _names.Contains(item);

    /// <inheritdoc/>
    public int Count => _names.Count;

    /// <inheritdoc/>
    public IEnumerator<string> GetEnumerator() => _names.GetEnumerator();

    /// <inheritdoc/>
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool IsProperSubsetOf(IEnumerable<string> other) => _names.IsProperSubsetOf(other);

    /// <inheritdoc/>
    public bool IsProperSupersetOf(IEnumerable<string> other) => _names.IsProperSupersetOf(other);

    /// <inheritdoc/>
    public bool IsSubsetOf(IEnumerable<string> other) => _names.IsSubsetOf(other);

    /// <inheritdoc/>
    public bool IsSupersetOf(IEnumerable<string> other) => _names.IsSupersetOf(other);

    /// <inheritdoc/>
    public bool Overlaps(IEnumerable<string> other) => _names.Overlaps(other);

    /// <inheritdoc/>
    public bool SetEquals(IEnumerable<string> other) => _names.SetEquals(other);
}
