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
/// 16 ビットより小さい浮動小数の型 (<c>bfloat16_t</c>、<c>floate5m2_t</c>、<c>floate4m3_t</c>、<c>floate2m1_t</c> などのマイクロスケーリングの型) の使い方を検査する
/// (GL0403 / GL0303 / GL0302)。glslang 16.6.0 の ParseHelper.cpp のこれらの型の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// これらの型 (とそれを含む構造体) は入力と出力にできない。8 ビットより小さい型 (<c>floate2m1_t</c>・<c>floate3m2_t</c>・<c>floate2m3_t</c>) は、
/// ブロックのメンバーと shared の変数にもできない。
/// </para>
/// <para>
/// マイクロスケーリングの型の値の作成の実引数は、特殊化でない定数でなければならない。
/// <c>bfloat16_t</c> と 8 ビットの浮動小数の型は、<c>bool</c> との間で値を作れない。
/// </para>
/// </remarks>
internal sealed class GlslSmallFloatAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.TypeNotAllowedHere, GlslRuleDescriptors.ConstantArgumentRequired, GlslRuleDescriptors.NoMatchingBuiltin];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.CodeTokens.Any(t => IsSmallFloat(t.Text)))
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (VariableDeclarationSyntax or InvocationExpressionSyntax))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            switch (node)
            {
                case VariableDeclarationSyntax declaration when declaration.Variables.FirstOrDefault() is { } first:
                    CheckDeclaration(requirement, program, declaration, first);
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "saturatedConvertEXT" } callee } call
                    when compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty:
                    CheckSaturatedConvert(requirement, compilation.GetExpressionTypeBinder(program), callee, [.. call.ArgumentExpressions]);
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee, OpenAngle: null } call
                    when call.ArgumentExpressions.Count() == 1 && compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty:
                    CheckConstructor(requirement, compilation.GetExpressionTypeBinder(program), callee, call.ArgumentExpressions.First());
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>宣言の記憶域と、ブロックのメンバー。</summary>
    private static void CheckDeclaration(GlslRequirements requirement, AnalyzedProgram program, VariableDeclarationSyntax declaration, VariableDeclaratorSyntax first)
    {
        string type = declaration.Type.Name;
        bool small = ContainsSmallFloat(program, type, subByte: false, 0);
        bool subByte = ContainsSmallFloat(program, type, subByte: true, 0);

        if (!small)
        {
            return;
        }

        if (declaration.Parent is CompilationUnitSyntax && declaration.ModifierTokens.Any(t => t.Text is "in" or "out"))
        {
            requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, first.NameToken, first.Name, $"16 ビットより小さい浮動小数の型 ({type}) とそれを含む構造体は、入力と出力にできません。");
        }

        if (subByte && declaration.ModifierTokens.Any(t => t.Text == "shared"))
        {
            requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, first.NameToken, first.Name, $"8 ビットより小さい型 ({type}) とそれを含む構造体は、shared の変数にできません。");
        }

        if (subByte && declaration.Parent is InterfaceBlockDeclarationSyntax or StructDeclarationSyntax { Keyword.Text: not "struct" })
        {
            requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, first.NameToken, first.Name, $"ブロックのメンバーは、8 ビットより小さい型 ({type}) とそれを含む構造体にできません。");
        }
    }

    /// <summary>
    /// saturatedConvertEXT (to, from)。実引数は 2 つで、1 つ目は 8 ビットの浮動小数 (floate5m2_t / floate4m3_t) の、2 つ目はそれ以外の型の、
    /// 形 (スカラー・ベクトルの成分の数、協調行列のスコープと行と列の数) の同じ値でなければならない (glslang の EOpConstructSaturated の検査)。
    /// </summary>
    private static void CheckSaturatedConvert(GlslRequirements requirement, ExpressionTypeBinder binder, IdentifierExpressionSyntax callee, ExpressionSyntax[] arguments)
    {
        (string Base, string Shape)?[] shapes = [.. arguments.Select(a => ShapeOf(binder, a))];

        if (shapes.Any(s => s is null))
        {
            return;
        }

        string described = string.Join(", ", arguments.Select(a => binder.GetTypesByConfiguration(a) is [{ Type: { } type }] ? type : "?"));

        if (arguments.Length != 2
            || shapes[0]!.Value.Base is not ("floate5m2_t" or "floate4m3_t")
            || shapes[1]!.Value.Base is "floate5m2_t" or "floate4m3_t"
            || shapes[0]!.Value.Shape != shapes[1]!.Value.Shape)
        {
            requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, callee.Identifier, callee.Name, described);
        }
    }

    /// <summary>値の要素の型と形。協調行列は型の引数から、ほかは数の型から求める。分からなければ <see langword="null"/>。</summary>
    private static (string Base, string Shape)? ShapeOf(ExpressionTypeBinder binder, ExpressionSyntax argument)
    {
        if (argument is IdentifierExpressionSyntax identifier
            && binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax { IsArray: false, Parent: VariableDeclarationSyntax { Type: { Name: "coopmat", OpenAngle: not null } type } } }])
        {
            string[] parameters = string.Concat(type.TemplateArguments.Select(t => t.Text)).Split(',');
            return parameters.Length == 5 ? (parameters[0], "coopmat " + string.Join(",", parameters[1..4])) : null;
        }

        return binder.GetTypesByConfiguration(argument) is [{ Type: { } name }] && binder.TypeSystem.TryDescribeNumeric(name, out ShaderNumericShape shape)
            ? (shape.BaseName, $"{shape.Kind} {shape.Rows}x{shape.Columns}")
            : null;
    }

    /// <summary>値の作成。</summary>
    private static void CheckConstructor(GlslRequirements requirement, ExpressionTypeBinder binder, IdentifierExpressionSyntax callee, ExpressionSyntax argument)
    {
        string name = callee.Name;

        // マイクロスケーリングの型の値は、特殊化でない定数からしか作れない (glslang の「constructor argument must be a non-spec constant」)。
        if (name is "floate2m1_t" or "floate3m2_t" or "floate2m3_t" or "floatue8m0_t" or "floatmxint8_t"
            && new GlslConstantEvaluator(binder).Evaluate(argument) is null
            && !IsConstant(binder, argument))
        {
            requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, callee.Identifier, $"{name} の値の作成の実引数 (特殊化でない定数)");
            return;
        }

        if (binder.GetTypesByConfiguration(argument) is not [{ Type: { } source }])
        {
            return;
        }

        // bfloat16_t と 8 ビットの浮動小数の型は、bool との間で値を作れない。
        bool limited = IsLimited(name) || IsLimited(source);
        bool boolean = name == "bool" || source == "bool";

        if (limited && boolean && name != source)
        {
            requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, callee.Identifier, name, source);
        }
    }

    /// <summary>const の変数か (特殊化の定数を除く)。</summary>
    private static bool IsConstant(ExpressionTypeBinder binder, ExpressionSyntax argument)
        => argument is IdentifierExpressionSyntax identifier
           && binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }]
           && declaration.HasModifier("const")
           && !declaration.ModifierTokens.Any(t => t.Text == "constant_id");

    /// <summary>型が、16 ビットより小さい浮動小数の型 (subByte なら 8 ビットより小さい型) を含むか。構造体のメンバーも見る。</summary>
    private static bool ContainsSmallFloat(AnalyzedProgram program, string type, bool subByte, int depth)
    {
        if (subByte ? IsSubByte(type) : IsSmallFloat(type))
        {
            return true;
        }

        return depth < 8
               && program.Structs.FirstOrDefault(s => s.Name == type && s.Keyword.Text == "struct") is { } structure
               && structure.Fields.Any(f => ContainsSmallFloat(program, f.Type.Name, subByte, depth + 1));
    }

    /// <summary>16 ビットより小さい浮動小数の型 (とそのベクトル)、bfloat16_t か。</summary>
    private static bool IsSmallFloat(string type) => IsLimited(type) || IsMicroscaling(type);

    /// <summary>bfloat16_t と 8 ビットの浮動小数の型 (とそのベクトル)。</summary>
    private static bool IsLimited(string type)
        => type.StartsWith("bfloat16", StringComparison.Ordinal) || type.StartsWith("bf16vec", StringComparison.Ordinal)
           || type.Contains("e5m2", StringComparison.Ordinal) || type.Contains("e4m3", StringComparison.Ordinal);

    /// <summary>マイクロスケーリングの型 (とそのベクトル)。</summary>
    private static bool IsMicroscaling(string type)
        => type.Contains("e2m1", StringComparison.Ordinal) || type.Contains("e3m2", StringComparison.Ordinal) || type.Contains("e2m3", StringComparison.Ordinal)
           || type.Contains("ue8m0", StringComparison.Ordinal) || type.Contains("mxint8", StringComparison.Ordinal);

    /// <summary>8 ビットより小さい型 (4 ビットの e2m1、6 ビットの e3m2 と e2m3)。</summary>
    private static bool IsSubByte(string type)
        => type.Contains("e2m1", StringComparison.Ordinal) || type.Contains("e3m2", StringComparison.Ordinal) || type.Contains("e2m3", StringComparison.Ordinal);
}
