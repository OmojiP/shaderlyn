using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Profiles;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Semantics;

/// <summary>
/// 入力からセマンティックモデルを組み立てる。
/// </summary>
/// <remarks>
/// <para>
/// <b>組み立てと、組み立て終えたものへの問い合わせを分ける。</b>
/// <see cref="ShaderCompilation"/> はルールが使う型で、
/// 「このシェーダーについて何が分かっているか」に答えるのが仕事である。
/// 一方ここにあるのは、展開・構成ごとの解析・宣言の集約といった、
/// 1 度だけ行う手順である。同じ型に置くと、公開されている面が実際より広く見える。
/// </para>
/// <para>
/// <c>.shader</c> と HLSL 単体ファイルで違うのは、
/// <see cref="Build"/> へ渡すブロックの取り出し方だけである。
/// 以降の道が分かれていないので、片方だけ検査が抜けることがない。
/// </para>
/// </remarks>
internal static class ShaderCompilationBuilder
{
    /// <summary>
    /// 取り出したコードブロックからセマンティックモデルを組み立てる。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="shaderLabTree">ShaderLab の構文木。</param>
    /// <param name="extractions">解析するコードブロック。</param>
    /// <param name="codeText">利用者が書いた HLSL だけを残したテキスト。</param>
    /// <param name="options">実行時設定。</param>
    /// <param name="isStandaloneHlsl">HLSL 単体ファイルとして解析しているかどうか。</param>
    /// <returns>構築されたセマンティックモデル。</returns>
    /// <remarks>
    /// <c>.shader</c> と HLSL 単体ファイルで違うのは、
    /// <b>ここへ渡すブロックの取り出し方だけ</b>である。
    /// 以降の道が分かれていないので、片方だけ検査が抜けることがない。
    /// </remarks>
    internal static ShaderCompilation Build(
        SourceText text,
        ShaderLabSyntaxTree shaderLabTree,
        ImmutableArray<ProgramBlockView> extractions,
        SourceText codeText,
        SemanticsOptions? options,
        bool isStandaloneHlsl = false)
    {
        options ??= SemanticsOptions.Default;

        ImmutableArray<PropertySymbol> properties = CollectProperties(shaderLabTree, text);
        HashSet<string> shaderLabReferences = CollectShaderLabPropertyReferences(shaderLabTree);

        PreprocessorOptions preprocessorOptions = new()
        {
            IncludeResolver = options.IncludeResolver,
            PredefinedMacros = options.PredefinedMacros,
            Prelude = options.UseFallbackMacros ? UnityShaderStubs.Prelude : null,
            TokenCache = options.TokenCache,
            IncludeCache = options.IncludeCache,
            IsUserInclude = options.IsUserInclude,
        };

        ImmutableArray<AnalyzedProgram>.Builder programs = ImmutableArray.CreateBuilder<AnalyzedProgram>();
        ImmutableArray<AnalyzedProgram>.Builder variants = ImmutableArray.CreateBuilder<AnalyzedProgram>();
        SortedSet<string> unexploredSymbols = new(StringComparer.Ordinal);
        SortedDictionary<string, SymbolCombination> unexploredCombinations = new(StringComparer.Ordinal);

        DeclarationIndex index = new(properties);

        foreach (ProgramBlockView extraction in extractions)
        {
            // どの #ifdef で両方の分岐を残すかは、展開しながら決める。
            // そのため「どれがシンボルか」は展開が始まる前に分かっていなければならない。
            // 指定があればそれを使い、無ければこのブロック自身の #pragma から拾う。
            // 宣言されたシンボルは、並べるかどうかと関係なく、組の記録にも使う。
            ImmutableHashSet<string> blockSymbols =
                CollectBlockSymbols(extraction.MaskedText, options, out SymbolConstraints constraints, out string? shaderTarget, out string? surfaceLighting);

            // どの構成も、このブロックのマクロ (#pragma target と #pragma kernel の指定) を重ねて展開する。
            // 落とすと、そのカーネルでは決して通らない分岐を読み、誤りとして報告する。
            // カーネルの指定のほうが具体的なので、同じ名前ならそちらが勝つ。
            ImmutableDictionary<string, string>? blockMacros = extraction.ExtraMacros;

            if (shaderTarget is not null)
            {
                blockMacros = ImmutableDictionary<string, string>.Empty
                    .Add("SHADER_TARGET", shaderTarget)
                    .SetItems(blockMacros ?? ImmutableDictionary<string, string>.Empty);
            }

            PreprocessorOptions baseOptions = WithExtraMacros(preprocessorOptions, blockMacros);

            if (extraction.Block?.Delimiter?.StartKeyword == "CGPROGRAM")
            {
                baseOptions = WithCgAutoIncludes(baseOptions, text.FilePath, surfaceLighting);
            }

            // このブロック自身が宣言したシンボル。ヘッダが宣言したものと区別するために覚えておく。
            ImmutableHashSet<string> ownSymbols = blockSymbols;
            ImmutableHashSet<string> includedSymbols = [];

            PreprocessorOptions blockOptions = BlockOptions(baseOptions, blockSymbols, constraints, options, []);

            HlslSyntaxTree tree = ParseWithoutMergedMacroConflicts(
                extraction.MaskedText, blockOptions, options.SyntaxTimings, out ImmutableArray<string> macroConflicts);

            // 取り込んだヘッダの #pragma は、取り込んでみるまで分からない。
            // ヘッダが宣言していたなら、その分も並べる対象にして展開し直す。
            if (TryAddIncludedSymbols(tree, ref blockSymbols, ref constraints))
            {
                includedSymbols = blockSymbols.Except(ownSymbols);
                blockOptions = BlockOptions(baseOptions, blockSymbols, constraints, options, includedSymbols);

                tree = ParseWithoutMergedMacroConflicts(
                    extraction.MaskedText, blockOptions, options.SyntaxTimings, out macroConflicts);
            }

            DeclarationSet declarations = UniformCollector.Collect(tree.Root);

            string? passName = FindPassName(extraction.Pass);
            AnalyzedProgram program = new(extraction, tree, declarations, passName)
            {
                MacroConflictSymbols = macroConflicts,
            };

            programs.Add(program);
            index.Absorb(tree, declarations, program.BlockKey);

            // シンボルは C# からも切り替えられる。
            // 有効になっていない分岐のコードも「いつか通る」コードなので、
            // このファイル自身が条件で見ているシンボルは 1 つずつ有効にして展開し直す。
            HashSet<string> defaults = DefaultSymbols(baseOptions.PredefinedMacros, constraints);
            ImmutableArray<ImmutableArray<string>> candidates =
                SelectVariantSymbols(tree, text, defaults, includedSymbols, macroConflicts);

            PreprocessorOptions variantOptions = blockOptions with
            {
                PredefinedMacros = baseOptions.PredefinedMacros,

                // 宣言したキーワードは既定の構成と同じにしておく。条件付きで覚えるマクロの条件はこれで書く
                // (#if !defined(_B) の中の #define WNB を、#ifdef WNB で「!_B のとき」と読む)。
                // 空にすると、バリアントだけがその分岐を並べず、関係の無いキーワードの否定が条件に付く。

                // 既定の構成で、並べた分岐のマクロがコードで使われたために外したシンボルは、ここでも並べない。
                // 並べると、そのマクロの定義が分岐の数だけ実行され、最後の定義がどの構成でも効いてしまう
                // (ヘッダの #if !defined(ENABLE_ALPHA) で書き分けた CTYPE が float4 になる)。
                BothBranchSymbols = blockOptions.BothBranchSymbols.Except(macroConflicts),

                // 既定の構成が並べた領域だけを並べる。判断が食い違うと、
                // 既定の木との違いが「そのシンボルで変わる部分」だけではなくなる。
                MergeOnlyRegions = new MergedRegionSet(tree.PreprocessResult.MergedRegions),
            };

            ExpansionPlan plan = PlanExpansions(candidates, tree, constraints, options);

            List<(SymbolExpansion Expansion, ConditionalMergeResult? Packed)> accepted = [];
            List<string> unpacked = [];
            int failedPacks = 0;
            KeywordRegionIndex baselineRegions = new(tree.PreprocessResult.KeywordRegions);

            SymbolExpansion[] expanded = ExpandSymbols(
                extraction.MaskedText, plan.Configurations, variantOptions, constraints, options.SyntaxTimings);

            for (int i = 0; i < expanded.Length; i++)
            {
                if (!plan.IsPacked[i])
                {
                    accepted.Add((expanded[i], null));
                    continue;
                }

                // まとめた構成は、違いのそれぞれがどのキーワードのものか言えたときだけ使う。
                if (VerifyPacked(tree, expanded[i], baselineRegions, constraints, text.FilePath) is { } packed)
                {
                    accepted.Add((expanded[i], packed));
                }
                else
                {
                    failedPacks++;
                    unpacked.AddRange(expanded[i].EnabledSymbols);
                }
            }

            // 使えなかったまとめは、1 つずつ展開し直す。まとめに使った回数は上限に数えない。
            int budget = Math.Max(0, options.MaxSymbolVariants) - (plan.Configurations.Length - failedPacks);
            ImmutableArray<ImmutableArray<string>> retried = [.. unpacked.Take(Math.Max(0, budget)).Select(s => ImmutableArray.Create(s))];

            foreach (SymbolExpansion single in ExpandSymbols(
                         extraction.MaskedText, retried, variantOptions, constraints, options.SyntaxTimings))
            {
                accepted.Add((single, null));
            }

            // 組は組のまま覚える。組の中のシンボルは、1 つずつなら調べていることがある。
            // 位置は展開の記録から引く。どこが読まれていないのかを指すのに要る。
            foreach (ImmutableArray<string> skipped in plan.Unexplored.Concat(unpacked.Skip(retried.Length).Select(s => ImmutableArray.Create(s))))
            {
                if (skipped.Length == 1)
                {
                    unexploredSymbols.Add(skipped[0]);
                }
                else
                {
                    unexploredCombinations.TryAdd(
                        string.Join(',', skipped),
                        tree.PreprocessResult.RequiredSymbolCombinations.First(
                            c => WithoutDefaults(c.Symbols, defaults).SequenceEqual(skipped)));
                }
            }

            foreach (((ImmutableArray<string> enabled, HlslSyntaxTree variantTree, DeclarationSet variantDeclarations), ConditionalMergeResult? packedMerge)
                     in accepted)
            {
                index.Absorb(variantTree, variantDeclarations, program.BlockKey);

                variants.Add(new AnalyzedProgram(extraction, variantTree, variantDeclarations, passName)
                {
                    EnabledSymbols = enabled,
                    PackedMerge = packedMerge,
                });
            }
        }

        index.Complete(text.FilePath, options.FixedSymbolConfiguration);

        ImmutableArray<HlslSyntaxToken> codeTokens = new HlslLexer(codeText).Lex(out _);

        return new ShaderCompilation(
            text,
            shaderLabTree,
            options.Profile,
            properties,
            programs.ToImmutable(),
            index.UniformsByName,
            index.Occurrences,
            index.DeclarationCounts,
            index.InactiveIdentifiers,
            index.UnanalyzedIdentifiers,
            shaderLabReferences,
            [.. index.IncludePaths],
            [.. index.UnresolvedIncludes],
            codeText,
            codeTokens,
            variants.ToImmutable(),
            [.. unexploredSymbols],
            [.. unexploredCombinations.Values])
        {
            IsStandaloneHlsl = isStandaloneHlsl,
            IsUserInclude = options.IsUserInclude,
            InactiveIdentifiersByBlock = index.InactiveIdentifiersByBlock.ToFrozenDictionary(
                pair => pair.Key,
                pair => pair.Value.ToFrozenSet(StringComparer.Ordinal)),
        };
    }

