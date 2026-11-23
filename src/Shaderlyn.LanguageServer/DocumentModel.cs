using System.Collections.Immutable;
using Shaderlyn.Cli.Environments;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 解析単位とセマンティックモデル (<see cref="AnalysisSession.Prepare"/>) と、モデルを組み立てたときのシンボル。
/// </summary>
/// <param name="File">環境が組み立てた解析単位とモデル。</param>
/// <param name="Symbols">定義済みにしたシンボル (このファイルが宣言したシンボルを除いたもの)。ルールを当てる実行器を選ぶのに使う。</param>
internal sealed record PreparedModel(PreparedFile File, ImmutableArray<string> Symbols)
{
    /// <summary>セマンティックモデル。構文だけを読む環境 (Godot など) では <see langword="null"/>。</summary>
    public ShaderCompilation? Compilation => File.Compilation;
}

/// <summary>
/// 開いている文書の、あるバージョンのセマンティックモデル。指摘・効いていない範囲・ホバーなどで共有する。
/// </summary>
/// <remarks>
/// <para>
/// <b>組み立ては 1 度だけ、受信のループの外で行う。</b>
/// 組み立ては大きいシェーダーで 1 秒ほどかかる (URP の Lit.shader)。受信のループで組み立てると、
/// その間に届いた編集の通知が詰まり、入力のたびに出る指摘 (構文の段) まで遅れる。
/// </para>
/// <para>
/// <b>同じ文書の組み立ては 1 つずつ行い、始める前に新しいバージョンが来ていたら組み立てない。</b>
/// 打鍵のたびに補完などを求められると、バージョンごとの組み立てが同時に走って CPU を取り合い、
/// どの応答も遅れる (URP の Lit.shader で、5 回の編集のそれぞれのホバーが 3.5 秒)。
/// 組み立てなかったバージョンへの要求には、空で答える (<see cref="SupersededException"/>)。
/// </para>
/// <para>
/// <b>使うのは 1 つずつにする (<see cref="UseAsync"/>)。</b>
/// モデルは中で覚える索引を普通の辞書に入れているので、ルールとホバーが同時に使うと壊れる。
/// </para>
/// </remarks>
internal sealed class DocumentModel
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>組み立てを始める。</summary>
    /// <param name="version">文書のバージョン。</param>
    /// <param name="session">組み立てる解析の単位。</param>
    /// <param name="definedSymbols">利用者が定義済みにしたシンボル。</param>
    /// <param name="build">組み立てる処理。スレッドプールで走らせる。</param>
    /// <param name="previous">同じ文書の前のモデル。その組み立てが終わってから始める。</param>
    public DocumentModel(
        int version,
        AnalysisSession session,
        ImmutableArray<string> definedSymbols,
        Func<PreparedModel> build,
        DocumentModel? previous)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(build);

        Version = version;
        Session = session;
        DefinedSymbols = definedSymbols;

        // 前のモデルは、このモデルに置き換えられた。まだ組み立てを始めていなければ、始めない。
        previous?.Supersede();
        Task waitPrevious = previous?.Model ?? Task.CompletedTask;

        Model = Task.Run(async () =>
        {
            try
            {
                await waitPrevious.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 前の組み立ての失敗は、前のモデルを使う側が受け取る。
            }

            return Volatile.Read(ref _superseded) ? throw new SupersededException() : build();
        });
    }

    private bool _superseded;
    private bool _required;

    /// <summary>新しいモデルに置き換えられたことを記録する。組み立てを始めていなければ、始めない。</summary>
    /// <remarks>受信のループから呼ぶ (<see cref="Require"/> と同じスレッド)。</remarks>
    private void Supersede()
    {
        if (!_required)
        {
            Volatile.Write(ref _superseded, true);
        }
    }

    /// <summary>
    /// 置き換えられても組み立てる。指摘を出す解析が待っている場合に呼ぶ。受信のループから呼ぶ。
    /// </summary>
    /// <remarks>
    /// 指摘を出す解析は、次の編集が届くと打ち切られる (そのときは組み立てなくてよい)。
    /// 設定ファイルの書き換えなどでモデルだけが置き換えられた場合は打ち切られないので、組み立てないと指摘が出ないままになる。
    /// </remarks>
    public void Require() => _required = true;

    /// <summary>文書のバージョン。</summary>
    public int Version { get; }

    /// <summary>組み立てた解析の単位。設定ファイルを書き換えると別の単位になる。</summary>
    public AnalysisSession Session { get; }

    /// <summary>利用者が定義済みにしたシンボル。</summary>
    public ImmutableArray<string> DefinedSymbols { get; }

    /// <summary>組み立ての結果。</summary>
    public Task<PreparedModel> Model { get; }

    /// <summary>同じバージョン・同じ解析の単位・同じシンボルで組み立てたものか。</summary>
    /// <param name="version">文書のバージョン。</param>
    /// <param name="session">解析の単位。</param>
    /// <param name="definedSymbols">利用者が定義済みにしたシンボル。</param>
    /// <returns>使い回せるなら <see langword="true"/>。</returns>
    public bool Matches(int version, AnalysisSession session, ImmutableArray<string> definedSymbols)
        => Version == version && ReferenceEquals(Session, session) && DefinedSymbols.SequenceEqual(definedSymbols, StringComparer.Ordinal);

    /// <summary>組み立てを待ち、ほかに使っているものがなくなってから使う。</summary>
    /// <typeparam name="T">結果の型。</typeparam>
    /// <param name="use">モデルを使う処理。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>処理の結果。</returns>
    public async Task<T> UseAsync<T>(Func<PreparedModel, T> use, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(use);

        PreparedModel model = await Model.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return use(model);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// 組み立てを始める前に、同じ文書の新しいモデルに置き換えられた。このモデルへの要求には空で答える。
/// </summary>
internal sealed class SupersededException : OperationCanceledException
{
    /// <summary>例外を作る。</summary>
    public SupersededException()
        : base("新しいバージョンのモデルに置き換えられました。")
    {
    }
}
