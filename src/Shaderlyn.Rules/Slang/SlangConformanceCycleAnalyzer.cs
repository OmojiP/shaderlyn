using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang のインターフェイスの適合の循環を検査する (SG0342)。
/// </summary>
/// <remarks>
/// <para>
/// インターフェイス I に適合する構造体のフィールド (配列・<c>Optional&lt;J&gt;</c>・入れ子の構造体のフィールドを含む) に、インターフェイス型 J の値があれば、I から J へ辺を引く。
/// 辺をたどって自分に戻るインターフェイスは「interface has cyclic dependency on itself」(E41002)、
/// その輪の中のインターフェイスに適合し、フィールドが輪の中のインターフェイスへ戻る構造体は「type contains circular reference through conforming interface」(E41003) である。
/// どちらも、そのインターフェイスの値の大きさが決まらないため (動的なディスパッチ)。
/// </para>
/// </remarks>
internal sealed class SlangConformanceCycleAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.CyclicConformance];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            StructDeclarationSyntax[] all = [.. program.Tree.Root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>().Where(s => s.NameToken is not null)];
            Dictionary<string, StructDeclarationSyntax> interfaces = new(StringComparer.Ordinal);
            Dictionary<string, StructDeclarationSyntax> structs = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax declaration in all)
            {
                (declaration.Keyword.Text == "interface" ? interfaces : structs).TryAdd(declaration.NameToken!.Text, declaration);
            }

            if (interfaces.Count == 0)
            {
                continue;
            }

            // 構造体ごとの、フィールドからたどれるインターフェイスと、適合するインターフェイス。
            Dictionary<string, HashSet<string>> reached = structs.ToDictionary(s => s.Key, s => FieldInterfaces(s.Value, interfaces, structs), StringComparer.Ordinal);
            Dictionary<string, string[]> conforms = structs.ToDictionary(s => s.Key, s => Bases(s.Value).Where(interfaces.ContainsKey).ToArray(), StringComparer.Ordinal);

            Dictionary<string, HashSet<string>> edges = interfaces.Keys.ToDictionary(i => i, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

            foreach ((string name, string[] bases) in conforms)
            {
                foreach (string conformed in bases)
                {
                    edges[conformed].UnionWith(reached[name]);
                }
            }

            HashSet<string> cyclic = [.. interfaces.Keys.Where(i => Reaches(edges, i, i))];

            if (cyclic.Count == 0)
            {
                continue;
            }

            void Report(ShaderSyntaxToken token, string message)
            {
                if (compilation.IsReportable(token) && token.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.CyclicConformance, location, message));
                }
            }

            foreach (string name in cyclic)
            {
                Report(interfaces[name].NameToken!, $"インターフェイス '{name}' に適合する型のフィールドをたどると、'{name}' に戻ります。値の大きさが決まりません。");
            }

            foreach ((string name, string[] bases) in conforms)
            {
                if (bases.Any(cyclic.Contains) && reached[name].Any(i => cyclic.Contains(i) && bases.Any(b => b == i || Reaches(edges, i, b))))
                {
                    Report(structs[name].NameToken!, $"構造体 '{name}' のフィールドが、適合するインターフェイス ({string.Join("・", bases.Where(cyclic.Contains))}) へ循環して戻ります。");
                }
            }
        }
    }

    /// <summary>構造体のフィールド (入れ子の構造体のフィールドを含む) の、インターフェイス型。</summary>
    private static HashSet<string> FieldInterfaces(
        StructDeclarationSyntax structure,
        Dictionary<string, StructDeclarationSyntax> interfaces,
        Dictionary<string, StructDeclarationSyntax> structs)
    {
        HashSet<string> found = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        Stack<StructDeclarationSyntax> pending = new([structure]);

        while (pending.Count > 0)
        {
            StructDeclarationSyntax current = pending.Pop();

            foreach (VariableDeclarationSyntax field in current.Members.OfType<VariableDeclarationSyntax>())
            {
                if (field.ModifierTokens.Any(t => t.Text == "static") || field.Type.IsPointer)
                {
                    continue;
                }

                // Optional<J>・J[] の J も、値として持つ (ポインターは値を持たないので、static のフィールドと同じく外す)。
                IEnumerable<string> names = [field.Type.NameToken.Text, .. field.Type.TemplateArguments.Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text)];

                foreach (string name in names)
                {
                    if (interfaces.ContainsKey(name))
                    {
                        found.Add(name);
                    }
                    else if (structs.TryGetValue(name, out StructDeclarationSyntax? nested) && visited.Add(name))
                    {
                        pending.Push(nested);
                    }
                }
            }
        }

        return found;
    }

    /// <summary>構造体の基底の並び (<c>struct S : IA, IB</c> の IA と IB)。</summary>
    private static IEnumerable<string> Bases(StructDeclarationSyntax structure)
    {
        bool afterColon = false;
        int depth = 0;

        foreach (ShaderSyntaxToken token in structure.DescendantTokens())
        {
            if (token.Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                yield break;
            }

            switch (token.Kind)
            {
                case ShaderSyntaxKind.ColonToken when depth == 0:
                    afterColon = true;
                    continue;
                case ShaderSyntaxKind.LessThanToken:
                    depth++;
                    continue;
                case ShaderSyntaxKind.GreaterThanToken:
                    depth--;
                    continue;
                case ShaderSyntaxKind.IdentifierToken when afterColon && depth == 0:
                    yield return token.Text;
                    continue;
            }
        }
    }

    private static bool Reaches(Dictionary<string, HashSet<string>> edges, string from, string target)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> pending = new(edges[from]);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            if (current == target)
            {
                return true;
            }

            if (seen.Add(current) && edges.TryGetValue(current, out HashSet<string>? next))
            {
                foreach (string n in next)
                {
                    pending.Push(n);
                }
            }
        }

        return false;
    }
}
