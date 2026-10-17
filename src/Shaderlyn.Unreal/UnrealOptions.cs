using System.Collections.Concurrent;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Unreal;

/// <summary>
/// Unreal Engine の環境の設定。
/// </summary>
/// <remarks>
/// <para>
/// <b>エンジン本体が無くても解析できる。</b> エンジンのフォルダーを与えなければ <c>/Engine/…</c> は解決できず、
/// 「宣言が無いこと」を根拠にする検査を行わない (HL0005 で伝える)。型の誤りなど、取り込んだ先を見なくてよい検査は行う。
/// </para>
/// <para>
/// <c>--unreal-generate</c> が C++ から書き出した登録とクラスを読み、<c>.usf</c> をエンジンがコンパイルする単位で解析する
/// (<see cref="CreateRegisteredUnits"/>)。クラスで値が 1 つに決まるマクロは単位に定義する。
/// 構成の軸 (パーミュテーション) は、まだ C++ から作らない。C++ の <c>SHADER_PERMUTATION_BOOL</c> / <c>SHADER_PERMUTATION_INT</c> に合わせて設定で与える。
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
    /// 生成のフォルダーにあるシェーダーの登録 (<see cref="UnrealShaderRegistrations"/>) から、ファイルの単位を作るものを返す。
    /// </summary>
    /// <returns>
    /// ファイル全体の単位を受け取り、登録されたエントリポイントごとの単位を返す。登録が無いファイルは空を返す。
    /// 生成のフォルダーが無いか、登録の一覧が無ければ <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>エンジンが実際にコンパイルする単位で解析する。</b>
    /// 単位は、エントリポイントとステージ (シェーダーモデル 6.6) を持ち、ステージのマクロ (<c>PIXELSHADER=1</c> など、ほかのステージは 0) を定義する。
    /// エンジンの C++ がそうするためである (UE 5.5 の <c>ShaderCompiler.cpp</c>)。
    /// </para>
    /// <para>
    /// 同じエントリポイントとステージを、別のクラス (パーミュテーションの違い) が登録していれば、単位は 1 つにする。
    /// ファイルのパスは、仮想パスの割り当て (<see cref="GetVirtualPathMappings"/>) で仮想パスにしてから引く。
    /// </para>
    /// </remarks>
    public Func<ShaderCompilationUnit, ImmutableArray<ShaderCompilationUnit>>? CreateRegisteredUnits()
    {
        if (GeneratedDirectory is not { } generated)
        {
            return null;
        }

        ImmutableArray<UnrealShaderRegistration> registrations = UnrealShaderRegistrations.Read(generated);

        if (registrations.IsEmpty)
        {
            return null;
        }

        ILookup<string, UnrealShaderRegistration> byPath = registrations.ToLookup(r => r.VirtualPath, StringComparer.OrdinalIgnoreCase);
        ImmutableDictionary<string, UnrealShaderClass> classes = UnrealShaderClasses.Read(generated);
        ImmutableDictionary<string, string> platformMacros = PlatformMacros;

        // 仮想パスの割り当てを、長いフォルダーから引く (/Engine/Generated は /Engine より先)。
        (string Virtual, string Directory)[] mappings =
        [
            .. GetVirtualPathMappings()
                .Select(p => (Virtual: p.Key.TrimEnd('/'), Directory: Path.GetFullPath(p.Value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .OrderByDescending(p => p.Directory.Length),
        ];

        return whole =>
        {
            if (string.IsNullOrEmpty(whole.Text.FilePath) || ToVirtualPath(whole.Text.FilePath, mappings) is not { } virtualPath)
            {
                return [];
            }

            IGrouping<(string EntryPoint, string Frequency), UnrealShaderRegistration>[] found =
            [
                .. byPath[virtualPath]
                    .GroupBy(r => (r.EntryPoint, r.Frequency))
                    .OrderBy(g => g.Key.EntryPoint, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Frequency, StringComparer.Ordinal),
            ];

            return [.. found.Select(g => CreateRegisteredUnit(whole, [.. g], classes, platformMacros, named: found.Length > 1))];
        };
    }

    /// <summary>ファイルのパスを仮想パスにする。</summary>
    /// <param name="filePath">ファイルのパス。</param>
    /// <param name="mappings">仮想パスの割り当て (長いフォルダーが先)。</param>
    /// <returns>仮想パス。どの割り当ての下にも無ければ <see langword="null"/>。</returns>
    private static string? ToVirtualPath(string filePath, (string Virtual, string Directory)[] mappings)
    {
        string full = Path.GetFullPath(filePath);

        foreach ((string virtualRoot, string directory) in mappings)
        {
            if (full.Length > directory.Length
                && full.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
                && (full[directory.Length] == Path.DirectorySeparatorChar || full[directory.Length] == Path.AltDirectorySeparatorChar))
            {
                return virtualRoot + full[directory.Length..].Replace('\\', '/');
            }
        }

        return null;
    }

    /// <summary>同じエントリポイントとステージの登録から単位を作る。</summary>
    /// <param name="whole">ファイル全体の単位。</param>
    /// <param name="group">登録 (同じエントリポイントとステージを、パーミュテーションの違うクラスが登録していることがある)。</param>
    /// <param name="classes">C++ のシェーダーのクラス。</param>
    /// <param name="platformMacros">シェーダープラットフォームでエンジンが定義するマクロ。</param>
    /// <param name="named">同じファイルにほかの単位があり、名前で見分ける必要があるか。</param>
    /// <returns>単位。</returns>
    private static ShaderCompilationUnit CreateRegisteredUnit(
        ShaderCompilationUnit whole,
        ImmutableArray<UnrealShaderRegistration> group,
        ImmutableDictionary<string, UnrealShaderClass> classes,
        ImmutableDictionary<string, string> platformMacros,
        bool named)
    {
        UnrealShaderRegistration registration = group[0];
        ImmutableArray<UnrealShaderClass?> found = [.. group.Select(r => classes.GetValueOrDefault(UnrealShaderClasses.BareClassName(r.ShaderClass)))];
        // 値の決まらないマクロとパーミュテーションは、まだ構成の軸にしない (定義しない。以前と同じ)。
        // 構成の軸にすると、軸で切り替わる #include の中の #pragma once のヘッダで、宣言が片方の構成にしか無いことになる
        // (前処理の不具合。計画の UE4 の残りを参照)。
        (ImmutableDictionary<string, string> classMacros, _) = MergeClasses(found);

        ImmutableArray<string> functions = UnrealShaderRegistrations.EntryFunctions(registration.EntryPoint);
        HlslShaderStage? stage = UnrealShaderRegistrations.ToStage(registration.Frequency);
        string display = $"{functions.FirstOrDefault() ?? registration.EntryPoint} (SF_{registration.Frequency})";
        string origin = $"C++ の {registration.Macro} ({Path.GetFileName(registration.SourceFile)} の {registration.ShaderClass})";

        return whole with
        {
            Name = named ? display : null,
            DisplayName = display,
            ExtraMacros = platformMacros.SetItems(classMacros).SetItems(UnrealShaderRegistrations.CreateStageMacros(registration.Frequency)),
            EntryPoints = [.. functions.Select(f => new ShaderEntryPoint(f, origin))],
            Target = stage is { } s ? new HlslShaderTarget(s, 6, 6) : null,

            // ライブラリ (レイトレーシングなど) は、ほかの単位とリンクするので、宣言だけの関数を誤りにしない (HL0311)。
            AllowsExternalDefinitions = stage is null or HlslShaderStage.Library,
        };
    }

    /// <summary>
    /// 同じ単位を登録したクラスの、定義するマクロとパーミュテーションを合わせる。
    /// </summary>
    /// <param name="classes">クラス。見つからなかったクラスは <see langword="null"/>。</param>
    /// <returns>どのクラスでも同じ値のマクロと、それ以外を構成の軸にした空間。</returns>
    /// <remarks>
    /// <para>
    /// <b>値が 1 つに決まるマクロは定義し、決まらないものは構成の軸にする。</b>
    /// 構成の軸にしたマクロは、両方の分岐を読む (値が分かっていれば、その値のそれぞれを読む)。
    /// どのクラスも定義しないマクロは定義しない。エンジンもそのマクロを定義しないからである。
    /// </para>
    /// <para>
    /// コンパイラのフラグは、エンジンが定義するマクロにする (<see cref="CompilerFlagMacros"/>)。
    /// </para>
    /// </remarks>
    internal static (ImmutableDictionary<string, string> Macros, VariantSpace Variants) MergeClasses(ImmutableArray<UnrealShaderClass?> classes)
    {
        // マクロの名前から、クラスごとの値 (無ければ "0"、決まらなければ null)。
        Dictionary<string, List<string?>> defines = new(StringComparer.Ordinal);
        Dictionary<string, List<ImmutableArray<int>?>> permutations = new(StringComparer.Ordinal);
        bool anyMissing = classes.Any(c => c is null);

        foreach (UnrealShaderClass shaderClass in classes.OfType<UnrealShaderClass>())
        {
            foreach ((string name, string? value) in shaderClass.Defines)
            {
                defines.TryAdd(name, []);
            }

            foreach ((string flag, string macro) in CompilerFlagMacros)
            {
                defines.TryAdd(macro, []);
            }

            foreach ((string name, ImmutableArray<int>? values) in shaderClass.Permutations)
            {
                permutations.TryAdd(name, []);
            }
        }

        foreach (UnrealShaderClass? shaderClass in classes)
        {
            foreach ((string name, List<string?> values) in defines)
            {
                values.Add(shaderClass is null ? null : ClassDefine(shaderClass, name));
            }

            foreach ((string name, List<ImmutableArray<int>?> values) in permutations)
            {
                values.Add(shaderClass is null ? null : shaderClass.Permutations.TryGetValue(name, out ImmutableArray<int>? found) ? found : [0]);
            }
        }

        ImmutableDictionary<string, string>.Builder macros = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        VariantSpace variants = VariantSpace.Empty;

        foreach ((string name, List<ImmutableArray<int>?> values) in permutations)
        {
            variants = variants.Union(values.Any(v => v is null)
                ? Unknown(name)
                : VariantSpace.ForValues(name, [.. values.SelectMany(v => v!.Value).Distinct().Order()]));
        }

        foreach ((string name, List<string?> values) in defines.Where(p => !permutations.ContainsKey(p.Key)))
        {
            if (values.All(v => v is not null) && values.Distinct(StringComparer.Ordinal).Count() == 1)
            {
                // どのクラスも定義しない ("0" のフラグのマクロ以外) ものは、すでに定義しないことで表している。
                macros[name] = values[0]!;
                continue;
            }

            variants = variants.Union(values.Any(v => v is null) || !values.All(v => int.TryParse(v, out _))
                ? Unknown(name)
                : VariantSpace.ForValues(name, [.. values.Select(v => int.Parse(v!, System.Globalization.CultureInfo.InvariantCulture)).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key)]));
        }

        // クラスが見つからなかった単位は、ほかのクラスの値を使えない。値の決まったマクロも構成の軸にする。
        if (anyMissing)
        {
            foreach (string name in macros.Keys.ToList())
            {
                macros.Remove(name);
                variants = variants.Union(Unknown(name));
            }
        }

        return (macros.ToImmutable(), variants);

        static VariantSpace Unknown(string name)
            => new([new VariantDeclaration(name, VariantSymbolKind.Compiled, null)], []);
    }

    /// <summary>クラスがマクロに与える値。定義しなければ <c>"0"</c>、決まらなければ <see langword="null"/>。</summary>
    private static string? ClassDefine(UnrealShaderClass shaderClass, string name)
    {
        if (shaderClass.Defines.TryGetValue(name, out string? value))
        {
            return value;
        }

        foreach ((string flag, string macro) in CompilerFlagMacros)
        {
            if (macro == name)
            {
                return shaderClass.CompilerFlags.TryGetValue(flag, out string? set) ? set : "0";
            }
        }

        return "0";
    }

    /// <summary>
    /// コンパイラのフラグと、それを見てエンジンが定義するマクロ (UE 5.5 の <c>ShaderFormatD3D.cpp</c>、シェーダーモデル 6.6 以上)。
    /// </summary>
    public static ImmutableArray<(string Flag, string Macro)> CompilerFlagMacros { get; } =
    [
        ("CFLAG_InlineRayTracing", "PLATFORM_SUPPORTS_INLINE_RAY_TRACING"),
        ("CFLAG_AllowRealTypes", "PLATFORM_SUPPORTS_REAL_TYPES"),
    ];

    /// <summary>
    /// PC の Direct3D 12 (<c>PCD3D_SM6</c>) で、エンジンがどのシェーダーにも定義するマクロ (UE 5.5 の <c>ShaderFormatD3D.cpp</c>)。
    /// </summary>
    /// <remarks>
    /// シェーダーモデル 6.6 でコンパイルする場合の値である。コンパイラのフラグで決まるものは <see cref="CompilerFlagMacros"/> にある。
    /// </remarks>
    public static ImmutableDictionary<string, string> PcD3DSm6Macros { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COMPILER_HLSL"] = "1",
            ["COMPILER_FXC"] = "0",
            ["COMPILER_DXC"] = "1",
            ["PLATFORM_SUPPORTS_ROV"] = "1",
            ["PLATFORM_SUPPORTS_SM6_0_WAVE_OPERATIONS"] = "1",
            ["PLATFORM_SUPPORTS_DIAGNOSTIC_BUFFER"] = "1",
            ["SM6_PROFILE"] = "1",
            ["SM5_PROFILE"] = "0",
            ["ES3_1_PROFILE"] = "0",
            ["PLATFORM_SUPPORTS_STATIC_SAMPLERS"] = "1",
            ["PLATFORM_SUPPORTS_CALLABLE_SHADERS"] = "1",
            ["COMPILER_SUPPORTS_NOINLINE"] = "1",
            ["__SHADER_TARGET_MAJOR"] = "6",
            ["__SHADER_TARGET_MINOR"] = "6",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 登録から作る単位に定義する、シェーダープラットフォームのマクロ。
    /// </summary>
    /// <remarks>
    /// 分かっているのは PC の Direct3D 12 (<c>PCD3D_SM6</c>) を DXC でコンパイルする場合だけである。
    /// ほかのプラットフォームでは定義しない (設定の <c>defines</c> で与える)。
    /// </remarks>
    public ImmutableDictionary<string, string> PlatformMacros
    {
        get
        {
            ImmutableDictionary<string, string> macros = Compiler == HlslCompiler.Dxc && ShaderPlatform == UnrealShaderLanguageVersions.DefaultShaderPlatform
                ? PcD3DSm6Macros
                : ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

            return EngineDirectory is { } engine && UnrealShaderLanguageVersions.ReadShaderPlatformSettings(engine, ShaderPlatform) is { } settings
                ? macros.SetItems(CreateDataDrivenMacros(settings))
                : macros;
        }
    }

    /// <summary>
    /// エンジンが、シェーダープラットフォームの設定 (<c>DataDrivenPlatformInfo.ini</c>) からそのまま決めるマクロと、その設定のキー
    /// (UE 5.5 の <c>ShaderCompiler.cpp</c> の <c>GlobalBeginCompileShader</c>)。
    /// </summary>
    /// <remarks>
    /// プロジェクトの設定 (コンソール変数) で決まるもの (<c>SUBSTRATE_ENABLED</c> など) は含めない。値はプロジェクトごとに違う。
    /// </remarks>
    public static ImmutableArray<(string Macro, string Key)> DataDrivenMacroKeys { get; } =
    [
        ("PLATFORM_SUPPORTS_CLIP_DISTANCE", "bSupportsClipDistance"),
        ("PLATFORM_SUPPORTS_VERTEX_SHADER_SRVS", "bSupportsVertexShaderSRVs"),
        ("PLATFORM_SUPPORTS_VERTEX_SHADER_UAVS", "bSupportsVertexShaderUAVs"),
        ("SUPPORTS_INDEPENDENT_SAMPLERS", "bSupportsIndependentSamplers"),
        ("PLATFORM_SUPPORTS_SHADER_ROOT_CONSTANTS", "bSupportsShaderRootConstants"),
        ("PLATFORM_SUPPORTS_RENDERTARGET_WRITE_MASK", "bSupportsRenderTargetWriteMask"),
        ("PLATFORM_SUPPORTS_PER_PIXEL_DBUFFER_MASK", "bSupportsPerPixelDBufferMask"),
        ("PLATFORM_SUPPORTS_MESH_SHADERS_TIER0", "bSupportsMeshShadersTier0"),
        ("PLATFORM_SUPPORTS_MESH_SHADERS_TIER1", "bSupportsMeshShadersTier1"),
        ("PLATFORM_ALLOW_SCENE_DATA_COMPRESSED_TRANSFORMS", "bSupportsSceneDataCompressedTransforms"),
        ("PLATFORM_SUPPORTS_UNIFORM_BUFFER_OBJECTS", "bSupportsUniformBufferObjects"),
        ("PLATFORM_SUPPORTS_RAY_TRACING_HIGH_END_EFFECTS", "bSupportsHighEndRayTracingEffects"),
        ("COMPILER_SUPPORTS_BARYCENTRIC_INTRINSICS", "bSupportsBarycentricsIntrinsics"),
        ("PLATFORM_SUPPORTS_BARYCENTRICS_SEMANTIC", "SupportsBarycentricsSemantic"),
    ];

    /// <summary>シェーダープラットフォームの設定から、<see cref="DataDrivenMacroKeys"/> のマクロを作る。</summary>
    /// <param name="settings">設定の節 (<see cref="UnrealShaderLanguageVersions.ReadShaderPlatformSettings"/>)。</param>
    /// <returns>マクロ。書いていないキーは 0 (エンジンの既定は「無い」)。</returns>
    /// <remarks>
    /// 値は <c>true</c> / <c>false</c> か、機能の有無 (<c>Unsupported</c> / <c>RuntimeDependent</c> / <c>RuntimeGuaranteed</c>) である。
    /// 機能の有無は、<c>Unsupported</c> 以外を 1 にする (エンジンも <c>!= ERHIFeatureSupport::Unsupported</c> で決める)。
    /// <c>PLATFORM_MAX_SAMPLERS</c> は書いてあるときだけ定義する。
    /// </remarks>
    internal static ImmutableDictionary<string, string> CreateDataDrivenMacros(ImmutableDictionary<string, string> settings)
    {
        ImmutableDictionary<string, string>.Builder macros = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach ((string macro, string key) in DataDrivenMacroKeys)
        {
            bool enabled = settings.TryGetValue(key, out string? value)
                           && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                               || value.Equals("RuntimeDependent", StringComparison.OrdinalIgnoreCase)
                               || value.Equals("RuntimeGuaranteed", StringComparison.OrdinalIgnoreCase));
            macros[macro] = enabled ? "1" : "0";
        }

        if (settings.TryGetValue("MaxSamplers", out string? samplers) && int.TryParse(samplers, out int count))
        {
            macros["PLATFORM_MAX_SAMPLERS"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return macros.ToImmutable();
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
            EngineDefinedMacros = ReadEngineDefinedMacros().Union(PreprocessorDefinedNames),
            EngineDefinedFunctions = EngineDefinedFunctions,
        });
    }

    /// <summary>
    /// エンジンの前処理が置き換える名前 (UE 5.5 の <c>ShaderPreprocessor.cpp</c>)。
    /// </summary>
    /// <remarks>
    /// <c>TEXT("…")</c> は、ShaderPrint の文字列 (<c>FShaderPrintText</c>) に置き換わる。シェーダーの中に定義は無い。
    /// </remarks>
    public static ImmutableHashSet<string> PreprocessorDefinedNames { get; } = ImmutableHashSet.Create(StringComparer.Ordinal, "TEXT");

    /// <summary>
    /// エンジンの前処理が、どのシェーダーにも定義を足す関数 (UE 5.5 の <c>ShaderPreprocessor.cpp</c>)。
    /// </summary>
    /// <remarks>
    /// シェーダーの中の <c>TEXT("…")</c> の文字を引く関数である。<c>ShaderPrintCommon.ush</c> は宣言だけを書く。
    /// </remarks>
    public static ImmutableHashSet<string> EngineDefinedFunctions { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "ShaderPrintGetChar", "ShaderPrintGetOffset", "ShaderPrintGetHash");

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
