using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Semantics.Programs;

/// <summary>名前が何を指していたか。</summary>
public enum DeclaredNameKind
{
    /// <summary>関数の中にも関数の外にも、その名前の変数の宣言が無い。</summary>
    NotFound,

    /// <summary>関数の中の変数か仮引数を指す。</summary>
    Local,

    /// <summary>関数の外の変数 (uniform、<c>static</c> / <c>groupshared</c> の変数) を指す。</summary>
    Global,

    /// <summary>
    /// 判断できない。関数の中に、範囲を決められない場所で同じ名前が宣言されている
    /// (波括弧を書かない <c>if (x) float a;</c> など)。
    /// </summary>
    Undecidable,
}

/// <summary>名前が指しうる宣言 1 つ。</summary>
/// <param name="Declarator">
/// 宣言子 (<see cref="VariableDeclaratorSyntax"/>) か、仮引数 (<see cref="ParameterSyntax"/>)。
/// </param>
/// <param name="Type">宣言した型。</param>
/// <param name="Condition">
/// 使っている位置からこの宣言が見える条件。
/// 関数の中の宣言なら、内側の同じ名前の宣言が無いことも掛け合わせてある。
/// </param>
public readonly record struct DeclaredName(HlslSyntaxNode Declarator, ResolvedTypeName Type, SymbolCondition Condition)
{
    /// <summary>配列の要素の総数を求める。</summary>
    /// <param name="length">要素の総数。</param>
    /// <returns>配列で、長さが数として書かれていた場合は <see langword="true"/>。</returns>
    public bool TryGetArrayLength(out int length)
    {
        switch (Declarator)
        {
            case VariableDeclaratorSyntax variable:
                return variable.TryGetArrayLength(out length);

            case ParameterSyntax parameter:
                return parameter.TryGetArrayLength(out length);

            default:
                length = 0;
                return false;
        }
    }
}

/// <summary>名前を解決した結果。</summary>
/// <param name="Kind">名前が何を指していたか。</param>
/// <param name="Candidates">
/// 指しうる宣言。<see cref="DeclaredNameKind.Local"/> と <see cref="DeclaredNameKind.Global"/> のときだけ中身がある。
/// 条件ごとに違う宣言があれば複数になる。
/// </param>
/// <remarks>
/// <para>
/// 型を求める仕組み (<see cref="ExpressionTypeBinder"/>) と、名前の宣言を見るルールは、同じ解決を使う。
/// ルールごとに名前の引き方が違うと、同じ名前を片方は局所変数、片方はグローバル変数と読むことになる。
/// </para>
/// <para>
/// <see cref="DeclaredNameKind.Undecidable"/> と <see cref="DeclaredNameKind.NotFound"/> を取り違えないこと。
/// 前者は「分からない」であり、それを根拠に報告してはならない。
/// </para>
/// </remarks>
public readonly record struct NameResolution(DeclaredNameKind Kind, ImmutableArray<DeclaredName> Candidates)
{
    /// <summary>その名前の変数の宣言が無い。</summary>
    public static NameResolution NotFound { get; } = new(DeclaredNameKind.NotFound, []);

    /// <summary>判断できない。</summary>
    public static NameResolution Undecidable { get; } = new(DeclaredNameKind.Undecidable, []);

    /// <summary>
    /// 候補のどれもが同じ長さの配列なら、その長さを求める。
    /// </summary>
    /// <param name="length">配列の要素の総数。</param>
    /// <returns>候補が 1 つ以上あり、どれも同じ長さの配列だった場合は <see langword="true"/>。</returns>
    public bool TryGetArrayLength(out int length)
    {
        length = 0;

        if (Candidates.IsDefaultOrEmpty)
        {
            return false;
        }

        int? found = null;

        foreach (DeclaredName candidate in Candidates)
        {
            if (!candidate.TryGetArrayLength(out int each) || (found is { } known && known != each))
            {
                return false;
            }

            found = each;
        }

        length = found ?? 0;
        return found is not null;
    }
}
