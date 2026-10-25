using System.Collections.Immutable;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Cli;

/// <summary>
/// 自前のホスト (自作ルール入りの CLI や言語サーバー) が足す、独自エンジンの環境。
/// </summary>
/// <remarks>
/// <para>
/// <b>独自エンジンの決まりは <see cref="IHlslEnvironment"/> (HLSL を使う環境) に書く。</b>
/// 構成の軸をどこから読むか (エンジン独自の <c>#pragma</c> など)、エントリポイント、定義済みマクロ、
/// ビルドのときに生成される取り込みを、環境が解析に教える。字句解析・プリプロセッサ・型・ルールはそのまま使える。
/// </para>
/// <para>
/// 設定ファイルの <c>environment</c> にこの名前を書くと選ばれる。コンパイル単位と構成の軸は、
/// 汎用の HLSL 環境と同じく設定ファイルの <c>units:</c> と <c>symbols:</c> からも作る。
/// 単位に当てはまらないファイルは、ファイル全体を 1 つの単位にする。
/// 設定から作った構成の空間とエントリポイントは単位 (<see cref="ShaderCompilationUnit.Variants"/> /
/// <see cref="ShaderCompilationUnit.EntryPoints"/>) に入る。使うかどうかは環境が決める。
/// </para>
/// <para>
/// Native AOT ではプラグインを読み込めないので、環境は自作ルールと同じく、ホストを作るときに渡す。
/// </para>
/// </remarks>
/// <param name="Name">設定ファイルとコマンドラインで指定する名前。組み込みの名前 (<c>unity</c> / <c>hlsl</c> / <c>unreal</c>) は使えない。</param>
/// <param name="DisplayName">画面に出す名前。</param>
/// <param name="DefaultExtensions">フォルダーを指定したときに探す拡張子 (<c>.fx</c> など、<c>.</c> から書く)。</param>
/// <param name="CreateEnvironment">意味解析に渡す環境を作る。解析を始めるときに 1 回呼ぶ。</param>
public sealed record HostEnvironment(
    string Name,
    string DisplayName,
    ImmutableArray<string> DefaultExtensions,
    Func<IShaderEnvironment> CreateEnvironment)
{
    /// <summary>
    /// HLSL ではない言語の環境が、ファイルを解析単位にする口。設定されていれば <see cref="CreateEnvironment"/> は使わない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Godot のシェーダー言語のように、HLSL の字句解析・型を使えない言語を足すための口である。</b>
    /// 解析単位 (<see cref="Core.Analysis.AnalysisTarget"/>) を返せば、CLI の実行・抑制コメント・ベースライン・出力・キャッシュはそのまま働く。
    /// </para>
    /// <para>
    /// 今は内部の口である (テストが作り物の言語で通り道を確かめている)。公開の形は、その言語を入れるときに決める。
    /// </para>
    /// </remarks>
    internal Func<Environments.IAnalysisEnvironment>? CreateAnalysisEnvironment { get; init; }
}
