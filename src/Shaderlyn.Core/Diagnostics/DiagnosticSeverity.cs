namespace Shaderlyn.Core.Diagnostics;

/// <summary>
/// 診断の重要度。
/// </summary>
/// <remarks>
/// <see cref="None"/> を含めているのは、設定ファイルで <c>SL1004: none</c> のように
/// ルールを個別に無効化できるようにするためである。無効化されたルールは
/// アナライザを実行する前の段階で除外され、実行コスト自体が発生しない。
/// 値の順序は「弱い順」に並んでおり、<c>--error-on warning</c> のような
/// 閾値判定を単純な比較で行えるようにしてある。
/// </remarks>
public enum DiagnosticSeverity
{
    /// <summary>報告しない。設定でルールを無効化した状態を表す。</summary>
    None = 0,

    /// <summary>情報。問題ではないが知らせる価値のある事柄。</summary>
    Info = 1,

    /// <summary>警告。修正が望ましいが、ビルドを止めるほどではない事柄。</summary>
    Warning = 2,

    /// <summary>エラー。修正すべき明確な問題。</summary>
    Error = 3,
}
