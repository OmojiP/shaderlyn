using System.Collections.Immutable;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Configuration.Yaml;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Tests;

/// <summary>
/// 設定ファイル用の YAML パーサーの検証。
/// </summary>
/// <remarks>
/// <b>読み取りの誤りは、壊れたことが表に出ない種類の不具合である。</b>
/// 入れ子の深さを 1 つ取り違えただけで設定が別の意味になり、
/// それでもツールは正常に動いているように見える。
/// 書式ごとに 1 つずつ検証する。
/// </remarks>
public sealed class MinimalYamlParserTests
{
    private static YamlNode ParseWithoutErrors(string text)
    {
        YamlNode? node = MinimalYamlParser.Parse(text, out ImmutableArray<YamlError> errors);

        Assert.True(
            errors.IsEmpty,
            $"予期しない読み取りエラー:\n{string.Join("\n", errors.Select(e => $"  {e.Line + 1}: {e.Message}"))}");
        Assert.NotNull(node);
        return node;
    }

    private static string ScalarAt(YamlNode node, params string[] path)
    {
        YamlNode current = node;

        foreach (string key in path)
        {
            YamlMapping mapping = Assert.IsType<YamlMapping>(current);
            Assert.True(mapping.TryGet(key, out YamlNode? next), $"キー '{key}' がありません。");
            current = next;
        }

        return Assert.IsType<YamlScalar>(current).Value;
    }

    [Fact]
    public void 単純なキーと値を読める()
    {
        YamlNode root = ParseWithoutErrors("profile: urp");
        Assert.Equal("urp", ScalarAt(root, "profile"));
    }

    [Fact]
    public void 入れ子の写像を読める()
    {
        YamlNode root = ParseWithoutErrors("""
            rules:
              SL1003: error
              SL1004: none
            """);

        Assert.Equal("error", ScalarAt(root, "rules", "SL1003"));
        Assert.Equal("none", ScalarAt(root, "rules", "SL1004"));
    }

    [Fact]
    public void 三段の入れ子を読める()
    {
        YamlNode root = ParseWithoutErrors("""
            rules:
              CF0411:
                severity: warning
                options:
                  maxSamplers: 8
            """);

        Assert.Equal("warning", ScalarAt(root, "rules", "CF0411", "severity"));
        Assert.Equal("8", ScalarAt(root, "rules", "CF0411", "options", "maxSamplers"));
    }

    [Fact]
    public void 流れ形式の並びを読める()
    {
        YamlNode root = ParseWithoutErrors("""include-paths: [ "Assets/A", Assets/B ]""");

        YamlMapping mapping = Assert.IsType<YamlMapping>(root);
        YamlSequence sequence = Assert.IsType<YamlSequence>(mapping.Entries["include-paths"]);

        Assert.Equal(["Assets/A", "Assets/B"], sequence.Items.Select(i => ((YamlScalar)i).Value));
    }

    [Fact]
    public void 流れ形式の写像を読める()
    {
        YamlNode root = ParseWithoutErrors("options: { maxSamplers: 8, name: foo }");

        Assert.Equal("8", ScalarAt(root, "options", "maxSamplers"));
        Assert.Equal("foo", ScalarAt(root, "options", "name"));
    }

    [Fact]
    public void ブロック形式の並びを読める()
    {
        YamlNode root = ParseWithoutErrors("""
            defines:
              - _NORMALMAP
              - _ALPHATEST_ON
            """);

        YamlMapping mapping = Assert.IsType<YamlMapping>(root);
        YamlSequence sequence = Assert.IsType<YamlSequence>(mapping.Entries["defines"]);

        Assert.Equal(["_NORMALMAP", "_ALPHATEST_ON"], sequence.Items.Select(i => ((YamlScalar)i).Value));
    }

