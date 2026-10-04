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
    /// 値はエンジンのシェーダーコンパイラ (ShaderFormatD3D) が定義するものに合わせた。エンジン本体では確かめていない。
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
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>既定の設定。</summary>
    public static UnrealOptions Default { get; } = new();

    /// <summary>エンジンのフォルダー (<c>…/UE_5.4/Engine</c>)。<c>/Engine</c> を <c>Shaders</c> の下に割り当てる。</summary>
    public string? EngineDirectory { get; init; }

    /// <summary>プロジェクトのフォルダー。<c>/Project</c> を <c>Shaders</c> の下に割り当てる。</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>そのほかの割り当て (<c>/Plugin/MyPlugin</c> → プラグインの <c>Shaders</c>)。</summary>
    public ImmutableDictionary<string, string> VirtualPaths { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>HLSL の言語のバージョン。既定は 2018 (エンジンが <c>-HV 2021</c> を付けるのは、そう指定したシェーダーだけ)。</summary>
    public HlslLanguageVersion LanguageVersion { get; init; } = HlslLanguageVersion.Hlsl2018;

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
            LanguageVersion = LanguageVersion,
            Variants = variants ?? VariantSpace.Empty,
            GeneratedIncludePrefixes = GeneratedIncludePrefixes,
            PredefinedMacros = DefaultPredefinedMacros,
        });
}
