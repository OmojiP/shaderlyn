using System.Globalization;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>GLSL のプロファイルの組み合わせ (glslang の <c>EProfile</c> のマスク)。</summary>
[Flags]
internal enum GlslProfileMask
{
    /// <summary>プロファイルの無いデスクトップ (1.50 より前)。glslang の <c>ENoProfile</c>。</summary>
    None = 1,

    /// <summary>デスクトップのコアプロファイル (1.50 以降)。</summary>
    Core = 2,

    /// <summary>デスクトップの互換プロファイル。</summary>
    Compatibility = 4,

    /// <summary>ES。</summary>
    Es = 8,

    /// <summary>ES 以外 (glslang の <c>~EEsProfile</c>)。</summary>
    NotEs = None | Core | Compatibility,

    /// <summary>すべて。</summary>
    All = NotEs | Es,
}

/// <summary>
/// 1 つの GLSL の木について、glslang の <c>TParseVersions</c> の検査 (バージョン・プロファイル・拡張・ステージ・SPIR-V) を行う道具。
/// glslang 16.6.0 の Versions.cpp を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// glslang の検査は <c>profileRequires(loc, EEsProfile, 310, ext, "feature")</c> のように書かれている。
/// 同じ形の呼び出しにしておけば、glslang の検査をそのまま移せる。
/// </para>
/// <para>
/// 拡張の振る舞いが <c>warn</c> のときに glslang が出す警告は出さない (使えるものとして扱う)。
/// </para>
/// </remarks>
internal sealed partial class GlslRequirements
{
    private readonly SyntaxTreeAnalysisContext _context;
    private readonly ShaderCompilation _compilation;
    private readonly HashSet<(string? File, int Start, string Message)> _reported;

    private GlslRequirements(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ShaderSyntaxTree tree,
        GlslLanguageOptions language,
        int version,
        HashSet<(string? File, int Start, string Message)> reported)
    {
        _context = context;
        _compilation = compilation;
        _reported = reported;
        Version = version;
        IsEs = language.Profile == GlslProfile.Es;
        Profile = IsEs ? GlslProfileMask.Es
            : version < 150 ? GlslProfileMask.None
            : language.Profile == GlslProfile.Compatibility ? GlslProfileMask.Compatibility
            : GlslProfileMask.Core;
        Stage = language.Stage;
        Vulkan = language.Vulkan;
        SpirvVersion = language.SpirvVersion;
        AutoMapLocations = language.AutoMapLocations;
        AutoMapBindings = language.AutoMapBindings;
        Extensions = new GlslExtensionTimeline(tree);
    }

    /// <summary>言語のバージョン。</summary>
    public int Version { get; }

    /// <summary>ES か。</summary>
    public bool IsEs { get; }

    /// <summary>プロファイル。</summary>
    public GlslProfileMask Profile { get; }

    /// <summary>ステージ。</summary>
    public GlslShaderStage? Stage { get; }

    /// <summary>Vulkan 向けか。分からなければ <see langword="null"/>。</summary>
    public bool? Vulkan { get; }

    /// <summary>作る SPIR-V のバージョン (1.3 なら 103)。作らなければ 0、分からなければ <see langword="null"/>。</summary>
    public int? SpirvVersion { get; }

    /// <summary>入力と出力の location をコンパイラが決めるか (glslang の <c>--auto-map-locations</c>)。</summary>
    public bool AutoMapLocations { get; }

    /// <summary>uniform と buffer の binding をコンパイラが決めるか (glslang の <c>--auto-map-bindings</c>)。</summary>
    public bool AutoMapBindings { get; }

    /// <summary>位置ごとの拡張の振る舞い。</summary>
    public GlslExtensionTimeline Extensions { get; }

    /// <summary>木から作る。言語のバージョンが分からない木 (取り込まれる断片) なら作らない。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">木。</param>
    /// <param name="reported">報告済みの位置と文。木をまたいで重ねて報告しないために、呼ぶ側で 1 つ持つ。</param>
    /// <returns>道具。作れなければ <see langword="null"/>。</returns>
    public static GlslRequirements? Create(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        HashSet<(string? File, int Start, string Message)> reported)
        => program.Tree.Language is GlslLanguageOptions { Version: { } version } language
            ? new GlslRequirements(context, compilation, program.Tree, language, version, reported)
            : null;

    /// <summary>トークンの時点で拡張を有効にしているか (glslang の <c>extensionTurnedOn</c>)。</summary>
    /// <param name="extension">拡張の名前。</param>
    /// <param name="at">位置のトークン。</param>
    /// <returns>有効なら <see langword="true"/>。</returns>
    public bool IsOn(string extension, ShaderSyntaxToken at) => Extensions.IsOn(extension, at);

