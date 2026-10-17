using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;

namespace Shaderlyn.Unreal;

/// <summary>
/// Unreal Engine がシェーダーごとに HLSL のどのバージョンでコンパイルするかを決める。
/// </summary>
/// <remarks>
/// <para>
/// エンジンはプラットフォームの設定 (<c>Engine/Config/&lt;プラットフォーム&gt;/DataDrivenPlatformInfo.ini</c> の
/// <c>[ShaderPlatform …]</c> の <c>EnablesHLSL2021ByDefault</c>) で決める (UE 5.5 の <c>ShaderCompiler.cpp</c>)。
/// </para>
/// <list type="bullet">
///   <item><description>0 (または書いていない): どのシェーダーも HLSL 2018</description></item>
///   <item><description>1: グローバルシェーダーは HLSL 2021、マテリアルのシェーダーは 2018 (UE 5.5 の PC の <c>PCD3D_SM6</c>)</description></item>
///   <item><description>2: どのシェーダーも HLSL 2021</description></item>
/// </list>
/// <para>
/// マテリアルのシェーダーかは、マテリアルから生成される <c>/Engine/Generated/Material.ush</c> を取り込むかで見分ける。
/// <c>BasePassVertexCommon.ush</c> のようにヘッダを経由して取り込むものもあるので、取り込みをたどる。
/// <c>#if</c> の条件は見ない (どれかの経路で取り込めば、マテリアルのシェーダーとみなす)。
/// </para>
/// </remarks>
public static partial class UnrealShaderLanguageVersions
{
    /// <summary>マテリアルから生成される取り込み。</summary>
    public const string MaterialInclude = "/Engine/Generated/Material.ush";

    /// <summary>既定のシェーダープラットフォーム (PC の Direct3D 12、シェーダーモデル 6)。</summary>
    public const string DefaultShaderPlatform = "PCD3D_SM6";

    /// <summary>取り込みをたどる深さの上限。</summary>
    private const int MaxIncludeDepth = 64;

    /// <summary>
    /// エンジンの設定から、そのシェーダープラットフォームの <c>EnablesHLSL2021ByDefault</c> を読む。
    /// </summary>
    /// <param name="engineDirectory">エンジンのフォルダー (<c>…/UE_5.5/Engine</c>)。</param>
    /// <param name="shaderPlatform">シェーダープラットフォームの名前 (<c>PCD3D_SM6</c>)。</param>
    /// <returns>値 (0 / 1 / 2)。設定が見つからなければ <see langword="null"/>。節はあるが書いていなければ 0。</returns>
    public static int? ReadEnablesHlsl2021ByDefault(string engineDirectory, string shaderPlatform)
    {
        if (ReadShaderPlatformSettings(engineDirectory, shaderPlatform) is not { } settings)
        {
            return null;
        }

        return settings.TryGetValue("EnablesHLSL2021ByDefault", out string? text) && int.TryParse(text, out int value) ? value : 0;
    }

    /// <summary>
    /// エンジンの設定 (<c>DataDrivenPlatformInfo.ini</c>) から、そのシェーダープラットフォームの節を読む。
    /// </summary>
    /// <param name="engineDirectory">エンジンのフォルダー (<c>…/UE_5.5/Engine</c>)。</param>
    /// <param name="shaderPlatform">シェーダープラットフォームの名前 (<c>PCD3D_SM6</c>)。</param>
    /// <returns>キーから値 (大文字と小文字を区別しない)。節が見つからなければ <see langword="null"/>。</returns>
    public static ImmutableDictionary<string, string>? ReadShaderPlatformSettings(string engineDirectory, string shaderPlatform)
    {
        ArgumentNullException.ThrowIfNull(engineDirectory);
        ArgumentNullException.ThrowIfNull(shaderPlatform);

        string config = Path.Combine(engineDirectory, "Config");

        if (!Directory.Exists(config))
        {
            return null;
        }

        string header = $"[ShaderPlatform {shaderPlatform}]";

        foreach (string file in Directory.EnumerateFiles(config, "DataDrivenPlatformInfo.ini", SearchOption.AllDirectories))
        {
            ImmutableDictionary<string, string>.Builder? section = null;

            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();

                if (line.StartsWith('['))
                {
                    if (section is not null)
                    {
                        break;
                    }

                    if (string.Equals(line, header, StringComparison.OrdinalIgnoreCase))
                    {
                        section = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    continue;
                }

                int equals = line.IndexOf('=', StringComparison.Ordinal);

                if (section is not null && equals > 0)
                {
                    section[line[..equals].Trim()] = line[(equals + 1)..].Trim();
                }
            }

            if (section is not null)
            {
                return section.ToImmutable();
            }
        }

        return null;
    }

