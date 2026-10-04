using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// Unity の環境。
/// </summary>
/// <param name="options">Unity の環境の設定。</param>
/// <remarks>
/// <para>
/// <c>.shader</c> は ShaderLab として読み、埋め込みの HLSL をコードブロックごとに解析する。
/// <c>.compute</c> / <c>.hlsl</c> / <c>.cginc</c> / <c>.hlslinc</c> は HLSL だけのファイルとして読む。
/// どの言語として読むかは拡張子だけで決める。<c>.compute</c> を ShaderLab として読むと、正しいファイルに SL0001 が出る。
/// </para>
/// <para>
/// フォルダーを指定したときは <c>.shader</c> と <c>.compute</c> を探す。
/// <c>.hlsl</c> と <c>.cginc</c> は単体では成立しない断片であることが多いので、名前を挙げたときだけ解析する。
/// </para>
/// </remarks>
internal sealed class UnityAnalysisEnvironment(UnityOptions options) : IAnalysisEnvironment
{
    /// <summary>名前。</summary>
    public const string EnvironmentName = "unity";

    /// <summary>Unity の環境の設定。</summary>
    public UnityOptions Options { get; } = options;

    /// <inheritdoc/>
    public string Name => EnvironmentName;

    /// <inheritdoc/>
    public string DisplayName => "Unity";

    /// <inheritdoc/>
    public ImmutableArray<string> DefaultExtensions { get; } = [".shader", ".compute"];

    /// <inheritdoc/>
    public PreparedFile Prepare(SourceText text, SemanticsOptions? semantics, AnalysisTimings? timings)
    {
        ArgumentNullException.ThrowIfNull(text);

        bool isHlsl = ShaderSourceKinds.FromPath(text.FilePath) == ShaderSourceKind.Hlsl;

        long start = AnalysisTimings.Now();
        ShaderLabSyntaxTree? tree = isHlsl ? null : ShaderLabSyntaxTree.Parse(text);
        timings?.Add(AnalysisStep.ShaderLab, start);

        // 意味解析を行う場合、解析単位の組み立てはセマンティックモデル側に任せる。
        // 埋め込み HLSL の構文エラーは、そこで初めて分かる解析基盤の診断であり、
        // ShaderLab の構文診断とあわせて渡さなければ出力に現れない。
        long expandStart = AnalysisTimings.Now();
        ShaderCompilation? compilation = semantics is null
            ? null
            : tree is null
                ? UnityCompilation.CreateForHlsl(text, semantics, Options)
                : UnityCompilation.Create(text, tree, semantics, Options);
        timings?.Add(AnalysisStep.Expand, expandStart);

        // 意味解析を省いた場合、HLSL 単体ファイルには見るものが無い。
        // ShaderLab 単体で完結するルールに、対応する構文が 1 つも無いためである。
        long unitStart = AnalysisTimings.Now();
        AnalysisTarget target = compilation is not null
            ? compilation.CreateAnalysisTarget()
            : tree is null
                ? new AnalysisTarget(text, null, [])
                : new AnalysisTarget(text, tree.Root, tree.Diagnostics);
        timings?.Add(AnalysisStep.Target, unitStart);

        return new PreparedFile(target, compilation?.AllIncludePaths ?? [], compilation);
    }
}
