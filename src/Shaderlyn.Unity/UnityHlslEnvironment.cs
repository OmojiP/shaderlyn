using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Unity;

/// <summary>
/// Unity の環境。
/// </summary>
/// <remarks>
/// <para>
/// 構成の空間は <c>#pragma multi_compile</c> / <c>shader_feature</c> / <c>dynamic_branch</c> から読む (<see cref="ShaderSymbols"/>)。
/// <c>#include_with_pragmas</c> を取り込みとし、<c>#line</c> を使えない指令とする (<see cref="UnityPreprocessorDirectives"/>)。
/// </para>
/// <para>
/// 単位ごとに、<c>#pragma target</c> から <c>SHADER_TARGET</c> を定義し、
/// <c>CGPROGRAM</c> には Unity が自動で取り込むヘッダを先頭に足す。
/// </para>
/// <para>
/// 定義済みマクロの指定が無ければ (<see cref="SemanticsOptions.PredefinedMacros"/> が <see langword="null"/>)、
/// <see cref="UnityOptions.DefaultPredefinedMacros"/> を使う。
/// </para>
/// </remarks>
/// <param name="options">Unity の環境の設定。</param>
public sealed class UnityHlslEnvironment(UnityOptions options) : IHlslEnvironment
{
    /// <summary><c>CGPROGRAM</c> に Unity が自動で取り込むヘッダ。</summary>
    private static readonly string[] CgAutoIncludes = ["HLSLSupport.cginc", "UnityShaderVariables.cginc"];

    /// <summary>サーフェスシェーダーに Unity が自動で取り込むヘッダ。</summary>
    private static readonly string[] SurfaceAutoIncludes = ["UnityCG.cginc", "Lighting.cginc"];

    /// <summary>作った先頭のコード。同じヘッダの組には同じテキストを返し、字句解析の結果を使い回す。</summary>
    private static readonly ConcurrentDictionary<string, SourceText> CgPreludes = new(StringComparer.Ordinal);

    /// <summary>関数名を第 1 引数に取る <c>#pragma</c>。</summary>
    private static readonly FrozenSet<string> EntryPointPragmas = new[]
    {
        "vertex", "fragment", "geometry", "hull", "domain", "surface", "kernel",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly UnityOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc/>
    public string Name => "unity";

    /// <inheritdoc/>
    /// <remarks>
    /// Unity のヘッダが引けない環境でも宣言を取り出せるよう、最小限のマクロを先頭に差し込む
    /// (<see cref="UnityOptions.UseFallbackMacros"/>、<see cref="UnityShaderStubs"/>)。
    /// </remarks>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        return UnityPreprocessorDirectives.Apply(options) with
        {
            PredefinedMacros = semantics.PredefinedMacros ?? UnityOptions.DefaultPredefinedMacros,
            Prelude = _options.UseFallbackMacros ? UnityShaderStubs.Prelude : options.Prelude,
        };
    }

    /// <inheritdoc/>
    public PreprocessorOptions PrepareUnit(
        HlslCompilationUnit unit,
        ImmutableArray<HlslSyntaxToken> tokens,
        PreprocessorOptions options,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(options);

        // カーネルの指定のほうが具体的なので、同じ名前ならそちらが勝つ。
        if (ReadShaderTarget(tokens) is { } shaderTarget
            && unit.ExtraMacros?.ContainsKey("SHADER_TARGET") != true)
        {
            options = options with { PredefinedMacros = options.PredefinedMacros.SetItem("SHADER_TARGET", shaderTarget) };
        }

        if (unit.Data is UnityProgramBlock { Block.Delimiter.StartKeyword: "CGPROGRAM" })
        {
            options = WithCgAutoIncludes(options, filePath, ReadSurfaceLighting(tokens));
        }

        return options;
    }

    /// <inheritdoc/>
    public VariantSpace ReadDeclaredVariants(HlslCompilationUnit unit, ImmutableArray<HlslSyntaxToken> tokens)
        => ShaderSymbols.ReadVariantSpaceFromTokens(tokens);

