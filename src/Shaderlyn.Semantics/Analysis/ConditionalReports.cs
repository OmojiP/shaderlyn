using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Semantics.Analysis;

/// <summary>
/// 構成の条件が付く報告を集め、同じ位置の報告は条件の和をとって 1 件にする。
/// </summary>
/// <remarks>
/// <para>
/// <b>条件の掛け方の順序をここに閉じ込める。</b> 組み込みのルールで何度も誤検出を出した手順である。
/// </para>
/// <list type="number">
///   <item><description>報告する構成 = ノードの条件 × その木が表す構成 (<see cref="ShaderCompilation.GetTreeConfiguration"/>) × 渡した条件</description></item>
///   <item><description>ありえない構成と、<c>#error</c> に到達する構成を除く (<see cref="ShaderCompilation.IsPossibleIn"/>)。残らなければ報告しない</description></item>
///   <item><description>条件を追えなかったら報告しない</description></item>
///   <item><description>同じルール・同じ位置・同じ引数の報告は、木 (Pass と構成) をまたいで条件の和をとる</description></item>
///   <item><description>読める形に短くして (<see cref="Conditional.ConditionMap.Simplify"/>) 報告する</description></item>
/// </list>
/// <para>
/// 木ごとに最初の報告だけを残すと、別の木の構成の分が条件から落ちる。和をとるので、解析の終わりにまとめて出す (<see cref="Build"/>)。
/// </para>
/// </remarks>
public sealed class ConditionalReports
{
    private readonly ShaderCompilation _compilation;
    private readonly Dictionary<(string Id, string FilePath, TextSpan Span, string Key), Entry> _entries = [];
    private readonly List<(string Id, string FilePath, TextSpan Span, string Key)> _order = [];

    /// <summary>集める先を作る。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    public ConditionalReports(ShaderCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
    }

    /// <summary>
    /// 構成の条件が付く報告を足す。メッセージの <c>{0}</c> に「構成：○○ のとき、」(どの構成でもなら空) が入る。
    /// </summary>
    /// <param name="program">ノードのある木。</param>
    /// <param name="node">報告の元になったノード。この位置の条件を掛ける。</param>
    /// <param name="when">報告する構成。ノードがあればどの構成でも報告するなら <see cref="SymbolCondition.Always"/>。</param>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="location">報告の位置。</param>
    /// <param name="arguments">メッセージの <c>{1}</c> 以降に入る引数。</param>
    /// <returns>報告する構成が残ったら <see langword="true"/>。</returns>
    public bool Add(
        AnalyzedProgram program,
        ShaderSyntaxNode node,
        SymbolCondition when,
        DiagnosticDescriptor descriptor,
        Location location,
        params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        object?[] copied = [.. arguments];
        return Add(
            program,
            node,
            when,
            descriptor,
            location,
            string.Join('\u0001', copied.Select(a => a?.ToString() ?? string.Empty)),
            condition => [Describe(condition), .. copied]);
    }

    /// <summary>
    /// 構成の条件が付く報告を足す。メッセージの引数は、まとめた条件から作る。
    /// </summary>
    /// <param name="program">ノードのある木。</param>
    /// <param name="node">報告の元になったノード。この位置の条件を掛ける。</param>
    /// <param name="when">報告する構成。</param>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="location">報告の位置。</param>
    /// <param name="key">同じ位置の報告をまとめる鍵 (引数のうち条件以外のもの)。鍵の違う報告はまとめない。</param>
    /// <param name="arguments">まとめて短くした条件から、メッセージの引数を作る手続き。同じ鍵では最初に渡したものを使う。</param>
    /// <returns>報告する構成が残ったら <see langword="true"/>。</returns>
    public bool Add(
        AnalyzedProgram program,
        ShaderSyntaxNode node,
        SymbolCondition when,
        DiagnosticDescriptor descriptor,
        Location location,
        string key,
        Func<SymbolCondition, object?[]> arguments)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(arguments);

        SymbolCondition condition = _compilation.GetEffectiveCondition(node, program)
            .And(_compilation.GetTreeConfiguration(program))
            .And(when);

        if (condition.IsUnknown || !_compilation.IsPossibleIn(program, condition))
        {
            return false;
        }

        (string, string, TextSpan, string) entryKey = (descriptor.Id, location.FilePath, location.Span, key);

        if (_entries.TryGetValue(entryKey, out Entry? entry))
        {
            entry.Condition = entry.Condition.Or(condition);
        }
        else
        {
            _entries[entryKey] = new Entry(descriptor, location, arguments) { Condition = condition };
            _order.Add(entryKey);
        }

        return true;
    }

    /// <summary>集めた報告を、足した順に作る。</summary>
    /// <returns>診断。</returns>
    public IEnumerable<Diagnostic> Build()
    {
        Conditional.ConditionMap map = _compilation.GetConditionMap();

        foreach ((string, string, TextSpan, string) key in _order)
        {
            Entry entry = _entries[key];
            SymbolCondition condition = map.Simplify(entry.Condition);

            if (condition.IsNever || condition.IsUnknown)
            {
                continue;
            }

            yield return Diagnostic.Create(entry.Descriptor, entry.Location, entry.Arguments(condition));
        }
    }

    /// <summary>条件を、メッセージの頭に置く形にする。</summary>
    /// <param name="condition">短くした条件。</param>
    /// <returns>「構成：○○ のとき、」。どの構成でもなら空。</returns>
    public static string Describe(SymbolCondition condition)
        => condition.IsAlways ? string.Empty : $"構成：{condition} のとき、";

    /// <summary>1 件分。条件は足すたびに和をとる。</summary>
    private sealed class Entry(DiagnosticDescriptor descriptor, Location location, Func<SymbolCondition, object?[]> arguments)
    {
        public DiagnosticDescriptor Descriptor { get; } = descriptor;

        public Location Location { get; } = location;

        public Func<SymbolCondition, object?[]> Arguments { get; } = arguments;

        public SymbolCondition Condition { get; set; } = SymbolCondition.Never;
    }
}
