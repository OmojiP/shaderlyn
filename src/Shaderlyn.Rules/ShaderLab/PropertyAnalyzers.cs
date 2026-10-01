using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Rules;

/// <summary>
/// プロパティ名の命名規約を検査する (SL1030)。
/// </summary>
/// <remarks>
/// アンダースコア始まりのみを検査する。それ以降の綴り方 (PascalCase かどうかなど) は
/// プロジェクトごとに方針が異なるため、既定で有効なルールとしては強すぎる。
/// 綴り方の規約は設定ファイルで宣言できるユーザー定義ルール (M4) の領分とする。
/// </remarks>
internal sealed class PropertyNamingAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderLabRuleDescriptors.PropertyNamingConvention];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<PropertyDeclarationSyntax>(AnalyzeProperty);

    /// <summary>プロパティ名を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    private static void AnalyzeProperty(NodeAnalysisContext<PropertyDeclarationSyntax> context)
    {
        SyntaxToken nameToken = context.Node.NameToken;

        if (nameToken.IsMissing || nameToken.Text.Length == 0)
        {
            return;
        }

        if (nameToken.Text[0] == '_')
        {
            return;
        }

        // Unity エンジンが提供する組み込みプロパティは unity_ 接頭辞を使う。
        // Unity 同梱のシェーダーがこの形で宣言しており、報告すると誤検出になる。
        if (nameToken.Text.StartsWith("unity_", StringComparison.Ordinal))
        {
            return;
        }

        context.ReportDiagnostic(
            ShaderLabRuleDescriptors.PropertyNamingConvention, nameToken.Span, nameToken.Text);
    }
}

/// <summary>
/// プロパティ属性の引数を検査する (SL1031)。
/// </summary>
/// <remarks>
/// <b>未知の属性名は報告しない。</b>
/// 属性は <c>MaterialPropertyDrawer</c> を継承したクラスを書くことで
/// プロジェクトごとに自由に追加できるため、このツールが知らないこと自体は誤りではない。
/// 未知の名前を報告する実装にすると、独自ドロワーを使っているプロジェクトで
/// 大量の誤検出が出て、ルール全体が無効化される。
/// </remarks>
internal sealed class PropertyAttributeAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderLabRuleDescriptors.InvalidAttributeArguments];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<PropertyAttributeSyntax>(AnalyzeAttribute);

    /// <summary>属性 1 件を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    private static void AnalyzeAttribute(NodeAnalysisContext<PropertyAttributeSyntax> context)
    {
        PropertyAttributeSyntax attribute = context.Node;

        if (attribute.NameToken.IsMissing)
        {
            return;
        }

        int argumentCount = CountArguments(attribute);

        if (string.Equals(attribute.Name, "Enum", StringComparison.OrdinalIgnoreCase))
        {
            ValidateEnum(context, attribute, argumentCount);
            return;
        }

        if (!ShaderLabKnownValues.AttributeArgumentCounts.TryGetValue(
                attribute.Name, out (int Min, int Max) expected))
        {
            return;
        }

        if (argumentCount < expected.Min || argumentCount > expected.Max)
        {
            context.ReportDiagnostic(
                ShaderLabRuleDescriptors.InvalidAttributeArguments,
                attribute.NameToken.Span,
                attribute.Name,
                argumentCount,
                DescribeExpectedCount(expected));
        }
    }

    /// <summary>
    /// <c>Enum</c> 属性を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="attribute">検査する属性。</param>
    /// <param name="argumentCount">引数の個数。</param>
    /// <remarks>
    /// <c>Enum</c> は引数の形が 2 通りある。
    /// C# の列挙型名を 1 つ渡す形 (<c>Enum(UnityEngine.Rendering.CullMode)</c>) と、
    /// 名前と値の組を並べる形 (<c>Enum(Off, 0, On, 1)</c>) である。
    /// 後者は必ず偶数個になるため、個数だけで両者を区別できる。
    /// </remarks>
    private static void ValidateEnum(
        NodeAnalysisContext<PropertyAttributeSyntax> context,
        PropertyAttributeSyntax attribute,
        int argumentCount)
    {
        if (argumentCount == 1 || (argumentCount >= 2 && argumentCount % 2 == 0))
        {
            return;
        }

        context.ReportDiagnostic(
            ShaderLabRuleDescriptors.InvalidAttributeArguments,
            attribute.NameToken.Span,
            attribute.Name,
            argumentCount,
            "列挙型名を 1 つ指定するか、名前と値の組を偶数個で指定してください (例: Enum(Off, 0, On, 1))。");
    }

    /// <summary>
    /// 属性の引数の個数を数える。
    /// </summary>
    /// <param name="attribute">対象の属性。</param>
    /// <returns>引数の個数。</returns>
    /// <remarks>
    /// 修飾名 <c>UnityEngine.Rendering.CullMode</c> は 1 つの引数として数える必要がある。
    /// 字句解析の段階では識別子とドットに分かれているため、ドットで連結された並びを
    /// 1 つにまとめてから数える。
    /// </remarks>
    private static int CountArguments(PropertyAttributeSyntax attribute)
    {
        int count = 0;
        bool expectingNewArgument = true;

        foreach (SyntaxToken token in attribute.ArgumentTokens)
        {
            // 引数の区切りはカンマだけである。
            if (token.Kind == SyntaxKind.CommaToken)
            {
                expectingNewArgument = true;
                continue;
            }

            // ドットは修飾名の途中を示すので、引数の個数には影響しない。
            // UnityEngine.Rendering.CullMode 全体で 1 つの引数である。
            if (token.Kind == SyntaxKind.DotToken)
            {
                continue;
            }

            if (expectingNewArgument)
            {
                count++;
                expectingNewArgument = false;
            }
        }

        return count;
    }

    /// <summary>期待される引数の個数を説明する文字列を作る。</summary>
    /// <param name="expected">期待される個数の範囲。</param>
    /// <returns>説明文。</returns>
    private static string DescribeExpectedCount((int Min, int Max) expected)
        => expected.Min == expected.Max
            ? $"{expected.Min} 個の引数が必要です。"
            : $"{expected.Min} 個から {expected.Max} 個の引数を指定してください。";
}

