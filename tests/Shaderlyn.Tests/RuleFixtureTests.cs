using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Cli;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;

namespace Shaderlyn.Tests;

/// <summary>
/// 規則ごとに、実際に違反するシェーダーを置いて報告されることを確かめる。
/// </summary>
/// <remarks>
/// <para>
/// <b>規則を足したら、それを破るシェーダーも足す。</b>
/// 単体テストは中の作りを知ったうえで書くため、
/// 「その作りのまま壊れていない」ことしか確かめられない。
/// ここでは利用者が書くのと同じ形のシェーダーを 1 本置き、
/// 端から端まで通して報告されることを見る。
/// </para>
/// <para>
/// <see cref="すべての規則に違反するシェーダーがある"/> が、
/// 置き忘れを見つける。規則を足して違反シェーダーを置かなければ落ちる。
/// </para>
/// </remarks>
public sealed class RuleFixtureTests
{
    /// <summary>違反するシェーダーを置いてある場所。</summary>
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "rules");

    /// <summary>
    /// 違反するシェーダーを置けない規則と、その理由。
    /// </summary>
    /// <remarks>
    /// <b>ここに足すときは理由を書くこと。</b>
    /// 理由を書けないなら、それは置けないのではなく置いていないだけである。
    /// </remarks>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["TOOL0001"] = "アナライザが例外を投げたときの報告。シェーダーの内容では起こせない",
        ["TOOL0002"] = "ファイルを読めなかったときの報告。シェーダーの内容では起こせない",
        ["TOOL0003"] = "設定ファイルの誤りの報告。シェーダーの内容では起こせない",

        // 循環は取り込み先で起きるため、報告は取り込んだファイルの側に出る。
        // .shader の診断としては CF0101 のメッセージの中に現れる。
        // これは HL0320_の循環はCF0101の中に現れる で確かめている。
        ["CF0020"] = "循環は取り込み先で起きるため、報告が CF0101 のメッセージの中に入る",

        // template は HLSL 2021 の構文で、Unity (HLSL 2018) の .shader では HL0003 になる。
        // Hlsl2021TemplateTests で、汎用の HLSL 環境の .hlsl として確かめている。
        ["HL0345"] = "HLSL 2021 の template の呼び出しなので、Unity の .shader では起こせない",

        // enum も HLSL 2021 の構文。Hlsl2021EnumTests で確かめている。
        ["HL0347"] = "HLSL 2021 の enum の値なので、Unity の .shader では起こせない",

        // Unity は HLSL 2021 でコンパイルしないので起こせない。Hlsl2021Tests で、汎用の HLSL 環境の .hlsl として確かめている。
        ["HL0349"] = "HLSL 2021 だけの規則なので、Unity の .shader では起こせない",

        // fixture は違反シェーダーの隣を探索パスにしているので、ヘッダから見つからない取り込みもシェーダーから引き直さずに見つかる。
        // UserHeaderTests で、Unity プロジェクトの形にして確かめている。
        ["UN0102"] = "fixture の探索パスで見つかり、シェーダーからの引き直しが起きない",

        // Unreal の環境だけの規則。UnrealEnvironmentTests で、Unreal の .usf として確かめている。
        ["UE0001"] = "Unreal の環境だけの規則なので、Unity の .shader では起こせない",
        ["UE0002"] = "Unreal の環境だけの規則なので、Unity の .shader では起こせない",
        ["UE0003"] = "Unreal の環境だけの規則なので、Unity の .shader では起こせない",

        // GLSL だけの規則。GlslRuleTests で、GLSL のファイルとして確かめている。
        ["GL0001"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0002"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0003"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0101"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0102"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0103"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0104"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0301"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0302"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0303"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0304"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0305"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0306"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0307"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0308"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0309"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0310"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0311"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0312"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0313"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0314"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0315"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0316"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0317"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0318"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0319"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0320"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0321"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0322"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0323"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0324"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0325"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0401"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0402"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0403"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0404"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0405"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0406"] = "GLSL だけの規則なので、Unity の .shader では起こせない",
        ["GL0201"] = "GLSL だけの規則なので、Unity の .shader では起こせない",

        // Godot シェーダー言語だけの規則。GodotRuleTests で、.gdshader として確かめている。
        ["GD0001"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0002"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0101"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0104"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0102"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0103"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0405"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0334"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0403"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0301"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0302"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0311"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0306"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0307"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0308"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0312"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0317"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0319"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0332"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0333"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0309"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0321"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["GD0322"] = "Godot シェーダー言語だけの規則なので、Unity の .shader では起こせない",
        ["WG0001"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0309"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0330"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["SG0101"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0401"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0342"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0343"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0359"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0360"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0361"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0362"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0363"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0358"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0357"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0356"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0355"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0354"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0353"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0352"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0351"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0350"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0349"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0005"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0348"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0347"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0346"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0345"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0344"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0407"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0318"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0308"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0304"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0301"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0309"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0311"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0402"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0004"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0404"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0405"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0406"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0403"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0316"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0103"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0102"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0341"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0338"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0321"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["SG0317"] = "Slang だけの規則なので、Unity の .shader では起こせない。SlangRuleTests で確かめている",
        ["WG0003"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0302"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0301"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0338"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0101"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0339"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0103"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0322"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0002"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0403"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0324"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0337"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0336"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0332"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0317"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0315"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0311"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0308"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0307"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",
        ["WG0303"] = "WGSL だけの規則なので、Unity の .shader では起こせない。WgslRuleTests で確かめている",

        // Unity のバージョンが分かるときだけ報告する (use_dxc d3d11 の非推奨は 6.6、inlineraytracing は 2023.2〜6.3)。
        // fixture はバージョンを渡さずに解析するので起こせない。CompileTargetRuleTests で確かめている。
        ["UN0202"] = "Unity のバージョンが分かるときだけ報告するので、バージョンを渡さない fixture では起こせない",
    };

    /// <summary>違反するシェーダーを置いてある規則。</summary>
    public static TheoryData<string> Rules
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string id in FixtureIds())
            {
                // 置いてはあるが、別の形でしか報告されない規則は theory から外す。
                // その形は専用の検証で確かめる。
                if (!Exempt.ContainsKey(id))
                {
                    data.Add(id);
                }
            }

            return data;
        }
    }

    /// <summary>置いてある違反シェーダーの規則を列挙する。</summary>
    /// <returns>規則の名前。</returns>
    private static IEnumerable<string> FixtureIds()
        => Directory.EnumerateFiles(FixtureDirectory, "*.shader")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>();

    [Theory]
    [MemberData(nameof(Rules))]
    public void 違反するシェーダーでその規則を報告する(string ruleId)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(ruleId);

        Assert.True(
            diagnostics.Any(d => d.Id == ruleId),
            $"{ruleId} を報告するはずが、報告したのは "
            + $"[{string.Join(", ", diagnostics.Select(d => d.Id).Distinct().Order(StringComparer.Ordinal))}] だった");
    }

    [Fact]
    public void すべての規則に違反するシェーダーがある()
    {
        HashSet<string> covered = [.. FixtureIds()];

        List<string> missing =
        [
            .. RuleCatalog.GetShaderRules(BuiltInAnalyzers.All)
                .Select(d => d.Id)
                .Where(id => !covered.Contains(id) && !Exempt.ContainsKey(id))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            missing.Count == 0,
            $"違反するシェーダーが存在しない規則: {string.Join(", ", missing)}。"
            + $"fixtures/rules/<ID>.shader を足すか、理由を添えて Exempt へ入れること");
    }

    [Fact]
    public void 置けない理由を書いていない除外がない()
    {
        // 理由の無い除外は「置けない」ではなく「置いていない」である。
        Assert.All(Exempt, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
    }

    [Fact]
    public void HL0320_の循環はCF0101の中に現れる()
    {
        // include の循環は取り込み先で起きるため、.shader 自身の診断にはならない。
        // 見送った事実は CF0101 が伝える。
        ImmutableArray<Diagnostic> diagnostics = Analyze("CF0020");

        Diagnostic reported = Assert.Single(diagnostics, d => d.Id == "CF0101");

        Assert.Contains("CF0020", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void TOOL0004_はパッケージでないパスにUnityの直し方を案内しない()
    {
        // 違反シェーダーが引けないのは definitely/missing.hlsl で、Unity とは関係が無い。
        // ここで --unity-project や PackageCache を案内すると、効果の無い設定を試させることになる。
        Diagnostic reported = Assert.Single(Analyze("TOOL0004"), d => d.Id == "TOOL0004");
        string message = reported.GetMessage();

        Assert.DoesNotContain("PackageCache", message, StringComparison.Ordinal);
        Assert.DoesNotContain("--unity-project", message, StringComparison.Ordinal);
        Assert.Contains("--include-path", message, StringComparison.Ordinal);
    }

    /// <summary>違反するシェーダーを解析して診断を得る。</summary>
    /// <param name="ruleId">対象の規則。</param>
    /// <returns>検出した診断。</returns>
    private static ImmutableArray<Diagnostic> Analyze(string ruleId)
    {
        string path = Path.Combine(FixtureDirectory, $"{ruleId}.shader");
        SourceText text = SourceText.Load(path);
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        SemanticsOptions options = new()
        {
            // 取り込みは 2 つの経路で解く。
            // Unity のヘッダは中身の要らない空のファイルとして与え、
            // 違反シェーダーの隣に置いた .hlsl は実際に読む。
            IncludeResolver = new CompositeIncludeResolver(
            [
                new UnityHeaderStubResolver(),
                new FileSystemIncludeResolver([FixtureDirectory]),
            ]),
        };

        ShaderCompilation compilation = UnityCompilation.Create(text, tree, options);

        return new AnalyzerDriver(BuiltInAnalyzers.All)
            .Analyze(compilation.CreateAnalysisTarget());
    }

    /// <summary>
    /// Unity のヘッダを、中身の無いファイルとして返す。
    /// </summary>
    /// <remarks>
    /// <b>Unity がインストールされていなくても検証できなければならない。</b>
    /// ここで見たいのは規則が働くかどうかであって、
    /// Unity のヘッダの中身ではない。
    /// 解決できたことにしないと、依存が揃っていない扱いになり
    /// プロパティと uniform の対応検査が丸ごと見送られる。
    /// </remarks>
    internal sealed class UnityHeaderStubResolver : IIncludeResolver
    {
        /// <inheritdoc/>
        public bool TryResolve(
            string path,
            string includingFilePath,
            [NotNullWhen(true)] out SourceText? resolved)
        {
            resolved = path.StartsWith("Packages/", StringComparison.Ordinal)
                ? SourceText.From(string.Empty, path)
                : null;

            return resolved is not null;
        }
    }
}
