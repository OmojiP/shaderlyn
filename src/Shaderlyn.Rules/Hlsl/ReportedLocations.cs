using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules;

/// <summary>
/// 報告した位置の集合。同じ位置を木ごとに重ねて報告しないために使う。
/// </summary>
/// <remarks>
/// <b>位置はファイルごとに数えるので、ファイルの名前と組にして覚える。</b>
/// 文字位置だけで覚えると、取り込んだヘッダの指摘と、解析しているファイルの同じ文字位置の指摘を同じものとみなし、
/// 後の方を報告しない (見逃す)。
/// </remarks>
internal sealed class ReportedLocations
{
    private readonly HashSet<(string File, int Start)> _locations = [];

    /// <summary>位置を覚える。</summary>
    /// <param name="location">報告する位置。</param>
    /// <returns>初めての位置なら <see langword="true"/>。</returns>
    public bool Add(Location location) => _locations.Add(Key(location));

    /// <summary>その位置を報告したかを判定する。</summary>
    /// <param name="location">位置。</param>
    /// <returns>報告していれば <see langword="true"/>。</returns>
    public bool Contains(Location location) => _locations.Contains(Key(location));

    private static (string File, int Start) Key(Location location) => (location.FilePath, location.Span.Start);
}
