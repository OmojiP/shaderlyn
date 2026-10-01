using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Semantics;

/// <summary>
/// <c>.shader</c> に埋め込まれたコードブロック 1 つ分の解析結果。
/// </summary>
/// <remarks>
/// 1 つの <c>HLSLPROGRAM</c> と、そこへ差し込まれる共通コード片
/// (<c>HLSLINCLUDE</c>) をあわせた 1 回分の解析に対応する。
/// Pass ごとに別の解析単位になるのは、Pass ごとに <c>#pragma</c> や
/// 有効なシンボルが異なり、同じ共通コードでも違う結果になりうるためである。
/// </remarks>
public sealed class AnalyzedProgram
{
    internal AnalyzedProgram(
        ProgramBlockView extraction,
        HlslSyntaxTree tree,
        DeclarationSet declarations,
        string? passName)
    {
        Block = extraction.Block;
        CodeSpan = extraction.CodeSpan;
        KernelName = extraction.KernelName;
        IncludeBlocks = extraction.IncludeBlocks;
        Pass = extraction.Pass;
        Text = extraction.MaskedText;
        Tree = tree;
        Uniforms = declarations.Uniforms;
        ConstantBuffers = declarations.ConstantBuffers;
        Structs = declarations.Structs;
        FunctionNames = declarations.FunctionNames;
        FunctionSignatures = declarations.FunctionSignatures;
        PassName = passName;
    }

    /// <summary>
    /// 対応する <c>*PROGRAM</c> ブロック。
    /// HLSL 単体ファイル (<c>.compute</c> など) では <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <b>ブロックを見分けるには <see cref="CodeSpan"/> を使うこと。</b>
    /// こちらは ShaderLab の構文木がある場合にしか無い。
    /// </remarks>
    public ProgramBlockSyntax? Block { get; }

    /// <summary>
    /// このブロックが占めるファイル上の範囲。
    /// </summary>
    /// <remarks>
    /// 同じファイルの中でブロックを見分けるための識別子でもある。
    /// 範囲はどの経路でも必ず決まる。
    /// </remarks>
    public TextSpan CodeSpan { get; }

    /// <summary>このブロックへ差し込まれた共通コード片。</summary>
    public ImmutableArray<ProgramBlockSyntax> IncludeBlocks { get; }

    /// <summary>ブロックを含む <c>Pass</c>。<c>Pass</c> の外にある場合は <see langword="null"/>。</summary>
    public PassSyntax? Pass { get; }

    /// <summary><c>Name "..."</c> で付けられた Pass の名前。無い場合は <see langword="null"/>。</summary>
    public string? PassName { get; }

    /// <summary>
    /// このブロックが対応するコンピュートシェーダーのカーネル名。
    /// カーネルでない場合は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <b>Unity はカーネルごとに別々にコンパイルする。</b>
    /// <c>#pragma kernel</c> が並べたマクロはそのカーネルのときだけ定義されるので、
    /// カーネルごとに 1 つのブロックになる。
    /// 同じファイルの中では、この名前がブロックを見分ける手がかりになる。
    /// </remarks>
    public string? KernelName { get; }

    /// <summary>
    /// 別々に組み立てられるコードを見分ける鍵。既定の構成とそのバリアントは同じ鍵を持つ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>位置 (<see cref="CodeSpan"/>) だけでは見分けられない。</b>
    /// コンピュートシェーダーはカーネルごとに別々に組み立てられ、どのカーネルも同じ位置から始まる。
    /// 別の Pass や別のカーネルにある同じ名前の宣言は別物であり、まとめて扱うと取り違える。
    /// </para>
    /// <para>
    /// 宣言を集めて突き合わせるルールは、この鍵ごとに分けて集めること。
    /// </para>
    /// </remarks>
    public (int Start, string? Kernel) BlockKey => (CodeSpan.Start, KernelName);

    /// <summary>
    /// この解析結果が、どのシンボルを有効にした構成のものか。
    /// 既定の構成では空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// シンボルは C# からも切り替えられるため、
    /// 有効になっていない分岐のコードも「いつか通る」コードである。
    /// どの構成で見つけた指摘かを示せないと、
    /// 利用者は自分のコードのどこを見ればよいか分からない。
    /// </para>
    /// <para>
    /// <b>2 つ以上になることがある。</b>
    /// <c>#if defined(_A) &amp;&amp; defined(_B)</c> の中は、
    /// どちらか一方だけを有効にした構成では現れない。
    /// 条件に書かれた論理積は、そのまま 1 つの構成として展開する。
    /// </para>
    /// </remarks>
    public ImmutableArray<string> EnabledSymbols { get; init; } = [];

    /// <summary>
    /// 互いに関係しないキーワードをまとめて有効にした構成なら、既定の木と突き合わせた結果。
    /// そうでなければ <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>まとめた構成の <see cref="EnabledSymbols"/> は論理積ではない。</b>
    /// 既定の木との違いは、それぞれどれか 1 つのキーワードが起こしたもので、
    /// 突き合わせの時点でキーワードごとの条件を付けてある。
    /// 条件の索引はこの結果をそのまま使う (<see cref="Conditional.ConditionMapBuilder"/>)。
    /// </para>
    /// <para>
    /// 木そのものは、まとめたキーワードをすべて有効にした 1 つの実在する構成である。
    /// </para>
    /// </remarks>
    public Conditional.ConditionalMergeResult? PackedMerge { get; init; }