    /// <summary>
    /// その設定の値で、単位ごとに言語のバージョンを決める手続きを作る。
    /// </summary>
    /// <param name="enablesHlsl2021ByDefault"><see cref="ReadEnablesHlsl2021ByDefault"/> の値。</param>
    /// <returns>
    /// 単位ごとに決める手続き。どの単位も同じバージョンなら <see langword="null"/>
    /// (そのバージョンは <see cref="EnvironmentLanguageVersion"/>)。
    /// </returns>
    public static Func<string, IIncludeResolver?, HlslLanguageVersion>? CreateSelector(int enablesHlsl2021ByDefault)
    {
        if (enablesHlsl2021ByDefault != 1)
        {
            return null;
        }

        // 同じヘッダを何度もたどらないよう、ファイルごとの結果を覚える (1 回の解析の間だけ)。
        ConcurrentDictionary<string, bool> materialByFile = new(StringComparer.OrdinalIgnoreCase);

        return (filePath, resolver) => IncludesMaterial(filePath, resolver, materialByFile)
            ? HlslLanguageVersion.Hlsl2018
            : HlslLanguageVersion.Hlsl2021;
    }

    /// <summary>その設定の値で、環境全体の言語のバージョン (単位ごとに決めない場合の値) を返す。</summary>
    /// <param name="enablesHlsl2021ByDefault"><see cref="ReadEnablesHlsl2021ByDefault"/> の値。</param>
    /// <returns>1 と 2 は HLSL 2021 (1 はマテリアルのシェーダーだけ単位ごとに 2018 にする)、0 は 2018。</returns>
    public static HlslLanguageVersion EnvironmentLanguageVersion(int enablesHlsl2021ByDefault)
        => enablesHlsl2021ByDefault is 1 or 2 ? HlslLanguageVersion.Hlsl2021 : HlslLanguageVersion.Hlsl2018;

    /// <summary>
    /// そのファイルが、取り込みをたどって <see cref="MaterialInclude"/> に届くかを判定する。
    /// </summary>
    /// <param name="filePath">単位のファイル。</param>
    /// <param name="resolver">取り込みを解決する手続き。無ければ、そのファイルに直接書いた取り込みだけを見る。</param>
    /// <param name="cache">ファイルごとの結果。</param>
    /// <returns>届くなら <see langword="true"/>。</returns>
    internal static bool IncludesMaterial(string filePath, IIncludeResolver? resolver, ConcurrentDictionary<string, bool>? cache = null)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        return Visit(SourceText.Load(filePath), depth: 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out _);

        // incomplete: 循環か深さの上限でたどるのを打ち切った。そのときの「届かない」は確かでないので覚えない。
        bool Visit(SourceText text, int depth, HashSet<string> visiting, out bool incomplete)
        {
            incomplete = false;

            if (cache is not null && cache.TryGetValue(text.FilePath, out bool known))
            {
                return known;
            }

            if (depth > MaxIncludeDepth || !visiting.Add(text.FilePath))
            {
                incomplete = true;
                return false;
            }

            bool result = false;

            foreach (Match match in IncludeLine().Matches(text.Content))
            {
                string path = match.Groups["path"].Value.Replace('\\', '/');

                if (string.Equals(path, MaterialInclude, StringComparison.OrdinalIgnoreCase))
                {
                    result = true;
                    break;
                }

                if (resolver is not null && resolver.TryResolve(path, text.FilePath, out SourceText? included))
                {
                    if (Visit(included, depth + 1, visiting, out bool childIncomplete))
                    {
                        result = true;
                        break;
                    }

                    incomplete |= childIncomplete;
                }
            }

            visiting.Remove(text.FilePath);

            if (result || !incomplete)
            {
                cache?.TryAdd(text.FilePath, result);
            }

            return result;
        }
    }

    [GeneratedRegex("""^[ \t]*#[ \t]*include[ \t]*["<](?<path>[^">]+)[">]""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IncludeLine();
}
