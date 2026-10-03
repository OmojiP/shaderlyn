using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// #define と #undef。マクロ表の更新。
/// </summary>
internal sealed partial class HlslPreprocessor
{
    // --------------------------------------------------------------------
    // #define / #undef
    // --------------------------------------------------------------------

    /// <summary>
    /// <c>#define</c> を処理する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 関数形式かオブジェクト形式かは
    /// <b>マクロ名の直後に空白を挟まず開き括弧が続くか</b>で決まる。
    /// <c>#define A(x) x</c> は関数形式、<c>#define A (x) x</c> はオブジェクト形式で
    /// 本体が <c>(x) x</c> になる。この区別を誤ると展開結果がまったく別のものになる。
    /// </para>
    /// </remarks>
    private void ProcessDefine()
    {
        ImmutableArray<HlslSyntaxToken> line = ReadDirectiveLine();

        if (line.IsEmpty || line[0].Kind != HlslSyntaxKind.IdentifierToken)
        {
            Report(HlslDescriptors.PreprocessorError, GetDirectiveLocation(line),
                "#define にはマクロ名が必要です。");
            return;
        }

        MacroDefinition definition = ParseMacroDefinition(line, report: true);
        HlslSyntaxToken nameToken = line[0];

        // 中身の違う再定義は、どちらが使われるかが読み手に分からない。
        // #undef してから定義し直すのが正しい書き方である。
        // 同じ中身の再定義は書き手の意図どおりなので何も言わない。
        //
        // 解析しているファイルの中で完結する場合だけ報告する。
        // Unity のヘッダは、取り込む順序で中身を差し替える書き方を多用している。
        // 実測では、絞らずに報告すると Unity 同梱の 109 件のうち 105 件が誤検出になった。
        // 並べた分岐の中では、どちらの定義も別の構成のものである。条件付きの検査が見る。
        if (!IsInKeptBranch
            && _macros.TryGetValue(nameToken.Text, out MacroDefinition? previous)
            && IsInRootSource(previous.NameToken)
            && IsInRootSource(nameToken)
            && !HasSameBody(previous, definition))
        {
            Report(HlslDescriptors.PreprocessorError, nameToken.GetLocation(),
                $"マクロ '{nameToken.Text}' を中身を変えて定義し直しています。"
                + "#undef してから定義してください。");
        }

        RecordConditionalDefinition(nameToken, line);
        RecordHeaderDefinition(nameToken);
        RecordMergedDefinition(nameToken.Text);
        SetMacro(nameToken.Text, definition);

        // 構成によって変わる条件の中で定義された名前は、構成が変われば別のものになる。
        // 本体が構成によって変わる名前を参照していても同じである。
        // インクルードガードや SHADER_API_* の中の定義は、どの構成でも同じなので数えない。
        if (IsInConfigurationDependentBranch || ReferencesConfigurationDependentName(line.Skip(1)))
        {
            AddConditionallyDefined(nameToken.Text);
        }
    }

