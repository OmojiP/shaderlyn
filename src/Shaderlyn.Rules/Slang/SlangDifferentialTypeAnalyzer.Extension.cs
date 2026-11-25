using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// extension で IDifferentiable に適合させた型の、合成する dzero・dadd に入らないフィールド (E30125)。
/// </summary>
internal sealed partial class SlangDifferentialTypeAnalyzer
{
    /// <summary>型の仮引数を微分する型にする制約。</summary>
    private static readonly ImmutableHashSet<string> DifferentiableConstraints = ["IDifferentiable", "__BuiltinFloatingPointType", "IFloat"];

    /// <summary>
    /// extension で IDifferentiable に適合させ、dzero と dadd の両方を書いていない型の、
    /// 定義では微分しないが extension の制約では微分する型の仮引数を使うフィールドを報告する (slangc 2026.19 の E30125)。
    /// </summary>
    /// <remarks>
    /// <c>struct V&lt;T : __BuiltinArithmeticType&gt; { T x; }</c> に <c>extension&lt;T : __BuiltinFloatingPointType&gt; V&lt;T&gt; : IDifferentiable { }</c> と書くと、
    /// slangc が合成する dzero・dadd は、型の定義の側で微分しない x を含まない。
    /// 型の定義の側でも T が微分する (制約が __BuiltinFloatingPointType など) 場合と、no_diff のフィールドは報告しない。
    /// extension の型引数は、型の仮引数と同じ位置の名前で対応させる。型の定義に where の制約がある場合は、制約を読み切れないので見ない。
    /// </remarks>
    private static void CheckExtensionSynthesis(CompilationUnitSyntax root, Dictionary<string, StructDeclarationSyntax?> byName, HashSet<string> differentiableInterfaces, Func<StructDeclarationSyntax, bool> hasWhere, Action<ShaderSyntaxToken, string> report)
    {
        foreach (IncompleteDeclarationSyntax extension in root.DescendantNodesAndSelf().OfType<IncompleteDeclarationSyntax>())
        {
            if (extension.Tokens is not [{ Text: "extension" or "__extension" }, ..] tokens
                || ReadExtensionHeader(tokens) is not { } header
                || !header.Conformances.Any(differentiableInterfaces.Contains)
                || !byName.TryGetValue(header.Target, out StructDeclarationSyntax? type) || type is null
                || type.GenericParameterTokens.IsDefaultOrEmpty
                || hasWhere(type)
                || (extension.BodyMembers.OfType<FunctionDeclarationSyntax>().Any(f => f.Name == "dzero")
                    && extension.BodyMembers.OfType<FunctionDeclarationSyntax>().Any(f => f.Name == "dadd")))
            {
                continue;
            }

            Dictionary<string, HashSet<string>> definition = ReadConstraints(type.GenericParameterTokens, []);
            string[] parameters = [.. definition.Keys];
            HashSet<string> becomes = new(StringComparer.Ordinal);

            for (int i = 0; i < parameters.Length && i < header.Arguments.Length; i++)
            {
                if (!definition[parameters[i]].Overlaps(DifferentiableConstraints)
                    && header.Constraints.TryGetValue(header.Arguments[i], out HashSet<string>? constraints) && constraints.Overlaps(DifferentiableConstraints))
                {
                    becomes.Add(parameters[i]);
                }
            }

            if (becomes.Count == 0)
            {
                continue;
            }

            foreach (VariableDeclarationSyntax field in type.Members.OfType<VariableDeclarationSyntax>())
            {
                if (field.ModifierTokens.Any(t => t.Text is "no_diff" or "static") || field.Type.ModifierTokens.Any(t => t.Text == "no_diff")
                    || field.Type.DescendantTokens().FirstOrDefault(t => becomes.Contains(t.Text)) is null)
                {
                    continue;
                }

                foreach (VariableDeclaratorSyntax variable in field.Variables)
                {
                    report(variable.NameToken,
                        $"フィールド '{variable.Name}' は extension の制約では微分しますが、{type.Name} の定義の側では微分しないので、slangc が合成する dzero・dadd に入りません。"
                        + "extension に dzero と dadd を書くか、フィールドに no_diff を書いてください。");
                }
            }
        }
    }

    /// <summary>extension の型の仮引数の制約・拡張する型の名前と型引数・適合させるインターフェイス。</summary>
    private sealed record ExtensionHeader(string Target, string[] Arguments, string[] Conformances, Dictionary<string, HashSet<string>> Constraints);

