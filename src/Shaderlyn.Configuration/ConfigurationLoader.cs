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
    private static readonly string[] KnownRootKeys =
    [
        "version", "profile", "include-paths", "defines", "rules",
    ];

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

            return new ShaderlynConfiguration
            {
                FilePath = filePath,
                Profile = ReadOptionalScalar("profile"),
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

        private string? ReadOptionalScalar(string key)
            => root.TryGetScalar(key, out YamlScalar? scalar) && scalar.Value.Length > 0 ? scalar.Value : null;

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

        private ImmutableArray<string> ReadStringSequence(string key)
        {
            if (!root.TryGet(key, out YamlNode? node))
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
