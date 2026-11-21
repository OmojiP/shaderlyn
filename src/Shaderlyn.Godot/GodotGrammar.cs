using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Godot.Parsing;

namespace Shaderlyn.Godot;

/// <summary>
/// 共通の構文解析器は読めるが、Godot のシェーダーのコンパイラ (<c>ShaderLanguage</c>) が構文の段階で誤りにする書き方を報告する (CF0001)。
/// </summary>
/// <remarks>
/// <para>
/// Godot の文法は C 系の言語より狭い。関数の外の変数は <c>const</c> / <c>uniform</c> / <c>varying</c> だけ、配列は 1 次元だけ、
/// サンプラーは uniform と仮引数だけ、文字列はヒントと <c>#include</c> の中だけである。
/// </para>
/// <para>
/// どれも Godot 4.7.2 が誤りにすることを、Godot に読ませて確かめた (<c>tools/oracles/godot-errors</c>)。
/// 構文木は壊れていないので、解析は止めない (<see cref="ShaderDescriptors.RecoverableSyntaxError"/>)。
/// </para>
/// </remarks>
internal static class GodotGrammar
{
    /// <summary>精度の修飾子。</summary>
    private static readonly FrozenSet<string> Precisions = new[] { "lowp", "mediump", "highp" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>補間の修飾子。</summary>
    private static readonly FrozenSet<string> Interpolations = new[] { "flat", "smooth" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>構文木と展開したトークンを調べ、Godot が誤りにする書き方を報告に足す。</summary>
    /// <param name="root">構文木の根。</param>
    /// <param name="tokens">展開したトークン。</param>
    /// <param name="text">解析しているファイル。このファイルに書いたものだけを報告する。</param>
    /// <param name="diagnostics">報告を足す先。</param>
    public static void Check(CompilationUnitSyntax root, ImmutableArray<ShaderSyntaxToken> tokens, SourceText text, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        void Report(ShaderSyntaxToken token, string message)
        {
            if (ReferenceEquals(token.Source, text) && !token.IsMissing && token.GetLocation() is { } location)
            {
                diagnostics.Add(Diagnostic.Create(ShaderDescriptors.RecoverableSyntaxError, location, message));
            }
        }

        CheckModes(root, Report);

        foreach (DeclarationSyntax declaration in root.Declarations)
        {
            switch (declaration)
            {
                case VariableDeclarationSyntax variable:
                    CheckGlobal(variable, Report);
                    break;
                case FunctionDeclarationSyntax function:
                    CheckFunction(function, Report);
                    break;
            }
        }

        foreach (LocalDeclarationStatementSyntax local in root.DescendantNodesAndSelf().OfType<LocalDeclarationStatementSyntax>())
        {
            if (local.Declaration is VariableDeclarationSyntax variable)
            {
                CheckLocal(variable, Report);
            }
        }

        CheckStrings(root, tokens, Report);
        CheckHintArguments(root, Report);
        CheckStatementsAndExpressions(root, tokens, Report);
        CheckBrackets(tokens, Report);
    }

    /// <summary>
    /// 閉じていない [ と ( を、Godot と同じく、閉じる前に出会った ; や波括弧の位置で報告する
    /// (共通の構文解析器は、ファイルの末尾まで読んでから報告する)。for の ( ) の中の ; は区切りである。最初の 1 つだけを報告する。
    /// </summary>
    private static void CheckBrackets(ImmutableArray<ShaderSyntaxToken> tokens, Action<ShaderSyntaxToken, string> report)
    {
        Stack<(ShaderSyntaxKind Kind, bool For)> open = new();

        for (int i = 0; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            switch (token.Kind)
            {
                case ShaderSyntaxKind.OpenParenToken:
                    open.Push((token.Kind, i > 0 && tokens[i - 1].TextIs("for")));
                    break;

                case ShaderSyntaxKind.OpenBracketToken:
                    open.Push((token.Kind, false));
                    break;

                case ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CloseBracketToken:
                    ShaderSyntaxKind expected = token.Kind == ShaderSyntaxKind.CloseParenToken ? ShaderSyntaxKind.OpenParenToken : ShaderSyntaxKind.OpenBracketToken;

                    if (open.Count == 0 || open.Peek().Kind != expected)
                    {
                        return;
                    }

                    open.Pop();
                    break;

                case ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken when open.Count > 0:
                    (ShaderSyntaxKind kind, bool isFor) = open.Peek();

                    if (kind == ShaderSyntaxKind.OpenParenToken && isFor && token.Kind == ShaderSyntaxKind.SemicolonToken)
                    {
                        break;
                    }

                    report(token, kind == ShaderSyntaxKind.OpenBracketToken ? "']' が必要です。" : "')' が必要です。");
                    return;
            }
        }
    }

    /// <summary>構造体・switch・式の、Godot の文法で書けない形を調べる。</summary>
    private static void CheckStatementsAndExpressions(CompilationUnitSyntax root, ImmutableArray<ShaderSyntaxToken> tokens, Action<ShaderSyntaxToken, string> report)
    {
        Dictionary<ShaderSyntaxToken, int> positions = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < tokens.Length; i++)
        {
            positions[tokens[i]] = i;
        }

        ShaderSyntaxKind Next(ShaderSyntaxToken token)
            => positions.TryGetValue(token, out int i) && i + 1 < tokens.Length ? tokens[i + 1].Kind : ShaderSyntaxKind.EndOfFileToken;

        foreach (Core.Syntax.SyntaxNode node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                // struct { float a; }; (名前の無い構造体)
                // do { } (true); (do の後ろの while が無い)
                case DoWhileStatementSyntax { WhileKeyword.IsMissing: true } loop:
                    report(loop.OpenParen.IsMissing ? loop.DoKeyword : loop.OpenParen, "do の文の後ろには while が必要です。");
                    break;

                case StructDeclarationSyntax { NameToken: null } structure:
                    report(structure.Keyword, "構造体には名前が必要です。");
                    break;

                // for (i = 0; …) (最初は変数の宣言でなければならない)
                case ForStatementSyntax { Initializer: ExpressionSyntax initializer } when initializer.FirstToken is { } initializerToken:
                    report(initializerToken, "for の最初には変数の宣言を書きます。");
                    break;

                // struct S { }; (メンバーの無い構造体)
                case StructDeclarationSyntax { Members.IsEmpty: true } structure:
                    report(structure.CloseBrace, "構造体には 1 つ以上のメンバーが必要です。");
                    break;

                // switch の中の、最初の case / default より前の文。
                case SwitchStatementSyntax { Statements: [var first and not SwitchLabelStatementSyntax, ..] }:
                    if (first.FirstToken is { } firstToken)
                    {
                        report(firstToken, "switch の中には case か default から書きます。");
                    }

                    break;

                // 1.0() (名前でないものの呼び出し)
                case InvocationExpressionSyntax { Target: LiteralExpressionSyntax literal }:
                    report(literal.Token, "関数の名前でないものは呼び出せません。");
                    break;

                // a. (メンバーの名前が無い)
                case MemberAccessExpressionSyntax { NameToken.IsMissing: true } member:
                    report(member.DotToken, "'.' の後ろにはメンバーの名前が必要です。");
                    break;

                // vec2 + 1.0 (型の名前の後ろに ( が無い。float[3](…) の配列の値の作成は [ が続く)
                case IdentifierExpressionSyntax identifier
                    when GodotKeywords.BuiltInTypes.Contains(identifier.Name)
                         && Next(identifier.Identifier) is not (ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken):
                    report(identifier.Identifier, $"型の名前 '{identifier.Name}' の後ろには ( が必要です。");
                    break;
            }
        }
    }


