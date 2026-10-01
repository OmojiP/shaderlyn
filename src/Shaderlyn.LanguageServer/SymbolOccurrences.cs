using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 名前が現れる場所をまとめて探す。
/// </summary>
/// <remarks>
/// <para>
/// <b>参照の一覧と名前の変更は、同じ探索の上に立つ。</b>
/// 「この名前を指している場所はどこか」を 1 か所で決めておかないと、
/// 一覧に出た場所と書き換わる場所が食い違う。
/// </para>
/// <para>
/// <b>探すのは展開前のトークン列である。</b>
/// 展開後の構文木では、マクロから生まれた名前が呼び出し位置に重なっており、
/// カーソルの下にある語と一致しない。
/// </para>
/// </remarks>
internal static class SymbolOccurrences
{
    /// <summary>
    /// カーソルの下の名前が現れる場所をすべて返す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>現れる場所。名前の上に無い場合は空。</returns>
    public static ImmutableArray<TextSpan> Find(ShaderCompilation compilation, int offset)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        if (CursorTarget.FindIdentifier(compilation, offset) is not { } name)
        {
            return [];
        }

        // 局所的な名前なら、その関数の中だけを見る。
        // 別の関数の同名の変数まで書き換えると、関係のないコードを壊す。
        TextSpan scope = FindScope(compilation, name);

        return
        [
            .. compilation.CodeTokens
                .Where(t => t.Kind == HlslSyntaxKind.IdentifierToken
                            && string.Equals(t.Text, name.Text, StringComparison.Ordinal)
                            && t.Span.Start >= scope.Start
                            && t.Span.End <= scope.End)
                .Select(t => t.Span),
        ];
    }

    /// <summary>
    /// カーソルの下の名前を返す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>名前。名前の上に無い場合は <see langword="null"/>。</returns>
    public static string? FindName(ShaderCompilation compilation, int offset)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        return CursorTarget.FindIdentifier(compilation, offset)?.Text;
    }

    /// <summary>
    /// その名前を探す範囲を決める。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">対象の名前。</param>
    /// <returns>探す範囲。</returns>
    /// <remarks>
    /// <para>
    /// <b>関数の中で宣言された名前は、その関数の中だけを見る。</b>
    /// 名前の範囲を追わずにファイル全体を書き換えると、
    /// 別の関数の同名の変数まで巻き込む。
    /// </para>
    /// <para>
    /// uniform や関数のようにファイル全体から見える名前は、ファイル全体を見る。
    /// </para>
    /// </remarks>
    private static TextSpan FindScope(ShaderCompilation compilation, HlslSyntaxToken name)
    {
        TextSpan whole = new(0, compilation.Text.Content.Length);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is not FunctionDeclarationSyntax function
                    || !compilation.IsWrittenHere(function)
                    || name.Span.Start < function.Span.Start
                    || name.Span.End > function.Span.End)
                {
                    continue;
                }

                return DeclaresLocally(function, name.Text) ? function.Span : whole;
            }
        }

        return whole;
    }

    /// <summary>その関数がその名前を自分で宣言しているかを判定する。</summary>
    /// <param name="function">対象の関数。</param>
    /// <param name="name">探す名前。</param>
    /// <returns>宣言していれば <see langword="true"/>。</returns>
    private static bool DeclaresLocally(FunctionDeclarationSyntax function, string name)
    {
        foreach (SyntaxNode node in function.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ParameterSyntax parameter when parameter.Name == name:
                    return true;

                case VariableDeclarationSyntax declaration
                    when declaration.Variables.Any(v => v.Name == name):
                    return true;

                default:
                    break;
            }
        }

        return false;
    }
}
