using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules;

/// <summary>
/// 使っている変数の宣言が、その構成に存在するかを検査する (HL0315)。
/// </summary>
/// <remarks>
/// <para>
/// <b>関数についての HL0311 と同じことを、グローバル変数について見る。</b>
/// 宣言が <c>#ifdef</c> の中にだけあると、名前はどこかの構成で宣言されているので、
/// 「宣言されていない識別子」(HL0310) にはならない。
/// 食い違いは「使う側の条件」と「宣言がある条件」の組み合わせにしか現れない。
/// </para>
/// <code>
/// #ifdef _A
/// float4 _Tint;               // _A のときだけ
/// #endif
/// half4 frag() : SV_Target { return _Tint; }   // 常に使う → !_A のとき宣言が無い
/// </code>
/// <para>
/// <b>対象はグローバル変数 (定数バッファのメンバーを含む) だけである。</b>
/// 名前が何を指すかは、使っている位置で解決する (<see cref="ExpressionTypeBinder.ResolveName"/>)。
/// その位置で局所変数か仮引数を指す名前は見ない。別の関数に同じ名前の局所変数があっても、
/// ここでグローバル変数を指しているなら検査する。
/// 局所変数が一部の構成でしか宣言されていなくても、局所変数の候補があれば見送る。
/// </para>
/// <para>
/// 次の場合は判断を見送る。
/// </para>
/// <list type="bullet">
///   <item><description>依存関係が解決できていない</description></item>
///   <item><description>どの構成でも解析しなかった領域に名前がある。そこで宣言されているかもしれない</description></item>
///   <item><description>
///     宣言がバリアントの木にしか無く、既定の木のどこへ足すかも分からない。その宣言の条件が分からない
///   </description></item>
///   <item><description>使う側か宣言の側の条件を追えなかった</description></item>
/// </list>
/// </remarks>
internal sealed class VariableDeclarationPresenceAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.VariableDeclarationNotPresent];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasCompleteDependencies)
        {
            return;
        }

        ConditionMap condition = compilation.GetConditionMap();

        (Dictionary<(int Start, string? Kernel), HashSet<string>> namesByBlock,
         Dictionary<AnalyzedProgram, Dictionary<string, SymbolCondition>> declaredByTree) =
            CollectGlobalDeclarations(compilation, condition);

        if (namesByBlock.Count == 0)
        {
            return;
        }

        ReportedLocations reported = new();

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not IdentifierExpressionSyntax identifier
                || !namesByBlock.TryGetValue(program.BlockKey, out HashSet<string>? names)
                || !names.Contains(identifier.Name)
                || compilation.UnanalyzedIdentifiers.Contains(identifier.Name)
                || !compilation.IsReportable(identifier))
            {
                continue;
            }

            // その位置で局所変数を指しているなら、このルールの対象ではない。
            // 判断できないなら見送る。別の構成でしか宣言されていない名前は NotFound になるが、それこそが検査の対象である。
            if (compilation.GetExpressionTypeBinder(program).ResolveName(identifier).Kind
                is DeclaredNameKind.Local or DeclaredNameKind.Undecidable)
            {
                continue;
            }

            // その木が表す構成を掛け合わせる (バリアントの木なら、その木を解析した構成も)。
            // 宣言はその木のものと比べる。木が表す構成は木どうしで重ならないので、ほかの木の宣言を足す必要は無い
            // (UndeclaredIdentifierAnalyzer と同じ)。木をまたいで足した条件を否定すると、式が膨れ上がる。
            SymbolCondition use = compilation.GetEffectiveCondition(identifier, program)
                .And(compilation.GetTreeConfiguration(program));
            SymbolCondition declaration =
                declaredByTree.TryGetValue(program, out Dictionary<string, SymbolCondition>? declared)
                && declared.TryGetValue(identifier.Name, out SymbolCondition present)
                    ? present
                    : SymbolCondition.Never;

            if (use.IsUnknown || declaration.IsUnknown)
            {
                continue;
            }

            // 使う条件が成り立ち、かつ宣言がある条件が成り立たない構成があるか。
            SymbolCondition missing = use.And(declaration.Negate());

            if (missing.IsUnknown
                || !compilation.IsPossibleIn(program, missing)
                || identifier.GetLocation() is not { } location
                || !reported.Add(location))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                HlslRuleDescriptors.VariableDeclarationNotPresent,
                location,
                identifier.Name,
                DescribeLeading(condition.Simplify(missing)),
                DescribeTrailing(condition.Simplify(missing))));
        }
    }

    /// <summary>宣言が無い構成を、利用者に読める形にする。</summary>
    /// <param name="missing">宣言が無い構成を表す条件。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeLeading(SymbolCondition missing)
        => missing.IsAlways ? string.Empty : $"構成：{missing} のとき、";

    /// <summary>どの構成でも宣言が無いなら、そのことを文の後ろに添える。</summary>
    /// <param name="missing">宣言が無い構成を表す条件。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeTrailing(SymbolCondition missing)
        => missing.IsAlways ? "どの構成でも" : string.Empty;

    /// <summary>
    /// グローバル変数の名前ごとに「宣言がある条件」を求める。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <returns>
    /// コードブロックごとの、グローバル変数として宣言された名前 (どれかの木で)。
    /// 別の Pass やカーネルの宣言は、そのコードからは見えない (<see cref="AnalyzedProgram.BlockKey"/>)。
    /// それと、木ごとの、名前と、その木で宣言がある条件。条件が分からない名前は <see cref="SymbolCondition.Unknown"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 木ごとに、その木の宣言 (取り込んだヘッダのものも含む) を数える。既定の木には、バリアントの木から足す宣言も数える。
    /// 同じ名前の宣言が複数の条件の下にあるなら、その和が宣言のある条件である。
    /// </para>
    /// <para>
    /// <b>使う側も宣言の側も、木ごとに比べる (使う側にはその木が表す構成を掛ける)。</b>
    /// 以前は既定の木の宣言とバリアントの木から足す宣言だけを数え、バリアントの木にしか無い宣言は条件を分からないとしていた。
    /// 既定の値で読んだ連なりの宣言 (<c>#if LIGHT_SOURCE_MODE == 0</c> の側) は木の中で条件を持たないので、
    /// ほかの値の構成にも宣言があることになるか、その構成の宣言を「無い」と取り違えていた
    /// (Unreal の CapsuleShadowShaders.usf)。
    /// </para>
    /// </remarks>
    private static (Dictionary<(int Start, string? Kernel), HashSet<string>> Names, Dictionary<AnalyzedProgram, Dictionary<string, SymbolCondition>> ByTree) CollectGlobalDeclarations(
        ShaderCompilation compilation,
        ConditionMap condition)
    {
        Dictionary<(int Start, string? Kernel), HashSet<string>> names = [];
        Dictionary<AnalyzedProgram, Dictionary<string, SymbolCondition>> byTree = [];

        void Add(AnalyzedProgram program, VariableDeclarationSyntax declaration, SymbolCondition present)
        {
            if (!names.TryGetValue(program.BlockKey, out HashSet<string>? blockNames))
            {
                blockNames = new HashSet<string>(StringComparer.Ordinal);
                names[program.BlockKey] = blockNames;
            }

            if (!byTree.TryGetValue(program, out Dictionary<string, SymbolCondition>? declared))
            {
                declared = new Dictionary<string, SymbolCondition>(StringComparer.Ordinal);
                byTree[program] = declared;
            }

            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                blockNames.Add(variable.Name);
                declared[variable.Name] = declared.TryGetValue(variable.Name, out SymbolCondition existing)
                    ? existing.Or(present)
                    : present;
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            foreach (VariableDeclarationSyntax declaration in EnumerateGlobalVariables(program.Tree.Root.Declarations))
            {
                Add(program, declaration, condition.GetCondition(declaration));
            }

            foreach (ConditionalNode inserted in condition.GetInsertedChildren(program.Tree.Root))
            {
                if (inserted.Node is DeclarationSyntax root)
                {
                    foreach (VariableDeclarationSyntax declaration in EnumerateGlobalVariables([root]))
                    {
                        Add(program, declaration, condition.GetCondition(declaration));
                    }
                }
            }
        }

        return (names, byTree);
    }

    /// <summary>グローバル変数の宣言を列挙する。定数バッファの中も見る。</summary>
    /// <param name="declarations">最上位の宣言。</param>
    /// <returns>変数の宣言。</returns>
    private static IEnumerable<VariableDeclarationSyntax> EnumerateGlobalVariables(
        IEnumerable<DeclarationSyntax> declarations)
    {
        foreach (DeclarationSyntax declaration in declarations)
        {
            switch (declaration)
            {
                case VariableDeclarationSyntax variable:
                    yield return variable;
                    break;

                case ConstantBufferDeclarationSyntax buffer:
                    foreach (VariableDeclarationSyntax member in EnumerateGlobalVariables(buffer.Members))
                    {
                        yield return member;
                    }

                    break;

                default:
                    break;
            }
        }
    }
}
