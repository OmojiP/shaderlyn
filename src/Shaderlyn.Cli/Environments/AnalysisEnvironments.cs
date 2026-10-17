using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// このツールが持つ環境の一覧。
/// </summary>
/// <remarks>
/// <para>
/// <b>環境は 1 つの CLI にまとめて持つ。</b> 環境ごとの差はコードと表で、バイナリはほとんど大きくならない。
/// 分けると、配布物・VS Code 拡張・GitHub Action が環境の数だけ増える。
/// 1 つのリポジトリで複数の環境を使うときも、設定ファイルの <c>environment</c> を切り替えるだけで済む。
/// </para>
/// <para>
/// 自前のホストが足した環境 (<see cref="HostEnvironment"/>) は、組み込みの環境の後ろに並ぶ。
/// </para>
/// </remarks>
internal static class AnalysisEnvironments
{
    /// <summary>組み込みの環境の名前。</summary>
    public static ImmutableArray<string> BuiltInNames { get; } =
    [
        UnityAnalysisEnvironment.EnvironmentName,
        Semantics.Environments.HlslEnvironment.EnvironmentName,
        Unreal.UnrealOptions.EnvironmentName,
        SyntaxOnlyAnalysisEnvironment.GlslName,
        SyntaxOnlyAnalysisEnvironment.GodotName,
        SyntaxOnlyAnalysisEnvironment.SlangName,
        SyntaxOnlyAnalysisEnvironment.WgslName,
    ];

    /// <summary>指定できる環境の名前。</summary>
    /// <param name="hosts">自前のホストが足した環境。</param>
    /// <returns>組み込みの名前と、足した環境の名前。</returns>
    public static ImmutableArray<string> Names(ImmutableArray<HostEnvironment> hosts)
        => [.. BuiltInNames, .. hosts.IsDefault ? [] : hosts.Select(h => h.Name)];

    /// <summary>
    /// 自前のホストが足した環境を確かめる。
    /// </summary>
    /// <param name="hosts">足した環境。</param>
    /// <exception cref="ArgumentException">名前が組み込みの環境か、ほかの足した環境と重なっている。</exception>
    public static void Validate(ImmutableArray<HostEnvironment> hosts)
    {
        if (hosts.IsDefaultOrEmpty)
        {
            return;
        }

        HashSet<string> names = [.. BuiltInNames];

        foreach (HostEnvironment host in hosts)
        {
            ArgumentNullException.ThrowIfNull(host);

            if (string.IsNullOrWhiteSpace(host.Name) || !names.Add(host.Name))
            {
                throw new ArgumentException(
                    $"環境の名前 '{host.Name}' は使えません。組み込みの環境 ({string.Join(", ", BuiltInNames)}) やほかの環境と重ならない名前にしてください。",
                    nameof(hosts));
            }
        }
    }

    /// <summary>
    /// 設定から環境を作る。
    /// </summary>
    /// <param name="settings">設定ファイルとコマンドラインを合わせた設定。足した環境も持つ。</param>
    /// <param name="environment">作った環境。</param>
    /// <returns>知っている名前なら <see langword="true"/>。</returns>
    public static bool TryCreate(EffectiveSettings settings, [NotNullWhen(true)] out IAnalysisEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(settings);

        environment = settings.Environment switch
        {
            UnityAnalysisEnvironment.EnvironmentName => new UnityAnalysisEnvironment(SemanticsSetup.CreateUnity(settings)),
            Semantics.Environments.HlslEnvironment.EnvironmentName => new HlslAnalysisEnvironment(settings.Configuration),
            Unreal.UnrealOptions.EnvironmentName => CreateUnreal(settings),
            SyntaxOnlyAnalysisEnvironment.GlslName => SyntaxOnlyAnalysisEnvironment.CreateGlsl(),
            SyntaxOnlyAnalysisEnvironment.GodotName => SyntaxOnlyAnalysisEnvironment.CreateGodot(),
            SyntaxOnlyAnalysisEnvironment.SlangName => SyntaxOnlyAnalysisEnvironment.CreateSlang(),
            SyntaxOnlyAnalysisEnvironment.WgslName => SyntaxOnlyAnalysisEnvironment.CreateWgsl(),
            _ => settings.HostEnvironments.FirstOrDefault(h => h.Name == settings.Environment) is { } host
                ? host.CreateAnalysisEnvironment?.Invoke()
                  ?? new HlslAnalysisEnvironment(settings.Configuration, host.CreateEnvironment(), host.DisplayName, host.DefaultExtensions)
                : null,
        };

        return environment is not null;
    }

    /// <summary>Unreal Engine の環境を作る。</summary>
    /// <param name="settings">設定。</param>
    /// <returns>環境。</returns>
    /// <remarks>
    /// 設定の <c>units:</c> に書いていない <c>.usf</c> は、C++ のシェーダーの登録 (<c>--unreal-generate</c> が書き出したもの) から単位を作る。
    /// </remarks>
    private static HlslAnalysisEnvironment CreateUnreal(EffectiveSettings settings)
    {
        Unreal.UnrealOptions unreal = SemanticsSetup.CreateUnreal(settings);

        return new HlslAnalysisEnvironment(
            settings.Configuration,
            unreal.CreateEnvironment(HlslAnalysisEnvironment.BuildVariants(settings.Configuration, null)),
            "Unreal Engine",
            [".usf"],
            unreal.CreateRegisteredUnits());
    }

    /// <summary>知らない環境の名前を伝える文。</summary>
    /// <param name="name">指定された名前。</param>
    /// <param name="hosts">自前のホストが足した環境。</param>
    /// <returns>指定できる名前を添えた文。</returns>
    public static string DescribeUnknown(string name, ImmutableArray<HostEnvironment> hosts)
        => $"環境 '{name}' はありません。指定できる環境: {string.Join(", ", Names(hosts))}";
}
