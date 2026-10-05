using System.Collections.Immutable;

namespace Shaderlyn.Hlsl.Syntax;

/// <summary>
/// 型の記述。
/// </summary>
/// <param name="modifierTokens">型名の前に置かれた修飾子。</param>
/// <param name="nameToken">型名。</param>
/// <param name="qualifierTokens">名前空間の修飾を表すトークン。</param>
/// <param name="openAngle">テンプレート引数の開き山括弧。無い場合は <see langword="null"/>。</param>
/// <param name="templateArguments">テンプレート引数のトークン。カンマを含む。</param>
/// <param name="closeAngle">テンプレート引数の閉じ山括弧。無い場合は <see langword="null"/>。</param>
/// <param name="aliasOf">typedef の別名で書かれていれば、元の型の名前。</param>
/// <remarks>
/// <c>Texture2D&lt;float4&gt;</c> や <c>StructuredBuffer&lt;MyStruct&gt;</c> のテンプレート引数は
/// トークン列として保持する。入れ子のテンプレートや行列型の記述まで構文木にしても、
/// 現時点でそれを必要とするルールが無いためである。
/// </remarks>
public sealed class HlslTypeSyntax(
    ImmutableArray<HlslSyntaxToken> modifierTokens,
    HlslSyntaxToken nameToken,
    ImmutableArray<HlslSyntaxToken> qualifierTokens,
    HlslSyntaxToken? openAngle,
    ImmutableArray<HlslSyntaxToken> templateArguments,
    HlslSyntaxToken? closeAngle,
    string? aliasOf = null) : HlslSyntaxNode
{
    /// <summary>
    /// typedef の別名で書かれていれば、元の型の名前。そうでなければ <see langword="null"/>。
    /// </summary>
    public string? AliasOf { get; } = aliasOf;

    /// <summary>
    /// 型名の前に置かれた修飾子。
    /// </summary>
    /// <remarks>
    /// <c>(const int) x</c> や <c>(unsigned int) x</c> のように、
    /// 型変換の括弧の中にも現れる。
    /// <b>読み飛ばすのではなく木に載せる。</b>
    /// 捨てると、その範囲を指す位置が作れなくなる。
    /// </remarks>
    public ImmutableArray<HlslSyntaxToken> ModifierTokens { get; } = modifierTokens;

    /// <summary>型名のトークン。修飾がある場合は最後の要素。</summary>
    public HlslSyntaxToken NameToken { get; } = nameToken;

    /// <summary>修飾を除いた型名。</summary>
    /// <remarks>
    /// <c>vector&lt;float, 3&gt;</c> と <c>matrix&lt;half, 4, 4&gt;</c> は、同じ型の短い名前
    /// (<c>float3</c> / <c>half4x4</c>) にして返す。引数の無い <c>vector</c> / <c>matrix</c> は
    /// <c>float4</c> / <c>float4x4</c> である。型名で比べるすべての判定が、書き方の違いを知らずに済む。
    /// 引数を読み取れない形は、書かれたとおりの名前を返す。
    /// typedef の別名 (<c>typedef float3 V3;</c> の <c>V3</c>) は元の型の名前 (<c>float3</c>) を返す。
    /// 書かれたとおりの名前は <see cref="NameToken"/> にある。
    /// </remarks>
    public string Name => _name ??= AliasOf ?? NormalizeName();

    private string? _name;

    /// <summary>
    /// <c>vector</c> / <c>matrix</c> の書き方を短い名前にする。
    /// </summary>
    /// <returns>短い名前。当てはまらなければ書かれたとおりの名前。</returns>
    private string NormalizeName()
    {
        string name = NameToken.Text;
        bool isVector = name == "vector";

        if (!isVector && name != "matrix")
        {
            return name;
        }

        if (OpenAngle is null)
        {
            return isVector ? "float4" : "float4x4";
        }

        // vector<T, N> は 3 個、matrix<T, R, C> は 5 個のトークン (カンマを含む) になる。
        ImmutableArray<HlslSyntaxToken> arguments = TemplateArguments;
        int expected = isVector ? 3 : 5;

        // 要素の型はスカラーの基底名に限る (float4 のような名前が作れるもの)。
        if (arguments.Length != expected
            || !Parsing.HlslKeywords.BuiltInTypes.Contains(arguments[0].Text + "4"))
        {
            return name;
        }

        for (int i = 1; i < expected; i += 2)
        {
            if (arguments[i].Kind != HlslSyntaxKind.CommaToken
                || arguments[i + 1].Text is not { Length: 1 } digit
                || digit[0] is < '1' or > '4')
            {
                return name;
            }
        }

        return isVector
            ? arguments[0].Text + arguments[2].Text
            : arguments[0].Text + arguments[2].Text + "x" + arguments[4].Text;
    }

    /// <summary>
    /// 名前空間の修飾を表すトークン。<c>UnifiedRT</c> と <c>::</c> が交互に並ぶ。
    /// </summary>
    /// <remarks>
    /// HLSL 2021 の名前空間に対応するために必要である。
    /// Unity のレイトレーシング関連のコードが
    /// <c>UnifiedRT::InstanceData</c> のような修飾名を使っている。
    /// </remarks>
    public ImmutableArray<HlslSyntaxToken> QualifierTokens { get; } = qualifierTokens;

    /// <summary>修飾を含む完全な型名。</summary>
    public string QualifiedName => QualifierTokens.IsEmpty
        ? Name
        : string.Concat(QualifierTokens.Select(t => t.Text)) + Name;

    /// <summary>テンプレート引数の開き山括弧。</summary>
    public HlslSyntaxToken? OpenAngle { get; } = openAngle;

    /// <summary>テンプレート引数のトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> TemplateArguments { get; } = templateArguments;

    /// <summary>テンプレート引数の閉じ山括弧。</summary>
    public HlslSyntaxToken? CloseAngle { get; } = closeAngle;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslSyntaxToken modifier in ModifierTokens)
        {
            yield return modifier;
        }

        foreach (HlslSyntaxToken qualifier in QualifierTokens)
        {
            yield return qualifier;
        }

        yield return NameToken;

        if (OpenAngle is not null)
        {
            yield return OpenAngle;
        }

        foreach (HlslSyntaxToken token in TemplateArguments)
        {
            yield return token;
        }

        if (CloseAngle is not null)
        {
            yield return CloseAngle;
        }
    }
}