    [Fact]
    public void 並びの要素が写像である形を読める()
    {
        // 「- key: value」に続けて同じ桁へキーを並べる形。
        // 設定ファイルで最もよく使う書き方であり、
        // 深さの計算を間違えると要素が丸ごと落ちる。
        YamlNode root = ParseWithoutErrors("""
            banned-symbols:
              - symbol: tex2D
                message: SAMPLE_TEXTURE2D を使ってください
              - symbol: UnityObjectToClipPos
                replacement: TransformObjectToHClip
            """);

        YamlMapping mapping = Assert.IsType<YamlMapping>(root);
        YamlSequence sequence = Assert.IsType<YamlSequence>(mapping.Entries["banned-symbols"]);

        Assert.Equal(2, sequence.Items.Length);
        Assert.Equal("tex2D", ScalarAt(sequence.Items[0], "symbol"));
        Assert.Equal("SAMPLE_TEXTURE2D を使ってください", ScalarAt(sequence.Items[0], "message"));
        Assert.Equal("UnityObjectToClipPos", ScalarAt(sequence.Items[1], "symbol"));
        Assert.Equal("TransformObjectToHClip", ScalarAt(sequence.Items[1], "replacement"));
    }

    [Fact]
    public void 引用符の中のコロンとシャープを値として扱う()
    {
        YamlNode root = ParseWithoutErrors("""
            message: "URP: SAMPLE_TEXTURE2D #1 を使ってください"
            """);

        Assert.Equal("URP: SAMPLE_TEXTURE2D #1 を使ってください", ScalarAt(root, "message"));
    }

    [Fact]
    public void 行末のコメントを取り除く()
    {
        YamlNode root = ParseWithoutErrors("""
            # 先頭のコメント
            profile: urp   # 行末のコメント
            """);

        Assert.Equal("urp", ScalarAt(root, "profile"));
    }

    [Fact]
    public void シャープが値の一部である場合はコメントにしない()
    {
        // 空白の直後にある # だけをコメントの開始とみなす。
        Assert.Equal("a#b", ScalarAt(ParseWithoutErrors("key: a#b"), "key"));
    }

    [Fact]
    public void 単引用符のエスケープを扱える()
        => Assert.Equal("it's", ScalarAt(ParseWithoutErrors("key: 'it''s'"), "key"));

    [Fact]
    public void 字下げにタブを使うと誤りとして報告する()
    {
        // タブを何も伝えずに読み替えると入れ子の深さが意図とずれ、
        // 「書いたのに効かない設定」になる。
        MinimalYamlParser.Parse("rules:\n\tSL1003: error", out ImmutableArray<YamlError> errors);

        Assert.Contains(errors, e => e.Message.Contains("タブ", StringComparison.Ordinal));
    }

    [Fact]
    public void キーの重複を報告する()
    {
        // どちらが効くかが実装依存になるので、何も伝えずに一方を採用することはしない。
        MinimalYamlParser.Parse("profile: urp\nprofile: brp", out ImmutableArray<YamlError> errors);

        Assert.Contains(errors, e => e.Message.Contains("重複", StringComparison.Ordinal));
    }

    [Fact]
    public void 閉じられていない括弧を報告する()
    {
        MinimalYamlParser.Parse("include-paths: [ a, b", out ImmutableArray<YamlError> errors);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void 空の入力を読んでも例外にならない()
    {
        Assert.Null(MinimalYamlParser.Parse(string.Empty, out ImmutableArray<YamlError> errors));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(":")]
    [InlineData("- - -")]
    [InlineData("a: [ { b: [ c")]
    [InlineData("   \n  \n")]
    [InlineData("key:\n  - \n  - \n")]
    public void 壊れた入力でも例外を投げない(string text)
    {
        // 設定ファイルの誤記でツールが落ちてはならない。
        MinimalYamlParser.Parse(text, out _);
    }
}

/// <summary>
/// 設定ファイルの読み込みの検証。
/// </summary>
public sealed class ConfigurationLoaderTests
{
    private static ShaderlynConfiguration Parse(string content)
        => ConfigurationLoader.Parse(content, Path.Combine("C:", "project", ".shaderlyn.yaml"));

    private static ShaderlynConfiguration ParseWithoutProblems(string content)
    {
        ShaderlynConfiguration configuration = Parse(content);

        Assert.True(
            configuration.Problems.IsEmpty,
            "予期しない設定の問題:\n"
            + string.Join("\n", configuration.Problems.Select(p => $"  {p.Line + 1}: {p.Message}")));

        return configuration;
    }

