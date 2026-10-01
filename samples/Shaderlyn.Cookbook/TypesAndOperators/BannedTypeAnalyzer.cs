using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 使ってはいけない型を報告する。
/// </summary>
/// <remarks>
/// <para>
/// <b>成分数を落とした名前でも照合する。</b>
/// <c>half</c> を禁じたい人が <c>half2</c> <c>half3</c> <c>half4</c> <c>half4x4</c> を
/// すべて書き並べるのは間違いのもとである。
/// <c>half4x4</c> → <c>half</c> の読み替えは
/// <see cref="HlslTypeClassifier.TryDescribeNumeric"/> が行う。
/// <c>half4</c> だけを禁じたいなら <c>half4</c> を渡せばよい。
/// </para>
/// <para>
/// <b>ヘッダのマクロが作った型には報告しない。</b>
/// マクロが展開した型は、利用者が自分のファイルで直せるものではない。
/// </para>
/// </remarks>
public sealed class BannedTypeAnalyzer(params string[] banned) : HlslRuleAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = [.. banned];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.BannedType];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<HlslTypeSyntax>(c =>
        {
            string name = c.Node.Name;

            if (!_banned.Contains(name) && !_banned.Contains(BaseName(name)))
            {
                return;
            }

            if (!c.Compilation.IsReportable(c.Node.NameToken))
            {
                return;
            }

            c.ReportDiagnostic(CookbookRules.BannedType, c.Node.NameToken.Span, name);
        });
    }

    /// <summary>成分数を落とした型名を返す。</summary>
    /// <param name="name">型名。</param>
    /// <returns><c>float4x4</c> なら <c>float</c>。数値型でなければそのまま。</returns>
    /// <remarks>
    /// <b>型名を自分で切り刻まないこと。</b>
    /// <c>Texture2D</c> の <c>2D</c> を成分数と読むような間違いは、
    /// 分類器に任せれば起こらない。
    /// </remarks>
    private static string BaseName(string name)
        => HlslTypeClassifier.TryDescribeNumeric(name, out HlslNumericShape shape) ? shape.BaseName : name;
}
