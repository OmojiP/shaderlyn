using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;
using Shaderlyn.Unreal;

namespace Shaderlyn.Testing;

/// <summary>
/// 自作ルールをシェーダーのソースへ当てて、出た指摘を取り出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>ルールを書いたら必ずテストを書く。</b>
/// 静的解析の誤検出は、そのルールだけでなくツール全体の信用を落とす。
/// 「出ること」だけでなく<b>「正しいコードに出ないこと」</b>を必ず確かめること。
/// </para>
/// <para>
/// <b>Roslyn との違い。</b>
/// Roslyn では <c>Microsoft.CodeAnalysis.Testing</c> を使い、
/// ソースへ <c>[|…|]</c> の印を書き込んで期待値と突き合わせる。
/// 表明の形まで枠組みが決めるため、
/// 「1 件も出ないこと」「メッセージの中身」のような素朴な確認が書きにくい。
/// </para>
/// <para>
/// ここでは<b>診断を返すだけ</b>にしてある。
/// 表明は利用者のテスト基盤 (xUnit でも NUnit でも) で書けばよい。
/// この型が引き受けるのは、セマンティックモデルの組み立てという間違えやすい手順だけである。
/// </para>
/// <example>
/// <code>
/// ImmutableArray&lt;Diagnostic&gt; diagnostics = new ShaderRuleVerifier(new MyAnalyzer())
///     .AddInclude("MyLib.hlsl", "float3 Tint(float3 c) { return c; }")
///     .Analyze(source);
///
/// Assert.Equal("MY0001", Assert.Single(diagnostics).Id);
/// </code>
/// </example>
/// </remarks>
public sealed class ShaderRuleVerifier
{
    private readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private readonly Dictionary<string, string> _includes = new(StringComparer.OrdinalIgnoreCase);
    private AnalyzerOptions _options = AnalyzerOptions.Default;
    private ImmutableDictionary<string, string>? _predefinedMacros;
    private string? _environment;

    /// <summary>
    /// 検査するアナライザを指定して生成する。
    /// </summary>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <remarks>
    /// <b>組み込みルールは走らない。</b>
    /// 渡したアナライザだけが実行されるので、
    /// 自作ルールのテストが組み込みルールの指摘に埋もれることがない。
    /// </remarks>
    public ShaderRuleVerifier(params DiagnosticAnalyzer[] analyzers)
    {
        ArgumentNullException.ThrowIfNull(analyzers);
        _analyzers = [.. analyzers];
    }

    /// <summary>
    /// <c>#include</c> で引けるファイルを足す。
    /// </summary>
    /// <param name="path"><c>#include</c> に書くパス。</param>
    /// <param name="content">そのファイルの中身。</param>
    /// <returns>自分自身。続けて呼べる。</returns>
    /// <remarks>
    /// <b>ディスクは読まない。</b>
    /// テストが Unity のインストール状態に依存すると、
    /// 手元では通って CI では落ちる (あるいはその逆) という形で壊れる。
    /// </remarks>
    public ShaderRuleVerifier AddInclude(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);

