using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 同じ形 (名前と仮引数の型) の関数の宣言どうしの食い違いを検査する (GL0323)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>handleFunctionDeclarator</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 仮引数の型が同じ前の宣言 (プロトタイプか定義) と比べる。仮引数の記憶域 (<c>in</c>・<c>const in</c>・<c>out</c>・<c>inout</c>) と、
/// 書いた精度の修飾子が違えば誤り。仮引数の型がすべて同じなのに戻り値の型が違っても誤り。
/// </para>
/// <para>
/// 精度は ES で、両方が書いたときだけ比べる (書かないときの精度は既定の精度で決まり、ここでは追わない。デスクトップでは精度は意味を持たない)。
/// 関数の外の宣言だけを見る。
/// </para>
/// </remarks>
internal sealed class GlslFunctionRedeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.FunctionRedeclarationMismatch];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (GlslRequirements.Create(context, compilation, program, reported) is not { } requirement)
            {
                continue;
            }

            Dictionary<string, FunctionDeclarationSyntax> first = new(StringComparer.Ordinal);
            Dictionary<string, int> controls = new(StringComparer.Ordinal);

            foreach (FunctionDeclarationSyntax function in program.Tree.Root.ChildNodes().OfType<FunctionDeclarationSyntax>())
            {
                ParameterSyntax[] parameters = [.. Parameters(function)];
                string key = function.Name + "(" + string.Join(",", parameters.Select(TypeKey)) + ")";

                // 関数の属性 (GL_EXT_function_control_attributes) は前の宣言の分と合わせ、inline と noinline の両方があれば誤り。
                int control = controls.GetValueOrDefault(key) | FunctionControl(compilation, function);
                controls[key] = control;

                if (control == (Inline | NoInline) && !function.Attributes.IsEmpty && compilation.IsReportable(function.NameToken))
                {
                    requirement.Report(GlslRuleDescriptors.FunctionRedeclarationMismatch, function.NameToken,
                        $"関数 '{function.Name}' の属性 inline と noinline は一緒に使えません (前の宣言の属性も合わせます)。");
                }

                if (!first.TryGetValue(key, out FunctionDeclarationSyntax? previous))
                {
                    first[key] = function;
                    continue;
                }

                if (!compilation.IsReportable(function.NameToken))
                {
                    continue;
                }

                ShaderSyntaxToken at = function.NameToken;
                ParameterSyntax[] before = [.. Parameters(previous)];

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (Storage(before[i]) != Storage(parameters[i]))
                    {
                        requirement.Report(GlslRuleDescriptors.FunctionRedeclarationMismatch, at,
                            $"関数 '{function.Name}' の {i + 1} つ目の仮引数の記憶域 ({Storage(parameters[i])}) が、前の宣言 ({Storage(before[i])}) と違います。");
                    }

                    if (requirement.IsEs && Precision(before[i]) is { } was && Precision(parameters[i]) is { } now && was != now)
                    {
                        requirement.Report(GlslRuleDescriptors.FunctionRedeclarationMismatch, at,
                            $"関数 '{function.Name}' の {i + 1} つ目の仮引数の精度 ({now}) が、前の宣言 ({was}) と違います。");
                    }
                }

                string returnType = ReturnKey(function);

                if (returnType != ReturnKey(previous))
                {
                    requirement.Report(GlslRuleDescriptors.FunctionRedeclarationMismatch, at,
                        $"関数 '{function.Name}' の戻り値の型 ({returnType}) が、同じ仮引数の前の宣言 ({ReturnKey(previous)}) と違います。");
                }
            }
        }
    }

    private const int Inline = 1;
    private const int NoInline = 2;

    /// <summary>関数の属性 ([[inline]] / [[noinline]]) の並び。属性の構文木は最初の名前だけを持つので、角括弧の中の字句を見る。</summary>
    private static int FunctionControl(ShaderCompilation compilation, FunctionDeclarationSyntax function)
    {
        int control = 0;

        foreach (AttributeSyntax attribute in function.Attributes)
        {
            foreach (ShaderSyntaxToken token in compilation.CodeTokens)
            {
                if (ReferenceEquals(token.Source, attribute.OpenBracket.Source)
                    && token.Span.Start >= attribute.OpenBracket.Span.Start && token.Span.End <= attribute.CloseBracket.Span.End)
                {
                    control |= token.Text switch
                    {
                        "inline" => Inline,
                        "noinline" or "dont_inline" => NoInline,
                        _ => 0,
                    };
                }
            }
        }

        return control;
    }

    /// <summary>仮引数。<c>(void)</c> は仮引数の無いものとする。</summary>
    private static IEnumerable<ParameterSyntax> Parameters(FunctionDeclarationSyntax function)
        => function.ParameterList.Where(p => !(p.Type.Name == "void" && p.NameToken.Text.Length == 0 && p.ArrayRankTokens.IsEmpty));

    /// <summary>仮引数の型 (配列の大きさを含む)。</summary>
    private static string TypeKey(ParameterSyntax parameter)
        => parameter.Type.Name + string.Concat(parameter.Type.ArrayRanks.Select(t => t.Text)) + "|" + string.Concat(parameter.ArrayRankTokens.Select(t => t.Text));

    /// <summary>戻り値の型 (配列の大きさを含む)。</summary>
    private static string ReturnKey(FunctionDeclarationSyntax function)
        => function.ReturnType.Name + string.Concat(function.ReturnType.ArrayRanks.Select(t => t.Text));

    /// <summary>仮引数の記憶域 (glslang の EvqIn・EvqConstReadOnly・EvqOut・EvqInOut)。</summary>
    private static string Storage(ParameterSyntax parameter)
    {
        string[] words = [.. Qualifiers(parameter)];

        return words.Contains("inout") ? "inout"
            : words.Contains("out") ? "out"
            : words.Contains("const") ? "const in"
            : "in";
    }

    /// <summary>書いた精度の修飾子。書いていなければ <see langword="null"/>。</summary>
    private static string? Precision(ParameterSyntax parameter)
        => Qualifiers(parameter).FirstOrDefault(t => t is "highp" or "mediump" or "lowp");

    /// <summary>括弧の外の修飾子の語。</summary>
    private static IEnumerable<string> Qualifiers(ParameterSyntax parameter)
    {
        int depth = 0;

        foreach (ShaderSyntaxToken token in parameter.ModifierTokens.Concat(parameter.Type.ModifierTokens))
        {
            depth += token.Kind switch
            {
                ShaderSyntaxKind.OpenParenToken => 1,
                ShaderSyntaxKind.CloseParenToken => -1,
                _ => 0,
            };

            if (depth == 0 && token.Kind == ShaderSyntaxKind.IdentifierToken)
            {
                yield return token.Text;
            }
        }
    }
}
