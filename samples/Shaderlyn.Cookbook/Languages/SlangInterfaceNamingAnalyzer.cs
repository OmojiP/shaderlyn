using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// Slang のインターフェイスの名前が <c>I</c> で始まっているかを検査する。
/// </summary>
/// <remarks>
/// <para>
/// <b>Slang だけに当てる例である。</b> <c>interface</c> は HLSL (シェーダーモデル 5 の動的リンク) にもあるが、
/// 名前の付け方の決まりは Slang のプロジェクトのものなので、<see cref="Languages"/> を <see cref="ShaderLanguages.Slang"/> にする。
/// </para>
/// <para>
/// インターフェイスは、<see cref="StructDeclarationSyntax.Keyword"/> が <c>interface</c> の宣言として構文木に入る。
/// </para>
/// </remarks>
public sealed class SlangInterfaceNamingAnalyzer : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.SlangInterfaceNaming];

    protected override ShaderLanguages Languages => ShaderLanguages.Slang;

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<StructDeclarationSyntax>(c =>
        {
            if (c.Node.Keyword.Text != "interface" || c.Node.NameToken is not { } name)
            {
                return;
            }

            // IFoo の形 (I の後ろが大文字) を求める。Image のような名前を通さない。
            if (name.Text.Length < 2 || name.Text[0] != 'I' || !char.IsUpper(name.Text[1]))
            {
                c.ReportDiagnostic(CookbookRules.SlangInterfaceNaming, name.Span, name.Text);
            }
        });
    }
}