/// <summary>
/// <c>: SV_POSITION</c> や <c>: register(t0)</c> のような修飾。
/// </summary>
/// <param name="colonToken"><c>:</c>。</param>
/// <param name="nameToken">修飾の名前。</param>
/// <param name="openParen">開き括弧。引数が無い場合は <see langword="null"/>。</param>
/// <param name="argumentTokens">引数のトークン。</param>
/// <param name="closeParen">閉じ括弧。引数が無い場合は <see langword="null"/>。</param>
/// <remarks>
/// セマンティクス (<c>SV_Target</c>、<c>TEXCOORD0</c>) と
/// レジスタ指定 (<c>register(t0)</c>)、パックオフセット (<c>packoffset(c0)</c>) を
/// 同じ形で表す。構文が同一であり、区別は名前を見れば足りるためである。
/// </remarks>
public sealed class SemanticSyntax(
    HlslSyntaxToken colonToken,
    HlslSyntaxToken nameToken,
    HlslSyntaxToken? openParen,
    ImmutableArray<HlslSyntaxToken> argumentTokens,
    HlslSyntaxToken? closeParen) : HlslSyntaxNode
{
    /// <summary><c>:</c> のトークン。</summary>
    public HlslSyntaxToken ColonToken { get; } = colonToken;

    /// <summary>修飾の名前のトークン。</summary>
    public HlslSyntaxToken NameToken { get; } = nameToken;

    /// <summary>修飾の名前。</summary>
    public string Name => NameToken.Text;

    /// <summary>レジスタ指定などの引数を伴うかどうか。</summary>
    public bool HasArguments => OpenParen is not null;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken? OpenParen { get; } = openParen;

    /// <summary>引数のトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> ArgumentTokens { get; } = argumentTokens;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken? CloseParen { get; } = closeParen;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return ColonToken;
        yield return NameToken;

        if (OpenParen is not null)
        {
            yield return OpenParen;
        }

        foreach (HlslSyntaxToken token in ArgumentTokens)
        {
            yield return token;
        }

        if (CloseParen is not null)
        {
            yield return CloseParen;
        }
    }
}

/// <summary>
/// <c>[unroll]</c> や <c>[numthreads(8, 8, 1)]</c> のような属性。
/// </summary>
/// <param name="openBracket">開き角括弧。</param>
/// <param name="nameToken">属性名。</param>
/// <param name="openParen">開き括弧。引数が無い場合は <see langword="null"/>。</param>
/// <param name="argumentTokens">引数のトークン。</param>
/// <param name="closeParen">閉じ括弧。引数が無い場合は <see langword="null"/>。</param>
/// <param name="closeBracket">閉じ角括弧。</param>
public sealed class HlslAttributeSyntax(
    HlslSyntaxToken openBracket,
    HlslSyntaxToken nameToken,
    HlslSyntaxToken? openParen,
    ImmutableArray<HlslSyntaxToken> argumentTokens,
    HlslSyntaxToken? closeParen,
    HlslSyntaxToken closeBracket) : HlslSyntaxNode
{
    /// <summary>開き角括弧。</summary>
    public HlslSyntaxToken OpenBracket { get; } = openBracket;

    /// <summary>属性名のトークン。</summary>
    public HlslSyntaxToken NameToken { get; } = nameToken;

    /// <summary>属性名。</summary>
    public string Name => NameToken.Text;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken? OpenParen { get; } = openParen;

    /// <summary>引数のトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> ArgumentTokens { get; } = argumentTokens;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken? CloseParen { get; } = closeParen;

    /// <summary>閉じ角括弧。</summary>
    public HlslSyntaxToken CloseBracket { get; } = closeBracket;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenBracket;
        yield return NameToken;

        if (OpenParen is not null)
        {
            yield return OpenParen;
        }

        foreach (HlslSyntaxToken token in ArgumentTokens)
        {
            yield return token;
        }

        if (CloseParen is not null)
        {
            yield return CloseParen;
        }

        yield return CloseBracket;
    }
}

/// <summary>宣言の基底クラス。</summary>
public abstract class HlslDeclarationSyntax : HlslSyntaxNode;

