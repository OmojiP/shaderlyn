using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Semantics.Programs;

/// <summary>関数の中の宣言 1 つ。</summary>
/// <param name="Type">宣言した型。</param>
/// <param name="Declarator">宣言子 (<see cref="VariableDeclaratorSyntax"/>) か、仮引数 (<see cref="ParameterSyntax"/>)。</param>
/// <param name="Condition">
/// 使っている位置からこの宣言が見える条件。
/// 宣言そのものの出現条件に、内側の同じ名前の宣言が無いことを掛け合わせたもの
/// (<see cref="LocalScopes.Lookup"/>)。範囲に載せる時点では宣言の出現条件だけを持つ。
/// </param>
internal readonly record struct LocalDeclaration(ResolvedTypeName Type, HlslSyntaxNode Declarator, SymbolCondition Condition);

/// <summary>
/// 関数 1 つの中で、名前がどの宣言を指すかを決める。
/// </summary>
/// <remarks>
/// <para>
/// <b>名前の範囲は波括弧ごとに追う。</b>
/// 使っている位置から外側へたどり、最初に見つかった範囲の宣言を採る。
/// 範囲を作るのは次の 4 つである。
/// </para>
/// <list type="bullet">
///   <item><description>波括弧 (<see cref="BlockStatementSyntax"/>)。使っている文より前の文の宣言が見える</description></item>
///   <item><description><c>switch</c> の本体 (<see cref="SwitchStatementSyntax"/>)。波括弧と同じ扱い</description></item>
///   <item><description>
///     <c>for</c> の初期化 (<see cref="ForStatementSyntax"/>)。その <c>for</c> 文の中で見え、
///     囲む波括弧の中ではその <c>for</c> 文より後でも見える (下記)
///   </description></item>
///   <item><description>関数の仮引数。関数の中のどこからでも見える</description></item>
/// </list>
/// <para>
/// <b>前後は文の順番で比べる。文字位置では比べない。</b>
/// マクロ展開で生まれたトークンはすべて呼び出し位置の範囲を持つので、
/// マクロが宣言と使用の両方を生むと、位置では前後が決まらない。
/// </para>
/// <para>
/// <b>条件付きの宣言は、外側の宣言を確実には隠さない。</b>
/// <c>#ifdef _A</c> の中の <c>float4 a</c> は、<c>_A</c> が無い構成では存在しないので、
/// そこでは外側の <c>float3 a</c> が見える。
/// 使う位置で確実に存在する宣言が見つかるまで、外側の範囲の宣言も候補に加える。
/// 候補の型が割れていれば型を決めず、構成ごとに決める仕組みに任せる
/// (<see cref="ExpressionTypeBinder.EnumerateAssumptions(HlslSyntaxNode, HlslExpressionSyntax, ConditionMap)"/>)。
/// </para>
/// <para>
/// <b>範囲の規則に載らない場所の宣言は、その名前を判断しない。</b>
/// 波括弧を書かない <c>if (x) float a;</c> のような宣言は、どの範囲にも属さない。
/// 位置を決められない宣言があるのに外側の宣言で型を決めると、別の変数の型で判定することになる。
/// </para>
/// <para>
/// <b>同じ範囲に同じ名前の宣言が複数あれば、使う位置に近い (後に書いた) 宣言を採る。</b>
/// 条件付きの宣言なら、それが無い構成ではその前の宣言も候補に残す。
/// </para>
/// <para>
/// <b><c>for</c> の初期化の変数は、囲む波括弧の範囲へ漏れる。</b>
/// Unity (fxc) が通すのはこの読み方である。ループの後でも見え、
/// 同じ波括弧の前の同じ名前の宣言より優先する (警告 X3078 "most recent declaration will be used")。
/// 漏れるのは囲む波括弧までで、その外では見えない。
/// DXC の <c>-HV 2021</c> は C++ と同じく <c>for</c> 文の中に閉じ込めるが、Unity の既定のコンパイルに合わせる。
/// </para>
/// <para>
/// 波括弧を書かない <c>for</c> / <c>if</c> / <c>while</c> の本体にある <c>for</c> の変数は、
/// 外の文の後で使うと fxc が内部エラーになる。その位置では名前を判断しない。
/// </para>
/// </remarks>
internal sealed class LocalScopes
{
    /// <summary>範囲の中の宣言 1 つ。</summary>
    /// <param name="Statement">
    /// 範囲の中での文の順番。<c>for</c> 文そのものの範囲での初期化は -1、仮引数は <see cref="int.MinValue"/>。
    /// </param>
    /// <param name="Declarator">同じ文の中での宣言子の順番 (<c>float a, b;</c> の <c>b</c> は 1)。</param>
    /// <param name="Declaration">宣言。</param>
    private readonly record struct Entry(int Statement, int Declarator, LocalDeclaration Declaration);

