using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.LanguageServer;

/// <summary>呼び出しの形 1 つ分。</summary>
/// <param name="Label">画面に出す形。</param>
/// <param name="Parameters">仮引数の表示名。</param>
internal readonly record struct SignatureInfo(string Label, ImmutableArray<string> Parameters);

/// <summary>呼び出しの途中で見せる案内。</summary>
/// <param name="Signatures">候補となる形。</param>
/// <param name="ActiveParameter">今書いている引数の位置。</param>
internal readonly record struct SignatureHelp(
    ImmutableArray<SignatureInfo> Signatures,
    int ActiveParameter);

/// <summary>
/// 呼び出しの途中で、その関数の形を見せる。
/// </summary>
/// <remarks>
/// <para>
/// <b>条件で形が変わる関数は、条件も添えて並べる。</b>
/// <c>#ifdef</c> で仮引数の型が切り替わる関数では、
/// どちらの形を見ているのかが分からないと書きようがない。
/// </para>
/// <para>
/// 組み込み関数の形は持っていない。名前を知っているだけである。
/// 利用者が宣言した関数だけを見せる。
/// </para>
/// </remarks>
internal static class SignatureHelpBuilder
{
    /// <summary>
    /// カーソルの位置にある呼び出しの案内を組み立てる。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>案内。呼び出しの中でなければ <see langword="null"/>。</returns>
    public static SignatureHelp? Build(ShaderCompilation compilation, int offset)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        if (FindInvocation(compilation, offset) is not { } invocation
            || invocation.TargetName is not { } name)
        {
            return null;
        }

        ConditionMap condition = compilation.GetConditionMap();
        ImmutableArray<SignatureInfo>.Builder signatures =
            ImmutableArray.CreateBuilder<SignatureInfo>();

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                // template の関数 (HLSL 2021) も出す。
                FunctionDeclarationSyntax? function = declaration switch
                {
                    FunctionDeclarationSyntax plain => plain,
                    TemplateDeclarationSyntax { Declaration: FunctionDeclarationSyntax generic } => generic,
                    _ => null,
                };

                if (function is null
                    || function.Name != name
                    || !compilation.IsWrittenHere(function))
                {
                    continue;
                }

                SignatureInfo info = Describe(function, condition.GetCondition(function));

                if (seen.Add(info.Label))
                {
                    signatures.Add(info);
                }
            }
        }

        // 宣言が無ければ、GLSL の組み込み関数の多重定義を出す。
        // 書いた実引数の型と合う形、拡張の型 (16 ビットの型など) を使わない形、拡張の要らない形の順に並べる。
        if (signatures.Count == 0
            && !compilation.Programs.IsEmpty
            && compilation.TypeSystem.ForTree(compilation.Programs[0].Tree) is GlslTypeSystem glsl)
        {
            ExpressionTypeEvaluator evaluator = compilation.GetExpressionTypeBinder(compilation.Programs[0]).GetEvaluatorFor(invocation);
            string?[] arguments = [.. invocation.ArgumentExpressions.Select(evaluator.Evaluate)];

            foreach (GlslBuiltinFunction function in OrderBuiltins(glsl.Builtins.GetFunctions(name), arguments).Take(MaxBuiltinSignatures))
            {
                ImmutableArray<string> parameters = [.. function.Parameters.Select((p, i) => function.WritesBack[i] ? $"out {p}" : p)];
                signatures.Add(new SignatureInfo($"{function.ReturnType} {name}({string.Join(", ", parameters)})", parameters));
            }
        }

        return signatures.Count == 0
            ? null
            : new SignatureHelp(signatures.ToImmutable(), CountCommasBefore(invocation, offset));
    }

    /// <summary>GLSL の組み込み関数の多重定義を、見せる順に並べる。</summary>
    /// <param name="overloads">多重定義。</param>
    /// <param name="argumentTypes">書いた実引数の型。分からないものは <see langword="null"/>。</param>
    /// <returns>実引数の型と同じ仮引数が多い形、拡張の型 (16 ビットの型など) を使わない形、拡張の要らない形の順。</returns>
    internal static IEnumerable<GlslBuiltinFunction> OrderBuiltins(IEnumerable<GlslBuiltinFunction> overloads, IReadOnlyList<string?> argumentTypes)
    {
        int Matches(GlslBuiltinFunction function)
            => Enumerable.Range(0, Math.Min(argumentTypes.Count, function.FixedParameterCount))
                .Count(i => argumentTypes[i] == function.Parameters[i]);

        return overloads
            .OrderByDescending(Matches)
            .ThenBy(f => f.UsesExtensionTypes)
            .ThenBy(f => f.Extensions.Length);
    }

    /// <summary>組み込み関数の多重定義を出す数。16 ビットの型の形などで数十になる関数がある。</summary>
    private const int MaxBuiltinSignatures = 12;

    /// <summary>関数 1 つ分の形を組み立てる。</summary>
    /// <param name="function">対象の関数。</param>
    /// <param name="condition">その関数が存在する条件。</param>
    /// <returns>形。</returns>
    private static SignatureInfo Describe(
        FunctionDeclarationSyntax function,
        SymbolCondition condition)
    {
        ImmutableArray<string> parameters =
        [
            .. function.ParameterList.Select(p =>
                $"{DescribeModifiers(p)}{p.Type.Name} {p.Name}"),
        ];

        string label = $"{function.ReturnType.Name} {function.Name}({string.Join(", ", parameters)})";

        if (function.Parent is TemplateDeclarationSyntax template)
        {
            label = $"template<{string.Join(", ", template.ParameterNames)}> {label}";
        }

        // 条件で形が変わる関数では、どちらを見ているのかが分からないと書きようがない。
        return new SignatureInfo(
            condition.IsAlways ? label : $"{label}  —  #if {condition} のとき",
            parameters);
    }

    /// <summary>仮引数の修飾を表す文字列を返す。</summary>
    /// <param name="parameter">対象の仮引数。</param>
    /// <returns>修飾。無ければ空。</returns>
    private static string DescribeModifiers(ParameterSyntax parameter)
        => parameter.ModifierTokens.IsEmpty
            ? string.Empty
            : string.Join(" ", parameter.ModifierTokens.Select(t => t.Text)) + " ";

    /// <summary>
    /// カーソルを囲む呼び出しを探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>見つかった呼び出し。無ければ <see langword="null"/>。</returns>
    /// <remarks>入れ子の呼び出しでは、最も内側のものを選ぶ。</remarks>
    private static InvocationExpressionSyntax? FindInvocation(
        ShaderCompilation compilation,
        int offset)
    {
        InvocationExpressionSyntax? found = null;

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is not InvocationExpressionSyntax invocation
                    || !compilation.IsWrittenHere(invocation)
                    || offset < invocation.OpenParen.Span.Start
                    || offset > invocation.CloseParen.Span.End)
                {
                    continue;
                }

                if (found is null || invocation.Span.Length <= found.Span.Length)
                {
                    found = invocation;
                }
            }
        }

        return found;
    }

    /// <summary>カーソルまでに書かれたカンマの数を数える。</summary>
    /// <param name="invocation">対象の呼び出し。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>今書いている引数の位置。</returns>
    private static int CountCommasBefore(InvocationExpressionSyntax invocation, int offset)
    {
        int count = 0;

        foreach (ShaderNodeOrTokenEntry item in invocation.Arguments)
        {
            if (item.Token is { } token
                && token.Kind == ShaderSyntaxKind.CommaToken
                && token.Span.End <= offset)
            {
                count++;
            }
        }

        return count;
    }
}
