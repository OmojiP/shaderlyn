using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Rules;

/// <summary>
/// 半透明描画と深度書き込みの組み合わせの矛盾を検出する (SL1020)。
/// </summary>
/// <remarks>
/// <para>
/// <b>ZWrite の既定値が On である</b>ことが、このルールが必要な理由である。
/// Transparent キューのシェーダーを書く際に <c>ZWrite Off</c> を書き忘れると、
/// 半透明オブジェクトが深度バッファへ書き込み、
/// 後ろにある半透明オブジェクトが深度テストで捨てられる。
/// 見え方が描画順とカメラ位置に依存するため、開発中は気づかず
/// 特定の視点でだけ破綻するという厄介な形で現れる。
/// </para>
/// <para>
/// レンダーステートは Shader / Category / SubShader / Pass の各階層で指定でき、
/// 内側の指定が外側を上書きする。そのため Pass を起点に外へ向かって
/// 最も近い指定を探す必要がある。
/// </para>
/// </remarks>
internal sealed class TransparencyAnalyzer : DiagnosticAnalyzer
{
    /// <summary>半透明とみなす描画キューの下限値。</summary>
    /// <remarks>Unity の Transparent キューの基準値が 3000 であることによる。</remarks>
    private const int TransparentQueueThreshold = 3000;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderLabRuleDescriptors.TransparentWithZWrite];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<PassSyntax>(AnalyzePass);

    /// <summary>Pass 1 つの実効レンダーステートを求めて矛盾を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    private static void AnalyzePass(NodeAnalysisContext<PassSyntax> context)
    {
        PassSyntax pass = context.Node;
        List<BlockSyntax> blocks = [.. EnclosingBlocks(pass)];

        string? queue = FindNearestQueue(blocks);
        if (queue is null || !IsTransparentQueue(queue))
        {
            return;
        }

        CommandSyntax? blend = FindNearestCommand(blocks, "Blend");
        if (blend is null || !IsBlendEnabled(blend))
        {
            return;
        }

        CommandSyntax? zwrite = FindNearestCommand(blocks, "ZWrite");

        // ZWrite が未指定なら既定値の On が効く。これが最も多い書き忘れの形である。
        if (zwrite is null)
        {
            context.ReportDiagnostic(
                ShaderLabRuleDescriptors.TransparentWithZWrite, pass.Keyword.Span, queue);
            return;
        }

        if (zwrite.ValueArguments.FirstOrDefault() is not LiteralArgumentSyntax literal)
        {
            // プロパティ参照で切り替えている場合、実際の値は静的には分からない。
            // 判断できないものを誤りとして報告してはならない。
            return;
        }

        if (literal.Token.TextIs("On"))
        {
            context.ReportDiagnostic(
                ShaderLabRuleDescriptors.TransparentWithZWrite, literal.Token.Span, queue);
        }
    }

    /// <summary>
    /// Pass を内側として、外へ向かって順に囲んでいるブロックを列挙する。
    /// </summary>
    /// <param name="pass">起点となる Pass。</param>
    /// <returns>Pass 自身の本体を先頭とする、内側から外側へのブロックの列。</returns>
    /// <remarks>
    /// <c>Category</c> を含めているのは、旧来のシェーダーが
    /// 複数の SubShader に共通のレンダーステートを Category 階層に置くためである。
    /// これを見落とすと、正しく設定されているシェーダーを誤検出することになる。
    /// </remarks>
    private static IEnumerable<BlockSyntax> EnclosingBlocks(PassSyntax pass)
    {
        yield return pass.Body;

        for (SyntaxNode? node = pass.Parent; node is not null; node = node.Parent)
        {
            BlockSyntax? block = node switch
            {
                SubShaderSyntax subShader => subShader.Body,
                CategorySyntax category => category.Body,
                ShaderDeclarationSyntax shader => shader.Body,
                _ => null,
            };

            if (block is not null)
            {
                yield return block;
            }
        }
    }

    /// <summary>最も内側にある指定された命令を探す。</summary>
    /// <param name="blocks">内側から外側へ並んだブロック。</param>
    /// <param name="commandName">探す命令名。</param>
    /// <returns>見つかった命令。無い場合は <see langword="null"/>。</returns>
    private static CommandSyntax? FindNearestCommand(List<BlockSyntax> blocks, string commandName)
    {
        foreach (BlockSyntax block in blocks)
        {
            CommandSyntax? found = block.Statements
                .OfType<CommandSyntax>()
                .LastOrDefault(c => c.NameIs(commandName));

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>最も内側で指定されている描画キューを探す。</summary>
    /// <param name="blocks">内側から外側へ並んだブロック。</param>
    /// <returns>描画キューの値。指定が無い場合は <see langword="null"/>。</returns>
    private static string? FindNearestQueue(List<BlockSyntax> blocks)
    {
        foreach (BlockSyntax block in blocks)
        {
            foreach (TagsBlockSyntax tags in block.Statements.OfType<TagsBlockSyntax>())
            {
                TagSyntax? queue = tags.Tags
                    .LastOrDefault(t => string.Equals(t.Key, "Queue", StringComparison.OrdinalIgnoreCase));

                if (queue is not null && !queue.ValueToken.IsMissing)
                {
                    return queue.Value.Trim();
                }
            }
        }

        return null;
    }

    /// <summary>描画キューの値が半透明の領域かを判定する。</summary>
    /// <param name="queue">描画キューの値。</param>
    /// <returns>半透明とみなす場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 数値指定 (<c>"3100"</c> など) にも対応する必要がある。
    /// 基準名にオフセットが付く形 (<c>Transparent+100</c>) は基準名だけを見れば足りる。
    /// </remarks>
    private static bool IsTransparentQueue(string queue)
    {
        if (int.TryParse(queue, out int numeric))
        {
            return numeric >= TransparentQueueThreshold;
        }

        int signIndex = queue.IndexOfAny(['+', '-']);
        string baseName = signIndex < 0 ? queue : queue[..signIndex].TrimEnd();

        return ShaderLabKnownValues.TransparentQueues.Contains(baseName);
    }

    /// <summary>ブレンドが有効かを判定する。</summary>
    /// <param name="blend"><c>Blend</c> 命令。</param>
    /// <returns>有効な場合は <see langword="true"/>。</returns>
    private static bool IsBlendEnabled(CommandSyntax blend)
        => blend.ValueArguments.FirstOrDefault() is not LiteralArgumentSyntax first
            || !first.Token.TextIs("Off");
}
