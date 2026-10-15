using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;

namespace Shaderlyn.Unreal.Rules;

/// <summary>
/// エンジンが生成するファイルが無いために、宣言の検査を行っていないことを報告する (UE0001 / UE0002)。
/// </summary>
/// <remarks>
/// <para>
/// 解決できなかった取り込みが、どれもエンジンが生成するファイルであるときだけ報告する。
/// ほかの取り込みも解決できていなければ、設定の誤りの見込みが高いので <c>SL0002</c> に任せる (こちらは報告しない)。
/// </para>
/// <para>
/// C++ から作れるもの (uniform buffer の宣言など) が欠けていれば UE0001 で <c>--unreal-generate</c> を案内する。
/// 欠けているのがマテリアルごとに作られるもの (<see cref="UnrealOptions.MaterialGeneratedIncludes"/>) だけなら UE0002 にする。
/// </para>
/// </remarks>
internal sealed class GeneratedIncludeAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>メッセージに列挙するファイルの最大数。</summary>
    private const int MaxListedIncludes = 3;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [UnrealRuleDescriptors.GeneratedFilesMissing, UnrealRuleDescriptors.MaterialGeneratedFilesUnknown];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasHlslPrograms
            || compilation.HasCompleteDependencies
            || !IsOnlyGeneratedMissing(compilation))
        {
            return;
        }

        ImmutableArray<string> missing = compilation.UnresolvedIncludes;
        ImmutableArray<string> buildable = [.. missing.Where(path => !UnrealOptions.IsMaterialGeneratedInclude(path))];

        context.ReportDiagnostic(
            buildable.IsEmpty ? UnrealRuleDescriptors.MaterialGeneratedFilesUnknown : UnrealRuleDescriptors.GeneratedFilesMissing,
            new Core.Text.TextSpan(0, 0),
            List(buildable.IsEmpty ? missing : buildable));
    }

    /// <summary>Unreal の環境で、宣言の一覧が欠けている理由がエンジンが生成するファイルだけかを判定する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>そうなら <see langword="true"/>。<c>SL0002</c> (<see cref="SkippedCheckAnalyzer"/>) は、このときは報告しない。</returns>
    private static bool IsOnlyGeneratedMissing(ShaderCompilation compilation)
        => compilation.Environment.Name == UnrealOptions.EnvironmentName
           && SkippedCheckAnalyzer.IsOnlyGeneratedIncludeMissing(compilation);

    private static string List(ImmutableArray<string> paths)
        => string.Join(", ", paths.Take(MaxListedIncludes))
           + (paths.Length > MaxListedIncludes ? $" ほか {paths.Length - MaxListedIncludes} 件" : string.Empty);
}
