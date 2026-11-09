using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の属性の置き場所 (WG0101) と、<c>@must_use</c> の関数の戻り値 (WG0338) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// WG0101: <c>@must_use</c> は値を返す関数にだけ書ける (変数・構造体のメンバー・値を返さない関数には書けない)。同じ属性を 2 度は書けない。
/// 構造体のメンバーに書けるのは、<c>@align</c>・<c>@size</c>・<c>@location</c>・<c>@builtin</c>・<c>@interpolate</c>・<c>@invariant</c>・<c>@blend_src</c>・<c>@per_primitive</c> だけである
/// (naga 30 の「unknown attribute」「only valid on function declarations」「repeated attribute」)。
/// </para>
/// <para>
/// WG0338: <c>@must_use</c> の利用者の関数を、戻り値を使わない文 (<c>f();</c>) として呼んではならない。
/// </para>
/// </remarks>
internal sealed class WgslAttributeAnalyzer : SemanticRuleAnalyzer
{
    private static readonly string[] MemberAttributes = ["align", "size", "location", "builtin", "interpolate", "invariant", "blend_src", "per_primitive"];

    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [WgslRuleDescriptors.AttributeNotAllowed, WgslRuleDescriptors.UnusedMustUseResult];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, string message)
        {
            if (token is { IsMissing: false } && compilation.IsReportable(token) && token.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;
            HashSet<string> mustUse = new(StringComparer.Ordinal);

            foreach (DeclarationSyntax declaration in root.Declarations)
            {
                switch (declaration)
                {
                    case FunctionDeclarationSyntax function:
                    {
                        ImmutableArray<WgslAttribute> attributes = [.. function.Attributes.Select(WgslAttribute.From)];
                        CheckRepeated(attributes, Report);

                        if (attributes.FirstOrDefault(a => a.Name.Text == "must_use") is { Name: { } must })
                        {
                            if (function.ReturnType.Name == "void")
                            {
                                Report(WgslRuleDescriptors.AttributeNotAllowed, must, $"@must_use の関数 '{function.Name}' は値を返しません。戻り値の型を書くか、@must_use を外してください。");
                            }
                            else
                            {
                                mustUse.Add(function.Name);
                            }
                        }

                        foreach (ParameterSyntax parameter in function.ParameterList)
                        {
                            CheckRepeated(WgslAttribute.Read(parameter.ModifierTokens), Report);
                        }

                        break;
                    }

                    case VariableDeclarationSyntax global:
                    {
                        ImmutableArray<WgslAttribute> attributes = WgslAttribute.Read(global.ModifierTokens);
                        CheckRepeated(attributes, Report);

                        if (attributes.FirstOrDefault(a => a.Name.Text == "must_use") is { Name: { } must })
                        {
                            Report(WgslRuleDescriptors.AttributeNotAllowed, must, "@must_use は、値を返す関数にだけ書けます。");
                        }

                        break;
                    }

                    case StructDeclarationSyntax structure:
                        foreach (VariableDeclarationSyntax member in structure.Members.OfType<VariableDeclarationSyntax>())
                        {
                            ImmutableArray<WgslAttribute> attributes = WgslAttribute.Read(member.ModifierTokens);
                            CheckRepeated(attributes, Report);

                            foreach (WgslAttribute attribute in attributes.Where(a => !MemberAttributes.Contains(a.Name.Text)))
                            {
                                Report(WgslRuleDescriptors.AttributeNotAllowed, attribute.Name, $"構造体のメンバーに @{attribute.Name.Text} は書けません。");
                            }
                        }

                        break;
                }
            }

            if (mustUse.Count == 0)
            {
                continue;
            }

            foreach (ExpressionStatementSyntax statement in root.DescendantNodesAndSelf().OfType<ExpressionStatementSyntax>())
            {
                if (statement.Expression is InvocationExpressionSyntax { TargetName: { } name, Target: IdentifierExpressionSyntax target } && mustUse.Contains(name))
                {
                    Report(WgslRuleDescriptors.UnusedMustUseResult, target.Identifier,
                        $"関数 '{name}' は @must_use なので、戻り値を使ってください (_ = {name}(…); と書くか、let で受けてください)。");
                }
            }
        }
    }

    /// <summary>同じ属性を 2 度書いていないか。</summary>
    private static void CheckRepeated(ImmutableArray<WgslAttribute> attributes, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (WgslAttribute attribute in attributes)
        {
            if (!seen.Add(attribute.Name.Text))
            {
                report(WgslRuleDescriptors.AttributeNotAllowed, attribute.Name, $"@{attribute.Name.Text} を 2 度書いています。");
            }
        }
    }
}
