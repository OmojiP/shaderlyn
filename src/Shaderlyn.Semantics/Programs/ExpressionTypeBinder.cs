using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Semantics.Programs;

/// <summary>
/// コードブロック 1 つの中で、式の型を求める評価器を用意する。
/// </summary>
/// <remarks>
/// <para>
/// <b>評価器を作るには文脈が要る。</b>
/// 名前の型はそれを囲む関数の宣言に依存し、
/// 関数の戻り値はそのブロックが取り込んだ宣言に依存する。
/// この組み立てをルールごとに書くと、片方が宣言を見落としたまま
/// 別の答えを出す、という食い違いが生まれる。
/// </para>
/// <para>
/// 関数 1 つにつき 1 回だけ宣言を集める。
/// 式ごとに集め直すと走査が二乗になる。
/// </para>
/// </remarks>
public sealed class ExpressionTypeBinder
{
    private readonly Dictionary<FunctionDeclarationSyntax, ExpressionTypeEvaluator> _byFunction = [];
    private readonly Dictionary<FunctionDeclarationSyntax, LocalScopes> _scopes = [];
    private readonly ShaderCompilation _compilation;
    private readonly FrozenDictionary<string, ImmutableArray<FunctionSignature>> _functionSignatures;
    private readonly FrozenDictionary<string, FrozenDictionary<string, ResolvedTypeName>> _structFields;

    /// <summary>この木で宣言された uniform。名前ごとに最初の宣言。</summary>
    private readonly FrozenDictionary<string, UniformSymbol> _ownUniforms;

    /// <summary>構造体の名前から、フィールド名とその宣言子への対応。配列の長さを読むのに使う。</summary>
    private readonly FrozenDictionary<string, FrozenDictionary<string, VariableDeclaratorSyntax>> _structFieldDeclarators;
    private readonly FrozenDictionary<string, ImmutableArray<(VariableDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator)>> _globalNames;
    private readonly FrozenDictionary<string, ResolvedTypeName?> _fileVariables;
    private readonly ExpressionTypeEvaluator _fileScope;
    private FrozenDictionary<string, ImmutableArray<FunctionSignature>>? _varyingFunctions;

    /// <summary>
    /// 条件によって違う型を返す関数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>同じ名前・同じ引数の個数で、返す型だけが違う宣言があるということである。</b>
    /// <c>#ifdef _A</c> で <c>float3 Sample()</c>、<c>#else</c> で <c>float4 Sample()</c> と書けば、
    /// 戻り値の型は構成によって変わる。
    /// 個数で絞るだけの <see cref="ExpressionTypeEvaluator"/> は、ここで候補が割れて型を不明にする。
    /// </para>
    /// <para>
    /// 名前を先に集めておく。ほとんどのシェーダーでは 1 つも無く、
    /// 無ければ仮定を立てる処理そのものを行わない。
    /// </para>
    /// </remarks>
    private FrozenDictionary<string, ImmutableArray<FunctionSignature>> VaryingFunctions
        => _varyingFunctions ??= CollectVaryingFunctions(_functionSignatures);

    /// <summary>型を求める対象の木。</summary>
    public AnalyzedProgram Program { get; }

