using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// 汎用の HLSL 環境の設定。
/// </summary>
public sealed record HlslEnvironmentOptions
{
    /// <summary>既定の設定。HLSL 2021、構成の軸なし。</summary>
    public static HlslEnvironmentOptions Default { get; } = new();

    /// <summary>環境の名前。既定は <c>hlsl</c>。同じ作りで設定だけが違う環境 (<c>unreal</c>) は名前を変える。</summary>
    public string Name { get; init; } = HlslEnvironment.EnvironmentName;

    /// <summary>コンパイラ。既定は <see cref="HlslCompiler.Dxc"/>。</summary>
    /// <remarks>FXC のときは言語のバージョンを使わず、DXC が定義するマクロ (<c>__HLSL_VERSION</c> など) も定義しない。</remarks>
    public HlslCompiler Compiler { get; init; } = HlslCompiler.Dxc;

    /// <summary>
    /// HLSL の言語のバージョン (DXC の <c>-HV</c>)。既定は <see cref="HlslLanguageVersion.Hlsl2021"/> (DXC 1.7 以降の既定)。
    /// </summary>
    public HlslLanguageVersion LanguageVersion { get; init; } = HlslLanguageVersion.Hlsl2021;

    /// <summary>このコードをコンパイルする方言。</summary>
    public HlslDialect Dialect => DialectFor(LanguageVersion);

    /// <summary>指定した言語のバージョンでコンパイルするときの方言。</summary>
    /// <param name="languageVersion">言語のバージョン。FXC のときは使わない。</param>
    /// <returns>方言。</returns>
    public HlslDialect DialectFor(HlslLanguageVersion languageVersion) => Compiler == HlslCompiler.Fxc
        ? HlslDialect.Fxc
        : HlslDialect.Dxc(languageVersion) with
        {
            Enable16BitTypes = Enable16BitTypes,
            AcceptsHlsl2021Declarations = AcceptHlsl2021DeclarationsIn2018 && languageVersion < HlslLanguageVersion.Hlsl2021,
        };

    /// <summary>
    /// HLSL 2018 で読む単位でも、HLSL 2021 の宣言 (template など) を誤りにしないか (<see cref="HlslDialect.AcceptsHlsl2021Declarations"/>)。
    /// </summary>
    /// <remarks>Unreal Engine がエンジンの設定に従ってバージョンを決めるときに立てる。C++ が個別に 2021 にするシェーダーがある。</remarks>
    public bool AcceptHlsl2021DeclarationsIn2018 { get; init; }

    /// <summary>
    /// 単位ごとに言語のバージョンを決める手続き。<see langword="null"/> なら、どの単位も <see cref="LanguageVersion"/> で読む。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 引数は単位のファイルのパスと、取り込みを解決する手続き (無ければ <see langword="null"/>)。DXC のときだけ使う。
    /// </para>
    /// <para>
    /// Unreal Engine 5.5 の PC (<c>PCD3D_SM6</c>) は、グローバルシェーダーを HLSL 2021、マテリアルのシェーダーを 2018 でコンパイルする
    /// (<c>DataDrivenPlatformInfo.ini</c> の <c>EnablesHLSL2021ByDefault=1</c>)。どちらかは取り込むファイルで決まる。
    /// </para>
    /// </remarks>
    public Func<string, IIncludeResolver?, HlslLanguageVersion>? SelectUnitLanguageVersion { get; init; }

    /// <summary>
    /// 単位ごとに足すマクロを決める手続き。引数は <see cref="SelectUnitLanguageVersion"/> と同じ。
    /// </summary>
    /// <remarks>
    /// 設定で同じ名前を定義していれば、そちらを使う。
    /// Unreal Engine は、マテリアルのシェーダーに <c>IS_MATERIAL_SHADER=1</c> を定義する。
    /// </remarks>
    public Func<string, IIncludeResolver?, ImmutableDictionary<string, string>>? SelectUnitMacros { get; init; }

