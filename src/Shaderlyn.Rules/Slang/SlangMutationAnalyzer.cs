using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、書き換えられないメソッドの中でのフィールドへの書き込みを検査する (SG0309)。
/// </summary>
/// <remarks>
/// <para>
/// Slang の構造体 (とクラス) のメソッドは、<c>[mutating]</c> を書かなければ自分 (<c>this</c>) を書き換えられない。
/// フィールドへの代入は「left of '=' is not an l-value」(E30011)、<c>++</c> と複合代入は「argument must be l-value」(E30047) になる (slangc 2026.19 で確かめた)。
/// <c>__init</c> と <c>static</c> のメソッド、<c>static</c> のフィールドは書き換えられる。
/// </para>
/// <para>
/// 見るのは、フィールドの名前 (仮引数や局所変数で隠れていないもの) か <c>this.フィールド</c> から始まり、メンバーを点でたどる書き込みである。
/// 添字は、配列か数のフィールドに直接書いたもの (<c>a[0] = x</c>) だけを見る。資源の要素 (<c>buf[0] = x</c>) とポインターの先 (<c>*p = x</c>) は書き換えではないので見ない。
/// 書き込む値の型が数か、フィールドそのものが数か利用者の構造体のときだけ報告する。GLSL の書き方のファイル (#version のあるもの) は見ない。
/// </para>
/// </remarks>
internal sealed class SlangMutationAnalyzer : SemanticRuleAnalyzer
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
            if (System.Text.RegularExpressions.Regex.IsMatch(program.Tree.Text.ToString(), @"^\s*#\s*version\b", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                continue;
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            ShaderTypeSystem types = binder.TypeSystem;
            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
            Dictionary<ShaderSyntaxToken, int> index = new(ReferenceEqualityComparer.Instance);

            for (int i = 0; i < tokens.Length; i++)
            {
                index[tokens[i]] = i;
            }

            // [[mutating]] (二重の角括弧) は属性の並びに残らないので、メソッドの前のトークンを、前の宣言の終わり (; { }) まで見る。
            bool IsMarkedMutating(FunctionDeclarationSyntax method)
            {
                if (method.FirstToken is not { } first || !index.TryGetValue(first, out int at))
                {
                    return true;
                }

                for (int i = at - 1; i >= 0 && tokens[i].Kind is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken); i--)
                {
                    if (tokens[i].Text is "mutating" or "__mutating")
                    {
                        return true;
                    }
                }

                return false;
            }

            Dictionary<string, StructDeclarationSyntax> userStructs = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax declared in program.Tree.Root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (declared.Keyword.Text is "struct" or "class" && declared.NameToken is { } declaredName)
                {
                    userStructs.TryAdd(declaredName.Text, declared);
                }
            }

            foreach (StructDeclarationSyntax structure in program.Tree.Root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (structure.Keyword.Text is not ("struct" or "class") || structure.NameToken is null)
                {
                    continue;
                }

                // 値を持つフィールド (static とポインターを除く)。
                Dictionary<string, (TypeSyntax Type, bool IsArray)> fields = new(StringComparer.Ordinal);

                foreach (VariableDeclarationSyntax field in structure.Members.OfType<VariableDeclarationSyntax>())
                {
                    if (field.ModifierTokens.Any(t => t.Text is "static" or "const") || field.Type.IsPointer)
                    {
                        continue;
                    }

                    foreach (VariableDeclaratorSyntax declarator in field.Variables)
                    {
                        fields.TryAdd(declarator.Name, (field.Type, declarator.IsArray));
                    }
                }

                if (fields.Count == 0)
                {
                    continue;
                }

                foreach (FunctionDeclarationSyntax method in structure.Members.OfType<FunctionDeclarationSyntax>())
                {
                    if (method.Body is null
                        || method.Name == "__init"
                        || method.ModifierTokens.Any(t => t.Text is "static" or "__ref")
                        || method.Attributes.Any(a => a.Name is "mutating" or "__mutating" or "ref" || a.DescendantTokens().Any(t => t.Text is "mutating" or "__mutating"))
                        || IsMarkedMutating(method))
                    {
                        continue;
                    }

                    foreach (ShaderSyntaxNode node in method.Body.DescendantNodesAndSelf())
                    {
                        (ShaderSyntaxToken at, ExpressionSyntax target)? write = node switch
                        {
                            AssignmentExpressionSyntax assignment => (assignment.OperatorToken, assignment.Left),
                            PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } prefix => (prefix.OperatorToken, prefix.Operand),
                            PostfixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } postfix => (postfix.OperatorToken, postfix.Operand),
                            _ => null,
                        };

                        if (write is not var (at, target)
                            || WrittenField(binder, types, userStructs, fields, target) is not { } name
                            || !compilation.IsReportable(at)
                            || at.GetLocation() is not { } location)
                        {
                            continue;
                        }

                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.NotWritable, location,
                            $"メソッド '{method.Name}' は [mutating] でないので、フィールド '{name}' を書き換えられません。書き換えるなら、メソッドに [mutating] を付けてください。"));
                    }
                }
            }
        }
    }

    /// <summary>書き込む先がこの構造体のフィールドなら、その名前。そう言い切れなければ <see langword="null"/>。</summary>
    private static string? WrittenField(
        ExpressionTypeBinder binder,
        ShaderTypeSystem types,
        Dictionary<string, StructDeclarationSyntax> userStructs,
        Dictionary<string, (TypeSyntax Type, bool IsArray)> fields,
        ExpressionSyntax target)
    {
        ExpressionSyntax current = target;
        bool indexed = false;

        // 根のフィールドから外へたどるメンバーの名前 (inner.v の v)。
        List<string> path = [];

        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                // this.r の r が根のフィールド。
                case MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken, Target: IdentifierExpressionSyntax { Name: "this" } } self:
                    return IsValueField(fields, self.Name, indexed, path, types, userStructs) ? self.Name : null;

                case MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } member:
                    if (indexed)
                    {
                        // 添字の後ろのメンバー (items[0].v) は、何に添字を書いたか決めにくいので見ない。
                        return null;
                    }

                    path.Insert(0, member.Name);
                    current = member.Target;
                    continue;

                case ElementAccessExpressionSyntax element:
                    if (indexed || path.Count > 0)
                    {
                        return null;
                    }

                    indexed = true;
                    current = element.Target;
                    continue;

                case IdentifierExpressionSyntax identifier:
                    return binder.ResolveName(identifier).Kind is DeclaredNameKind.Local or DeclaredNameKind.Undecidable
                           || !IsValueField(fields, identifier.Name, indexed, path, types, userStructs)
                        ? null
                        : identifier.Name;

                default:
                    return null;
            }
        }
    }

    /// <summary>フィールドが値を持ち、その書き込みが自分の書き換えになるか。</summary>
    private static bool IsValueField(
        Dictionary<string, (TypeSyntax Type, bool IsArray)> fields,
        string name,
        bool indexed,
        List<string> path,
        ShaderTypeSystem types,
        Dictionary<string, StructDeclarationSyntax> userStructs)
    {
        if (!fields.TryGetValue(name, out (TypeSyntax Type, bool IsArray) field))
        {
            return false;
        }

        bool numeric = types.TryDescribeNumeric(field.Type.Name, out _);
        bool userStruct = userStructs.ContainsKey(field.Type.Name);

        // 添字は、配列か数 (ベクトル・行列) のフィールドに直接書いたものだけ。資源の要素は書き換えではない。
        if (indexed)
        {
            return field.IsArray ? numeric || userStruct : numeric;
        }

        if (path.Count == 0)
        {
            return numeric || userStruct;
        }

        // メンバーをたどった書き込みは、数の成分 (c.x) か、構造体のフィールドをたどった先の数 (inner.v) だけ (資源のメンバーを持つ構造体を避ける)。
        if (field.IsArray)
        {
            return false;
        }

        string type = field.Type.Name;

        for (int i = 0; i < path.Count; i++)
        {
            if (types.TryDescribeNumeric(type, out _))
            {
                return i == path.Count - 1;
            }

            if (!userStructs.TryGetValue(type, out StructDeclarationSyntax? structure)
                || structure.Members.OfType<VariableDeclarationSyntax>()
                    .FirstOrDefault(m => m.Variables.Any(v => v.Name == path[i])) is not { } member
                || member.ModifierTokens.Any(t => t.Text == "static")
                || member.Type.IsPointer
                || member.Variables.First(v => v.Name == path[i]).IsArray)
            {
                return false;
            }

            type = member.Type.Name;
        }

        return types.TryDescribeNumeric(type, out _);
    }
}