/// <summary>ファイル全体を表す根ノード。</summary>
/// <param name="declarations">トップレベルの宣言。</param>
/// <param name="endOfFileToken">終端トークン。</param>
public sealed class HlslCompilationUnitSyntax(
    ImmutableArray<HlslDeclarationSyntax> declarations,
    HlslSyntaxToken endOfFileToken) : HlslSyntaxNode
{
    /// <summary>トップレベルの宣言。</summary>
    public ImmutableArray<HlslDeclarationSyntax> Declarations { get; } = declarations;

    /// <summary>終端トークン。</summary>
    public HlslSyntaxToken EndOfFileToken { get; } = endOfFileToken;

    /// <summary>
    /// typedef の別名から、元の型の名前への対応。
    /// </summary>
    /// <remarks>
    /// 型の記述 (<see cref="HlslTypeSyntax.Name"/>) は構文解析の時点で元の型の名前に置き換えてある。
    /// これは <c>V3(1, 2, 3)</c> のように、別名を関数のように呼んで値を作る式のためにある。
    /// </remarks>
    public System.Collections.Frozen.FrozenDictionary<string, string> TypeAliases { get; init; } =
        System.Collections.Frozen.FrozenDictionary<string, string>.Empty;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslDeclarationSyntax declaration in Declarations)
        {
            yield return declaration;
        }

        yield return EndOfFileToken;
    }
}

/// <summary>
/// 変数宣言 1 件の宣言子。
/// </summary>
/// <param name="nameToken">変数名。</param>
/// <param name="arrayRankTokens">配列の次元を表すトークン。角括弧と大きさの式を含む。</param>
/// <param name="semantics">セマンティクスやレジスタ指定。</param>
/// <param name="equalsToken">初期化子との間の等号。無い場合は <see langword="null"/>。</param>
/// <param name="initializer">初期化子。無い場合は <see langword="null"/>。</param>
/// <remarks>
/// 配列の大きさをトークン列として保持しているのは、
/// <c>float a[MAX_LIGHTS]</c> のようにマクロ由来の定数式が入りうるためである。
/// 式として解析することもできるが、現時点でそれを必要とするルールが無い。
/// </remarks>
public sealed class VariableDeclaratorSyntax(
    HlslSyntaxToken nameToken,
    ImmutableArray<HlslSyntaxToken> arrayRankTokens,
    ImmutableArray<SemanticSyntax> semantics,
    HlslSyntaxToken? equalsToken,
    HlslExpressionSyntax? initializer) : HlslSyntaxNode
{
    /// <summary>変数名のトークン。</summary>
    public HlslSyntaxToken NameToken { get; } = nameToken;

    /// <summary>変数名。</summary>
    public string Name => NameToken.Text;

    /// <summary>配列の次元を表すトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> ArrayRankTokens { get; } = arrayRankTokens;

    /// <summary>配列かどうか。</summary>
    public bool IsArray => !ArrayRankTokens.IsEmpty;

    /// <summary>配列の次元の数。配列でなければ 0。</summary>
    public int ArrayDimensions => HlslSyntaxFacts.CountArrayDimensions(ArrayRankTokens);

    /// <summary>配列の要素の総数を求める。</summary>
    /// <param name="length">要素の総数。</param>
    /// <returns>数として書かれていた場合は <see langword="true"/>。</returns>
    /// <remarks>多次元は次元の積になる。長さが式で書かれている場合は求まらない。</remarks>
    public bool TryGetArrayLength(out int length)
        => HlslSyntaxFacts.TryGetArrayLength(ArrayRankTokens, out length);

    /// <summary>セマンティクスやレジスタ指定。</summary>
    public ImmutableArray<SemanticSyntax> Semantics { get; } = semantics;

    /// <summary>初期化子との間の等号。</summary>
    public HlslSyntaxToken? EqualsToken { get; } = equalsToken;

    /// <summary>初期化子。</summary>
    public HlslExpressionSyntax? Initializer { get; } = initializer;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return NameToken;

        foreach (HlslSyntaxToken token in ArrayRankTokens)
        {
            yield return token;
        }

        foreach (SemanticSyntax semantic in Semantics)
        {
            yield return semantic;
        }

        if (EqualsToken is not null)
        {
            yield return EqualsToken;
        }

        if (Initializer is not null)
        {
            yield return Initializer;
        }
    }
}

/// <summary>
/// 変数宣言。<c>static const float4 a = 0, b;</c> のように複数の宣言子を持てる。
/// </summary>
/// <param name="modifierTokens"><c>static</c> や <c>const</c> などの修飾子。</param>
/// <param name="type">型。</param>
/// <param name="declarators">宣言子。</param>
/// <param name="semicolonToken">終端のセミコロン。</param>
public sealed class VariableDeclarationSyntax(
    ImmutableArray<HlslSyntaxToken> modifierTokens,
    HlslTypeSyntax type,
    ImmutableArray<HlslNodeOrTokenEntry> declarators,
    HlslSyntaxToken semicolonToken) : HlslDeclarationSyntax
{
    /// <summary>修飾子のトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> ModifierTokens { get; } = modifierTokens;

    /// <summary>型。</summary>
    public HlslTypeSyntax Type { get; } = type;

    /// <summary>宣言子。区切りのカンマを含む。</summary>
    public ImmutableArray<HlslNodeOrTokenEntry> Declarators { get; } = declarators;

    /// <summary>終端のセミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <summary>区切りのカンマを除いた宣言子。</summary>
    public IEnumerable<VariableDeclaratorSyntax> Variables
        => Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>();

    /// <summary>指定した修飾子が付いているかどうかを判定する。</summary>
    /// <param name="modifier">調べる修飾子。</param>
    /// <returns>付いている場合は <see langword="true"/>。</returns>
    public bool HasModifier(string modifier) => ModifierTokens.Any(t => t.TextIs(modifier));

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslSyntaxToken modifier in ModifierTokens)
        {
            yield return modifier;
        }

        yield return Type;

        foreach (HlslNodeOrTokenEntry declarator in Declarators)
        {
            yield return declarator.ToNodeOrToken();
        }

        yield return SemicolonToken;
    }
}

