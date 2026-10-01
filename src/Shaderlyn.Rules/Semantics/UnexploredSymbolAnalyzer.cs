using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules;

/// <summary>
/// 展開しきれなかったシンボルの経路があることを報告する (SL0003)。
/// </summary>
/// <remarks>
/// <para>
/// <b>「調べていない」ことを伝えないままにしてはならない。</b>
/// シンボルは C# からも切り替えられるため、宣言された経路はどれもいつか通る。
/// 上限を超えて展開しなかった経路のコードは、一度も読んでいない。
/// そこに構文エラーがあっても、そこでしか使われないプロパティがあっても、何も出ない。
/// </para>
/// <para>
/// 指摘が出ないことを「問題が無い」と受け取られるのは、
/// 誤検出よりも発見が遅れ、結果として高くつく。
/// </para>
/// <para>
/// <b>調べなかった条件の位置に、シンボルごとに出す。</b>
/// ファイルの先頭に 1 件だけ出すと、どの <c>#ifdef</c> の中が読まれていないのかを
/// 利用者が名前から探すことになる。条件の行に出せば、その場で抑制コメントも書ける。
/// </para>
/// <para>
/// <b>組として同時に有効にする構成は、組ごとに出す。</b>
/// 組の中のシンボルは 1 つずつなら調べていることがある。
/// シンボルごとの指摘にすると、読んでいる分岐まで読んでいないように伝わる。
/// </para>
/// </remarks>
internal sealed class UnexploredSymbolAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SemanticRuleDescriptors.UnexploredSymbols];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ReportSymbols(context, compilation);
        ReportCombinations(context, compilation);
    }

    /// <summary>1 つだけ有効にする構成を作らなかったシンボルを報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    private static void ReportSymbols(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (compilation.UnexploredSymbols.IsEmpty)
        {
            return;
        }

        HashSet<string> unexplored = new(compilation.UnexploredSymbols, StringComparer.Ordinal);
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (HlslSyntaxToken token in program.Tree.PreprocessResult.ConditionalIdentifiers)
            {
                // 同じシンボルを見ている条件が複数あっても、最初の 1 か所で足りる。
                // 展開しなかったのはシンボルであって、条件の行ではない。
                if (!unexplored.Contains(token.Text)
                    || !compilation.IsWrittenHere(token)
                    || !reported.Add(token.Text))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    SemanticRuleDescriptors.UnexploredSymbols, token.GetLocation(), DescribeSymbol(token.Text)));
            }
        }

        // 条件がヘッダにしか無いシンボルは、このファイルで宣言した #pragma の行に出す。
        // .shader で宣言して共通の .hlsl の機能だけを切り替える書き方では、このファイルに条件が無い。
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (HlslSyntaxToken token in ShaderSymbols.EnumerateDeclared(program.Tree.PreprocessResult.Pragmas))
            {
                if (!unexplored.Contains(token.Text)
                    || !compilation.IsWrittenHere(token)
                    || !reported.Add(token.Text))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    SemanticRuleDescriptors.UnexploredSymbols, token.GetLocation(), DescribeSymbol(token.Text)));
            }

            // #pragma multi_compile_instancing のような組み込みの宣言は、シンボルの名前を書かない。宣言の名前に出す。
            foreach ((HlslSyntaxToken declaration, string symbol) in
                ShaderSymbols.EnumerateBuiltInDeclared(program.Tree.PreprocessResult.Pragmas))
            {
                if (!unexplored.Contains(symbol)
                    || !compilation.IsWrittenHere(declaration)
                    || !reported.Add(symbol))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    SemanticRuleDescriptors.UnexploredSymbols, declaration.GetLocation(), DescribeSymbol(symbol)));
            }
        }

        // 宣言も条件もこのファイルに無いシンボルは、今までどおりファイルの先頭に出す。
        // 何も報告しないよりは、どこか 1 か所に出すほうがよい。
        foreach (string symbol in compilation.UnexploredSymbols.Where(s => !reported.Contains(s)))
        {
            context.ReportDiagnostic(
                SemanticRuleDescriptors.UnexploredSymbols,
                new Core.Text.TextSpan(0, 0),
                DescribeSymbol(symbol));
        }
    }

    /// <summary>同時に有効にする構成を作らなかったシンボルの組を報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <remarks>
    /// <para>
    /// 位置は、その組でしか通らない分岐を始めた指令である。
    /// <c>#ifdef _A</c> の中の <c>#ifdef _B</c> なら、内側の <c>#ifdef</c> になる。
    /// </para>
    /// <para>
    /// <b>取り込んだヘッダに書かれた条件は、ヘッダの位置を指さない。</b>
    /// 指摘は解析しているファイルに出すものなので、条件がこのファイルの外にあるときは、
    /// シンボルごとの報告と同じようにファイルの先頭へ出す。
    /// </para>
    /// </remarks>
    private static void ReportCombinations(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        foreach (SymbolCombination combination in compilation.UnexploredSymbolCombinations)
        {
            if (!string.Equals(combination.Location.FilePath, compilation.Text.FilePath, StringComparison.Ordinal))
            {
                context.ReportDiagnostic(
                    SemanticRuleDescriptors.UnexploredSymbols,
                    new Core.Text.TextSpan(0, 0),
                    DescribeCombination(combination.Symbols));
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                SemanticRuleDescriptors.UnexploredSymbols,
                combination.Location,
                DescribeCombination(combination.Symbols)));
        }
    }

    /// <summary>シンボル 1 つ分のメッセージの引数を作る。</summary>
    /// <param name="symbol">シンボル。</param>
    /// <returns>メッセージの引数。</returns>
    private static string[] DescribeSymbol(string symbol) => [$"'{symbol}'", string.Empty, "このシンボル"];

    /// <summary>組 1 つ分のメッセージの引数を作る。</summary>
    /// <param name="combination">組。</param>
    /// <returns>メッセージの引数。</returns>
    private static string[] DescribeCombination(ImmutableArray<string> combination)
        => [string.Join(" と ", combination.Select(s => $"'{s}'")), "同時に", "この組み合わせ"];
}
