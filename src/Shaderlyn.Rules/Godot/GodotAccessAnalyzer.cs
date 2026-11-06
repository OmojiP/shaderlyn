using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の、書き込めない値への書き込み (GD0309) と、out / inout の仮引数に渡せない値 (GD0306) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// Godot 4.7.2 の <c>ShaderLanguage::_validate_assign</c> と、関数の呼び出しの out の実引数の検査を写した。
/// 代入 (<c>=</c>、<c>+=</c> など) と <c>++</c> / <c>--</c> の左は、添字とメンバーを外した元の名前で決まる。
/// <list type="bullet">
/// <item>uniform と const の変数 (関数の外・関数の中・<c>const</c> の仮引数) には書き込めない</item>
/// <item>組み込みの変数は、その関数で書き込めないもの (<c>TIME</c>、<c>fragment</c> の <c>VERTEX</c>、<c>VIEWPORT_SIZE</c> など) に書き込めない</item>
/// <item>関数の呼び出しの結果と、同じ成分を 2 度選んだスウィズル (<c>v.xx</c>) には書き込めない</item>
/// </list>
/// varying への書き込みは、ステージの規則が別にあるので、ここでは見ない。
/// </para>
/// <para>
/// 利用者の関数の out / inout の仮引数 (<c>const</c> の仮引数でないもの) には、リテラル・式・関数の呼び出しの結果・const の変数・uniform・
/// 書き込めない組み込みを渡せない。ベクトルと行列の添字 (<c>v[0]</c>) は Godot では演算なので渡せない (配列の要素 <c>a[0]</c> は渡せる)。
/// メンバー (<c>v.x</c>) は、元の変数が const のときだけ渡せない。組み込み関数の out の仮引数 (<c>modf</c> の 2 番目など) も同じに扱う。
/// </para>
/// </remarks>
internal sealed class GodotAccessAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>スウィズルの成分の文字。</summary>
    private const string SwizzleLetters = "xyzwrgbastpq";

    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GodotRuleDescriptors.ReadOnlyTarget, GodotRuleDescriptors.InvalidOutArgument];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];
        Dictionary<AnalyzedProgram, ExpressionTypeBinder> binders = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken token, string message)
        {
            if (compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            ExpressionSyntax? target = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } prefix => prefix.Operand,
                PostfixUnaryExpressionSyntax postfix => postfix.Operand,
                _ => null,
            };

            if (target is null && node is not InvocationExpressionSyntax)
            {
                continue;
            }

            if (compilation.TypeSystem.ForTree(program.Tree) is not GodotTypeSystem types
                || node.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is not { } function)
            {
                continue;
            }

            if (!binders.TryGetValue(program, out ExpressionTypeBinder? binder))
            {
                binder = binders[program] = compilation.GetExpressionTypeBinder(program);
            }

            Access access = new(binder, types, function.Name);

            if (target is not null)
            {
                if (access.FindReadOnly(target) is { } readOnly)
                {
                    Report(GodotRuleDescriptors.ReadOnlyTarget, readOnly.Token, readOnly.Message);
                }
            }
            else if (node is InvocationExpressionSyntax invocation)
            {
                CheckOutArguments(invocation, program.Tree.Root, access, Report);
            }
        }
    }

    /// <summary>out / inout の仮引数に渡した実引数を調べる。</summary>
    private static void CheckOutArguments(
        InvocationExpressionSyntax invocation,
        CompilationUnitSyntax root,
        Access access,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        if (invocation.Target is not IdentifierExpressionSyntax callee)
        {
            return;
        }

        ImmutableArray<ExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];

        // 利用者の関数。同じ名前の多重定義があれば、実引数の数の合うものがすべて同じ修飾子のときだけ調べる。
        FunctionDeclarationSyntax[] functions = [.. root.Declarations.OfType<FunctionDeclarationSyntax>()
            .Where(f => f.Name == callee.Name && f.ParameterList.Count() == arguments.Length)];

        if (functions.Length > 0)
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                string[] qualifiers = [.. functions.Select(f => QualifierOf(f.ParameterList.ElementAt(i))).Distinct()];

                if (qualifiers is ["out" or "inout"] && access.FindNotWritable(arguments[i]) is { } token)
                {
                    report(GodotRuleDescriptors.InvalidOutArgument, token,
                        $"{qualifiers[0]} の仮引数には、書き込める変数を渡します (定数・uniform・式は渡せません)。");
                }
            }

            return;
        }

        // 組み込み関数の out の仮引数 (modf の 2 番目など)。
        if (GodotBuiltins.Instance.OutArguments.TryGetValue(callee.Name, out ImmutableArray<int> outs))
        {
            foreach (int index in outs.Where(i => i < arguments.Length))
            {
                if (access.FindNotWritable(arguments[index]) is { } token)
                {
                    report(GodotRuleDescriptors.InvalidOutArgument, token,
                        $"組み込み関数 '{callee.Name}' の {index + 1} 番目の実引数は out なので、書き込める変数を渡します。");
                }
            }
        }

        static string QualifierOf(ParameterSyntax parameter)
            => parameter.ModifierTokens.Any(t => t.TextIs("const")) ? "const"
               : parameter.ModifierTokens.FirstOrDefault(t => t.Text is "out" or "inout")?.Text ?? "in";
    }

    /// <summary>関数の中の、名前の書き込めるかどうかを調べる。</summary>
    private sealed class Access(ExpressionTypeBinder binder, GodotTypeSystem types, string function)
    {
        /// <summary>代入の左を調べ、書き込めなければ、報告する位置と文を返す。</summary>
        public (ShaderSyntaxToken Token, string Message)? FindReadOnly(ExpressionSyntax target)
        {
            while (true)
            {
                switch (target)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        target = parenthesized.Expression;
                        continue;

                    case ElementAccessExpressionSyntax element:
                        target = element.Target;
                        continue;

                    case MemberAccessExpressionSyntax member:
                        if (HasDuplicateSwizzle(member))
                        {
                            return (member.NameToken, $"同じ成分を 2 度選んだスウィズル '{member.Name}' には書き込めません。");
                        }

                        target = member.Target;
                        continue;

                    case InvocationExpressionSyntax invocation when invocation.Target.FirstToken is { } first:
                        return (first, "関数の呼び出しの結果には書き込めません。");

                    case IdentifierExpressionSyntax identifier:
                        return KindOf(identifier) switch
                        {
                            NameKind.Uniform => (identifier.Identifier, $"uniform '{identifier.Name}' には書き込めません。"),
                            NameKind.Constant => (identifier.Identifier, $"定数 '{identifier.Name}' は書き換えられません。"),
                            NameKind.ReadOnlyBuiltin => (identifier.Identifier, $"組み込みの '{identifier.Name}' は、{function} 関数の中では書き換えられません。"),
                            _ => null,
                        };

                    default:
                        return null;
                }
            }
        }

        /// <summary>out の実引数が書き込めるものかを調べ、書き込めなければ報告する位置を返す。</summary>
        public ShaderSyntaxToken? FindNotWritable(ExpressionSyntax argument)
        {
            switch (argument)
            {
                // 値 (リテラル・演算・呼び出し・値の作成) は渡せない。
                case LiteralExpressionSyntax or BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                    or ConditionalExpressionSyntax or InvocationExpressionSyntax or AssignmentExpressionSyntax or CastExpressionSyntax:
                    return argument.FirstToken;

                case ParenthesizedExpressionSyntax parenthesized:
                    return FindNotWritable(parenthesized.Expression);

                case IdentifierExpressionSyntax identifier:
                    return KindOf(identifier) is NameKind.Uniform or NameKind.Constant or NameKind.ReadOnlyBuiltin ? identifier.Identifier : null;

                // 配列の変数の要素 (a[0]) は、配列が const か uniform なら渡せない。
                // ベクトルと行列の添字 (v[0]) は Godot では演算なので渡せない。
                case ElementAccessExpressionSyntax { Target: IdentifierExpressionSyntax array }:
                    NameResolution resolution = binder.ResolveName(array);

                    if (resolution.Kind is not (DeclaredNameKind.Local or DeclaredNameKind.Global) || resolution.Candidates.IsEmpty)
                    {
                        return null;
                    }

                    if (resolution.Candidates.All(c => !c.Type.IsArray))
                    {
                        return argument.FirstToken;
                    }

                    return resolution.Candidates.All(c => c.Type.IsArray) && KindOf(array) is NameKind.Constant or NameKind.Uniform
                        ? array.Identifier
                        : null;

                // メンバーとスウィズル (v.x、s.a) は、元の変数が const なら渡せない (uniform は渡せる)。
                case MemberAccessExpressionSyntax member:
                    ExpressionSyntax owner = member.Target;

                    while (owner is MemberAccessExpressionSyntax inner)
                    {
                        owner = inner.Target;
                    }

                    return owner is IdentifierExpressionSyntax name && KindOf(name) is NameKind.Constant or NameKind.ReadOnlyBuiltin
                        ? name.Identifier
                        : null;

                default:
                    return null;
            }
        }

        /// <summary>名前が指すものの、書き込みに関わる種類。</summary>
        private NameKind KindOf(IdentifierExpressionSyntax identifier)
        {
            NameResolution resolution = binder.ResolveName(identifier);

            switch (resolution.Kind)
            {
                case DeclaredNameKind.Local or DeclaredNameKind.Global:
                    NameKind[] kinds = [.. resolution.Candidates.Select(c => KindOf(c.Declarator)).Distinct()];
                    return kinds is [var single] ? single : NameKind.Other;

                case DeclaredNameKind.NotFound:
                    return types.IsBuiltinVariableReadOnly(identifier.Name, function) ? NameKind.ReadOnlyBuiltin : NameKind.Other;

                default:
                    return NameKind.Other;
            }
        }

        private static NameKind KindOf(ShaderSyntaxNode declarator) => declarator switch
        {
            ParameterSyntax parameter when parameter.ModifierTokens.Any(t => t.TextIs("const")) => NameKind.Constant,
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } when declaration.HasModifier("const") => NameKind.Constant,
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } when declaration.HasModifier("uniform") => NameKind.Uniform,
            _ => NameKind.Other,
        };

        /// <summary>同じ成分を 2 度選んだスウィズルか (<c>v.xx</c>)。</summary>
        private bool HasDuplicateSwizzle(MemberAccessExpressionSyntax member)
        {
            string name = member.Name;

            return name.Length > 1
                   && name.All(c => SwizzleLetters.Contains(c, StringComparison.Ordinal))
                   && name.Distinct().Count() < name.Length
                   && binder.GetTypesByConfiguration(member.Target) is [{ Type: { } type }]
                   && type.Contains("vec", StringComparison.Ordinal);
        }
    }

    /// <summary>名前が指すものの種類。</summary>
    private enum NameKind
    {
        /// <summary>書き込める (か、分からない)。</summary>
        Other,

        /// <summary>uniform。</summary>
        Uniform,

        /// <summary>const の変数か仮引数。</summary>
        Constant,

        /// <summary>その関数で書き込めない組み込み。</summary>
        ReadOnlyBuiltin,
    }
}
