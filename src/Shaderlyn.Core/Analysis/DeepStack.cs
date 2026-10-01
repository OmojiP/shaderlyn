using System.Runtime.ExceptionServices;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// 深い再帰を伴う処理を、大きなスタックを持つスレッドで実行する。
/// </summary>
/// <remarks>
/// <para>
/// <b>構文木を歩く処理は、木の深さだけ再帰する。</b>
/// <c>1 + 1 + 1 ...</c> のように長い式は、左へ伸びる二項式の連なりになる。
/// 既定のスタック (1 MB) では <b>5000 項ほどで足りなくなる</b>。
/// </para>
/// <para>
/// <b>スタックが尽きると、捕まえる手立てが無い。</b>
/// .NET の <c>StackOverflowException</c> は <c>try</c> で受け取れず、
/// その場でプロセスが終了する。CLI なら解析結果が 1 件も残らず、
/// 言語サーバーなら常駐しているプロセスごと落ちる。
/// </para>
/// <para>
/// <b>敵対的な入力に限った話ではない。</b>
/// 自動生成したシェーダーの定数式は、この長さに届く。
/// </para>
/// <para>
/// 再帰している場所は 1 つではない。構文木を歩く処理、式の型を求める処理、
/// 構成ごとの木を突き合わせる処理がそれぞれ再帰する。
/// 1 か所ずつ書き換えるより、<b>実行するスタックを広げるほうが確実</b>である。
/// </para>
/// <para>
/// 予約するだけなので、実際に使った分しか物理メモリは消費しない。
/// 並列に解析していても、スレッドあたりの予約が増えるだけである。
/// </para>
/// </remarks>
internal static class DeepStack
{
    /// <summary>
    /// 解析 1 件に与えるスタックの大きさ。
    /// </summary>
    /// <remarks>
    /// 既定の 32 倍である。実測では <c>1 + 1 + ...</c> を 2000 項までしか扱えなかったものが、
    /// この大きさでは 6 万項でも通る。
    /// </remarks>
    public const int StackSizeBytes = 32 * 1024 * 1024;

    /// <summary>
    /// 大きなスタックを持つスレッドで実行し、結果を返す。
    /// </summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    /// <param name="work">実行する処理。</param>
    /// <returns><paramref name="work"/> の戻り値。</returns>
    /// <remarks>
    /// 例外は呼び出し元へそのまま投げ直す。スタックトレースは失われない。
    /// </remarks>
    public static T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        T result = default!;
        ExceptionDispatchInfo? failure = null;

        Thread worker = new(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            },
            StackSizeBytes)
        {
            IsBackground = true,
        };

        worker.Start();
        worker.Join();

        failure?.Throw();
        return result;
    }
}
