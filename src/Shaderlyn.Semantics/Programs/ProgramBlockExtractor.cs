using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Semantics.Programs;

/// <summary>
/// <c>.shader</c> に埋め込まれた HLSL を、ファイル上の位置を保ったまま取り出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>取り出し方の要点は「切り出さずにマスクする」ことである。</b>
/// 埋め込みコードを部分文字列として切り出すと、そのテキスト上のオフセットは
/// 元のファイルのオフセットとずれる。診断の位置がずれたリンタは、
/// 指摘が正しくても直す場所を示せない。
/// </para>
/// <para>
/// そこで、元のファイルと<b>同じ長さ・同じ改行位置</b>を持つテキストを作り、
/// 解析対象としたい範囲だけを残して他をすべて空白で埋める。
/// 行数もオフセットも元のファイルと完全に一致するため、
/// 位置の変換処理がそもそも要らない。
/// </para>
/// <para>
/// この方式にはもう 1 つ利点がある。
/// <c>HLSLINCLUDE</c> の中身と <c>HLSLPROGRAM</c> の中身を<b>同時に</b>残せば、
/// 「共通コード片を各 Pass の先頭へ差し込む」という Unity の挙動が
/// 位置を保ったまま自然に再現される。連結も順序の調整も要らない。
/// </para>
/// </remarks>
internal static class ProgramBlockExtractor
{
    /// <summary>
    /// シェーダーに含まれる、HLSL として解析すべきコードブロックを列挙する。
    /// </summary>
    /// <param name="tree">ShaderLab の構文木。</param>
    /// <returns>
    /// 解析すべきブロックの一覧。各要素は 1 回分の解析単位に対応する。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 共通コード片 (<c>HLSLINCLUDE</c> / <c>CGINCLUDE</c>) 自体は解析単位にしない。
    /// 単体では成立しない断片であることが多く、それを解析しても診断の雑音にしかならないためである。
    /// 共通コード片は、それが適用される各 <c>HLSLPROGRAM</c> の一部として解析される。
    /// </para>
    /// <para>
    /// 共通コード片しか持たないシェーダー (<c>UsePass</c> で他所から参照される形) では、
    /// 解析単位が 1 つも作られない。
    /// </para>
    /// </remarks>
    public static ImmutableArray<ProgramBlockView> Extract(ShaderLab.ShaderLabSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        ImmutableArray<ProgramBlockView>.Builder results =
            ImmutableArray.CreateBuilder<ProgramBlockView>();

        foreach (SyntaxNode node in tree.Root.DescendantNodesAndSelf())
        {
            if (node is not ProgramBlockSyntax block
                || block.IsIncludeBlock
                || block.Language != ProgramBlockLanguage.Hlsl)
            {
                continue;
            }

            ImmutableArray<ProgramBlockSyntax> applicableIncludes = FindApplicableIncludeBlocks(block);

            ImmutableArray<TextSpan> visibleSpans =
            [
                .. applicableIncludes.Select(i => i.BodySpan),
                block.BodySpan,
            ];

            results.Add(new ProgramBlockView(
                block,
                applicableIncludes,
                FindEnclosingPass(block),
                CreateMaskedText(tree.Text, visibleSpans),
                block.Token.Span));
        }

        return results.ToImmutable();
    }

    /// <summary>コンピュートシェーダーのカーネルをコンパイルするときに Unity が定義するマクロ。</summary>
    private static readonly ImmutableDictionary<string, string> ComputeStageMacros =
        ImmutableDictionary<string, string>.Empty.Add("SHADER_STAGE_COMPUTE", "1");

