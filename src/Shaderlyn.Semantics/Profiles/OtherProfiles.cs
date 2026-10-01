using System.Collections.Immutable;

namespace Shaderlyn.Semantics.Profiles;

/// <summary>
/// Built-in レンダーパイプラインの知識。
/// </summary>
/// <remarks>
/// <para>
/// <b>意図的に空である。</b>
/// Built-in には SRP Batcher が無く、定数バッファの命名規約も存在しない。
/// 「非推奨の API」も、Built-in においては <c>UnityObjectToClipPos</c> などが正規の書き方である。
/// </para>
/// <para>
/// それでもクラスとして存在させているのは、
/// <b>差し替え点が実在することを保証するため</b>である。
/// 「今は URP だけ」という理由で URP の知識をルールへ直接書き込むと、
/// 後から他のパイプラインに対応する際に、どこがパイプライン依存だったのかを
/// 全ルールから掘り起こすことになる。
/// </para>
/// </remarks>
internal sealed class BuiltInPipelineProfile : IRenderPipelineProfile
{
    /// <inheritdoc/>
    public string Name => "brp";

    /// <inheritdoc/>
    public string DisplayName => "Built-in Render Pipeline";

    /// <inheritdoc/>
    /// <remarks>Built-in には SRP Batcher が無く、この概念が存在しない。</remarks>
    public string? MaterialConstantBufferName => null;

    /// <inheritdoc/>
    public ImmutableDictionary<string, string> ObsoleteApis { get; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <inheritdoc/>
    /// <remarks>
    /// 固有のルールを 1 つも持たないため、当てはまるかどうかを判定する意味が無い。
    /// 常に <see langword="true"/> を返しても、有効になるルールは存在しない。
    /// </remarks>
    public bool AppliesTo(ShaderCompilation compilation) => true;
}

/// <summary>
/// High Definition Render Pipeline (HDRP) の知識。
/// </summary>
/// <remarks>
/// <para>
/// <b>定数バッファの名前のみを収録した最小限の実装である。</b>
/// HDRP のシェーダーは <c>ShaderGraph</c> から生成されるものが大半で、
/// 手書きのシェーダーに対する検査の需要が URP ほど明確でない。
/// </para>
/// <para>
/// 収録していない知識について報告しないのは、
/// 裏付けの無い指摘を出すより望ましい。
/// 実際に HDRP のプロジェクトで使う段になったら、
/// 誤検出を実測しながら知識を足していくこと。
/// </para>
/// </remarks>
internal sealed class HdrpProfile : IRenderPipelineProfile
{
    /// <inheritdoc/>
    public string Name => "hdrp";

    /// <inheritdoc/>
    public string DisplayName => "High Definition Render Pipeline";

    /// <inheritdoc/>
    /// <remarks>HDRP も SRP であり、SRP Batcher の規約は URP と共通である。</remarks>
    public string? MaterialConstantBufferName => "UnityPerMaterial";

    /// <inheritdoc/>
    public ImmutableDictionary<string, string> ObsoleteApis { get; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool AppliesTo(ShaderCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        return compilation.AllIncludePaths.Any(
            path => path.Contains("com.unity.render-pipelines.high-definition", StringComparison.OrdinalIgnoreCase));
    }
}
