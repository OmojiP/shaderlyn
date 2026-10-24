using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Rules;

/// <summary>
/// 報告した位置の集合。同じ位置を木ごとに重ねて報告しないために使う。
/// </summary>
/// <remarks>
/// <para>
/// <b>位置はファイルごとに数えるので、ファイルの名前と組にして覚える。</b>
/// 文字位置だけで覚えると、取り込んだヘッダの指摘と、解析しているファイルの同じ文字位置の指摘を同じものとみなし、
/// 後の方を報告しない (見逃す)。
/// </para>
/// <para>
/// <b>構成の条件が付く報告は、木をまたいで条件の和をとる</b> (<see cref="AddConditional"/>)。
/// 最初の木の報告だけを残すと、ほかの木の構成の分が条件から落ちる。
/// 集めた報告は解析の終わりに <see cref="BuildConditional"/> で出す。
/// </para>
/// </remarks>
internal sealed class ReportedLocations
{
    private readonly HashSet<(string File, int Start)> _locations = [];
    private readonly HashSet<(string File, int Start)> _conditionalLocations = [];
    private readonly ConditionalReports? _conditional;

    /// <summary>位置だけを覚える。</summary>
    public ReportedLocations()
    {
    }

    /// <summary>位置と、構成の条件が付く報告を覚える。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    public ReportedLocations(ShaderCompilation compilation) => _conditional = new ConditionalReports(compilation);

    /// <summary>位置を覚える。</summary>
    /// <param name="location">報告する位置。</param>
    /// <returns>初めての位置なら <see langword="true"/>。</returns>
    public bool Add(Location location) => _locations.Add(Key(location));

    /// <summary>その位置を報告したかを判定する。</summary>
    /// <param name="location">位置。</param>
    /// <returns>報告していれば <see langword="true"/>。</returns>
    public bool Contains(Location location) => _locations.Contains(Key(location));

    /// <summary>
    /// 構成の条件が付く報告を足す (<see cref="ConditionalReports"/>)。
    /// </summary>
    /// <param name="program">ノードのある木。</param>
    /// <param name="node">報告の元になったノード。</param>
    /// <param name="when">報告する構成。</param>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="location">報告の位置。</param>
    /// <param name="key">同じ位置の報告をまとめる鍵 (引数のうち条件以外のもの)。</param>
    /// <param name="arguments">まとめた条件から、メッセージの引数を作る手続き。</param>
    /// <param name="withTreeConfiguration">木が表す構成を掛けるか (<see cref="ConditionalReports"/>)。</param>
    /// <returns>足したら <see langword="true"/>。</returns>
    /// <remarks>
    /// その位置に条件の付かない報告を先に出していれば、足さない。
    /// 足したら位置も覚えるので、ほかの木から条件の付かない報告を重ねない。
    /// </remarks>
    public bool AddConditional(
        AnalyzedProgram program,
        ShaderSyntaxNode node,
        SymbolCondition when,
        DiagnosticDescriptor descriptor,
        Location location,
        string key,
        Func<SymbolCondition, object?[]> arguments,
        bool withTreeConfiguration = true)
    {
        if (_conditional is null)
        {
            throw new InvalidOperationException("構成の条件が付く報告は、セマンティックモデルを渡して作ったときだけ集められる。");
        }

        if (Contains(location) && !_conditionalLocations.Contains(Key(location)))
        {
            return false;
        }

        if (!_conditional.Add(program, node, when, descriptor, location, key, arguments, withTreeConfiguration))
        {
            return false;
        }

        _conditionalLocations.Add(Key(location));
        Add(location);
        return true;
    }

    /// <summary>集めた構成の条件が付く報告を作る。</summary>
    /// <returns>診断。</returns>
    public IEnumerable<Diagnostic> BuildConditional() => _conditional?.Build() ?? [];

    private static (string File, int Start) Key(Location location) => (location.FilePath, location.Span.Start);
}