    /// <summary>
    /// 束縛器を生成する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象のコードブロック。</param>
    public ExpressionTypeBinder(ShaderCompilation compilation, AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(program);

        _compilation = compilation;
        Program = program;
        _ownUniforms = program.Uniforms
            .GroupBy(uniform => uniform.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _functionSignatures = program.FunctionSignatures.ToFrozenDictionary(StringComparer.Ordinal);
        _structFields = CollectStructFields(program, out _structFieldDeclarators);
        _globalNames = CollectGlobalNames(program.Tree.Root.Declarations);
        _fileVariables = CollectFileVariables(_globalNames);
        _fileScope = new ExpressionTypeEvaluator(
            identifier => LookupGlobal(identifier.Name), _functionSignatures, _structFields)
        {
            TypeAliases = program.Tree.Root.TypeAliases,
        };
    }

    /// <summary>
    /// 構造体のフィールドの型を集める。
    /// </summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <param name="declarators">構造体の名前から、フィールド名とその宣言子への対応。</param>
    /// <returns>構造体の名前から、フィールド名と型への対応。</returns>
    /// <remarks>
    /// <para>
    /// <c>IN.uv</c> のような書き方はシェーダーの至るところに現れる。
    /// フィールドの型を引けないと、頂点から受け取った値を使う式が軒並み判定できなくなる。
    /// </para>
    /// <para>
    /// 配列のフィールドも載せる。<c>data.v</c> そのものは型を持たず、<c>data.v[i]</c> が要素の型になる。
    /// </para>
    /// </remarks>
    private static FrozenDictionary<string, FrozenDictionary<string, ResolvedTypeName>> CollectStructFields(
        AnalyzedProgram program,
        out FrozenDictionary<string, FrozenDictionary<string, VariableDeclaratorSyntax>> declarators)
    {
        Dictionary<string, FrozenDictionary<string, ResolvedTypeName>> byStruct = new(StringComparer.Ordinal);
        Dictionary<string, FrozenDictionary<string, VariableDeclaratorSyntax>> declaratorsByStruct = new(StringComparer.Ordinal);

        foreach (StructDeclarationSyntax declaration in program.Structs)
        {
            if (declaration.Name.Length == 0 || byStruct.ContainsKey(declaration.Name))
            {
                continue;
            }

            Dictionary<string, ResolvedTypeName> fields = new(StringComparer.Ordinal);
            Dictionary<string, VariableDeclaratorSyntax> fieldDeclarators = new(StringComparer.Ordinal);

            foreach (HlslDeclarationSyntax member in declaration.Members)
            {
                if (member is not VariableDeclarationSyntax variable)
                {
                    continue;
                }

                foreach (VariableDeclaratorSyntax field in variable.Variables)
                {
                    fields[field.Name] = new ResolvedTypeName(
                        variable.Type.Name,
                        field.IsArray,
                        GetTemplateArgument(variable.Type),
                        field.ArrayDimensions);
                    fieldDeclarators[field.Name] = field;
                }
            }

            byStruct[declaration.Name] = fields.ToFrozenDictionary(StringComparer.Ordinal);
            declaratorsByStruct[declaration.Name] = fieldDeclarators.ToFrozenDictionary(StringComparer.Ordinal);
        }

        declarators = declaratorsByStruct.ToFrozenDictionary(StringComparer.Ordinal);
        return byStruct.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>構造体のフィールドの宣言子を引く。</summary>
    /// <param name="structType">構造体の名前。</param>
    /// <param name="fieldName">フィールドの名前。</param>
    /// <param name="declarator">フィールドの宣言子。</param>
    /// <param name="elementType">フィールドの型 (配列なら要素の型)。</param>
    /// <returns>見つかれば <see langword="true"/>。</returns>
    public bool TryGetStructField(
        string structType,
        string fieldName,
        [NotNullWhen(true)] out VariableDeclaratorSyntax? declarator,
        [NotNullWhen(true)] out string? elementType)
    {
        declarator = null;
        elementType = null;

        if (!_structFieldDeclarators.TryGetValue(structType, out FrozenDictionary<string, VariableDeclaratorSyntax>? fields)
            || !fields.TryGetValue(fieldName, out declarator))
        {
            return false;
        }

        elementType = _structFields[structType][fieldName].TypeName;
        return true;
    }

    /// <summary>
    /// ノードを囲む関数に対応する評価器を返す。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>評価器。関数の外にあるノードには、局所的な宣言を持たない評価器を返す。</returns>
    public ExpressionTypeEvaluator GetEvaluatorFor(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is not { } function)
        {
            return _fileScope;
        }

        if (!_byFunction.TryGetValue(function, out ExpressionTypeEvaluator? evaluator))
        {
            LocalScopes scopes = GetScopes(function);

            evaluator = _byFunction[function] = new ExpressionTypeEvaluator(
                identifier => Resolve(scopes, identifier, static candidates => candidates),
                _functionSignatures,
                _structFields)
            {
                TypeAliases = Program.Tree.Root.TypeAliases,
            };
        }

        return evaluator;
    }

    /// <summary>識別子が指す宣言の型を引く。</summary>
    /// <param name="scopes">識別子を囲む関数の範囲。</param>
    /// <param name="identifier">使っている識別子。</param>
    /// <param name="narrow">見える宣言を、その構成で見えるものに絞る。絞らないなら素通しにする。</param>
    /// <returns>型。見つからない、型が割れている、または判断できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 関数の中に宣言が無ければ関数の外を引く。
    /// 関数の中に位置を決められない宣言があれば、外は引かない。
    /// 外にある同じ名前の宣言で型を決めると、別の変数の型で判定することになる。
    /// </remarks>
    private ResolvedTypeName? Resolve(
        LocalScopes scopes,
        IdentifierExpressionSyntax identifier,
        Func<ImmutableArray<LocalDeclaration>, IEnumerable<LocalDeclaration>> narrow)
    {
        if (scopes.Lookup(identifier) is not { } candidates)
        {
            return null;
        }

        if (candidates.IsEmpty)
        {
            return LookupGlobal(identifier.Name);
        }

        ResolvedTypeName[] types = [.. narrow(candidates).Select(c => c.Type).Distinct()];

        return types.Length == 1 ? types[0] : null;
    }

    /// <summary>
    /// 型が構成によって変わる名前を含む式について、型を決められる仮定を列挙する。
    /// </summary>
    /// <param name="node">式を含むノード。この位置の出現条件から始める。</param>
    /// <param name="expression">型を求めたい式。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <returns>
    /// 成り立つ仮定。式の中の名前がどれも 1 つの型に決まっていれば空。
    /// 仮定が多すぎる場合も空 (判断しない)。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>同じ名前を条件ごとに違う型で宣言するのは普通の書き方である。</b>
    /// <c>#ifdef _A</c> で <c>float3 color</c>、<c>#else</c> で <c>float4 color</c> と書けば、
    /// <c>color</c> の型は構成によって変わる。<see cref="GetEvaluatorFor(SyntaxNode)"/> はこの名前の型を不明にする。
    /// </para>
    /// <para>
    /// 宣言ごとに「その宣言が見える」仮定 (使う位置の条件 ∧ 宣言の条件) を作り、成り立つものを返す。
    /// 仮定ごとに <see cref="GetEvaluatorFor(SyntaxNode, SymbolCondition, ConditionMap)"/> で型を求めれば、
    /// 「<c>_A</c> と <c>_B</c> のとき <c>color</c> は <c>float3</c>」のように構成ごとに判断できる。
    /// </para>
    /// </remarks>
    public ImmutableArray<SymbolCondition> EnumerateAssumptions(
        HlslSyntaxNode node,
        HlslExpressionSyntax expression,
        ConditionMap conditions)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return EnumerateAssumptions(node, [expression], conditions);
    }

