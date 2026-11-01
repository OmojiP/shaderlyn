using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// GLSL のプリプロセッサの指令を検査する (GL0003)。glslang 16.6.0 の preprocessor/Pp.cpp の誤りを移したもの。
/// </summary>
/// <remarks>
/// <para>
/// ES では、<c>#if</c> / <c>#elif</c> の式に定義されていないマクロを書くと誤りである (<c>evalToToken</c> の
/// 「undefined macro in expression not allowed in es profile」)。<c>&amp;&amp;</c> の左が 0、<c>||</c> の左が 0 でないときの右のように、
/// 短絡で評価しない側は除く (<c>#if defined(A) &amp;&amp; A</c> は書ける)。
/// 前処理が記録したもの (<see cref="PreprocessResult.UndefinedConditionIdentifiers"/>) を報告する。
/// </para>
/// </remarks>
internal sealed class GlslPreprocessorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.PreprocessorDirective];

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
            if (program.Tree.PreprocessResult.UndefinedConditionIdentifiers.IsDefaultOrEmpty
                || GlslRequirements.Create(context, compilation, program, reported) is not { } requirement)
            {
                continue;
            }

            foreach (UndefinedConditionIdentifier undefined in program.Tree.PreprocessResult.UndefinedConditionIdentifiers)
            {
                requirement.Report(
                    GlslRuleDescriptors.PreprocessorDirective,
                    undefined.Directive,
                    $"ES では、#if / #elif の式に定義されていないマクロ '{undefined.Name}' を書けません。");
            }
        }
    }
}
