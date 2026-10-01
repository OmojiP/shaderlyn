using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// 宣言から分かった名前の型。
/// </summary>
/// <param name="TypeName">宣言に書かれた型名。</param>
/// <param name="IsArray">配列として宣言されているかどうか。</param>
/// <param name="TemplateArgument">
/// <c>StructuredBuffer&lt;float4&gt;</c> のようなテンプレート引数。無い場合は <see langword="null"/>。
/// </param>
/// <param name="ArrayDimensions">
/// 配列の次元の数。<c>float3 a[4][8]</c> なら 2。配列でないか、数えていなければ 0 (配列なら 1 とみなす)。
/// </param>
/// <remarks>
/// <b>「それ自体の型」と「添字で取り出したものの型」を分けて持つ必要がある。</b>
/// <c>float4 values[8];</c> の <c>values</c> 自体は配列であって <c>float4</c> ではない。
/// 一方 <c>values[i]</c> は <c>float4</c> である。
/// <c>StructuredBuffer&lt;float4&gt;</c> も同じで、
/// 添字で取り出して初めて <c>float4</c> になる。
/// 型名だけを持つと、この 2 つを取り違える。
/// </remarks>
public readonly record struct ResolvedTypeName(string TypeName, bool IsArray, string? TemplateArgument = null, int ArrayDimensions = 0);

/// <summary>
/// 式の型を、確実に分かる場合にだけ求める。
/// </summary>
/// <remarks>
/// <para>
/// <b>これは型検査器ではない。</b>
/// 分からないときは <see langword="null"/> を返す。
/// 呼び出し側は「型が違う」ことを根拠に診断を出すため、
/// 推測で型を返すと、正しく書かれたコードを誤りとして指摘することになる。
/// </para>
/// <para>
/// <b>扱わないのは、規則そのものが決まらないものだけである。</b>
/// 実引数の型による多重定義の選択は、暗黙の型変換の優先順位が要る。
/// 踏み込むのは「完全に一致する候補」と「成分を切り捨てる候補は、どの引数でも劣らない候補に負ける」の 2 つだけである。
/// <c>half</c> と <c>float</c> と <c>real</c> の間の順位は決まらないので扱わない。
/// </para>
/// <para>
/// 多重定義は引数の<b>個数</b>で絞り、残った候補がすべて同じ型を返すときに答える。
/// 行列の添字は行を表すベクトル、<c>asfloat</c> は形を変えず基底型だけを差し替える。
/// どちらも規則が書けるので扱う。
/// </para>
/// <para>
/// 扱わないことによる損失は「指摘が出ない」だけであり、方向として安全な側である。
/// ただし報告しない範囲は測って狭める。表に無い組み込み関数は 0 件まで減らした
/// (<see cref="HlslIntrinsics"/>)。
/// </para>
/// </remarks>
public sealed class ExpressionTypeEvaluator
{
    /// <summary>スウィズルに使える成分名。</summary>
    /// <remarks>位置 (xyzw) と色 (rgba) は混ぜられないが、混ざっていても型は変わらない。</remarks>
    private const string SwizzleComponents = "xyzwrgba";

    private readonly Func<IdentifierExpressionSyntax, ResolvedTypeName?> _resolve;
    private readonly FrozenDictionary<string, ImmutableArray<FunctionSignature>> _functionSignatures;
    private readonly FrozenDictionary<string, FrozenDictionary<string, ResolvedTypeName>> _structFields;
    private readonly FrozenDictionary<string, ImmutableArray<FunctionSignature>>? _functionOverrides;

    /// <summary>
    /// 評価器を生成する。
    /// </summary>
    /// <param name="resolve">
    /// 識別子が指す宣言の型を引く関数。見つからない、または型が定まらない場合は <see langword="null"/> を返す。
    /// </param>
    /// <param name="functionSignatures">利用者が宣言した関数の、宣言されている形。</param>
    /// <param name="structFields">構造体の名前から、フィールド名と型への対応。</param>
    /// <param name="functionOverrides">
    /// その構成で見える宣言だけに絞った形。<see cref="FunctionSignature"/> より先に引く。
    /// 絞らない場合は <see langword="null"/>。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>名前ではなく識別子のノードで引く。</b>
    /// 同じ名前でも、使っている位置によって指す宣言が違う。
    /// 内側の波括弧で宣言し直した名前は、その波括弧の中と外で別の変数である
    /// (<c>LocalScopes</c>)。
    /// </para>
    /// <para>
    /// <paramref name="functionOverrides"/> は、条件ごとに違う型を返す関数のためにある。
    /// 全体を作り直すと関数の数だけ費用がかかるので、変わる名前だけを上から重ねる。
    /// </para>
    /// </remarks>
    public ExpressionTypeEvaluator(
        Func<IdentifierExpressionSyntax, ResolvedTypeName?> resolve,
        FrozenDictionary<string, ImmutableArray<FunctionSignature>> functionSignatures,
        FrozenDictionary<string, FrozenDictionary<string, ResolvedTypeName>> structFields,
        FrozenDictionary<string, ImmutableArray<FunctionSignature>>? functionOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        _resolve = resolve;
        _functionSignatures = functionSignatures;
        _structFields = structFields;
        _functionOverrides = functionOverrides;
    }