    /// <summary><c>extension&lt;T : I&gt; V&lt;T&gt; : IDifferentiable where T : J { …</c> を読む。</summary>
    private static ExtensionHeader? ReadExtensionHeader(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        int i = 1;
        ImmutableArray<ShaderSyntaxToken> generic = [];

        if (i < tokens.Length && tokens[i].Kind == ShaderSyntaxKind.LessThanToken)
        {
            int end = SkipAngles(tokens, i);
            generic = tokens[i..end];
            i = end;
        }

        if (i >= tokens.Length || tokens[i].Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return null;
        }

        string target = tokens[i++].Text;
        List<string> arguments = [];

        if (i < tokens.Length && tokens[i].Kind == ShaderSyntaxKind.LessThanToken)
        {
            int end = SkipAngles(tokens, i);

            // 型引数の並びの、山括弧の深さが 1 の識別子 (V<T, U> の T と U)。
            int depth = 0;

            for (int j = i; j < end; j++)
            {
                depth += tokens[j].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, ShaderSyntaxKind.GreaterThanGreaterThanToken => -2, _ => 0 };

                if (depth == 1 && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && tokens[j - 1].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.CommaToken)
                {
                    arguments.Add(tokens[j].Text);
                }
            }

            i = end;
        }

        List<string> conformances = [];
        int where = tokens.Length;

        if (i < tokens.Length && tokens[i].Kind == ShaderSyntaxKind.ColonToken)
        {
            for (i++; i < tokens.Length && tokens[i].Kind != ShaderSyntaxKind.OpenBraceToken && tokens[i].Text != "where"; i++)
            {
                if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    conformances.Add(tokens[i].Text);
                }
            }
        }

        if (i < tokens.Length && tokens[i].Text == "where")
        {
            where = i;
        }

        int brace = tokens.IndexOf(tokens.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.OpenBraceToken)!);
        ImmutableArray<ShaderSyntaxToken> whereTokens = where < tokens.Length ? tokens[where..(brace < 0 ? tokens.Length : brace)] : [];

        return new ExtensionHeader(target, [.. arguments], [.. conformances], ReadConstraints(generic, whereTokens));
    }

    /// <summary>
    /// ジェネリックの仮引数の並び (<c>&lt;T : I, U&gt;</c>) と where の制約 (<c>where T : J</c>) から、仮引数ごとの制約の名前を集める。
    /// 値の仮引数 (<c>let N : int</c>) は入れない。
    /// </summary>
    private static Dictionary<string, HashSet<string>> ReadConstraints(ImmutableArray<ShaderSyntaxToken> generic, ImmutableArray<ShaderSyntaxToken> where)
    {
        Dictionary<string, HashSet<string>> constraints = new(StringComparer.Ordinal);
        string? current = null;
        bool afterColon = false;
        int depth = 0;

        foreach (ShaderSyntaxToken token in generic)
        {
            depth += token.Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, ShaderSyntaxKind.GreaterThanGreaterThanToken => -2, _ => 0 };

            if (depth == 1 && token.Kind == ShaderSyntaxKind.CommaToken)
            {
                current = null;
                afterColon = false;
            }
            else if (depth == 1 && token.Kind == ShaderSyntaxKind.ColonToken)
            {
                afterColon = true;
            }
            else if (depth == 1 && token.Kind == ShaderSyntaxKind.IdentifierToken)
            {
                if (token.Text is "let" or "each" or "typename")
                {
                    if (token.Text == "let")
                    {
                        current = "";
                    }

                    continue;
                }

                if (current is null)
                {
                    current = token.Text;
                    constraints[current] = new(StringComparer.Ordinal);
                }
                else if (afterColon && current.Length > 0)
                {
                    constraints[current].Add(token.Text);
                }
            }
        }

        // where T : J, K (where T.Differential == T のような制約は見ない)
        for (int i = 0; i + 2 < where.Length; i++)
        {
            if (where[i].Text == "where" && where[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && where[i + 2].Kind == ShaderSyntaxKind.ColonToken
                && constraints.TryGetValue(where[i + 1].Text, out HashSet<string>? set))
            {
                for (int j = i + 3; j < where.Length && where[j].Text != "where"; j++)
                {
                    if (where[j].Kind == ShaderSyntaxKind.IdentifierToken)
                    {
                        set.Add(where[j].Text);
                    }
                }
            }
        }

        return constraints;
    }

    /// <summary><paramref name="open"/> の <c>&lt;</c> と対になる <c>&gt;</c> の次の位置。</summary>
    private static int SkipAngles(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            depth += tokens[i].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, ShaderSyntaxKind.GreaterThanGreaterThanToken => -2, _ => 0 };

            if (depth <= 0)
            {
                return i + 1;
            }
        }

        return tokens.Length;
    }
}
