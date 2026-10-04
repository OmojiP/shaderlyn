using System.Buffers;
using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Profiles;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// Unity のシェーダー 1 つ分の、ShaderLab の側の情報。
/// </summary>
/// <remarks>
/// <para>
/// <b>解析そのもの (展開・構成・型) は、この中身を見ない。</b>
/// ShaderLab の <c>Properties</c> と HLSL の uniform の対応を見るルールと、エディタが使う。
/// </para>
/// <para>
/// プロパティの名前が HLSL のコードに現れた回数は、使われるときに初めて数える。
/// 既定の構成とバリアントのすべての木を数える。
/// </para>
/// </remarks>
internal sealed class UnityShaderData
{
    /// <summary>作る。</summary>
    /// <param name="text">解析しているファイル。</param>
    /// <param name="tree">ShaderLab の構文木。HLSL 単体のファイルでは、全部を空白でマスクしたテキストから作った空の木。</param>
    /// <param name="profile">レンダーパイプラインのプロファイル。</param>
    /// <param name="isStandaloneHlsl">HLSL 単体のファイルかどうか。</param>
    public UnityShaderData(SourceText text, ShaderLabSyntaxTree tree, IRenderPipelineProfile profile, bool isStandaloneHlsl)
    {
        Tree = tree;
        Profile = profile;
        IsStandaloneHlsl = isStandaloneHlsl;
        Properties = CollectProperties(tree, text);
        PropertyReferences = CollectShaderLabPropertyReferences(tree);
    }

    /// <summary>ShaderLab の構文木。</summary>
    public ShaderLabSyntaxTree Tree { get; }

    /// <summary>レンダーパイプラインのプロファイル。</summary>
    public IRenderPipelineProfile Profile { get; }

    /// <summary>HLSL 単体のファイル (<c>.compute</c> など) かどうか。</summary>
    public bool IsStandaloneHlsl { get; }

    /// <summary><c>Properties</c> に宣言されたプロパティ。</summary>
    public ImmutableArray<PropertySymbol> Properties { get; }

    /// <summary>ShaderLab の側から参照されている名前 (<c>Cull [_Cull]</c> など)。</summary>
    public HashSet<string> PropertyReferences { get; }

    /// <summary>
    /// プロパティの名前と同じ頭文字を持つ識別子が、展開後のコードに現れた回数を数える。
    /// </summary>
    /// <param name="trees">数える木。既定の構成とバリアントのすべて。</param>
    /// <returns>名前ごとの回数。</returns>
    /// <remarks>
    /// プロパティ名の先頭文字だけを集めて、トークン走査の足切りに使う。
    /// URP のヘッダ群を展開すると 1 ブロックあたり数十万トークンになるため、
    /// 全トークンに対して辞書を引くと解析時間がここに吸われる。
    /// </remarks>
    public Dictionary<string, int> CountOccurrences(IEnumerable<AnalyzedProgram> trees)
    {
        SearchValues<char> initials = SearchValues.Create(
            [.. Properties.Where(p => p.Name.Length > 0).Select(p => p.Name[0]).Distinct()]);

        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in trees)
        {
            foreach (HlslSyntaxToken token in program.Tree.PreprocessResult.Tokens)
            {
                if (token.Kind != HlslSyntaxKind.IdentifierToken
                    || token.Text.Length == 0
                    || !initials.Contains(token.Text[0]))
                {
                    continue;
                }

                occurrences[token.Text] = occurrences.GetValueOrDefault(token.Text) + 1;
            }
        }

        return occurrences;
    }

    /// <summary><c>Properties</c> ブロックからプロパティを集める。</summary>
    /// <param name="tree">ShaderLab の構文木。</param>
    /// <param name="text">ソーステキスト。</param>
    /// <returns>見つかったプロパティ。</returns>
    private static ImmutableArray<PropertySymbol> CollectProperties(ShaderLabSyntaxTree tree, SourceText text)
    {
        ImmutableArray<PropertySymbol>.Builder properties = ImmutableArray.CreateBuilder<PropertySymbol>();

        foreach (SyntaxNode node in tree.Root.DescendantNodesAndSelf())
        {
            if (node is PropertyDeclarationSyntax declaration
                && !declaration.NameToken.IsMissing
                && declaration.Name.Length > 0)
            {
                properties.Add(new PropertySymbol(declaration, text));
            }
        }

        return properties.ToImmutable();
    }

    /// <summary>
    /// ShaderLab の側からプロパティを参照している名前を集める。
    /// </summary>
    /// <param name="tree">ShaderLab の構文木。</param>
    /// <returns>参照されている名前。</returns>
    /// <remarks>
    /// <c>Cull [_Cull]</c> のように、プロパティをレンダーステートの値として使う記法がある。
    /// </remarks>
    private static HashSet<string> CollectShaderLabPropertyReferences(ShaderLabSyntaxTree tree)
    {
        HashSet<string> references = new(StringComparer.Ordinal);

        foreach (SyntaxNode node in tree.Root.DescendantNodesAndSelf())
        {
            if (node is PropertyReferenceArgumentSyntax reference && !reference.NameToken.IsMissing)
            {
                references.Add(reference.Name);
            }
        }

        return references;
    }
}