    /// <summary><c>shader_type</c> / <c>render_mode</c> / <c>stencil_mode</c> / <c>group_uniforms</c> の形を調べる。</summary>
    private static void CheckModes(CompilationUnitSyntax root, Action<ShaderSyntaxToken, string> report)
    {
        bool shaderType = false;
        bool groupOpen = false;

        foreach (IncompleteDeclarationSyntax declaration in root.Declarations.OfType<IncompleteDeclarationSyntax>())
        {
            ImmutableArray<ShaderSyntaxToken> tokens = declaration.Tokens;

            if (tokens.IsEmpty)
            {
                continue;
            }

            // 最後のトークンはセミコロン (宣言として読めなかった ; だけのものもある)。
            int end = Math.Max(1, tokens[^1].Kind == ShaderSyntaxKind.SemicolonToken ? tokens.Length - 1 : tokens.Length);
            ImmutableArray<ShaderSyntaxToken> body = tokens[1..end];

            switch (tokens[0].Text)
            {
                case "shader_type":
                    if (shaderType)
                    {
                        report(tokens[0], "shader_type は 1 度だけ書けます。");
                    }

                    // shader_type spatial void … (名前の後ろに ; が無い)
                    if (body.Length > 1)
                    {
                        report(body[0], $"shader_type {body[0].Text} の後ろには ';' が必要です。");
                    }

                    shaderType = true;
                    break;

                case "render_mode" or "stencil_mode" when body.IsEmpty || body[0].Kind != ShaderSyntaxKind.IdentifierToken:
                    report(body.IsEmpty ? tokens[^1] : body[0], $"{tokens[0].Text} の後ろには、モードの名前が必要です。");
                    break;

                case "group_uniforms":
                    if (body.IsEmpty)
                    {
                        if (!groupOpen)
                        {
                            report(tokens[0], "group_uniforms; で閉じるグループが開かれていません。");
                        }

                        groupOpen = false;
                        break;
                    }

                    // group_uniforms a; / group_uniforms a.b; (Godot はドットでいくつつないでも通す)
                    bool wellFormed = body.Length % 2 == 1
                        && body.Select((t, i) => i % 2 == 0 ? t.Kind == ShaderSyntaxKind.IdentifierToken : t.Kind == ShaderSyntaxKind.DotToken).All(ok => ok);

                    if (!wellFormed)
                    {
                        report(body.Length > 1 ? body[1] : body[0], "group_uniforms の後ろには、グループの名前 (a や a.b) と ; を書きます。");
                    }

                    groupOpen = true;
                    break;
            }
        }
    }

