using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// カーソルの下の要素についての説明。
/// </summary>
/// <param name="Markdown">表示する内容。</param>
/// <param name="Span">対応するソース上の範囲。</param>
internal readonly record struct HoverResult(string Markdown, TextSpan Span);

/// <summary>
/// カーソルの下の要素について答える。
/// </summary>
/// <remarks>
/// <para>
/// <b>「型を判定できません」と答えてよいのは、値を表す式だけである。</b>
/// 型名や宣言の上でそう答えると、
/// 解析が壊れているという印象だけを与えて何の役にも立たない。
/// 何であるかが分かる要素は、分かる形で答える。
/// </para>
/// <para>
/// 値を表す式については、判定できなかったことも答える。
/// 型を根拠にするルールが何も報告しないとき、
/// 原因のほとんどはその式の型を判定できなかったことであり、
/// 指摘が出ないという結果だけからは
/// 「問題が無い」のか「判断できていない」のか区別が付かない。
/// </para>
/// </remarks>
internal static partial class HoverBuilder
{
    /// <summary>
    /// 指定した位置の説明を組み立てる。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。答えるものが無い場合は <see langword="null"/>。</returns>
    public static HoverResult? Build(ShaderCompilation compilation, int offset)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        return BuildForHlsl(compilation, offset)
               ?? BuildForPragma(compilation, offset)
               ?? BuildForMacro(compilation, offset)
               ?? BuildForProperty(compilation, offset);
    }

    /// <summary>
    /// 埋め込み HLSL の要素について答える。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。答えるものが無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 最も内側のノードから外へ辿り、説明できるものが見つかった時点で止める。
    /// 内側ほど利用者が指したものに近い。
    /// </remarks>
    private static HoverResult? BuildForHlsl(ShaderCompilation compilation, int offset)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (FindInnermost(compilation, program, offset) is not { } node)
            {
                continue;
            }

            ExpressionTypeBinder binder = new(compilation, program);

            for (SyntaxNode? current = node; current is not null; current = current.Parent)
            {
                if (Describe(compilation, program, binder, current, offset) is { } described)
                {
                    return WithCondition(compilation, current, described);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// カーソルの下にある最も内側のノードを探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象のコードブロック。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>見つかったノード。無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>範囲の内側にあることを、端に触れていることより優先する。</b>
    /// <c>uv.x</c> の <c>.</c> は <c>uv</c> の終端でもある。
    /// 短いほうを選ぶ規則だけで決めると、<c>.</c> の上で <c>uv</c> を答えることになり、
    /// 利用者が見ている語と食い違う。
    /// </remarks>
    private static HlslSyntaxNode? FindInnermost(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        int offset)
    {
        HlslSyntaxNode? inside = null;
        HlslSyntaxNode? touching = null;

        foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
        {
            if (node is not HlslSyntaxNode hlsl
                || hlsl.Span.Length == 0
                || offset < hlsl.Span.Start
                || offset > hlsl.Span.End
                || !compilation.IsWrittenHere(hlsl))
            {
                continue;
            }

            if (offset < hlsl.Span.End)
            {
                if (inside is null || hlsl.Span.Length <= inside.Span.Length)
                {
                    inside = hlsl;
                }
            }
            else if (touching is null || hlsl.Span.Length <= touching.Span.Length)
            {
                touching = hlsl;
            }
        }

        return inside ?? touching;
    }

    /// <summary>
    /// ノード 1 つを説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象のコードブロック。</param>
    /// <param name="binder">型の評価器を用意する束縛器。</param>
    /// <param name="node">説明するノード。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。このノードでは答えられない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 宣言のほうを式より先に見る。
    /// <c>half4 _BaseColor;</c> の <c>half4</c> は型であって、型が不明な式ではない。
    /// </remarks>
    private static HoverResult? Describe(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        ExpressionTypeBinder binder,
        SyntaxNode node,
        int offset) => node switch
    {
        HlslTypeSyntax type => DescribeType(type.Name, type.Span),

        // セマンティクスは「何が入ってくるか」「どこへ出るか」を決める。
        // 名前を覚えていないと読めない部分であり、説明する価値が最も高い。
        SemanticSyntax semantic when Touches(semantic.NameToken, offset)
            => DescribeSemantic(semantic),

        VariableDeclarationSyntax declaration when TouchesDeclarator(declaration, offset) is { } declarator
            => DescribeVariable(compilation, declaration, declarator),

        ParameterSyntax parameter when Touches(parameter.NameToken, offset)
            => DescribeParameter(parameter),

        FunctionDeclarationSyntax function when Touches(function.NameToken, offset)
            => DescribeFunction(function),

        StructDeclarationSyntax { NameToken: { } structName } structure when Touches(structName, offset)
            => DescribeStruct(structure, structName),

        ConstantBufferDeclarationSyntax { NameToken: { } bufferName } buffer when Touches(bufferName, offset)
            => DescribeConstantBuffer(compilation, buffer, bufferName),

        IdentifierExpressionSyntax identifier when IsCallee(identifier)
            => DescribeCallee(program, identifier),

        // 代入と、構文エラーから作られた式は値として評価しない。
        // ここで「型を判定できません」と答えると、
        // 評価しようとして失敗したかのように読めてしまう。
        AssignmentExpressionSyntax or IncompleteExpressionSyntax => null,

        HlslExpressionSyntax expression => DescribeExpression(compilation, binder, expression),

        _ => null,
    };

    /// <summary>
    /// 説明に、その要素が存在する条件を添える。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="node">説明した要素。</param>
    /// <param name="described">組み立てた説明。</param>
    /// <returns>条件を添えた説明。</returns>
    /// <remarks>
    /// <para>
    /// <b>条件付きの要素は、そう見えなければならない。</b>
    /// <c>#ifdef</c> で囲まれた宣言は 1 本の木の中にあり、
    /// 何も断らなければ無条件の宣言と区別が付かない。
    /// 「ここにあるのだから使える」と読まれると、
    /// 別の構成で壊れるコードがそのまま書かれる。
    /// </para>
    /// <para>
    /// 条件を追えなかった箇所も、そう答える。
    /// 何も伝えないと、無条件だと受け取られる。
    /// </para>
    /// </remarks>
    private static HoverResult WithCondition(
        ShaderCompilation compilation,
        SyntaxNode node,
        HoverResult described)
    {
        if (node is not HlslSyntaxNode hlsl)
        {
            return described;
        }

        SymbolCondition condition = compilation.GetConditionMap().GetCondition(hlsl);

        if (condition.IsAlways)
        {
            return described;
        }

        string note = condition.IsUnknown
            ? "この箇所の `#ifdef` の条件は追えていません。"
            : $"この構成でだけ存在します: `#if {condition}`";

        return described with { Markdown = $"{described.Markdown}\n\n---\n\n{note}" };
    }

    /// <summary>トークンがカーソルの下にあるかを判定する。</summary>
    /// <param name="token">対象のトークン。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>下にある場合は <see langword="true"/>。</returns>
    private static bool Touches(HlslSyntaxToken token, int offset)
        => CursorTarget.Touches(token.Span, offset);

    /// <summary>宣言の中で、カーソルの下にある宣言子を探す。</summary>
    /// <param name="declaration">対象の宣言。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>見つかった宣言子。無い場合は <see langword="null"/>。</returns>
    private static VariableDeclaratorSyntax? TouchesDeclarator(VariableDeclarationSyntax declaration, int offset)
        => declaration.Variables.FirstOrDefault(v => Touches(v.NameToken, offset));

    /// <summary>識別子が呼び出しの対象かどうかを判定する。</summary>
    /// <param name="identifier">対象の識別子。</param>
    /// <returns>呼び出しの対象であれば <see langword="true"/>。</returns>
    private static bool IsCallee(IdentifierExpressionSyntax identifier)
        => identifier.Parent is InvocationExpressionSyntax invocation
           && ReferenceEquals(invocation.Target, identifier);
}