    /// <summary>範囲を作るノードごとの、名前から宣言への対応。</summary>
    private readonly Dictionary<SyntaxNode, Dictionary<string, List<Entry>>> _scopes = [];

    /// <summary>範囲の中での、文の順番。</summary>
    private readonly Dictionary<SyntaxNode, int> _statementIndex = [];

    /// <summary>
    /// 範囲の直下の文の中で、波括弧を挟まずに入れ子になった <c>for</c> 文の初期化で宣言された名前と、その文の順番。
    /// 文の後で使うと fxc が内部エラーになるので、判断しない。
    /// </summary>
    private readonly Dictionary<SyntaxNode, Dictionary<string, List<int>>> _loopNames = [];

    /// <summary>範囲の規則に載らない場所で宣言された名前。</summary>
    private readonly HashSet<string> _untracked = new(StringComparer.Ordinal);

    /// <summary>引いた結果。同じ識別子を何度も引く。</summary>
    private readonly Dictionary<IdentifierExpressionSyntax, ImmutableArray<LocalDeclaration>?> _cache = [];

    private readonly ConditionMap _conditions;

    /// <summary>関数の中の宣言を集める。</summary>
    /// <param name="function">対象の関数。</param>
    /// <param name="conditions">出現条件の索引。条件付きの宣言が外側を隠すかどうかを決めるのに使う。</param>
    public LocalScopes(FunctionDeclarationSyntax function, ConditionMap conditions)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(conditions);

