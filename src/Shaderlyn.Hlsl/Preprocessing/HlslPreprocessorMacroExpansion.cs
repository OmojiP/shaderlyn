using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// マクロ展開。
/// </summary>
internal sealed partial class HlslPreprocessor
{
    // --------------------------------------------------------------------
    // マクロ展開
    // --------------------------------------------------------------------

    /// <summary>
    /// 現在位置の識別子がマクロなら展開する。
    /// </summary>
    /// <returns>展開した場合は <see langword="true"/>。</returns>
    private bool TryExpandMacro()
    {
        HlslSyntaxToken nameToken = PeekToken()!;

        if (!_macros.TryGetValue(nameToken.Text, out MacroDefinition? macro))
        {
            return false;
        }

        if (IsMacroBeingExpanded(macro.Name))
        {
            return false;
        }

        NoteMergedMacroUse(macro.Name);

        if (!macro.IsFunctionLike)
        {
            TakeToken();
            PushExpansionSource(RelocateTokens(MarkMacroBody(macro), nameToken), macro.Name);
            return true;
        }

        // 関数形式マクロは、直後に開き括弧が無ければ展開しない (C の規則)。
        // マクロ名を関数ポインタのように参照するコードを壊さないために必要である。
        if (!NextNonExhaustedTokenIsOpenParen())
        {
            return false;
        }

        TakeToken();
        ImmutableArray<ImmutableArray<HlslSyntaxToken>>? arguments = ReadMacroArguments(nameToken);

        if (arguments is null)
        {
            return true;
        }

        if (!macro.AcceptsArgumentCount(arguments.Value.Length))
        {
            Report(HlslDescriptors.PreprocessorError, nameToken.GetLocation(),
                $"マクロ '{macro.Name}' の引数の個数が合いません (期待 {macro.Parameters.Length} 個、実際 {arguments.Value.Length} 個)。");
            return true;
        }

        ImmutableArray<HlslSyntaxToken> substituted = SubstituteMacroBody(macro, arguments.Value, nameToken);
        PushExpansionSource(substituted, macro.Name);
        return true;
    }

    /// <summary>次に読めるトークンが開き括弧かどうかを判定する。</summary>
    /// <returns>開き括弧の場合は <see langword="true"/>。</returns>
    private bool NextNonExhaustedTokenIsOpenParen()
    {
        // 現在のトークン (マクロ名) の 1 つ先を、ソース境界をまたいで探す。
        for (int sourceIndex = _sources.Count - 1; sourceIndex >= 0; sourceIndex--)
        {
            TokenSource source = _sources[sourceIndex];
            int start = sourceIndex == _sources.Count - 1 ? source.Index + 1 : source.Index;

            if (start < source.Tokens.Length)
            {
                return source.Tokens[start].Kind == HlslSyntaxKind.OpenParenToken;
            }
        }

        return false;
    }