    /// <summary>
    /// <c>#define</c> の行をマクロ定義として読む。
    /// </summary>
    /// <param name="line">指令の行のトークン。先頭はマクロ名。</param>
    /// <param name="report">書き方の誤りを報告するかどうか。</param>
    /// <returns>読み取った定義。</returns>
    /// <remarks>
    /// <para>
    /// 関数形式かオブジェクト形式かは
    /// <b>マクロ名の直後に空白を挟まず開き括弧が続くか</b>で決まる。
    /// <c>#define A(x) x</c> は関数形式、<c>#define A (x) x</c> はオブジェクト形式で
    /// 本体が <c>(x) x</c> になる。この区別を誤ると展開結果がまったく別のものになる。
    /// </para>
    /// <para>
    /// 読み飛ばす分岐の <c>#define</c> も、条件付きで覚えるために同じ読み方をする。
    /// そちらでは報告しない。その分岐はこの構成では処理していないので、
    /// 同じ誤りを構成ごとに何度も報告することになる。
    /// </para>
    /// </remarks>
    private MacroDefinition ParseMacroDefinition(ImmutableArray<HlslSyntaxToken> line, bool report)
    {
        HlslSyntaxToken nameToken = line[0];
        int index = 1;

        bool isFunctionLike = index < line.Length
            && line[index].Kind == HlslSyntaxKind.OpenParenToken
            && IsAdjacent(nameToken, line[index]);

        ImmutableArray<string>.Builder parameters = ImmutableArray.CreateBuilder<string>();
        bool isVariadic = false;

        if (isFunctionLike)
        {
            index++;

            while (index < line.Length && line[index].Kind != HlslSyntaxKind.CloseParenToken)
            {
                if (line[index].Kind == HlslSyntaxKind.CommaToken)
                {
                    index++;
                    continue;
                }

                if (line[index].Kind == HlslSyntaxKind.DotToken)
                {
                    // 可変長引数の "..." は 3 つのドットとして字句解析される。
                    while (index < line.Length && line[index].Kind == HlslSyntaxKind.DotToken)
                    {
                        index++;
                    }

                    isVariadic = true;
                    continue;
                }

                if (line[index].Kind == HlslSyntaxKind.IdentifierToken)
                {
                    // 同じ名前の仮引数が 2 つあると、どちらに置き換えるか決まらない。
                    if (report && parameters.Contains(line[index].Text, StringComparer.Ordinal))
                    {
                        Report(HlslDescriptors.PreprocessorError, line[index].GetLocation(),
                            $"マクロ '{nameToken.Text}' の仮引数 '{line[index].Text}' が重複しています。");
                    }

                    parameters.Add(line[index].Text);
                    index++;
                    continue;
                }

                if (report)
                {
                    Report(HlslDescriptors.PreprocessorError, line[index].GetLocation(),
                        $"マクロの仮引数として解釈できません: '{line[index].Text}'");
                }

                index++;
            }

            if (index < line.Length)
            {
                index++;
            }
            else if (report)
            {
                Report(HlslDescriptors.PreprocessorError, nameToken.GetLocation(),
                    "マクロの仮引数の括弧が閉じられていません。");
            }
        }

        return new MacroDefinition(
            nameToken.Text,
            nameToken,
            isFunctionLike,
            parameters.ToImmutable(),
            isVariadic,
            [.. line.Skip(index)]);
    }

    /// <summary>
    /// 並べた分岐の中の定義を覚える。
    /// </summary>
    /// <param name="name">マクロの名前。</param>
    /// <remarks>
    /// <b>並べた分岐の <c>#define</c> は、どの構成でも効いてしまう。</b>
    /// マクロ表は 1 つしかなく、展開も 1 回しか行わない。
    /// 条件の中でしか使われない前提で並べているので (<c>CanMerge</c>)、
    /// コードとして展開されたら前提が崩れる。そのときに並べ直せるよう、条件のシンボルを覚えておく。
    /// </remarks>
    private void RecordMergedDefinition(string name)
    {
        if (!IsInKeptBranch)
        {
            return;
        }

        ImmutableArray<string> symbols = [.. CurrentCondition.EnumerateSymbols()];

        if (!symbols.IsEmpty)
        {
            _mergedMacros[name] = symbols;
            Record(recording => recording.MergedMacros[name] = symbols);

            _mergedMacroDefinitions[name] = _mergedMacroDefinitions.TryGetValue(name, out (SymbolCondition Condition, int Count) previous)
                ? (previous.Condition.Or(CurrentCondition), previous.Count + 1)
                : (CurrentCondition, 1);
        }
    }

    /// <summary>取り込みの展開結果を使い回す鍵に入れる、マクロの状態のハッシュ。</summary>
    /// <remarks>
    /// 同じマクロ表でも、どのマクロが構成によって変わるか、ヘッダのマクロがどの条件で定義されたかが違えば、
    /// 並べる分岐も条件の読み方も変わる。
    /// </remarks>
    private ulong CacheStateHash => _macroTableHash ^ _conditionallyDefinedHash ^ _headerDefinitionsHash;

