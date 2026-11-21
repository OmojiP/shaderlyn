using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Godot.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の式を検査する。定数の式 (GD0317)、関数を値として使う (GD0312)、再帰 (GD0332)、整数のベクトルの 0 での割り算 (GD0333)。
/// </summary>
/// <remarks>
/// <para>Godot 4.7.2 で確かめた決まり。</para>
/// <list type="bullet">
/// <item><c>const</c> の初期値には、const でない変数・uniform・varying・利用者の関数の呼び出しを書けない。
/// 組み込みの変数 (<c>TIME</c>) と組み込み関数 (<c>sin</c>) は書ける</item>
/// <item>関数の名前 (利用者の関数と組み込み関数) は、呼び出しの外では使えない</item>
/// <item>関数は自分自身を呼べない (Godot には前方宣言が無いので、再帰はこれだけ)</item>
/// <item>定数の値の作成どうしの整数のベクトルの割り算 (<c>ivec2(4) / ivec2(0)</c>) と剰余 (<c>uvec2</c> を含む) で、右に 0 の成分があれば誤り。
/// スカラーどうし (<c>4 / 0</c>) と <c>uvec</c> の割り算は Godot が何も言わない</item>
/// </list>
/// </remarks>
internal sealed class GodotExpressionAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        GodotRuleDescriptors.ConstantExpressionExpected, GodotRuleDescriptors.FunctionAsValue,
        GodotRuleDescriptors.Recursion, GodotRuleDescriptors.DivisionByZero,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];
        Dictionary<AnalyzedProgram, ExpressionTypeBinder> binders = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, string message)
        {
            if (token is not null
                && compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!binders.TryGetValue(program, out ExpressionTypeBinder? binder))
            {
                binder = binders[program] = compilation.GetExpressionTypeBinder(program);
            }

            CompilationUnitSyntax root = program.Tree.Root;

            switch (node)
            {
                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration, Initializer: { } initializer } when declaration.HasModifier("const"):
                    if (FindNonConstant(initializer, binder, root) is { } found)
                    {
                        Report(GodotRuleDescriptors.ConstantExpressionExpected, found.Token, found.Message);
                    }

                    break;

                case IdentifierExpressionSyntax identifier when IsFunctionName(identifier, binder, root) && !IsCallTarget(identifier):
                    Report(GodotRuleDescriptors.FunctionAsValue, identifier.Identifier, $"関数 '{identifier.Name}' は、呼び出しの外では使用できません。");
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee }
                    when callee.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } function
                         && function.Name == callee.Name
                         && binder.ResolveName(callee).Kind == DeclaredNameKind.NotFound:
                    Report(GodotRuleDescriptors.Recursion, callee.Identifier, $"関数 '{callee.Name}' は自分自身を呼べません (再帰は使用できません)。");
                    break;

                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.SlashToken or ShaderSyntaxKind.PercentToken } binary
                    when IsDivisionByZero(binary, binder):
                    Report(GodotRuleDescriptors.DivisionByZero, binary.OperatorToken, binary.OperatorToken.Kind == ShaderSyntaxKind.SlashToken
                        ? "整数のベクトルを 0 の成分で割っています。"
                        : "整数のベクトルの剰余を 0 の成分で求めています。");
                    break;
            }
        }
    }

    /// <summary>const の初期値の中の、定数でないものを探す。</summary>
    private static (ShaderSyntaxToken Token, string Message)? FindNonConstant(ExpressionSyntax initializer, ExpressionTypeBinder binder, CompilationUnitSyntax root)
    {
        foreach (ShaderSyntaxNode node in initializer.DescendantNodesAndSelf().OfType<ShaderSyntaxNode>())
        {
            switch (node)
            {
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee }
                    when root.Declarations.OfType<FunctionDeclarationSyntax>().Any(f => f.Name == callee.Name)
                         && binder.ResolveName(callee).Kind == DeclaredNameKind.NotFound:
                    return (callee.Identifier, $"定数の初期値では、利用者の関数 '{callee.Name}' を呼べません。");

                case IdentifierExpressionSyntax identifier when !IsCallTarget(identifier):
                    NameResolution resolution = binder.ResolveName(identifier);

                    if (resolution.Kind is DeclaredNameKind.Local or DeclaredNameKind.Global
                        && resolution.Candidates.Length > 0
                        && resolution.Candidates.All(c => !IsConstant(c.Declarator)))
                    {
                        return (identifier.Identifier, $"定数の初期値に、定数でない '{identifier.Name}' は書けません。");
                    }

                    break;
            }
        }

        return null;

        static bool IsConstant(ShaderSyntaxNode declarator) => declarator switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } => declaration.HasModifier("const"),
            ParameterSyntax parameter => parameter.ModifierTokens.Any(t => t.TextIs("const")),
            _ => false,
        };
    }

    /// <summary>名前が関数 (利用者の関数か組み込み関数) を指すか。</summary>
    private static bool IsFunctionName(IdentifierExpressionSyntax identifier, ExpressionTypeBinder binder, CompilationUnitSyntax root)
    {
        if (GodotKeywords.BuiltInTypes.Contains(identifier.Name)
            || identifier.Parent is MemberAccessExpressionSyntax { NameToken: var name } && ReferenceEquals(name, identifier.Identifier)
            || binder.ResolveName(identifier).Kind != DeclaredNameKind.NotFound)
        {
            return false;
        }

        return root.Declarations.OfType<FunctionDeclarationSyntax>().Any(f => f.Name == identifier.Name)
               || (GodotBuiltins.Instance.Functions.ContainsKey(identifier.Name)
                   && !GodotBuiltins.Instance.ShaderTypes.Any(t => GodotBuiltins.Instance.GetVariables(t).Any(v => v.Name == identifier.Name)));
    }

    /// <summary>呼び出しの関数の名前の位置か。</summary>
    private static bool IsCallTarget(IdentifierExpressionSyntax identifier)
        => identifier.Parent is InvocationExpressionSyntax invocation && invocation.Target == identifier;

    /// <summary>定数の整数のベクトルを、0 の成分を持つ定数で割っているか。</summary>
    private static bool IsDivisionByZero(BinaryExpressionSyntax binary, ExpressionTypeBinder binder)
    {
        if (binder.GetTypesByConfiguration(binary) is not [{ Type: { } type }])
        {
            return false;
        }

        bool division = binary.OperatorToken.Kind == ShaderSyntaxKind.SlashToken;

        if (!(type.StartsWith("ivec", StringComparison.Ordinal) || (!division && type.StartsWith("uvec", StringComparison.Ordinal))))
        {
            return false;
        }

        return Components(binary.Left) is not null && Components(binary.Right) is { } right && right.Contains(0);
    }

    /// <summary>リテラルとリテラルだけの値の作成の、整数の成分。定数でなければ <see langword="null"/>。</summary>
    private static ImmutableArray<long>? Components(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Components(parenthesized.Expression);

            case LiteralExpressionSyntax literal:
                return GodotLiterals.TryParseInteger(literal.Token.Text, out long value) ? [value] : null;

            case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: var name } } invocation when GodotKeywords.BuiltInTypes.Contains(name):
                ImmutableArray<long>.Builder values = ImmutableArray.CreateBuilder<long>();

                foreach (ExpressionSyntax argument in invocation.ArgumentExpressions)
                {
                    if (Components(argument) is not { } part)
                    {
                        return null;
                    }

                    values.AddRange(part);
                }

                return values.ToImmutable();

            default:
                return null;
        }
    }
}