    /// <summary>
    /// HLSL 単体ファイルを、丸ごと 1 つのコードブロックとして扱う。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="kernels">
    /// <c>#pragma kernel</c> が宣言するカーネル。無ければ空。
    /// </param>
    /// <returns>
    /// 解析単位。カーネルが宣言されていればカーネルごとに 1 つ、
    /// 無ければファイル全体で 1 つ。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b><c>.compute</c> にはファイル全体が 1 つの <c>HLSLPROGRAM</c> しかない。</b>
    /// マスクするものが無いので、元のテキストをそのまま解析対象にする。
    /// これで展開・構文解析・セマンティックモデル・HLSL のルール群は
    /// 埋め込み HLSL とまったく同じ道を通る。
    /// </para>
    /// <para>
    /// <b>カーネルごとに別の解析単位になる。</b>
    /// Unity はカーネルごとに別々にコンパイルし、
    /// <c>#pragma kernel</c> が並べたマクロはそのカーネルのときだけ定義される。
    /// <c>VARIANT=0</c> と <c>VARIANT=1</c> は両立しないので、1 つにまとめられない。
    /// </para>
    /// <para>
    /// <see cref="ProgramBlockView.Block"/> は <see langword="null"/> になる。
    /// 対応する ShaderLab のノードが無いためである。
    /// <b>偽物のノードは作らない。</b>
    /// 存在しない位置を指すノードを木に入れると、いつか誰かがそれを位置として使う。
    /// </para>
    /// </remarks>
    public static ImmutableArray<ProgramBlockView> ForStandaloneHlsl(
        SourceText text,
        ImmutableArray<ComputeKernel> kernels)
    {
        ArgumentNullException.ThrowIfNull(text);

        TextSpan whole = new(0, text.Length);

        // カーネルが 1 つも無ければ、構成は 1 つしかない。
        if (kernels.IsDefaultOrEmpty)
        {
            return
            [
                new ProgramBlockView(
                    Block: null,
                    IncludeBlocks: [],
                    Pass: null,
                    MaskedText: text,
                    CodeSpan: whole),
            ];
        }

        // Unity はカーネルごとに別々にコンパイルする。
        // #pragma kernel が並べたマクロはそのカーネルのときだけ定義されるので、
        // 1 つの構成にまとめることはできない (VARIANT=0 と VARIANT=1 は両立しない)。
        //
        // カーネルのコンパイルでは SHADER_STAGE_COMPUTE も定義される。
        // ヘッダはこれを見て、コンピュートシェーダーでだけ使うマクロ (UNITY_XR_ASSIGN_VIEW_INDEX など) を定義する。
        ImmutableArray<ProgramBlockView>.Builder results =
            ImmutableArray.CreateBuilder<ProgramBlockView>(kernels.Length);

        foreach (ComputeKernel kernel in kernels)
        {
            results.Add(new ProgramBlockView(
                Block: null,
                IncludeBlocks: [],
                Pass: null,
                MaskedText: text,
                CodeSpan: whole,
                ExtraMacros: ComputeStageMacros.SetItems(kernel.Macros),
                KernelName: kernel.Name));
        }

        return results.MoveToImmutable();
    }

    /// <summary>
    /// シェーダーが自分で書いた HLSL のコードだけを残したテキストを作る。
    /// </summary>
    /// <param name="tree">ShaderLab の構文木。</param>
    /// <returns>
    /// 元のファイルと同じ長さ・同じ行構成を持ち、
    /// HLSL のコードブロックの中身だけが残されたテキスト。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>マクロ展開も include 解決もしない、「書かれたとおりのコード」である。</b>
    /// 「この関数は使わない」という形のルールが必要とするのはこちらであり、
    /// 展開後のトークン列では役に立たない。
    /// <c>UNITY_MATRIX_MVP</c> のようにマクロ自体を禁止したい場合、
    /// 展開後にはその名前が跡形も無く消えているためである。
    /// </para>
    /// <para>
    /// 共通コード片も含めてファイル全体で 1 つのテキストにまとめている。
    /// ブロックごとに作ると、複数の Pass へ差し込まれる共通コード片が
    /// Pass の数だけ重複して報告されてしまう。
    /// </para>
    /// </remarks>
    public static SourceText ExtractAllCode(ShaderLab.ShaderLabSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        ImmutableArray<TextSpan>.Builder spans = ImmutableArray.CreateBuilder<TextSpan>();

        foreach (SyntaxNode node in tree.Root.DescendantNodesAndSelf())
        {
            if (node is ProgramBlockSyntax block && block.Language == ProgramBlockLanguage.Hlsl)
            {
                spans.Add(block.BodySpan);
            }
        }

        return CreateMaskedText(tree.Text, spans.ToImmutable());
    }

