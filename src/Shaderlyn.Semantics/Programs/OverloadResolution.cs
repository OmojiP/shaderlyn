using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Semantics.Programs;

/// <summary>
/// 呼び出しの解決がどこまで行えたか。
/// </summary>
/// <remarks>
/// <b>「解決できなかった」には種類がある。</b>
/// 宣言が無いのか、渡せる宣言が無いのか、複数あって選べないのかは、
/// ルールから見ると別の話である。
/// 「選べなかった」を「呼ばれていない」と取り違えると、
/// 分からないことを根拠にした指摘が出る。
/// </remarks>
public enum OverloadResolutionStatus
{
    /// <summary>判断できなかった。</summary>
    /// <remarks>
    /// 実引数の型が分からない、呼び出しの出現条件が追えない、
    /// <c>obj.Method(...)</c> の形である、などが当たる。
    /// <b>この値を見たルールは報告しないこと。</b>
    /// </remarks>
    Unknown,

    /// <summary>宣言が 1 つに決まった。</summary>
    Resolved,

    /// <summary>組み込み関数だった。対応する宣言は無い。</summary>
    /// <remarks><c>lerp</c> や <c>saturate</c> のように、宣言がどこにも書かれていない関数である。</remarks>
    Intrinsic,

    /// <summary>その構成に、その名前の関数宣言が無い。</summary>
    NotDeclared,

    /// <summary>その個数の実引数を受け付ける宣言が無い。</summary>
    ArgumentCountMismatch,

    /// <summary>個数は合うが、実引数を渡せる宣言が 1 つも無い。</summary>
    NoMatch,

    /// <summary>渡せる宣言が複数あり、どれが選ばれるかを決められない。</summary>
    Ambiguous,
}

/// <summary>
/// 呼び出し 1 か所を解決した結果。
/// </summary>
/// <remarks>
/// <para>
/// <b>これは HLSL のオーバーロード解決の近似である。</b>
/// 実際のコンパイラは変換の費用で候補を順位づける。
/// ここでは「渡せる候補」を絞ったうえで、実引数の型と厳密に一致する位置が
/// 最も多い候補を選ぶ。決め手が無ければ <see cref="OverloadResolutionStatus.Ambiguous"/> を返し、
/// <b>当てずっぽうで 1 つを選ぶことはしない。</b>
/// </para>
/// <para>
/// 同じ形の宣言が複数あっても曖昧とは呼ばない。
/// プロトタイプと定義が別々に書かれている形はごく普通であり、
/// どちらを指しても「呼ばれるのはこの関数だ」という答えは変わらないためである。
/// その場合は本体を持つ定義を返す。
/// </para>
/// </remarks>
public sealed class OverloadResolution
{
    /// <summary>解決結果を作る。</summary>
    /// <param name="status">どこまで行えたか。</param>
    /// <param name="declaration">解決先。決まらなかった場合は <see langword="null"/>。</param>
    /// <param name="candidates">名前と出現条件で絞った候補。</param>
    /// <param name="argumentTypes">実引数の型。分からないものは <see langword="null"/>。</param>
    internal OverloadResolution(
        OverloadResolutionStatus status,
        FunctionDeclarationSyntax? declaration,
        ImmutableArray<FunctionDeclarationSyntax> candidates,
        ImmutableArray<string?> argumentTypes)
    {
        Status = status;
        Declaration = declaration;
        Candidates = candidates;
        ArgumentTypes = argumentTypes;
        ParameterTypes = declaration is null
            ? []
            : [.. declaration.ParameterList.Select(p => p.Type.Name)];
    }

    /// <summary>どこまで解決できたか。</summary>
    public OverloadResolutionStatus Status { get; }

    /// <summary>
    /// 呼ばれる関数の宣言。
    /// </summary>
    /// <remarks>
    /// 1 つに決まらなかった場合は <see langword="null"/>。
    /// <b><see langword="null"/> は「そんな関数は無い」ではなく「決められなかった」である。</b>
    /// </remarks>
    public FunctionDeclarationSyntax? Declaration { get; }