    /// <summary>互いに関係しないキーワードをまとめて有効にした構成かどうか。</summary>
    public bool IsPacked => PackedMerge is not null;

    /// <summary>
    /// 分岐の中で定義したマクロがコードとして使われたために、両方の分岐を並べなかったシンボル。
    /// </summary>
    /// <remarks>
    /// 展開の結果 (<see cref="HlslSyntaxTree.PreprocessResult"/>) の
    /// <c>BothBranchDeclines</c> には現れない。展開し直すときに並べる対象から外すためである。
    /// なぜバリアントが要ったのかを数えるために残す。挙動には使わない。
    /// </remarks>
    public ImmutableArray<string> MacroConflictSymbols { get; init; } = [];

    /// <summary>
    /// この木を解析した構成が表す条件。既定の構成なら「常に」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>バリアントの木の中の「条件が付いていない」は、「どの構成でも」ではない。</b>
    /// そのシンボルを有効にした構成の中での話である。
    /// <c>#pragma multi_compile_fragment _ _A _B</c> の <c>_A</c> を有効にした木では、
    /// <c>_B</c> は決して有効にならないので <c>#ifndef _B</c> の領域は条件無しで現れる。
    /// </para>
    /// <para>
    /// これを掛け合わせずに宣言側の条件と突き合わせると、
    /// 「<c>_B</c> のとき存在しない」という、その木では起こらない構成の話を報告することになる。
    /// </para>
    /// </remarks>
    public SymbolCondition AnalyzedCondition
    {
        get
        {
            SymbolCondition condition = SymbolCondition.Always;

            foreach (string symbol in EnabledSymbols)
            {
                condition = condition.And(SymbolCondition.Symbol(symbol));
            }

            return condition;
        }
    }

    /// <summary>
    /// 解析に使ったソーステキスト。
    /// </summary>
    /// <remarks>
    /// 元の <c>.shader</c> と同じ長さ・同じ行構成を持ち、
    /// 解析対象の範囲以外が空白で覆われている。
    /// このテキスト上の位置は、そのまま元のファイル上の位置として使える。
    /// </remarks>
    public SourceText Text { get; }

    /// <summary>HLSL としての解析結果。</summary>
    public HlslSyntaxTree Tree { get; }

    /// <summary>このブロックから見える uniform。include されたファイルの分も含む。</summary>
    public ImmutableArray<UniformSymbol> Uniforms { get; }

    /// <summary>このブロックから見える定数バッファ。</summary>
    public ImmutableArray<ConstantBufferSymbol> ConstantBuffers { get; }

    /// <summary>このブロックから見える構造体。include されたファイルの分も含む。</summary>
    public ImmutableArray<StructDeclarationSyntax> Structs { get; }

    /// <summary>
    /// このブロックから見える関数の名前。
    /// </summary>
    /// <remarks>
    /// プロトタイプ宣言だけのものも含む。
    /// 定義がどこにあるかはリンクの問題であり、名前の存在とは別の話である。
    /// </remarks>
    public ImmutableHashSet<string> FunctionNames { get; }

    /// <summary>
    /// このブロックから見える関数の、宣言されている形。
    /// </summary>
    /// <remarks>
    /// 同じ名前に複数の形があるのはオーバーロードである。
    /// どれが選ばれるかは実引数の型で決まるが、
    /// 個数で絞った結果がすべて同じ型を返すなら、解決しなくても戻り値の型は分かる。
    /// </remarks>
    public ImmutableDictionary<string, ImmutableArray<FunctionSignature>> FunctionSignatures { get; }

    /// <summary>プリプロセスと構文解析で検出した問題。</summary>
    public ImmutableArray<Diagnostic> Diagnostics => Tree.Diagnostics;

    /// <summary>解決できなかった include のパス。</summary>
    public ImmutableArray<string> UnresolvedIncludes => Tree.PreprocessResult.UnresolvedIncludes;

    /// <summary>
    /// 診断の位置に使う、ブロックの先頭を指す位置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ブロック全体に対する指摘 (「この Pass に〜が足りない」など) の位置に使う。
    /// 開始シンボルを指すので、どのブロックの話かが読み手に伝わる。
    /// </para>
    /// <para>
    /// HLSL 単体ファイルには開始シンボルが無いので、ファイルの先頭を指す。
    /// </para>
    /// </remarks>
    public Location GetBlockLocation()
        => Location.Create(Text, new TextSpan(CodeSpan.Start, GetKeywordLength()));

    /// <summary>開始シンボルの長さを求める。</summary>
    /// <returns>開始シンボルの文字数。判別できない場合は 1。</returns>
    private int GetKeywordLength()
        => Block?.Delimiter is { } delimiter ? delimiter.StartKeyword.Length : 1;
}
