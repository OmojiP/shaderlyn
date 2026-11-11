using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、型引数の違うジェネリックの型どうしと、種類の違うテクスチャどうしの代入と初期化を検査する (CF0410)。
/// </summary>
/// <remarks>
/// <para>
/// 型の名前だけを比べる共通の検査は、<c>G&lt;int&gt;</c> と <c>G&lt;float&gt;</c>、<c>Texture1D&lt;int&gt;</c> と <c>Texture1D&lt;float&gt;</c> を同じ型と見る。
/// slangc はどれも暗黙に変換しない (E30019「type mismatch in expression」)。
/// </para>
/// <para>
/// 見るのは、右辺が名前 (変数・仮引数) のときだけである。両方の型引数を書いていて、その綴りが違うときと、読むだけのテクスチャ (Texture1D・Texture2D など) の次元が違うときに報告する。
/// 型引数を書かない型 (既定の型引数) と、ジェネリックの仮引数を型引数にしたものは見ない。
/// </para>
/// </remarks>
internal sealed class SlangGenericConversionAnalyzer : SemanticRuleAnalyzer
{
    private static readonly string[] Textures =
    [
        "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray", "Texture3D", "TextureCube", "TextureCubeArray",
    ];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.ValueNotConvertible];

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
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            HashSet<string> generics = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>()
                .Where(s => s.NameToken is not null && !s.GenericParameterTokens.IsDefaultOrEmpty)
                .Select(s => s.NameToken!.Text)];

            // ジェネリックの仮引数の名前 (型引数に書くと、何の型か決まらない)。
            HashSet<string> parameters = [.. root.DescendantNodesAndSelf().SelectMany(n => n switch
            {
                StructDeclarationSyntax s => s.GenericParameterTokens,
                FunctionDeclarationSyntax f => f.GenericParameterTokens,
                _ => [],
            }).Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text)];

            ResolvedTypeName? TypeOf(ExpressionSyntax expression)
                => expression is IdentifierExpressionSyntax identifier && binder.ResolveName(identifier).Candidates is [var candidate] && !candidate.Type.IsArray
                    ? candidate.Type
                    : null;

            HashSet<string> structs = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>().Where(s => s.NameToken is not null).Select(s => s.NameToken!.Text)];
            structs.ExceptWith(root.TypeAliases.Keys);

            // 型引数の綴りの違いが型の違いと言えるもの: 数、組み込みの型の名前、ファイルで宣言した構造体 (別名でもジェネリックの仮引数でもないもの)。
            bool IsKnown(string argument)
                => !parameters.Contains(argument) && !root.TypeAliases.ContainsKey(argument)
                   && (argument.All(char.IsAsciiDigit) || binder.TypeSystem.IsBuiltInTypeName(argument) || structs.Contains(argument));

            void Check(ResolvedTypeName target, ExpressionSyntax value)
            {
                if (TypeOf(value) is not { } source || Describe(target, source, generics, a => IsKnown(a)) is not { } reason
                    || !compilation.IsReportable(value) || value.GetLocation() is not { } location)
                {
                    return;
                }

                context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.ValueNotConvertible, location, Show(target), Show(source), reason));
            }

            // 構造体の名前 (extension の中の入れ子の型も、トークンの並びから集める) と、別名にした名前。
            ImmutableArray<ShaderSyntaxToken> all = program.Tree.PreprocessResult.Tokens;
            HashSet<string> structNames = new(StringComparer.Ordinal);
            HashSet<string> aliasNames = [.. root.TypeAliases.Keys];

            for (int i = 0; i + 2 < all.Length; i++)
            {
                if (all[i].Text is "struct" or "class" && all[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && all[i + 2].Kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.ColonToken)
                {
                    structNames.Add(all[i + 1].Text);
                }
                else if (all[i].Text is "typealias" or "typedef" or "associatedtype" && all[i + 1].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    aliasNames.Add(all[i + 1].Text);
                }
            }

            // 入れ子の型 (A<T>.B・A.B.C) の変数への、数のリテラルの代入。最後の名前が構造体なら、数から暗黙に変換できない (E30019)。
            void CheckNested(AssignmentExpressionSyntax assignment)
            {
                if (assignment.Right is not LiteralExpressionSyntax { Token.Kind: ShaderSyntaxKind.NumericLiteralToken } literal
                    || assignment.Left is not IdentifierExpressionSyntax target
                    || binder.ResolveName(target).Candidates is not [{ Declarator: VariableDeclaratorSyntax { IsArray: false, Parent: VariableDeclarationSyntax { Type: var type } } }]
                    || (type.NestedTypeTokens.IsDefaultOrEmpty ? (type.QualifierTokens.IsDefaultOrEmpty ? null : type.NameToken) : type.NestedTypeTokens.LastOrDefault(t => t.Kind == ShaderSyntaxKind.IdentifierToken)) is not { } last
                    || !structNames.Contains(last.Text) || aliasNames.Contains(last.Text)
                    || !compilation.IsReportable(literal) || literal.GetLocation() is not { } location)
                {
                    return;
                }

                string shown = string.Concat(type.QualifierTokens.IsDefaultOrEmpty ? [] : type.QualifierTokens.Select(t => t.Text)) + type.NameToken.Text + string.Concat(type.TemplateArguments.IsDefaultOrEmpty ? [] : type.TemplateArguments.Select(t => t.Text).Prepend("<").Append(">")) + string.Concat(type.NestedTypeTokens.Select(t => t.Text));
                context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.ValueNotConvertible, location, shown, ShaderLiteral.GetKind(literal.Token) == ShaderLiteralKind.Floating ? "float" : "int",
                    "数は構造体へ暗黙に変換されません。"));
            }

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken } assignment when TypeOf(assignment.Left) is { } left:
                        Check(left, assignment.Right);
                        CheckNested(assignment);
                        break;

                    case VariableDeclarationSyntax declaration:
                        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                        {
                            // SamplerDescriptorHeap[i] はサンプラーに、ResourceDescriptorHeap[i] は資源にだけなる (E30019)。.Handle の型も同じ。
                            if (variable.Initializer is ElementAccessExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "SamplerDescriptorHeap" or "ResourceDescriptorHeap" } heap }
                                && (declaration.Type.NameToken.Text == "Handle" && !declaration.Type.QualifierTokens.IsDefaultOrEmpty ? declaration.Type.QualifierTokens[0].Text
                                    : declaration.Type.QualifierTokens.IsDefaultOrEmpty ? declaration.Type.NameToken.Text : string.Empty) is { Length: > 0 } heapTarget
                                && (heapTarget.StartsWith("Sampler", StringComparison.Ordinal) ? heap.Name == "ResourceDescriptorHeap"
                                    : heapTarget.Contains("Texture", StringComparison.Ordinal) || heapTarget.EndsWith("Buffer", StringComparison.Ordinal) ? heap.Name == "SamplerDescriptorHeap" : false)
                                && compilation.IsReportable(variable.Initializer) && variable.Initializer.GetLocation() is { } heapLocation)
                            {
                                context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.ValueNotConvertible, heapLocation, heapTarget, $"{heap.Name} の要素",
                                    heap.Name == "SamplerDescriptorHeap" ? "SamplerDescriptorHeap からはサンプラーだけを作れます。資源は ResourceDescriptorHeap から作ってください。"
                                        : "ResourceDescriptorHeap からは資源だけを作れます。サンプラーは SamplerDescriptorHeap から作ってください。"));
                                continue;
                            }

                            if (variable.Initializer is { } initializer && !variable.IsArray && declaration.Type.OpenAngle is not null)
                            {
                                Check(new ResolvedTypeName(declaration.Type.Name, false, TemplateText(declaration.Type)), initializer);
                            }
                        }

                        break;
                }
            }
        }
    }

    private static string? TemplateText(TypeSyntax type)
        => type.TemplateArguments.IsDefaultOrEmpty ? null : string.Join(", ", string.Concat(type.TemplateArguments.Select(t => t.Text)).Split(',', StringSplitOptions.TrimEntries));

    private static string Show(ResolvedTypeName type) => type.TemplateArgument is { } arguments ? $"{type.TypeName}<{arguments}>" : type.TypeName;

    /// <summary>暗黙に変換できない理由。変換できるか、決められなければ <see langword="null"/>。</summary>
    private static string? Describe(ResolvedTypeName target, ResolvedTypeName source, HashSet<string> generics, Func<string, bool> known)
    {
        bool targetTexture = Textures.Contains(target.TypeName);
        bool sourceTexture = Textures.Contains(source.TypeName);

        if (targetTexture && sourceTexture && target.TypeName != source.TypeName)
        {
            return "テクスチャの種類 (次元) の違う値は代入できません。";
        }

        if (target.TypeName != source.TypeName
            || (!targetTexture && !generics.Contains(target.TypeName))
            || target.TemplateArgument is not { } targetArguments
            || source.TemplateArgument is not { } sourceArguments)
        {
            return null;
        }

        string[] left = Normalize(targetArguments);
        string[] right = Normalize(sourceArguments);

        if (left.Length != right.Length || left.Concat(right).Any(a => !IsPlainName(a) || !known(a)) || left.SequenceEqual(right, StringComparer.Ordinal))
        {
            return null;
        }

        return "型引数の違うジェネリックの型どうしは、暗黙に変換されません。";
    }

    private static string[] Normalize(string arguments) => [.. arguments.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>型引数が名前か数だけか (式や入れ子の型引数は綴りの違いが型の違いとは限らない)。</summary>
    private static bool IsPlainName(string argument) => argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