    /// <summary>
    /// そのプロファイルなら、バージョンが <paramref name="minVersion"/> 以上か、拡張のどれかを有効にしていなければならない (glslang の <c>profileRequires</c>)。
    /// </summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="profiles">検査するプロファイル。</param>
    /// <param name="minVersion">使えるバージョン。0 ならバージョンでは使えない (拡張が要る)。</param>
    /// <param name="extensions">使えるようにする拡張。</param>
    /// <param name="feature">機能の名前。文に入れる。</param>
    public void ProfileRequires(ShaderSyntaxToken at, GlslProfileMask profiles, int minVersion, IReadOnlyList<string> extensions, string feature)
    {
        if ((Profile & profiles) == 0
            || (minVersion > 0 && Version >= minVersion)
            || extensions.Any(e => IsOn(e, at)))
        {
            return;
        }

        List<string> ways = [];

        if (minVersion > 0)
        {
            ways.Add($"#version {minVersion}{(IsEs ? " es" : string.Empty)} 以降にする");
        }

        if (extensions.Count > 0)
        {
            ways.Add($"拡張 {string.Join("・", extensions)} {(extensions.Count > 1 ? "のどれかを" : "を")}有効にする");
        }

        string hint = ways.Count > 0 ? $"{string.Join("か、", ways)}と使用できます。" : string.Empty;
        Report(GlslRuleDescriptors.NotAvailable, at, $"{feature} は {DescribeVersion()} では使用できません。{hint}");
    }

    /// <summary>そのプロファイルでなければならない (glslang の <c>requireProfile</c>)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="profiles">使えるプロファイル。</param>
    /// <param name="feature">機能の名前。</param>
    public void RequireProfile(ShaderSyntaxToken at, GlslProfileMask profiles, string feature)
    {
        if ((Profile & profiles) == 0)
        {
            Report(GlslRuleDescriptors.NotAvailable, at, $"{feature} は {DescribeProfile()} では使用できません。");
        }
    }

    /// <summary>拡張のどれかを有効にしていなければならない (glslang の <c>requireExtensions</c>)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="extensions">拡張。</param>
    /// <param name="feature">機能の名前。</param>
    /// <returns>有効にしていれば <see langword="true"/>。</returns>
    public bool RequireExtensions(ShaderSyntaxToken at, IReadOnlyList<string> extensions, string feature)
    {
        if (extensions.Count == 0 || extensions.Any(e => IsOn(e, at)))
        {
            return true;
        }

        string message = extensions.Count == 1
            ? $"{feature} を使用するには、拡張 {extensions[0]} を有効にしてください (#extension {extensions[0]} : enable)。"
            : $"{feature} を使用するには、拡張 {string.Join("・", extensions)} のどれかを有効にしてください。";
        ReportWithFix(GlslRuleDescriptors.NotAvailable, at, extensions.Count == 1 ? EnableExtensionFix(at, extensions[0]) : null, message);
        return false;
    }

    /// <summary>そのステージでなければならない (glslang の <c>requireStage</c>)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="stages">使えるステージ。</param>
    /// <param name="feature">機能の名前。</param>
    public void RequireStage(ShaderSyntaxToken at, IReadOnlyCollection<GlslShaderStage> stages, string feature)
    {
        if (Stage is { } stage && !stages.Contains(stage))
        {
            Report(GlslRuleDescriptors.NotAvailableInStage, at, feature, DescribeStage(stage));
        }
    }

    /// <summary>SPIR-V のバージョンが <paramref name="minimum"/> 以上でなければならない (glslang の「requires SPIR-V 1.3」)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="minimum">要る SPIR-V のバージョン (1.3 なら 103)。</param>
    /// <param name="feature">機能の名前。</param>
    /// <remarks>SPIR-V を作らない (0) か、バージョンが分からなければ何もしない。</remarks>
    public void RequireSpirv(ShaderSyntaxToken at, int minimum, string feature)
    {
        if (SpirvVersion is { } current && current > 0 && current < minimum)
        {
            Report(
                GlslRuleDescriptors.NotAvailable,
                at,
                $"{feature} は SPIR-V {DescribeSpirv(minimum)} 以降が必要です (今は {DescribeSpirv(current)})。設定ファイルの glsl: target か glsl: spirv で SPIR-V のバージョンを上げてください。");
        }
    }