    /// <inheritdoc/>
    public VariantSpace ReadVariants(HlslCompilationUnit unit, PreprocessResult result) => ShaderSymbols.ReadVariantSpace(result.Pragmas);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// 関数名を第 1 引数に取る <c>#pragma</c> (<c>vertex</c> / <c>fragment</c> / <c>geometry</c> / <c>hull</c> /
    /// <c>domain</c> / <c>surface</c> / <c>kernel</c>) から読む。<c>surface</c> はサーフェスシェーダーの記述関数を指す。
    /// </para>
    /// <para>
    /// <b>コンピュートシェーダーは、このカーネルの <c>#pragma kernel</c> だけを読む。</b>
    /// カーネルごとに別々にコンパイルされ、単位もカーネルごとに 1 つある。
    /// この単位はこのカーネルの構成でしか展開していないので、他のカーネルの入口がここに見えていなくても当たり前である。
    /// </para>
    /// </remarks>
    /// <inheritdoc/>
    /// <remarks>
    /// 下線で始まり、小文字を含まない名前 (<c>_NORMALMAP</c>)。
    /// Unity のシンボルはこの慣習に従っており、プラットフォーム判定 (<c>SHADER_API_D3D11</c>) や内部の名前と取り違えずに済む。
    /// </remarks>
    public bool LooksLikeVariantSymbol(string name)
        => name is { Length: > 1 }
           && name[0] == '_'
           && !name.Any(char.IsAsciiLetterLower);

    /// <inheritdoc/>
    public ImmutableArray<HlslEntryPoint> ReadEntryPoints(HlslCompilationUnit unit, PreprocessResult result)
    {
        ArgumentNullException.ThrowIfNull(unit);

        ImmutableArray<HlslEntryPoint>.Builder entryPoints = ImmutableArray.CreateBuilder<HlslEntryPoint>();

        foreach (PragmaDirective pragma in result.Pragmas)
        {
            if (!EntryPointPragmas.Contains(pragma.Name)
                || pragma.Arguments.FirstOrDefault() is not { Kind: HlslSyntaxKind.IdentifierToken } name)
            {
                continue;
            }

            if (unit.Name is { } kernel
                && string.Equals(pragma.Name, "kernel", StringComparison.Ordinal)
                && !string.Equals(name.Text, kernel, StringComparison.Ordinal))
            {
                continue;
            }

            entryPoints.Add(new HlslEntryPoint(name.Text, "#pragma " + pragma.Name, name.GetLocation()));
        }

        return entryPoints.ToImmutable();
    }

