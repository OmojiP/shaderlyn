using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Hlsl.Syntax;

/// <summary>
/// HLSL 構文木の子要素。ノードとトークンのどちらかを表す。
/// </summary>
public readonly struct HlslNodeOrToken
{
    private HlslNodeOrToken(HlslSyntaxNode? node, HlslSyntaxToken? token)
    {
        Node = node;
        Token = token;
    }

    /// <summary>ノードの場合はその参照。</summary>
    public HlslSyntaxNode? Node { get; }

    /// <summary>トークンの場合はその参照。</summary>
    public HlslSyntaxToken? Token { get; }

    /// <summary>ノードを表しているかどうか。</summary>
    public bool IsNode => Node is not null;

    /// <summary>トークンを表しているかどうか。</summary>
    public bool IsToken => Token is not null;

    /// <summary>トークンをこの型へ変換する。</summary>
    /// <param name="token">変換元のトークン。</param>
    public static implicit operator HlslNodeOrToken(HlslSyntaxToken token) => new(null, token);

    /// <summary>ノードをこの型へ変換する。</summary>
    /// <param name="node">変換元のノード。</param>
    public static implicit operator HlslNodeOrToken(HlslSyntaxNode node) => new(node, null);

    /// <summary>内容を表す文字列を返す。</summary>
    /// <returns>デバッグ用の文字列表現。</returns>
    public override string ToString() => Node?.GetType().Name ?? Token?.ToString() ?? "<空>";
}

/// <summary>
/// HLSL の構文ノードに共通する基底クラス。
/// </summary>
/// <remarks>
/// <para>
/// <b>ShaderLab の構文木と違い、完全忠実性は成り立たない。</b>
/// この木はプリプロセス後のトークン列から組み立てられるため、
/// マクロは展開済みで、条件分岐の非活性領域は存在せず、
/// include されたファイルのトークンが混ざっている。
/// 元のテキストを復元することは原理的にできない。
/// </para>
/// <para>
/// その帰結として、1 つのノードに含まれるトークンが
/// <b>複数のファイル由来である場合がある</b>。
/// <see cref="Span"/> は同一ファイル内でのみ意味を持つため、
/// 診断の位置には <see cref="GetLocation"/> を使うこと。
/// </para>
/// </remarks>
public abstract class HlslSyntaxNode : SyntaxNode
{
    private TextSpan? _span;
    private HlslSyntaxToken? _firstToken;
    private bool _firstTokenComputed;

    /// <inheritdoc/>
    public override TextSpan FullSpan => Span;

    /// <inheritdoc/>
    /// <remarks>
    /// 先頭のトークンから、同じファイルに属する最後のトークンまでを範囲とする。
    /// 途中で別のファイル由来のトークンが現れた場合、そこで範囲を打ち切る。
    /// 異なるファイルのオフセットを混ぜた範囲は無意味であり、
    /// 診断の位置として使うと存在しない場所を指してしまうためである。
    /// </remarks>
    public override TextSpan Span => _span ??= ComputeSpan();