    /// <summary>
    /// 指定したブロックに差し込まれる共通コード片を、ファイル上の出現順に集める。
    /// </summary>
    /// <param name="block">対象のコードブロック。</param>
    /// <returns>適用される共通コード片。</returns>
    /// <remarks>
    /// <para>
    /// Unity は <c>CGINCLUDE</c> / <c>HLSLINCLUDE</c> を、
    /// それが書かれたブロック (<c>Shader</c> 直下、<c>Category</c> 内、<c>SubShader</c> 内) に
    /// 含まれるすべての <c>*PROGRAM</c> の先頭へ差し込む。
    /// したがって「対象ブロックの祖先ブロックに書かれた共通コード片」がすべて適用対象になる。
    /// </para>
    /// <para>
    /// 並べ替えは行わない。呼び出し元は位置を保ったままマスクする方式で解析するため、
    /// ファイル上の順序がそのまま処理順になる。
    /// </para>
    /// </remarks>
    private static ImmutableArray<ProgramBlockSyntax> FindApplicableIncludeBlocks(ProgramBlockSyntax block)
    {
        List<ProgramBlockSyntax> includes = [];

        for (SyntaxNode? ancestor = block.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is not BlockSyntax enclosing)
            {
                continue;
            }

            foreach (ShaderLabStatementSyntax statement in enclosing.Statements)
            {
                if (statement is ProgramBlockSyntax candidate
                    && candidate.IsIncludeBlock
                    && candidate.Language == ProgramBlockLanguage.Hlsl)
                {
                    includes.Add(candidate);
                }
            }
        }

        includes.Sort(static (a, b) => a.BodySpan.Start.CompareTo(b.BodySpan.Start));
        return [.. includes];
    }

    /// <summary>ブロックを含む <c>Pass</c> を探す。</summary>
    /// <param name="block">対象のコードブロック。</param>
    /// <returns>見つかった <c>Pass</c>。<c>Pass</c> の外にある場合は <see langword="null"/>。</returns>
    private static PassSyntax? FindEnclosingPass(ProgramBlockSyntax block)
    {
        for (SyntaxNode? ancestor = block.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is PassSyntax pass)
            {
                return pass;
            }
        }

        return null;
    }

    /// <summary>
    /// 指定した範囲だけを残し、他を空白でマスクしたテキストを作る。
    /// </summary>
    /// <param name="text">元のソーステキスト。</param>
    /// <param name="visibleSpans">残す範囲。</param>
    /// <returns>元と同じ長さ・同じ行構成を持つテキスト。</returns>
    /// <remarks>
    /// <b>改行はそのまま残す。</b>
    /// 空白で置き換えてしまうと行の区切りが消え、行番号が元のファイルとずれる。
    /// 位置を保つことがこの方式の唯一の目的なので、ここを崩してはならない。
    /// </remarks>
    private static SourceText CreateMaskedText(SourceText text, ImmutableArray<TextSpan> visibleSpans)
    {
        char[] masked = new char[text.Length];

        for (int i = 0; i < masked.Length; i++)
        {
            char c = text[i];
            masked[i] = c is '\r' or '\n' ? c : ' ';
        }

        foreach (TextSpan span in visibleSpans)
        {
            for (int i = span.Start; i < span.End && i < text.Length; i++)
            {
                masked[i] = text[i];
            }
        }

        return SourceText.From(new string(masked), text.FilePath, text.Encoding);
    }
}

/// <summary>
/// HLSL として解析する 1 単位分の切り出し結果。
/// </summary>
/// <param name="Block">
/// 対象の <c>*PROGRAM</c> ブロック。
/// HLSL 単体ファイルでは対応するノードが無いため <see langword="null"/>。
/// </param>
/// <param name="IncludeBlocks">このブロックへ差し込まれる共通コード片。</param>
/// <param name="Pass">ブロックを含む <c>Pass</c>。<c>Pass</c> の外にある場合は <see langword="null"/>。</param>
/// <param name="MaskedText">
/// 元のファイルと同じ長さ・同じ行構成を持ち、解析対象の範囲だけが残されたテキスト。
/// </param>
/// <param name="CodeSpan">
/// このブロックが占めるファイル上の範囲。
/// </param>
/// <param name="ExtraMacros">
/// このブロックの展開でだけ定義するマクロ。
/// <c>#pragma kernel</c> が並べた指定がここに入る。
/// </param>
/// <param name="KernelName">
/// このブロックが対応するコンピュートシェーダーのカーネル名。
/// カーネルでない場合は <see langword="null"/>。
/// </param>
/// <remarks>
/// <b>ブロックの同一性は <paramref name="CodeSpan"/> で判定する。</b>
/// <paramref name="Block"/> は ShaderLab の構文木がある場合にしか無いが、
/// 範囲はどちらの経路でも必ず決まる。
/// </remarks>
internal readonly record struct ProgramBlockView(
    ProgramBlockSyntax? Block,
    ImmutableArray<ProgramBlockSyntax> IncludeBlocks,
    PassSyntax? Pass,
    SourceText MaskedText,
    TextSpan CodeSpan,
    ImmutableDictionary<string, string>? ExtraMacros = null,
    string? KernelName = null);