/// <summary>関数の仮引数。</summary>
/// <param name="modifierTokens"><c>in</c> / <c>out</c> / <c>inout</c> / <c>uniform</c> などの修飾子。</param>
/// <param name="type">型。</param>
/// <param name="nameToken">仮引数名。</param>
/// <param name="arrayRankTokens">配列の次元を表すトークン。</param>
/// <param name="semantics">セマンティクス。</param>
/// <param name="equalsToken">既定値との間の等号。無い場合は <see langword="null"/>。</param>
/// <param name="defaultValue">既定値。無い場合は <see langword="null"/>。</param>
public sealed class ParameterSyntax(
    ImmutableArray<HlslSyntaxToken> modifierTokens,
    HlslTypeSyntax type,
    HlslSyntaxToken nameToken,
    ImmutableArray<HlslSyntaxToken> arrayRankTokens,
    ImmutableArray<SemanticSyntax> semantics,
    HlslSyntaxToken? equalsToken,
    HlslExpressionSyntax? defaultValue) : HlslSyntaxNode
{
    /// <summary>修飾子のトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> ModifierTokens { get; } = modifierTokens;

    /// <summary>型。</summary>
    public HlslTypeSyntax Type { get; } = type;

    /// <summary>仮引数名のトークン。</summary>
    public HlslSyntaxToken NameToken { get; } = nameToken;

    /// <summary>仮引数名。</summary>
    public string Name => NameToken.Text;

    /// <summary>配列の次元を表すトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> ArrayRankTokens { get; } = arrayRankTokens;

    /// <summary>配列の仮引数かどうか。</summary>
    public bool IsArray => !ArrayRankTokens.IsEmpty;

    /// <summary>配列の次元の数。配列でなければ 0。</summary>
    public int ArrayDimensions => HlslSyntaxFacts.CountArrayDimensions(ArrayRankTokens);

    /// <summary>配列の要素の総数を求める。</summary>
    /// <param name="length">要素の総数。</param>
    /// <returns>数として書かれていた場合は <see langword="true"/>。</returns>
    /// <remarks>多次元は次元の積になる。長さが式で書かれている場合は求まらない。</remarks>
    public bool TryGetArrayLength(out int length)
        => HlslSyntaxFacts.TryGetArrayLength(ArrayRankTokens, out length);

    /// <summary>セマンティクス。</summary>
    public ImmutableArray<SemanticSyntax> Semantics { get; } = semantics;

    /// <summary>既定値との間の等号。</summary>
    public HlslSyntaxToken? EqualsToken { get; } = equalsToken;

    /// <summary>既定値。</summary>
    public HlslExpressionSyntax? DefaultValue { get; } = defaultValue;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslSyntaxToken modifier in ModifierTokens)
        {
            yield return modifier;
        }

        yield return Type;
        yield return NameToken;

        foreach (HlslSyntaxToken token in ArrayRankTokens)
        {
            yield return token;
        }

        foreach (SemanticSyntax semantic in Semantics)
        {
            yield return semantic;
        }

        if (EqualsToken is not null)
        {
            yield return EqualsToken;
        }

        if (DefaultValue is not null)
        {
            yield return DefaultValue;
        }
    }
}

