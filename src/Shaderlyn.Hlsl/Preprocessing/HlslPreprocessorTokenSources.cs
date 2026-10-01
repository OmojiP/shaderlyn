using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// トークンソースのスタック。取り込んだファイルと展開中のマクロを積み、読み終えたら外側へ戻る。
/// </summary>
internal sealed partial class HlslPreprocessor
{
    // --------------------------------------------------------------------
    // トークンソースのスタック
    // --------------------------------------------------------------------

    /// <summary>
    /// トークンの供給元。ファイルの内容かマクロの展開結果を表す。
    /// </summary>
    /// <param name="Tokens">供給するトークン。</param>
    /// <param name="IsFile">ファイル由来かどうか。指令を解釈してよいのはファイル由来のみ。</param>
    /// <param name="MacroName">マクロ展開の場合、その名前。再帰展開の抑止に使う。</param>
    /// <param name="IncludePath">ファイル由来の場合、そのパス。循環 include の検出に使う。</param>
    private sealed record TokenSource(
        ImmutableArray<HlslSyntaxToken> Tokens,
        bool IsFile,
        string? MacroName,
        string? IncludePath)
    {
        /// <summary>次に読むトークンの位置。</summary>
        public int Index { get; set; }

        /// <summary>すべて読み終えたかどうか。</summary>
        public bool IsExhausted => Index >= Tokens.Length;
    }

    private TokenSource CurrentSource => _sources[^1];

    /// <summary>ファイルをトークン列に分解してスタックへ積む。</summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <param name="includePath">循環検出に使うパス。最上位ファイルでは <see langword="null"/>。</param>
    private void PushFileSource(SourceText text, string? includePath = null)
    {
        ImmutableArray<HlslSyntaxToken> tokens;
        ImmutableArray<HlslLexer.LexerDiagnostic> lexerDiagnostics;

        if (_options.TokenCache is { } cache)
        {
            tokens = cache.GetOrLex(text, out lexerDiagnostics);
        }
        else
        {
            HlslLexer lexer = new(text);
            tokens = lexer.Lex(out lexerDiagnostics);
        }

        foreach (HlslLexer.LexerDiagnostic diagnostic in lexerDiagnostics)
        {
            Report(HlslDescriptors.SyntaxError, Location.Create(text, diagnostic.Span), diagnostic.Message);
        }

        // 終端トークンは取り除く。取り込んだファイルの終端で全体が終わってはならない。
        ImmutableArray<HlslSyntaxToken> body = tokens.Length > 0 && tokens[^1].Kind == HlslSyntaxKind.EndOfFileToken
            ? tokens.RemoveAt(tokens.Length - 1)
            : tokens;

        _sources.Add(new TokenSource(body, IsFile: true, MacroName: null, IncludePath: includePath));

        if (includePath is not null)
        {
            _activeIncludePaths.Add(includePath);
        }
    }

    /// <summary>マクロの展開結果をスタックへ積む。</summary>
    /// <param name="tokens">展開結果のトークン。</param>
    /// <param name="macroName">展開したマクロの名前。</param>
    private void PushExpansionSource(ImmutableArray<HlslSyntaxToken> tokens, string macroName)
        => _sources.Add(new TokenSource(tokens, IsFile: false, MacroName: macroName, IncludePath: null));

    /// <summary>
    /// 次のトークンを取り出さずに見る。読み終えたソースは取り除く。
    /// </summary>
    /// <returns>次のトークン。すべて読み終えた場合は <see langword="null"/>。</returns>
    private HlslSyntaxToken? PeekToken()
    {
        while (_sources.Count > 0)
        {
            TokenSource source = CurrentSource;
            if (!source.IsExhausted)
            {
                return source.Tokens[source.Index];
            }

            PopSource();
        }

        return null;
    }

    /// <summary>
    /// 現在のソース内に限って次のトークンを見る。ソースの取り除きは行わない。
    /// </summary>
    /// <returns>次のトークン。現在のソースを読み終えている場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>指令の処理では <see cref="PeekToken"/> ではなくこちらを使わなければならない。</b>
    /// 指令の行がファイルをまたぐことは無いので、境界で止まるのが正しい。
    /// </para>
    /// <para>
    /// さらに重要な理由がある。<see cref="PeekToken"/> は読み終えたソースを取り除くため、
    /// <c>#include</c> の行を読み終えた時点で include 元のファイルがスタックから消えてしまう。
    /// すると取り込み先を積む前に include 元が「処理中」でなくなり、
    /// <b>循環 include の検出が働かず無限ループになる</b>。
    /// </para>
    /// </remarks>
    private HlslSyntaxToken? PeekInCurrentSource()
    {
        if (_sources.Count == 0)
        {
            return null;
        }

        TokenSource source = CurrentSource;
        return source.IsExhausted ? null : source.Tokens[source.Index];
    }

    /// <summary>次のトークンを取り出す。</summary>
    /// <returns>取り出したトークン。すべて読み終えた場合は終端トークン。</returns>
    private HlslSyntaxToken TakeToken()
    {
        HlslSyntaxToken? token = PeekToken();
        if (token is null)
        {
            return CreateSyntheticToken(HlslSyntaxKind.EndOfFileToken, string.Empty);
        }

        CurrentSource.Index++;
        return token;
    }

    /// <summary>読み終えたソースをスタックから取り除く。</summary>
    private void PopSource()
    {
        TokenSource source = CurrentSource;
        _sources.RemoveAt(_sources.Count - 1);

        if (source.IncludePath is not null)
        {
            _activeIncludePaths.Remove(source.IncludePath);
            EndRecording(source.IncludePath);
        }
    }

    /// <summary>指定したマクロが現在展開中かどうかを判定する。</summary>
    /// <param name="name">マクロ名。</param>
    /// <returns>展開中の場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 展開中のマクロを再度展開しないことで、直接・間接の再帰を止める。
    /// C 標準の hide set ほど厳密ではないが、無限展開を確実に防げる。
    /// </remarks>
    private bool IsMacroBeingExpanded(string name)
        => _sources.Any(source => source.MacroName is not null && string.Equals(source.MacroName, name, StringComparison.Ordinal));
}
