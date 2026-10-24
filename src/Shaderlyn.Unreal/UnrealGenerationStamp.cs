using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Shaderlyn.Unreal;

/// <summary>
/// <c>--unreal-generate</c> が読んだ ini と C++ の記録。生成した後に変わったかを確かめるのに使う。
/// </summary>
/// <remarks>
/// <para>
/// <b>生成物は、生成したときの ini と C++ から作った写しである。</b>
/// プロジェクトの設定 (ini) を変えたり、エンジンやプロジェクトの C++ を変えたりしても、生成し直すまで解析は古い値で読む。
/// Substrate を有効にしたのに <c>SUBSTRATE_ENABLED=0</c> のまま読むと、黙って誤検出が出る。
/// </para>
/// <para>
/// ini は中身のハッシュを、C++ は一覧と更新日時と大きさを記録する。
/// ini は数個なので一瞬で読める。C++ はエンジンだけで 9 万を超え、中身を読むと、ディスクのキャッシュが無いときに
/// 生成し直すのと同じくらい (UE 5.5 で約 100 秒) かかる。一覧と更新日時なら約 2 秒で済む。
/// </para>
/// </remarks>
public static class UnrealGenerationStamp
{
    /// <summary>記録のファイルの名前。生成物のフォルダーの直下に置く。</summary>
    public const string FileName = "ShaderlynGenerationStamp.tsv";

    /// <summary>記録の形式の版。形式を変えたら上げる。</summary>
    private const string FormatVersion = "1";

    /// <summary>
    /// 読んだ ini と C++ を記録する。
    /// </summary>
    /// <param name="outputDirectory">生成物のフォルダー。</param>
    /// <param name="engineDirectory">エンジンのフォルダー。</param>
    /// <param name="projectDirectory">プロジェクトのフォルダー。無ければ <see langword="null"/>。</param>
    /// <param name="sourceRoots">C++ を探したフォルダー。</param>
    public static void Write(string outputDirectory, string engineDirectory, string? projectDirectory, IEnumerable<string> sourceRoots)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(engineDirectory);
        ArgumentNullException.ThrowIfNull(sourceRoots);

        StringBuilder builder = new();
        builder.AppendLine("# Shaderlyn が --unreal-generate のときに読んだ ini と C++ の記録。解析を始めるときに、生成した後に変わったかを確かめる。");
        builder.Append("version\t").AppendLine(FormatVersion);

        foreach (string ini in IniFiles(engineDirectory, projectDirectory))
        {
            builder.Append("ini\t").Append(ini).Append('\t').AppendLine(HashFile(ini));
        }

        ImmutableArray<string> roots = [.. sourceRoots.Select(Path.GetFullPath)];

        for (int i = 0; i < roots.Length; i++)
        {
            builder.Append("root\t").AppendLine(roots[i]);

            foreach ((string relative, long ticks, long length) in EnumerateSources(roots[i]).OrderBy(f => f.Relative, StringComparer.Ordinal))
            {
                builder.Append(CultureInfo.InvariantCulture, $"file\t{i}\t{relative}\t{ticks}\t{length}").AppendLine();
            }
        }

