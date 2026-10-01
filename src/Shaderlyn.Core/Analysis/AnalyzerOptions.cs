using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// 解析の実行時設定。ルールごとの重要度上書きと、ルール固有のオプションを保持する。
/// </summary>
/// <remarks>
/// この型は設定ファイルの書式から独立している。
/// 設定ファイル (.shaderlyn.yaml) の読み込みは CLI 層の責務であり、
/// 読み込み結果をこの型へ詰め替えて Core へ渡す。
/// こうしておくことで、Core は YAML パーサに依存せず、
/// テストからは設定ファイルを用意せずに任意の設定を注入できる。
/// </remarks>
public sealed class AnalyzerOptions
{
    private readonly ImmutableDictionary<string, DiagnosticSeverity> _severityOverrides;
    private readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> _ruleOptions;
    private readonly ImmutableDictionary<Type, object> _extensions;

    /// <summary>
    /// 実行時設定を生成する。
    /// </summary>
    /// <param name="severityOverrides">ルール ID から重要度への上書き表。</param>
    /// <param name="ruleOptions">ルール ID からオプション名・値への表。</param>
    public AnalyzerOptions(
        ImmutableDictionary<string, DiagnosticSeverity>? severityOverrides = null,
        ImmutableDictionary<string, ImmutableDictionary<string, string>>? ruleOptions = null)
        : this(severityOverrides, ruleOptions, ImmutableDictionary<Type, object>.Empty)
    {
    }

    private AnalyzerOptions(
        ImmutableDictionary<string, DiagnosticSeverity>? severityOverrides,
        ImmutableDictionary<string, ImmutableDictionary<string, string>>? ruleOptions,
        ImmutableDictionary<Type, object> extensions)
    {
        // ルール ID の大文字小文字は区別しない。設定ファイルに sl1003 と書かれても意図どおり効かせたい。
        _severityOverrides = severityOverrides ?? ImmutableDictionary<string, DiagnosticSeverity>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);
        _ruleOptions = ruleOptions ?? ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);
        _extensions = extensions;
    }

    /// <summary>
    /// 追加の設定を添えた複製を返す。
    /// </summary>
    /// <typeparam name="TExtension">添える設定の型。取り出す際のキーになる。</typeparam>
    /// <param name="extension">添える設定。</param>
    /// <returns>設定を添えた新しい実行時設定。</returns>
    /// <remarks>
    /// <b>層の依存方向を守るための仕組みである。</b>
    /// 設定ファイル由来のプロジェクト固有ルール (<c>banned-symbols</c> など) の定義は
    /// Core が知りうる型ではない。かといってルールがそれを受け取れなければ、
    /// 設定ファイルだけでルールを書くという要件が成立しない。
    /// <see cref="AnalysisTarget.WithModel{TModel}"/> と同じ考え方である。
    /// </remarks>
    public AnalyzerOptions WithExtension<TExtension>(TExtension extension)
        where TExtension : class
    {
        ArgumentNullException.ThrowIfNull(extension);
        return new AnalyzerOptions(
            _severityOverrides, _ruleOptions, _extensions.SetItem(typeof(TExtension), extension));
    }

    /// <summary>
    /// 添えられた追加の設定を取り出す。
    /// </summary>
    /// <typeparam name="TExtension">取り出す設定の型。</typeparam>
    /// <returns>添えられていればその設定。無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 設定されていない場合、それを必要とするルールは何も報告しない。
    /// 設定ファイルで定義するルールは、定義が無ければ検査対象が存在しないので、
    /// これは「検査していない」ではなく「検査する内容が無い」状態である。
    /// </remarks>
    public TExtension? GetExtension<TExtension>()
        where TExtension : class
        => _extensions.TryGetValue(typeof(TExtension), out object? extension) ? (TExtension)extension : null;

    /// <summary>上書きを一切含まない既定の設定。</summary>
    public static AnalyzerOptions Default { get; } = new();

    /// <summary>
    /// ルールの実効重要度を返す。
    /// </summary>
    /// <param name="descriptor">対象のルール定義。</param>
    /// <returns>
    /// 設定による上書きがあればその値、無ければ <see cref="DiagnosticDescriptor.DefaultSeverity"/>。
    /// <see cref="DiagnosticSeverity.None"/> はそのルールが無効であることを意味する。
    /// </returns>
    public DiagnosticSeverity GetEffectiveSeverity(DiagnosticDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return _severityOverrides.TryGetValue(descriptor.Id, out DiagnosticSeverity severity)
            ? severity
            : descriptor.DefaultSeverity;
    }

    /// <summary>
    /// ルールが有効かどうかを判定する。
    /// </summary>
    /// <param name="descriptor">対象のルール定義。</param>
    /// <returns>実効重要度が <see cref="DiagnosticSeverity.None"/> でなければ <see langword="true"/>。</returns>
    /// <remarks>
    /// ドライバはこの判定でアナライザを実行前に除外する。
    /// 無効なルールの診断を生成してから捨てるのではなく、そもそも実行しないことで
    /// 大量のルールを抱えても実行コストが設定に比例するようにしている。
    /// </remarks>
    public bool IsEnabled(DiagnosticDescriptor descriptor) => GetEffectiveSeverity(descriptor) != DiagnosticSeverity.None;

    /// <summary>
    /// ルール固有のオプション値を取得する。
    /// </summary>
    /// <param name="ruleId">ルール ID。</param>
    /// <param name="optionName">オプション名。</param>
    /// <param name="value">取得できた値。</param>
    /// <returns>オプションが設定されていれば <see langword="true"/>。</returns>
    public bool TryGetOption(string ruleId, string optionName, out string value)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        ArgumentNullException.ThrowIfNull(optionName);

        if (_ruleOptions.TryGetValue(ruleId, out ImmutableDictionary<string, string>? options)
            && options.TryGetValue(optionName, out string? found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// ルール固有のオプションを整数として取得する。
    /// </summary>
    /// <param name="ruleId">ルール ID。</param>
    /// <param name="optionName">オプション名。</param>
    /// <param name="defaultValue">設定が無い、または数値として解釈できない場合に返す値。</param>
    /// <returns>解釈できた整数値、あるいは <paramref name="defaultValue"/>。</returns>
    /// <remarks>
    /// 解釈できない値を例外にせず既定値へ倒すのは、設定ファイルの些細な誤記で
    /// 解析全体が失敗するのを避けるためである。誤記自体は設定読み込み層が別途警告する。
    /// </remarks>
    public int GetIntOption(string ruleId, string optionName, int defaultValue)
        => TryGetOption(ruleId, optionName, out string raw) && int.TryParse(raw, out int parsed)
            ? parsed
            : defaultValue;
}
