using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// 1 つ以上のルールを実装するアナライザの基底クラス。
/// </summary>
/// <remarks>
/// <para>
/// Roslyn の <c>DiagnosticAnalyzer</c> と同じ利用形態を意図している。
/// 派生クラスは <see cref="SupportedDiagnostics"/> で自分が報告しうるルールを申告し、
/// <see cref="Initialize(AnalysisContext)"/> で関心のある構文ノードにアクションを登録する。
/// </para>
/// <para>
/// <b>派生クラスは状態を持ってはならない。</b>
/// アナライザのインスタンスは全ファイルで共有され、
/// 複数ファイルを並列に解析する際に同じインスタンスが同時に呼ばれる
/// (<c>AnalysisRunner</c> はコア数だけ並列に走らせる)。
/// 1 ファイル分の走査をまたいで情報を集約したい場合 (「どの Pass からも参照されていないプロパティ」
/// のような検査) は、インスタンスフィールドではなく
/// <see cref="AnalysisContext.RegisterAnalysisStartAction"/> を使い、
/// ファイル 1 つ分のスコープを持つ状態オブジェクトをクロージャで捕捉すること。
/// </para>
/// <para>
/// 名前を <c>Shaderlyn</c> ではなく <c>DiagnosticAnalyzer</c> にしているのは、
/// ルート名前空間 <c>Shaderlyn</c> と衝突して参照が曖昧になるのを避けるためであり、
/// あわせて Roslyn の呼称に揃える意図もある。
/// </para>
/// </remarks>
public abstract class DiagnosticAnalyzer
{
    /// <summary>
    /// このアナライザが報告しうるルールの一覧。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ドライバはこの一覧を使って、設定で全ルールが無効化されているアナライザを
    /// 実行前に丸ごと除外する。申告していないルールを報告した場合は
    /// デバッグビルドで検出されるため、追加時の申告漏れに注意すること。
    /// </para>
    /// <para>
    /// CLI の <c>--list-rules</c> と SARIF の <c>rules[]</c> セクションもこの一覧から生成される。
    /// </para>
    /// </remarks>
    public abstract ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }

    /// <summary>
    /// 解析対象への関心を登録する。ドライバの構築時に一度だけ呼ばれる。
    /// </summary>
    /// <param name="context">アクションを登録するためのコンテキスト。</param>
    /// <remarks>
    /// ファイルごとに呼ばれるのではなく、ドライバの生成時に 1 回だけ呼ばれる点に注意。
    /// ここで登録されたアクションが、以降すべてのファイルに対して適用される。
    /// </remarks>
    public abstract void Initialize(AnalysisContext context);
}
