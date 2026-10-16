using System.Buffers;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab;
using Shaderlyn.ShaderLab.Syntax;
using Shaderlyn.Unity.Profiles;

namespace Shaderlyn.Unity;

/// <summary>
/// Unity のシェーダー 1 つ分の、ShaderLab の側の情報。
/// </summary>
/// <remarks>
/// <para>
/// <b>解析そのもの (展開・構成・型) は、この中身を見ない。</b>
/// ShaderLab の <c>Properties</c> と HLSL の uniform の対応を見るルールと、エディタが使う。
/// セマンティックモデルから引くには <see cref="UnityCompilationExtensions.GetUnity"/> を使う。
/// </para>
/// <para>
/// プロパティの名前が HLSL のコードに現れた回数は、使われるときに初めて数える。
/// 既定の構成とバリアントのすべての木を数える。
/// </para>
/// </remarks>
public sealed class UnityShaderModel
{
    private readonly HashSet<string> _propertyReferences;
    private ShaderCompilation? _compilation;
    private Lazy<Dictionary<string, int>>? _occurrences;

    /// <summary>作る。</summary>
    /// <param name="text">解析しているファイル。</param>
    /// <param name="tree">ShaderLab の構文木。HLSL 単体のファイルでは、全部を空白でマスクしたテキストから作った空の木。</param>
    /// <param name="profile">レンダーパイプラインのプロファイル。</param>
    /// <param name="isStandaloneHlsl">HLSL 単体のファイルかどうか。</param>
    internal UnityShaderModel(SourceText text, ShaderLabSyntaxTree tree, IRenderPipelineProfile profile, bool isStandaloneHlsl)
    {
        Tree = tree;
        Profile = profile;
        IsStandaloneHlsl = isStandaloneHlsl;
        Properties = CollectProperties(tree, text);
        _propertyReferences = CollectShaderLabPropertyReferences(tree);
    }

    /// <summary>ShaderLab としての構文木。</summary>
    /// <remarks>
    /// HLSL 単体ファイルでは、全部を空白でマスクしたテキストから作った空の木である。
    /// <see cref="IsStandaloneHlsl"/> を参照。
    /// </remarks>
    public ShaderLabSyntaxTree Tree { get; }

    /// <summary>適用するレンダーパイプラインプロファイル。</summary>
    public IRenderPipelineProfile Profile { get; }

    /// <summary>
    /// HLSL 単体ファイル (<c>.compute</c> など) を解析したものかどうか。
    /// </summary>
    /// <remarks>
    /// <b>ShaderLab の構造を持たないので、それを根拠にする検査は対象が無い。</b>
    /// 「対象が無いので報告しない」は「検査できなかった」とは違うので、
    /// 見送ったことを報告する必要も無い。
    /// </remarks>
    public bool IsStandaloneHlsl { get; }

    /// <summary><c>Properties</c> に宣言されたプロパティ。</summary>
    public ImmutableArray<PropertySymbol> Properties { get; }

    /// <summary>シェーダー名。<c>Shader</c> 宣言が無い場合は空文字列。</summary>
    public string ShaderName => Tree.Root.Shader?.Name ?? string.Empty;

    /// <summary>このモデルを持つセマンティックモデル。</summary>
    private ShaderCompilation Compilation
        => _compilation ?? throw new InvalidOperationException("セマンティックモデルに結び付けられていません。");

    /// <summary>
    /// 名前が ShaderLab 側から参照されているかどうかを判定する。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>参照されている場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <c>Cull [_Cull]</c> のように、プロパティをレンダーステートの値として使う記法がある。
    /// この形で使われるプロパティは HLSL 側に uniform を持たないのが正常であり、
    /// 「対応する宣言が無い」という指摘の対象から外さなければならない。
    /// </remarks>
    public bool IsReferencedFromShaderLab(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _propertyReferences.Contains(name);
    }

    /// <summary>
    /// プロパティの名前が HLSL のコード中に現れた回数を返す。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>出現回数。マクロ展開後のトークン列を数えたもの。</returns>
    /// <remarks>
    /// <para>
    /// 「宣言されているが 1 度も読まれていない」を判定するために使う。
    /// 宣言そのものが 1 回分を占めるため、回数が 1 であれば
    /// 宣言以外のどこにも現れていないことになる。
    /// </para>
    /// <para>
    /// ブロックが複数ある場合はブロックごとに数えて合算する。
    /// 同じ共通コード片が複数の Pass へ差し込まれれば、その分だけ回数が増える。
    /// したがって<b>回数そのものに意味があるのは「1 かどうか」だけ</b>である。
    /// </para>
    /// <para>
    /// 数えるのは、プロパティの名前と頭文字が同じ識別子だけである。プロパティでない名前には 0 を返しうる。
    /// </para>
    /// </remarks>
    public int CountIdentifierOccurrences(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _occurrences!.Value.GetValueOrDefault(name);
    }

    /// <summary>
    /// プロパティの名前が uniform として宣言されているのに、宣言以外のどこにも現れないかを判定する。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>宣言だけで一度も使われていない場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 出現回数と宣言の個数を比べる。同じ共通コード片が複数の Pass へ差し込まれると
    /// 宣言も出現も Pass の数だけ増えるため、単純に「出現が 1 回か」では判定できない。
    /// </para>
    /// <para>
    /// 非活性領域に現れる名前は対象外とする。
    /// 別の構成では使われている可能性があり、断定できない。
    /// </para>
    /// </remarks>
    public bool IsDeclaredButNeverUsed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        int declarations = Compilation.CountUniformDeclarations(name);
        return declarations > 0
               && CountIdentifierOccurrences(name) <= declarations
               && !Compilation.AppearsOutsideAnalyzedCode(name);
    }

    /// <summary>
    /// プロファイルが指定する、マテリアル用の定数バッファを列挙する。
    /// </summary>
    /// <returns>該当する定数バッファ。プロファイルがこの概念を持たない場合は空。</returns>
    public ImmutableArray<ConstantBufferSymbol> GetMaterialConstantBuffers()
    {
        if (Profile.MaterialConstantBufferName is not { } bufferName)
        {
            return [];
        }

        return
        [
            .. Compilation.Programs
                .SelectMany(p => p.ConstantBuffers)
                .Where(b => string.Equals(b.Name, bufferName, StringComparison.Ordinal))
        ];
    }

    /// <summary>セマンティックモデルに結び付ける。作り方 (<see cref="UnityCompilation"/>) が 1 度だけ呼ぶ。</summary>
    /// <param name="compilation">このモデルを持つセマンティックモデル。</param>
    internal void Attach(ShaderCompilation compilation)
    {
        _compilation = compilation;

        // 既定の構成とバリアントのすべての木を数える。使われるときに初めて数える。
        _occurrences = new(() => CountOccurrences([.. compilation.Programs, .. compilation.SymbolVariants]));
    }

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
    private Dictionary<string, int> CountOccurrences(IEnumerable<AnalyzedProgram> trees)
    {
        SearchValues<char> initials = SearchValues.Create(
            [.. Properties.Where(p => p.Name.Length > 0).Select(p => p.Name[0]).Distinct()]);

        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in trees)
        {
            foreach (ShaderSyntaxToken token in program.Tree.PreprocessResult.Tokens)
            {
                if (token.Kind != ShaderSyntaxKind.IdentifierToken
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
