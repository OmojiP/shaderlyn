using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Shaderlyn.Semantics.Profiles;

/// <summary>
/// 利用できるレンダーパイプラインプロファイルの一覧。
/// </summary>
/// <remarks>
/// Native AOT ではアセンブリを走査して実装を自動収集できないため、
/// 静的な配列として明示的に列挙する。
/// </remarks>
internal static class RenderPipelineProfiles
{
    /// <summary>実装されている全プロファイル。</summary>
    public static ImmutableArray<IRenderPipelineProfile> All { get; } =
    [
        new UrpProfile(),
        new BuiltInPipelineProfile(),
        new HdrpProfile(),
    ];

    /// <summary>
    /// 既定のプロファイル。
    /// </summary>
    /// <remarks>
    /// URP を既定にしている。ただし URP 固有のルールは
    /// <see cref="IRenderPipelineProfile.AppliesTo"/> による裏付けを取ってから適用されるため、
    /// Built-in のシェーダーが混ざったプロジェクトでも誤検出にはならない。
    /// </remarks>
    public static IRenderPipelineProfile Default { get; } = All[0];

    /// <summary>
    /// 名前からプロファイルを引く。
    /// </summary>
    /// <param name="name">プロファイル名。</param>
    /// <param name="profile">見つかったプロファイル。</param>
    /// <returns>見つかった場合は <see langword="true"/>。</returns>
    public static bool TryGet(string name, [NotNullWhen(true)] out IRenderPipelineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (IRenderPipelineProfile candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                profile = candidate;
                return true;
            }
        }

        profile = null;
        return false;
    }

    /// <summary>指定できるプロファイル名を並べた文字列を返す。</summary>
    /// <returns>エラーメッセージやヘルプに使う文字列。</returns>
    public static string GetAvailableNames() => string.Join(", ", All.Select(p => p.Name));
}