    /// <summary>
    /// 名前と出現条件で絞った候補。
    /// </summary>
    /// <remarks>
    /// 曖昧だったときに「何と何で迷ったか」を利用者へ見せるために残している。
    /// 実引数を渡せるかどうかでは絞っていない。
    /// </remarks>
    public ImmutableArray<FunctionDeclarationSyntax> Candidates { get; }

    /// <summary>実引数の型。分からなかったものは <see langword="null"/>。</summary>
    public ImmutableArray<string?> ArgumentTypes { get; }

    /// <summary>解決先の仮引数の型名。決まらなかった場合は空。</summary>
    /// <remarks>「この形のオーバーロードを禁じる」ようなルールは、これと照合すればよい。</remarks>
    public ImmutableArray<string> ParameterTypes { get; }

    /// <summary>宣言が 1 つに決まったかどうか。</summary>
    public bool IsResolved => Declaration is not null;
}

/// <summary>
/// 呼び出しを、その構成に存在する宣言のどれかへ結びつける。
/// </summary>
/// <remarks>
/// <para>
/// <b>候補はその構成に存在する宣言だけに絞る。</b>
/// <c>#ifdef</c> で仮引数が切り替わる関数では、条件を見ないと
/// 別の構成の宣言が候補に混ざる。
/// </para>
/// <para>
/// 入口は <see cref="ShaderCompilation.ResolveCall"/> である。
/// 索引と型の評価器はそちらで使い回す。
/// </para>
/// </remarks>
internal static class OverloadResolver
{
    /// <summary>判断できなかったことを表す結果。</summary>
    private static readonly OverloadResolution UnknownResult =
        new(OverloadResolutionStatus.Unknown, null, [], []);

    /// <summary>
    /// 呼び出しを解決する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="invocation">解決する呼び出し。</param>
    /// <param name="binder">実引数の型を求める仕組み。</param>
    /// <returns>解決結果。</returns>
    public static OverloadResolution Resolve(
        ShaderCompilation compilation,
        InvocationExpressionSyntax invocation,
        ExpressionTypeBinder binder)
    {
        if (invocation.TargetName is not { } name)
        {
            // obj.Method(...) の形。宣言として書かれているものではないので、解決先は無い。
            // テクスチャのメソッドだけは、組み込みであると分かる。
            return invocation.Target is MemberAccessExpressionSyntax member
                   && HlslIntrinsics.GetTextureMethodReturnType(member.Name) != TextureMethodReturnType.Unknown
                ? new OverloadResolution(OverloadResolutionStatus.Intrinsic, null, [], [])
                : UnknownResult;
        }

        ImmutableArray<FunctionDeclarationSyntax> declared = compilation.GetFunctionDeclarations(name, binder.Program);

        if (declared.IsEmpty)
        {
            return new OverloadResolution(
                HlslIntrinsics.IsKnownFunction(name)
                    ? OverloadResolutionStatus.Intrinsic
                    : OverloadResolutionStatus.NotDeclared,
                null,
                [],
                []);
        }

        ImmutableArray<FunctionDeclarationSyntax> candidates = Narrow(compilation, invocation, declared);

        if (candidates.IsEmpty)
        {
            return new OverloadResolution(OverloadResolutionStatus.NotDeclared, null, [], []);
        }

        ImmutableArray<HlslExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];

        ImmutableArray<FunctionDeclarationSyntax> fitting =
            [.. candidates.Where(c => CallCompatibility.AcceptsArgumentCount(c.ParameterList, arguments.Length))];

        if (fitting.IsEmpty)
        {
            return new OverloadResolution(
                OverloadResolutionStatus.ArgumentCountMismatch, null, candidates, []);
        }

        ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(invocation);
        ImmutableArray<string?> argumentTypes = [.. arguments.Select(evaluator.Evaluate)];

