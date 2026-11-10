using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の宣言の置き場所を検査する (SG0101)。
/// </summary>
/// <remarks>
/// <para>
/// slangc 2026.19 の「declaration not allowed here」(E30102) に当たる。トークンの並びと、波括弧の入れ物の種類 (ファイルの直下・構造体・インターフェイス・extension・関数) で見る。
/// </para>
/// <list type="bullet">
/// <item>型を書く位置の構造体の定義 (<c>void f(struct {} a)</c>、<c>-&gt; struct S {}</c>、<c>typealias A = struct S {}</c>、<c>x as struct S {}</c> など)。
/// 文の頭 (<c>;</c> <c>{</c> <c>}</c> <c>]</c> と修飾子の後ろ) に書いた構造体だけが宣言である。<c>struct S {} f()</c> (戻り値の型の構造体) も誤り</item>
/// <item>構造体・extension の中の namespace・extension・interface・__constraint。インターフェイスの中の、それに加えて enum・struct・class</item>
/// <item>ファイルの直下の __init と associatedtype</item>
/// <item>関数の中の関数の定義 (<c>float g(int x) { … }</c>)</item>
/// </list>
/// </remarks>
internal sealed class SlangDeclarationPlacementAnalyzer : SemanticRuleAnalyzer
{
    private enum Container
    {
        Global,
        Struct,
        Interface,
        Extension,
        Namespace,
        Function,
        Block,
    }

