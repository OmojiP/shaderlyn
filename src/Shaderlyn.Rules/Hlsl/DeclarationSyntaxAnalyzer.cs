using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// 宣言と式の形が成り立っているかを検査する (HL0343 / HL0211 / HL0370)。
/// </summary>
/// <remarks>
/// <para>
/// どれも「書かれた数どうしが合っているか」を見るだけで、
/// 型の推論も構成の条件も要らない。
/// </para>
/// <para>
/// <b>数字で書かれている場合だけ判定する。</b>
/// 添字も配列の長さもマクロや変数で書けるが、
/// その場合の値はここでは分からない。
/// </para>
/// </remarks>
internal sealed class DeclarationSyntaxAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>レジスタを指定する修飾の名前。</summary>
    private const string RegisterAnnotation = "register";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        HlslRuleDescriptors.ConstructorArgumentMismatch,
        HlslRuleDescriptors.DuplicateRegister,
        HlslRuleDescriptors.ArrayIndexOutOfBounds,
        HlslRuleDescriptors.LiteralTruncated,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 同じコードが Pass ごとに解析されるため、同じ位置を重ねて報告しない。
        HashSet<int> reported = [];

        // レジスタは宣言をまたいで衝突する。コードブロックごとに 1 つの表を持つ。
        // 別の Pass は別々にコンパイルされるので、そこで同じ番号を使っても衝突しない。
        Dictionary<(int Start, string? Kernel), Dictionary<string, List<RegisterOwner>>> registers = [];
        Dictionary<(int Start, string? Kernel), HashSet<string>> used = [];

        // 報告できるのは利用者が書いたコードだけである。
        // 展開後の木の大半はヘッダで、そこを歩いても 1 件も見つからない。
        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!compilation.IsReportable(node))
            {
                continue;
            }

            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    AnalyzeConstructor(context, compilation.GetExpressionTypeBinder(program), invocation, reported);
                    break;

                case ElementAccessExpressionSyntax access:
                    AnalyzeIndex(context, compilation.GetExpressionTypeBinder(program), access, reported);
                    break;

                case LiteralExpressionSyntax literal:
                    AnalyzeLiteral(context, literal, reported);
                    break;

                case VariableDeclarationSyntax declaration:
                    AnalyzeRegisters(
                        context,
                        compilation,
                        program,
                        declaration,
                        GetOrAdd(registers, program.BlockKey, static () => new(StringComparer.OrdinalIgnoreCase)),
                        () => GetOrAdd(used, program.BlockKey, () => CollectUsedNames(compilation, program)),
                        reported);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// 数値型のコンストラクタの引数を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="invocation">検査する呼び出し。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <para>
    /// 値を 1 つだけ渡す形は、スカラーの複製 (<c>float3(1)</c>) と
    /// 成分の切り捨て (<c>float3(v4)</c>) が許される。
    /// 足りない場合だけ誤りである。
    /// </para>
    /// <para>
    /// 2 つ以上渡す形は、平らにした成分の数がちょうど一致していなければならない。
    /// </para>
    /// </remarks>
    private static void AnalyzeConstructor(
        SyntaxTreeAnalysisContext context,
        ExpressionTypeBinder binder,
        InvocationExpressionSyntax invocation,
        HashSet<int> reported)
    {
        if (invocation.TargetName is not { } name
            || !HlslTypeClassifier.TryDescribeNumeric(name, out HlslNumericShape shape))
        {
            return;
        }

        int required = shape.Rows * shape.Columns;
        ImmutableArray<HlslExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];

        if (arguments.IsEmpty || CountComponents(binder, arguments) is not { } written)
        {
            return;
        }

        // 1 つだけ渡す形は、複製も切り捨ても許される。
        bool valid = arguments.Length == 1
            ? written == 1 || written >= required
            : written == required;

        if (valid || invocation.GetLocation() is not { } location || !reported.Add(location.Span.Start))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.ConstructorArgumentMismatch,
            location,
            name,
            required,
            $"渡しているのは {written} 個です"));
    }

    /// <summary>
    /// 引数に書かれた成分の個数を数える。
    /// </summary>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="arguments">対象の引数。</param>
    /// <returns>個数。1 つでも型が分からない引数があれば <see langword="null"/>。</returns>
    private static int? CountComponents(
        ExpressionTypeBinder binder,
        ImmutableArray<HlslExpressionSyntax> arguments)
    {
        int total = 0;

        foreach (HlslExpressionSyntax argument in arguments)
        {
            if (binder.CountComponents(argument) is not { } components)
            {
                return null;
            }

            total += components;
        }

        return total;
    }

    /// <summary>
    /// 整数のリテラルが 32 ビットに収まっているかを検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="literal">検査するリテラル。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// 浮動小数のリテラルは対象にしない。桁が多くても精度が落ちるだけで、
    /// 上位の桁が捨てられるわけではない。
    /// </remarks>
    private static void AnalyzeLiteral(
        SyntaxTreeAnalysisContext context,
        LiteralExpressionSyntax literal,
        HashSet<int> reported)
    {
        if (literal.Token.Kind != HlslSyntaxKind.NumericLiteralToken
            || !IsTruncatedInteger(literal.Token)
            || literal.GetLocation() is not { } location
            || !reported.Add(location.Span.Start))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.LiteralTruncated, location, literal.Token.Text));
    }

    /// <summary>
    /// 整数のリテラルが 32 ビットに収まらないかを判定する。
    /// </summary>
    /// <param name="token">リテラルのトークン。</param>
    /// <returns>収まらないなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>表記の見分けは <see cref="HlslLiteral"/> に任せる。</b>
    /// 8 進か 16 進かの判定を書き写すと、片方だけが直されて食い違う。
    /// ここに残すのは「何桁を超えたら収まらないか」という、このルール固有の判断だけである。
    /// </para>
    /// <para>
    /// 浮動小数は対象にしない。桁が多くても精度が落ちるだけである。
    /// 読み取れない形は「収まっている」に倒す。
    /// </para>
    /// </remarks>
    private static bool IsTruncatedInteger(HlslSyntaxToken token)
    {
        string digits = HlslLiteral.GetDigits(token);

        switch (HlslLiteral.GetKind(token))
        {
            case HlslLiteralKind.Hexadecimal:
                return digits[2..].TrimStart('0').Length > 8;

            case HlslLiteralKind.Octal:
                string octal = digits.TrimStart('0');
                return octal.Length > 11 || (octal.Length == 11 && octal[0] > '3');

            case HlslLiteralKind.Decimal:
                return !ulong.TryParse(digits, out ulong value) || value > uint.MaxValue;

            default:
                return false;
        }
    }

    /// <summary>配列の添字が範囲に収まっているかを検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="binder">名前が指す宣言を引く仕組み。</param>
    /// <param name="access">検査する添字の式。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// 配列の長さは、その位置で名前が指している宣言から読む。
    /// 別の関数に同じ名前で長さの違う配列があっても取り違えない。
    /// 名前が何を指すか決められない、または候補の長さが 1 つに決まらないなら報告しない。
    /// </remarks>
    private static void AnalyzeIndex(
        SyntaxTreeAnalysisContext context,
        ExpressionTypeBinder binder,
        ElementAccessExpressionSyntax access,
        HashSet<int> reported)
    {
        if (access.Target is not IdentifierExpressionSyntax target
            || binder.ResolveName(target) is not { Kind: DeclaredNameKind.Local or DeclaredNameKind.Global } resolution
            || !resolution.TryGetArrayLength(out int length)
            || access.Index is not LiteralExpressionSyntax literal
            || !HlslLiteral.TryGetInt64(literal.Token, out long index)
            || index < length
            || access.GetLocation() is not { } location
            || !reported.Add(location.Span.Start))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.ArrayIndexOutOfBounds, location, target.Name, length, index));
    }

    /// <summary>
    /// レジスタの重複を検査する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">宣言がある木。</param>
    /// <param name="declaration">検査する宣言。</param>
    /// <param name="registers">このコードブロックですでに使われているレジスタと、使っている宣言。</param>
    /// <param name="usedNames">このコードブロックのコードが使っている名前を求める。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// <para>
    /// レジスタの種類ごとに別の空間である。<c>b0</c> と <c>t0</c> は衝突しない。
    /// 指定の文字列をそのまま鍵にすれば、種類も番号もまとめて見られる。
    /// </para>
    /// <para>
    /// <b>使われていないリソースは衝突しない。</b> コンパイラは使われていないリソースを取り除いてから割り当てる。
    /// HDRP の CopyStencilBuffer.shader は、共通コード片で <c>_HTile</c> と <c>_StencilBufferCopy</c> を
    /// どちらも <c>register(u1)</c> に宣言し、Pass ごとにどちらか一方だけを使う。
    /// </para>
    /// <para>
    /// <b>同時には存在しない宣言どうしも衝突しない。</b>
    /// <c>#ifdef _B</c> と <c>#else</c> で別の名前を同じレジスタに宣言しても、1 つの構成には 1 つしか無い。
    /// 宣言の出現条件に、その木が表す構成 (<see cref="ShaderCompilation.GetTreeConfiguration"/>) を掛けて比べる。
    /// 条件が分からない組は報告しない。
    /// </para>
    /// </remarks>
    private static void AnalyzeRegisters(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        VariableDeclarationSyntax declaration,
        Dictionary<string, List<RegisterOwner>> registers,
        Func<HashSet<string>> usedNames,
        HashSet<int> reported)
    {
        SymbolCondition? present = null;

        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
        {
            foreach (SemanticSyntax annotation in variable.Semantics)
            {
                if (!string.Equals(annotation.Name, RegisterAnnotation, StringComparison.OrdinalIgnoreCase)
                    || DescribeRegister(annotation) is not { } register
                    || !usedNames().Contains(variable.Name))
                {
                    continue;
                }

                present ??= compilation.GetEffectiveCondition(declaration, program)
                    .And(compilation.GetTreeConfiguration(program));

                if (!registers.TryGetValue(register, out List<RegisterOwner>? owners))
                {
                    owners = [];
                    registers[register] = owners;
                }

                // 同じ宣言が Pass ごと・構成ごとに現れる。同じ名前どうしは衝突ではない。
                // 同時に存在しうる、別の名前の宣言だけを衝突とみなす。
                SymbolCondition here = present.Value;
                int conflictIndex = owners.FindIndex(owner =>
                    !string.Equals(owner.Name, variable.Name, StringComparison.Ordinal)
                    && !owner.Condition.IsUnknown
                    && !here.IsUnknown
                    && compilation.GetConditionMap().IsPossible(owner.Condition.And(here)));
                RegisterOwner? conflict = conflictIndex >= 0 ? owners[conflictIndex] : null;

                owners.Add(new RegisterOwner(variable.Name, here));

                if (conflict is not { } found
                    || annotation.GetLocation() is not { } location
                    || !reported.Add(location.Span.Start))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    HlslRuleDescriptors.DuplicateRegister, location, register, found.Name));
            }
        }
    }

    /// <summary>レジスタを使っている宣言 1 つ。</summary>
    /// <param name="Name">変数の名前。</param>
    /// <param name="Condition">宣言が存在する構成。</param>
    private readonly record struct RegisterOwner(string Name, SymbolCondition Condition);

    /// <summary>コードブロックのコードが名前として使っている識別子を集める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象のコードブロックの木。</param>
    /// <returns>同じコードブロックのどの構成の木かで使われている名前。</returns>
    /// <remarks>ヘッダのコードも数える。ヘッダの関数から使われるリソースも割り当てられる。</remarks>
    private static HashSet<string> CollectUsedNames(ShaderCompilation compilation, AnalyzedProgram program)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram tree in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            if (tree.BlockKey != program.BlockKey)
            {
                continue;
            }

            foreach (SyntaxNode node in tree.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is IdentifierExpressionSyntax identifier)
                {
                    names.Add(identifier.Name);
                }
            }
        }

        return names;
    }

    /// <summary>辞書から値を引き、無ければ作って入れる。</summary>
    /// <typeparam name="TKey">鍵の型。</typeparam>
    /// <typeparam name="TValue">値の型。</typeparam>
    /// <param name="dictionary">対象の辞書。</param>
    /// <param name="key">鍵。</param>
    /// <param name="create">値を作る関数。</param>
    /// <returns>引いた値か、作った値。</returns>
    private static TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, Func<TValue> create)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out TValue? value))
        {
            value = create();
            dictionary[key] = value;
        }

        return value;
    }

    /// <summary>レジスタの指定を 1 つの文字列にする。</summary>
    /// <param name="annotation">対象の修飾。</param>
    /// <returns>指定の文字列。読み取れない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>register(t0)</c> のように 1 つだけ書かれた形を見る。
    /// 空間の指定を伴う <c>register(t0, space1)</c> は、
    /// 同じ番号でも衝突しないので対象にしない。
    /// </remarks>
    private static string? DescribeRegister(SemanticSyntax annotation)
    {
        ImmutableArray<HlslSyntaxToken> arguments = annotation.ArgumentTokens;

        return arguments.Length == 1 && arguments[0].Kind == HlslSyntaxKind.IdentifierToken
            ? arguments[0].Text
            : null;
    }
}
