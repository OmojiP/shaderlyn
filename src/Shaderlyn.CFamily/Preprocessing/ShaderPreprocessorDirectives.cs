using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.CFamily.Preprocessing;

/// <summary>
/// 指令の処理。行頭の # を読み分け、それぞれの指令へ振り分ける。
/// </summary>
internal sealed partial class ShaderPreprocessor
{
    // --------------------------------------------------------------------
    // 指令の処理
    // --------------------------------------------------------------------

    /// <summary>
    /// プリプロセッサ指令を 1 つ処理する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 条件分岐に関わる指令 (<c>#if</c> 系) は非活性領域でも処理しなければならない。
    /// 入れ子の対応を追えなくなり、<c>#endif</c> の位置を見失うためである。
    /// それ以外の指令は非活性領域では読み飛ばす。
    /// </para>
    /// </remarks>
    private void ProcessDirective()
    {
        TakeToken();

        ShaderSyntaxToken? next = PeekInCurrentSource();
        if (next is null || next.IsAtLineStart)
        {
            // 内容のない # だけの行。C では空指令として許される。
            return;
        }

        string directive = next.Kind == ShaderSyntaxKind.IdentifierToken ? next.Text : string.Empty;

        switch (directive)
        {
            case "if":
            case "ifdef":
            case "ifndef":
            case "elif":
            case "else":
            case "endif":
                ProcessConditionalDirective(TakeToken(), directive);
                return;

            // 通る分岐でも読み飛ばす分岐でも数える。どちらも、その連なりのキーワードでマクロ表や取り込みが変わる。
            case "define":
            case "undef":
                NoteMacroKeywords(NameAfterDirective());
                break;

            case "include":
                NoteMacroAffectingSymbols();
                break;

            case var _ when _options.AdditionalIncludeDirectives.Contains(directive):
                NoteMacroAffectingSymbols();
                break;
        }

        if (IsSkipping)
        {
            // 読み飛ばす分岐の #define も、名前と中身と条件だけは覚える。
            // 別の分岐の定義と中身が食い違えば、両方が有効な構成では再定義になる。
            SkipDirectiveLine(directive == "define");
            return;
        }

        ShaderSyntaxToken directiveToken = TakeToken();

        // 環境が受け付けない指令 (Unity の #line など)。
        if (_options.UnsupportedDirectives.Contains(directive))
        {
            Report(ShaderDescriptors.UnsupportedDeclaration, directiveToken.GetLocation(), $"#{directive} 指令", _options.DescribeUnavailable());
            SkipDirectiveLine();
            return;
        }

        // 環境が足した取り込みの指令 (Unity の #include_with_pragmas など)。
        if (_options.AdditionalIncludeDirectives.Contains(directive))
        {
            ProcessInclude(directiveToken, directive);
            return;
        }

        switch (directive)
        {
            case "define":
                ProcessDefine(directiveToken);
                break;

            case "undef":
                ProcessUndef(directiveToken);
                break;

            case "include":
                ProcessInclude(directiveToken);
                break;

            case "pragma":
                ProcessPragma();
                break;

            // #error / #warning は解析には影響しないため読み飛ばす。
            // #error を診断として報告しないのは、それが非活性領域に置かれる
            // 「この構成では使えない」という印であることが多く、
            // 単一構成での展開では偽の指摘になりやすいためである。
            // #line は報告の位置を付け替えるだけで、展開には影響しない。
            // 受け付けない環境は UnsupportedDirectives で報告する。
            case "error":
            case "warning":
            case "version":
            case "extension":
            case "line":
                SkipDirectiveLine();
                break;

            default:
                Report(
                    ShaderDescriptors.PreprocessorError,
                    directiveToken.GetLocation(),
                    $"不明なプリプロセッサ指令です: '#{directiveToken.Text}'");
                SkipDirectiveLine();
                break;
        }
    }

    /// <summary>条件分岐に関わる指令を処理する。</summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    /// <param name="directive">指令名。</param>
    private void ProcessConditionalDirective(ShaderSyntaxToken directiveToken, string directive)
    {
        switch (directive)
        {
            case "if":
            {
                // #if defined(X) は #ifdef X と同じ形である。
                // 式であることそのものは、並べられない理由にならない。
                ImmutableArray<ShaderSyntaxToken> line = ReadDirectiveLine();
                int recordedBefore = _conditionalIdentifiers.Count;
                SymbolCondition? read = TryReadDefinedCondition(line);
                RecordBareKeywords(line, recordedBefore);
                // どの構成でも成り立たない条件 (#if _A == 2 など) の分岐は、どの構成でも通らない。
                // 構成によらない条件と同じく、並べる必要も、そのシンボルを有効にした構成を作る必要も無い。
                bool neverTrue = NoteIfNeverTrue(line, directiveToken, _conditionals.Count);

                // 文の途中で分かれる領域は、分岐ごとに文を複製して並べる。複製したら領域は読み終えている。
                if (!neverTrue
                    && read is { IsUnknown: false } readable
                    && readable.EnumerateSymbols().Any()
                    && TryHoistRegion(directiveToken, readable))
                {
                    break;
                }

                SymbolCondition? kept = neverTrue ? null : ChooseKeptCondition(read, directiveToken);

                // キーワード以外を値に解いたらキーワードが残らなかった条件 (defined(_A) && SHADER_TARGET >= 45 で
                // SHADER_TARGET が 35 のときなど) は、どの構成でも同じ分岐を通る。並べられなかったとは数えない。
                // 構成によって定義が変わるマクロを値に解いていれば、その値はこの構成でのものにすぎない。
                bool isStatic = neverTrue
                                || (read is { IsUnknown: false } resolved
                                    && !resolved.EnumerateSymbols().Any()
                                    && !ReferencesConfigurationDependentMacro(line));

                // どの構成でも同じ分岐を通るので、その行のキーワードは構成ごとに展開し直さなくてよい。
                // 未定義の名前は 0 として解く (C の規則。fxc と DXC も同じ)。
                if (isStatic)
                {
                    foreach (ShaderSyntaxToken token in line)
                    {
                        if (token.Kind == ShaderSyntaxKind.IdentifierToken && _options.BothBranchSymbols.Contains(token.Text))
                        {
                            AddMergedSymbols(SymbolCondition.Symbol(token.Text));
                        }
                    }
                }

                if (kept is null && !isStatic)
                {
                    // #define しか無い領域として記録済みなら、巻き上げで補えたかの判断をここで確定させない。
                    // #if !defined(_A) && !defined(_B) のように、別々の #ifdef で書き分けた定義の残りを受け持つ形である。
                    ImmutableArray<string>? hoistable = _hoistableRegionMacros.TryGetValue(directiveToken, out ImmutableArray<string> macros)
                        ? macros
                        : null;

                    DeclineBothBranchSymbolsIn(
                        line,
                        directiveToken,
                        read is null || read.Value.IsUnknown ? BothBranchDeclineReason.UnreadableCondition : null,
                        _conditionals.Count,
                        hoistable);

                    NoteMergedMacroConditionUse(line.Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text));
                }

                BeginConditional(directiveToken, EvaluateCondition(line), kept);
                _conditionals[^1].IsConfigurationDependent = !isStatic && ReferencesConfigurationDependentName(line);
                AddConditionSymbols(_conditionals[^1], line);
                NoteRegionDependencies(_conditionals[^1], line.Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text), readsValues: true);
                EnterPathBranch(_conditionals[^1], directiveToken, TryReadPathCondition(line, directiveToken));
                break;
            }

            case "ifdef":
            case "ifndef":
            {
                bool expectDefined = directive == "ifdef";
                bool value = EvaluateDefinedLine(directiveToken, expectDefined, out string? symbol);

                // 文の途中で分かれる領域は、分岐ごとに文を複製して並べる。条件の読み方は ChooseKeptCondition と同じ。
                SymbolCondition? defined = symbol is null
                    ? null
                    : _options.BothBranchSymbols.Contains(symbol)
                        ? SymbolOrDefinition(symbol)
                        : GetDefinedCondition(symbol);

                if (defined is { } present && TryHoistRegion(directiveToken, expectDefined ? present : present.Negate()))
                {
                    break;
                }

                BeginConditional(directiveToken, value, ChooseKeptCondition(symbol, expectDefined, directiveToken));

                // インクルードガード: ヘッダが自分で定義した名前のために、2 度目の取り込みの中身を飛ばした。
                // #pragma once と同じく、飛ばした取り込みの条件をヘッダの範囲に足す (NoteSkippedReinclude)。
                if (!expectDefined
                    && !value
                    && symbol is not null
                    && _conditionals[^1].KeptCondition is null
                    && !IsInRootFile(directiveToken)
                    && directiveToken.Source.FilePath is { } guardedFile
                    && _macros.TryGetValue(symbol, out MacroDefinition? guard)
                    && string.Equals(guard.NameToken.Source.FilePath, guardedFile, StringComparison.OrdinalIgnoreCase))
                {
                    NoteSkippedReinclude(guardedFile);
                }

                // 名前が読めなかった行は、構成によって変わるものとして扱う (並べない側へ倒す)。
                _conditionals[^1].IsConfigurationDependent = symbol is null || IsConfigurationDependentName(symbol);

                if (symbol is not null)
                {
                    _conditionals[^1].ConditionSymbols.Add(symbol);
                    NoteRegionDependencies(_conditionals[^1], [symbol], readsValues: false);

                    if (_conditionals[^1].KeptCondition is null && !_conditionals[^1].IsParentSkipping)
                    {
                        NoteMergedMacroConditionUse([symbol]);
                    }
                }
                else
                {
                    _conditionals[^1].RegionDependsOnMacros = true;
                }

                EnterPathBranch(_conditionals[^1], directiveToken, ReadPathSymbol(symbol, expectDefined, directiveToken));
                break;
            }

