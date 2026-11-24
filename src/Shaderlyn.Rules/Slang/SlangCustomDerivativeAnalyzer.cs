using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、自分で書いた微分の関数と <c>no_diff</c> の書き方を検査する (SG0360)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>[ForwardDerivative(g)]</c> の g の仮引数: 微分する型 (float・half・double とそのベクトル・行列) の仮引数 T は <c>DifferentialPair&lt;T&gt;</c>、ほかの型は T のまま。数が合わなければ E31150、型が合わなければ E31149</item>
/// <item><c>[BackwardDerivative(g)]</c> の g の仮引数: 微分する型の仮引数 T は <c>inout DifferentialPair&lt;T&gt;</c>、ほかの型は T のまま。戻り値が微分する型なら、最後にその微分 (T か T.Differential) の仮引数を足す</item>
/// <item><c>[ForwardDerivativeOf(f)]</c> と <c>[BackwardDerivativeOf(f)]</c> (微分の関数の側に書く属性) も、同じ形で見る</item>
/// <item>static のメソッドの微分に指した、static でないメソッド (E31156)</item>
/// <item>関数の呼び出しでない値の <c>no_diff x</c> (E38031) と、微分しない関数の中の <c>no_diff</c> (E38033)</item>
/// <item>微分する関数の中の後ろ向きの微分の呼び出し (E38037)、let の DifferentialPair を渡す後ろ向きの微分 (E30047)</item>
/// <item>微分する仮引数の <c>__constref</c> (E38034)、IDifferentiable の型の微分するメソッドの <c>[constref]</c> (E38036)</item>
/// <item>インターフェイスの要件に当てた微分の関数 (E31152) と、微分の関数にした要件 (E31153)</item>
/// </list>
/// 見るのは、ジェネリックでなく、同じ名前の関数が 1 つだけで、仮引数の型がどれも組み込みの数の型 (微分するかどうかが決まるもの) で、
/// 元の関数の仮引数に <c>out</c>・<c>inout</c>・<c>no_diff</c> を書いていないものだけである。
/// </remarks>
internal sealed partial class SlangCustomDerivativeAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>微分する組み込みの型 (float・half・double とそのベクトル・行列)。</summary>
    [GeneratedRegex(@"^(float|half|double)([1-4](x[1-4])?)?$")]
    private static partial Regex DifferentiableType();

    /// <summary>微分しない組み込みの型 (整数と bool とそのベクトル・行列)。</summary>
    [GeneratedRegex(@"^(u?int|bool|u?int(8|16|32|64)_t)([1-4](x[1-4])?)?$")]
    private static partial Regex PlainType();

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.CustomDerivative, SlangRuleDescriptors.InterfaceRequirement];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;

            void Report(ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.CustomDerivative, location, message));
                }
            }

            // 名前ごとの関数 (関数の外のものと、型のメソッド)。同じ入れ物の同じ名前が 2 つ以上あれば、どれか決めない。
            FunctionDeclarationSyntax[] all = [.. root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>()];

            FunctionDeclarationSyntax? Find(string name, object? container)
            {
                FunctionDeclarationSyntax[] found = [.. all.Where(f => f.Name == name && ReferenceEquals(f.Parent, container) && f.QualifierTokens.IsEmpty)];

                if (found.Length == 0 && container is not CompilationUnitSyntax)
                {
                    found = [.. all.Where(f => f.Name == name && f.Parent is CompilationUnitSyntax && f.QualifierTokens.IsEmpty)];
                }

                // 宣言と定義の 2 つ (前方宣言) は 1 つと数える。
                return found.Length == 1 || (found.Length == 2 && found.Count(f => f.Body is not null) == 1) ? found.First(f => f.Body is not null || found.Length == 1) : null;
            }

            // 微分の属性で指した関数の名前 (微分の関数として微分の文脈で使う)。
            HashSet<string> derivatives = [.. all.SelectMany(f => f.Attributes)
                .Where(a => a.Name.Contains("Derivative", StringComparison.Ordinal) || a.Name.Contains("PrimalSubstitute", StringComparison.Ordinal))
                .SelectMany(a => a.ArgumentTokens).Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text)];

            // 微分する関数 (fwd_diff(f)・bwd_diff(f) で微分するもの)。
            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
            HashSet<string> differentiated = new(StringComparer.Ordinal);

            for (int i = 0; i + 3 < tokens.Length; i++)
            {
                if (tokens[i].Text is "fwd_diff" or "bwd_diff" or "__fwd_diff" or "__bwd_diff" && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken
                    && tokens[i + 2].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 3].Kind == ShaderSyntaxKind.CloseParenToken)
                {
                    differentiated.Add(tokens[i + 2].Text);
                }
            }

            // インターフェイスの要件 (本体の無いメソッド) の名前。インターフェイスの名前ごと。
            Dictionary<string, HashSet<string>> requirements = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax face in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>().Where(t => t.Keyword.Text == "interface" && t.NameToken is not null))
            {
                requirements[face.Name] = [.. face.Members.OfType<FunctionDeclarationSyntax>().Where(m => m.Body is null).Select(m => m.Name)];
            }

            bool IsRequirement(FunctionDeclarationSyntax f) => f.Body is null && f.Parent is StructDeclarationSyntax { Keyword.Text: "interface" };

            CheckNonExportedDerivatives(compilation, program, all, Report);
            CheckDifferentiableRequirements(compilation, program, context);
            CheckNonDifferentiableCalls(compilation, program, all, tokens, context);

            foreach (FunctionDeclarationSyntax function in all)
            {
                CheckRequirementDerivatives(function, requirements, IsRequirement(function), Report);
                CheckConstRef(function, Report);
                CheckBackwardCalls(function, tokens, differentiated, Report);

                foreach (AttributeSyntax attribute in function.Attributes)
                {
                    // [PrimalSubstituteOf(f)] の関数は、f と同じか強い微分の属性が必要 (前向き < 後ろ向き。E31158)。
                    if (attribute.Name == "PrimalSubstituteOf"
                        && attribute.ArgumentTokens is [{ Kind: ShaderSyntaxKind.IdentifierToken } substituted]
                        && Find(substituted.Text, function.Parent) is { } original
                        && DifferentiabilityLevel(original) is var required and > 0 and < 3
                        && DifferentiabilityLevel(function) < required)
                    {
                        Report(attribute.NameToken,
                            $"'{original.Name}' は{(required == 2 ? "後ろ向き" : "前向き")}に微分する関数なので、置き換える関数 '{function.Name}' にも [{(required == 2 ? "Differentiable" : "ForwardDifferentiable")}] を書いてください。");
                        continue;
                    }

                    if (attribute.Name is not ("ForwardDerivative" or "BackwardDerivative" or "ForwardDerivativeOf" or "BackwardDerivativeOf")
                        || attribute.ArgumentTokens is not [{ Kind: ShaderSyntaxKind.IdentifierToken } target]
                        || Find(target.Text, function.Parent) is not { } other)
                    {
                        continue;
                    }

                    bool of = attribute.Name.EndsWith("Of", StringComparison.Ordinal);
                    (FunctionDeclarationSyntax primal, FunctionDeclarationSyntax derivative) = of ? (other, function) : (function, other);
                    bool backward = attribute.Name.StartsWith("Backward", StringComparison.Ordinal);

                    // static のメソッドの微分は、static でなければならない (E31156)。
                    if (!of && primal.Parent is StructDeclarationSyntax && ReferenceEquals(primal.Parent, derivative.Parent)
                        && IsStatic(primal) && !IsStatic(derivative))
                    {
                        Report(attribute.NameToken, $"static のメソッド '{primal.Name}' の微分 '{derivative.Name}' も、static にしてください。");
                        continue;
                    }

                    // 型のメソッド (static でないもの) の微分は、最初に自分の DifferentialPair を受け取る。その形は見ない。
                    if (primal.Parent is StructDeclarationSyntax && !IsStatic(primal))
                    {
                        continue;
                    }

                    if (CheckSignature(primal, derivative, backward) is { } problem)
                    {
                        Report(attribute.NameToken, $"微分の関数 '{derivative.Name}' の形が、'{primal.Name}' から決まる形と合いません。{problem}");
                    }
                }

                CheckNoDiff(function, program.Tree.PreprocessResult.Tokens, derivatives.Contains(function.Name), Report);
            }
        }
    }

    /// <summary>
    /// インターフェイスの要件と微分の属性 (E31152・E31153)。要件に微分を当てるもの (<c>[ForwardDerivativeOf(original)]</c> の original が要件) と、
    /// 要件を微分の関数にするもの (要件に書いた <c>[ForwardDerivativeOf(f)]</c>、<c>[ForwardDerivative(IFoo.dd)]</c>)。
    /// </summary>
    private static void CheckRequirementDerivatives(FunctionDeclarationSyntax function, Dictionary<string, HashSet<string>> requirements, bool isRequirement,
        Action<ShaderSyntaxToken, string> report)
    {
        foreach (AttributeSyntax attribute in function.Attributes)
        {
            if (!attribute.Name.Contains("Derivative", StringComparison.Ordinal) && !attribute.Name.Contains("PrimalSubstitute", StringComparison.Ordinal))
            {
                continue;
            }

            bool of = attribute.Name.EndsWith("Of", StringComparison.Ordinal);
            ImmutableArray<ShaderSyntaxToken> arguments = attribute.ArgumentTokens;

            // 指した関数が要件か (IFoo.bar と、同じインターフェイスの中の bar)。
            bool targetIsRequirement = arguments switch
            {
                [{ Kind: ShaderSyntaxKind.IdentifierToken } owner, { Kind: ShaderSyntaxKind.DotToken }, { Kind: ShaderSyntaxKind.IdentifierToken } name]
                    => requirements.TryGetValue(owner.Text, out HashSet<string>? names) && names.Contains(name.Text),
                [{ Kind: ShaderSyntaxKind.IdentifierToken } name]
                    => function.Parent is StructDeclarationSyntax { Keyword.Text: "interface" } face && requirements.TryGetValue(face.Name, out HashSet<string>? own) && own.Contains(name.Text),
                _ => false,
            };

            // 元の関数が要件 (…Of の指す先、…Of でなければ書いた関数) なら E31152、微分の関数が要件なら E31153。
            bool primalIsRequirement = of ? targetIsRequirement : isRequirement;
            bool derivativeIsRequirement = of ? isRequirement : targetIsRequirement;

            if (primalIsRequirement)
            {
                report(attribute.NameToken, "インターフェイスの要件に、微分の関数を当てられません。微分は、要件を満たす型の側で書いてください。");
            }
            else if (derivativeIsRequirement)
            {
                report(attribute.NameToken, "インターフェイスの要件を、微分の関数にはできません。");
            }
        }
    }

    /// <summary>微分する関数の __constref の微分する仮引数 (E38034) と、IDifferentiable の型の微分するメソッドの [constref] (E38036)。</summary>
    private static void CheckConstRef(FunctionDeclarationSyntax function, Action<ShaderSyntaxToken, string> report)
    {
        if (!function.Attributes.Any(a => a.Name is "Differentiable" or "ForwardDifferentiable" or "BackwardDifferentiable"))
        {
            return;
        }

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            if ((parameter.ModifierTokens.FirstOrDefault(t => t.Text == "__constref") ?? parameter.Type.ModifierTokens.FirstOrDefault(t => t.Text == "__constref")) is { } constref
                && DifferentiableType().IsMatch(TypeText(parameter.Type))
                && !parameter.ModifierTokens.Any(t => t.Text == "no_diff") && !parameter.Type.ModifierTokens.Any(t => t.Text == "no_diff"))
            {
                report(constref, $"微分する関数の微分する仮引数 '{parameter.Name}' に、__constref は書けません。");
            }
        }

        if (function.Attributes.FirstOrDefault(a => a.Name == "constref") is { } attribute
            && function.Parent is StructDeclarationSyntax owner && owner.Keyword.Text is "struct" or "class"
            && owner.BaseTypeName == "IDifferentiable")
        {
            report(attribute.NameToken, "IDifferentiable の型の微分するメソッドに、[constref] は書けません。");
        }
    }

    /// <summary>
    /// 微分する関数の中の、後ろ向きの微分の呼び出し (E38037。後ろ向きの微分の結果は、さらに微分できない) と、
    /// let の DifferentialPair を後ろ向きの微分に渡すもの (E30047。書き換えられる値でなければならない)。
    /// </summary>
    private static void CheckBackwardCalls(FunctionDeclarationSyntax function, ImmutableArray<ShaderSyntaxToken> all, HashSet<string> differentiated, Action<ShaderSyntaxToken, string> report)
    {
        if (function.Body is not { } body || !all.Any(t => t.Text is "bwd_diff" or "__bwd_diff"))
        {
            return;
        }

        ShaderSyntaxToken[] tokens = [.. all.Where(t => ReferenceEquals(t.Source, body.OpenBrace.Source)
                                                         && t.Span.Start > body.OpenBrace.Span.Start && t.Span.Start < body.CloseBrace.Span.Start)];
        bool isDifferentiated = differentiated.Contains(function.Name)
                                && function.Attributes.Any(a => a.Name is "Differentiable" or "ForwardDifferentiable" or "BackwardDifferentiable");

        // let x = diffPair(…); の x。
        HashSet<string> immutablePairs = new(StringComparer.Ordinal);

        for (int i = 0; i + 3 < tokens.Length; i++)
        {
            if (tokens[i].Text == "let" && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 2].Kind == ShaderSyntaxKind.EqualsToken
                && tokens[i + 3].Text is "diffPair" or "DifferentialPair")
            {
                immutablePairs.Add(tokens[i + 1].Text);
            }
        }

        for (int i = 0; i + 4 < tokens.Length; i++)
        {
            if (tokens[i].Text is not ("bwd_diff" or "__bwd_diff") || tokens[i + 1].Kind != ShaderSyntaxKind.OpenParenToken
                || tokens[i + 2].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 3].Kind != ShaderSyntaxKind.CloseParenToken
                || tokens[i + 4].Kind != ShaderSyntaxKind.OpenParenToken)
            {
                continue;
            }

            if (isDifferentiated)
            {
                report(tokens[i], $"微分する関数 '{function.Name}' の中では、後ろ向きの微分 ({tokens[i].Text}) を呼べません。後ろ向きの微分の結果は、さらに微分できません。");
                continue;
            }

            // 実引数のうち、深さ 0 の名前だけのもの。
            int depth = 0;

            for (int j = i + 5; j < tokens.Length; j++)
            {
                depth += tokens[j].Kind switch { ShaderSyntaxKind.OpenParenToken => 1, ShaderSyntaxKind.CloseParenToken => -1, _ => 0 };

                if (depth < 0)
                {
                    break;
                }

                if (depth == 0 && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && immutablePairs.Contains(tokens[j].Text)
                    && tokens[j - 1].Kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CommaToken
                    && tokens[j + 1].Kind is ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.CloseParenToken)
                {
                    report(tokens[i], $"後ろ向きの微分の実引数 '{tokens[j].Text}' は let の値なので、微分を書き込めません。var で宣言してください。");
                    break;
                }
            }
        }
    }

    private static bool IsStatic(FunctionDeclarationSyntax function) => function.ModifierTokens.Any(t => t.Text == "static");

    /// <summary>
    /// 公開する関数の <c>[ForwardDerivative(g)]</c>・<c>[BackwardDerivative(g)]</c> の g が、__exported でなく取り込んだモジュールの関数 (E31162)。
    /// </summary>
    /// <remarks>
    /// 公開するとは、module の宣言のあるファイルでは public を書いた関数 (入れ物の型も public)、module の宣言の無いファイルでは internal・private を書いていない関数。
    /// g の宣言がどれも、根のモジュールから見えるが根のモジュールを取り込んだモジュールからは見えないファイルにある場合に報告する。
    /// </remarks>
    private static void CheckNonExportedDerivatives(ShaderCompilation compilation, AnalyzedProgram program, FunctionDeclarationSyntax[] all, Action<ShaderSyntaxToken, string> report)
    {
        if (compilation.Text.FilePath is not { } rootPath
            || !all.Any(f => f.Attributes.Any(a => a.Name is "ForwardDerivative" or "BackwardDerivative")))
        {
            return;
        }

        string root = Path.GetFullPath(rootPath);
        HashSet<string> hidden = SlangImportScopeAnalyzer.NotReexportedFiles(root, program);

        if (hidden.Count == 0)
        {
            return;
        }

        bool InFile(ShaderSyntaxToken token, Func<string, bool> predicate) => token.Source.FilePath is { } path && predicate(Path.GetFullPath(path));

        // 根のファイルの module の宣言 (文の先頭の module)。
        ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
        bool hasModule = tokens.Where((t, i) => t.Text == "module" && (i == 0 || tokens[i - 1].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken)
                                                && InFile(t, p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase))).Any();

        bool IsPublic(FunctionDeclarationSyntax function)
        {
            if (!hasModule)
            {
                return !function.ModifierTokens.Any(t => t.Text is "internal" or "private");
            }

            if (!function.ModifierTokens.Any(t => t.Text == "public"))
            {
                return false;
            }

            for (Shaderlyn.Core.Syntax.SyntaxNode? parent = function.Parent; parent is not null and not CompilationUnitSyntax; parent = parent.Parent)
            {
                if (parent is StructDeclarationSyntax type && !type.Modifiers.Any(t => t.Text == "public"))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (FunctionDeclarationSyntax function in all)
        {
            if (!InFile(function.NameToken, p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase)) || !IsPublic(function))
            {
                continue;
            }

            foreach (AttributeSyntax attribute in function.Attributes)
            {
                if (attribute.Name is not ("ForwardDerivative" or "BackwardDerivative")
                    || attribute.ArgumentTokens is not [{ Kind: ShaderSyntaxKind.IdentifierToken } target])
                {
                    continue;
                }

                FunctionDeclarationSyntax[] declarations = [.. all.Where(f => f.Name == target.Text && f.QualifierTokens.IsEmpty)];

                if (declarations.Length > 0 && declarations.All(d => InFile(d.NameToken, hidden.Contains)))
                {
                    report(attribute.NameToken,
                        $"'{target.Text}' は __exported import でなく取り込んだモジュールの関数なので、公開する関数 '{function.Name}' の微分にできません。"
                        + "そのモジュールを __exported import で取り込むか、微分の関数をこのモジュールで定義してください。");
                }
            }
        }
    }

    /// <summary>
    /// 微分の属性を書いたインターフェイスの要件を、弱い微分の属性のメソッドで満たしている箇所 (SG0359。E38110・E38105)。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>IDifferentiable</c> の <c>dzero</c>・<c>dadd</c> は後ろ向きに微分する要件なので、自分で書く場合は <c>[Differentiable]</c> が必要 (<c>[ForwardDifferentiable]</c> では足りない)</item>
    /// <item>利用者のインターフェイスの要件に <c>[ForwardDifferentiable]</c>・<c>[Differentiable]</c>・<c>[BackwardDifferentiable]</c> を書いた場合は、それと同じか強い属性が必要</item>
    /// </list>
    /// <c>[TreatAsDifferentiable]</c> はどちらにも当たる。型の宣言の継承の並びに書いたインターフェイス (継承したインターフェイスを含む) だけを見る (extension の適合は見ない)。
    /// 同じ名前の要件が 2 つ以上あるか、型に同じ名前のメソッドが 2 つ以上あれば見ない。
    /// </remarks>
    private static void CheckDifferentiableRequirements(ShaderCompilation compilation, AnalyzedProgram program, SyntaxTreeAnalysisContext context)
    {
        ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
        Dictionary<ShaderSyntaxToken, int> index = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < tokens.Length; i++)
        {
            index[tokens[i]] = i;
        }

        StructDeclarationSyntax[] types = [.. program.Tree.Root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>().Where(t => t.NameToken is not null)];

        // 型の継承の並びの名前 (名前の後ろの : から { まで、<…> の外の名前)。
        List<string> BasesOf(StructDeclarationSyntax type)
        {
            List<string> names = [];

            if (!index.TryGetValue(type.NameToken!, out int start) || !index.TryGetValue(type.OpenBrace, out int open))
            {
                return names;
            }

            int angle = 0;
            bool afterColon = false;

            for (int i = start + 1; i < open && tokens[i].Text != "where"; i++)
            {
                angle += tokens[i].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, ShaderSyntaxKind.GreaterThanGreaterThanToken => -2, _ => 0 };
                afterColon |= angle == 0 && tokens[i].Kind == ShaderSyntaxKind.ColonToken;

                if (afterColon && angle == 0 && tokens[i].Kind == ShaderSyntaxKind.IdentifierToken
                    && tokens[i - 1].Kind is ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.CommaToken)
                {
                    names.Add(tokens[i].Text);
                }
            }

            return names;
        }

        // 利用者のインターフェイス (1 度だけ宣言したもの) の、微分の属性を書いた要件の名前と強さ。
        Dictionary<string, StructDeclarationSyntax?> interfaces = new(StringComparer.Ordinal);

        foreach (StructDeclarationSyntax face in types.Where(t => t.Keyword.Text == "interface"))
        {
            interfaces[face.Name] = interfaces.ContainsKey(face.Name) ? null : face;
        }

        // インターフェイスと、その継承をたどった要件 (名前 → 強さ。同じ名前が 2 つ以上なら 0)。IDifferentiable は dzero・dadd を後ろ向きに微分する。
        Dictionary<string, int> Requirements(string name, int depth)
        {
            Dictionary<string, int> result = new(StringComparer.Ordinal);

            if (depth > 8)
            {
                return result;
            }

            if (name == "IDifferentiable" && !interfaces.ContainsKey(name))
            {
                result["dzero"] = 2;
                result["dadd"] = 2;
                return result;
            }

            if (!interfaces.TryGetValue(name, out StructDeclarationSyntax? face) || face is null)
            {
                return result;
            }

            foreach (FunctionDeclarationSyntax requirement in face.Members.OfType<FunctionDeclarationSyntax>().Where(m => m.Body is null))
            {
                result[requirement.Name] = result.ContainsKey(requirement.Name) ? 0 : DifferentiabilityLevel(requirement) is var level and < 3 ? level : 0;
            }

            foreach (string inherited in BasesOf(face))
            {
                foreach ((string key, int level) in Requirements(inherited, depth + 1))
                {
                    result[key] = result.ContainsKey(key) ? 0 : level;
                }
            }

            return result;
        }

        foreach (StructDeclarationSyntax type in types.Where(t => t.Keyword.Text is "struct" or "class"))
        {
            Dictionary<string, int> required = new(StringComparer.Ordinal);

            foreach (string face in BasesOf(type))
            {
                foreach ((string key, int level) in Requirements(face, 0))
                {
                    required[key] = required.ContainsKey(key) ? 0 : level;
                }
            }

            foreach (IGrouping<string, FunctionDeclarationSyntax> methods in type.Members.OfType<FunctionDeclarationSyntax>().GroupBy(m => m.Name, StringComparer.Ordinal))
            {
                if (methods.Count() != 1 || !required.TryGetValue(methods.Key, out int level) || level == 0)
                {
                    continue;
                }

                FunctionDeclarationSyntax method = methods.First();

                // 自分で書いた微分は、前向き ([ForwardDerivative]) で前向きの、前向きと後ろ向きの両方で後ろ向きの要件を満たす
                // (後ろ向きだけでは前向きの要件が残る)。[PrimalSubstituteOf] で置き換えた関数と [PrimalSubstitute] の関数は見ない。
                bool PointsHere(string attribute) => type.Members.OfType<FunctionDeclarationSyntax>()
                    .Any(m => m.Attributes.Any(a => a.Name == attribute && a.ArgumentTokens.Any(t => t.Text == method.Name)));

                bool forwardCustom = method.Attributes.Any(a => a.Name == "ForwardDerivative") || PointsHere("ForwardDerivativeOf");
                bool backwardCustom = method.Attributes.Any(a => a.Name == "BackwardDerivative") || PointsHere("BackwardDerivativeOf");
                int custom = forwardCustom ? backwardCustom ? 2 : 1 : 0;
                bool substituted = method.Attributes.Any(a => a.Name == "PrimalSubstitute") || PointsHere("PrimalSubstituteOf");

                if (!substituted && Math.Max(DifferentiabilityLevel(method), custom) < level &&compilation.IsReportable(method.NameToken) && method.NameToken.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.InterfaceRequirement, location,
                        $"'{method.Name}' は{(level == 2 ? "後ろ向き" : "前向き")}に微分するインターフェイスの要件なので、{(level == 2 ? "[Differentiable] を" : "[ForwardDifferentiable] 以上の微分の属性を")}書いてください。"));
                }
            }
        }
    }

    /// <summary>関数の微分の属性の強さ。0 は微分しない、1 は前向き、2 は後ろ向き ([Differentiable] を含む)、3 は [TreatAsDifferentiable]。</summary>
    private static int DifferentiabilityLevel(FunctionDeclarationSyntax function)
        => function.Attributes.Any(a => a.Name == "TreatAsDifferentiable") ? 3
           : function.Attributes.Any(a => a.Name is "Differentiable" or "BackwardDifferentiable") ? 2
           : function.Attributes.Any(a => a.Name == "ForwardDifferentiable") ? 1
           : 0;

    /// <summary>微分の関数の仮引数の、合わないところ。合うか、決められなければ <see langword="null"/>。</summary>
    private static string? CheckSignature(FunctionDeclarationSyntax primal, FunctionDeclarationSyntax derivative, bool backward)
    {
        if (!primal.GenericParameterTokens.IsDefaultOrEmpty || !derivative.GenericParameterTokens.IsDefaultOrEmpty
            || primal.ParameterList.Any(p => p.IsArray || p.ModifierTokens.Any(t => t.Text is "out" or "inout" or "no_diff" or "ref") || p.Type.ModifierTokens.Any(t => t.Text is "out" or "inout" or "no_diff"))
            || derivative.ParameterList.Any(p => p.IsArray))
        {
            return null;
        }

        List<(string Type, bool InOut)> expected = [];

        foreach (ParameterSyntax parameter in primal.ParameterList)
        {
            string type = TypeText(parameter.Type);

            if (DifferentiableType().IsMatch(type))
            {
                expected.Add(($"DifferentialPair<{type}>", backward));
            }
            else if (PlainType().IsMatch(type))
            {
                expected.Add((type, false));
            }
            else
            {
                return null;
            }
        }

        string returned = TypeText(primal.ReturnType);
        bool differentiableReturn = DifferentiableType().IsMatch(returned);

        if (!differentiableReturn && returned != "void" && !PlainType().IsMatch(returned))
        {
            return null;
        }

        if (backward && differentiableReturn)
        {
            expected.Add((returned, false));
        }

        ParameterSyntax[] actual = [.. derivative.ParameterList];

        if (actual.Length != expected.Count)
        {
            return $"仮引数は {expected.Count} 個 ({string.Join(", ", expected.Select(e => (e.InOut ? "inout " : string.Empty) + e.Type))}) ですが、{actual.Length} 個あります。";
        }

        for (int i = 0; i < actual.Length; i++)
        {
            string type = TypeText(actual[i].Type);
            bool inout = actual[i].ModifierTokens.Any(t => t.Text == "inout") || actual[i].Type.ModifierTokens.Any(t => t.Text == "inout");
            bool lastDifferential = backward && differentiableReturn && i == actual.Length - 1;
            bool typeMatches = type == expected[i].Type || (lastDifferential && type == expected[i].Type + ".Differential")
                               || type.Replace("DiffPair<", "DifferentialPair<", StringComparison.Ordinal) == expected[i].Type;

            // 型の名前が組み込みの型でも DifferentialPair でもなければ (別名など)、決めない。
            if (!typeMatches && !DifferentiableType().IsMatch(type) && !PlainType().IsMatch(type) && !type.StartsWith("DifferentialPair<", StringComparison.Ordinal)
                && !type.EndsWith(".Differential", StringComparison.Ordinal))
            {
                return null;
            }

            if (!typeMatches || (expected[i].InOut && !inout))
            {
                return $"{i + 1} 番目の仮引数は {(expected[i].InOut ? "inout " : string.Empty)}{expected[i].Type}{(lastDifferential ? $" (か {expected[i].Type}.Differential)" : string.Empty)} ですが、{(inout ? "inout " : string.Empty)}{type} です。";
            }
        }

        return null;
    }

    /// <summary>型の綴り (修飾子を除く)。</summary>
    private static string TypeText(TypeSyntax type)
        => string.Concat(type.DescendantTokens().Where(t => !type.ModifierTokens.Contains(t)).Select(t => t.Text));

    /// <summary>関数の本体の no_diff を見る。</summary>
    /// <remarks>構文木は no_diff を残さないので、本体の範囲のトークンを読む。</remarks>
    private static void CheckNoDiff(FunctionDeclarationSyntax function, ImmutableArray<ShaderSyntaxToken> all, bool isDerivative, Action<ShaderSyntaxToken, string> report)
    {
        if (function.Body is not { } body || !all.Any(t => t.Text == "no_diff"))
        {
            return;
        }

        ShaderSyntaxToken[] tokens = [.. all.Where(t => ReferenceEquals(t.Source, body.OpenBrace.Source)
                                                         && t.Span.Start > body.OpenBrace.Span.Start && t.Span.Start < body.CloseBrace.Span.Start)];
        // 微分する関数・微分の関数 (属性で微分を書いたもの、ほかの関数の微分の属性で指したもの)・微分するとみなす関数。
        bool differentiable = isDerivative
                              || function.Attributes.Any(a => a.Name.Contains("Differentiable", StringComparison.Ordinal) || a.Name.Contains("Derivative", StringComparison.Ordinal)
                                                              || a.Name.Contains("PrimalSubstitute", StringComparison.Ordinal) || a.Name == "PreferRecompute")
                              || function.ModifierTokens.Any(t => t.Text is "__differentiable");

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text != "no_diff" || tokens[i].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            // no_diff float2 pos = …; (微分しない局所変数の宣言) は見ない。
            if (tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && i + 2 < tokens.Length && tokens[i + 2].Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.LessThanToken)
            {
                continue;
            }

            // no_diff x; (名前だけ。x.y のように . でつないだものも)
            int j = i + 1;

            while (j + 1 < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && tokens[j + 1].Kind == ShaderSyntaxKind.DotToken)
            {
                j += 2;
            }

            if (tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && j + 1 < tokens.Length
                && tokens[j + 1].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.CloseParenToken
                    or ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken
                && i > 0 && (tokens[i - 1].Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CommaToken || tokens[i - 1].Text == "return"))
            {
                report(tokens[i], "no_diff は、関数の呼び出しにだけ書けます (no_diff f(x))。");
                continue;
            }

            if (!differentiable)
            {
                report(tokens[i], $"微分しない関数 '{function.Name}' の中では、no_diff は使用できません。");
            }
        }
    }
}