    /// <summary>
    /// 解析したコードブロックから集めた索引。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>1 つの <c>.shader</c> の中身は、何度も別々に解析される。</b>
    /// Pass ごとに共通コード片を差し込んで展開し直し、
    /// さらにシンボル構成ごとに展開し直す。
    /// どの解析結果からも同じ索引へ積み上げる必要がある。
    /// </para>
    /// <para>
    /// 積み上げ先をまとめて 1 つの型にしておかないと、
    /// 構築の手続きが 7 つの入れ物を抱えたまま長くなり、
    /// どれをどこで更新しているのかが読めなくなる。
    /// </para>
    /// </remarks>
    private sealed class DeclarationIndex
    {
        private readonly SearchValues<char> _propertyInitials;

        /// <summary>索引を用意する。</summary>
        /// <param name="properties"><c>Properties</c> に宣言されたプロパティ。</param>
        public DeclarationIndex(ImmutableArray<PropertySymbol> properties)
        {
            // プロパティ名の先頭文字だけを集めておき、トークン走査の足切りに使う。
            // URP のヘッダ群を展開すると 1 ブロックあたり数十万トークンになるため、
            // 全トークンに対して辞書を引くと解析時間がここに吸われる。
            _propertyInitials = SearchValues.Create(
                [.. properties.Where(p => p.Name.Length > 0).Select(p => p.Name[0]).Distinct()]);
        }

        /// <summary>名前ごとの uniform 宣言。</summary>
        public Dictionary<string, List<UniformSymbol>> UniformsByName { get; } = new(StringComparer.Ordinal);

        /// <summary>プロパティ名が展開後のコードに現れた回数。</summary>
        public Dictionary<string, int> Occurrences { get; } = new(StringComparer.Ordinal);

        /// <summary>名前ごとの宣言の数。</summary>
        public Dictionary<string, int> DeclarationCounts { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// 解析したコードに現れない場所に現れた識別子。「見つからない」を根拠にする検査を見送る根拠になる。
        /// </summary>
        /// <remarks>
        /// <see cref="UnanalyzedIdentifiers"/> に、呼び出されなかったマクロの本体に現れた名前を足したもの。
        /// <see cref="Complete"/> を呼ぶまでは揃っていない。
        /// </remarks>
        public HashSet<string> InactiveIdentifiers => _all.Inactive;

        /// <summary>どの構成でも解析されなかった非活性領域に現れた識別子。</summary>
        /// <remarks><see cref="Complete"/> を呼ぶまでは揃っていない。</remarks>
        public HashSet<string> UnanalyzedIdentifiers => _all.Unanalyzed;

        /// <summary>
        /// コードブロックごとの、解析したコードに現れない場所に現れた識別子 (<see cref="InactiveIdentifiers"/> のブロック版)。
        /// </summary>
        /// <remarks>
        /// 別々にコンパイルされるブロック (Pass、カーネル) の読み飛ばしを混ぜないためにある。
        /// ある Pass の非活性領域に名前があっても、別の Pass の構成でその名前が現れることにはならない。
        /// <see cref="Complete"/> を呼ぶまでは揃っていない。
        /// </remarks>
        public Dictionary<(int Start, string? Kernel), HashSet<string>> InactiveIdentifiersByBlock { get; } = [];

        /// <summary>シェーダー全体の読み飛ばし。</summary>
        private readonly SkippedNames _all = new();

        /// <summary>コードブロックごとの読み飛ばし。</summary>
        private readonly Dictionary<(int Start, string? Kernel), SkippedNames> _byBlock = [];

        /// <summary>現れた <c>#include</c> のパス。解決の成否は問わない。</summary>
        public SortedSet<string> IncludePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>解決できなかった <c>#include</c> のパス。</summary>
        public SortedSet<string> UnresolvedIncludes { get; } = new(StringComparer.Ordinal);

        /// <summary>解析結果 1 つ分を索引へ取り込む。</summary>
        /// <param name="tree">解析して得た構文木。</param>
        /// <param name="declarations">その構成で見つかった宣言。</param>
        /// <param name="block">その木のコードブロックの鍵 (<see cref="AnalyzedProgram.BlockKey"/>)。</param>
        public void Absorb(HlslSyntaxTree tree, DeclarationSet declarations, (int Start, string? Kernel) block)
        {
            foreach (UniformSymbol uniform in declarations.Uniforms)
            {
                // 同名の uniform が複数のブロックに現れるのは普通である
                // (共通コード片が各 Pass へ差し込まれるため)。すべて保持する。
                if (!UniformsByName.TryGetValue(uniform.Name, out List<UniformSymbol>? group))
                {
                    group = UniformsByName[uniform.Name] = [];
                }

                group.Add(uniform);
                DeclarationCounts[uniform.Name] = DeclarationCounts.GetValueOrDefault(uniform.Name) + 1;
            }

            CountOccurrences(tree.PreprocessResult.Tokens, _propertyInitials, Occurrences);

            _all.Add(tree);

            if (!_byBlock.TryGetValue(block, out SkippedNames? forBlock))
            {
                forBlock = new SkippedNames();
                _byBlock[block] = forBlock;
            }

            forBlock.Add(tree);

            IncludePaths.UnionWith(tree.PreprocessResult.ResolvedIncludes);
            IncludePaths.UnionWith(tree.PreprocessResult.UnresolvedIncludes);
            UnresolvedIncludes.UnionWith(tree.PreprocessResult.UnresolvedIncludes);
        }

        /// <summary>
        /// すべての構成を取り込んだあとで、どの構成でも解析されなかった場所の名前を確定する。
        /// </summary>
        /// <param name="filePath">解析しているファイルのパス。</param>
        /// <param name="fixedConfiguration">
        /// シンボルの構成を 1 つに固定した解析かどうか (<see cref="SemanticsOptions.FixedSymbolConfiguration"/>)。
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>ある構成で読み飛ばした場所でも、別の構成が解析していれば「見えていない」ではない。</b>
        /// <c>#ifdef _A</c> の中は、<c>_A</c> を有効にした構成の木に載っている。
        /// そこにある宣言や参照は、その構成の索引に入っている。
        /// 名前だけで「どこかで読み飛ばされた名前」を見送りの根拠にすると、
        /// その構成で実際に使われていない uniform まで「使われているかもしれない」ことになる。
        /// </para>
        /// <para>
        /// 別の構成の展開結果に同じ位置のトークンがあれば、その構成が解析している。
        /// マクロ展開で生まれたトークンは呼び出し位置を持つので数えない。
        /// ファイルはパスで見分ける。コードブロックごとに別のマスクしたテキストを解析するが、
        /// どれも元のファイルと同じ長さ・同じ位置を持つ。
        /// </para>
        /// </remarks>
        public void Complete(string filePath, bool fixedConfiguration)
        {
            _all.Complete(filePath, fixedConfiguration);

            foreach (((int Start, string? Kernel) block, SkippedNames names) in _byBlock)
            {
                names.Complete(filePath, fixedConfiguration);
                InactiveIdentifiersByBlock[block] = names.Inactive;
            }
        }
    }

    /// <summary>
    /// 解析した木のどれにも現れず、読み飛ばした場所やマクロの本体にだけ現れた名前を集める。
    /// </summary>
    /// <remarks>
    /// シェーダー全体の分と、コードブロックごとの分を同じ手順で作る (<see cref="DeclarationIndex"/>)。
    /// </remarks>
    private sealed class SkippedNames
    {
        /// <summary>取り込んだ解析結果。読み飛ばした位置を、別の構成が解析したかを確かめるのに使う。</summary>
        private readonly List<HlslSyntaxTree> _trees = [];

        /// <summary>解析しているファイル自身の非活性領域に現れた識別子。開始位置から名前を引く。</summary>
        private readonly Dictionary<int, string> _skippedRootIdentifiers = [];

        /// <summary>どの構成でも解析されなかった非活性領域に現れた識別子。</summary>
        public HashSet<string> Unanalyzed { get; } = new(StringComparer.Ordinal);

        /// <summary><see cref="Unanalyzed"/> に、呼び出されなかったマクロの本体に現れた名前を足したもの。</summary>
        public HashSet<string> Inactive { get; } = new(StringComparer.Ordinal);