/// <summary>関数の宣言または定義。</summary>
/// <param name="attributes">属性。</param>
/// <param name="modifierTokens">修飾子。</param>
/// <param name="returnType">戻り値の型。</param>
/// <param name="nameToken">関数名。</param>
/// <param name="openParen">開き括弧。</param>
/// <param name="parameters">仮引数。区切りのカンマを含む。</param>
/// <param name="closeParen">閉じ括弧。</param>
/// <param name="semantics">戻り値のセマンティクス。</param>
/// <param name="body">本体。プロトタイプ宣言の場合は <see langword="null"/>。</param>
/// <param name="semicolonToken">プロトタイプ宣言の終端のセミコロン。</param>
/// <param name="qualifierTokens">
/// 構造体の外で書いたメソッドの定義 (<c>uint Wave::GetIndex() { ... }</c>) の、関数名の前の修飾。
/// <c>Wave</c> と <c>::</c> が交互に並ぶ。ふつうの関数では空。
/// </param>
/// <param name="operatorTokens">
/// 演算子の多重定義 (HLSL 2021 の <c>Complex operator+(Complex other)</c>) の、<c>operator</c> に続く記号。
/// <paramref name="nameToken"/> は <c>operator</c> になる。ふつうの関数では空。
/// </param>
public sealed class FunctionDeclarationSyntax(
    ImmutableArray<HlslAttributeSyntax> attributes,
    ImmutableArray<HlslSyntaxToken> modifierTokens,
    HlslTypeSyntax returnType,
    HlslSyntaxToken nameToken,
    HlslSyntaxToken openParen,
    ImmutableArray<HlslNodeOrTokenEntry> parameters,
    HlslSyntaxToken closeParen,
    ImmutableArray<SemanticSyntax> semantics,
    BlockStatementSyntax? body,
    HlslSyntaxToken? semicolonToken,
    ImmutableArray<HlslSyntaxToken> qualifierTokens = default,
    ImmutableArray<HlslSyntaxToken> operatorTokens = default) : HlslDeclarationSyntax
{
    /// <summary>
    /// 関数名の前の修飾 (<c>Wave</c> と <c>::</c>)。ふつうの関数では空。
    /// </summary>
    /// <remarks>
    /// 修飾があるのは、構造体の中で宣言したメソッドを外で定義したものである。
    /// SRP の <c>ThreadingEmuImpl.hlsl</c> が <c>uint Wave::GetIndex() { return indexW; }</c> と書いていて、
    /// D3D11 でもコンパイルされる。グローバルな関数ではないので、関数の表には載せない。
    /// </remarks>
    public ImmutableArray<HlslSyntaxToken> QualifierTokens { get; } = qualifierTokens.IsDefault ? [] : qualifierTokens;

    /// <summary>構造体のメソッドを外で定義したものかどうか。</summary>
    public bool IsMethodDefinition => !QualifierTokens.IsEmpty;

    /// <summary>メソッドを持つ構造体の名前。メソッドの定義でなければ <see langword="null"/>。</summary>
    public string? OwnerName => QualifierTokens.Length >= 2 ? QualifierTokens[^2].Text : null;

    /// <summary>属性。</summary>
    public ImmutableArray<HlslAttributeSyntax> Attributes { get; } = attributes;

    /// <summary>修飾子のトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> ModifierTokens { get; } = modifierTokens;

    /// <summary>戻り値の型。</summary>
    public HlslTypeSyntax ReturnType { get; } = returnType;

    /// <summary>関数名のトークン。演算子の多重定義では <c>operator</c>。</summary>
    public HlslSyntaxToken NameToken { get; } = nameToken;

    /// <summary>
    /// 演算子の多重定義の、<c>operator</c> に続く記号 (<c>+</c>、<c>[</c> と <c>]</c> など)。ふつうの関数では空。
    /// </summary>
    public ImmutableArray<HlslSyntaxToken> OperatorTokens { get; } = operatorTokens.IsDefault ? [] : operatorTokens;

    /// <summary>演算子の多重定義かどうか。</summary>
    public bool IsOperator => !OperatorTokens.IsEmpty;

    /// <summary>関数名。演算子の多重定義では <c>operator+</c> のように記号を続けた名前。</summary>
    public string Name => IsOperator
        ? NameToken.Text + string.Concat(OperatorTokens.Select(t => t.Text))
        : NameToken.Text;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>仮引数。区切りのカンマを含む。</summary>
    public ImmutableArray<HlslNodeOrTokenEntry> Parameters { get; } = parameters;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>戻り値のセマンティクス。</summary>
    public ImmutableArray<SemanticSyntax> Semantics { get; } = semantics;

    /// <summary>本体。プロトタイプ宣言の場合は <see langword="null"/>。</summary>
    public BlockStatementSyntax? Body { get; } = body;

    /// <summary>プロトタイプ宣言の終端のセミコロン。</summary>
    public HlslSyntaxToken? SemicolonToken { get; } = semicolonToken;

    /// <summary>本体を持つ定義かどうか。</summary>
    public bool IsDefinition => Body is not null;

    /// <summary>区切りのカンマを除いた仮引数。</summary>
    public IEnumerable<ParameterSyntax> ParameterList
        => Parameters.Select(p => p.Node).OfType<ParameterSyntax>();

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslAttributeSyntax attribute in Attributes)
        {
            yield return attribute;
        }

        foreach (HlslSyntaxToken modifier in ModifierTokens)
        {
            yield return modifier;
        }

        yield return ReturnType;

        foreach (HlslSyntaxToken qualifier in QualifierTokens)
        {
            yield return qualifier;
        }

        yield return NameToken;

        foreach (HlslSyntaxToken symbol in OperatorTokens)
        {
            yield return symbol;
        }

        yield return OpenParen;

        foreach (HlslNodeOrTokenEntry parameter in Parameters)
        {
            yield return parameter.ToNodeOrToken();
        }

        yield return CloseParen;

        foreach (SemanticSyntax semantic in Semantics)
        {
            yield return semantic;
        }

        if (Body is not null)
        {
            yield return Body;
        }

        if (SemicolonToken is not null)
        {
            yield return SemicolonToken;
        }
    }
}