    /// <summary>
    /// 取り込んだヘッダのマクロ定義を、定義がある条件とともに覚える。
    /// </summary>
    /// <param name="nameToken">マクロ名のトークン。</param>
    /// <remarks>
    /// <para>
    /// 並べた分岐の中の定義は、その分岐の条件のもとで定義されたものとして覚える。
    /// 並べた分岐の外の定義は、すでに条件付きで覚えている名前だけを「常に」として覚え直す。
    /// すべての名前を覚えると、ヘッダの数千のマクロを抱えることになる。
    /// </para>
    /// <para>
    /// 条件は並べた分岐の条件 (<see cref="CurrentCondition"/>) を使う。
    /// ヘッダの条件は解析しているファイルのシンボルを経路の条件として読まない (<see cref="TryReadPathCondition"/>) ので、
    /// 経路の条件では足りない。
    /// </para>
    /// </remarks>
    private void RecordHeaderDefinition(HlslSyntaxToken nameToken)
    {
        if (IsInRootFile(nameToken))
        {
            return;
        }

        SymbolCondition condition = CurrentCondition;

        if (condition.IsAlways && !_headerDefinitions.ContainsKey(nameToken.Text))
        {
            return;
        }

        SetHeaderDefinition(
            nameToken.Text,
            _headerDefinitions.TryGetValue(nameToken.Text, out SymbolCondition existing) ? existing.Or(condition) : condition);
    }

    /// <summary>ヘッダのマクロの、定義がある条件を書き換える。</summary>
    /// <param name="name">マクロの名前。</param>
    /// <param name="condition">定義がある条件。<see langword="null"/> なら忘れる。</param>
    private void SetHeaderDefinition(string name, SymbolCondition? condition)
    {
        NoteHeaderDefinitionWrite(name);

        if (_headerDefinitions.RawTryGetValue(name, out SymbolCondition previous))
        {
            _headerDefinitionsHash ^= HashHeaderDefinition(name, previous);
            _headerDefinitions.RawRemove(name);
        }

        if (condition is { } present)
        {
            _headerDefinitions.RawSet(name, present);
            _headerDefinitionsHash ^= HashHeaderDefinition(name, present);
        }

        Record(recording => recording.HeaderDefinitionChanges.Add(new(name, condition)));
    }

    /// <summary>条件の中で定義された名前 1 つ分のハッシュ。</summary>
    /// <param name="name">名前。</param>
    /// <returns>ハッシュ。</returns>
    private static ulong HashConditionallyDefined(string name)
        => (ulong)StringComparer.Ordinal.GetHashCode(name) * 0x9E3779B97F4A7C15UL;

    /// <summary>ヘッダのマクロ 1 つ分のハッシュ。</summary>
    /// <param name="name">マクロの名前。</param>
    /// <param name="condition">定義がある条件。</param>
    /// <returns>ハッシュ。</returns>
    private static ulong HashHeaderDefinition(string name, SymbolCondition condition)
        => (ulong)(uint)HashCode.Combine(StringComparer.Ordinal.GetHashCode(name), condition.GetHashCode()) * 0xC2B2AE3D27D4EB4FUL;

    /// <summary>
    /// 並べた分岐で定義したマクロが、コードとして展開されたことを覚える。
    /// </summary>
    /// <param name="name">展開したマクロの名前。</param>
    /// <param name="atExpansion">
    /// 展開したその場で呼んでいるか。取り込みの結果を使い回したときは <see langword="false"/> で、使う位置の条件が分からない。
    /// </param>
    /// <remarks>
    /// その展開はどれか 1 つの構成のものでしかない。
    /// 呼び出し側は、このシンボルを並べずに展開し直す (<c>ShaderCompilation.Build</c>)。
    /// </remarks>
    private void NoteMergedMacroUse(string name, bool atExpansion = true)
    {
        NoteUnhoistedExpansion(name);

        // 定義した分岐の中 (定義の条件を含む条件の下) で使うなら、どの構成でもその定義が効いている。
        if (atExpansion && IsUsedWhereDefined(name))
        {
            return;
        }

        if (_mergedMacros.TryGetValue(name, out ImmutableArray<string> symbols))
        {
            _mergedMacroConflicts.UnionWith(symbols);
            Record(recording => recording.MergedMacroConflicts.UnionWith(symbols));
        }

        // 取り込みの結果は使い回される。そのときは展開をやり直さないので、記録から知るしかない。
        if (_conditionalMacros.ContainsKey(name))
        {
            Record(recording => recording.ExpandedRootMacros.Add(name));
        }
    }

