using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、微分の型 (<c>IDifferentiable</c> の型の <c>Differential</c>) と <c>[DerivativeMember]</c> の書き方を検査する (SG0361)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>typealias Differential = D;</c> の D が自分でないのに、微分するフィールドに <c>[DerivativeMember]</c> も <c>no_diff</c> も書いていない (E30121。D に同じ名前のフィールドがあっても当てない)</item>
/// <item><c>[DerivativeMember(D.m)]</c> の m が D のフィールドでない (E31130)。指したフィールドの型が微分しない型 (int など。E30310)</item>
/// <item>関数の外の変数の <c>[DerivativeMember]</c> (E31132。型のメンバーにだけ書ける)</item>
/// </list>
/// 見るのは、微分するかどうかが型の名前で決まるフィールド (float・half・double とそのベクトル・行列と、その配列) と、ファイルの中の構造体の D だけである。
/// 構文木はフィールドの属性と、型の中の typealias を残さないので、トークンの並びを読む。
/// </remarks>
internal sealed partial class SlangDifferentialTypeAnalyzer : SemanticRuleAnalyzer
{
    [GeneratedRegex(@"^(float|half|double)([1-4](x[1-4])?)?$")]
    private static partial Regex DifferentiableType();

    [GeneratedRegex(@"^(u?int|bool|u?int(8|16|32|64)_t)([1-4](x[1-4])?)?$")]
    private static partial Regex PlainType();

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.DifferentialType];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;

            if (!tokens.Any(t => t.Text is "IDifferentiable" or "DerivativeMember"))
            {
                continue;
            }

            CompilationUnitSyntax root = program.Tree.Root;
            Dictionary<ShaderSyntaxToken, int> index = new(ReferenceEqualityComparer.Instance);

            for (int i = 0; i < tokens.Length; i++)
            {
                index[tokens[i]] = i;
            }

            void Report(ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.DifferentialType, location, message));
                }
            }

            StructDeclarationSyntax[] structs = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>().Where(s => s.NameToken is not null)];
            Dictionary<string, StructDeclarationSyntax?> byName = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax s in structs)
            {
                byName[s.Name] = byName.ContainsKey(s.Name) ? null : s;
            }

            // IDifferentiable を継承するインターフェイス (ファイルの中のもの)。
            HashSet<string> differentiableInterfaces = new(StringComparer.Ordinal) { "IDifferentiable" };
            bool grew = true;

            while (grew)
            {
                grew = false;

                foreach (StructDeclarationSyntax s in structs.Where(s => s.Keyword.Text == "interface"))
                {
                    if (!differentiableInterfaces.Contains(s.Name) && Bases(s, tokens, index).Any(differentiableInterfaces.Contains))
                    {
                        grew |= differentiableInterfaces.Add(s.Name);
                    }
                }
            }

            foreach (StructDeclarationSyntax type in structs)
            {
                if (type.Keyword.Text is not ("struct" or "class") || !index.TryGetValue(type.OpenBrace, out int open) || !index.TryGetValue(type.CloseBrace, out int close))
                {
                    continue;
                }

                bool differentiable = Bases(type, tokens, index).Any(differentiableInterfaces.Contains);

                // 値の微分 (IDifferentiable) とポインターの微分 (IDifferentiablePtrType) の両方には適合できない (E30311)。
                if (differentiable && Bases(type, tokens, index).Contains("IDifferentiablePtrType") && type.NameToken is { } bothName)
                {
                    Report(bothName, $"{type.Name} は、IDifferentiable と IDifferentiablePtrType のどちらか一方にだけ適合させてください。");
                }

                // typealias Differential = D; (型の直下)
                string? differentialName = DifferentialName(tokens, open, close);

                StructDeclarationSyntax? differential = differentialName is not null && byName.TryGetValue(differentialName, out StructDeclarationSyntax? d) ? d : null;

                // 微分の型 D の微分は D でなければならない (D の typealias Differential が別の型。E30096)。
                if (differentiable && differential is not null && differentialName != type.Name
                    && differential.GenericParameterTokens.IsDefaultOrEmpty
                    && index.TryGetValue(differential.OpenBrace, out int differentialOpen) && index.TryGetValue(differential.CloseBrace, out int differentialClose)
                    && DifferentialName(tokens, differentialOpen, differentialClose) is { } inner && inner != differentialName && inner != "This"
                    && Bases(type, tokens, index).FirstOrDefault(differentiableInterfaces.Contains) is { } baseName
                    && tokens.Skip(index[type.NameToken!]).Take(open - index[type.NameToken!]).FirstOrDefault(t => t.Text == baseName) is { } baseToken)
                {
                    Report(baseToken, $"{type.Name} の微分の型 {differentialName} の微分は、{differentialName} 自身でなければなりません ({differentialName} の Differential は {inner} です)。");
                }
                HashSet<string>? differentialFields = differential is null || !differential.GenericParameterTokens.IsDefaultOrEmpty
                    ? null
                    : [.. differential.Fields.SelectMany(f => f.Variables.Select(v => v.Name))];

                foreach (VariableDeclarationSyntax field in type.Fields)
                {
                    if (field.ModifierTokens.Any(t => t.Text is "static" or "const" or "no_diff") || field.Type.ModifierTokens.Any(t => t.Text == "no_diff")
                        || field.FirstToken is not { } first || !index.TryGetValue(first, out int start))
                    {
                        continue;
                    }

                    // フィールドの前の [DerivativeMember(D.m)]。
                    (ShaderSyntaxToken Name, ShaderSyntaxToken? Owner, ShaderSyntaxToken? Member)? member = DerivativeMemberBefore(tokens, start);
                    string typeName = field.Type.NameToken.Text;
                    bool fieldDifferentiable = DifferentiableType().IsMatch(typeName) && field.Type.OpenAngle is null;

                    if (member is { } attribute)
                    {
                        if (PlainType().IsMatch(typeName))
                        {
                            Report(attribute.Name, $"[DerivativeMember] を書いたフィールドの型 ({typeName}) は、微分しない型です。");
                        }
                        else if (differentialFields is not null && attribute.Owner is { } owner && (owner.Text == "Differential" || owner.Text == differentialName)
                                 && attribute.Member is { } target && !differentialFields.Contains(target.Text))
                        {
                            Report(attribute.Name, $"[DerivativeMember] には、微分の型 {differentialName} のフィールドを書いてください ({target.Text} は {differentialName} のフィールドではありません)。");
                        }

                        continue;
                    }

                    // 微分の型が自分でなければ、微分するフィールドには [DerivativeMember] で当たるフィールドを書く (同じ名前のフィールドがあっても当てない)。
                    if (differentiable && fieldDifferentiable && differentialFields is not null && differentialName != type.Name)
                    {
                        foreach (VariableDeclaratorSyntax variable in field.Variables)
                        {
                            Report(variable.NameToken, $"微分するフィールド '{variable.Name}' に、微分の型 {differentialName} の当たるフィールドを [DerivativeMember({differentialName}.…)] で書くか、no_diff を付けてください。");
                        }
                    }
                }
            }

            // 関数の外の変数の [DerivativeMember]。
            foreach (VariableDeclarationSyntax global in root.Declarations.OfType<VariableDeclarationSyntax>())
            {
                if (global.FirstToken is { } first && index.TryGetValue(first, out int start) && DerivativeMemberBefore(tokens, start) is { } attribute)
                {
                    Report(attribute.Name, "[DerivativeMember] は、型のフィールドにだけ書けます。");
                }
            }
        }
    }

    /// <summary>型の直下の <c>typealias Differential = D;</c> の D。書いていなければ <see langword="null"/>。</summary>
    private static string? DifferentialName(ImmutableArray<ShaderSyntaxToken> tokens, int open, int close)
    {
        string? name = null;
        int depth = 0;

        for (int i = open; i < close; i++)
        {
            depth += tokens[i].Kind switch { ShaderSyntaxKind.OpenBraceToken => 1, ShaderSyntaxKind.CloseBraceToken => -1, _ => 0 };

            if (depth == 1 && i + 4 < close && tokens[i].Text == "typealias" && tokens[i + 1].Text == "Differential" && tokens[i + 2].Kind == ShaderSyntaxKind.EqualsToken
                && tokens[i + 3].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 4].Kind == ShaderSyntaxKind.SemicolonToken)
            {
                name = tokens[i + 3].Text;
            }
        }

        return name;
    }

    /// <summary>型の宣言の基底の名前 (: の後ろの、{ の前の名前)。</summary>
    private static IEnumerable<string> Bases(StructDeclarationSyntax type, ImmutableArray<ShaderSyntaxToken> tokens, Dictionary<ShaderSyntaxToken, int> index)
    {
        if (type.NameToken is not { } name || !index.TryGetValue(name, out int start) || !index.TryGetValue(type.OpenBrace, out int open))
        {
            yield break;
        }

        int angle = 0;
        bool afterColon = false;

        for (int i = start + 1; i < open; i++)
        {
            ShaderSyntaxToken token = tokens[i];
            angle += token.Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, ShaderSyntaxKind.GreaterThanGreaterThanToken => -2, _ => 0 };

            if (token.Text == "where")
            {
                yield break;
            }

            if (angle == 0 && token.Kind == ShaderSyntaxKind.ColonToken)
            {
                afterColon = true;
            }
            else if (afterColon && angle == 0 && token.Kind == ShaderSyntaxKind.IdentifierToken && tokens[i - 1].Kind is ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.CommaToken)
            {
                yield return token.Text;
            }
        }
    }

    /// <summary>宣言の前の <c>[DerivativeMember(D.m)]</c>。無ければ <see langword="null"/>。</summary>
    private static (ShaderSyntaxToken Name, ShaderSyntaxToken? Owner, ShaderSyntaxToken? Member)? DerivativeMemberBefore(ImmutableArray<ShaderSyntaxToken> tokens, int start)
    {
        // 前の属性 [ … ] を、後ろからたどる。
        int i = start - 1;

        while (i >= 0 && tokens[i].Kind == ShaderSyntaxKind.CloseBracketToken)
        {
            int depth = 0;
            int j = i;

            for (; j >= 0; j--)
            {
                if (tokens[j].Kind == ShaderSyntaxKind.CloseBracketToken)
                {
                    depth++;
                }
                else if (tokens[j].Kind == ShaderSyntaxKind.OpenBracketToken && --depth == 0)
                {
                    break;
                }
            }

            if (j < 0)
            {
                return null;
            }

            if (j + 1 < i && tokens[j + 1].Text == "DerivativeMember")
            {
                // [DerivativeMember(D.m)]
                bool qualified = j + 6 <= i && tokens[j + 2].Kind == ShaderSyntaxKind.OpenParenToken && tokens[j + 3].Kind == ShaderSyntaxKind.IdentifierToken
                                 && tokens[j + 4].Kind == ShaderSyntaxKind.DotToken && tokens[j + 5].Kind == ShaderSyntaxKind.IdentifierToken
                                 && tokens[j + 6].Kind == ShaderSyntaxKind.CloseParenToken;
                return (tokens[j + 1], qualified ? tokens[j + 3] : null, qualified ? tokens[j + 5] : null);
            }

            i = j - 1;
        }

        return null;
    }
}