    /// <summary>
    /// 拡張を有効にする行 (<c>#extension X : enable</c>) を加える修正を作る。
    /// </summary>
    /// <param name="at">報告する位置のトークン。</param>
    /// <param name="extension">有効にする拡張。</param>
    /// <returns>修正。作れない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 同じファイルの <c>#version</c> の行の次に加える。<c>#version</c> が無ければファイルの先頭に加える。
    /// ファイルにその拡張の <c>#extension</c> がすでにある場合は作らない (後ろの行の <c>disable</c> などで打ち消され、指摘が消えないことがある)。
    /// </remarks>
    private static DiagnosticFix? EnableExtensionFix(ShaderSyntaxToken at, string extension)
    {
        if (!TokenFixes.CanEdit(at) || at.Source is not { } source)
        {
            return null;
        }

        string text = source.Content;

        if (Regex.IsMatch(text, $@"^[ \t]*#[ \t]*extension[ \t]+{Regex.Escape(extension)}\b", RegexOptions.Multiline))
        {
            return null;
        }

        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string directive = $"#extension {extension} : enable";
        Match version = Regex.Match(text, @"^[ \t]*#[ \t]*version\b[^\r\n]*(\r\n|\n)?", RegexOptions.Multiline);

        (int position, string line) = !version.Success
            ? (0, directive + newLine)
            : version.Groups[1].Success
                ? (version.Index + version.Length, directive + newLine)
                : (version.Index + version.Length, newLine + directive);

        return DiagnosticFix.Insert($"'{directive}' を追加する", position, line);
    }

    /// <summary>報告する。同じ位置の同じ文は 1 度だけ。報告できない位置 (利用者のファイルでない) なら報告しない。</summary>
    public void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, params object?[] arguments)
        => ReportWithFix(descriptor, at, null, arguments);

    /// <summary>修正を添えて報告する。同じ位置の同じ文は 1 度だけ。</summary>
    /// <param name="descriptor">ルール。</param>
    /// <param name="at">位置のトークン。</param>
    /// <param name="fix">修正。書き換える位置のファイルが報告の位置と違う場合は添えない。</param>
    /// <param name="arguments">文の引数。</param>
    private void ReportWithFix(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, DiagnosticFix? fix, params object?[] arguments)
    {
        if (!_compilation.IsReportable(at) || at.GetLocation() is not { } location)
        {
            return;
        }

        Diagnostic diagnostic = Diagnostic.Create(descriptor, location, arguments);

        if (fix is not null && ReferenceEquals(at.Source, location.Source))
        {
            diagnostic = diagnostic.WithFix(fix);
        }

        if (_reported.Add((location.FilePath, location.Span.Start, diagnostic.GetMessage())))
        {
            _context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>文に入れる、言語のバージョンとプロファイル。</summary>
    private string DescribeVersion() => IsEs
        ? $"#version {Version} es"
        : Profile switch
        {
            GlslProfileMask.Core => $"#version {Version} core",
            GlslProfileMask.Compatibility => $"#version {Version} compatibility",
            _ => $"#version {Version}",
        };

    /// <summary>文に入れる、プロファイル。</summary>
    private string DescribeProfile() => Profile switch
    {
        GlslProfileMask.Es => "ES",
        GlslProfileMask.Core => "コアプロファイル",
        GlslProfileMask.Compatibility => "互換プロファイル",
        _ => $"プロファイルが存在しない #version {Version}",
    };

    /// <summary>SPIR-V のバージョンを <c>1.3</c> の形にする。</summary>
    private static string DescribeSpirv(int version)
        => string.Create(CultureInfo.InvariantCulture, $"{version / 100}.{version % 100}");

    /// <summary>文に入れる、ステージの名前。</summary>
    public static string DescribeStage(GlslShaderStage stage) => stage switch
    {
        GlslShaderStage.Vertex => "頂点シェーダー",
        GlslShaderStage.TessellationControl => "テッセレーションの制御シェーダー",
        GlslShaderStage.TessellationEvaluation => "テッセレーションの評価シェーダー",
        GlslShaderStage.Geometry => "ジオメトリシェーダー",
        GlslShaderStage.Fragment => "フラグメントシェーダー",
        GlslShaderStage.Compute => "コンピュートシェーダー",
        GlslShaderStage.Task => "タスクシェーダー",
        GlslShaderStage.Mesh => "メッシュシェーダー",
        GlslShaderStage.RayGeneration => "レイの生成のシェーダー",
        GlslShaderStage.Intersection => "交差のシェーダー",
        GlslShaderStage.AnyHit => "任意のヒットのシェーダー",
        GlslShaderStage.ClosestHit => "最も近いヒットのシェーダー",
        GlslShaderStage.Miss => "ミスのシェーダー",
        _ => "呼び出し可能シェーダー",
    };
}
