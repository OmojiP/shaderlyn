using System.Collections.Immutable;
using Shaderlyn.Configuration.Yaml;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Configuration;

/// <summary>
/// <c>.shaderlyn.yaml</c> を読み込む。
/// </summary>
/// <remarks>
/// <para>
/// <b>知らないキーは誤りとして報告する。</b>
/// 綴りを間違えたキーを、何も伝えずに無視すると、
/// 設定したつもりのルールが効かないまま運用されることになる。
/// これは静的解析の導入で最も避けたい失敗である。
/// </para>
/// <para>
/// 一方で、問題があっても読み込み自体は失敗させない。
/// 読めた範囲を返し、問題は <see cref="ShaderlynConfiguration.Problems"/> で報告する。
/// </para>
/// </remarks>
internal static class ConfigurationLoader
{
    /// <summary>最上位で認識するキー。</summary>
    /// <remarks>
    /// <c>profile</c> は v0.1 の書き方である。今は <c>unity:</c> の節に書く。節の外も、警告を出して読む。
    /// </remarks>
    private static readonly string[] KnownRootKeys =
    [
        "version", "environment", "unity", "unreal", "hlsl", "profile", "include-paths", "defines", "symbols", "units", "rules",
    ];

    /// <summary><c>unity:</c> の節で認識するキー。</summary>
    private static readonly string[] KnownUnityKeys = ["profile", "project", "editor"];

    /// <summary><c>unreal:</c> の節で認識するキー。</summary>
    private static readonly string[] KnownUnrealKeys = ["engine", "project", "virtual-paths"];

    /// <summary><c>hlsl:</c> の節で認識するキー。</summary>
    private static readonly string[] KnownHlslKeys = ["version"];

    /// <summary><c>units:</c> の要素で認識するキー。</summary>
    private static readonly string[] KnownUnitKeys = ["files", "entry", "target", "defines", "symbols"];

    /// <summary><c>symbols:</c> の要素で認識するキー。</summary>
    private static readonly string[] KnownSymbolKeys = ["name", "values", "one-of", "at-most-one"];

    /// <summary>コンパイルのターゲットの形 (<c>ps_6_6</c>)。</summary>
    private static readonly System.Text.RegularExpressions.Regex TargetPattern =
        new("^(ps|vs|gs|hs|ds|cs|lib|ms|as)_[0-9]+_[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 指定したフォルダーから上へ辿って設定ファイルを探す。
    /// </summary>
    /// <param name="startDirectory">探索を開始するフォルダー。</param>
    /// <returns>見つかった設定ファイルのパス。見つからない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// リポジトリのルートに置く運用を想定している。
    /// 解析対象のパスからルートまでを辿ることで、
    /// どのフォルダーから実行しても同じ設定が使われるようにする。
    /// </remarks>
    public static string? FindConfigurationFile(string startDirectory)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);

