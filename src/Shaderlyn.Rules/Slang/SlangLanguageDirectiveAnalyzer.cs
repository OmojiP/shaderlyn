using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の言語を選ぶ指令 (<c>#language</c>・<c>#lang</c>・GLSL の <c>#version</c>) を検査する (SG0366)。
/// </summary>
/// <remarks>
/// slangc 2026.19 で確認した条件。
/// <list type="bullet">
/// <item><c>#language</c> は <c>[slang] &lt;バージョン&gt;</c> と書く。バージョンは <c>2018</c>・<c>2025</c>・<c>2026</c>・<c>legacy</c> (2018)・<c>latest</c> (2026)。<c>202a</c> (2025)・<c>202b</c> (2026)・<c>202c</c>・<c>202e</c> も書ける。
/// 知らない数は E15207、知らない名前 (<c>glsl</c>・<c>hlsl</c>・大文字の <c>LATEST</c> など) は E15208、バージョンがないか整数でないものは E15206</item>
/// <item>1 つの翻訳単位 (#include で取り込んだファイルを含む) で、<c>#language</c> (Slang) と <c>#version</c> (GLSL) を両方書くと E00121</item>
/// <item>違うバージョンの <c>#language</c> を 2 つ書くと E00126 (<c>latest</c> と <c>2026</c>、<c>legacy</c> と <c>2018</c> は同じ)</item>
/// </list>
/// <c>__include</c> で取り込む別のファイルとの食い違い (E00123) と、<c>#version</c> で GLSL として解析すること (警告 E00120) は見ない。
/// </remarks>
internal sealed class SlangLanguageDirectiveAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.LanguageDirective];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        HashSet<ShaderSyntaxToken> reported = new(ReferenceEqualityComparer.Instance);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            RecordedDirective? firstLanguage = null;
            bool firstIsSlang = false;
            (RecordedDirective Directive, string Version)? firstVersion = null;

            foreach (RecordedDirective directive in program.Tree.PreprocessResult.RecordedDirectives)
            {
                string? version = null;
                bool isSlang;

                if (directive.Directive.Text == "version")
                {
                    isSlang = false;
                }
                else if (directive.Directive.Text is "language" or "lang")
                {
                    isSlang = true;

                    if (ParseVersion(directive.Tokens, out version) is { } error)
                    {
                        Report(directive.Directive, error);
                        continue;
                    }
                }
                else
                {
                    continue;
                }

                if (firstLanguage is { } first && firstIsSlang != isSlang)
                {
                    Report(directive.Directive,
                        $"1 つの翻訳単位に、Slang を選ぶ #language と GLSL を選ぶ #version を両方書けません ({Describe(first)} と食い違います)。"
                        + "Slang のファイルでは #version を消してください。");
                    continue;
                }

                if (firstLanguage is null)
                {
                    firstLanguage = directive;
                    firstIsSlang = isSlang;
                }

                if (version is null)
                {
                    continue;
                }

                if (firstVersion is { } previous && previous.Version != version)
                {
                    Report(directive.Directive,
                        $"1 つの翻訳単位に、違うバージョンの #language を書けません ({Describe(previous.Directive)} と食い違います)。");
                }
                else
                {
                    firstVersion ??= (directive, version);
                }
            }
        }

        void Report(ShaderSyntaxToken token, string message)
        {
            if (compilation.IsReportable(token) && reported.Add(token) && token.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.LanguageDirective, location, message));
            }
        }
    }

    /// <summary>指令の位置の説明 (「3 行目の #language」)。</summary>
    private static string Describe(RecordedDirective directive)
        => directive.Directive.GetLocation() is { } location
            ? $"{(location.LineSpan.Start.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} 行目の #{directive.Directive.Text}"
            : $"前の #{directive.Directive.Text}";

    /// <summary><c>#language</c> の後ろを読み、バージョン (legacy・latest は数に直す) を返す。誤りならその説明を返す。</summary>
    private static string? ParseVersion(ImmutableArray<ShaderSyntaxToken> tokens, out string? version)
    {
        version = null;
        int index = 0;

        if (tokens.Length > 0 && tokens[0].Kind == ShaderSyntaxKind.IdentifierToken)
        {
            if (string.Equals(tokens[0].Text, "slang", StringComparison.OrdinalIgnoreCase))
            {
                index = 1;
            }
            else if (tokens[0].Text is not ("latest" or "legacy"))
            {
                return $"#language に書けるのは Slang のバージョンだけです ('{tokens[0].Text}' は知らない言語です)。#language 2026 のように書いてください。";
            }
        }

        if (index >= tokens.Length)
        {
            return "#language にバージョンを書いてください (#language 2026 など)。";
        }

        ShaderSyntaxToken value = tokens[index];

        // 202c のように数と名前が続けて書かれたものは、字句解析で 2 つに分かれるので、つなげて扱う。
        string text = value.Text;
        if (value.Kind == ShaderSyntaxKind.NumericLiteralToken
            && index + 1 < tokens.Length
            && tokens[index + 1].Kind == ShaderSyntaxKind.IdentifierToken
            && tokens[index + 1].Span.Start == value.Span.End)
        {
            text += tokens[index + 1].Text;
        }

        // slangc 2026.19 は 202a (2025)・202b (2026)・202c (2026 より新しいバージョン)・202e も受け付ける。
        // 202e はどのバージョンとも食い違いにならないので、バージョンを決めずに通す。
        version = text switch
        {
            "2018" or "legacy" => "2018",
            "2025" or "202a" => "2025",
            "2026" or "latest" or "202b" => "2026",
            "202c" => "202c",
            _ => null,
        };

        if (version is not null || text == "202e")
        {
            return null;
        }

        return value.Kind == ShaderSyntaxKind.IdentifierToken
               || (value.Kind == ShaderSyntaxKind.NumericLiteralToken && (value.Text.All(char.IsAsciiDigit) || value.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)))
            ? $"'{value.Text}' は Slang のバージョンではありません。2018・2025・2026・legacy・latest のどれかを書いてください。"
            : "#language のバージョンは整数で書いてください (#language 2026 など)。";
    }
}
