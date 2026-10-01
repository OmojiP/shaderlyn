using System.Text.RegularExpressions;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// ルール API リファレンスと実装の照合。
/// </summary>
/// <remarks>
/// <para>
/// <b>ノード型の一覧が欠けていると、ルールを書く人はその型に辿り着けない。</b>
/// <c>RegisterNodeAction&lt;TNode&gt;</c> の <c>TNode</c> に何を書けるかは、
/// この一覧だけが答える。載っていない型は、利用者から見れば存在しないのと同じである。
/// </para>
/// <para>
/// 人のレビューでは、型が 1 つ増えたことに気づけない。
/// <see cref="RuleDocumentationTests"/> がルールに対してしているのと同じ照合を、
/// ノード型に対して行う。
/// </para>
/// </remarks>
public sealed partial class ApiReferenceTests
{
    /// <summary>リファレンスの場所。</summary>
    private static string ReferencePath
        => Path.Combine(AppContext.BaseDirectory, "docs", "custom-rules", "api-reference.md");

    /// <summary>
    /// ドキュメントに書かれている型名を拾う正規表現。
    /// </summary>
    /// <returns>照合に使う正規表現。</returns>
    /// <remarks>
    /// バッククォートで囲まれたものだけを見る。
    /// 地の文に現れた語まで拾うと、説明のために書いた名前で落ちる。
    /// </remarks>
    [GeneratedRegex("`([A-Za-z]+Syntax(?:Node)?)`")]
    private static partial Regex TypeNameInDocument();

    /// <summary>
    /// 一覧に載っていなければならないノード型。
    /// </summary>
    /// <returns>ShaderLab と HLSL の公開ノード型。</returns>
    /// <remarks>
    /// 抽象型も含める。<b>登録先として使うのはむしろ抽象型のほうである。</b>
    /// 基底型を指定すると派生型すべてで発火するため、
    /// 「式すべて」を見たい人は <see cref="HlslExpressionSyntax"/> を書く。
    /// </remarks>
    private static IReadOnlyList<Type> RequiredNodeTypes()
    {
        Type[] roots = [typeof(ShaderLabSyntaxNode), typeof(HlslSyntaxNode)];

        return
        [
            .. roots
                .SelectMany(root => root.Assembly.GetExportedTypes().Where(root.IsAssignableFrom))
                .Distinct()
                .OrderBy(type => type.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// 実在する型の名前をすべて集める。
    /// </summary>
    /// <returns>公開されている型の名前。</returns>
    /// <remarks>
    /// ノード型に限らない。<c>HlslTypeSyntax</c> のように
    /// ノードではない型の名前もドキュメントには現れる。
    /// </remarks>
    private static HashSet<string> AllPublicTypeNames()
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (Type root in (Type[])[typeof(ShaderLabSyntaxNode), typeof(HlslSyntaxNode)])
        {
            foreach (Type type in root.Assembly.GetExportedTypes())
            {
                names.Add(type.Name);
            }
        }

        foreach (Type type in typeof(Core.Syntax.SyntaxNode).Assembly.GetExportedTypes())
        {
            names.Add(type.Name);
        }

        return names;
    }

    [Fact]
    public void すべてのノード型がリファレンスに載っている()
    {
        string document = File.ReadAllText(ReferencePath);

        List<string> missing =
        [
            .. RequiredNodeTypes()
                .Select(type => type.Name)
                .Where(name => !document.Contains($"`{name}`", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            missing.Count == 0,
            $"次のノード型が docs/custom-rules/api-reference.md にありません "
            + $"({missing.Count} 件):{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void リファレンスに実在しない型が書かれていない()
    {
        string document = File.ReadAllText(ReferencePath);
        HashSet<string> existing = AllPublicTypeNames();

        List<string> unknown =
        [
            .. TypeNameInDocument().Matches(document)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Where(name => !existing.Contains(name))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            unknown.Count == 0,
            $"次の型は実装に存在しません。消えたか、綴りが違います:{Environment.NewLine}"
            + string.Join(Environment.NewLine, unknown));
    }
}
