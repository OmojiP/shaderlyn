using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
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
/// <para>
/// <c>#extension</c> の形 (<c>CPPextension</c> と Versions.cpp の <c>updateExtensionBehavior</c>) も見る。
/// 名前・<c>:</c>・振る舞い (<c>require</c> / <c>enable</c> / <c>warn</c> / <c>disable</c>) の順に書き、後ろには何も書けない。
/// <c>all</c> には <c>require</c> と <c>enable</c> を書けない。glslang が知らない拡張を <c>require</c> にした誤りは、知っている拡張の一覧が要るので見ない。
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
            if (GlslRequirements.Create(context, compilation, program, reported) is not { } requirement)
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

            foreach (RecordedDirective directive in program.Tree.PreprocessResult.RecordedDirectives)
            {
                if (directive.Directive.Text == "extension" && compilation.IsReportable(directive.Directive) && DescribeExtensionError(directive.Tokens) is { } message)
                {
                    requirement.Report(GlslRuleDescriptors.PreprocessorDirective, directive.Directive, message);
                }
            }
        }
    }

    /// <summary><c>#extension</c> の形の誤りを述べる。</summary>
    /// <param name="tokens">指令の名前の後ろのトークン。</param>
    /// <returns>誤りの説明。誤りが無ければ <see langword="null"/>。</returns>
    private static string? DescribeExtensionError(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        if (tokens.IsEmpty || tokens[0].Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return "#extension の後には拡張の名前が必要です。";
        }

        if (tokens.Length < 2 || tokens[1].Kind != ShaderSyntaxKind.ColonToken)
        {
            return $"#extension の拡張の名前 '{tokens[0].Text}' の後には ':' が必要です。";
        }

        if (tokens.Length < 3 || tokens[2].Text is not ("require" or "enable" or "warn" or "disable"))
        {
            string behavior = tokens.Length < 3 ? "(無し)" : $"'{tokens[2].Text}'";
            return $"#extension の振る舞い {behavior} は書けません。require・enable・warn・disable のどれかを書いてください。";
        }

        if (tokens[0].Text == "all" && tokens[2].Text is "require" or "enable")
        {
            return $"#extension all には '{tokens[2].Text}' を書けません。warn か disable を書いてください。";
        }

        return tokens.Length > 3 ? $"#extension の後ろに余分な記述があります: '{tokens[3].Text}'" : null;
    }
}