    /// <summary>
    /// HLSL 2021 で読む単位にだけ定義するマクロ (Unreal Engine の <c>COMPILER_SUPPORTS_HLSL2021</c>)。DXC のときだけ使う。
    /// </summary>
    public ImmutableDictionary<string, string> Hlsl2021Macros { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// 16 ビットの型を使うか (DXC の <c>-enable-16bit-types</c>)。立っていれば <c>__HLSL_ENABLE_16_BIT</c> を 1 で定義する。
    /// </summary>
    /// <remarks>
    /// DXC の版によって、このオプションでマクロを定義するかが違う (1.6 は定義しない)。設定で明示したときだけ定義する。
    /// </remarks>
    public bool Enable16BitTypes { get; init; }

    /// <summary>
    /// どの単位にも共通する構成の空間。単位ごとの指定 (<see cref="HlslCompilationUnit.Variants"/>) があれば、そちらを使う。
    /// </summary>
    public VariantSpace Variants { get; init; } = VariantSpace.Empty;

    /// <summary>
    /// ビルドのときに生成される <c>#include</c> のパスの接頭辞 (<c>/Engine/Generated/</c>)。
    /// </summary>
    /// <remarks><see cref="IHlslEnvironment.IsGeneratedInclude"/> を見ること。</remarks>
    public ImmutableArray<string> GeneratedIncludePrefixes { get; init; } = [];

    /// <summary>
    /// 生成される取り込みのうち、宣言する関数が分かっているもの。キーはパスで、<c>/</c> で終わるものはその下のすべてを指す。
    /// 値は宣言しうる関数の名前 (関数を宣言しないなら空)。
    /// </summary>
    /// <remarks><see cref="IHlslEnvironment.GetFunctionsDeclaredIn"/> を見ること。</remarks>
    public ImmutableDictionary<string, ImmutableArray<string>> GeneratedIncludeFunctions { get; init; } =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    /// <summary>
    /// どの単位にも共通する定義済みマクロ。DXC が定義するものの後、解析の設定で与えたものの前に重ねる。
    /// </summary>
    /// <remarks>エンジンのシェーダーコンパイラが定義するもの (Unreal の <c>COMPILER_HLSL</c> など) を与える。</remarks>
    public ImmutableDictionary<string, string> PredefinedMacros { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// エンジンがシェーダーごとに定義するマクロの名前 (値は分からない)。
    /// </summary>
    /// <remarks><see cref="IHlslEnvironment.IsDefinedByEngine"/> を見ること。</remarks>
    public ImmutableHashSet<string> EngineDefinedMacros { get; init; } = ImmutableHashSet<string>.Empty;
}

/// <summary>
/// 汎用の HLSL 環境。DXC で直接コンパイルする構成 (DirectX 12 のエンジン、独自エンジン) に使う。
/// </summary>
/// <param name="options">環境の設定。</param>
/// <remarks>
/// <para>
/// <b>構成の空間・エントリポイント・ターゲットは、ソースの中からは読まない。</b>
/// どれも設定ファイルで単位ごとに与える (<see cref="HlslCompilationUnit.Variants"/> /
/// <see cref="HlslCompilationUnit.EntryPoints"/> / <see cref="HlslCompilationUnit.Target"/>)。
/// <c>#pragma</c> は DXC と同じく読み流す。
/// </para>
/// <para>
/// 定義済みマクロは、DXC が定義するもの (<see cref="HlslShaderTarget.CreateDxcMacros"/>) と、
/// 解析の設定で与えたもの (<see cref="SemanticsOptions.PredefinedMacros"/>) である。同じ名前なら設定が勝つ。
/// </para>
/// </remarks>
public sealed class HlslEnvironment(HlslEnvironmentOptions? options = null) : IHlslEnvironment
{
    /// <summary>環境の名前。</summary>
    public const string EnvironmentName = "hlsl";

    /// <summary>環境の設定。</summary>
    public HlslEnvironmentOptions Options { get; } = options ?? HlslEnvironmentOptions.Default;

    /// <inheritdoc/>
    public string Name => Options.Name;

    /// <inheritdoc/>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        // FXC は DXC のマクロ (__HLSL_VERSION / __SHADER_STAGE_* など) を定義しない。
        ImmutableDictionary<string, string> compilerMacros = Options.Compiler == HlslCompiler.Fxc
            ? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
            : HlslShaderTarget.CreateCommonDxcMacros(Options.LanguageVersion)
                .SetItems(Options.Enable16BitTypes ? [new("__HLSL_ENABLE_16_BIT", "1")] : [])
                .SetItems(Options.LanguageVersion >= HlslLanguageVersion.Hlsl2021 ? Options.Hlsl2021Macros : []);

        return options with
        {
            Language = options.Hlsl() with { Dialects = [Options.Dialect] },
            PredefinedMacros = compilerMacros
                .SetItems(Options.PredefinedMacros)
                .SetItems(semantics.PredefinedMacros ?? ImmutableDictionary<string, string>.Empty),
        };
    }

    /// <inheritdoc/>
    public PreprocessorOptions PrepareUnit(
        HlslCompilationUnit unit,
        ImmutableArray<ShaderSyntaxToken> tokens,
        PreprocessorOptions options,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(options);

        if (Options.SelectUnitMacros?.Invoke(filePath, options.IncludeResolver) is { Count: > 0 } unitMacros)
        {
            ImmutableDictionary<string, string> macros = options.PredefinedMacros;

            foreach ((string name, string value) in unitMacros)
            {
                if (!macros.ContainsKey(name))
                {
                    macros = macros.Add(name, value);
                }
            }

            options = options with { PredefinedMacros = macros };
        }

        if (Options.Compiler != HlslCompiler.Dxc)
        {
            return options;
        }

        // 単位ごとに言語のバージョンが違えば、方言と、バージョンで決まるマクロを差し替える。
        HlslLanguageVersion version = Options.SelectUnitLanguageVersion?.Invoke(filePath, options.IncludeResolver)
                                      ?? Options.LanguageVersion;

        if (version != Options.LanguageVersion)
        {
            ImmutableDictionary<string, string> macros = options.PredefinedMacros
                .SetItem("__HLSL_VERSION", ((int)version).ToString(System.Globalization.CultureInfo.InvariantCulture));

            foreach ((string name, string value) in Options.Hlsl2021Macros)
            {
                macros = version >= HlslLanguageVersion.Hlsl2021 ? macros.SetItem(name, value) : macros.Remove(name);
            }

            options = options.WithHlsl(h => h with { Dialects = [Options.DialectFor(version)] }) with { PredefinedMacros = macros };
        }

        // ターゲットで決まるマクロは、設定で与えたマクロ (共通・単位ごと) に負ける。
        return unit.Target is { } target
            ? options with { PredefinedMacros = target.CreateDxcMacros(version).SetItems(options.PredefinedMacros) }
            : options;
    }

    /// <inheritdoc/>
    public VariantSpace ReadDeclaredVariants(HlslCompilationUnit unit, ImmutableArray<ShaderSyntaxToken> tokens)
        => VariantsOf(unit);

    /// <inheritdoc/>
    public VariantSpace ReadVariants(HlslCompilationUnit unit, PreprocessResult result) => VariantsOf(unit);

    /// <inheritdoc/>
    public ImmutableArray<HlslEntryPoint> ReadEntryPoints(HlslCompilationUnit unit, PreprocessResult result)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.EntryPoints;
    }

