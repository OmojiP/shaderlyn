using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Cookbook;

/// <summary>
/// レンダーステートの命令に書ける値を縛る。
/// </summary>
/// <remarks>
/// <para>
/// <b>値として正しいかと、チームとして許すかは別の問いである。</b>
/// <c>Cull Off</c> は ShaderLab として正しい。組み込みの検査 (SL1021) は通る。
/// 「このプロジェクトでは書かせない」はここで決める。
/// </para>
/// <para>
/// <b><c>Cull [_Cull]</c> の形は見送る。</b>
/// 実際の値はマテリアル側で決まるため、その場で確かめようがない。
/// </para>
/// <para>
/// 引数を 1 つずつ見る。<c>Blend One Zero</c> のように複数の値を取る命令では、
/// 許す値をすべて並べること。
/// </para>
/// </remarks>
public sealed class CommandValueAnalyzer(string command, params string[] allowedValues) : DiagnosticAnalyzer
{
    private readonly ImmutableHashSet<string> _allowed =
        allowedValues.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CookbookRules.CommandValueNotAllowed];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<CommandSyntax>(c =>
        {
            if (!c.Node.NameIs(command))
            {
                return;
            }

            foreach (CommandArgumentSyntax argument in c.Node.ValueArguments)
            {
                if (argument is not LiteralArgumentSyntax literal || _allowed.Contains(literal.Text))
                {
                    continue;
                }

                c.ReportDiagnostic(
                    CookbookRules.CommandValueNotAllowed, literal.Token.Span, c.Node.Name, literal.Text);
            }
        });
    }
}
