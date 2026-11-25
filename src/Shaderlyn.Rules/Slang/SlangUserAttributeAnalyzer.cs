using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の利用者が定義した属性 (<c>[__AttributeUsage(_AttributeTargets.Struct)] struct MyStructAttribute { … }</c>) の使い方を検査する。
/// 置き場所 (SG0102) と、実引数の数と型 (SG0304)。
/// </summary>
/// <remarks>
/// <para>slangc 2026.19 で確かめた決まり。</para>
/// <list type="bullet">
/// <item><c>Struct</c> は struct の宣言にだけ書ける (class・enum・interface も誤り)。<c>Var</c> は大域の変数・局所変数・フィールドに書ける (仮引数は誤り)。
/// <c>Param</c> は仮引数にだけ書ける。<c>Function</c> は関数とメソッドにだけ書ける (E31002)</item>
/// <item>実引数は、static でないフィールドに順に入る。初期値の存在しないフィールドの数より少なければ E31001、フィールドの数より多ければ E39999</item>
/// <item>数のフィールドに文字列のリテラル、String のフィールドに文字列でないリテラルを書くと E30019</item>
/// </list>
/// <para>関数の外の宣言の属性は構文木に残らないので、トークンの並びを読む。属性の名前は、構造体の名前から Attribute を除いたもの。</para>
/// </remarks>
internal sealed class SlangUserAttributeAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>利用者の属性 1 つ。</summary>
    private sealed record UserAttribute(ImmutableHashSet<string> Targets, int Required, ImmutableArray<string> FieldTypes);

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.ModifierNotAllowed, SlangRuleDescriptors.InvalidAttributeValue];

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
            Dictionary<ShaderSyntaxToken, int> positions = new(ReferenceEqualityComparer.Instance);

            for (int i = 0; i < tokens.Length; i++)
            {
                positions[tokens[i]] = i;
            }

            Dictionary<string, UserAttribute?> attributes = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax type in program.Tree.Root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (type.Keyword.Text != "struct" || type.NameToken is not { } name || !name.Text.EndsWith("Attribute", StringComparison.Ordinal)
                    || name.Text.Length == "Attribute".Length || !positions.TryGetValue(type.Keyword, out int keyword)
                    || UsageTargets(tokens, keyword) is not { } targets)
                {
                    continue;
                }

                string attributeName = name.Text[..^"Attribute".Length];

                // 基底の型を持つものと、同じ名前が 2 つあるものは見ない。
                if (type.BaseType is not null || attributes.ContainsKey(attributeName))
                {
                    attributes[attributeName] = null;
                    continue;
                }

                List<string> fieldTypes = [];
                int required = 0;

                foreach (VariableDeclarationSyntax field in type.Fields.Where(f => !f.HasModifier("static")))
                {
                    foreach (VariableDeclaratorSyntax variable in field.Variables)
                    {
                        fieldTypes.Add(variable.IsArray ? "?" : field.Type.NameToken.Text);
                        required += variable.EqualsToken is null && fieldTypes.Count == required + 1 ? 1 : 0;
                    }
                }

                attributes[attributeName] = new UserAttribute(targets, required, [.. fieldTypes]);
            }

            if (attributes.Values.All(a => a is null))
            {
                continue;
            }

            for (int i = 1; i + 1 < tokens.Length; i++)
            {
                if (tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i - 1].Kind != ShaderSyntaxKind.OpenBracketToken
                    || !attributes.TryGetValue(tokens[i].Text, out UserAttribute? attribute) || attribute is null)
                {
                    continue;
                }

                // [[Name(…)]] の外の [ と、属性の並びの前のトークン。
                int open = i - 1;
                bool doubled = open >= 1 && tokens[open - 1].Kind == ShaderSyntaxKind.OpenBracketToken;
                int start = doubled ? open - 1 : open;
                ShaderSyntaxKind before = start >= 1 ? tokens[start - 1].Kind : ShaderSyntaxKind.SemicolonToken;

                if (before is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken
                    or ShaderSyntaxKind.CloseBracketToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CommaToken))
                {
                    continue;
                }

                if (ReadArguments(tokens, i + 1) is not { } read)
                {
                    continue;
                }

                (List<List<ShaderSyntaxToken>> arguments, int close) = read;
                int end = close + (doubled ? 2 : 1);

                if (end > tokens.Length || tokens[end - 1].Kind != ShaderSyntaxKind.CloseBracketToken
                    || (doubled && tokens[end - 2].Kind != ShaderSyntaxKind.CloseBracketToken))
                {
                    continue;
                }

                bool parameter = before is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CommaToken;

                void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, string message)
                {
                    if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                    }
                }

                if (Construct(tokens, end, parameter) is { } construct && !attribute.Targets.Contains(construct))
                {
                    string[] order = [.. new[] { "Struct", "Var", "Param", "Function" }.Where(attribute.Targets.Contains)];
                    Report(SlangRuleDescriptors.ModifierNotAllowed, tokens[i],
                        $"属性 [{tokens[i].Text}] は、{string.Join("・", order.Select(Describe))}にだけ書けます ({string.Join("・", order.Select(t => $"_AttributeTargets.{t}"))})。");
                }

                if (arguments.Count < attribute.Required || arguments.Count > attribute.FieldTypes.Length)
                {
                    string expected = attribute.Required == attribute.FieldTypes.Length
                        ? $"{attribute.Required} 個"
                        : $"{attribute.Required} 個から {attribute.FieldTypes.Length} 個";
                    Report(SlangRuleDescriptors.InvalidAttributeValue, tokens[i],
                        $"属性 [{tokens[i].Text}] の実引数は {expected}ですが、{arguments.Count} 個書いています。");
                    continue;
                }

                for (int k = 0; k < arguments.Count; k++)
                {
                    if (arguments[k] is not [{ } literal])
                    {
                        continue;
                    }

                    bool text = literal.Kind == ShaderSyntaxKind.StringLiteralToken;
                    bool literalValue = text || literal.Kind == ShaderSyntaxKind.NumericLiteralToken || literal.Text is "true" or "false";
                    string fieldType = attribute.FieldTypes[k];

                    if ((text && IsNumericName(fieldType)) || (!text && literalValue && fieldType is "String" or "string"))
                    {
                        Report(SlangRuleDescriptors.InvalidAttributeValue, literal,
                            $"属性 [{tokens[i].Text}] の {k + 1} 番目の実引数は {fieldType} のフィールドに入るので、{(text ? "文字列" : "文字列でない値")}は書けません。");
                    }
                }
            }
        }
    }

    /// <summary>
    /// struct の前の [__AttributeUsage(_AttributeTargets.X)] の X (複数書けば、そのすべて)。
    /// 書いていないか、読めない形 (<c>_AttributeTargets.Struct | …</c> など) があれば <see langword="null"/>。
    /// </summary>
    private static ImmutableHashSet<string>? UsageTargets(ImmutableArray<ShaderSyntaxToken> tokens, int keyword)
    {
        HashSet<string> targets = new(StringComparer.Ordinal);
        int close = keyword - 1;

        while (close >= 0 && tokens[close].Kind == ShaderSyntaxKind.CloseBracketToken)
        {
            // 対応する [ を探す。
            int open = close;

            for (int depth = 0; open >= 0; open--)
            {
                depth += tokens[open].Kind == ShaderSyntaxKind.CloseBracketToken ? 1 : tokens[open].Kind == ShaderSyntaxKind.OpenBracketToken ? -1 : 0;

                if (depth == 0)
                {
                    break;
                }
            }

            if (open < 0)
            {
                return null;
            }

            // [ __AttributeUsage ( _AttributeTargets . X ) ]
            if (close - open == 7 && tokens[open + 1].Text == "__AttributeUsage" && tokens[open + 2].Kind == ShaderSyntaxKind.OpenParenToken
                && tokens[open + 3].Text == "_AttributeTargets" && tokens[open + 4].Kind == ShaderSyntaxKind.DotToken
                && tokens[open + 5].Text is "Struct" or "Var" or "Function" or "Param" && tokens[open + 6].Kind == ShaderSyntaxKind.CloseParenToken)
            {
                targets.Add(tokens[open + 5].Text);
            }
            else if (Enumerable.Range(open, close - open).Any(k => tokens[k].Text == "__AttributeUsage"))
            {
                return null;
            }

            close = open - 1;
        }

        return targets.Count == 0 ? null : targets.ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <summary>属性の名前の後ろの (…) の実引数 (深さ 0 の , で分けたもの) と、閉じの後ろの位置。( が存在しなければ実引数は 0 個。</summary>
    private static (List<List<ShaderSyntaxToken>> Arguments, int Close)? ReadArguments(ImmutableArray<ShaderSyntaxToken> tokens, int at)
    {
        if (at >= tokens.Length || tokens[at].Kind != ShaderSyntaxKind.OpenParenToken)
        {
            return ([], at);
        }

        List<List<ShaderSyntaxToken>> arguments = [[]];
        int depth = 0;

        for (int j = at; j < tokens.Length; j++)
        {
            ShaderSyntaxKind kind = tokens[j].Kind;

            if (kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken)
            {
                return null;
            }

            if (kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken)
            {
                if (depth++ == 0)
                {
                    continue;
                }
            }
            else if (kind is ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CloseBracketToken)
            {
                if (--depth == 0)
                {
                    return (arguments is [[]] ? [] : arguments, j + 1);
                }
            }
            else if (kind == ShaderSyntaxKind.CommaToken && depth == 1)
            {
                arguments.Add([]);
                continue;
            }

            arguments[^1].Add(tokens[j]);
        }

        return null;
    }

    /// <summary>属性の後ろの宣言の種類 (Struct・Var・Function・Param)。決められなければ <see langword="null"/>。class・enum・interface は "Other"。</summary>
    private static string? Construct(ImmutableArray<ShaderSyntaxToken> tokens, int at, bool parameter)
    {
        int j = at;

        // 続く属性を飛ばす。
        while (j < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.OpenBracketToken)
        {
            int depth = 0;

            for (; j < tokens.Length; j++)
            {
                depth += tokens[j].Kind == ShaderSyntaxKind.OpenBracketToken ? 1 : tokens[j].Kind == ShaderSyntaxKind.CloseBracketToken ? -1 : 0;

                if (depth == 0)
                {
                    j++;
                    break;
                }
            }
        }

        if (j >= tokens.Length)
        {
            return null;
        }

        if (parameter)
        {
            return "Param";
        }

        switch (tokens[j].Text)
        {
            case "struct":
                return "Struct";
            case "class" or "enum" or "interface":
                return "Other";
            case "namespace" or "extension" or "typealias" or "typedef" or "property" or "__subscript" or "import" or "__include" or "module" or "implementing"
                or "cbuffer" or "tbuffer" or "func" or "__generic" or "associatedtype" or "using":
                return null;
            case "__init":
                return "Function";
            case "let" or "var":
                return "Var";
        }

        int angle = 0;

        for (int k = j; k < tokens.Length; k++)
        {
            ShaderSyntaxKind kind = tokens[k].Kind;

            if (kind == ShaderSyntaxKind.LessThanToken)
            {
                angle++;
            }
            else if (kind == ShaderSyntaxKind.GreaterThanToken)
            {
                angle--;
            }
            else if (kind == ShaderSyntaxKind.GreaterThanGreaterThanToken)
            {
                angle -= 2;
            }
            else if (angle <= 0 && kind == ShaderSyntaxKind.OpenParenToken)
            {
                return k > j && tokens[k - 1].Kind == ShaderSyntaxKind.IdentifierToken ? "Function" : null;
            }
            else if (angle <= 0 && kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.CommaToken
                         or ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.OpenBracketToken)
            {
                return k > j && tokens[k - 1].Kind == ShaderSyntaxKind.IdentifierToken ? "Var" : null;
            }
            else if (kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>置き場所の説明。</summary>
    private static string Describe(string target) => target switch
    {
        "Struct" => "struct の宣言",
        "Var" => "変数とフィールド",
        "Function" => "関数",
        _ => "仮引数",
    };

    /// <summary>数とベクトルの組み込みの型の名前か。</summary>
    private static bool IsNumericName(string name)
        => name.TrimEnd('1', '2', '3', '4', 'x') is "int" or "uint" or "float" or "half" or "double" or "bool"
               or "int8_t" or "int16_t" or "int32_t" or "int64_t" or "uint8_t" or "uint16_t" or "uint32_t" or "uint64_t"
               or "float16_t" or "float32_t" or "float64_t";
}