    /// <summary>並べた分岐で定義したマクロを、定義した条件の下で使っているかを判定する。</summary>
    /// <param name="name">展開したマクロの名前。</param>
    /// <returns>定義が 1 つだけで、今の位置の条件がその定義の条件を含んでいれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <code>
    /// #ifdef _A
    /// #define USE_A_IN_CODE 1
    /// float G() { return USE_A_IN_CODE; }   // _A のときにしか無い。そのとき定義も効いている
    /// #endif
    /// </code>
    /// </remarks>
    private bool IsUsedWhereDefined(string name)
    {
        if (!_mergedMacroDefinitions.TryGetValue(name, out (SymbolCondition Condition, int Count) definition)
            || definition.Count != 1
            || definition.Condition.IsUnknown)
        {
            return false;
        }

        SymbolCondition use = CurrentCondition;

        return !use.IsUnknown && !_options.SymbolConstraints.IsPossible(use.And(definition.Condition.Negate()));
    }

    /// <summary>条件付きで覚えるマクロの定義 1 つ。</summary>
    /// <param name="Body">名前より後ろの記述。仮引数の括弧も含む。</param>
    /// <param name="Condition">その定義に通る条件。</param>
    /// <param name="Definition">読み取った定義。巻き上げに使う。</param>
    private readonly record struct ConditionalMacro(
        string Body,
        SymbolCondition Condition,
        MacroDefinition Definition);

    /// <summary>
    /// 解析しているファイルのマクロ定義を、条件付きで覚える。
    /// </summary>
    /// <param name="nameToken">マクロ名のトークン。</param>
    /// <param name="line">指令の行のトークン。先頭はマクロ名。</param>
    /// <remarks>
    /// <para>
    /// <b>読み飛ばした分岐の定義も覚える。</b>
    /// <c>#ifdef _A</c> と <c>#ifdef _B</c> の両方で同じ名前を違う中身で定義すると、
    /// 両方が有効な構成では「中身を変えた再定義」になる。
    /// 単一構成の展開ではどちらか一方しか通らないので、条件を持たなければ気づけない。
    /// </para>
    /// <para>
    /// 取り込んだヘッダの定義は覚えない。ヘッダは取り込む順序で中身を差し替える書き方を多用しており、
    /// 条件も追えていない。利用者が直せる範囲でもない。
    /// </para>
    /// </remarks>
    private void RecordConditionalDefinition(HlslSyntaxToken nameToken, ImmutableArray<HlslSyntaxToken> line)
    {
        if (!IsInRootFile(nameToken))
        {
            return;
        }

        SymbolCondition condition = _options.SymbolConstraints.Apply(CurrentPathCondition);

        if (condition.IsNever)
        {
            return;
        }

        string body = string.Join(" ", line.Skip(1).Select(token => token.Text));

        if (!_conditionalMacros.TryGetValue(nameToken.Text, out List<ConditionalMacro>? definitions))
        {
            definitions = [];
            _conditionalMacros[nameToken.Text] = definitions;
        }

        foreach (ConditionalMacro previous in definitions)
        {
            // どちらも「常に」なら、展開の側の検査が報告している。
            if (string.Equals(previous.Body, body, StringComparison.Ordinal)
                || (previous.Condition.IsAlways && condition.IsAlways)
                || !_options.SymbolConstraints.IsPossible(previous.Condition.And(condition)))
            {
                continue;
            }

            SymbolCondition both = _options.SymbolConstraints.Apply(previous.Condition.And(condition));

            Report(HlslDescriptors.PreprocessorError, nameToken.GetLocation(),
                $"マクロ '{nameToken.Text}' を中身を変えて定義し直しています"
                + (both.IsAlways ? string.Empty : $" ({both} のとき)")
                + "。#undef してから定義してください。");
            break;
        }

        MacroDefinition definition = ParseMacroDefinition(line, report: false);

        definitions.Add(new ConditionalMacro(body, condition, definition));
        _writtenDefinitions.Add((definition, condition));
    }