/// <summary>構造体の宣言。</summary>
/// <param name="keyword"><c>struct</c> キーワード。</param>
/// <param name="nameToken">構造体名。無名の場合は <see langword="null"/>。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="members">メンバー。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
/// <param name="declarators">構造体宣言に続く変数宣言子。</param>
/// <param name="semicolonToken">終端のセミコロン。</param>
public sealed class StructDeclarationSyntax(
    HlslSyntaxToken keyword,
    HlslSyntaxToken? nameToken,
    HlslSyntaxToken openBrace,
    ImmutableArray<HlslDeclarationSyntax> members,
    HlslSyntaxToken closeBrace,
    ImmutableArray<HlslNodeOrTokenEntry> declarators,
    HlslSyntaxToken semicolonToken) : HlslDeclarationSyntax
{
    /// <summary><c>struct</c> キーワード。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary>構造体名のトークン。</summary>
    public HlslSyntaxToken? NameToken { get; } = nameToken;

    /// <summary>構造体名。無名の場合は空文字列。</summary>
    public string Name => NameToken?.Text ?? string.Empty;

    /// <summary>開き波括弧。</summary>
    public HlslSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>メンバー。</summary>
    public ImmutableArray<HlslDeclarationSyntax> Members { get; } = members;

    /// <summary>閉じ波括弧。</summary>
    public HlslSyntaxToken CloseBrace { get; } = closeBrace;

    /// <summary>構造体宣言に続く変数宣言子。</summary>
    public ImmutableArray<HlslNodeOrTokenEntry> Declarators { get; } = declarators;

    /// <summary>終端のセミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <summary>メンバーのうち変数宣言であるもの。</summary>
    public IEnumerable<VariableDeclarationSyntax> Fields => Members.OfType<VariableDeclarationSyntax>();

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        if (NameToken is not null)
        {
            yield return NameToken;
        }

        yield return OpenBrace;

        foreach (HlslDeclarationSyntax member in Members)
        {
            yield return member;
        }

        yield return CloseBrace;

        foreach (HlslNodeOrTokenEntry declarator in Declarators)
        {
            yield return declarator.ToNodeOrToken();
        }

        yield return SemicolonToken;
    }
}

/// <summary>
/// <c>cbuffer</c> / <c>tbuffer</c> の宣言。
/// </summary>
/// <param name="keyword">キーワード。</param>
/// <param name="nameToken">バッファ名。</param>
/// <param name="semantics">レジスタ指定。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="members">メンバー。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
/// <param name="semicolonToken">終端のセミコロン。省略可能。</param>
/// <remarks>
/// URP における <c>CBUFFER_START(UnityPerMaterial)</c> はマクロであり、
/// 展開されると <c>cbuffer UnityPerMaterial {</c> になる。
/// SRP Batcher の互換性検査 (URP0001) はこのノードを対象に行う。
/// </remarks>
public sealed class ConstantBufferDeclarationSyntax(
    HlslSyntaxToken keyword,
    HlslSyntaxToken? nameToken,
    ImmutableArray<SemanticSyntax> semantics,
    HlslSyntaxToken openBrace,
    ImmutableArray<HlslDeclarationSyntax> members,
    HlslSyntaxToken closeBrace,
    HlslSyntaxToken? semicolonToken) : HlslDeclarationSyntax
{
    /// <summary>キーワードのトークン。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary>バッファ名のトークン。</summary>
    public HlslSyntaxToken? NameToken { get; } = nameToken;

    /// <summary>バッファ名。無名の場合は空文字列。</summary>
    public string Name => NameToken?.Text ?? string.Empty;

    /// <summary>レジスタ指定。</summary>
    public ImmutableArray<SemanticSyntax> Semantics { get; } = semantics;

    /// <summary>開き波括弧。</summary>
    public HlslSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>メンバー。</summary>
    public ImmutableArray<HlslDeclarationSyntax> Members { get; } = members;

    /// <summary>閉じ波括弧。</summary>
    public HlslSyntaxToken CloseBrace { get; } = closeBrace;

    /// <summary>終端のセミコロン。</summary>
    public HlslSyntaxToken? SemicolonToken { get; } = semicolonToken;

    /// <summary>メンバーのうち変数宣言であるもの。</summary>
    public IEnumerable<VariableDeclarationSyntax> Fields => Members.OfType<VariableDeclarationSyntax>();

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        if (NameToken is not null)
        {
            yield return NameToken;
        }

        foreach (SemanticSyntax semantic in Semantics)
        {
            yield return semantic;
        }

        yield return OpenBrace;

        foreach (HlslDeclarationSyntax member in Members)
        {
            yield return member;
        }

        yield return CloseBrace;

        if (SemicolonToken is not null)
        {
            yield return SemicolonToken;
        }
    }
}

/// <summary><c>typedef</c> の宣言。</summary>
/// <param name="keyword"><c>typedef</c> キーワード。</param>
/// <param name="tokens">型と別名を表すトークン。</param>
/// <param name="semicolonToken">終端のセミコロン。</param>
/// <remarks>
/// 内容をトークン列として保持するにとどめる。
/// typedef は解析対象のシェーダーでは稀にしか現れず、
/// 構文木にしても現時点で使う先が無い。
/// </remarks>
public sealed class TypedefDeclarationSyntax(
    HlslSyntaxToken keyword,
    ImmutableArray<HlslSyntaxToken> tokens,
    HlslSyntaxToken semicolonToken) : HlslDeclarationSyntax
{
    /// <summary><c>typedef</c> キーワード。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary>型と別名を表すトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> Tokens { get; } = tokens;

    /// <summary>終端のセミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        foreach (HlslSyntaxToken token in Tokens)
        {
            yield return token;
        }

        yield return SemicolonToken;
    }
}

