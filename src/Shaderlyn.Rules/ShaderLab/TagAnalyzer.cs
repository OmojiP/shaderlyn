using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Rules;

/// <summary>
/// タグ名と値を検査する (SL1010 / SL1011 / SL1012)。
/// </summary>
/// <remarks>
/// Unity は不正なタグ名も不正なタグ値もエラーにしない。
/// そのためスペルミスは「設定したつもりが効いていない」という形で現れ、
/// 実行して初めて気づくことになる。静的に検出する価値が高い部類のルールである。
/// </remarks>
internal sealed class TagAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        ShaderLabRuleDescriptors.UnknownTag,
        ShaderLabRuleDescriptors.MisspelledTagValue,
        ShaderLabRuleDescriptors.InvalidTagValue,
    ];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<TagSyntax>(AnalyzeTag);

    /// <summary>タグ 1 件を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    private static void AnalyzeTag(NodeAnalysisContext<TagSyntax> context)
    {
        TagSyntax tag = context.Node;

        // 構文エラーからの回復で合成されたトークンには診断を出さない。
        // 同じ 1 か所の誤りについて構文エラーと重ねて指摘すると、出力が読めなくなる。
        if (tag.KeyToken.IsMissing || tag.ValueToken.IsMissing)
        {
            return;
        }

        if (!ShaderLabKnownValues.TagKeys.Contains(tag.Key))
        {
            // 未知であること自体は報告しない。既知のタグのスペルミスに見える場合だけ報告する。
            if (FindLikelyMisspelling(tag.Key, ShaderLabKnownValues.TagKeys) is { } suggestion)
            {
                ReportMisspelling(
                    context, ShaderLabRuleDescriptors.UnknownTag, tag.KeyToken, suggestion, tag.Key, suggestion);
            }

            return;
        }

        ValidateTagValue(context, tag);
    }

    /// <summary>
    /// 既知の名前のスペルミスに見えるかを判定する。
    /// </summary>
    /// <param name="key">判定する名前。</param>
    /// <param name="known">照合先の既知の名前。</param>
    /// <returns>スペルミスと思われる場合の正しい候補。該当しない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>「既知の一覧に無いから誤り」という判定にしてはならない。</b>
    /// タグの名前空間は開かれており、レンダーパイプライン・地形・スプライト・
    /// テクスチャストリーミングなど、各機能が独自のタグを追加する。
    /// 一覧に無いものを報告する実装では、Unity 同梱のシェーダーに対してだけでも
    /// 56 件の誤検出が出ていた。一覧を足して回っても追いつかない。
    /// </para>
    /// <para>
    /// このルールの目的は「スペルミスに気づかせること」なので、
    /// 既知のタグに十分近い名前だけを報告する。
    /// <c>RendrType</c> は <c>RenderType</c> から 1 文字違いなので報告し、
    /// <c>TerrainCompatible</c> はどの既知タグからも遠いので報告しない。
    /// </para>
    /// <para>
    /// 短い名前を対象から外しているのは、短い語ほど無関係な名前同士でも
    /// 編集距離が小さくなり、意味のない指摘が増えるためである。
    /// </para>
    /// <para>
    /// タグ名にもタグ値にも同じ判定を使う。どちらも
    /// 「一覧に無いこと」を根拠にできない一方、Unity がスペルミスを報告しない点は同じである。
    /// </para>
    /// </remarks>
    private static string? FindLikelyMisspelling(string key, FrozenSet<string> known)
    {
        const int minimumLength = 5;
        const int maximumDistance = 2;

        if (key.Length < minimumLength)
        {
            return null;
        }

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in known)
        {
            // 長さが大きく違うものはスペルミスではありえない。距離計算自体を省く。
            if (Math.Abs(candidate.Length - key.Length) > maximumDistance)
            {
                continue;
            }

            int distance = ComputeEditDistance(key, candidate, maximumDistance);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= maximumDistance ? best : null;
    }

    /// <summary>
    /// 2 つの文字列の編集距離を求める。
    /// </summary>
    /// <param name="left">比較する文字列。</param>
    /// <param name="right">比較する文字列。</param>
    /// <param name="limit">この値を超えた時点で計算を打ち切る。</param>
    /// <returns>編集距離。<paramref name="limit"/> を超える場合はそれより大きい値。</returns>
    /// <remarks>
    /// 大文字小文字は区別しない。ShaderLab のタグ名は区別するが、
    /// 大小の誤りもスペルミスとして知らせる価値があるためである。
    /// </remarks>
    private static int ComputeEditDistance(string left, string right, int limit)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];

        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            int rowMinimum = current[0];

            for (int j = 1; j <= right.Length; j++)
            {
                int substitutionCost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);

                rowMinimum = Math.Min(rowMinimum, current[j]);
            }

            // この行の最小値が上限を超えていれば、最終的な距離も必ず上限を超える。
            if (rowMinimum > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    /// <summary>既知のタグについて、値が妥当かを検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="tag">検査するタグ。</param>
    private static void ValidateTagValue(NodeAnalysisContext<TagSyntax> context, TagSyntax tag)
    {
        if (string.Equals(tag.Key, "Queue", StringComparison.OrdinalIgnoreCase))
        {
            ValidateQueue(context, tag);
            return;
        }

        if (ShaderLabKnownValues.BooleanTagKeys.Contains(tag.Key))
        {
            ValidateAgainstSet(context, tag, BooleanValues, "True または False を指定してください。");
            return;
        }

        if (string.Equals(tag.Key, "DisableBatching", StringComparison.OrdinalIgnoreCase))
        {
            ValidateAgainstSet(
                context, tag, ShaderLabKnownValues.DisableBatchingValues,
                "True, False, LODFading のいずれかを指定してください。");
            return;
        }

        if (string.Equals(tag.Key, "PreviewType", StringComparison.OrdinalIgnoreCase))
        {
            ValidateAgainstSet(
                context, tag, ShaderLabKnownValues.PreviewTypeValues,
                "Sphere, Plane, Skybox のいずれかを指定してください。");
            return;
        }

        if (string.Equals(tag.Key, "RenderType", StringComparison.OrdinalIgnoreCase))
        {
            ValidateRenderType(context, tag);
        }

        // LightMode はここでは検証しない。
        // 妥当な値はレンダーパイプラインごとに異なるうえ、
        // カスタムのレンダラー機能が独自の値を自由に足せるため、
        // RP プロファイルを持つルール (URP0005 など) の領分とする。
    }

    /// <summary>
    /// <c>RenderType</c> タグの値を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="tag">検査するタグ。</param>
    /// <remarks>
    /// <para>
    /// <b>既知の値でないこと自体は報告しない。</b>
    /// <c>RenderType</c> は <c>Camera.RenderWithShader</c> によるシェーダー置き換えの目印であり、
    /// プロジェクトが自由な値を付けてよい。「一覧に無いから誤り」とは言えない。
    /// </para>
    /// <para>
    /// 一方で、<c>Opaque</c> を <c>Opaqu</c> と書いても Unity は何も言わない。
    /// 置き換えの対象から外れたことにも、エディタが不透明として扱わなくなったことにも
    /// 気づけないまま進むことになる。
    /// 既知の値に十分近いものだけを、スペルミスの疑いとして報告する。
    /// </para>
    /// </remarks>
    private static void ValidateRenderType(NodeAnalysisContext<TagSyntax> context, TagSyntax tag)
    {
        if (ShaderLabKnownValues.RenderTypes.Contains(tag.Value))
        {
            return;
        }

        if (FindLikelyMisspelling(tag.Value, ShaderLabKnownValues.RenderTypes) is { } suggestion)
        {
            ReportMisspelling(
                context,
                ShaderLabRuleDescriptors.MisspelledTagValue,
                tag.ValueToken,
                suggestion,
                tag.Key,
                tag.Value,
                suggestion);
        }
    }

    /// <summary>
    /// スペルミスを、直し方を添えて報告する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="token">指摘するトークン。</param>
    /// <param name="suggestion">正しい綴り。</param>
    /// <param name="messageArguments">メッセージへ埋め込む引数。</param>
    /// <remarks>
    /// <b>正しい綴りを知っているのはここだけである。</b>
    /// メッセージの文面から読み取らせると、文面を変えるたびに直しが壊れる。
    /// 診断に添えておけば、エディタはそれを 1 回の操作で適用できる。
    /// </remarks>
    private static void ReportMisspelling(
        NodeAnalysisContext<TagSyntax> context,
        DiagnosticDescriptor descriptor,
        SyntaxToken token,
        string suggestion,
        params object?[] messageArguments)
    {
        context.ReportDiagnostic(
            Diagnostic.Create(descriptor, Location.Create(context.Unit.Text, token.Span), messageArguments)
                .WithSuggestedReplacement(Requote(token, suggestion)));
    }

    /// <summary>
    /// 元のトークンに合わせて引用符を付け直す。
    /// </summary>
    /// <param name="token">元のトークン。</param>
    /// <param name="value">置き換える値。</param>
    /// <returns>そのまま範囲を置き換えられる文字列。</returns>
    /// <remarks>
    /// 指摘の範囲は引用符を含む。引用符を外した値で置き換えると
    /// <c>"Opaqu"</c> が <c>Opaque</c> になり、別の壊れ方をする。
    /// </remarks>
    private static string Requote(SyntaxToken token, string value)
        => token.Text.StartsWith('"') ? $"\"{value}\"" : value;

    /// <summary>
    /// <c>Queue</c> タグの値を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="tag">検査するタグ。</param>
    /// <remarks>
    /// 値は <c>Transparent+100</c> のように基準名と数値オフセットの組み合わせを取れる。
    /// 純粋な数値 (<c>2500</c> など) も指定できるため、それらは妥当とみなす。
    /// </remarks>
    private static void ValidateQueue(NodeAnalysisContext<TagSyntax> context, TagSyntax tag)
    {
        string value = tag.Value.Trim();

        if (value.Length == 0)
        {
            Report(context, tag, "描画キュー名または数値を指定してください。");
            return;
        }

        // 数値のみの指定は妥当。
        if (int.TryParse(value, out _))
        {
            return;
        }

        (string baseName, string offset) = SplitQueueOffset(value);

        if (!ShaderLabKnownValues.RenderQueues.Contains(baseName))
        {
            Report(context, tag,
                $"指定できる値: {string.Join(", ", ShaderLabKnownValues.RenderQueues.Order(StringComparer.Ordinal))} (数値のオフセットを付けられます)");
            return;
        }

        if (offset.Length > 0 && !int.TryParse(offset, out _))
        {
            Report(context, tag, "オフセットは整数で指定してください (例: Transparent+100)。");
        }
    }

    /// <summary>
    /// 描画キューの値を基準名とオフセットに分解する。
    /// </summary>
    /// <param name="value">分解する値。</param>
    /// <returns>基準名と、符号を含むオフセット文字列 (無い場合は空)。</returns>
    private static (string BaseName, string Offset) SplitQueueOffset(string value)
    {
        int signIndex = value.IndexOfAny(['+', '-']);
        return signIndex < 0
            ? (value, string.Empty)
            : (value[..signIndex].TrimEnd(), value[signIndex..].Trim());
    }

    /// <summary>値が許容集合に含まれるかを検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="tag">検査するタグ。</param>
    /// <param name="allowed">許容される値の集合。</param>
    /// <param name="hint">エラーメッセージに添える助言。</param>
    private static void ValidateAgainstSet(
        NodeAnalysisContext<TagSyntax> context,
        TagSyntax tag,
        FrozenSet<string> allowed,
        string hint)
    {
        if (!allowed.Contains(tag.Value))
        {
            Report(context, tag, hint);
        }
    }

    /// <summary>タグ値の不正を報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="tag">対象のタグ。</param>
    /// <param name="hint">助言。</param>
    private static void Report(NodeAnalysisContext<TagSyntax> context, TagSyntax tag, string hint)
        => context.ReportDiagnostic(
            ShaderLabRuleDescriptors.InvalidTagValue, tag.ValueToken.Span, tag.Key, tag.Value, hint);

    /// <summary>真偽値タグに指定できる値。</summary>
    private static readonly FrozenSet<string> BooleanValues =
        new[] { "True", "False" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
