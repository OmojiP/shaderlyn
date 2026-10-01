using System.Collections.Immutable;
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

        Dictionary<(int Start, string? Kernel), Dictionary<string, SymbolCondition>> declaredByBlock =
            CollectGlobalDeclarations(compilation, condition);

        if (declaredByBlock.Count == 0)
        {
            return;
        }

        HashSet<int> reported = [];

        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not IdentifierExpressionSyntax identifier
                || !declaredByBlock.TryGetValue(program.BlockKey, out Dictionary<string, SymbolCondition>? declared)
                || !declared.TryGetValue(identifier.Name, out SymbolCondition declaration)
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

            // バリアントの木の中なら、その木を解析した構成も掛け合わせる。
            SymbolCondition use = compilation.GetEffectiveCondition(identifier, program);

            if (use.IsUnknown || declaration.IsUnknown)
            {
                continue;
            }

            // 使う条件が成り立ち、かつ宣言がある条件が成り立たない構成があるか。
            SymbolCondition missing = use.And(declaration.Negate());

            if (missing.IsUnknown
                || !condition.IsPossible(missing)
                || identifier.GetLocation() is not { } location
                || !reported.Add(location.Span.Start))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                HlslRuleDescriptors.VariableDeclarationNotPresent,
                location,
                identifier.Name,
                Describe(condition.Simplify(missing))));
        }
    }

    /// <summary>宣言が無い構成を、利用者に読める形にする。</summary>
    /// <param name="missing">宣言が無い構成を表す条件。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string Describe(SymbolCondition missing)
        => missing.IsAlways ? "どの構成でも" : $"{missing} のとき";

    /// <summary>
    /// グローバル変数の名前ごとに「宣言がある条件」を求める。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <returns>
    /// コードブロックごとの、名前と、その宣言がある条件。条件が分からない名前は <see cref="SymbolCondition.Unknown"/>。
    /// 別の Pass やカーネルの宣言は、そのコードからは見えない (<see cref="AnalyzedProgram.BlockKey"/>)。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 既定の木の宣言 (取り込んだヘッダのものも含む) と、バリアントの木から既定の木へ足す宣言を数える。
    /// 同じ名前の宣言が複数の条件の下にあるなら、その和が宣言のある条件である。
    /// </para>
    /// <para>
    /// <b>バリアントの木にしか無く、足し先も分からない宣言があれば、その名前の条件は分からない。</b>
    /// 数えずに済ませると、その構成の宣言を「無い」と取り違える。
    /// </para>
    /// </remarks>
    private static Dictionary<(int Start, string? Kernel), Dictionary<string, SymbolCondition>> CollectGlobalDeclarations(
        ShaderCompilation compilation,
        ConditionMap condition)
    {
        Dictionary<(int Start, string? Kernel), Dictionary<string, SymbolCondition>> byBlock = [];
        HashSet<((int Start, string? Kernel) Block, string File, int Start)> known = [];

        Dictionary<string, SymbolCondition> For((int Start, string? Kernel) block)
        {
            if (!byBlock.TryGetValue(block, out Dictionary<string, SymbolCondition>? declared))
            {
                declared = new Dictionary<string, SymbolCondition>(StringComparer.Ordinal);
                byBlock[block] = declared;
            }

            return declared;
        }

        void Add((int Start, string? Kernel) block, VariableDeclarationSyntax declaration, SymbolCondition present)
        {
            Dictionary<string, SymbolCondition> declared = For(block);

            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                known.Add((block, variable.NameToken.Source.FilePath, variable.NameToken.Span.Start));

                declared[variable.Name] = declared.TryGetValue(variable.Name, out SymbolCondition existing)
                    ? existing.Or(present)
                    : present;
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            (int Start, string? Kernel) block = program.BlockKey;

            foreach (VariableDeclarationSyntax declaration in EnumerateGlobalVariables(program.Tree.Root.Declarations))
            {
                Add(block, declaration, condition.GetCondition(declaration));
            }

            foreach (ConditionalNode inserted in condition.GetInsertedChildren(program.Tree.Root))
            {
                if (inserted.Node is HlslDeclarationSyntax root)
                {
                    foreach (VariableDeclarationSyntax declaration in EnumerateGlobalVariables([root]))
                    {
                        Add(block, declaration, condition.GetCondition(declaration));
                    }
                }
            }
        }

        // 既定の木にも、足すノードにも無い宣言がバリアントの木にあれば、その名前は条件が分からない。
        foreach (AnalyzedProgram variant in compilation.SymbolVariants)
        {
            (int Start, string? Kernel) block = variant.BlockKey;
            Dictionary<string, SymbolCondition> declared = For(block);

            foreach (VariableDeclarationSyntax declaration in EnumerateGlobalVariables(variant.Tree.Root.Declarations))
            {
                foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                {
                    if (!known.Contains((block, variable.NameToken.Source.FilePath, variable.NameToken.Span.Start)))
                    {
                        declared[variable.Name] = SymbolCondition.Unknown;
                    }
                }
            }
        }

        return byBlock;
    }

    /// <summary>グローバル変数の宣言を列挙する。定数バッファの中も見る。</summary>
    /// <param name="declarations">最上位の宣言。</param>
    /// <returns>変数の宣言。</returns>
    private static IEnumerable<VariableDeclarationSyntax> EnumerateGlobalVariables(
        IEnumerable<HlslDeclarationSyntax> declarations)
    {
        foreach (HlslDeclarationSyntax declaration in declarations)
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