    /// <summary>
    /// 関数形式マクロの実引数を読み取る。
    /// </summary>
    /// <param name="nameToken">マクロ名のトークン。診断の位置に使う。</param>
    /// <returns>引数ごとのトークン列。括弧が閉じられていない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 括弧の深さを数えながらカンマで区切る。
    /// 入れ子の括弧の中にあるカンマは区切りではない
    /// (<c>FOO(float2(1, 2))</c> の引数は 1 つである)。
    /// </remarks>
    private ImmutableArray<ImmutableArray<HlslSyntaxToken>>? ReadMacroArguments(HlslSyntaxToken nameToken)
    {
        // 開き括弧を消費する。
        TakeToken();

        ImmutableArray<ImmutableArray<HlslSyntaxToken>>.Builder arguments =
            ImmutableArray.CreateBuilder<ImmutableArray<HlslSyntaxToken>>();
        ImmutableArray<HlslSyntaxToken>.Builder current = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        int depth = 0;

        while (true)
        {
            HlslSyntaxToken? token = PeekToken();

            if (token is null)
            {
                Report(HlslDescriptors.PreprocessorError, nameToken.GetLocation(),
                    $"マクロ '{nameToken.Text}' の引数の括弧が閉じられていません。");
                return null;
            }

            if (token.Kind == HlslSyntaxKind.CloseParenToken && depth == 0)
            {
                TakeToken();

                if (current.Count > 0 || arguments.Count > 0)
                {
                    arguments.Add(current.ToImmutable());
                }

                return arguments.ToImmutable();
            }

            if (token.Kind == HlslSyntaxKind.CommaToken && depth == 0)
            {
                TakeToken();
                arguments.Add(current.ToImmutable());
                current.Clear();
                continue;
            }

            if (token.Kind is HlslSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (token.Kind is HlslSyntaxKind.CloseParenToken)
            {
                depth--;
            }

            current.Add(TakeToken());
        }
    }

    /// <summary>
    /// マクロ本体の仮引数を実引数で置き換える。
    /// </summary>
    /// <param name="macro">展開するマクロ。</param>
    /// <param name="arguments">実引数。</param>
    /// <param name="callSite">呼び出し位置のトークン。</param>
    /// <returns>置換後のトークン列。</returns>
    /// <remarks>
    /// <c>#</c> (文字列化) と <c>##</c> (連結) に対応する。
    /// これらの演算子の対象となる引数は、マクロ展開せずにそのまま使う必要がある。
    /// </remarks>
    private ImmutableArray<HlslSyntaxToken> SubstituteMacroBody(
        MacroDefinition macro,
        ImmutableArray<ImmutableArray<HlslSyntaxToken>> arguments,
        HlslSyntaxToken callSite)
    {
        Dictionary<string, ImmutableArray<HlslSyntaxToken>> bindings = new(StringComparer.Ordinal);

        for (int i = 0; i < macro.Parameters.Length; i++)
        {
            bindings[macro.Parameters[i]] = i < arguments.Length ? arguments[i] : [];
        }

        if (macro.IsVariadic)
        {
            ImmutableArray<HlslSyntaxToken>.Builder variadic = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

            for (int i = macro.Parameters.Length; i < arguments.Length; i++)
            {
                if (variadic.Count > 0)
                {
                    variadic.Add(CreateSyntheticToken(HlslSyntaxKind.CommaToken, ",", callSite));
                }

                variadic.AddRange(arguments[i]);
            }

            bindings[MacroDefinition.VariadicArgumentName] = variadic.ToImmutable();
        }

        ImmutableArray<HlslSyntaxToken>.Builder result = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        // 実引数として埋め込んだトークンの位置と、解析しているファイルで書かれた位置 (RelocateSubstituted)。
        Dictionary<int, TextSpan?> argumentSpans = [];

        // 本体での位置を付けた形を使う。実引数として埋め込むトークンには付かない。
        ImmutableArray<HlslSyntaxToken> body = MarkMacroBody(macro);

        for (int i = 0; i < body.Length; i++)
        {
            HlslSyntaxToken token = body[i];

            // # による文字列化
            if (token.Kind == HlslSyntaxKind.HashToken
                && i + 1 < body.Length
                && body[i + 1].Kind == HlslSyntaxKind.IdentifierToken
                && bindings.TryGetValue(body[i + 1].Text, out ImmutableArray<HlslSyntaxToken> toStringize))
            {
                result.Add(CreateStringToken(Stringize(toStringize), callSite));
                i++;
                continue;
            }

            // ## による連結。
            //
            // a ## b ## c のように連結が連なる場合は、左から順に畳み込む。
            // 1 回分だけ処理して抜けると、2 つ目の ## が演算子のまま出力へ漏れ、
            // 展開結果が構文として成立しなくなる。
            // Unity の Common.hlsl には
            // #define FRAMEBUFFER_INPUT_FLOAT(idx) ... float4 _UnityFBInput##idx##_TexelSize
            // のように連鎖する定義が実在する。
            if (i + 2 < body.Length && body[i + 1].Kind == HlslSyntaxKind.HashHashToken)
            {
                ImmutableArray<HlslSyntaxToken> pasted = ResolveOperand(token, bindings);

                while (i + 2 < body.Length && body[i + 1].Kind == HlslSyntaxKind.HashHashToken)
                {
                    pasted = Paste(pasted, ResolveOperand(body[i + 2], bindings), callSite);
                    i += 2;
                }

                result.AddRange(pasted);
                continue;
            }

            if (token.Kind == HlslSyntaxKind.IdentifierToken
                && bindings.TryGetValue(token.Text, out ImmutableArray<HlslSyntaxToken> replacement))
            {
                // 引数は展開してから埋め込む (C の規則)。
                foreach (HlslSyntaxToken argument in ExpandTokenList(replacement, depth: 0))
                {
                    argumentSpans[result.Count] = WrittenArgumentSpan(argument);
                    result.Add(argument);
                }

                continue;
            }

            result.Add(token);
        }

        return RelocateSubstituted(result.ToImmutable(), argumentSpans, callSite);
    }

    /// <summary>
    /// 実引数のトークンが、利用者のファイルで書かれた位置を返す。
    /// </summary>
    /// <param name="token">実引数のトークン。</param>
    /// <returns>書かれた位置。書かれたとおりのトークンでなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 入れ子のマクロの実引数として渡ってきたものは、最初に書かれた位置を引き継ぐ。
    /// 実引数の中にあったマクロが作ったトークンは、書かれたとおりではないので付けない。
    /// </remarks>
    private TextSpan? WrittenArgumentSpan(HlslSyntaxToken token)
        => token.MacroArgumentSpan
           ?? (IsWrittenInUserFile(token) ? token.Span : null);

    /// <summary>
    /// 置換後のトークン列を呼び出し位置へ移す。実引数のトークンには書かれた位置を残す。
    /// </summary>
    /// <param name="tokens">置換後のトークン列。</param>
    /// <param name="arguments">実引数として埋め込んだトークンの位置と、書かれた位置。</param>
    /// <param name="callSite">呼び出し位置のトークン。</param>
    /// <returns>位置を移したトークン列。</returns>
    /// <remarks>
    /// <para>
    /// <b>実引数は、利用者が呼び出し位置に書いたコードである</b> (<see cref="HlslSyntaxToken.MacroArgumentSpan"/>)。
    /// 位置 (<see cref="HlslSyntaxToken.Span"/>) は本体と同じく呼び出し位置へ移し、書かれた位置を別に残す。
    /// 構成ごとの木の突き合わせは位置を鍵にしているので、実引数だけを元の位置に残すと対応が崩れる。
    /// </para>
    /// <para>
    /// 書かれた位置は、呼び出しも利用者のファイルにある場合にだけ残す。
    /// <c>#</c> や <c>##</c> で作ったトークンは本体の側に数える。書かれたとおりのトークンではない。
    /// </para>
    /// </remarks>
    private ImmutableArray<HlslSyntaxToken> RelocateSubstituted(
        ImmutableArray<HlslSyntaxToken> tokens,
        Dictionary<int, TextSpan?> arguments,
        HlslSyntaxToken callSite)
    {
        bool ownCallSite = IsUserFile(callSite.Source.FilePath);

        if (arguments.Count == 0 || !ownCallSite)
        {
            return RelocateTokens(tokens, callSite);
        }

        ImmutableArray<HlslSyntaxToken>.Builder relocated = ImmutableArray.CreateBuilder<HlslSyntaxToken>(tokens.Length);

        for (int i = 0; i < tokens.Length; i++)
        {
            relocated.Add(tokens[i].RelocateToMacroCallSite(
                callSite.Source,
                callSite.Span,
                keepDefinition: true,
                argumentSpan: arguments.TryGetValue(i, out TextSpan? written) ? written : null));
        }

        return relocated.MoveToImmutable();
    }

    /// <summary>連結演算子の対象を、仮引数なら実引数に置き換えて返す。</summary>
    /// <param name="token">対象のトークン。</param>
    /// <param name="bindings">仮引数から実引数への対応。</param>
    /// <returns>置き換え後のトークン列。</returns>
    private static ImmutableArray<HlslSyntaxToken> ResolveOperand(
        HlslSyntaxToken token,
        Dictionary<string, ImmutableArray<HlslSyntaxToken>> bindings)
        => token.Kind == HlslSyntaxKind.IdentifierToken
            && bindings.TryGetValue(token.Text, out ImmutableArray<HlslSyntaxToken> replacement)
            ? replacement
            : [token];

    /// <summary>
    /// 2 つのトークン列を連結する。境界の 2 トークンは 1 つのトークンへ融合する。
    /// </summary>
    /// <param name="left">左側のトークン列。</param>
    /// <param name="right">右側のトークン列。</param>
    /// <param name="callSite">呼び出し位置のトークン。</param>
    /// <returns>連結後のトークン列。</returns>
    /// <remarks>
    /// 融合結果を字句解析し直して種別を決める。
    /// <c>a ## b</c> が識別子 <c>ab</c> になるのはこの再解析による。
    /// </remarks>
    private ImmutableArray<HlslSyntaxToken> Paste(
        ImmutableArray<HlslSyntaxToken> left,
        ImmutableArray<HlslSyntaxToken> right,
        HlslSyntaxToken callSite)
    {
        if (left.IsEmpty)
        {
            return right;
        }

        if (right.IsEmpty)
        {
            return left;
        }

        ImmutableArray<HlslSyntaxToken>.Builder result = ImmutableArray.CreateBuilder<HlslSyntaxToken>();
        result.AddRange(left.Take(left.Length - 1));

        string fused = left[^1].Text + right[0].Text;
        SourceText fusedText = SourceText.From(fused, "<トークン連結>");
        HlslLexer lexer = new(fusedText);
        ImmutableArray<HlslSyntaxToken> fusedTokens = lexer.Lex(out _);

        foreach (HlslSyntaxToken token in fusedTokens)
        {
            if (token.Kind != HlslSyntaxKind.EndOfFileToken)
            {
                result.Add(token.RelocateToMacroCallSite(callSite.Source, callSite.Span));

            }
        }

        result.AddRange(right.Skip(1));
        return result.ToImmutable();
    }

    /// <summary>トークン列を文字列リテラルの中身へ変換する。</summary>
    /// <param name="tokens">変換するトークン列。</param>
    /// <returns>文字列化した結果。</returns>
    private static string Stringize(ImmutableArray<HlslSyntaxToken> tokens)
        => string.Join(" ", tokens.Select(t => t.Text));

    /// <summary>
    /// トークン列をマクロ展開する。
    /// </summary>
    /// <param name="tokens">展開するトークン列。</param>
    /// <param name="depth">現在の再帰の深さ。</param>
    /// <returns>展開後のトークン列。</returns>
    /// <remarks>
    /// 条件式の評価とマクロ引数の展開に使う。主ループとは別経路であり、
    /// スタックへ積まずにその場で完結させる必要があるため独立した実装になっている。
    /// </remarks>
    private ImmutableArray<HlslSyntaxToken> ExpandTokenList(ImmutableArray<HlslSyntaxToken> tokens, int depth)
    {
        if (depth >= _options.MaxExpansionDepth)
        {
            return tokens;
        }

        ImmutableArray<HlslSyntaxToken>.Builder result = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        for (int i = 0; i < tokens.Length; i++)
        {
            HlslSyntaxToken token = tokens[i];

            // defined の被演算子は展開してはならない。
            // 展開してしまうと defined(SHADER_API_D3D11) が defined(1) になり、
            // 「定義されているか」という判定そのものが成り立たなくなる。
            // マクロの本体に defined を含む書き方は Unity が実際に使っている
            // (#define FORCE_SHADOW_SCALAR_READ !defined(...) && ... など)。
            if (token.Kind == HlslSyntaxKind.IdentifierToken && token.TextIs("defined"))
            {
                i = CopyDefinedOperand(tokens, i, result);
                continue;
            }

            if (token.Kind != HlslSyntaxKind.IdentifierToken
                || !_macros.TryGetValue(token.Text, out MacroDefinition? macro)
                || IsMacroBeingExpanded(macro.Name))
            {
                result.Add(token);
                continue;
            }

            NoteMergedMacroUse(macro.Name);

            if (!macro.IsFunctionLike)
            {
                _sources.Add(new TokenSource([], IsFile: false, MacroName: macro.Name, IncludePath: null));
                result.AddRange(ExpandTokenList(macro.Body, depth + 1));
                _sources.RemoveAt(_sources.Count - 1);
                continue;
            }

            if (i + 1 >= tokens.Length || tokens[i + 1].Kind != HlslSyntaxKind.OpenParenToken)
            {
                result.Add(token);
                continue;
            }

            int index = i + 2;
            ImmutableArray<ImmutableArray<HlslSyntaxToken>>? arguments =
                ReadArgumentsFromList(tokens, ref index);

            if (arguments is null || !macro.AcceptsArgumentCount(arguments.Value.Length))
            {
                result.Add(token);
                continue;
            }

            // 実引数の展開は、このマクロを「展開中」として記録する前に行う。
            //
            // 記録してから実引数を展開すると、実引数の中に同じマクロの呼び出しがある場合に
            // それが再帰とみなされて展開されなくなる。
            // C の規則では、マクロ名が展開を抑止されるのは自分自身の置換結果の中だけで、
            // 実引数の中は対象外である。実引数は置換より前に展開されるため、
            // その時点で外側のマクロの展開はまだ始まっていない。
            //
            // Unity の HDRP には
            // CALL_MERGE_NAME(CALL_MERGE_NAME(sampler, name), 0)
            // という入れ子の呼び出しが実在し、これを扱えないと
            // 連結が成立せず該当のヘッダ以降が丸ごと解析できなくなる。
            ImmutableArray<HlslSyntaxToken> substituted = SubstituteMacroBody(macro, arguments.Value, token);

            _sources.Add(new TokenSource([], IsFile: false, MacroName: macro.Name, IncludePath: null));
            result.AddRange(ExpandTokenList(substituted, depth + 1));
            _sources.RemoveAt(_sources.Count - 1);

            i = index - 1;
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// <c>defined</c> とその被演算子を、展開せずにそのまま複写する。
    /// </summary>
    /// <param name="tokens">対象のトークン列。</param>
    /// <param name="index"><c>defined</c> の位置。</param>
    /// <param name="result">複写先。</param>
    /// <returns>複写し終えた最後のトークンの位置。</returns>
    /// <remarks>
    /// <c>defined X</c> と <c>defined ( X )</c> の 2 つの形に対応する。
    /// 形が崩れている場合は <c>defined</c> だけを複写して呼び出し元へ戻す。
    /// 誤った入力に対して読み進めすぎないためである。
    /// </remarks>
    private static int CopyDefinedOperand(
        ImmutableArray<HlslSyntaxToken> tokens,
        int index,
        ImmutableArray<HlslSyntaxToken>.Builder result)
    {
        result.Add(tokens[index]);

        int next = index + 1;

        if (next < tokens.Length && tokens[next].Kind == HlslSyntaxKind.OpenParenToken)
        {
            result.Add(tokens[next]);
            next++;

            if (next < tokens.Length && tokens[next].Kind == HlslSyntaxKind.IdentifierToken)
            {
                result.Add(tokens[next]);
                next++;

                if (next < tokens.Length && tokens[next].Kind == HlslSyntaxKind.CloseParenToken)
                {
                    result.Add(tokens[next]);
                    return next;
                }
            }

            return next - 1;
        }

        if (next < tokens.Length && tokens[next].Kind == HlslSyntaxKind.IdentifierToken)
        {
            result.Add(tokens[next]);
            return next;
        }

        return index;
    }

    /// <summary>トークン列から関数形式マクロの実引数を読み取る。</summary>
    /// <param name="tokens">対象のトークン列。</param>
    /// <param name="index">開き括弧の次の位置。読み終えた位置を返す。</param>
    /// <returns>引数ごとのトークン列。括弧が閉じられていない場合は <see langword="null"/>。</returns>
    private static ImmutableArray<ImmutableArray<HlslSyntaxToken>>? ReadArgumentsFromList(
        ImmutableArray<HlslSyntaxToken> tokens,
        ref int index)
    {
        ImmutableArray<ImmutableArray<HlslSyntaxToken>>.Builder arguments =
            ImmutableArray.CreateBuilder<ImmutableArray<HlslSyntaxToken>>();
        ImmutableArray<HlslSyntaxToken>.Builder current = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        int depth = 0;

        while (index < tokens.Length)
        {
            HlslSyntaxToken token = tokens[index];

            if (token.Kind == HlslSyntaxKind.CloseParenToken && depth == 0)
            {
                index++;

                if (current.Count > 0 || arguments.Count > 0)
                {
                    arguments.Add(current.ToImmutable());
                }

                return arguments.ToImmutable();
            }

            if (token.Kind == HlslSyntaxKind.CommaToken && depth == 0)
            {
                arguments.Add(current.ToImmutable());
                current.Clear();
                index++;
                continue;
            }

            if (token.Kind == HlslSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (token.Kind == HlslSyntaxKind.CloseParenToken)
            {
                depth--;
            }

            current.Add(token);
            index++;
        }

        return null;
    }

    /// <summary>
    /// トークン列の位置情報を呼び出し位置へ移す。
    /// </summary>
    /// <param name="tokens">対象のトークン列。</param>
    /// <param name="callSite">呼び出し位置のトークン。</param>
    /// <returns>位置を移したトークン列。</returns>
    /// <remarks>
    /// <para>
    /// <b>診断は利用者がコードを書いた場所に出さなければならない。</b>
    /// マクロ定義の中を指しても、利用者はそこを直せないし直すべきでもない。
    /// そのため展開結果のトークンには、マクロを呼び出した位置を与える。
    /// </para>
    /// <para>
    /// ただし<b>利用者のファイルが書いたマクロ</b>なら、本体も利用者が直せる。
    /// 本体での位置を残しておき、報告に使えるようにする
    /// (<see cref="HlslSyntaxToken.MacroDefinitionSpan"/>)。
    /// </para>
    /// </remarks>
    private ImmutableArray<HlslSyntaxToken> RelocateTokens(
        ImmutableArray<HlslSyntaxToken> tokens,
        HlslSyntaxToken callSite)
    {
        // 呼び出しが利用者のファイルにある場合にだけ、本体の位置を残す。
        // Unity のヘッダの中で展開されたコードは、そもそも報告しない場所である。
        bool ownCallSite = IsUserFile(callSite.Source.FilePath);

        return [.. tokens.Select(t => t.RelocateToMacroCallSite(callSite.Source, callSite.Span, ownCallSite))];
    }

    /// <summary>
    /// マクロの本体のトークンに、本体での位置を付けたものを返す。
    /// </summary>
    /// <param name="macro">対象のマクロ。</param>
    /// <returns>位置を付けたトークン列。付ける必要が無ければ本体そのもの。</returns>
    /// <remarks>
    /// <para>
    /// 利用者のファイルが書いたマクロだけを対象にする。
    /// Unity や外部パッケージのヘッダの本体は利用者に直しようがない。
    /// </para>
    /// <para>
    /// <b>展開のたびに作り直さない。</b>
    /// 本体は変わらないので、1 度作れば足りる。
    /// Unity のヘッダはマクロを大量に展開するため、
    /// ここで毎回トークンを作り直すと展開の費用がそのまま増える。
    /// </para>
    /// <para>
    /// 覚えるのはこの展開器の中だけである。
    /// マクロ定義は取り込みの記録として使い回されるので、そこへ書き込むと
    /// 別のファイルを解析したときに、このファイルを指す位置が残る。
    /// </para>
    /// </remarks>
    private ImmutableArray<HlslSyntaxToken> MarkMacroBody(MacroDefinition macro)
    {
        if (_markedBodies.TryGetValue(macro, out ImmutableArray<HlslSyntaxToken> marked))
        {
            return marked;
        }

        marked = macro.Body.Any(IsWrittenInUserFile)
            ? [.. macro.Body.Select(t => IsWrittenInUserFile(t) ? t.WithMacroDefinitionSpan(t.Span, t.Source) : t)]
            : macro.Body;

        _markedBodies[macro] = marked;
        return marked;
    }
}
