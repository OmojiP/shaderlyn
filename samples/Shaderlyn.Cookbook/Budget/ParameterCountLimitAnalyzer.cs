using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 関数の仮引数の数を制限する。上限は設定ファイルから変えられる。
/// </summary>
/// <remarks>
/// <para>
/// <b>設定ファイルの <c>options</c> を読む例である。</b>
/// <c>.shaderlyn.yaml</c> に次のように書くと、コンストラクタで渡した既定値を上書きできる。
/// 読むオプションはルールごとに自分で決める。組み込みルールに同じことを書いても効かない。
/// </para>
/// <code>
/// rules:
///   COOK0030:
///     severity: warning
///     options: { maxParameters: 4 }
/// </code>
/// <para>
/// <b>値はプロジェクトごとに変わるが、ルールの有無は変わらない。</b>
/// この形が向くのは閾値のような数値である。
/// 禁止する名前の一覧のように項目が増えていくものは、
/// コンストラクタで渡したほうが型のまま扱えて誤りに気づきやすい。
/// </para>
/// <para>
/// <b>設定が無ければコンストラクタの既定値で動く。</b>
/// 設定ファイルを置いていない利用者にも、そのまま働くようにしておく。
/// </para>
/// </remarks>
/// <param name="defaultMaxParameters">設定ファイルに <c>maxParameters</c> が無いときに使う上限。</param>
public sealed class ParameterCountLimitAnalyzer(int defaultMaxParameters = 8) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.TooManyParameters];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
        {
            // 数値として読めない値が書かれていた場合も既定値へ倒れる。
            // 書き間違い自体は設定の読み込み側が TOOL0003 で報告する。
            int maximum = c.Options.GetIntOption(
                CookbookRules.TooManyParameters.Id,
                "maxParameters",
                defaultMaxParameters);

            if (!c.Compilation.IsReportable(c.Node.NameToken))
            {
                return;
            }

            int parameters = c.Node.ParameterList.Count();

            if (parameters > maximum)
            {
                c.ReportDiagnostic(
                    CookbookRules.TooManyParameters,
                    c.Node.NameToken.Span,
                    c.Node.Name,
                    parameters,
                    maximum);
            }
        });
    }
}
