using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、static でないメンバーの使い方 (SG0344) と、式の形 (SG0301 の <c>-&gt;</c>・添字・sizeof、CF0510 の添字の範囲) を検査する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SG0344: static のメソッドの中の <c>this</c> (E30120) と、修飾しない static でないメソッドの呼び出しとフィールド (E30100)。
/// 型の名前で書いた static でないフィールド (<c>Color.Red</c> の Color が構造体。E30100)</item>
/// <item>SG0301: ポインターでない構造体の値への <c>-&gt;</c> (E30101)、スカラーの値への添字 (E30013)、関数の名前の <c>sizeof</c> (E30099)</item>
/// <item>CF0510: 大きさを数で書いた配列の、負の数と定数の式の添字の範囲の外 (E30029。数のリテラルの添字は CF0510 の本体が見る)</item>
/// </list>
/// </remarks>
internal sealed class SlangMemberUseAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SlangRuleDescriptors.NonStaticMemberFromStatic, SlangRuleDescriptors.WrongOperandTypes, HlslRuleDescriptors.ArrayIndexOutOfBounds];

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

            CompilationUnitSyntax root = program.Tree.Root;
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            SlangConstants constants = SlangConstants.Of(root);

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                }
            }

            Dictionary<string, StructDeclarationSyntax?> structs = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax structure in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (structure.NameToken is { } name && structure.Keyword.Text is "struct" or "class")
                {
                    structs[name.Text] = structs.ContainsKey(name.Text) ? null : structure;
                }
            }

            // extension で足されうる型 (extension S { … }) は、メンバーを決められない。
            SlangOpenTypes extended = SlangOpenTypes.Of(compilation);
            HashSet<string> globalFunctions = [.. root.Declarations.OfType<FunctionDeclarationSyntax>().Select(f => f.Name)];

            foreach (StructDeclarationSyntax structure in structs.Values.OfType<StructDeclarationSyntax>())
            {
                CheckStaticMethods(structure, globalFunctions, Report);
            }

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    // Color.Red (Color が構造体の型の名前で、Red が static でないフィールド)
                    case MemberAccessExpressionSyntax { Target: IdentifierExpressionSyntax target, DotToken.Kind: ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken } access
                        when structs.TryGetValue(target.Name, out StructDeclarationSyntax? structure) && structure is not null
                             && !extended.Contains(target.Name)
                             && binder.ResolveName(target).Kind == DeclaredNameKind.NotFound
                             && Members(structure).Fields.Contains(access.NameToken.Text)
                             && !(access.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, access)):
                        Report(SlangRuleDescriptors.NonStaticMemberFromStatic, access.NameToken,
                            $"型の名前 {target.Name} からは、static でないフィールド '{access.NameToken.Text}' を使用できません。値 (変数) から使用してください。");
                        break;

                    // val->member (val がポインターでない構造体)
                    case MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.MinusToken } arrow
                        when binder.GetTypesByConfiguration(arrow.Target) is [{ Type: { } type }]
                             && structs.ContainsKey(type)
                             && !(arrow.Target is IdentifierExpressionSyntax arrowTarget && binder.ResolveName(arrowTarget).Candidates.Any(c => c.Type.IsArray)):
                        Report(SlangRuleDescriptors.WrongOperandTypes, arrow.DotToken, $"-> はポインターにだけ使用できます ({type} はポインターではありません。'.' を使用してください)。");
                        break;

                    case ElementAccessExpressionSyntax element when element.Target is IdentifierExpressionSyntax indexed
                                                                    && binder.ResolveName(indexed).Candidates is [var candidate]:
                        CheckIndex(element, indexed, candidate, binder, constants, Report, (location, name, length, index) =>
                        {
                            if (compilation.IsReportable(element.OpenBracket))
                            {
                                context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.ArrayIndexOutOfBounds, location, name, length, index));
                            }
                        });
                        break;

                    // sizeof(func) (関数の名前)
                    case InvocationExpressionSyntax { TargetName: "sizeof" } size
                        when size.ArgumentExpressions.ToArray() is [IdentifierExpressionSyntax argument]
                             && globalFunctions.Contains(argument.Name)
                             && binder.ResolveName(argument).Kind == DeclaredNameKind.NotFound
                             && !structs.ContainsKey(argument.Name):
                        Report(SlangRuleDescriptors.WrongOperandTypes, argument.Identifier, $"sizeof には型か値を渡してください (関数 '{argument.Name}' は渡せません)。");
                        break;
                }
            }
        }
    }

    private static void CheckIndex(
        ElementAccessExpressionSyntax element,
        IdentifierExpressionSyntax indexed,
        DeclaredName candidate,
        ExpressionTypeBinder binder,
        SlangConstants constants,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report,
        Action<Location, string, int, long> outOfBounds)
    {
        if (candidate.Type.IsArray)
        {
            // 1 次元で、大きさを数で書いた配列だけ。
            if (candidate.Type.ArrayDimensions <= 1
                && candidate.TryGetArrayLength(out int length)
                && constants.Evaluate(element.Index) is { } index
                && (index < 0 || index >= length)
                && !(index >= 0 && element.Index is LiteralExpressionSyntax)
                && !element.OpenBracket.IsMissing)
            {
                // 数のリテラルの添字が長さを超えるものは、C 系の共通の CF0510 の本体が報告する。負の数と定数の式の添字を足す。
                outOfBounds(element.OpenBracket.GetLocation(), indexed.Name, length, index);
            }

            return;
        }

        // typedef の別名 (typedef int[3] Arr3;) は、配列でありうるので見ない。
        TypeSyntax? declared = candidate.Declarator switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } => declaration.Type,
            ParameterSyntax parameter => parameter.Type,
            _ => null,
        };

        if (declared is not null
            && declared.NameToken.Text == candidate.Type.TypeName
            && declared.ArrayRanks.IsDefaultOrEmpty
            && candidate.Type.TemplateArgument is null
            && binder.TypeSystem.TryDescribeNumeric(candidate.Type.TypeName, out ShaderNumericShape shape)
            && shape.Kind == ShaderNumericKind.Scalar
            && !candidate.Type.TypeName.Contains('*', StringComparison.Ordinal))
        {
            report(SlangRuleDescriptors.WrongOperandTypes, element.OpenBracket, $"スカラーの値 '{indexed.Name}' ({candidate.Type.TypeName}) に添字は使用できません。");
        }
    }

    /// <summary>構造体の static でないメソッドとフィールドの名前と、static のメンバーの名前。</summary>
    private static (HashSet<string> Methods, HashSet<string> Fields, HashSet<string> Statics) Members(StructDeclarationSyntax structure)
    {
        HashSet<string> methods = new(StringComparer.Ordinal);
        HashSet<string> fields = new(StringComparer.Ordinal);
        HashSet<string> statics = new(StringComparer.Ordinal);

        foreach (DeclarationSyntax member in structure.Members)
        {
            switch (member)
            {
                case FunctionDeclarationSyntax method:
                    (method.ModifierTokens.Any(t => t.Text == "static") ? statics : methods).Add(method.Name);
                    break;

                case VariableDeclarationSyntax field:
                    bool isStatic = field.ModifierTokens.Any(t => t.Text is "static" or "const");

                    foreach (VariableDeclaratorSyntax variable in field.Variables)
                    {
                        (isStatic ? statics : fields).Add(variable.Name);
                    }

                    break;
            }
        }

        methods.ExceptWith(statics);
        fields.ExceptWith(statics);
        fields.ExceptWith(methods);
        return (methods, fields, statics);
    }

    /// <summary>static のメソッドの中の this と、修飾しない static でないメンバーの使用。</summary>
    private static void CheckStaticMethods(StructDeclarationSyntax structure, HashSet<string> globalFunctions, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        (HashSet<string> methods, HashSet<string> fields, _) = Members(structure);

        foreach (FunctionDeclarationSyntax method in structure.Members.OfType<FunctionDeclarationSyntax>())
        {
            if (method.Body is not { } body || !method.ModifierTokens.Any(t => t.Text == "static"))
            {
                continue;
            }

            // 仮引数と局所変数の名前 (メンバーを隠す)。
            HashSet<string> locals = [.. method.ParameterList.Select(p => p.Name)];
            locals.UnionWith(body.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>().Select(v => v.Name));

            foreach (IdentifierExpressionSyntax identifier in body.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
            {
                // メンバーの名前 (x.name の name) は見ない。
                if (identifier.Parent is MemberAccessExpressionSyntax access && !ReferenceEquals(access.Target, identifier))
                {
                    continue;
                }

                if (identifier.Name == "this")
                {
                    report(SlangRuleDescriptors.NonStaticMemberFromStatic, identifier.Identifier, $"static のメソッド '{method.Name}' の中では this を使用できません。");
                    continue;
                }

                if (locals.Contains(identifier.Name))
                {
                    continue;
                }

                bool called = identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier);

                if ((called && methods.Contains(identifier.Name) && !globalFunctions.Contains(identifier.Name))
                    || (!called && fields.Contains(identifier.Name)))
                {
                    report(SlangRuleDescriptors.NonStaticMemberFromStatic, identifier.Identifier,
                        $"static のメソッド '{method.Name}' からは、static でない{(called ? "メソッド" : "フィールド")} '{identifier.Name}' を使用できません。");
                }
            }
        }
    }
}
