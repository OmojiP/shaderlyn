using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// 呼び出しが、その構成に存在する宣言と噛み合っているかを検査する
/// (HL0340 / HL0341 / HL0342 / HL0344 / HL0345)。
/// </summary>
/// <remarks>
/// <para>
/// <b>3 つの検査を 1 つのアナライザに置いている。</b>
/// どれも「その構成に存在する宣言はどれか」を決めたあとの話であり、
/// そこが最も手のかかる部分だからである。分けると同じ絞り込みを 3 回書くことになる。
/// </para>
/// <para>
/// <b>候補はその構成に存在する宣言だけに絞る。</b>
/// <c>#ifdef</c> で仮引数が切り替わる関数では、
/// 条件を見ないと別の構成の宣言が候補に混ざり、
/// 合わない呼び出しが合っているように見える。
/// </para>
/// <para>
/// <b>候補が 1 つも無い場合はここでは何も言わない。</b>
/// その構成に定義が無いという話であり、HL0311 の担当である。
/// </para>
/// </remarks>
internal sealed class CallSignatureAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        HlslRuleDescriptors.ArgumentNotConvertible,
        HlslRuleDescriptors.ArgumentCountMismatch,
        HlslRuleDescriptors.ArgumentNotAssignable,
        HlslRuleDescriptors.ArrayArgumentMismatch,
        HlslRuleDescriptors.TemplateArgumentConflict,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ConditionMap condition = compilation.GetConditionMap();

        // 配列は式で作れないので、渡せるのは配列として宣言された名前だけである。
        // 名前はその位置で指している宣言から引く (ExpressionTypeBinder.ResolveName)。
        bool declaresArrays = DeclaresArrays(compilation);

        // 同じコードが Pass ごと・構成ごとに解析されるため、同じ位置を重ねて報告しない。
        ReportedLocations reported = new();

        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is InvocationExpressionSyntax invocation)
            {
                AnalyzeCall(
                    context,
                    compilation,
                    condition,
                    compilation.GetExpressionTypeBinder(program),
                    invocation,
                    declaresArrays,
                    program.AnalyzedCondition,
                    reported);
            }
        }
    }

    /// <summary>呼び出し 1 か所を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="invocation">検査する呼び出し。</param>
    /// <param name="declaresArrays">このシェーダーが長さの書かれた配列を 1 つでも宣言しているか。</param>
    /// <param name="analyzed">この木を解析した構成の条件。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeCall(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ConditionMap condition,
        ExpressionTypeBinder binder,
        InvocationExpressionSyntax invocation,
        bool declaresArrays,
        SymbolCondition analyzed,
        ReportedLocations reported)
    {
        if (!compilation.IsReportable(invocation))
        {
            return;
        }

        // 構造体のメソッド (s.Get()、HLSL 2021)。
        if (invocation.Target is MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access)
        {
            AnalyzeMethodCall(context, compilation, condition, binder, invocation, access, reported);
            return;
        }

        if (invocation.TargetName is not { } name)
        {
            return;
        }

        // this. を省いた囲む構造体のメソッドや、関数の中の変数 (関数オブジェクト) の呼び出し。
        // 大域の同じ名前の関数とは照らさない (HLSL 2021)。
        if (binder.CallsNonGlobalFunction(invocation, name))
        {
            return;
        }

        // template の関数と同じ名前なら、型引数の推論を含めて別に見る (HLSL 2021)。
        if (binder.TryGetTemplateFunctions(name, out ImmutableArray<TemplateFunctionCandidate> templates))
        {
            AnalyzeTemplateCall(context, compilation, condition, binder, invocation, name, templates, reported);
            return;
        }

        if (compilation.GetFunctionDeclarations(name, binder.Program) is not { IsEmpty: false } all)
        {
            AnalyzeIntrinsicCall(context, compilation, invocation, name, reported);
            return;
        }

        // 取り込みを解決できていなければ、見えていない多重定義があるかもしれない (template の関数の呼び出しと同じ)。
        // Unreal Engine の LumenCardComputeShader.usf は、生成される VertexFactory.ush を通して取り込む多重定義
        // GetInstanceSceneData(inout FVisibleCluster, bool) を呼んでおり、見えている多重定義と照らして誤りと言っていた。
        if (compilation.MayLackOverloadsOf(name))
        {
            return;
        }

        // バリアントの木では、条件が付いていないことは「どの構成でも」を意味しない。
        SymbolCondition call = condition.GetCondition(invocation).And(analyzed);

        if (call.IsUnknown)
        {
            return;
        }

        // その構成に存在する宣言だけを候補にする。
        List<FunctionDeclarationSyntax> candidates =
            [.. all.Where(c => CallCompatibility.IsPresentWith(condition, c, call))];

        if (candidates.Count == 0)
        {
            // その構成には定義そのものが無い。HL0311 の担当である。
            return;
        }

        ImmutableArray<HlslExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];

        List<FunctionDeclarationSyntax> fitting =
            [.. candidates.Where(c => CallCompatibility.AcceptsArgumentCount([.. c.ParameterList], arguments.Length))];

        // 組み込み関数と同じ名前で多重定義していれば、組み込み関数の側が受け付けることもある。
        bool intrinsicAccepts = HlslIntrinsicArity.Functions.TryGetValue(name, out IntrinsicArity intrinsic)
                                && intrinsic.Accepts(arguments.Length);

        // 構成ごとに覆えているかを見るのは既定の木だけにする。出現条件は既定の木を基準にしたもので、
        // バリアントの木 (キーワードをまとめて有効にした木を含む) の中では合わないことがある。
        // バリアントの木は、それ自身を 1 つの構成として、合う宣言が 1 つでもあれば通す。
        if (!binder.Program.EnabledSymbols.IsDefaultOrEmpty)
        {
            AnalyzeCallInVariant(context, binder, invocation, name, candidates, arguments, intrinsicAccepts, declaresArrays, call, reported);
            return;
        }

        // 個数の合う宣言が、呼び出しのあるすべての構成に存在するか。
        // 存在しうる宣言のどれか 1 つが合えば通すと、構成 _B でだけ 2 個受け付ける関数に
        // 2 個渡す呼び出しを、!_B でも通してしまう。
        // 宣言が 1 つも無い構成は HL0311 の担当である。宣言のある構成だけで比べる。
        if (!intrinsicAccepts
            && PresentCondition(condition, candidates) is { } anyPresent
            && FindUncovered(condition, call.And(anyPresent), fitting) is { } arityMissing)
        {
            ReportArity(
                context,
                invocation,
                name,
                [.. candidates.Where(c => CallCompatibility.IsPresentWith(condition, c, arityMissing))],
                arguments.Length,
                arityMissing,
                reported);

            return;
        }

        if (fitting.Count == 0)
        {
            return;
        }

        // 実引数の型は候補ごとに変わらない。先に 1 度だけ求める。
        ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(invocation);
        string?[] argumentTypes = [.. arguments.Select(evaluator.Evaluate)];

        List<FunctionDeclarationSyntax> accepting = [];
        List<(FunctionDeclarationSyntax Declaration, Mismatch Mismatch)> rejecting = [];

        foreach (FunctionDeclarationSyntax declaration in fitting)
        {
            if (FindMismatch([.. declaration.ParameterList], arguments, argumentTypes, binder, declaresArrays) is { } mismatch)
            {
                rejecting.Add((declaration, mismatch));
            }
            else
            {
                accepting.Add(declaration);
            }
        }

        // 受け付ける宣言が、呼び出しのあるすべての構成に存在するか。個数と同じ理由で、1 つあれば通すのでは足りない。
        // 個数の合う宣言がある構成だけで比べる。個数の合わない構成は上で報告している。
        if (PresentCondition(condition, fitting) is not { } fittingPresent
            || FindUncovered(condition, call.And(fittingPresent), accepting) is not { } typeMissing)
        {
            return;
        }

        // 報告するのは、受け付ける宣言が無い構成に存在する宣言の食い違いである。
        if (rejecting.FirstOrDefault(r => CallCompatibility.IsPresentWith(condition, r.Declaration, typeMissing))
                is not { Declaration: not null } rejected
            || arguments[rejected.Mismatch.Index].GetLocation() is not { } location
            || !reported.Add(location))
        {
            return;
        }

        Report(context, rejected.Mismatch, location, name, typeMissing);
    }

    /// <summary>バリアントの木の呼び出しを、その木を 1 つの構成として検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="invocation">検査する呼び出し。</param>
    /// <param name="name">関数の名前。</param>
    /// <param name="candidates">その構成に存在しうる宣言。</param>
    /// <param name="arguments">実引数。</param>
    /// <param name="intrinsicAccepts">同じ名前の組み込み関数がその個数を受け付けるか。</param>
    /// <param name="declaresArrays">このシェーダーが長さの書かれた配列を 1 つでも宣言しているか。</param>
    /// <param name="call">呼び出しの出現条件。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeCallInVariant(
        SyntaxTreeAnalysisContext context,
        ExpressionTypeBinder binder,
        InvocationExpressionSyntax invocation,
        string name,
        List<FunctionDeclarationSyntax> candidates,
        ImmutableArray<HlslExpressionSyntax> arguments,
        bool intrinsicAccepts,
        bool declaresArrays,
        SymbolCondition call,
        ReportedLocations reported)
    {
        List<FunctionDeclarationSyntax> fitting =
            [.. candidates.Where(c => CallCompatibility.AcceptsArgumentCount([.. c.ParameterList], arguments.Length))];

        if (fitting.Count == 0)
        {
            if (!intrinsicAccepts)
            {
                ReportArity(context, invocation, name, candidates, arguments.Length, call, reported);
            }

            return;
        }

        ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(invocation);
        string?[] argumentTypes = [.. arguments.Select(evaluator.Evaluate)];
        Mismatch? first = null;

        foreach (FunctionDeclarationSyntax declaration in fitting)
        {
            if (FindMismatch([.. declaration.ParameterList], arguments, argumentTypes, binder, declaresArrays) is not { } mismatch)
            {
                return;
            }

            first ??= mismatch;
        }

        if (first is { } found
            && arguments[found.Index].GetLocation() is { } location
            && reported.Add(location))
        {
            Report(context, found, location, name, call);
        }
    }

    /// <summary>
    /// 呼び出しのある構成のうち、受け付ける宣言がどれも存在しない構成を求める。
    /// </summary>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="call">呼び出しの出現条件。</param>
    /// <param name="accepting">受け付ける宣言。</param>
    /// <returns>そういう構成があればその条件 (読める形に簡約したもの)。無いか、判断できなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 条件の分からない宣言は、どの構成にもあるものとして扱う。分からないものを根拠に報告しない。
    /// </remarks>
    private static SymbolCondition? FindUncovered(
        ConditionMap condition,
        SymbolCondition call,
        List<FunctionDeclarationSyntax> accepting)
    {
        if (PresentCondition(condition, accepting) is not { } covered)
        {
            return null;
        }

        SymbolCondition missing = call.And(covered.Negate());

        return !missing.IsUnknown && condition.IsPossible(missing)
            ? condition.Simplify(missing)
            : null;
    }

    /// <summary>宣言のどれかが存在する条件 (それぞれの条件の和) を求める。</summary>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="declarations">宣言。</param>
    /// <returns>条件。条件の分からない宣言があれば <see langword="null"/>。</returns>
    private static SymbolCondition? PresentCondition(ConditionMap condition, List<FunctionDeclarationSyntax> declarations)
    {
        SymbolCondition present = SymbolCondition.Never;

        foreach (FunctionDeclarationSyntax declaration in declarations)
        {
            SymbolCondition one = condition.GetCondition(declaration);

            if (one.IsUnknown)
            {
                return null;
            }

            present = present.Or(one);
        }

        return present;
    }

    /// <summary>組み込み関数の呼び出しの引数の個数を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="invocation">検査する呼び出し。</param>
    /// <param name="name">関数の名前。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <para>
    /// 受け付ける個数は DirectXShaderCompiler の組み込み関数の表から作った (<see cref="HlslIntrinsicArity"/>)。
    /// 表に無い名前は見ない。
    /// </para>
    /// <para>
    /// <b>同じ名前の関数がどこかで宣言されていれば見ない。</b> ヘッダも含めて、取り込めなかったヘッダがあれば見ない。
    /// そこに同じ名前の関数やマクロがあるかもしれない。
    /// </para>
    /// </remarks>
    private static void AnalyzeIntrinsicCall(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        InvocationExpressionSyntax invocation,
        string name,
        ReportedLocations reported)
    {
        if (compilation.MayLackOverloadsOf(name)
            || !HlslIntrinsicArity.Functions.TryGetValue(name, out IntrinsicArity arity)
            || !compilation.GetFunctionDeclarations(name).IsEmpty
            || compilation.IsFunctionLikeMacro(name)
            || HlslTypeClassifier.Classify(name) != HlslTypeClass.Unknown)
        {
            return;
        }

        int actual = invocation.ArgumentExpressions.Count();

        if (arity.Accepts(actual)
            || invocation.GetLocation() is not { } location
            || !reported.Add(location))
        {
            return;
        }

        string accepted = string.Join(" / ", arity.Counts.Select(c => $"{c} 個"));

        if (arity.VariadicFrom >= 0)
        {
            accepted = $"{arity.VariadicFrom} 個以上";
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.ArgumentCountMismatch,
            location,
            name,
            actual,
            $"必要な引数は {accepted}です",
            string.Empty));
    }

    /// <summary>見つかった食い違いを報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="found">食い違いの中身。</param>
    /// <param name="location">報告位置。</param>
    /// <param name="name">関数の名前。</param>
    /// <param name="call">呼び出しの出現条件。</param>
    private static void Report(
        SyntaxTreeAnalysisContext context,
        Mismatch found,
        Location location,
        string name,
        SymbolCondition call)
    {
        if (found.Kind == MismatchKind.ArrayMismatch)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                HlslRuleDescriptors.ArrayArgumentMismatch,
                location,
                name,
                found.Index + 1,
                found.ParameterType,
                found.ArgumentType));

            return;
        }

        if (found.Kind == MismatchKind.NotAssignable)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                HlslRuleDescriptors.ArgumentNotAssignable,
                location,
                name,
                found.Index + 1,
                found.ParameterType,
                DescribeCondition(call)));

            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.ArgumentNotConvertible,
            location,
            name,
            found.Index + 1,
            found.ArgumentType,
            found.ParameterType,
            DescribeCondition(call)));
    }

    /// <summary>template の関数と同じ名前の関数の呼び出しを検査する (HL0341 / HL0345)。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="invocation">検査する呼び出し。</param>
    /// <param name="name">関数の名前。</param>
    /// <param name="candidates">同じ名前の関数すべて (template でないものを含む)。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <para>
    /// 候補は宣言の出現条件で絞らない。そのため、宣言にも呼び出しにも条件が付いていないときだけ報告する。
    /// 取り込みを解決できていなければ、ほかの多重定義があるかもしれないので報告しない。
    /// </para>
    /// <para>
    /// 型引数の食い違い (HL0345) は、同じ名前の関数が template の関数 1 つだけのときに見る。
    /// 候補が複数あれば、どれが選ばれるかは実引数の型の変換で決まり、その規則は扱わない。
    /// </para>
    /// </remarks>
    private static void AnalyzeTemplateCall(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ConditionMap condition,
        ExpressionTypeBinder binder,
        InvocationExpressionSyntax invocation,
        string name,
        ImmutableArray<TemplateFunctionCandidate> candidates,
        ReportedLocations reported)
    {
        if (compilation.MayLackOverloadsOf(name)
            || !binder.Program.EnabledSymbols.IsDefaultOrEmpty
            || !condition.GetCondition(invocation).IsAlways
            || candidates.Any(c => !condition.GetCondition(c.Declaration).IsAlways))
        {
            return;
        }

        // 型引数を書いた呼び出しは、template の関数だけが候補になる。
        TemplateFunctionCandidate[] pool = invocation.HasTypeArguments ? [.. candidates.Where(c => c.IsTemplate)] : [.. candidates];
        ImmutableArray<HlslExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];

        if (pool.Length == 0 || invocation.GetLocation() is not { } location || reported.Contains(location))
        {
            return;
        }

        if (!pool.Any(c => CallCompatibility.AcceptsArgumentCount(c.Declaration.ParameterList, arguments.Length)))
        {
            ReportArity(
                context, invocation, name, [.. pool.Select(c => c.Declaration)], arguments.Length, SymbolCondition.Always, reported);
            return;
        }

        if (pool is not [{ IsTemplate: true } single])
        {
            return;
        }

        ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(invocation);
        TemplateDeductionResult deduced = TemplateDeduction.Deduce(
            single, invocation, argument => TemplateDeduction.ArgumentType(evaluator, argument));

        if (!deduced.Fails || !reported.Add(location))
        {
            return;
        }

        string message;

        if (deduced.Mismatch is { } mismatch)
        {
            message = $"template の関数 '{name}' の型引数を推論できません。"
                      + $"第 {mismatch.ArgumentIndex + 1} 引数 ({mismatch.ArgumentType}) は仮引数 {mismatch.ParameterType} の形と一致しません。"
                      + "仮引数の形に合う値を渡してください。";
        }
        else
        {
            TemplateDeductionConflict conflict = deduced.Conflict!.Value;
            string? literalText = TemplateDeduction.IsLiteral(conflict.FirstType)
                ? TemplateDeduction.AsLiteral(arguments[conflict.FirstIndex])?.Text
                : TemplateDeduction.IsLiteral(conflict.SecondType)
                    ? TemplateDeduction.AsLiteral(arguments[conflict.SecondIndex])?.Text
                    : null;
            string? otherType = TemplateDeduction.IsLiteral(conflict.FirstType)
                ? (TemplateDeduction.IsLiteral(conflict.SecondType) ? null : conflict.SecondType)
                : conflict.FirstType;
            string typeArgument = otherType ?? "float";

            // 何ができないか、なぜか、どう直すかを分けて書く。
            string reason = $"第 {conflict.FirstIndex + 1} 引数 ({DescribeDeduced(conflict.FirstType, arguments[conflict.FirstIndex])}) と"
                            + $"第 {conflict.SecondIndex + 1} 引数 ({DescribeDeduced(conflict.SecondType, arguments[conflict.SecondIndex])}) で"
                            + $" {conflict.TypeParameter} の型が一致しません。";
            string fix = literalText is null
                ? $"実引数の型を揃えるか、{name}<{typeArgument}>(...) のように型引数を書いてください。"
                : literalText.Contains('.', StringComparison.Ordinal)
                    ? $"{literalText} のように f の付かない小数は、float とは別の型として推論されます。"
                      + $"{literalText}f と書くか、{name}<{typeArgument}>(...) のように型引数を書いてください。"
                    : $"{literalText} のように接尾辞の付かない整数は、int とは別の型として推論されます。"
                      + $"{name}<{typeArgument}>(...) のように型引数を書いてください。";

            message = $"template の関数 '{name}' の型引数 {conflict.TypeParameter} を推論できません。{reason}{fix}";
        }

        context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.TemplateArgumentConflict, location, message));
    }

    /// <summary>構造体のメソッドの呼び出しで、引数の個数を検査する (HL0341、HLSL 2021)。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="condition">出現条件の索引。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="invocation">検査する呼び出し。</param>
    /// <param name="access">呼び出しの対象 (<c>s.Get</c>)。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// 宣言は出現条件で絞らない。そのため、宣言にも呼び出しにも条件が付いていないときだけ報告する。
    /// template のメソッドがあれば、型引数で形が変わりうるので見ない。
    /// </remarks>
    private static void AnalyzeMethodCall(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ConditionMap condition,
        ExpressionTypeBinder binder,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax access,
        ReportedLocations reported)
    {
        if (compilation.MayLackOverloadsOf(access.Name)
            || !binder.Program.EnabledSymbols.IsDefaultOrEmpty
            || !condition.GetCondition(invocation).IsAlways
            || binder.GetEvaluatorFor(invocation).Evaluate(access.Target) is not { } receiver
            || !binder.TryGetStructFunctions(receiver, access.Name, out ImmutableArray<FunctionDeclarationSyntax> overloads)
            || overloads.Any(o => o.Parent is TemplateDeclarationSyntax || !condition.GetCondition(o).IsAlways))
        {
            return;
        }

        int actual = invocation.ArgumentExpressions.Count();

        if (overloads.Any(o => CallCompatibility.AcceptsArgumentCount(o.ParameterList, actual)))
        {
            return;
        }

        ReportArity(context, invocation, $"{receiver}.{access.Name}", [.. overloads], actual, SymbolCondition.Always, reported);
    }

    /// <summary>推論に使った実引数の型を、画面に出す形にする。</summary>
    /// <param name="type">推論に使った型。</param>
    /// <param name="argument">実引数。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeDeduced(string type, HlslExpressionSyntax argument)
        => TemplateDeduction.IsLiteral(type)
            ? TemplateDeduction.AsLiteral(argument)?.Text ?? type
            : type;

    /// <summary>引数の個数が合わないことを報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="invocation">対象の呼び出し。</param>
    /// <param name="name">関数の名前。</param>
    /// <param name="candidates">その構成に存在する宣言。</param>
    /// <param name="actual">渡している引数の個数。</param>
    /// <param name="call">呼び出しの出現条件。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void ReportArity(
        SyntaxTreeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        string name,
        List<FunctionDeclarationSyntax> candidates,
        int actual,
        SymbolCondition call,
        ReportedLocations reported)
    {
        if (invocation.GetLocation() is not { } location || !reported.Add(location))
        {
            return;
        }

        // 受け付けられる個数を並べる。同じ個数の宣言が複数あっても 1 度だけ書く。
        IEnumerable<string> accepted = candidates
            .Select(c => (ImmutableArray<ParameterSyntax>)[.. c.ParameterList])
            .Select(DescribeArity)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.ArgumentCountMismatch,
            location,
            name,
            actual,
            $"必要な引数は {string.Join(" / ", accepted)}です",
            DescribeCondition(call)));
    }

    /// <summary>宣言が受け付ける引数の個数を表す文字列を返す。</summary>
    /// <param name="parameters">仮引数。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeArity(ImmutableArray<ParameterSyntax> parameters)
    {
        int required = parameters.Count(p => p.DefaultValue is null);

        return required == parameters.Length
            ? $"{parameters.Length} 個"
            : $"{required}〜{parameters.Length} 個";
    }

    /// <summary>どの構成での話かを添える。</summary>
    /// <param name="call">呼び出しの出現条件。</param>
    /// <returns>添える文字列。無条件なら空。</returns>
    private static string DescribeCondition(SymbolCondition call)
        => call.IsAlways ? string.Empty : $" (構成：{call} のとき)";

    /// <summary>食い違いの種類。</summary>
    private enum MismatchKind
    {
        /// <summary>仮引数の型へ変換できない。</summary>
        NotConvertible,

        /// <summary>書き戻す先が無い。</summary>
        NotAssignable,

        /// <summary>配列の形が合わない。</summary>
        ArrayMismatch,
    }

    /// <summary>合わない引数 1 つ分。</summary>
    /// <param name="Kind">食い違いの種類。</param>
    /// <param name="Index">何番目の引数か。</param>
    /// <param name="ArgumentType">渡している値の型。</param>
    /// <param name="ParameterType">仮引数の型。</param>
    private readonly record struct Mismatch(
        MismatchKind Kind,
        int Index,
        string ArgumentType,
        string ParameterType);

    /// <summary>
    /// 渡せない引数を探す。
    /// </summary>
    /// <param name="parameters">仮引数。</param>
    /// <param name="arguments">実引数の式。</param>
    /// <param name="argumentTypes">実引数の型。分からないものは <see langword="null"/>。</param>
    /// <param name="binder">名前が指す宣言を引く仕組み。</param>
    /// <param name="declaresArrays">このシェーダーが長さの書かれた配列を 1 つでも宣言しているか。</param>
    /// <returns>見つかった食い違い。無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>分からないものは合っているとみなす。</b>
    /// 型が分からないことは「合っていない」ことの証拠にならない。
    /// </remarks>
    private static Mismatch? FindMismatch(
        ImmutableArray<ParameterSyntax> parameters,
        ImmutableArray<HlslExpressionSyntax> arguments,
        string?[] argumentTypes,
        ExpressionTypeBinder binder,
        bool declaresArrays)
    {
        for (int i = 0; i < argumentTypes.Length && i < parameters.Length; i++)
        {
            ParameterSyntax parameter = parameters[i];
            bool writeback = CallCompatibility.IsWriteback(parameter);

            if (writeback && !CallCompatibility.IsAssignable(arguments[i]))
            {
                return new Mismatch(
                    MismatchKind.NotAssignable, i, argumentTypes[i] ?? string.Empty, DescribeModifier(parameter));
            }

            // 配列は要素の型と長さの両方が一致していなければならない。
            if (!parameter.ArrayRankTokens.IsEmpty)
            {
                if (DescribeArrayMismatch(parameter, arguments[i], binder, declaresArrays) is { } reason)
                {
                    return new Mismatch(
                        MismatchKind.ArrayMismatch, i, reason, DescribeArrayParameter(parameter));
                }

                continue;
            }

            if (HlslConversion.IsConvertible(argumentTypes[i], parameter.Type.Name, writeback))
            {
                continue;
            }

            return new Mismatch(
                MismatchKind.NotConvertible, i, argumentTypes[i]!, parameter.Type.Name);
        }

        return null;
    }

    /// <summary>書き戻す修飾の名前を返す。</summary>
    /// <param name="parameter">対象の仮引数。</param>
    /// <returns>修飾の名前。</returns>
    private static string DescribeModifier(ParameterSyntax parameter)
    {
        foreach (ShaderSyntaxToken modifier in parameter.ModifierTokens)
        {
            if (CallCompatibility.IsWritebackModifier(modifier.Text))
            {
                return modifier.Text;
            }
        }

        return "out";
    }

    /// <summary>
    /// 配列の仮引数に対して、実引数の形が合わない理由を返す。
    /// </summary>
    /// <param name="parameter">対象の仮引数。</param>
    /// <param name="argument">渡している実引数。</param>
    /// <param name="binder">名前が指す宣言を引く仕組み。</param>
    /// <param name="declaresArrays">このシェーダーが長さの書かれた配列を 1 つでも宣言しているか。</param>
    /// <returns>合わない理由。合っているか判断できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>実引数が名前で書かれている場合は、その位置で指している宣言の形で判断する。</b>
    /// 別の関数に同じ名前で長さの違う配列があっても、取り違えない。
    /// 名前が何を指すか決められない、または候補の形が 1 つに決まらないなら報告しない。
    /// </para>
    /// <para>
    /// <b>構造体の配列のフィールド (<c>data.v</c>) も配列である。</b> フィールドの宣言の形で判断する。
    /// </para>
    /// <para>
    /// それ以外の式は、型が数値型などと分かったときだけ「配列ではない」とする。
    /// 多次元配列の一部 (<c>a[0]</c>) のように、型が分からない式は配列かもしれない。
    /// 報告するのは、このシェーダーが配列を宣言している場合だけにしている。
    /// </para>
    /// </remarks>
    private static string? DescribeArrayMismatch(
        ParameterSyntax parameter,
        HlslExpressionSyntax argument,
        ExpressionTypeBinder binder,
        bool declaresArrays)
    {
        if (argument is MemberAccessExpressionSyntax member)
        {
            return DescribeFieldArrayMismatch(parameter, member, binder, declaresArrays);
        }

        if (argument is not IdentifierExpressionSyntax identifier)
        {
            return declaresArrays && binder.GetEvaluatorFor(argument).Evaluate(argument) is not null
                ? "配列ではない値を渡しています"
                : null;
        }

        NameResolution resolution = binder.ResolveName(identifier);

        if (resolution.Kind is not (DeclaredNameKind.Local or DeclaredNameKind.Global)
            || !resolution.TryGetArrayLength(out int actualLength)
            || resolution.Candidates.Select(c => c.Type.TypeName).Distinct(StringComparer.Ordinal).ToArray()
                is not [string elementType])
        {
            return null;
        }

        if (!parameter.TryGetArrayLength(out int expected))
        {
            return null;
        }

        if (actualLength != expected)
        {
            return $"{elementType}[{actualLength}] を渡しています";
        }

        return string.Equals(elementType, parameter.Type.Name, StringComparison.Ordinal)
            ? null
            : $"{elementType}[{actualLength}] を渡しています";
    }

    /// <summary>構造体のフィールドを配列の仮引数へ渡す実引数を検査する。</summary>
    /// <param name="parameter">配列の仮引数。</param>
    /// <param name="member">実引数のメンバー参照。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="declaresArrays">このシェーダーが長さの書かれた配列を宣言しているか。</param>
    /// <returns>合わない理由。合っているか判断できない場合は <see langword="null"/>。</returns>
    private static string? DescribeFieldArrayMismatch(
        ParameterSyntax parameter,
        MemberAccessExpressionSyntax member,
        ExpressionTypeBinder binder,
        bool declaresArrays)
    {
        if (binder.GetEvaluatorFor(member).Evaluate(member.Target) is not { } structType
            || !binder.TryGetStructField(structType, member.Name, out VariableDeclaratorSyntax? field, out string? elementType))
        {
            // 構造体のフィールドでなければ、成分の取り出し (v.xyz) などである。型が分かれば配列ではない。
            return declaresArrays && binder.GetEvaluatorFor(member).Evaluate(member) is not null
                ? "配列ではない値を渡しています"
                : null;
        }

        if (!field.IsArray)
        {
            return declaresArrays ? "配列ではない値を渡しています" : null;
        }

        if (!field.TryGetArrayLength(out int actualLength) || !parameter.TryGetArrayLength(out int expected))
        {
            return null;
        }

        if (actualLength != expected)
        {
            return $"{elementType}[{actualLength}] を渡しています";
        }

        return string.Equals(elementType, parameter.Type.Name, StringComparison.Ordinal)
            ? null
            : $"{elementType}[{actualLength}] を渡しています";
    }

    /// <summary>配列の仮引数を表す文字列を返す。</summary>
    /// <param name="parameter">対象の仮引数。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeArrayParameter(ParameterSyntax parameter)
        => parameter.TryGetArrayLength(out int length)
            ? $"{parameter.Type.Name}[{length}]"
            : $"{parameter.Type.Name} の配列";

    /// <summary>このシェーダーが、長さの書かれた配列を 1 つでも宣言しているかを調べる。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>宣言していれば <see langword="true"/>。</returns>
    private static bool DeclaresArrays(ShaderCompilation compilation)
    {
        foreach ((HlslSyntaxNode node, AnalyzedProgram _) in compilation.EnumerateRuleNodes())
        {
            if (node is VariableDeclarationSyntax declaration
                && compilation.IsReportable(declaration)
                && declaration.Variables.Any(v => v.IsArray && v.TryGetArrayLength(out _)))
            {
                return true;
            }
        }

        return false;
    }
}