/// <summary>
/// シェーダー名の構造を検査する (SL1040)。
/// </summary>
/// <remarks>
/// シェーダー名は Unity のマテリアルインスペクタでスラッシュ区切りのメニュー階層になる。
/// 構造が壊れているとメニューに現れない、あるいは選択できない項目になるが、
/// Unity は警告を出さないため気づきにくい。
/// </remarks>
internal sealed class ShaderNameAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderLabRuleDescriptors.InvalidShaderName];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<ShaderDeclarationSyntax>(AnalyzeShaderName);

    /// <summary>シェーダー名を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    private static void AnalyzeShaderName(NodeAnalysisContext<ShaderDeclarationSyntax> context)
    {
        SyntaxToken nameToken = context.Node.NameToken;

        if (nameToken.IsMissing)
        {
            return;
        }

        string name = nameToken.ValueText;
        string? problem = FindProblem(name);

        if (problem is not null)
        {
            context.ReportDiagnostic(
                ShaderLabRuleDescriptors.InvalidShaderName, nameToken.Span, name, problem);
        }
    }

    /// <summary>
    /// シェーダー名の構造上の問題を探す。
    /// </summary>
    /// <param name="name">検査するシェーダー名。</param>
    /// <returns>問題の説明。問題が無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// スラッシュを含まない名前は問題としない。
    /// 階層に属さないシェーダーはメニューの最上位に表示されるだけで、正常に動作する。
    /// </remarks>
    private static string? FindProblem(string name)
    {
        if (name.Length == 0)
        {
            return "シェーダー名が空です。";
        }

        if (name.Trim() != name)
        {
            return "先頭または末尾に空白があります。";
        }

        string[] segments = name.Split('/');

        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                return "階層の区切り '/' の前後に空の階層があります。";
            }

            if (segment.Trim() != segment)
            {
                return $"階層 '{segment}' の先頭または末尾に空白があります。";
            }
        }

        return null;
    }
}