        _conditions = conditions;

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            Add(
                function,
                parameter.Name,
                new Entry(
                    int.MinValue,
                    0,
                    new LocalDeclaration(
                        new ResolvedTypeName(
                            parameter.Type.Name,
                            !parameter.ArrayRankTokens.IsEmpty,
                            ExpressionTypeBinder.GetTemplateArgument(parameter.Type),
                            parameter.ArrayDimensions),
                        parameter,
                        conditions.GetCondition(parameter))));
        }

        HashSet<VariableDeclarationSyntax> tracked = [];

        foreach (SyntaxNode node in function.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case BlockStatementSyntax block:
                    AddStatements(block, block.Statements, tracked);
                    break;

                case SwitchStatementSyntax body:
                    AddStatements(body, body.Statements, tracked);
                    break;

                case ForStatementSyntax loop when loop.Initializer is VariableDeclarationSyntax initializer:
                    AddDeclaration(loop, -1, initializer, tracked);
                    break;

                default:
                    break;
            }
        }

        // 範囲に載せられなかった宣言。構造体のフィールドは変数ではないので除く。
        foreach (VariableDeclarationSyntax declaration in function.DescendantNodesAndSelf().OfType<VariableDeclarationSyntax>())
        {
            if (tracked.Contains(declaration)
                || declaration.Ancestors().TakeWhile(a => a is not FunctionDeclarationSyntax).Any(a => a is StructDeclarationSyntax))
            {
                continue;
            }

            foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
            {
                _untracked.Add(declarator.Name);
            }
        }

        HasVaryingTypes = _scopes.Values
            .SelectMany(byName => byName)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Any(group => group.SelectMany(pair => pair.Value).Select(e => e.Declaration.Type).Distinct().Count() > 1);
    }

    /// <summary>
    /// 同じ名前を違う型で宣言している箇所があるかどうか。
    /// </summary>
    /// <remarks>
    /// 無ければ、構成ごとに型を決める仕組みを動かす必要が無い。
    /// 範囲が違えば別の変数なので、実際に型が割れるとは限らない。早抜けのための目安である。
    /// </remarks>
    public bool HasVaryingTypes { get; }

    /// <summary>識別子から見える宣言を返す。</summary>
    /// <param name="identifier">使っている識別子。</param>
    /// <returns>
    /// 見える宣言。関数の中に宣言が無ければ空 (関数の外を引けばよい)。
    /// 範囲の規則に載らない宣言があって判断できない場合は <see langword="null"/>。
    /// </returns>
    public ImmutableArray<LocalDeclaration>? Lookup(IdentifierExpressionSyntax identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (!_cache.TryGetValue(identifier, out ImmutableArray<LocalDeclaration>? found))
        {
            found = _cache[identifier] = Find(identifier);
        }

        return found;
    }

    private ImmutableArray<LocalDeclaration>? Find(IdentifierExpressionSyntax identifier)
    {
        string name = identifier.Name;

        if (_untracked.Contains(name))
        {
            return null;
        }

        ImmutableArray<LocalDeclaration>.Builder found = ImmutableArray.CreateBuilder<LocalDeclaration>();
        SymbolCondition use = _conditions.GetCondition(identifier);
        SyntaxNode child = identifier;

        // 内側の範囲で同じ名前の宣言が存在する構成。そこでは外側の宣言は隠れている。
        SymbolCondition hidden = SymbolCondition.Never;

        foreach (SyntaxNode ancestor in identifier.Ancestors())
        {
            // 前の文の、波括弧を挟まずに入れ子になった for の変数。fxc でも正しく扱えない。
            if (_loopNames.TryGetValue(ancestor, out Dictionary<string, List<int>>? loops)
                && loops.TryGetValue(name, out List<int>? starts)
                && _statementIndex.TryGetValue(child, out int current)
                && starts.Exists(start => start < current))
            {
                return null;
            }

            if (_scopes.TryGetValue(ancestor, out Dictionary<string, List<Entry>>? byName)
                && byName.TryGetValue(name, out List<Entry>? entries))
            {
                (int position, int declarator) = PositionOf(ancestor, child, identifier);

                // 使う位置に近い宣言から見る。確実に存在する宣言があれば、それより前と外側の同じ名前は隠れている。
                IEnumerable<Entry> visible = entries
                    .Where(entry => entry.Statement < position
                                    || (entry.Statement == position && entry.Declarator <= declarator))
                    .OrderByDescending(entry => entry.Statement)
                    .ThenByDescending(entry => entry.Declarator);

                foreach (Entry entry in visible)
                {
                    SymbolCondition declared = entry.Declaration.Condition;

                    found.Add(entry.Declaration with
                    {
                        Condition = hidden.IsNever ? declared : declared.And(hidden.Negate()),
                    });

                    if (IsCertainlyPresent(declared, use))
                    {
                        return found.ToImmutable();
                    }

                    hidden = hidden.Or(declared);
                }
            }

            if (ancestor is FunctionDeclarationSyntax)
            {
                break;
            }

            child = ancestor;
        }

        return found.ToImmutable();
    }

    /// <summary>範囲の中で、使っている位置がどこにあたるかを求める。</summary>
    /// <param name="scope">範囲を作るノード。</param>
    /// <param name="child">範囲の直下にある、使っている位置を含むノード。</param>
    /// <param name="identifier">使っている識別子。</param>
    /// <returns>
    /// 文の順番と、同じ文の中での宣言子の順番。
    /// この位置より前の宣言が見える。同じ文なら、その宣言子までが見える。
    /// </returns>
    /// <remarks>
    /// 同じ宣言子を含むのは、C と同じく宣言した名前がその初期化子の中で既に見えるためである
    /// (<c>float a = a;</c> の右の <c>a</c> は、いま宣言した <c>a</c>)。
    /// </remarks>
    private (int Statement, int Declarator) PositionOf(SyntaxNode scope, SyntaxNode child, IdentifierExpressionSyntax identifier)
    {
        int statement = scope switch
        {
            FunctionDeclarationSyntax => int.MaxValue,
            ForStatementSyntax loop => ReferenceEquals(loop.Initializer, child) ? -1 : int.MaxValue,

            // 本体の文でなければ (switch の対象の式など)、この範囲の宣言はまだ見えない。
            _ => _statementIndex.TryGetValue(child, out int index) ? index : -1,
        };

        return (statement, statement is int.MaxValue ? int.MaxValue : DeclaratorOf(identifier, child));
    }

    /// <summary>識別子を含む宣言子が、その文の中で何番目かを求める。</summary>
    /// <param name="identifier">使っている識別子。</param>
    /// <param name="statement">識別子を含む文。</param>
    /// <returns>宣言子の順番。宣言子の中に無ければ <see cref="int.MaxValue"/>。</returns>
    private static int DeclaratorOf(IdentifierExpressionSyntax identifier, SyntaxNode statement)
    {
        foreach (SyntaxNode ancestor in identifier.Ancestors())
        {
            if (ReferenceEquals(ancestor, statement))
            {
                break;
            }

            if (ancestor is VariableDeclaratorSyntax declarator
                && declarator.Parent is VariableDeclarationSyntax declaration)
            {
                int index = 0;

                foreach (VariableDeclaratorSyntax candidate in declaration.Variables)
                {
                    if (ReferenceEquals(candidate, declarator))
                    {
                        return index;
                    }

                    index++;
                }
            }
        }

        return int.MaxValue;
    }

    /// <summary>その宣言が、使う位置で確実に存在するかを判定する。</summary>
    /// <param name="declared">宣言の出現条件。</param>
    /// <param name="use">使う位置の出現条件。</param>
    /// <returns>確実に存在するなら <see langword="true"/>。分からなければ <see langword="false"/>。</returns>
    private bool IsCertainlyPresent(SymbolCondition declared, SymbolCondition use)
    {
        if (declared.IsAlways)
        {
            return true;
        }

        return !declared.IsUnknown
               && !use.IsUnknown
               && !_conditions.IsPossible(use.And(declared.Negate()));
    }

    private void AddStatements(SyntaxNode scope, ImmutableArray<HlslStatementSyntax> statements, HashSet<VariableDeclarationSyntax> tracked)
    {
        for (int i = 0; i < statements.Length; i++)
        {
            _statementIndex[statements[i]] = i;

            if (statements[i] is LocalDeclarationStatementSyntax { Declaration: VariableDeclarationSyntax declaration })
            {
                AddDeclaration(scope, i, declaration, tracked);
            }
            else if (statements[i] is ForStatementSyntax { Initializer: VariableDeclarationSyntax initializer })
            {
                // 初期化の変数は囲む範囲へ漏れる。その for 文より後の文から見える。
                AddDeclaration(scope, i, initializer, tracked);
            }

            // 波括弧を挟まずに入れ子になった for の変数は、この文の後では判断しない。
            IEnumerable<ForStatementSyntax> nestedLoops = statements[i]
                .DescendantNodesAndSelf()
                .OfType<ForStatementSyntax>()
                .Where(loop => !ReferenceEquals(loop, statements[i]));

            foreach (ForStatementSyntax nested in nestedLoops)
            {
                if (nested.Initializer is VariableDeclarationSyntax nestedInitializer
                    && !nested.Ancestors()
                        .TakeWhile(ancestor => !ReferenceEquals(ancestor, statements[i]))
                        .Append(statements[i])
                        .Any(ancestor => ancestor is BlockStatementSyntax or SwitchStatementSyntax))
                {
                    AddLoopNames(scope, i, nestedInitializer);
                }
            }
        }
    }

    private void AddDeclaration(SyntaxNode scope, int statement, VariableDeclarationSyntax declaration, HashSet<VariableDeclarationSyntax> tracked)
    {
        tracked.Add(declaration);

        int index = 0;

        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
        {
            Add(
                scope,
                variable.Name,
                new Entry(
                    statement,
                    index++,
                    new LocalDeclaration(
                        new ResolvedTypeName(
                            declaration.Type.Name,
                            variable.IsArray,
                            ExpressionTypeBinder.GetTemplateArgument(declaration.Type),
                            variable.ArrayDimensions),
                        variable,
                        _conditions.GetCondition(declaration))));
        }
    }

    private void AddLoopNames(SyntaxNode scope, int statement, VariableDeclarationSyntax initializer)
    {
        if (!_loopNames.TryGetValue(scope, out Dictionary<string, List<int>>? byName))
        {
            byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            _loopNames[scope] = byName;
        }

        foreach (VariableDeclaratorSyntax variable in initializer.Variables)
        {
            if (!byName.TryGetValue(variable.Name, out List<int>? starts))
            {
                starts = [];
                byName[variable.Name] = starts;
            }

            starts.Add(statement);
        }
    }

    private void Add(SyntaxNode scope, string name, Entry entry)
    {
        if (!_scopes.TryGetValue(scope, out Dictionary<string, List<Entry>>? byName))
        {
            byName = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            _scopes[scope] = byName;
        }

        if (!byName.TryGetValue(name, out List<Entry>? entries))
        {
            entries = [];
            byName[name] = entries;
        }

        entries.Add(entry);
    }
}