        /// <summary>解析結果 1 つ分を取り込む。</summary>
        /// <param name="tree">解析して得た構文木。</param>
        public void Add(HlslSyntaxTree tree)
        {
            _trees.Add(tree);

            // 取り込んだファイルの読み飛ばしは、取り込み結果の使い回しが名前しか持たないので、名前のまま数える。
            // 解析しているファイル自身のものは位置で持ち、Complete で別の構成が解析したかを確かめる。
            PreprocessResult result = tree.PreprocessResult;

            if (!result.SkippedIncludedIdentifiers.IsDefaultOrEmpty)
            {
                Unanalyzed.UnionWith(result.SkippedIncludedIdentifiers);
            }

            if (!result.SkippedRootIdentifiers.IsDefaultOrEmpty)
            {
                foreach (HlslSyntaxToken token in result.SkippedRootIdentifiers)
                {
                    _skippedRootIdentifiers[token.Span.Start] = token.Text;
                }
            }

            CollectMacroBodyIdentifiers(result.Macros, Inactive);
        }

        /// <summary>
        /// すべての構成を取り込んだあとで、どの構成でも解析されなかった場所の名前を確定する。
        /// </summary>
        /// <param name="filePath">解析しているファイルのパス。</param>
        /// <param name="fixedConfiguration">シンボルの構成を 1 つに固定した解析かどうか。</param>
        public void Complete(string filePath, bool fixedConfiguration)
        {
            if (fixedConfiguration)
            {
                ForgetSymbolExcludedRegions(filePath);
            }

            if (_skippedRootIdentifiers.Count > 0)
            {
                foreach (HlslSyntaxTree tree in _trees)
                {
                    // その構成で処理した #define の名前も、解析された位置である。
                    // #ifdef _B の中の #define EXTRA 1 は、_B の木で定義されている。
                    // 名前はコードのトークンとしては現れないので、これを数えないと
                    // 「どの構成でも解析しなかった領域の名前」として、どこで使っても宣言済みになる。
                    foreach (MacroDefinition macro in tree.PreprocessResult.Macros.Values)
                    {
                        if (!macro.NameToken.IsFromMacroExpansion
                            && string.Equals(macro.NameToken.Source.FilePath, filePath, StringComparison.Ordinal))
                        {
                            _skippedRootIdentifiers.Remove(macro.NameToken.Span.Start);
                        }
                    }

                    foreach (HlslSyntaxToken token in tree.PreprocessResult.Tokens)
                    {
                        // このファイルが書いたマクロの本体から来たトークンも、
                        // 本体の位置が解析されたことを表す。展開されて初めて中身が検査される。
                        if (token.MacroDefinitionSpan is { } definition)
                        {
                            // 位置はこのファイルの中のものだけを鍵にしている。ヘッダのマクロの本体の位置は別のファイルを指す。
                            if (string.Equals(token.MacroDefinitionSource?.FilePath, filePath, StringComparison.Ordinal))
                            {
                                _skippedRootIdentifiers.Remove(definition.Start);
                            }

                            continue;
                        }

                        if (!token.IsFromMacroExpansion
                            && string.Equals(token.Source.FilePath, filePath, StringComparison.Ordinal))
                        {
                            _skippedRootIdentifiers.Remove(token.Span.Start);
                        }
                    }

                    if (_skippedRootIdentifiers.Count == 0)
                    {
                        break;
                    }
                }

                Unanalyzed.UnionWith(_skippedRootIdentifiers.Values);
            }

            Inactive.UnionWith(Unanalyzed);
        }

