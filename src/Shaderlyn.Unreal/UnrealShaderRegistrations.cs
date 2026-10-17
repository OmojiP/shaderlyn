using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Unreal;

/// <summary>
/// C++ がシェーダーを登録した 1 件。どの <c>.usf</c> の、どの関数を、どのステージでコンパイルするか。
/// </summary>
/// <param name="VirtualPath">シェーダーのファイルの仮想パス (<c>/Engine/Private/CapsuleShadowShaders.usf</c>)。</param>
/// <param name="EntryPoint">エントリポイントの欄。ふつうは関数の名前で、ヒットグループは関数を並べる (<see cref="UnrealShaderRegistrations.EntryFunctions"/>)。</param>
/// <param name="Frequency">ステージ (C++ の <c>SF_Pixel</c> などから <c>SF_</c> を除いたもの)。</param>
/// <param name="ShaderClass">登録した C++ のクラス。</param>
/// <param name="Macro">登録に使ったマクロ (<c>IMPLEMENT_GLOBAL_SHADER</c> など)。</param>
/// <param name="SourceFile">登録を書いた C++ のファイル。</param>
public sealed record UnrealShaderRegistration(
    string VirtualPath,
    string EntryPoint,
    string Frequency,
    string ShaderClass,
    string Macro,
    string SourceFile);

/// <summary>
/// C++ のシェーダーの登録 (<c>IMPLEMENT_GLOBAL_SHADER</c> など) を読み書きする。
/// </summary>
/// <remarks>
/// <para>
/// <b>登録から、<c>.usf</c> ごとのエントリポイントとステージが分かる。</b>
/// 設定の <c>units:</c> に書かなくても、エンジンが実際にコンパイルする単位で解析できる。
/// </para>
/// <para>
/// 読むのは次の 3 つの形である (UE 5.5 のエンジンとプラグインで約 1800 件)。
/// </para>
/// <code>
/// IMPLEMENT_GLOBAL_SHADER(FClass, "/Engine/Private/X.usf", "MainCS", SF_Compute);
/// IMPLEMENT_SHADER_TYPE(, FClass, TEXT("/Engine/Private/X.usf"), TEXT("Main"), SF_Pixel);
/// IMPLEMENT_MATERIAL_SHADER_TYPE(, FClass, TEXT("/Engine/Private/X.usf"), TEXT("MainVS"), SF_Vertex);
/// </code>
/// <para>
/// これらを包んだ独自のマクロ (<c>IMPLEMENT_SLATE_PIXELSHADER_TYPE</c> など) は読めない。パスがマクロの定義の中にあるためである。
/// 読めなかったマクロは名前と数を伝える (<see cref="UnrealUniformBufferGeneration.UnreadShaderRegistrations"/>)。
/// </para>
/// <para>
/// 生成 (<c>--unreal-generate</c>) のときに読んで <see cref="FileName"/> に書き出し、解析はそれを読む。
/// エンジンの C++ は数万ファイルあり、解析のたびに読むと遅いためである。
/// </para>
/// </remarks>
public static partial class UnrealShaderRegistrations
{
    /// <summary>生成のフォルダーに書き出す一覧のファイルの名前。</summary>
    public const string FileName = "ShaderlynShaderRegistrations.tsv";

    /// <summary>
    /// C++ のコード (コメントとマクロの定義を除いたもの) から登録を拾う。
    /// </summary>
    /// <param name="sourceFile">C++ のファイル。</param>
    /// <param name="code">コメントとマクロの定義を除いた中身。</param>
    /// <param name="registrations">拾った登録を足す先。</param>
    /// <param name="unread">読めなかった登録のマクロの名前と数を足す先。</param>
    internal static void Collect(
        string sourceFile,
        string code,
        ConcurrentBag<UnrealShaderRegistration> registrations,
        ConcurrentDictionary<string, int> unread)
    {
        Dictionary<string, int> read = new(StringComparer.Ordinal);

        foreach (Match match in GlobalShader().Matches(code))
        {
            registrations.Add(Create(match, "IMPLEMENT_GLOBAL_SHADER", sourceFile));
            read["IMPLEMENT_GLOBAL_SHADER"] = read.GetValueOrDefault("IMPLEMENT_GLOBAL_SHADER") + 1;
        }

        foreach (Match match in ShaderType().Matches(code))
        {
            string macro = match.Groups["macro"].Value;
            registrations.Add(Create(match, macro, sourceFile));
            read[macro] = read.GetValueOrDefault(macro) + 1;
        }

        // 読めなかった登録を数える。独自のマクロと、3 つの形でも引数を読めなかったもの (パスを別のマクロで書いたなど)。
        foreach (IGrouping<string, string> group in AnyShaderMacro().Matches(code)
                     .Select(m => m.Groups["macro"].Value)
                     .Where(name => !IsNotShaderRegistration(name))
                     .GroupBy(name => name, StringComparer.Ordinal))
        {
            int missed = group.Count() - read.GetValueOrDefault(group.Key);

            if (missed > 0)
            {
                unread.AddOrUpdate(group.Key, missed, (_, count) => count + missed);
            }
        }
    }

