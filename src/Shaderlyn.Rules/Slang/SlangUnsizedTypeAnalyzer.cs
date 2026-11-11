using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、大きさの決まらない型 (大きさを書かない配列 <c>int a[]</c> と、それを最後のフィールドに持つ構造体) の使い方を検査する (SG0308)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>構造体の、最後でないフィールドの大きさの決まらない型 (E30070「unsized member must be last」)</item>
/// <item>大きさの決まらない構造体をフィールドに持つ構造体 (E30072。slangc はその構造体の名前に報告する)</item>
/// <item>局所変数の、大きさの決まらない型 (初期値の無い <c>float a[]</c> と、大きさの決まらない構造体。E30071)</item>
/// <item>StructuredBuffer などの要素の、大きさの決まらない構造体 (E30031)</item>
/// <item>定数バッファー (cbuffer) のメンバーの、大きさを書かない配列 (E31215)</item>
/// </list>
/// 関数の外の <c>int a[];</c> (大きさを決めない資源の配列) は通る。
/// </remarks>
internal sealed class SlangUnsizedTypeAnalyzer : SemanticRuleAnalyzer
{
    private static readonly string[] StructuredBuffers =
        ["StructuredBuffer", "RWStructuredBuffer", "AppendStructuredBuffer", "ConsumeStructuredBuffer", "RasterizerOrderedStructuredBuffer"];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.UnsizedType, SlangRuleDescriptors.InvalidArraySize];

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

            void Report(ShaderSyntaxToken? at, string message) => ReportWith(SlangRuleDescriptors.UnsizedType, at, message);

            void ReportWith(DiagnosticDescriptor descriptor, ShaderSyntaxToken? at, string message)
            {
                if (at is { IsMissing: false } && compilation.IsReportable(at) && at.GetLocation() is { } location)
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

            Dictionary<string, bool> unsized = new(StringComparer.Ordinal);

            bool IsUnsizedStruct(string name, int depth)
            {
                if (unsized.TryGetValue(name, out bool known))
                {
                    return known;
                }

                if (depth > 16 || !structs.TryGetValue(name, out StructDeclarationSyntax? structure) || structure is null)
                {
                    return false;
                }

                unsized[name] = false;
                (VariableDeclarationSyntax Declaration, VariableDeclaratorSyntax Variable)[] fields = [.. Fields(structure)];
                bool result = fields.Length > 0 && IsUnsized(fields[^1].Declaration.Type, fields[^1].Variable, depth + 1);
                unsized[name] = result;
                return result;
            }

            bool IsUnsized(TypeSyntax type, VariableDeclaratorSyntax variable, int depth)
                => OutermostRankIsEmpty(variable, type) || (!variable.IsArray && type.NestedTypeTokens.IsDefaultOrEmpty && IsUnsizedStruct(type.Name, depth));

            foreach (StructDeclarationSyntax structure in structs.Values.OfType<StructDeclarationSyntax>())
            {
                var fields = Fields(structure).ToArray();
                bool nested = false;

                for (int i = 0; i < fields.Length; i++)
                {
                    (VariableDeclarationSyntax declaration, VariableDeclaratorSyntax variable) = fields[i];

                    if (!IsUnsized(declaration.Type, variable, 0))
                    {
                        continue;
                    }

                    if (!variable.IsArray)
                    {
                        nested = true;
                    }

                    if (i < fields.Length - 1)
                    {
                        Report(variable.NameToken, $"大きさの決まらない型のフィールド '{variable.Name}' は、構造体の最後のフィールドにしか置けません。");
                    }
                }

                if (nested)
                {
                    Report(structure.NameToken, $"構造体 {structure.Name} は、大きさの決まらない構造体をフィールドに持てません。大きさを決めない配列は、外側の構造体に直接置いてください。");
                }
            }

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                // 配列の大きさの、0 での割り算と負の数 (int a[1 / 0]・-> int[-1])。
                ImmutableArray<ShaderSyntaxToken> ranks = node switch
                {
                    VariableDeclaratorSyntax declarator => declarator.ArrayRankTokens,
                    TypeSyntax type => type.ArrayRanks,
                    _ => [],
                };

                for (int i = 1; !ranks.IsDefaultOrEmpty && i + 1 < ranks.Length; i++)
                {
                    if (ranks[i].Kind is ShaderSyntaxKind.SlashToken or ShaderSyntaxKind.PercentToken && ranks[i + 1] is { Kind: ShaderSyntaxKind.NumericLiteralToken, Text: "0" })
                    {
                        ReportWith(SlangRuleDescriptors.InvalidArraySize, ranks[i], "配列の大きさの式が 0 で割っています。");
                    }
                    else if (ranks[i].Kind == ShaderSyntaxKind.MinusToken && ranks[i - 1].Kind == ShaderSyntaxKind.OpenBracketToken
                             && ranks[i + 1].Kind == ShaderSyntaxKind.NumericLiteralToken && i + 2 < ranks.Length && ranks[i + 2].Kind == ShaderSyntaxKind.CloseBracketToken)
                    {
                        ReportWith(SlangRuleDescriptors.InvalidArraySize, ranks[i], "配列の大きさは負の数にできません。");
                    }
                }

                switch (node)
                {
                    case LocalDeclarationStatementSyntax { Declaration: VariableDeclarationSyntax declaration }:
                        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                        {
                            bool unsizedArray = OutermostRankIsEmpty(variable, declaration.Type) && variable.Initializer is null;
                            bool unsizedStruct = !variable.IsArray && declaration.Type.NestedTypeTokens.IsDefaultOrEmpty && IsUnsizedStruct(declaration.Type.Name, 0);

                            if (unsizedArray || unsizedStruct)
                            {
                                Report(variable.NameToken, unsizedArray
                                    ? $"局所変数の '{variable.Name}' は、大きさを書かない配列にできません。大きさを書くか、初期値を書いてください。"
                                    : $"局所変数の '{variable.Name}' の型 {declaration.Type.Name} は、大きさの決まらない構造体です (最後のフィールドが大きさを決めない配列)。");
                            }
                        }

                        break;

                    case TypeSyntax type when StructuredBuffers.Contains(type.NameToken.Text)
                                              && !type.TemplateArguments.IsDefault
                                              && type.TemplateArguments is [{ Kind: ShaderSyntaxKind.IdentifierToken } element]
                                              && IsUnsizedStruct(element.Text, 0):
                        Report(type.NameToken, $"{type.NameToken.Text} の要素に、大きさの決まらない構造体 {element.Text} は使えません。");
                        break;

                    case ConstantBufferDeclarationSyntax buffer:
                        foreach (VariableDeclarationSyntax member in buffer.Members.OfType<VariableDeclarationSyntax>())
                        {
                            foreach (VariableDeclaratorSyntax variable in member.Variables.Where(v => OutermostRankIsEmpty(v, member.Type)))
                            {
                                Report(variable.NameToken, $"定数バッファーのメンバー '{variable.Name}' は、大きさを書かない配列にできません。");
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary>構造体の static でないフィールドを、書いた順に並べる。</summary>
    private static IEnumerable<(VariableDeclarationSyntax Declaration, VariableDeclaratorSyntax Variable)> Fields(StructDeclarationSyntax structure)
        => structure.Members.OfType<VariableDeclarationSyntax>()
            .Where(d => !d.ModifierTokens.Any(t => t.Text is "static" or "const"))
            .SelectMany(d => d.Variables.Select(v => (d, v)));

    /// <summary>いちばん外側の次元が大きさを書かない配列 (<c>a[]</c>・<c>int[] a</c>) か。</summary>
    private static bool OutermostRankIsEmpty(VariableDeclaratorSyntax variable, TypeSyntax type)
    {
        ImmutableArray<ShaderSyntaxToken> ranks = variable.ArrayRankTokens.IsDefaultOrEmpty ? type.ArrayRanks : variable.ArrayRankTokens;
        return ranks.Length >= 2 && ranks[0].Kind == ShaderSyntaxKind.OpenBracketToken && ranks[1].Kind == ShaderSyntaxKind.CloseBracketToken;
    }
}
