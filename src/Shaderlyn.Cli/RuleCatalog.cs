using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Cli;

/// <summary>
/// このツールが報告しうるルールの一覧。
/// </summary>
/// <remarks>
/// <para>
/// <b>アナライザが申告するルールだけでは足りない。</b>
/// 構文エラー (SL0001 / HL0001) やプリプロセッサのエラー (HL0002) は
/// 解析基盤そのものが報告するもので、どのアナライザにも属さない。
/// ツール自身の問題 (TOOL 系) も同様である。
/// </para>
/// <para>
/// SARIF の <c>rules</c> にこれらが欠けていると、
/// GitHub のアラート画面で「不明なルール」として表示され、
/// 説明にも詳細ドキュメントにも辿れなくなる。
/// 一覧を 1 か所にまとめ、出力もドキュメントの検証も同じ定義を見るようにしている。
/// </para>
/// </remarks>
internal static class RuleCatalog
{
    /// <summary>
    /// アナライザに属さない、解析基盤が報告するルール。
    /// </summary>
    private static ImmutableArray<DiagnosticDescriptor> FrameworkDescriptors { get; } =
    [
        ShaderLab.ShaderLabDescriptors.SyntaxError,
        Hlsl.HlslDescriptors.SyntaxError,
        Hlsl.HlslDescriptors.PreprocessorError,
        Hlsl.HlslDescriptors.UnsupportedDeclaration,
        Hlsl.HlslDescriptors.ExtraTokensAfterConditionName,
        Hlsl.HlslDescriptors.CircularInclude,
        Hlsl.HlslDescriptors.UnresolvedInclude,
    ];

    /// <summary>
    /// ツール自身の問題を表すルール。
    /// </summary>
    /// <remarks>
    /// <para>
    /// シェーダーコードの問題ではないため、<c>--list-rules</c> には出さない。
    /// SARIF には含める。出力に現れる ID が <c>rules</c> に無いのは不整合だからである。
    /// </para>
    /// <para>
    /// <b>ドキュメントは「読んだ人が直せるもの」にだけ用意する。</b>
    /// <c>TOOL0005</c> と <c>TOOL0006</c> はアナライザの実装の誤りであり、
    /// 読むのは自分でルールを書いた人である。直し方が書けるので用意する。
    /// </para>
    /// </remarks>
    private static ImmutableArray<DiagnosticDescriptor> ToolDescriptors { get; } =
    [
        WellKnownDescriptors.AnalyzerThrew,
        WellKnownDescriptors.FileReadFailed,
        WellKnownDescriptors.ConfigurationProblem,
        WellKnownDescriptors.UndeclaredRuleReported,
        WellKnownDescriptors.MessageArgumentMismatch,
    ];

    /// <summary>
    /// シェーダーコードを対象とするルールをすべて返す。
    /// </summary>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <returns>ルール ID 順に並べた定義。</returns>
    /// <remarks>
    /// <c>--list-rules</c> とルールのドキュメント検証が使う。
    /// ツール自身の問題を表すルールは含まない。
    /// </remarks>
    public static ImmutableArray<DiagnosticDescriptor> GetShaderRules(
        ImmutableArray<DiagnosticAnalyzer> analyzers)
        =>
        [
            .. FrameworkDescriptors
                .Concat(analyzers.SelectMany(a => a.SupportedDiagnostics))
                .DistinctBy(d => d.Id, StringComparer.Ordinal)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
        ];

    /// <summary>
    /// 出力に現れうるすべてのルールを返す。
    /// </summary>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <returns>ルール ID 順に並べた定義。</returns>
    /// <remarks>SARIF の <c>rules</c> セクションが使う。</remarks>
    public static ImmutableArray<DiagnosticDescriptor> GetAllRules(
        ImmutableArray<DiagnosticAnalyzer> analyzers)
        =>
        [
            .. GetShaderRules(analyzers)
                .Concat(ToolDescriptors)
                .DistinctBy(d => d.Id, StringComparer.Ordinal)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
        ];
}
