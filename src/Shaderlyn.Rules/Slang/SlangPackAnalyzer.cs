using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、空でありうる値の並び (可変長の型の仮引数 <c>each T</c>・<c>let each D : int</c>) の使い方を検査する (SG0354)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>where nonempty(T)</c> の無い並びの <c>__first(T)</c>・<c>__last(T)</c> (E30412)。空のタプルの <c>__first(makeTuple())</c> (E30411)</item>
/// <item><c>where nonempty(…)</c> を求める関数へ、空でありうる並び (nonempty の無い並びと <c>__trimFirst</c>・<c>__trimLast</c> の結果) か空の型引数 (<c>f&lt;&gt;()</c>) を渡すもの (E30412・E30440)</item>
/// <item><c>where optional nonempty(D)</c> (E30415。nonempty の制約は optional にできない)</item>
/// </list>
/// 構文解析はジェネリックの仮引数と where を構文木に残さないので、トークンの並びを読む。
/// </remarks>
internal sealed class SlangPackAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.PackMayBeEmpty];

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

            if (!tokens.Any(t => t.Text is "each" or "makeTuple"))
            {
                continue;
            }

            void Report(ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.PackMayBeEmpty, location, message));
                }
            }

            // 関数ごとの、並びの仮引数と、空でないと決めた並び。名前ごとに、nonempty を求める仮引数の位置。
            List<(int Start, int End, List<string> Packs, HashSet<string> NonEmpty)> functions = [];
            Dictionary<string, (List<string> Parameters, HashSet<int> Required)?> requiring = new(StringComparer.Ordinal);

            for (int i = 1; i + 1 < tokens.Length; i++)
            {
                if (tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 1].Kind != ShaderSyntaxKind.LessThanToken
                    || tokens[i - 1].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i - 1].Text is "return" or "new")
                {
                    continue;
                }

                (List<string> parameters, List<string> packs, int close) = ReadParameters(tokens, i + 1);

                if (packs.Count == 0 || close >= tokens.Length || tokens[close].Kind != ShaderSyntaxKind.OpenParenToken || Matching(tokens, close) is not { } end)
                {
                    continue;
                }

                // where nonempty(D) と where optional nonempty(D)
                HashSet<string> nonEmpty = new(StringComparer.Ordinal);
                int body = end + 1;

                for (; body < tokens.Length && tokens[body].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); body++)
                {
                    if (tokens[body].Text == "nonempty" && body + 2 < tokens.Length && tokens[body + 1].Kind == ShaderSyntaxKind.OpenParenToken)
                    {
                        if (tokens[body - 1].Text == "optional")
                        {
                            Report(tokens[body - 1], "nonempty の制約は optional にできません。");
                        }
                        else
                        {
                            nonEmpty.Add(tokens[body + 2].Text);
                        }
                    }
                }

                string name = tokens[i].Text;
                HashSet<int> required = [.. parameters.Select((p, index) => (p, index)).Where(x => nonEmpty.Contains(x.p)).Select(x => x.index)];
                requiring[name] = requiring.ContainsKey(name) ? null : (parameters, required);

                if (body < tokens.Length && tokens[body].Kind == ShaderSyntaxKind.OpenBraceToken && MatchingBrace(tokens, body) is { } bodyEnd)
                {
                    functions.Add((body, bodyEnd, packs, nonEmpty));
                }
            }

            foreach ((int start, int end, List<string> packs, HashSet<string> nonEmpty) in functions)
            {
                for (int i = start; i + 3 < end; i++)
                {
                    ShaderSyntaxToken token = tokens[i];

                    // __first(T)・__last(T)・__first(makeTuple())
                    if (token.Text is "__first" or "__last" && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken)
                    {
                        if (tokens[i + 2].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 3].Kind == ShaderSyntaxKind.CloseParenToken
                            && packs.Contains(tokens[i + 2].Text) && !nonEmpty.Contains(tokens[i + 2].Text))
                        {
                            Report(token, $"並び {tokens[i + 2].Text} は空でありうるので、{token.Text} を使えません。関数に where nonempty({tokens[i + 2].Text}) を書いてください。");
                        }

                        continue;
                    }

                    // consumeNonEmpty<D>() (nonempty を求める仮引数への、空でありうる並び)
                    if (token.Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken
                        && requiring.TryGetValue(token.Text, out var callee) && callee is { Required.Count: > 0 } target)
                    {
                        CheckForward(tokens, i, target.Required, packs, nonEmpty, Report);
                    }
                }
            }

            // __first(makeTuple()) (空のタプル)。どの関数の中でも同じ。
            for (int i = 0; i + 4 < tokens.Length; i++)
            {
                if (tokens[i].Text is "__first" or "__last" && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken && tokens[i + 2].Text == "makeTuple"
                    && tokens[i + 3].Kind == ShaderSyntaxKind.OpenParenToken && tokens[i + 4].Kind == ShaderSyntaxKind.CloseParenToken)
                {
                    Report(tokens[i], $"空のタプルに {tokens[i].Text} は使えません。");
                }
            }

            // 関数の外の呼び出し (int x = goodValueUse<>();)
            for (int i = 1; i + 2 < tokens.Length; i++)
            {
                if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken
                    && tokens[i + 2].Kind == ShaderSyntaxKind.GreaterThanToken
                    && requiring.TryGetValue(tokens[i].Text, out var empty) && empty is { Required.Count: > 0 }
                    && !functions.Any(f => i > f.Start && i < f.End))
                {
                    Report(tokens[i], $"'{tokens[i].Text}' は where nonempty(…) を求めるので、空の型引数 (<>) では呼べません。");
                }
            }
        }
    }

    /// <summary>nonempty を求める関数へ渡す型引数を見る。</summary>
    private static void CheckForward(ImmutableArray<ShaderSyntaxToken> tokens, int name, HashSet<int> required, List<string> packs, HashSet<string> nonEmpty, Action<ShaderSyntaxToken, string> report)
    {
        int depth = 0;
        int index = 0;
        List<ShaderSyntaxToken> argument = [];

        for (int i = name + 1; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind == ShaderSyntaxKind.LessThanToken)
            {
                if (depth++ == 0)
                {
                    continue;
                }
            }
            else if (token.Kind == ShaderSyntaxKind.GreaterThanToken && --depth == 0)
            {
                Check(argument, index);
                return;
            }
            else if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 1)
            {
                Check(argument, index++);
                argument = [];
                continue;
            }
            else if (token.Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken)
            {
                return;
            }

            argument.Add(token);
        }

        void Check(List<ShaderSyntaxToken> written, int position)
        {
            if (!required.Contains(position))
            {
                return;
            }

            if (written is [{ Kind: ShaderSyntaxKind.IdentifierToken } single] && packs.Contains(single.Text) && !nonEmpty.Contains(single.Text))
            {
                report(tokens[name], $"並び {single.Text} は空でありうるので、where nonempty(…) を求める '{tokens[name].Text}' に渡せません。");
            }
            else if (written is [{ Text: "__trimFirst" or "__trimLast" } trim, ..])
            {
                report(tokens[name], $"{trim.Text} の結果は空でありうるので、where nonempty(…) を求める '{tokens[name].Text}' に渡せません。");
            }
            else if (written.Count == 0)
            {
                report(tokens[name], $"'{tokens[name].Text}' は where nonempty(…) を求めるので、空の型引数では呼べません。");
            }
        }
    }

    /// <summary><c>&lt;each T, let each D : int, U&gt;</c> の仮引数の名前と、そのうちの並び (each) の名前。</summary>
    private static (List<string> Parameters, List<string> Packs, int Next) ReadParameters(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        List<string> parameters = [];
        List<string> packs = [];
        int depth = 0;
        bool expectName = true;
        bool pack = false;
        int i = open;

        for (; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind == ShaderSyntaxKind.LessThanToken)
            {
                depth++;
                continue;
            }

            if (token.Kind == ShaderSyntaxKind.GreaterThanToken && --depth == 0)
            {
                return (parameters, packs, i + 1);
            }

            if (token.Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.OpenParenToken)
            {
                return ([], [], i);
            }

            if (depth != 1)
            {
                continue;
            }

            if (token.Kind == ShaderSyntaxKind.CommaToken)
            {
                expectName = true;
                pack = false;
            }
            else if (expectName && token.Text is "let")
            {
            }
            else if (expectName && token.Text is "each")
            {
                pack = true;
            }
            else if (expectName && token.Kind == ShaderSyntaxKind.IdentifierToken)
            {
                parameters.Add(token.Text);

                if (pack)
                {
                    packs.Add(token.Text);
                }

                expectName = false;
            }
        }

        return ([], [], i);
    }

    private static int? Matching(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.CloseParenToken && --depth == 0)
            {
                return i;
            }
        }

        return null;
    }

    private static int? MatchingBrace(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
            {
                return i;
            }
        }

        return null;
    }
}