        ImmutableArray<FunctionDeclarationSyntax> viable =
            [.. fitting.Where(c => Accepts(c, arguments, argumentTypes))];

        if (viable.IsEmpty)
        {
            return new OverloadResolution(OverloadResolutionStatus.NoMatch, null, candidates, argumentTypes);
        }

        return Choose(viable, candidates, argumentTypes);
    }

    /// <summary>
    /// 渡せる候補の中から 1 つを選ぶ。
    /// </summary>
    /// <param name="viable">実引数を渡せる候補。</param>
    /// <param name="candidates">名前と条件で絞った候補。</param>
    /// <param name="argumentTypes">実引数の型。</param>
    /// <returns>解決結果。</returns>
    private static OverloadResolution Choose(
        ImmutableArray<FunctionDeclarationSyntax> viable,
        ImmutableArray<FunctionDeclarationSyntax> candidates,
        ImmutableArray<string?> argumentTypes)
    {
        // 同じ形の宣言が並んでいるだけなら、迷う余地は無い。
        if (HasSingleShape(viable))
        {
            return new OverloadResolution(
                OverloadResolutionStatus.Resolved, PreferDefinition(viable), candidates, argumentTypes);
        }

        // 型の分からない引数が候補を分けているなら、順位づけの根拠が無い。
        if (IsBlockedByUnknown(viable, argumentTypes))
        {
            return new OverloadResolution(OverloadResolutionStatus.Unknown, null, candidates, argumentTypes);
        }

        int best = viable.Max(c => CountExactMatches(c, argumentTypes));

        ImmutableArray<FunctionDeclarationSyntax> winners =
            [.. viable.Where(c => CountExactMatches(c, argumentTypes) == best)];

        return HasSingleShape(winners)
            ? new OverloadResolution(
                OverloadResolutionStatus.Resolved, PreferDefinition(winners), candidates, argumentTypes)
            : new OverloadResolution(OverloadResolutionStatus.Ambiguous, null, candidates, argumentTypes);
    }

    /// <summary>
    /// 呼び出しの構成に存在しうる宣言だけへ絞る。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="invocation">対象の呼び出し。</param>
    /// <param name="declared">同じ名前で宣言されているものすべて。</param>
    /// <returns>絞った候補。</returns>
    /// <remarks>
    /// <b>条件が分からないものは、あるものとして扱う。</b>
    /// 無いことにすると、追えなかっただけの宣言を候補から外したうえで
    /// 「1 つに決まった」と言うことになる。
    /// </remarks>
    private static ImmutableArray<FunctionDeclarationSyntax> Narrow(
        ShaderCompilation compilation,
        InvocationExpressionSyntax invocation,
        ImmutableArray<FunctionDeclarationSyntax> declared)
    {
        if (declared.Length == 1)
        {
            return declared;
        }

        ConditionMap condition = compilation.GetConditionMap();
        SymbolCondition call = condition.GetCondition(invocation);

        if (call.IsUnknown)
        {
            return declared;
        }

        return [.. declared.Where(c => CallCompatibility.IsPresentWith(condition, c, call))];
    }

    /// <summary>
    /// 実引数をすべて渡せるかを判定する。
    /// </summary>
    /// <param name="function">対象の宣言。</param>
    /// <param name="arguments">実引数の式。</param>
    /// <param name="argumentTypes">実引数の型。</param>
    /// <returns>渡せるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>分からないものは渡せるとみなす。</b>
    /// 型が分からないことは「渡せない」ことの証拠にならない。
    /// そのぶん候補が絞りきれず <see cref="OverloadResolutionStatus.Ambiguous"/> や
    /// <see cref="OverloadResolutionStatus.Unknown"/> になるが、それが正しい答えである。
    /// </remarks>
    private static bool Accepts(
        FunctionDeclarationSyntax function,
        ImmutableArray<HlslExpressionSyntax> arguments,
        ImmutableArray<string?> argumentTypes)
    {
        int index = 0;

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            if (index >= argumentTypes.Length)
            {
                break;
            }

            bool writeback = CallCompatibility.IsWriteback(parameter);

            // 配列は式で作れない。形の判定は HL0344 の担当なので、ここでは通す。
            if (parameter.ArrayRankTokens.IsEmpty
                && !HlslConversion.IsConvertible(argumentTypes[index], parameter.Type.Name, writeback))
            {
                return false;
            }

            if (writeback && !CallCompatibility.IsAssignable(arguments[index]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    /// <summary>
    /// 型の分からない引数のせいで候補を選べないかを判定する。
    /// </summary>
    /// <param name="viable">実引数を渡せる候補。</param>
    /// <param name="argumentTypes">実引数の型。</param>
    /// <returns>選べないなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>型が分からないこと自体は、決め手を欠くことを意味しない。</b>
    /// その位置の仮引数がどの候補でも同じ型なら、
    /// そこで候補が分かれることはなく、順位は他の位置で決まる。
    /// </para>
    /// <para>
    /// <c>Mix(Unknown(), uv)</c> のような呼び出しがこれに当たる。
    /// 1 つ目の型は分からないが、どの候補も 1 つ目の仮引数が <c>float</c> なら、
    /// そこで候補は分かれない。選ぶ根拠は 2 つ目にある。
    /// </para>
    /// </remarks>
    private static bool IsBlockedByUnknown(
        ImmutableArray<FunctionDeclarationSyntax> viable,
        ImmutableArray<string?> argumentTypes)
    {
        for (int i = 0; i < argumentTypes.Length; i++)
        {
            if (argumentTypes[i] is not null)
            {
                continue;
            }

            if (viable.Select(f => ParameterTypeAt(f, i))
                      .Distinct(StringComparer.Ordinal)
                      .Count() > 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>指定した位置の仮引数の型名を返す。</summary>
    /// <param name="function">対象の宣言。</param>
    /// <param name="index">何番目か。</param>
    /// <returns>型名。その位置に仮引数が無ければ <see langword="null"/>。</returns>
    private static string? ParameterTypeAt(FunctionDeclarationSyntax function, int index)
        => function.ParameterList.Skip(index).FirstOrDefault()?.Type.Name;

    /// <summary>実引数の型と厳密に一致する仮引数の数を数える。</summary>
    /// <param name="function">対象の宣言。</param>
    /// <param name="argumentTypes">実引数の型。</param>
    /// <returns>一致した位置の数。</returns>
    private static int CountExactMatches(
        FunctionDeclarationSyntax function,
        ImmutableArray<string?> argumentTypes)
    {
        int index = 0;
        int matches = 0;

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            if (index >= argumentTypes.Length)
            {
                break;
            }

            if (string.Equals(argumentTypes[index], parameter.Type.Name, StringComparison.Ordinal))
            {
                matches++;
            }

            index++;
        }

        return matches;
    }

    /// <summary>候補がすべて同じ形かを判定する。</summary>
    /// <param name="functions">対象の候補。</param>
    /// <returns>同じ形なら <see langword="true"/>。</returns>
    private static bool HasSingleShape(ImmutableArray<FunctionDeclarationSyntax> functions)
        => functions.Select(Describe).Distinct(StringComparer.Ordinal).Count() == 1;

    /// <summary>宣言の形を文字列で表す。</summary>
    /// <param name="function">対象の宣言。</param>
    /// <returns>仮引数の型を並べた文字列。</returns>
    private static string Describe(FunctionDeclarationSyntax function)
        => string.Join(",", function.ParameterList.Select(p => p.Type.Name));

    /// <summary>本体を持つ定義を優先して返す。</summary>
    /// <param name="functions">同じ形の宣言。</param>
    /// <returns>定義。無ければ先頭。</returns>
    private static FunctionDeclarationSyntax PreferDefinition(
        ImmutableArray<FunctionDeclarationSyntax> functions)
        => functions.FirstOrDefault(f => f.IsDefinition) ?? functions[0];
}
