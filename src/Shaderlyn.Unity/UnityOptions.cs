using System.Collections.Immutable;
using Shaderlyn.Unity.Profiles;

namespace Shaderlyn.Unity;

/// <summary>
/// Unity の環境の設定。
/// </summary>
/// <remarks>
/// 環境に依らない設定 (include の解決、構成の上限など) は <see cref="Semantics.SemanticsOptions"/> に置く。
/// </remarks>
public sealed record UnityOptions
{
    /// <summary>既定の設定。</summary>
    public static UnityOptions Default { get; } = new();

    /// <summary>
    /// 定義済みマクロを指定しない場合に使うもの (<see cref="Semantics.SemanticsOptions.PredefinedMacros"/> が <see langword="null"/> のとき)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>単一構成での展開なので、どれか 1 つの構成を選ばなければならない</b>。
    /// ここでは D3D11 向けの構成を選んでいる。Unity のヘッダは
    /// <c>SHADER_API_*</c> のいずれも定義されていないと取り込むファイルを決められず、
    /// 何も定義しないままでは解析が成立しないためである。
    /// </para>
    /// <para>
    /// グラフィックス API の選択は uniform の宣言にはほとんど影響しない。
    /// 影響するのはテクスチャのサンプリング方法や精度の扱いであり、
    /// 現在のルールが見ている範囲の外にある。
    /// </para>
    /// <para>
    /// 別の構成で解析したい場合は <c>--define</c> で上書きする。
    /// </para>
    /// </remarks>
    public static ImmutableDictionary<string, string> DefaultPredefinedMacros { get; } =
        ImmutableDictionary<string, string>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add("SHADER_API_D3D11", "1")
            .Add("SHADER_API_DESKTOP", "1")
            .Add("UNITY_COMPILER_HLSL", "1")
            .Add("SHADER_TARGET", "45")

            // Unity 6 を想定する。#if UNITY_VERSION >= ... という分岐が
            // ヘッダの各所にあり、未定義だと 0 として扱われて古い側の経路が選ばれる。
            .Add("UNITY_VERSION", "600000");

    /// <summary>適用するレンダーパイプラインプロファイル。</summary>
    public IRenderPipelineProfile Profile { get; init; } = RenderPipelineProfiles.Default;

    /// <summary>
    /// Unity のヘッダを取り込めなかった場合に備えて、既定のマクロ定義を先頭に差し込むかどうか。
    /// </summary>
    /// <remarks>
    /// 既定で有効。詳細は <see cref="UnityShaderStubs"/> を参照。
    /// 無効にするのは、先頭に差し込むコードの影響を切り分けて調べたい場合に限られる。
    /// </remarks>
    public bool UseFallbackMacros { get; init; } = true;

    /// <summary>
    /// Unity のバージョン (<c>6000.3.0f1</c> / <c>6.3</c> など)。分からなければ <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// シェーダーのコンパイラの選び方がバージョンで変わる (<see cref="UnityCompilerSelection"/>)。
    /// 分からなければ、どのバージョンの結果も並べる。
    /// </remarks>
    public string? EditorVersion { get; init; }

    /// <summary><see cref="EditorVersion"/> のまとまり。分からなければ <see langword="null"/>。</summary>
    public UnityVersionBand? VersionBand => UnityVersions.TryGetBand(EditorVersion, out UnityVersionBand? band) ? band : null;

    /// <summary>
    /// シェーダーをコンパイルするグラフィックス API。既定は DX11 と DX12 (<see cref="UnityGraphicsApis.Default"/>)。
    /// </summary>
    public ImmutableArray<UnityGraphicsApi> GraphicsApis { get; init; } = UnityGraphicsApis.Default;

    /// <summary>
    /// Shader Compiler Backend を DXC にした API (Unity 6.6 から)。既定は無し。
    /// </summary>
    /// <remarks>DX11 は設定に関わらず FXC でコンパイルされる。</remarks>
    public ImmutableArray<UnityGraphicsApi> DxcBackendApis { get; init; } = [];
}