    /// <summary>
    /// 型が構成によって変わる名前を含む複数の式について、どの式の型も決められる仮定を列挙する。
    /// </summary>
    /// <param name="node">式を含むノード。この位置の出現条件から始める。</param>
    /// <param name="expressions">型を求めたい式。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <returns>
    /// 成り立つ仮定。どの式の中の名前も 1 つの型に決まっていれば空。
    /// 仮定が多すぎる場合も空 (判断しない)。
    /// </returns>
    /// <remarks>
    /// <b>代入の左辺と右辺のように、同じ構成で型を比べる式に使う。</b>
    /// 式ごとに別々に仮定を立てると、左辺は <c>_A</c>、右辺は <c>_D</c> という
    /// 同時には比べられない組を比べることになる。
    /// 両方の式に出てくる名前をまとめて仮定を立てれば、1 つの仮定の下で両方の型が決まる。
    /// </remarks>
    public ImmutableArray<SymbolCondition> EnumerateAssumptions(
        HlslSyntaxNode node,
        ImmutableArray<HlslExpressionSyntax> expressions,
        ConditionMap conditions)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(conditions);

        LocalScopes? scopes =
            node.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } function
                ? GetScopes(function)
                : null;

        if (scopes?.HasVaryingTypes != true && VaryingFunctions.Count == 0)
        {
            return [];
        }

        SymbolCondition use = conditions.GetCondition(node);

        if (use.IsUnknown)
        {
            return [];
        }

        List<SymbolCondition> assumptions = [use];
        bool found = false;

        // 同じ文の中の同じ名前は、同じ位置から引くので同じ宣言を指す。
        foreach (IdentifierExpressionSyntax identifier in expressions
                     .SelectMany(e => e.DescendantNodesAndSelf())
                     .OfType<IdentifierExpressionSyntax>()
                     .DistinctBy(i => i.Name, StringComparer.Ordinal))
        {
            // 型が割れている名前だけが、構成ごとに分ける対象になる。
            if (scopes?.Lookup(identifier) is not { Length: > 1 } candidates
                || candidates.Select(c => c.Type).Distinct().Count() < 2)
            {
                continue;
            }

            found = true;
            List<SymbolCondition> next = [];

            foreach (SymbolCondition assumption in assumptions)
            {
                foreach (LocalDeclaration candidate in candidates)
                {
                    // 宣言の出現条件ではなく、この位置から見える条件を使う。
                    // 内側の同じ名前に隠される構成では、外側の宣言は見えない (LocalScopes)。
                    SymbolCondition declared = candidate.Condition;

                    if (declared.IsUnknown)
                    {
                        return [];
                    }

                    SymbolCondition combined = conditions.Simplify(assumption.And(declared));

                    if (!combined.IsUnknown && conditions.IsPossible(combined) && !next.Contains(combined))
                    {
                        next.Add(combined);
                    }
                }
            }

            // 仮定が増えすぎたら判断しない。どれも同じ重みで調べる理由が無い。
            if (next.Count > MaxAssumptions)
            {
                return [];
            }

            assumptions = next;
        }

        // 条件によって違う型を返す関数も、呼んでいれば同じように仮定を分ける。
        foreach (string name in expressions
                     .SelectMany(e => e.DescendantNodesAndSelf())
                     .OfType<InvocationExpressionSyntax>()
                     .Select(i => i.TargetName)
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            if (!VaryingFunctions.TryGetValue(name, out ImmutableArray<FunctionSignature> candidates))
            {
                continue;
            }

            found = true;
            List<SymbolCondition> next = [];

            foreach (SymbolCondition assumption in assumptions)
            {
                foreach (FunctionSignature candidate in candidates)
                {
                    if (candidate.Declaration is not { } declaration)
                    {
                        return [];
                    }

                    SymbolCondition declared = conditions.GetCondition(declaration);

                    if (declared.IsUnknown)
                    {
                        return [];
                    }

                    SymbolCondition combined = conditions.Simplify(assumption.And(declared));

                    if (!combined.IsUnknown && conditions.IsPossible(combined) && !next.Contains(combined))
                    {
                        next.Add(combined);
                    }
                }
            }

            if (next.Count > MaxAssumptions)
            {
                return [];
            }

            assumptions = next;
        }

        return found ? [.. assumptions] : [];
    }

    /// <summary>
    /// 条件によって違う型を返す関数を集める。
    /// </summary>
    /// <param name="signatures">関数の名前から、宣言されている形への対応。</param>
    /// <returns>返す型が割れている名前と、その形。</returns>
    /// <remarks>
    /// 引数の個数で絞っても型が 1 つに決まらない名前だけを残す。
    /// 個数で決まるなら、構成を分けて考える必要は無い。
    /// </remarks>
    private static FrozenDictionary<string, ImmutableArray<FunctionSignature>> CollectVaryingFunctions(
        FrozenDictionary<string, ImmutableArray<FunctionSignature>> signatures)
    {
        Dictionary<string, ImmutableArray<FunctionSignature>> varying = new(StringComparer.Ordinal);

        foreach ((string name, ImmutableArray<FunctionSignature> candidates) in signatures)
        {
            if (candidates.Length < 2
                || candidates.Select(c => c.ReturnType).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                continue;
            }

            // 個数で絞れば型が決まる名前は、ふつうのオーバーロードである。
            bool decidedByCount = candidates
                .SelectMany(c => Enumerable.Range(c.MinimumArguments, c.MaximumArguments - c.MinimumArguments + 1))
                .Distinct()
                .All(count => candidates
                    .Where(c => count >= c.MinimumArguments && count <= c.MaximumArguments)
                    .Select(c => c.ReturnType)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == 1);

            if (!decidedByCount)
            {
                varying[name] = candidates;
            }
        }

        return varying.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// 仮定の下で、ノードを囲む関数に対応する評価器を返す。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <param name="assumption">成り立っているとする条件 (<see cref="EnumerateAssumptions(HlslSyntaxNode, HlslExpressionSyntax, ConditionMap)"/>)。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <returns>評価器。</returns>
    /// <remarks>
    /// 型が構成によって変わる名前は、仮定の下で見える宣言だけから型を決める。
    /// 見える宣言の型が 1 つに決まらなければ、今までどおり不明にする。
    /// </remarks>
    public ExpressionTypeEvaluator GetEvaluatorFor(SyntaxNode node, SymbolCondition assumption, ConditionMap conditions)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(conditions);

        LocalScopes? scopes =
            node.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } function
                ? GetScopes(function)
                : null;

        if ((scopes?.HasVaryingTypes != true && VaryingFunctions.Count == 0) || assumption.IsUnknown)
        {
            return GetEvaluatorFor(node);
        }

        // 型が割れている名前は、仮定の下で見える宣言だけから型を決める。
        IEnumerable<LocalDeclaration> Visible(ImmutableArray<LocalDeclaration> candidates)
            => candidates.Where(c => c.Condition is { IsUnknown: false } declared
                                     && conditions.IsPossible(assumption.And(declared)));

        Func<IdentifierExpressionSyntax, ResolvedTypeName?> resolve = scopes is null
            ? identifier => LookupGlobal(identifier.Name)
            : identifier => Resolve(scopes, identifier, Visible);

        // 条件によって違う型を返す関数は、その構成で見える宣言だけに絞る。
        // 表を作り直すと関数の数だけ費用がかかるので、変わる名前だけを重ねる。
        Dictionary<string, ImmutableArray<FunctionSignature>> overrides = new(StringComparer.Ordinal);

        foreach ((string name, ImmutableArray<FunctionSignature> candidates) in VaryingFunctions)
        {
            ImmutableArray<FunctionSignature> visible =
            [
                .. candidates.Where(c =>
                    c.Declaration is not { } declaration
                    || (conditions.GetCondition(declaration) is { IsUnknown: false } declared
                        && conditions.IsPossible(assumption.And(declared)))),
            ];

            if (visible.Length != candidates.Length)
            {
                overrides[name] = visible;
            }
        }

        return new ExpressionTypeEvaluator(
            resolve,
            _functionSignatures,
            _structFields,
            overrides.Count == 0 ? null : overrides.ToFrozenDictionary(StringComparer.Ordinal))
        {
            TypeAliases = Program.Tree.Root.TypeAliases,
        };
    }

    /// <summary>1 つの式について調べる仮定の上限。</summary>
    private const int MaxAssumptions = 16;

    /// <summary>関数の中の名前の範囲を、関数ごとに 1 回だけ作る。</summary>
    /// <param name="function">対象の関数。</param>
    /// <returns>その関数の範囲。</returns>
    /// <remarks>式ごとに作り直すと走査が二乗になる。</remarks>
    private LocalScopes GetScopes(FunctionDeclarationSyntax function)
    {
        if (!_scopes.TryGetValue(function, out LocalScopes? scopes))
        {
            scopes = _scopes[function] = new LocalScopes(function, _compilation.GetConditionMap());
        }

        return scopes;
    }

    /// <summary>
    /// 式が持つ数値の成分の個数を求める。
    /// </summary>
    /// <param name="expression">対象の式。</param>
    /// <returns>個数 (<c>float3</c> なら 3、<c>float2x2</c> なら 4)。型が分からない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// コンストラクタの引数 (<c>float4(a.xy, 0, 1)</c>) と波括弧の初期化 (<c>{ 1, 2, 3 }</c>) は、
    /// どちらも成分を平らにして数える。その 1 要素分の数え方である。
    /// </para>
    /// <para>
    /// <b>数のリテラルは、型が決まらなくても成分は 1 つである。</b>
    /// 接尾辞の無い <c>0</c> や <c>0.5</c> の型は文脈で決まるため、型としては判定できない。
    /// </para>
    /// </remarks>
    public int? CountComponents(HlslExpressionSyntax expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (expression is LiteralExpressionSyntax)
        {
            return 1;
        }

        return HlslTypeClassifier.TryDescribeNumeric(
                   GetEvaluatorFor(expression).Evaluate(expression), out HlslNumericShape shape)
            ? shape.Rows * shape.Columns
            : null;
    }

    /// <summary>関数の外で宣言された名前の型を引く。</summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>見つかった型。無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// uniform を先に引き、無ければ <c>static</c> / <c>groupshared</c> の変数を引く
    /// (<see cref="CollectFileVariables"/>)。
    /// </para>
    /// <para>
    /// <b>uniform はこの木の宣言から引く。</b>
    /// コンピュートシェーダーのカーネルごとに <c>CTYPE=float3</c> / <c>CTYPE=float4</c> と指定し、
    /// <c>RWTexture2D&lt;CTYPE&gt;</c> を宣言する書き方がある (HDRP の DepthOfFieldMip.compute)。
    /// シェーダー全体で最初に見つかった宣言を使うと、別のカーネルの型で判定する。
    /// この木に無ければ、これまでどおりシェーダー全体から引く。
    /// </para>
    /// </remarks>
    private ResolvedTypeName? LookupGlobal(string name)
    {
        if (_ownUniforms.TryGetValue(name, out UniformSymbol? uniform) || _compilation.TryGetUniform(name, out uniform))
        {
            return new ResolvedTypeName(uniform.TypeName, uniform.IsArray, GetTemplateArgument(uniform.Declaration.Type), uniform.Declarator.ArrayDimensions);
        }

        return _fileVariables.TryGetValue(name, out ResolvedTypeName? variable) ? variable : null;
    }

    /// <summary>
    /// 関数の外で宣言された、uniform ではない変数の型を集める。
    /// </summary>
    /// <param name="globals">関数の外の変数の宣言 (<see cref="CollectGlobalNames"/>)。</param>
    /// <returns>名前から型への対応。型が 1 つに決まらない名前は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>static</c> と <c>groupshared</c> の変数は uniform ではない。</b>
    /// マテリアルから値が入らないので uniform の一覧には載せない (<c>UniformCollector</c>)。
    /// ただし式の中では普通の変数として使われる。
    /// <c>static const float4 KERNEL_WEIGHTS = ...;</c> を関数の中で使うと、
    /// ここで型を引けなければ、その名前を含む式が軒並み検査されなかった。
    /// </para>
    /// <para>
    /// 同じ名前を違う型で宣言していれば、その名前の型は不明にする。
    /// 条件ごとに違う型で宣言した名前を構成ごとに決める仕組みは、今は関数の中にしか無い。
    /// </para>
    /// <para>
    /// uniform の一覧は変えない。<c>SL1001</c> などの対応検査が見るのはそちらである。
    /// </para>
    /// </remarks>
    private static FrozenDictionary<string, ResolvedTypeName?> CollectFileVariables(
        FrozenDictionary<string, ImmutableArray<(VariableDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator)>> globals)
    {
        Dictionary<string, ResolvedTypeName?> variables = new(StringComparer.Ordinal);

        foreach ((string name, ImmutableArray<(VariableDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator)> declared) in globals)
        {
            foreach ((VariableDeclarationSyntax declaration, VariableDeclaratorSyntax declarator) in declared)
            {
                if (!declaration.HasModifier("static") && !declaration.HasModifier("groupshared"))
                {
                    continue;
                }

                ResolvedTypeName type = new(declaration.Type.Name, declarator.IsArray, GetTemplateArgument(declaration.Type), declarator.ArrayDimensions);

                variables[name] = variables.TryGetValue(name, out ResolvedTypeName? existing) && existing != type
                    ? null
                    : type;
            }
        }

        return variables.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// 関数の外で宣言された変数を、名前ごとに集める。
    /// </summary>
    /// <param name="declarations">トップレベルの宣言。</param>
    /// <returns>名前から、その名前の宣言への対応。</returns>
    /// <remarks>
    /// uniform (定数バッファのメンバーを含む) と、<c>static</c> / <c>groupshared</c> の変数のどちらも入る。
    /// 名前空間の中の宣言もトップレベルと同じ扱いになる。
    /// 取り込んだヘッダの宣言も入る。関数の中から見える名前であることに変わりはない。
    /// </remarks>
    private static FrozenDictionary<string, ImmutableArray<(VariableDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator)>>
        CollectGlobalNames(ImmutableArray<HlslDeclarationSyntax> declarations)
    {
        Dictionary<string, List<(VariableDeclarationSyntax, VariableDeclaratorSyntax)>> names = new(StringComparer.Ordinal);

        void Collect(IEnumerable<HlslDeclarationSyntax> scope)
        {
            foreach (HlslDeclarationSyntax declaration in scope)
            {
                switch (declaration)
                {
                    case VariableDeclarationSyntax variable:
                        foreach (VariableDeclaratorSyntax declarator in variable.Variables)
                        {
                            if (!names.TryGetValue(declarator.Name, out List<(VariableDeclarationSyntax, VariableDeclaratorSyntax)>? list))
                            {
                                list = [];
                                names[declarator.Name] = list;
                            }

                            list.Add((variable, declarator));
                        }

                        break;

                    case ConstantBufferDeclarationSyntax buffer:
                        Collect(buffer.Members);
                        break;

                    case NamespaceDeclarationSyntax ns:
                        Collect(ns.Members);
                        break;

                    default:
                        break;
                }
            }
        }

        Collect(declarations);
        return names.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// 識別子が指している変数の宣言を求める。
    /// </summary>
    /// <param name="identifier">使っている識別子。</param>
    /// <returns>名前が何を指していたかと、指しうる宣言。</returns>
    /// <remarks>
    /// <para>
    /// <b>型を求めるときと同じ解決を使う。</b>
    /// 関数の中は、使っている位置から波括弧ごとに外側へたどる (<see cref="LocalScopes"/>)。
    /// 関数の中に宣言が無ければ、関数の外の変数を見る。
    /// </para>
    /// <para>
    /// 関数の中に、範囲を決められない場所で同じ名前が宣言されていれば
    /// <see cref="DeclaredNameKind.Undecidable"/> を返す。関数の外を見に行かない。
    /// 外にある同じ名前の宣言を指していると答えると、別の変数について判断することになる。
    /// </para>
    /// <para>
    /// 見ているのは、このコードブロック (この構成の木) の宣言だけである。
    /// 別の構成でしか宣言されていない名前は <see cref="DeclaredNameKind.NotFound"/> になる。
    /// </para>
    /// </remarks>
    public NameResolution ResolveName(IdentifierExpressionSyntax identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (identifier.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } function)
        {
            if (GetScopes(function).Lookup(identifier) is not { } locals)
            {
                return NameResolution.Undecidable;
            }

            if (!locals.IsEmpty)
            {
                return new NameResolution(
                    DeclaredNameKind.Local,
                    [.. locals.Select(local => new DeclaredName(local.Declarator, local.Type, local.Condition))]);
            }
        }

        if (!_globalNames.TryGetValue(identifier.Name, out ImmutableArray<(VariableDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator)> globals))
        {
            return NameResolution.NotFound;
        }

        ConditionMap conditions = _compilation.GetConditionMap();

        return new NameResolution(
            DeclaredNameKind.Global,
            [
                .. globals.Select(global => new DeclaredName(
                    global.Declarator,
                    new ResolvedTypeName(global.Declaration.Type.Name, global.Declarator.IsArray, GetTemplateArgument(global.Declaration.Type), global.Declarator.ArrayDimensions),
                    conditions.GetCondition(global.Declaration))),
            ]);
    }

    /// <summary>
    /// 型のテンプレート引数を取り出す。
    /// </summary>
    /// <param name="type">対象の型。</param>
    /// <returns>テンプレート引数の型名。無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>StructuredBuffer&lt;float4&gt;</c> から要素の型を得るために使う。
    /// 引数が 1 つの単純な形だけを扱う。
    /// </remarks>
    internal static string? GetTemplateArgument(HlslTypeSyntax type)
        => type.TemplateArguments is [{ Kind: HlslSyntaxKind.IdentifierToken } single]
            ? single.Text
            : null;
}