    /// <summary>シェーダーの登録ではない <c>IMPLEMENT_…SHADER…</c> のマクロ (構造体や uniform buffer の登録) か。</summary>
    /// <param name="name">マクロの名前。</param>
    /// <returns>シェーダーの登録ではなければ <see langword="true"/>。</returns>
    /// <remarks>シェーダーのパイプライン (<c>IMPLEMENT_SHADERPIPELINE_TYPE_VSPS</c>) は、登録済みのシェーダーを組み合わせるだけなので数えない。</remarks>
    private static bool IsNotShaderRegistration(string name)
        => name.Contains("PARAMETER", StringComparison.Ordinal)
           || name.Contains("UNIFORM_BUFFER", StringComparison.Ordinal)
           || name.Contains("SHADERPIPELINE", StringComparison.Ordinal)
           || name.EndsWith("_STRUCT", StringComparison.Ordinal);

    /// <summary>登録のエントリポイントの欄を、関数の名前に分ける。</summary>
    /// <param name="entryPoint">エントリポイントの欄。</param>
    /// <returns>関数の名前。</returns>
    /// <remarks>
    /// レイトレーシングのヒットグループは、1 つの登録に関数を並べる
    /// (<c>"closesthit=MainCHS anyhit=MainAHS intersection=MainIS"</c>)。どれもエントリポイントである。
    /// </remarks>
    public static ImmutableArray<string> EntryFunctions(string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);

