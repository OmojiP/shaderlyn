using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Semantics.Conditional;

/// <summary>
/// 構文木のノード 1 つと、それが存在する条件。
/// </summary>
/// <param name="Node">対象のノード。</param>
/// <param name="Condition">このノードが存在する条件。</param>
public readonly record struct ConditionalNode(HlslSyntaxNode Node, SymbolCondition Condition);

/// <summary>
/// 既定の構成の木には無く、バリアントの木にしか無いノード。
/// </summary>
/// <param name="Parent">既定の構成の木で、このノードが入る親。</param>
/// <param name="Node">入れるノード。</param>
/// <param name="Condition">このノードが存在する条件。</param>
/// <remarks>
/// <para>
/// <b>1 本の木として見せるために要る。</b>
/// <c>#ifdef</c> がマクロの定義を切り替えている場合、使う側の 1 行は
/// 既定の構成では空文になり、バリアントでは呼び出し式になる。
/// 呼び出し式のほうはバリアントの木にしか無いので、
/// 既定の木を歩くだけでは見えない。
/// </para>
/// <para>
/// どこへ入れるかは突き合わせのときにしか分からない。
/// そのときの左側の親が、そのまま挿入先である。
/// </para>
/// </remarks>
public readonly record struct NodeInsertion(
    HlslSyntaxNode Parent,
    HlslSyntaxNode Node,
    SymbolCondition Condition);

/// <summary>
/// 2 つの構成で組み立てた構文木を突き合わせた結果。
/// </summary>
/// <remarks>
/// <b>併合できたかどうかを必ず持つ。</b>
/// 突き合わせられなかった箇所があるのに、あるものだけを条件付きで返すと、
/// 「条件を調べた結果ここには何も無い」と読めてしまう。
/// </remarks>
public sealed class ConditionalMergeResult
{
    internal ConditionalMergeResult(
        ImmutableArray<ConditionalNode> conditionalNodes,
        ImmutableArray<Location> unmergedLocations,
        ImmutableArray<NodeInsertion> insertedNodes,
        ImmutableArray<Location> unattributedLocations)
    {
        ConditionalNodes = conditionalNodes;
        UnmergedLocations = unmergedLocations;
        NodeInsertions = insertedNodes;
        UnattributedLocations = unattributedLocations;
    }

    /// <summary>
    /// まとめて有効にした構成で、違いをどのキーワードのものか言えなかった箇所。
    /// </summary>
    /// <remarks>
    /// 違いごとにキーワードを決める突き合わせ (<see cref="ConditionalMerge.Merge(HlslSyntaxNode, HlslSyntaxNode, ImmutableArray{string}, string?, Func{HlslSyntaxNode, bool, string?})"/>)
    /// でだけ埋まる。1 つでもあれば、その構成の条件は信用できない。
    /// </remarks>
    public ImmutableArray<Location> UnattributedLocations { get; }

    /// <summary>
    /// バリアントの木にしか無いノードと、その挿入先。
    /// </summary>
    /// <remarks>
    /// <see cref="ConditionalNodes"/> の部分集合である。
    /// あちらは「条件が何か」を答えるためのもので、こちらは
    /// 「1 本の木として並べるとどこに入るか」を答えるためのものである。
    /// </remarks>
    public ImmutableArray<NodeInsertion> NodeInsertions { get; }

    /// <summary>
    /// 条件付きで存在する部分木の根。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 無条件のノードは含まない。木のほとんどは無条件であり、
    /// それを並べても読み手にも呼び出し側にも意味が無い。
    /// </para>
    /// <para>
    /// <b>子孫は含まない。</b> 根が条件付きなら中身もまるごと同じ条件のもとにある。
    /// あるノードの条件を知りたいときは、親を辿って集めること
    /// (<c>ConditionMap.GetCondition</c> がそれを行う)。
    /// </para>
    /// </remarks>
    public ImmutableArray<ConditionalNode> ConditionalNodes { get; }

    /// <summary>
    /// 突き合わせられなかった箇所。
    /// </summary>
    /// <remarks>
    /// 同じ位置に両方の構成でノードがあるのに、種類が違って対応が取れなかった場所である。
    /// <c>#if</c> が構文の単位をまたいでいると、この形になる。
    /// </remarks>
    public ImmutableArray<Location> UnmergedLocations { get; }

    /// <summary>すべて突き合わせられたかどうか。</summary>
    public bool IsComplete => UnmergedLocations.IsEmpty;
}

