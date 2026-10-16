using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules;

/// <summary>
/// 解決できない <c>#include</c> を、その行で報告する (HL0321)。
/// </summary>
/// <remarks>
/// <para>
/// <b>解決できなかったことは HL0005 が伝えるが、場所は伝えない。</b>
/// HL0005 はファイルの先頭に出る 1 件の報告であり、
/// パスを綴り間違えた行がどこかは分からない。
/// 直す相手が 1 行に決まっているなら、その行を指すべきである。
/// </para>
/// <para>
/// <b>1 件も解決できていないときは、原因はパスではなく環境である。</b>
/// Unity のヘッダを引けない状態は「シェーダーの誤り」ではなく
/// 「解析の前提が揃っていない」状態であり、直す相手が違う。
/// そこで <c>HL0321</c> を 1 行ずつ並べる代わりに、
/// <c>TOOL0004</c> をファイルごとに 1 件だけ、直し方とともに報告する。
/// </para>
/// <para>
/// <b>どちらの場合もエラーにする。</b>
/// ヘッダの中身を知らないまま行った検査は、出た指摘も出なかった指摘も当てにならない。
/// かつては警告のまま解析を続けていたが、
/// 「精度が落ちたことが分からない結果」を利用者が信じてしまう形になっていた。
/// </para>
/// </remarks>
internal sealed class IncludePathAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslDescriptors.UnresolvedInclude, WellKnownDescriptors.IncludesUnavailable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 範囲の外で読まなかったものがあるなら、原因は環境ではなく範囲である。1 行ずつ理由を添えて報告する。
        if (!HasResolvedAnything(compilation) && !HasOutsideBoundary(compilation))
        {
            ReportEnvironment(context, compilation);
            return;
        }

        // 同じ 1 行が複数のパスに入る (HLSLINCLUDE)。報告は場所ごとに 1 回でよい。
        HashSet<(string File, TextSpan Span)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (IncludeReference include in program.Tree.PreprocessResult.Includes)
            {
                if (include.IsResolved
                    || compilation.Environment.IsGeneratedInclude(include.Path)
                    || !IsWrittenInSource(compilation, include)
                    || !reported.Add((include.Location.FilePath, include.Location.Span)))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    HlslDescriptors.UnresolvedInclude, include.Location, include.Path,
                    compilation.DescribeUnresolvedInclude?.Invoke(include) ?? DescribeOutsideBoundary(include)));
            }
        }
    }

    /// <summary>読んでよい範囲の外にあったために読まなかった include があるかを判定する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    private static bool HasOutsideBoundary(ShaderCompilation compilation)
        => compilation.Programs.Any(p => p.Tree.PreprocessResult.Includes.Any(i => i.OutsideBoundaryPath is not null));

    /// <summary>範囲の外にファイルがあったなら、そのことと直し方を伝える文。</summary>
    /// <param name="include">解決できなかった include。</param>
    /// <returns>文。無ければ空。</returns>
    /// <remarks>
    /// 読んでよい範囲は、ファイルを読ませる 1 行で任意のファイルを読めないようにするためにある
    /// (<see cref="Hlsl.Preprocessing.IncludeBoundary"/>)。「見つからない」と言うと、利用者はパスの誤りを探してしまう。
    /// </remarks>
    private static string DescribeOutsideBoundary(IncludeReference include)
        => include.OutsideBoundaryPath is { } path
            ? "読んでよい範囲の外にあるため読みません。読む場合は --include-path (設定ファイルは include-paths) に加えてください。"
            : string.Empty;

    /// <summary>
    /// この解析で include を 1 件でも解決できたかを判定する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>1 件でも解決できていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 解決の仕組みが働いていることの確認である。
    /// 1 件も解決できていないなら、原因はパスではなく設定にある。
    /// </remarks>
    private static bool HasResolvedAnything(ShaderCompilation compilation)
        => compilation.Programs.Any(p => !p.Tree.PreprocessResult.ResolvedIncludes.IsEmpty);

    /// <summary>
    /// 解析の前提が揃っていないことを、ファイルごとに 1 件だけ報告する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <remarks>
    /// <b>直し方を書く。</b>
    /// この状態に落ちる原因はほぼ 2 つ (指定していない / CI がキャッシュしていない) であり、
    /// 「見つかりません」とだけ言われても利用者は次に何をすればよいか分からない。
    /// 引けなかったヘッダを 1 つ添えて、何を探しに行ったのかも示す。
    /// </remarks>
    private static void ReportEnvironment(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation)
    {
        // このファイルが書いた #include が 1 つも無いなら、そもそも解決するものが無い。
        // 「引けなかった」ではないので何も言わない。
        List<IncludeReference> unresolved =
        [
            .. compilation.Programs
                .SelectMany(p => p.Tree.PreprocessResult.Includes)
                .Where(i => !i.IsResolved
                            && !compilation.Environment.IsGeneratedInclude(i.Path)
                            && IsWrittenInSource(compilation, i)),
        ];

        if (unresolved.Count == 0)
        {
            return;
        }

        IncludeReference include = unresolved[0];

        context.ReportDiagnostic(Diagnostic.Create(
            WellKnownDescriptors.IncludesUnavailable,
            include.Location,
            $"'{include.Path}'",
            compilation.DescribeUnresolvedInclude?.Invoke(include) ?? DescribeFix(include.Path)));
    }

    /// <summary>
    /// 引けなかったパスの形から、直し方を選ぶ。
    /// </summary>
    /// <param name="path">書かれたままのパス。</param>
    /// <returns>直し方の文面。</returns>
    /// <remarks>
    /// <para>
    /// <b>パスの形を見ずに Unity の直し方を案内しない。</b>
    /// <c>definitely/missing.hlsl</c> のようなパスに「--unity-project を渡す」
    /// 「PackageCache をキャッシュする」と言っても直らない。
    /// 利用者は効果の無い設定を試して時間を失う。
    /// </para>
    /// <para>
    /// CLI とエディタで同じ文面を使うため、オプション名だけで済ませない。
    /// VS Code には <c>--unity-project</c> を渡す場所が無い。
    /// </para>
    /// </remarks>
    private static string DescribeFix(string path)
    {
        string normalized = path.Replace('\\', '/');

        if (normalized.StartsWith("/Engine/", StringComparison.Ordinal)
            || normalized.StartsWith("/Plugin/", StringComparison.Ordinal)
            || normalized.StartsWith("/Project/", StringComparison.Ordinal))
        {
            return "Unreal の仮想パスを参照しています。設定ファイルの unreal: に、エンジンの場所 (engine) と"
                + "プロジェクト・プラグインのフォルダー (project / virtual-paths) を書いてください。";
        }

        if (normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            // CLI は、--unity-project を付けていないなら見つけたプロジェクトを添えた文を作る (DescribeUnresolvedInclude)。
            // ここに来るのは、付けているのにパッケージを引けない場合か、言語サーバーの場合である。
            return "Unity プロジェクトとして解析する場合は --unity-project でプロジェクトのルートを指定してください"
                + " (指定済みなら、Unity でプロジェクトを 1 度開いて Library/PackageCache を作ってください)。";
        }

        // Unity 同梱のヘッダ (UnityCG.cginc / Lighting.cginc など) は、フォルダーを付けずに .cginc で書く。
        if (!normalized.Contains('/', StringComparison.Ordinal)
            && normalized.EndsWith(".cginc", StringComparison.OrdinalIgnoreCase))
        {
            return $"{path} は Unity Editor 同梱のヘッダです。Unity Editor が見つかりません。--unity-editor で場所を指定してください。";
        }

        return "ヘッダの場所を --include-path (設定ファイルは include-paths) に加えてください。";
    }

    /// <summary>
    /// <c>#include</c> が利用者のファイルに書かれたものかを判定する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="include">判定する <c>#include</c>。</param>
    /// <returns>このファイルか、利用者が書いたヘッダに書かれたものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// Unity や外部パッケージのヘッダの中の <c>#include</c> は、利用者に直しようがない。
    /// 利用者が書いた共通の <c>.hlsl</c> の中の <c>#include</c> は直せる。
    /// </remarks>
    private static bool IsWrittenInSource(ShaderCompilation compilation, IncludeReference include)
        => compilation.IsUserFile(include.Location.FilePath);
}
