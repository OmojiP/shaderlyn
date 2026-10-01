using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab.Parsing;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.ShaderLab;

/// <summary>
/// 解析済みの ShaderLab ファイル。構文木と、字句・構文解析で検出した診断を保持する。
/// </summary>
/// <remarks>
/// 字句解析・構文解析・親の設定という一連の手順をこの型に閉じ込めている。
/// 利用側が手順を組み立てる必要をなくし、親の設定漏れのような不完全な木が
/// 外へ出ていかないようにするためである。
/// </remarks>
public sealed class ShaderLabSyntaxTree
{
    private ShaderLabSyntaxTree(
        SourceText text,
        ShaderLabCompilationUnitSyntax root,
        ImmutableArray<SyntaxToken> tokens,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        Tokens = tokens;
        Diagnostics = diagnostics;
    }

    /// <summary>解析対象のソーステキスト。</summary>
    public SourceText Text { get; }

    /// <summary>構文木の根。</summary>
    public ShaderLabCompilationUnitSyntax Root { get; }

    /// <summary>
    /// 字句解析が生成したトークン列。
    /// </summary>
    /// <remarks>
    /// 構文木を経由せずトークン列を直接参照したい用途 (抑制コメントの走査など) のために公開している。
    /// </remarks>
    public ImmutableArray<SyntaxToken> Tokens { get; }

    /// <summary>字句・構文解析で検出した診断。</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// ソーステキストを解析して構文木を作る。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <returns>解析結果。</returns>
    /// <remarks>
    /// <b>このメソッドは入力がどれだけ壊れていても例外を投げない。</b>
    /// 解釈できない箇所は診断として報告され、構文木にはそのままトークンとして残る。
    /// </remarks>
    public static ShaderLabSyntaxTree Parse(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        ShaderLabLexer lexer = new(text);
        ImmutableArray<SyntaxToken> tokens = lexer.Lex(out ImmutableArray<ShaderLabLexer.LexerDiagnostic> lexerDiagnostics);

        ShaderLabParser parser = new(text, tokens);
        ShaderLabCompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);

        SyntaxNode.WireParents(root);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (ShaderLabLexer.LexerDiagnostic lexerDiagnostic in lexerDiagnostics)
        {
            diagnostics.Add(Diagnostic.Create(
                ShaderLabDescriptors.SyntaxError,
                Location.Create(text, lexerDiagnostic.Span),
                lexerDiagnostic.Message));
        }

        diagnostics.AddRange(parseDiagnostics);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderLabSyntaxTree(text, root, tokens, diagnostics.ToImmutable());
    }

    /// <summary>
    /// 構文木からソーステキストを復元する。
    /// </summary>
    /// <returns>復元されたテキスト。</returns>
    /// <remarks>
    /// <para>
    /// <b>この結果は常に元のテキストと完全に一致しなければならない。</b>
    /// 一致しない場合、構文木のどこかでトークンが欠落しているか順序が狂っており、
    /// そのままでは将来のフォーマッタや自動修正が成立しない。
    /// </para>
    /// <para>
    /// この性質は全 fixture に対するラウンドトリップテストで検証している。
    /// 新しい構文ノードを追加したら、必ず fixture も追加すること。
    /// </para>
    /// </remarks>
    public string ToFullString()
    {
        System.Text.StringBuilder builder = new(Text.Length);

        foreach (SyntaxToken token in Root.DescendantTokens())
        {
            builder.Append(Text.AsSpan(token.FullSpan));
        }

        return builder.ToString();
    }
}