    /// <summary>
    /// <c>#undef</c> されたマクロの、条件付きの記録を捨てる。
    /// </summary>
    /// <param name="name">マクロの名前。</param>
    /// <remarks>
    /// 条件付きの <c>#undef</c> を追うと、定義がある条件は「定義 ∧ その後に undef されていない」になる。
    /// そこまで追わずに、<c>#undef</c> された名前は覚えるのをやめる。
    /// 分からないものを根拠に再定義とは言わない。
    /// </remarks>
    private void ForgetConditionalDefinitions(string name) => _conditionalMacros.Remove(name);

    /// <summary>ヘッダの <c>#undef</c> を、定義がある条件に反映する。</summary>
    /// <param name="nameToken">マクロ名のトークン。</param>
    /// <remarks>
    /// <para>
    /// 並べた分岐の外の <c>#undef</c> は、それまでの定義を消すだけである。
    /// </para>
    /// <para>
    /// <b>並べた分岐の中の <c>#undef</c> は、条件として書けない。</b>
    /// 「その条件のときは定義されていない」を、定義がある条件の足し合わせでは表せない。
    /// その分岐のシンボルは並べずに展開し直す (<see cref="PreprocessResult.MergedMacroConflicts"/>)。
    /// </para>
    /// </remarks>
    private void ForgetHeaderDefinition(HlslSyntaxToken nameToken)
    {
        if (IsInRootFile(nameToken))
        {
            return;
        }

        SymbolCondition condition = CurrentCondition;

        if (!condition.IsAlways)
        {
            ImmutableArray<string> symbols = [.. condition.EnumerateSymbols()];

            _mergedMacroConflicts.UnionWith(symbols);
            Record(recording => recording.MergedMacroConflicts.UnionWith(symbols));
            return;
        }

        if (_headerDefinitions.ContainsKey(nameToken.Text))
        {
            SetHeaderDefinition(nameToken.Text, null);
        }
    }

    /// <summary>
    /// その名前が定義されている条件を返す。
    /// </summary>
    /// <param name="name">マクロの名前。</param>
    /// <returns>定義されている条件。条件付きで覚えていなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>#ifndef TINT</c> のような条件を読むのに使う。
    /// 覚えていなければ、その場の定義の有無で解く (<see cref="ReadDefinedAtom"/>)。
    /// </remarks>
    private SymbolCondition? GetDefinedCondition(string name)
    {
        bool fromHeader = _headerDefinitions.TryGetValue(name, out SymbolCondition header);

        if (!_conditionalMacros.TryGetValue(name, out List<ConditionalMacro>? definitions))
        {
            return fromHeader ? header : null;
        }

        SymbolCondition condition = fromHeader ? header : SymbolCondition.Never;

        foreach (ConditionalMacro definition in definitions)
        {
            condition = condition.Or(definition.Condition);
        }

        return condition;
    }

    /// <summary>非活性領域に現れた識別子を覚える。</summary>
    /// <param name="token">識別子のトークン。</param>
    /// <remarks>
    /// <para>
    /// <b>解析しているファイル自身のものは、位置ごと覚える。</b>
    /// この構成で読み飛ばした場所を、別の構成が解析していることがある。
    /// そこにある宣言は別の構成の木に見えているので、名前だけを「宣言済み」として扱う必要は無い。
    /// どの構成でも読み飛ばされた場所かどうかは、位置が無いと見分けられない
    /// (<see cref="PreprocessResult.SkippedRootIdentifiers"/>)。
    /// </para>
    /// <para>
    /// <b>記録へは、全体の集合に新しいかどうかに関わらず足す。</b>
    /// 詳しくは <see cref="Record"/> を見ること。
    /// 取り込みの記録に入るのは取り込んだファイルのものだけなので、位置は要らない。
    /// </para>
    /// </remarks>
    private void AddSkippedIdentifier(HlslSyntaxToken token)
    {
        string name = token.Text;

        _skippedIdentifiers.Add(name);

        if (IsInRootSource(token))
        {
            _skippedRootIdentifiers.Add(token);
            return;
        }

        _skippedIncludedIdentifiers.Add(name);
        Record(recording => recording.SkippedIdentifiers.Add(name));
    }

