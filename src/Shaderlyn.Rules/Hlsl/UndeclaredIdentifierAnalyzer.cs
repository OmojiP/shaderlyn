using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// 宣言されていない識別子の使用を報告する (HL0310)。
/// </summary>
/// <remarks>
/// <para>
/// <b>この検査は「宣言をすべて把握できている」ことが前提である。</b>
/// 取り込めなかったヘッダが 1 つでもあれば、そこにある宣言が見えないまま
/// 「宣言されていない」と言うことになる。だから依存が揃うまでは何もしない。
/// </para>
/// <para>
/// <b>呼び出しの対象も見る。</b>
/// 組み込み関数の名前を <see cref="HlslIntrinsics.IsKnownFunction"/> で除けるので、
/// スペルミスの関数呼び出しも報告できる。
/// 表に無い名前が出たら、それは表の不足である。報告しないのではなく表へ足す。
/// </para>
/// <para>
/// <b>関数の中の変数としてしか宣言されていない名前は、その位置で見えるかを確かめる。</b>
/// 別の関数の局所変数は、その位置からは見えない。
/// 名前が指す宣言は <see cref="ExpressionTypeBinder.ResolveName"/> で引き、
/// 同じ位置が現れるすべての木で何も指していない (<see cref="DeclaredNameKind.NotFound"/>) ときだけ報告する。
/// 決められない (<see cref="DeclaredNameKind.Undecidable"/>) なら報告しない。
/// 関数の外の宣言やマクロとしてどこかにある名前は、これまでどおり範囲を見ずに「宣言されている」とする。
/// </para>
/// </remarks>
internal sealed class UndeclaredIdentifierAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>
    /// 宣言なしで使える名前。
    /// </summary>
    /// <remarks>
    /// 真偽値のリテラルは構文の上では識別子と同じ形をしている。
    /// </remarks>
    private static readonly string[] AlwaysAvailable = ["true", "false"];

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.UndeclaredIdentifier];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 取り込めなかったヘッダがあるなら、宣言を把握できていない。
        if (!compilation.HasCompleteDependencies)
        {
            return;
        }

        DeclaredNames declared = new(compilation);

        // 名前空間の修飾 (UnifiedRT::TraceRay の UnifiedRT) の位置。
        // 構文の形はメンバーアクセスと同じなので、左側は識別子の式として現れる。
        // 変数でも関数でもないため、宣言を探しても見つからない。
        HashSet<int> qualifiers = [];

        // 同じ位置の使用を、木をまたいで集める (Uses)。
        Dictionary<int, Uses> uses = [];

        // 報告できるのは利用者が書いたコードだけである。
        // 展開後の木の大半はヘッダで、そこを歩いても 1 件も見つからない。
        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            // 行きがけ順なので、修飾は左側の識別子より先に現れる。
            if (node is MemberAccessExpressionSyntax { DotToken.Kind: HlslSyntaxKind.ColonColonToken } access
                && access.Target.GetLocation() is { } qualifier)
            {
                qualifiers.Add(qualifier.Span.Start);
                continue;
            }

            if (node is IdentifierExpressionSyntax identifier)
            {
                AnalyzeIdentifier(compilation, program, declared, identifier, qualifiers, uses);
            }
        }

        foreach (Uses use in uses.Values)
        {
            if (use.Missing(compilation) is { } missing)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    HlslRuleDescriptors.UndeclaredIdentifier,
                    use.Location,
                    use.Name,
                    missing.IsAlways ? "どこにも" : $" {missing} のとき"));
            }
        }
    }

    /// <summary>識別子 1 つを、それがある木で見えるかどうかとともに覚える。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">識別子がある木。</param>
    /// <param name="declared">宣言されている名前。</param>
    /// <param name="identifier">検査する識別子。</param>
    /// <param name="qualifiers">名前空間の修飾として現れた識別子の位置。</param>
    /// <param name="uses">位置ごとの、木ごとの使用。</param>
    private static void AnalyzeIdentifier(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        DeclaredNames declared,
        IdentifierExpressionSyntax identifier,
        HashSet<int> qualifiers,
        Dictionary<int, Uses> uses)
    {
        // 括弧を付けて呼んだ関数形式マクロの名前が木に残っていれば、その構成ではそのマクロは定義されていない。
        // 定義されていれば展開されて消えている。括弧を付けずに名前だけを書いたものは、今までどおり見ない。
        bool invoked = identifier.Parent is InvocationExpressionSyntax invocation
                       && ReferenceEquals(invocation.Target, identifier);

        if (HlslIntrinsics.IsKnownFunction(identifier.Name)
            || HlslIntrinsics.IsKnownConstant(identifier.Name)
            || (!invoked && compilation.IsFunctionLikeMacro(identifier.Name))
            || HlslTypeClassifier.Classify(identifier.Name) != HlslTypeClass.Unknown
            || program.Tree.Root.TypeAliases.ContainsKey(identifier.Name)
            || !compilation.IsReportable(identifier)
            || identifier.GetLocation() is not { } location
            || qualifiers.Contains(location.Span.Start))
        {
            return;
        }

        bool visible = true;
        SymbolCondition? declaredWhen = null;

        if (compilation.LocalOnlyDeclaredNames.Contains(identifier.Name))
        {
            // 関数の中の変数としてしか宣言されていない名前は、その位置で見えるかを確かめる。
            // 見えるか決められないなら、見えるものとする。
            NameResolution resolution = compilation.GetExpressionTypeBinder(program).ResolveName(identifier);

            if (resolution.Kind == DeclaredNameKind.NotFound)
            {
                visible = false;
            }
            else if (resolution.Kind != DeclaredNameKind.Undecidable && program.EnabledSymbols.IsDefaultOrEmpty)
            {
                // 見えた宣言が #ifdef の中にしか無ければ、その条件のときだけ見える。
                // 両方の分岐を 1 本の木に並べると、_B の中の宣言が、並べた木のどこからも見えてしまう。
                // 宣言の条件は既定の木を基準にしたものなので、既定の木でだけ使う。
                // バリアントの木 (キーワードをまとめて有効にした木を含む) では、見えたか見えなかったかだけを見る。
                declaredWhen = CandidatesCondition(resolution);
            }
        }
        else
        {
            visible = declared.Contains(identifier.Name, program, invoked);
        }

        if (!uses.TryGetValue(location.Span.Start, out Uses? use))
        {
            // 見える木しか無い位置は、覚えておいても報告にならない。条件を求める費用もかけない。
            if (visible && declaredWhen is null && !declared.MayBeHidden)
            {
                return;
            }

            use = new Uses(location, identifier.Name);
            uses[location.Span.Start] = use;
        }

        use.Occurrences.Add(new Occurrence(identifier, program, visible, declaredWhen));
    }

    /// <summary>見えた宣言のどれかがある条件を求める。</summary>
    /// <param name="resolution">名前の解決の結果。</param>
    /// <returns>条件。どの構成にもあるか、条件が分からなければ <see langword="null"/> (どの構成でも見えるとみなす)。</returns>
    private static SymbolCondition? CandidatesCondition(NameResolution resolution)
    {
        SymbolCondition present = SymbolCondition.Never;

        foreach (DeclaredName candidate in resolution.Candidates)
        {
            if (candidate.Condition.IsUnknown || candidate.Condition.IsAlways)
            {
                return null;
            }

            present = present.Or(candidate.Condition);
        }

        return resolution.Candidates.IsEmpty ? null : present;
    }

    /// <summary>1 つの木での、1 つの位置の使用。</summary>
    /// <param name="Node">識別子。</param>
    /// <param name="Program">その木。</param>
    /// <param name="Visible">宣言が見えたかどうか。</param>
    /// <param name="DeclaredWhen">見えた宣言が一部の構成にしか無いなら、その条件。</param>
    private readonly record struct Occurrence(
        IdentifierExpressionSyntax Node,
        AnalyzedProgram Program,
        bool Visible,
        SymbolCondition? DeclaredWhen);

    /// <summary>
    /// 1 つの位置の使用を、木をまたいで集めたもの。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ある木で見えなくても、その構成で誤りとは限らない。逆に、別の木で見えても誤りでないとは限らない。</b>
    /// どちらも、木を解析した構成を踏まえて判断する。
    /// </para>
    /// <para>
    /// 突き合わせで既定の木へ並べた分岐は、宣言の側が並べられずにバリアントの木にしか無いことがある。
    /// 既定の木では使う側だけがあって宣言が見えないが、その構成の木には両方がある。
    /// </para>
    /// <code>
    /// #if USE_TILES
    /// void MAIN(uint groupThreadId : SV_GroupThreadID)   // 仮引数の違う関数の頭は並べられない
    /// #else
    /// void MAIN(uint3 dispatchThreadId : SV_DispatchThreadID)
    /// #endif
    /// {
    /// #if USE_TILES
    ///     if (groupThreadId == 0u) { ... }   // 並べられる。既定の木では仮引数が見えない
    /// #endif
    /// }
    /// </code>
    /// <para>
    /// 一方、<c>#ifdef _B</c> の中でだけ定義したマクロや局所変数を常に使っていれば、
    /// <c>_B</c> の木では見えても、<c>!_B</c> の構成では宣言されていない。
    /// 「どれかの木で見えれば誤りではない」とすると、これを見落とす。
    /// </para>
    /// <para>
    /// そこで、見えなかった木の使用の条件から、見えた木の使用の条件を除いて、残る構成があるかを見る。
    /// 条件の分からない使用があれば判断しない。
    /// </para>
    /// </remarks>
    private sealed class Uses(Location location, string name)
    {
        /// <summary>報告する位置。</summary>
        public Location Location { get; } = location;

        /// <summary>名前。</summary>
        public string Name { get; } = name;

        /// <summary>木ごとの使用。</summary>
        public List<Occurrence> Occurrences { get; } = [];

        /// <summary>宣言が見えない構成を求める。</summary>
        /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
        /// <returns>そういう構成があればその条件。無いか判断できなければ <see langword="null"/>。</returns>
        public SymbolCondition? Missing(ShaderCompilation compilation)
        {
            if (Occurrences.TrueForAll(o => o.Visible && o.DeclaredWhen is null))
            {
                return null;
            }

            // 見えなかった部分を、木ごとに足し合わせる。
            // 木が表す構成は木どうしで重ならない (バリアントになったシンボルの値がどこかで違う)。
            // だから別の木で見えたことを差し引く必要は無い。和を否定すると、長い論理積の和の否定になって式が膨れ上がる。
            // 同じ木の中で見える部分と見えない部分は、宣言の条件 (DeclaredWhen) で分けてある。
            ConditionMap map = compilation.GetConditionMap();
            SymbolCondition missing = SymbolCondition.Never;

            foreach (Occurrence occurrence in Occurrences)
            {
                if (occurrence.Visible && occurrence.DeclaredWhen is null)
                {
                    continue;
                }

                // その木が表す構成を掛ける。どの木も解析していない組み合わせについては言わない。
                SymbolCondition use = compilation.GetEffectiveCondition(occurrence.Node, occurrence.Program)
                    .And(compilation.GetTreeConfiguration(occurrence.Program));

                if (use.IsUnknown)
                {
                    return null;
                }

                SymbolCondition hidden = occurrence.Visible
                    ? use.And(occurrence.DeclaredWhen!.Value.Negate())
                    : use;

                if (!hidden.IsUnknown && map.IsPossible(hidden))
                {
                    missing = missing.Or(hidden);
                }
            }

            return missing.IsNever ? null : map.Simplify(missing);
        }
    }

    /// <summary>
    /// 木ごとに、宣言されている名前を引く。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取り込んだヘッダの宣言も含める。使ってよいかどうかとは別の話である。
    /// </para>
    /// <para>
    /// <b>どの構成でも解析されない領域に現れた名前は、宣言されているものとして扱う。</b>
    /// <c>SHADER_API_*</c> のような環境の条件は 1 通りにしか解かないため、
    /// その裏側で宣言される名前はこの解析からは見えない。
    /// バリアントの上限を超えたシンボルの領域も同じである。
    /// </para>
    /// <para>
    /// <b>マクロは木ごとに見る。</b>
    /// 同じコードブロックの木をまとめると、<c>#ifdef _B</c> の中でだけ定義したマクロを、
    /// <c>_B</c> を有効にしていない木でも「宣言されている」とすることになる。
    /// 条件ごとに見え方が違うことは <see cref="Uses"/> が判断する。
    /// 関数の中の変数も、使う位置で木ごとに解決する。
    /// </para>
    /// <para>
    /// シンボル・uniform・関数の外の宣言は、コードブロックごとにまとめる。
    /// 一部の構成でだけある変数と関数は、HL0315 と HL0311 が構成を添えて報告する。
    /// </para>
    /// <para>
    /// <b>取り込んだヘッダの宣言はコードブロックごとに見る</b> (<see cref="ShaderCompilation.GetHeaderDeclaredNames"/>)。
    /// 別の Pass が取り込んだヘッダの宣言は、そのコードからは見えない。
    /// ヘッダの中で条件付きの宣言は、変数は HL0315、関数は HL0311 が構成ごとに判断している。
    /// </para>
    /// </remarks>
    private sealed class DeclaredNames
    {
        private readonly ShaderCompilation _compilation;
        private readonly HashSet<string> _common = new(StringComparer.Ordinal);
        private readonly Dictionary<(int Start, string? Kernel), HashSet<string>> _own = [];

        /// <summary>組み立てる。</summary>
        /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
        public DeclaredNames(ShaderCompilation compilation)
        {
            _compilation = compilation;
            _common.UnionWith(AlwaysAvailable);
            _common.UnionWith(compilation.UnanalyzedIdentifiers);

            // バリアントがあるか、コードブロックが複数あれば、木によって見え方が違いうる。
            MayBeHidden = !compilation.SymbolVariants.IsEmpty || compilation.Programs.Length > 1;
        }

        /// <summary>木によって見え方が違いうるか。違わないなら、見える位置を覚えておく必要が無い。</summary>
        public bool MayBeHidden { get; }

        /// <summary>その木で名前が宣言されているかを判定する。</summary>
        /// <param name="name">木に残っている識別子の名前。</param>
        /// <param name="program">木。</param>
        /// <param name="invoked">括弧を付けて呼んでいるかどうか。</param>
        /// <returns>宣言されていれば <see langword="true"/>。</returns>
        /// <remarks>
        /// <para>
        /// <b>マクロの名前が識別子のまま木に残っているなら、その位置ではそのマクロは定義されていない。</b>
        /// 定義されていれば展開されて消えている。マクロの表は展開を終えたときのもので、
        /// <c>#ifdef _B</c> の中の <c>#define</c> も条件付きで載っている。
        /// 表に名前があることだけで「宣言されている」とすると、<c>!_B</c> の構成で使っている箇所を見落とす。
        /// </para>
        /// <para>
        /// 関数形式マクロは、括弧を付けずに名前だけを書けば展開されない。それは定義されていても残る。
        /// </para>
        /// </remarks>
        public bool Contains(string name, AnalyzedProgram program, bool invoked)
            => _common.Contains(name)
               || (_compilation.GetHeaderDeclaredNames(program.BlockKey).Contains(name)
                   && !_compilation.LocalOnlyDeclaredNames.Contains(name))
               || (program.Tree.PreprocessResult.Macros.TryGetValue(name, out MacroDefinition? macro)
                   && macro.IsFunctionLike
                   && !invoked)
               || Own(program).Contains(name);

        /// <summary>
        /// そのコードブロックの名前 (シンボル・uniform・このファイルに書いた関数の外の宣言) を返す。
        /// </summary>
        /// <param name="program">木。</param>
        /// <returns>同じコードブロックのどの木かで宣言された名前。</returns>
        /// <remarks>
        /// <b>これらはコードブロックごとにまとめる。</b>
        /// 一部の構成でだけある変数や関数は、HL0315 / HL0311 が構成を添えて報告する。
        /// ここで木ごとに見ると、同じ誤りを 2 つのルールが報告し、並べ方によって報告が変わる。
        /// </remarks>
        private HashSet<string> Own(AnalyzedProgram program)
        {
            if (!_own.TryGetValue(program.BlockKey, out HashSet<string>? names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);

                foreach (AnalyzedProgram tree in _compilation.Programs.Concat(_compilation.SymbolVariants))
                {
                    if (tree.BlockKey != program.BlockKey)
                    {
                        continue;
                    }

                    PreprocessResult result = tree.Tree.PreprocessResult;
                    names.UnionWith(ShaderSymbols.CollectDeclared(result.Pragmas));
                    names.UnionWith(tree.Uniforms.Select(uniform => uniform.Name));
                    AddWrittenDeclarations(_compilation, tree, names);
                }

                _own[program.BlockKey] = names;
            }

            return names;
        }
    }

    /// <summary>このファイルに書かれた宣言の名前を足す。関数の中の変数と仮引数は除く。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象の木。</param>
    /// <param name="declared">足す先。</param>
    private static void AddWrittenDeclarations(ShaderCompilation compilation, AnalyzedProgram program, HashSet<string> declared)
    {
        foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
        {
            if (declaration.FirstToken is not { } first
                || !string.Equals(first.Source.FilePath, compilation.Text.FilePath, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case FunctionDeclarationSyntax function:
                        declared.Add(function.Name);
                        break;

                    case StructDeclarationSyntax structure:
                        declared.Add(structure.Name);
                        break;

                    case ConstantBufferDeclarationSyntax { NameToken: { } bufferName }:
                        declared.Add(bufferName.Text);
                        break;

                    case VariableDeclarationSyntax variable when !IsInsideFunction(variable):
                        foreach (VariableDeclaratorSyntax declarator in variable.Variables)
                        {
                            declared.Add(declarator.Name);
                        }

                        break;

                    default:
                        break;
                }
            }
        }
    }

    /// <summary>変数の宣言が関数の中の変数かを判定する。構造体のフィールドは変数ではない。</summary>
    /// <param name="declaration">対象の宣言。</param>
    /// <returns>関数の中の変数なら <see langword="true"/>。</returns>
    private static bool IsInsideFunction(VariableDeclarationSyntax declaration)
    {
        foreach (SyntaxNode ancestor in declaration.Ancestors())
        {
            if (ancestor is FunctionDeclarationSyntax)
            {
                return true;
            }

            if (ancestor is StructDeclarationSyntax)
            {
                return false;
            }
        }

        return false;
    }
}