/// <summary>
/// 2 つの構成で組み立てた構文木を突き合わせて、ノードごとの出現条件を求める。
/// </summary>
/// <remarks>
/// <para>
/// <b>突き合わせられる根拠はマスクしたテキストにある。</b>
/// 2 つの木は同じテキストから作られており、
/// 同じ場所に書かれたコードは同じ出どころと同じ範囲を持つ。
/// つまり「同じノードかどうか」は位置で判定できる。
/// </para>
/// <para>
/// 木を作り直すのではなく、<b>どのノードがどの条件のもとにあるか</b>を脇に出す。
/// 既存の解析の経路には手を入れず、並行して確かめられるようにするためである。
/// </para>
/// </remarks>
internal static class ConditionalMerge
{
    /// <summary>
    /// 既定の構成と、シンボルを 1 つ有効にした構成を突き合わせる。
    /// </summary>
    /// <param name="baseline">既定の構成で組み立てた木。</param>
    /// <param name="variant">シンボルを有効にした構成で組み立てた木。</param>
    /// <param name="enabledSymbols">有効にしたシンボル。2 つ以上なら論理積として扱う。</param>
    /// <param name="ownFilePath">解析しているファイルのパス。選択肢にする単位を絞るのに使う。</param>
    /// <returns>突き合わせた結果。</returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>両方にあるノード … 無条件 (結果には含めない)</description></item>
    ///   <item><description>既定にだけあるノード … <c>!シンボル</c></description></item>
    ///   <item><description>バリアントにだけあるノード … <c>シンボル</c></description></item>
    /// </list>
    /// </remarks>
    public static ConditionalMergeResult Merge(
        HlslSyntaxNode baseline,
        HlslSyntaxNode variant,
        ImmutableArray<string> enabledSymbols,
        string? ownFilePath = null)
        => Merge(baseline, variant, enabledSymbols, ownFilePath, attribute: null);

    /// <summary>
    /// 既定の構成と、シンボルを有効にした構成を突き合わせる。違いごとに、どのシンボルのものかを決められる。
    /// </summary>
    /// <param name="baseline">既定の構成で組み立てた木。</param>
    /// <param name="variant">シンボルを有効にした構成で組み立てた木。</param>
    /// <param name="enabledSymbols">有効にしたシンボル。</param>
    /// <param name="ownFilePath">解析しているファイルのパス。選択肢にする単位を絞るのに使う。</param>
    /// <param name="attribute">
    /// 片方の木にしか無い部分木の根が、どのシンボルのものかを答える処理。
    /// 2 つ目の引数は、そのノードがバリアントの木のものかどうか。言えなければ <see langword="null"/> を返す。
    /// この処理を渡さなければ、有効にしたシンボルの論理積を条件にする。
    /// </param>
    /// <returns>突き合わせた結果。</returns>
    /// <remarks>
    /// <para>
    /// <b>互いに関係しないキーワードをまとめて有効にした構成のためにある。</b>
    /// その構成の木と既定の木との違いは、どれか 1 つのキーワードが起こしたものである。
    /// 論理積を条件にすると、<c>_A</c> だけで現れるコードに <c>_A かつ _B</c> が付いてしまう。
    /// </para>
    /// <para>
    /// 言えなかった違いは <see cref="ConditionalMergeResult.UnattributedLocations"/> に残る。
    /// 1 つでもあれば、呼び出し側はその構成を使わず、キーワードを 1 つずつ展開し直す。
    /// </para>
    /// </remarks>
    public static ConditionalMergeResult Merge(
        HlslSyntaxNode baseline,
        HlslSyntaxNode variant,
        ImmutableArray<string> enabledSymbols,
        string? ownFilePath,
        Func<HlslSyntaxNode, bool, string?>? attribute)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(variant);

        if (enabledSymbols.IsDefaultOrEmpty)
        {
            throw new ArgumentException("有効にしたシンボルが要ります。", nameof(enabledSymbols));
        }

        // 2 つ以上を同時に有効にした構成では、その論理積が条件になる。
        SymbolCondition enabled = SymbolCondition.Always;

        foreach (string symbol in enabledSymbols)
        {
            enabled = enabled.And(SymbolCondition.Symbol(symbol));
        }

        MergeState state = new(enabled, attribute, ownFilePath);

        Align(baseline, variant, state);

