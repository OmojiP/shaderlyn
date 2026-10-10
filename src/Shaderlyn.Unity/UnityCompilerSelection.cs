using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Unity;

/// <summary>
/// Unity が、パス (コンピュートシェーダーはカーネル) を API ごとにどのコンパイラでコンパイルするかを決める。
/// </summary>
/// <remarks>
/// <para>
/// 規則は Unity 2022.3〜6.6 の実測 (DX12 / DX11 / Vulkan、<c>.shader</c> と <c>.compute</c>) から作った。
/// </para>
/// <list type="bullet">
///   <item>既定は FXC。DXC で失敗しても FXC には戻らない</item>
///   <item>DXC は全バージョン・全 API で HLSL 2018。単体の DXC と違い、<c>sampler2D</c> / <c>tex2D</c> / <c>sampler_state</c> を受け付ける</item>
///   <item><c>#pragma use_dxc</c> (API の指定で対象を絞る)、DXC が要る <c>#pragma require</c> で DXC になる</item>
///   <item><c>multi_compile</c> の <c>UNITY_DEVICE_SUPPORTS_NATIVE_16BIT</c> / <c>INLINE_RAY_TRACING</c> は、キーワードが有効な構成だけ DXC。
///         <c>WAVE_ANY</c> / <c>WAVE_8</c>〜<c>128</c> はコンピュートシェーダーでだけ DXC</item>
///   <item>DXC になるシェーダーは DX11 では使えない。6.5 以前は DX11 と DX12 が同じ扱いで、API 指定の無い <c>use_dxc</c> は DX11 も DXC にする</item>
///   <item><c>never_use_dxc</c> は 6.5 以前は <c>require</c> に勝ち、6.6 からは負ける</item>
/// </list>
/// <para>
/// <b>読む <c>#pragma</c> は、コードブロック (と <c>HLSLINCLUDE</c>) に直接書いたものだけである。</b>
/// 2023.2 以降は include ファイルの中の指定が効かない。2022.3 では効くが、ここでは読まない。
/// </para>
/// <para>
/// <b>Unity のバージョンが分からなければ、どのまとまりの結果も並べる。</b>
/// まとまりで結果が変わるコンパイル先には、そのまとまりを理由に添える。
/// </para>
/// </remarks>
public static class UnityCompilerSelection
{
    /// <summary>Unity の DXC。HLSL 2018 で、DX9 の書き方を受け付ける。</summary>
    public static HlslDialect Dxc { get; } = HlslDialect.Dxc(HlslLanguageVersion.Hlsl2018) with
    {
        AcceptsLegacySamplerSyntax = true,
        Label = "DXC (HLSL 2018)",
    };

    /// <summary>Unity の DXC で、16 ビットの型を有効にしたもの (<c>require native16bit</c>)。</summary>
    public static HlslDialect Dxc16Bit { get; } = Dxc with
    {
        Enable16BitTypes = true,
        Label = "DXC (HLSL 2018、16 ビットの型)",
    };

    /// <summary>16 ビットの型のキーワード。</summary>
    public const string Native16BitKeyword = "UNITY_DEVICE_SUPPORTS_NATIVE_16BIT";

    /// <summary>インラインのレイトレーシングのキーワード。</summary>
    public const string InlineRayTracingKeyword = "UNITY_DEVICE_SUPPORTS_INLINE_RAY_TRACING";

