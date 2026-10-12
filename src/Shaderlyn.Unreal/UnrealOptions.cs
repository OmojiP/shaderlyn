using System.Collections.Immutable;
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

    /// <summary>そのほかの割り当て (<c>/Plugin/MyPlugin</c> → プラグインの <c>Shaders</c>)。</summary>
    public ImmutableDictionary<string, string> VirtualPaths { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>コンパイラ。既定は DXC。設定の <c>hlsl: compiler</c> で変える。</summary>
    /// <remarks>Unreal Engine がどのコンパイラ・版でコンパイルするかは実測していない。既定は今までどおり DXC 2018 としている。</remarks>
    public HlslCompiler Compiler { get; init; } = HlslCompiler.Dxc;

    /// <summary>HLSL の言語のバージョン。既定は 2018。HLSL 2021 で書いたシェーダーは設定の <c>hlsl: version</c> で 2021 にする。</summary>
    public HlslLanguageVersion LanguageVersion { get; init; } = HlslLanguageVersion.Hlsl2018;

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
        }

        return mappings;
    }

    /// <summary>
    /// この設定の、意味解析に渡す環境を作る。
    /// </summary>
    /// <param name="variants">どの単位にも共通する構成の空間 (パーミュテーション)。</param>
    /// <returns>環境。</returns>
    public HlslEnvironment CreateEnvironment(VariantSpace? variants = null)
        => new(new HlslEnvironmentOptions
        {
            Name = EnvironmentName,
            Compiler = Compiler,
            LanguageVersion = LanguageVersion,
            Enable16BitTypes = Enable16BitTypes,
            Variants = variants ?? VariantSpace.Empty,
            GeneratedIncludePrefixes = GeneratedIncludePrefixes,
            PredefinedMacros = PredefinedMacros,
        });

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