        return entryPoint.Contains('=', StringComparison.Ordinal)
            ?
            [
                .. entryPoint.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part[(part.IndexOf('=', StringComparison.Ordinal) + 1)..])
                    .Where(name => name.Length > 0),
            ]
            : [entryPoint];
    }

    private static UnrealShaderRegistration Create(Match match, string macro, string sourceFile)
        => new(
            match.Groups["path"].Value,
            match.Groups["entry"].Value,
            match.Groups["stage"].Value,
            match.Groups["class"].Value.Trim(),
            macro,
            sourceFile);

    /// <summary>登録の一覧を書き出す。</summary>
    /// <param name="outputDirectory">書き出す先 (生成のフォルダー)。</param>
    /// <param name="registrations">登録。</param>
    /// <returns>書き出したファイル。</returns>
    internal static string Write(string outputDirectory, IEnumerable<UnrealShaderRegistration> registrations)
    {
        StringBuilder text = new();
        text.Append("# Shaderlyn が C++ のシェーダーの登録から作った。仮想パス\tエントリポイント\tステージ\tクラス\tマクロ\tC++ のファイル\n");

        foreach (UnrealShaderRegistration r in registrations
                     .Distinct()
                     .OrderBy(r => r.VirtualPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.EntryPoint, StringComparer.Ordinal)
                     .ThenBy(r => r.Frequency, StringComparer.Ordinal)
                     .ThenBy(r => r.ShaderClass, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"{r.VirtualPath}\t{r.EntryPoint}\t{r.Frequency}\t{r.ShaderClass}\t{r.Macro}\t{r.SourceFile}\n");
        }

        string file = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(file, text.ToString());
        return file;
    }

    /// <summary>書き出した登録の一覧を読む。</summary>
    /// <param name="generatedDirectory">生成のフォルダー。</param>
    /// <returns>登録。一覧が無ければ空。</returns>
    public static ImmutableArray<UnrealShaderRegistration> Read(string generatedDirectory)
    {
        ArgumentNullException.ThrowIfNull(generatedDirectory);
        string file = Path.Combine(generatedDirectory, FileName);

        try
        {
            return
            [
                .. File.ReadLines(file)
                    .Where(line => line.Length > 0 && line[0] != '#')
                    .Select(line => line.Split('\t'))
                    .Where(parts => parts.Length >= 6)
                    .Select(parts => new UnrealShaderRegistration(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5])),
            ];
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

    /// <summary>ステージ (<c>SF_</c> を除いた名前) を、HLSL のステージにする。</summary>
    /// <param name="frequency">ステージ。</param>
    /// <returns>HLSL のステージ。分からなければ <see langword="null"/>。</returns>
    /// <remarks>レイトレーシングとワークグラフのステージは、ライブラリ (<c>lib_6_x</c>) としてコンパイルされる。</remarks>
    public static HlslShaderStage? ToStage(string frequency) => frequency switch
    {
        "Pixel" => HlslShaderStage.Pixel,
        "Vertex" => HlslShaderStage.Vertex,
        "Geometry" => HlslShaderStage.Geometry,
        "Compute" => HlslShaderStage.Compute,
        "Mesh" => HlslShaderStage.Mesh,
        "Amplification" => HlslShaderStage.Amplification,
        "RayGen" or "RayMiss" or "RayHitGroup" or "RayCallable" or "WorkGraphRoot" or "WorkGraphComputeNode" => HlslShaderStage.Library,
        _ => null,
    };

    /// <summary>
    /// エンジンの C++ が、ステージごとにシェーダーに定義するマクロ (UE 5.5 の <c>ShaderCompiler.cpp</c>)。
    /// </summary>
    /// <remarks>そのステージのものが 1、ほかは 0 である。</remarks>
    public static ImmutableArray<(string Macro, string Frequency)> StageMacros { get; } =
    [
        ("PIXELSHADER", "Pixel"),
        ("VERTEXSHADER", "Vertex"),
        ("MESHSHADER", "Mesh"),
        ("AMPLIFICATIONSHADER", "Amplification"),
        ("GEOMETRYSHADER", "Geometry"),
        ("COMPUTESHADER", "Compute"),
        ("RAYCALLABLESHADER", "RayCallable"),
        ("RAYHITGROUPSHADER", "RayHitGroup"),
        ("RAYGENSHADER", "RayGen"),
        ("RAYMISSSHADER", "RayMiss"),
        ("WORKGRAPHROOTSHADER", "WorkGraphRoot"),
        ("WORKGRAPHCOMPUTESHADER", "WorkGraphComputeNode"),
    ];

    /// <summary>そのステージでエンジンが定義するマクロ (そのステージのものが 1、ほかは 0)。</summary>
    /// <param name="frequency">ステージ。</param>
    /// <returns>マクロ。</returns>
    public static ImmutableDictionary<string, string> CreateStageMacros(string frequency)
        => StageMacros.ToImmutableDictionary(
            p => p.Macro,
            p => p.Frequency == frequency ? "1" : "0",
            StringComparer.Ordinal);

    [GeneratedRegex("""\bIMPLEMENT_GLOBAL_SHADER\s*\(\s*(?<class>[^,"]+?)\s*,\s*"(?<path>[^"]+)"\s*,\s*"(?<entry>[^"]+)"\s*,\s*SF_(?<stage>\w+)\s*\)""", RegexOptions.CultureInvariant)]
    private static partial Regex GlobalShader();

    [GeneratedRegex("""\b(?<macro>IMPLEMENT_(?:MATERIAL_)?SHADER_TYPE)\s*\([^,()]*,\s*(?<class>[^"]+?)\s*,\s*TEXT\s*\(\s*"(?<path>[^"]+)"\s*\)\s*,\s*TEXT\s*\(\s*"(?<entry>[^"]+)"\s*\)\s*,\s*SF_(?<stage>\w+)\s*\)""", RegexOptions.CultureInvariant)]
    private static partial Regex ShaderType();

    [GeneratedRegex("""\b(?<macro>IMPLEMENT_\w*SHADER\w*)\s*\(""", RegexOptions.CultureInvariant)]
    private static partial Regex AnyShaderMacro();
}