    /// <summary>ウェーブの命令のキーワード。コンピュートシェーダーでだけ DXC になる。</summary>
    public static FrozenSet<string> WaveKeywords { get; } = new[]
    {
        "UNITY_DEVICE_SUPPORTS_WAVE_ANY",
        "UNITY_DEVICE_SUPPORTS_WAVE_8",
        "UNITY_DEVICE_SUPPORTS_WAVE_16",
        "UNITY_DEVICE_SUPPORTS_WAVE_32",
        "UNITY_DEVICE_SUPPORTS_WAVE_64",
        "UNITY_DEVICE_SUPPORTS_WAVE_128",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// DXC が要る <c>#pragma require</c> の値 (<c>inlineraytracing</c> を除く 12 個)。どのバージョンでも DXC になる。
    /// </summary>
    public static FrozenSet<string> DxcRequirements { get; } = new[]
    {
        "wavebasic", "wavevote", "waveballot", "wavemath", "wavemultiprefix", "waveshuffle", "quadshuffle",
        "int64", "native16bit", "barycentrics", "int64bufferatomics", "int64groupsharedatomics",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>インラインのレイトレーシングの <c>#pragma require</c> の値。バージョンで扱いが変わる。</summary>
    public const string InlineRayTracingRequirement = "inlineraytracing";

    /// <summary>
    /// コードブロックのコンパイル先を決める。
    /// </summary>
    /// <param name="tokens">コードブロックを字句解析しただけのトークン列。</param>
    /// <param name="isCompute">コンピュートシェーダーか。</param>
    /// <param name="options">Unity の環境の設定 (バージョン、API、Shader Compiler Backend)。</param>
    /// <param name="included">
    /// 取り込んだ include ファイルの <c>#pragma</c>。2022.3 だけが使う。読んでいなければ <see langword="null"/>。
    /// </param>
    /// <returns>コンパイル先。API の順に並ぶ。</returns>
    public static ImmutableArray<HlslCompileTarget> Select(
        ImmutableArray<HlslSyntaxToken> tokens,
        bool isCompute,
        UnityOptions options,
        UnityCompilerPragmas? included = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Select(UnityCompilerPragmas.Read(tokens), isCompute, options, included);
    }

    /// <summary>読み取った <c>#pragma</c> からコンパイル先を決める。</summary>
    /// <param name="pragmas">読み取った <c>#pragma</c>。</param>
    /// <param name="isCompute">コンピュートシェーダーか。</param>
    /// <param name="options">Unity の環境の設定。</param>
    /// <param name="included">取り込んだ include ファイルの <c>#pragma</c>。2022.3 だけが使う。</param>
    /// <returns>コンパイル先。</returns>
    public static ImmutableArray<HlslCompileTarget> Select(
        UnityCompilerPragmas pragmas,
        bool isCompute,
        UnityOptions options,
        UnityCompilerPragmas? included = null)
    {
        ArgumentNullException.ThrowIfNull(pragmas);
        ArgumentNullException.ThrowIfNull(options);

        ImmutableArray<UnityGraphicsApi> apis = options.GraphicsApis.IsDefaultOrEmpty ? UnityGraphicsApis.Default : options.GraphicsApis;

        if (options.VersionBand is { } known)
        {
            return [.. apis.SelectMany(api => Decide(Effective(pragmas, included, known), isCompute, options, api, known))];
        }

        // バージョンが分からなければ、まとまりごとに決めて並べる。同じ結果はまとめる。
        ImmutableArray<HlslCompileTarget>.Builder merged = ImmutableArray.CreateBuilder<HlslCompileTarget>();

        foreach (UnityGraphicsApi api in apis)
        {
            List<(HlslCompileTarget Target, List<UnityVersionBand> Bands)> found = [];

            foreach (UnityVersionBand band in UnityVersions.AllBands)
            {
                foreach (HlslCompileTarget target in Decide(Effective(pragmas, included, band), isCompute, options, api, band))
                {
                    int index = found.FindIndex(f => SameTarget(f.Target, target));

                    if (index < 0)
                    {
                        found.Add((target, [band]));
                    }
                    else
                    {
                        found[index].Bands.Add(band);
                    }
                }
            }

            foreach ((HlslCompileTarget target, List<UnityVersionBand> bands) in found)
            {
                merged.Add(bands.Count == UnityVersions.AllBands.Count
                    ? target
                    : target with { Reason = JoinReason(target.Reason, DescribeBands(bands)) });
            }
        }

        return merged.ToImmutable();
    }

    /// <summary>そのまとまりで効く <c>#pragma</c>。2022.3 は include ファイルの中の <c>use_dxc</c> / <c>require</c> も効く。</summary>
    /// <param name="pragmas">コードブロックに直接書いたもの。</param>
    /// <param name="included">include ファイルの中のもの。</param>
    /// <param name="band">Unity のバージョンのまとまり。</param>
    /// <returns>効くもの。</returns>
    private static UnityCompilerPragmas Effective(UnityCompilerPragmas pragmas, UnityCompilerPragmas? included, UnityVersionBand band)
        => band == UnityVersionBand.Unity2022_3 && included is not null ? pragmas.WithIncluded(included) : pragmas;

    /// <summary>1 つの API と 1 つのまとまりでのコンパイル先を決める。</summary>
    /// <param name="pragmas">読み取った <c>#pragma</c>。</param>
    /// <param name="isCompute">コンピュートシェーダーか。</param>
    /// <param name="options">Unity の環境の設定。</param>
    /// <param name="api">API。</param>
    /// <param name="band">Unity のバージョンのまとまり。</param>
    /// <returns>コンパイル先。</returns>
    private static IEnumerable<HlslCompileTarget> Decide(
        UnityCompilerPragmas pragmas,
        bool isCompute,
        UnityOptions options,
        UnityGraphicsApi api,
        UnityVersionBand band)
    {
        string platform = UnityGraphicsApis.GetName(api);

        // OpenGL 系は確かめていない。FXC の結果を変換して使うので、FXC として扱う。
        if (api is UnityGraphicsApi.OpenGLES3 or UnityGraphicsApi.OpenGLCore)
        {
            return [new HlslCompileTarget(platform, HlslDialect.Fxc)];
        }

        bool latest = band == UnityVersionBand.Unity6_6OrLater;

        // 2023.2〜6.3 は require inlineraytracing を満たす GPU が無いと判定し、シェーダーそのものをコンパイルしない。
        if (band == UnityVersionBand.Unity2023_2To6_3 && pragmas.Requires(InlineRayTracingRequirement))
        {
            return [new HlslCompileTarget(platform, Dxc)
            {
                IsUsable = false,
                Reason = "#pragma require inlineraytracing はコンパイルされない",
            }];
        }

        // DXC が要る require。inlineraytracing は 6.4 から DXC になる (2022.3 は知らない値として無視する)。
        string? required = pragmas.UnconditionalRequirements.FirstOrDefault(DxcRequirements.Contains)
                           ?? (band >= UnityVersionBand.Unity6_4To6_5 && pragmas.Requires(InlineRayTracingRequirement)
                               ? InlineRayTracingRequirement
                               : null);
        bool sixteenBit = pragmas.Requires("native16bit");
        bool never = pragmas.NeverUseDxcApplies(api, band);

        // 構成で DXC になるキーワード (キーワードが有効な構成だけ DXC)。
        List<(string Keyword, bool SixteenBit, string? Reason)> keywords = [];

        if (!never || latest)
        {
            foreach (string keyword in pragmas.DeviceKeywords)
            {
                if (keyword == Native16BitKeyword
                    || (keyword == InlineRayTracingKeyword && band != UnityVersionBand.Unity2022_3)
                    || (isCompute && WaveKeywords.Contains(keyword)))
                {
                    keywords.Add((keyword, keyword == Native16BitKeyword, null));
                }
            }

            foreach ((string value, string keyword) in pragmas.ConditionalRequirements)
            {
                if (DxcRequirements.Contains(value) && keywords.All(k => k.Keyword != keyword))
                {
                    keywords.Add((keyword, value.Equals("native16bit", StringComparison.OrdinalIgnoreCase), $"#pragma require {value} : {keyword}"));
                }
            }
        }

        if (api == UnityGraphicsApi.Direct3D11)
        {
            if (required is not null)
            {
                return [new HlslCompileTarget(platform, sixteenBit ? Dxc16Bit : Dxc)
                {
                    IsUsable = false,
                    Reason = $"#pragma require {required} は DXC が要るので DX11 では使えない",
                }];
            }

            if (never)
            {
                return [new HlslCompileTarget(platform, HlslDialect.Fxc) { Reason = "#pragma never_use_dxc" }];
            }

            if (!latest && pragmas.UseDxcApplies(api, band))
            {
                return [new HlslCompileTarget(platform, Dxc)
                {
                    IsUsable = false,
                    Reason = "6.5 以前の #pragma use_dxc は DX11 も DXC にするので、DX11 では使えない",
                }];
            }

            // キーワードが有効な構成は、6.5 以前は DXC でコンパイルされ、6.6 からはコンパイルされない。どちらも DX11 では使えない
            // (デバイスのキーワードは DX11 では有効にならない)。
            return
            [
                new HlslCompileTarget(platform, HlslDialect.Fxc) { UnlessAnySymbol = [.. keywords.Select(k => k.Keyword)] },
                .. keywords.Select(k => new HlslCompileTarget(platform, k.SixteenBit ? Dxc16Bit : Dxc)
                {
                    WhenAnySymbol = [k.Keyword],
                    IsUsable = false,
                    Reason = k.Reason is null ? "DX11 では有効にならないキーワード" : $"{k.Reason} は DX11 では使えない",
                }),
            ];
        }

        // DX12 / Vulkan / Metal。
        if (never && !latest)
        {
            return [new HlslCompileTarget(platform, HlslDialect.Fxc) { Reason = "#pragma never_use_dxc" }];
        }

        // 6.6 は、Vulkan と Metal で一部の require をコンパイルしない。
        if (latest && pragmas.UnconditionalRequirements.FirstOrDefault(value => IsNotCompiled(api, value)) is { } unsupported)
        {
            return [new HlslCompileTarget(platform, sixteenBit ? Dxc16Bit : Dxc)
            {
                IsUsable = false,
                Reason = $"#pragma require {unsupported} はコンパイルされない",
            }];
        }

        // 6.6 以降の never_use_dxc は use_dxc と Backend の設定に勝ち、require とキーワードに負ける。
        string? reason = required is not null
            ? $"#pragma require {required}"
            : never
                ? null
                : pragmas.UseDxcApplies(api, band)
                    ? "#pragma use_dxc"
                    : options.DxcBackendApis.Contains(api) && latest
                        ? "Shader Compiler Backend が DXC"
                        : null;

        HlslDialect baseDialect = reason is null ? HlslDialect.Fxc : sixteenBit ? Dxc16Bit : Dxc;
        string? baseReason = reason ?? (never ? "#pragma never_use_dxc" : null);

        if (keywords.Count == 0)
        {
            return [new HlslCompileTarget(platform, baseDialect) { Reason = baseReason }];
        }

        List<HlslCompileTarget> targets = [];

        foreach ((string keyword, bool keywordSixteenBit, string? keywordReason) in keywords)
        {
            if (baseDialect == Dxc16Bit || (baseDialect == Dxc && !keywordSixteenBit))
            {
                continue;
            }

            targets.Add(new HlslCompileTarget(platform, keywordSixteenBit || sixteenBit ? Dxc16Bit : Dxc)
            {
                WhenAnySymbol = [keyword],
                Reason = reason ?? keywordReason,
            });
        }

        ImmutableArray<string> exceptions = [.. targets.SelectMany(t => t.WhenAnySymbol)];
        targets.Insert(0, new HlslCompileTarget(platform, baseDialect) { UnlessAnySymbol = exceptions, Reason = baseReason });

        return targets;
    }

    /// <summary>
    /// Unity 6.6 がその API でコンパイルしない <c>#pragma require</c> の値か。
    /// </summary>
    /// <param name="api">API。</param>
    /// <param name="value">値。</param>
    /// <returns>コンパイルしないなら <see langword="true"/>。</returns>
    /// <remarks>Vulkan は <c>wavemultiprefix</c>、Metal はそれと 64 ビットのアトミック 2 つ (実測)。</remarks>
    private static bool IsNotCompiled(UnityGraphicsApi api, string value) => api switch
    {
        UnityGraphicsApi.Vulkan => value == "wavemultiprefix",
        UnityGraphicsApi.Metal => value is "wavemultiprefix" or "int64bufferatomics" or "int64groupsharedatomics",
        _ => false,
    };

    /// <summary>理由を除いて同じコンパイル先か。</summary>
    /// <param name="a">1 つ目。</param>
    /// <param name="b">2 つ目。</param>
    /// <returns>同じなら <see langword="true"/>。</returns>
    private static bool SameTarget(HlslCompileTarget a, HlslCompileTarget b)
        => a.Platform == b.Platform
           && a.Dialect == b.Dialect
           && a.IsUsable == b.IsUsable
           && a.Reason == b.Reason
           && a.WhenAnySymbol.SequenceEqual(b.WhenAnySymbol)
           && a.UnlessAnySymbol.SequenceEqual(b.UnlessAnySymbol);

    /// <summary>理由にまとまりを添える。</summary>
    /// <param name="reason">理由。</param>
    /// <param name="bands">まとまり。</param>
    /// <returns>添えた理由。</returns>
    private static string JoinReason(string? reason, string bands)
        => reason is null ? bands : $"{reason}、{bands}";

    /// <summary>まとまりの並びを短く言う (<c>Unity 6.5 以前</c> など)。</summary>
    /// <param name="bands">まとまり (古い順)。</param>
    /// <returns>言葉。</returns>
    internal static string DescribeBands(IReadOnlyList<UnityVersionBand> bands)
    {
        string[] lower = ["2022.3", "2023.2", "6.4", "6.6"];
        string[] upper = ["2022.3", "6.3", "6.5", string.Empty];

        int first = (int)bands[0];
        int last = (int)bands[^1];

        if (last - first + 1 != bands.Count)
        {
            return string.Join("・", bands.Select(UnityVersions.Describe));
        }

        if (first == 0)
        {
            return $"Unity {upper[last]} 以前";
        }

        if (last == UnityVersions.AllBands.Count - 1)
        {
            return $"Unity {lower[first]} 以降";
        }

        return first == last ? UnityVersions.Describe(bands[0]) : $"Unity {lower[first]}〜{upper[last]}";
    }
}

/// <summary>
/// コンパイラの選び方に関わる <c>#pragma</c> を読んだもの。
/// </summary>
public sealed class UnityCompilerPragmas
{
    /// <summary><c>multi_compile</c> などの名前の先頭。</summary>
    private static readonly string[] KeywordPragmas = ["multi_compile", "shader_feature"];

    /// <summary>API の指定の無い <c>#pragma use_dxc</c> があるか。</summary>
    public bool UseDxcForAll { get; private set; }

    /// <summary><c>#pragma use_dxc</c> に書かれた API の名前 (小文字)。</summary>
    public ImmutableHashSet<string> UseDxcApis { get; private set; } = [];

    /// <summary><c>#pragma use_dxc</c> があるか。</summary>
    public bool HasUseDxc => UseDxcForAll || !UseDxcApis.IsEmpty;

    /// <summary>API の指定の無い <c>#pragma never_use_dxc</c> があるか。</summary>
    public bool NeverUseDxcForAll { get; private set; }

    /// <summary><c>#pragma never_use_dxc</c> に書かれた API の名前 (小文字)。</summary>
    public ImmutableHashSet<string> NeverUseDxcApis { get; private set; } = [];

    /// <summary><c>#pragma never_use_dxc</c> があるか。</summary>
    public bool HasNeverUseDxc => NeverUseDxcForAll || !NeverUseDxcApis.IsEmpty;

    /// <summary>キーワードの条件の無い <c>#pragma require</c> の値 (小文字)。</summary>
    public ImmutableArray<string> UnconditionalRequirements { get; private set; } = [];

    /// <summary>キーワードの条件付きの <c>#pragma require 値 : キーワード</c>。</summary>
    public ImmutableArray<(string Value, string Keyword)> ConditionalRequirements { get; private set; } = [];

    /// <summary><c>multi_compile</c> / <c>shader_feature</c> で宣言した、デバイスのキーワード (<c>UNITY_DEVICE_SUPPORTS_*</c>)。</summary>
    public ImmutableArray<string> DeviceKeywords { get; private set; } = [];

    /// <summary>トークン列から読む。</summary>
    /// <param name="tokens">字句解析しただけのトークン列。</param>
    /// <returns>読んだもの。</returns>
    public static UnityCompilerPragmas Read(ImmutableArray<HlslSyntaxToken> tokens)
    {
        UnityCompilerPragmas result = new();
        ImmutableHashSet<string>.Builder useDxc = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        ImmutableHashSet<string>.Builder neverUseDxc = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        List<string> requires = [];
        List<(string, string)> conditional = [];
        List<string> keywords = [];

        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Kind != HlslSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || tokens[i + 1].Text != "pragma"
                || tokens[i + 2].IsAtLineStart)
            {
                continue;
            }

            string name = tokens[i + 2].Text;
            List<string> arguments = [];
            int end = i + 3;

            while (end < tokens.Length && !tokens[end].IsAtLineStart && tokens[end].Kind != HlslSyntaxKind.EndOfFileToken)
            {
                arguments.Add(tokens[end].Text);
                end++;
            }

            switch (name)
            {
                case "use_dxc":
                    result.UseDxcForAll |= arguments.Count == 0;
                    useDxc.UnionWith(arguments.Select(a => a.ToLowerInvariant()));
                    break;

                case "never_use_dxc":
                    result.NeverUseDxcForAll |= arguments.Count == 0;
                    neverUseDxc.UnionWith(arguments.Select(a => a.ToLowerInvariant()));
                    break;

                case "require":
                    int colon = arguments.IndexOf(":");
                    IEnumerable<string> values = (colon < 0 ? arguments : arguments.Take(colon)).Select(a => a.ToLowerInvariant());

                    if (colon < 0)
                    {
                        requires.AddRange(values);
                    }
                    else
                    {
                        string[] conditions = [.. arguments.Skip(colon + 1)];
                        conditional.AddRange(values.SelectMany(v => conditions.Select(k => (v, k))));
                    }

                    break;

                default:
                    if (KeywordPragmas.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                    {
                        keywords.AddRange(arguments.Where(a => a.StartsWith("UNITY_DEVICE_SUPPORTS_", StringComparison.Ordinal)));
                    }

                    break;
            }

            i = end - 1;
        }

        result.UseDxcApis = useDxc.ToImmutable();
        result.NeverUseDxcApis = neverUseDxc.ToImmutable();
        result.UnconditionalRequirements = [.. requires.Distinct(StringComparer.Ordinal)];
        result.ConditionalRequirements = [.. conditional.Distinct()];
        result.DeviceKeywords = [.. keywords.Distinct(StringComparer.Ordinal)];
        return result;
    }

    /// <summary><c>use_dxc</c> も <c>require</c> も無いか。</summary>
    public bool HasNoCompilerPragmas => !HasUseDxc && UnconditionalRequirements.IsEmpty && ConditionalRequirements.IsEmpty;

    /// <summary>
    /// include ファイルの中の <c>use_dxc</c> と <c>require</c> を足したものを返す。
    /// </summary>
    /// <param name="included">include ファイルの中のもの。</param>
    /// <returns>足したもの。</returns>
    /// <remarks>
    /// 2022.3 で効くと確かめたのは <c>use_dxc</c> と <c>require</c> だけなので、<c>never_use_dxc</c> とキーワードは足さない。
    /// </remarks>
    public UnityCompilerPragmas WithIncluded(UnityCompilerPragmas included)
    {
        ArgumentNullException.ThrowIfNull(included);

        return new UnityCompilerPragmas
        {
            UseDxcForAll = UseDxcForAll || included.UseDxcForAll,
            UseDxcApis = UseDxcApis.Union(included.UseDxcApis),
            NeverUseDxcForAll = NeverUseDxcForAll,
            NeverUseDxcApis = NeverUseDxcApis,
            UnconditionalRequirements = [.. UnconditionalRequirements.Concat(included.UnconditionalRequirements).Distinct(StringComparer.Ordinal)],
            ConditionalRequirements = [.. ConditionalRequirements.Concat(included.ConditionalRequirements).Distinct()],
            DeviceKeywords = DeviceKeywords,
        };
    }

    /// <summary>キーワードの条件の無い <c>#pragma require</c> にその値があるか。</summary>
    /// <param name="value">値。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    public bool Requires(string value) => UnconditionalRequirements.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>#pragma use_dxc</c> がその API に効くか。</summary>
    /// <param name="api">API。</param>
    /// <param name="band">Unity のバージョンのまとまり。</param>
    /// <returns>効くなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>API の指定なし: どの API にも効く (6.6 からは DX11 を除く。DX11 の扱いは呼び出し側)</item>
    ///   <item><c>d3d11</c>: DX12 に効く。6.5 以前は DX11 にも効く (DX11 と DX12 が同じ扱い)</item>
    ///   <item><c>dx12</c>: 6.6 から DX12 に効く。6.5 以前は無視される</item>
    ///   <item><c>d3d12</c>: どのバージョンでも無視される</item>
    ///   <item><c>vulkan</c> / <c>metal</c>: その API に効く (<c>metal</c> は確かめていない)</item>
    /// </list>
    /// </remarks>
    public bool UseDxcApplies(UnityGraphicsApi api, UnityVersionBand band)
        => Applies(UseDxcForAll, UseDxcApis, api, band);

    /// <summary><c>#pragma never_use_dxc</c> がその API に効くか。API の名前の読み方は <see cref="UseDxcApplies"/> と同じとみなす。</summary>
    /// <param name="api">API。</param>
    /// <param name="band">Unity のバージョンのまとまり。</param>
    /// <returns>効くなら <see langword="true"/>。</returns>
    public bool NeverUseDxcApplies(UnityGraphicsApi api, UnityVersionBand band)
        => Applies(NeverUseDxcForAll, NeverUseDxcApis, api, band);

    /// <summary>API の名前の並びが、その API に効くか。</summary>
    /// <param name="all">API の指定が無いものがあるか。</param>
    /// <param name="names">API の名前。</param>
    /// <param name="api">API。</param>
    /// <param name="band">Unity のバージョンのまとまり。</param>
    /// <returns>効くなら <see langword="true"/>。</returns>
    private static bool Applies(bool all, ImmutableHashSet<string> names, UnityGraphicsApi api, UnityVersionBand band)
    {
        if (all)
        {
            return true;
        }

        bool latest = band == UnityVersionBand.Unity6_6OrLater;

        return api switch
        {
            UnityGraphicsApi.Direct3D11 => names.Contains("d3d11") && !latest,
            UnityGraphicsApi.Direct3D12 => names.Contains("d3d11") || (latest && names.Contains("dx12")),
            UnityGraphicsApi.Vulkan => names.Contains("vulkan"),
            UnityGraphicsApi.Metal => names.Contains("metal"),
            _ => false,
        };
    }
}