    /// <summary>関数の外の変数を調べる。</summary>
    private static void CheckGlobal(VariableDeclarationSyntax declaration, Action<ShaderSyntaxToken, string> report)
    {
        ImmutableArray<ShaderSyntaxToken> modifiers = declaration.ModifierTokens;
        int uniform = modifiers.IndexOf(modifiers.FirstOrDefault(m => m.Text == "uniform")!);
        int varying = modifiers.IndexOf(modifiers.FirstOrDefault(m => m.Text == "varying")!);
        bool isConst = declaration.HasModifier("const");

        foreach (ShaderSyntaxToken scope in modifiers.Where(m => m.Text is "global" or "instance"))
        {
            if (uniform < 0)
            {
                report(scope, $"{scope.Text} の後ろには uniform が必要です。");
            }
        }

        foreach (ShaderSyntaxToken interpolation in modifiers.Where(m => Interpolations.Contains(m.Text)))
        {
            if (uniform >= 0)
            {
                report(interpolation, "uniform に補間の修飾子は付けられません。");
            }
            else if (varying < 0 || modifiers.IndexOf(interpolation) < varying)
            {
                report(interpolation, $"補間の修飾子は varying の後ろに書きます (varying {interpolation.Text} …)。");
            }
        }

        if (uniform < 0 && varying < 0 && !isConst && !modifiers.Any(m => m.Text is "global" or "instance" || Interpolations.Contains(m.Text)))
        {
            ShaderSyntaxToken at = declaration.Variables.FirstOrDefault()?.NameToken ?? declaration.Type.NameToken;
            report(at, "関数の外には、const の定数・uniform・varying だけを宣言できます。");
        }

        if (isConst)
        {
            if (IsSampler(declaration.Type))
            {
                report(declaration.Type.NameToken, "サンプラーの定数は宣言できません。");
            }

            foreach (VariableDeclaratorSyntax variable in declaration.Variables.Where(v => v.EqualsToken is null))
            {
                report(variable.NameToken, $"定数 '{variable.Name}' には初期値が必要です。");
            }
        }

        CheckArrayDimensions(declaration, report);
    }

