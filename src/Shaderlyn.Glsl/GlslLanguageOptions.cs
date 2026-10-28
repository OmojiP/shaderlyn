using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Glsl.Parsing;

namespace Shaderlyn.Glsl;

/// <summary>GLSL のプロファイル (<c>#version</c> の 2 つ目の語)。</summary>
internal enum GlslProfile
{
    /// <summary>デスクトップの GLSL のコアプロファイル。1.50 より前はプロファイルが無いが、これで表す。</summary>
    Core,

    /// <summary>デスクトップの GLSL の互換プロファイル (<c>compatibility</c>)。</summary>
    Compatibility,

    /// <summary>GLSL ES (<c>es</c>。<c>#version 100</c> も ES)。</summary>
    Es,
}

/// <summary>GLSL のシェーダーのステージ。ファイルの拡張子で決まる (glslang と同じ)。</summary>
internal enum GlslShaderStage
{
    /// <summary>頂点シェーダー (<c>.vert</c>)。</summary>
    Vertex,

    /// <summary>テッセレーションの制御シェーダー (<c>.tesc</c>)。</summary>
    TessellationControl,

    /// <summary>テッセレーションの評価シェーダー (<c>.tese</c>)。</summary>
    TessellationEvaluation,

    /// <summary>ジオメトリシェーダー (<c>.geom</c>)。</summary>
    Geometry,

    /// <summary>フラグメントシェーダー (<c>.frag</c>)。</summary>
    Fragment,

    /// <summary>コンピュートシェーダー (<c>.comp</c>)。</summary>
    Compute,

    /// <summary>タスクシェーダー (<c>.task</c>)。</summary>
    Task,

    /// <summary>メッシュシェーダー (<c>.mesh</c>)。</summary>
    Mesh,

    /// <summary>レイの生成 (<c>.rgen</c>)。</summary>
    RayGeneration,

    /// <summary>交差 (<c>.rint</c>)。</summary>
    Intersection,

    /// <summary>任意のヒット (<c>.rahit</c>)。</summary>
    AnyHit,

    /// <summary>最も近いヒット (<c>.rchit</c>)。</summary>
    ClosestHit,

    /// <summary>ミス (<c>.rmiss</c>)。</summary>
    Miss,

    /// <summary>呼び出し可能 (<c>.rcall</c>)。</summary>
    Callable,
}

/// <summary>
/// GLSL の言語の設定 (<see cref="PreprocessorOptions.Language"/>)。木を読んだバージョン・プロファイル・ステージを持つ。
/// </summary>
/// <param name="Version">言語のバージョン (<c>#version</c> の数。<c>450</c> など)。分からなければ <see langword="null"/>。</param>
/// <param name="Profile">プロファイル。</param>
/// <param name="Stage">ステージ。取り込まれる断片 (<c>.glsl</c>) など、分からなければ <see langword="null"/>。</param>
/// <remarks>
/// 型システムと組み込みの表 (GL2a / GL2b) は、使える型と関数をここから決める。
/// </remarks>
internal sealed record GlslLanguageOptions(int? Version, GlslProfile Profile, GlslShaderStage? Stage) : ShaderLanguageOptions
{
    /// <summary>既定の設定。バージョンもステージも分からない。</summary>
    public static GlslLanguageOptions Default { get; } = new(null, GlslProfile.Core, null);

    /// <summary>
    /// <c>#version</c> が無いファイルのバージョン。glslang は ES の 100 として読む (glslang 16.6.0 で確かめた)。
    /// </summary>
    /// <remarks>GLSL 4.60 の仕様書は 1.10 とするが、実際にコンパイルする glslang に合わせる。</remarks>
    public const int VersionWithoutDirective = 100;

    /// <inheritdoc/>
    public override string DescribeUnavailable() => "GLSL";

