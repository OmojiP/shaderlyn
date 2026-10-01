namespace Shaderlyn.Core.Analysis;

/// <summary>
/// 構文木を 1 本作るまでの段。
/// </summary>
public enum SyntaxStep
{
    /// <summary>マクロ展開と <c>#include</c> の取り込み。</summary>
    Preprocess,

    /// <summary>展開したトークン列からの構文解析。</summary>
    Parse,

    /// <summary>作った節に親を結ぶ処理。</summary>
    WireParents,
}

/// <summary>
/// 構文木を作る段ごとの時間を受け取る先。
/// </summary>
/// <remarks>
/// <para>
/// <b>「展開とセマンティックモデル」で一括りにすると、直す場所が決まらない。</b>
/// 展開・構文解析・親の設定は対策が全く違う。
/// キャッシュで減らせるのは展開だけであり、
/// 構文解析と親の設定を減らすには作る節そのものを減らすしかない。
/// </para>
/// <para>
/// <b>この内訳は残しておく。</b>
/// 段階を 1 つ進めるたびに測り直す必要があり、
/// そのたびに一時的な計測を仕込んでは外すと、
/// 測り方が揃わず前回の数字と比べられない。
/// </para>
/// <para>
/// 受け取る側は複数のスレッドから同時に呼ばれる。
/// </para>
/// </remarks>
public interface ISyntaxTimingRecorder
{
    /// <summary>ある段の時間を記録する。</summary>
    /// <param name="phase">段。</param>
    /// <param name="elapsedTicks">かかった時間 (Stopwatch の刻み)。</param>
    void RecordSyntaxStep(SyntaxStep phase, long elapsedTicks);
}
