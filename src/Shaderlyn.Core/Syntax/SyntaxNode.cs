using Shaderlyn.Core.Text;

namespace Shaderlyn.Core.Syntax;

/// <summary>
/// 構文木のノードに共通する基底クラス。ShaderLab と HLSL の両方のノードがこれを継承する。
/// </summary>
/// <remarks>
/// <para>
/// 解析ドライバがノードの具象型だけを見て走査・ディスパッチできるようにするための最小限の抽象である。
/// 言語固有の情報 (ShaderLab の Pass、HLSL の式など) は派生側が持つ。
/// </para>
/// <para>
/// Roslyn のような red/green ツリーは採用していない。
/// red/green ツリーの利点は「編集後の部分再構築」と「同一構文の共有によるメモリ削減」だが、
/// 遅いのは構文解析ではなくマクロ展開であり、シェーダー 1 つは数千行に収まるので、どちらの利点も効かない。
/// 一方で、ノードの種類ごとにクラスが 2 つ要り、どちらの層を触っているかを常に意識することになる。
/// ノードの親が 1 つに決まっていることを前提にしたコードも多い。
/// </para>
/// <para>
/// <see cref="FullSpan"/> と <see cref="Span"/> を分けているのは完全忠実性のためである。
/// <see cref="FullSpan"/> は前後の空白・コメントを含み、隣接ノードの <see cref="FullSpan"/> と
/// 隙間なく連結すると元のテキストが復元できる。この性質はラウンドトリップテストで検証している。
/// </para>
/// </remarks>
public abstract class SyntaxNode
{
    /// <summary>
    /// 前後の trivia (空白・コメント・プリプロセッサ指令) を含めた範囲。
    /// </summary>
    /// <remarks>
    /// 抽象プロパティにしているのは、範囲を子要素から導出する実装を許すためである。
    /// コンストラクタ引数で受け取る形にすると、派生クラスは子を組み立てる前に
    /// 範囲を計算しなければならず、同じ走査を 2 回書くことになる。
    /// </remarks>
    public abstract TextSpan FullSpan { get; }

    /// <summary>trivia を除いた、ノード本体の範囲。診断の位置として使うのはこちら。</summary>
    public abstract TextSpan Span { get; }

    /// <summary>
    /// 親ノード。根ノードの場合は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 構築時ではなく <see cref="SetParent(SyntaxNode)"/> で後から設定する。
    /// 子から先に作って親へ渡す構築順序のため、親の参照は子の生成時点では存在しないためである。
    /// </remarks>
    public SyntaxNode? Parent { get; private set; }

    /// <summary>
    /// このノードの直接の子ノードを、ソースコード上の出現順に列挙する。
    /// </summary>
    /// <returns>子ノードの列。子を持たないノードは空の列を返す。</returns>
    public abstract IEnumerable<SyntaxNode> ChildNodes();

    /// <summary>
    /// 親ノードを設定する。
    /// </summary>
    /// <param name="parent">親となるノード。</param>
    /// <exception cref="InvalidOperationException">既に別の親が設定されている場合。</exception>
    /// <remarks>
    /// 同じノードを 2 つの親に付け替えることは、木構造の不変条件を壊し
    /// 祖先の探索を無限ループさせうるため、明示的に拒否する。
    /// </remarks>
    public void SetParent(SyntaxNode parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (Parent is not null && !ReferenceEquals(Parent, parent))
        {
            throw new InvalidOperationException(
                $"ノード {GetType().Name} には既に親 {Parent.GetType().Name} が設定されています。");
        }

        Parent = parent;
    }

