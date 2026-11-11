using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の <c>throw</c> と <c>try</c> の書き方を検査する (SG0345)。
/// </summary>
/// <remarks>
/// <para>
/// <c>throws</c> を書かない関数の、<c>catch</c> で受けない <c>throw</c> (E30115) と <c>try</c> (E30093)。
/// <c>defer</c> の中の、<c>catch</c> で受けない <c>throw</c> (E30113) と <c>try</c> (E30114)。
/// 関数の <c>throws</c> の列挙型と違う列挙型の値の <c>throw</c> (E30116)。
/// </para>
/// <para>
/// 構文解析は <c>try</c>・<c>defer</c>・<c>do</c> のキーワードを構文木に残さないので、関数の本体のトークンの並びを読む。
/// <c>do { … } catch</c> と <c>try { … } catch</c> の波括弧の中は、受けたものとする。ラムダ式の本体は、外の関数と別なので見ない。
/// </para>
/// </remarks>
internal sealed class SlangErrorHandlingAnalyzer : SemanticRuleAnalyzer
{
    private enum Scope
    {
        Plain,
        Caught,
        Defer,
        Lambda,
    }

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.ErrorHandling];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
            Dictionary<ShaderSyntaxToken, int> positions = new(ReferenceEqualityComparer.Instance);

            for (int i = 0; i < tokens.Length; i++)
            {
                positions[tokens[i]] = i;
            }

            HashSet<string> enums = [.. program.Tree.Root.DescendantNodesAndSelf().OfType<EnumDeclarationSyntax>().Select(e => e.Name).Where(n => n.Length > 0)];

            foreach (FunctionDeclarationSyntax function in program.Tree.Root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
            {
                if (function.Body is not { } body
                    || !positions.TryGetValue(function.CloseParen, out int close)
                    || !positions.TryGetValue(body.OpenBrace, out int open)
                    || !positions.TryGetValue(body.CloseBrace, out int end)
                    || close >= open)
                {
                    continue;
                }

                // throws の後ろの型。書いていなければ null。
                string? thrown = null;
                bool throws = false;

                for (int i = close + 1; i < open; i++)
                {
                    if (tokens[i].Text == "throws")
                    {
                        throws = true;
                        thrown = i + 1 < open && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken ? tokens[i + 1].Text : null;
                    }
                }

                Check(context, compilation, tokens, open, end, throws, thrown, enums);
            }
        }
    }

    private static void Check(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ImmutableArray<ShaderSyntaxToken> tokens,
        int open,
        int end,
        bool throws,
        string? thrown,
        HashSet<string> enums)
    {
        Stack<Scope> scopes = new();
        bool deferStatement = false;

        void Report(ShaderSyntaxToken at, string message)
        {
            if (compilation.IsReportable(at) && at.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ErrorHandling, location, message));
            }
        }

        for (int i = open + 1; i < end; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            switch (token.Kind)
            {
                case ShaderSyntaxKind.OpenBraceToken:
                    ShaderSyntaxToken previous = tokens[i - 1];
                    Scope scope = previous.Text is "do" or "try" && CloseFollowedByCatch(tokens, i, end) ? Scope.Caught
                        : previous.Text == "defer" ? Scope.Defer
                        : (previous.Kind == ShaderSyntaxKind.GreaterThanToken && tokens[i - 2].Kind == ShaderSyntaxKind.EqualsToken) || (previous.Kind == ShaderSyntaxKind.CloseParenToken && IsLambdaParameters(tokens, i - 1)) ? Scope.Lambda
                        : Scope.Plain;
                    scopes.Push(scope);
                    continue;

                case ShaderSyntaxKind.CloseBraceToken:
                    if (scopes.Count > 0)
                    {
                        scopes.Pop();
                    }

                    continue;

                case ShaderSyntaxKind.SemicolonToken:
                    deferStatement = false;
                    continue;
            }

            // defer の後ろの 1 つの文 (defer f();)。
            if (token.Text == "defer" && i + 1 < end && tokens[i + 1].Kind != ShaderSyntaxKind.OpenBraceToken)
            {
                deferStatement = true;
                continue;
            }

            bool isThrow = token.Text == "throw";
            bool isTry = token.Text == "try" && i + 1 < end && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken;

            if (!isThrow && !isTry)
            {
                continue;
            }

            Scope innermost = deferStatement ? Scope.Defer : Scope.Plain;

            foreach (Scope enclosing in scopes)
            {
                if (enclosing != Scope.Plain)
                {
                    innermost = deferStatement ? Scope.Defer : enclosing;
                    break;
                }
            }

            switch (innermost)
            {
                case Scope.Caught or Scope.Lambda:
                    break;

                case Scope.Defer:
                    Report(token, isThrow
                        ? "defer の中の throw は、defer の中の catch で受けなければなりません (defer から誤りを外へ投げられません)。"
                        : "defer の中の try は、defer の中の catch で受けなければなりません (defer から誤りを外へ投げられません)。");
                    break;

                case Scope.Plain when !throws:
                    Report(token, isThrow
                        ? "throws を書いていない関数で、catch で受けない throw を書いています。関数に throws を書くか、do { … } catch で受けてください。"
                        : "throws を書いていない関数で、catch で受けない try を書いています。関数に throws を書くか、do { … } catch で受けてください。");
                    break;

                case Scope.Plain when isThrow && thrown is not null && enums.Contains(thrown)
                                      && i + 2 < end && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                                      && tokens[i + 2].Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken
                                      && enums.Contains(tokens[i + 1].Text) && tokens[i + 1].Text != thrown:
                    Report(token, $"関数は {thrown} を投げると書いていますが、{tokens[i + 1].Text} の値を投げています。");
                    break;
            }
        }
    }

    /// <summary>index の <c>{</c> に対応する <c>}</c> の後ろが <c>catch</c> か。</summary>
    private static bool CloseFollowedByCatch(ImmutableArray<ShaderSyntaxToken> tokens, int index, int end)
    {
        int depth = 0;

        for (int i = index; i < end; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
            {
                return i + 1 < tokens.Length && tokens[i + 1].Text == "catch";
            }
        }

        return false;
    }

    /// <summary>index の <c>)</c> がラムダ式の仮引数の閉じ括弧か (<c>(x) { … }</c>)。対応する <c>(</c> の前が式の続き (<c>=</c>・<c>(</c>・<c>,</c>・<c>return</c>) なら、ラムダ式とする。</summary>
    private static bool IsLambdaParameters(ImmutableArray<ShaderSyntaxToken> tokens, int index)
    {
        int depth = 0;

        for (int i = index; i >= 0; i--)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.CloseParenToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.OpenParenToken && --depth == 0)
            {
                return i > 0 && (tokens[i - 1].Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CommaToken
                    || tokens[i - 1].Text == "return");
            }
        }

        return false;
    }
}
