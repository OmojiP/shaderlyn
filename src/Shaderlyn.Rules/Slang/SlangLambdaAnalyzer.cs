using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang のラムダ式 (<c>(int a) =&gt; { … }</c>) の本体を検査する。取り込んだ変数への書き込み (SG0309)、戻り値の型の食い違いとコピーできない型の取り込み (SG0355)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SG0309: 本体の外の局所変数と仮引数 (取り込んだ値) への代入と増減 (E30011)。取り込んだ値は書き換えられない</item>
/// <item>SG0355: 戻り値の型を書かないラムダ式の、整数のリテラルと浮動小数のリテラルの両方を返す return (E30111)。
/// コピーできない型 (RayQuery・HitObject) の局所変数の取り込み (E30112)</item>
/// </list>
/// 構文解析はラムダ式を構文木に残さないので、トークンの並びを読む。本体を波括弧で書いたものだけを見る。
/// </remarks>
internal sealed class SlangLambdaAnalyzer : SemanticRuleAnalyzer
{
    private static readonly string[] NonCopyable = ["RayQuery", "HitObject"];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.NotWritable, SlangRuleDescriptors.LambdaForm];

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

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                }
            }

            // 関数ごとの、本体の局所変数と仮引数の名前と型 (ラムダ式の外のもの)。
            List<(int Open, int Close, Dictionary<string, string> Names)> functions = [];
            Dictionary<ShaderSyntaxToken, int> index = new(ReferenceEqualityComparer.Instance);

            for (int i = 0; i < tokens.Length; i++)
            {
                index[tokens[i]] = i;
            }

            foreach (FunctionDeclarationSyntax function in program.Tree.Root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
            {
                if (function.Body is not { } body || !index.TryGetValue(body.OpenBrace, out int open) || !index.TryGetValue(body.CloseBrace, out int close))
                {
                    continue;
                }

                Dictionary<string, string> names = new(StringComparer.Ordinal);

                foreach (ParameterSyntax parameter in function.ParameterList)
                {
                    names.TryAdd(parameter.Name, parameter.Type.NameToken.Text);
                }

                foreach (VariableDeclaratorSyntax variable in body.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
                {
                    if (variable.Parent is VariableDeclarationSyntax declaration)
                    {
                        names.TryAdd(variable.Name, declaration.Type.NameToken.Text);
                    }
                }

                functions.Add((open, close, names));
            }

            for (int i = 0; i + 3 < tokens.Length; i++)
            {
                // ) => {
                if (tokens[i].Kind != ShaderSyntaxKind.CloseParenToken
                    || tokens[i + 1].Kind != ShaderSyntaxKind.EqualsToken || tokens[i + 2].Kind != ShaderSyntaxKind.GreaterThanToken
                    || tokens[i + 3].Kind != ShaderSyntaxKind.OpenBraceToken
                    || Opening(tokens, i) is not { } paramsOpen
                    || Closing(tokens, i + 3) is not { } bodyClose
                    || functions.FirstOrDefault(f => i > f.Open && i < f.Close) is not { Names: { } outer })
                {
                    continue;
                }

                // ラムダ式の仮引数 (それぞれの仮引数の最後の名前) と、本体で宣言した名前。
                HashSet<string> own = new(StringComparer.Ordinal);

                for (int j = paramsOpen + 1; j < i; j++)
                {
                    if (tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && tokens[j + 1].Kind is ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.CloseParenToken)
                    {
                        own.Add(tokens[j].Text);
                    }
                }

                for (int j = i + 4; j + 1 < bodyClose; j++)
                {
                    if (tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && tokens[j + 1].Kind == ShaderSyntaxKind.IdentifierToken
                        && j + 2 < bodyClose && tokens[j + 2].Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.ColonToken)
                    {
                        own.Add(tokens[j + 1].Text);
                    }
                }

                bool returnsInteger = false;
                bool returnsFloating = false;
                bool reportedReturn = false;
                HashSet<string> captured = new(StringComparer.Ordinal);

                for (int j = i + 4; j < bodyClose; j++)
                {
                    ShaderSyntaxToken token = tokens[j];

                    if (token.Kind != ShaderSyntaxKind.IdentifierToken)
                    {
                        continue;
                    }

                    bool member = tokens[j - 1].Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken;
                    bool local = !member && !own.Contains(token.Text) && outer.ContainsKey(token.Text);

                    // c = …・c += …・c++ (取り込んだ値への書き込み)
                    if (local && j + 1 < bodyClose
                        && (tokens[j + 1].Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken
                                or ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken
                            || tokens[j - 1].Kind is ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken)
                        && !(tokens[j + 1].Kind == ShaderSyntaxKind.EqualsToken && j + 2 < bodyClose && tokens[j + 2].Kind == ShaderSyntaxKind.EqualsToken))
                    {
                        Report(SlangRuleDescriptors.NotWritable, token, $"ラムダ式に取り込んだ '{token.Text}' は書き換えられません。");
                    }

                    // RayQuery q を取り込む
                    if (local && NonCopyable.Contains(outer[token.Text]) && captured.Add(token.Text))
                    {
                        Report(SlangRuleDescriptors.LambdaForm, token, $"コピーできない型 ({outer[token.Text]}) の '{token.Text}' は、ラムダ式に取り込めません。");
                    }

                    // return 1; と return 3.0; (戻り値の型の食い違い)
                    if (token.Text == "return" && j + 2 < bodyClose && tokens[j + 1].Kind == ShaderSyntaxKind.NumericLiteralToken && tokens[j + 2].Kind == ShaderSyntaxKind.SemicolonToken)
                    {
                        bool floating = ShaderLiteral.GetKind(tokens[j + 1]) == ShaderLiteralKind.Floating;
                        returnsInteger |= !floating;
                        returnsFloating |= floating;

                        if (returnsInteger && returnsFloating && !reportedReturn)
                        {
                            reportedReturn = true;
                            Report(SlangRuleDescriptors.LambdaForm, token, "ラムダ式の return の値の型 (整数と浮動小数) が食い違っています。どちらかにそろえてください。");
                        }
                    }
                }

                i = bodyClose;
            }
        }
    }

    /// <summary>index の ) に対応する ( の位置。</summary>
    private static int? Opening(ImmutableArray<ShaderSyntaxToken> tokens, int close)
    {
        int depth = 0;

        for (int i = close; i >= 0; i--)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.CloseParenToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.OpenParenToken && --depth == 0)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>index の { に対応する } の位置。</summary>
    private static int? Closing(ImmutableArray<ShaderSyntaxToken> tokens, int open)
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
