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
/// メンバー参照が成り立っているかを検査する (HL0313 / HL0312)。
/// </summary>
/// <remarks>
/// <para>
/// 2 つの検査は同じ場所を歩く。
/// 「そのメンバーがあるか」(HL0312) と
/// 「その構成にあるか」(HL0313) は、順に見なければならない。
/// 無いものを条件で語っても仕方がなく、
/// 条件付きで<b>ある</b>ものを「無い」と言ってもいけない。
/// </para>
/// <para>
/// <b>そのシェーダー自身が書いた構造体と、成分の数が分かる数値型だけを見る。</b>
/// 取り込んだヘッダの構造体を指摘しても直しようがなく、
/// テクスチャのメソッド呼び出しは形が違う。
/// </para>
/// </remarks>
internal sealed class MemberAccessAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>位置を表す成分名。</summary>
    private const string PositionComponents = "xyzw";

    /// <summary>色を表す成分名。</summary>
    private const string ColorComponents = "rgba";

    /// <summary>スウィズルで取り出せる最大の成分数。</summary>
    private const int MaxSwizzleLength = 4;

    /// <summary>行列の成分の書き方を示す文言。</summary>
    private const string MatrixFormHint =
        "行列の成分は _m00 形式か _11 形式で書きます。混ぜて書くことはできません。";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        HlslRuleDescriptors.StructMemberNotPresent,
        HlslRuleDescriptors.MemberNotFound,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ConditionMap condition = compilation.GetConditionMap();
        Dictionary<(int Start, string? Kernel), Dictionary<string, StructInfo>> structs = CollectStructs(compilation, condition);

        // 同じコードが Pass ごと・構成ごとに解析されるため、同じ位置を重ねて報告しない。
        ReportedLocations reported = new();

        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is MemberAccessExpressionSyntax access)
            {
                AnalyzeAccess(
                    context,
                    compilation,
                    condition,
                    structs.TryGetValue(program.BlockKey, out Dictionary<string, StructInfo>? own) ? own : [],
                    compilation.GetExpressionTypeBinder(program),
                    access,
                    program.AnalyzedCondition,
                    reported);
            }
        }
    }

    /// <summary>メンバー参照 1 か所を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="structs">このシェーダーが書いた構造体のうち、参照と同じコードブロックのもの。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="access">検査する参照。</param>
    /// <param name="analyzed">
    /// この木を解析した構成の条件 (<see cref="AnalyzedProgram.AnalyzedCondition"/>)。
    /// 既定の木なら「常に」で、構成ごとの型もそこでだけ求める。
    /// </param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeAccess(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ConditionMap condition,
        Dictionary<string, StructInfo> structs,
        ExpressionTypeBinder binder,
        MemberAccessExpressionSyntax access,
        SymbolCondition analyzed,
        ReportedLocations reported)
    {
        // メンバー名そのものが書かれたものでなければ見ない。
        // UNITY_SETUP_INSTANCE_ID(v) の v.instanceID は、v は書かれたものだが .instanceID はヘッダのマクロの本体である。
        if (!compilation.IsReportable(access) || !compilation.IsReportable(access.NameToken))
        {
            return;
        }

        // 名前の修飾 (Mode::A、名前空間の UnifiedRT::TraceRay)。enum の列挙子だけを見る (HLSL 2021)。
        if (access.DotToken.Kind == HlslSyntaxKind.ColonColonToken)
        {
            if (access.Target is IdentifierExpressionSyntax { Name: var enumName }
                && binder.TryGetEnum(enumName, out EnumInfo? enumeration)
                && !enumeration.Members.Contains(access.Name)
                && access.GetLocation() is { } enumLocation
                && reported.Add(enumLocation))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    HlslRuleDescriptors.MemberNotFound,
                    enumLocation,
                    $"enum '{enumName}'",
                    $"列挙子 '{access.Name}' ",
                    DescribeCandidates(enumeration.Members, "列挙子")));
            }

            return;
        }

        // メソッド呼び出しは形が違う。テクスチャの Sample などがこれに当たる。構造体のメソッドだけを見る。
        if (IsMethodCall(access))
        {
            AnalyzeMethodCall(context, structs, binder, access, reported);
            return;
        }

        // 対象の型が分からないなら報告しない。
        // 分からないものを根拠に「メンバーが無い」と言ってはならない。
        // 構成によって型が変わる名前なら、構成ごとに型を求めて検査する。
        if (binder.GetEvaluatorFor(access).Evaluate(access.Target) is not { } typeName)
        {
            if (analyzed.IsAlways)
            {
                AnalyzeSwizzleByAssumption(context, condition, binder, access, reported);
            }

            return;
        }

        if (structs.TryGetValue(typeName, out StructInfo structure))
        {
            AnalyzeStructMember(context, condition, structs, typeName, structure, access, analyzed, reported);
            return;
        }

        AnalyzeSwizzle(context, typeName, access, reported);
    }

    /// <summary>構造体のメンバー参照を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="structs">同じコードブロックの構造体。継承した基底を引く。</param>
    /// <param name="typeName">構造体の名前。</param>
    /// <param name="structure">構造体の情報。</param>
    /// <param name="access">検査する参照。</param>
    /// <param name="analyzed">この木を解析した構成の条件。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeStructMember(
        SyntaxTreeAnalysisContext context,
        ConditionMap condition,
        Dictionary<string, StructInfo> structs,
        string typeName,
        StructInfo structure,
        MemberAccessExpressionSyntax access,
        SymbolCondition analyzed,
        ReportedLocations reported)
    {
        if (access.GetLocation() is not { } location)
        {
            return;
        }

        // まず「そもそもあるか」。無いものを条件で語っても仕方がない。
        // 継承した基底 (struct D : B) のメンバーもある。基底を引けなければ「無い」とは言わない。
        switch (FindMember(structs, structure, access.Name, depth: 0))
        {
            case MemberLookup.Missing:
                if (reported.Add(location))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        HlslRuleDescriptors.MemberNotFound,
                        location,
                        $"構造体 '{typeName}'",
                        $"メンバー '{access.Name}' ",
                        DescribeCandidates(AllMembers(structs, structure), "メンバー")));
                }

                return;

            // 基底のメンバーの条件は見ない。
            case MemberLookup.Inherited or MemberLookup.Unknown:
                return;

            default:
                break;
        }

        // 次に「その構成にあるか」。条件が付いていないメンバーはどの構成にもある。
        if (!structure.Conditions.TryGetValue(access.Name, out SymbolCondition declared))
        {
            return;
        }

        // バリアントの木では、条件が付いていないことは「どの構成でも」を意味しない。
        // そのシンボルを有効にした構成の中での話なので、掛け合わせてから比べる。
        SymbolCondition use = condition.GetCondition(access).And(analyzed);

        if (use.IsUnknown || declared.IsUnknown)
        {
            return;
        }

        // 参照する条件が成り立ち、かつメンバーがある条件が成り立たない構成があるか。
        SymbolCondition missing = use.And(declared.Negate());

        if (missing.IsUnknown || !condition.IsPossible(missing) || !reported.Add(location))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.StructMemberNotPresent,
            location,
            typeName,
            access.Name,
            missing.IsAlways ? "どの構成でも" : $"構成：{missing} のとき"));
    }

    /// <summary>構造体のメソッドの呼び出し (HLSL 2021 の <c>s.Get()</c>) で、そのメソッドがあるかを検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="structs">このシェーダーが書いた構造体のうち、参照と同じコードブロックのもの。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="access">呼び出しの対象の参照。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// フィールドと同じ名前なら報告しない (フィールドを呼ぶ誤りは扱わない)。
    /// 基底を引けない構造体では「無い」とは言わない。
    /// </remarks>
    private static void AnalyzeMethodCall(
        SyntaxTreeAnalysisContext context,
        Dictionary<string, StructInfo> structs,
        ExpressionTypeBinder binder,
        MemberAccessExpressionSyntax access,
        ReportedLocations reported)
    {
        if (access.DotToken.Kind != HlslSyntaxKind.DotToken
            || binder.GetEvaluatorFor(access).Evaluate(access.Target) is not { } typeName
            || !structs.TryGetValue(typeName, out StructInfo structure)
            || FindMember(structs, structure, access.Name, depth: 0, includeMethods: true) != MemberLookup.Missing
            || access.GetLocation() is not { } location
            || !reported.Add(location))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.MemberNotFound,
            location,
            $"構造体 '{typeName}'",
            $"メンバー '{access.Name}' ",
            DescribeCandidates(AllMembers(structs, structure, includeMethods: true), "メンバー")));
    }

    /// <summary>
    /// 型が構成によって変わる名前について、構成ごとに型を求めて成分の取り出しを検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="access">検査する参照。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <code>
    /// #ifdef _A
    ///     float3 color = 1;
    /// #else
    ///     float4 color = 1;
    /// #endif
    /// #ifdef _B
    ///     color.w = 0.5;    // _A と _B のとき color は float3 なので .w は無い
    /// #endif
    /// </code>
    /// 構成ごとの型は、使う位置の条件と宣言の条件を掛け合わせて求める
    /// (<see cref="ExpressionTypeBinder.EnumerateAssumptions(HlslSyntaxNode, HlslExpressionSyntax, ConditionMap)"/>)。
    /// 1 つの構成で誤りなら、その構成を添えて報告する。
    /// </remarks>
    private static void AnalyzeSwizzleByAssumption(
        SyntaxTreeAnalysisContext context,
        ConditionMap condition,
        ExpressionTypeBinder binder,
        MemberAccessExpressionSyntax access,
        ReportedLocations reported)
    {
        foreach (SymbolCondition assumption in binder.EnumerateAssumptions(access, access.Target, condition))
        {
            if (binder.GetEvaluatorFor(access, assumption, condition).Evaluate(access.Target) is not { } typeName)
            {
                continue;
            }

            string note = assumption.IsAlways
                ? string.Empty
                : $"構成：{assumption} のとき、この式は {typeName} です。";

            if (AnalyzeSwizzle(context, typeName, access, reported, note))
            {
                return;
            }
        }
    }

    /// <summary>数値型のスウィズルを検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="typeName">対象の型名。</param>
    /// <param name="access">検査する参照。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <param name="note">理由の前に添える文。構成ごとに型を求めた場合に、その構成を示す。</param>
    /// <returns>報告したら <see langword="true"/>。</returns>
    /// <remarks>
    /// 行列には <c>_m00</c> 形式 (0 始まり) と <c>_11</c> 形式 (1 始まり) があり、
    /// 規則が違う。混ぜて書くこともできない。
    /// </remarks>
    private static bool AnalyzeSwizzle(
        SyntaxTreeAnalysisContext context,
        string typeName,
        MemberAccessExpressionSyntax access,
        ReportedLocations reported,
        string note = "")
    {
        if (!HlslTypeClassifier.TryDescribeNumeric(typeName, out HlslNumericShape shape))
        {
            return false;
        }

        string? error = shape.Kind == HlslNumericKind.Matrix
            ? DescribeMatrixSwizzleError(access.Name, shape)
            : DescribeSwizzleError(access.Name, shape.Columns);

        if (error is not { } reason
            || access.GetLocation() is not { } location
            || !reported.Add(location))
        {
            return false;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.MemberNotFound, location, typeName, $"成分 {access.Name} ", note + reason));
        return true;
    }

    /// <summary>
    /// 行列の成分の取り出しとして成り立たない理由を返す。
    /// </summary>
    /// <param name="name">取り出そうとしている成分名。</param>
    /// <param name="shape">元の行列の形。</param>
    /// <returns>成り立たない理由。成り立つなら <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 行列には 2 通りの書き方がある。
    /// <c>_m00</c> 形式は 0 始まり、<c>_11</c> 形式は 1 始まりで、
    /// <b>混ぜて書くことはできない</b>。
    /// </para>
    /// <para>
    /// 行列にはこれ以外のメンバーが無いので、
    /// どちらの形にも当てはまらない名前も誤りである。
    /// </para>
    /// </remarks>
    private static string? DescribeMatrixSwizzleError(string name, HlslNumericShape shape)
    {
        bool zeroBased = name.StartsWith("_m", StringComparison.Ordinal);
        int stride = zeroBased ? 4 : 3;

        if (name.Length == 0 || name[0] != '_' || name.Length % stride != 0)
        {
            return MatrixFormHint;
        }

        int taken = name.Length / stride;

        if (taken > MaxSwizzleLength)
        {
            return $"一度に取り出せるのは {MaxSwizzleLength} 成分までです。";
        }

        for (int i = 0; i < taken; i++)
        {
            ReadOnlySpan<char> part = name.AsSpan(i * stride, stride);

            if (part[0] != '_'
                || (zeroBased && part[1] != 'm')
                || !TryReadIndex(part[stride - 2], zeroBased, out int row)
                || !TryReadIndex(part[stride - 1], zeroBased, out int column))
            {
                return MatrixFormHint;
            }

            if (row >= shape.Rows || column >= shape.Columns)
            {
                return $"この行列は {shape.Rows} 行 {shape.Columns} 列です。";
            }
        }

        return null;
    }

    /// <summary>行列の成分の添字を読む。</summary>
    /// <param name="digit">読む文字。</param>
    /// <param name="zeroBased">0 始まりの書き方かどうか。</param>
    /// <param name="index">読み取った添字。0 始まりに揃える。</param>
    /// <returns>読み取れたなら <see langword="true"/>。</returns>
    private static bool TryReadIndex(char digit, bool zeroBased, out int index)
    {
        index = 0;

        if (digit is < '0' or > '9')
        {
            return false;
        }

        int value = digit - '0';

        // 1 始まりの書き方に 0 は無い。それ以外は範囲の判定に任せる。
        // 「形が違う」より「この行列は 3 行 3 列です」のほうが直しやすい。
        if (!zeroBased && value == 0)
        {
            return false;
        }

        index = zeroBased ? value : value - 1;
        return true;
    }

    /// <summary>
    /// スウィズルとして成り立たない理由を返す。
    /// </summary>
    /// <param name="name">取り出そうとしている成分名。</param>
    /// <param name="components">元の型の成分数。</param>
    /// <returns>成り立たない理由。成り立つなら <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>スカラーとベクトルに、スウィズル以外のメンバーは無い。</b>
    /// だからここでは「成り立たないものはすべて誤り」と言い切れる。
    /// 規則の違う行列は、呼び出し元で除いてある。
    /// </remarks>
    private static string? DescribeSwizzleError(string name, int components)
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Length > MaxSwizzleLength)
        {
            return $"一度に取り出せるのは {MaxSwizzleLength} 成分までです。";
        }

        bool position = name.All(c => PositionComponents.Contains(c, StringComparison.Ordinal));
        bool color = name.All(c => ColorComponents.Contains(c, StringComparison.Ordinal));

        if (!position && !color)
        {
            // 両方の並びの文字だけでできているなら、混ぜたということである。
            return name.All(c =>
                PositionComponents.Contains(c, StringComparison.Ordinal)
                || ColorComponents.Contains(c, StringComparison.Ordinal))
                ? $"位置 ({string.Join(", ", PositionComponents.Select(c => c.ToString()))}) と "
                  + $"色 ({string.Join(", ", ColorComponents.Select(c => c.ToString()))}) は混ぜられません。"
                : DescribeComponents(PositionComponents, components);
        }

        string set = position ? PositionComponents : ColorComponents;

        foreach (char component in name)
        {
            if (set.IndexOf(component, StringComparison.Ordinal) >= components)
            {
                return DescribeComponents(set, components);
            }
        }

        return null;
    }

    /// <summary>その型で使える成分名を並べた文字列を返す。</summary>
    /// <param name="set">成分名の並び。</param>
    /// <param name="components">成分数。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeComponents(string set, int components)
        => $"使える成分は {string.Join(", ", set[..components].Select(c => c.ToString()))} です。";

    /// <summary>構造体にあるメンバーを並べた文字列を返す。</summary>
    /// <param name="members">メンバーの名前。</param>
    /// <returns>画面に出す文字列。</returns>
    /// <remarks>
    /// スペルミスのほとんどは、正しい名前を並べれば自分で気づける。
    /// 多すぎる場合は並べない。読み手の助けにならない。
    /// </remarks>
    /// <param name="kind">何を並べるか (<c>メンバー</c> / <c>列挙子</c>)。</param>
    private static string DescribeCandidates(IReadOnlySet<string> members, string kind)
        => members.Count is 0 or > 12
            ? string.Empty
            : $"存在する{kind}は {string.Join(", ", members.Order(StringComparer.Ordinal))} です。";

    /// <summary>継承の深さの上限。循環した継承 (誤ったコード) で止まらなくならないようにする。</summary>
    private const int MaxInheritanceDepth = 8;

    /// <summary>メンバーを探した結果。</summary>
    private enum MemberLookup
    {
        /// <summary>その構造体自身にある。</summary>
        Own,

        /// <summary>継承した基底にある。</summary>
        Inherited,

        /// <summary>どこにも無い。基底もすべて引けた。</summary>
        Missing,

        /// <summary>基底を引けないので分からない。</summary>
        Unknown,
    }

    /// <summary>構造体と、その基底からメンバーを探す。</summary>
    /// <param name="structs">同じコードブロックの構造体。</param>
    /// <param name="structure">探し始める構造体。</param>
    /// <param name="name">メンバーの名前。</param>
    /// <param name="depth">継承をたどった深さ。</param>
    /// <param name="includeMethods">メソッドも探すかどうか。</param>
    /// <returns>探した結果。</returns>
    private static MemberLookup FindMember(
        Dictionary<string, StructInfo> structs,
        StructInfo structure,
        string name,
        int depth,
        bool includeMethods = false)
    {
        if (structure.Members.Contains(name) || (includeMethods && structure.Methods.Contains(name)))
        {
            return depth == 0 ? MemberLookup.Own : MemberLookup.Inherited;
        }

        foreach (string baseName in structure.Bases)
        {
            if (depth >= MaxInheritanceDepth || !structs.TryGetValue(baseName, out StructInfo baseInfo))
            {
                return MemberLookup.Unknown;
            }

            MemberLookup found = FindMember(structs, baseInfo, name, depth + 1, includeMethods);

            if (found != MemberLookup.Missing)
            {
                return found == MemberLookup.Unknown ? found : MemberLookup.Inherited;
            }
        }

        return MemberLookup.Missing;
    }

    /// <summary>構造体と、その基底のメンバーをすべて集める。</summary>
    /// <param name="structs">同じコードブロックの構造体。</param>
    /// <param name="structure">集め始める構造体。</param>
    /// <param name="includeMethods">メソッドも集めるかどうか。</param>
    /// <returns>メンバーの名前。</returns>
    private static HashSet<string> AllMembers(Dictionary<string, StructInfo> structs, StructInfo structure, bool includeMethods = false)
    {
        HashSet<string> members = new(structure.Members, StringComparer.Ordinal);

        if (includeMethods)
        {
            members.UnionWith(structure.Methods);
        }

        Queue<(StructInfo Info, int Depth)> pending = new([(structure, 0)]);

        while (pending.TryDequeue(out (StructInfo Info, int Depth) current))
        {
            foreach (string baseName in current.Info.Bases)
            {
                if (current.Depth < MaxInheritanceDepth && structs.TryGetValue(baseName, out StructInfo baseInfo))
                {
                    members.UnionWith(baseInfo.Members);

                    if (includeMethods)
                    {
                        members.UnionWith(baseInfo.Methods);
                    }

                    pending.Enqueue((baseInfo, current.Depth + 1));
                }
            }
        }

        return members;
    }

    /// <summary>メソッド呼び出しの対象かどうかを判定する。</summary>
    /// <param name="access">対象の参照。</param>
    /// <returns>メソッド呼び出しであれば <see langword="true"/>。</returns>
    private static bool IsMethodCall(MemberAccessExpressionSyntax access)
        => access.Parent is InvocationExpressionSyntax invocation
           && ReferenceEquals(invocation.Target, access);

    /// <summary>構造体 1 つ分の情報。</summary>
    /// <param name="Members">すべてのメンバーの名前。</param>
    /// <param name="Conditions">条件が付いたメンバーの、宣言がある条件。</param>
    /// <param name="Bases">継承した基底の名前。</param>
    /// <param name="Methods">メソッドの名前。</param>
    private readonly record struct StructInfo(
        IReadOnlySet<string> Members,
        Dictionary<string, SymbolCondition> Conditions,
        HashSet<string> Bases,
        HashSet<string> Methods);

    /// <summary>
    /// このシェーダーが書いた構造体を集める。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <returns>コードブロックごとの、構造体の名前と、その情報。</returns>
    /// <remarks>
    /// <para>
    /// <b>コードブロックごとに分けて集める (<see cref="AnalyzedProgram.BlockKey"/>)。</b>
    /// 別の Pass に同じ名前の構造体があるのは普通である。
    /// 名前だけでまとめると、片方ではメンバーが <c>#if</c> の中、もう片方では常にある、という食い違いを
    /// 1 つの構造体の条件として取り違える (URP の Sprite-Lit-Default.shader の <c>Varyings</c>)。
    /// </para>
    /// <para>
    /// <b>条件が付いたメンバーだけを条件の表に入れる。</b>
    /// 木のほとんどのメンバーは無条件であり、
    /// それを入れると表が膨らむだけで判定は 1 つも増えない。
    /// </para>
    /// </remarks>
    private static Dictionary<(int Start, string? Kernel), Dictionary<string, StructInfo>> CollectStructs(
        ShaderCompilation compilation,
        ConditionMap condition)
    {
        Dictionary<(int Start, string? Kernel), Dictionary<string, StructInfo>> blocks = [];

        foreach (AnalyzedProgram program in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            if (!blocks.TryGetValue(program.BlockKey, out Dictionary<string, StructInfo>? structs))
            {
                structs = new Dictionary<string, StructInfo>(StringComparer.Ordinal);
                blocks[program.BlockKey] = structs;
            }

            // template の構造体 (HLSL 2021) も、メンバーがあるかどうかは型引数によらない。
            IEnumerable<StructDeclarationSyntax> declared = program.Structs.Concat(
                ExpressionTypeBinder.TemplateStructsOf(program.Tree.Root.Declarations).Select(t => t.Structure));

            foreach (StructDeclarationSyntax structure in declared)
            {
                if (structure.Name.Length == 0 || !compilation.IsReportable(structure))
                {
                    continue;
                }

                if (!structs.TryGetValue(structure.Name, out StructInfo info))
                {
                    info = new StructInfo(
                        new HashSet<string>(StringComparer.Ordinal),
                        new Dictionary<string, SymbolCondition>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal));

                    structs[structure.Name] = info;
                }

                Absorb(info, structure, condition);
            }
        }

        return blocks;
    }

    /// <summary>構造体 1 つ分のメンバーを表へ足す。</summary>
    /// <param name="info">書き足す先。</param>
    /// <param name="structure">対象の構造体。</param>
    /// <param name="condition">出現条件の索引。</param>
    private static void Absorb(
        StructInfo info,
        StructDeclarationSyntax structure,
        ConditionMap condition)
    {
        if (structure.BaseTypeName is { } baseName)
        {
            info.Bases.Add(baseName);
        }

        // メソッド (template のメソッドを含む)。演算子は名前で呼ばないので含めない。
        foreach (HlslDeclarationSyntax member in structure.Members)
        {
            if (member is FunctionDeclarationSyntax { IsOperator: false } method)
            {
                info.Methods.Add(method.Name);
            }
            else if (member is TemplateDeclarationSyntax { Declaration: FunctionDeclarationSyntax { IsOperator: false } generic })
            {
                info.Methods.Add(generic.Name);
            }
        }

        foreach (VariableDeclarationSyntax field in structure.Fields)
        {
            SymbolCondition declared = condition.GetCondition(field);

            foreach (VariableDeclaratorSyntax variable in field.Variables)
            {
                ((HashSet<string>)info.Members).Add(variable.Name);

                if (declared.IsAlways)
                {
                    // 無条件で現れたなら、条件は付いていない。
                    info.Conditions.Remove(variable.Name);
                    continue;
                }

                // 条件が付いた宣言が複数あるなら、その和が宣言のある条件である。
                info.Conditions[variable.Name] =
                    info.Conditions.TryGetValue(variable.Name, out SymbolCondition existing)
                        ? existing.Or(declared)
                        : declared;
            }
        }
    }
}
