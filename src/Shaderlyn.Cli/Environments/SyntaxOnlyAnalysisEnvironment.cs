using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// 構文だけを読む環境。意味解析とルールはまだ働かない言語 (GLSL・Godot・Slang・WGSL) のためにある。
/// </summary>
/// <remarks>
/// <para>
/// <b>意味解析のモデル (<see cref="ShaderCompilation"/>) を作らない。</b>
/// ルールはモデルが無ければ何もしないので、出るのは構文エラーだけになる。
/// HLSL のルールを他の言語の木に当てると、型や組み込み関数を知らないために誤検出が出る (GLSL の <c>vec3</c> を宣言されていない名前と読むなど)。
/// 言語の型システム (GL2 など) ができたら、意味解析を働かせる。
/// </para>
/// <para>
/// 読む方法 (<c>parse</c>) が無い言語は、木を作らない (何も指摘しない)。まだ読めない言語で、正しいコードを構文エラーにしないためである。
/// </para>
/// </remarks>
/// <param name="name">環境の名前 (<c>glsl</c> など)。</param>
/// <param name="displayName">表示用の名前。</param>
/// <param name="defaultExtensions">設定が無いときに解析する拡張子。</param>
/// <param name="parse">コードを、展開の設定で読んで木を作る。<see langword="null"/> なら読まない。</param>
internal sealed class SyntaxOnlyAnalysisEnvironment(
    string name,
    string displayName,
    ImmutableArray<string> defaultExtensions,
    Func<SourceText, PreprocessorOptions, ShaderSyntaxTree>? parse) : IAnalysisEnvironment
{
    /// <summary>GLSL の環境の名前。</summary>
    public const string GlslName = "glsl";

    /// <summary>Godot の環境の名前。</summary>
    public const string GodotName = "godot";

    /// <summary>Slang の環境の名前。</summary>
    public const string SlangName = "slang";

    /// <summary>WGSL の環境の名前。</summary>
    public const string WgslName = "wgsl";

    /// <summary>GLSL のステージを表す拡張子と、取り込まれる側の <c>.glsl</c>。</summary>
    public static ImmutableArray<string> GlslExtensions { get; } =
    [
        ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese",
        ".mesh", ".task", ".rgen", ".rint", ".rahit", ".rchit", ".rmiss", ".rcall", ".glsl",
    ];

    /// <summary>GLSL の環境を作る。構文だけを読む (単位 P2 の試作のパーサー)。</summary>
    /// <returns>環境。</returns>
    public static SyntaxOnlyAnalysisEnvironment CreateGlsl()
        => new(GlslName, "GLSL", GlslExtensions, GlslSyntaxTree.Parse);

    /// <summary>Godot の環境を作る。まだ読まない (GD1 で読む)。</summary>
    /// <returns>環境。</returns>
    public static SyntaxOnlyAnalysisEnvironment CreateGodot() => new(GodotName, "Godot", [".gdshader", ".gdshaderinc"], null);

    /// <summary>Slang の環境を作る。まだ読まない (SG1 で読む)。</summary>
    /// <returns>環境。</returns>
    public static SyntaxOnlyAnalysisEnvironment CreateSlang() => new(SlangName, "Slang", [".slang"], null);

    /// <summary>WGSL の環境を作る。まだ読まない (WG1 で読む)。</summary>
    /// <returns>環境。</returns>
    public static SyntaxOnlyAnalysisEnvironment CreateWgsl() => new(WgslName, "WGSL", [".wgsl"], null);

    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <inheritdoc/>
    public string DisplayName { get; } = displayName;

    /// <inheritdoc/>
    public ImmutableArray<string> DefaultExtensions { get; } = defaultExtensions;

    /// <inheritdoc/>
    public Semantics.Environments.IShaderEnvironment? ShaderEnvironment => null;

    /// <inheritdoc/>
    public PreparedFile Prepare(SourceText text, SemanticsOptions? semantics, AnalysisTimings? timings)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (parse is null)
        {
            return new PreparedFile(new AnalysisTarget(text, null, []), [], null);
        }

        long start = AnalysisTimings.Now();
        ShaderSyntaxTree tree = parse(text, CreatePreprocessorOptions(semantics));
        timings?.Add(AnalysisStep.Expand, start);

        return new PreparedFile(
            new AnalysisTarget(text, tree.Root, tree.Diagnostics),
            tree.PreprocessResult.ResolvedIncludes,
            null);
    }

    /// <summary>解析の設定から、展開の設定を作る。</summary>
    /// <param name="semantics">解析の設定。<see langword="null"/> なら取り込みを解決しない。</param>
    /// <returns>展開の設定。</returns>
    /// <remarks>
    /// <para>
    /// <b>取り込みの解決器は、解析の設定のものを使う。</b>
    /// 読んでよい場所が解析対象の根に限られ (<c>IncludeBoundary</c>)、探索パス (<c>--include</c> / <c>include-paths</c>) も効く。
    /// 自前の解決器を作ると、<c>#include "C:/Users/.../.ssh/id_rsa"</c> のような外のファイルまで読めてしまう。
    /// </para>
    /// <para>
    /// 取り込みの使い回し (<see cref="SemanticsOptions.IncludeCache"/>) も共有する。
    /// 使い回しの鍵に言語が入っているので (<see cref="PreprocessorOptions.FingerprintForCache"/>)、HLSL の展開結果を使うことは無い。
    /// </para>
    /// </remarks>
    private static PreprocessorOptions CreatePreprocessorOptions(SemanticsOptions? semantics)
        => semantics is null
            ? new PreprocessorOptions()
            : new PreprocessorOptions
            {
                IncludeResolver = semantics.IncludeResolver,
                IsUserInclude = semantics.IsUserInclude,
                IncludeCache = semantics.IncludeCache,
                TokenCache = semantics.TokenCache,
                PredefinedMacros = semantics.PredefinedMacros ?? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
            };
}