            case "elif":
                ProcessElif(directiveToken);
                break;

            case "else":
                ProcessElse(directiveToken);
                break;

            case "endif":
                ProcessEndif(directiveToken);
                break;
        }
    }

    /// <summary>新しい条件ブロックを開始する。</summary>
    /// <param name="directive">指令のトークン。</param>
    /// <param name="condition">条件の評価結果。</param>
    /// <param name="kept">
    /// 両方の分岐を残す場合の、最初の分岐の条件。残さない場合は <see langword="null"/>。
    /// </param>
    private void BeginConditional(ShaderSyntaxToken directive, bool condition, SymbolCondition? kept)
    {
        bool parentSkipping = IsSkipping;
        bool active = condition && !parentSkipping;

        // 外側と合わせて決して成り立たない分岐は並べない。領域としては残し、次の分岐に条件を付ける。
        bool impossible = kept is { } first && !IsPossibleBranch(first, _conditionals.Count);

        // 入れ子に入る前に、外側の条件が付く範囲をここで区切る。
        // 区切らないと、入れ子より手前に書かれたコードが条件を失う。
        // 間に構成によらない条件が挟まっていても、外側の並べた分岐の範囲は開いている。
        if (kept is not null && _conditionals.Any(state => state.KeptCondition is not null))
        {
            CloseConditionalRange();

            foreach (ConditionalState outer in _conditionals)
            {
                outer.BranchSplit |= outer.BranchStart is not null;
            }
        }

        _conditionals.Add(new ConditionalState(directive)
        {
            IsBranchActive = active && !impossible,
            HasTakenBranch = active && !impossible,
            IsParentSkipping = parentSkipping,
            KeptCondition = impossible ? null : kept,
            Folded = kept is not null,

            // 最初の分岐が外れたときに通るのは、その条件の裏返しである。
            Remaining = kept?.Negate() ?? SymbolCondition.Always,
        });

        // 両方を残す条件に入った。ここから先のトークンには条件が付く。
        if (kept is not null)
        {
            OpenConditionalRange();
            StartBranchRange(_conditionals[^1]);

            MergedRegion region = new(directive.Source.FilePath, directive.Span.Start);

            _mergedRegions.Add(region);
            Record(recording => recording.MergedRegions.Add(region));

            AddMergedSymbols(kept.Value);
        }

        StartInactiveBranch(_conditionals[^1], directive);
    }

    /// <summary><c>#elif</c> を処理する。</summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    private void ProcessElif(ShaderSyntaxToken directiveToken)
    {
        if (_conditionals.Count == 0)
        {
            Report(ShaderDescriptors.PreprocessorError, directiveToken.GetLocation(),
                "対応する #if がない #elif です。");
            SkipDirectiveLine();
            return;
        }

        ConditionalState state = _conditionals[^1];

        if (state.SeenElse)
        {
            Report(ShaderDescriptors.PreprocessorError, directiveToken.GetLocation(),
                "#else より後に #elif は書けません。");
        }

        // 既にいずれかの分岐が採用されている場合、条件式を評価する必要はないが、
        // 行は読み飛ばさなければならない。
        ImmutableArray<ShaderSyntaxToken> line = ReadDirectiveLine();
        int recordedBefore = _conditionalIdentifiers.Count;

        // どの構成でも成り立たない分岐のために、構成ごとの展開は要らない。
        bool neverTrue = NoteIfNeverTrue(line, directiveToken, _conditionals.Count - 1);

        EndInactiveBranch(state, directiveToken);
        AddConditionSymbols(state, line);
        NoteRegionDependencies(state, line.Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text), readsValues: true);
        state.IsConfigurationDependent |= !neverTrue && ReferencesConfigurationDependentName(line);

        if (state.Folded)
        {
            EnterFoldedBranch(state, neverTrue ? NeverBranch(state) : ReadElifCondition(state, line, directiveToken));
        }
        else
        {
            if (!neverTrue)
            {
                // #define しか無い領域の #elif は、#if と同じく、使う文を定義ごとに複製して補えることがある。
                ImmutableArray<string>? hoistable = _hoistableRegionMacros.TryGetValue(state.Directive, out ImmutableArray<string> macros)
                    ? macros
                    : null;

                DeclineBothBranchSymbolsIn(
                    line, directiveToken, BothBranchDeclineReason.FollowsUnmergedBranch, _conditionals.Count - 1, hoistable);
            }

            if (!state.IsParentSkipping)
            {
                NoteMergedMacroConditionUse(line.Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text));
            }

            bool condition = EvaluateCondition(line);

            state.IsBranchActive = condition && !state.HasTakenBranch && !state.IsParentSkipping;
            state.HasTakenBranch = state.HasTakenBranch || state.IsBranchActive;
        }

        RecordBareKeywords(line, recordedBefore);
        StartInactiveBranch(state, directiveToken);
        EnterPathBranch(state, directiveToken, TryReadPathCondition(line, directiveToken));
    }

    /// <summary>
    /// 条件式の行に <c>defined</c> を付けずに書かれた、宣言されたキーワードを、条件で参照された名前として記録する。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="recordedBefore">この行を読む前の、記録した名前の数。この行で記録済みのものは重ねない。</param>
    /// <remarks>
    /// <para>
    /// 条件を記号として読む経路 (<see cref="TryReadDefinedCondition"/>) は、並べる対象のキーワードしか記録しない。
    /// 並べ直しのために外したキーワード (<c>MergedMacroConflicts</c>) を <c>#if _BLOOM_HQ</c> の形で見ていると、どこにも記録されず、
    /// 「条件のどこにも現れない」(HL0331) と報告され、そのキーワードを有効にした構成も作られなかった。
    /// </para>
    /// <para>
    /// <c>defined(X)</c> の形は、値を解くときに記録している (<see cref="ResolveDefinedOperators"/>)。
    /// </para>
    /// </remarks>
    private void RecordBareKeywords(ImmutableArray<ShaderSyntaxToken> line, int recordedBefore)
    {
        HashSet<ShaderSyntaxToken>? recorded = null;

        for (int i = 0; i < line.Length; i++)
        {
            ShaderSyntaxToken token = line[i];

            if (token.Kind != ShaderSyntaxKind.IdentifierToken
                || !(_options.ConfigurationSymbols.Contains(token.Text)
                     || _options.DeclaredSymbols.Contains(token.Text)
                     || _options.IncludedDeclaredSymbols.Contains(token.Text))
                || IsDefinedOperand(line, i))
            {
                continue;
            }

            recorded ??= [.. _conditionalIdentifiers.Skip(recordedBefore)];

            if (recorded.Add(token))
            {
                RecordConditionalIdentifier(token);
            }
        }
    }

    /// <summary>その位置の名前が <c>defined</c> の被演算子かを判定する。</summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">名前の位置。</param>
    /// <returns><c>defined X</c> / <c>defined(X)</c> の <c>X</c> なら <see langword="true"/>。</returns>
    private static bool IsDefinedOperand(ImmutableArray<ShaderSyntaxToken> line, int index)
        => (index >= 1 && line[index - 1].TextIs("defined"))
           || (index >= 2 && line[index - 1].Kind == ShaderSyntaxKind.OpenParenToken && line[index - 2].TextIs("defined"));

    /// <summary>
    /// 両方の分岐を残す領域で、どの構成でも成り立たない <c>#elif</c> の分岐に付ける条件を返す。
    /// </summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <returns>決して成り立たない条件。残りの分岐の条件は変えない。</returns>
    private static SymbolCondition NeverBranch(ConditionalState state)
        => state.Remaining.And(SymbolCondition.Never);

    /// <summary>
    /// 両方の分岐を残す領域で、<c>#elif</c> の分岐に付ける条件を求める。
    /// </summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <param name="line">条件式の行。</param>
    /// <param name="directive">指令名のトークン。</param>
    /// <returns>この分岐に付ける条件。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>#elif</c> は「それまでの分岐がすべて外れたとき」に通る。</b>
    /// だから条件は、それまでの否定とこの分岐の条件を掛け合わせた形になる。
    /// </para>
    /// <para>
    /// <b>記号として扱えない条件は、値を解いて定数にする。</b>
    /// <c>#elif UNITY_VERSION &gt;= 202200</c> のような条件は、
    /// 単一構成での展開では真偽が決まっている。
    /// 真なら「それまでの否定」がそのまま条件になり、
    /// 偽ならこの分岐はどの構成でも通らない。
    /// </para>
    /// </remarks>
    private SymbolCondition ReadElifCondition(
        ConditionalState state,
        ImmutableArray<ShaderSyntaxToken> line,
        ShaderSyntaxToken directive)
    {
        SymbolCondition condition;

        if (TryReadDefinedCondition(line) is { } written && !written.IsUnknown)
        {
            condition = written;
        }
        else
        {
            // 記号として扱えなかった。この式の中のシンボルは、
            // ここでは 1 つの構成に絞っている。
            DeclineBothBranchSymbolsIn(line, directive, BothBranchDeclineReason.UnreadableCondition, _conditionals.Count - 1);

            condition = EvaluateCondition(line) ? SymbolCondition.Always : SymbolCondition.Never;
        }

        SymbolCondition branch = state.Remaining.And(condition);

        state.Remaining = state.Remaining.And(condition.Negate());

        return branch;
    }

    /// <summary>
    /// 両方の分岐を残す領域で、次の分岐へ移る。
    /// </summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <param name="branch">その分岐に付ける条件。</param>
    /// <remarks>
    /// どの構成でも通らない分岐は、条件を付けずに読み飛ばす。
    /// 同時には定義されないシンボルを外側と合わせて求める分岐も、どの構成でも通らない。
    /// 通らないと分かっているコードを並べても、指せる構成が無い。
    /// </remarks>
    private void EnterFoldedBranch(ConditionalState state, SymbolCondition branch)
    {
        CloseConditionalRange();
        CloseBranchRange(state);

        if (branch.IsNever || !IsPossibleBranch(branch, _conditionals.Count - 1))
        {
            state.KeptCondition = null;
            state.IsBranchActive = false;
        }
        else
        {
            state.KeptCondition = branch;
            AddMergedSymbols(branch);
        }

        OpenConditionalRange();
        StartBranchRange(state);
    }

    /// <summary>並べた分岐の始まりと、その分岐に付く条件を覚える。</summary>
    /// <param name="state">分岐を始めた連なり。</param>
    private void StartBranchRange(ConditionalState state)
    {
        state.BranchStart = state.KeptCondition is not null ? _output.Count : null;
        state.BranchCondition = _options.SymbolConstraints.Apply(CurrentCondition);
        state.BranchSplit = false;
    }

    /// <summary>
    /// 入れ子の <c>#if</c> が区切った並べた分岐について、分岐全体の範囲も記録する。
    /// </summary>
    /// <param name="state">分岐を終える連なり。</param>
    /// <remarks>
    /// <para>
    /// <b>条件の付く範囲は、入れ子の <c>#if</c> の前・中・後に分かれて記録される。</b>
    /// ノードに条件を付けるのは、そのノードがどれか 1 つの範囲の内側にあるときである。
    /// 関数の本体に <c>#if</c> を書くと、関数そのものはどの範囲の内側にも収まらず、条件が付かなかった (「常に」)。
    /// <c>#if A … #elif B</c> の分岐ごとに同じ名前の関数を書くと、それらを同時に存在すると判断し、
    /// 重複した宣言 (HL0314) と報告していた (HDRP の MotionBlurMotionVecPrep.compute)。
    /// 分岐全体の範囲を分岐の条件で足せば、分岐をまたぐノードにも条件が付く。
    /// 入れ子の中の範囲には、外側の条件を含む条件が付いているので、重ねて掛けても変わらない。
    /// </para>
    /// </remarks>
    private void CloseBranchRange(ConditionalState state)
    {
        if (state.BranchStart is { } start && state.BranchSplit && _output.Count > start)
        {
            _conditionalRegions.Add(new ConditionalTokenRange(start, _output.Count - start, state.BranchCondition));
        }

        state.BranchStart = null;
        state.BranchSplit = false;
    }

    /// <summary>
    /// その領域が、構成によって定義が変わるマクロを呼んでいるかを調べる。
    /// </summary>
    /// <param name="start">領域の開始位置。</param>
    /// <param name="end">領域の終了位置 (この位置は含まない)。</param>
    /// <returns>呼んでいれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>並べてよいのは、並べたあとも同じように展開できる場合だけである。</b>
    /// 定義を切り替えている領域を並べない (<c>SwitchesMacros</c>) のと同じ理由が、
    /// その名前を<b>使う</b>側にもある。
    /// </para>
    /// <code>
    /// // 定義は構成ごとに 1 つだけ有効になる (引数の数まで違う)
    /// #define SAMPLE_GI(staticLm, sh, normalWS) ...
    ///
    /// // 呼ぶ側を並べると、有効でない側の呼び出しも残る
    /// #if defined(_SCREEN_SPACE_IRRADIANCE)
    ///     SAMPLE_GI(_ScreenSpaceIrradiance, positionCS.xy)   // 引数 2 個
    /// #else
    ///     SAMPLE_GI(staticLightmapUV, vertexSH, normalWS)    // 引数 3 個
    /// #endif
    /// </code>
    /// <para>
    /// 並べると、有効な定義に対して引数の数が合わない呼び出しが現れ、
    /// そこから先の展開が丸ごと崩れる。実物の URP がこの形をしている。
    /// </para>
    /// </remarks>
    private bool CallsConfigurationDependentMacro(int start, int end)
    {
        if (_conditionallyDefinedMacros.Count == 0)
        {
            return false;
        }

        ImmutableArray<ShaderSyntaxToken> tokens = CurrentSource.Tokens;

        for (int i = start; i < end && i < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken
                && _conditionallyDefinedMacros.Contains(tokens[i].Text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// その条件式に出てくるシンボルを「残せなかった」と記録する。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="directive">指令名のトークン。</param>
    /// <param name="reason">
    /// 残せなかった理由。並べる判断のほうで理由を記録済みなら <see langword="null"/> (二重に数えない)。
    /// </param>
    /// <param name="outer">
    /// 外側の条件の数。<c>#elif</c> では、その連なり自身を数えない。
    /// </param>
    /// <param name="hoistableMacros">
    /// <c>#define</c> しか無い領域の <c>#elif</c> で、その領域が定義するマクロ。
    /// 使う文がすべて巻き上げで補えたら、シンボルを並べ直す。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>まとめなかった条件のシンボルは、まだ構成ごとの展開が要る。</b>
    /// <c>#if</c> の式や、記号として扱えなかった <c>#elif</c> の式は、
    /// 1 つの構成に絞っている。その式が守っている <c>#include</c> の中身は、
    /// 既定の構成では丸ごと見えていない。
    /// </para>
    /// <para>
    /// ここを落とすと、別の場所で同じシンボルをまとめられていた場合に
    /// 「もうバリアントは要らない」と判断され、見えていない宣言を
    /// 「どこにも宣言されていない」と報告することになる。
    /// 実物では <c>#if defined(HDR_COLORSPACE_CONVERSION)</c> で守られた取り込みが
    /// ちょうどこの形をしていた。
    /// </para>
    /// </remarks>
    private void DeclineBothBranchSymbolsIn(
        ImmutableArray<ShaderSyntaxToken> line,
        ShaderSyntaxToken directive,
        BothBranchDeclineReason? reason,
        int outer,
        ImmutableArray<string>? hoistableMacros = null)
    {
        // 構成によらない条件のために読み飛ばしている分岐は、どの構成でも読まれない。
        if (_options.BothBranchSymbols.IsEmpty || IsInStaticSkippedBranch(outer))
        {
            return;
        }

        foreach (ShaderSyntaxToken token in line)
        {
            if (token.Kind == ShaderSyntaxKind.IdentifierToken
                && _options.BothBranchSymbols.Contains(token.Text))
            {
                AddDeclinedSymbol(token.Text, reason, directive, hoistableMacros);
            }
        }

        // 行にキーワードが書かれていなくても、マクロの本体を通してキーワードを見ていることがある。
        // #define VELOCITY_REJECTION defined(ENABLE_MV_REJECTION) を #if VELOCITY_REJECTION で見る形である。
        // 読めなかったのなら、そのキーワードも並べられなかったと数える。
        if (reason is BothBranchDeclineReason.UnreadableCondition)
        {
            foreach (string symbol in CollectSymbolsThroughMacros(line))
            {
                AddDeclinedSymbol(symbol, reason, directive);
            }
        }
    }

    /// <summary>行のマクロの本体を辿って、そこに現れるキーワードを集める。</summary>
    /// <param name="line">条件式の行。</param>
    /// <returns>本体に現れたキーワード。行に直接書かれたものは含まない。</returns>
    /// <remarks>辿るのは構成によって定義が変わるマクロだけで、深さは 8 段までとする。</remarks>
    private HashSet<string> CollectSymbolsThroughMacros(ImmutableArray<ShaderSyntaxToken> line)
    {
        HashSet<string> found = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        Stack<(string Name, int Depth)> pending = new();

        foreach (ShaderSyntaxToken token in line)
        {
            if (token.Kind == ShaderSyntaxKind.IdentifierToken && _conditionallyDefinedMacros.Contains(token.Text))
            {
                pending.Push((token.Text, 0));
            }
        }

        while (pending.Count > 0)
        {
            (string name, int depth) = pending.Pop();

            if (depth > 8 || !visited.Add(name) || !_macros.TryGetValue(name, out MacroDefinition? macro))
            {
                continue;
            }

            foreach (ShaderSyntaxToken token in macro.Body)
            {
                if (token.Kind != ShaderSyntaxKind.IdentifierToken)
                {
                    continue;
                }

                if (_options.BothBranchSymbols.Contains(token.Text))
                {
                    found.Add(token.Text);
                }
                else if (_conditionallyDefinedMacros.Contains(token.Text))
                {
                    pending.Push((token.Text, depth + 1));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 条件式を出現条件へ変換する。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <returns>変換した条件。変換できない形であれば <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>defined()</c> だけでできた式は、そのまま条件として書ける。</b>
    /// <c>defined(A) || defined(B)</c> は <c>A ∨ B</c> であり、
    /// 出現条件は積和形なので、この形は素直に載る (<see cref="SymbolCondition"/>)。
    /// <c>#if defined(X)</c> が <c>#ifdef X</c> と同じ形であることも、この道で扱える。
    /// </para>
    /// <para>
    /// <b>シンボルでない名前は、その場で値を解いて定数にする。</b>
    /// 単一構成での展開では、それが定義されているかは決まっている。
    /// <c>defined(SHADER_API_D3D11) &amp;&amp; defined(_NORMALMAP)</c> は、
    /// 前半を解いたうえで <c>_NORMALMAP</c> だけを記号として残せる。
    /// </para>
    /// <para>
    /// 比較や算術が混ざる式は変換しない。値を解く側へ倒す。
    /// </para>
    /// </remarks>
    private SymbolCondition? TryReadDefinedCondition(ImmutableArray<ShaderSyntaxToken> line)
        => TryReadCondition(
            line,
            new ConditionReading(_options.BothBranchSymbols, Record: true, UseMacroConditions: true));

    /// <summary>
    /// 条件式を、宣言されたシンボルの条件として読む。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="directive">指令名のトークン。</param>
    /// <returns>読んだ条件。読まない場合や読めない形であれば <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 並べるかどうかの判断 (<see cref="TryReadDefinedCondition"/>) とは読むシンボルが違う。
    /// こちらは組の記録に使う (<see cref="PreprocessorOptions.DeclaredSymbols"/>)。
    /// </para>
    /// <para>
    /// 名前は記録しない。同じ行を並べる判断のほうでも読んでいるので、二重に数えることになる。
    /// </para>
    /// <para>
    /// <b>取り込んだファイルの条件も読む。</b>
    /// ヘッダの分岐が切り替えるのは、解析しているファイルから見える宣言である。
    /// 並べられなかった分岐を構成として作れるよう、条件を記号のまま持っておく。
    /// ただし読むのは、そのヘッダ自身が宣言したシンボルだけである
    /// (<see cref="PreprocessorOptions.IncludedDeclaredSymbols"/>)。
    /// マクロの中から来た指令は読まない。位置がどの分岐のものか決まらない。
    /// </para>
    /// </remarks>
    private SymbolCondition? TryReadPathCondition(ImmutableArray<ShaderSyntaxToken> line, ShaderSyntaxToken directive)
    {
        if (_options.DeclaredSymbols.IsEmpty || directive.IsFromMacroExpansion)
        {
            return null;
        }

        ImmutableHashSet<string> symbols = IsInRootFile(directive)
            ? _options.DeclaredSymbols
            : _options.IncludedDeclaredSymbols;

        return symbols.IsEmpty
            ? null
            : TryReadCondition(line, new ConditionReading(symbols, Record: false, UseMacroConditions: true));
    }

    /// <summary>条件式を読む。</summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="reading">読み方。</param>
    /// <returns>読んだ条件。読めない形であれば <see langword="null"/>。</returns>
    private SymbolCondition? TryReadCondition(ImmutableArray<ShaderSyntaxToken> line, ConditionReading reading)
    {
        int index = 0;

        SymbolCondition? condition = ReadOrExpression(line, ref index, reading);

        return condition is not null && index == line.Length ? condition : null;
    }

    /// <summary>条件式の読み方。</summary>
    /// <param name="Symbols">記号として残す名前。ほかの名前は、その場で値を解いて定数にする。</param>
    /// <param name="Record">読んだ名前を、条件で参照された名前として記録するかどうか。</param>
    /// <param name="UseMacroConditions">
    /// シンボルでない名前を、条件付きで覚えたマクロの定義の条件として読むかどうか。
    /// </param>
    private readonly record struct ConditionReading(
        ImmutableHashSet<string> Symbols,
        bool Record,
        bool UseMacroConditions = false);

    /// <summary><c>||</c> で連なる条件を読む。</summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">読み取り位置。</param>
    /// <param name="reading">読み方。</param>
    /// <returns>読み取った条件。読めない形であれば <see langword="null"/>。</returns>
    private SymbolCondition? ReadOrExpression(ImmutableArray<ShaderSyntaxToken> line, ref int index, ConditionReading reading)
    {
        if (ReadAndExpression(line, ref index, reading) is not { } left)
        {
            return null;
        }

        while (index < line.Length && line[index].Kind == ShaderSyntaxKind.BarBarToken)
        {
            index++;

            if (ReadAndExpression(line, ref index, reading) is not { } right)
            {
                return null;
            }

            left = left.Or(right);
        }

        return left;
    }

    /// <summary><c>&amp;&amp;</c> で連なる条件を読む。</summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">読み取り位置。</param>
    /// <param name="reading">読み方。</param>
    /// <returns>読み取った条件。読めない形であれば <see langword="null"/>。</returns>
    private SymbolCondition? ReadAndExpression(ImmutableArray<ShaderSyntaxToken> line, ref int index, ConditionReading reading)
    {
        if (ReadBitwiseOrExpression(line, ref index, reading) is not { } left)
        {
            return null;
        }

        while (index < line.Length && line[index].Kind == ShaderSyntaxKind.AmpersandAmpersandToken)
        {
            index++;

            if (ReadBitwiseOrExpression(line, ref index, reading) is not { } right)
            {
                return null;
            }

            left = left.And(right);
        }

        return left;
    }

    /// <summary><c>|</c> で連なる条件を読む。</summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">読み取り位置。</param>
    /// <param name="reading">読み方。</param>
    /// <returns>読み取った条件。読めない形であれば <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>#if (_A | _B)</c> は <c>#if _A || _B</c> と同じ意味になる。</b>
    /// ビットごとの論理和が 0 でないのは、どちらかの項が 0 でないときだけである。
    /// 項の値が 0 と 1 に限らなくても成り立つ。
    /// </para>
    /// <para>
    /// <c>&amp;</c> は読まない。<c>2 &amp; 1</c> は 0 なので、「どちらも 0 でない」とは一致しない。
    /// </para>
    /// </remarks>
    private SymbolCondition? ReadBitwiseOrExpression(ImmutableArray<ShaderSyntaxToken> line, ref int index, ConditionReading reading)
    {
        if (ReadUnaryExpression(line, ref index, reading) is not { } left)
        {
            return null;
        }

        while (index < line.Length && line[index].Kind == ShaderSyntaxKind.BarToken)
        {
            index++;

            if (ReadUnaryExpression(line, ref index, reading) is not { } right)
            {
                return null;
            }

            left = left.Or(right);
        }

        return left;
    }

    /// <summary>否定・括弧・<c>defined()</c>・シンボル名そのものを読む。</summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">読み取り位置。</param>
    /// <param name="reading">読み方。</param>
    /// <returns>読み取った条件。読めない形であれば <see langword="null"/>。</returns>
    private SymbolCondition? ReadUnaryExpression(ImmutableArray<ShaderSyntaxToken> line, ref int index, ConditionReading reading)
    {
        if (index >= line.Length)
        {
            return null;
        }

        if (line[index].Kind == ShaderSyntaxKind.ExclamationToken)
        {
            index++;
            return ReadUnaryExpression(line, ref index, reading)?.Negate();
        }

        if (line[index].Kind == ShaderSyntaxKind.OpenParenToken)
        {
            index++;

            if (ReadOrExpression(line, ref index, reading) is not { } inner
                || index >= line.Length
                || line[index].Kind != ShaderSyntaxKind.CloseParenToken)
            {
                return null;
            }

            index++;
            return inner;
        }

        return ReadDefinedAtom(line, ref index, reading)
               ?? ReadValueAtom(line, ref index, reading)
               ?? ReadSymbolAtom(line, ref index, reading)
               ?? ReadConstantOperand(line, ref index);
    }

    /// <summary>
    /// 値を持つシンボルの比較 (<c>QUALITY == 2</c>、<c>QUALITY &gt;= 1</c>) か、名前そのもの (<c>QUALITY</c>) を読む。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">読み取り位置。</param>
    /// <param name="reading">読み方。</param>
    /// <returns>
    /// 比較が成り立つ値の原子の論理和。値を持つシンボルでないか、記号として読めない値があれば <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 名前だけなら 0 でない値の論理和 (<c>#if QUALITY</c> は <c>QUALITY != 0</c>)。
    /// 比べる相手は整数のリテラルに限る。左右を入れ替えた形 (<c>2 == QUALITY</c>) は読まない。
    /// </para>
    /// <para>
    /// <b>成り立つ値のどれかが記号として残す名前に入っていなければ、記号としては読まない。</b>
    /// その原子の構成は別に展開するので、値を解く側へ倒す。
    /// </para>
    /// </remarks>
    private SymbolCondition? ReadValueAtom(ImmutableArray<ShaderSyntaxToken> line, ref int index, ConditionReading reading)
    {
        if (_options.ValueSymbols.IsEmpty
            || index >= line.Length
            || line[index].Kind != ShaderSyntaxKind.IdentifierToken
            || !_options.ValueSymbols.TryGetValue(line[index].Text, out ImmutableArray<int> values))
        {
            return null;
        }

        ShaderSyntaxToken name = line[index];
        int position = index + 1;
        Func<int, bool> holds = value => value != 0;

        if (position + 1 < line.Length
            && ComparisonOf(line[position].Kind) is { } compare
            && line[position + 1].Kind == ShaderSyntaxKind.NumericLiteralToken
            && int.TryParse(line[position + 1].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int operand))
        {
            holds = value => compare(value, operand);
            position += 2;
        }

        if (reading.Record)
        {
            RecordConditionalIdentifier(name);
        }

        SymbolCondition condition = SymbolCondition.Never;

        foreach (int value in values.Where(holds))
        {
            string atom = VariantValues.Atom(name.Text, value);

            if (!reading.Symbols.Contains(atom))
            {
                return null;
            }

            condition = condition.Or(SymbolCondition.Symbol(atom));
        }

        index = position;
        return condition;
    }

    /// <summary>比較の演算子を、値どうしの比較に直す。</summary>
    /// <param name="kind">演算子のトークンの種類。</param>
    /// <returns>比較。比較の演算子でなければ <see langword="null"/>。</returns>
    private static Func<int, int, bool>? ComparisonOf(ShaderSyntaxKind kind) => kind switch
    {
        ShaderSyntaxKind.EqualsEqualsToken => (a, b) => a == b,
        ShaderSyntaxKind.ExclamationEqualsToken => (a, b) => a != b,
        ShaderSyntaxKind.LessThanToken => (a, b) => a < b,
        ShaderSyntaxKind.LessThanEqualsToken => (a, b) => a <= b,
        ShaderSyntaxKind.GreaterThanToken => (a, b) => a > b,
        ShaderSyntaxKind.GreaterThanEqualsToken => (a, b) => a >= b,
        _ => null,
    };

    /// <summary>
    /// 構成によって変わる名前を含まない項を、この構成での値に解いて読む。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">読み取り位置。</param>
    /// <returns>
    /// 真なら「常に」、偽なら「決して」。
    /// 構成によって変わる名前を含むか、値を解けなければ <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b><c>defined(_A) &amp;&amp; (SHADER_TARGET &gt;= 45)</c> の右側は、単一構成での値がどの構成でも同じである。</b>
    /// 比較や算術を含む項を丸ごと読めない扱いにすると、左のキーワードの分岐まで並べられなくなる。
    /// 項の範囲は、括弧の深さが 0 のまま次の <c>&amp;&amp;</c> / <c>||</c> / <c>|</c> か閉じ括弧が来るまでとする。
    /// </para>
    /// <para>
    /// 構成によって定義が変わるマクロを含む項は解かない。その値はこの構成でのものにすぎない。
    /// </para>
    /// </remarks>
    private SymbolCondition? ReadConstantOperand(ImmutableArray<ShaderSyntaxToken> line, ref int index)
    {
        int end = index;
        int depth = 0;

        for (; end < line.Length; end++)
        {
            ShaderSyntaxKind kind = line[end].Kind;

            if (kind == ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (kind == ShaderSyntaxKind.CloseParenToken)
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (depth == 0 && kind is ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken or ShaderSyntaxKind.BarToken)
            {
                break;
            }
        }

        if (end == index || depth != 0)
        {
            return null;
        }

        ImmutableArray<ShaderSyntaxToken> operand = line[index..end];

        if (ReferencesConfigurationDependentName(operand)
            || !TryEvaluateSilently(operand, out bool value))
        {
            return null;
        }

        index = end;
        return value ? SymbolCondition.Always : SymbolCondition.Never;
    }

    /// <summary>シンボル名そのものを読む。</summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">読み取り位置。</param>
    /// <param name="reading">読み方。</param>
    /// <returns>読み取った条件。シンボルでなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>#if _A</c> は <c>#ifdef _A</c> と同じ意味で書かれている。</b>
    /// Unity は有効なシンボルを値 1 のマクロとして定義するため、
    /// URP 自身も <c>#if _ALPHATEST_ON</c> の形を使っている。
    /// 記号として扱わないと、この書き方をした分岐が、何も伝えられないまま落ちる。
    /// </para>
    /// <para>
    /// <b>宣言されたシンボルに限る。</b>
    /// <c>#if SHADER_API_D3D11</c> や <c>#if UNITY_VERSION &gt;= 600</c> は
    /// 値としての判定であり、単一構成で解けば済む。
    /// 名前そのものは記録しておく。スペルミスは <c>HL0330</c> が拾う。
    /// </para>
    /// </remarks>
    private SymbolCondition? ReadSymbolAtom(ImmutableArray<ShaderSyntaxToken> line, ref int index, ConditionReading reading)
    {
        if (index >= line.Length || line[index].Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return null;
        }

        ShaderSyntaxToken name = line[index];

        if (reading.Record)
        {
            RecordConditionalIdentifier(name);
        }

        if (!reading.Symbols.Contains(name.Text))
        {
            return null;
        }

        index++;
        return reading.UseMacroConditions ? SymbolOrDefinition(name.Text) : SymbolCondition.Symbol(name.Text);
    }

    /// <summary><c>defined(X)</c> を読む。</summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="index">読み取り位置。</param>
    /// <param name="reading">読み方。</param>
    /// <returns>読み取った条件。この形でなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 括弧は省略できる (<c>defined X</c>)。プリプロセッサの文法として認められている。
    /// </remarks>
    private SymbolCondition? ReadDefinedAtom(ImmutableArray<ShaderSyntaxToken> line, ref int index, ConditionReading reading)
    {
        if (index >= line.Length
            || line[index].Kind != ShaderSyntaxKind.IdentifierToken
            || line[index].Text != "defined")
        {
            return null;
        }

        int position = index + 1;
        bool parenthesized = position < line.Length
                             && line[position].Kind == ShaderSyntaxKind.OpenParenToken;

        if (parenthesized)
        {
            position++;
        }

        if (position >= line.Length || line[position].Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return null;
        }

        ShaderSyntaxToken name = line[position];
        position++;

        if (parenthesized)
        {
            if (position >= line.Length || line[position].Kind != ShaderSyntaxKind.CloseParenToken)
            {
                return null;
            }

            position++;
        }

        index = position;

        if (reading.Record)
        {
            RecordConditionalIdentifier(name);
        }

        if (reading.Symbols.Contains(name.Text))
        {
            return reading.UseMacroConditions ? SymbolOrDefinition(name.Text) : SymbolCondition.Symbol(name.Text);
        }

        // 条件付きで覚えたマクロなら、その定義がある条件として読む。
        // #ifndef TINT のような書き方は、それまでの #define の条件の裏返しである。
        if (reading.UseMacroConditions && GetDefinedCondition(name.Text) is { } defined)
        {
            return defined;
        }

        // ほかは、その場で定義の有無を解いて定数にする。
        return _macros.ContainsKey(name.Text) ? SymbolCondition.Always : SymbolCondition.Never;
    }

    /// <summary><c>#else</c> を処理する。</summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    private void ProcessElse(ShaderSyntaxToken directiveToken)
    {
        if (_conditionals.Count == 0)
        {
            Report(ShaderDescriptors.PreprocessorError, directiveToken.GetLocation(),
                "対応する #if がない #else です。");
            SkipDirectiveLine();
            return;
        }

        ConditionalState state = _conditionals[^1];

        if (state.SeenElse)
        {
            Report(ShaderDescriptors.PreprocessorError, directiveToken.GetLocation(),
                "1 つの #if に #else は 1 つしか書けません。");
        }

        EndInactiveBranch(state, directiveToken);

        state.SeenElse = true;
        state.IsBranchActive = !state.HasTakenBranch && !state.IsParentSkipping;
        state.HasTakenBranch = state.HasTakenBranch || state.IsBranchActive;

        // 両方を残す領域では、ここに来るのは「それまでの分岐がすべて外れたとき」である。
        if (state.Folded)
        {
            EnterFoldedBranch(state, state.Remaining);
            state.Remaining = SymbolCondition.Never;
        }

        StartInactiveBranch(state, directiveToken);
        EnterPathBranch(state, directiveToken, SymbolCondition.Always);

        SkipBareDirectiveLine("else");
    }

    /// <summary><c>#endif</c> を処理する。</summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    private void ProcessEndif(ShaderSyntaxToken directiveToken)
    {
        if (_conditionals.Count == 0)
        {
            Report(ShaderDescriptors.PreprocessorError, directiveToken.GetLocation(),
                "対応する #if がない #endif です。");
        }
        else
        {
            EndInactiveBranch(_conditionals[^1], directiveToken);

            if (_conditionals[^1].Folded)
            {
                CloseConditionalRange();
                CloseBranchRange(_conditionals[^1]);
            }

            RecordKeywordRegion(_conditionals[^1], directiveToken);
            _conditionals.RemoveAt(_conditionals.Count - 1);
        }

        SkipBareDirectiveLine("endif");
    }

    /// <summary>条件付きの範囲を開く。</summary>
    /// <remarks>出力のどこから条件が付くかを覚えておく。</remarks>
    private void OpenConditionalRange() => _conditionalRangeStart = _output.Count;

    /// <summary>
    /// 条件付きの範囲を閉じ、記録する。
    /// </summary>
    /// <remarks>
    /// 1 つもトークンが出ていない分岐は記録しない。
    /// 空の分岐に条件を付けても、指せる場所が無い。
    /// </remarks>
    private void CloseConditionalRange()
    {
        int length = _output.Count - _conditionalRangeStart;

        if (length > 0)
        {
            _conditionalRegions.Add(
                new ConditionalTokenRange(
                    _conditionalRangeStart,
                    length,
                    _options.SymbolConstraints.Apply(CurrentCondition)));
        }

        _conditionalRangeStart = _output.Count;
    }

    /// <summary>
    /// 今の分岐が非活性なら、その始まりを覚える。
    /// </summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <param name="directive">分岐を始めた指令の名前のトークン。</param>
    /// <remarks>
    /// <para>
    /// <b>外側が非活性な分岐は覚えない。</b>外側の範囲が丸ごと含んでいる。
    /// </para>
    /// <para>
    /// 両方を残す分岐は非活性ではない。どちらの経路もいつか通るコードである。
    /// </para>
    /// </remarks>
    private void StartInactiveBranch(ConditionalState state, ShaderSyntaxToken directive)
        => state.InactiveFrom = !state.IsParentSkipping
                                && !state.IsBranchActive
                                && state.KeptCondition is null
                                && IsInRootFile(directive)
            ? directive.Span.End
            : null;

    /// <summary>非活性の分岐が終わったので、その範囲を記録する。</summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <param name="directive">分岐を閉じた指令の名前のトークン。</param>
    /// <remarks>
    /// ファイル終端まで閉じられなかった分岐は記録しない。
    /// どこまでが非活性なのか、書いた人の意図が分からない。
    /// </remarks>
    private void EndInactiveBranch(ConditionalState state, ShaderSyntaxToken directive)
    {
        if (state.InactiveFrom is { } from
            && IsInRootFile(directive)
            && directive.Span.Start > from)
        {
            _inactiveRegions.Add(new InactiveRegion(
                Location.Create(directive.Source, TextSpan.FromBounds(from, directive.Span.Start)),
                [.. state.ConditionSymbols]));
        }

        state.InactiveFrom = null;
    }

    /// <summary>トークンが、解析しているファイルそのものに書かれたものかを判定する。</summary>
    /// <param name="token">判定するトークン。</param>
    /// <returns>このファイルに書かれたものであれば <see langword="true"/>。</returns>
    private bool IsInRootFile(ShaderSyntaxToken token)
        => _rootSource is not null
           && !token.IsFromMacroExpansion
           && string.Equals(token.Source.FilePath, _rootSource.FilePath, StringComparison.Ordinal);

    /// <summary>条件に書かれた名前から、その連なりが見ているキーワードを覚えさせる。</summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <param name="names">条件に書かれた名前。</param>
    /// <param name="readsValues">
    /// マクロの中身の値も見る条件 (<c>#if</c> / <c>#elif</c>) かどうか。<c>#ifdef</c> は定義の有無しか見ない。
    /// </param>
    /// <remarks>
    /// <para>
    /// 条件付きで覚えたマクロ (<c>#ifdef _A</c> の中の <c>#define USE_A</c>) は、定義がある条件のキーワードを見ていることにする。
    /// <c>#if</c> なら、中身に書かれたキーワードも見ている (HDRP の <c>#if REQUIRE_NORMAL</c>)。
    /// </para>
    /// <para>
    /// <b>どのキーワードで変わるのか分からないマクロを見ていれば、それを印として残す。</b>
    /// 構成で変わる分岐の中で定義されたマクロでも、定義がある条件を覚えていないことがある (並べなかった分岐のヘッダの定義)。
    /// そうした連なりの中の違いは、どのキーワードが起こしたのか言えない。
    /// </para>
    /// </remarks>
    private void NoteRegionDependencies(ConditionalState state, IEnumerable<string> names, bool readsValues)
    {
        foreach (string name in names)
        {
            if (name == "defined")
            {
                continue;
            }

            if (_options.ConfigurationSymbols.Contains(name)
                || _options.DeclaredSymbols.Contains(name)
                || _options.IncludedDeclaredSymbols.Contains(name)
                || _options.BothBranchSymbols.Contains(name))
            {
                state.RegionSymbols.Add(name);

                // キーワードの名前が、別のキーワードの条件の下で #define されていることもある。
                if (GetDefinedCondition(name) is { } alsoDefined)
                {
                    AddRegionCondition(state, alsoDefined);
                }

                continue;
            }

            if (GetDefinedCondition(name) is { IsAlways: false } defined)
            {
                AddRegionCondition(state, defined);

                // #ifdef なら定義の有無だけを見ている。#if は中身の値も見ている。
                if (!readsValues)
                {
                    continue;
                }
            }

            if (_conditionallyDefinedMacros.Contains(name) || _macroKeywords.ContainsKey(name))
            {
                // 定義を変えたキーワードと、本体に書かれたキーワードを見ている
                // (HDRP の #define REQUIRE_NORMAL ... defined(_VERTEX_DISPLACEMENT) を #if REQUIRE_NORMAL で見る)。
                // どこかにどのキーワードで変わるのか分からない出どころがあれば、それを印として残す。
                state.RegionSymbols.UnionWith(CollectKeywordsThroughMacro(name, out bool unknown));
                state.RegionDependsOnMacros |= unknown;
            }
        }
    }

    /// <summary>マクロの定義を変えたキーワードと、本体に書かれたキーワードを集める。</summary>
    /// <param name="name">マクロの名前。</param>
    /// <param name="unknown">どのキーワードで変わるのか分からない出どころがあったかどうか。</param>
    /// <returns>集めたキーワード。辿るのは構成で変わるマクロだけで、深さは 8 段までとする。</returns>
    /// <remarks>
    /// 構成で変わるマクロなのに、定義を変えたキーワードも本体のキーワードも見つからないものは、分からない出どころとする
    /// (取り込んだヘッダの記録を使い回して、定義を変えた連なりを見ていないなど)。
    /// </remarks>
    private HashSet<string> CollectKeywordsThroughMacro(string name, out bool unknown)
    {
        HashSet<string> found = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        Stack<(string Name, int Depth)> pending = new();

        unknown = false;
        pending.Push((name, 0));

        while (pending.Count > 0)
        {
            (string current, int depth) = pending.Pop();

            if (depth > 8)
            {
                unknown = true;
                continue;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            int before = found.Count;

            if (_unknownMacroSources.Contains(current))
            {
                unknown = true;
            }

            if (_macroKeywords.TryGetValue(current, out HashSet<string>? changers))
            {
                found.UnionWith(changers);
            }

            if (!_macros.TryGetValue(current, out MacroDefinition? macro))
            {
                // この構成では定義されていない。定義を変えたキーワードが分かっていれば足りる。
                unknown |= changers is null;
                continue;
            }

            bool bodyDepends = false;

            foreach (ShaderSyntaxToken token in macro.Body)
            {
                if (token.Kind != ShaderSyntaxKind.IdentifierToken)
                {
                    continue;
                }

                if (_options.ConfigurationSymbols.Contains(token.Text)
                    || _options.DeclaredSymbols.Contains(token.Text)
                    || _options.IncludedDeclaredSymbols.Contains(token.Text)
                    || _options.BothBranchSymbols.Contains(token.Text))
                {
                    found.Add(token.Text);
                    bodyDepends = true;
                }
                else if (_conditionallyDefinedMacros.Contains(token.Text) || _macroKeywords.ContainsKey(token.Text))
                {
                    pending.Push((token.Text, depth + 1));
                    bodyDepends = true;
                }
            }

            // 構成で変わるマクロなのに、何で変わるのかを 1 つも見つけられなかった。
            if (_conditionallyDefinedMacros.Contains(current) && changers is null && !bodyDepends && found.Count == before)
            {
                unknown = true;
            }
        }

        return found;
    }

    /// <summary>条件に現れるキーワードを、連なりが見ているものとして足す。</summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <param name="condition">足す条件。</param>
    private static void AddRegionCondition(ConditionalState state, SymbolCondition condition)
    {
        if (condition.IsUnknown)
        {
            state.RegionDependsOnMacros = true;
            return;
        }

        state.RegionSymbols.UnionWith(condition.EnumerateSymbols());
    }

    /// <summary>
    /// 並べなかった条件が、並べた分岐で定義したマクロを見ていたら、その分岐のシンボルを並べ直しの対象にする。
    /// </summary>
    /// <param name="names">条件に書かれた名前。</param>
    /// <remarks>
    /// <para>
    /// <b>並べた分岐の <c>#define</c> は、マクロ表ではどの構成でも効く。</b>
    /// 条件で見るだけなら、その条件を並べて定義がある条件として読めば害が無い。
    /// 並べなかった条件はその場の定義の有無で分岐を選ぶので、定義が無い構成でも定義がある側を通ってしまう
    /// (<c>#ifdef _A</c> の中の <c>#define USE_A</c> を、文の途中で分かれる <c>#ifdef USE_A</c> で見ると、既定の構成が <c>_A</c> の側を読む)。
    /// </para>
    /// <para>
    /// コードでの使用 (<see cref="NoteMergedMacroUse"/>) と同じく、そのシンボルは並べずに展開し直す。
    /// </para>
    /// </remarks>
    private void NoteMergedMacroConditionUse(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (_mergedMacros.TryGetValue(name, out ImmutableArray<string> symbols))
            {
                _mergedMacroConflicts.UnionWith(symbols);
                Record(recording => recording.MergedMacroConflicts.UnionWith(symbols));
            }
        }
    }

    /// <summary><c>#define</c> / <c>#undef</c> の指令名の次にある、マクロの名前を返す。</summary>
    /// <returns>名前のトークン。読めなければ <see langword="null"/>。</returns>
    private ShaderSyntaxToken? NameAfterDirective()
    {
        TokenSource source = CurrentSource;
        int index = source.Index + 1;

        return index < source.Tokens.Length
               && !source.Tokens[index].IsAtLineStart
               && source.Tokens[index].Kind == ShaderSyntaxKind.IdentifierToken
            ? source.Tokens[index]
            : null;
    }

    /// <summary>
    /// 今の位置を囲む連なりが見ているキーワードを、そのマクロの定義を変えるものとして覚える。
    /// </summary>
    /// <param name="name">定義・削除するマクロの名前のトークン。読めなければ <see langword="null"/>。</param>
    /// <remarks>
    /// <para>
    /// <b>マクロを変えるだけでは、連なりの外は変わらない。</b>
    /// 変わるのは、そのマクロがコードで使われるときである。条件でしか使わなければ、
    /// その条件の連なりがそのキーワードを見ていることにすれば、違いのキーワードを決められる
    /// (<see cref="NoteRegionDependencies"/>)。
    /// 使われた時点で、そのキーワードをまとめられないものとして数える (<see cref="NoteMacroUseInCode"/>)。
    /// </para>
    /// <para>
    /// どのキーワードで変わるのか分からない連なりの中の定義は、その印を残す。
    /// そのマクロを見る条件の連なりは、どのキーワードのものか言えない。
    /// </para>
    /// </remarks>
    private void NoteMacroKeywords(ShaderSyntaxToken? name)
    {
        if (name is null)
        {
            NoteMacroAffectingSymbols();
            return;
        }

        foreach (ConditionalState state in _conditionals)
        {
            if (state.RegionDependsOnMacros && _unknownMacroSources.Add(name.Text))
            {
                string unknown = name.Text;
                Record(recording => recording.UnknownMacroSources.Add(unknown));
            }

            foreach (string symbol in state.RegionSymbols)
            {
                AddMacroKeyword(name.Text, symbol);
            }
        }
    }

    /// <summary>マクロの定義を変えるキーワードを 1 つ覚える。</summary>
    /// <param name="macro">マクロの名前。</param>
    /// <param name="symbol">キーワード。</param>
    private void AddMacroKeyword(string macro, string symbol)
    {
        if (!_macroKeywords.TryGetValue(macro, out HashSet<string>? symbols))
        {
            symbols = new HashSet<string>(StringComparer.Ordinal);
            _macroKeywords[macro] = symbols;
        }

        symbols.Add(symbol);

        // 記録へは、全体に新しいかどうかに関わらず足す (取り込みの記録は使い回される)。
        Record(recording => recording.MacroKeywordChanges.Add(new KeyValuePair<string, string>(macro, symbol)));
    }

    /// <summary>
    /// コードに現れた名前が、キーワードで定義の変わるマクロなら、そのキーワードをまとめられないものとして数える。
    /// </summary>
    /// <param name="name">コードに現れた識別子。</param>
    /// <remarks>
    /// 読み飛ばした分岐でだけ定義されたマクロの名前は、この構成ではただの識別子として現れる。それも数える。
    /// </remarks>
    private void NoteMacroUseInCode(string name)
    {
        if (!_macroKeywords.TryGetValue(name, out HashSet<string>? symbols))
        {
            return;
        }

        foreach (string symbol in symbols)
        {
            _macroAffectingSymbols.Add(symbol);
            Record(recording => recording.MacroAffectingSymbols.Add(symbol));
        }
    }

    /// <summary>今の位置を囲む連なりが見ているキーワードを、マクロ表か取り込みを変えるものとして覚える。</summary>
    /// <remarks>
    /// そうしたキーワードは、有効にするとその連なりの外の展開まで変わりうる。
    /// 違いがどのキーワードの領域の中にあるかで条件を付ける、まとめた構成には入れない。
    /// </remarks>
    private void NoteMacroAffectingSymbols()
    {
        foreach (ConditionalState state in _conditionals)
        {
            foreach (string symbol in state.RegionSymbols)
            {
                // 記録へは、全体の集合に新しいかどうかに関わらず足す (取り込みの記録は使い回される)。
                _macroAffectingSymbols.Add(symbol);
                Record(recording => recording.MacroAffectingSymbols.Add(symbol));
            }
        }
    }

    /// <summary>閉じた連なりが構成によって結果の変わるものなら、その範囲を記録する。</summary>
    /// <param name="state">閉じた条件ブロック。</param>
    /// <param name="endif"><c>#endif</c> の指令名のトークン。</param>
    /// <remarks>
    /// 外側が読み飛ばされた連なりは記録しない。出力に何も残らない。
    /// 始まりと終わりが別のファイルにある連なりも記録しない (ヘッダが開いた <c>#if</c> を別のファイルで閉じる形)。
    /// </remarks>
    private void RecordKeywordRegion(ConditionalState state, ShaderSyntaxToken endif)
    {
        if (state.IsParentSkipping
            || (state.RegionSymbols.Count == 0 && !state.RegionDependsOnMacros)
            || state.Directive.IsFromMacroExpansion
            || !ReferenceEquals(state.Directive.Source, endif.Source))
        {
            return;
        }

        KeywordRegion region = new(
            state.Directive.Source.FilePath,
            state.Directive.Span.Start,
            endif.Span.End,
            [.. state.RegionSymbols.Order(StringComparer.Ordinal)],
            state.RegionDependsOnMacros);

        _keywordRegions.Add(region);
        Record(recording => recording.KeywordRegions.Add(region));
    }

    /// <summary>条件式の行に書かれた名前を、条件ブロックに覚えさせる。</summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <param name="line">条件式の行。</param>
    private static void AddConditionSymbols(ConditionalState state, ImmutableArray<ShaderSyntaxToken> line)
    {
        foreach (ShaderSyntaxToken token in line)
        {
            if (token.Kind == ShaderSyntaxKind.IdentifierToken && token.Text != "defined")
            {
                state.ConditionSymbols.Add(token.Text);
            }
        }
    }

    /// <summary>ファイル終端まで閉じられなかった条件分岐を報告する。</summary>
    /// <remarks>
    /// 閉じ忘れは、以降のコードが丸ごと無効化されるか丸ごと有効化されるという形で
    /// 大きな影響を及ぼすため、必ず報告する。
    /// </remarks>
    private void ReportUnterminatedConditionals()
    {
        foreach (ConditionalState state in _conditionals)
        {
            Report(ShaderDescriptors.PreprocessorError, state.Directive.GetLocation(),
                "#if に対応する #endif が存在しません。");
        }
    }

    // --------------------------------------------------------------------
    // #pragma
    // --------------------------------------------------------------------

    /// <summary>
    /// <c>#pragma</c> を記録する。
    /// </summary>
    /// <remarks>
    /// 内容は解釈せずそのまま保持する。
    /// <c>#pragma vertex</c> や <c>#pragma multi_compile</c> はルールが必要とするため、
    /// 読み飛ばしてはならない。
    /// </remarks>
    private void ProcessPragma()
    {
        ImmutableArray<ShaderSyntaxToken> line = ReadDirectiveLine();

        if (!line.IsEmpty)
        {
            _pragmas.Add(new PragmaDirective(line, line[0]));
        }

        if (line.Length == 1 && line[0].Text == "once" && !line[0].IsFromMacroExpansion && _options.HonorPragmaOnce)
        {
            MarkIncludedOnce(line[0]);
        }
    }

    /// <summary><c>#pragma once</c> を書いたファイルの印の名前に付ける前置き。識別子には書けない形にする。</summary>
    private const string OnceMarkerPrefix = "#pragma once:";

    /// <summary>
    /// <c>#pragma once</c> を書いたファイルを、2 度目からは取り込まないものとして覚える。
    /// </summary>
    /// <param name="onceToken"><c>once</c> のトークン。</param>
    /// <remarks>
    /// <para>
    /// <b>Unreal Engine のヘッダは、インクルードガードではなく <c>#pragma once</c> で 2 度目の取り込みを防ぐ。</b>
    /// 扱わずにいると、同じヘッダを何度も展開し (同じ宣言の重複 HL0314、同じマクロの再定義)、
    /// ヘッダどうしが互いを取り込むだけで循環 (HL0320) と報告し、展開の量も膨れ上がっていた。
    /// DXC と FXC は、2 度目の取り込みを丸ごと飛ばす。
    /// </para>
    /// <para>
    /// <b>印はマクロ表に、識別子には書けない名前で置く。</b>
    /// 取り込み結果の使い回し (<see cref="IncludeCache"/>) は、マクロ表の状態と、展開が読んだ名前で照合している。
    /// 印をマクロ表に置けば、どのファイルを取り込み済みかもその照合に入る。
    /// 結果のマクロ表 (<see cref="PreprocessResult.Macros"/>) からは外す。
    /// </para>
    /// </remarks>
    private void MarkIncludedOnce(ShaderSyntaxToken onceToken)
    {
        string name = OnceMarkerPrefix + onceToken.Source.FilePath;
        SetMacro(name, new MacroDefinition(name, onceToken, isFunctionLike: false, [], isVariadic: false, []));
    }

    /// <summary>そのファイルが <c>#pragma once</c> を書いていて、すでに取り込まれているかを判定する。</summary>
    /// <param name="filePath">取り込むファイルのパス。</param>
    /// <returns>取り込み済みなら <see langword="true"/>。</returns>
    private bool IsIncludedOnce(string filePath) => _macros.ContainsKey(OnceMarkerPrefix + filePath);

    // --------------------------------------------------------------------
    // 指令の行の読み取り
    // --------------------------------------------------------------------

    /// <summary>
    /// 指令の残りの行を読み取る。
    /// </summary>
    /// <returns>行のトークン列。</returns>
    /// <remarks>
    /// 行の終わりは「次のトークンが行頭にある」ことで判定する。
    /// 行継続 (<c>\</c> + 改行) は字句解析の段階で行の切れ目として扱われないため、
    /// 複数行に分けて書かれたマクロ定義もここで 1 つの行として読み取れる。
    /// </remarks>
    private ImmutableArray<ShaderSyntaxToken> ReadDirectiveLine()
    {
        ImmutableArray<ShaderSyntaxToken>.Builder line = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        while (true)
        {
            ShaderSyntaxToken? token = PeekInCurrentSource();

            if (token is null || token.IsAtLineStart)
            {
                break;
            }

            line.Add(TakeToken());
        }

        return line.ToImmutable();
    }

    /// <summary>
    /// 非活性領域にある指令の残りの行を読み飛ばす。
    /// </summary>
    /// <param name="isDefine"><c>#define</c> の行かどうか。条件付きで覚えるために名前と中身を読む。</param>
    /// <remarks>
    /// <para>
    /// <b>読み飛ばす行に現れた識別子は記録する。</b>
    /// 非活性領域の <c>#define</c> は、そのままでは「どこにも現れない名前」として扱われてしまう。
    /// </para>
    /// <para>
    /// URP の <c>LitInput.hlsl</c> はまさにこの形をしている。
    /// <code>
    /// #ifdef _METALLICSPECGLOSSMAP
    /// #define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_MetallicGlossMap, ...)
    /// #endif
    /// </code>
    /// <c>_MetallicGlossMap</c> はこの定義の中でしか読まれない。
    /// 記録しないと、正しく使われているテクスチャを
    /// 「宣言だけで使われていない」と誤って報告することになる。
    /// </para>
    /// </remarks>
    private void SkipDirectiveLine(bool isDefine = false)
    {
        ImmutableArray<ShaderSyntaxToken> line = ReadDirectiveLine();

        // 行の先頭は指令の名前である (読み飛ばす経路では消費していない)。その次がマクロの名前になる。
        if (isDefine && line.Length >= 2 && line[1].Kind == ShaderSyntaxKind.IdentifierToken)
        {
            RecordConditionalDefinition(line[1], [.. line.Skip(1)], active: false);
        }

        // 読み飛ばす分岐の #undef も、別の構成では効いている。
        // 覚えたままにすると、#undef してから定義し直した中身を「#undef せずに定義し直した」と報告する。
        // 実行される #undef と同じく、それまでの定義の条件を縮める (ForgetConditionalDefinitions)。
        if (line.Length >= 2 && line[0].Text == "undef" && line[1].Kind == ShaderSyntaxKind.IdentifierToken)
        {
            ForgetConditionalDefinitions(line[1], active: false);
        }

        // 構成によって変わる条件のために読み飛ばした定義は、別の構成では効いている。
        // この構成で定義されていなくても、構成によって定義が変わるマクロである。
        // 数えないと、そのマクロを見る条件を構成によらないものと取り違える
        // (ヘッダの #if defined(UNITY_DOTS_INSTANCING_ENABLED) は DOTS_INSTANCING_ON で切り替わる)。
        if (line.Length >= 2
            && line[0].Text is "define" or "undef"
            && line[1].Kind == ShaderSyntaxKind.IdentifierToken
            && IsInConfigurationDependentBranch)
        {
            AddConditionallyDefined(line[1].Text);
        }

        foreach (ShaderSyntaxToken token in line)
        {
            if (token.Kind == ShaderSyntaxKind.IdentifierToken)
            {
                AddSkippedIdentifier(token);
            }
        }
    }

    /// <summary>
    /// 何も続かないはずの指令の行に、余分なトークンが無いかを見て読み飛ばす。
    /// </summary>
    /// <param name="directive">指令の名前。</param>
    /// <remarks>
    /// <c>#else</c> と <c>#endif</c> には何も続かない。
    /// <c>#endif _FOO</c> のように書いても無視されるため、
    /// どの <c>#if</c> に対応するつもりだったのかが失われる。
    /// コメントは字句解析の段階でトリビアになるので、ここには現れない。
    /// </remarks>
    private void SkipBareDirectiveLine(string directive)
    {
        ImmutableArray<ShaderSyntaxToken> line = ReadDirectiveLine();

        if (!line.IsEmpty)
        {
            Report(ShaderDescriptors.PreprocessorError, line[0].GetLocation(),
                $"#{directive} の後には何も書けません: '{line[0].Text}'。"
                + "コメントにするなら // を付けてください。");
        }

        foreach (ShaderSyntaxToken token in line)
        {
            if (token.Kind == ShaderSyntaxKind.IdentifierToken)
            {
                AddSkippedIdentifier(token);
            }
        }
    }
}