        return state.ToResult();
    }

    /// <summary>突き合わせの途中の状態。</summary>
    /// <param name="enabled">有効にしたシンボルの論理積。</param>
    /// <param name="attribute">違いのシンボルを答える処理。無ければ論理積を条件にする。</param>
    /// <param name="ownFilePath">解析しているファイルのパス。</param>
    private sealed class MergeState(
        SymbolCondition enabled,
        Func<HlslSyntaxNode, bool, string?>? attribute,
        string? ownFilePath)
    {
        /// <summary>解析しているファイルのパス。</summary>
        public string? OwnFilePath { get; } = ownFilePath;

        /// <summary>条件付きのノード。</summary>
        public ImmutableArray<ConditionalNode>.Builder Conditional { get; } = ImmutableArray.CreateBuilder<ConditionalNode>();

        /// <summary>突き合わせられなかった箇所。</summary>
        public ImmutableArray<Location>.Builder Unmerged { get; } = ImmutableArray.CreateBuilder<Location>();

        /// <summary>バリアントにしか無いノードとその挿入先。</summary>
        public ImmutableArray<NodeInsertion>.Builder Inserted { get; } = ImmutableArray.CreateBuilder<NodeInsertion>();

        /// <summary>どのシンボルのものか言えなかった違い。</summary>
        public ImmutableArray<Location>.Builder Unattributed { get; } = ImmutableArray.CreateBuilder<Location>();

        /// <summary>片方の木にしか無い部分木の根に付ける条件を求める。</summary>
        /// <param name="node">部分木の根。</param>
        /// <param name="fromVariant">バリアントの木のノードかどうか。</param>
        /// <returns>付ける条件。</returns>
        public SymbolCondition ConditionFor(HlslSyntaxNode node, bool fromVariant)
        {
            if (attribute is not null)
            {
                if (attribute(node, fromVariant) is { } symbol)
                {
                    return SymbolCondition.Symbol(symbol, fromVariant);
                }

                AddUnattributed(node);
            }

            return fromVariant ? enabled : enabled.Negate();
        }

        /// <summary>どのシンボルのものか言えなかった違いを覚える。</summary>
        /// <param name="node">その違いのノード。</param>
        public void AddUnattributed(HlslSyntaxNode node)
            => Unattributed.Add(node.GetLocation() ?? Location.Create(SourceText.From(string.Empty, "<不明>"), default));

        /// <summary>違いごとにシンボルを決めているかどうか。</summary>
        public bool AttributesEachDifference => attribute is not null;

        /// <summary>結果にまとめる。</summary>
        /// <returns>突き合わせた結果。</returns>
        public ConditionalMergeResult ToResult()
            => new(Conditional.ToImmutable(), Unmerged.ToImmutable(), Inserted.ToImmutable(), Unattributed.ToImmutable());
    }

    /// <summary>
    /// 対応する 2 つのノードの子を並べて突き合わせる。
    /// </summary>
    /// <param name="baseline">既定の構成のノード。</param>
    /// <param name="variant">バリアントのノード。</param>
    /// <param name="state">突き合わせの状態。</param>
    private static void Align(HlslSyntaxNode baseline, HlslSyntaxNode variant, MergeState state)
    {
        // 木のほとんどの場所では、2 つの構成の子は 1 つずつそのまま対応する。
        // その道では入れ物を 1 つも作らずに進む。
        // ここで入れ物を作ると、ノードの数だけ作ることになる。
        using IEnumerator<SyntaxNode> leftWalk = baseline.ChildNodes().GetEnumerator();
        using IEnumerator<SyntaxNode> rightWalk = variant.ChildNodes().GetEnumerator();

        List<HlslSyntaxNode>? left = null;
        List<HlslSyntaxNode>? right = null;

        while (true)
        {
            HlslSyntaxNode? leftChild = NextHlslNode(leftWalk);
            HlslSyntaxNode? rightChild = NextHlslNode(rightWalk);

            if (leftChild is null && rightChild is null)
            {
                // 最後までそのまま対応した。
                return;
            }

            if (leftChild is not null
                && rightChild is not null
                && NodeKey.For(leftChild) == NodeKey.For(rightChild))
            {
                AlignOrChoose(leftChild, rightChild, baseline, state);
                continue;
            }

            // 食い違った。ここから先だけを取り出して、腰を据えて突き合わせる。
            left = Collect(leftChild, leftWalk);
            right = Collect(rightChild, rightWalk);
            break;
        }

        // 片方にしか無い子を飛ばしながら進むために、
        // 「この鍵が相手の何番目にあるか」を先に引けるようにしておく。
        Dictionary<NodeKey, int> leftPositions = BuildPositions(left);
        Dictionary<NodeKey, int> rightPositions = BuildPositions(right);

        int i = 0;
        int j = 0;

        while (i < left.Count && j < right.Count)
        {
            NodeKey leftKey = NodeKey.For(left[i]);
            NodeKey rightKey = NodeKey.For(right[j]);

            if (leftKey == rightKey)
            {
                // 同じノードなので、中をさらに突き合わせる。
                AlignOrChoose(left[i], right[j], baseline, state);
                i++;
                j++;
                continue;
            }

            bool leftMatchable = rightPositions.TryGetValue(leftKey, out int inRight) && inRight >= j;
            bool rightMatchable = leftPositions.TryGetValue(rightKey, out int inLeft) && inLeft >= i;

            if (leftMatchable && !rightMatchable)
            {
                // 左はこの先で対応が取れる。右のこれはバリアントにしか無い。
                AddRoot(right[j], fromVariant: true, baseline, state);
                j++;
                continue;
            }

            if (!leftMatchable && rightMatchable)
            {
                AddRoot(left[i], fromVariant: false, null, state);
                i++;
                continue;
            }

            if (leftMatchable && rightMatchable)
            {
                // どちらもこの先で対応が取れる。間にあるものを近いほうから片付ける。
                if (inRight - j <= inLeft - i)
                {
                    AddRoot(right[j], fromVariant: true, baseline, state);
                    j++;
                }
                else
                {
                    AddRoot(left[i], fromVariant: false, null, state);
                    i++;
                }

                continue;
            }

            // どちらも相手の並びに無い。
            // 同じ場所を占めているなら、構成によって別の形に解釈されており対応を取れない。
            // このファイルに書かれた文・宣言の中でなら、その単位ごと選択肢にしてある
            // (AlignOrChoose)。ここへ落ちるのは、そうできなかった場合である。
            // 場所が違うなら、それぞれの構成にしか無いものが並んでいるだけである
            // (#if と #else に別々のものを書いた形が、ちょうどこれになる)。
            if (ReferenceEquals(left[i].Source, right[j].Source) && left[i].Span == right[j].Span)
            {
                // 位置はファイルごとに意味を持つ。どのファイルの話かを添えて残す。
                if (left[i].GetLocation() is { } location) { state.Unmerged.Add(location); }
                i++;
                j++;
                continue;
            }

            if (left[i].Span.Start <= right[j].Span.Start)
            {
                AddRoot(left[i], fromVariant: false, null, state);
                i++;
            }
            else
            {
                AddRoot(right[j], fromVariant: true, baseline, state);
                j++;
            }
        }

        for (; i < left.Count; i++)
        {
            AddRoot(left[i], fromVariant: false, null, state);
        }

        for (; j < right.Count; j++)
        {
            AddRoot(right[j], fromVariant: true, baseline, state);
        }
    }

    /// <summary>
    /// 対応が取れた 2 つのノードを、中まで突き合わせるか、選択肢として分けるかを決める。
    /// </summary>
    /// <param name="left">既定の構成のノード。</param>
    /// <param name="right">バリアントのノード。</param>
    /// <param name="parent">バリアントのノードを入れる先 (既定の構成の親)。</param>
    /// <param name="state">突き合わせの状態。</param>
    /// <remarks>
    /// <para>
    /// <b>同じ場所の文が、構成によって別のコードになることがある。</b>
    /// 中身が構成で変わるマクロを使っていると、位置も種類も同じまま、トークンだけが入れ替わる。
    /// 位置と種類で対応を取るこの突き合わせは、それを「同じノード」と見なして通してしまい、
    /// 既定の構成の側の形だけで型を決めていた
    /// (<c>#define VEC float2(1, 1)</c> と <c>float4(1, 1, 1, 1)</c> を切り替えると、
    /// <c>VEC.w</c> の誤りがどちらの構成でも出なくなる)。
    /// </para>
    /// <para>
    /// そこで、トークンの並びが違う文・宣言は、中へ降りずに<b>両方を選択肢として残す</b>。
    /// 既定の構成の側にはシンボルの否定を、バリアントの側にはシンボルを付け、
    /// バリアントの側は 1 本の木へ入れるものとして記録する。
    /// ルールはバリアントの側も歩き、その木の文脈で型を決めるので、
    /// 構成ごとの誤りをそれぞれ報告できる。
    /// SuperC (Gazzillo &amp; Grimm, PLDI 2012) が条件付きの選択肢 (static choice) を
    /// 構文木に残すのと同じ考え方を、文・宣言の単位で行うものである。
    /// </para>
    /// <para>
    /// <b>対象は、解析しているファイルに書かれた文・宣言だけである。</b>
    /// 取り込んだヘッダの中はもともと報告しないので、分けても使い道が無い。
    /// トークンを数え直す費用も、ファイル自身のコードに限れば問題にならない
    /// (ヘッダまで広げると、1 ブロックあたり数十万ノードを何度も走査することになる)。
    /// </para>
    /// </remarks>
    private static void AlignOrChoose(
        HlslSyntaxNode left,
        HlslSyntaxNode right,
        HlslSyntaxNode parent,
        MergeState state)
    {
        if (IsChoiceUnit(left, state.OwnFilePath) && !HasSameTokens(left, right))
        {
            SymbolCondition leftCondition = AddRoot(left, fromVariant: false, null, state);
            SymbolCondition rightCondition = AddRoot(right, fromVariant: true, parent, state);

            // 選択肢の両側は同じシンボルで分かれていなければならない。
            // 違うシンボルが付いたなら、この文は 2 つのシンボルで変わっている。
            if (state.AttributesEachDifference && leftCondition != rightCondition.Negate())
            {
                state.AddUnattributed(left);
            }

            return;
        }

        Align(left, right, state);
    }

    /// <summary>
    /// そのノードが、選択肢として分けてよい単位かを判定する。
    /// </summary>
    /// <param name="node">判定するノード。</param>
    /// <param name="ownFilePath">解析しているファイルのパス。</param>
    /// <returns>分けてよければ <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 文と宣言だけを単位にする。式の途中で分けると、
    /// バリアントの側の断片だけを歩くことになり、囲む式の型が決められない。
    /// </para>
    /// </remarks>
    private static bool IsChoiceUnit(HlslSyntaxNode node, string? ownFilePath)
        => ownFilePath is not null
           && node is HlslStatementSyntax or HlslDeclarationSyntax
           && node.FirstToken is { } token
           && !token.IsFromMacroExpansion
           && string.Equals(token.Source.FilePath, ownFilePath, StringComparison.Ordinal);

    /// <summary>
    /// 2 つのノードが、それ自身の部分では同じトークンの並びでできているかを判定する。
    /// </summary>
    /// <param name="left">既定の構成のノード。</param>
    /// <param name="right">バリアントのノード。</param>
    /// <returns>同じ並びであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>中に入れ子になっている文・宣言は見ない。</b>
    /// そこの違いは、その文のところで分ければ足りる。
    /// ブロックや関数まで丸ごと分けると、中の「どの構成にもあるコード」まで条件付きになり、
    /// 同じ指摘が両方の条件で 2 度出る。
    /// <c>if (COND)</c> の条件だけが構成で変わる場合は、
    /// 条件は <c>if</c> 文自身のトークンなので、ここで違いとして見つかる。
    /// </remarks>
    private static bool HasSameTokens(HlslSyntaxNode left, HlslSyntaxNode right)
    {
        using IEnumerator<HlslSyntaxToken> leftWalk = OwnTokens(left).GetEnumerator();
        using IEnumerator<HlslSyntaxToken> rightWalk = OwnTokens(right).GetEnumerator();

        while (true)
        {
            bool hasLeft = leftWalk.MoveNext();
            bool hasRight = rightWalk.MoveNext();

            if (!hasLeft || !hasRight)
            {
                return hasLeft == hasRight;
            }

            if (!string.Equals(leftWalk.Current.Text, rightWalk.Current.Text, StringComparison.Ordinal))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// そのノード自身のトークンを、入れ子の文・宣言を飛ばして返す。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>そのノード自身のトークン。</returns>
    private static IEnumerable<HlslSyntaxToken> OwnTokens(HlslSyntaxNode node)
    {
        foreach (HlslNodeOrToken child in node.ChildNodesAndTokens())
        {
            if (child.Token is { } token)
            {
                yield return token;
                continue;
            }

            if (child.Node is not { } inner || inner is HlslStatementSyntax or HlslDeclarationSyntax)
            {
                continue;
            }

            foreach (HlslSyntaxToken nested in OwnTokens(inner))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// 片方の構成にしか無い部分木を、その根だけ記録する。
    /// </summary>
    /// <param name="node">部分木の根。</param>
    /// <param name="fromVariant">バリアントの木のノードかどうか。</param>
    /// <param name="parent">
    /// 既定の構成の木での挿入先。既定の構成にあるノードなら <see langword="null"/>。
    /// </param>
    /// <param name="state">突き合わせの状態。</param>
    /// <returns>与えた条件。</returns>
    /// <remarks>
    /// <para>
    /// <b>子孫には書かない。</b>
    /// 根が条件付きなら、その中身もまるごと同じ条件のもとにある。
    /// 子孫の条件は親を辿れば分かるので、書けば同じことを繰り返すだけである。
    /// </para>
    /// <para>
    /// これは量の問題である。シンボルが <c>#include</c> を切り替えている場合、
    /// 1 つの部分木が 1 万を超えるノードを持つ。
    /// 全部に書くと、条件の表がブロックの規模と同じ大きさになる。
    /// </para>
    /// </remarks>
    private static SymbolCondition AddRoot(
        HlslSyntaxNode node,
        bool fromVariant,
        HlslSyntaxNode? parent,
        MergeState state)
    {
        SymbolCondition condition = state.ConditionFor(node, fromVariant);

        state.Conditional.Add(new ConditionalNode(node, condition));

        // 既定の構成の木に無いノードは、どこへ入れるかも覚えておく。
        // 挿入先が分かるのは突き合わせている今だけである。
        if (parent is not null)
        {
            state.Inserted.Add(new NodeInsertion(parent, node, condition));
        }

        return condition;
    }

    /// <summary>次の HLSL のノードまで進める。</summary>
    /// <param name="walk">走査中の列挙子。</param>
    /// <returns>次のノード。終わっていれば <see langword="null"/>。</returns>
    private static HlslSyntaxNode? NextHlslNode(IEnumerator<SyntaxNode> walk)
    {
        while (walk.MoveNext())
        {
            if (walk.Current is HlslSyntaxNode hlsl)
            {
                return hlsl;
            }
        }

        return null;
    }

    /// <summary>食い違った位置から先の子を集める。</summary>
    /// <param name="first">食い違った子。無い場合は <see langword="null"/>。</param>
    /// <param name="rest">続きの列挙子。</param>
    /// <returns>集めた並び。</returns>
    private static List<HlslSyntaxNode> Collect(HlslSyntaxNode? first, IEnumerator<SyntaxNode> rest)
    {
        List<HlslSyntaxNode> collected = [];

        if (first is not null)
        {
            collected.Add(first);
        }

        while (NextHlslNode(rest) is { } next)
        {
            collected.Add(next);
        }

        return collected;
    }

    /// <summary>
    /// 鍵から並びの位置を引ける表を作る。
    /// </summary>
    /// <param name="nodes">対象の並び。</param>
    /// <returns>鍵と位置の対応。</returns>
    /// <remarks>
    /// 同じ鍵が 2 度現れた場合は最初の位置を残す。
    /// 位置は「相手の先に現れるか」を見るためだけに使うので、最初のもので足りる。
    /// </remarks>
    private static Dictionary<NodeKey, int> BuildPositions(List<HlslSyntaxNode> nodes)
    {
        Dictionary<NodeKey, int> positions = [];

        for (int i = 0; i < nodes.Count; i++)
        {
            positions.TryAdd(NodeKey.For(nodes[i]), i);
        }

        return positions;
    }

    /// <summary>
    /// 2 つの木のノードを対応づけるための鍵。
    /// </summary>
    /// <param name="Type">ノードの種類。</param>
    /// <param name="Source">出どころのファイル。</param>
    /// <param name="Span">出どころの中での範囲。</param>
    /// <remarks>
    /// <para>
    /// マスクしたテキストは元ファイルと同じ長さ・同じ行構成を持つため、
    /// 同じ場所に書かれたコードは、どちらの構成でも同じ出どころと同じ範囲になる。
    /// </para>
    /// <para>
    /// <b>種類も鍵に含める。</b>
    /// 同じ場所が構成によって別の形に解釈されることがあり、
    /// そのときは対応が取れないものとして扱わなければならない。
    /// </para>
    /// </remarks>
    private readonly record struct NodeKey(Type Type, SourceText? Source, TextSpan Span)
    {
        /// <summary>ノードから鍵を作る。</summary>
        /// <param name="node">対象のノード。</param>
        /// <returns>作った鍵。</returns>
        public static NodeKey For(HlslSyntaxNode node) => new(node.GetType(), node.Source, node.Span);
    }
}
