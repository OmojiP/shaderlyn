using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.Glsl;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>GLSL の組み込み関数の多重定義 1 つ。</summary>
/// <param name="ReturnType">戻り値の型。</param>
/// <param name="Parameters">仮引数の型。最後が <c>...</c> なら、そこから後ろにいくつでも引数を書ける (<c>debugPrintfEXT</c>)。</param>
/// <param name="WritesBack">仮引数ごとの、書き戻す引数 (<c>out</c> / <c>inout</c>) か。</param>
/// <param name="Extensions">使うのに要る拡張 (どれか 1 つ)。要らなければ空。</param>
internal sealed record GlslBuiltinFunction(
    string ReturnType,
    ImmutableArray<string> Parameters,
    ImmutableArray<bool> WritesBack,
    ImmutableArray<string> Extensions)
{
    /// <summary>後ろにいくつでも引数を書けるか (仮引数の最後が <c>...</c>)。</summary>
    public bool IsVariadic => !Parameters.IsEmpty && Parameters[^1] == "...";

    /// <summary>型の決まった仮引数の数 (<c>...</c> を除く)。</summary>
    public int FixedParameterCount => IsVariadic ? Parameters.Length - 1 : Parameters.Length;
}

/// <summary>GLSL の組み込み変数 1 つ。</summary>
/// <param name="Type">型。配列なら <c>[]</c> や <c>[32]</c> が付く。ブロックの配列 (<c>gl_in</c>) は <c>block</c>。</param>
/// <param name="Storage">記憶域 (<c>in</c> / <c>out</c> / <c>uniform</c> / <c>const</c>)。</param>
/// <param name="Extensions">使うのに要る拡張 (どれか 1 つ)。要らなければ空。</param>
internal sealed record GlslBuiltinVariable(string Type, string Storage, ImmutableArray<string> Extensions);

/// <summary>
/// GLSL の組み込みの関数と変数の表 (<c>GlslBuiltins.g.txt</c>)。
/// </summary>
/// <remarks>
/// <para>
/// 表は <c>tools/glsl/builtins.mjs</c> が glslang の <c>--dump-builtin-symbols</c> から作る。
/// 構成 (Vulkan か OpenGL、プロファイル、バージョン、ステージ) ごとに、使える関数と変数が違う。
/// </para>
/// <para>
/// <b>分からないものは、ありうるすべての構成を合わせる。</b>
/// ステージの分からない断片 (<c>.glsl</c>) はすべてのステージの、バージョンの分からない木はすべてのバージョンの組み込みを使う。
/// glslang に渡す指定 (<c>-V</c>) はソースから分からないので、Vulkan と OpenGL の組み込みはいつも合わせる。
/// 多く知っているほうが、正しいコードを「宣言されていない」と言わずに済む。
/// </para>
/// </remarks>
internal sealed class GlslBuiltins
{
    /// <summary>埋め込んだ表の名前。</summary>
    private const string ResourceName = "Shaderlyn.Semantics.GlslBuiltins.txt";

    private static readonly Lazy<GlslBuiltins> Table = new(Load);

    private readonly ConcurrentDictionary<(int? Version, GlslProfile Profile, GlslShaderStage? Stage), GlslBuiltinView> _views = new();

    private GlslBuiltins(
        ImmutableArray<(string Profile, int Version, string Stage)> configurations,
        ImmutableArray<BitArray> sets,
        FrozenDictionary<string, ImmutableArray<(GlslBuiltinFunction Function, int Set)>> functions,
        FrozenDictionary<string, ImmutableArray<(GlslBuiltinVariable Variable, int Set)>> variables)
    {
        Configurations = configurations;
        Sets = sets;
        Functions = functions;
        Variables = variables;
    }

    /// <summary>表の構成 (プロファイル・バージョン・ステージ)。番号は表の構成の番号。</summary>
    private ImmutableArray<(string Profile, int Version, string Stage)> Configurations { get; }

    /// <summary>構成の集まり。番号は表の集まりの番号。</summary>
    private ImmutableArray<BitArray> Sets { get; }

    /// <summary>関数の名前から、多重定義と、使える構成の集まり。</summary>
    internal FrozenDictionary<string, ImmutableArray<(GlslBuiltinFunction Function, int Set)>> Functions { get; }

