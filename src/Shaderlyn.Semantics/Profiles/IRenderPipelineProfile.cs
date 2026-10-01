using System.Collections.Immutable;

namespace Shaderlyn.Semantics.Profiles;

/// <summary>
/// レンダーパイプラインごとに異なる知識をまとめた差し替え点。
/// </summary>
/// <remarks>
/// <para>
/// <b>パイプライン依存の知識をルールに直接書かないための境界である。</b>
/// 「<c>UnityPerMaterial</c> という名前の定数バッファ」も
/// 「<c>UnityObjectToClipPos</c> は使わない」も URP に固有の話であり、
/// Built-in パイプラインのプロジェクトでそのまま指摘すると誤りになる。
/// </para>
/// <para>
/// 当面 URP のみを本格的に実装するが、
/// <b>他のパイプラインへ差し替えられる形にしておくことが要件である</b>。
/// そのため Built-in と HDRP についても、値が空であっても実体のあるクラスを用意し、
/// 差し替え点が実在することを保証している。
/// </para>
/// <para>
/// パイプラインに依存しないルール (プロパティと uniform の対応など) は、
/// この型を一切参照してはならない。
/// </para>
/// </remarks>
public interface IRenderPipelineProfile
{
    /// <summary>設定ファイルやコマンドラインで指定する名前。</summary>
    string Name { get; }

    /// <summary>人間向けの表示名。</summary>
    string DisplayName { get; }

    /// <summary>
    /// マテリアルのプロパティを収める定数バッファの名前。
    /// この概念を持たないパイプラインでは <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// SRP では、マテリアルごとの値をこの名前の定数バッファへまとめて置くことで
    /// SRP Batcher が有効になる。名前は規約であり、1 文字でも違うと機能しない。
    /// </remarks>
    string? MaterialConstantBufferName { get; }

    /// <summary>
    /// このパイプラインで使うべきでない API と、その代替。
    /// </summary>
    /// <remarks>
    /// キーが使用を避けるべき識別子、値が推奨される代替。
    /// 代替が無い場合は空文字列を入れる。
    /// </remarks>
    ImmutableDictionary<string, string> ObsoleteApis { get; }

    /// <summary>
    /// このプロファイルの知識が、対象のシェーダーに当てはまるかを判定する。
    /// </summary>
    /// <param name="compilation">判定対象のシェーダー。</param>
    /// <returns>当てはまる場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>プロファイルの指定だけを根拠に、パイプライン固有のルールを適用してはならない。</b>
    /// 1 つのプロジェクトに URP のシェーダーと Built-in 時代のシェーダーが
    /// 混在しているのは珍しくない。プロファイルが URP だからといって、
    /// <c>UnityCG.cginc</c> しか使っていないシェーダーに
    /// 「<c>TransformObjectToHClip</c> を使え」と指摘するのは誤りである。
    /// </para>
    /// <para>
    /// この判定は、対象のシェーダー自身が持つ根拠 (取り込んでいるヘッダなど) に基づいて行う。
    /// </para>
    /// </remarks>
    bool AppliesTo(ShaderCompilation compilation);
}