    /// <summary>両方の分岐を残せなかったシンボルを覚える。</summary>
    /// <param name="name">シンボル。</param>
    /// <param name="reason">残せなかった理由。記録済みなら <see langword="null"/> (二重に数えない)。</param>
    /// <param name="at">その条件の指令のトークン。書かれたファイルを覚えるのに使う。</param>
    /// <param name="hoistableMacros">
    /// 巻き上げで補えるかもしれない領域なら、その領域が定義するマクロ (<see cref="FindHoistableDefinitions"/>)。
    /// </param>
    private void AddDeclinedSymbol(
        string name,
        BothBranchDeclineReason? reason,
        HlslSyntaxToken at,
        ImmutableArray<string>? hoistableMacros = null)
    {
        if (hoistableMacros is { } macros)
        {
            NoteHoistableDecline(name, macros);
        }
        else
        {
            _firmDeclines.Add(name);
        }

        _declinedBothBranchSymbols.Add(name);
        Record(recording => recording.DeclinedBothBranchSymbols.Add(name));

        if (reason is { } known)
        {
            BothBranchDecline decline = new(name, known, at.Source.FilePath) { DirectiveSpan = at.Span };

            _bothBranchDeclines.Add(decline);
            Record(recording => recording.BothBranchDeclines.Add(decline));
        }
    }

    /// <summary>両方の分岐を並べた条件のシンボルを覚える。</summary>
    /// <param name="condition">並べた分岐の条件。</param>
    /// <remarks>
    /// 中身が <c>#define</c> だけの分岐は、並べてもトークンを出さないので条件付きの範囲に現れない。
    /// 範囲だけから数えると、並べたのに並べなかったことになり、要らない構成を展開する。
    /// </remarks>
    private void AddMergedSymbols(SymbolCondition condition)
    {
        foreach (string symbol in condition.EnumerateSymbols())
        {
            if (_mergedSymbols.Add(symbol))
            {
                Record(recording => recording.MergedSymbols.Add(symbol));
            }
        }
    }

    /// <summary>1 つずつ有効にしただけでは通らないシンボルの組を覚える。</summary>
    /// <param name="combination">同時に定義されている必要があるシンボル。</param>
    /// <param name="directive">その組でしか通らない分岐を始めた指令の名前のトークン。</param>
    /// <remarks>
    /// 取り込んだヘッダに書かれた条件は覚えない。呼ぶ側が確かめる
    /// (<see cref="RecordRequiredCombinations"/>)。
    /// ヘッダの条件を満たす構成を作っても、解析しているファイルのコードは変わらない。
    /// </remarks>
    private void AddRequiredCombination(ImmutableArray<string> combination, HlslSyntaxToken directive)
    {
        ImmutableArray<string> sorted = [.. combination.Order(StringComparer.Ordinal)];

        _requiredSymbolCombinations.TryAdd(
            string.Join('\u0000', sorted),
            new SymbolCombination(sorted, directive.GetLocation()));
    }

    /// <summary>条件の中で定義された名前を覚える。</summary>
    /// <param name="name">名前。</param>
    private void AddConditionallyDefined(string name)
    {
        // 足したあとは、外の状態によらず「ある」し、集合は空でない。
        NoteConditionallyDefinedWrite(name);
        NoteConditionallyDefinedWrite(TrackedNameSet.CountName);

        if (_conditionallyDefinedMacros.RawAdd(name))
        {
            _conditionallyDefinedHash ^= HashConditionallyDefined(name);
        }

        Record(recording => recording.ConditionallyDefined.Add(name));
    }

    /// <summary>解決できた取り込み先を覚える。</summary>
    /// <param name="path">パス。</param>
    private void AddResolvedInclude(string path)
    {
        _resolvedIncludes.Add(path);
        Record(recording => recording.ResolvedIncludes.Add(path));
    }

