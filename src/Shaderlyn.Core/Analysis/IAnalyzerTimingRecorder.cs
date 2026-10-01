namespace Shaderlyn.Core.Analysis;

/// <summary>
/// アナライザ 1 つ分の実行時間を受け取る先。
/// </summary>
/// <remarks>
/// <para>
/// <b>どのルールが重いかは、測らなければ分からない。</b>
/// 「展開が重いはずだ」と見込んで直しにかかると、外れたときに丸ごと無駄になる。
/// </para>
/// <para>
/// 受け取る側は複数のスレッドから同時に呼ばれる。
/// </para>
/// </remarks>
internal interface IAnalyzerTimingRecorder
{
    /// <summary>実行時間を記録する。</summary>
    /// <param name="analyzer">アナライザの名前。</param>
    /// <param name="elapsedTicks">かかった時間 (Stopwatch の刻み)。</param>
    void Record(string analyzer, long elapsedTicks);
}
