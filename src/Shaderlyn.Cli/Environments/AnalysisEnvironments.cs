using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// このツールが持つ環境の一覧。
/// </summary>
/// <remarks>
/// <b>環境は 1 つの CLI にまとめて持つ。</b> 環境ごとの差はコードと表で、バイナリはほとんど大きくならない。
/// 分けると、配布物・VS Code 拡張・GitHub Action が環境の数だけ増える。
/// 1 つのリポジトリで複数の環境を使うときも、設定ファイルの <c>environment</c> を切り替えるだけで済む。
/// </remarks>
internal static class AnalysisEnvironments
{
    /// <summary>指定できる環境の名前。</summary>
    public static ImmutableArray<string> Names { get; } = [UnityAnalysisEnvironment.EnvironmentName, Semantics.Environments.HlslEnvironment.EnvironmentName];

    /// <summary>
    /// 設定から環境を作る。
    /// </summary>
    /// <param name="settings">設定ファイルとコマンドラインを合わせた設定。</param>
    /// <param name="environment">作った環境。</param>
    /// <returns>知っている名前なら <see langword="true"/>。</returns>
    public static bool TryCreate(EffectiveSettings settings, [NotNullWhen(true)] out IAnalysisEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(settings);

        environment = settings.Environment switch
        {
            UnityAnalysisEnvironment.EnvironmentName => new UnityAnalysisEnvironment(SemanticsSetup.CreateUnity(settings)),
            Semantics.Environments.HlslEnvironment.EnvironmentName => new HlslAnalysisEnvironment(settings.Configuration),
            _ => null,
        };

        return environment is not null;
    }

    /// <summary>知らない環境の名前を伝える文。</summary>
    /// <param name="name">指定された名前。</param>
    /// <returns>指定できる名前を添えた文。</returns>
    public static string DescribeUnknown(string name)
        => $"環境 '{name}' はありません。指定できる環境: {string.Join(", ", Names)}";
}