    /// <summary>
    /// <c>#pragma surface 関数 照明モデル</c> から照明モデルの名前を読む。
    /// </summary>
    /// <param name="tokens">字句解析しただけのトークン列。</param>
    /// <returns>照明モデルの名前。サーフェスシェーダーでなければ <see langword="null"/>。</returns>
    private static string? ReadSurfaceLighting(ImmutableArray<HlslSyntaxToken> tokens)
    {
        for (int i = 0; i + 4 < tokens.Length; i++)
        {
            if (tokens[i].Kind == HlslSyntaxKind.HashToken
                && tokens[i].IsAtLineStart
                && tokens[i + 1].Text == "pragma"
                && tokens[i + 2].Text == "surface"
                && !tokens[i + 3].IsAtLineStart
                && !tokens[i + 4].IsAtLineStart)
            {
                return tokens[i + 4].Text;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>#pragma target</c> から、Unity が定義する <c>SHADER_TARGET</c> の値を読む。
    /// </summary>
    /// <param name="tokens">字句解析しただけのトークン列。</param>
    /// <returns>
    /// <c>SHADER_TARGET</c> の値 (<c>#pragma target 3.5</c> なら <c>35</c>)。
    /// 指定が無ければ <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>ヘッダは <c>SHADER_TARGET</c> で経路を選ぶ。</b>
    /// <c>HLSLSupport.cginc</c> の <c>#if (SHADER_TARGET &lt; 30)</c> や
    /// <c>UNITY_SM40_PLUS_PLATFORM</c> がそうである。既定の 45 のまま読むと、
    /// <c>#pragma target 2.0</c> のシェーダーでは決して通らない経路を検査する。
    /// </para>
    /// <para>
    /// 複数書かれていれば最も高いものを使う。<c>HLSLINCLUDE</c> と <c>HLSLPROGRAM</c> の両方に書く形がある。
    /// </para>
    /// <para>
    /// <b>指定が無いときは何もしない。</b> Unity の既定値や、<c>#pragma geometry</c> などが
    /// 引き上げる値は、この環境で確かめられないので推測しない。
    /// </para>
    /// </remarks>
    private static string? ReadShaderTarget(ImmutableArray<HlslSyntaxToken> tokens)
    {
        int best = -1;

        for (int i = 0; i + 3 < tokens.Length; i++)
        {
            if (tokens[i].Kind != HlslSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || tokens[i + 1].Text != "pragma"
                || tokens[i + 2].Text != "target"
                || tokens[i + 3].IsAtLineStart)
            {
                continue;
            }

            // "4.5" は 1 つの数値トークンになる。"4.5" 以外の書き方 ("4.5 compute" の後半など) は無視する。
            string text = tokens[i + 3].Text;
            int dot = text.IndexOf('.');

            if (dot > 0
                && dot + 1 < text.Length
                && int.TryParse(text.AsSpan(0, dot), out int major)
                && char.IsAsciiDigit(text[dot + 1]))
            {
                best = Math.Max(best, (major * 10) + (text[dot + 1] - '0'));
            }
        }

        return best < 0 ? null : best.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>CGPROGRAM</c> に Unity が自動で取り込むヘッダを、先頭のコードに足した複製を返す。
    /// </summary>
    /// <param name="options">元の設定。</param>
    /// <param name="filePath">解析しているファイルのパス。取り込むヘッダを探す基点になる。</param>
    /// <param name="surfaceLighting"><c>#pragma surface</c> の照明モデル。サーフェスシェーダーでなければ <see langword="null"/>。</param>
    /// <returns>足した設定。ヘッダが 1 つでも見つからなければ元の設定。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>CGPROGRAM</c> は、書いていないヘッダも取り込む。</b>
    /// Unity のマニュアル (「HLSL in Unity」) によれば、<c>CGPROGRAM</c> には
    /// <c>HLSLSupport.cginc</c> と <c>UnityShaderVariables.cginc</c> が自動で取り込まれる。
    /// 取り込まないと、<c>fixed4</c> や <c>unity_ObjectToWorld</c> を使うだけのシェーダーで、
    /// 宣言されていない名前を読むことになる。<c>HLSLPROGRAM</c> には取り込まれない。
    /// </para>
    /// <para>
    /// <b>見つからなければ何もしない。</b> Unity が無い環境で足すと、利用者が書いていない
    /// <c>#include</c> が解決できず、依存関係が不完全だとして検査を見送ることになる。
    /// </para>
    /// <para>
    /// 見つけたファイルは絶対パスで取り込む。先頭のコードは実在しないパスを持つので、
    /// そこからの相対では探せない。
    /// </para>
    /// <para>
    /// <b>サーフェスシェーダーは、さらに照明のヘッダも取り込む。</b>
    /// Unity の「Standard Surface Shader」の雛形は、<c>#include</c> を 1 行も書かずに
    /// <c>SurfaceOutputStandard</c> を使う。<c>Lambert</c> の照明関数 <c>LightingLambert</c> も書かずに使う。
    /// 生成されるコードは <c>UnityCG.cginc</c> と <c>Lighting.cginc</c> を取り込み、
    /// <c>Standard</c> / <c>StandardSpecular</c> ではさらに <c>UnityPBSLighting.cginc</c> を取り込む。
    /// </para>
    /// </remarks>
    private static PreprocessorOptions WithCgAutoIncludes(
        PreprocessorOptions options,
        string filePath,
        string? surfaceLighting)
    {
        if (options.IncludeResolver is not { } resolver)
        {
            return options;
        }

        IEnumerable<string> names = CgAutoIncludes;

        if (surfaceLighting is not null)
        {
            names = names.Concat(SurfaceAutoIncludes);

            if (surfaceLighting.StartsWith("Standard", StringComparison.Ordinal))
            {
                names = names.Append("UnityPBSLighting.cginc");
            }
        }

        StringBuilder includes = new();

        foreach (string name in names)
        {
            if (!resolver.TryResolve(name, filePath, out SourceText? header) || string.IsNullOrEmpty(header.FilePath))
            {
                return options;
            }

            includes.Append("#include \"").Append(header.FilePath.Replace('\\', '/')).Append("\"\n");
        }

        string key = (options.Prelude?.ToString() ?? string.Empty) + "\n" + includes;
        SourceText prelude = CgPreludes.GetOrAdd(
            key,
            static (k, path) => SourceText.From(k, path),
            options.Prelude?.FilePath ?? "<shaderlyn CGPROGRAM>");

        return options with { Prelude = prelude };
    }
}