    /// <summary>typedef の別名から、元の型の名前への対応。</summary>
    public FrozenDictionary<string, string> TypeAliases { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <summary>
    /// 式の型名を求める。
    /// </summary>
    /// <param name="expression">対象の式。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    public string? Evaluate(HlslExpressionSyntax? expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => Evaluate(parenthesized.Expression),

        // 変換先が書かれている。パーサーが優先順位を解決済みなので、
        // (half)x * y の型を half と誤って主張することはない。
        CastExpressionSyntax cast => cast.Type.Name,

        LiteralExpressionSyntax literal => EvaluateLiteral(literal),
        InvocationExpressionSyntax invocation => EvaluateInvocation(invocation),
        IdentifierExpressionSyntax identifier => EvaluateIdentifier(identifier),
        MemberAccessExpressionSyntax member => EvaluateSwizzle(member),
        ElementAccessExpressionSyntax element => EvaluateElementAccess(element),
        BinaryExpressionSyntax binary => EvaluateBinary(binary),
        ConditionalExpressionSyntax conditional => Combine(conditional.WhenTrue, conditional.WhenFalse),

        // 代入の値は、代入した後の代入先である。a = b = 0 の b = 0 は b の型になる。
        AssignmentExpressionSyntax assignment => Evaluate(assignment.Left),

        // 符号の反転は型を変えない。
        PrefixUnaryExpressionSyntax { OperatorToken.Kind: HlslSyntaxKind.PlusToken or HlslSyntaxKind.MinusToken }
            unary => Evaluate(unary.Operand),

        // 論理否定は成分ごとに bool を返す。
        PrefixUnaryExpressionSyntax { OperatorToken.Kind: HlslSyntaxKind.ExclamationToken }
            negation => ToBool(Evaluate(negation.Operand)),

        _ => null,
    };

    /// <summary>
    /// リテラルの型を求める。
    /// </summary>
    /// <param name="literal">対象のリテラル。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>表記の読み解きは <see cref="HlslLiteral"/> が持つ。</b>
    /// 接尾辞・16 進・8 進の規則をここに書き写すと、片方だけが直されて食い違う。
    /// </remarks>
    private static string? EvaluateLiteral(LiteralExpressionSyntax literal)
        => HlslLiteral.GetTypeName(literal.Token);

