using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Hlsl.Preprocessing;

namespace Shaderlyn.Cli;

/// <summary>
/// 解析の内訳を測る。
/// </summary>
/// <remarks>
/// <para>
/// <b>速さを直す前に、どこに時間が行っているかを測れるようにする。</b>
/// 合計だけが分かっても、展開・構文解析・ルールのどれを直せばよいかは決まらない。
/// </para>
/// <para>
/// <b>足すのは CPU 時間であり、実時間ではない。</b>
/// ファイルは並列に解析するので、合計は実時間より大きくなる。
/// 比率を見るためのものである。
/// </para>
/// <para>
/// 計測していないときの費用は、null かどうかの判定 1 回だけである。
/// </para>
/// </remarks>
internal sealed class AnalysisTimings : IAnalyzerTimingRecorder, ISyntaxTimingRecorder
{
    private readonly ConcurrentDictionary<string, long> _byAnalyzer = new(StringComparer.Ordinal);
    private long _shaderLabTicks;
    private long _expandTicks;
    private long _unitTicks;
    private long _ruleTicks;
    private long _preprocessTicks;
    private long _parseTicks;
    private long _wireTicks;
    private long _programs;
    private long _variants;
    private long _tokens;

    /// <summary>今の時刻を返す。</summary>
    /// <returns>時刻。<see cref="Add"/> に渡す。</returns>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>ある段の時間を足す。</summary>
    /// <param name="phase">段。</param>
    /// <param name="start"><see cref="Now"/> で取った開始時刻。</param>
    public void Add(AnalysisStep phase, long start)
    {
        long elapsed = Stopwatch.GetTimestamp() - start;

        switch (phase)
        {
            case AnalysisStep.ShaderLab: Interlocked.Add(ref _shaderLabTicks, elapsed); break;
            case AnalysisStep.Expand: Interlocked.Add(ref _expandTicks, elapsed); break;
            case AnalysisStep.Target: Interlocked.Add(ref _unitTicks, elapsed); break;
            case AnalysisStep.Rules: Interlocked.Add(ref _ruleTicks, elapsed); break;
            default: break;
        }
    }

    /// <summary>1 ファイル分の規模を足す。</summary>
    /// <param name="programs">コードブロックの数。</param>
    /// <param name="variants">シンボルのバリアントの数。</param>
    /// <param name="tokens">展開後のトークンの数。</param>
    public void AddScale(int programs, int variants, int tokens)
    {
        Interlocked.Add(ref _programs, programs);
        Interlocked.Add(ref _variants, variants);
        Interlocked.Add(ref _tokens, tokens);
    }

    /// <inheritdoc/>
    public void Record(string analyzer, long elapsedTicks)
        => _byAnalyzer.AddOrUpdate(analyzer, elapsedTicks, (_, v) => v + elapsedTicks);

    /// <inheritdoc/>
    public void RecordSyntaxStep(SyntaxStep phase, long elapsedTicks)
    {
        switch (phase)
        {
            case SyntaxStep.Preprocess: Interlocked.Add(ref _preprocessTicks, elapsedTicks); break;
            case SyntaxStep.Parse: Interlocked.Add(ref _parseTicks, elapsedTicks); break;
            case SyntaxStep.WireParents: Interlocked.Add(ref _wireTicks, elapsedTicks); break;
            default: break;
        }
    }

    /// <summary>測った内訳を人が読む形にする。</summary>
    /// <returns>内訳。</returns>
    /// <param name="includeCache">取り込みのキャッシュ。null なら出さない。</param>
    /// <param name="analysisCache">実行をまたぐ結果のキャッシュ。null なら出さない。</param>
    public string Describe(HlslIncludeCache? includeCache = null, Caching.AnalysisCache? analysisCache = null)
    {
        long total = _shaderLabTicks + _expandTicks + _unitTicks + _ruleTicks;
        StringBuilder text = new();

        text.AppendLine("解析の内訳 (CPU 時間の合計。並列に走るので実時間より大きい)");
        Append(text, "ShaderLab の構文解析", _shaderLabTicks, total);
        Append(text, "展開とセマンティックモデル", _expandTicks, total);

        // 「展開とセマンティックモデル」の中身。ここは対策が段ごとに全く違うので分けて出す。
        // キャッシュで減らせるのは展開だけであり、
        // 構文解析と親の設定を減らすには作る節そのものを減らすしかない。
        if (_preprocessTicks + _parseTicks + _wireTicks > 0)
        {
            Append(text, "  うち 展開", _preprocessTicks, _expandTicks);
            Append(text, "  うち 構文解析", _parseTicks, _expandTicks);
            Append(text, "  うち 親の設定", _wireTicks, _expandTicks);
        }

        Append(text, "解析単位の組み立て", _unitTicks, total);
        Append(text, "ルール", _ruleTicks, total);
        Append(text, "合計", total, total);

        text.AppendLine(CultureInfo.InvariantCulture, $"  コードブロック {_programs:N0} 個 / バリアント {_variants:N0} 個 / 展開後トークン {_tokens:N0} 個");

        if (includeCache is { } cache)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  取り込みの使い回し {cache.Hits:N0} 回 / 展開 {cache.Misses:N0} 回 / 覚えた {cache.Count:N0} 件");
        }

        if (analysisCache is { } results)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  前回の結果の使い回し {results.Hits:N0} 件 / 解析 {results.Misses:N0} 件 / 記録済み {results.LoadedCount:N0} 件");
        }

        if (!_byAnalyzer.IsEmpty)
        {
            text.AppendLine();
            text.AppendLine("重い順のルール (ルールの合計に対する割合)");

            foreach ((string name, long ticks) in _byAnalyzer.OrderByDescending(p => p.Value).Take(10))
            {
                Append(text, name, ticks, _ruleTicks);
            }
        }

        return text.ToString();
    }

    /// <summary>1 行を書き出す。</summary>
    /// <param name="text">書き出し先。</param>
    /// <param name="name">段の名前。</param>
    /// <param name="ticks">時間。</param>
    /// <param name="total">合計。割合を出すのに使う。</param>
    private static void Append(StringBuilder text, string name, long ticks, long total)
    {
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        double share = total == 0 ? 0 : 100.0 * ticks / total;

        text.AppendLine(CultureInfo.InvariantCulture, $"  {name,-22} {ms,9:F0} ms  {share,5:F1}%");
    }
}

/// <summary>解析の段。</summary>
internal enum AnalysisStep
{
    /// <summary>ShaderLab の構文解析。</summary>
    ShaderLab,

    /// <summary>埋め込み HLSL の展開とセマンティックモデルの組み立て。</summary>
    Expand,

    /// <summary>解析対象の組み立て。</summary>
    Target,

    /// <summary>ルールの実行。</summary>
    Rules,
}
