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

    /// <summary>
    /// 登録を包んだ独自のマクロの定義と、その呼び出しを集めたもの。すべてのファイルを読んでから展開する
    /// (<see cref="ExpandMacros"/>)。定義と呼び出しは別のファイルにあることがある。
    /// </summary>
    internal sealed class MacroRegistrations
    {
        /// <summary>マクロの名前から、定義したファイルと仮引数と本体。同じ名前をファイルごとに別に定義していることがある。</summary>
        public ConcurrentDictionary<string, ConcurrentBag<(string File, ImmutableArray<string> Parameters, string Body)>> Definitions { get; } =
            new(StringComparer.Ordinal);

        /// <summary>呼び出し (マクロの名前、括弧の中、C++ のファイル)。</summary>
        public ConcurrentBag<(string Name, string Arguments, string File)> Invocations { get; } = [];
    }

    /// <summary>
    /// 登録を包んだ独自のマクロ (<c>#define IMPLEMENT_RESOLVE_SHADER(Width, MSAA, UseFMask) … IMPLEMENT_SHADER_TYPE(…)</c>) の定義と、
    /// <c>IMPLEMENT_</c> で始まるマクロの呼び出しを集める。並行して呼んでよい。
    /// </summary>
    /// <param name="sourceFile">C++ のファイル。</param>
    /// <param name="text">ファイルの中身。</param>
    /// <param name="code">コメントとマクロの定義を除いた中身。</param>
    /// <param name="macros">集める先。</param>
    internal static void CollectMacros(string sourceFile, string text, string code, MacroRegistrations macros)
    {
        if (!text.Contains("#define", StringComparison.Ordinal) && !code.Contains("IMPLEMENT_", StringComparison.Ordinal))
        {
            return;
        }

        foreach (string line in JoinContinuations(BlockComment().Replace(text, " ")))
        {
            if (MacroDefinition().Match(line) is { Success: true } definition)
            {
                string body = LineComment().Replace(definition.Groups["body"].Value, " ");

                // 登録の 3 つの形 (IMPLEMENT_SHADER_TYPE など) 自身の定義は集めない。展開すると、登録として読めなくなる。
                if (body.Contains("IMPLEMENT_", StringComparison.Ordinal) && !IsBaseRegistration(definition.Groups["name"].Value))
                {
                    ImmutableArray<string> parameters =
                        [.. definition.Groups["parameters"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
                    macros.Definitions.GetOrAdd(definition.Groups["name"].Value, _ => []).Add((sourceFile, parameters, body));
                }
            }
        }

        foreach (Match call in MacroCall().Matches(code))
        {
            string name = call.Groups["name"].Value;

            if (IsBaseRegistration(name)
                || ReadParenthesized(code, call.Index + call.Length - 1) is not { } arguments)
            {
                continue;
            }

            macros.Invocations.Add((name, arguments, sourceFile));
        }
    }

    /// <summary>
    /// 集めた独自のマクロの呼び出しを展開し、中の登録を拾う。拾えた呼び出しは、読めなかった登録の数から引く。
    /// </summary>
    /// <param name="macros">集めた定義と呼び出し。</param>
    /// <param name="registrations">拾った登録を足す先。</param>
    /// <param name="unread">読めなかった登録のマクロの名前と数。</param>
    /// <remarks>
    /// <para>
    /// C のプリプロセッサと同じく、引数は括弧の外のカンマで分け、<c>#引数</c> を文字列にし、<c>##</c> でつなぐ。
    /// 展開した中にほかの独自のマクロがあれば、それも展開する (4 段まで)。
    /// </para>
    /// <para>
    /// <b>展開した中の <c>typedef</c> から、クラスのテンプレートの引数が分かる。</b>
    /// <c>typedef TShadowProjectionPS&lt;Quality, UseFadePlane, …&gt; FShadowProjectionPS##Quality##…;</c> の後に
    /// その名前で登録するので、登録のクラスを <c>TShadowProjectionPS&lt;1,false,…&gt;</c> にする。クラスのマクロを引数ごとに読める。
    /// </para>
    /// </remarks>
    internal static void ExpandMacros(
        MacroRegistrations macros,
        ConcurrentBag<UnrealShaderRegistration> registrations,
        ConcurrentDictionary<string, int> unread)
    {
        foreach ((string name, string arguments, string file) in macros.Invocations)
        {
            if (Expand(macros, name, arguments, file, depth: 0) is not { } expanded)
            {
                continue;
            }

            Dictionary<string, string> typedefs = new(StringComparer.Ordinal);

            foreach (Match typedef in Typedef().Matches(expanded))
            {
                typedefs[typedef.Groups["alias"].Value] = Regex.Replace(typedef.Groups["target"].Value, @"\s+", string.Empty);
            }

            int found = 0;

            foreach (Match match in GlobalShader().Matches(expanded))
            {
                registrations.Add(WithTypedef(Create(match, name, file), typedefs));
                found++;
            }

            foreach (Match match in ShaderType().Matches(expanded))
            {
                registrations.Add(WithTypedef(Create(match, name, file), typedefs));
                found++;
            }

            if (found > 0)
            {
                unread.AddOrUpdate(name, 0, (_, count) => Math.Max(0, count - 1));
            }
        }

        foreach (string name in unread.Where(p => p.Value == 0).Select(p => p.Key).ToList())
        {
            unread.TryRemove(name, out _);
        }

        static UnrealShaderRegistration WithTypedef(UnrealShaderRegistration registration, Dictionary<string, string> typedefs)
            => typedefs.TryGetValue(registration.ShaderClass, out string? target)
                ? registration with { ShaderClass = target }
                : registration;
    }

    /// <summary>独自のマクロの呼び出しを 1 つ展開する。</summary>
    /// <returns>展開した中身。定義が無いか、引数の数が合わなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 同じ名前の定義が複数あれば、呼び出しと同じファイルの定義を使う。無ければ、引数の数が合うもののうちファイルの名前が先のもの。
    /// </remarks>
    private static string? Expand(MacroRegistrations macros, string name, string arguments, string file, int depth)
    {
        if (depth > 4 || !macros.Definitions.TryGetValue(name, out ConcurrentBag<(string File, ImmutableArray<string> Parameters, string Body)>? candidates))
        {
            return null;
        }

        List<string> values = SplitArguments(arguments);
        (string File, ImmutableArray<string> Parameters, string Body)[] fitting =
        [
            .. candidates
                .Where(d => values.Count == d.Parameters.Length || (d.Parameters.IsEmpty && values is [""]))
                .OrderBy(d => string.Equals(d.File, file, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => d.File, StringComparer.OrdinalIgnoreCase),
        ];

        if (fitting.Length == 0)
        {
            return null;
        }

        (_, ImmutableArray<string> parameters, string definitionBody) = fitting[0];
        (ImmutableArray<string> Parameters, string Body) definition = (parameters, definitionBody);

        Dictionary<string, string> bound = new(StringComparer.Ordinal);

        for (int i = 0; i < definition.Parameters.Length; i++)
        {
            bound[definition.Parameters[i]] = values[i].Trim();
        }

        // #引数 を文字列に、引数を値に置き換えてから、## でつなぐ。
        string body = Stringize().Replace(definition.Body, m => bound.TryGetValue(m.Groups["name"].Value, out string? value) ? "\"" + value + "\"" : m.Value);
        body = Identifier().Replace(body, m => bound.TryGetValue(m.Value, out string? value) ? value : m.Value);
        body = Paste().Replace(body, string.Empty);

        // 中のほかの独自のマクロも展開する。
        StringBuilder result = new();
        int position = 0;

        foreach (Match call in MacroCall().Matches(body))
        {
            if (call.Index < position
                || !macros.Definitions.ContainsKey(call.Groups["name"].Value)
                || ReadParenthesized(body, call.Index + call.Length - 1) is not { } inner
                || Expand(macros, call.Groups["name"].Value, inner, file, depth + 1) is not { } nested)
            {
                continue;
            }

            result.Append(body, position, call.Index - position).Append(nested);
            position = call.Index + call.Length + inner.Length + 1;
        }

        return result.Append(body, position, body.Length - position).ToString();
    }

    /// <summary>開き括弧から、対応する閉じ括弧までの中身を返す。</summary>
    /// <param name="text">文字列。</param>
    /// <param name="open">開き括弧の位置。</param>
    /// <returns>括弧の中身。閉じていなければ <see langword="null"/>。</returns>
    private static string? ReadParenthesized(string text, int open)
    {
        int depth = 0;

        for (int i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')' when --depth == 0:
                    return text[(open + 1)..i];
                case '"':
                    int close = text.IndexOf('"', i + 1);

                    if (close < 0)
                    {
                        return null;
                    }

                    i = close;
                    break;
            }
        }

        return null;
    }

    /// <summary>マクロの引数を、括弧の外のカンマで分ける。</summary>
    private static List<string> SplitArguments(string arguments)
    {
        List<string> values = [];
        int depth = 0;
        int start = 0;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    values.Add(arguments[start..i]);
                    start = i + 1;
                    break;
            }
        }

        values.Add(arguments[start..]);
        return values;
    }

    /// <summary>行の継続 (<c>\</c> と改行) をつなげた行を返す。</summary>
    private static IEnumerable<string> JoinContinuations(string text)
    {
        StringBuilder current = new();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.EndsWith('\\'))
            {
                current.Append(line, 0, line.Length - 1).Append(' ');
                continue;
            }

            current.Append(line);
            yield return current.ToString();
            current.Clear();
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>登録を読む 3 つの形のマクロ (<see cref="GlobalShader"/> と <see cref="ShaderType"/>) か。</summary>
    private static bool IsBaseRegistration(string name)
        => name is "IMPLEMENT_GLOBAL_SHADER" or "IMPLEMENT_SHADER_TYPE" or "IMPLEMENT_MATERIAL_SHADER_TYPE";

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

    /// <summary><c>IMPLEMENT_</c> で始まるマクロの呼び出しの、開き括弧まで。</summary>
    [GeneratedRegex("""\b(?<name>IMPLEMENT_\w+)\s*\(""", RegexOptions.CultureInvariant)]
    private static partial Regex MacroCall();

    /// <summary>関数形式のマクロの定義 (行の継続をつなげた 1 行)。</summary>
    [GeneratedRegex("""^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\((?<parameters>[^)]*)\)(?<body>.*)$""", RegexOptions.CultureInvariant)]
    private static partial Regex MacroDefinition();

    [GeneratedRegex("""/\*.*?\*/""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockComment();

    [GeneratedRegex("""//.*$""", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();

    /// <summary>文字列にする仮引数 (<c>#Name</c>。<c>##</c> の片方ではないもの)。</summary>
    [GeneratedRegex("""(?<!#)#\s*(?<name>[A-Za-z_]\w*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Stringize();

    [GeneratedRegex("""\b[A-Za-z_]\w*\b""", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    /// <summary>トークンをつなぐ <c>##</c> と、その前後の空白。</summary>
    [GeneratedRegex("""\s*##\s*""", RegexOptions.CultureInvariant)]
    private static partial Regex Paste();

    /// <summary><c>typedef 型 名前;</c>。</summary>
    [GeneratedRegex("""\btypedef\s+(?<target>[^;]+?)\s+(?<alias>[A-Za-z_]\w*)\s*;""", RegexOptions.CultureInvariant)]
    private static partial Regex Typedef();
}