    /// <summary>
    /// 呼び出しの形をした式の型を求める。
    /// </summary>
    /// <param name="invocation">対象の呼び出し。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 見る順序は、コンストラクタ、利用者が宣言した関数、組み込み関数である。
    /// </para>
    /// <para>
    /// <b>宣言が組み込み関数より先でなければならない。</b>
    /// 利用者が <c>saturate</c> のような名前で自分の関数を定義することはありえる。
    /// 表を先に見ると、書かれている宣言を無視して誤った型を主張することになる。
    /// </para>
    /// </remarks>
    private string? EvaluateInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.TargetName is not { } name)
        {
            // tex.Sample(...) のように、呼び出しの対象がメンバーの場合。
            return invocation.Target is MemberAccessExpressionSyntax access
                ? EvaluateTextureMethod(access)
                : null;
        }

        if (HlslTypeClassifier.Classify(name) != HlslTypeClass.Unknown)
        {
            return name;
        }

        // typedef の別名で値を作る式 (V3(1, 2, 3)) は、元の型の値になる。
        if (TypeAliases.TryGetValue(name, out string? aliasOf))
        {
            return aliasOf;
        }

        ImmutableArray<HlslExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];

        if (_functionOverrides?.TryGetValue(name, out ImmutableArray<FunctionSignature> visible) == true)
        {
            return ResolveReturnType(visible, arguments);
        }

        if (_functionSignatures.TryGetValue(name, out ImmutableArray<FunctionSignature> signatures))
        {
            return ResolveReturnType(signatures, arguments);
        }

        return EvaluateIntrinsic(name, arguments);
    }

    /// <summary>
    /// オーバーロードの中から戻り値の型を決める。
    /// </summary>
    /// <param name="signatures">同じ名前で宣言されている形。</param>
    /// <param name="arguments">実引数。</param>
    /// <returns>型名。決まらない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// まず個数で絞り、残った候補がすべて同じ型を返す場合に答える。
    /// どれが選ばれるかを決めなくても、戻り値の型は確定しているためである。
    /// </para>
    /// <para>
    /// <b>候補が割れていれば、実引数の型が仮引数の型とすべて一致する候補がちょうど 1 つのときに答える。</b>
    /// 完全に一致する候補は、どんな暗黙の型変換の優先順位のもとでも選ばれる。
    /// Unity のヘッダは <c>Max3</c> や <c>LinearToSRGB</c> を型ごとに多重定義している。
    /// </para>
    /// <para>
    /// 完全に一致する候補が無ければ、成分を切り捨てる候補を除いて答える (<see cref="ResolveWithoutTruncation"/>)。
    /// それ以外の変換どうしの順位には踏み込まない。
    /// </para>
    /// </remarks>
    private string? ResolveReturnType(ImmutableArray<FunctionSignature> signatures, ImmutableArray<HlslExpressionSyntax> arguments)
    {
        int argumentCount = arguments.Length;
        string? resolved = null;
        bool divided = false;

        foreach (FunctionSignature signature in signatures)
        {
            if (argumentCount < signature.MinimumArguments || argumentCount > signature.MaximumArguments)
            {
                continue;
            }

            if (resolved is null)
            {
                resolved = signature.ReturnType;
                continue;
            }

            if (!string.Equals(resolved, signature.ReturnType, StringComparison.Ordinal))
            {
                divided = true;
            }
        }

        return divided ? ResolveByExactArguments(signatures, arguments) : resolved;
    }

    /// <summary>実引数の型が仮引数の型とすべて一致する候補の戻り値の型を求める。</summary>
    /// <param name="signatures">同じ名前で宣言されている形。</param>
    /// <param name="arguments">実引数。</param>
    /// <returns>一致する候補がちょうど 1 つならその戻り値の型。それ以外は <see langword="null"/>。</returns>
    private string? ResolveByExactArguments(ImmutableArray<FunctionSignature> signatures, ImmutableArray<HlslExpressionSyntax> arguments)
    {
        string?[] argumentTypes = new string?[arguments.Length];

        for (int i = 0; i < arguments.Length; i++)
        {
            if ((argumentTypes[i] = Evaluate(arguments[i])) is null)
            {
                return null;
            }
        }

        string? match = null;
        HashSet<FunctionDeclarationSyntax> seen = [];
        List<(string ReturnType, int[] Ranks)> viable = [];
        bool rankable = true;

        foreach (FunctionSignature signature in signatures)
        {
            if (signature.Declaration is not { } declaration)
            {
                // 形しか分からない候補があると、一致するかを確かめられない。
                return null;
            }

            if (!seen.Add(declaration)
                || arguments.Length < signature.MinimumArguments
                || arguments.Length > signature.MaximumArguments)
            {
                continue;
            }

            ImmutableArray<ParameterSyntax> parameters = [.. declaration.ParameterList];
            bool exact = true;

            for (int i = 0; i < arguments.Length && exact; i++)
            {
                exact = !parameters[i].IsArray
                        && string.Equals(parameters[i].Type.Name, argumentTypes[i], StringComparison.Ordinal);
            }

            if (!exact)
            {
                // 完全に一致しない候補は、変換の順位で比べるために取っておく。
                if (rankable)
                {
                    switch (RankConversions(parameters, argumentTypes))
                    {
                        case null:
                            rankable = false;
                            break;
                        case { Length: 0 }:
                            break;
                        case int[] ranks:
                            viable.Add((signature.ReturnType, ranks));
                            break;
                    }
                }

                continue;
            }

            if (match is not null && !string.Equals(match, signature.ReturnType, StringComparison.Ordinal))
            {
                return null;
            }

            match = signature.ReturnType;
        }

        return match ?? (rankable ? ResolveWithoutTruncation(viable) : null);
    }

    /// <summary>変換の順位。小さいほど良い。</summary>
    private const int RankIdentical = 0;

    /// <summary>形は同じで基底型だけが変わる (<c>half4</c> から <c>float4</c>)。</summary>
    private const int RankBaseConversion = 1;

    /// <summary>スカラーを全成分へ複製する。</summary>
    private const int RankSplat = 2;

    /// <summary>成分を切り捨てる。</summary>
    private const int RankTruncation = 3;

    /// <summary>
    /// 候補の仮引数へ実引数を渡すときの、引数ごとの変換の順位を求める。
    /// </summary>
    /// <param name="parameters">候補の仮引数。</param>
    /// <param name="argumentTypes">実引数の型。</param>
    /// <returns>
    /// 引数ごとの順位。渡せない候補は空の配列。
    /// 数値型でない引数や配列の仮引数があって順位を決められなければ <see langword="null"/>。
    /// </returns>
    private static int[]? RankConversions(ImmutableArray<ParameterSyntax> parameters, string?[] argumentTypes)
    {
        int[] ranks = new int[argumentTypes.Length];

        for (int i = 0; i < argumentTypes.Length; i++)
        {
            ParameterSyntax parameter = parameters[i];

            if (parameter.IsArray
                || !HlslTypeClassifier.TryDescribeNumeric(argumentTypes[i], out HlslNumericShape argument)
                || !HlslTypeClassifier.TryDescribeNumeric(parameter.Type.Name, out HlslNumericShape target))
            {
                return null;
            }

            bool writesBack = parameter.ModifierTokens.Any(t => t.TextIs("out") || t.TextIs("inout"));

            if (!HlslConversion.IsConvertible(argument, target, exact: writesBack))
            {
                return [];
            }

            bool sameShape = argument.Kind == target.Kind
                             && argument.Rows == target.Rows
                             && argument.Columns == target.Columns;

            ranks[i] = sameShape
                ? (string.Equals(argument.BaseName, target.BaseName, StringComparison.Ordinal) ? RankIdentical : RankBaseConversion)
                : argument.Kind == HlslNumericKind.Scalar ? RankSplat : RankTruncation;
        }

        return ranks;
    }

    /// <summary>
    /// 完全に一致する候補が無いとき、成分を切り捨てる候補を除いて戻り値の型を決める。
    /// </summary>
    /// <param name="viable">渡せる候補の戻り値の型と、引数ごとの変換の順位。</param>
    /// <returns>残った候補がすべて同じ型を返すならその型。それ以外は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>切り捨ては最も悪い変換である。</b> コンパイラは警告 (fxc の X3206) を出してまで通す。
    /// SRP の <c>LinearToSRGB</c> は <c>real</c> から <c>real4</c> まで多重定義してあり、
    /// D3D11 では <c>real</c> は <c>float</c> になる。<c>half4</c> を渡すと完全に一致する候補は無いが、
    /// <c>float4</c> の候補は基底型を変えるだけで済み、ほかの候補は成分を切り捨てる。
    /// </para>
    /// <para>
    /// <b>除くのは、どの引数でも劣らない候補がほかにある場合だけにする。</b>
    /// ある引数では切り捨てるが別の引数では良い候補は、順位の付け方によって選ばれうる。
    /// 変換の間の細かな順位 (基底型の変換と複製のどちらが良いか) には踏み込まない。
    /// </para>
    /// </remarks>
    private static string? ResolveWithoutTruncation(List<(string ReturnType, int[] Ranks)> viable)
    {
        if (viable.Count == 0)
        {
            return null;
        }

        string? resolved = null;

        foreach ((string returnType, int[] ranks) in viable)
        {
            bool dominated = ranks.Contains(RankTruncation)
                             && viable.Any(other => !other.Ranks.Contains(RankTruncation)
                                                    && other.Ranks.Zip(ranks).All(pair => pair.First <= pair.Second));

            if (dominated)
            {
                continue;
            }

            if (resolved is not null && !string.Equals(resolved, returnType, StringComparison.Ordinal))
            {
                return null;
            }

            resolved = returnType;
        }

        return resolved;
    }

    /// <summary>
    /// テクスチャのメソッドの結果の型を求める。
    /// </summary>
    /// <param name="access">呼び出しの対象となっているメンバーアクセス。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <c>Texture2D&lt;float4&gt;</c> のようにテンプレート引数が書かれていればその型、
    /// 書かれていなければ <c>float4</c> を返す。これは HLSL の既定である。
    /// </para>
    /// <para>
    /// 比較つきのサンプリング (<c>SampleCmp</c> 系) は、
    /// 比較の結果を返すのでスカラーになる。
    /// </para>
    /// </remarks>
    private string? EvaluateTextureMethod(MemberAccessExpressionSyntax access)
    {
        if (access.Target is not IdentifierExpressionSyntax identifier
            || _resolve(identifier) is not { } texture
            || !HlslTypeClassifier.Classify(texture.TypeName).IsTexture())
        {
            return null;
        }

        return HlslIntrinsics.GetTextureMethodReturnType(access.Name) switch
        {
            TextureMethodReturnType.Element => texture.TemplateArgument ?? "float4",
            TextureMethodReturnType.Scalar => "float",
            _ => null,
        };
    }

    /// <summary>
    /// 組み込み関数の結果の型を求める。
    /// </summary>
    /// <param name="name">関数の名前。</param>
    /// <param name="arguments">実引数。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 引数を揃えた型を先に求める。<c>lerp(half3, half3, half)</c> は
    /// スカラーが各成分へ配られるため <c>half3</c> になる。
    /// </remarks>
    private string? EvaluateIntrinsic(string name, ImmutableArray<HlslExpressionSyntax> arguments)
    {
        IntrinsicResultRule shape = HlslIntrinsics.GetResultShape(name);

        switch (shape)
        {
            case IntrinsicResultRule.Bool:
                return "bool";

            case IntrinsicResultRule.MatrixProduct:
                return EvaluateMatrixProduct(arguments);

            case IntrinsicResultRule.Transpose:
                return EvaluateTranspose(arguments);

            case IntrinsicResultRule.Determinant:
                return EvaluateDeterminant(arguments);

            case IntrinsicResultRule.FixedBaseType:
                return EvaluateFixedBaseType(HlslIntrinsics.GetFixedBaseType(name), arguments);

            case IntrinsicResultRule.SameAsFirstArgument:
                return arguments.IsEmpty ? null : Evaluate(arguments[0]);

            case IntrinsicResultRule.Void:
                return "void";

            case IntrinsicResultRule.FixedType:
                return HlslIntrinsics.GetFixedType(name);

            case IntrinsicResultRule.BoolPerComponent:
                return arguments.Length == 1 ? ToBool(Evaluate(arguments[0])) : null;

            default:
                break;
        }

        if (shape == IntrinsicResultRule.Unknown || arguments.IsEmpty)
        {
            return null;
        }

        string? combined = Evaluate(arguments[0]);

        for (int i = 1; i < arguments.Length && combined is not null; i++)
        {
            combined = Combine(combined, Evaluate(arguments[i]));
        }

        if (shape == IntrinsicResultRule.SameAsArguments)
        {
            return combined;
        }

        return HlslTypeClassifier.TryDecomposeNumeric(combined, out string baseName, out _)
            ? HlslTypeClassifier.ComposeNumeric(baseName, 1)
            : null;
    }

    /// <summary>
    /// 名前の型を求める。
    /// </summary>
    /// <param name="identifier">調べる識別子。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 配列そのものは型を持たないものとして扱う。
    /// <c>values</c> は <c>float4</c> ではなく <c>float4</c> の配列である。
    /// </para>
    /// <para>
    /// <b><c>true</c> と <c>false</c> は予約語であり、名前として宣言されることはない。</b>
    /// 構文の上では識別子として現れるため、ここで <c>bool</c> と答えないと
    /// <c>bool</c> のリテラルだけが型の分からない式になる。
    /// 宣言を先に見るのは、万一同じ名前の宣言があってもそちらを優先するためである。
    /// </para>
    /// </remarks>
    private string? EvaluateIdentifier(IdentifierExpressionSyntax identifier)
    {
        if (_resolve(identifier) is { } resolved)
        {
            return resolved.IsArray ? null : resolved.TypeName;
        }

        return identifier.Name is "true" or "false" ? "bool" : null;
    }

    /// <summary>
    /// 添字アクセスの結果の型を求める。
    /// </summary>
    /// <param name="element">対象の添字アクセス。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 配列なら要素の型、行列なら行を表すベクトル、ベクトルならスカラーになる。
    /// <c>m[0][1]</c> のように重ねた場合も、内側から順に解けば同じ規則で辿れる。
    /// </para>
    /// <para>
    /// バッファの添字はテンプレート引数の型を返すが、別の規則が要るため主張しない。
    /// </para>
    /// </remarks>
    private string? EvaluateElementAccess(ElementAccessExpressionSyntax element)
    {
        // 配列は、添字を次元の数だけ重ねて初めて要素になる。
        // float3 a[4][8] の a[0] はまだ配列で、a[0][1] が float3 である。
        int depth = 1;
        HlslExpressionSyntax root = element.Target;

        while (root is ElementAccessExpressionSyntax inner)
        {
            depth++;
            root = inner.Target;
        }

        if (ResolveArray(root) is { } array)
        {
            int dimensions = Math.Max(1, array.ArrayDimensions);

            if (depth < dimensions)
            {
                return null;
            }

            if (depth == dimensions)
            {
                return array.TypeName;
            }

            // 次元より多い添字は、要素 (ベクトルや行列) の添字である。下の規則で内側から解く。
        }

        // バッファとテクスチャは、それ自体の型と取り出したものの型が別物である。名前の宣言を先に見る。
        if (element.Target is IdentifierExpressionSyntax identifier
            && _resolve(identifier) is { IsArray: false } resolved)
        {
            switch (HlslTypeClassifier.Classify(resolved.TypeName))
            {
                case HlslTypeClass.Buffer:
                    return resolved.TemplateArgument;

                // tex[coord] は要素の型を返す。テンプレート引数を書かない Texture2D の要素は float4 である。
                // Cg の sampler2D も同じ分類だが、添字では読めないので除く。
                case HlslTypeClass.Texture2D or HlslTypeClass.Texture3D or HlslTypeClass.Texture2DArray
                    when resolved.TypeName.StartsWith("Texture", StringComparison.Ordinal)
                         || resolved.TypeName.StartsWith("RWTexture", StringComparison.Ordinal):
                    return resolved.TemplateArgument
                           ?? (resolved.TypeName.StartsWith("Texture", StringComparison.Ordinal) ? "float4" : null);

                default:
                    break;
            }
        }

        if (!HlslTypeClassifier.TryDescribeNumeric(Evaluate(element.Target), out HlslNumericShape shape))
        {
            return null;
        }

        return shape.Kind switch
        {
            HlslNumericKind.Matrix => HlslTypeClassifier.ComposeNumeric(shape.BaseName, shape.Columns),
            HlslNumericKind.Vector => shape.BaseName,
            _ => null,
        };
    }

    /// <summary>添字の起点が配列なら、その宣言の型を求める。</summary>
    /// <param name="root">添字を重ねた式の、いちばん内側の対象。</param>
    /// <returns>配列の宣言の型。配列でないか分からなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 名前 (<c>a[i]</c>) と、構造体の配列のフィールド (<c>data.v[i]</c>) を扱う。
    /// </remarks>
    private ResolvedTypeName? ResolveArray(HlslExpressionSyntax root)
    {
        switch (root)
        {
            case IdentifierExpressionSyntax identifier when _resolve(identifier) is { IsArray: true } resolved:
                return resolved;

            case MemberAccessExpressionSyntax member
                when Evaluate(member.Target) is { } structType
                     && _structFields.TryGetValue(structType, out FrozenDictionary<string, ResolvedTypeName>? fields)
                     && fields.TryGetValue(member.Name, out ResolvedTypeName field)
                     && field.IsArray:
                return field;

            default:
                return null;
        }
    }

    /// <summary>
    /// スウィズルの結果の型を求める。
    /// </summary>
    /// <param name="member">対象のメンバーアクセス。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 構文上は構造体のメンバー参照と区別が付かない。
    /// 対象が数値型で、かつ名前が成分名だけでできている場合にのみスウィズルとみなす。
    /// 数値型でなければ、構造体のフィールドとして型を引く。
    /// </remarks>
    private string? EvaluateSwizzle(MemberAccessExpressionSyntax member)
    {
        string? targetType = Evaluate(member.Target);

        if (!HlslTypeClassifier.TryDescribeNumeric(targetType, out HlslNumericShape shape))
        {
            // 数値型でなければ、構造体のフィールドとして引く。配列のフィールドそのものは型を持たない。
            return targetType is not null
                   && _structFields.TryGetValue(targetType, out FrozenDictionary<string, ResolvedTypeName>? fields)
                   && fields.TryGetValue(member.Name, out ResolvedTypeName field)
                   && !field.IsArray
                ? field.TypeName
                : null;
        }

        if (shape.Kind == HlslNumericKind.Matrix)
        {
            return EvaluateMatrixSwizzle(shape, member.Name);
        }

        string name = member.Name;

        if (name.Length is 0 or > 4 || name.Any(c => !SwizzleComponents.Contains(c, StringComparison.Ordinal)))
        {
            return null;
        }

        // 元の成分数を超える取り出しは成立しない。構造体のフィールドか、誤ったコードである。
        return name.Any(c => ComponentIndex(c) >= shape.Columns)
            ? null
            : HlslTypeClassifier.ComposeNumeric(shape.BaseName, name.Length);
    }

    /// <summary>
    /// 行列のスウィズルの結果の型を求める。
    /// </summary>
    /// <param name="shape">対象の行列の形。</param>
    /// <param name="name">メンバー名。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 行列には 2 通りの成分の書き方がある。
    /// <c>_m00</c> 形式は 0 始まり、<c>_11</c> 形式は 1 始まりで、混ぜて書くことはできない。
    /// どちらも取り出した個数がそのまま結果の成分数になる。
    /// </para>
    /// <para>
    /// 範囲を超える添字は成立しない。構造体のフィールドか、誤ったコードである。
    /// </para>
    /// </remarks>
    private static string? EvaluateMatrixSwizzle(HlslNumericShape shape, string name)
    {
        bool zeroBased = name.StartsWith("_m", StringComparison.Ordinal);
        int stride = zeroBased ? 4 : 3;
        int offset = zeroBased ? 1 : 0;

        if (name.Length == 0 || name.Length % stride != 0)
        {
            return null;
        }

        int count = name.Length / stride;

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<char> part = name.AsSpan(i * stride, stride);

            if (part[0] != '_'
                || (zeroBased && part[1] != 'm')
                || !char.IsAsciiDigit(part[offset + 1])
                || !char.IsAsciiDigit(part[offset + 2]))
            {
                return null;
            }

            int row = part[offset + 1] - '0' - (zeroBased ? 0 : 1);
            int column = part[offset + 2] - '0' - (zeroBased ? 0 : 1);

            if (row < 0 || row >= shape.Rows || column < 0 || column >= shape.Columns)
            {
                return null;
            }
        }

        return HlslTypeClassifier.ComposeNumeric(shape.BaseName, count);
    }

    /// <summary>成分名が何番目の成分を指すかを返す。</summary>
    /// <param name="component">成分名の文字。</param>
    /// <returns>0 始まりの位置。</returns>
    private static int ComponentIndex(char component)
        => SwizzleComponents.IndexOf(component, StringComparison.Ordinal) % 4;

    /// <summary>
    /// 二項演算の結果の型を求める。
    /// </summary>
    /// <param name="binary">対象の演算。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 代入は左辺の型になるが、実引数の位置に代入を書くコードは検査の対象として想定していない。
    /// </remarks>
    private string? EvaluateBinary(BinaryExpressionSyntax binary) => binary.OperatorToken.Kind switch
    {
        HlslSyntaxKind.PlusToken or HlslSyntaxKind.MinusToken
            or HlslSyntaxKind.AsteriskToken or HlslSyntaxKind.SlashToken
            or HlslSyntaxKind.PercentToken => Combine(binary.Left, binary.Right),

        HlslSyntaxKind.EqualsEqualsToken or HlslSyntaxKind.ExclamationEqualsToken
            or HlslSyntaxKind.LessThanToken or HlslSyntaxKind.GreaterThanToken
            or HlslSyntaxKind.LessThanEqualsToken or HlslSyntaxKind.GreaterThanEqualsToken
            or HlslSyntaxKind.AmpersandAmpersandToken or HlslSyntaxKind.BarBarToken
            => ToBool(Combine(binary.Left, binary.Right)),

        // ビット演算は整数どうしに限る。型の揃え方は算術と同じである。
        HlslSyntaxKind.AmpersandToken or HlslSyntaxKind.BarToken or HlslSyntaxKind.CaretToken
            => EvaluateBitwise(binary),

        // シフトの結果は左辺の基本型になる。成分数は揃える (1 << uint3(...) は int3)。
        HlslSyntaxKind.LessThanLessThanToken or HlslSyntaxKind.GreaterThanGreaterThanToken
            => EvaluateShift(binary),

        _ => null,
    };

    /// <summary>ビットごとの論理演算の結果の型を求める。</summary>
    /// <param name="binary">対象の式。</param>
    /// <returns>両辺が整数型なら揃えた型。それ以外は <see langword="null"/>。</returns>
    private string? EvaluateBitwise(BinaryExpressionSyntax binary)
    {
        string? left = Evaluate(binary.Left);
        string? right = Evaluate(binary.Right);

        return IsInteger(left) && IsInteger(right) ? Combine(left, right) : null;
    }

    /// <summary>シフトの結果の型を求める。</summary>
    /// <param name="binary">対象の式。</param>
    /// <returns>両辺が整数型なら、左辺の基本型で成分数を揃えた型。それ以外は <see langword="null"/>。</returns>
    private string? EvaluateShift(BinaryExpressionSyntax binary)
    {
        string? left = Evaluate(binary.Left);
        string? right = Evaluate(binary.Right);

        if (!IsInteger(left) || !IsInteger(right)
            || !HlslTypeClassifier.TryDescribeNumeric(left, out HlslNumericShape leftShape)
            || !HlslTypeClassifier.TryDescribeNumeric(right, out HlslNumericShape rightShape)
            || leftShape.Kind == HlslNumericKind.Matrix
            || rightShape.Kind == HlslNumericKind.Matrix)
        {
            return null;
        }

        // 成分数の違うベクトルどうしは揃わない。スカラーは相手に合わせて広がる。
        if (leftShape.Columns > 1 && rightShape.Columns > 1 && leftShape.Columns != rightShape.Columns)
        {
            return null;
        }

        return HlslTypeClassifier.ComposeNumeric(leftShape.BaseName, Math.Max(leftShape.Columns, rightShape.Columns));
    }

    /// <summary>整数の基本型を持つ数値型かを判定する。</summary>
    /// <param name="typeName">型名。</param>
    /// <returns>整数型なら <see langword="true"/>。</returns>
    private static bool IsInteger(string? typeName)
        => HlslTypeClassifier.TryDescribeNumeric(typeName, out HlslNumericShape shape)
           && shape.BaseName is "int" or "uint" or "dword" or "int16_t" or "uint16_t" or "int32_t" or "uint32_t"
               or "int64_t" or "uint64_t" or "min16int" or "min16uint" or "min12int";

    /// <summary>
    /// 比較の結果の型を、成分数を保ったまま <c>bool</c> にする。
    /// </summary>
    /// <param name="operandType">揃えた被演算子の型。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// HLSL の比較は成分ごとに行われる。<c>float4 &gt; float4</c> は <c>bool4</c> である。
    /// スカラーどうしの比較だけが <c>bool</c> になる。
    /// </remarks>
    private static string? ToBool(string? operandType)
        => HlslTypeClassifier.TryDecomposeNumeric(operandType, out _, out int components)
            ? HlslTypeClassifier.ComposeNumeric("bool", components)
            : null;

    /// <summary>
    /// 2 つの式の型を突き合わせ、揃えた結果の型を求める。
    /// </summary>
    /// <param name="left">一方の式。</param>
    /// <param name="right">もう一方の式。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    private string? Combine(HlslExpressionSyntax left, HlslExpressionSyntax right)
        => Combine(Evaluate(left), Evaluate(right));

    /// <summary>
    /// 2 つの型を突き合わせ、揃えた結果の型を求める。
    /// </summary>
    /// <param name="left">一方の型名。</param>
    /// <param name="right">もう一方の型名。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 基底型は順位の高いほうへ揃う。<c>half * float</c> は <c>float</c> である。
    /// 順位の決まらない基底型 (<c>real</c> / <c>fixed</c> / <c>min16float</c> 系) が
    /// 相手と違う型で混ざった場合は何も主張しない。
    /// </para>
    /// <para>
    /// 成分数は、片方がスカラーならもう片方に揃う。スカラーが各成分へ配られるためである。
    /// 成分数が食い違う組み合わせは成立しないので、その場合も主張しない。
    /// </para>
    /// </remarks>
    private static string? Combine(string? left, string? right)
    {
        if (!HlslTypeClassifier.TryDescribeNumeric(left, out HlslNumericShape a)
            || !HlslTypeClassifier.TryDescribeNumeric(right, out HlslNumericShape b)
            || HlslTypeClassifier.GetWiderBaseName(a.BaseName, b.BaseName) is not { } baseName)
        {
            return null;
        }

        // スカラーは相手の形へ配られる。
        if (a.Kind == HlslNumericKind.Scalar)
        {
            return HlslTypeClassifier.Compose(b with { BaseName = baseName });
        }

        if (b.Kind == HlslNumericKind.Scalar)
        {
            return HlslTypeClassifier.Compose(a with { BaseName = baseName });
        }

        // 形が違うものどうしは成立しない。
        // 行列とベクトルの * は成分ごとの演算にならず、mul を使う場面である。
        return a.Kind == b.Kind && a.Rows == b.Rows && a.Columns == b.Columns
            ? HlslTypeClassifier.Compose(a with { BaseName = baseName })
            : null;
    }

    /// <summary>
    /// <c>mul</c> の結果の型を求める。
    /// </summary>
    /// <param name="arguments">実引数。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <c>mul</c> は成分ごとの演算ではなく行列の積である。
    /// 左の列数と右の行数が一致していなければ成立しない。
    /// ベクトルは文脈によって行ベクトルにも列ベクトルにもなるため、
    /// 一致する向きがあればそちらで解釈する。
    /// </para>
    /// <para>
    /// スカラーが混ざる場合は成分ごとの積になり、相手の形がそのまま結果になる。
    /// </para>
    /// </remarks>
    private string? EvaluateMatrixProduct(ImmutableArray<HlslExpressionSyntax> arguments)
    {
        if (arguments.Length != 2
            || !HlslTypeClassifier.TryDescribeNumeric(Evaluate(arguments[0]), out HlslNumericShape a)
            || !HlslTypeClassifier.TryDescribeNumeric(Evaluate(arguments[1]), out HlslNumericShape b)
            || HlslTypeClassifier.GetWiderBaseName(a.BaseName, b.BaseName) is not { } baseName)
        {
            return null;
        }

        if (a.Kind == HlslNumericKind.Scalar)
        {
            return HlslTypeClassifier.Compose(b with { BaseName = baseName });
        }

        if (b.Kind == HlslNumericKind.Scalar)
        {
            return HlslTypeClassifier.Compose(a with { BaseName = baseName });
        }

        // ベクトルどうしは内積になり、結果はスカラーである。
        if (a.Kind == HlslNumericKind.Vector && b.Kind == HlslNumericKind.Vector)
        {
            return a.Columns == b.Columns ? baseName : null;
        }

        // ベクトルは左なら行ベクトル、右なら列ベクトルとして扱えば行列と同じ規則で解ける。
        int leftRows = a.Rows;
        int leftColumns = a.Columns;
        (int rightRows, int rightColumns) = b.Kind == HlslNumericKind.Vector
            ? (b.Columns, 1)
            : (b.Rows, b.Columns);

        if (leftColumns != rightRows)
        {
            return null;
        }

        HlslNumericKind kind = leftRows == 1 || rightColumns == 1
            ? HlslNumericKind.Vector
            : HlslNumericKind.Matrix;

        return kind == HlslNumericKind.Vector
            ? HlslTypeClassifier.ComposeNumeric(baseName, Math.Max(leftRows, rightColumns))
            : HlslTypeClassifier.Compose(new HlslNumericShape(kind, baseName, leftRows, rightColumns));
    }

    /// <summary>
    /// 引数と同じ形で、基底型だけが決まっている関数の型を求める。
    /// </summary>
    /// <param name="baseName">結果の基底型。</param>
    /// <param name="arguments">実引数。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>asfloat</c> はビットの並びをそのまま別の型として読み直す。
    /// <c>sign</c> や <c>f32tof16</c> は値も変わるが、形が変わらない点は同じである。
    /// どちらも基底型だけを差し替えればよい。
    /// </remarks>
    private string? EvaluateFixedBaseType(string? baseName, ImmutableArray<HlslExpressionSyntax> arguments)
    {
        if (baseName is null
            || arguments.IsEmpty
            || !HlslTypeClassifier.TryDescribeNumeric(Evaluate(arguments[0]), out HlslNumericShape shape))
        {
            return null;
        }

        return HlslTypeClassifier.Compose(shape with { BaseName = baseName });
    }

    /// <summary>
    /// 行列を転置した型を求める。
    /// </summary>
    /// <param name="arguments">実引数。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    private string? EvaluateTranspose(ImmutableArray<HlslExpressionSyntax> arguments)
        => arguments.Length == 1
           && HlslTypeClassifier.TryDescribeNumeric(Evaluate(arguments[0]), out HlslNumericShape shape)
           && shape.Kind == HlslNumericKind.Matrix
            ? HlslTypeClassifier.Compose(shape with { Rows = shape.Columns, Columns = shape.Rows })
            : null;

    /// <summary>
    /// 行列式の型を求める。
    /// </summary>
    /// <param name="arguments">実引数。</param>
    /// <returns>型名。判定できない場合は <see langword="null"/>。</returns>
    /// <remarks>行列式はスカラーである。基底型は元の行列と同じ。</remarks>
    private string? EvaluateDeterminant(ImmutableArray<HlslExpressionSyntax> arguments)
        => arguments.Length == 1
           && HlslTypeClassifier.TryDescribeNumeric(Evaluate(arguments[0]), out HlslNumericShape shape)
           && shape.Kind == HlslNumericKind.Matrix
            ? shape.BaseName
            : null;
}