    /// <summary>解決できなかった取り込みを覚える。</summary>
    /// <param name="path">パス。</param>
    private void AddUnresolvedInclude(string path)
    {
        _unresolvedIncludes.Add(path);
        Record(recording => recording.UnresolvedIncludes.Add(path));
    }

    /// <summary>
    /// 記録中の取り込みすべてに、起きたことを書き足す。
    /// </summary>
    /// <param name="add">書き足す処理。</param>
    /// <remarks>
    /// <para>
    /// <b>集合を複製しない。</b>
    /// 記録の開始時に集合をまるごとコピーする形にすると、
    /// 取り込み 1 つにつき集合の大きさに比例した費用がかかる。
    /// 取り込みは 1 回の実行で 1,000 回近く起きるので、それだけで展開より高くつく。
    /// </para>
    /// <para>
    /// <b>呼ぶ側は「全体の集合に新しいときだけ」という条件を付けてはならない。</b>
    /// 記録は<b>そのヘッダが何を足したか</b>でなければならない。
    /// 新しいときだけ書き足すと、記録の中身が<b>そのヘッダより前に何を見たか</b>に
    /// 依存してしまう。取り込みの手前の状態は鍵 (<see cref="IncludeCacheKey"/>) に
    /// 入っていないので、同じ鍵で中身の違う記録ができあがる。
    /// </para>
    /// <para>
    /// 実際にそうなっていた。<c>RingBuffer.hlsl</c> を取り込み済みの経路で作られた記録は
    /// 非活性領域の名前を 14 個しか持たず、未取り込みの経路で作られた記録は 19 個持つ。
    /// 前者を後者の文脈で使い回すと <c>RingBuffer</c> が「どこにも宣言されていない」
    /// (HL0310) になる。並列に解析するとどちらが先に記録されるかが変わるため、
    /// <b>実行するたびに結果が変わっていた。</b>
    /// </para>
    /// </remarks>
    private void Record(Action<IncludeRecording> add)
    {
        foreach (IncludeRecording recording in _recordings)
        {
            add(recording);
        }
    }

    /// <summary>
    /// マクロ表を書き換える。
    /// </summary>
    /// <param name="name">マクロの名前。</param>
    /// <param name="definition">新しい定義。<see langword="null"/> なら削除。</param>
    /// <remarks>
    /// <b>書き換えはすべてここを通す。</b>
    /// マクロ表のハッシュ (<see cref="_macroTableHash"/>) が実際の表とずれると、
    /// 取り込みの展開結果を**別の状態のもので使い回してしまう**。
    /// ずれても症状は「違う展開結果が返る」ことだけで、エラーにも例外にもならない。
    /// </remarks>
    private void SetMacro(string name, MacroDefinition? definition)
    {
        // 覚えている最中なら、この名前が最後にどうなったかを記録する。
        // 入れ子で覚えている場合、変化は外側の取り込みの分にも入る。
        foreach (IncludeRecording recording in _recordings)
        {
            recording.MacroChanges[name] = definition;
        }

        NoteMacroWrite(name);

        if (_macroHashes.Remove(name, out ulong existing))
        {
            _macroTableHash ^= existing;
        }

        if (definition is null)
        {
            _macros.RawRemove(name);
            return;
        }

        ulong hash = HashMacro(name, definition);
        _macros.RawSet(name, definition);
        _macroHashes[name] = hash;
        _macroTableHash ^= hash;
    }

    /// <summary>
    /// マクロ 1 つ分のハッシュを求める。
    /// </summary>
    /// <param name="name">マクロの名前。</param>
    /// <param name="definition">定義。</param>
    /// <returns>ハッシュ。</returns>
    /// <remarks>
    /// <para>
    /// 本体はトークンの文字列を連ねて数える。
    /// 同じ名前を同じ中身で定義し直した場合に鍵が変わらないようにするためである。
    /// </para>
    /// <para>
    /// <b>64 ビットで数える。</b>
    /// 排他的論理和で足し引きする表のハッシュなので、
    /// 幅が足りないと「別の表なのに同じ鍵」が現実的な確率で起きる。
    /// それは、誤りと分からないまま違う展開結果を使い回すことを意味する。
    /// </para>
    /// </remarks>
    private static ulong HashMacro(string name, MacroDefinition definition)
    {
        const ulong Offset = 14695981039346656037;

        ulong hash = Offset;

        Fold(ref hash, name);
        Fold(ref hash, definition.IsFunctionLike ? "()" : ".");
        Fold(ref hash, definition.IsVariadic ? "..." : ".");

        foreach (string parameter in definition.Parameters)
        {
            Fold(ref hash, parameter);
        }

        foreach (HlslSyntaxToken token in definition.Body)
        {
            Fold(ref hash, token.Text);
        }

        // 排他的論理和で足し引きするので、0 は「表に無い」と区別が付かない。
        return hash | 1UL;
    }

