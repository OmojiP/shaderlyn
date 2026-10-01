using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;

namespace Shaderlyn.Rules;

/// <summary>
/// 呼んでいる関数の定義が、その構成に存在するかを検査する (HL0311)。
/// </summary>
/// <remarks>
/// <para>
/// <b>これは出現条件を見なければ判定できないルールである。</b>
/// 定義が <c>#ifdef</c> の中にあると、宣言だけを見れば揃っているように見える。
/// シンボルを 1 つずつ有効にした木を別々に見ても、どれかには必ず定義がある。
/// 食い違いは「呼ぶ側の条件」と「定義がある条件」の組み合わせにしか現れない。
/// </para>
/// <para>
/// <b>宣言が見えている関数を対象にする。取り込んだヘッダの宣言も含む。</b>
/// 宣言が見えていて実装がどこにも無いなら、その構成ではリンクできない。
/// 宣言をこのファイルに書いたかどうかは、その判断と関係がない。
/// </para>
/// <para>
/// <b>組み込み関数は対象外である。</b>
/// どこにも宣言が無いので、そもそもこの表に載らない。
/// 載せてしまうと、あらゆる呼び出しが「定義が無い」になる。
/// </para>
/// <para>
/// 報告するのは、利用者がこのファイルに書いた呼び出しだけである。
/// ヘッダの中の呼び出しは直せないので報告しない。
/// </para>
/// </remarks>
internal sealed class FunctionDefinitionAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.FunctionDefinitionNotFound];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ConditionMap condition = compilation.GetConditionMap();

        // コードブロックごとに、名前ごとの「定義がある条件」を求める。
        // 同じ名前の定義が複数の条件の下にあるなら、その和が定義のある条件である。
        // 別の Pass やカーネルの定義は、その呼び出しからは見えない。
        Dictionary<(int Start, string? Kernel), Dictionary<string, SymbolCondition>> definedByBlock = [];

        foreach (IGrouping<(int Start, string? Kernel), AnalyzedProgram> block in EnumeratePrograms(compilation).GroupBy(p => p.BlockKey))
        {
            HashSet<string> declared = CollectDeclared(block);

            if (declared.Count > 0)
            {
                definedByBlock[block.Key] = CollectDefinitionConditions(block, condition, declared);
            }
        }

        if (definedByBlock.Count == 0)
        {
            return;
        }

        // 同じコードが Pass ごとに解析されるため、同じ位置を重ねて報告しない。
        HashSet<int> reported = [];

        foreach (AnalyzedProgram program in EnumeratePrograms(compilation))
        {
            if (!definedByBlock.TryGetValue(program.BlockKey, out Dictionary<string, SymbolCondition>? defined))
            {
                continue;
            }

            foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                if (!compilation.IsReportable(declaration))
                {
                    continue;
                }

                foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
                {
                    if (node is InvocationExpressionSyntax invocation)
                    {
                        AnalyzeCall(
                            context,
                            compilation,
                            condition,
                            defined,
                            invocation,
                            program.AnalyzedCondition,
                            reported);
                    }
                }
            }
        }
    }

    /// <summary>呼び出し 1 つ分を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="defined">名前ごとの、定義がある条件。</param>
    /// <param name="invocation">検査する呼び出し。</param>
    /// <param name="analyzed">この木を解析した構成の条件。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeCall(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ConditionMap condition,
        Dictionary<string, SymbolCondition> defined,
        InvocationExpressionSyntax invocation,
        SymbolCondition analyzed,
        HashSet<int> reported)
    {
        if (invocation.TargetName is not { } name
            || !defined.TryGetValue(name, out SymbolCondition definition)
            || !compilation.IsReportable(invocation))
        {
            return;
        }

        // バリアントの木では、条件が付いていないことは「どの構成でも」を意味しない。
        SymbolCondition call = condition.GetCondition(invocation).And(analyzed);

        // 条件が分かっていないなら報告しない。
        // 分からないものを根拠に「定義が無い」と言ってはならない。
        if (call.IsUnknown || definition.IsUnknown)
        {
            return;
        }

        // 呼ぶ条件が成り立ち、かつ定義がある条件が成り立たない構成があるか。
        SymbolCondition missing = call.And(definition.Negate());

        if (missing.IsUnknown || !condition.IsPossible(missing))
        {
            return;
        }

        if (invocation.GetLocation() is not { } location || !reported.Add(location.Span.Start))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.FunctionDefinitionNotFound,
            location,
            name,
            Describe(missing)));
    }

    /// <summary>定義が無い構成を、利用者に読める形にする。</summary>
    /// <param name="missing">定義が無い構成を表す条件。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string Describe(SymbolCondition missing)
        => missing.IsAlways ? "どの構成でも" : $"{missing} のとき";

    /// <summary>
    /// 宣言が見えている関数の名前を集める。
    /// </summary>
    /// <param name="programs">1 つのコードブロックの、既定の構成とバリアントの木。</param>
    /// <returns>関数の名前。</returns>
    /// <remarks>
    /// 宣言だけの形 (プロトタイプ) も定義も、どちらもここに含める。
    /// 「実装があるかどうかを問える関数か」を決めるためのものである。
    /// 組み込み関数はここに載らないので、自然に対象外になる。
    /// </remarks>
    private static HashSet<string> CollectDeclared(IEnumerable<AnalyzedProgram> programs)
    {
        HashSet<string> declared = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in programs)
        {
            foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                if (declaration is FunctionDeclarationSyntax function
                    && !function.NameToken.IsMissing)
                {
                    declared.Add(function.Name);
                }
            }
        }

        return declared;
    }

    /// <summary>
    /// 名前ごとに「定義がある条件」を求める。
    /// </summary>
    /// <param name="programs">1 つのコードブロックの、既定の構成とバリアントの木。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="names">対象の名前。</param>
    /// <returns>名前と、その定義がある条件。</returns>
    /// <remarks>
    /// <para>
    /// <b>取り込んだヘッダの定義も数える。</b>
    /// このファイルが宣言だけを書き、定義はヘッダにある形は正しい。
    /// </para>
    /// <para>
    /// 定義が 1 つも無い名前は「決して成り立たない」を持つ。
    /// これで「どこにも定義が無い」も同じ判定に載る。
    /// </para>
    /// </remarks>
    private static Dictionary<string, SymbolCondition> CollectDefinitionConditions(
        IEnumerable<AnalyzedProgram> programs,
        ConditionMap condition,
        HashSet<string> names)
    {
        Dictionary<string, SymbolCondition> defined = new(StringComparer.Ordinal);

        foreach (string name in names)
        {
            defined[name] = SymbolCondition.Never;
        }

        foreach (AnalyzedProgram program in programs)
        {
            foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                if (declaration is not FunctionDeclarationSyntax { IsDefinition: true } function
                    || !names.Contains(function.Name))
                {
                    continue;
                }

                defined[function.Name] = defined[function.Name].Or(condition.GetCondition(function));
            }
        }

        return defined;
    }

    /// <summary>検査の対象になるコードブロックを列挙する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>既定の構成と、まとめられなかったシンボルのバリアント。</returns>
    /// <remarks>
    /// まとめられなかった <c>#ifdef</c> の中の定義はバリアントの木にしか無い。
    /// 既定の木だけを見ると、その定義を「無い」と数えてしまう。
    /// </remarks>
    private static IEnumerable<AnalyzedProgram> EnumeratePrograms(ShaderCompilation compilation)
        => compilation.Programs.Concat(compilation.SymbolVariants);
}
