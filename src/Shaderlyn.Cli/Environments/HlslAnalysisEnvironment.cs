using System.Collections.Immutable;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// 汎用の HLSL 環境。DXC で直接コンパイルする構成 (DirectX 12 のエンジン、独自エンジン) に使う。
/// </summary>
/// <remarks>
/// <para>
/// <b>コンパイル単位と構成の軸は、設定ファイルから作る。</b>
/// <c>units:</c> に当てはまるファイルは、当てはまる単位ごとに 1 回ずつ解析する (エントリポイントとターゲットを変えた 2 回のコンパイルなど)。
/// 当てはまらないファイル (取り込まれる断片) は、ファイル全体を 1 つの単位にし、<c>symbols:</c> のすべてを構成の軸にする。
/// </para>
/// <para>
/// フォルダーを指定したときは <c>.hlsl</c> を探す。<c>.hlsli</c> は取り込まれる断片なので、名前を挙げたときだけ解析する。
/// </para>
/// </remarks>
internal sealed class HlslAnalysisEnvironment : IAnalysisEnvironment
{
    /// <summary>設定ファイルで指定したエントリポイントの出どころとして、診断に出す語。</summary>
    private const string EntryOrigin = "設定ファイルの entry";

    private readonly ShaderlynConfiguration _configuration;
    private readonly IHlslEnvironment _environment;

    /// <summary>設定ファイルから、汎用の HLSL 環境を作る。</summary>
    /// <param name="configuration">設定ファイルの内容。</param>
    public HlslAnalysisEnvironment(ShaderlynConfiguration configuration)
        : this(
            configuration,
            new HlslEnvironment(new HlslEnvironmentOptions
            {
                LanguageVersion = configuration?.HlslVersion == 2018 ? HlslLanguageVersion.Hlsl2018 : HlslLanguageVersion.Hlsl2021,
                Enable16BitTypes = configuration?.HlslEnable16BitTypes == true,
                Variants = BuildVariants(configuration!, null),
            }),
            "HLSL (DXC)",
            [".hlsl"])
    {
    }

    /// <summary>設定ファイルと、意味解析に渡す環境から作る。</summary>
    /// <param name="configuration">設定ファイルの内容。<c>units:</c> と <c>symbols:</c> を読む。</param>
    /// <param name="environment">意味解析に渡す環境 (Unreal なら名前と生成される取り込みが違う)。</param>
    /// <param name="displayName">画面に出す名前。</param>
    /// <param name="defaultExtensions">フォルダーを指定したときに探す拡張子。</param>
    public HlslAnalysisEnvironment(
        ShaderlynConfiguration configuration,
        IHlslEnvironment environment,
        string displayName,
        ImmutableArray<string> defaultExtensions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        _configuration = configuration;
        _environment = environment;
        DisplayName = displayName;
        DefaultExtensions = defaultExtensions;
    }

    /// <inheritdoc/>
    public string Name => _environment.Name;

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
    public ImmutableArray<string> DefaultExtensions { get; }

    /// <inheritdoc/>
    public IHlslEnvironment HlslEnvironment => _environment;

    /// <inheritdoc/>
    public PreparedFile Prepare(SourceText text, SemanticsOptions? semantics, AnalysisTimings? timings)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (semantics is null)
        {
            return new PreparedFile(new AnalysisTarget(text, null, []), [], null);
        }

