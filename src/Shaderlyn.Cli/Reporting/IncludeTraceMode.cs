namespace Shaderlyn.Cli.Reporting;

/// <summary>
/// ヘッダの中の指摘に、どのシェーダーから取り込んだかの経路 (<see cref="Core.Diagnostics.IncludeRoute"/>) をどこまで出すか
/// (<c>--include-trace</c>、設定ファイルの <c>report: include-trace</c>)。
/// </summary>
public enum IncludeTraceMode
{
    /// <summary>出さない。</summary>
    None,

    /// <summary>最初の 1 本と、ほかに何本あるかを出す (既定)。</summary>
    First,

    /// <summary>すべて出す。</summary>
    All,
}

/// <summary><see cref="IncludeTraceMode"/> の名前 (<c>none</c> / <c>first</c> / <c>all</c>) を読む。</summary>
internal static class IncludeTraceModes
{
    /// <summary>名前を読む。</summary>
    /// <param name="value">名前。</param>
    /// <returns>読めた値。読めなければ <see langword="null"/>。</returns>
    public static IncludeTraceMode? TryParse(string? value) => value switch
    {
        "none" => IncludeTraceMode.None,
        "first" => IncludeTraceMode.First,
        "all" => IncludeTraceMode.All,
        _ => null,
    };
}
