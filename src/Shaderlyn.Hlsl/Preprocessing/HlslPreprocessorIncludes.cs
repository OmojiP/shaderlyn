using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// #include。取り込み先の解決、循環の検出、展開結果の使い回し。
/// </summary>
internal sealed partial class HlslPreprocessor
{
    // --------------------------------------------------------------------
    // #include
    // --------------------------------------------------------------------

    /// <summary>
    /// <c>#include</c> を処理する。
    /// </summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    /// <remarks>
    /// パスの記法は <c>"..."</c> と <c>&lt;...&gt;</c> の 2 通りある。
    /// 山括弧は字句解析では比較演算子に分解されているため、
    /// トークンを結合して元のパスを復元する必要がある。
    /// </remarks>
    private void ProcessInclude(HlslSyntaxToken directiveToken)
    {
        ImmutableArray<HlslSyntaxToken> line = ReadDirectiveLine();

        string? path = ExtractIncludePath(line);
        if (path is null)
        {
            Report(HlslDescriptors.PreprocessorError, directiveToken.GetLocation(),
                "#include のパスを解釈できません。");
            return;
        }

        Location pathLocation = GetIncludePathLocation(directiveToken, line);

        if (_options.IncludeResolver is null)
        {
            AddUnresolvedInclude(path);
            _includes.Add(new IncludeReference(path, pathLocation, ResolvedFilePath: null));
            return;
        }

        string includingFilePath = directiveToken.Source.FilePath;

        if (!_options.IncludeResolver.TryResolve(path, includingFilePath, out SourceText? included))
        {
            AddUnresolvedInclude(path);
            _includes.Add(new IncludeReference(path, pathLocation, ResolvedFilePath: null));

            if (_options.ReportUnresolvedIncludes)
            {
                Report(HlslDescriptors.UnresolvedInclude, directiveToken.GetLocation(), path);
            }

            return;
        }

        _includes.Add(new IncludeReference(path, pathLocation, included.FilePath));

        if (_activeIncludePaths.Contains(included.FilePath))
        {
            Report(HlslDescriptors.CircularInclude, directiveToken.GetLocation(), path);
            return;
        }

        int includeDepth = _sources.Count(source => source.IncludePath is not null);
        if (includeDepth >= _options.MaxIncludeDepth)
        {
            Report(HlslDescriptors.PreprocessorError, directiveToken.GetLocation(),
                $"include の入れ子が深すぎます (上限 {_options.MaxIncludeDepth} 段)。");
            return;
        }

        AddResolvedInclude(included.FilePath);

        if (TryReuseExpansion(included))
        {
            return;
        }

        PushFileSource(included, included.FilePath);
        BeginRecording(included);
    }

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
        if (_conditionals.Count > 0)
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
        HlslSyntaxToken directiveToken,
        ImmutableArray<HlslSyntaxToken> line)
    {
        if (line.IsEmpty)
        {
            return directiveToken.GetLocation();
        }

        HlslSyntaxToken first = line[0];

        if (first.Kind == HlslSyntaxKind.StringLiteralToken)
        {
            return first.GetLocation();
        }

        // 山括弧の閉じが無い場合も、開いた位置までは指せる。
        HlslSyntaxToken? last = line.LastOrDefault(t => t.Kind == HlslSyntaxKind.GreaterThanToken);

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
    private static string? ExtractIncludePath(ImmutableArray<HlslSyntaxToken> line)
    {
        if (line.IsEmpty)
        {
            return null;
        }

        if (line[0].Kind == HlslSyntaxKind.StringLiteralToken)
        {
            return line[0].ValueText;
        }

        // 山括弧は < と > に分解されているため、間のトークンのテキストを連結して復元する。
        if (line[0].Kind == HlslSyntaxKind.LessThanToken)
        {
            System.Text.StringBuilder builder = new();

            for (int i = 1; i < line.Length; i++)
            {
                if (line[i].Kind == HlslSyntaxKind.GreaterThanToken)
                {
                    return builder.Length > 0 ? builder.ToString() : null;
                }

                builder.Append(line[i].Text);
            }
        }

        return null;
    }
}