        File.WriteAllText(Path.Combine(outputDirectory, FileName), builder.ToString());
    }

    /// <summary>
    /// 生成した後に ini と C++ が変わったかを確かめる。
    /// </summary>
    /// <param name="generatedDirectory">生成物のフォルダー (設定の <c>unreal: generated</c>)。</param>
    /// <returns>確かめた結果。生成物のフォルダーが無ければ <see langword="null"/>。</returns>
    public static UnrealGenerationStaleness? Check(string generatedDirectory)
    {
        ArgumentNullException.ThrowIfNull(generatedDirectory);

        if (!Directory.Exists(generatedDirectory))
        {
            return null;
        }

        string path = Path.Combine(generatedDirectory, FileName);
        string[] lines;

        try
        {
            lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lines = [];
        }

        if (!lines.Any(line => line == $"version\t{FormatVersion}"))
        {
            return new UnrealGenerationStaleness { HasRecord = false };
        }

        List<string> changedIni = [];
        List<string> roots = [];
        Dictionary<(int Root, string Relative), (long Ticks, long Length)> recorded = [];

        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');

            switch (fields[0])
            {
                case "ini" when fields.Length == 3:
                    if (HashFile(fields[1]) != fields[2])
                    {
                        changedIni.Add(fields[1]);
                    }

                    break;

                case "root" when fields.Length == 2:
                    roots.Add(fields[1]);
                    break;

                case "file" when fields.Length == 5
                                 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int root)
                                 && long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                                 && long.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out long length):
                    recorded[(root, fields[2])] = (ticks, length);
                    break;
            }
        }

        int changed = 0;
        int added = 0;
        HashSet<(int, string)> seen = [];

        for (int i = 0; i < roots.Count; i++)
        {
            foreach ((string relative, long ticks, long length) in EnumerateSources(roots[i]))
            {
                seen.Add((i, relative));

                if (!recorded.TryGetValue((i, relative), out (long Ticks, long Length) before))
                {
                    added++;
                }
                else if (before != (ticks, length))
                {
                    changed++;
                }
            }
        }

        return new UnrealGenerationStaleness
        {
            HasRecord = true,
            ChangedIniFiles = [.. changedIni],
            ChangedSourceFiles = changed,
            AddedSourceFiles = added,
            RemovedSourceFiles = recorded.Keys.Count(key => !seen.Contains(key)),
        };
    }

    /// <summary>生成が読む ini (コンソール変数の ini と、プラットフォームの設定)。</summary>
    private static IEnumerable<string> IniFiles(string engineDirectory, string? projectDirectory)
    {
        foreach (string ini in UnrealConsoleVariables.IniFiles(engineDirectory, projectDirectory))
        {
            yield return Path.GetFullPath(ini);
        }

        string config = Path.Combine(engineDirectory, "Config");

        if (Directory.Exists(config))
        {
            foreach (string ini in Directory.EnumerateFiles(config, "DataDrivenPlatformInfo.ini", SearchOption.AllDirectories)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(ini);
            }
        }
    }

    /// <summary>ファイルの中身のハッシュ。無ければ <c>-</c>。</summary>
    private static string HashFile(string path)
    {
        try
        {
            return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "-";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "-";
        }
    }

    /// <summary>フォルダーの下の C++ のファイル (<see cref="UnrealUniformBufferGenerator.IsSourceFile"/>)。</summary>
    private static IEnumerable<(string Relative, long Ticks, long Length)> EnumerateSources(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles("*.*", options))
        {
            if (UnrealUniformBufferGenerator.IsSourceFile(file.FullName))
            {
                yield return (Path.GetRelativePath(root, file.FullName), file.LastWriteTimeUtc.Ticks, file.Length);
            }
        }
    }
}

/// <summary>
/// 生成した後に ini と C++ が変わったかを確かめた結果 (<see cref="UnrealGenerationStamp.Check"/>)。
/// </summary>
public sealed class UnrealGenerationStaleness
{
    /// <summary>記録があったか。この版より前の Shaderlyn で生成したものには無い。</summary>
    public required bool HasRecord { get; init; }

    /// <summary>中身が変わった ini (消えたものと、新しくできたものも含む)。</summary>
    public ImmutableArray<string> ChangedIniFiles { get; init; } = [];

    /// <summary>更新日時か大きさが変わった C++ のファイルの数。</summary>
    public int ChangedSourceFiles { get; init; }

    /// <summary>増えた C++ のファイルの数。</summary>
    public int AddedSourceFiles { get; init; }

    /// <summary>消えた C++ のファイルの数。</summary>
    public int RemovedSourceFiles { get; init; }

    /// <summary>生成し直したほうがよいか。記録が無いときも <see langword="true"/>。</summary>
    public bool IsStale => !HasRecord || !ChangedIniFiles.IsEmpty || ChangedSourceFiles + AddedSourceFiles + RemovedSourceFiles > 0;

    /// <summary>利用者に伝える文。生成し直さなくてよければ <see langword="null"/>。</summary>
    /// <returns>文。</returns>
    public string? Describe()
    {
        if (!IsStale)
        {
            return null;
        }

        if (!HasRecord)
        {
            return "Unreal の生成物に、生成したときの ini と C++ の記録がありません。--unreal-generate をやり直すと、"
                   + "設定や C++ を変えたときに生成し直しが要ることを伝えられるようになります。";
        }

        List<string> reasons = [];

        if (!ChangedIniFiles.IsEmpty)
        {
            reasons.Add($"ini が変わりました ({string.Join(", ", ChangedIniFiles.Select(Path.GetFileName))})");
        }

        List<string> sources = [];

        if (ChangedSourceFiles > 0)
        {
            sources.Add($"変更 {ChangedSourceFiles} 件");
        }

        if (AddedSourceFiles > 0)
        {
            sources.Add($"追加 {AddedSourceFiles} 件");
        }

        if (RemovedSourceFiles > 0)
        {
            sources.Add($"削除 {RemovedSourceFiles} 件");
        }

        if (sources.Count > 0)
        {
            reasons.Add($"C++ のファイルが変わりました ({string.Join("、", sources)})");
        }

        return $"Unreal の生成物を作った後に、{string.Join("。また、", reasons)}。"
               + "解析は生成したときの値で読むので、--unreal-generate をやり直してください。";
    }
}
