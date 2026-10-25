using System.Collections.Immutable;
using Shaderlyn.CFamily;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.CFamily.Preprocessing;

/// <summary>
/// マクロ展開。
/// </summary>
internal sealed partial class ShaderPreprocessor
{
    // --------------------------------------------------------------------
    // マクロ展開
    // --------------------------------------------------------------------

    /// <summary>
    /// 現在位置の識別子が、コンパイラが定義する <c>__LINE__</c> / <c>__FILE__</c> なら展開する。
    /// </summary>
    /// <param name="nameToken">識別子のトークン。</param>
    /// <returns>展開した場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// FXC も DXC も C のプリプロセッサと同じく定義する。展開しないと、宣言されていない名前 (CF0310) になる
    /// (Unreal の D3DCommon.ush の <c>UE_DEBUG_BREAK</c>)。
    /// </remarks>
    private bool TryExpandPredefinedMacro(ShaderSyntaxToken nameToken)
    {
        ShaderSyntaxToken? replacement = nameToken.Text switch
        {
            "__LINE__" => CreateNumericToken(nameToken, (nameToken.GetLocation()?.LineSpan.Start.Line ?? 0) + 1),
            "__FILE__" => CreateStringToken(nameToken.Source.FilePath ?? string.Empty, nameToken),
            _ => null,
        };

        if (replacement is null)
        {
            return false;
        }

        TakeToken();
        PushExpansionSource([replacement], nameToken.Text);
        return true;
    }