    /// <summary>文字列を FNV-1a で畳み込む。</summary>
    /// <param name="hash">畳み込む先。</param>
    /// <param name="text">畳み込む文字列。</param>
    private static void Fold(ref ulong hash, string text)
    {
        const ulong Prime = 1099511628211;

        foreach (char c in text)
        {
            hash = (hash ^ c) * Prime;
        }

        // 区切りを入れる。"AB" + "C" と "A" + "BC" を同じにしない。
        hash = (hash ^ 0xFF) * Prime;
    }

    /// <summary>そのトークンが、解析しているファイルに書かれたものかを判定する。</summary>
    /// <param name="token">対象のトークン。</param>
    /// <returns>このファイルのものであれば <see langword="true"/>。</returns>
    private bool IsInRootSource(HlslSyntaxToken token)
        => _rootSource is { } root && ReferenceEquals(token.Source, root);

    /// <summary>
    /// 2 つのマクロ定義が同じ中身かを判定する。
    /// </summary>
    /// <param name="left">比べる定義。</param>
    /// <param name="right">比べる定義。</param>
    /// <returns>同じであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// トークンの文字列で比べる。空白や改行の違いは中身の違いではない。
    /// </remarks>
    private static bool HasSameBody(MacroDefinition left, MacroDefinition right)
    {
        if (left.IsFunctionLike != right.IsFunctionLike
            || left.IsVariadic != right.IsVariadic
            || !left.Parameters.SequenceEqual(right.Parameters, StringComparer.Ordinal)
            || left.Body.Length != right.Body.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Body.Length; i++)
        {
            if (!string.Equals(left.Body[i].Text, right.Body[i].Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>#undef</c> を処理する。</summary>
    private void ProcessUndef()
    {
        ImmutableArray<HlslSyntaxToken> line = ReadDirectiveLine();

        if (line.IsEmpty || line[0].Kind != HlslSyntaxKind.IdentifierToken)
        {
            Report(HlslDescriptors.PreprocessorError, GetDirectiveLocation(line),
                "#undef にはマクロ名が必要です。");
            return;
        }

        ForgetConditionalDefinitions(line[0].Text);
        ForgetHeaderDefinition(line[0]);
        SetMacro(line[0].Text, null);

        // 構成によって変わる条件の中で消した名前も、構成によって定義が変わる。
        if (IsInConfigurationDependentBranch)
        {
            AddConditionallyDefined(line[0].Text);
        }
    }

    /// <summary>設定で与えられた定義済みマクロを登録する。</summary>
    private void DefinePredefinedMacros()
    {
        foreach ((string name, string value) in _options.PredefinedMacros)
        {
            SourceText text = SourceText.From($"#define {name} {value}", "<定義済みマクロ>");
            HlslLexer lexer = new(text);
            ImmutableArray<HlslSyntaxToken> tokens = lexer.Lex(out _);

            // "#" "define" "NAME" 以降が本体になる。
            ImmutableArray<HlslSyntaxToken> body =
                [.. tokens.Skip(3).Where(t => t.Kind != HlslSyntaxKind.EndOfFileToken)];

            HlslSyntaxToken nameToken = tokens.Length > 2
                ? tokens[2]
                : new HlslSyntaxToken(HlslSyntaxKind.IdentifierToken, text, new TextSpan(0, 0), name);

            SetMacro(name, new MacroDefinition(name, nameToken, isFunctionLike: false, [], isVariadic: false, body));
        }
    }
}
