using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Unity;

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
/// 共通コード片 (<c>HLSLINCLUDE</c>) は、ファイルの中の同じ種類のものをすべて残した別のテキストにし
/// (<see cref="UnityProgramBlock.IncludeText"/>)、コードブロックより先に読む (<c>PreprocessorOptions.LeadingText</c>)。
/// Unity は共通コード片をすべてファイルの順につなぎ、どのコードブロックの前にも置くので、
/// コードブロックと同じテキストに位置のまま残すと、コードブロックより後に書いた共通コード片が後ろに並んでしまう。
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
    public static ImmutableArray<UnityProgramBlock> Extract(ShaderLab.ShaderLabSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        ImmutableArray<UnityProgramBlock>.Builder results =
            ImmutableArray.CreateBuilder<UnityProgramBlock>();

        // 共通コード片は、種類 (CGINCLUDE / HLSLINCLUDE) ごとにファイル全体で 1 つのテキストにする。どのコードブロックからも同じものを読む。
        Dictionary<string, (ImmutableArray<ProgramBlockSyntax> Blocks, SourceText? Text)> includesByKind = new(StringComparer.Ordinal);

        foreach (SyntaxNode node in tree.Root.DescendantNodesAndSelf())
        {
            if (node is not ProgramBlockSyntax block
                || block.IsIncludeBlock
                || block.Language != ProgramBlockLanguage.Hlsl)
            {
                continue;
            }

            string kind = IncludeKeywordFor(block);

            if (!includesByKind.TryGetValue(kind, out (ImmutableArray<ProgramBlockSyntax> Blocks, SourceText? Text) includes))
            {
                ImmutableArray<ProgramBlockSyntax> blocks = FindIncludeBlocks(tree, kind);
                includes = (blocks, blocks.IsEmpty ? null : CreateMaskedText(tree.Text, [.. blocks.Select(i => i.BodySpan)]));
                includesByKind[kind] = includes;
            }

            results.Add(new UnityProgramBlock(
                block,
                includes.Blocks,
                FindEnclosingPass(block),
                CreateMaskedText(tree.Text, [block.BodySpan]),
                block.Token.Span)
            {
                PassName = FindPassName(FindEnclosingPass(block)),
                IncludeText = includes.Text,
            });
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
    /// <see cref="UnityProgramBlock.Block"/> は <see langword="null"/> になる。
    /// 対応する ShaderLab のノードが無いためである。
    /// <b>偽物のノードは作らない。</b>
    /// 存在しない位置を指すノードを木に入れると、いつか誰かがそれを位置として使う。
    /// </para>
    /// </remarks>
    public static ImmutableArray<UnityProgramBlock> ForStandaloneHlsl(
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
                new UnityProgramBlock(
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
        ImmutableArray<UnityProgramBlock>.Builder results =
            ImmutableArray.CreateBuilder<UnityProgramBlock>(kernels.Length);

        foreach (ComputeKernel kernel in kernels)
        {
            results.Add(new UnityProgramBlock(
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
    /// コードブロックに差し込まれる共通コード片の開始の語を返す (<c>CGPROGRAM</c> なら <c>CGINCLUDE</c>)。
    /// </summary>
    /// <param name="block">コードブロック。</param>
    /// <returns>共通コード片の開始の語。</returns>
    /// <remarks>
    /// <c>CGINCLUDE</c> は <c>HLSLPROGRAM</c> に入らず、<c>HLSLINCLUDE</c> は <c>CGPROGRAM</c> に入らない (Unity 6.6 で確かめた)。
    /// </remarks>
    private static string IncludeKeywordFor(ProgramBlockSyntax block)
        => block.Delimiter?.StartKeyword == "CGPROGRAM" ? "CGINCLUDE" : "HLSLINCLUDE";

    /// <summary>
    /// ファイルの中の、その種類の共通コード片を、ファイル上の出現順にすべて集める。
    /// </summary>
    /// <param name="tree">ShaderLab の構文木。</param>
    /// <param name="keyword">共通コード片の開始の語 (<c>HLSLINCLUDE</c> / <c>CGINCLUDE</c>)。</param>
    /// <returns>共通コード片。</returns>
    /// <remarks>
    /// <para>
    /// <b>Unity は、ファイルの中の共通コード片をすべてファイルの順につなぎ、どのコードブロックの前にも置く</b> (Unity 6.6 で確かめた)。
    /// コードブロックより後に書いたものも、別の SubShader のものも、Pass の中に書いたものも入る。
    /// 2 つの共通コード片が同じマクロを定義し直していれば、どの Pass も後のほうの値を見る。
    /// </para>
    /// <para>
    /// 以前は、コードブロックを囲むブロック (Shader / SubShader) に書いたものだけを、その場所のまま並べていた。
    /// Pass より後の共通コード片はコードブロックの後ろに並び、そのマクロが効かなかった (宣言されていない、と誤って報告していた)。
    /// </para>
    /// </remarks>
    private static ImmutableArray<ProgramBlockSyntax> FindIncludeBlocks(ShaderLab.ShaderLabSyntaxTree tree, string keyword)
        => [
            .. tree.Root.DescendantNodesAndSelf()
                .OfType<ProgramBlockSyntax>()
                .Where(b => b.IsIncludeBlock && b.Language == ProgramBlockLanguage.Hlsl && b.Delimiter?.StartKeyword == keyword)
                .OrderBy(b => b.BodySpan.Start),
        ];

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

    /// <summary>Pass に付けられた名前を探す。</summary>
    /// <param name="pass">対象の Pass。<see langword="null"/> の場合は名前も無い。</param>
    /// <returns>見つかった名前。無い場合は <see langword="null"/>。</returns>
    private static string? FindPassName(PassSyntax? pass)
    {
        if (pass is null)
        {
            return null;
        }

        foreach (ShaderLabStatementSyntax statement in pass.Body.Statements)
        {
            if (statement is CommandSyntax command
                && command.NameIs("Name")
                && command.ValueArguments.FirstOrDefault() is LiteralArgumentSyntax literal)
            {
                return literal.Token.ValueText;
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
/// Unity のコードブロック 1 つ分。コンパイル単位 (<see cref="HlslCompilationUnit"/>) に持たせる付帯情報。
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
/// <para>
/// <b>ブロックの同一性は <paramref name="CodeSpan"/> で判定する。</b>
/// <paramref name="Block"/> は ShaderLab の構文木がある場合にしか無いが、
/// 範囲はどちらの経路でも必ず決まる。
/// </para>
/// <para>
/// 解析した木から引くには <see cref="UnityCompilationExtensions.GetUnityBlock"/> を使う。
/// </para>
/// </remarks>
public sealed record UnityProgramBlock(
    ProgramBlockSyntax? Block,
    ImmutableArray<ProgramBlockSyntax> IncludeBlocks,
    PassSyntax? Pass,
    SourceText MaskedText,
    TextSpan CodeSpan,
    ImmutableDictionary<string, string>? ExtraMacros = null,
    string? KernelName = null)
{
    /// <summary><c>Name "..."</c> で付けられた Pass の名前。無い場合は <see langword="null"/>。</summary>
    public string? PassName { get; init; }

    /// <summary>
    /// このコードブロックの前に読む共通コード片 (<see cref="IncludeBlocks"/>) だけを残したテキスト。無ければ <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <see cref="MaskedText"/> と同じく、元のファイルと同じ位置に置いてある。同じ種類のコードブロックはどれも同じテキストを持つ。
    /// </remarks>
    public SourceText? IncludeText { get; init; }

    /// <summary>コンパイル単位にする。この切り出し自体を単位の付帯情報として持たせる。</summary>
    /// <returns>コンパイル単位。</returns>
    /// <remarks>
    /// 単位全体についての指摘は、<c>HLSLPROGRAM</c> などの開始の語を指す。どのブロックの話かが読み手に伝わる。
    /// HLSL 単体ファイルには開始の語が無いので、ファイルの先頭を指す。
    /// </remarks>
    internal HlslCompilationUnit ToUnit()
        => new(
            MaskedText,
            CodeSpan,
            KernelName,
            ExtraMacros,
            this,
            new TextSpan(CodeSpan.Start, Block?.Delimiter is { } delimiter ? delimiter.StartKeyword.Length : 1))
        {
            DisplayName = DescribeUnit(),
        };

    /// <summary>単位の呼び名 (<c>Pass "Forward"</c>、名前の無い Pass は <c>HLSLPROGRAM (12 行目)</c>、カーネルは <c>カーネル CSMain</c>)。</summary>
    private string? DescribeUnit()
    {
        if (KernelName is { } kernel)
        {
            return $"カーネル {kernel}";
        }

        if (PassName is { } pass)
        {
            return $"Pass \"{pass}\"";
        }

        return Block?.Delimiter is { } delimiter
            ? $"{delimiter.StartKeyword} ({MaskedText.GetLinePositionSpan(CodeSpan).Start.Line + 1} 行目)"
            : null;
    }
}
