using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Rules.Glsl;

/// <summary><c>layout(…)</c> の中の 1 つ (<c>std140</c> や <c>location = 2</c>)。</summary>
/// <param name="Name">名前のトークン。</param>
/// <param name="Value">値の式のトークン (<c>=</c> より後ろ)。値が無ければ空。</param>
/// <param name="HasValue"><c>=</c> を書いたか。</param>
internal readonly record struct GlslLayoutEntry(ShaderSyntaxToken Name, ImmutableArray<ShaderSyntaxToken> Value, bool HasValue);

/// <summary>
/// 修飾子のトークン (<c>layout(std140, binding = 2) uniform</c>) から <c>layout(…)</c> の中身を取り出し、値の式を畳む。
/// </summary>
/// <remarks>
/// GLSL の構文木は <c>layout(…)</c> を括弧ごと修飾子のトークンとして持ち、中の式を構文木にしない。
/// そのため値の式は、ここでトークンから読んで畳む (<see cref="Evaluate"/>)。
/// </remarks>
internal static class GlslLayoutQualifier
{
    /// <summary>修飾子のトークンから、<c>layout(…)</c> の中の 1 つずつを取り出す。</summary>
    /// <param name="modifiers">修飾子のトークン。</param>
    /// <returns><c>layout(…)</c> の中の 1 つずつ。</returns>
    public static IEnumerable<GlslLayoutEntry> Read(ImmutableArray<ShaderSyntaxToken> modifiers)
    {
        for (int i = 0; i < modifiers.Length; i++)
        {
            if (modifiers[i].Text != "layout" || i + 1 >= modifiers.Length || modifiers[i + 1].Kind != ShaderSyntaxKind.OpenParenToken)
            {
                continue;
            }

            int depth = 0;
            int start = i + 2;

            for (i += 1; i < modifiers.Length; i++)
            {
                ShaderSyntaxKind kind = modifiers[i].Kind;

                if (kind == ShaderSyntaxKind.OpenParenToken)
                {
                    depth++;
                }
                else if (kind == ShaderSyntaxKind.CloseParenToken && --depth == 0
                         || kind == ShaderSyntaxKind.CommaToken && depth == 1)
                {
                    if (Entry(modifiers[start..i]) is { } entry)
                    {
                        yield return entry;
                    }

                    start = i + 1;

                    if (depth == 0)
                    {
                        break;
                    }
                }
            }
        }
    }

    private static GlslLayoutEntry? Entry(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        if (tokens is not [{ Kind: ShaderSyntaxKind.IdentifierToken } name, ..])
        {
            return null;
        }

        return tokens is [_, { Kind: ShaderSyntaxKind.EqualsToken }, ..]
            ? new GlslLayoutEntry(name, tokens[2..], true)
            : new GlslLayoutEntry(name, [], false);
    }

    /// <summary>値の式のトークンを畳む。</summary>
    /// <param name="tokens">式のトークン。</param>
    /// <param name="resolve">名前の値 (<c>const</c> の変数)。分からなければ <see langword="null"/>。</param>
    /// <returns>値。畳めなければ <see langword="null"/>。</returns>
    public static GlslConstant? Evaluate(ImmutableArray<ShaderSyntaxToken> tokens, Func<ShaderSyntaxToken, GlslConstant?> resolve)
    {
        Reader reader = new(tokens, resolve);
        GlslConstant? value = reader.Conditional();
        return reader.AtEnd ? value : null;
    }

    /// <summary>トークンの並びを、演算子の優先順位どおりに読んで畳む。</summary>
    private sealed class Reader(ImmutableArray<ShaderSyntaxToken> tokens, Func<ShaderSyntaxToken, GlslConstant?> resolve)
    {
        private int _position;

        public bool AtEnd => _position >= tokens.Length;

        private ShaderSyntaxKind Current => AtEnd ? ShaderSyntaxKind.EndOfFileToken : tokens[_position].Kind;

        public GlslConstant? Conditional()
        {
            GlslConstant? condition = Binary(0);

            if (Current != ShaderSyntaxKind.QuestionToken)
            {
                return condition;
            }

            _position++;
            GlslConstant? whenTrue = Conditional();

            if (Current != ShaderSyntaxKind.ColonToken)
            {
                return null;
            }

            _position++;
            GlslConstant? whenFalse = Conditional();
            return condition is { Kind: GlslConstantKind.Bool } c && whenTrue is { } t && whenFalse is { } f
                ? (c.Value != 0 ? t : f) with { IsLiteral = false }
                : null;
        }

        private GlslConstant? Binary(int minimum)
        {
            GlslConstant? left = Unary();

            while (Precedence(Current) is { } precedence && precedence >= minimum)
            {
                ShaderSyntaxKind kind = Current;
                _position++;
                GlslConstant? right = Binary(precedence + 1);
                left = left is { } l && right is { } r ? GlslConstantEvaluator.ApplyBinary(kind, l, r) : null;
            }

            return left;
        }

        private GlslConstant? Unary()
        {
            ShaderSyntaxKind kind = Current;

            if (kind is ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.ExclamationToken or ShaderSyntaxKind.TildeToken)
            {
                _position++;
                return Unary() is { } operand ? GlslConstantEvaluator.ApplyUnary(kind, operand) : null;
            }

            return Primary();
        }

        private GlslConstant? Primary()
        {
            if (AtEnd)
            {
                return null;
            }

            ShaderSyntaxToken token = tokens[_position++];

            if (token.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                GlslConstant? inner = Conditional();

                if (Current != ShaderSyntaxKind.CloseParenToken)
                {
                    return null;
                }

                _position++;
                return inner is { } value ? value with { IsLiteral = false } : null;
            }

            if (GlslConstantEvaluator.EvaluateLiteral(token) is { } literal)
            {
                return literal;
            }

            if (token.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                return null;
            }

            // int(x) など。
            if (Current == ShaderSyntaxKind.OpenParenToken && token.Text is "int" or "uint" or "bool" or "float" or "double")
            {
                _position++;
                GlslConstant? argument = Conditional();

                if (Current != ShaderSyntaxKind.CloseParenToken)
                {
                    return null;
                }

                _position++;
                return argument is { } value ? GlslConstantEvaluator.Construct(token.Text, value) : null;
            }

            return resolve(token) is { } resolved ? resolved with { IsLiteral = false } : null;
        }

        /// <summary>二項の演算子の優先順位。大きいほど強く結び付く。</summary>
        private static int? Precedence(ShaderSyntaxKind kind) => kind switch
        {
            ShaderSyntaxKind.BarBarToken => 0,
            ShaderSyntaxKind.CaretCaretToken => 1,
            ShaderSyntaxKind.AmpersandAmpersandToken => 2,
            ShaderSyntaxKind.BarToken => 3,
            ShaderSyntaxKind.CaretToken => 4,
            ShaderSyntaxKind.AmpersandToken => 5,
            ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken => 6,
            ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.GreaterThanToken
                or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanEqualsToken => 7,
            ShaderSyntaxKind.LessThanLessThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken => 8,
            ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken => 9,
            ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken or ShaderSyntaxKind.PercentToken => 10,
            _ => null,
        };
    }
}
