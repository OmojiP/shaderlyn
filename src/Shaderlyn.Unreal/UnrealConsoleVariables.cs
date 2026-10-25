using System.Collections.Immutable;

namespace Shaderlyn.Unreal;

/// <summary>
/// エンジンとプロジェクトの ini に書かれた、コンソール変数の値を読む。
/// </summary>
/// <remarks>
/// <para>
/// <b>シェーダーに定義されるマクロの多くは、プロジェクトの設定 (コンソール変数) で決まる。</b>
/// エンジンは <c>GlobalBeginCompileShader</c> でコンソール変数を読み、どのシェーダーにも <c>SUBSTRATE_ENABLED</c> などを定義する。
/// クラスの <c>ShouldCompilePermutation</c> もコンソール変数を読む (<c>Substrate::IsSubstrateEnabled()</c>)。
/// 値は C++ に書いた既定値 (<c>TAutoConsoleVariable</c> の宣言) を、ini の値が上書きする。
/// </para>
/// <para>
/// 読む順は、エンジンの <c>BaseEngine.ini</c>、エンジンの Windows の ini、プロジェクトの <c>DefaultEngine.ini</c>、
/// プロジェクトの Windows の ini である。後に読んだ値が優先する (PC の Direct3D 12 として読む)。
/// 読む節は、プロジェクトの設定の画面が書く <c>[/Script/Engine.RendererSettings]</c> と
/// <c>[/Script/Engine.RendererOverrideSettings]</c>、コンソール変数を直接書く <c>[SystemSettings]</c> と <c>[ConsoleVariables]</c> である。
/// </para>
/// </remarks>
public static class UnrealConsoleVariables
{
    /// <summary>コンソール変数を書く節。</summary>
    private static readonly string[] Sections =
    [
        "/Script/Engine.RendererSettings",
        "/Script/Engine.RendererOverrideSettings",
        "SystemSettings",
        "ConsoleVariables",
    ];

    /// <summary>ini に書かれたコンソール変数の値を読む。</summary>
    /// <param name="engineDirectory">エンジンのフォルダー (<c>…/UE_5.5/Engine</c>)。</param>
    /// <param name="projectDirectory">プロジェクトのフォルダー。無ければ <see langword="null"/>。</param>
    /// <returns>コンソール変数の名前 (大文字と小文字を区別しない) から値。</returns>
    public static ImmutableDictionary<string, string> ReadOverrides(string engineDirectory, string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(engineDirectory);

        ImmutableDictionary<string, string>.Builder values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in IniFiles(engineDirectory, projectDirectory))
        {
            Read(file, values);
        }

        return values.ToImmutable();
    }

    /// <summary>読む ini を、優先の低い順に並べる。</summary>
    /// <param name="engineDirectory">エンジンのフォルダー。</param>
    /// <param name="projectDirectory">プロジェクトのフォルダー。無ければ <see langword="null"/>。</param>
    /// <returns>ini のパス。無いファイルも含む。</returns>
    internal static IEnumerable<string> IniFiles(string engineDirectory, string? projectDirectory)
    {
        yield return Path.Combine(engineDirectory, "Config", "BaseEngine.ini");
        yield return Path.Combine(engineDirectory, "Config", "Windows", "BaseWindowsEngine.ini");
        yield return Path.Combine(engineDirectory, "Config", "Windows", "WindowsEngine.ini");

        if (projectDirectory is not null)
        {
            yield return Path.Combine(projectDirectory, "Config", "DefaultEngine.ini");
            yield return Path.Combine(projectDirectory, "Config", "Windows", "WindowsEngine.ini");
        }
    }

    /// <summary>ini を 1 つ読み、コンソール変数の節の値を足す。</summary>
    private static void Read(string file, ImmutableDictionary<string, string>.Builder values)
    {
        IEnumerable<string> lines;

        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        bool inSection = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (line.StartsWith('['))
            {
                inSection = line.EndsWith(']') && Sections.Contains(line[1..^1], StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);

            // 配列の書き方 (+Key=、-Key=) と、コメント (;) は読まない。
            if (!inSection || equals <= 0 || line[0] is ';' or '+' or '-' or '.' or '!')
            {
                continue;
            }

            string key = line[..equals].Trim();

            // コンソール変数は r.Substrate のように、名前に点を含む。設定の項目 (MaxSkinBones) は読まない。
            if (key.Contains('.', StringComparison.Ordinal))
            {
                values[key] = line[(equals + 1)..].Trim().Trim('"');
            }
        }
    }
}
