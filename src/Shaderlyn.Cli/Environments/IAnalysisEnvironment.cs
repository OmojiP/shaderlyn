using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// 1 ファイルを解析単位にした結果。
/// </summary>
/// <param name="Target">アナライザに渡す解析単位。</param>
/// <param name="IncludePaths">解析中に取り込んだファイルのパス。結果のキャッシュの鍵に使う。</param>
/// <param name="Compilation">
/// HLSL のセマンティックモデル。意味解析をしなかった場合と、HLSL ではない言語の環境では <see langword="null"/>。
/// </param>
internal sealed record PreparedFile(
    AnalysisTarget Target,
    ImmutableArray<string> IncludePaths,
    ShaderCompilation? Compilation);

/// <summary>
/// 解析する環境 (Unity、Unreal、独自エンジン)。
/// </summary>
/// <remarks>
/// <para>
/// <b>CLI と言語サーバーは、ファイルをこの口を通して解析単位にする。</b>
/// どの言語として読むか、どう単位に切り出すか、どの定義済みマクロで展開するかは、環境が決める。
/// </para>
/// <para>
/// <b>返すのは言語に依らない解析単位 (<see cref="AnalysisTarget"/>) である。</b>
/// HLSL ではない言語 (Godot のシェーダー言語) の環境を足すときも、この口のまま入る。
/// </para>
/// <para>
/// 今は内部の口である。独自エンジンのホストに公開する形は、そのとき要るものに合わせて決める。
/// </para>
/// </remarks>
internal interface IAnalysisEnvironment
{
    /// <summary>設定ファイルとコマンドラインで指定する名前 (<c>unity</c> など)。</summary>
    string Name { get; }

    /// <summary>画面に出す名前。</summary>
    string DisplayName { get; }

    /// <summary>フォルダーを指定したときに探す拡張子。</summary>
    ImmutableArray<string> DefaultExtensions { get; }

    /// <summary>
    /// 意味解析に渡す HLSL の環境。HLSL ではない言語の環境では <see langword="null"/>。
    /// </summary>
    /// <remarks>言語サーバーが、解析せずに宣言されたシンボルを読むのに使う。</remarks>
    Semantics.Environments.IHlslEnvironment? HlslEnvironment { get; }

    /// <summary>
    /// 1 ファイルを解析単位にする。
    /// </summary>
    /// <param name="text">解析するファイル。</param>
    /// <param name="semantics">意味解析の設定。<see langword="null"/> なら構文だけを見る。</param>
    /// <param name="timings">段ごとの時間を受け取る先。<see langword="null"/> なら測らない。</param>
    /// <returns>解析単位。</returns>
    PreparedFile Prepare(SourceText text, SemanticsOptions? semantics, AnalysisTimings? timings);
}