    [Fact]
    public void 計画に書かれた設定例をそのまま読める()
    {
        // 仕様として提示した書き方が実際に読めることを確かめる。
        ShaderlynConfiguration configuration = ParseWithoutProblems("""
            version: 1
            unity:
              profile: urp
            include-paths: [ "Assets/Shaders/Include" ]
            defines: [ "_NORMALMAP" ]

            rules:
              SL1003: error
              SL1004: none
              CF0411:
                severity: warning
                options: { maxSamplers: 8 }
            """);

        Assert.Equal("urp", configuration.Profile);
        Assert.Equal(["_NORMALMAP"], configuration.Defines.ToArray());
        Assert.Single(configuration.IncludePaths);

        Assert.Equal(DiagnosticSeverity.Error, configuration.RuleSeverities["SL1003"]);
        Assert.Equal(DiagnosticSeverity.None, configuration.RuleSeverities["SL1004"]);
        Assert.Equal(DiagnosticSeverity.Warning, configuration.RuleSeverities["CF0411"]);
        Assert.Equal("8", configuration.RuleOptions["CF0411"]["maxSamplers"]);

    }

    [Fact]
    public void 環境とunityの節を読む()
    {
        ShaderlynConfiguration configuration = ParseWithoutProblems("""
            environment: unity
            unity:
              profile: hdrp
              project: ../MyGame
              editor: C:/Unity/Data
            """);

        Assert.Equal("unity", configuration.Environment);
        Assert.Equal("hdrp", configuration.Profile);

        // パスは設定ファイルからの相対で解決する。
        Assert.True(Path.IsPathRooted(configuration.UnityProjectPath));
        Assert.EndsWith("MyGame", configuration.UnityProjectPath, StringComparison.Ordinal);
        Assert.NotNull(configuration.UnityEditorPath);
    }