    /// <summary>
    /// このノードの子要素を、トークン列に現れる順に列挙する。
    /// </summary>
    /// <returns>子のノードとトークンの列。</returns>
    public abstract IEnumerable<HlslNodeOrToken> ChildNodesAndTokens();

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNode> ChildNodes()
    {
        foreach (HlslNodeOrToken child in ChildNodesAndTokens())
        {
            if (child.Node is not null)
            {
                yield return child.Node;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>子とトークンの列挙を直に使う。</b>
    /// <see cref="ChildNodes"/> はこの列挙を包んでおり、
    /// 1 ノードにつき列挙子が 2 つ確保される。
    /// 展開後の木は数百万ノードあるので、包みを 1 枚外すだけで速くなる。
    /// </remarks>
    protected override void WireChildren(Stack<SyntaxNode> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        foreach (HlslNodeOrToken child in ChildNodesAndTokens())
        {
            if (child.Node is { } node)
            {
                node.SetParent(this);
                pending.Push(node);
            }
        }
    }

    /// <summary>
    /// このノードに含まれるすべてのトークンを、出現順に列挙する。
    /// </summary>
    /// <returns>トークンの列。</returns>
    public IEnumerable<HlslSyntaxToken> DescendantTokens()
    {
        foreach (HlslNodeOrToken child in ChildNodesAndTokens())
        {
            if (child.Token is not null)
            {
                yield return child.Token;
            }
            else if (child.Node is not null)
            {
                foreach (HlslSyntaxToken token in child.Node.DescendantTokens())
                {
                    yield return token;
                }
            }
        }
    }

    /// <summary>
    /// このノードの先頭のトークン。
    /// </summary>
    /// <remarks>
    /// 診断の位置と、どのファイル由来かの判定に使う。
    /// 子の分を使い回すので、木全体でもノード数に比例した走査で済む。
    /// </remarks>
    public HlslSyntaxToken? FirstToken
    {
        get
        {
            if (!_firstTokenComputed)
            {
                _firstToken = FindFirstToken();
                _firstTokenComputed = true;
            }

            return _firstToken;
        }
    }

    /// <summary>このノードが属するソーステキスト。</summary>
    public SourceText? Source => FirstToken?.Source;

    /// <summary>
    /// このノードの位置を診断用の位置情報として返す。
    /// </summary>
    /// <returns>位置情報。トークンを 1 つも含まない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>診断を出すときは <see cref="Span"/> ではなくこれを使うこと。</b>
    /// ノードがどのファイル由来かの情報を含んでおり、
    /// include されたファイルの中の記述にも正しい位置を与えられる。
    /// </remarks>
    public Location? GetLocation()
    {
        HlslSyntaxToken? first = FirstToken;

        if (first is null)
        {
            return null;
        }

        // 利用者のファイルが書いたマクロから来たコードは、本体の位置を指す。
        // そこが利用者の直す場所であり、同じマクロを何度使っても 1 か所にまとまる
        // (HlslSyntaxToken.MacroDefinitionSpan)。本体は呼び出しと別のファイルにあることがある。
        if (first.MacroDefinitionSpan is { } definition)
        {
            return Location.Create(first.MacroDefinitionSource ?? first.Source, definition);
        }

        // マクロの実引数として書かれたコードは、書かれた位置を指す (HlslSyntaxToken.MacroArgumentSpan)。
        // 終わりも実引数なら、そこまでを含める。
        if (first.MacroArgumentSpan is { } argument)
        {
            TextSpan? last = LastArgumentSpan();
            return Location.Create(
                first.Source,
                last is { } end && end.End > argument.Start ? TextSpan.FromBounds(argument.Start, end.End) : argument);
        }

        return Location.Create(first.Source, Span);
    }

    /// <summary>このノードの最後のトークンが実引数として書かれた位置を返す。</summary>
    /// <returns>書かれた位置。最後のトークンが実引数でなければ <see langword="null"/>。</returns>
    private TextSpan? LastArgumentSpan()
    {
        HlslSyntaxToken? last = null;

        foreach (HlslSyntaxToken token in DescendantTokens())
        {
            last = token;
        }

        return last?.MacroArgumentSpan;
    }

    /// <summary>直接の子から先頭のトークンを探す。</summary>
    /// <returns>見つかったトークン。1 つも含まない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 子ノードの先頭トークンは覚えてあるので、子を順に見れば足りる。
    /// 部分木のトークンを毎回すべて歩く必要は無い。
    /// </remarks>
    private HlslSyntaxToken? FindFirstToken()
    {
        foreach (HlslNodeOrToken child in ChildNodesAndTokens())
        {
            if (child.Token is { } token)
            {
                return token;
            }

            if (child.Node?.FirstToken is { } fromNode)
            {
                return fromNode;
            }
        }

        return null;
    }

    /// <summary>
    /// 先頭のトークンと、それと同じファイルに属する末尾のトークンから範囲を求める。
    /// </summary>
    /// <returns>算出された範囲。</returns>
    /// <remarks>
    /// <para>
    /// <b>直接の子だけを見る。</b>
    /// 子ノードの範囲は同じ規則で求まっており、覚えてもある。
    /// 部分木のトークンを毎回すべて歩くと、木全体では
    /// ノード数 × 部分木の大きさに比例した走査になり、
    /// 深く入れ子になったヘッダでは解析時間の大半がここに消える。
    /// </para>
    /// <para>
    /// ファイルが変わったらそこで打ち切るのは、
    /// 別ファイルのオフセットを混ぜた範囲が存在しない場所を指すためである。
    /// 子ノードの範囲も同じ理由で既に打ち切られているので、
    /// 直接の子だけを見ても同じ結果になる。
    /// </para>
    /// </remarks>
    private TextSpan ComputeSpan()
    {
        SourceText? source = null;
        int start = -1;
        int end = -1;

        foreach (HlslNodeOrToken child in ChildNodesAndTokens())
        {
            SourceText? childSource;
            TextSpan childSpan;

            if (child.Token is { } token)
            {
                childSource = token.Source;
                childSpan = token.Span;
            }
            else if (child.Node is { } node)
            {
                // トークンを 1 つも含まない子は範囲を持たない。飛ばす。
                if (node.FirstToken is not { } childFirst)
                {
                    continue;
                }

                childSource = childFirst.Source;
                childSpan = node.Span;
            }
            else
            {
                continue;
            }

            if (source is null)
            {
                source = childSource;
                start = childSpan.Start;
                end = childSpan.End;
                continue;
            }

            if (!ReferenceEquals(childSource, source))
            {
                break;
            }

            end = Math.Max(end, childSpan.End);
        }

        return source is null ? default : TextSpan.FromBounds(start, Math.Max(start, end));
    }
}
