using System.Collections.Concurrent;
using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Unreal;

/// <summary>
/// Unreal Engine の環境の設定。
/// </summary>
/// <remarks>
/// <para>
/// <b>エンジン本体が無くても解析できる。</b> エンジンのフォルダーを与えなければ <c>/Engine/…</c> は解決できず、
/// 「宣言が無いこと」を根拠にする検査を行わない (SL0002 で伝える)。型の誤りなど、取り込んだ先を見なくてよい検査は行う。
/// </para>
/// <para>
/// 構成の軸 (パーミュテーション) は、C++ の <c>SHADER_PERMUTATION_BOOL</c> / <c>SHADER_PERMUTATION_INT</c> に合わせて設定で与える。
/// C++ を読んで集めることはしない。
/// </para>
/// </remarks>
public sealed record UnrealOptions
{
    /// <summary>環境の名前。</summary>
    public const string EnvironmentName = "unreal";

    /// <summary>
    /// ビルドのときにエンジンが生成する取り込みの接頭辞。uniform buffer・マテリアル・頂点ファクトリの宣言が入る。
    /// </summary>
    public static ImmutableArray<string> GeneratedIncludePrefixes { get; } = ["/Engine/Generated/"];

    /// <summary>
    /// 生成される取り込みのうち、宣言する関数がエンジンの C++ で決まっているもの。
    /// </summary>
    /// <remarks>
    /// <para>
    /// uniform buffer の宣言 (<c>ShaderParameters.cpp</c> が作る) は cbuffer と構造体だけで、関数を宣言しない。
    /// <c>GeneratedInstancedStereo.ush</c> (<c>ShaderCompiler.cpp</c> の <c>GenerateInstancedStereoCode</c>) が宣言する関数は
    /// <c>FinalizeViewState</c> (前方宣言)・<c>GetPrimaryView</c>・<c>GetInstancedView</c> だけである (UE 5.5 で確かめた)。
    /// </para>
    /// <para>
    /// <c>Material.ush</c> / <c>VertexFactory.ush</c> はマテリアルや頂点ファクトリごとに関数が変わるので入れない。
    /// </para>
    /// </remarks>
    public static ImmutableDictionary<string, ImmutableArray<string>> GeneratedIncludeFunctions { get; } =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["/Engine/Generated/GeneratedUniformBuffers.ush"] = [],
            ["/Engine/Generated/UniformBuffers/"] = [],
            ["/Engine/Generated/GeneratedInstancedStereo.ush"] = ["FinalizeViewState", "GetPrimaryView", "GetInstancedView"],
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 生成される取り込みのうち、マテリアルと頂点ファクトリごとに作られるもの。C++ からは作れない
    /// (<see cref="UnrealUniformBufferGenerator"/> が作らない)。
    /// </summary>
    public static ImmutableArray<string> MaterialGeneratedIncludes { get; } =
        ["/Engine/Generated/Material.ush", "/Engine/Generated/VertexFactory.ush"];

