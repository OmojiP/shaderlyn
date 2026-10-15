using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// HLSL として 1 回コンパイルされる単位。
/// </summary>
/// <param name="Text">
/// 解析するテキスト。元のファイルと同じ長さ・同じ行構成を持ち、この単位のコードだけを残したもの。
/// このテキスト上の位置は、そのまま元のファイル上の位置になる。
/// </param>
/// <param name="CodeSpan">この単位が占めるファイル上の範囲。同じファイルの中で単位を見分ける鍵の一部。</param>
/// <param name="Name">
/// 同じ範囲から複数の単位を作るときに見分ける名前。Unity のコンピュートシェーダーのカーネル名にあたる。
/// 無ければ <see langword="null"/>。
/// </param>
/// <param name="ExtraMacros">この単位の展開でだけ定義するマクロ。無ければ <see langword="null"/>。</param>
/// <param name="Data">
/// 環境が単位に持たせる情報 (Unity なら、ShaderLab のブロックと Pass)。
/// 解析そのものは中身を見ない。
/// </param>
/// <param name="MarkerSpan">
/// 単位全体についての指摘を出す位置。Unity なら <c>HLSLPROGRAM</c> の語。
/// 無ければ <see langword="null"/> で、単位の先頭の 1 文字を指す。
/// </param>
/// <remarks>
/// <para>
/// <b>環境が作り、解析はこの形だけを見る。</b>
/// Unity は <c>.shader</c> の <c>HLSLPROGRAM</c> と、<c>.compute</c> のカーネルごとに作る。
/// 独自エンジンは、設定に書いたコンパイル単位から作る。
/// </para>
/// <para>
/// <b>切り出さずにマスクする。</b> 部分文字列として切り出すと、位置が元のファイルとずれる。
/// </para>
/// </remarks>
public sealed record HlslCompilationUnit(
    SourceText Text,
    TextSpan CodeSpan,
    string? Name = null,
    ImmutableDictionary<string, string>? ExtraMacros = null,
    object? Data = null,
    TextSpan? MarkerSpan = null)
{
    /// <summary>
    /// 利用者に見せる単位の呼び名 (Unity なら <c>Pass "Forward"</c>)。無ければ <see langword="null"/>。
    /// </summary>
    /// <remarks>ヘッダの指摘に、どこから取り込んだか (<see cref="Core.Diagnostics.IncludeRoute"/>) を添えるのに使う。</remarks>
    public string? DisplayName { get; init; }

    /// <summary>
    /// ソースの外 (設定ファイル) で指定したエントリポイント。無ければ空。
    /// </summary>
    /// <remarks>
    /// ソースの中の指令で指定する環境 (Unity の <c>#pragma vertex</c>) は、ここではなく展開の結果から読む
    /// (<see cref="IHlslEnvironment.ReadEntryPoints"/>)。
    /// </remarks>
    public ImmutableArray<HlslEntryPoint> EntryPoints { get; init; } = [];

    /// <summary>
    /// ソースの外 (設定ファイル) で指定したコンパイルのターゲット (<c>ps_6_6</c>)。無ければ <see langword="null"/>。
    /// </summary>
    /// <remarks>ターゲットで決まるマクロ (DXC の <c>__SHADER_TARGET_STAGE</c> など) を定義するのに使う。</remarks>
    public HlslShaderTarget? Target { get; init; }

    /// <summary>
    /// ソースの外 (設定ファイル) で指定した構成の空間。無ければ <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// ソースの中の指令で宣言する環境 (Unity の <c>#pragma multi_compile</c>) は、ここではなく単位のトークンと展開の結果から読む。
    /// </remarks>
    public VariantSpace? Variants { get; init; }

    /// <summary>
    /// 宣言だけの関数の定義が、ほかの場所 (ほかのライブラリ) にありうるかどうか。既定は <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DXR のライブラリ (<c>lib_6_x</c>) は、ほかのライブラリとリンクしてから使う。宣言だけして定義はほかのライブラリに置く書き方がある。
    /// 立っていれば、宣言に定義が無いこと (HL0311) を報告しない。
    /// </para>
    /// <para>
    /// 環境が決める。汎用の HLSL 環境は、ターゲットがライブラリか、ターゲットが分からない単位で立てる。Unity は立てない (リンクしない)。
    /// </para>
    /// </remarks>
    public bool AllowsExternalDefinitions { get; init; }
}
