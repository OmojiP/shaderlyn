using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の組み込みの書き方 (register・sizeof・bit_cast・エントリポイント) を検査する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SG0103: 番号の無い <c>register(t)</c> (E39008)、2 つ目が space でない <c>register(s0, s1)</c> (E39009)、番号の無い <c>space</c> (E39010)</item>
/// <item>SG0301: 2 つ目に数か制約の無い型の仮引数を書いた <c>sizeof(T, L)</c> (E30086。データの並べ方の型を書く) と、エントリポイントの中の、大きさの違う型どうしの <c>bit_cast&lt;To, From&gt;</c> (E41202。コードを作るときに見る)</item>
/// <item>SG0407: SV_DispatchThreadID・SV_GroupID・SV_GroupThreadID の仮引数の、整数の 1 から 3 個の成分でない型 (E38024)</item>
/// <item>SG0304: <c>[shader("…")]</c> の、無いステージの名前 (E36105)</item>
/// <item>SG0345: <c>throws</c> を書いたエントリポイント (E38053)</item>
/// </list>
/// </remarks>
internal sealed partial class SlangBuiltinFormAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>[shader("…")] に書けるステージの名前。</summary>
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "vertex", "pixel", "fragment", "geometry", "hull", "domain", "compute", "raygeneration", "raygen", "intersection", "anyhit",
        "closesthit", "miss", "callable", "mesh", "amplification", "task", "node", "dispatch",
    };

    /// <summary>組み込みの数の型 (スカラーとベクトル) の名前と、成分の大きさ (バイト)・成分の数。</summary>
    [GeneratedRegex(@"^(?<base>bool|int|uint|float|half|double|int8_t|uint8_t|int16_t|uint16_t|int32_t|uint32_t|int64_t|uint64_t|float16_t|float32_t|float64_t)(?<count>[1-4])?$")]
    private static partial Regex NumericType();

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SlangRuleDescriptors.RegisterMismatch, SlangRuleDescriptors.WrongOperandTypes, SlangRuleDescriptors.InvalidDeclarationType,
         SlangRuleDescriptors.InvalidAttributeValue, SlangRuleDescriptors.ErrorHandling];

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

            CheckTokens(tokens, program.Tree.Root, Report);
            CheckExistentialBitCast(compilation, program, Report);

            foreach (FunctionDeclarationSyntax function in program.Tree.Root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
            {
                CheckEntryPoint(function, tokens, Report);
            }
        }
    }

    /// <summary>
    /// エントリポイント ([shader]・[numthreads] の関数) から届く関数の中の、インターフェイスの型への <c>bit_cast&lt;IFoo&gt;(x)</c> (E41204。コードを作るときに見る)。
    /// </summary>
    private static void CheckExistentialBitCast(ShaderCompilation compilation, AnalyzedProgram program, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        CompilationUnitSyntax root = program.Tree.Root;
        HashSet<string> interfaces = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>()
            .Where(t => t.Keyword.Text == "interface" && t.NameToken is not null && t.GenericParameterTokens.IsDefaultOrEmpty)
            .GroupBy(t => t.Name, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Key)];

        if (interfaces.Count == 0)
        {
            return;
        }

        FunctionDeclarationSyntax[] entries = [.. root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>()
            .Where(f => f.Body is not null && f.Attributes.Any(a => a.Name is "shader" or "numthreads"))];
        HashSet<FunctionDeclarationSyntax> reachable = [.. entries];

        foreach (FunctionDeclarationSyntax entry in entries)
        {
            reachable.UnionWith(compilation.EnumerateCallees(entry, program, int.MaxValue).Select(c => c.Function));
        }

        foreach (FunctionDeclarationSyntax function in reachable)
        {
            ShaderSyntaxToken[] body = [.. function.Body?.DescendantTokens() ?? []];

            for (int i = 0; i + 3 < body.Length; i++)
            {
                if (body[i].Text == "bit_cast" && body[i + 1].Kind == ShaderSyntaxKind.LessThanToken && body[i + 2].Kind == ShaderSyntaxKind.IdentifierToken
                    && body[i + 3].Kind == ShaderSyntaxKind.GreaterThanToken && interfaces.Contains(body[i + 2].Text))
                {
                    report(SlangRuleDescriptors.WrongOperandTypes, body[i],
                        $"bit_cast では、インターフェイスの型 {body[i + 2].Text} (具体的な型の決まらない型) の値を作れません。具体的な型にしてください。");
                }
            }
        }
    }

    private static void CheckTokens(ImmutableArray<ShaderSyntaxToken> tokens, CompilationUnitSyntax root, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        // 関数の、制約の無い型の仮引数 (<L> の L)。
        HashSet<string> unconstrained = new(StringComparer.Ordinal);

        foreach (FunctionDeclarationSyntax function in root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
        {
            ImmutableArray<ShaderSyntaxToken> generic = function.GenericParameterTokens;

            for (int i = 1; i + 1 < generic.Length; i++)
            {
                if (generic[i].Kind == ShaderSyntaxKind.IdentifierToken && generic[i - 1].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.CommaToken
                    && generic[i + 1].Kind is ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.GreaterThanToken)
                {
                    unconstrained.Add(generic[i].Text);
                }
            }
        }

        // エントリポイントの本体 (bit_cast の大きさは、コードを作るときに見るので、エントリポイントの中だけを見る)。
        BlockStatementSyntax[] entryBodies = [.. root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>()
            .Where(f => f.Body is not null && f.Attributes.Any(a => a.Name is "shader" or "numthreads")).Select(f => f.Body!)];

        bool InEntryPoint(ShaderSyntaxToken token)
            => entryBodies.Any(b => ReferenceEquals(b.OpenBrace.Source, token.Source) && b.OpenBrace.Span.Start < token.Span.Start && token.Span.Start < b.CloseBrace.Span.Start);

        for (int i = 0; i + 3 < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            // : register(t)・register(s0, s1)・register(s2, space)
            if (token.Text == "register" && i > 0 && tokens[i - 1].Kind == ShaderSyntaxKind.ColonToken && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken
                && tokens[i + 2].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                ShaderSyntaxToken slot = tokens[i + 2];

                if (slot.Text.All(char.IsAsciiLetter) && !slot.Text.StartsWith("space", StringComparison.Ordinal))
                {
                    report(SlangRuleDescriptors.RegisterMismatch, slot, $"register({slot.Text}) にはレジスタの番号を書いてください ({slot.Text}0 など)。");
                }
                else if (tokens[i + 3].Kind == ShaderSyntaxKind.CommaToken && i + 4 < tokens.Length && tokens[i + 4].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    ShaderSyntaxToken space = tokens[i + 4];

                    if (!space.Text.StartsWith("space", StringComparison.Ordinal))
                    {
                        report(SlangRuleDescriptors.RegisterMismatch, space, $"register の 2 つ目には space と番号を書いてください (space0 など。{space.Text} は space ではありません)。");
                    }
                    else if (space.Text == "space")
                    {
                        report(SlangRuleDescriptors.RegisterMismatch, space, "space の後ろに番号を書いてください (space0 など)。");
                    }
                }
            }

            // sizeof(float3, L)・sizeof(float3, 14) (2 つ目はデータの並べ方の型)
            if (token.Text is "sizeof" or "alignof" && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken
                && FindComma(tokens, i + 1) is { } comma && comma + 2 < tokens.Length && tokens[comma + 2].Kind == ShaderSyntaxKind.CloseParenToken
                && (tokens[comma + 1].Kind == ShaderSyntaxKind.NumericLiteralToken || unconstrained.Contains(tokens[comma + 1].Text)))
            {
                report(SlangRuleDescriptors.WrongOperandTypes, tokens[comma + 1],
                    $"{token.Text} の 2 つ目には、データの並べ方の型 (DefaultDataLayout・Std430DataLayout など、IBufferDataLayout の型) を書いてください。");
            }

            // bit_cast<uint32_t, uint64_t>(x) (大きさの違う型)
            if (token.Text == "bit_cast" && tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken && i + 5 < tokens.Length
                && tokens[i + 2].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 3].Kind == ShaderSyntaxKind.CommaToken
                && tokens[i + 4].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 5].Kind == ShaderSyntaxKind.GreaterThanToken
                && SizeOf(tokens[i + 2].Text) is { } to && SizeOf(tokens[i + 4].Text) is { } from && to != from && InEntryPoint(token))
            {
                report(SlangRuleDescriptors.WrongOperandTypes, token,
                    $"bit_cast の型の大きさが違います ({tokens[i + 2].Text} は {to} バイト、{tokens[i + 4].Text} は {from} バイト)。");
            }
        }
    }

    /// <summary>( の中の深さ 0 の , の位置。無ければ <see langword="null"/>。</summary>
    private static int? FindComma(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            depth += tokens[i].Kind switch
            {
                ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.LessThanToken => 1,
                ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.GreaterThanToken => -1,
                _ => 0,
            };

            if (depth == 0 && tokens[i].Kind == ShaderSyntaxKind.CloseParenToken)
            {
                return null;
            }

            if (depth == 1 && tokens[i].Kind == ShaderSyntaxKind.CommaToken)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>組み込みの数の型の大きさ (バイト)。分からなければ <see langword="null"/>。</summary>
    private static int? SizeOf(string type)
    {
        if (NumericType().Match(type) is not { Success: true } match)
        {
            return null;
        }

        int element = match.Groups["base"].Value switch
        {
            "int8_t" or "uint8_t" => 1,
            "half" or "int16_t" or "uint16_t" or "float16_t" => 2,
            "double" or "int64_t" or "uint64_t" or "float64_t" => 8,
            "bool" => 4,
            _ => 4,
        };

        return element * (match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1);
    }

    /// <summary>エントリポイントの、ステージの名前・throws・スレッドの番号の仮引数の型を見る。</summary>
    private static void CheckEntryPoint(FunctionDeclarationSyntax function, ImmutableArray<ShaderSyntaxToken> tokens, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        bool isEntry = false;

        foreach (AttributeSyntax attribute in function.Attributes)
        {
            if (attribute.Name == "numthreads")
            {
                isEntry = true;
            }

            if (attribute.Name == "shader" && attribute.ArgumentTokens is [{ Kind: ShaderSyntaxKind.StringLiteralToken } stage])
            {
                isEntry = true;
                string name = stage.Text.Trim('"');

                // _ を含む名前 (_raygen・ser_motion_raygen) は、ステージを含む能力の名前として書ける。
                if (!Stages.Contains(name) && !name.Contains('_', StringComparison.Ordinal))
                {
                    report(SlangRuleDescriptors.InvalidAttributeValue, attribute.NameToken, $"[shader(\"{name}\")] のステージの名前がありません (compute・vertex・fragment など)。");
                }
            }
        }

        if (!isEntry)
        {
            return;
        }

        // ) と { の間の throws
        int close = tokens.IndexOf(function.CloseParen);

        for (int i = close + 1; close >= 0 && i < tokens.Length && tokens[i].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); i++)
        {
            if (tokens[i].Text == "throws")
            {
                report(SlangRuleDescriptors.ErrorHandling, function.NameToken, $"エントリポイント '{function.Name}' には throws を書けません。誤りは関数の中で受けてください (try … catch)。");
                break;
            }
        }

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            if (parameter.Semantics.FirstOrDefault(s => s.Name.ToUpperInvariant() is "SV_DISPATCHTHREADID" or "SV_GROUPID" or "SV_GROUPTHREADID") is not { } semantic
                || parameter.Type.OpenAngle is not null)
            {
                continue;
            }

            string type = parameter.Type.NameToken.Text;

            if (NumericType().Match(type) is { Success: true } match
                && (match.Groups["base"].Value is not ("int" or "uint" or "int32_t" or "uint32_t") || (match.Groups["count"].Success && match.Groups["count"].Value == "4")))
            {
                report(SlangRuleDescriptors.InvalidDeclarationType, parameter.NameToken,
                    $"{semantic.Name} の仮引数の型は、int か uint の 1 から 3 個の成分にしてください ({type} は使えません)。");
            }
        }
    }
}
