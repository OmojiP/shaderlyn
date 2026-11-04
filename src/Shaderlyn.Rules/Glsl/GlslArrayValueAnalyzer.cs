using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 配列の値の使い方を検査する (GL0301 / GL0403)。glslang 16.6.0 の ParseHelper.cpp の
/// <c>specializationCheck</c> (「can't use with types containing arrays sized with a specialization constant」) と、
/// <c>executeInitializer</c> の「array initializer must be sized」「null initializers can't size unsized arrays」を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 特殊化の定数 (<c>layout(constant_id = …) const</c>) で大きさを決めた配列は、初期値にも、代入 (<c>=</c>) にも、比較 (<c>==</c> / <c>!=</c>) にも使えない。
/// </para>
/// <para>
/// 大きさを書かない配列の変数の初期値は、大きさの決まった配列でなければならない。大きさを書かない配列 (と実行時に大きさの決まるブロックのメンバー) と、
/// 空の初期化子 (<c>{ }</c>) では大きさを決められない。
/// </para>
/// <para>
/// 配列の値の作成 (<c>float[4][7](a, b, c, d)</c>) の実引数は、内側の次元の形の配列でなければならない (CF0410)。
/// 配列どうしの <c>==</c> / <c>!=</c> は、同じ形の配列どうしでなければならない (GL0301)。
/// 内側の次元の大きさを書かない配列の初期化子の要素は、どれも同じ大きさの配列でなければならない (CF0410)。
/// 次元の大きさは整数のリテラルのときだけ比べる。
/// </para>
/// </remarks>
internal sealed class GlslArrayValueAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.WrongOperandTypes, GlslRuleDescriptors.TypeNotAllowedHere, HlslRuleDescriptors.ValueNotConvertible, GlslRuleDescriptors.NoMatchingBuiltin];

    /// <summary>配列の形。次元は外から順で、大きさを書かない次元は空の文字列。</summary>
    private sealed record Shape(string Element, ImmutableArray<string> Dimensions)
    {
        public override string ToString() => Element + string.Concat(Dimensions.Select(d => $"[{d}]"));
    }

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, (GlslRequirements Requirement, HashSet<string> Specs)?> states = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (VariableDeclaratorSyntax { Initializer: not null } or AssignmentExpressionSyntax or BinaryExpressionSyntax
                or InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax or IdentifierExpressionSyntax }))
            {
                continue;
            }

            if (!states.TryGetValue(program, out var state))
            {
                state = states[program] = GlslRequirements.Create(context, compilation, program, reported) is { } created
                    ? (created, CollectSpecConstants(program))
                    : null;
            }

            if (state is not { } found)
            {
                continue;
            }

            (GlslRequirements requirement, HashSet<string> specs) = found;
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            Shapes shapes = new(binder, name => compilation.GetFunctionDeclarations(name, program));

            switch (node)
            {
                // 構造体の定義と一緒に宣言した変数の初期化子 (struct { float a; int b; } e = { … };)。
                case VariableDeclaratorSyntax { Initializer: { } structInitializer, Parent: StructDeclarationSyntax { Keyword.Text: "struct" } inline } structVariable:
                    CheckInlineStruct(requirement, structVariable, inline, structInitializer);
                    break;

                case VariableDeclaratorSyntax { Initializer: { } initializer } declarator:
                    if (specs.Count > 0 && (IsSpecSized(declarator, specs) || IsSpecSizedValue(binder, initializer, specs)))
                    {
                        requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, declarator.NameToken, declarator.Name, "特殊化の定数で大きさを決めた配列は、初期値に使えません。");
                    }
                    else if (IsOuterUnsized(declarator) && initializer is InitializerListExpressionSyntax { Elements.IsEmpty: true })
                    {
                        requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, declarator.NameToken, declarator.Name, "空の初期化子 ({ }) では、大きさを書かない配列の大きさを決められません。");
                    }
                    else if (IsOuterUnsized(declarator) && IsUnsizedValue(binder, initializer))
                    {
                        requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, declarator.NameToken, declarator.Name, "大きさを書かない配列の初期値は、大きさの決まった配列でなければなりません。");
                    }
                    else if (initializer is InitializerListExpressionSyntax list && Dims(declarator.ArrayRankTokens) is [_, "", ..]
                             && list.ChildNodes().OfType<ExpressionSyntax>().Select(shapes.Of).ToArray() is { Length: > 1 } elements
                             && elements.All(e => e is { Dimensions.Length: > 0 })
                             && elements.Skip(1).FirstOrDefault(e => SameDimensions(e!.Dimensions, elements[0]!.Dimensions) == false) is { } different)
                    {
                        requirement.Report(HlslRuleDescriptors.ValueNotConvertible, declarator.NameToken, $"最初の要素と同じ形の配列 ({elements[0]})", different.ToString(), string.Empty);
                    }

                    break;

                case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken } assignment
                    when specs.Count > 0 && (IsSpecSizedValue(binder, assignment.Left, specs) || IsSpecSizedValue(binder, assignment.Right, specs)):
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, assignment.OperatorToken, "特殊化の定数で大きさを決めた配列は、代入できません。");
                    break;

                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken } binary
                    when specs.Count > 0 && (IsSpecSizedValue(binder, binary.Left, specs) || IsSpecSizedValue(binder, binary.Right, specs)):
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, binary.OperatorToken, $"特殊化の定数で大きさを決めた配列は、'{binary.OperatorToken.Text}' で比べられません。");
                    break;

                // 大きさの決まっていない配列 (ブロックの最後のメンバー) には代入できない。
                case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken, Left: MemberAccessExpressionSyntax target } unsizedAssignment
                    when FindBlockMember(target) is { } unsizedMember && IsOuterUnsized(unsizedMember):
                    requirement.Report(HlslRuleDescriptors.ValueNotConvertible, unsizedAssignment.OperatorToken, $"大きさの決まっていない配列 '{unsizedMember.Name}'", "配列の値", "大きさの決まっていない配列には代入できません。");
                    break;

                // 形の違う配列どうしは比べられない。
                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken } compared
                    when shapes.Of(compared.Left) is { Dimensions.Length: > 0 } left && shapes.Of(compared.Right) is { Dimensions.Length: > 0 } right
                         && (left.Element != right.Element || SameDimensions(left.Dimensions, right.Dimensions) == false):
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, compared.OperatorToken, $"演算子 '{compared.OperatorToken.Text}' は、形の違う配列 ({left} と {right}) に使えません。");
                    break;

                // 数の型の値の作成には、構造体を渡せない (glslang の「not enough data provided for construction」)。
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax numeric, OpenAngle: null } numericConstructor
                    when binder.TypeSystem is GlslTypeSystem types && types.TryDescribeNumeric(numeric.Name, out _)
                         && numericConstructor.ArgumentExpressions.Select(a => binder.GetTypesByConfiguration(a) is [{ Type: { } type }] ? type : null)
                             .FirstOrDefault(t => t is not null && program.Structs.Any(s => s.Name == t && s.Keyword.Text == "struct")) is { } structure:
                    requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, numeric.Identifier, numeric.Name, $"構造体 {structure}");
                    break;

                case InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax arrayType } constructor:
                    CheckConstructor(requirement, shapes, arrayType, [.. constructor.ArgumentExpressions]);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// 構造体の定義と一緒に宣言した変数の初期化子。波括弧の要素の数はメンバーの数と同じでなければならず (glslang の「wrong number of structure members」)、
    /// 配列なら、要素に別の構造体の値の作成を書けない。
    /// </summary>
    private static void CheckInlineStruct(GlslRequirements requirement, VariableDeclaratorSyntax variable, StructDeclarationSyntax inline, ExpressionSyntax initializer)
    {
        if (initializer is not InitializerListExpressionSyntax list)
        {
            return;
        }

        ExpressionSyntax[] elements = [.. list.ChildNodes().OfType<ExpressionSyntax>()];
        int members = inline.Fields.Sum(f => f.Variables.Count());
        string type = inline.NameToken is { IsMissing: false } name ? name.Text : "名前の無い構造体";

        if (!variable.IsArray)
        {
            if (elements.Length != members)
            {
                requirement.Report(HlslRuleDescriptors.ValueNotConvertible, variable.NameToken, $"{type} ({members} 個のメンバー)", $"{elements.Length} 個の要素の初期化子", string.Empty);
            }

            return;
        }

        if (elements.FirstOrDefault(e => e is InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } && callee.Name != inline.Name
                                         && !Shaderlyn.Glsl.Parsing.GlslKeywords.IsBuiltInType(callee.Name)) is InvocationExpressionSyntax { Target: IdentifierExpressionSyntax other })
        {
            requirement.Report(HlslRuleDescriptors.ValueNotConvertible, variable.NameToken, $"配列の要素 ({type})", $"構造体 {other.Name} の値", string.Empty);
        }
    }

    /// <summary>配列の値の作成の実引数は、内側の次元の形の配列でなければならない (glslang の constructor の検査)。</summary>
    private static void CheckConstructor(GlslRequirements requirement, Shapes shapes, ArrayTypeExpressionSyntax arrayType, ExpressionSyntax[] arguments)
    {
        ImmutableArray<string> dimensions = Dims(arrayType.ArrayRanks);

        if (dimensions.IsEmpty)
        {
            return;
        }

        Shape expected = new(arrayType.ElementType.Name, dimensions[1..]);

        foreach (ExpressionSyntax argument in arguments)
        {
            if (shapes.Of(argument) is not { } actual)
            {
                continue;
            }

            // 配列でない要素は、暗黙に変換できる同じ形 (スカラー・ベクトル・行列の成分の数) の値でなければならない。
            if (actual.Dimensions.IsEmpty && expected.Dimensions.IsEmpty)
            {
                if (shapes.Inconvertible(actual.Element, expected.Element))
                {
                    requirement.Report(HlslRuleDescriptors.ValueNotConvertible, arrayType.ElementType.Identifier, $"配列の要素 ({expected})", actual.ToString(), string.Empty);
                    return;
                }

                continue;
            }

            if (actual.Dimensions.Length == expected.Dimensions.Length && SameDimensions(actual.Dimensions, expected.Dimensions) != false)
            {
                continue;
            }

            // 要素の型が配列でないのに、配列を渡した (glslang の「constructing non-array constituent from array argument」)。
            // 次元の数か大きさが違う (glslang の「array constructor argument not correct type to construct array element」)。
            requirement.Report(HlslRuleDescriptors.ValueNotConvertible, arrayType.ElementType.Identifier, $"配列の要素 ({expected})", actual.ToString(), string.Empty);
            return;
        }
    }

    /// <summary>次元が同じか。大きさを書かない次元はどれとも同じとし、リテラルでない大きさは分からないとする。</summary>
    private static bool? SameDimensions(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        bool? same = true;

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] == right[i] || left[i].Length == 0 || right[i].Length == 0)
            {
                continue;
            }

            if (long.TryParse(left[i], out long a) && long.TryParse(right[i], out long b))
            {
                if (a != b)
                {
                    return false;
                }
            }
            else
            {
                same = null;
            }
        }

        return same;
    }

    /// <summary>角括弧の並びの、次元ごとの大きさの字句。</summary>
    private static ImmutableArray<string> Dims(ImmutableArray<ShaderSyntaxToken> ranks)
    {
        ImmutableArray<string>.Builder dimensions = ImmutableArray.CreateBuilder<string>();
        List<string> current = [];
        int depth = 0;

        foreach (ShaderSyntaxToken token in ranks)
        {
            if (token.Kind == ShaderSyntaxKind.OpenBracketToken && depth++ == 0)
            {
                current.Clear();
            }
            else if (token.Kind == ShaderSyntaxKind.CloseBracketToken && --depth == 0)
            {
                dimensions.Add(string.Concat(current));
            }
            else
            {
                current.Add(token.Text);
            }
        }

        return dimensions.ToImmutable();
    }

    /// <summary>式の配列の形を求める。</summary>
    private sealed class Shapes(ExpressionTypeBinder binder, Func<string, ImmutableArray<FunctionDeclarationSyntax>> functions)
    {
        /// <summary>数の型どうしで、形が違うか、暗黙に変換できないか。どちらかが数の型でなければ <see langword="false"/>。</summary>
        public bool Inconvertible(string from, string to)
            => binder.TypeSystem is GlslTypeSystem types
               && types.TryDescribeNumeric(from, out ShaderNumericShape source) && types.TryDescribeNumeric(to, out ShaderNumericShape target)
               && (source.Kind != target.Kind || source.Rows != target.Rows || source.Columns != target.Columns
                   || source.BaseName != target.BaseName && !types.IsBaseConvertible(source.BaseName, target.BaseName));

        /// <summary>式の形。分からなければ <see langword="null"/>。配列でない値は次元の無い形。</summary>
        public Shape? Of(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => Of(parenthesized.Expression),
            IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates is [{ Declarator: var declarator }] ? FromDeclarator(declarator) : null,
            ElementAccessExpressionSyntax element => Of(element.Target) is { Dimensions.Length: > 0 } outer ? outer with { Dimensions = outer.Dimensions[1..] } : null,
            InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax arrayType } constructor => FromConstructor(arrayType, constructor.ArgumentExpressions.Count()),
            InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } call when functions(callee.Name) is { IsEmpty: false } declared
                => declared.Select(f => new Shape(f.ReturnType.Name, Dims(f.ReturnType.ArrayRanks))).DistinctBy(s => s.ToString()).ToArray() is [var only] ? only : null,
            LiteralExpressionSyntax or InvocationExpressionSyntax { Target: IdentifierExpressionSyntax }
                => binder.GetTypesByConfiguration(expression) is [{ Type: { } type }] && binder.TypeSystem.TryDescribeNumeric(type, out _) ? new Shape(type, []) : null,
            _ => null,
        };

        private static Shape? FromDeclarator(ShaderSyntaxNode declarator) => declarator switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable
                => new Shape(declaration.Type.Name, [.. Dims(variable.ArrayRankTokens), .. Dims(declaration.Type.ArrayRanks)]),
            ParameterSyntax parameter => new Shape(parameter.Type.Name, [.. Dims(parameter.ArrayRankTokens), .. Dims(parameter.Type.ArrayRanks)]),
            _ => null,
        };

        private static Shape FromConstructor(ArrayTypeExpressionSyntax arrayType, int count)
        {
            ImmutableArray<string> dimensions = Dims(arrayType.ArrayRanks);

            return new Shape(arrayType.ElementType.Name, dimensions is [ "", .. var rest ] ? [count.ToString(System.Globalization.CultureInfo.InvariantCulture), .. rest] : dimensions);
        }
    }

    /// <summary>特殊化の定数の名前。</summary>
    private static HashSet<string> CollectSpecConstants(AnalyzedProgram program)
        => [.. program.Tree.Root.ChildNodes().OfType<VariableDeclarationSyntax>()
            .Where(d => GlslLayoutQualifier.Read(d.ModifierTokens).Any(e => e.Name.Text == "constant_id"))
            .SelectMany(d => d.Variables.Select(v => v.Name))];

    /// <summary>配列の大きさに特殊化の定数を書いた宣言か。</summary>
    private static bool IsSpecSized(VariableDeclaratorSyntax declarator, HashSet<string> specs)
        => declarator.ArrayRankTokens.Any(t => t.Kind == ShaderSyntaxKind.IdentifierToken && specs.Contains(t.Text))
           || declarator.Parent is VariableDeclarationSyntax { Type.ArrayRanks: var ranks } && ranks.Any(t => t.Kind == ShaderSyntaxKind.IdentifierToken && specs.Contains(t.Text));

    /// <summary>添字を付けない、大きさに特殊化の定数を書いた配列の変数か。</summary>
    private static bool IsSpecSizedValue(ExpressionTypeBinder binder, ExpressionSyntax expression, HashSet<string> specs) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsSpecSizedValue(binder, parenthesized.Expression, specs),
        IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax declarator }] && IsSpecSized(declarator, specs),
        _ => false,
    };

    /// <summary>一番外の次元の大きさを書かない配列の宣言か。</summary>
    private static bool IsOuterUnsized(VariableDeclaratorSyntax declarator)
        => declarator.ArrayRankTokens is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }, ..];

    /// <summary>大きさを書かない配列の変数か、ブロックの大きさを書かない配列のメンバーか (添字を付けないもの)。</summary>
    private static bool IsUnsizedValue(ExpressionTypeBinder binder, ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsUnsizedValue(binder, parenthesized.Expression),
        IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax declarator }]
                                                 && IsOuterUnsized(declarator) && declarator.Initializer is null && !IsResized(declarator),
        MemberAccessExpressionSyntax member => FindBlockMember(member) is { } declarator && IsOuterUnsized(declarator),
        _ => false,
    };

    /// <summary>同じ範囲で、大きさを書いて宣言し直しているか。</summary>
    private static bool IsResized(VariableDeclaratorSyntax declarator)
        => declarator.Parent?.Parent is { } scope
           && scope.ChildNodes().OfType<VariableDeclarationSyntax>().SelectMany(d => d.Variables).Any(v => v != declarator && v.Name == declarator.Name);

    /// <summary>名前のあるブロックのインスタンスのメンバーの宣言子。</summary>
    private static VariableDeclaratorSyntax? FindBlockMember(MemberAccessExpressionSyntax member)
    {
        if ((member.Target is ElementAccessExpressionSyntax { Target: var indexed } ? indexed : member.Target) is not IdentifierExpressionSyntax instance
            || member.Ancestors().OfType<CompilationUnitSyntax>().FirstOrDefault() is not { } unit)
        {
            return null;
        }

        VariableDeclaratorSyntax[] found =
        [
            .. unit.ChildNodes()
                .Where(n => n is StructDeclarationSyntax { Keyword.Text: not "struct" } or InterfaceBlockDeclarationSyntax
                            && n.ChildNodes().OfType<VariableDeclaratorSyntax>().Any(v => v.Name == instance.Name))
                .SelectMany(n => n.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
                .Where(v => v.Name == member.Name && v.Parent is VariableDeclarationSyntax),
        ];

        return found is [var only] ? only : null;
    }
}
