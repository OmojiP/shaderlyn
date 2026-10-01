using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Tests;

/// <summary>
/// 解析ドライバの検証に使う最小の構文ノード群。
/// ShaderLab / HLSL のパーサに依存せずにドライバ単体を検証するために用意している。
/// </summary>
internal abstract class TestNode : SyntaxNode
{
    protected TestNode(TextSpan span, IReadOnlyList<TestNode> children)
    {
        Span = span;
        FullSpan = span;
        Children = children;
        foreach (TestNode child in children)
        {
            child.SetParent(this);
        }
    }

    public override TextSpan Span { get; }

    public override TextSpan FullSpan { get; }

    public IReadOnlyList<TestNode> Children { get; }

    public override IEnumerable<SyntaxNode> ChildNodes() => Children;
}

/// <summary>木の根を表すテスト用ノード。</summary>
internal sealed class TestRootNode(TextSpan span, params TestNode[] children) : TestNode(span, children);

/// <summary>名前を持つ宣言を表すテスト用ノード。</summary>
internal sealed class TestDeclarationNode(TextSpan span, string name, params TestNode[] children)
    : TestNode(span, children)
{
    public string Name { get; } = name;
}

/// <summary>式の基底を表すテスト用ノード。基底型での登録が効くことの検証に使う。</summary>
internal abstract class TestExpressionNode(TextSpan span, params TestNode[] children) : TestNode(span, children);

/// <summary>識別子の参照を表すテスト用ノード。</summary>
internal sealed class TestIdentifierNode(TextSpan span, string name) : TestExpressionNode(span)
{
    public string Name { get; } = name;
}

/// <summary>リテラルを表すテスト用ノード。</summary>
internal sealed class TestLiteralNode(TextSpan span) : TestExpressionNode(span);