    /// <summary>
    /// 現在位置の識別子がマクロなら展開する。
    /// </summary>
    /// <returns>展開した場合は <see langword="true"/>。</returns>
    private bool TryExpandMacro()
    {
        ShaderSyntaxToken nameToken = PeekToken()!;

        if (!_macros.TryGetValue(nameToken.Text, out MacroDefinition? macro))
        {
            return TryExpandPredefinedMacro(nameToken);
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
        ImmutableArray<ImmutableArray<ShaderSyntaxToken>>? arguments = ReadMacroArguments(nameToken);

        if (arguments is null)
        {
            return true;
        }

        if (!macro.AcceptsArgumentCount(arguments.Value.Length))
        {
            Report(ShaderDescriptors.PreprocessorError, nameToken.GetLocation(),
                $"マクロ '{macro.Name}' の引数の個数が一致しません (期待 {macro.Parameters.Length} 個、実際 {arguments.Value.Length} 個)。");
            return true;
        }

        ImmutableArray<ShaderSyntaxToken> substituted = SubstituteMacroBody(macro, arguments.Value, nameToken);
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
                return source.Tokens[start].Kind == ShaderSyntaxKind.OpenParenToken;
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
    private ImmutableArray<ImmutableArray<ShaderSyntaxToken>>? ReadMacroArguments(ShaderSyntaxToken nameToken)
    {
        // 開き括弧を消費する。
        TakeToken();

        ImmutableArray<ImmutableArray<ShaderSyntaxToken>>.Builder arguments =
            ImmutableArray.CreateBuilder<ImmutableArray<ShaderSyntaxToken>>();
        ImmutableArray<ShaderSyntaxToken>.Builder current = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        int depth = 0;

        while (true)
        {
            ShaderSyntaxToken? token = PeekToken();

            if (token is null)
            {
                Report(ShaderDescriptors.PreprocessorError, nameToken.GetLocation(),
                    $"マクロ '{nameToken.Text}' の引数の括弧が閉じられていません。");
                return null;
            }

            if (token.Kind == ShaderSyntaxKind.CloseParenToken && depth == 0)
            {
                TakeToken();

                if (current.Count > 0 || arguments.Count > 0)
                {
                    arguments.Add(current.ToImmutable());
                }

                return arguments.ToImmutable();
            }

            if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
            {
                TakeToken();
                arguments.Add(current.ToImmutable());
                current.Clear();
                continue;
            }

            if (token.Kind is ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (token.Kind is ShaderSyntaxKind.CloseParenToken)
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
    private ImmutableArray<ShaderSyntaxToken> SubstituteMacroBody(
        MacroDefinition macro,
        ImmutableArray<ImmutableArray<ShaderSyntaxToken>> arguments,
        ShaderSyntaxToken callSite)
    {
        Dictionary<string, ImmutableArray<ShaderSyntaxToken>> bindings = new(StringComparer.Ordinal);

        for (int i = 0; i < macro.Parameters.Length; i++)
        {
            bindings[macro.Parameters[i]] = i < arguments.Length ? arguments[i] : [];
        }

        if (macro.IsVariadic)
        {
            ImmutableArray<ShaderSyntaxToken>.Builder variadic = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

            for (int i = macro.Parameters.Length; i < arguments.Length; i++)
            {
                if (variadic.Count > 0)
                {
                    variadic.Add(CreateSyntheticToken(ShaderSyntaxKind.CommaToken, ",", callSite));
                }

                variadic.AddRange(arguments[i]);
            }

            bindings[MacroDefinition.VariadicArgumentName] = variadic.ToImmutable();
        }

        ImmutableArray<ShaderSyntaxToken>.Builder result = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        // 実引数として埋め込んだトークンの位置と、解析しているファイルで書かれた位置 (RelocateSubstituted)。
        Dictionary<int, TextSpan?> argumentSpans = [];

        // 本体での位置を付けた形を使う。実引数として埋め込むトークンには付かない。
        ImmutableArray<ShaderSyntaxToken> body = MarkMacroBody(macro);

        for (int i = 0; i < body.Length; i++)
        {
            ShaderSyntaxToken token = body[i];

            // # による文字列化
            if (token.Kind == ShaderSyntaxKind.HashToken
                && i + 1 < body.Length
                && body[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                && bindings.TryGetValue(body[i + 1].Text, out ImmutableArray<ShaderSyntaxToken> toStringize))
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
            if (i + 2 < body.Length && body[i + 1].Kind == ShaderSyntaxKind.HashHashToken)
            {
                ImmutableArray<ShaderSyntaxToken> pasted = ResolveOperand(token, bindings);

                while (i + 2 < body.Length && body[i + 1].Kind == ShaderSyntaxKind.HashHashToken)
                {
                    pasted = Paste(pasted, ResolveOperand(body[i + 2], bindings), callSite);
                    i += 2;

                    // 数字に続けて書いた名前 (Name##1x の 1x) は、C のプリプロセッサでは 1 つのトークン (pp-number) である。
                    // 字句解析は 1 と x に分けるので、続けて連結する。分けたままだと DstBuffer1 x になる (Unreal の ByteBuffer.usf)。
                    while (i + 1 < body.Length
                           && body[i].Kind == ShaderSyntaxKind.NumericLiteralToken
                           && body[i + 1].Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken
                           && ReferenceEquals(body[i].Source, body[i + 1].Source)
                           && body[i].Span.End == body[i + 1].Span.Start
                           && !bindings.ContainsKey(body[i + 1].Text))
                    {
                        pasted = Paste(pasted, [body[i + 1]], callSite);
                        i++;
                    }
                }

                result.AddRange(pasted);
                continue;
            }

            if (token.Kind == ShaderSyntaxKind.IdentifierToken
                && bindings.TryGetValue(token.Text, out ImmutableArray<ShaderSyntaxToken> replacement))
            {
                // 引数は展開してから埋め込む (C の規則)。
                foreach (ShaderSyntaxToken argument in ExpandTokenList(replacement, depth: 0))
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
    private TextSpan? WrittenArgumentSpan(ShaderSyntaxToken token)
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
    /// <b>実引数は、利用者が呼び出し位置に書いたコードである</b> (<see cref="ShaderSyntaxToken.MacroArgumentSpan"/>)。
    /// 位置 (<see cref="ShaderSyntaxToken.Span"/>) は本体と同じく呼び出し位置へ移し、書かれた位置を別に残す。
    /// 構成ごとの木の突き合わせは位置を鍵にしているので、実引数だけを元の位置に残すと対応が崩れる。
    /// </para>
    /// <para>
    /// 書かれた位置は、呼び出しも利用者のファイルにある場合にだけ残す。
    /// <c>#</c> や <c>##</c> で作ったトークンは本体の側に数える。書かれたとおりのトークンではない。
    /// </para>
    /// </remarks>
    private ImmutableArray<ShaderSyntaxToken> RelocateSubstituted(
        ImmutableArray<ShaderSyntaxToken> tokens,
        Dictionary<int, TextSpan?> arguments,
        ShaderSyntaxToken callSite)
    {
        bool ownCallSite = IsUserFile(callSite.Source.FilePath);

        if (arguments.Count == 0 || !ownCallSite)
        {
            return RelocateTokens(tokens, callSite);
        }

        ImmutableArray<ShaderSyntaxToken>.Builder relocated = ImmutableArray.CreateBuilder<ShaderSyntaxToken>(tokens.Length);

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
    private static ImmutableArray<ShaderSyntaxToken> ResolveOperand(
        ShaderSyntaxToken token,
        Dictionary<string, ImmutableArray<ShaderSyntaxToken>> bindings)
        => token.Kind == ShaderSyntaxKind.IdentifierToken
            && bindings.TryGetValue(token.Text, out ImmutableArray<ShaderSyntaxToken> replacement)
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
    private ImmutableArray<ShaderSyntaxToken> Paste(
        ImmutableArray<ShaderSyntaxToken> left,
        ImmutableArray<ShaderSyntaxToken> right,
        ShaderSyntaxToken callSite)
    {
        if (left.IsEmpty)
        {
            return right;
        }

        if (right.IsEmpty)
        {
            return left;
        }

        ImmutableArray<ShaderSyntaxToken>.Builder result = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
        result.AddRange(left.Take(left.Length - 1));

        string fused = left[^1].Text + right[0].Text;
        SourceText fusedText = SourceText.From(fused, "<トークン連結>");
        ShaderLexer lexer = new(fusedText);
        ImmutableArray<ShaderSyntaxToken> fusedTokens = lexer.Lex(out _);

        foreach (ShaderSyntaxToken token in fusedTokens)
        {
            if (token.Kind != ShaderSyntaxKind.EndOfFileToken)
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
    private static string Stringize(ImmutableArray<ShaderSyntaxToken> tokens)
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
    private ImmutableArray<ShaderSyntaxToken> ExpandTokenList(ImmutableArray<ShaderSyntaxToken> tokens, int depth)
    {
        if (depth >= _options.MaxExpansionDepth)
        {
            return tokens;
        }

        ImmutableArray<ShaderSyntaxToken>.Builder result = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        for (int i = 0; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            // defined の被演算子は展開してはならない。
            // 展開してしまうと defined(SHADER_API_D3D11) が defined(1) になり、
            // 「定義されているか」という判定そのものが成り立たなくなる。
            // マクロの本体に defined を含む書き方は Unity が実際に使っている
            // (#define FORCE_SHADOW_SCALAR_READ !defined(...) && ... など)。
            if (token.Kind == ShaderSyntaxKind.IdentifierToken && token.TextIs("defined"))
            {
                i = CopyDefinedOperand(tokens, i, result);
                continue;
            }

            if (token.Kind != ShaderSyntaxKind.IdentifierToken
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

            if (i + 1 >= tokens.Length || tokens[i + 1].Kind != ShaderSyntaxKind.OpenParenToken)
            {
                result.Add(token);
                continue;
            }

            int index = i + 2;
            ImmutableArray<ImmutableArray<ShaderSyntaxToken>>? arguments =
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
            ImmutableArray<ShaderSyntaxToken> substituted = SubstituteMacroBody(macro, arguments.Value, token);

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
        ImmutableArray<ShaderSyntaxToken> tokens,
        int index,
        ImmutableArray<ShaderSyntaxToken>.Builder result)
    {
        result.Add(tokens[index]);

        int next = index + 1;

        if (next < tokens.Length && tokens[next].Kind == ShaderSyntaxKind.OpenParenToken)
        {
            result.Add(tokens[next]);
            next++;

            if (next < tokens.Length && tokens[next].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                result.Add(tokens[next]);
                next++;

                if (next < tokens.Length && tokens[next].Kind == ShaderSyntaxKind.CloseParenToken)
                {
                    result.Add(tokens[next]);
                    return next;
                }
            }

            return next - 1;
        }

        if (next < tokens.Length && tokens[next].Kind == ShaderSyntaxKind.IdentifierToken)
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
    private static ImmutableArray<ImmutableArray<ShaderSyntaxToken>>? ReadArgumentsFromList(
        ImmutableArray<ShaderSyntaxToken> tokens,
        ref int index)
    {
        ImmutableArray<ImmutableArray<ShaderSyntaxToken>>.Builder arguments =
            ImmutableArray.CreateBuilder<ImmutableArray<ShaderSyntaxToken>>();
        ImmutableArray<ShaderSyntaxToken>.Builder current = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        int depth = 0;

        while (index < tokens.Length)
        {
            ShaderSyntaxToken token = tokens[index];

            if (token.Kind == ShaderSyntaxKind.CloseParenToken && depth == 0)
            {
                index++;

                if (current.Count > 0 || arguments.Count > 0)
                {
                    arguments.Add(current.ToImmutable());
                }

                return arguments.ToImmutable();
            }

            if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
            {
                arguments.Add(current.ToImmutable());
                current.Clear();
                index++;
                continue;
            }

            if (token.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (token.Kind == ShaderSyntaxKind.CloseParenToken)
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
    /// (<see cref="ShaderSyntaxToken.MacroDefinitionSpan"/>)。
    /// </para>
    /// </remarks>
    private ImmutableArray<ShaderSyntaxToken> RelocateTokens(
        ImmutableArray<ShaderSyntaxToken> tokens,
        ShaderSyntaxToken callSite)
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
    private ImmutableArray<ShaderSyntaxToken> MarkMacroBody(MacroDefinition macro)
    {
        if (_markedBodies.TryGetValue(macro, out ImmutableArray<ShaderSyntaxToken> marked))
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
