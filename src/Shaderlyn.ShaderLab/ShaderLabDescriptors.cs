using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.ShaderLab;

/// <summary>
/// ShaderLab の字句・構文解析が報告するルール定義。
/// </summary>
internal static class ShaderLabDescriptors
{
    /// <summary>
    /// 構文エラー。
    /// </summary>
    /// <remarks>
    /// <para>
    /// パーサはこの診断を出したうえで解析を継続する。
    /// 1 か所の誤りでファイル全体の検査を放棄すると、他の問題が一切報告されなくなるためである。
    /// </para>
    /// <para>
    /// ただし構文が壊れた箇所の周辺では、後続ルールの結果は信頼できない。
    /// パーサが合成した欠落トークンに対してルールが診断を出さないよう、
    /// 各ルールは <c>SyntaxToken.IsMissing</c> を確認する必要がある。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor SyntaxError { get; } = new(
        id: "SL0001",
        title: "構文エラー",
        messageFormat: "{0}",
        category: "Syntax",
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ShaderLab の構文として解釈できない記述があります。"
            + "この箇所より後の解析結果は信頼できない可能性があるため、まずこのエラーを解消してください。",
        helpLinkUri: DocumentationLinks.For("SL0001"));
}
