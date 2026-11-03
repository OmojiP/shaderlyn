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
/// <para>
/// 到達した <c>#error</c> も伝える (<c>CPPerror</c>)。GLSL には構成を選ぶエンジンが無く、到達すればコンパイルが止まる。
/// HLSL と同じく情報として伝える CF0104 は、GLSL では構成によらないものを出さない (<see cref="ErrorConfigurationAnalyzer"/>)。
/// </para>
/// <para>
/// <c>#define</c> と <c>#undef</c> の予約された名前 (ParseHelper.cpp の <c>reservedPpErrorCheck</c>) と、
/// <c>#pragma optimize</c> / <c>#pragma debug</c> と、SPIR-V を作るときの <c>#pragma STDGL</c> の形 (<c>handlePragma</c>) も見る。
/// </para>
/// <para>
/// <c>#version</c> は、ファイルの最初 (前にはコメントと空白だけ) に 1 つだけ書ける (Pp.cpp の <c>CPPversion</c> の「must occur first in shader」)。
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
        ReportLongTokens(context, compilation.Text);

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

            bool versionSeen = false;

            foreach (RecordedDirective directive in program.Tree.PreprocessResult.RecordedDirectives)
            {
                if (directive.Directive.Text == "version")
                {
                    if (compilation.IsReportable(directive.Directive))
                    {
                        if (versionSeen || HasTokenBefore(directive.Directive))
                        {
                            requirement.Report(GlslRuleDescriptors.PreprocessorDirective, directive.Directive, "#version はファイルの最初に 1 つだけ書けます。");
                        }
                        else if (requirement.IsEs && requirement.Version >= 300 && HasNonBlankBefore(directive.Directive))
                        {
                            requirement.Report(GlslRuleDescriptors.PreprocessorDirective, directive.Directive, "ES の 3.00 からは、#version の前にコメントや空の行も書けません。");
                        }
                    }

                    versionSeen = true;
                }
                else if (directive.Directive.Text == "extension" && compilation.IsReportable(directive.Directive) && DescribeExtensionError(directive.Tokens) is { } message)
                {
                    requirement.Report(GlslRuleDescriptors.PreprocessorDirective, directive.Directive, message);
                }
                else if (directive.Directive.Text is "define" or "undef" && compilation.IsReportable(directive.Directive)
                         && DescribeReservedName(requirement, directive) is { } reserved)
                {
                    requirement.Report(GlslRuleDescriptors.PreprocessorDirective, directive.Tokens[0], reserved);
                }
                else if (directive.Directive.Text == "error" && compilation.IsReportable(directive.Directive))
                {
                    requirement.Report(
                        GlslRuleDescriptors.PreprocessorDirective,
                        directive.Directive,
                        $"#error に到達しました: {string.Join(" ", directive.Tokens.Select(token => token.Text))}");
                }
            }

            foreach (PragmaDirective pragma in program.Tree.PreprocessResult.Pragmas)
            {
                if (pragma.Name is "optimize" or "debug" && compilation.IsReportable(pragma.NameToken) && !IsOnOffPragma(pragma.Tokens))
                {
                    requirement.Report(
                        GlslRuleDescriptors.PreprocessorDirective,
                        pragma.NameToken,
                        $"#pragma {pragma.Name} の形が誤っています。#pragma {pragma.Name}(on) か #pragma {pragma.Name}(off) と書いてください。");
                }
                else if (pragma.Name == "STDGL" && requirement.SpirvVersion > 0 && compilation.IsReportable(pragma.NameToken) && !IsInvariantAllPragma(pragma.Tokens))
                {
                    requirement.Report(
                        GlslRuleDescriptors.PreprocessorDirective,
                        pragma.NameToken,
                        "#pragma STDGL の形が誤っています。SPIR-V を作るときは #pragma STDGL invariant(all) だけを書けます。");
                }
            }
        }
    }

    /// <summary>glslang の 1 つのトークンの長さの上限 (MaxTokenLength)。</summary>
    private const int MaxTokenLength = 1024;

    /// <summary>
    /// 1024 文字を越える名前と数のリテラルを報告する (PpScanner.cpp の「name too long」「numeric literal too long」「float literal too long」)。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="text">解析しているファイル。</param>
    /// <remarks>
    /// 前処理の指令の中のトークンも数えるので、展開した後のトークンでなく、ファイルの文字列をコメントと文字列を除いて読む。
    /// </remarks>
    private static void ReportLongTokens(SyntaxTreeAnalysisContext context, Core.Text.SourceText text)
    {
        string content = text.ToString();
        int i = 0;

        while (i < content.Length)
        {
            char c = content[i];

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                int newline = content.IndexOf('\n', i);
                i = newline < 0 ? content.Length : newline;
                continue;
            }

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                int close = content.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? content.Length : close + 2;
                continue;
            }

            if (c == '"')
            {
                int close = content.IndexOf('"', i + 1);
                i = close < 0 ? content.Length : close + 1;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_' || char.IsAsciiDigit(c) || c == '.' && i + 1 < content.Length && char.IsAsciiDigit(content[i + 1]))
            {
                bool number = !(char.IsAsciiLetter(c) || c == '_');
                int start = i;

                while (i < content.Length && (char.IsAsciiLetterOrDigit(content[i]) || content[i] == '_' || number && content[i] == '.'))
                {
                    i++;
                }

                if (i - start > MaxTokenLength)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        GlslRuleDescriptors.PreprocessorDirective,
                        Location.Create(text, new Core.Text.TextSpan(start, 1)),
                        number
                            ? $"数のリテラルが長すぎます ({i - start} 文字。{MaxTokenLength} 文字まで)。"
                            : $"名前が長すぎます ({i - start} 文字。{MaxTokenLength} 文字まで)。"));
                }

                continue;
            }

            i++;
        }
    }

    /// <summary>指令の <c>#</c> より前に、空白とタブでないもの (コメントと改行を含む) があるか。ES の 3.00 からは、#version の前にはこれらも書けない (glslang の scanVersion)。</summary>
    /// <param name="directive">指令の名前のトークン。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    private static bool HasNonBlankBefore(ShaderSyntaxToken directive)
    {
        string text = directive.Source.ToString();
        int end = text.LastIndexOf('#', Math.Min(directive.Span.Start, text.Length - 1));
        return text.AsSpan(0, Math.Max(end, 0)).TrimStart('﻿').ContainsAnyExcept(' ', '	');
    }

    /// <summary>指令の <c>#</c> より前に、コメントと空白でないものがあるか。</summary>
    /// <param name="directive">指令の名前のトークン。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    private static bool HasTokenBefore(ShaderSyntaxToken directive)
    {
        string text = directive.Source.ToString();
        int end = text.LastIndexOf('#', Math.Min(directive.Span.Start, text.Length - 1));

        for (int i = 0; i < end; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            // 行のコメントは、行末の \ で次の行に続く。
            if (text[i] == '/' && i + 1 < end && text[i + 1] == '/')
            {
                int newline = text.IndexOf('\n', i);

                while (newline > 0 && text.AsSpan(i, newline - i).TrimEnd('\r').EndsWith("\\", StringComparison.Ordinal))
                {
                    newline = text.IndexOf('\n', newline + 1);
                }

                i = newline < 0 ? end : newline;
                continue;
            }

            if (text[i] == '/' && i + 1 < end && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? end : close + 1;
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>#pragma optimize</c> / <c>#pragma debug</c> の形か。名前・<c>(</c>・1 つのトークン・<c>)</c> の 4 つである。
    /// </summary>
    /// <param name="tokens">指令の名前の後ろのトークン。</param>
    /// <returns>形が合えば <see langword="true"/>。</returns>
    /// <remarks>括弧の中が <c>on</c> でも <c>off</c> でもないものは、glslang も誤りにしない (知らない <c>#pragma</c> と同じく無視する)。</remarks>
    private static bool IsOnOffPragma(ImmutableArray<ShaderSyntaxToken> tokens)
        => tokens.Length == 4
           && tokens[1].Kind == ShaderSyntaxKind.OpenParenToken
           && (tokens[2].Text is not ("on" or "off") || tokens[3].Kind == ShaderSyntaxKind.CloseParenToken);

    /// <summary><c>#pragma STDGL invariant(all)</c> の形か。</summary>
    /// <param name="tokens">指令の名前の後ろのトークン。</param>
    /// <returns>形が合えば <see langword="true"/>。</returns>
    /// <remarks>
    /// glslang は、括弧の片方だけが合わないものは誤りにしない (<c>(</c> と <c>)</c> の両方が違うときだけ「Invalid STDGL declaration」)。
    /// </remarks>
    private static bool IsInvariantAllPragma(ImmutableArray<ShaderSyntaxToken> tokens)
        => tokens.Length == 5
           && (tokens[2].Kind == ShaderSyntaxKind.OpenParenToken || tokens[4].Kind == ShaderSyntaxKind.CloseParenToken)
           && tokens[1].Text == "invariant"
           && tokens[3].Text == "all";

    /// <summary><c>#define</c> / <c>#undef</c> の名前が予約されていれば、その説明を返す。</summary>
    /// <param name="requirement">バージョンと拡張。</param>
    /// <param name="directive">指令。<see cref="RecordedDirective.Tokens"/> は名前のトークン 1 つ。</param>
    /// <returns>誤りの説明。誤りが無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>GL_</c> で始まる名前と <c>defined</c> は定義も取り消しもできない。<c>__</c> を含む名前は、ES の 3.00 より前では誤り、
    /// ES の 3.00 から (ES でなければ警告で、ここでは伝えない) は <c>__LINE__</c>・<c>__FILE__</c>・<c>__VERSION__</c> だけが誤りである。
    /// <c>GL_EXT_spirv_intrinsics</c> を有効にすると、<c>GL_</c> と <c>__</c> の名前は書ける。
    /// </remarks>
    private static string? DescribeReservedName(GlslRequirements requirement, RecordedDirective directive)
    {
        if (directive.Tokens is not [{ Kind: ShaderSyntaxKind.IdentifierToken } name])
        {
            return null;
        }

        string text = name.Text;
        bool spirv = requirement.IsOn("GL_EXT_spirv_intrinsics", directive.Directive);

        if (text.StartsWith("GL_", StringComparison.Ordinal) && !spirv)
        {
            return $"GL_ で始まる名前 '{text}' は #{directive.Directive.Text} できません。";
        }

        if (text == "defined")
        {
            return $"'defined' は #{directive.Directive.Text} できません。";
        }

        if (!text.Contains("__", StringComparison.Ordinal) || spirv || !requirement.IsEs)
        {
            return null;
        }

        if (requirement.Version < 300)
        {
            return $"ES の 3.00 より前では、__ を含む名前 '{text}' は #{directive.Directive.Text} できません。";
        }

        return text is "__LINE__" or "__FILE__" or "__VERSION__" ? $"あらかじめ定義された名前 '{text}' は #{directive.Directive.Text} できません。" : null;
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