    /// <inheritdoc/>
    /// <remarks>構成の軸は設定で宣言するものなので、宣言に無い名前をシンボルとは見なさない。</remarks>
    public bool LooksLikeVariantSymbol(string name) => false;

    /// <inheritdoc/>
    public bool IsGeneratedInclude(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string normalized = path.Replace('\\', '/');
        return Options.GeneratedIncludePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public bool IsDefinedByEngine(string name) => Options.EngineDefinedMacros.Contains(name);

    /// <inheritdoc/>
    public ImmutableArray<string>? GetFunctionsDeclaredIn(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string normalized = path.Replace('\\', '/');

        foreach ((string entry, ImmutableArray<string> functions) in Options.GeneratedIncludeFunctions)
        {
            if (entry.EndsWith('/')
                    ? normalized.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(normalized, entry, StringComparison.OrdinalIgnoreCase))
            {
                return functions;
            }
        }

        return null;
    }

    /// <summary>
    /// ファイル全体を 1 つの単位にする。
    /// </summary>
    /// <param name="text">ファイル。</param>
    /// <returns>単位。構成の空間・エントリポイント・ターゲットは持たない。</returns>
    public static HlslCompilationUnit WholeFile(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new HlslCompilationUnit(text, new TextSpan(0, text.Length));
    }

    /// <summary>単位の構成の空間。単位の指定が無ければ共通のもの。</summary>
    /// <param name="unit">単位。</param>
    /// <returns>構成の空間。</returns>
    private VariantSpace VariantsOf(HlslCompilationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.Variants ?? Options.Variants;
    }
}