        long expandStart = AnalysisTimings.Now();
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, CreateUnits(text), text, _environment, ShaderSourceInfo.None, semantics);
        timings?.Add(AnalysisStep.Expand, expandStart);

        long unitStart = AnalysisTimings.Now();
        AnalysisTarget target = compilation.CreateAnalysisTarget();
        timings?.Add(AnalysisStep.Target, unitStart);

        return new PreparedFile(target, compilation.AllIncludePaths, compilation);
    }

    /// <summary>ファイルのコンパイル単位を作る。</summary>
    /// <param name="text">ファイル。</param>
    /// <returns>単位。<c>units:</c> に当てはまらなければ、ファイル全体の 1 つ。</returns>
    private ImmutableArray<HlslCompilationUnit> CreateUnits(SourceText text)
    {
        // ターゲットが分からない単位では、ほかのライブラリとリンクするかも分からない。宣言だけの関数を誤りにしない (HL0311)。
        HlslCompilationUnit whole = Semantics.Environments.HlslEnvironment.WholeFile(text) with { AllowsExternalDefinitions = true };

        if (string.IsNullOrEmpty(text.FilePath))
        {
            return [whole];
        }

        ConfiguredUnit[] matched = [.. _configuration.Units.Where(unit => unit.Matches(text.FilePath))];

        if (matched.Length == 0)
        {
            return [whole];
        }

        // 同じ範囲から作る単位は名前で見分ける。エントリポイントの名前が重ならなければそれを使う。
        bool entriesAreUnique = matched.All(u => u.Entry is not null)
                                && matched.Select(u => u.Entry).Distinct(StringComparer.Ordinal).Count() == matched.Length;

        return
        [
            .. matched.Select((unit, index) => whole with
            {
                Name = matched.Length == 1 ? null : entriesAreUnique ? unit.Entry : $"{unit.Entry ?? "unit"}#{index + 1}",
                ExtraMacros = unit.Defines.IsEmpty ? null : SemanticsSetup.ApplyDefines(ImmutableDictionary<string, string>.Empty, unit.Defines),
                EntryPoints = unit.Entry is { } entry ? [new HlslEntryPoint(entry, EntryOrigin)] : [],
                Target = HlslShaderTarget.TryParse(unit.Target, out HlslShaderTarget? target) ? target : null,
                AllowsExternalDefinitions = target is null || target.Value.Stage == HlslShaderStage.Library,
                Variants = unit.Symbols is { } symbols ? BuildVariants(_configuration, symbols) : null,
            }),
        ];
    }

    /// <summary>
    /// 設定ファイルの <c>symbols:</c> から構成の空間を作る。
    /// </summary>
    /// <param name="configuration">設定ファイルの内容。</param>
    /// <param name="only">使うシンボルの名前。<see langword="null"/> ならすべて。</param>
    /// <returns>構成の空間。</returns>
    /// <remarks>
    /// 組 (<c>one-of</c> / <c>at-most-one</c>) は、組のシンボルがすべて使われるときだけ入れる。
    /// 一部だけを使う単位では、残りのシンボルが無いので、組の制約が意味を持たない。
    /// </remarks>
    internal static VariantSpace BuildVariants(ShaderlynConfiguration configuration, ImmutableArray<string>? only)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        bool Uses(string name) => only is not { } names || names.Contains(name, StringComparer.Ordinal);

        VariantSpace space = VariantSpace.Empty;
        List<VariantDeclaration> declarations = [];
        List<VariantGroup> groups = [];

        foreach (ConfiguredSymbol symbol in configuration.Symbols.Where(s => Uses(s.Name)))
        {
            if (symbol.Values.IsEmpty)
            {
                declarations.Add(new VariantDeclaration(symbol.Name, VariantSymbolKind.Compiled, null));
            }
            else
            {
                space = space.Union(VariantSpace.ForValues(symbol.Name, symbol.Values));
            }
        }

        foreach (ConfiguredSymbolGroup group in configuration.SymbolGroups.Where(g => g.Symbols.All(Uses)))
        {
            foreach (string name in group.Symbols.Where(n => !declarations.Any(d => d.Name == n)))
            {
                declarations.Add(new VariantDeclaration(name, VariantSymbolKind.Compiled, null));
            }

            groups.Add(new VariantGroup(group.Symbols, group.RequiresOne));
        }

        return space.Union(new VariantSpace(declarations, groups));
    }
}