/// <summary>
/// <c>namespace</c> の宣言。
/// </summary>
/// <param name="keyword"><c>namespace</c> キーワード。</param>
/// <param name="nameToken">名前空間の名前。無名の場合は <see langword="null"/>。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="members">中の宣言。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
/// <remarks>
/// HLSL 2021 で追加された機能で、Unity のレイトレーシング関連のコードが使っている。
/// 対応していないと、名前空間を含むファイルとそれを include するファイルが
/// まとめて解析できなくなる。
/// </remarks>
public sealed class NamespaceDeclarationSyntax(
    HlslSyntaxToken keyword,
    HlslSyntaxToken? nameToken,
    HlslSyntaxToken openBrace,
    ImmutableArray<HlslDeclarationSyntax> members,
    HlslSyntaxToken closeBrace) : HlslDeclarationSyntax
{
    /// <summary><c>namespace</c> キーワード。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary>名前空間の名前のトークン。</summary>
    public HlslSyntaxToken? NameToken { get; } = nameToken;

    /// <summary>名前空間の名前。無名の場合は空文字列。</summary>
    public string Name => NameToken?.Text ?? string.Empty;

    /// <summary>開き波括弧。</summary>
    public HlslSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>中の宣言。</summary>
    public ImmutableArray<HlslDeclarationSyntax> Members { get; } = members;

    /// <summary>閉じ波括弧。</summary>
    public HlslSyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        if (NameToken is not null)
        {
            yield return NameToken;
        }

        yield return OpenBrace;

        foreach (HlslDeclarationSyntax member in Members)
        {
            yield return member;
        }

        yield return CloseBrace;
    }
}

/// <summary>
/// <c>enum</c> の宣言 (HLSL 2021)。
/// </summary>
/// <param name="keyword"><c>enum</c> キーワード。</param>
/// <param name="scopeKeyword"><c>enum class</c> / <c>enum struct</c> の <c>class</c> / <c>struct</c>。無ければ <see langword="null"/>。</param>
/// <param name="nameToken">型の名前。無名の場合は <see langword="null"/>。</param>
/// <param name="baseTypeTokens">基になる型の指定 (<c>: uint</c>) のトークン。無ければ空。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="members">列挙子。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
/// <param name="semicolonToken">セミコロン。</param>
/// <remarks>
/// <para>
/// <b>スコープの無い列挙子 (<c>enum Mode { A }</c>) は、外側の名前として見える。</b>
/// <c>A</c> とも <c>Mode::A</c> とも書ける。<c>enum class</c> の列挙子は <c>Mode::A</c> とだけ書ける。
/// </para>
/// <para>
/// HLSL 2018 (Unity) では使えない。パーサーは HLSL 2021 のときだけこのノードを作り、2018 では HL0003 を報告して読み飛ばす。
/// </para>
/// </remarks>
public sealed class EnumDeclarationSyntax(
    HlslSyntaxToken keyword,
    HlslSyntaxToken? scopeKeyword,
    HlslSyntaxToken? nameToken,
    ImmutableArray<HlslSyntaxToken> baseTypeTokens,
    HlslSyntaxToken openBrace,
    ImmutableArray<EnumMemberSyntax> members,
    HlslSyntaxToken closeBrace,
    HlslSyntaxToken semicolonToken) : HlslDeclarationSyntax
{
    /// <summary><c>enum</c> キーワード。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary><c>enum class</c> / <c>enum struct</c> の後ろのキーワード。無ければ <see langword="null"/>。</summary>
    public HlslSyntaxToken? ScopeKeyword { get; } = scopeKeyword;

    /// <summary>列挙子が型の名前で修飾しないと見えない (<c>enum class</c>) か。</summary>
    public bool IsScoped => ScopeKeyword is not null;

    /// <summary>型の名前のトークン。無名の場合は <see langword="null"/>。</summary>
    public HlslSyntaxToken? NameToken { get; } = nameToken;

    /// <summary>型の名前。無名の場合は空文字列。</summary>
    public string Name => NameToken?.Text ?? string.Empty;

    /// <summary>基になる型の指定 (<c>: uint</c>) のトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> BaseTypeTokens { get; } = baseTypeTokens;

    /// <summary>開き波括弧。</summary>
    public HlslSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>列挙子。</summary>
    public ImmutableArray<EnumMemberSyntax> Members { get; } = members;

    /// <summary>閉じ波括弧。</summary>
    public HlslSyntaxToken CloseBrace { get; } = closeBrace;

    /// <summary>セミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        if (ScopeKeyword is not null)
        {
            yield return ScopeKeyword;
        }

        if (NameToken is not null)
        {
            yield return NameToken;
        }

        foreach (HlslSyntaxToken token in BaseTypeTokens)
        {
            yield return token;
        }

        yield return OpenBrace;

        foreach (EnumMemberSyntax member in Members)
        {
            yield return member;
        }

        yield return CloseBrace;
        yield return SemicolonToken;
    }
}