    /// <summary>変数の名前から、型と、使える構成の集まり。</summary>
    internal FrozenDictionary<string, ImmutableArray<(GlslBuiltinVariable Variable, int Set)>> Variables { get; }

    /// <summary>表。初めて使うときに読む。</summary>
    public static GlslBuiltins Instance => Table.Value;

    /// <summary>
    /// 言語のバージョン・プロファイル・ステージで使える組み込みを返す。
    /// </summary>
    /// <param name="version">バージョン。分からなければ <see langword="null"/> (すべてのバージョンを合わせる)。</param>
    /// <param name="profile">プロファイル。</param>
    /// <param name="stage">ステージ。分からなければ <see langword="null"/> (すべてのステージを合わせる)。</param>
    /// <returns>使える組み込み。</returns>
    public GlslBuiltinView For(int? version, GlslProfile profile, GlslShaderStage? stage)
        => _views.GetOrAdd((version, profile, stage), key => new GlslBuiltinView(this, Select(key.Version, key.Profile, key.Stage)));

    /// <summary>当てはまる構成を選ぶ。</summary>
    private BitArray Select(int? version, GlslProfile profile, GlslShaderStage? stage)
    {
        string? stageName = stage is { } s ? StageName(s) : null;
        string profileName = profile switch
        {
            GlslProfile.Es => "es",
            GlslProfile.Compatibility => "compatibility",
            _ => "core",
        };

        // 表に無いバージョン (#version 500 など) は、表にある、それより前でいちばん新しいバージョンとして読む。
        int? listed = version is { } v
            ? Configurations.Where(c => c.Profile == profileName && c.Version <= v).Select(c => (int?)c.Version).Max()
            : null;

        BitArray selected = new(Configurations.Length);

        for (int i = 0; i < Configurations.Length; i++)
        {
            (string p, int ver, string st) = Configurations[i];

            selected[i] = (stageName is null || st == stageName)
                          && (version is null || listed is null || (p == profileName && ver == listed));
        }

        return selected;
    }

    /// <summary>表の中のステージの名前 (拡張子)。</summary>
    private static string StageName(GlslShaderStage stage) => stage switch
    {
        GlslShaderStage.Vertex => "vert",
        GlslShaderStage.TessellationControl => "tesc",
        GlslShaderStage.TessellationEvaluation => "tese",
        GlslShaderStage.Geometry => "geom",
        GlslShaderStage.Fragment => "frag",
        GlslShaderStage.Compute => "comp",
        GlslShaderStage.Task => "task",
        GlslShaderStage.Mesh => "mesh",
        GlslShaderStage.RayGeneration => "rgen",
        GlslShaderStage.Intersection => "rint",
        GlslShaderStage.AnyHit => "rahit",
        GlslShaderStage.ClosestHit => "rchit",
        GlslShaderStage.Miss => "rmiss",
        _ => "rcall",
    };