        try
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startDirectory));

            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, ShaderlynConfiguration.FileName);

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// 設定ファイルを読み込む。
    /// </summary>
    /// <param name="filePath">設定ファイルのパス。</param>
    /// <returns>読み込んだ設定。</returns>
    /// <remarks>
    /// ファイルが読めない場合も例外にはせず、問題として報告した空の設定を返す。
    /// </remarks>
    public static ShaderlynConfiguration Load(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShaderlynConfiguration
            {
                FilePath = filePath,
                Problems = [new ConfigurationProblem(0, $"設定ファイルを読み込めません: {ex.Message}")],
            };
        }

        return Parse(content, filePath);
    }

    /// <summary>
    /// 設定の内容を読み取る。
    /// </summary>
    /// <param name="content">設定ファイルの内容。</param>
    /// <param name="filePath">
    /// 設定ファイルのパス。相対パスの解決と診断の位置に使う。
    /// </param>
    /// <returns>読み込んだ設定。</returns>
    public static ShaderlynConfiguration Parse(string content, string filePath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(filePath);

        ImmutableArray<ConfigurationProblem>.Builder problems =
            ImmutableArray.CreateBuilder<ConfigurationProblem>();

        YamlNode? root = MinimalYamlParser.Parse(content, out ImmutableArray<YamlError> yamlErrors);

        foreach (YamlError error in yamlErrors)
        {
            problems.Add(new ConfigurationProblem(error.Line, error.Message));
        }

        if (root is null)
        {
            return new ShaderlynConfiguration { FilePath = filePath, Problems = problems.ToImmutable() };
        }

        if (root is not YamlMapping mapping)
        {
            problems.Add(new ConfigurationProblem(root.Line, "設定ファイルの最上位はキーと値の対でなければなりません。"));
            return new ShaderlynConfiguration { FilePath = filePath, Problems = problems.ToImmutable() };
        }

        Reader reader = new(mapping, filePath, problems);
        return reader.Read();
    }

    /// <summary>設定ファイル 1 つ分の読み取り。</summary>
    /// <param name="root">最上位の写像。</param>
    /// <param name="filePath">設定ファイルのパス。</param>
    /// <param name="problems">問題の報告先。</param>
    private sealed class Reader(
        YamlMapping root,
        string filePath,
        ImmutableArray<ConfigurationProblem>.Builder problems)
    {
        public ShaderlynConfiguration Read()
        {
            ReportUnknownKeys(root, KnownRootKeys, "設定");
            CheckVersion();

            YamlMapping? unity = ReadSection("unity");

            if (unity is not null)
            {
                ReportUnknownKeys(unity, KnownUnityKeys, "unity:");
            }

            YamlMapping? unreal = ReadSection("unreal");

            if (unreal is not null)
            {
                ReportUnknownKeys(unreal, KnownUnrealKeys, "unreal:");
            }

            YamlMapping? hlsl = ReadSection("hlsl");

            if (hlsl is not null)
            {
                ReportUnknownKeys(hlsl, KnownHlslKeys, "hlsl:");
            }

            (ImmutableArray<ConfiguredSymbol> symbols, ImmutableArray<ConfiguredSymbolGroup> groups) = ReadSymbols();

            return new ShaderlynConfiguration
            {
                FilePath = filePath,
                Environment = ReadOptionalScalar(root, "environment"),
                EnvironmentLine = root.TryGetScalar("environment", out YamlScalar? environment) ? environment.Line : 0,
                Profile = ReadProfile(unity),
                UnityProjectPath = ResolvePath(ReadOptionalScalar(unity, "project")),
                UnityEditorPath = ResolvePath(ReadOptionalScalar(unity, "editor")),
                UnrealEngineDirectory = ResolvePath(ReadOptionalScalar(unreal, "engine")),
                UnrealProjectDirectory = ResolvePath(ReadOptionalScalar(unreal, "project")),
                UnrealVirtualPaths = ReadVirtualPaths(unreal),
                HlslVersion = ReadHlslVersion(hlsl),
                Symbols = symbols,
                SymbolGroups = groups,
                Units = ReadUnits(symbols, groups),
                IncludePaths = ReadIncludePaths(),
                Defines = ReadStringSequence("defines"),
                RuleSeverities = ReadRuleSeverities(out ImmutableDictionary<string, ImmutableDictionary<string, string>> options),
                RuleOptions = options,
                Problems = problems.ToImmutable(),
            };
        }

        /// <summary>
        /// 設定ファイルのバージョンを確かめる。
        /// </summary>
        /// <remarks>
        /// バージョンが無いことは誤りにしない。書き始めの障壁を上げないためである。
        /// 知らないバージョンは報告する。読み方が変わっている可能性があり、
        /// 何も伝えずに古い解釈を当てはめると意図と違う設定になる。
        /// </remarks>
        private void CheckVersion()
        {
            if (!root.TryGetScalar("version", out YamlScalar? version))
            {
                return;
            }

            if (!version.TryGetInt32(out int value))
            {
                problems.Add(new ConfigurationProblem(version.Line, $"version には整数を指定してください ('{version.Value}')。"));
                return;
            }

            if (value > ShaderlynConfiguration.SupportedVersion)
            {
                problems.Add(new ConfigurationProblem(
                    version.Line,
                    $"設定ファイルのバージョン {value} はこのツールより新しいものです "
                    + $"(対応しているバージョン: {ShaderlynConfiguration.SupportedVersion})。"
                    + "解釈できない設定は無視されます。"));
            }
        }

        /// <summary>写像からスカラーの値を読む。</summary>
        /// <param name="mapping">読む写像。無ければ値も無い。</param>
        /// <param name="key">キー。</param>
        /// <returns>値。無いか空なら <see langword="null"/>。</returns>
        private static string? ReadOptionalScalar(YamlMapping? mapping, string key)
            => mapping is not null && mapping.TryGetScalar(key, out YamlScalar? scalar) && scalar.Value.Length > 0
                ? scalar.Value
                : null;

        /// <summary>最上位の節 (キーと値の対) を読む。</summary>
        /// <param name="key">節の名前。</param>
        /// <returns>節。無いか空なら <see langword="null"/>。</returns>
        private YamlMapping? ReadSection(string key)
        {
            if (!root.TryGet(key, out YamlNode? node) || node is YamlScalar { Value.Length: 0 })
            {
                return null;
            }

            if (node is YamlMapping mapping)
            {
                return mapping;
            }

            problems.Add(new ConfigurationProblem(node.Line, $"{key}: にはキーと値の対を指定してください。"));
            return null;
        }

        /// <summary>
        /// レンダーパイプラインのプロファイルを読む。
        /// </summary>
        /// <param name="unity"><c>unity:</c> の節。無ければ <see langword="null"/>。</param>
        /// <returns>プロファイルの名前。指定が無ければ <see langword="null"/>。</returns>
        /// <remarks>
        /// <para>
        /// <b>節の外の <c>profile</c> も読む。</b> v0.1 の設定ファイルをそのまま使えるようにするためである。
        /// ただし、Unity の環境だけの設定であることが分かるよう、<c>unity:</c> の下へ移すよう伝える。
        /// </para>
        /// <para>
        /// 両方あれば <c>unity:</c> の節が勝つ。食い違っていれば、どちらを使ったかを伝える。
        /// </para>
        /// </remarks>
        private string? ReadProfile(YamlMapping? unity)
        {
            string? inSection = ReadOptionalScalar(unity, "profile");

            if (!root.TryGetScalar("profile", out YamlScalar? legacy) || legacy.Value.Length == 0)
            {
                return inSection;
            }

            if (inSection is null)
            {
                problems.Add(new ConfigurationProblem(
                    legacy.Line,
                    "profile は unity: の節の下に書いてください。Unity の環境だけの設定です。"
                    + "節の外の profile も読みます。"));
                return legacy.Value;
            }

            problems.Add(new ConfigurationProblem(
                legacy.Line,
                $"profile が節の外と unity: の節の両方にあります。unity: の節の '{inSection}' を使います。"
                + "節の外の profile は消してください。"));
            return inSection;
        }

        /// <summary>
        /// Unreal の仮想パスの割り当て (<c>unreal: virtual-paths</c>) を読む。
        /// </summary>
        /// <param name="unreal"><c>unreal:</c> の節。無ければ <see langword="null"/>。</param>
        /// <returns>仮想パスから、設定ファイルからの相対で解決したフォルダーへの対応。</returns>
        /// <remarks>
        /// <code>
        /// unreal:
        ///   virtual-paths:
        ///     /Plugin/MyPlugin: Plugins/MyPlugin/Shaders
        /// </code>
        /// </remarks>
        private ImmutableDictionary<string, string> ReadVirtualPaths(YamlMapping? unreal)
        {
            ImmutableDictionary<string, string>.Builder paths = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

            if (unreal is null || !unreal.TryGet("virtual-paths", out YamlNode? node) || node is YamlScalar { Value.Length: 0 })
            {
                return paths.ToImmutable();
            }

            if (node is not YamlMapping mapping)
            {
                problems.Add(new ConfigurationProblem(node.Line, "unreal: の virtual-paths には、仮想パスとフォルダーの対を書いてください。"));
                return paths.ToImmutable();
            }

            foreach (string key in mapping.Keys)
            {
                if (!key.StartsWith('/'))
                {
                    problems.Add(new ConfigurationProblem(
                        mapping.Entries[key].Line, $"仮想パス '{key}' は / で始めてください (/Plugin/MyPlugin のように)。"));
                }
                else if (mapping.Entries[key] is YamlScalar { Value.Length: > 0 } directory)
                {
                    paths[key] = ResolvePath(directory.Value)!;
                }
                else
                {
                    problems.Add(new ConfigurationProblem(mapping.Entries[key].Line, $"仮想パス '{key}' にはフォルダーを書いてください。"));
                }
            }

            return paths.ToImmutable();
        }

        /// <summary>HLSL の言語のバージョン (<c>hlsl: version</c>) を読む。</summary>
        /// <param name="hlsl"><c>hlsl:</c> の節。無ければ <see langword="null"/>。</param>
        /// <returns>2018 か 2021。指定が無いか読めなければ <see langword="null"/>。</returns>
        private int? ReadHlslVersion(YamlMapping? hlsl)
        {
            if (hlsl is null || !hlsl.TryGetScalar("version", out YamlScalar? version) || version.Value.Length == 0)
            {
                return null;
            }

            if (version.TryGetInt32(out int value) && value is 2018 or 2021)
            {
                return value;
            }

            problems.Add(new ConfigurationProblem(
                version.Line, $"hlsl: の version には 2018 か 2021 を指定してください ('{version.Value}')。"));
            return null;
        }

        /// <summary>
        /// 構成の軸になるシンボル (<c>symbols:</c>) を読む。
        /// </summary>
        /// <returns>シンボルと、同時には有効にならない組。</returns>
        /// <remarks>
        /// <code>
        /// symbols:
        ///   - name: QUALITY
        ///     values: [0, 1, 2]          # 省略すると「定義されているか・いないか」
        ///   - name: USE_FOG
        ///   - one-of: [MODE_A, MODE_B]   # 同時には有効にならず、どれか 1 つは有効
        ///   - at-most-one: [FX_A, FX_B]  # 同時には有効にならない
        /// </code>
        /// 組に書いた名前は、<c>name</c> で宣言していなくても構成の軸になる。
        /// </remarks>
        private (ImmutableArray<ConfiguredSymbol> Symbols, ImmutableArray<ConfiguredSymbolGroup> Groups) ReadSymbols()
        {
            ImmutableArray<ConfiguredSymbol>.Builder symbols = ImmutableArray.CreateBuilder<ConfiguredSymbol>();
            ImmutableArray<ConfiguredSymbolGroup>.Builder groups = ImmutableArray.CreateBuilder<ConfiguredSymbolGroup>();

            foreach (YamlMapping item in ReadMappingSequence("symbols"))
            {
                ReportUnknownKeys(item, KnownSymbolKeys, "symbols の要素");

                string[] kinds = [.. new[] { "name", "one-of", "at-most-one" }.Where(item.Entries.ContainsKey)];

                if (kinds.Length != 1)
                {
                    problems.Add(new ConfigurationProblem(
                        item.Line, "symbols の要素には name / one-of / at-most-one のどれか 1 つを書いてください。"));
                    continue;
                }

                if (kinds[0] == "name")
                {
                    if (ReadSymbol(item) is { } symbol)
                    {
                        if (symbols.Any(s => s.Name == symbol.Name))
                        {
                            problems.Add(new ConfigurationProblem(symbol.Line, $"シンボル '{symbol.Name}' が 2 回宣言されています。"));
                            continue;
                        }

                        symbols.Add(symbol);
                    }

                    continue;
                }

                if (item.Entries[kinds[0]] is not YamlSequence members
                    || members.Items.Length < 2
                    || members.Items.Any(m => m is not YamlScalar { Value.Length: > 0 }))
                {
                    problems.Add(new ConfigurationProblem(item.Line, $"{kinds[0]} にはシンボルの名前を 2 つ以上並べてください。"));
                    continue;
                }

                groups.Add(new ConfiguredSymbolGroup(
                    [.. members.Items.Cast<YamlScalar>().Select(m => m.Value)],
                    RequiresOne: kinds[0] == "one-of",
                    item.Line));
            }

            foreach (ConfiguredSymbolGroup group in groups)
            {
                foreach (string name in group.Symbols.Where(n => symbols.FirstOrDefault(s => s.Name == n) is { Values.IsEmpty: false }))
                {
                    problems.Add(new ConfigurationProblem(
                        group.Line, $"値を持つシンボル '{name}' は組に入れられません。値ごとの構成はもともと同時には有効になりません。"));
                }
            }

            return (symbols.ToImmutable(), groups.ToImmutable());
        }

        /// <summary><c>symbols:</c> の <c>name</c> の要素を読む。</summary>
        /// <param name="item">要素。</param>
        /// <returns>シンボル。読めなければ <see langword="null"/>。</returns>
        private ConfiguredSymbol? ReadSymbol(YamlMapping item)
        {
            if (!item.TryGetScalar("name", out YamlScalar? name) || name.Value.Length == 0)
            {
                problems.Add(new ConfigurationProblem(item.Line, "symbols の name にはシンボルの名前を書いてください。"));
                return null;
            }

            if (!item.TryGet("values", out YamlNode? valuesNode))
            {
                return new ConfiguredSymbol(name.Value, [], name.Line);
            }

            List<int> values = [];

            if (valuesNode is YamlSequence sequence)
            {
                foreach (YamlNode value in sequence.Items)
                {
                    if (value is YamlScalar scalar && scalar.TryGetInt32(out int number))
                    {
                        if (!values.Contains(number))
                        {
                            values.Add(number);
                        }
                    }
                    else
                    {
                        problems.Add(new ConfigurationProblem(value.Line, $"シンボル '{name.Value}' の values には整数を並べてください。"));
                        return null;
                    }
                }
            }

            if (values.Count < 2)
            {
                problems.Add(new ConfigurationProblem(
                    valuesNode.Line, $"シンボル '{name.Value}' の values には、とりうる値を 2 つ以上並べてください。"));
                return null;
            }

            return new ConfiguredSymbol(name.Value, [.. values], name.Line);
        }

        /// <summary>
        /// コンパイル単位 (<c>units:</c>) を読む。
        /// </summary>
        /// <param name="symbols">宣言したシンボル。単位が使うシンボルの名前を確かめる。</param>
        /// <param name="groups">宣言した組。</param>
        /// <returns>単位。</returns>
        /// <remarks>
        /// <code>
        /// units:
        ///   - files: Shaders/**/*.hlsl
        ///     entry: MainPS
        ///     target: ps_6_6
        ///     defines: [USE_FOO=1]
        ///     symbols: [QUALITY]
        /// </code>
        /// </remarks>
        private ImmutableArray<ConfiguredUnit> ReadUnits(
            ImmutableArray<ConfiguredSymbol> symbols,
            ImmutableArray<ConfiguredSymbolGroup> groups)
        {
            ImmutableArray<ConfiguredUnit>.Builder units = ImmutableArray.CreateBuilder<ConfiguredUnit>();
            string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;

            HashSet<string> declared = [.. symbols.Select(s => s.Name), .. groups.SelectMany(g => g.Symbols)];

            foreach (YamlMapping item in ReadMappingSequence("units"))
            {
                ReportUnknownKeys(item, KnownUnitKeys, "units の要素");

                if (ReadOptionalScalar(item, "files") is not { } files)
                {
                    problems.Add(new ConfigurationProblem(item.Line, "units の要素には files (対象のファイルの glob) を書いてください。"));
                    continue;
                }

                string? target = ReadOptionalScalar(item, "target");

                if (target is not null && !TargetPattern.IsMatch(target))
                {
                    problems.Add(new ConfigurationProblem(
                        item.Entries["target"].Line,
                        $"target '{target}' を解釈できません。ps_6_6 のように、ステージ (ps / vs / gs / hs / ds / cs / lib / ms / as) と"
                        + "シェーダーモデルを書いてください。"));
                    target = null;
                }

                ImmutableArray<string>? unitSymbols = null;

                if (item.TryGet("symbols", out YamlNode? _))
                {
                    unitSymbols = ReadStringSequence(item, "symbols");

                    foreach (string name in unitSymbols.Value.Where(n => !declared.Contains(n)))
                    {
                        problems.Add(new ConfigurationProblem(
                            item.Entries["symbols"].Line, $"シンボル '{name}' は symbols: で宣言されていません。"));
                    }
                }

                units.Add(new ConfiguredUnit(files, baseDirectory, item.Line)
                {
                    Entry = ReadOptionalScalar(item, "entry"),
                    Target = target,
                    Defines = ReadStringSequence(item, "defines"),
                    Symbols = unitSymbols,
                });
            }

            return units.ToImmutable();
        }

        /// <summary>最上位の、写像の並びを読む。</summary>
        /// <param name="key">キー。</param>
        /// <returns>写像の要素。写像でない要素は報告して飛ばす。</returns>
        private IEnumerable<YamlMapping> ReadMappingSequence(string key)
        {
            if (!root.TryGet(key, out YamlNode? node) || node is YamlScalar { Value.Length: 0 })
            {
                yield break;
            }

            if (node is not YamlSequence sequence)
            {
                problems.Add(new ConfigurationProblem(node.Line, $"{key} には要素の並びを指定してください。"));
                yield break;
            }

            foreach (YamlNode item in sequence.Items)
            {
                if (item is YamlMapping mapping)
                {
                    yield return mapping;
                }
                else
                {
                    problems.Add(new ConfigurationProblem(item.Line, $"{key} の要素にはキーと値の対を書いてください。"));
                }
            }
        }

        /// <summary>設定ファイルからの相対パスを解決する。</summary>
        /// <param name="path">設定に書かれたパス。</param>
        /// <returns>絶対パス。指定が無ければ <see langword="null"/>。</returns>
        private string? ResolvePath(string? path)
        {
            if (path is null)
            {
                return null;
            }

            string? baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));

            return string.IsNullOrEmpty(baseDirectory)
                ? path
                : Path.GetFullPath(Path.Combine(baseDirectory, path));
        }

        /// <summary>
        /// <c>include-paths</c> を読み、設定ファイルからの相対で解決する。
        /// </summary>
        /// <returns>絶対パスに直した探索パス。</returns>
        /// <remarks>
        /// <b>相対の基点は設定ファイルの場所である。</b>
        /// 実行時の作業フォルダーを基点にすると、
        /// どこから実行したかによって解決結果が変わり、
        /// CI とローカルで挙動が食い違う。
        /// </remarks>
        private ImmutableArray<string> ReadIncludePaths()
        {
            string? baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));

            return
            [
                .. ReadStringSequence("include-paths")
                    .Select(path => string.IsNullOrEmpty(baseDirectory)
                        ? path
                        : Path.GetFullPath(Path.Combine(baseDirectory, path)))
            ];
        }

        private ImmutableArray<string> ReadStringSequence(string key) => ReadStringSequence(root, key);

        private ImmutableArray<string> ReadStringSequence(YamlMapping mapping, string key)
        {
            if (!mapping.TryGet(key, out YamlNode? node))
            {
                return [];
            }

            if (node is YamlScalar { Value.Length: 0 })
            {
                return [];
            }

            if (node is not YamlSequence sequence)
            {
                problems.Add(new ConfigurationProblem(node.Line, $"{key} には値の並びを指定してください。"));
                return [];
            }

            ImmutableArray<string>.Builder values = ImmutableArray.CreateBuilder<string>();

            foreach (YamlNode item in sequence.Items)
            {
                if (item is YamlScalar scalar)
                {
                    values.Add(scalar.Value);
                }
                else
                {
                    problems.Add(new ConfigurationProblem(item.Line, $"{key} の要素は文字列でなければなりません。"));
                }
            }

            return values.ToImmutable();
        }

        /// <summary>
        /// <c>rules</c> の内容を読む。
        /// </summary>
        /// <param name="options">読み取ったルール固有のオプション。</param>
        /// <returns>ルール ID から重要度への対応。</returns>
        /// <remarks>
        /// 2 通りの書き方を受け付ける。
        /// <code>
        /// rules:
        ///   SL1003: error
        ///   HL0351:
        ///     severity: warning
        ///     options: { maxSamplers: 8 }
        /// </code>
        /// </remarks>
        private ImmutableDictionary<string, DiagnosticSeverity> ReadRuleSeverities(
            out ImmutableDictionary<string, ImmutableDictionary<string, string>> options)
        {
            ImmutableDictionary<string, DiagnosticSeverity>.Builder severities =
                ImmutableDictionary.CreateBuilder<string, DiagnosticSeverity>(StringComparer.OrdinalIgnoreCase);
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Builder optionBuilder =
                ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(
                    StringComparer.OrdinalIgnoreCase);

            if (root.TryGet("rules", out YamlNode? node) && node is YamlMapping rules)
            {
                foreach (string ruleId in rules.Keys)
                {
                    YamlNode value = rules.Entries[ruleId];

                    switch (value)
                    {
                        case YamlScalar scalar:
                            AddSeverity(severities, ruleId, scalar);
                            break;

                        case YamlMapping detail:
                            ReadRuleDetail(ruleId, detail, severities, optionBuilder);
                            break;

                        default:
                            problems.Add(new ConfigurationProblem(
                                value.Line, $"ルール {ruleId} の設定を解釈できません。"));
                            break;
                    }
                }
            }
            else if (node is not null and not YamlScalar { Value.Length: 0 })
            {
                problems.Add(new ConfigurationProblem(node.Line, "rules にはルール ID と設定の対を指定してください。"));
            }

            options = optionBuilder.ToImmutable();
            return severities.ToImmutable();
        }

        private void ReadRuleDetail(
            string ruleId,
            YamlMapping detail,
            ImmutableDictionary<string, DiagnosticSeverity>.Builder severities,
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Builder optionBuilder)
        {
            ReportUnknownKeys(detail, ["severity", "options"], $"ルール {ruleId}");

            if (detail.TryGetScalar("severity", out YamlScalar? severity))
            {
                AddSeverity(severities, ruleId, severity);
            }

            if (!detail.TryGet("options", out YamlNode? optionsNode))
            {
                return;
            }

            if (optionsNode is not YamlMapping optionsMapping)
            {
                problems.Add(new ConfigurationProblem(
                    optionsNode.Line, $"ルール {ruleId} の options にはキーと値の対を指定してください。"));
                return;
            }

            ImmutableDictionary<string, string>.Builder values =
                ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string optionName in optionsMapping.Keys)
            {
                if (optionsMapping.Entries[optionName] is YamlScalar optionValue)
                {
                    values[optionName] = optionValue.Value;
                }
                else
                {
                    problems.Add(new ConfigurationProblem(
                        optionsMapping.Entries[optionName].Line,
                        $"ルール {ruleId} の options の値は文字列でなければなりません。"));
                }
            }

            optionBuilder[ruleId] = values.ToImmutable();
        }

        private void AddSeverity(
            ImmutableDictionary<string, DiagnosticSeverity>.Builder severities,
            string ruleId,
            YamlScalar scalar)
        {
            if (TryParseSeverity(scalar.Value, out DiagnosticSeverity severity))
            {
                severities[ruleId] = severity;
                return;
            }

            problems.Add(new ConfigurationProblem(
                scalar.Line,
                $"ルール {ruleId} の重要度 '{scalar.Value}' を解釈できません。"
                + "none, info, warning, error のいずれかを指定してください。"));
        }

        /// <summary>
        /// 認識できないキーを報告する。
        /// </summary>
        /// <param name="mapping">検査する写像。</param>
        /// <param name="knownKeys">認識するキー。</param>
        /// <param name="context">メッセージに含める文脈。</param>
        /// <remarks>
        /// <b>綴りを間違えたキーを、何も伝えずに無視してはならない。</b>
        /// 「設定したつもりで効いていない」ことに気づけないまま運用されるのは、
        /// 設定を書けないことよりも悪い。
        /// </remarks>
        private void ReportUnknownKeys(YamlMapping mapping, string[] knownKeys, string context)
        {
            foreach (string key in mapping.Keys)
            {
                if (!knownKeys.Contains(key, StringComparer.Ordinal))
                {
                    problems.Add(new ConfigurationProblem(
                        mapping.Entries[key].Line,
                        $"{context}に指定できないキー '{key}' があります。"
                        + $"指定できるキー: {string.Join(", ", knownKeys)}"));
                }
            }
        }
    }

    /// <summary>重要度の綴りを解釈する。</summary>
    /// <param name="text">設定に書かれた文字列。</param>
    /// <param name="severity">解釈できた重要度。</param>
    /// <returns>解釈できた場合は <see langword="true"/>。</returns>
    private static bool TryParseSeverity(string text, out DiagnosticSeverity severity)
        => Enum.TryParse(text, ignoreCase: true, out severity);
}
