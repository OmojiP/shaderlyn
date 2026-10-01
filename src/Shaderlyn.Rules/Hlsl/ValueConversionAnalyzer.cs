using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// 値が受け取る側の型に収まっているかを検査する (HL0350 / HL0351)。
/// </summary>
/// <remarks>
/// <para>
/// 代入・初期化・<c>return</c> のいずれも「値を受け取る側へ渡す」点で同じであり、
/// 判定も引数の受け渡し (HL0340) と同じ規則で書ける。
/// </para>
/// <para>
/// <b>1 本の木で型が決まる場合、ここでは出現条件を見ない。</b>
/// 代入も <c>return</c> も、その場に書かれている型どうしの話であり、
/// 構成によって変わるのは<b>どの宣言が見えているか</b>だけである。
/// それは型の評価器が既に解いている。
/// </para>
/// <para>
/// 型が決まらなかったときだけ、構成ごとに求め直す (HL0353)。
/// 同じ名前を条件ごとに違う型で宣言していると 1 本の木では型が決まらず、
/// そのままでは成分が落ちる構成があっても何も言えない。
/// </para>
/// </remarks>
internal sealed class ValueConversionAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>値を返さない関数の戻り値の型名。</summary>
    private const string VoidTypeName = "void";

    /// <summary>
    /// 構造体の入れ子をたどる深さの上限。
    /// </summary>
    /// <remarks>
    /// 自分自身を含む構造体は書けないが、解析の途中では循環しうる。
    /// 打ち切っても「数えない」に倒れるだけで、誤検出にはならない。
    /// </remarks>
    private const int MaxStructDepth = 8;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        HlslRuleDescriptors.ValueNotConvertible,
        HlslRuleDescriptors.ReturnNotConvertible,
        HlslRuleDescriptors.MissingReturn,
        HlslRuleDescriptors.InitializerCountMismatch,
        HlslRuleDescriptors.ConditionalTruncation,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 同じコードが Pass ごと・構成ごとに解析されるため、同じ位置を重ねて報告しない。
        HashSet<int> reported = [];
        ConditionMap conditions = compilation.GetConditionMap();

        // 構造体の波括弧による初期化は、メンバーを平らにして数える。
        // 構成をまたいで集めると、別の構成のメンバー数で数えることになる。
        // 初期化を書いた木と同じ木の宣言だけを見る。
        Dictionary<AnalyzedProgram, Dictionary<string, StructDeclarationSyntax>> structsByProgram = [];

        foreach ((HlslDeclarationSyntax declaration, AnalyzedProgram program) in compilation.EnumerateRuleDeclarations())
        {
            if (declaration is not FunctionDeclarationSyntax { Body: not null } function)
            {
                continue;
            }

            if (!structsByProgram.TryGetValue(program, out Dictionary<string, StructDeclarationSyntax>? structs))
            {
                structs = structsByProgram[program] = CollectStructs(compilation, program);
            }

            AnalyzeReturnPaths(context, compilation, program, function, reported);
            AnalyzeFunction(
                context, compilation, conditions, compilation.GetExpressionTypeBinder(program), function, structs, reported);
        }
    }

    /// <summary>関数 1 つ分を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="function">検査する関数。</param>
    /// <param name="structs">このシェーダーが書いた構造体。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeFunction(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        FunctionDeclarationSyntax function,
        IReadOnlyDictionary<string, StructDeclarationSyntax> structs,
        HashSet<int> reported)
    {
        foreach (SyntaxNode node in function.DescendantNodesAndSelf())
        {
            if (!compilation.IsReportable((HlslSyntaxNode)node))
            {
                continue;
            }

            switch (node)
            {
                case VariableDeclarationSyntax declaration:
                    AnalyzeInitializers(context, compilation, conditions, binder, declaration, structs, reported);
                    break;

                case AssignmentExpressionSyntax assignment:
                    AnalyzeAssignment(context, conditions, binder, assignment, reported);
                    break;

                case ReturnStatementSyntax statement:
                    AnalyzeReturn(context, conditions, binder, function, statement, reported);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// 値を返さずに終わる経路が無いかを検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">関数がある木。</param>
    /// <param name="function">検査する関数。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <para>
    /// 判定は <see cref="ControlFlow"/> にある。
    /// 知らない形の文は「抜ける」に倒すので、見落とすことはあっても誤って指摘はしない。
    /// </para>
    /// <para>
    /// <b>構成ごとに判定する。</b> 両方の分岐を並べた木には、同時には存在しない文が並んでいる。
    /// <c>#ifdef _B return 1; #endif</c> だけの関数を並べた木のまま見ると、<c>!_B</c> の構成でも返すように見える。
    /// 本体の文の出現条件から構成を列挙し (<see cref="ShaderCompilation.TryEnumerateConfigurations"/>)、
    /// 構成ごとに、その構成に存在する文だけで判定する。列挙できなければ、並べた木のまま判定する。
    /// </para>
    /// </remarks>
    private static void AnalyzeReturnPaths(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        FunctionDeclarationSyntax function,
        HashSet<int> reported)
    {
        string returnType = function.ReturnType.Name;

        if (string.Equals(returnType, VoidTypeName, StringComparison.Ordinal)
            || function.Body is not { } body
            || function.NameToken.GetLocation() is not { } location
            || reported.Contains(location.Span.Start))
        {
            return;
        }

        if (FindConfigurationsWithoutReturn(compilation, program, function, body) is not { } missing)
        {
            return;
        }

        reported.Add(location.Span.Start);

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.MissingReturn,
            location,
            function.Name,
            returnType,
            missing.IsAlways ? string.Empty : $" ({missing} のとき)"));
    }

    /// <summary>値を返さずに終わる経路がある構成を求める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">関数がある木。</param>
    /// <param name="function">関数。関数自身が存在しない構成は数えない。</param>
    /// <param name="body">関数の本体。</param>
    /// <returns>そういう構成の条件。どの構成でも必ず返すなら <see langword="null"/>。</returns>
    private static SymbolCondition? FindConfigurationsWithoutReturn(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        FunctionDeclarationSyntax function,
        BlockStatementSyntax body)
    {
        // 構成ごとに判定するのは既定の木だけにする。ノードの出現条件は既定の木を基準にしたもので、
        // バリアントの木 (キーワードをまとめて有効にした木を含む) では、その木に実際にある文にも合わない条件が付きうる。
        // バリアントの木は、それ自身を 1 つの構成として判定する。
        if (!program.EnabledSymbols.IsDefaultOrEmpty)
        {
            return ControlFlow.AlwaysExits(body) ? null : SymbolCondition.Always;
        }

        List<HlslSyntaxNode> statements = [.. body.DescendantNodesAndSelf().OfType<HlslStatementSyntax>()];

        // 関数がある構成は、関数の先頭 (戻り値の型) の出現条件で代表させる。
        // 出現条件は #if の領域が内側の指令で切れた単位で付くので、関数全体のように複数の切れ目にまたがるノードには付かない。
        // 関数ごと #ifdef _BASE の中にあっても、関数の宣言そのものは「常に」になり、_BASE の無い構成で本体が空に見える。
        HlslSyntaxNode head = function.ReturnType;

        if (!compilation.TryEnumerateConfigurations([head, .. statements], program, out ImmutableArray<NodeConfiguration> configurations)
            || configurations.Length <= 1)
        {
            // 構成が 1 つしか無いか、列挙できないなら、並べた木のまま判定する。
            return ControlFlow.AlwaysExits(body) ? null : SymbolCondition.Always;
        }

        // 関数自身が #if の中にあれば、関数が存在しない構成は数えない。本体の文が無いのは当たり前である。
        // その木が表さない構成も数えない (ShaderCompilation.GetTreeConfiguration)。
        // 既定の木には、別の構成で形の変わる文が「その構成では無い」という条件付きで並んでいる。
        // その構成の本当の中身は、そのキーワードを有効にしたバリアントの木にある。
        SymbolCondition tree = compilation.GetTreeConfiguration(program);
        configurations = [.. configurations.Where(c => c.Contains(head) && tree.IsSatisfiedBy(c.EnabledSymbols.Contains))];

        if (configurations.IsEmpty)
        {
            return null;
        }

        // 構成を表す条件を作るために、本体の条件に現れるシンボルを集める。
        SortedSet<string> symbols = new(StringComparer.Ordinal);

        foreach (HlslSyntaxNode statement in statements)
        {
            symbols.UnionWith(compilation.GetEffectiveCondition(statement, program).EnumerateSymbols());
        }

        SymbolCondition missing = SymbolCondition.Never;
        int failed = 0;

        foreach (NodeConfiguration configuration in configurations)
        {
            if (ControlFlow.AlwaysExits(body, configuration.Contains))
            {
                continue;
            }

            failed++;
            SymbolCondition assignment = SymbolCondition.Always;

            foreach (string symbol in symbols)
            {
                assignment = assignment.And(SymbolCondition.Symbol(symbol, configuration.EnabledSymbols.Contains(symbol)));
            }

            missing = missing.Or(assignment);
        }

        return failed == 0
            ? null
            : failed == configurations.Length ? SymbolCondition.Always : compilation.GetConditionMap().Simplify(missing);
    }

    /// <summary>初期化子を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="declaration">検査する宣言。</param>
    /// <param name="structs">このシェーダーが書いた構造体。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeInitializers(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        VariableDeclarationSyntax declaration,
        IReadOnlyDictionary<string, StructDeclarationSyntax> structs,
        HashSet<int> reported)
    {
        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
        {
            if (variable.Initializer is not { } initializer)
            {
                continue;
            }

            string target = $"{declaration.Type.Name} の '{variable.Name}'";

            // 波括弧による初期化は、型ではなく要素の個数で見る。
            if (initializer is InitializerListExpressionSyntax list)
            {
                CheckInitializerList(
                    context, compilation, binder, list, declaration, variable, structs, target, reported);
                continue;
            }

            // 配列に単一の値を与える形は、規則が違う。踏み込まない。
            if (variable.IsArray)
            {
                continue;
            }

            Check(context, conditions, binder, initializer, declaration.Type.Name, target, reported);
        }
    }

    /// <summary>
    /// 波括弧による初期化の要素の個数を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="list">検査する初期化子リスト。</param>
    /// <param name="declaration">対象の宣言。</param>
    /// <param name="variable">対象の宣言子。</param>
    /// <param name="structs">このシェーダーが書いた構造体。</param>
    /// <param name="target">報告に出す代入先の呼び名。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// HLSL の波括弧による初期化は、平らにした成分の個数がちょうど一致していなければならない。
    /// 入れ子の波括弧もベクトルも、成分に展開してから数える。
    /// </remarks>
    private static void CheckInitializerList(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ExpressionTypeBinder binder,
        InitializerListExpressionSyntax list,
        VariableDeclarationSyntax declaration,
        VariableDeclaratorSyntax variable,
        IReadOnlyDictionary<string, StructDeclarationSyntax> structs,
        string target,
        HashSet<int> reported)
    {
        // 出現条件ごとに突き合わせる。
        // #ifdef で増えるメンバーは、同じ #ifdef で増える要素と釣り合っていればよい。
        // 平らに合計すると、条件の付いた分が片側にだけ並んでいるときに食い違って見える。
        ConditionMap conditions = compilation.GetConditionMap();

        if (CountRequiredComponents(conditions, declaration, variable, structs) is not { } required
            || CountWrittenComponents(conditions, binder, list) is not { } written
            || list.GetLocation() is not { } location)
        {
            return;
        }

        // 片側にしか無い条件があるときは判断しない。
        // 代入先のメンバーと書いた要素で、同じ #ifdef の扱いが分かれることがある
        // (一方は 1 本の木に並び、もう一方は構成ごとの展開へ回る)。
        // その状態では、釣り合っていないのか、別の木に並んでいるだけなのかを決められない。
        if (!required.Keys.ToHashSet().SetEquals(written.Keys))
        {
            return;
        }

        foreach ((SymbolCondition condition, int need) in required)
        {
            int wrote = written[condition];

            if (need == wrote || !reported.Add(location.Span.Start))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                HlslRuleDescriptors.InitializerCountMismatch, location, target, wrote, need));
            return;
        }
    }

    /// <summary>
    /// 代入先が必要とする成分の個数を求める。
    /// </summary>
    /// <param name="conditions">ノードの出現条件。</param>
    /// <param name="declaration">対象の宣言。</param>
    /// <param name="variable">対象の宣言子。</param>
    /// <param name="structs">このシェーダーが書いた構造体。</param>
    /// <returns>出現条件ごとの必要な個数。求められない場合は <see langword="null"/>。</returns>
    private static Dictionary<SymbolCondition, int>? CountRequiredComponents(
        ConditionMap conditions,
        VariableDeclarationSyntax declaration,
        VariableDeclaratorSyntax variable,
        IReadOnlyDictionary<string, StructDeclarationSyntax> structs)
    {
        if (CountElementComponents(conditions, declaration.Type.Name, structs) is not { } perElement)
        {
            return null;
        }

        if (!variable.IsArray)
        {
            return perElement;
        }

        if (!variable.TryGetArrayLength(out int length))
        {
            return null;
        }

        return perElement.ToDictionary(pair => pair.Key, pair => pair.Value * length);
    }

    /// <summary>
    /// 要素 1 つ分の成分の個数を求める。
    /// </summary>
    /// <param name="conditions">ノードの出現条件。</param>
    /// <param name="typeName">要素の型名。</param>
    /// <param name="structs">このシェーダーが書いた構造体。</param>
    /// <returns>出現条件ごとの個数。求められない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>構造体はメンバーを平らにして数える。</b>
    /// HLSL の波括弧による初期化は、構造体でも成分の個数がちょうど一致していなければならない。
    /// 入れ子の構造体もたどる。たどれない型が 1 つでもあれば数えない。
    /// </remarks>
    private static Dictionary<SymbolCondition, int>? CountElementComponents(
        ConditionMap conditions,
        string typeName,
        IReadOnlyDictionary<string, StructDeclarationSyntax> structs)
        => CountElementComponents(conditions, typeName, structs, depth: 0);

    /// <summary>構造体の入れ子をたどりながら成分を数える。</summary>
    /// <param name="conditions">ノードの出現条件。</param>
    /// <param name="typeName">要素の型名。</param>
    /// <param name="structs">このシェーダーが書いた構造体。</param>
    /// <param name="depth">たどった深さ。</param>
    /// <returns>出現条件ごとの個数。求められない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 自分自身を含む構造体は書けないが、解析の途中では循環しうる。深さで打ち切る。
    /// </para>
    /// <para>
    /// <b>条件ごとに分けて数える。</b>
    /// <c>#ifdef</c> で増えるメンバーは、同じ条件で増える初期化の要素と釣り合っていればよい。
    /// 合計して比べると、条件付きの分が片側にしか並んでいないときに食い違って見える。
    /// </para>
    /// </remarks>
    private static Dictionary<SymbolCondition, int>? CountElementComponents(
        ConditionMap conditions,
        string typeName,
        IReadOnlyDictionary<string, StructDeclarationSyntax> structs,
        int depth)
    {
        Dictionary<SymbolCondition, int> counts = [];

        if (HlslTypeClassifier.TryDescribeNumeric(typeName, out HlslNumericShape shape))
        {
            counts[SymbolCondition.Always] = shape.Rows * shape.Columns;
            return counts;
        }

        if (depth >= MaxStructDepth || !structs.TryGetValue(typeName, out StructDeclarationSyntax? structure))
        {
            return null;
        }

        foreach (VariableDeclarationSyntax field in structure.Fields)
        {
            SymbolCondition condition = conditions.GetCondition(field);

            // 条件を追えなかったメンバーがあれば、この構造体は数えない。
            if (condition.IsUnknown)
            {
                return null;
            }

            if (CountElementComponents(conditions, field.Type.Name, structs, depth + 1) is not { } perMember)
            {
                return null;
            }

            foreach (VariableDeclaratorSyntax member in field.Variables)
            {
                int count = member.IsArray ? (member.TryGetArrayLength(out int length) ? length : 0) : 1;

                if (count == 0)
                {
                    return null;
                }

                foreach ((SymbolCondition inner, int components) in perMember)
                {
                    SymbolCondition combined = condition.And(inner);
                    counts[combined] = counts.GetValueOrDefault(combined) + (components * count);
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// 波括弧の中に書かれた成分の個数を数える。
    /// </summary>
    /// <param name="conditions">ノードの出現条件。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="list">対象の初期化子リスト。</param>
    /// <returns>出現条件ごとの個数。1 つでも型が分からない要素があれば <see langword="null"/>。</returns>
    /// <remarks>代入先のメンバーと同じく、条件ごとに分けて数える。</remarks>
    private static Dictionary<SymbolCondition, int>? CountWrittenComponents(
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        InitializerListExpressionSyntax list)
    {
        Dictionary<SymbolCondition, int> counts = [];

        foreach (HlslNodeOrTokenEntry item in list.Elements)
        {
            if (item.Node is not HlslExpressionSyntax element)
            {
                // 区切りのカンマ。
                continue;
            }

            SymbolCondition condition = conditions.GetCondition(element);

            // 条件を追えなかった要素があれば、この初期化は数えない。
            if (condition.IsUnknown)
            {
                return null;
            }

            if (element is InitializerListExpressionSyntax nested)
            {
                if (CountWrittenComponents(conditions, binder, nested) is not { } inner)
                {
                    return null;
                }

                foreach ((SymbolCondition innerCondition, int components) in inner)
                {
                    SymbolCondition combined = condition.And(innerCondition);
                    counts[combined] = counts.GetValueOrDefault(combined) + components;
                }

                continue;
            }

            if (binder.CountComponents(element) is not { } written)
            {
                return null;
            }

            counts[condition] = counts.GetValueOrDefault(condition) + written;
        }

        return counts;
    }

    /// <summary>代入を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="assignment">検査する代入。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <para>
    /// 複合代入 (<c>+=</c> など) も、右辺を左辺の型へ変換してから演算する。
    /// 判定は単純代入と同じでよい。
    /// </para>
    /// <para>
    /// <b>代入先の型が構成によって変わるなら、構成ごとに両辺の型を求めて判定する。</b>
    /// 以前は代入先の型が決まらないと、そこで検査をやめていた。
    /// <c>#if _A</c> で <c>float a</c>、<c>#elif _B</c> で <c>float3 a</c> と宣言した名前へ
    /// <c>a = float4(...)</c> と代入すると、どちらの構成でも成分が落ちるのに何も報告しなかった。
    /// </para>
    /// </remarks>
    private static void AnalyzeAssignment(
        SyntaxTreeAnalysisContext context,
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        AssignmentExpressionSyntax assignment,
        HashSet<int> reported)
    {
        if (binder.GetEvaluatorFor(assignment).Evaluate(assignment.Left) is not { } targetType)
        {
            AnalyzeAssignmentByConfiguration(context, conditions, binder, assignment, reported);
            return;
        }

        Check(context, conditions, binder, assignment.Right, targetType, targetType, reported);
    }

    /// <summary>代入先の型が構成によって変わる代入を、構成ごとに検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="assignment">検査する代入。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <para>
    /// <b>仮定は両辺をまとめて立てる。</b>
    /// 左辺だけで仮定を立てると右辺の型が決まらないことがあり、
    /// 別々に立てると、同時には成り立たない構成どうしの型を比べることになる。
    /// </para>
    /// <para>
    /// 渡せない構成を、値が落ちる構成より先に報告する。
    /// 渡せない構成はコンパイルに失敗するので、警告の陰に隠してはならない。
    /// どの仮定でも両辺の型が決まらなければ、何も報告しない。
    /// </para>
    /// </remarks>
    private static void AnalyzeAssignmentByConfiguration(
        SyntaxTreeAnalysisContext context,
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        AssignmentExpressionSyntax assignment,
        HashSet<int> reported)
    {
        if (assignment.Right.GetLocation() is not { } location)
        {
            return;
        }

        (SymbolCondition Assumption, string Target, string Value)? truncating = null;

        foreach (SymbolCondition assumption in binder.EnumerateAssumptions(
                     assignment, [assignment.Left, assignment.Right], conditions))
        {
            ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(assignment, assumption, conditions);

            if (evaluator.Evaluate(assignment.Left) is not { } targetType
                || evaluator.Evaluate(assignment.Right) is not { } valueType)
            {
                continue;
            }

            if (!HlslConversion.IsConvertible(valueType, targetType))
            {
                if (reported.Add(location.Span.Start))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        HlslRuleDescriptors.ValueNotConvertible,
                        location,
                        targetType,
                        valueType,
                        assumption.IsAlways ? string.Empty : $"{assumption} のとき、代入先は {targetType} です。"));
                }

                return;
            }

            if (truncating is null && HlslConversion.IsTruncating(valueType, targetType))
            {
                truncating = (assumption, targetType, valueType);
            }
        }

        if (truncating is { } finding && reported.Add(location.Span.Start))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                HlslRuleDescriptors.ConditionalTruncation,
                location,
                DescribeAssumption(finding.Assumption),
                DescribeValue(assignment.Right),
                finding.Value,
                $"代入先の {finding.Target}"));
        }
    }

    /// <summary>値が受け取る側に収まっているかを確かめ、収まらなければ報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="value">渡している値。</param>
    /// <param name="targetType">受け取る側の型名。</param>
    /// <param name="target">報告に出す受け取る側の呼び名。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void Check(
        SyntaxTreeAnalysisContext context,
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        HlslExpressionSyntax value,
        string targetType,
        string target,
        HashSet<int> reported)
    {
        if (value.GetLocation() is not { } location)
        {
            return;
        }

        if (binder.GetEvaluatorFor(value).Evaluate(value) is not { } valueType)
        {
            if (FindByConfiguration(conditions, binder, value, targetType) is { } finding
                && reported.Add(location.Span.Start))
            {
                context.ReportDiagnostic(finding.Convertible
                    ? Diagnostic.Create(
                        HlslRuleDescriptors.ConditionalTruncation,
                        location,
                        DescribeAssumption(finding.Assumption),
                        DescribeValue(value),
                        finding.ValueType,
                        target)
                    : Diagnostic.Create(
                        HlslRuleDescriptors.ValueNotConvertible,
                        location,
                        target,
                        finding.ValueType,
                        DescribeNote(finding)));
            }

            return;
        }

        if (HlslConversion.IsConvertible(valueType, targetType) || !reported.Add(location.Span.Start))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.ValueNotConvertible, location, target, valueType, string.Empty));
    }

    /// <summary>構成ごとに型を求めて見つけた食い違い 1 件。</summary>
    /// <param name="Assumption">その食い違いが起きる構成。</param>
    /// <param name="ValueType">その構成での値の型。</param>
    /// <param name="Convertible">
    /// 渡せはするかどうか。<see langword="true"/> なら成分が落ちるだけ (HL0353)、
    /// <see langword="false"/> なら渡せない (HL0350 / HL0351)。
    /// </param>
    private readonly record struct ConfigurationFinding(
        SymbolCondition Assumption,
        string ValueType,
        bool Convertible);

    /// <summary>
    /// 構成ごとに型を求め、受け取る側と噛み合わない構成を探す。
    /// </summary>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="value">渡している値。</param>
    /// <param name="targetType">受け取る側の型名。</param>
    /// <returns>見つかった食い違い。どの構成でも噛み合っていれば <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>渡せない構成を、値が落ちる構成より先に返す。</b>
    /// 渡せない構成はコンパイルに失敗する。
    /// 値が落ちるだけの構成を先に報告すると、より重い誤りが警告の陰に隠れる。
    /// </para>
    /// <para>
    /// 見つけるのは 1 件だけである。
    /// 同じ 1 か所について構成の数だけ並べても、直し方は変わらない。
    /// </para>
    /// </remarks>
    private static ConfigurationFinding? FindByConfiguration(
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        HlslExpressionSyntax value,
        string targetType)
    {
        ConfigurationFinding? truncating = null;

        foreach (SymbolCondition assumption in binder.EnumerateAssumptions(value, value, conditions))
        {
            if (binder.GetEvaluatorFor(value, assumption, conditions).Evaluate(value) is not { } valueType)
            {
                continue;
            }

            if (!HlslConversion.IsConvertible(valueType, targetType))
            {
                return new ConfigurationFinding(assumption, valueType, Convertible: false);
            }

            if (truncating is null && HlslConversion.IsTruncating(valueType, targetType))
            {
                truncating = new ConfigurationFinding(assumption, valueType, Convertible: true);
            }
        }

        return truncating;
    }

    /// <summary>構成を文言にする。</summary>
    /// <param name="assumption">対象の構成。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeAssumption(SymbolCondition assumption)
        => assumption.IsAlways ? "どの構成でも" : $"{assumption} のとき";

    /// <summary>渡している値を文言にする。</summary>
    /// <param name="value">対象の値。</param>
    /// <returns>画面に出す文字列。</returns>
    /// <remarks>
    /// 長い式をそのまま載せると読めなくなる。名前で指せるときだけ名前を出す。
    /// </remarks>
    private static string DescribeValue(HlslExpressionSyntax value)
        => value is IdentifierExpressionSyntax identifier ? $"'{identifier.Name}'" : "この値";

    /// <summary>
    /// 「どの構成でそうなるか」の注記を作る。
    /// </summary>
    /// <param name="finding">対象の食い違い。</param>
    /// <returns>注記。構成を選ぶまでもない場合は空。</returns>
    /// <remarks>
    /// <b>文言を変えてはならない。</b>
    /// 並べる経路と並べない経路の指摘を突き合わせるテストが、
    /// この形の一文を注記として取り除いてから比べている
    /// (<c>BothBranchConsistencyTests.WithoutAssumptionNote</c>)。
    /// 構成ごとに展開した経路では、その構成の型がそのまま決まるので注記が付かない。
    /// </remarks>
    private static string DescribeNote(ConfigurationFinding finding)
        => finding.Assumption.IsAlways
            ? string.Empty
            : $"{finding.Assumption} のとき、この式は {finding.ValueType} です。";

    /// <summary><c>return</c> を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="function">囲んでいる関数。</param>
    /// <param name="statement">検査する <c>return</c>。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeReturn(
        SyntaxTreeAnalysisContext context,
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        FunctionDeclarationSyntax function,
        ReturnStatementSyntax statement,
        HashSet<int> reported)
    {
        string returnType = function.ReturnType.Name;
        bool isVoid = string.Equals(returnType, VoidTypeName, StringComparison.Ordinal);

        if (statement.GetLocation() is not { } location)
        {
            return;
        }

        if (statement.Expression is not { } value)
        {
            // 値を返す関数が、値なしで return している。
            if (!isVoid && reported.Add(location.Span.Start))
            {
                Report(context, function.Name, returnType, "値を返していません", location);
            }

            return;
        }

        if (isVoid)
        {
            if (reported.Add(location.Span.Start))
            {
                Report(context, function.Name, returnType, "値を返しています", location);
            }

            return;
        }

        if (binder.GetEvaluatorFor(value).Evaluate(value) is not { } valueType)
        {
            // 1 本の木で型が決まらないなら、構成ごとに求め直す。
            AnalyzeReturnByConfiguration(
                context, conditions, binder, function, value, returnType, location, reported);
            return;
        }

        if (HlslConversion.IsConvertible(valueType, returnType)
            || !reported.Add(location.Span.Start))
        {
            return;
        }

        Report(context, function.Name, returnType, $"{valueType} を返しています", location);
    }

    /// <summary>
    /// 型が構成によって変わる <c>return</c> を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="function">囲んでいる関数。</param>
    /// <param name="value">返している値。</param>
    /// <param name="returnType">戻り値の型。</param>
    /// <param name="location"><c>return</c> の位置。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <b>渡せない構成は、1 本の木で型が決まる場合と同じ位置に報告する。</b>
    /// 構成ごとに展開した経路では同じ誤りが <c>return</c> の位置に出る。
    /// 位置がずれると、並べる経路と並べない経路の突き合わせで別の指摘に見える
    /// (<c>BothBranchConsistencyTests</c>)。
    /// 成分が落ちるだけの構成 (HL0353) は、
    /// どの構成でも展開した経路には出ないので式の位置を指す。
    /// </remarks>
    private static void AnalyzeReturnByConfiguration(
        SyntaxTreeAnalysisContext context,
        ConditionMap conditions,
        ExpressionTypeBinder binder,
        FunctionDeclarationSyntax function,
        HlslExpressionSyntax value,
        string returnType,
        Location location,
        HashSet<int> reported)
    {
        if (FindByConfiguration(conditions, binder, value, returnType) is not { } finding)
        {
            return;
        }

        if (!finding.Convertible)
        {
            if (reported.Add(location.Span.Start))
            {
                Report(
                    context,
                    function.Name,
                    returnType,
                    $"{finding.ValueType} を返しています",
                    location,
                    DescribeNote(finding));
            }

            return;
        }

        if (value.GetLocation() is { } valueLocation && reported.Add(valueLocation.Span.Start))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                HlslRuleDescriptors.ConditionalTruncation,
                valueLocation,
                DescribeAssumption(finding.Assumption),
                DescribeValue(value),
                finding.ValueType,
                $"戻り値の {returnType}"));
        }
    }

    /// <summary><c>return</c> の食い違いを報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="name">関数の名前。</param>
    /// <param name="returnType">戻り値の型。</param>
    /// <param name="reason">食い違いの中身。</param>
    /// <param name="location">報告位置。</param>
    /// <param name="note">「どの構成でそうなるか」の注記。構成によらない場合は空。</param>
    private static void Report(
        SyntaxTreeAnalysisContext context,
        string name,
        string returnType,
        string reason,
        Location location,
        string note = "")
        => context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.ReturnNotConvertible, location, name, returnType, reason, note));

    /// <summary>
    /// このシェーダーが書いた構造体を、名前で引けるように集める。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象のコードブロック。</param>
    /// <returns>構造体の名前と、その宣言。</returns>
    /// <remarks>
    /// <para>
    /// 取り込んだヘッダの構造体は入れない。
    /// ヘッダの構造体を波括弧で初期化する書き方は、指摘しても直しようがない。
    /// </para>
    /// <para>
    /// <b>1 つの構成の中だけで集める。</b>
    /// 構成をまたいで集めると、メンバーが条件付きの構造体で、
    /// 別の構成のメンバー数を使って数えることになる。
    /// <c>#ifdef</c> で増えるメンバーと、同じ <c>#ifdef</c> で増える初期化の要素は、
    /// どの構成でも釣り合っているのに食い違って見える。
    /// </para>
    /// </remarks>
    private static Dictionary<string, StructDeclarationSyntax> CollectStructs(
        ShaderCompilation compilation,
        AnalyzedProgram program)
    {
        Dictionary<string, StructDeclarationSyntax> structs = new(StringComparer.Ordinal);

        foreach (StructDeclarationSyntax structure in program.Structs)
        {
            if (structure.Name.Length > 0 && compilation.IsReportable(structure))
            {
                structs[structure.Name] = structure;
            }
        }

        return structs;
    }

}
