using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Rules;

/// <summary>
/// レンダーステート命令の引数の値を検査する (SL1021)。
/// </summary>
/// <remarks>
/// Unity は不正な値を指定してもエラーにせず既定値へ倒して動作を続けるため、
/// スペルミスは「設定したつもりが効いていない」という形でしか現れない。
/// </remarks>
internal sealed class RenderStateValueAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderLabRuleDescriptors.InvalidRenderStateValue];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.RegisterNodeAction<CommandSyntax>(AnalyzeCommand);
        context.RegisterNodeAction<StencilBlockSyntax>(AnalyzeStencilBlock);
    }

    /// <summary>
    /// 通常のレンダーステート命令を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <remarks>
    /// ステンシルブロック内の命令はここでは扱わない。
    /// <c>Pass</c> や <c>Comp</c> はステンシル文脈でのみ意味を持ち、
    /// 同名の命令が別の意味で使われる可能性があるためである。
    /// </remarks>
    private static void AnalyzeCommand(NodeAnalysisContext<CommandSyntax> context)
    {
        CommandSyntax command = context.Node;

        if (command.Parent is StencilBlockSyntax)
        {
            return;
        }

        if (command.NameIs("Blend"))
        {
            ValidateBlend(context, command);
            return;
        }

        if (!ShaderLabKnownValues.CommandValues.TryGetValue(command.Name, out FrozenSet<string>? allowed))
        {
            return;
        }

        foreach (CommandArgumentSyntax argument in command.ValueArguments)
        {
            ValidateArgument(context, command.Name, argument, allowed);
        }
    }

    /// <summary>ステンシルブロック内の命令を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    private static void AnalyzeStencilBlock(NodeAnalysisContext<StencilBlockSyntax> context)
    {
        foreach (CommandSyntax command in context.Node.Commands)
        {
            if (!ShaderLabKnownValues.StencilCommandValues.TryGetValue(command.Name, out FrozenSet<string>? allowed))
            {
                continue;
            }

            foreach (CommandArgumentSyntax argument in command.ValueArguments)
            {
                ValidateArgument(context, command.Name, argument, allowed);
            }
        }
    }

    /// <summary>
    /// <c>Blend</c> 命令を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="command">検査する命令。</param>
    /// <remarks>
    /// <para>
    /// <c>Blend</c> は他の命令と形が違うため個別に扱う。
    /// <c>Blend Off</c> でブレンドを無効化でき、
    /// <c>Blend 1 SrcAlpha OneMinusSrcAlpha</c> のように先頭にレンダーターゲット番号を取れる。
    /// </para>
    /// <para>
    /// <b><c>Off</c> はどの位置にあっても妥当とする。</b>
    /// レンダーターゲット番号を伴う <c>Blend 1 Off</c> という書き方があり、
    /// 引数が 1 つの場合だけを例外にしていた時期には、
    /// Unity 同梱のシェーダーに 5 件の誤検出が出ていた。
    /// </para>
    /// <para>
    /// 数値の引数はレンダーターゲット番号とみなして検証しない。
    /// </para>
    /// </remarks>
    private static void ValidateBlend(NodeAnalysisContext<CommandSyntax> context, CommandSyntax command)
    {
        foreach (CommandArgumentSyntax argument in command.ValueArguments)
        {
            if (argument is LiteralArgumentSyntax literal && literal.Token.TextIs("Off"))
            {
                continue;
            }

            ValidateArgument(context, command.Name, argument, ShaderLabKnownValues.BlendFactors);
        }
    }

    /// <summary>
    /// 引数 1 つを許容値の集合と照合する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="commandName">命令名。メッセージに使う。</param>
    /// <param name="argument">検査する引数。</param>
    /// <param name="allowed">許容される値の集合。</param>
    /// <remarks>
    /// <para>
    /// <b>プロパティ参照と数値は検証しない。</b>
    /// <c>Cull [_Cull]</c> の実際の値はマテリアル側で決まるため静的には分からず、
    /// 数値はレンダーターゲット番号やマスク値など命令ごとに意味が異なる。
    /// 判断できないものを誤りとして報告しないことが、誤検出を出さないための原則である。
    /// </para>
    /// </remarks>
    private static void ValidateArgument<TNode>(
        NodeAnalysisContext<TNode> context,
        string commandName,
        CommandArgumentSyntax argument,
        FrozenSet<string> allowed)
        where TNode : Core.Syntax.SyntaxNode
    {
        if (argument is not LiteralArgumentSyntax literal)
        {
            return;
        }

        if (literal.Token.IsMissing || literal.Token.Kind != SyntaxKind.IdentifierToken)
        {
            return;
        }

        if (allowed.Contains(literal.Text))
        {
            return;
        }

        context.ReportDiagnostic(
            ShaderLabRuleDescriptors.InvalidRenderStateValue,
            literal.Token.Span,
            commandName,
            literal.Text,
            string.Join(", ", allowed.Order(StringComparer.Ordinal)));
    }
}
