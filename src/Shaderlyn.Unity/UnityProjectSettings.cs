using System.Collections.Immutable;
using System.Globalization;

namespace Shaderlyn.Unity;

/// <summary>
/// Unity プロジェクトの設定のうち、シェーダーのコンパイラの選び方に関わるもの。
/// </summary>
/// <param name="EditorVersion">Unity のバージョン (<c>ProjectSettings/ProjectVersion.txt</c> の <c>m_EditorVersion</c>)。読めなければ <see langword="null"/>。</param>
/// <param name="GraphicsApis">Windows 向けのグラフィックス API (<c>ProjectSettings.asset</c> の <c>m_BuildTargetGraphicsAPIs</c>)。読めなければ空。</param>
/// <param name="DxcBackendApis">
/// Shader Compiler Backend を DXC にした API (Unity 6.6 の <c>compilerToolchainOverride: 2</c>)。無ければ空。
/// </param>
public sealed record UnityProjectSettings(
    string? EditorVersion,
    ImmutableArray<UnityGraphicsApi> GraphicsApis,
    ImmutableArray<UnityGraphicsApi> DxcBackendApis)
{
    /// <summary>何も読めなかったときの値。</summary>
    public static UnityProjectSettings Empty { get; } = new(null, [], []);

    /// <summary>Unity の <c>BuildTarget.StandaloneWindows</c> / <c>StandaloneWindows64</c> の番号。</summary>
    private static readonly string[] WindowsBuildTargets = ["5", "19"];

    /// <summary>
    /// プロジェクトのフォルダーから設定を読む。
    /// </summary>
    /// <param name="projectPath">プロジェクトのルート (<c>Assets</c> と <c>ProjectSettings</c> があるフォルダー)。</param>
    /// <returns>設定。読めなかったものは空。</returns>
    /// <remarks>
    /// <para>
    /// <b>グラフィックス API は Windows のスタンドアロン向けだけを読む。</b>
    /// 解析の既定が Windows (DX11 と DX12) で、実測もそこで行ったためである。
    /// Android などは <c>unity: graphics-apis</c> で指定する。
    /// </para>
    /// <para>
    /// <b>Shader Compiler Backend は、<c>GraphicsSettings.asset</c> と Windows のビルドプロファイルを読む。</b>
    /// どのビルドプロファイルが選ばれているかはライブラリのフォルダーにしか無いので、
    /// どれかで DXC にしていれば DXC として扱う。
    /// </para>
    /// <para>読めないファイルや知らない書き方は、例外を投げずに読み飛ばす。</para>
    /// </remarks>
    public static UnityProjectSettings Read(string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);

        string settings = Path.Combine(projectPath, "ProjectSettings");

        string? version = ReadLines(Path.Combine(settings, "ProjectVersion.txt"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
            .Select(line => line["m_EditorVersion:".Length..].Trim())
            .FirstOrDefault(value => value.Length > 0);

        ImmutableArray<UnityGraphicsApi> apis = ReadWindowsGraphicsApis(ReadLines(Path.Combine(settings, "ProjectSettings.asset")));

        IEnumerable<string> profiles = SafeEnumerate(Path.Combine(projectPath, "Assets", "Settings", "Build Profiles"), "*.asset");
        HashSet<UnityGraphicsApi> dxc = [.. ReadDxcBackendApis(ReadLines(Path.Combine(settings, "GraphicsSettings.asset")))];

        foreach (string profile in profiles)
        {
            string[] lines = ReadLines(profile);

            if (lines.Any(line => line.Trim() is var t
                                  && t.StartsWith("m_BuildTarget:", StringComparison.Ordinal)
                                  && WindowsBuildTargets.Contains(t["m_BuildTarget:".Length..].Trim())))
            {
                dxc.UnionWith(ReadDxcBackendApis(lines));
            }
        }

        return new UnityProjectSettings(version, apis, [.. dxc.Order()]);
    }

    /// <summary>
    /// <c>m_BuildTargetGraphicsAPIs</c> から Windows のスタンドアロン向けの API を読む。
    /// </summary>
    /// <param name="lines">ファイルの行。</param>
    /// <returns>API。無ければ空。</returns>
    /// <remarks>
    /// <c>m_APIs</c> は 4 バイトずつのリトルエンディアンの番号を 16 進で並べたもの (<c>1200000002000000</c> = DX12, DX11)。
    /// Unity 6.6 は末尾に <c>i</c> が付く。
    /// </remarks>
    internal static ImmutableArray<UnityGraphicsApi> ReadWindowsGraphicsApis(string[] lines)
    {
        bool inWindows = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (line.StartsWith("- m_BuildTarget:", StringComparison.Ordinal))
            {
                inWindows = line.EndsWith("WindowsStandaloneSupport", StringComparison.Ordinal);
                continue;
            }

            if (!inWindows || !line.StartsWith("m_APIs:", StringComparison.Ordinal))
            {
                continue;
            }

            string hex = line["m_APIs:".Length..].Trim().TrimEnd('i');
            ImmutableArray<UnityGraphicsApi>.Builder apis = ImmutableArray.CreateBuilder<UnityGraphicsApi>();

            for (int i = 0; i + 8 <= hex.Length; i += 8)
            {
                if (uint.TryParse(hex.AsSpan(i, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)
                    && UnityGraphicsApis.TryFromDeviceType((int)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value), out UnityGraphicsApi? api))
                {
                    apis.Add(api.Value);
                }
            }

            return apis.ToImmutable();
        }

        return [];
    }

    /// <summary>
    /// <c>compilerSettings</c> から、DXC にした API を読む。
    /// </summary>
    /// <param name="lines">ファイルの行。</param>
    /// <returns>API。</returns>
    /// <remarks>
    /// <code>
    /// compilerSettings:
    /// - graphicsAPI: 18
    ///   compilerToolchainOverride: 2
    /// </code>
    /// <c>compilerToolchainOverride</c> は 1 が FXC、2 が DXC。
    /// </remarks>
    internal static IEnumerable<UnityGraphicsApi> ReadDxcBackendApis(string[] lines)
    {
        int? deviceType = null;

        foreach (string raw in lines)
        {
            string line = raw.Trim().TrimStart('-').Trim();

            if (line.StartsWith("graphicsAPI:", StringComparison.Ordinal))
            {
                deviceType = int.TryParse(line["graphicsAPI:".Length..].Trim(), CultureInfo.InvariantCulture, out int value) ? value : null;
            }
            else if (line.StartsWith("compilerToolchainOverride:", StringComparison.Ordinal))
            {
                if (deviceType is { } type
                    && line["compilerToolchainOverride:".Length..].Trim() == "2"
                    && UnityGraphicsApis.TryFromDeviceType(type, out UnityGraphicsApi? api))
                {
                    yield return api.Value;
                }

                deviceType = null;
            }
        }
    }

    /// <summary>ファイルの行を読む。読めなければ空。</summary>
    /// <param name="path">パス。</param>
    /// <returns>行。</returns>
    private static string[] ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>フォルダーのファイルを列挙する。無いか読めなければ空。</summary>
    /// <param name="directory">フォルダー。</param>
    /// <param name="pattern">名前の型。</param>
    /// <returns>パス。</returns>
    private static IEnumerable<string> SafeEnumerate(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
