using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;

namespace Shaderlyn.Rules;

/// <summary>
/// 同じ名前を 2 回宣言している箇所を報告する (HL0314)。
/// </summary>
/// <remarks>
/// <para>
/// <b>出現条件があって初めて書ける検査である。</b>
/// <c>#ifdef</c> の両方の分岐を 1 本の木に並べると、同じ名前の宣言が 2 つ現れる。
/// 数えるだけでは、普通の書き方を誤りと言うことになる。
/// 2 つの出現条件を掛け合わせ、**同時に成り立つ構成があるときだけ**報告する。
/// </para>
/// <code>
/// #ifdef A
///     float4 _Color;   // A
/// #else
///     float4 _Color;   // !A   → A ∧ !A は成り立たない。報告しない
/// #endif
/// </code>
/// <para>
/// <b>関数は形まで見る。</b>
/// HLSL は多重定義を許すので、仮引数の型が違えば誤りではない。
/// 宣言だけを先に書く形 (プロトタイプ) も正しい。
/// 同じ形の実装が 2 つあるときだけ報告する。
/// </para>
/// <para>
/// <b>関数の中は、波括弧 1 つ分だけを 1 つの範囲として見る。</b>
/// 同じ波括弧の直下に並ぶ宣言どうしだけを突き合わせる。
/// 内側の波括弧で同じ名前を宣言するのは覆い隠し (shadowing) であり、誤りではない。
/// 名前の範囲を外側へたどらないので、覆い隠しを二重宣言と取り違えることがない。
/// </para>
/// <code>
/// float a;
/// #ifdef _D
///     float4 a;   // 同じ波括弧の直下。_D の構成では 2 つある → 報告する
/// #endif
/// { float a; }    // 内側の波括弧。覆い隠しであって二重宣言ではない → 報告しない
/// </code>
/// <para>
/// 範囲の単位は名前の解決と同じである。
/// <c>switch</c> の本体は 1 つの範囲で、別の <c>case</c> の同じ名前は二重宣言になる (X3003)。
/// 仮引数は関数のいちばん外の波括弧と同じ範囲にある (X3036)。
/// </para>
/// <para>
/// 波括弧を書かない <c>if (x) float a;</c> は、それ自体が 1 つの範囲になるため集めない。
/// </para>
/// <para>
/// <b><c>for</c> の初期化の変数は、囲む波括弧の宣言として数える。</b>
/// Unity (fxc) では囲む波括弧へ漏れ、後で同じ名前を宣言すると X3003 になる。
/// ただし初期化の変数のほうが後なら誤りではない。前の宣言より優先する旨の警告 (X3078) で済む。
/// </para>
/// </remarks>
internal sealed class RedeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>構造体の宣言の形。関数の形 (仮引数の型の並び) や変数の形 (空) とは重ならない。</summary>
    private const string StructShape = "<struct>";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.Redeclaration];

    /// <summary>1 つの宣言。</summary>
    /// <param name="Name">宣言している名前。</param>
    /// <param name="Location">報告に使う位置。</param>
    /// <param name="Condition">その宣言が存在する条件。</param>
    /// <param name="Shape">
    /// 同じ名前でも別のものとして扱う形。関数なら仮引数の型の並び。
    /// 変数なら空。
    /// </param>
    /// <param name="IsLoopVariable"><c>for</c> の初期化で宣言した変数か。</param>
    private readonly record struct Declared(
        string Name,
        Location Location,
        SymbolCondition Condition,
        string Shape,
        bool IsLoopVariable = false);

    /// <summary>シンボルを有効にして展開した木 (バリアント) で見つかった宣言。</summary>
    /// <param name="EnabledSymbols">有効にしたシンボル。</param>
    /// <param name="DeclarationStarts">その木にある宣言の名前の位置。</param>
    private readonly record struct AnalyzedConfiguration(
        ImmutableArray<string> EnabledSymbols,
        HashSet<(string File, int Start)> DeclarationStarts);

    /// <summary>宣言の位置を、ファイルとファイルの中の位置の組で表す。</summary>
    /// <param name="location">宣言の位置。</param>
    /// <returns>位置の組。</returns>
    /// <remarks>
    /// <b>位置だけではファイルを見分けられない。</b>
    /// 取り込んだヘッダの関数の開き波括弧が、別のファイルの関数と同じ位置にあると、
    /// 両方の局所変数を同じ範囲のものとして突き合わせてしまう。
    /// Unreal Engine のシェーダーのように、報告するヘッダが多い環境で起きていた
    /// (SobolRandom.ush の <c>Result</c> を、別のファイルの関数の <c>Result</c> と重複していると報告した)。
    /// </remarks>
    private static (string File, int Start) KeyOf(Location location) => (location.FilePath, location.Span.Start);

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ConditionMap condition = compilation.GetConditionMap();

        // Pass ごとに別のコードとして組み立てられ、さらに波括弧ごとに別の範囲になる。
        // 別の Pass に同じ名前の関数があるのも、内側の波括弧で同じ名前を宣言するのも、二重宣言ではない。
        // 範囲はトップレベルを 0、関数の中は開き波括弧の位置で表す。
        // 範囲の位置は、ファイルと位置の組で持つ (KeyOf)。トップレベルはファイルを問わず 1 つの範囲である。
        Dictionary<(int Block, string ScopeFile, int Scope), Dictionary<string, List<Declared>>> byScope = [];

        // 同じ宣言が既定の木とバリアントの木の両方に現れる。位置で 1 つに数える。
        HashSet<(int Block, string File, int Start)> seen = [];

        // 報告の「先に書かれた」を決めるための、ファイルが現れた順。
        Dictionary<string, int> fileOrder = new(StringComparer.Ordinal);

        // バリアントの木は、有効にしたシンボルを固定した構成である (Collides)。
        Dictionary<int, List<AnalyzedConfiguration>> configurationsByBlock = [];

        // コードブロックを見分ける番号。
        // コンピュートシェーダーはカーネルごとに別のコードとして組み立てられ、どれも同じ位置から始まる。
        // 位置だけで見分けると、別のカーネルの宣言どうしを同じ範囲のものとして突き合わせてしまう。
        Dictionary<(int Start, string? Kernel), int> blocks = [];

        foreach (AnalyzedProgram program in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            if (!blocks.TryGetValue(program.BlockKey, out int block))
            {
                block = blocks[program.BlockKey] = blocks.Count;
            }

            AnalyzedConfiguration? configuration = null;

            if (!program.EnabledSymbols.IsDefaultOrEmpty)
            {
                configuration = new AnalyzedConfiguration(program.EnabledSymbols, []);

                if (!configurationsByBlock.TryGetValue(block, out List<AnalyzedConfiguration>? configurations))
                {
                    configurations = [];
                    configurationsByBlock[block] = configurations;
                }

                configurations.Add(configuration.Value);
            }

            foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                if (!compilation.IsReportable(declaration))
                {
                    continue;
                }

                Collect(declaration, (string.Empty, 0));

                // 関数の中の範囲は宣言の中で閉じている。前のコードブロックと同じに展開した宣言は、そちらで集めた。
                if (compilation.IsRepeatedDeclaration(declaration))
                {
                    continue;
                }

                // 関数の中は、波括弧ごとに別の範囲として集める。switch の本体も 1 つの範囲である。
                foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
                {
                    (ShaderSyntaxToken open, ImmutableArray<StatementSyntax> statements) = node switch
                    {
                        BlockStatementSyntax body => (body.OpenBrace, body.Statements),
                        SwitchStatementSyntax body => (body.OpenBrace, body.Statements),
                        _ => (default(ShaderSyntaxToken)!, []),
                    };

                    if (statements.IsDefaultOrEmpty)
                    {
                        continue;
                    }

                    // 仮引数は、関数のいちばん外の波括弧と同じ範囲にある。
                    if (node.Parent is FunctionDeclarationSyntax function)
                    {
                        CollectParameters(function, KeyOf(open.GetLocation()));
                    }

                    foreach (StatementSyntax statement in statements)
                    {
                        if (statement is LocalDeclarationStatementSyntax local
                            && compilation.IsReportable(local.Declaration))
                        {
                            Collect(local.Declaration, KeyOf(open.GetLocation()));
                        }

                        // for の初期化の変数は、囲む波括弧へ漏れる。
                        if (statement is ForStatementSyntax { Initializer: VariableDeclarationSyntax initializer }
                            && compilation.IsReportable(initializer))
                        {
                            foreach (Declared declared in Describe(initializer, condition))
                            {
                                Add(declared with { IsLoopVariable = true }, KeyOf(open.GetLocation()));
                            }
                        }
                    }
                }
            }

            void CollectParameters(FunctionDeclarationSyntax function, (string File, int Start) scope)
            {
                foreach (ParameterSyntax parameter in function.ParameterList)
                {
                    if (!parameter.NameToken.IsMissing)
                    {
                        Add(
                            new Declared(parameter.Name, parameter.NameToken.GetLocation(), condition.GetCondition(parameter), string.Empty),
                            scope);
                    }
                }
            }

            void Collect(DeclarationSyntax declaration, (string File, int Start) scope)
            {
                foreach (Declared declared in Describe(declaration, condition))
                {
                    Add(declared, scope);
                }
            }

            void Add(Declared declared, (string File, int Start) scope)
            {
                (string file, int start) = KeyOf(declared.Location);
                configuration?.DeclarationStarts.Add((file, start));

                if (!seen.Add((block, file, start)))
                {
                    return;
                }

                fileOrder.TryAdd(file, fileOrder.Count);

                if (!byScope.TryGetValue((block, scope.File, scope.Start), out Dictionary<string, List<Declared>>? byName))
                {
                    byName = new Dictionary<string, List<Declared>>(StringComparer.Ordinal);
                    byScope[(block, scope.File, scope.Start)] = byName;
                }

                if (!byName.TryGetValue(declared.Name, out List<Declared>? list))
                {
                    list = [];
                    byName[declared.Name] = list;
                }

                list.Add(declared);
            }
        }

        // 同じ宣言が複数の Pass から見える (HLSLINCLUDE) 場合、
        // その組は Pass の数だけ見つかる。位置の組で 1 度だけ報告する。
        HashSet<(string File, int Start)> reported = [];

        foreach (((int block, string _, int _), Dictionary<string, List<Declared>> byName) in byScope)
        {
            List<AnalyzedConfiguration> configurations =
                configurationsByBlock.TryGetValue(block, out List<AnalyzedConfiguration>? found) ? found : [];

            foreach (List<Declared> declarations in byName.Values)
            {
                Report(context, condition, declarations, configurations, fileOrder, reported);
            }
        }
    }

    /// <summary>宣言 1 つを、名前と形に分解する。</summary>
    /// <param name="declaration">対象の宣言。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <returns>分解した宣言。対象外なら空。</returns>
    private static IEnumerable<Declared> Describe(
        DeclarationSyntax declaration,
        ConditionMap condition)
    {
        switch (declaration)
        {
            // 実装を持たない宣言 (プロトタイプ) は、実装と並んでいても誤りではない。
            case FunctionDeclarationSyntax function when function.IsDefinition
                                                         && !function.NameToken.IsMissing:
                yield return new Declared(
                    function.Name,
                    function.NameToken.GetLocation(),
                    condition.GetCondition(function),
                    DescribeShape(function));
                break;

            case VariableDeclarationSyntax variable:
                foreach (VariableDeclaratorSyntax declarator in variable.Variables)
                {
                    yield return new Declared(
                        declarator.Name,
                        declarator.NameToken.GetLocation(),
                        condition.GetCondition(variable),
                        string.Empty);
                }

                break;

            // 構造体の名前は、変数や関数の名前とはぶつからない。C++ と同じく、同じ名前の変数は型の名前を隠すだけである
            // (ConstantBuffer<Constants> Constants。DXC はこれを受け付け、D3D12 のサンプルもこの書き方をする)。
            // 形を分けて、構造体どうしだけを比べる。
            case StructDeclarationSyntax structure
                when structure.NameToken is { IsMissing: false } structureName:
                yield return new Declared(
                    structure.Name,
                    structureName.GetLocation(),
                    condition.GetCondition(structure),
                    StructShape);
                break;

            default:
                break;
        }
    }

    /// <summary>関数を、多重定義として区別する形で表す。</summary>
    /// <param name="function">対象の関数。</param>
    /// <returns>仮引数の型を並べた文字列。</returns>
    /// <remarks>
    /// 型引数 (<c>TLaneVector2D&lt;half, 3, …&gt;</c> と <c>&lt;half, 1, …&gt;</c>) と配列の次元 (<c>Complex a[N]</c> と <c>Complex a[2][N]</c>) が
    /// 違えば別の多重定義である。型の名前だけで比べると、Unreal Engine の TSR や FFT のシェーダーを重複と報告していた。
    /// </remarks>
    private static string DescribeShape(FunctionDeclarationSyntax function)
        => string.Join(",", function.ParameterList.Select(p => p.Type.OverloadShape + new string('#', p.ArrayRankTokens.Count(t => t.Kind == ShaderSyntaxKind.OpenBracketToken))));

    /// <summary>同じ名前の宣言をつき合わせて報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="declarations">同じ名前の宣言。</param>
    /// <param name="configurations">このブロックを構成を決めて展開した木。</param>
    /// <param name="fileOrder">ファイルが現れた順。別のファイルの宣言は、先に現れたファイルのものを先とする。</param>
    /// <param name="reported">既に報告した位置。</param>
    /// <remarks>
    /// 3 つ以上あるときは、先に書かれたものとの組を 1 つだけ報告する。
    /// 先の宣言が別のファイルにあれば、行番号にファイル名を添える。
    /// 同じ誤りを組み合わせの数だけ並べても読みにくくなるだけである。
    /// </remarks>
    private static void Report(
        SyntaxTreeAnalysisContext context,
        ConditionMap condition,
        List<Declared> declarations,
        List<AnalyzedConfiguration> configurations,
        Dictionary<string, int> fileOrder,
        HashSet<(string File, int Start)> reported)
    {
        if (declarations.Count < 2)
        {
            return;
        }

        declarations.Sort((left, right) =>
        {
            int byFile = fileOrder[left.Location.FilePath].CompareTo(fileOrder[right.Location.FilePath]);
            return byFile != 0 ? byFile : left.Location.Span.Start.CompareTo(right.Location.Span.Start);
        });

        for (int i = 1; i < declarations.Count; i++)
        {
            Declared later = declarations[i];

            for (int j = 0; j < i; j++)
            {
                Declared earlier = declarations[j];

                if (!Collides(condition, earlier, later, configurations))
                {
                    continue;
                }

                if (!reported.Add(KeyOf(later.Location)))
                {
                    break;
                }

                int line = earlier.Location.LineSpan.Start.Line + 1;

                context.ReportDiagnostic(Diagnostic.Create(
                    HlslRuleDescriptors.Redeclaration,
                    later.Location,
                    later.Name,
                    string.Equals(earlier.Location.FilePath, later.Location.FilePath, StringComparison.Ordinal)
                        ? line.ToString(CultureInfo.InvariantCulture)
                        : $"{Path.GetFileName(earlier.Location.FilePath)} の {line}"));

                break;
            }
        }
    }

    /// <summary>2 つの宣言が同時に存在しうるかを判定する。</summary>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="earlier">先に書かれた宣言。</param>
    /// <param name="later">後に書かれた宣言。</param>
    /// <param name="configurations">このブロックを構成を決めて展開した木。</param>
    /// <returns>同時に存在しうるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>条件が分からないときは報告しない。</b>
    /// 突き合わせられなかった箇所では、同時に存在するかを言えない。
    /// </para>
    /// <para>
    /// <b>両方の条件を満たす構成を展開していれば、その木で決める。</b>
    /// バリアントにだけある宣言の条件は「有効にしたシンボルがすべて定義されている」であり、
    /// 有効にしなかったシンボルが無いことまでは含まない。
    /// </para>
    /// <code>
    /// #ifdef _A               // 並べられない
    ///     #ifdef _B
    ///         Texture2DArray _Src;  // _A と _B の構成で見つかる → 条件は A ∧ B
    ///     #else
    ///         Texture2D _Src;       // _A の構成で見つかる     → 条件は A (本当は A ∧ !B)
    ///     #endif
    /// #endif
    /// </code>
    /// <para>
    /// 条件だけを掛け合わせると <c>A ∧ B</c> が成り立ち、正しい書き方を誤りと言うことになる。
    /// <c>_A</c> と <c>_B</c> の構成の木には後者が無いので、同時には存在しないと分かる。
    /// 満たす構成を 1 つも展開していなければ、条件だけで判断する。
    /// </para>
    /// </remarks>
    private static bool Collides(
        ConditionMap condition,
        Declared earlier,
        Declared later,
        List<AnalyzedConfiguration> configurations)
    {
        // 後の宣言が for の初期化なら、前の宣言より優先されるだけで誤りではない (X3078)。
        if (later.IsLoopVariable
            || !string.Equals(earlier.Shape, later.Shape, StringComparison.Ordinal))
        {
            return false;
        }

        SymbolCondition both = earlier.Condition.And(later.Condition);

        if (both.IsUnknown || !condition.IsPossible(both))
        {
            return false;
        }

        bool expanded = false;

        foreach (AnalyzedConfiguration configuration in configurations)
        {
            if (!IsSatisfiedBy(both, configuration.EnabledSymbols))
            {
                continue;
            }

            if (configuration.DeclarationStarts.Contains(KeyOf(earlier.Location))
                && configuration.DeclarationStarts.Contains(KeyOf(later.Location)))
            {
                return true;
            }

            expanded = true;
        }

        return !expanded;
    }

    /// <summary>その構成で条件が成り立つかを判定する。</summary>
    /// <param name="condition">対象の条件。</param>
    /// <param name="enabledSymbols">有効にしたシンボル。ほかのシンボルは無いものとする。</param>
    /// <returns>成り立つなら <see langword="true"/>。</returns>
    private static bool IsSatisfiedBy(SymbolCondition condition, ImmutableArray<string> enabledSymbols)
    {
        SymbolCondition assignment = SymbolCondition.Always;

        foreach (string symbol in condition.EnumerateSymbols())
        {
            assignment = assignment.And(SymbolCondition.Symbol(symbol, enabledSymbols.Contains(symbol)));
        }

        return !condition.And(assignment).IsNever;
    }
}