        _includes[path] = content;
        return this;
    }

    /// <summary>
    /// 実行時設定を差し替える。
    /// </summary>
    /// <param name="options">重要度の上書きやルールごとの設定。</param>
    /// <returns>自分自身。続けて呼べる。</returns>
    /// <remarks>設定ファイルからルールを無効にできることを確かめる場合に使う。</remarks>
    public ShaderRuleVerifier WithOptions(AnalyzerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        return this;
    }

    /// <summary>
    /// 解析開始時に定義済みとするマクロを指定する。
    /// </summary>
    /// <param name="macros">名前から値への対応。</param>
    /// <returns>自分自身。続けて呼べる。</returns>
    /// <remarks>
    /// 指定しない場合は既定のマクロ (<see cref="UnityOptions.DefaultPredefinedMacros"/>) を使う。
    /// </remarks>
    public ShaderRuleVerifier WithPredefinedMacros(IReadOnlyDictionary<string, string> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        _predefinedMacros = macros.ToImmutableDictionary(StringComparer.Ordinal);
        return this;
    }

    /// <summary>
    /// 解析する環境を指定する。
    /// </summary>
    /// <param name="environment">
    /// 設定ファイルの <c>environment</c> と同じ名前: <c>unity</c> / <c>hlsl</c> (汎用の HLSL) / <c>unreal</c> / <c>glsl</c> / <c>godot</c> / <c>slang</c> / <c>wgsl</c>。
    /// </param>
    /// <returns>自分自身。続けて呼べる。</returns>
    /// <remarks>
    /// <para>
    /// 指定しない場合は、<see cref="Analyze"/> に渡したパスの拡張子で決める
    /// (<c>.usf</c>・<c>.ush</c> は Unreal Engine、<c>.slang</c> は Slang、<c>.vert</c>・<c>.frag</c>・<c>.glsl</c> などは GLSL、
    /// <c>.gdshader</c> は Godot、<c>.wgsl</c> は WGSL、それ以外は Unity)。設定ファイルを置かずに CLI でファイルを直接指定した場合と同じ環境である。
    /// <c>.mesh</c> は Unity のメッシュのアセットと同じ拡張子なので、CLI と同じく Unity になる。GLSL として確かめる場合は <c>glsl</c> を指定する。
    /// Unreal Engine は、C++ のシェーダーの登録を読まず、エンジンの設定も既定のまま解析する (取り込むヘッダは <see cref="AddInclude"/> で与える)。
    /// <c>.hlsl</c> は Unity の HLSL として扱うので、汎用の HLSL として確かめる場合は <c>hlsl</c> を指定する。
    /// </para>
    /// <para>
    /// Unity 以外の環境では、ファイル全体を 1 つのコンパイル単位にし、定義済みのマクロは置かない
    /// (<see cref="WithPredefinedMacros"/> で指定できる)。CLI で設定ファイルを置かずに解析した場合と同じである。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">知らない環境の名前。</exception>
    public ShaderRuleVerifier WithEnvironment(string environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment is not ("unity" or "hlsl" or "unreal" or "glsl" or "godot" or "slang" or "wgsl"))
        {
            throw new ArgumentException($"環境 '{environment}' は不明です。unity・hlsl・unreal・glsl・godot・slang・wgsl のどれかを指定してください。", nameof(environment));
        }

        _environment = environment;
        return this;
    }

    /// <summary>
    /// ソースを解析して、出た指摘を返す。
    /// </summary>
    /// <param name="source">解析するソース。</param>
    /// <param name="filePath">
    /// そのソースのパス。拡張子で ShaderLab と HLSL 単体を切り替える。
    /// 省略すると <c>Assets/Test.shader</c> として扱う。
    /// </param>
    /// <returns>ソースコード上の出現順に並んだ指摘。</returns>
    /// <remarks>
    /// <para>
    /// <c>.compute</c> や <c>.hlsl</c> を渡せば HLSL 単体として解析される。
    /// 判定は本体と同じ <see cref="ShaderSourceKinds"/> を通す。
    /// </para>
    /// <para>
    /// 返るのは<b>抑制コメントを適用したあと</b>の指摘である。
    /// 実際に利用者の目に入るものと同じものを見ることになる。
    /// </para>
    /// </remarks>
    public ImmutableArray<Diagnostic> Analyze(string source, string filePath = "Assets/Test.shader")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filePath);

        SourceText text = SourceText.From(source, filePath);
        string environment = _environment ?? EnvironmentOf(filePath);

        SemanticsOptions semantics = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(_includes),
            PredefinedMacros = _predefinedMacros ?? (environment == "unity" ? UnityOptions.DefaultPredefinedMacros : ImmutableDictionary<string, string>.Empty),
        };

        ShaderCompilation compilation = environment switch
        {
            "unity" when ShaderSourceKinds.FromPath(filePath) == ShaderSourceKind.Hlsl => UnityCompilation.CreateForHlsl(text, semantics),
            "unity" => UnityCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), semantics),
            "glsl" => Create(new GlslEnvironment(), GlslEnvironment.WholeFile(text)),
            "godot" => Create(new GodotEnvironment(), GodotEnvironment.WholeFile(text)),
            "slang" => Create(new SlangEnvironment(), SlangEnvironment.WholeFile(text)),
            "wgsl" => Create(new WgslEnvironment(), WgslEnvironment.WholeFile(text)),
            "unreal" => Create(UnrealOptions.Default.CreateEnvironment(), new ShaderCompilationUnit(text, new TextSpan(0, text.Length))),
            _ => Create(new HlslEnvironment(), new ShaderCompilationUnit(text, new TextSpan(0, text.Length))),
        };

        return new AnalyzerDriver(_analyzers, _options).Analyze(compilation.CreateAnalysisTarget());

        ShaderCompilation Create(IShaderEnvironment shaderEnvironment, ShaderCompilationUnit unit)
            => ShaderCompilation.Create(text, [unit], text, shaderEnvironment, ShaderSourceInfo.None, semantics);
    }

    /// <summary>拡張子から環境を決める (<see cref="WithEnvironment"/>)。</summary>
    /// <remarks>
    /// 設定ファイルを置かずに CLI でファイルを直接指定したときと同じ環境にする (テストで確かめている)。
    /// <c>.mesh</c> は Unity のメッシュのアセットと同じ拡張子なので、CLI と同じく GLSL にしない。
    /// </remarks>
    private static string EnvironmentOf(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".slang" => "slang",
            ".wgsl" => "wgsl",
            ".gdshader" or ".gdshaderinc" => "godot",
            ".usf" or ".ush" => "unreal",
            ".glsl" or ".vert" or ".frag" or ".comp" or ".geom" or ".tesc" or ".tese" or ".task"
                or ".rgen" or ".rint" or ".rahit" or ".rchit" or ".rmiss" or ".rcall" => "glsl",
            _ => "unity",
        };
    }

    /// <summary>
    /// 指摘を人が読める 1 行ずつの文字列にする。
    /// </summary>
    /// <param name="diagnostics">対象の指摘。</param>
    /// <returns>1 件 1 行の文字列。</returns>
    /// <remarks>
    /// <b>表明が落ちたときに何が出ていたかを見せるためのものである。</b>
    /// <c>Assert.Empty(diagnostics)</c> だけでは、
    /// 落ちたときに「何が出たのか」が分からず調べ直すことになる。
    /// <c>Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics))</c> と書くこと。
    /// </remarks>
    public static string Describe(ImmutableArray<Diagnostic> diagnostics)
        => diagnostics.IsDefaultOrEmpty
            ? "(指摘なし)"
            : string.Join(
                Environment.NewLine,
                diagnostics.Select(d => $"  {d.Location.LineSpan.Start} {d.Id}: {d.GetMessage()}"));
}
