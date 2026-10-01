using System.Collections.Immutable;

namespace Shaderlyn.Semantics.Profiles;

/// <summary>
/// Universal Render Pipeline (URP) の知識。
/// </summary>
/// <remarks>
/// 収録している知識は URP 14 系 (Unity 2022 LTS) 以降を基準にしている。
/// この範囲では、ここで扱っている規約 (定数バッファの名前、変換関数の名前) に変更は無い。
/// </remarks>
internal sealed class UrpProfile : IRenderPipelineProfile
{
    /// <summary>
    /// URP であることの根拠となる、取り込みパスに含まれる文字列。
    /// </summary>
    /// <remarks>
    /// <c>core</c> パッケージも根拠に含めている。
    /// URP のシェーダーは <c>Core.hlsl</c> を経由して SRP Core のヘッダを取り込むが、
    /// SRP Core だけを直接使うシェーダー (共通のユーティリティのみ利用) も
    /// Built-in 用ではないため、URP の規約を適用してよい。
    /// </remarks>
    private static readonly string[] PipelineIncludeMarkers =
    [
        "com.unity.render-pipelines.universal",
        "com.unity.render-pipelines.core",
    ];

    /// <summary>
    /// このプロファイルの対象外であることを示す、取り込みパスに含まれる文字列。
    /// </summary>
    /// <remarks>
    /// <b>HDRP のシェーダーも SRP Core を取り込む。</b>
    /// そのため <see cref="PipelineIncludeMarkers"/> だけで判定すると、
    /// HDRP のシェーダーを URP のものとみなしてしまう。
    /// 規約の多くは SRP 共通だが、診断メッセージが誤ったパイプライン名を名乗るうえ、
    /// URP に固有の知識まで当てはめることになる。
    /// </remarks>
    private static readonly string[] ExcludedIncludeMarkers =
    [
        "com.unity.render-pipelines.high-definition",
    ];

    /// <inheritdoc/>
    public string Name => "urp";

    /// <inheritdoc/>
    public string DisplayName => "Universal Render Pipeline";

    /// <inheritdoc/>
    public string? MaterialConstantBufferName => "UnityPerMaterial";

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// 収録しているのは、URP で<b>動作しない、あるいは意図しない結果になる</b>ものに限る。
    /// 「古い書き方だが動く」ものは含めない。動くものを機械的に指摘すると、
    /// 移行の判断をルールが奪ってしまう。
    /// </para>
    /// <para>
    /// <c>UNITY_MATRIX_MVP</c> は URP では定義されているものの、
    /// SRP Batcher と相性が悪く公式に非推奨とされている。
    /// </para>
    /// </remarks>
    public ImmutableDictionary<string, string> ObsoleteApis { get; } =
        ImmutableDictionary<string, string>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add("UnityObjectToClipPos", "TransformObjectToHClip")
            .Add("UnityObjectToWorldNormal", "TransformObjectToWorldNormal")
            .Add("UnityObjectToWorldDir", "TransformObjectToWorldDir")
            .Add("UnityWorldToObjectDir", "TransformWorldToObjectDir")
            .Add("UnityWorldSpaceViewDir", "GetWorldSpaceViewDir")
            .Add("WorldSpaceViewDir", "GetWorldSpaceViewDir")
            .Add("UnityWorldSpaceLightDir", "GetMainLight")
            .Add("ShadeVertexLights", "GetMainLight / GetAdditionalLight")
            .Add("UNITY_MATRIX_MVP", "UNITY_MATRIX_VP と UNITY_MATRIX_M の組み合わせ")
            .Add("tex2D", "SAMPLE_TEXTURE2D")
            .Add("tex2Dlod", "SAMPLE_TEXTURE2D_LOD")
            .Add("tex2Dbias", "SAMPLE_TEXTURE2D_BIAS")
            .Add("tex2Dgrad", "SAMPLE_TEXTURE2D_GRAD")
            .Add("tex3D", "SAMPLE_TEXTURE3D")
            .Add("texCUBE", "SAMPLE_TEXTURECUBE")
            .Add("texCUBElod", "SAMPLE_TEXTURECUBE_LOD");

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// 判定の根拠は「URP または SRP Core のヘッダを取り込んでいるか」である。
    /// URP のシェーダーは例外なく <c>Core.hlsl</c> かそれに類するヘッダを取り込むため、
    /// これを根拠にできる。
    /// </para>
    /// <para>
    /// <b>取り込みパスは、解決できたかどうかにかかわらず判定に使う。</b>
    /// Unity をインストールしていない環境ではヘッダを読めないが、
    /// <c>#include</c> にそう書いてあること自体が URP を使っている証拠である。
    /// 解決できた場合だけ判定できる作りにすると、CI で URP のルールが
    /// 何も伝えられないまま効かなくなる。
    /// </para>
    /// </remarks>
    public bool AppliesTo(ShaderCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        bool matched = false;

        foreach (string path in compilation.AllIncludePaths)
        {
            foreach (string excluded in ExcludedIncludeMarkers)
            {
                if (path.Contains(excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            foreach (string marker in PipelineIncludeMarkers)
            {
                if (path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                }
            }
        }

        return matched;
    }
}
