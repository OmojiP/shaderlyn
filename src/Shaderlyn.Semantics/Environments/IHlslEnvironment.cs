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
    /// <param name="unit">対象の単位。</param>
    /// <param name="result">展開の結果。</param>
    /// <returns>構成の空間。</returns>
    VariantSpace ReadVariants(HlslCompilationUnit unit, PreprocessResult result);

    /// <summary>
    /// 単位のエントリポイントを読む。
    /// </summary>
    /// <param name="unit">対象の単位。</param>
    /// <param name="result">既定の構成で展開した結果。取り込んだヘッダの指令も含む。</param>
    /// <returns>
    /// エントリポイント。解析は、この名前の関数があるかを確かめる (HL0301)。
    /// 指定が無い (取り込まれる断片など) なら空。
    /// </returns>
    /// <remarks>
    /// Unity は <c>#pragma vertex</c> などから、設定でコンパイル単位を与える環境は単位の情報
    /// (<see cref="HlslCompilationUnit.EntryPoints"/>) から読む。
    /// </remarks>
    ImmutableArray<HlslEntryPoint> ReadEntryPoints(HlslCompilationUnit unit, PreprocessResult result);

    /// <summary>
    /// 宣言されていない名前が、構成の軸になるシンボルのつもりで書かれたものらしいかを判定する。
    /// </summary>
    /// <param name="name">条件に書かれた名前。</param>
    /// <returns>シンボルらしければ <see langword="true"/>。宣言し忘れとして報告する (HL0330)。</returns>
    /// <remarks>
    /// Unity は「下線で始まる大文字の名前」(<c>_NORMALMAP</c>) という慣習を持つ。
    /// 慣習の無い環境 (<c>_DEBUG</c> などが普通に条件に出る) は常に <see langword="false"/> を返す。
    /// </remarks>
    bool LooksLikeVariantSymbol(string name);

    /// <summary>
    /// <c>#include</c> のパスが、ビルドのときにエンジンの C++ が生成するファイルかを判定する。
    /// </summary>
    /// <param name="path"><c>#include</c> に書かれたパス。</param>
    /// <returns>生成されるファイルなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// Unreal の <c>/Engine/Generated/…</c> (uniform buffer やマテリアルの宣言) がそうである。ディスクには無いので解決できない。
    /// </para>
    /// <para>
    /// <b>解決できないことは変わらないが、誤りとしては報告しない (HL0321)。</b>
    /// 中身を知らないので、「宣言が無い」ことを根拠にする検査は行わない (SL0002 で伝える)。
    /// </para>
    /// </remarks>
    bool IsGeneratedInclude(string path);
}
