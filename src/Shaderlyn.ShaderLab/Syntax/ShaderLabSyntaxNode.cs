using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.ShaderLab.Syntax;

/// <summary>
/// 構文木の子要素。ノードとトークンのどちらかを表す。
/// </summary>
/// <remarks>
/// ノードとトークンを同じ列で扱えるようにすることで、
/// 構文木を「元のテキストの順序どおりに、1 要素も漏らさず」走査できる。
/// この性質がラウンドトリップ (元テキストの完全復元) の根拠になる。
/// </remarks>
public readonly struct SyntaxNodeOrToken
{
    private SyntaxNodeOrToken(ShaderLabSyntaxNode? node, SyntaxToken? token)
    {
        Node = node;
        Token = token;
    }

    /// <summary>ノードの場合はその参照。トークンの場合は <see langword="null"/>。</summary>
    public ShaderLabSyntaxNode? Node { get; }

    /// <summary>トークンの場合はその参照。ノードの場合は <see langword="null"/>。</summary>
    public SyntaxToken? Token { get; }

    /// <summary>ノードを表しているかどうか。</summary>
    public bool IsNode => Node is not null;

    /// <summary>トークンを表しているかどうか。</summary>
    public bool IsToken => Token is not null;

    /// <summary>trivia を含めた範囲。</summary>
    public TextSpan FullSpan => Node?.FullSpan ?? Token?.FullSpan ?? default;

    /// <summary>trivia を除いた範囲。</summary>
    public TextSpan Span => Node?.Span ?? Token?.Span ?? default;

    /// <summary>トークンをこの型へ変換する。</summary>
    /// <param name="token">変換元のトークン。</param>
    public static implicit operator SyntaxNodeOrToken(SyntaxToken token) => new(null, token);

    /// <summary>ノードをこの型へ変換する。</summary>
    /// <param name="node">変換元のノード。</param>
    public static implicit operator SyntaxNodeOrToken(ShaderLabSyntaxNode node) => new(node, null);

    /// <summary>内容を表す文字列を返す。</summary>
    /// <returns>デバッグ用の文字列表現。</returns>
    public override string ToString() => Node?.GetType().Name ?? Token?.ToString() ?? "<空>";
}

/// <summary>
/// ShaderLab の構文ノードに共通する基底クラス。
/// </summary>
/// <remarks>
/// <para>
/// 範囲は子要素から導出する。派生クラスは <see cref="ChildNodesAndTokens"/> を
/// 「元のテキストに現れる順序で、1 要素も漏らさず」実装するだけでよく、
/// 範囲の計算を個別に書く必要はない。
/// </para>
/// <para>
/// <b>この「漏らさず」という条件がラウンドトリップの前提である。</b>
/// 実装を忘れた要素があると、その部分がテキスト復元から欠落する。
/// ラウンドトリップテストはまさにこの漏れを検出するために存在する。
/// </para>
/// </remarks>
public abstract class ShaderLabSyntaxNode : SyntaxNode
{
    private TextSpan? _span;
    private TextSpan? _fullSpan;

    /// <inheritdoc/>
    public override TextSpan FullSpan => _fullSpan ??= ComputeSpan(useFullSpan: true);

    /// <inheritdoc/>
    public override TextSpan Span => _span ??= ComputeSpan(useFullSpan: false);

    /// <summary>
    /// このノードの子要素を、ソースコード上の出現順に列挙する。
    /// </summary>
    /// <returns>子のノードとトークンの列。</returns>
    /// <remarks>
    /// <b>実装時はトークンを 1 つも省略してはならない。</b>
    /// 括弧やカンマのように意味を持たないトークンも必ず含めること。
    /// 省略するとラウンドトリップが壊れ、将来のフォーマッタや自動修正の土台も失われる。
    /// </remarks>
    public abstract IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens();

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNode> ChildNodes()
    {
        foreach (SyntaxNodeOrToken child in ChildNodesAndTokens())
        {
            if (child.Node is not null)
            {
                yield return child.Node;
            }
        }
    }

    /// <summary>
    /// このノードに含まれるすべてのトークンを、出現順に列挙する。
    /// </summary>
    /// <returns>トークンの列。</returns>
    public IEnumerable<SyntaxToken> DescendantTokens()
    {
        foreach (SyntaxNodeOrToken child in ChildNodesAndTokens())
        {
            if (child.Token is not null)
            {
                yield return child.Token;
            }
            else if (child.Node is not null)
            {
                foreach (SyntaxToken token in child.Node.DescendantTokens())
                {
                    yield return token;
                }
            }
        }
    }

    /// <summary>
    /// 子要素の範囲からこのノードの範囲を求める。
    /// </summary>
    /// <param name="useFullSpan">trivia を含めた範囲を求める場合は <see langword="true"/>。</param>
    /// <returns>算出された範囲。子要素が 1 つも無い場合は長さ 0 の範囲。</returns>
    private TextSpan ComputeSpan(bool useFullSpan)
    {
        int start = -1;
        int end = -1;

        foreach (SyntaxNodeOrToken child in ChildNodesAndTokens())
        {
            TextSpan childSpan = useFullSpan ? child.FullSpan : child.Span;

            if (start < 0)
            {
                start = childSpan.Start;
            }

            end = childSpan.End;
        }

        return start < 0 ? default : TextSpan.FromBounds(start, Math.Max(start, end));
    }
}