/// <summary>列挙子 1 つ。</summary>
/// <param name="nameToken">名前。</param>
/// <param name="equalsToken">値を与える <c>=</c>。無ければ <see langword="null"/>。</param>
/// <param name="value">値の式。無ければ <see langword="null"/>。</param>
/// <param name="commaToken">後ろのカンマ。最後の列挙子では無いことがある。</param>
public sealed class EnumMemberSyntax(
    HlslSyntaxToken nameToken,
    HlslSyntaxToken? equalsToken,
    HlslExpressionSyntax? value,
    HlslSyntaxToken? commaToken) : HlslSyntaxNode
{
    /// <summary>名前のトークン。</summary>
    public HlslSyntaxToken NameToken { get; } = nameToken;

    /// <summary>名前。</summary>
    public string Name => NameToken.Text;

    /// <summary>値を与える <c>=</c>。</summary>
    public HlslSyntaxToken? EqualsToken { get; } = equalsToken;

    /// <summary>値の式。</summary>
    public HlslExpressionSyntax? Value { get; } = value;

    /// <summary>後ろのカンマ。</summary>
    public HlslSyntaxToken? CommaToken { get; } = commaToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return NameToken;

        if (EqualsToken is not null)
        {
            yield return EqualsToken;
        }

        if (Value is not null)
        {
            yield return Value;
        }

        if (CommaToken is not null)
        {
            yield return CommaToken;
        }
    }
}

/// <summary>解釈できなかった宣言。</summary>
/// <param name="tokens">読み飛ばしたトークン。</param>
public sealed class IncompleteDeclarationSyntax(ImmutableArray<HlslSyntaxToken> tokens) : HlslDeclarationSyntax
{
    /// <summary>読み飛ばしたトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> Tokens { get; } = tokens;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslSyntaxToken token in Tokens)
        {
            yield return token;
        }
    }
}

/// <summary>
/// <c>template</c> の宣言 (HLSL 2021)。<c>template&lt;typename T&gt;</c> に続く関数か構造体を持つ。
/// </summary>
/// <param name="keyword"><c>template</c> キーワード。</param>
/// <param name="openAngle">型引数の並びの開き山括弧。</param>
/// <param name="parameterTokens">型引数の並び (<c>typename T, uint N = 4</c>) のトークン。カンマを含む。</param>
/// <param name="closeAngle">型引数の並びの閉じ山括弧。</param>
/// <param name="declaration">型引数を使う宣言 (関数か構造体)。</param>
/// <remarks>
/// 型引数 (<c>T</c>) を含む型は、使う側で何に置き換わるかが決まるまで分からない。
/// 検査はそうした型を「分からない型」として扱う。
/// </remarks>
public sealed class TemplateDeclarationSyntax(
    HlslSyntaxToken keyword,
    HlslSyntaxToken openAngle,
    ImmutableArray<HlslSyntaxToken> parameterTokens,
    HlslSyntaxToken closeAngle,
    HlslDeclarationSyntax declaration) : HlslDeclarationSyntax
{
    /// <summary><c>template</c> キーワード。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary>型引数の並びの開き山括弧。</summary>
    public HlslSyntaxToken OpenAngle { get; } = openAngle;

    /// <summary>型引数の並びのトークン。カンマを含む。</summary>
    public ImmutableArray<HlslSyntaxToken> ParameterTokens { get; } = parameterTokens;

    /// <summary>型引数の並びの閉じ山括弧。</summary>
    public HlslSyntaxToken CloseAngle { get; } = closeAngle;

    /// <summary>型引数を使う宣言 (関数か構造体)。</summary>
    public HlslDeclarationSyntax Declaration { get; } = declaration;

    /// <summary>型引数の名前 (<c>typename T, uint N</c> の <c>T</c> と <c>N</c>)。</summary>
    /// <remarks>カンマで区切った 1 つずつの、既定値 (<c>=</c> の後ろ) より前にある最後の名前である。</remarks>
    public ImmutableArray<string> ParameterNames { get; } = ReadParameterNames(parameterTokens);

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return OpenAngle;

        foreach (HlslSyntaxToken token in ParameterTokens)
        {
            yield return token;
        }

        yield return CloseAngle;
        yield return Declaration;
    }

    /// <summary>型引数の並びから、型引数の名前を読む。</summary>
    /// <param name="tokens">型引数の並びのトークン。</param>
    /// <returns>型引数の名前。</returns>
    private static ImmutableArray<string> ReadParameterNames(ImmutableArray<HlslSyntaxToken> tokens)
    {
        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();
        string? last = null;
        bool inDefault = false;
        int depth = 0;

        foreach (HlslSyntaxToken token in tokens)
        {
            switch (token.Kind)
            {
                case HlslSyntaxKind.LessThanToken:
                    depth++;
                    break;

                case HlslSyntaxKind.GreaterThanToken:
                    depth--;
                    break;

                case HlslSyntaxKind.CommaToken when depth == 0:
                    if (last is not null)
                    {
                        names.Add(last);
                    }

                    last = null;
                    inDefault = false;
                    break;

                case HlslSyntaxKind.EqualsToken when depth == 0:
                    inDefault = true;
                    break;

                case HlslSyntaxKind.IdentifierToken when depth == 0 && !inDefault:
                    last = token.Text;
                    break;

                default:
                    break;
            }
        }

        if (last is not null)
        {
            names.Add(last);
        }

        return names.ToImmutable();
    }
}