    /// <summary>構成の集まりが、選んだ構成のどれかを含むか。</summary>
    internal bool Intersects(int set, BitArray selected)
    {
        BitArray members = Sets[set];

        for (int i = 0; i < selected.Length; i++)
        {
            if (selected[i] && members[i])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>埋め込んだ表を読む。</summary>
    private static GlslBuiltins Load()
    {
        using Stream stream = typeof(GlslBuiltins).Assembly.GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException($"埋め込んだ表 {ResourceName} がありません。");
        using StreamReader reader = new(stream);

        Dictionary<int, int> configurationIndex = [];
        List<(string Profile, int Version, string Stage)> configurations = [];
        List<string> setLines = [];
        Dictionary<string, List<(GlslBuiltinFunction, int)>> functions = new(StringComparer.Ordinal);
        Dictionary<string, List<(GlslBuiltinVariable, int)>> variables = new(StringComparer.Ordinal);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] fields = line.Split('\t');

            switch (fields[0])
            {
                case "C":
                    configurationIndex[int.Parse(fields[1], CultureInfo.InvariantCulture)] = configurations.Count;
                    configurations.Add((fields[3], int.Parse(fields[4], CultureInfo.InvariantCulture), fields[5]));
                    break;

                case "S":
                    setLines.Add(fields[2]);
                    break;

                case "F":
                    string[] parameters = fields[3].Length == 0 ? [] : fields[3].Split(';');
                    Add(functions, fields[1], (new GlslBuiltinFunction(
                        fields[2],
                        [.. parameters.Select(p => p[(p.IndexOf(' ', StringComparison.Ordinal) + 1)..])],
                        [.. parameters.Select(p => p.StartsWith("out ", StringComparison.Ordinal) || p.StartsWith("inout ", StringComparison.Ordinal))],
                        SplitExtensions(fields[4])), int.Parse(fields[5], CultureInfo.InvariantCulture)));
                    break;

                case "V":
                    Add(variables, fields[1], (new GlslBuiltinVariable(fields[2], fields[3], SplitExtensions(fields[4])),
                        int.Parse(fields[5], CultureInfo.InvariantCulture)));
                    break;

                default:
                    break;
            }
        }

        // 集まりは、表の構成の番号の範囲で書かれている。読んだ構成の並びの番号に直す。
        ImmutableArray<BitArray> sets =
        [
            .. setLines.Select(ranges =>
            {
                BitArray set = new(configurations.Count);

                foreach (string range in ranges.Split(','))
                {
                    int dash = range.IndexOf('-', StringComparison.Ordinal);
                    int from = int.Parse(dash < 0 ? range : range[..dash], CultureInfo.InvariantCulture);
                    int to = dash < 0 ? from : int.Parse(range[(dash + 1)..], CultureInfo.InvariantCulture);

                    for (int i = from; i <= to; i++)
                    {
                        if (configurationIndex.TryGetValue(i, out int index))
                        {
                            set[index] = true;
                        }
                    }
                }

                return set;
            }),
        ];

        return new GlslBuiltins(
            [.. configurations],
            sets,
            functions.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal),
            variables.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal));

        static void Add<T>(Dictionary<string, List<T>> map, string name, T item)
        {
            if (!map.TryGetValue(name, out List<T>? list))
            {
                map[name] = list = [];
            }

            list.Add(item);
        }

        static ImmutableArray<string> SplitExtensions(string text)
            => text.Length == 0 ? [] : [.. text.Split(',')];
    }
}

/// <summary>
/// ある構成 (バージョン・プロファイル・ステージ) で使える GLSL の組み込み。
/// </summary>
internal sealed class GlslBuiltinView
{
    private readonly GlslBuiltins _table;
    private readonly BitArray _selected;
    private readonly ConcurrentDictionary<string, ImmutableArray<GlslBuiltinFunction>> _functions = new(StringComparer.Ordinal);

    /// <summary>表と、選んだ構成から作る。</summary>
    /// <param name="table">表。</param>
    /// <param name="selected">選んだ構成。</param>
    internal GlslBuiltinView(GlslBuiltins table, BitArray selected)
    {
        _table = table;
        _selected = selected;
        FunctionNames = [.. table.Functions.Where(p => p.Value.Any(f => table.Intersects(f.Set, selected))).Select(p => p.Key).Order(StringComparer.Ordinal)];
    }

    /// <summary>使える関数の名前。</summary>
    public ImmutableArray<string> FunctionNames { get; }

    /// <summary>その名前の関数の多重定義のうち、使えるもの。</summary>
    /// <param name="name">名前。</param>
    /// <returns>多重定義。無ければ空。</returns>
    public ImmutableArray<GlslBuiltinFunction> GetFunctions(string name)
        => _functions.GetOrAdd(name, n => _table.Functions.TryGetValue(n, out ImmutableArray<(GlslBuiltinFunction Function, int Set)> all)
            ? [.. all.Where(f => _table.Intersects(f.Set, _selected)).Select(f => f.Function)]
            : []);

    /// <summary>その名前の組み込み変数を引く。</summary>
    /// <param name="name">名前。</param>
    /// <returns>変数。無ければ <see langword="null"/>。拡張の要らないものを先に返す。</returns>
    public GlslBuiltinVariable? GetVariable(string name)
        => _table.Variables.TryGetValue(name, out ImmutableArray<(GlslBuiltinVariable Variable, int Set)> all)
            ? all.Where(v => _table.Intersects(v.Set, _selected)).Select(v => v.Variable).OrderBy(v => v.Extensions.Length).FirstOrDefault()
            : null;
}
