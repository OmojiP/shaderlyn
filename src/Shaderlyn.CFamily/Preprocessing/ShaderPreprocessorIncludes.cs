using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.CFamily;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.CFamily.Preprocessing;

/// <summary>
/// #include。取り込み先の解決、循環の検出、展開結果の使い回し。
/// </summary>
internal sealed partial class ShaderPreprocessor
{
    // --------------------------------------------------------------------
    // #include
    // --------------------------------------------------------------------

    /// <summary>
    /// <c>#include</c> を処理する。
    /// </summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    /// <param name="directive">指令の名前 (<c>include</c>、環境が足した <c>include_with_pragmas</c> など)。取り込みの記録に残す。</param>
    /// <remarks>
    /// パスの記法は <c>"..."</c> と <c>&lt;...&gt;</c> の 2 通りある。
    /// 山括弧は字句解析では比較演算子に分解されているため、
    /// トークンを結合して元のパスを復元する必要がある。
    /// </remarks>
    private void ProcessInclude(ShaderSyntaxToken directiveToken, string directive = "include")
    {
        ImmutableArray<ShaderSyntaxToken> line = ReadDirectiveLine();

        string? path = ExtractIncludePath(line);
        if (path is null)
        {
            Report(ShaderDescriptors.PreprocessorError, directiveToken.GetLocation(),
                "#include のパスを解釈できません。");
            return;
        }

        Location pathLocation = GetIncludePathLocation(directiveToken, line);

        if (_options.IncludeResolver is null)
        {
            AddUnresolvedInclude(path);
            _includes.Add(new IncludeReference(path, pathLocation, ResolvedFilePath: null) { Directive = directive });
            return;
        }

        string includingFilePath = directiveToken.Source.FilePath;

        if (!TryResolveInclude(_options.IncludeResolver, path, includingFilePath, out SourceText? included, out bool fromFallback))
        {
            AddUnresolvedInclude(path);
            _includes.Add(new IncludeReference(path, pathLocation, ResolvedFilePath: null)
            {
                Directive = directive,
                OutsideBoundaryPath = _options.IncludeResolver.FindOutsideBoundary(path, includingFilePath),
            });

            if (_options.ReportUnresolvedIncludes)
            {
                Report(ShaderDescriptors.UnresolvedInclude, directiveToken.GetLocation(), path, string.Empty);
            }

            return;
        }

        _includes.Add(new IncludeReference(path, pathLocation, included.FilePath) { IsUtf16 = included.IsUtf16, IsResolvedFromFallback = fromFallback, Directive = directive });

        // #pragma once を書いたファイルの 2 度目の取り込みは、丸ごと飛ばす (MarkIncludedOnce)。
        // 互いを取り込むヘッダ (Platform.ush と BindlessResources.ush) も、これで循環にならない。
        if (IsIncludedOnce(included.FilePath))
        {
            NoteSkippedReinclude(included.FilePath);
            AddResolvedInclude(included.FilePath);
            return;
        }

        if (_activeIncludePaths.Contains(included.FilePath))
        {
            Report(ShaderDescriptors.CircularInclude, directiveToken.GetLocation(), path);

            // 循環かどうかは、そのときの取り込みの連なりで決まる。囲んでいる取り込みの結果は覚えない。
            // 覚えると、別のファイルを解析したときの連なりで見つけた循環を使い回し、
            // 解析の順番で報告の位置が変わっていた (ファイルを並列に解析する CLI では、実行のたびに変わりうる)。
            foreach (IncludeRecording outer in _recordings)
            {
                outer.Uncacheable = true;
            }

            return;
        }

        int includeDepth = _sources.Count(source => source.IncludePath is not null);
        if (includeDepth >= _options.MaxIncludeDepth)
        {
            Report(ShaderDescriptors.PreprocessorError, directiveToken.GetLocation(),
                $"include の入れ子が深すぎます (上限 {_options.MaxIncludeDepth} 段)。");
            return;
        }

        AddResolvedInclude(included.FilePath);
        NoteFirstInclusion(new FirstInclusion(included.FilePath, includingFilePath, _options.SymbolConstraints.Apply(CurrentCondition)));

        // 利用者が書いたヘッダは覚えも使い回しもしない。条件付きで覚えた定義 (RecordConditionalDefinition) は
        // 展開結果に入らないので、使い回すと巻き上げができなくなる。囲んでいる取り込みの記録も覚えない。
        if (IsUserHeaderInclude(included))
        {
            foreach (IncludeRecording outer in _recordings)
            {
                outer.Uncacheable = true;
            }

            PushFileSource(included, included.FilePath);
            return;
        }

        if (TryReuseExpansion(included))
        {
            return;
        }

        PushFileSource(included, included.FilePath);
        BeginRecording(included);
    }

