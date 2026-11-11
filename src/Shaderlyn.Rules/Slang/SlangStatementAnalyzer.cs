using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の switch の札 (SG0321) と、static const の初期値の循環 (SG0317) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// SG0321: <c>case</c> と <c>default</c> は、<c>switch</c> の本体の直下にだけ書ける。<c>switch</c> の外と、<c>case</c> の中の <c>{ }</c> の中は誤り
/// (slangc 2026.19 の「'case' is not allowed outside a 'switch' body」)。HLSL の DXC は入れ子のブロックの中の <c>case</c> も読むが、Slang は読まない。
/// </para>
/// <para>
/// SG0317: <c>static const</c> の初期値が、直接か、同じ入れ物 (ファイルの直下か同じ構造体) のほかの <c>static const</c> を通して自分自身を使えば誤り
/// (「the initial-value expression for variable depends on the value of the variable itself」)。
/// </para>
/// </remarks>
internal sealed class SlangStatementAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.SwitchStatement, SlangRuleDescriptors.CyclicConstant, SlangRuleDescriptors.DiscardedResult];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram _) in compilation.EnumerateRuleNodes())
        {
            if (node is SwitchLabelStatementSyntax label && label.Parent is not SwitchStatementSyntax
                && compilation.IsReportable(label.Keyword) && label.Keyword.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.SwitchStatement, location,
                    $"{label.Keyword.Text} は switch の本体の直下にだけ書けます。"));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CheckDiscardedResults(context, compilation, program.Tree.Root);

            CheckConstants(context, compilation, program.Tree.Root.Declarations);

            foreach (StructDeclarationSyntax structure in program.Tree.Root.Declarations.OfType<StructDeclarationSyntax>())
            {
                CheckConstants(context, compilation, structure.Members);
            }
        }
    }

    /// <summary>
    /// [NoDiscard] の関数の戻り値を捨てる式を報告する (SG0338)。文として書いた呼び出し、for の更新の式と、
    /// それが括弧・?: の枝・&amp;&amp; と || の右にあるものを、捨てたものとする。
    /// </summary>
    /// <remarks>同じ名前の宣言 (多重定義) がどれも [NoDiscard] の関数だけを見る。__init は見ない (slangc も見ない)。</remarks>
    private static void CheckDiscardedResults(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, CompilationUnitSyntax root)
    {
        FunctionDeclarationSyntax[] functions = [.. root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>()];

        // void を返す関数の [NoDiscard] は、宣言の誤り (捨てる戻り値が無い)。その呼び出しは捨てたことにしない。
        foreach (FunctionDeclarationSyntax function in functions)
        {
            if (function.Name != "__init"
                && function.ReturnType.Name == "void"
                && !function.ReturnType.IsPointer
                && function.Attributes.FirstOrDefault(a => a.NameToken.Text == "NoDiscard") is { } attribute
                && compilation.IsReportable(function.NameToken)
                && function.NameToken.GetLocation() is { } declared)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.DiscardedResult, declared,
                    $"'{function.Name}' は void を返すので、[NoDiscard] は書けません (捨てる戻り値がありません)。"));
            }
        }

        HashSet<string> marked = [.. functions
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .Where(g => g.Key != "__init" && g.All(f => f.Attributes.Any(a => a.NameToken.Text == "NoDiscard") && f.ReturnType.Name != "void"))
            .Select(g => g.Key)];

        if (marked.Count == 0)
        {
            return;
        }

        void Check(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    Check(parenthesized.Expression);
                    break;

                case ConditionalExpressionSyntax conditional:
                    Check(conditional.WhenTrue);
                    Check(conditional.WhenFalse);
                    break;

                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken } logical:
                    Check(logical.Right);
                    break;

                // a, b は、両方の値を捨てる。
                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.CommaToken } comma:
                    Check(comma.Left);
                    Check(comma.Right);
                    break;

                case InvocationExpressionSyntax call:
                    ShaderSyntaxToken? name = call.Target switch
                    {
                        IdentifierExpressionSyntax identifier => identifier.Identifier,
                        MemberAccessExpressionSyntax member => member.NameToken,
                        _ => null,
                    };

                    if (name is not null && marked.Contains(name.Text) && compilation.IsReportable(name) && name.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.DiscardedResult, location,
                            $"'{name.Text}' は [NoDiscard] の関数なので、戻り値を使ってください (使わないなら、その旨が分かるように変数に受けてください)。"));
                    }

                    break;
            }
        }

        foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ExpressionStatementSyntax statement:
                    Check(statement.Expression);
                    break;

                case ForStatementSyntax loop:
                    foreach (ExpressionSyntax incrementor in loop.Incrementors.Select(i => i.Node).OfType<ExpressionSyntax>())
                    {
                        Check(incrementor);
                    }

                    break;
            }
        }
    }

    /// <summary>同じ入れ物の static const の初期値の依存をたどり、自分自身に戻るものを報告する。</summary>
    private static void CheckConstants(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, ImmutableArray<DeclarationSyntax> declarations)
    {
        Dictionary<string, VariableDeclaratorSyntax> constants = new(StringComparer.Ordinal);

        foreach (VariableDeclarationSyntax declaration in declarations.OfType<VariableDeclarationSyntax>())
        {
            if (declaration.ModifierTokens.Any(t => t.Text == "static") && declaration.ModifierTokens.Any(t => t.Text == "const"))
            {
                foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                {
                    if (declarator.Initializer is not null)
                    {
                        constants.TryAdd(declarator.Name, declarator);
                    }
                }
            }
        }

        if (constants.Count == 0)
        {
            return;
        }

        Dictionary<string, string[]> uses = constants.ToDictionary(
            c => c.Key,
            c => c.Value.Initializer!.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>()
                .Where(i => i.Parent is not MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access || !ReferenceEquals(access.NameToken, i.Identifier))
                .Select(i => i.Name).Where(constants.ContainsKey).Distinct().ToArray(),
            StringComparer.Ordinal);

        foreach ((string name, VariableDeclaratorSyntax declarator) in constants)
        {
            if (!ReachesSelf(uses, name) || !compilation.IsReportable(declarator.NameToken) || declarator.NameToken.GetLocation() is not { } location)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.CyclicConstant, location,
                $"static const の '{name}' の初期値が、自分自身の値に依っています。"));
        }
    }

    private static bool ReachesSelf(Dictionary<string, string[]> uses, string start)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> pending = new(uses[start]);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            if (current == start)
            {
                return true;
            }

            if (seen.Add(current))
            {
                foreach (string next in uses[current])
                {
                    pending.Push(next);
                }
            }
        }

        return false;
    }
}
