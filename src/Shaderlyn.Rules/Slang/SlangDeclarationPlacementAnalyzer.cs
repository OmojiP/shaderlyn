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
/// <item>ファイルの直下の __init と associatedtype。extension の中の仮引数の無い __init (E30851)</item>
/// <item>関数の中の関数の定義 (<c>float g(int x) { … }</c>)</item>
/// <item>extension とインターフェイスの中の、static でないフィールド (E31400)</item>
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
        ["public", "private", "internal", "export", "static", "extern", "typedef", "const", "uniform", "groupshared", "__exported", "inline", "partial", "dyn"];

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
            // 構文解析が合成したトークン (__init の前の void など。幅が 0) は、書いたものではないので除く。
            ShaderSyntaxToken[] tokens = [.. program.Tree.Root.DescendantTokens().Where(t => !t.IsMissing && t.Span.Length > 0)];

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

            // extension<T = int> Foo<T> と __func_extension<T : IFloat = float> … の既定値 (E20104)。extension のジェネリックの仮引数は、型から決まるので既定値を書けない。
            if (token.Text is "extension" or "__func_extension" && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken)
            {
                int depth = 0;

                for (int j = i + 1; j < tokens.Length; j++)
                {
                    depth += tokens[j].Kind switch
                    {
                        ShaderSyntaxKind.LessThanToken => 1,
                        ShaderSyntaxKind.GreaterThanToken => -1,
                        ShaderSyntaxKind.GreaterThanGreaterThanToken => -2,
                        _ => 0,
                    };

                    if (depth <= 0 || tokens[j].Kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken)
                    {
                        break;
                    }

                    if (depth == 1 && tokens[j].Kind == ShaderSyntaxKind.EqualsToken && tokens[j - 1].Kind == ShaderSyntaxKind.IdentifierToken)
                    {
                        report(tokens[j], $"{token.Text} のジェネリックの仮引数には既定値を書けません (仮引数は、拡張する型から決まります)。");
                    }
                }
            }

            // extension の中の演算子 (E30073)。型の中の演算子は、構文木を読む SlangDeclarationAnalyzer が見る。呼び出しの operator() は書ける。
            if (Current() == Container.Extension
                && token.Text == "operator"
                && i + 2 < tokens.Length
                && !(tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken && tokens[i + 2].Kind == ShaderSyntaxKind.CloseParenToken))
            {
                report(token, $"演算子 operator{tokens[i + 1].Text} は、extension の中に書けません。関数の外に書いてください。");
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
                // extension の中の仮引数の無い __init (既定の初期化と重なる。E30851)。仮引数のある __init は書ける。
                (Container.Extension, "__init") when i + 2 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken && tokens[i + 2].Kind == ShaderSyntaxKind.CloseParenToken => "仮引数が存在しない __init",
                _ => null,
            };

            if (what is not null)
            {
                report(token, $"{Describe(container)}の中に {what} は書けません。");
                continue;
            }

            // extension とインターフェイスの中の、static でないフィールド (int x; / let x = 0;。E31400)。メソッド (名前の後ろが「(」) と、型引数のある型のフィールドは見ない。
            if (container is Container.Extension or Container.Interface
                && i + 2 < tokens.Length
                && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                && (tokens[i + 2].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.OpenBracketToken
                    || (token.Text is "let" or "var" && tokens[i + 2].Kind == ShaderSyntaxKind.ColonToken))
                && (token.Text is "let" or "var" || !Shaderlyn.Slang.Parsing.SlangKeywords.DeclarationKeywords.Contains(token.Text))
                && token.Text is not ("return" or "typedef" or "using" or "static" or "const")
                && !tokens[statementStart..i].Any(t => t.Text is "static" or "typedef"))
            {
                report(tokens[i + 1], $"{Describe(container)}の中に、static でないフィールド '{tokens[i + 1].Text}' は書けません。{(container == Container.Interface ? "値の要件は static const で書いてください。" : "extension で足せるのは、メソッド・static のメンバー・型だけです。")}");
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
        Container.Extension => "extension ",
        Container.Namespace => "namespace ",
        _ => "ファイルの直下",
    };
}