    /// <summary>
    /// 木全体を走査して、すべてのノードに親の参照を設定する。
    /// </summary>
    /// <param name="root">走査の起点となる根ノード。</param>
    /// <remarks>
    /// <para>
    /// 各ノードのコンストラクタで子に親を設定する方式は採らない。
    /// 派生クラスがフィールドを設定したあとに必ず呼ぶ、という規律を
    /// ノードの種類が増えるたびに守り続けるのは現実的でなく、
    /// 忘れても気づけない (親が辿れないだけでコンパイルは通る) ためである。
    /// </para>
    /// <para>
    /// 構文木の構築が完了した一点でこのメソッドを呼ぶことで、
    /// 設定漏れが構造的に起こりえないようにしている。
    /// </para>
    /// </remarks>
    public static void WireParents(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // 子の列挙は 1 ノードにつき 1 回で済ませる。
        // DescendantNodesAndSelf を挟むと、走査のためと接続のためで 2 回列挙することになり、
        // 列挙のたびにイテレータが確保される。
        // URP のヘッダを展開した木は 4 万ノードを超え、
        // Lit.shader 1 件の実測でここだけで 13MB を確保していた (この形では 5MB)。
        // 接続の順序は結果に影響しないので、走査順は保証しない。
        Stack<SyntaxNode> pending = new();
        pending.Push(root);

        while (pending.Count > 0)
        {
            pending.Pop().WireChildren(pending);
        }
    }

    /// <summary>
    /// 直接の子に自分を親として設定し、さらに辿る対象として積む。
    /// </summary>
    /// <param name="pending">これから辿るノードを積む先。</param>
    /// <remarks>
    /// <b>言語ごとに、子を 1 回の列挙で取り出せる形へ差し替えるための入口である。</b>
    /// 既定の実装は <see cref="ChildNodes"/> を使う。
    /// HLSL の木では <c>ChildNodes</c> が
    /// <c>ChildNodesAndTokens</c> を包む形になっており、
    /// 1 ノードにつき列挙子が 2 つ確保される。
    /// 展開後の木は数百万ノードあるので、その 1 つ分が実測に現れる。
    /// </remarks>
    protected virtual void WireChildren(Stack<SyntaxNode> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        foreach (SyntaxNode child in ChildNodes())
        {
            child.SetParent(this);
            pending.Push(child);
        }
    }

    /// <summary>
    /// 自分自身とすべての子孫ノードを、行きがけ順 (自分 → 子の順) で列挙する。
    /// </summary>
    /// <returns>自分自身を先頭とする子孫ノードの列。</returns>
    /// <remarks>
    /// <para>
    /// 再帰ではなく明示的なスタックで実装している。
    /// 深くネストしたマクロ展開や長大な式の連鎖でスタックオーバーフローを起こさないためである。
    /// リンタは壊れた入力を与えられる前提で書く必要がある。
    /// </para>
    /// <para>
    /// <b>逆順に積むのに <c>Enumerable.Reverse</c> を使わない。</b>
    /// あれはノードごとにバッファ配列を確保する。
    /// この走査はすべてのルール・ホバー・定義探索が使う最も熱い経路であり、
    /// 4 万ノードの木を 1 回歩くだけで 4 万個の配列になる。
    /// 走査全体で 1 つのバッファを使い回す。
    /// </para>
    /// </remarks>
    public IEnumerable<SyntaxNode> DescendantNodesAndSelf()
    {
        Stack<SyntaxNode> pending = new();
        pending.Push(this);

        List<SyntaxNode> buffer = [];

        while (pending.Count > 0)
        {
            SyntaxNode current = pending.Pop();
            yield return current;

            buffer.Clear();
            buffer.AddRange(current.ChildNodes());

            // スタックは後挿入先出しなので、逆順に積むことで出現順に取り出される。
            for (int i = buffer.Count - 1; i >= 0; i--)
            {
                pending.Push(buffer[i]);
            }
        }
    }

    /// <summary>
    /// 自分から根に向かって祖先ノードを順に列挙する。
    /// </summary>
    /// <returns>直接の親から根までのノードの列。自分自身は含まない。</returns>
    public IEnumerable<SyntaxNode> Ancestors()
    {
        for (SyntaxNode? current = Parent; current is not null; current = current.Parent)
        {
            yield return current;
        }
    }

    /// <summary>
    /// 指定した型の最も近い祖先ノードを探す。
    /// </summary>
    /// <typeparam name="TNode">探す祖先の型。</typeparam>
    /// <returns>見つかった祖先。存在しない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// HLSL のノードから、それを囲む ShaderLab の Pass を辿るといった用途で使う。
    /// </remarks>
    public TNode? FirstAncestorOrDefault<TNode>() where TNode : SyntaxNode
        => Ancestors().OfType<TNode>().FirstOrDefault();
}
