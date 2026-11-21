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

            if (!tokens.Any(t => t.Text is "each" or "makeTuple" or "countof" or "expand"))
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
            Dictionary<string, int?> emptyAt = new(StringComparer.Ordinal);

            for (int i = 1; i + 1 < tokens.Length; i++)
            {
                if (tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 1].Kind != ShaderSyntaxKind.LessThanToken
                    || tokens[i - 1].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i - 1].Text is "return" or "new")
                {
                    continue;
                }

                (List<string> parameters, List<string> packs, int close) = ReadParameters(tokens, i + 1);

                // 仮引数の並びの each T の T が、each で宣言した型の仮引数でない (E30103)。
                if (parameters.Count > 0 && close < tokens.Length && tokens[close].Kind == ShaderSyntaxKind.OpenParenToken && Matching(tokens, close) is { } parameterEnd)
                {
                    for (int j = close + 1; j + 1 < parameterEnd; j++)
                    {
                        if (tokens[j].Text == "each" && tokens[j + 1].Kind == ShaderSyntaxKind.IdentifierToken
                            && parameters.Contains(tokens[j + 1].Text) && !packs.Contains(tokens[j + 1].Text))
                        {
                            Report(tokens[j], $"型の仮引数 {tokens[j + 1].Text} は可変長の並び (each {tokens[j + 1].Text}) ではないので、each を付けられません。");
                        }
                    }
                }

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

                // nonempty を求める並びが、仮引数の expand each T からしか決まらないなら、その外の仮引数の数だけの実引数の呼び出しでは空になる。
                if (nonEmpty.Count > 0)
                {
                    List<List<ShaderSyntaxToken>> declared = Split([.. tokens.Skip(close + 1).Take(end - close - 1)]);
                    int packed = declared.Count(d => d.Zip(d.Skip(1)).Any(x => x.First.Text == "each" && nonEmpty.Contains(x.Second.Text)));
                    bool onlyThere = declared.All(d => !d.Any(t => nonEmpty.Contains(t.Text)) || d.Any(t => t.Text == "expand"));

                    if (packed > 0 && onlyThere && declared.All(d => !d.Any(t => t.Kind == ShaderSyntaxKind.EqualsToken)))
                    {
                        emptyAt[name] = emptyAt.ContainsKey(name) ? null : declared.Count - packed;
                    }
                }

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
                            Report(token, $"並び {tokens[i + 2].Text} は空でありうるので、{token.Text} を使用できません。関数に where nonempty({tokens[i + 2].Text}) を書いてください。");
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

            CheckCountConstraints(tokens, Report);

            // foo() (nonempty を求める並びが実引数から空に決まる呼び出し。E30440)
            for (int i = 1; i + 1 < tokens.Length; i++)
            {
                if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken
                    && emptyAt.TryGetValue(tokens[i].Text, out int? fixedCount) && fixedCount is { } count
                    && (tokens[i - 1].Kind is not (ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.GreaterThanToken) || tokens[i - 1].Text is "return" or "else")
                    && Matching(tokens, i + 1) is { } callEnd
                    && !tokens.Skip(i + 2).Take(callEnd - i - 2).Any(t => t.Text is "expand" or "each")
                    && Split([.. tokens.Skip(i + 2).Take(callEnd - i - 2)]).Count == count)
                {
                    Report(tokens[i], $"'{tokens[i].Text}' は where nonempty(…) を求めるので、並びが空になる実引数では呼べません。");
                }
            }

            // expand (x + 1) (each の無い expand。E30105) と、each の無い型の仮引数の each T (E30103)。
            for (int i = 0; i + 1 < tokens.Length; i++)
            {
                if (tokens[i].Text == "expand" && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken && Matching(tokens, i + 1) is { } close
                    && !tokens.Skip(i + 2).Take(close - i - 2).Any(t => t.Text == "each"))
                {
                    Report(tokens[i], "expand の式に each が存在しないので、何を広げるか決まりません。");
                }
            }

            // __first(makeTuple()) (空のタプル)。どの関数の中でも同じ。
            for (int i = 0; i + 4 < tokens.Length; i++)
            {
                if (tokens[i].Text is "__first" or "__last" && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken && tokens[i + 2].Text == "makeTuple"
                    && tokens[i + 3].Kind == ShaderSyntaxKind.OpenParenToken && tokens[i + 4].Kind == ShaderSyntaxKind.CloseParenToken)
                {
                    Report(tokens[i], $"空のタプルに {tokens[i].Text} は使用できません。");
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

    /// <summary>
    /// <c>where countof(T) == N</c> の書き方 (E30430・E30431・E30432・E30434・E30436・E30437) と、型引数を書いた呼び出しの並びの数 (E30433)。
    /// </summary>
    private static void CheckCountConstraints(ImmutableArray<ShaderSyntaxToken> tokens, Action<ShaderSyntaxToken, string> report)
    {
        // 関数の名前から、値の仮引数 N の位置と、仮引数の並び (実引数を受け取る位置) の位置。
        Dictionary<string, (int Count, int Pack)?> counted = new(StringComparer.Ordinal);

        for (int i = 1; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 1].Kind != ShaderSyntaxKind.LessThanToken
                || tokens[i - 1].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i - 1].Text is "return" or "new")
            {
                continue;
            }

            (List<string> parameters, List<string> packs, int close) = ReadParameters(tokens, i + 1);

            if (parameters.Count == 0 || close >= tokens.Length)
            {
                continue;
            }

            int end = close;
            List<ShaderSyntaxToken> functionParameters = [];

            if (tokens[close].Kind == ShaderSyntaxKind.OpenParenToken && Matching(tokens, close) is { } closeParen)
            {
                functionParameters = [.. tokens.Skip(close + 1).Take(closeParen - close - 1)];
                end = closeParen + 1;
            }

            // where の節を 1 つずつ読む (次の where か、本体の { か ; まで)。
            for (int j = end; j < tokens.Length && tokens[j].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); j++)
            {
                if (tokens[j].Text != "where")
                {
                    continue;
                }

                List<ShaderSyntaxToken> clause = [];

                for (int k = j + 1; k < tokens.Length && tokens[k].Text != "where" && tokens[k].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); k++)
                {
                    clause.Add(tokens[k]);
                }

                if (!clause.Where((t, index) => t.Text == "countof" && index + 1 < clause.Count && clause[index + 1].Kind == ShaderSyntaxKind.OpenParenToken).Any())
                {
                    continue;
                }

                if (clause is [{ Text: "optional" } optional, { Text: "countof" }, ..])
                {
                    report(optional, "countof(…) の制約は optional にできません。");
                    continue;
                }

                if (clause is not [{ Text: "countof" } first, { Kind: ShaderSyntaxKind.OpenParenToken }, { Kind: ShaderSyntaxKind.IdentifierToken } target, { Kind: ShaderSyntaxKind.CloseParenToken }, var op, .. var rest])
                {
                    report(clause.First(t => t.Text == "countof" && clause.IndexOf(t) + 1 < clause.Count && clause[clause.IndexOf(t) + 1].Kind == ShaderSyntaxKind.OpenParenToken), "countof(…) の制約は、countof(…) を左に書いてください (where countof(T) == N)。");
                    continue;
                }

                if (op.Kind != ShaderSyntaxKind.EqualsEqualsToken)
                {
                    report(first, $"countof(…) の制約には == を使用してください ('{op.Text}' は使用できません)。");
                    continue;
                }

                if (!packs.Contains(target.Text))
                {
                    report(first, $"countof(…) の制約の {target.Text} は、この宣言の可変長の仮引数でなければなりません。");
                    continue;
                }

                if (rest is [{ Kind: ShaderSyntaxKind.IdentifierToken } callee, { Kind: ShaderSyntaxKind.OpenParenToken }, ..] && callee.Text != "countof")
                {
                    report(first, $"countof(…) の制約の右には、コンパイル時の整数の定数か値の仮引数を書いてください ('{callee.Text}(…)' は使用できません)。");
                    continue;
                }

                // countof(P) == N (N が値の仮引数、P が関数の最後の仮引数の並び)。型引数を書いた呼び出しで数を比べる。
                if (rest is [{ Kind: ShaderSyntaxKind.IdentifierToken } count] && parameters.IndexOf(count.Text) is var countIndex and >= 0
                    && Split(functionParameters) is { Count: > 0 } split
                    && split.FindIndex(p => p.Count == 2 && p[0].Text == target.Text) is var packIndex and >= 0
                    && packIndex == split.Count - 1)
                {
                    string name = tokens[i].Text;
                    counted[name] = counted.ContainsKey(name) ? null : (countIndex, packIndex);
                }
            }
        }

        // 同じ名前の宣言 (多重定義) の数。1 つだけのものの呼び出しを見る。
        Dictionary<string, int> declarations = new(StringComparer.Ordinal);

        for (int i = 1; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i - 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i - 1].Text != "return"
                && tokens[i + 1].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.OpenParenToken)
            {
                declarations[tokens[i].Text] = declarations.GetValueOrDefault(tokens[i].Text) + 1;
            }
        }

        // load<2>(1, 2, 3)
        for (int i = 1; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 1].Kind != ShaderSyntaxKind.LessThanToken
                || !counted.TryGetValue(tokens[i].Text, out var rule) || rule is not { } found
                || declarations.GetValueOrDefault(tokens[i].Text) != 1
                || (tokens[i - 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i - 1].Text != "return"))
            {
                continue;
            }

            int depth = 0;
            int close = i + 1;

            for (; close < tokens.Length; close++)
            {
                depth += tokens[close].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, _ => 0 };

                if (depth == 0)
                {
                    break;
                }
            }

            List<List<ShaderSyntaxToken>> typeArguments = Split([.. tokens.Skip(i + 2).Take(close - i - 2)]);

            if (close + 1 >= tokens.Length || tokens[close + 1].Kind != ShaderSyntaxKind.OpenParenToken || Matching(tokens, close + 1) is not { } end
                || found.Count >= typeArguments.Count || typeArguments[found.Count] is not [{ Kind: ShaderSyntaxKind.NumericLiteralToken } literal]
                || !ShaderLiteral.TryGetInt64(literal, out long expected))
            {
                continue;
            }

            int written = Split([.. tokens.Skip(close + 2).Take(end - close - 2)]).Count - found.Pack;

            if (written >= 0 && written != expected)
            {
                report(tokens[i], $"'{tokens[i].Text}' の並びの数 ({written}) が、countof(…) の制約の数 ({expected}) と合いません。");
            }
        }
    }

    /// <summary>トークンを、深さ 0 の , で分ける。空なら 0 個。</summary>
    private static List<List<ShaderSyntaxToken>> Split(IReadOnlyList<ShaderSyntaxToken> tokens)
    {
        List<List<ShaderSyntaxToken>> parts = [];
        List<ShaderSyntaxToken> current = [];
        int depth = 0;

        foreach (ShaderSyntaxToken token in tokens)
        {
            depth += token.Kind switch
            {
                ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken => 1,
                ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CloseBracketToken => -1,
                _ => 0,
            };

            if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
            {
                parts.Add(current);
                current = [];
                continue;
            }

            current.Add(token);
        }

        if (current.Count > 0 || parts.Count > 0)
        {
            parts.Add(current);
        }

        return parts;
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