        /// <summary>
        /// シンボルだけで外れた分岐の名前を、見えていない名前から外す。
        /// </summary>
        /// <param name="filePath">解析しているファイルのパス。</param>
        /// <remarks>
        /// 構成を固定した解析では、その分岐はこの構成でコンパイルされないコードである。
        /// 外れた範囲は、条件に書かれた名前がすべて宣言されたシンボルであるものに限る。
        /// 環境の条件 (<c>SHADER_API_*</c>) が混ざる範囲は、構成を固定しても値が分からない。
        /// </remarks>
        private void ForgetSymbolExcludedRegions(string filePath)
        {
            foreach (HlslSyntaxTree tree in _trees)
            {
                PreprocessResult result = tree.PreprocessResult;

                if (result.InactiveRegions.IsDefaultOrEmpty)
                {
                    continue;
                }

                HashSet<string> declared = ShaderSymbols.CollectDeclared(result.Pragmas);

                foreach (InactiveRegion region in result.InactiveRegions)
                {
                    if (region.ConditionSymbols.IsDefaultOrEmpty
                        || !region.ConditionSymbols.All(declared.Contains)
                        || !string.Equals(region.Location.FilePath, filePath, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    TextSpan span = region.Location.Span;

                    foreach (int start in _skippedRootIdentifiers.Keys.Where(span.Contains).ToList())
                    {
                        _skippedRootIdentifiers.Remove(start);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 追加で展開するシンボルを選ぶ。
    /// </summary>
    /// <param name="tree">既定の構成での解析結果。</param>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="defaults">既定の構成で定義するシンボル。1 つだけ有効にした構成は既定の構成と同じなので作らない。</param>
    /// <param name="includedSymbols">取り込んだヘッダ自身が宣言したシンボル。</param>
    /// <param name="macroConflicts">
    /// 並べた分岐のマクロが使われたために、並べる対象から外して展開し直したシンボル。
    /// 並べる対象でないシンボルの条件は、並べられなかったとも記録されないので、ここから拾う。
    /// </param>
    /// <returns>展開するシンボル。優先する順に並べる。上限は <see cref="PlanExpansions"/> が当てる。</returns>
    /// <remarks>
    /// <para>
    /// <b>そのファイル自身が条件で見ているシンボルだけを対象にする。</b>
    /// Unity 同梱の URP シェーダーは 40 を超えるシンボルを宣言することがあり、
    /// すべてを展開すると現実的な時間で終わらない。
    /// 一方、そのファイルの条件が見ていないシンボルを有効にしても、
    /// そのファイルのコードは 1 行も変わらない。
    /// </para>
    /// <para>
    /// 宣言されていないシンボルは対象にしない。
    /// 宣言が無ければ切り替えようがなく、その分岐は決して通らない
    /// (それ自体は HL0330 が報告する)。
    /// </para>
    /// <para>
    /// 並び順を名前で固定するのは、上限に達したときにどれが選ばれるかを
    /// 実行のたびに変えないためである。
    /// </para>
    /// <para>
    /// <b>ヘッダでしか条件に現れないキーワードも、そこで並べられなければ構成を作る。</b>
    /// <c>.shader</c> で宣言して共通の <c>.hlsl</c> の機能だけを切り替える運用では、
    /// 解析しているファイルに条件が 1 つも無い。上限に達したときに落ちるのは、これらからにする。
    /// </para>
    /// <para>
    /// <b>条件に書かれた論理積は、そのまま 1 つの構成として作る。</b>
    /// <c>#if defined(_A) &amp;&amp; defined(_B)</c> の中は、どちらか一方だけの構成には現れない。
    /// 組み合わせをすべて作れば構成は 2ⁿ になるが、書かれている組だけなら書いた数で収まる。
    /// </para>
    /// </remarks>
    private static ImmutableArray<ImmutableArray<string>> SelectVariantSymbols(
        HlslSyntaxTree tree,
        SourceText text,
        HashSet<string> defaults,
        ImmutableHashSet<string> includedSymbols,
        ImmutableArray<string> macroConflicts)
    {
        PreprocessResult result = tree.PreprocessResult;

        HashSet<string> declared = ShaderSymbols.CollectDeclared(result.Pragmas);

        // 両方の分岐を 1 つの木に残せたシンボルは、バリアントとして展開し直す必要が無い。
        // 残せたかどうかは展開してみないと分からないので、展開の結果を見て判断する。
        HashSet<string> alreadyMerged = CollectMergedSymbols(result);

        SortedSet<string> singles = new(StringComparer.Ordinal);
        SortedSet<string> fromIncluded = new(StringComparer.Ordinal);

        foreach (HlslSyntaxToken token in result.ConditionalIdentifiers)
        {
            if (!declared.Contains(token.Text)
                || alreadyMerged.Contains(token.Text)
                || defaults.Contains(token.Text)
                || token.IsFromMacroExpansion)
            {
                continue;
            }

            // このファイルが自分で書いた条件を先に置く。
            // ヘッダの条件は、そのヘッダ自身が宣言したシンボルのものだけを、後ろに置く。
            // このファイルが宣言したシンボルの条件はヘッダ側に大量にあり、
            // 全部を候補にすると 1 ブロックで数十件になる (PreprocessorOptions.IncludedDeclaredSymbols)。
            if (string.Equals(token.Source.FilePath, text.FilePath, StringComparison.Ordinal))
            {
                singles.Add(token.Text);
            }
            else if (includedSymbols.Contains(token.Text))
            {
                fromIncluded.Add(token.Text);
            }
        }

        fromIncluded.ExceptWith(singles);

        // 1 つずつの構成を先に置く。上限に達したときに落ちるのは組のほうになる。
        // 組は、その中のどれか 1 つを有効にした構成では通らない領域のためにある。
        List<ImmutableArray<string>> candidates = [.. singles.Select(s => ImmutableArray.Create(s))];

        candidates.AddRange(SelectVariantCombinations(
            result,
            declared,
            alreadyMerged,
            defaults,
            [.. singles, .. fromIncluded],
            ShaderSymbols.CollectConstraints(result.Pragmas),
            text.FilePath));

        candidates.AddRange(fromIncluded.Select(s => ImmutableArray.Create(s)));

        // このファイルが宣言したキーワードでも、条件がヘッダにしか無いことがある。
        // .shader で宣言して、共通の .hlsl の機能だけを切り替える書き方である。
        // ヘッダでその分岐を並べられなかったなら、有効にした構成を作らないと、その側はどの木にも載らない。
        // 並べ直しで外したシンボルも同じである。並べる対象でなくなったので、ヘッダの条件は並べられなかったとも記録されない。
        SortedSet<string> declinedInHeaders = new(
            result.DeclinedBothBranchSymbols
                .Concat(macroConflicts.Where(s => result.ConditionalIdentifiers.Any(t => t.Text == s)))
                .Where(s => declared.Contains(s) && !defaults.Contains(s)),
            StringComparer.Ordinal);

        declinedInHeaders.ExceptWith(singles);
        declinedInHeaders.ExceptWith(fromIncluded);
        candidates.AddRange(declinedInHeaders.Select(s => ImmutableArray.Create(s)));

        return [.. candidates];
    }

    /// <summary>展開する構成の計画。</summary>
    /// <param name="Configurations">展開する構成。1 件が 1 回の展開で、同時に有効にするシンボルを並べる。</param>
    /// <param name="IsPacked">
    /// その構成が、互いに関係しないキーワードをまとめたものかどうか。<see cref="Configurations"/> と同じ順に並ぶ。
    /// </param>
    /// <param name="Unexplored">上限に収まらなかった候補。</param>
    private sealed record ExpansionPlan(
        ImmutableArray<ImmutableArray<string>> Configurations,
        ImmutableArray<bool> IsPacked,
        ImmutableArray<ImmutableArray<string>> Unexplored);

    /// <summary>1 つのまとめに入れるキーワードの上限。</summary>
    /// <remarks>
    /// 1 つでも違いのキーワードを決められなければ、まとめ全体を 1 つずつ展開し直す。
    /// 大きくしすぎると、1 つの誤算で展開し直す数が増える。
    /// </remarks>
    private const int MaxPackSize = 8;

    /// <summary>
    /// 候補を、上限までの展開の回数に割り当てる。
    /// </summary>
    /// <param name="candidates">候補。優先する順に並ぶ。</param>
    /// <param name="tree">既定の構成の木。</param>
    /// <param name="constraints">宣言から分かる構成の制約。</param>
    /// <param name="options">実行時設定。</param>
    /// <returns>展開の計画。</returns>
    /// <remarks>
    /// <para>
    /// <b>互いに関係しないキーワードは、1 回の展開にまとめる。</b>
    /// まとめたキーワードは、1 つずつ展開したときとは違う文脈 (ほかのキーワードも有効) で解析されるが、
    /// どの組み合わせも実在する構成である。違いがどのキーワードのものかは、展開のあとで確かめる
    /// (<see cref="VerifyPacked"/>)。
    /// </para>
    /// <para>
    /// まとめるのは 1 つずつの候補のうち、分岐の中でマクロを定義・削除せず、ヘッダも取り込まないものだけである
    /// (<see cref="PreprocessResult.MacroAffectingSymbols"/>)。そうしたキーワードは、有効にすると
    /// 連なりの外の展開まで変わるので、違いがどのキーワードのものかを連なりから言えない。
    /// </para>
    /// <para>
    /// 同じまとめに入れないもの:
    /// 同時には有効にならないもの (同じ宣言の行)、条件に論理積で書かれた組 (その組の構成を別に作る)。
    /// </para>
    /// <para>
    /// 既にあるまとめに入るなら、上限を越えた候補でもまとめに入れる。展開の回数は増えない。
    /// </para>
    /// </remarks>
    private static ExpansionPlan PlanExpansions(
        ImmutableArray<ImmutableArray<string>> candidates,
        HlslSyntaxTree tree,
        SymbolConstraints constraints,
        SemanticsOptions options)
    {
        PreprocessResult result = tree.PreprocessResult;
        int limit = Math.Max(0, options.MaxSymbolVariants);

        HashSet<string> affecting = result.MacroAffectingSymbols.IsDefault
            ? []
            : [.. result.MacroAffectingSymbols];

        ImmutableArray<ImmutableArray<string>> combinations = result.RequiredSymbolCombinations.IsDefault
            ? []
            : [.. result.RequiredSymbolCombinations.Select(c => c.Symbols)];

        // 1 つの連なりが一緒に見ているキーワードは、同じまとめに入れない。その連なりの中の違いは、どちらのものとも言えない
        // (HDRP の ATTRIBUTES_NEED_TEXCOORD1 は _ALPHATEST_ON と _DEPTHOFFSET_ON の両方で定義が変わる)。
        HashSet<(string, string)> together = [];

        if (!result.KeywordRegions.IsDefaultOrEmpty)
        {
            foreach (KeywordRegion region in result.KeywordRegions)
            {
                for (int i = 0; i < region.Symbols.Length; i++)
                {
                    for (int j = i + 1; j < region.Symbols.Length; j++)
                    {
                        together.Add(OrderedPair(region.Symbols[i], region.Symbols[j]));
                    }
                }
            }

            // 同じトップレベルの宣言の中に連なりがあるキーワードも同じまとめに入れない。
            // 展開してから確かめるが (AreSeparated)、先に分けておけば捨てるまとめが減る。
            foreach (HashSet<string> symbols in GroupRegionSymbolsByDeclaration(tree.Root, result.KeywordRegions))
            {
                foreach (string a in symbols)
                {
                    foreach (string b in symbols)
                    {
                        if (string.CompareOrdinal(a, b) < 0)
                        {
                            together.Add((a, b));
                        }
                    }
                }
            }
        }

        bool Compatible(string a, string b)
            => !constraints.AreExclusive(a, b)
               && !together.Contains(OrderedPair(a, b))
               && !combinations.Any(c => c.Contains(a) && c.Contains(b));

        // まずまとめられるキーワードをまとめる。まとめは 1 回の展開で何個ものキーワードを調べるので、先に割り当てる。
        List<List<string>> packs = [];
        List<ImmutableArray<string>> others = [];

        foreach (ImmutableArray<string> candidate in candidates)
        {
            // どれか 1 つが必ず有効な行のキーワードは、有効にすると先頭のキーワードが外れる。
            // その違いは、有効にしたキーワードの連なりの外に出る。
            bool canPack = options.PackIndependentSymbols
                           && candidate.Length == 1
                           && !affecting.Contains(candidate[0])
                           && !constraints.RequiredGroups.Any(group => group.Contains(candidate[0]));

            if (!canPack)
            {
                others.Add(candidate);
                continue;
            }

            List<string>? pack = packs.FirstOrDefault(
                p => p.Count < MaxPackSize && p.All(member => Compatible(member, candidate[0])));

            if (pack is null)
            {
                packs.Add([candidate[0]]);
            }
            else
            {
                pack.Add(candidate[0]);
            }
        }

        // 1 つしか入らなかったまとめは、ふつうの 1 つずつの構成に戻す。優先の順は元の候補の順に揃える。
        HashSet<string> alone = [.. packs.Where(p => p.Count == 1).Select(p => p[0])];
        List<ImmutableArray<string>> ordered =
        [
            .. packs.Where(p => p.Count > 1).Select(p => p.ToImmutableArray()),
            .. candidates.Where(c => others.Contains(c) || (c.Length == 1 && alone.Contains(c[0]))),
        ];

        int taken = Math.Min(limit, ordered.Count);

        return new ExpansionPlan(
            [.. ordered.Take(taken)],
            [.. ordered.Take(taken).Select((c, i) => i < packs.Count(p => p.Count > 1))],
            [.. ordered.Skip(taken).SelectMany(c => c.Length > 1 && IsPack(c) ? c.Select(s => ImmutableArray.Create(s)) : [c])]);

        // まとめかどうか (論理積の組ではないか)。
        bool IsPack(ImmutableArray<string> configuration)
            => packs.Any(p => p.Count > 1 && p.SequenceEqual(configuration));
    }

    /// <summary>トップレベルの宣言ごとに、その中にある連なりが見ているキーワードを集める。</summary>
    /// <param name="root">既定の構成の木の根。</param>
    /// <param name="regions">構成によって結果が変わる連なり。</param>
    /// <returns>2 つ以上のキーワードが集まった宣言ごとのキーワード。</returns>
    /// <remarks>
    /// ヘッダ込みで宣言も連なりも数千になるので、ファイルごとに開始位置で並べて二分探索する。
    /// </remarks>
    private static IEnumerable<HashSet<string>> GroupRegionSymbolsByDeclaration(
        HlslCompilationUnitSyntax root,
        ImmutableArray<KeywordRegion> regions)
    {
        Dictionary<string, List<(int Start, int End, int Index)>> declarations = new(StringComparer.Ordinal);
        int index = 0;

        foreach (HlslDeclarationSyntax declaration in root.Declarations)
        {
            string file = declaration.Source?.FilePath ?? string.Empty;

            if (!declarations.TryGetValue(file, out List<(int Start, int End, int Index)>? list))
            {
                list = [];
                declarations[file] = list;
            }

            list.Add((declaration.Span.Start, declaration.Span.End, index++));
        }

        foreach (List<(int Start, int End, int Index)> list in declarations.Values)
        {
            list.Sort((x, y) => x.Start.CompareTo(y.Start));
        }

        Dictionary<int, HashSet<string>> byDeclaration = [];

        foreach (KeywordRegion region in regions)
        {
            if (region.Symbols.IsEmpty
                || !declarations.TryGetValue(region.FilePath, out List<(int Start, int End, int Index)>? list))
            {
                continue;
            }

            // 連なりより前で始まる最後の宣言から見る。宣言どうしは重ならない。
            int low = 0;
            int high = list.Count - 1;
            int first = list.Count;

            while (low <= high)
            {
                int middle = (low + high) / 2;

                if (list[middle].End > region.Start)
                {
                    first = middle;
                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            for (int i = first; i < list.Count && list[i].Start < region.End; i++)
            {
                if (!byDeclaration.TryGetValue(list[i].Index, out HashSet<string>? symbols))
                {
                    symbols = new HashSet<string>(StringComparer.Ordinal);
                    byDeclaration[list[i].Index] = symbols;
                }

                symbols.UnionWith(region.Symbols);
            }
        }

        return byDeclaration.Values.Where(s => s.Count > 1);
    }

    /// <summary>2 つの名前を、順序によらない組にする。</summary>
    /// <param name="a">1 つ目。</param>
    /// <param name="b">2 つ目。</param>
    /// <returns>名前順に並べた組。</returns>
    private static (string, string) OrderedPair(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>
    /// まとめた構成の木を既定の木と突き合わせ、違いのそれぞれにキーワードを決める。
    /// </summary>
    /// <param name="baseline">既定の構成の木。</param>
    /// <param name="packed">まとめた構成で展開した結果。</param>
    /// <param name="baselineRegions">既定の構成の、構成によって結果が変わる連なり。</param>
    /// <param name="constraints">宣言から分かる構成の制約。</param>
    /// <param name="filePath">解析しているファイルのパス。</param>
    /// <returns>突き合わせた結果。1 つでも決められない違いがあれば <see langword="null"/>。</returns>
    /// <remarks>
    /// 違いのノードを囲む連なりが、まとめたキーワードのうち 1 つだけを見ていれば、そのキーワードの違いとする
    /// (<see cref="KeywordRegionIndex"/>)。既定の木にしか無いノードは既定の木の連なりで、
    /// まとめた木にしか無いノードはまとめた木の連なりで見る。
    /// </remarks>
    private static ConditionalMergeResult? VerifyPacked(
        HlslSyntaxTree baseline,
        SymbolExpansion packed,
        KeywordRegionIndex baselineRegions,
        SymbolConstraints constraints,
        string filePath)
    {
        KeywordRegionIndex packedRegions = new(packed.Tree.PreprocessResult.KeywordRegions);
        ImmutableArray<string> members = packed.EnabledSymbols;

        // 有効にしたキーワードと同時には有効にならないキーワードは、その構成では並べずに外れる。その連なりも有効にしたキーワードのものに数える。
        string? MemberOf(string symbol)
            => members.Contains(symbol) ? symbol : members.FirstOrDefault(member => constraints.AreExclusive(member, symbol));

        ConditionalMergeResult merged = ConditionalMerge.Merge(
            baseline.Root,
            packed.Tree.Root,
            members,
            filePath,
            (node, fromVariant) => (fromVariant ? packedRegions : baselineRegions).Attribute(node, MemberOf));

        return merged.IsComplete && merged.UnattributedLocations.IsEmpty && AreSeparated(merged) ? merged : null;
    }

    /// <summary>
    /// まとめたキーワードの違いどうしが、互いの解析を変えられない位置にあるかを判定する。
    /// </summary>
    /// <param name="merged">違いごとにキーワードを決めた突き合わせの結果。</param>
    /// <returns>どの 2 つのキーワードの違いも分かれていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>まとめてよいのは、1 つずつ展開したときと解析の結果が変わらないキーワードだけである。</b>
    /// まとめた木で <c>_A</c> の違いを解析するとき、<c>_B</c> も有効になっている。
    /// <c>_B</c> の違いが <c>_A</c> の違いの解析に関われば、「<c>_A</c> だけのときに壊れるコード」が
    /// <c>_B</c> に隠されて見えなくなる (<c>_A</c> の中で使う名前を <c>_B</c> が宣言する、
    /// 同じ関数の中で <c>_B</c> が <c>return</c> を足す、など)。
    /// </para>
    /// <para>
    /// そこで次のどちらかがあれば、まとめを捨てて 1 つずつ展開し直す。
    /// </para>
    /// <list type="bullet">
    ///   <item><description>2 つのキーワードの違いが、同じトップレベルの宣言 (関数・構造体・cbuffer など) の中にある</description></item>
    ///   <item><description>一方の違いが宣言する名前が、もう一方の違いに現れる</description></item>
    /// </list>
    /// <para>
    /// これを満たせば、あるキーワードの違いの解析に使われるものは、ほかのキーワードの違いの外にあり、
    /// どちらの木でも同じである。
    /// </para>
    /// </remarks>
    private static bool AreSeparated(ConditionalMergeResult merged)
    {
        Dictionary<string, MemberFootprint> footprints = new(StringComparer.Ordinal);

        foreach (ConditionalNode conditional in merged.ConditionalNodes)
        {
            if (conditional.Condition.EnumerateSymbols().SingleOrDefault() is not { } member)
            {
                return false;
            }

            if (!footprints.TryGetValue(member, out MemberFootprint? footprint))
            {
                footprint = new MemberFootprint();
                footprints[member] = footprint;
            }

            footprint.Add(conditional.Node);
        }

        List<MemberFootprint> all = [.. footprints.Values];

        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                if (all[i].Interferes(all[j]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>まとめたキーワード 1 つの違いが占める範囲と、宣言する名前・現れる名前。</summary>
    private sealed class MemberFootprint
    {
        private readonly List<(string File, int Start, int End)> _topLevel = [];
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
        private readonly HashSet<string> _identifiers = new(StringComparer.Ordinal);

        /// <summary>違いの部分木 1 つを足す。</summary>
        /// <param name="node">部分木の根。</param>
        public void Add(HlslSyntaxNode node)
        {
            HlslSyntaxNode top = node;

            while (top.Parent is HlslSyntaxNode parent and not HlslCompilationUnitSyntax)
            {
                top = parent;
            }

            _topLevel.Add((top.Source?.FilePath ?? string.Empty, top.Span.Start, top.Span.End));

            foreach (HlslSyntaxToken token in node.DescendantTokens())
            {
                if (token.Kind == HlslSyntaxKind.IdentifierToken)
                {
                    _identifiers.Add(token.Text);
                }
            }

            foreach (SyntaxNode descendant in node.DescendantNodesAndSelf())
            {
                string? name = descendant switch
                {
                    VariableDeclaratorSyntax variable => variable.Name,
                    ParameterSyntax parameter => parameter.Name,
                    FunctionDeclarationSyntax function => function.Name,
                    StructDeclarationSyntax structure => structure.Name,
                    ConstantBufferDeclarationSyntax buffer => buffer.Name,
                    TypedefDeclarationSyntax typedef => typedef.Tokens.LastOrDefault(t => t.Kind == HlslSyntaxKind.IdentifierToken)?.Text,
                    _ => null,
                };

                if (!string.IsNullOrEmpty(name))
                {
                    _declared.Add(name);
                }
            }
        }

        /// <summary>もう一方のキーワードの違いと関わり合うかを判定する。</summary>
        /// <param name="other">もう一方。</param>
        /// <returns>関わり合えば <see langword="true"/>。</returns>
        public bool Interferes(MemberFootprint other)
            => _topLevel.Any(a => other._topLevel.Any(
                   b => string.Equals(a.File, b.File, StringComparison.Ordinal) && a.Start < b.End && b.Start < a.End))
               || _declared.Overlaps(other._identifiers)
               || other._declared.Overlaps(_identifiers);
    }

    /// <summary>
    /// 条件に書かれた論理積のうち、構成として作るものを選ぶ。
    /// </summary>
    /// <param name="result">展開の結果。</param>
    /// <param name="declared">宣言されているシンボル。</param>
    /// <param name="alreadyMerged">両方の分岐を 1 つの木に残せたシンボル。</param>
    /// <param name="defaults">既定の構成で定義するシンボル。</param>
    /// <param name="singles">1 つずつの構成として作るシンボル。</param>
    /// <param name="constraints">宣言から分かる構成の制約。</param>
    /// <param name="filePath">解析しているファイル。ここに書かれた条件の組を先に置く。</param>
    /// <returns>構成として作る組。並び順は決定的である。</returns>
    /// <remarks>
    /// <para>
    /// <b>組の中のシンボルは、すべて宣言されていなければならない。</b>
    /// 宣言の無いシンボルは切り替えようがない (それ自体は HL0330 が報告する)。
    /// </para>
    /// <para>
    /// <b>両方の分岐を並べたシンボルが混ざっていてもよい。</b>
    /// 並べた <c>#ifdef _A</c> の中の、並べられなかった <c>#ifdef _B</c> は、
    /// 既定の構成では <c>_B</c> が無いので読み飛ばされ、<c>_B</c> の構成では <c>_A</c> が無いので読み飛ばされる。
    /// 組の全部を並べたなら、その領域は既定の構成の木に載っているので作らない。
    /// </para>
    /// <para>
    /// <b>同じ宣言に並べたシンボルどうしの組は作らない。</b>
    /// <c>#pragma multi_compile _ _X _Y</c> の <c>_X</c> と <c>_Y</c> は同時に有効にならない。
    /// 取り込んだヘッダの宣言も見る (展開の途中では、このブロック自身の宣言しか見ていない)。
    /// </para>
    /// <para>
    /// <b>既定の構成で定義するシンボルは組から除く。</b>
    /// <c>#pragma multi_compile MODE_A MODE_B</c> の <c>MODE_A</c> は既定の構成で定義してあるので、
    /// <c>MODE_A</c> と <c>_X</c> の組は <c>_X</c> だけの構成と同じである。
    /// </para>
    /// <para>
    /// <b>1 つだけの組も、1 つずつの構成に無ければ作る。</b>
    /// <c>#ifdef MODE_A ... #else</c> の <c>#else</c> は <c>MODE_B</c> を有効にしないと通らないが、
    /// 条件に <c>MODE_B</c> が書かれていなければ、1 つずつの構成の候補には入らない
    /// (<see cref="SymbolConstraints.EnumerateRequiredCombinations"/>)。
    /// </para>
    /// <para>
    /// <b>このファイルに書かれた条件の組を先に置く。</b>
    /// 取り込んだヘッダに書かれた条件の組も作るが、上限に達したときに落ちるのはヘッダ側からにする。
    /// </para>
    /// <para>
    /// 並び順は、組の中身を並べた文字列で固定する。
    /// 上限に達したときにどれが落ちるかを、実行のたびに変えない。
    /// </para>
    /// </remarks>
    private static IEnumerable<ImmutableArray<string>> SelectVariantCombinations(
        PreprocessResult result,
        HashSet<string> declared,
        HashSet<string> alreadyMerged,
        HashSet<string> defaults,
        HashSet<string> singles,
        SymbolConstraints constraints,
        string filePath)
    {
        if (result.RequiredSymbolCombinations.IsDefaultOrEmpty)
        {
            return [];
        }

        return result.RequiredSymbolCombinations
            .Where(c => c.Symbols.All(declared.Contains) && constraints.IsPossible(RequireAll(c.Symbols)))
            .Select(c => (Symbols: WithoutDefaults(c.Symbols, defaults), c.Location))
            .Where(c => (c.Symbols.Length > 1 || (c.Symbols.Length == 1 && !singles.Contains(c.Symbols[0])))
                        && !c.Symbols.All(alreadyMerged.Contains))
            .DistinctBy(c => string.Join(',', c.Symbols), StringComparer.Ordinal)
            .OrderBy(c => string.Equals(c.Location.FilePath, filePath, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(c => string.Join(',', c.Symbols), StringComparer.Ordinal)
            .Select(c => c.Symbols);
    }

    /// <summary>組から、既定の構成で定義するシンボルを除く。</summary>
    /// <param name="combination">対象の組。</param>
    /// <param name="defaults">既定の構成で定義するシンボル。</param>
    /// <returns>除いた組。</returns>
    private static ImmutableArray<string> WithoutDefaults(ImmutableArray<string> combination, HashSet<string> defaults)
        => combination.Any(defaults.Contains) ? [.. combination.Where(s => !defaults.Contains(s))] : combination;

    /// <summary>すべてのシンボルが定義されている、という条件を作る。</summary>
    /// <param name="symbols">対象のシンボル。</param>
    /// <returns>作った条件。</returns>
    private static SymbolCondition RequireAll(ImmutableArray<string> symbols)
        => symbols.Aggregate(SymbolCondition.Always, (condition, symbol) => condition.And(SymbolCondition.Symbol(symbol)));

    /// <summary>
    /// このブロック自身が宣言しているシンボルを、展開の前に拾う。
    /// </summary>
    /// <param name="maskedText">対象のコードブロック。</param>
    /// <param name="options">実行時設定。</param>
    /// <param name="exclusions">同じ宣言に並べたために、同時には定義されないシンボルの組。</param>
    /// <param name="shaderTarget"><c>#pragma target</c> から決まる <c>SHADER_TARGET</c> の値。無ければ <see langword="null"/>。</param>
    /// <param name="surfaceLighting"><c>#pragma surface</c> の照明モデル。サーフェスシェーダーでなければ <see langword="null"/>。</param>
    /// <returns>拾ったシンボル。</returns>
    /// <remarks>
    /// 字句解析の結果は共有しているので、ここで読むのは実質ただである
    /// (このあとの展開が同じトークン列を使う)。
    /// </remarks>
    private static ImmutableHashSet<string> CollectBlockSymbols(
        SourceText maskedText,
        SemanticsOptions options,
        out SymbolConstraints exclusions,
        out string? shaderTarget,
        out string? surfaceLighting)
    {
        ImmutableArray<HlslSyntaxToken> tokens = options.TokenCache is { } cache
            ? cache.GetOrLex(maskedText, out _)
            : new HlslLexer(maskedText).Lex(out _);

        exclusions = ShaderSymbols.CollectConstraintsFromTokens(tokens);
        shaderTarget = ReadShaderTarget(tokens);
        surfaceLighting = ReadSurfaceLighting(tokens);
        return [.. ShaderSymbols.CollectDeclaredFromTokens(tokens)];
    }

    /// <summary>
    /// <c>#pragma surface 関数 照明モデル</c> から照明モデルの名前を読む。
    /// </summary>
    /// <param name="tokens">字句解析しただけのトークン列。</param>
    /// <returns>照明モデルの名前。サーフェスシェーダーでなければ <see langword="null"/>。</returns>
    private static string? ReadSurfaceLighting(ImmutableArray<HlslSyntaxToken> tokens)
    {
        for (int i = 0; i + 4 < tokens.Length; i++)
        {
            if (tokens[i].Kind == HlslSyntaxKind.HashToken
                && tokens[i].IsAtLineStart
                && tokens[i + 1].Text == "pragma"
                && tokens[i + 2].Text == "surface"
                && !tokens[i + 3].IsAtLineStart
                && !tokens[i + 4].IsAtLineStart)
            {
                return tokens[i + 4].Text;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>#pragma target</c> から、Unity が定義する <c>SHADER_TARGET</c> の値を読む。
    /// </summary>
    /// <param name="tokens">字句解析しただけのトークン列。</param>
    /// <returns>
    /// <c>SHADER_TARGET</c> の値 (<c>#pragma target 3.5</c> なら <c>35</c>)。
    /// 指定が無ければ <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>ヘッダは <c>SHADER_TARGET</c> で経路を選ぶ。</b>
    /// <c>HLSLSupport.cginc</c> の <c>#if (SHADER_TARGET &lt; 30)</c> や
    /// <c>UNITY_SM40_PLUS_PLATFORM</c> がそうである。既定の 45 のまま読むと、
    /// <c>#pragma target 2.0</c> のシェーダーでは決して通らない経路を検査する。
    /// </para>
    /// <para>
    /// 複数書かれていれば最も高いものを使う。<c>HLSLINCLUDE</c> と <c>HLSLPROGRAM</c> の両方に書く形がある。
    /// </para>
    /// <para>
    /// <b>指定が無いときは何もしない。</b> Unity の既定値や、<c>#pragma geometry</c> などが
    /// 引き上げる値は、この環境で確かめられないので推測しない。
    /// </para>
    /// </remarks>
    private static string? ReadShaderTarget(ImmutableArray<HlslSyntaxToken> tokens)
    {
        int best = -1;

        for (int i = 0; i + 3 < tokens.Length; i++)
        {
            if (tokens[i].Kind != HlslSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || tokens[i + 1].Text != "pragma"
                || tokens[i + 2].Text != "target"
                || tokens[i + 3].IsAtLineStart)
            {
                continue;
            }

            // "4.5" は 1 つの数値トークンになる。"4.5" 以外の書き方 ("4.5 compute" の後半など) は無視する。
            string text = tokens[i + 3].Text;
            int dot = text.IndexOf('.');

            if (dot > 0
                && dot + 1 < text.Length
                && int.TryParse(text.AsSpan(0, dot), out int major)
                && char.IsAsciiDigit(text[dot + 1]))
            {
                best = Math.Max(best, (major * 10) + (text[dot + 1] - '0'));
            }
        }

        return best < 0 ? null : best.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 取り込んだヘッダが宣言したシンボルを、並べる対象に足す。
    /// </summary>
    /// <param name="tree">1 度目の展開の結果。</param>
    /// <param name="blockSymbols">並べる対象のシンボル。足した結果を書き戻す。</param>
    /// <param name="constraints">宣言から分かる構成の制約。ヘッダの宣言も見たものを書き戻す。</param>
    /// <returns>足したものがあり、展開し直す価値があるかどうか。</returns>
    /// <remarks>
    /// <para>
    /// <b>ヘッダの <c>#pragma</c> は、取り込んでみるまで分からない。</b>
    /// 共通の関数を <c>Common.hlsl</c> に切り出し、そこに <c>#pragma multi_compile</c> も置く書き方は珍しくない。
    /// このブロックのテキストを読むだけでは、その宣言が見えない。
    /// 見えないまま展開すると、ヘッダの <c>#ifdef</c> は既定の構成の側しか読まれず、
    /// もう一方の側にあるコードは一度も検査されない。
    /// </para>
    /// <para>
    /// <b>条件で見ていないシンボルを足しても、コードは 1 行も変わらない。</b>
    /// 展開をもう 1 度行うのは、ヘッダが宣言したシンボルの <c>#ifdef</c> が実際にある場合だけにする。
    /// Unity 同梱のヘッダはほとんど宣言を持たないので、ふだんはここで止まる。
    /// </para>
    /// <para>
    /// 制約も宣言から拾い直す。同じ行に並べたシンボルを同時に定義された形で並べてしまうと、
    /// 実在しない構成のコードを検査することになる。
    /// </para>
    /// </remarks>
    private static bool TryAddIncludedSymbols(
        HlslSyntaxTree tree,
        ref ImmutableHashSet<string> blockSymbols,
        ref SymbolConstraints constraints)
    {
        PreprocessResult result = tree.PreprocessResult;
        ImmutableHashSet<string> known = blockSymbols;
        HashSet<string> declared = ShaderSymbols.CollectDeclared(result.Pragmas);

        if (declared.All(known.Contains))
        {
            return false;
        }

        if (!result.ConditionalIdentifiers.Any(t => declared.Contains(t.Text) && !known.Contains(t.Text)))
        {
            return false;
        }

        blockSymbols = known.Union(declared);
        constraints = ShaderSymbols.CollectConstraints(result.Pragmas);
        return true;
    }

    /// <summary>コードブロックを展開する設定を作る。</summary>
    /// <param name="baseOptions">このブロックのマクロを重ねた設定。</param>
    /// <param name="blockSymbols">並べる対象のシンボル。</param>
    /// <param name="constraints">宣言から分かる構成の制約。</param>
    /// <param name="options">実行時設定。</param>
    /// <param name="includedSymbols">取り込んだヘッダ自身が宣言したシンボル。</param>
    /// <returns>作った設定。</returns>
    /// <remarks>
    /// <c>_</c> の無い <c>multi_compile</c> の行は、既定の構成でも先頭のシンボルを定義する。
    /// 「どれも無い」構成は実在しない。
    /// </remarks>
    private static PreprocessorOptions BlockOptions(
        PreprocessorOptions baseOptions,
        ImmutableHashSet<string> blockSymbols,
        SymbolConstraints constraints,
        SemanticsOptions options,
        ImmutableHashSet<string> includedSymbols)
        => WithBothBranchSymbols(
            baseOptions with { PredefinedMacros = ConfigurationMacros(baseOptions.PredefinedMacros, [], constraints) },
            options.BothBranchSymbols ?? blockSymbols) with
        {
            DeclaredSymbols = blockSymbols,
            ConfigurationSymbols = blockSymbols,
            IncludedDeclaredSymbols = includedSymbols,
            SymbolConstraints = constraints,
            HoistConditionalMacros = options.HoistConditionalMacros,
            MergeSwitchedIncludes = options.MergeSwitchedIncludes,
        };

    /// <summary>設定のシンボルだけを差し替えた複製を返す。</summary>
    /// <param name="options">元の設定。</param>
    /// <param name="kept">両方の分岐を残すシンボル。</param>
    /// <returns>差し替えた設定。</returns>
    /// <remarks>
    /// 項目を書き写さない。設定が 1 つ増えたときに、
    /// ここだけ古いままになるのを避ける。
    /// </remarks>
    private static PreprocessorOptions WithBothBranchSymbols(
        PreprocessorOptions options,
        ImmutableHashSet<string> kept)
        => options with { BothBranchSymbols = kept };

    /// <summary><c>CGPROGRAM</c> に Unity が自動で取り込むヘッダ。</summary>
    private static readonly string[] CgAutoIncludes = ["HLSLSupport.cginc", "UnityShaderVariables.cginc"];

    /// <summary>サーフェスシェーダーに Unity が自動で取り込むヘッダ。</summary>
    private static readonly string[] SurfaceAutoIncludes = ["UnityCG.cginc", "Lighting.cginc"];

    /// <summary>作った先頭のコード。同じヘッダの組には同じテキストを返し、字句解析の結果を使い回す。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceText> CgPreludes =
        new(StringComparer.Ordinal);

    /// <summary>
    /// <c>CGPROGRAM</c> に Unity が自動で取り込むヘッダを、先頭のコードに足した複製を返す。
    /// </summary>
    /// <param name="options">元の設定。</param>
    /// <param name="filePath">解析しているファイルのパス。取り込むヘッダを探す基点になる。</param>
    /// <param name="surfaceLighting"><c>#pragma surface</c> の照明モデル。サーフェスシェーダーでなければ <see langword="null"/>。</param>
    /// <returns>足した設定。ヘッダが 1 つでも見つからなければ元の設定。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>CGPROGRAM</c> は、書いていないヘッダも取り込む。</b>
    /// Unity のマニュアル (「HLSL in Unity」) によれば、<c>CGPROGRAM</c> には
    /// <c>HLSLSupport.cginc</c> と <c>UnityShaderVariables.cginc</c> が自動で取り込まれる。
    /// 取り込まないと、<c>fixed4</c> や <c>unity_ObjectToWorld</c> を使うだけのシェーダーで、
    /// 宣言されていない名前を読むことになる。<c>HLSLPROGRAM</c> には取り込まれない。
    /// </para>
    /// <para>
    /// <b>見つからなければ何もしない。</b> Unity が無い環境で足すと、利用者が書いていない
    /// <c>#include</c> が解決できず、依存関係が不完全だとして検査を見送ることになる。
    /// </para>
    /// <para>
    /// 見つけたファイルは絶対パスで取り込む。先頭のコードは実在しないパスを持つので、
    /// そこからの相対では探せない。
    /// </para>
    /// <para>
    /// <b>サーフェスシェーダーは、さらに照明のヘッダも取り込む。</b>
    /// Unity の「Standard Surface Shader」の雛形は、<c>#include</c> を 1 行も書かずに
    /// <c>SurfaceOutputStandard</c> を使う。<c>Lambert</c> の照明関数 <c>LightingLambert</c> も書かずに使う。
    /// 生成されるコードは <c>UnityCG.cginc</c> と <c>Lighting.cginc</c> を取り込み、
    /// <c>Standard</c> / <c>StandardSpecular</c> ではさらに <c>UnityPBSLighting.cginc</c> を取り込む。
    /// </para>
    /// </remarks>
    private static PreprocessorOptions WithCgAutoIncludes(
        PreprocessorOptions options,
        string filePath,
        string? surfaceLighting)
    {
        if (options.IncludeResolver is not { } resolver)
        {
            return options;
        }

        IEnumerable<string> names = CgAutoIncludes;

        if (surfaceLighting is not null)
        {
            names = names.Concat(SurfaceAutoIncludes);

            if (surfaceLighting.StartsWith("Standard", StringComparison.Ordinal))
            {
                names = names.Append("UnityPBSLighting.cginc");
            }
        }

        System.Text.StringBuilder includes = new();

        foreach (string name in names)
        {
            if (!resolver.TryResolve(name, filePath, out SourceText? header) || string.IsNullOrEmpty(header.FilePath))
            {
                return options;
            }

            includes.Append("#include \"").Append(header.FilePath.Replace('\\', '/')).Append("\"\n");
        }

        string key = (options.Prelude?.ToString() ?? string.Empty) + "\n" + includes;
        SourceText prelude = CgPreludes.GetOrAdd(
            key,
            static (k, path) => SourceText.From(k, path),
            options.Prelude?.FilePath ?? "<shaderlyn CGPROGRAM>");

        return options with { Prelude = prelude };
    }

    /// <summary>
    /// このブロックだけで定義されるマクロを重ねた複製を返す。
    /// </summary>
    /// <param name="options">元の設定。</param>
    /// <param name="extra">重ねるマクロ。無い場合は <see langword="null"/>。</param>
    /// <returns>重ねた設定。重ねるものが無ければ元の設定。</returns>
    /// <remarks>
    /// <c>#pragma kernel</c> がカーネルごとに並べたマクロがここへ来る。
    /// <b>ブロック側が勝つ。</b> カーネルの指定は
    /// そのカーネルをコンパイルするときの事実であり、
    /// コマンドラインの既定より具体的である。
    /// </remarks>
    private static PreprocessorOptions WithExtraMacros(
        PreprocessorOptions options,
        ImmutableDictionary<string, string>? extra)
    {
        if (extra is not { Count: > 0 })
        {
            return options;
        }

        ImmutableDictionary<string, string> macros = options.PredefinedMacros;

        foreach ((string name, string value) in extra)
        {
            macros = macros.SetItem(name, value);
        }

        return options with { PredefinedMacros = macros };
    }

    /// <summary>
    /// 両方の分岐を 1 つの木に残せたシンボルを集める。
    /// </summary>
    /// <param name="result">展開の結果。</param>
    /// <returns>残せたシンボルの名前。</returns>
    /// <remarks>
    /// <para>
    /// <b>残せたかどうかは展開してみないと分からない。</b>
    /// 設定で指定していても、その領域がマクロを切り替えていたり
    /// 構文の単位で閉じていなければ、今までどおり片方だけが残る。
    /// </para>
    /// <para>
    /// <b>1 つのシンボルが複数の領域を守っていることがある。</b>
    /// 1 か所でも残せなかったなら、そのシンボルはまだ構成ごとの展開が要る。
    /// 残せた場所があるからといって外すと、残せなかった場所の宣言が見えなくなる。
    /// </para>
    /// </remarks>
    private static HashSet<string> CollectMergedSymbols(PreprocessResult result)
    {
        HashSet<string> merged = new(StringComparer.Ordinal);

        foreach (ConditionalTokenRange range in result.ConditionalRegions)
        {
            foreach (string symbol in range.Condition.EnumerateSymbols())
            {
                merged.Add(symbol);
            }
        }

        // 中身が #define だけの分岐は、並べても範囲に現れない。
        if (!result.MergedSymbols.IsDefaultOrEmpty)
        {
            merged.UnionWith(result.MergedSymbols);
        }

        merged.ExceptWith(result.DeclinedBothBranchSymbols);

        return merged;
    }

    /// <summary>1 つの構成で展開した結果 1 件分。</summary>
    /// <param name="EnabledSymbols">有効にしたシンボル。</param>
    /// <param name="Tree">展開して得た構文木。</param>
    /// <param name="Declarations">その構成で見つかった宣言。</param>
    private readonly record struct SymbolExpansion(
        ImmutableArray<string> EnabledSymbols,
        HlslSyntaxTree Tree,
        DeclarationSet Declarations);

    /// <summary>
    /// 構成ごとに展開する。
    /// </summary>
    /// <param name="maskedText">対象のコードブロック。</param>
    /// <param name="configurations">展開する構成。1 件が 1 つの構成で、同時に有効にするシンボルを並べる。</param>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <param name="constraints">宣言から分かる構成の制約。どれか 1 つが必ず有効な行の既定を決めるのに使う。</param>
    /// <param name="syntaxTimings">段ごとの時間を受け取る先。測らない場合は <see langword="null"/>。</param>
    /// <returns>展開した結果。<paramref name="configurations"/> と同じ順序で並ぶ。</returns>
    /// <remarks>
    /// <para>
    /// <b>展開はシンボルごとに独立している。</b>
    /// どの構成も同じ入力を読み、互いの結果を参照しない。
    /// 1 件あたり 100ms を超えることもあり、
    /// エディタでは保存から指摘が出るまでの間隔にそのまま乗る。
    /// </para>
    /// <para>
    /// <b>取り込む順序は変えない。</b>
    /// 結果を添字の位置へ書き戻し、呼び出し側は順番に取り出す。
    /// uniform の一覧や出現回数は取り込んだ順に積み上がるため、
    /// 実行のたびに順序が変わると、同じ入力から違う報告が出る。
    /// </para>
    /// <para>
    /// 1 件しかないときは並列にしない。仕事より段取りのほうが高くつく。
    /// </para>
    /// </remarks>
    private static SymbolExpansion[] ExpandSymbols(
        SourceText maskedText,
        ImmutableArray<ImmutableArray<string>> configurations,
        PreprocessorOptions options,
        SymbolConstraints constraints,
        ISyntaxTimingRecorder? syntaxTimings)
    {
        SymbolExpansion[] results = new SymbolExpansion[configurations.Length];

        if (configurations.Length == 0)
        {
            return results;
        }

        if (configurations.Length == 1)
        {
            results[0] = Expand(maskedText, configurations[0], options, constraints, syntaxTimings);
            return results;
        }

        Parallel.For(
            0,
            configurations.Length,
            i => results[i] = Expand(maskedText, configurations[i], options, constraints, syntaxTimings));

        return results;
    }

    /// <summary>
    /// 構成 1 つ分の定義済みマクロを作る。
    /// </summary>
    /// <param name="predefined">構成に関わらず定義するマクロ。</param>
    /// <param name="enabledSymbols">有効にするシンボル。</param>
    /// <param name="constraints">宣言から分かる構成の制約。</param>
    /// <returns>作ったマクロ。</returns>
    /// <remarks>
    /// <b>どれか 1 つが必ず有効な行は、どれも有効にしていなければ先頭を定義する。</b>
    /// <c>#pragma multi_compile MODE_A MODE_B</c> で <c>MODE_B</c> を有効にした構成は <c>MODE_A</c> を定義せず、
    /// どちらも選んでいない構成は <c>MODE_A</c> を定義する。
    /// 定義しないと、Unity がコンパイルしない「どちらも無い」構成を解析することになる。
    /// どれを選んでも実在する構成の 1 つなので、宣言に書かれた先頭を選ぶ。
    /// </remarks>
    private static ImmutableDictionary<string, string> ConfigurationMacros(
        ImmutableDictionary<string, string> predefined,
        ImmutableArray<string> enabledSymbols,
        SymbolConstraints constraints)
    {
        ImmutableDictionary<string, string> macros = predefined;

        foreach (string symbol in enabledSymbols)
        {
            macros = macros.SetItem(symbol, "1");
        }

        foreach (ImmutableArray<string> group in constraints.RequiredGroups)
        {
            if (!group.Any(macros.ContainsKey))
            {
                macros = macros.SetItem(group[0], "1");
            }
        }

        return macros;
    }

    /// <summary>
    /// 並べた分岐のマクロがコードとして展開されたら、そのシンボルを並べずに展開し直す。
    /// </summary>
    /// <param name="maskedText">対象のコードブロック。</param>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <param name="syntaxTimings">段ごとの時間を受け取る先。測らない場合は <see langword="null"/>。</param>
    /// <param name="removed">並べる対象から外したシンボル。名前順に並ぶ。</param>
    /// <returns>展開して得た構文木。</returns>
    /// <remarks>
    /// <para>
    /// <b>並べてよいかは、展開してみるまで分からない。</b>
    /// 分岐の中の <c>#define</c> は、条件の中でしか使われていなければ並べてよい。
    /// コードの中で使われていないことは、このファイルのトークンを見れば分かるが、
    /// 取り込むヘッダが使うかどうかは並べるかを決める時点では分からない。
    /// </para>
    /// <para>
    /// 先回りして諦めるのではなく、展開してから事実で判断する。
    /// 実際に展開されたシンボルだけを並べる対象から外し、そのブロックを展開し直す
    /// (<see cref="PreprocessResult.MergedMacroConflicts"/>)。
    /// </para>
    /// <para>
    /// 繰り返しは、外したシンボルが増えなくなるまで。並べる対象は毎回減るので必ず終わる。
    /// </para>
    /// </remarks>
    private static HlslSyntaxTree ParseWithoutMergedMacroConflicts(
        SourceText maskedText,
        PreprocessorOptions options,
        ISyntaxTimingRecorder? syntaxTimings,
        out ImmutableArray<string> removed)
    {
        ImmutableHashSet<string> original = options.BothBranchSymbols;
        HlslSyntaxTree tree = HlslSyntaxTree.Parse(maskedText, options, syntaxTimings);

        while (!tree.PreprocessResult.MergedMacroConflicts.IsDefaultOrEmpty)
        {
            ImmutableHashSet<string> kept = options.BothBranchSymbols.Except(tree.PreprocessResult.MergedMacroConflicts);

            if (kept.Count == options.BothBranchSymbols.Count)
            {
                break;
            }

            options = options with { BothBranchSymbols = kept };
            tree = HlslSyntaxTree.Parse(maskedText, options, syntaxTimings);
        }

        removed = [.. original.Except(options.BothBranchSymbols).Order(StringComparer.Ordinal)];
        return tree;
    }

    /// <summary>既定の構成で定義するシンボルを求める。</summary>
    /// <param name="predefined">構成に関わらず定義するマクロ。</param>
    /// <param name="constraints">宣言から分かる構成の制約。</param>
    /// <returns>既定の構成で定義するシンボル。</returns>
    /// <remarks>
    /// これを 1 つだけ有効にした構成は、既定の構成と同じである。バリアントとして作り直す必要が無い。
    /// </remarks>
    private static HashSet<string> DefaultSymbols(
        ImmutableDictionary<string, string> predefined,
        SymbolConstraints constraints)
        => [.. constraints.RequiredGroups.Where(g => !g.Any(predefined.ContainsKey)).Select(g => g[0])];

    /// <summary>指定した構成で展開する。</summary>
    /// <param name="maskedText">対象のコードブロック。</param>
    /// <param name="enabledSymbols">有効にするシンボル。</param>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <param name="constraints">宣言から分かる構成の制約。どれか 1 つが必ず有効な行の既定を決めるのに使う。</param>
    /// <param name="syntaxTimings">段ごとの時間を受け取る先。測らない場合は <see langword="null"/>。</param>
    /// <returns>展開した結果。</returns>
    /// <remarks>
    /// <para>
    /// 2 つ以上を同時に有効にするのは、論理積で守られた領域のためである。
    /// Unity がシンボルを値 1 のマクロとして定義するのに合わせる。
    /// </para>
    /// <para>
    /// <b>有効にしたシンボル以外は、既定の構成と同じく両方の分岐を並べる。</b>
    /// 突き合わせは「既定の木にあってバリアントに無いものは、そのシンボルのときは無い」と読む。
    /// 並べずに展開すると、既定の木で並べた別のシンボルの分岐までバリアントから消え、
    /// 無関係なシンボルの否定がその分岐の条件に付く。
    /// </para>
    /// <para>
    /// 有効にしたシンボルと同時には有効にならないシンボルも並べない。この構成では無いと決まっている。
    /// 並べると、<c>#ifdef _X</c> の中の <c>#ifdef _Y</c> が <c>_Y</c> の構成で通ってしまう。
    /// </para>
    /// </remarks>
    private static SymbolExpansion Expand(
        SourceText maskedText,
        ImmutableArray<string> enabledSymbols,
        PreprocessorOptions options,
        SymbolConstraints constraints,
        ISyntaxTimingRecorder? syntaxTimings)
    {
        // 既定の構成と同じシンボルを並べ、有効にしたシンボルだけを固定する。
        // 並べずに展開すると、既定の木と違うのは有効にしたシンボルの分岐だけではなくなる。
        // 既定の木で並べた分岐がまるごと「このシンボルのときは無い」と読まれてしまう。
        PreprocessorOptions variantOptions = options with
        {
            PredefinedMacros = ConfigurationMacros(options.PredefinedMacros, enabledSymbols, constraints),
            BothBranchSymbols = options.BothBranchSymbols
                .Except(enabledSymbols)
                .Except(options.BothBranchSymbols.Where(s => enabledSymbols.Any(e => constraints.AreExclusive(s, e)))),
        };

        HlslSyntaxTree tree = HlslSyntaxTree.Parse(maskedText, variantOptions, syntaxTimings);

        return new SymbolExpansion(enabledSymbols, tree, UniformCollector.Collect(tree.Root));
    }

    /// <summary><c>Properties</c> ブロックからプロパティを集める。</summary>
    /// <param name="tree">ShaderLab の構文木。</param>
    /// <param name="text">ソーステキスト。</param>
    /// <returns>見つかったプロパティ。</returns>
    private static ImmutableArray<PropertySymbol> CollectProperties(ShaderLabSyntaxTree tree, SourceText text)
    {
        ImmutableArray<PropertySymbol>.Builder properties = ImmutableArray.CreateBuilder<PropertySymbol>();

        foreach (SyntaxNode node in tree.Root.DescendantNodesAndSelf())
        {
            if (node is PropertyDeclarationSyntax declaration
                && !declaration.NameToken.IsMissing
                && declaration.Name.Length > 0)
            {
                properties.Add(new PropertySymbol(declaration, text));
            }
        }

        return properties.ToImmutable();
    }

    /// <summary>
    /// ShaderLab 側からプロパティを参照している箇所を集める。
    /// </summary>
    /// <param name="tree">ShaderLab の構文木。</param>
    /// <returns>参照されているプロパティ名。</returns>
    /// <remarks>
    /// <c>Cull [_Cull]</c> や <c>SetTexture [_MainTex]</c> の形の参照が対象。
    /// </remarks>
    private static HashSet<string> CollectShaderLabPropertyReferences(ShaderLabSyntaxTree tree)
    {
        HashSet<string> references = new(StringComparer.Ordinal);

        foreach (SyntaxNode node in tree.Root.DescendantNodesAndSelf())
        {
            if (node is PropertyReferenceArgumentSyntax reference && !reference.NameToken.IsMissing)
            {
                references.Add(reference.Name);
            }
        }

        return references;
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
    /// マクロ定義の本体に現れた識別子を集める。
    /// </summary>
    /// <param name="macros">処理完了時点で定義されているマクロ。</param>
    /// <param name="identifiers">集計先。</param>
    /// <remarks>
    /// <b>呼び出されなかったマクロの中身は、展開後のコードに一切現れない。</b>
    /// URP はテクスチャの参照をマクロ定義の中だけに書くことがあり
    /// (<c>#define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_MetallicGlossMap, ...)</c>)、
    /// これを見ないと正しく使われているテクスチャを「使われていない」と報告してしまう。
    /// </remarks>
    private static void CollectMacroBodyIdentifiers(
        ImmutableDictionary<string, MacroDefinition> macros,
        HashSet<string> identifiers)
    {
        foreach (MacroDefinition macro in macros.Values)
        {
            foreach (HlslSyntaxToken token in macro.Body)
            {
                if (token.Kind == HlslSyntaxKind.IdentifierToken)
                {
                    identifiers.Add(token.Text);
                }
            }
        }
    }

    /// <summary>
    /// トークン列に現れた識別子の回数を数える。
    /// </summary>
    /// <param name="tokens">展開済みのトークン列。</param>
    /// <param name="initials">数える対象の名前の先頭文字。</param>
    /// <param name="occurrences">回数の集計先。</param>
    /// <remarks>
    /// 先頭文字による足切りを先に行う。展開後のトークン列は
    /// URP のヘッダ群を含めると数十万件になり、
    /// すべてに対して辞書を引くと解析時間の大半がここに消える。
    /// </remarks>
    private static void CountOccurrences(
        ImmutableArray<HlslSyntaxToken> tokens,
        SearchValues<char> initials,
        Dictionary<string, int> occurrences)
    {
        foreach (HlslSyntaxToken token in tokens)
        {
            if (token.Kind != HlslSyntaxKind.IdentifierToken
                || token.Text.Length == 0
                || !initials.Contains(token.Text[0]))
            {
                continue;
            }

            occurrences[token.Text] = occurrences.GetValueOrDefault(token.Text) + 1;
        }
    }
}