    /// <summary>取り込みのパスが、マテリアルごとに作られる生成ファイル (<see cref="MaterialGeneratedIncludes"/>) かを判定する。</summary>
    /// <param name="path"><c>#include</c> に書かれたパス。</param>
    /// <returns>そうなら <see langword="true"/>。</returns>
    public static bool IsMaterialGeneratedInclude(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string normalized = path.Replace('\\', '/');
        return MaterialGeneratedIncludes.Any(include => string.Equals(normalized, include, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// シェーダーコンパイラが定義するマクロの既定値。PC の Direct3D 12 (シェーダーモデル 6) を想定する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// エンジンの <c>Platform.ush</c> などが、コンパイラとプラットフォームを見分けるのに使う。
    /// PC の Direct3D 12 を想定した値である。エンジンの版やプラットフォームで違いうるので、設定で上書きできるようにしてある。
    /// </para>
    /// <para>
    /// ほかのプラットフォーム (Vulkan、コンソール) を想定するなら、設定ファイルの <c>defines</c> で上書きする。
    /// </para>
    /// </remarks>
    public static ImmutableDictionary<string, string> DefaultPredefinedMacros { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COMPILER_HLSL"] = "1",
            ["COMPILER_DXC"] = "1",
            ["SM6_PROFILE"] = "1",

            // エンジンの C++ がどのシェーダーにも定義する (ShaderCompiler.cpp)。PC の Direct3D は深度を反転している。
            ["HAS_INVERTED_Z_BUFFER"] = "1",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>既定の設定。</summary>
    public static UnrealOptions Default { get; } = new();

    /// <summary>エンジンのフォルダー (<c>…/UE_5.4/Engine</c>)。<c>/Engine</c> を <c>Shaders</c> の下に割り当てる。</summary>
    public string? EngineDirectory { get; init; }

    /// <summary>プロジェクトのフォルダー。<c>/Project</c> を <c>Shaders</c> の下に割り当てる。</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>
    /// C++ から作った取り込みのフォルダー (<see cref="UnrealUniformBufferGenerator"/> の書き出し先)。
    /// <c>/Engine/Generated</c> に割り当て、エンジンが定義するマクロの名前を読む。
    /// </summary>
    public string? GeneratedDirectory { get; init; }

    /// <summary>そのほかの割り当て (<c>/Plugin/MyPlugin</c> → プラグインの <c>Shaders</c>)。</summary>
    public ImmutableDictionary<string, string> VirtualPaths { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>コンパイラ。既定は DXC。設定の <c>hlsl: compiler</c> で変える。</summary>
    public HlslCompiler Compiler { get; init; } = HlslCompiler.Dxc;

    /// <summary>
    /// HLSL の言語のバージョン。既定は 2018。
    /// </summary>
    /// <remarks>
    /// <see cref="FollowEngineLanguageVersion"/> が立っていて、エンジンの設定を読めたときは使わない。
    /// 設定の <c>hlsl: version</c> を書いたときは、すべてのシェーダーをこのバージョンで読む。
    /// </remarks>
    public HlslLanguageVersion LanguageVersion { get; init; } = HlslLanguageVersion.Hlsl2018;

    /// <summary>
    /// 言語のバージョンを、エンジンの設定 (<c>DataDrivenPlatformInfo.ini</c> の <c>EnablesHLSL2021ByDefault</c>) に合わせて
    /// シェーダーごとに決めるか。CLI と言語サーバーは、設定に <c>hlsl: version</c> が無ければ立てる。
    /// </summary>
    /// <remarks>
    /// UE 5.5 の PC (<c>PCD3D_SM6</c>) は、グローバルシェーダーを HLSL 2021、マテリアルのシェーダーを 2018 でコンパイルする
    /// (<see cref="UnrealShaderLanguageVersions"/>)。エンジンのフォルダーが無いか、設定を読めなければ <see cref="LanguageVersion"/> を使う。
    /// </remarks>
    public bool FollowEngineLanguageVersion { get; init; }

    /// <summary>エンジンの設定を読むシェーダープラットフォーム。既定は <c>PCD3D_SM6</c>。</summary>
    public string ShaderPlatform { get; init; } = UnrealShaderLanguageVersions.DefaultShaderPlatform;

    /// <summary>
    /// エンジンの設定から読んだ <c>EnablesHLSL2021ByDefault</c>。読まないか、読めなければ <see langword="null"/>。
    /// </summary>
    public int? EngineHlsl2021Default
        => FollowEngineLanguageVersion && Compiler == HlslCompiler.Dxc && EngineDirectory is { } engine
            ? UnrealShaderLanguageVersions.ReadEnablesHlsl2021ByDefault(engine, ShaderPlatform)
            : null;

    /// <summary>16 ビットの型を使うか (<c>hlsl: enable-16bit-types</c>)。立っていれば <c>__HLSL_ENABLE_16_BIT</c> を定義する。</summary>
    public bool Enable16BitTypes { get; init; }

    /// <summary>
    /// 仮想パスの割り当てをすべて集める。
    /// </summary>
    /// <returns>仮想パスからディスクのフォルダーへの割り当て。</returns>
    public ImmutableDictionary<string, string> GetVirtualPathMappings()
    {
        ImmutableDictionary<string, string> mappings = VirtualPaths;

        if (EngineDirectory is { } engine)
        {
            mappings = mappings.SetItem("/Engine", Path.Combine(engine, "Shaders"));
        }

        if (ProjectDirectory is { } project)
        {
            mappings = mappings.SetItem("/Project", Path.Combine(project, "Shaders"));

            // エディタは GBuffer の書き方を、起動のときにプロジェクトの Intermediate に書き出す (AutogenShaderHeaders.ush)。
            string autogen = Path.Combine(project, "Intermediate", "ShaderAutogen", ShaderPlatform);

            if (Directory.Exists(autogen) && !mappings.ContainsKey(ShaderAutogenVirtualPath))
            {
                mappings = mappings.Add(ShaderAutogenVirtualPath, autogen);
            }
        }

        if (GeneratedDirectory is { } generated && !mappings.ContainsKey(GeneratedVirtualPath))
        {
            mappings = mappings.Add(GeneratedVirtualPath, generated);
        }

        return mappings;
    }

    /// <summary>生成される取り込みの仮想パス。</summary>
    private const string GeneratedVirtualPath = "/Engine/Generated";

    /// <summary>エディタが書き出す GBuffer の取り込みの仮想パス。</summary>
    private const string ShaderAutogenVirtualPath = "/Engine/Generated/ShaderAutogen";

    /// <summary>
    /// <see cref="GeneratedDirectory"/> にある、エンジンがシェーダーごとに定義するマクロの名前を読む。
    /// </summary>
    /// <returns>マクロの名前。フォルダーが無いか、読めなければ空。</returns>
    public ImmutableHashSet<string> ReadEngineDefinedMacros()
    {
        if (GeneratedDirectory is not { } generated)
        {
            return ImmutableHashSet<string>.Empty;
        }

        try
        {
            return File.ReadAllLines(Path.Combine(generated, UnrealUniformBufferGenerator.EngineDefinesFileName))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToImmutableHashSet(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return ImmutableHashSet<string>.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return ImmutableHashSet<string>.Empty;
        }
    }

    /// <summary>
    /// この設定の、意味解析に渡す環境を作る。
    /// </summary>
    /// <param name="variants">どの単位にも共通する構成の空間 (パーミュテーション)。</param>
    /// <returns>環境。</returns>
    public HlslEnvironment CreateEnvironment(VariantSpace? variants = null)
    {
        int? engineDefault = EngineHlsl2021Default;

        return new(new HlslEnvironmentOptions
        {
            Name = EnvironmentName,
            Compiler = Compiler,
            LanguageVersion = engineDefault is { } value ? UnrealShaderLanguageVersions.EnvironmentLanguageVersion(value) : LanguageVersion,
            SelectUnitLanguageVersion = engineDefault is { } mode ? UnrealShaderLanguageVersions.CreateSelector(mode) : null,
            SelectUnitMacros = CreateMaterialMacroSelector(),

            // シェーダーの C++ が個別に HLSL 2021 にすることがある (CFLAG_HLSL2021)。C++ を読むまでは見分けられない。
            AcceptHlsl2021DeclarationsIn2018 = engineDefault is not null,
            Hlsl2021Macros = Compiler == HlslCompiler.Dxc ? Hlsl2021Macros : ImmutableDictionary<string, string>.Empty,
            Enable16BitTypes = Enable16BitTypes,
            Variants = variants ?? VariantSpace.Empty,
            GeneratedIncludePrefixes = GeneratedIncludePrefixes,
            GeneratedIncludeFunctions = GeneratedIncludeFunctions,
            PredefinedMacros = DefaultPredefinedMacros,
            EngineDefinedMacros = ReadEngineDefinedMacros(),
        });
    }

    /// <summary>マテリアルのシェーダーにだけ定義するマクロ。</summary>
    /// <remarks>
    /// エンジンはマテリアルを翻訳するときに <c>IS_MATERIAL_SHADER=1</c> を定義する (UE 5.5 の <c>HLSLMaterialTranslator.cpp</c>)。
    /// エンジンのヘッダ (<c>LumenRadianceCacheMarkCommon.ush</c> など) は、このマクロで宣言を切り替える。
    /// </remarks>
    public static ImmutableDictionary<string, string> MaterialShaderMacros { get; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal).Add("IS_MATERIAL_SHADER", "1");

    /// <summary>マテリアルのシェーダー (<c>/Engine/Generated/Material.ush</c> を取り込むもの) に、<see cref="MaterialShaderMacros"/> を足す手続きを作る。</summary>
    private static Func<string, IIncludeResolver?, ImmutableDictionary<string, string>> CreateMaterialMacroSelector()
    {
        ConcurrentDictionary<string, bool> materialByFile = new(StringComparer.OrdinalIgnoreCase);

        return (filePath, resolver) => UnrealShaderLanguageVersions.IncludesMaterial(filePath, resolver, materialByFile)
            ? MaterialShaderMacros
            : ImmutableDictionary<string, string>.Empty;
    }

    /// <summary>
    /// HLSL 2021 で読むシェーダーにだけ定義するマクロ。
    /// </summary>
    /// <remarks>
    /// エンジンは C++ でシェーダーに <c>CFLAG_HLSL2021</c> を立てると、DXC に <c>-HV 2021</c> を渡し、
    /// <c>COMPILER_SUPPORTS_HLSL2021=1</c> を定義する (<c>ShaderCompiler.cpp</c>)。
    /// </remarks>
    public static ImmutableDictionary<string, string> Hlsl2021Macros { get; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal).Add("COMPILER_SUPPORTS_HLSL2021", "1");

    /// <summary>
    /// この設定で定義するマクロ。既定のマクロ (<see cref="DefaultPredefinedMacros"/>) に、言語のバージョンで決まるものを足す。
    /// </summary>
    /// <remarks>
    /// <b>HLSL 2021 なら <c>COMPILER_SUPPORTS_HLSL2021=1</c> を定義する。</b>
    /// エンジンは C++ でシェーダーに <c>CFLAG_HLSL2021</c> を立てると、DXC に <c>-HV 2021</c> を渡し、このマクロを定義する
    /// (<c>ShaderCompiler.cpp</c>)。エンジンのヘッダ (<c>DoubleFloat.ush</c> など) は、このマクロで HLSL 2021 の書き方に切り替える。
    /// 定義しないと、<c>hlsl: version: 2021</c> にしても 2018 向けの分岐を読むことになる。
    /// </remarks>
    public ImmutableDictionary<string, string> PredefinedMacros
        => Compiler == HlslCompiler.Dxc && LanguageVersion >= HlslLanguageVersion.Hlsl2021
            ? DefaultPredefinedMacros.SetItem("COMPILER_SUPPORTS_HLSL2021", "1")
            : DefaultPredefinedMacros;
}
