using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の型のメモリの配置 (WG0103) と大きさ (WG0339) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// WG0103: 構造体のメンバーの <c>@align(n)</c> は 2 の累乗で、型の揃え以上でなければならない。<c>@size(n)</c> は型の大きさ以上でなければならない。
/// <c>var&lt;uniform&gt;</c> の配列 (構造体のメンバーの中も) の要素の間隔は 16 の倍数でなければならない。
/// </para>
/// <para>
/// WG0339: 配列・構造体の大きさは 2147483647 バイトまで (naga の「type is too large」)。メンバーや要素が大きすぎればそこで、全体が大きすぎれば型の名前の位置で報告する。
/// </para>
/// </remarks>
internal sealed class WgslLayoutAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [WgslRuleDescriptors.InvalidLayout, WgslRuleDescriptors.TypeTooLarge];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Id)> reported = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, string message)
        {
            if (token is { IsMissing: false }
                && compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start, descriptor.Id)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;
            WgslTypes types = new(root);
            WgslLayout layout = new(types, new WgslConstants(compilation.GetExpressionTypeBinder(program)));

            foreach (DeclarationSyntax declaration in root.Declarations)
            {
                switch (declaration)
                {
                    case StructDeclarationSyntax structure:
                        CheckStruct(types, layout, structure, Report);
                        break;

                    case TypedefDeclarationSyntax { Keyword.Text: "alias", Tokens: [{ } name, { Kind: ShaderSyntaxKind.EqualsToken }, .. var aliased] }:
                        CheckSize(layout, types.Parse([.. aliased]), name, Report);
                        break;

                    case VariableDeclarationSyntax global when global.Type.NameToken.Text is not ("var" or "let" or "const" or "override"):
                    {
                        WgslTypeRef type = types.Of(global.Type);
                        CheckSize(layout, type, global.Type.NameToken, Report);

                        if (global.ModifierTokens.SkipWhile(t => t.Text != "var").Skip(2).FirstOrDefault() is { Text: "uniform" })
                        {
                            CheckUniformStride(types, layout, type, global.Type.NameToken, Report, 0);
                        }

                        break;
                    }
                }
            }
        }
    }

    /// <summary>構造体のメンバーの @align / @size と、構造体の大きさ。</summary>
    private static void CheckStruct(WgslTypes types, WgslLayout layout, StructDeclarationSyntax structure, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        bool memberTooLarge = false;

        foreach (VariableDeclarationSyntax member in structure.Members.OfType<VariableDeclarationSyntax>())
        {
            ImmutableArray<WgslAttribute> attributes = WgslAttribute.Read(member.ModifierTokens);
            WgslTypeLayout? natural = layout.MemberLayout(member);
            string name = member.Variables.FirstOrDefault()?.Name ?? string.Empty;

            if (attributes.FirstOrDefault(a => a.Name.Text == "align") is { Name: not null } alignAttribute && layout.Number(attributes, "align") is { } align)
            {
                if (align <= 0 || (align & (align - 1)) != 0)
                {
                    report(WgslRuleDescriptors.InvalidLayout, alignAttribute.Arguments.FirstOrDefault(), $"メンバー '{name}' の @align({align}) は 2 の累乗でなければなりません。");
                }
                else if (natural is { } required && align < required.Align)
                {
                    report(WgslRuleDescriptors.InvalidLayout, alignAttribute.Arguments.FirstOrDefault(), $"メンバー '{name}' の @align({align}) は、型の揃え ({required.Align}) 以上でなければなりません。");
                }
            }

            if (attributes.FirstOrDefault(a => a.Name.Text == "size") is { Name: not null } sizeAttribute && layout.Number(attributes, "size") is { } size
                && natural is { } minimum && size < minimum.Size)
            {
                report(WgslRuleDescriptors.InvalidLayout, sizeAttribute.Arguments.FirstOrDefault(), $"メンバー '{name}' の @size({size}) は、型の大きさ ({minimum.Size}) 以上でなければなりません。");
            }

            if (natural is { Size: > WgslLayout.MaxSize })
            {
                memberTooLarge = true;
                report(WgslRuleDescriptors.TypeTooLarge, member.Type.NameToken, $"メンバー '{name}' の型 {types.Of(member.Type)} が大きすぎます (2147483647 バイトまで)。");
            }
        }

        if (!memberTooLarge && structure.NameToken is { } structName
            && layout.Of(new WgslTypeRef(structName.Text, [], [structName])) is { Size: > WgslLayout.MaxSize })
        {
            report(WgslRuleDescriptors.TypeTooLarge, structName, $"構造体 {structName.Text} が大きすぎます (2147483647 バイトまで)。");
        }
    }

    /// <summary>型の大きさ (要素の中から先に見る)。</summary>
    private static bool CheckSize(WgslLayout layout, WgslTypeRef type, ShaderSyntaxToken at, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        foreach (WgslTypeRef argument in type.Arguments.Where(a => a.Name.Length > 0))
        {
            if (CheckSize(layout, argument, argument.First ?? at, report))
            {
                return true;
            }
        }

        if (type.Name == "array" && layout.Of(type) is { Size: > WgslLayout.MaxSize })
        {
            report(WgslRuleDescriptors.TypeTooLarge, type.First ?? at, $"型 {type} が大きすぎます (2147483647 バイトまで)。");
            return true;
        }

        return false;
    }

    /// <summary>var&lt;uniform&gt; の配列の要素の間隔 (16 の倍数)。</summary>
    private static void CheckUniformStride(WgslTypes types, WgslLayout layout, WgslTypeRef type, ShaderSyntaxToken at, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report, int depth)
    {
        if (depth > 16)
        {
            return;
        }

        if (type.Name == "array")
        {
            if (layout.StrideOf(type) is { } stride && stride % 16 != 0)
            {
                report(WgslRuleDescriptors.InvalidLayout, at, $"var<uniform> の配列の要素の間隔は 16 の倍数でなければなりませんが、{type} の間隔は {stride} です。要素を vec4 などにするか、@align(16) の構造体にしてください。");
                return;
            }

            if (type.Arguments.Length >= 1)
            {
                CheckUniformStride(types, layout, type.Arguments[0], at, report, depth + 1);
            }

            return;
        }

        if (types.MembersOf(type.Name) is { } members)
        {
            foreach ((_, WgslTypeRef member, VariableDeclarationSyntax declaration) in members)
            {
                CheckUniformStride(types, layout, member, declaration.Type.NameToken, report, depth + 1);
            }
        }
    }
}
