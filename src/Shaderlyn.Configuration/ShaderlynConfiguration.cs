using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Configuration;

/// <summary>
/// 設定ファイルの読み込みで見つかった問題。
/// </summary>
/// <param name="Line">問題のある 0 始まりの行番号。</param>
/// <param name="Message">問題の内容。</param>
internal readonly record struct ConfigurationProblem(int Line, string Message);

/// <summary>
/// <c>.shaderlyn.yaml</c> の内容。
/// </summary>
/// <remarks>
/// <para>
/// この型は解析の実行時設定 (<c>AnalyzerOptions</c> や <c>SemanticsOptions</c>) とは別物である。
/// 設定ファイルの書式に対応する入れ物であり、
/// 実行時設定への詰め替えは呼び出し側 (CLI) が行う。
/// </para>
/// <para>
/// この分離により、Core と Semantics は設定ファイルの書式を知らずに済み、
/// テストからは設定ファイルを用意せずに任意の設定を注入できる。
/// </para>
/// </remarks>
internal sealed class ShaderlynConfiguration
{
    /// <summary>設定ファイルの既定の名前。</summary>
    public const string FileName = ".shaderlyn.yaml";

    /// <summary>
    /// このツールが理解する設定ファイルのバージョン。
    /// </summary>
    /// <remarks>
    /// 書式を変更する際にこれを上げ、古いバージョンを読んだときに何が変わったかを示せるようにする。
    /// </remarks>
    public const int SupportedVersion = 1;

    /// <summary>何も設定されていない状態。</summary>
    public static ShaderlynConfiguration Empty { get; } = new();

    /// <summary>読み込み元のファイルパス。設定ファイルが無い場合は <see langword="null"/>。</summary>
    public string? FilePath { get; init; }

    /// <summary>適用するレンダーパイプラインプロファイルの名前。指定が無い場合は <see langword="null"/>。</summary>
    public string? Profile { get; init; }

    /// <summary><c>#include</c> の追加探索パス。設定ファイルからの相対で解決済み。</summary>
    public ImmutableArray<string> IncludePaths { get; init; } = [];

    /// <summary>定義済みとするマクロ。<c>NAME</c> または <c>NAME=VALUE</c> の形。</summary>
    public ImmutableArray<string> Defines { get; init; } = [];

    /// <summary>ルール ID から重要度への上書き。</summary>
    public ImmutableDictionary<string, DiagnosticSeverity> RuleSeverities { get; init; } =
        ImmutableDictionary<string, DiagnosticSeverity>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>ルール ID からオプション名・値への対応。</summary>
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> RuleOptions { get; init; } =
        ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 読み込みで見つかった問題。
    /// </summary>
    /// <remarks>
    /// <b>問題があっても読み込みは失敗させない。</b>
    /// 設定ファイルの些細な誤記で解析全体が動かなくなるより、
    /// 読めた範囲で解析を続けたほうがよい。
    /// ただし問題自体は必ず報告する。何も伝えずに無視すると
    /// 「設定したつもりで効いていない」状態に気づけない。
    /// </remarks>
    public ImmutableArray<ConfigurationProblem> Problems { get; init; } = [];
}
