using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// HLSL を使う環境 (Unity、Unreal、独自エンジン) が、解析に与えるもの。
/// </summary>
/// <remarks>
/// <para>
/// <b>解析は環境の決まりを持たない。</b> どのシンボルが構成の軸になるか、どの指令を受け付けるか、
/// コンパイル単位ごとに何を定義するかは、ここから受け取る。
/// </para>
/// <para>
/// 構成の空間は 2 回読む。取り込んだヘッダが宣言するシンボルは、取り込んでみるまで分からないためである。
/// </para>
/// <list type="number">
///   <item><description>展開の前: 単位のトークンから (<see cref="ReadDeclaredVariants"/>)</description></item>
///   <item><description>展開の後: 取り込んだ先を含む展開の結果から (<see cref="ReadVariants"/>)。足すものがあれば、解析が展開し直す</description></item>
/// </list>
/// </remarks>
public interface IHlslEnvironment
{
    /// <summary>環境の名前 (<c>unity</c> など)。</summary>
    string Name { get; }

    /// <summary>
    /// どの単位にも共通するプリプロセッサの設定を重ねる。
    /// </summary>
    /// <param name="options">解析の設定から作った設定。</param>
    /// <param name="semantics">解析の設定。</param>
    /// <returns>重ねた設定。</returns>
    /// <remarks>受け付ける指令、先頭に差し込むコード (ヘッダが無い環境のための代わり) などを与える。</remarks>
    PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics);

    /// <summary>
    /// 単位の展開に使う設定を作る。
    /// </summary>
    /// <param name="unit">対象の単位。</param>
    /// <param name="tokens">単位のテキストを字句解析しただけのトークン。</param>
    /// <param name="options">共通の設定に、単位のマクロ (<see cref="HlslCompilationUnit.ExtraMacros"/>) を重ねたもの。</param>
    /// <param name="filePath">解析しているファイルのパス。</param>
    /// <returns>この単位の展開に使う設定。</returns>
    /// <remarks>
    /// 単位の中身で決まる設定を与える。Unity なら <c>#pragma target</c> の <c>SHADER_TARGET</c> と、
    /// <c>CGPROGRAM</c> が自動で取り込むヘッダである。
    /// </remarks>
    PreprocessorOptions PrepareUnit(
        HlslCompilationUnit unit,
        ImmutableArray<HlslSyntaxToken> tokens,
        PreprocessorOptions options,
        string filePath);

    /// <summary>
    /// 展開の前に、単位が宣言する構成の空間を読む。
    /// </summary>
    /// <param name="unit">対象の単位。</param>
    /// <param name="tokens">単位のテキストを字句解析しただけのトークン。</param>
    /// <returns>構成の空間。</returns>
    VariantSpace ReadDeclaredVariants(HlslCompilationUnit unit, ImmutableArray<HlslSyntaxToken> tokens);

    /// <summary>
    /// 展開の結果から、構成の空間を読む。取り込んだヘッダの宣言も含む。
    /// </summary>
    /// <param name="result">展開の結果。</param>
    /// <returns>構成の空間。</returns>
    VariantSpace ReadVariants(PreprocessResult result);
}