    /// <summary>構造体の宣言の前に書ける語。</summary>
    private static readonly string[] Modifiers =
        ["public", "private", "internal", "export", "static", "extern", "typedef", "const", "uniform", "groupshared", "__exported", "inline", "partial"];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.DeclarationNotAllowed];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ShaderSyntaxToken[] tokens = [.. program.Tree.Root.DescendantTokens().Where(t => !t.IsMissing)];

            void Report(ShaderSyntaxToken token, string message)
            {
                if (compilation.IsReportable(token) && token.GetLocation() is { } location && reported.Add((location.FilePath, location.Span.Start)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.DeclarationNotAllowed, location, message));
                }
            }

            Walk(tokens, Report);
        }
    }

    private static void Walk(ShaderSyntaxToken[] tokens, Action<ShaderSyntaxToken, string> report)
    {
        Stack<Container> containers = new();
        int statementStart = 0;

        Container Current() => containers.Count == 0 ? Container.Global : containers.Peek();
        // いちばん近いブロックでない入れ物が関数か (関数の中の局所の構造体のメソッドは、関数の中の関数ではない)。
        bool InFunction() => containers.FirstOrDefault(c => c != Container.Block) == Container.Function;

        for (int i = 0; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            switch (token.Kind)
            {
                case ShaderSyntaxKind.OpenBraceToken:
                    containers.Push(Classify(tokens, statementStart, i, InFunction()));
                    statementStart = i + 1;
                    continue;

                case ShaderSyntaxKind.CloseBraceToken:
                    if (containers.Count > 0)
                    {
                        containers.Pop();
                    }

                    // struct S {} f() の S は、関数の戻り値の型に書いた構造体である。
                    if (i + 2 < tokens.Length && IsStructHeader(tokens, statementStart, i)
                        && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 2].Kind == ShaderSyntaxKind.OpenParenToken
                        && Current() is Container.Global or Container.Struct)
                    {
                        report(tokens[i + 1], "関数の戻り値の型に構造体を定義することはできません。構造体を先に宣言してから、その名前を書いてください。");
                    }

                    statementStart = i + 1;
                    continue;

                case ShaderSyntaxKind.SemicolonToken:
                    statementStart = i + 1;
                    continue;
            }

            if (token.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            bool atStatementStart = IsAtStatementStart(tokens, statementStart, i);

            // 型を書く位置の構造体の定義。
            if (token.Text == "struct" && !atStatementStart && i + 1 < tokens.Length
                && (tokens[i + 1].Kind == ShaderSyntaxKind.OpenBraceToken
                    || (tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && i + 2 < tokens.Length && tokens[i + 2].Kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.ColonToken)))
            {
                report(token, "型を書く位置に構造体を定義することはできません。構造体を先に宣言してから、その名前を書いてください。");
                continue;
            }

            if (!atStatementStart)
            {
                continue;
            }

            Container container = Current();
            string? what = (container, token.Text) switch
            {
                (Container.Struct or Container.Extension or Container.Interface, "namespace" or "extension" or "interface") => token.Text,
                (Container.Struct or Container.Extension, "__constraint") => token.Text,
                (Container.Interface, "enum" or "struct" or "class") => token.Text,
                (Container.Global or Container.Namespace, "__init" or "associatedtype") => token.Text,
                _ => null,
            };

            if (what is not null)
            {
                report(token, $"{Describe(container)}の中に {what} は書けません。");
                continue;
            }

            // 関数の中の関数の定義 (型 名前 ( … ) {)。
            if (InFunction() && i + 2 < tokens.Length
                && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 2].Kind == ShaderSyntaxKind.OpenParenToken
                && token.Text is not ("return" or "if" or "while" or "for" or "switch" or "else" or "case" or "do")
                && MatchingParen(tokens, i + 2) is { } close
                && close + 1 < tokens.Length && tokens[close + 1].Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                report(tokens[i + 1], $"関数の中に関数 '{tokens[i + 1].Text}' を定義することはできません。");
            }
        }
    }

    /// <summary>文の頭か (修飾子と属性を除いて、<paramref name="index"/> が文の最初の語か)。</summary>
    private static bool IsAtStatementStart(ShaderSyntaxToken[] tokens, int start, int index)
    {
        int i = start;

        while (i < index)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenBracketToken)
            {
                // [attribute(…)] を飛ばす。
                int depth = 0;
                for (; i < index; i++)
                {
                    depth += tokens[i].Kind switch
                    {
                        ShaderSyntaxKind.OpenBracketToken => 1,
                        ShaderSyntaxKind.CloseBracketToken => -1,
                        _ => 0,
                    };

                    if (depth == 0)
                    {
                        break;
                    }
                }

                i++;
                continue;
            }

            if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && Modifiers.Contains(tokens[i].Text))
            {
                i++;
                continue;
            }

            // __extern_cpp・__target_intrinsic(cpp, "Foo")・__glsl_extension(…) などの、__ で始まる修飾子 (括弧の中ごと) を飛ばす。
            if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i].Text.StartsWith("__", StringComparison.Ordinal) && tokens[i].Text != "__generic")
            {
                i++;

                if (i < index && tokens[i].Kind == ShaderSyntaxKind.OpenParenToken && MatchingParen(tokens, i) is { } close && close < index)
                {
                    i = close + 1;
                }

                continue;
            }

            // __generic<T : IFoo> struct S { … } の __generic<…> を飛ばす。
            if (tokens[i].Text == "__generic" && i + 1 < index && tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken)
            {
                int depth = 0;
                for (i++; i < index; i++)
                {
                    depth += tokens[i].Kind switch
                    {
                        ShaderSyntaxKind.LessThanToken => 1,
                        ShaderSyntaxKind.GreaterThanToken => -1,
                        ShaderSyntaxKind.GreaterThanGreaterThanToken => -2,
                        _ => 0,
                    };

                    if (depth <= 0)
                    {
                        break;
                    }
                }

                i++;
                continue;
            }

            return false;
        }

        return i == index;
    }

    /// <summary>文の頭から <c>{</c> までが構造体の見出し (<c>struct S</c>) か。</summary>
    private static bool IsStructHeader(ShaderSyntaxToken[] tokens, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Text is "struct" or "class")
            {
                return IsAtStatementStart(tokens, start, i);
            }
        }

        return false;
    }

    /// <summary><c>{</c> が開く入れ物の種類。</summary>
    private static Container Classify(ShaderSyntaxToken[] tokens, int start, int brace, bool inFunction)
    {
        for (int i = start; i < brace; i++)
        {
            switch (tokens[i].Text)
            {
                case "struct" or "class" when IsAtStatementStart(tokens, start, i):
                    return Container.Struct;
                case "interface" when IsAtStatementStart(tokens, start, i):
                    return Container.Interface;
                case "extension" when IsAtStatementStart(tokens, start, i) || (i > start && tokens[i - 1].Kind == ShaderSyntaxKind.GreaterThanToken):
                    return Container.Extension;
                case "namespace" when IsAtStatementStart(tokens, start, i):
                    return Container.Namespace;
                case "enum" or "cbuffer" or "tbuffer" when IsAtStatementStart(tokens, start, i):
                    return Container.Block;
            }
        }

        // ) の後ろの { は関数の本体 (関数の中では if・for などのブロック)。
        bool hasParen = tokens.Skip(start).Take(brace - start).Any(t => t.Kind == ShaderSyntaxKind.CloseParenToken);
        return hasParen && !inFunction ? Container.Function : inFunction ? Container.Block : Container.Block;
    }

    private static int? MatchingParen(ShaderSyntaxToken[] tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            depth += tokens[i].Kind switch
            {
                ShaderSyntaxKind.OpenParenToken => 1,
                ShaderSyntaxKind.CloseParenToken => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return i;
            }
        }

        return null;
    }

    private static string Describe(Container container) => container switch
    {
        Container.Struct => "構造体",
        Container.Interface => "インターフェイス",
        Container.Extension => "extension",
        Container.Namespace => "namespace",
        _ => "ファイルの直下",
    };
}
