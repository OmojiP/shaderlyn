namespace Shaderlyn.Cli;

/// <summary>
/// プロセスの終了コード。
/// </summary>
/// <remarks>
/// <para>
/// 「指摘が見つかった (1)」と「ツール自体が失敗した (2)」を明確に分けているのが要点である。
/// CI ではこの 2 つを区別できないと、設定ファイルの誤記でツールが起動できなかった場合に
/// 「指摘が無かった」と誤解釈され、検査が素通りしていることに誰も気づかなくなる。
/// </para>
/// <para>
/// どの重要度から <see cref="ExitCode.DiagnosticsFound"/> とするかは <c>--error-on</c> で制御する。
/// 既定では警告以上を検出時に 1 を返す。
/// </para>
/// </remarks>
public enum ExitCode
{
    /// <summary>閾値以上の指摘が無かった。</summary>
    Success = 0,

    /// <summary>閾値以上の指摘が見つかった。解析自体は正常に完了している。</summary>
    DiagnosticsFound = 1,

    /// <summary>引数の誤り・ファイルの読み込み失敗など、ツール自体が正常に実行できなかった。</summary>
    ToolError = 2,
}