    /// <summary>
    /// v0.1 の書き方 (節の外の profile) も読み、unity: の下へ移すよう伝えることを検証する。
    /// </summary>
    [Fact]
    public void 節の外のprofileも警告して読む()
    {
        ShaderlynConfiguration configuration = Parse("profile: brp");

        Assert.Equal("brp", configuration.Profile);
        ConfigurationProblem problem = Assert.Single(configuration.Problems);
        Assert.Contains("unity:", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void profileが両方にあればunityの節を使う()
    {
        ShaderlynConfiguration configuration = Parse("""
            profile: brp
            unity:
              profile: urp
            """);

        Assert.Equal("urp", configuration.Profile);
        Assert.Contains(configuration.Problems, p => p.Message.Contains("両方", StringComparison.Ordinal));
    }

    [Fact]
    public void unityのコンパイラの設定を読む()
    {
        ShaderlynConfiguration configuration = ParseWithoutProblems("""
            unity:
              version: 6.3
              graphics-apis: [dx12, D3D11, vulkan]
              shader-compiler-backend: DXC
            """);

        Assert.Equal("6.3", configuration.UnityVersion);
        Assert.Equal(["dx12", "dx11", "vulkan"], configuration.UnityGraphicsApis.ToArray());
        Assert.Equal("dxc", configuration.UnityShaderCompilerBackend);
    }

    [Theory]
    [InlineData("version: six", "version")]
    [InlineData("graphics-apis: [dx13]", "dx13")]
    [InlineData("shader-compiler-backend: spirv", "shader-compiler-backend")]
    public void unityのコンパイラの設定の誤りを報告する(string line, string expected)
    {
        ShaderlynConfiguration configuration = Parse("unity:\n  " + line + "\n");

        ConfigurationProblem problem = Assert.Single(configuration.Problems);
        Assert.Contains(expected, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void unityの節の知らないキーを報告する()
    {
        ShaderlynConfiguration configuration = Parse("""
            unity:
              profle: urp
            """);

        Assert.Contains(configuration.Problems, p => p.Message.Contains("profle", StringComparison.Ordinal));
    }

    [Fact]
    public void 探索パスは設定ファイルからの相対で解決する()
    {
        // 実行時の作業フォルダーを基点にすると、
        // どこから実行したかで解決結果が変わり CI とローカルで食い違う。
        ShaderlynConfiguration configuration = ParseWithoutProblems("""include-paths: [ "Shaders/Include" ]""");

        Assert.True(Path.IsPathRooted(configuration.IncludePaths[0]));
        Assert.EndsWith(
            Path.Combine("Shaders", "Include"), configuration.IncludePaths[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 知らないキーを報告する()
    {
        // 綴りを間違えたキーを、何も伝えずに無視すると、
        // 設定したつもりのルールが効かないまま運用される。
        ShaderlynConfiguration configuration = Parse("include-path:\n  - Assets");

        Assert.Contains(configuration.Problems, p => p.Message.Contains("include-path", StringComparison.Ordinal));
    }

    [Fact]
    public void ルール設定の中の知らないキーも報告する()
    {
        ShaderlynConfiguration configuration = Parse("""
            rules:
              SL1003:
                severty: error
            """);

        Assert.Contains(configuration.Problems, p => p.Message.Contains("severty", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>/</c> で囲んだキーを、ルール ID の正規表現として読むことを検証する。
    /// ID をそのまま書いた設定が先に効き、正規表現どうしは後に書いたものが効く。ID の一部にだけ当たる式は当たらない。
    /// </summary>
    [Fact]
    public void ルールIDを正規表現で指定して重要度を変える()
    {
        ShaderlynConfiguration configuration = ParseWithoutProblems("""
            rules:
              /sl.*/: error
              SL1003: warning
              /SL10.*/:
                severity: info
              "/(CF|UN)03[0-9]{2}/": none
              /0001/: none
            """);

        Assert.Equal(["sl.*", "SL10.*", "(CF|UN)03[0-9]{2}", "0001"], configuration.RuleSeverityPatterns.Select(p => p.Pattern).ToArray());

        AnalyzerOptions options = new(configuration.RuleSeverities, configuration.RuleOptions, configuration.RuleSeverityPatterns);

        Assert.Equal(DiagnosticSeverity.Warning, options.GetEffectiveSeverity(Descriptor("SL1003")));
        Assert.Equal(DiagnosticSeverity.Info, options.GetEffectiveSeverity(Descriptor("SL1004")));
        Assert.Equal(DiagnosticSeverity.Error, options.GetEffectiveSeverity(Descriptor("SL2001")));
        Assert.Equal(DiagnosticSeverity.None, options.GetEffectiveSeverity(Descriptor("CF0310")));
        Assert.Equal(DiagnosticSeverity.Warning, options.GetEffectiveSeverity(Descriptor("CF0001")));

        // ID の全体と一致したものだけが当たる。/0001/ は SL0001 に当たらない (部分一致なら none になる)。
        Assert.Equal(DiagnosticSeverity.Error, options.GetEffectiveSeverity(Descriptor("SL0001")));

        static DiagnosticDescriptor Descriptor(string id)
            => new(id, "title", "message", "category", DiagnosticSeverity.Warning);
    }

    [Fact]
    public void ルールIDの正規表現の誤りを報告する()
    {
        ShaderlynConfiguration configuration = Parse("""
            rules:
              "/SL[/": error
              /HL.*/:
                severity: error
                options: { max: 1 }
            """);

        Assert.Contains(configuration.Problems, p => p.Message.Contains("/SL[/", StringComparison.Ordinal) && p.Message.Contains("正規表現", StringComparison.Ordinal));
        Assert.Contains(configuration.Problems, p => p.Message.Contains("options を書けません", StringComparison.Ordinal));
        Assert.Single(configuration.RuleSeverityPatterns);
    }

    [Fact]
    public void 解釈できない重要度を報告する()
    {
        ShaderlynConfiguration configuration = Parse("rules:\n  SL1003: critical");
        Assert.Contains(configuration.Problems, p => p.Message.Contains("critical", StringComparison.Ordinal));
    }

    [Fact]
    public void 新しいバージョンの設定ファイルを報告する()
    {
        ShaderlynConfiguration configuration = Parse("version: 99");
        Assert.Contains(configuration.Problems, p => p.Message.Contains("99", StringComparison.Ordinal));
    }

    [Fact]
    public void 空の設定ファイルを読める()
    {
        ShaderlynConfiguration configuration = ParseWithoutProblems(string.Empty);
        Assert.Empty(configuration.RuleSeverities);
        Assert.Empty(configuration.Problems);
    }

    [Fact]
    public void 設定ファイルを上のフォルダーから探す()
    {
        string root = Path.Combine(Path.GetTempPath(), $"shaderlyn-config-{Guid.NewGuid():N}");
        string nested = Path.Combine(root, "Assets", "Shaders");

        try
        {
            Directory.CreateDirectory(nested);
            string configPath = Path.Combine(root, ShaderlynConfiguration.FileName);
            File.WriteAllText(configPath, "profile: urp");

            Assert.Equal(configPath, ConfigurationLoader.FindConfigurationFile(nested));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 後始末の失敗はテスト結果に影響させない。
            }
        }
    }
}
