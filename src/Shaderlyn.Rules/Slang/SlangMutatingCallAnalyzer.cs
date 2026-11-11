using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、書き換えられない値への <c>[mutating]</c> のメソッドの呼び出しを検査する (SG0309)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>[mutating]</c> でないメソッドの中の、同じ型の <c>[mutating]</c> のメソッドの修飾しない呼び出し (自分は書き換えられない。E30050)</item>
/// <item>定数バッファーのメンバー・関数の外の static でない変数 (uniform)・<c>ParameterBlock&lt;T&gt;</c> と <c>ConstantBuffer&lt;T&gt;</c> の値への、<c>[mutating]</c> のメソッドの呼び出し (E30050)</item>
/// </list>
/// </remarks>
internal sealed class SlangMutatingCallAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.NotWritable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;
            Dictionary<string, HashSet<string>> mutating = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> other = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax structure in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (structure.NameToken is not { } name)
                {
                    continue;
                }

                foreach (FunctionDeclarationSyntax method in structure.Members.OfType<FunctionDeclarationSyntax>())
                {
                    bool isMutating = method.Attributes.Any(a => a.Name == "mutating") || method.ModifierTokens.Any(t => t.Text == "mutating");
                    Dictionary<string, HashSet<string>> target = isMutating ? mutating : other;

                    if (!target.TryGetValue(name.Text, out HashSet<string>? set))
                    {
                        target[name.Text] = set = new HashSet<string>(StringComparer.Ordinal);
                    }

                    set.Add(method.Name);
                }
            }

            if (mutating.Count == 0)
            {
                continue;
            }

            // 多重定義で [mutating] でないものもある名前は、どちらが選ばれるか決めない。
            foreach ((string type, HashSet<string> names) in mutating)
            {
                if (other.TryGetValue(type, out HashSet<string>? plain))
                {
                    names.ExceptWith(plain);
                }
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            void Report(ShaderSyntaxToken at, string message)
            {
                if (compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.NotWritable, location, message));
                }
            }

            foreach (InvocationExpressionSyntax call in root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                switch (call.Target)
                {
                    // [mutating] でないメソッドの中の increment()
                    case IdentifierExpressionSyntax { Name: var name } identifier
                        when call.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { Parent: StructDeclarationSyntax { NameToken: { } owner } } method
                             && mutating.TryGetValue(owner.Text, out HashSet<string>? own) && own.Contains(name)
                             && !method.Attributes.Any(a => a.Name == "mutating") && !method.ModifierTokens.Any(t => t.Text is "mutating" or "static")
                             && method.Name != "__init"
                             && binder.ResolveName(identifier).Kind == DeclaredNameKind.NotFound:
                        Report(identifier.Identifier, $"[mutating] でないメソッド '{method.Name}' の中では、自分を書き換える [mutating] のメソッド '{name}' を呼べません。");
                        break;

                    // gCounter.increment() (定数バッファーのメンバー・uniform・ParameterBlock の値)
                    case MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken, Target: IdentifierExpressionSyntax receiver } access
                        when binder.ResolveName(receiver).Candidates is [{ Declarator: var declarator }]
                             && DescribeImmutable(declarator) is ({ } what, { } typeName)
                             && mutating.TryGetValue(typeName, out HashSet<string>? methods) && methods.Contains(access.Name):
                        Report(access.NameToken, $"{what} '{receiver.Name}' は書き換えられないので、[mutating] のメソッド '{access.Name}' を呼べません。");
                        break;
                }
            }
        }
    }

    /// <summary>書き換えられない値の説明と、その値の型の名前。書き換えられるか、分からなければ (null, null)。</summary>
    private static (string? What, string? Type) DescribeImmutable(ShaderSyntaxNode declarator)
    {
        (TypeSyntax? type, string? what) = declarator switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: ConstantBufferDeclarationSyntax } declaration } => (declaration.Type, "定数バッファーのメンバー"),
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: CompilationUnitSyntax or NamespaceDeclarationSyntax } declaration }
                when !declaration.ModifierTokens.Any(t => t.Text is "static" or "groupshared") => (declaration.Type, "uniform の値"),
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } => (declaration.Type, null),
            ParameterSyntax parameter => (parameter.Type, null),
            _ => (null, null),
        };

        if (type is null)
        {
            return (null, null);
        }

        // ParameterBlock<T> と ConstantBuffer<T> の値は、どこにあっても書き換えられない。
        if (type.NameToken.Text is "ParameterBlock" or "ConstantBuffer" && type.TemplateArguments is [{ Kind: ShaderSyntaxKind.IdentifierToken } element])
        {
            return ($"{type.NameToken.Text} の値", element.Text);
        }

        return what is null || type.OpenAngle is not null ? (null, null) : (what, type.NameToken.Text);
    }
}
