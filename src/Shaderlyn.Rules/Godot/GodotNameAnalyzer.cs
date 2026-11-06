using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Godot;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の、組み込みの名前の宣言し直し (GD0405) と、関数の外の名前を隠す局所変数 (CF0314) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// Godot 4.7.2 の <c>has_builtin</c> と <c>_find_identifier</c> による宣言の検査を写し、Godot に読ませて確かめた。
/// <list type="bullet">
/// <item>関数の外の宣言 (const・uniform・varying)・関数・仮引数には、そのシェーダーの種類のどの関数の組み込みの変数と定数の名前も使えない
/// (<c>spatial</c> の仮引数 <c>ALBEDO</c> も誤り)。関数には組み込み関数の名前 (<c>sin</c>) も使えない</item>
/// <item>局所変数には、その関数で使える組み込みの名前 (<c>fragment</c> の <c>TIME</c>、利用者の関数の <c>PI</c>) と、
/// 関数の外の const・uniform・varying の名前が使えない。外側の局所変数と同じ名前は使える</item>
/// </list>
/// 構造体の名前は組み込みの名前と同じでもよい。構造体のメンバーの名前の重複も CF0314 として報告する。
/// </para>
/// </remarks>
internal sealed class GodotNameAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GodotRuleDescriptors.BuiltinRedefinition, HlslRuleDescriptors.Redeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken token, params object[] arguments)
        {
            if (compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, arguments));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (program.Tree.Language is not GodotLanguageOptions { ShaderType: { } shaderType }
                || compilation.TypeSystem.ForTree(program.Tree) is not GodotTypeSystem types)
            {
                continue;
            }

            HashSet<string> builtins = [.. GodotBuiltins.Instance.GetVariables(shaderType).Select(v => v.Name)];
            Dictionary<string, ShaderSyntaxToken> globals = new(StringComparer.Ordinal);

            void Builtin(ShaderSyntaxToken name, bool function = false)
            {
                if (builtins.Contains(name.Text))
                {
                    Report(GodotRuleDescriptors.BuiltinRedefinition, name, $"'{name.Text}' は組み込みの名前なので、宣言し直せません。");
                }
                else if (function && GodotBuiltins.Instance.Functions.ContainsKey(name.Text))
                {
                    Report(GodotRuleDescriptors.BuiltinRedefinition, name, $"'{name.Text}' は組み込み関数の名前なので、関数の名前にできません。");
                }
            }

            foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                switch (declaration)
                {
                    // 構造体のメンバーの名前の重複。
                    case StructDeclarationSyntax structure:
                        Dictionary<string, ShaderSyntaxToken> members = new(StringComparer.Ordinal);

                        foreach (VariableDeclaratorSyntax member in structure.Members.OfType<VariableDeclarationSyntax>().SelectMany(m => m.Variables))
                        {
                            if (!members.TryAdd(member.Name, member.NameToken) && members[member.Name].GetLocation() is { } first)
                            {
                                Report(HlslRuleDescriptors.Redeclaration, member.NameToken, member.Name,
                                    (first.LineSpan.Start.Line + 1).ToString(CultureInfo.InvariantCulture));
                            }
                        }

                        break;

                    case VariableDeclarationSyntax variable:
                        foreach (VariableDeclaratorSyntax declarator in variable.Variables)
                        {
                            Builtin(declarator.NameToken);
                            globals.TryAdd(declarator.Name, declarator.NameToken);
                        }

                        break;

                    case FunctionDeclarationSyntax function:
                        Builtin(function.NameToken, function: true);

                        foreach (ParameterSyntax parameter in function.ParameterList)
                        {
                            Builtin(parameter.NameToken);
                        }

                        if (function.Body is null)
                        {
                            break;
                        }

                        foreach (VariableDeclarationSyntax local in function.Body.DescendantNodesAndSelf().OfType<LocalDeclarationStatementSyntax>()
                                     .Select(l => l.Declaration).OfType<VariableDeclarationSyntax>()
                                     .Concat(function.Body.DescendantNodesAndSelf().OfType<ForStatementSyntax>().Select(f => f.Initializer).OfType<VariableDeclarationSyntax>()))
                        {
                            foreach (VariableDeclaratorSyntax declarator in local.Variables)
                            {
                                if (types.IsBuiltinVariableAvailable(declarator.Name, function.Name))
                                {
                                    Report(GodotRuleDescriptors.BuiltinRedefinition, declarator.NameToken,
                                        $"'{declarator.Name}' は組み込みの名前なので、宣言し直せません。");
                                }
                                else if (globals.TryGetValue(declarator.Name, out ShaderSyntaxToken? global)
                                         && global.GetLocation() is { } location)
                                {
                                    Report(HlslRuleDescriptors.Redeclaration, declarator.NameToken, declarator.Name,
                                        (location.LineSpan.Start.Line + 1).ToString(CultureInfo.InvariantCulture));
                                }
                            }
                        }

                        break;
                }
            }
        }
    }
}