    /// <summary>
    /// 取り込みを引く。書いたファイルから引けなければ、<see cref="PreprocessorOptions.FallbackIncludingFilePath"/> から引き直す。
    /// </summary>
    /// <param name="resolver">取り込みを解決する仕組み。</param>
    /// <param name="path">書かれたパス。</param>
    /// <param name="includingFilePath">取り込みを書いたファイル。</param>
    /// <param name="included">引いたファイル。</param>
    /// <param name="fromFallback">書いたファイルから引けず、<see cref="PreprocessorOptions.FallbackIncludingFilePath"/> から引けたか。</param>
    /// <returns>引けたら <see langword="true"/>。</returns>
    private bool TryResolveInclude(
        IIncludeResolver resolver,
        string path,
        string includingFilePath,
        [NotNullWhen(true)] out SourceText? included,
        out bool fromFallback)
    {
        fromFallback = false;

        if (resolver.TryResolve(path, includingFilePath, out included))
        {
            return true;
        }

        if (_options.FallbackIncludingFilePath is not { } fallback
            || string.Equals(Path.GetDirectoryName(fallback), Path.GetDirectoryName(includingFilePath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 引き直した結果 (引けたかどうかも) は解析しているファイルの場所で変わる。囲んでいる取り込みの展開結果は覚えない。
        foreach (IncludeRecording outer in _recordings)
        {
            outer.Uncacheable = true;
        }

        fromFallback = resolver.TryResolve(path, fallback, out included);
        return fromFallback;
    }

    /// <summary>利用者が書いたヘッダで、展開結果を覚えないものかを判定する。</summary>
    /// <param name="included">取り込んだファイル。</param>
    /// <returns>覚えないなら <see langword="true"/>。</returns>
    private bool IsUserHeaderInclude(SourceText included)
        => IsHoistingEnabled && IsUserFile(included.FilePath);

    /// <summary>
    /// 覚えてある展開結果を使えるなら使う。
    /// </summary>
    /// <param name="included">取り込んだファイル。</param>
    /// <returns>使い回した場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>覚えるのも使うのも、条件の外で取り込まれた場合だけである。</b>
    /// 条件の中では、その条件がまとめられているかどうかで
    /// 展開の仕方 (両方の分岐を残すか) が変わる。
    /// 入れ子のヘッダも、外側のヘッダごと覚えるので取りこぼしにはならない。
    /// </remarks>
    private bool TryReuseExpansion(SourceText included)
    {
        if (_options.IncludeCache is not { } cache || _conditionals.Count > 0)
        {
            return false;
        }

        IncludeCacheKey key = new(included.FilePath, CacheStateHash, _options.FingerprintForCache);

        if (!cache.TryGet(key, reads => DigestReads(reads) == reads.Digest, out IncludeExpansion? expansion)
            || expansion is null)
        {
            return false;
        }

        NoteReusedReads(expansion.Reads);
        Replay(expansion);
        return true;
    }

    /// <summary>
    /// これから展開する取り込みを、覚える対象として記録し始める。
    /// </summary>
    /// <param name="included">取り込んだファイル。</param>
    private void BeginRecording(SourceText included)
    {
        if (_options.IncludeCache is null || _conditionals.Count > 0)
        {
            return;
        }

        _recordings.Add(new IncludeRecording(
            new IncludeCacheKey(included.FilePath, CacheStateHash, _options.FingerprintForCache),
            included.FilePath,
            _output.Count,
            _pragmas.Count,
            _diagnostics.Count,
            _conditionalRegions.Count,
            _conditionalIdentifiers.Count,
            _includes.Count,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            BothBranchDeclines: [],
            MergedSymbols: new HashSet<string>(StringComparer.Ordinal),
            MergedRegions: [],
            KeywordRegions: [],
            MacroAffectingSymbols: new HashSet<string>(StringComparer.Ordinal),
            MacroKeywordChanges: [],
            UnknownMacroSources: new HashSet<string>(StringComparer.Ordinal),
            CodeIdentifiers: new HashSet<string>(StringComparer.Ordinal),
            HeaderDefinitionChanges: [],
            MergedMacros: new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, MacroDefinition?>(StringComparer.Ordinal)));
    }

    /// <summary>
    /// 記録していた取り込みが終わったので、展開結果を覚える。
    /// </summary>
    /// <param name="filePath">読み終えたファイルのパス。</param>
    private void EndRecording(string filePath)
    {
        if (_recordings.Count == 0
            || _options.IncludeCache is not { } cache
            || !string.Equals(_recordings[^1].FilePath, filePath, StringComparison.Ordinal))
        {
            return;
        }

        IncludeRecording recording = _recordings[^1];
        _recordings.RemoveAt(_recordings.Count - 1);

        // 途中で条件が閉じられていない (ヘッダが #if を開いたまま終わった) 場合、
        // 続きの展開はこのヘッダの外の状態に依存する。覚えない。
        // 利用者が書いたヘッダを取り込んでいた場合と、循環を見つけた場合も覚えない (IsUserHeaderInclude)。
        if (_conditionals.Count > 0 || recording.Uncacheable)
        {
            return;
        }

        int start = recording.OutputStart;

        cache.Add(recording.Key, new IncludeExpansion(
            Tokens: [.. _output.Skip(start)],
            MacroChanges: [.. recording.MacroChanges],
            ConditionallyDefined: [.. recording.ConditionallyDefined],
            Pragmas: [.. _pragmas.Skip(recording.PragmaStart)],
            Diagnostics: [.. _diagnostics.Skip(recording.DiagnosticStart)],
            ConditionalRegions:
            [
                .. _conditionalRegions.Skip(recording.ConditionalRegionStart)
                    .Select(r => r with { Start = r.Start - start }),
            ],
            ConditionalIdentifiers: [.. _conditionalIdentifiers.Skip(recording.ConditionalIdentifierStart)],
            SkippedIdentifiers: [.. recording.SkippedIdentifiers],
            ExpandedRootMacros: [.. recording.ExpandedRootMacros],
            DeclinedBothBranchSymbols: [.. recording.DeclinedBothBranchSymbols],
            BothBranchDeclines: [.. recording.BothBranchDeclines],
            MergedSymbols: [.. recording.MergedSymbols],
            MergedRegions: [.. recording.MergedRegions],
            KeywordRegions: [.. recording.KeywordRegions],
            MacroAffectingSymbols: [.. recording.MacroAffectingSymbols],
            MacroKeywordChanges: [.. recording.MacroKeywordChanges],
            UnknownMacroSources: [.. recording.UnknownMacroSources],
            CodeIdentifiers: [.. recording.CodeIdentifiers],
            HeaderDefinitionChanges: [.. recording.HeaderDefinitionChanges],
            MergedMacros: [.. recording.MergedMacros],
            MergedMacroConflicts: [.. recording.MergedMacroConflicts],
            Includes: [.. _includes.Skip(recording.IncludeStart)],
            ResolvedIncludes: [.. recording.ResolvedIncludes],
            UnresolvedIncludes: [.. recording.UnresolvedIncludes])
        {
            Reads = ToReadSet(recording),
            SkippedUserIdentifiers = [.. recording.SkippedUserIdentifiers],
            FirstInclusions = [.. recording.FirstInclusions],
            SkippedReincludes = [.. recording.SkippedReincludes],
            ReachedErrorDirectives = [.. recording.ReachedErrorDirectives],
        });
    }

    /// <summary>
    /// 覚えてある展開結果を、今の状態へ適用する。
    /// </summary>
    /// <param name="expansion">適用する展開結果。</param>
    /// <remarks>
    /// 出したものと変えたものを、記録したときと同じ順序で足し直す。
    /// トークンは位置を持つ不変の値なので、そのまま使い回せる。
    /// </remarks>
    private void Replay(IncludeExpansion expansion)
    {
        int start = _output.Count;

        _output.AddRange(expansion.Tokens);
        _pragmas.AddRange(expansion.Pragmas);
        _diagnostics.AddRange(expansion.Diagnostics);
        _conditionalIdentifiers.AddRange(expansion.ConditionalIdentifiers);
        _includes.AddRange(expansion.Includes);

        foreach (ConditionalTokenRange range in expansion.ConditionalRegions)
        {
            _conditionalRegions.Add(range with { Start = range.Start + start });
        }

        foreach ((string name, MacroDefinition? definition) in expansion.MacroChanges)
        {
            SetMacro(name, definition);
        }

        foreach (string name in expansion.ConditionallyDefined)
        {
            AddConditionallyDefined(name);
        }
        _skippedIdentifiers.UnionWith(expansion.SkippedIdentifiers);
        _skippedIncludedIdentifiers.UnionWith(expansion.SkippedIdentifiers);

        foreach (FirstInclusion inclusion in expansion.FirstInclusions)
        {
            NoteFirstInclusion(inclusion);
        }

        _reachedErrorDirectives.AddRange(expansion.ReachedErrorDirectives);

        if (!expansion.ReachedErrorDirectives.IsEmpty)
        {
            Record(recording => recording.ReachedErrorDirectives.AddRange(expansion.ReachedErrorDirectives));
        }

        foreach ((string file, SymbolCondition condition) in expansion.SkippedReincludes)
        {
            _skippedReincludes.Add(new(file, condition));
            Record(recording => recording.SkippedReincludes.Add(new(file, condition)));
        }

        foreach (ShaderSyntaxToken token in expansion.SkippedUserIdentifiers)
        {
            _skippedIdentifiers.Add(token.Text);
            _skippedUserHeaderIdentifiers.Add(token);
            Record(recording => recording.SkippedUserIdentifiers.Add(token));
        }

        // 取り込みの結果を使い回すと、その中の展開はもう一度は行われない。
        // 並べた分岐で定義したマクロが使われていたかどうかは、記録から知る。
        foreach (string name in expansion.ExpandedRootMacros)
        {
            NoteMergedMacroUse(name, atExpansion: false);
        }

        // ヘッダの領域は巻き上げで補わない (FindHoistableDefinitions)。
        _declinedBothBranchSymbols.UnionWith(expansion.DeclinedBothBranchSymbols);
        _firmDeclines.UnionWith(expansion.DeclinedBothBranchSymbols);
        _bothBranchDeclines.UnionWith(expansion.BothBranchDeclines);
        _mergedSymbols.UnionWith(expansion.MergedSymbols);
        _mergedRegions.UnionWith(expansion.MergedRegions);
        _keywordRegions.AddRange(expansion.KeywordRegions);
        _macroAffectingSymbols.UnionWith(expansion.MacroAffectingSymbols);
        _unknownMacroSources.UnionWith(expansion.UnknownMacroSources);

        foreach ((string macro, string symbol) in expansion.MacroKeywordChanges)
        {
            AddMacroKeyword(macro, symbol);
        }

        // コードに現れた名前は、今のマクロの記録で数え直す。覚えたときとは記録が違うことがある。
        // 取り込みの中で定義したマクロも数えるので、定義の記録を当て直してから数える (早めに数える側へ倒れる)。
        foreach (string used in expansion.CodeIdentifiers)
        {
            NoteMacroUseInCode(used);

            if (_recordings.Count > 0)
            {
                Record(recording => recording.CodeIdentifiers.Add(used));
            }
        }

        foreach ((string name, SymbolCondition? condition) in expansion.HeaderDefinitionChanges)
        {
            SetHeaderDefinition(name, condition);
        }

        foreach ((string name, ImmutableArray<string> symbols) in expansion.MergedMacros)
        {
            _mergedMacros[name] = symbols;
        }

        _mergedMacroConflicts.UnionWith(expansion.MergedMacroConflicts);
        _resolvedIncludes.UnionWith(expansion.ResolvedIncludes);
        _unresolvedIncludes.UnionWith(expansion.UnresolvedIncludes);

        // 使い回した結果を、外側で記録中の取り込みにも書き足す。
        // 書き足さないと、外側の取り込みを覚えたときに、入れ子で使い回した分が抜け落ちる。
        // 3 度目にその外側を使い回すと、解決できない取り込み (Unreal の生成される取り込み) などが消え、
        // 宣言が揃っていると誤って判断して検査を行っていた (エンジンのシェーダーで HL0310 が出たり出なかったりした)。
        // ほかの手続きを通して足しているもの (マクロ、条件の中で定義された名前、コードの名前など) は、そちらが記録している。
        if (_recordings.Count > 0)
        {
            Record(recording =>
            {
                recording.SkippedIdentifiers.UnionWith(expansion.SkippedIdentifiers);
                recording.DeclinedBothBranchSymbols.UnionWith(expansion.DeclinedBothBranchSymbols);
                recording.BothBranchDeclines.UnionWith(expansion.BothBranchDeclines);
                recording.MergedSymbols.UnionWith(expansion.MergedSymbols);
                recording.MergedRegions.UnionWith(expansion.MergedRegions);
                recording.KeywordRegions.AddRange(expansion.KeywordRegions);
                recording.MacroAffectingSymbols.UnionWith(expansion.MacroAffectingSymbols);
                recording.UnknownMacroSources.UnionWith(expansion.UnknownMacroSources);
                recording.MergedMacroConflicts.UnionWith(expansion.MergedMacroConflicts);
                recording.ResolvedIncludes.UnionWith(expansion.ResolvedIncludes);
                recording.UnresolvedIncludes.UnionWith(expansion.UnresolvedIncludes);

                foreach ((string name, ImmutableArray<string> symbols) in expansion.MergedMacros)
                {
                    recording.MergedMacros[name] = symbols;
                }
            });
        }

        // 覚えたときと同じ位置から次の条件付き範囲が始まる。
        _conditionalRangeStart = _output.Count;
    }

    /// <summary>
    /// <c>#include</c> に書かれたパスの位置を求める。
    /// </summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    /// <param name="line">指令の行のトークン。</param>
    /// <returns>パスの位置。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>"..."</c> と <c>&lt;...&gt;</c> で位置の求め方が違う。</b>
    /// 前者は 1 つの文字列リテラルだが、
    /// 後者は字句解析で比較演算子とその他のトークンに分解されている。
    /// 山括弧の側は <c>&lt;</c> から <c>&gt;</c> までをまとめて 1 つの範囲として扱う。
    /// </para>
    /// <para>
    /// 行が空の場合だけ指令名の位置に落とす。
    /// パスが書かれていないなら、指すべき場所は <c>#include</c> そのものしかない。
    /// </para>
    /// </remarks>
    private static Location GetIncludePathLocation(
        ShaderSyntaxToken directiveToken,
        ImmutableArray<ShaderSyntaxToken> line)
    {
        if (line.IsEmpty)
        {
            return directiveToken.GetLocation();
        }

        ShaderSyntaxToken first = line[0];

        if (first.Kind == ShaderSyntaxKind.StringLiteralToken)
        {
            return first.GetLocation();
        }

        // 山括弧の閉じが無い場合も、開いた位置までは指せる。
        ShaderSyntaxToken? last = line.LastOrDefault(t => t.Kind == ShaderSyntaxKind.GreaterThanToken);

        return last is not null
               && ReferenceEquals(last.Source, first.Source)
               && last.Span.End > first.Span.Start
            ? Location.Create(first.Source, TextSpan.FromBounds(first.Span.Start, last.Span.End))
            : first.GetLocation();
    }

    /// <summary>
    /// <c>#include</c> の行からパスを取り出す。
    /// </summary>
    /// <param name="line">指令の行のトークン。</param>
    /// <returns>取り出したパス。解釈できない場合は <see langword="null"/>。</returns>
    private static string? ExtractIncludePath(ImmutableArray<ShaderSyntaxToken> line)
    {
        if (line.IsEmpty)
        {
            return null;
        }

        if (line[0].Kind == ShaderSyntaxKind.StringLiteralToken)
        {
            return line[0].ValueText;
        }

        // 山括弧は < と > に分解されているため、間のトークンのテキストを連結して復元する。
        if (line[0].Kind == ShaderSyntaxKind.LessThanToken)
        {
            System.Text.StringBuilder builder = new();

            for (int i = 1; i < line.Length; i++)
            {
                if (line[i].Kind == ShaderSyntaxKind.GreaterThanToken)
                {
                    return builder.Length > 0 ? builder.ToString() : null;
                }

                builder.Append(line[i].Text);
            }
        }

        return null;
    }
    /// <summary>
    /// <c>#pragma</c> を、呼び出し側へ伝わるものと伝わらないものに分ける (<see cref="PreprocessorOptions.PragmaIncludeDirectives"/>)。
    /// </summary>
    /// <returns>伝わるものと、伝わらないもの。</returns>
    /// <remarks>
    /// <para>
    /// 取り込みの記録 (<see cref="IncludeReference"/>) から、取り込みのつながりを組み立てて決める。
    /// どのファイルにも取り込まれていないファイル (解析しているファイル、Unity の HLSLINCLUDE) から、伝える指令だけでたどれるファイルの
    /// <c>#pragma</c> が伝わる。
    /// </para>
    /// <para>
    /// 展開しながら決めないのは、取り込みの展開結果を使い回すためである。同じヘッダの展開結果が、
    /// <c>#include</c> からも <c>#include_with_pragmas</c> からも使われる。記録はどちらの場合も同じ形で残る。
    /// </para>
    /// </remarks>
    private (ImmutableArray<PragmaDirective> Pragmas, ImmutableArray<PragmaDirective> Ignored) PartitionPragmas()
    {
        if (_options.PragmaIncludeDirectives is not { } propagating || _includes.Count == 0)
        {
            return (_pragmas.ToImmutable(), []);
        }

        Dictionary<string, List<(string Target, bool Propagates)>> edges = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);

        foreach (IncludeReference include in _includes)
        {
            if (include.ResolvedFilePath is not { } target || include.Location.FilePath is not { } from)
            {
                continue;
            }

            included.Add(target);

            if (!edges.TryGetValue(from, out List<(string Target, bool Propagates)>? list))
            {
                list = [];
                edges[from] = list;
            }

            list.Add((target, propagating.Contains(include.Directive)));
        }

        // どこにも取り込まれていないファイルを起点に、伝える指令だけでたどる。
        HashSet<string> reached = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pending = new(edges.Keys.Where(file => !included.Contains(file)));

        foreach (string root in pending)
        {
            reached.Add(root);
        }

        while (pending.TryDequeue(out string? file))
        {
            if (!edges.TryGetValue(file, out List<(string Target, bool Propagates)>? targets))
            {
                continue;
            }

            foreach ((string target, bool propagates) in targets)
            {
                if (propagates && reached.Add(target))
                {
                    pending.Enqueue(target);
                }
            }
        }

        ImmutableArray<PragmaDirective>.Builder kept = ImmutableArray.CreateBuilder<PragmaDirective>();
        ImmutableArray<PragmaDirective>.Builder ignored = ImmutableArray.CreateBuilder<PragmaDirective>();

        foreach (PragmaDirective pragma in _pragmas)
        {
            string? file = pragma.NameToken.Source.FilePath;
            bool propagates = file is null || !included.Contains(file) || reached.Contains(file);
            (propagates ? kept : ignored).Add(pragma);
        }

        return (kept.ToImmutable(), ignored.ToImmutable());
    }

    /// <summary>ファイルを初めて取り込んだときの記録を覚える。2 度目からは覚え直さない。</summary>
    /// <param name="inclusion">記録。</param>
    private void NoteFirstInclusion(FirstInclusion inclusion)
    {
        if (_firstInclusions.TryAdd(inclusion.FilePath, inclusion))
        {
            Record(recording => recording.FirstInclusions.Add(inclusion));
        }
    }

    /// <summary>
    /// <c>#pragma once</c> かインクルードガードのために、2 度目の取り込みを飛ばしたことを覚える。
    /// </summary>
    /// <param name="filePath">飛ばしたファイル。</param>
    /// <remarks>
    /// <para>
    /// <b>1 度目を並べた分岐の中で取り込んでいると、ヘッダの宣言がその分岐の構成にしか無いことになる。</b>
    /// <c>#if _A</c> の中で取り込み、あとで条件無しにもう一度取り込むと、2 度目は飛ばすので、
    /// ヘッダの範囲には 1 度目の条件 (<c>_A</c>) しか付かない。<c>!_A</c> の構成で、ヘッダの構造体のメンバーが無いと報告していた (HL0313)。
    /// 展開の最後に、飛ばした取り込みの条件をヘッダの範囲に足す (<see cref="AddSkippedReincludeRanges"/>)。
    /// </para>
    /// </remarks>
    private void NoteSkippedReinclude(string filePath)
    {
        SymbolCondition condition = _options.SymbolConstraints.Apply(CurrentCondition);
        _skippedReincludes.Add(new(filePath, condition));
        Record(recording => recording.SkippedReincludes.Add(new(filePath, condition)));
    }

    /// <summary>
    /// 飛ばした 2 度目の取り込みの条件を、1 度目のヘッダの範囲に足す (<see cref="NoteSkippedReinclude"/>)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1 度目のヘッダの範囲を写し、1 度目の取り込みの条件を外して、2 度目の条件を掛ける。
    /// 1 度目の取り込みの中で初めて取り込んだファイル (<c>ShadingModels.ush</c> の中の <c>BRDF.ush</c>) の範囲も写す。
    /// 2 度目を飛ばすと、それらのファイルも取り込まれないからである。
    /// ヘッダの中の <c>#if</c> の条件は残る。写した範囲は元と同じ位置なので、条件は「または」で足される (<c>ConditionMapBuilder</c>)。
    /// </para>
    /// <para>
    /// 1 度目の条件が「かつ」で並べた形でないと外せない。その場合は足さない (今までどおり)。
    /// 2 度目の条件が 1 度目の条件に含まれるなら、足しても変わらないので足さない。
    /// </para>
    /// </remarks>
    private void AddSkippedReincludeRanges()
    {
        if (_skippedReincludes.Count == 0)
        {
            return;
        }

        List<ConditionalTokenRange> added = [];
        ILookup<string, string> children = _firstInclusions.Values.ToLookup(i => i.IncludingFilePath, i => i.FilePath, StringComparer.OrdinalIgnoreCase);

        foreach ((string filePath, SymbolCondition second) in _skippedReincludes)
        {
            if (!_firstInclusions.TryGetValue(filePath, out FirstInclusion inclusion))
            {
                continue;
            }

            SymbolCondition first = inclusion.Condition;

            if (first.IsAlways
                || first.IsUnknown
                || second.IsUnknown
                || !_options.SymbolConstraints.IsPossible(second.And(first.Negate()))
                || ForcedValues(first) is not { } forced)
            {
                continue;
            }

            HashSet<string> files = Descendants(filePath, children);

            foreach (ConditionalTokenRange range in _conditionalRegions)
            {
                if (range.IsHoisted
                    || range.Start < 0
                    || range.Start >= _output.Count
                    || _output[range.Start].Source.FilePath is not { } rangeFile
                    || !files.Contains(rangeFile))
                {
                    continue;
                }

                SymbolCondition inner = range.Condition.Assume(symbol => forced.TryGetValue(symbol, out bool value) ? value : null);

                if (!inner.IsNever && !inner.IsUnknown)
                {
                    added.Add(range with { Condition = _options.SymbolConstraints.Apply(inner.And(second)) });
                }
            }
        }

        _conditionalRegions.AddRange(added);
    }

    /// <summary>ファイルと、その 1 度目の取り込みの中で初めて取り込んだファイルを、たどれる限り集める。</summary>
    /// <param name="filePath">起点のファイル。</param>
    /// <param name="children">取り込んだ側のファイルから、初めて取り込んだファイル。</param>
    /// <returns>ファイル。</returns>
    private static HashSet<string> Descendants(string filePath, ILookup<string, string> children)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase) { filePath };
        Stack<string> pending = new([filePath]);

        while (pending.TryPop(out string? file))
        {
            foreach (string child in children[file])
            {
                if (files.Add(child))
                {
                    pending.Push(child);
                }
            }
        }

        return files;
    }

    /// <summary>「かつ」で並べた条件の、シンボルごとの値を求める。</summary>
    /// <param name="condition">条件。</param>
    /// <returns>シンボルから値。「かつ」で並べた形でなければ <see langword="null"/>。</returns>
    private static Dictionary<string, bool>? ForcedValues(SymbolCondition condition)
    {
        Dictionary<string, bool> forced = new(StringComparer.Ordinal);

        foreach (string symbol in condition.EnumerateSymbols())
        {
            bool canBeTrue = !condition.Assume(s => s == symbol ? true : null).IsNever;
            bool canBeFalse = !condition.Assume(s => s == symbol ? false : null).IsNever;

            if (canBeTrue == canBeFalse)
            {
                return null;
            }

            forced[symbol] = canBeTrue;
        }

        // 値を決めた条件が元の条件と同じなら、「かつ」で並べた形である。
        SymbolCondition rebuilt = SymbolCondition.Always;

        foreach ((string symbol, bool value) in forced)
        {
            rebuilt = rebuilt.And(SymbolCondition.Symbol(symbol, value));
        }

        return rebuilt == condition ? forced : null;
    }
}