    /// <inheritdoc/>
    public override bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens) => GlslParser.IsCompleteUnits(tokens);

    /// <summary>
    /// コンパイラが定義するマクロを作る。
    /// </summary>
    /// <returns>マクロ。バージョンが分からなければ空。</returns>
    /// <remarks>
    /// glslang 16.6.0 で、<c>#ifndef</c> と <c>#error</c> で確かめた。
    /// <list type="bullet">
    ///   <item><description><c>__VERSION__</c> はバージョン、<c>GL_FRAGMENT_PRECISION_HIGH</c> はどのステージでも 1</description></item>
    ///   <item><description>ES (<c>#version 100</c>、<c>300 es</c> など) は <c>GL_ES</c> を 1 にする。<c>GL_es_profile</c> は定義しない</description></item>
    ///   <item><description>デスクトップの 1.50 以降は <c>GL_core_profile</c> を 1 にし、互換プロファイルなら <c>GL_compatibility_profile</c> も 1 にする</description></item>
    /// </list>
    /// <c>VULKAN</c> (<c>-V</c>) と <c>GL_SPIRV</c> (<c>-G</c>) はコンパイルの指定で決まるので、ここでは定義しない。
    /// 拡張のマクロ (<c>GL_EXT_…</c>) も、まだ定義しない (組み込みの表と一緒に足す)。
    /// </remarks>
    public ImmutableDictionary<string, string> CreateCompilerMacros()
    {
        ImmutableDictionary<string, string> macros = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

        if (Version is not { } version)
        {
            return macros;
        }

        macros = macros
            .SetItem("__VERSION__", version.ToString(CultureInfo.InvariantCulture))
            .SetItem("GL_FRAGMENT_PRECISION_HIGH", "1");

        if (Profile == GlslProfile.Es)
        {
            return macros.SetItem("GL_ES", "1");
        }

        if (version >= 150)
        {
            macros = macros.SetItem("GL_core_profile", "1");

            if (Profile == GlslProfile.Compatibility)
            {
                macros = macros.SetItem("GL_compatibility_profile", "1");
            }
        }

        return macros;
    }

    /// <summary>
    /// 展開の前のトークンから、<c>#version</c> の指定を読む。
    /// </summary>
    /// <param name="tokens">ファイルを字句解析しただけのトークン。</param>
    /// <param name="version">バージョン。</param>
    /// <param name="profile">プロファイル。書いていなければ、100 と 300 は ES、ほかはコア。</param>
    /// <returns><c>#version</c> を読めれば <see langword="true"/>。</returns>
    /// <remarks>最初の <c>#version</c> だけを読む。2 つ目以降はコンパイラが誤りにする。</remarks>
    public static bool TryReadVersion(ImmutableArray<ShaderSyntaxToken> tokens, out int version, out GlslProfile profile)
    {
        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || tokens[i + 1].Text != "version"
                || tokens[i + 2].IsAtLineStart)
            {
                continue;
            }

            if (!int.TryParse(tokens[i + 2].Text, NumberStyles.None, CultureInfo.InvariantCulture, out version))
            {
                break;
            }

            string? written = i + 3 < tokens.Length && !tokens[i + 3].IsAtLineStart ? tokens[i + 3].Text : null;
            profile = written switch
            {
                "es" => GlslProfile.Es,
                "compatibility" => GlslProfile.Compatibility,
                "core" => GlslProfile.Core,
                _ => version is 100 or 300 ? GlslProfile.Es : GlslProfile.Core,
            };

            return true;
        }

        version = 0;
        profile = GlslProfile.Core;
        return false;
    }

    /// <summary>ファイルの名前から、ステージを決める。</summary>
    /// <param name="filePath">ファイルのパス。</param>
    /// <returns>ステージ。<c>.glsl</c> などステージを表さない拡張子なら <see langword="null"/>。</returns>
    /// <remarks>
    /// glslang と同じく、<c>.glsl</c> の前にステージの拡張子があれば (<c>shadow.vert.glsl</c>) そのステージにする。
    /// </remarks>
    public static GlslShaderStage? StageFromFileName(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        string extension = Path.GetExtension(filePath);

        if (extension.Equals(".glsl", StringComparison.OrdinalIgnoreCase))
        {
            extension = Path.GetExtension(Path.GetFileNameWithoutExtension(filePath));
        }

        return extension.ToLowerInvariant() switch
        {
            ".vert" => GlslShaderStage.Vertex,
            ".tesc" => GlslShaderStage.TessellationControl,
            ".tese" => GlslShaderStage.TessellationEvaluation,
            ".geom" => GlslShaderStage.Geometry,
            ".frag" => GlslShaderStage.Fragment,
            ".comp" => GlslShaderStage.Compute,
            ".task" => GlslShaderStage.Task,
            ".mesh" => GlslShaderStage.Mesh,
            ".rgen" => GlslShaderStage.RayGeneration,
            ".rint" => GlslShaderStage.Intersection,
            ".rahit" => GlslShaderStage.AnyHit,
            ".rchit" => GlslShaderStage.ClosestHit,
            ".rmiss" => GlslShaderStage.Miss,
            ".rcall" => GlslShaderStage.Callable,
            _ => null,
        };
    }
}