    /// <summary>関数の宣言 (戻り値と仮引数) を調べる。</summary>
    private static void CheckFunction(FunctionDeclarationSyntax function, Action<ShaderSyntaxToken, string> report)
    {
        // float f(float x); (Godot には関数の前方宣言が無い)
        if (function.Body is null)
        {
            report(function.NameToken, $"関数 '{function.Name}' には本体 {{ … }} が必要です (Godot では前方宣言を書けません)。");
        }

        if (function.ModifierTokens.FirstOrDefault(m => m.Text == "const") is { } constToken)
        {
            report(constToken, "関数の戻り値に const は付けられません。");
        }

        if (IsSampler(function.ReturnType))
        {
            report(function.ReturnType.NameToken, "サンプラーを返す関数は宣言できません。");
        }

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            ShaderSyntaxToken? direction = parameter.ModifierTokens.FirstOrDefault(m => m.Text is "out" or "inout");

            if (direction is not null && parameter.ModifierTokens.Any(m => m.Text == "const"))
            {
                report(direction, $"const の仮引数に {direction.Text} は付けられません。");
            }

            if (direction is not null && IsSampler(parameter.Type))
            {
                report(parameter.Type.NameToken, "サンプラーは out / inout の仮引数にできません。");
            }

            if (parameter.Type.Name == "void")
            {
                report(parameter.Type.NameToken, "仮引数の型に void は使用できません。");
            }
            else if (parameter.NameToken.IsMissing || parameter.NameToken.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                report(parameter.Type.NameToken, "仮引数には名前が必要です。");
            }
        }
    }

    /// <summary>関数の中の変数を調べる。</summary>
    private static void CheckLocal(VariableDeclarationSyntax declaration, Action<ShaderSyntaxToken, string> report)
    {
        if (IsSampler(declaration.Type))
        {
            report(declaration.Type.NameToken, "関数の中にサンプラーの変数は宣言できません。");
        }

        if (declaration.ModifierTokens.FirstOrDefault(m => Precisions.Contains(m.Text)) is { } precision)
        {
            string type = declaration.Type.Name;

            if (type is "bool" or "bvec2" or "bvec3" or "bvec4")
            {
                report(precision, "bool の型に精度の修飾子は付けられません。");
            }
            else if (!GodotKeywords.BuiltInTypes.Contains(type))
            {
                report(precision, "構造体の型に精度の修飾子は付けられません。");
            }
        }

        foreach (VariableDeclaratorSyntax variable in declaration.Variables.Where(v => v.EqualsToken is null && declaration.HasModifier("const")))
        {
            report(variable.NameToken, $"定数 '{variable.Name}' には初期値が必要です。");
        }

        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
        {
            // 要素の数を書かない配列は、{ … } か配列の値の作成でしか初期化できない (float a[] = 1.0; は誤り)。
            if (IsUnsized(variable))
            {
                // float a[]; / float a[] = float[](…); (大きさを書かない配列は { … } でだけ初期化できる)
                if (variable.Initializer is null)
                {
                    report(variable.NameToken, $"大きさを書かない配列 '{variable.Name}' には、初期値 {{ … }} が必要です。");
                }
                else if (variable.Initializer is not InitializerListExpressionSyntax && variable.Initializer.FirstToken is { } first)
                {
                    report(first, $"大きさを書かない配列 '{variable.Name}' は {{ … }} で初期化します。");
                }
            }
            else if (variable.IsArray && variable.Initializer is LiteralExpressionSyntax literal)
            {
                report(literal.Token, $"配列 '{variable.Name}' の初期化には {{ … }} か配列の値の作成が必要です。");
            }
        }

        CheckArrayDimensions(declaration, report);
    }

    /// <summary>大きさを書かない配列 (<c>float a[]</c>、<c>float[] a</c>) か。</summary>
    private static bool IsUnsized(VariableDeclaratorSyntax variable)
        => variable.ArrayRankTokens is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }]
           || (variable.ArrayRankTokens.IsEmpty && variable.Parent is VariableDeclarationSyntax { Type.ArrayRanks: [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }] });

    /// <summary>配列の配列 (<c>float a[2][3]</c>) を報告する。Godot の配列は 1 次元だけである。</summary>
    private static void CheckArrayDimensions(VariableDeclarationSyntax declaration, Action<ShaderSyntaxToken, string> report)
    {
        foreach (VariableDeclaratorSyntax variable in declaration.Variables.Where(v => v.ArrayDimensions > 1))
        {
            ShaderSyntaxToken second = variable.ArrayRankTokens.Where(t => t.Kind == ShaderSyntaxKind.OpenBracketToken).Skip(1).FirstOrDefault()
                ?? variable.NameToken;
            report(second, "Godot の配列は 1 次元だけです (配列の配列は書けません)。");
        }
    }

    /// <summary>文字列のリテラルを報告する。Godot で文字列を書けるのは、ヒントの引数 (<c>hint_enum("A")</c>) と <c>#include</c> だけである。</summary>
    private static void CheckStrings(CompilationUnitSyntax root, ImmutableArray<ShaderSyntaxToken> tokens, Action<ShaderSyntaxToken, string> report)
    {
        HashSet<ShaderSyntaxToken> allowed = [.. root.DescendantNodesAndSelf().OfType<SemanticSyntax>().SelectMany(s => s.ArgumentTokens)];

        foreach (ShaderSyntaxToken token in tokens.Where(t => t.Kind == ShaderSyntaxKind.StringLiteralToken && !allowed.Contains(t)))
        {
            report(token, "Godot では、文字列はヒントの引数と #include の中にしか書けません。");
        }
    }

    /// <summary>ヒントの引数がカンマで区切られているかを調べる (<c>hint_range(0.0 1.0)</c> は誤り)。</summary>
    private static void CheckHintArguments(CompilationUnitSyntax root, Action<ShaderSyntaxToken, string> report)
    {
        foreach (SemanticSyntax hint in root.DescendantNodesAndSelf().OfType<SemanticSyntax>())
        {
            ImmutableArray<ShaderSyntaxToken> arguments = hint.ArgumentTokens;

            for (int i = 1; i < arguments.Length; i++)
            {
                if (IsValueEnd(arguments[i - 1]) && IsValueStart(arguments[i]))
                {
                    report(arguments[i], $"{hint.Name} の引数はカンマで区切ります。");
                    break;
                }
            }
        }

        static bool IsValueEnd(ShaderSyntaxToken token)
            => token.Kind is ShaderSyntaxKind.NumericLiteralToken or ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.StringLiteralToken or ShaderSyntaxKind.CloseParenToken;

        static bool IsValueStart(ShaderSyntaxToken token)
            => token.Kind is ShaderSyntaxKind.NumericLiteralToken or ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.StringLiteralToken;
    }

    /// <summary>サンプラーの型か。</summary>
    private static bool IsSampler(TypeSyntax type) => type.Name.Contains("sampler", StringComparison.Ordinal);
}
