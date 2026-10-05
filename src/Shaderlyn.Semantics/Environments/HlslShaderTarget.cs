using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Shaderlyn.Hlsl.Parsing;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// シェーダーのステージ。値は DXC の <c>__SHADER_STAGE_*</c> と同じ。
/// </summary>
public enum HlslShaderStage
{
    /// <summary>ピクセルシェーダー (<c>ps</c>)。</summary>
    Pixel = 0,

    /// <summary>頂点シェーダー (<c>vs</c>)。</summary>
    Vertex = 1,

    /// <summary>ジオメトリシェーダー (<c>gs</c>)。</summary>
    Geometry = 2,

    /// <summary>ハルシェーダー (<c>hs</c>)。</summary>
    Hull = 3,

    /// <summary>ドメインシェーダー (<c>ds</c>)。</summary>
    Domain = 4,

    /// <summary>コンピュートシェーダー (<c>cs</c>)。</summary>
    Compute = 5,

    /// <summary>ライブラリ (<c>lib</c>)。レイトレーシングのシェーダーなど。</summary>
    Library = 6,

    /// <summary>メッシュシェーダー (<c>ms</c>)。</summary>
    Mesh = 13,

    /// <summary>増幅シェーダー (<c>as</c>)。</summary>
    Amplification = 14,
}

/// <summary>
/// コンパイルのターゲット (<c>ps_6_6</c> など)。
/// </summary>
/// <param name="Stage">ステージ。</param>
/// <param name="Major">シェーダーモデルの主番号。</param>
/// <param name="Minor">シェーダーモデルの副番号。</param>
/// <remarks>
/// DXC の <c>-T</c> に渡す書き方である。DXC が定義するマクロ (<see cref="CreateDxcMacros"/>) を決めるのに使う。
/// </remarks>
public readonly record struct HlslShaderTarget(HlslShaderStage Stage, int Major, int Minor)
{
    /// <summary>ステージの接頭辞。</summary>
    private static readonly ImmutableArray<(string Prefix, HlslShaderStage Stage)> Prefixes =
    [
        ("ps", HlslShaderStage.Pixel),
        ("vs", HlslShaderStage.Vertex),
        ("gs", HlslShaderStage.Geometry),
        ("hs", HlslShaderStage.Hull),
        ("ds", HlslShaderStage.Domain),
        ("cs", HlslShaderStage.Compute),
        ("lib", HlslShaderStage.Library),
        ("ms", HlslShaderStage.Mesh),
        ("as", HlslShaderStage.Amplification),
    ];

    /// <summary>
    /// <c>ps_6_6</c> の形を読む。
    /// </summary>
    /// <param name="text">読む文字列。</param>
    /// <param name="target">読んだターゲット。</param>
    /// <returns>読めたら <see langword="true"/>。</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HlslShaderTarget? target)
    {
        target = null;

        string[] parts = text?.Split('_') ?? [];

        if (parts.Length != 3
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
        {
            return false;
        }

        foreach ((string prefix, HlslShaderStage stage) in Prefixes)
        {
            if (string.Equals(parts[0], prefix, StringComparison.Ordinal))
            {
                target = new HlslShaderTarget(stage, major, minor);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// DXC がこのターゲットのコンパイルで定義するマクロを作る。
    /// </summary>
    /// <param name="languageVersion">HLSL の言語のバージョン (<c>-HV</c>)。</param>
    /// <returns>マクロの名前と値。</returns>
    /// <remarks>
    /// <para>
    /// 値は DXC (1.6) の振る舞いに合わせた。<c>__SHADER_TARGET_STAGE</c> はステージの番号で、
    /// <c>__SHADER_STAGE_PIXEL</c> などと比べて使う。<c>__HLSL_VERSION</c> は <c>-HV</c> の値。
    /// </para>
    /// <para>
    /// <b>DXC はシェーダーモデル 6.0 未満を 6.0 に上げる。</b> <c>ps_5_0</c> でも <c>__SHADER_TARGET_MAJOR</c> は 6 になる。
    /// </para>
    /// <para>
    /// DXC の版 (<c>__DXC_VERSION_*</c>) は定義しない。版によって有無が違う <c>__HLSL_ENABLE_16_BIT</c> は、
    /// 設定で 16 ビットの型を使うと明示したときだけ環境が定義する (<see cref="HlslEnvironmentOptions.Enable16BitTypes"/>)。
    /// 分からないものを決め打ちすると、使う側の分岐を誤って選ぶ。
    /// </para>
    /// </remarks>
    public ImmutableDictionary<string, string> CreateDxcMacros(HlslLanguageVersion languageVersion)
    {
        (int major, int minor) = Major < 6 ? (6, 0) : (Major, Minor);

        return CreateCommonDxcMacros(languageVersion).SetItems(
        [
            new("__SHADER_TARGET_STAGE", Number((int)Stage)),
            new("__SHADER_TARGET_MAJOR", Number(major)),
            new("__SHADER_TARGET_MINOR", Number(minor)),
        ]);
    }

    /// <summary>
    /// DXC がターゲットに依らず定義するマクロを作る。
    /// </summary>
    /// <param name="languageVersion">HLSL の言語のバージョン (<c>-HV</c>)。</param>
    /// <returns>マクロの名前と値。</returns>
    /// <remarks>ステージの番号 (<c>__SHADER_STAGE_PIXEL</c> など)、<c>__HLSL_VERSION</c>、<c>__hlsl_dx_compiler</c>。</remarks>
    public static ImmutableDictionary<string, string> CreateCommonDxcMacros(HlslLanguageVersion languageVersion)
    {
        ImmutableDictionary<string, string>.Builder macros = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (HlslShaderStage stage in Enum.GetValues<HlslShaderStage>())
        {
            macros["__SHADER_STAGE_" + stage.ToString().ToUpperInvariant()] = Number((int)stage);
        }

        macros["__HLSL_VERSION"] = languageVersion == HlslLanguageVersion.Hlsl2021 ? "2021" : "2018";
        macros["__hlsl_dx_compiler"] = "1";

        return macros.ToImmutable();
    }

    /// <summary>整数をマクロの値にする。</summary>
    /// <param name="value">値。</param>
    /// <returns>10 進の文字列。</returns>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override string ToString()
    {
        HlslShaderStage stage = Stage;
        string prefix = Prefixes.First(p => p.Stage == stage).Prefix;
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}_{Major}_{Minor}");
    }
}
