using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// サンプラーの作成 (<c>sampler2D(t, s)</c>) の実引数を検査する (GL0306 / GL0001)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>samplerConstructorLocationCheck</c> の前の、サンプラーの作成の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 実引数は 2 つで、1 つ目はサンプラーの型と同じ次元と値の型のテクスチャ (<c>sampler2DShadow</c> なら <c>texture2D</c>)、
/// 2 つ目は <c>sampler</c> か <c>samplerShadow</c> で、どちらも配列でないもの。
/// 実引数が 1 つの書き方 (ハンドルからの作成) は <c>GL_ARB_bindless_texture</c> が要る。
/// </para>
/// <para>型が分からない実引数は見ない。</para>
/// </remarks>
internal sealed class GlslSamplerConstructorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.VariableArgumentRequired, GlslRuleDescriptors.NotAvailable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation || !IsCombinedSampler(callee.Name))
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

            ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];
            string name = callee.Name;
            ShaderSyntaxToken at = callee.Identifier;
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            switch (arguments.Length)
            {
                case 0:
                    continue;

                case 1:
                    requirement.RequireExtensions(at, ["GL_ARB_bindless_texture"], $"'{name}' の作成に実引数を 1 つ渡す書き方");
                    continue;

                case > 2:
                    requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 3, "書けません。実引数はテクスチャとサンプラーの 2 つです");
                    continue;
            }

            if (TypeOf(binder, arguments[0]) is { } texture)
            {
                string expected = ExpectedTexture(name);

                if (!IsTexture(texture) || IsArrayValue(binder, arguments[0]))
                {
                    requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 1, $"配列でないテクスチャの型 ({expected}) でなければなりません (今は {texture})");
                    continue;
                }

                if (texture != expected)
                {
                    requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 1, $"{name} と次元と値の型が同じテクスチャ ({expected}) でなければなりません (今は {texture})");
                    continue;
                }
            }

            if (IsArrayValue(binder, arguments[1]))
            {
                requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 2, "配列でない sampler か samplerShadow でなければなりません (今は配列)");
            }
            else if (TypeOf(binder, arguments[1]) is { } sampler && sampler is not ("sampler" or "samplerShadow"))
            {
                requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, at, name, 2, $"配列でない sampler か samplerShadow でなければなりません (今は {sampler})");
            }
        }
    }

    /// <summary>テクスチャとサンプラーを組み合わせたサンプラーの型 (<c>sampler2D</c>、<c>isampler3D</c>、<c>samplerCubeShadow</c> など) か。</summary>
    private static bool IsCombinedSampler(string name)
        => name is not ("sampler" or "samplerShadow")
           && GlslKeywords.IsBuiltInType(name)
           && name.TrimStart('i', 'u').StartsWith("sampler", StringComparison.Ordinal)
           && !name.Contains("External", StringComparison.Ordinal);

    /// <summary>テクスチャの型か。</summary>
    private static bool IsTexture(string name)
        => GlslKeywords.IsBuiltInType(name) && name.TrimStart('i', 'u').StartsWith("texture", StringComparison.Ordinal);

    /// <summary>サンプラーの型に合うテクスチャの型 (<c>sampler2DShadow</c> なら <c>texture2D</c>)。</summary>
    private static string ExpectedTexture(string sampler)
    {
        string texture = sampler.Replace("sampler", "texture", StringComparison.Ordinal);
        return texture.EndsWith("Shadow", StringComparison.Ordinal) ? texture[..^"Shadow".Length] : texture;
    }

    /// <summary>実引数の型。分からないか構成で変わるなら <see langword="null"/>。</summary>
    private static string? TypeOf(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => binder.GetTypesByConfiguration(expression) is [{ Type: { } name }] ? name : null;

    /// <summary>配列そのものの値か。</summary>
    private static bool IsArrayValue(ExpressionTypeBinder binder, ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsArrayValue(binder, parenthesized.Expression),
        IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates.Any(c => c.Type.IsArray),
        _ => false,
    };
}
