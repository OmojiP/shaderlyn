using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Tests;

/// <summary>HLSL の構文解析を短く検証するための補助。</summary>
internal static class HlslHarness
{
    /// <summary>解析して構文木を返す。</summary>
    public static HlslSyntaxTree Parse(string content)
        => HlslSyntaxTree.Parse(SourceText.From(content, "test.hlsl"));

    /// <summary>診断メッセージを連結する。</summary>
    public static string Describe(ImmutableArray<Diagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"  {d.Location.LineSpan.Start} {d.Id}: {d.GetMessage()}"));

    /// <summary>診断が 1 件も出ないことを確認したうえで構文木を返す。</summary>
    public static HlslSyntaxTree ParseValid(string content)
    {
        HlslSyntaxTree tree = Parse(content);
        Assert.True(tree.Diagnostics.IsEmpty, $"予期しない診断:\n{Describe(tree.Diagnostics)}");
        return tree;
    }
}

public sealed class HlslDeclarationParsingTests
{
    [Fact]
    public void 変数宣言を解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("float4 _BaseColor;");

        VariableDeclarationSyntax declaration =
            Assert.IsType<VariableDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal("float4", declaration.Type.Name);
        Assert.Equal("_BaseColor", Assert.Single(declaration.Variables).Name);
    }

    [Fact]
    public void 修飾子と初期化子つきの宣言を解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("static const float PI = 3.14159f;");

        VariableDeclarationSyntax declaration =
            Assert.IsType<VariableDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.True(declaration.HasModifier("static"));
        Assert.True(declaration.HasModifier("const"));
        Assert.NotNull(Assert.Single(declaration.Variables).Initializer);
    }

    [Fact]
    public void 複数の宣言子を一度に解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("float a = 0, b, c[4];");

        VariableDeclarationSyntax declaration =
            Assert.IsType<VariableDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal(["a", "b", "c"], declaration.Variables.Select(v => v.Name));
        Assert.True(declaration.Variables.Last().IsArray);
    }

    [Fact]
    public void テンプレート引数を伴う型を解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("Texture2D<float4> _BaseMap;");

        VariableDeclarationSyntax declaration =
            Assert.IsType<VariableDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal("Texture2D", declaration.Type.Name);
        Assert.NotNull(declaration.Type.OpenAngle);
        Assert.Equal("float4", Assert.Single(declaration.Type.TemplateArguments).Text);
    }

    [Fact]
    public void レジスタ指定を解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("Texture2D _BaseMap : register(t0);");

        VariableDeclaratorSyntax declarator = Assert
            .IsType<VariableDeclarationSyntax>(Assert.Single(tree.Root.Declarations))
            .Variables.Single();

        SemanticSyntax semantic = Assert.Single(declarator.Semantics);
        Assert.Equal("register", semantic.Name);
        Assert.True(semantic.HasArguments);
    }

    [Fact]
    public void 構造体とセマンティクスを解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };
            """);

        StructDeclarationSyntax structure =
            Assert.IsType<StructDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal("Varyings", structure.Name);
        Assert.Equal(2, structure.Fields.Count());

        VariableDeclaratorSyntax position = structure.Fields.First().Variables.Single();
        Assert.Equal("SV_POSITION", Assert.Single(position.Semantics).Name);
    }

    [Fact]
    public void 定数バッファを解析できる()
    {
        // URP の CBUFFER_START(UnityPerMaterial) はマクロ展開後にこの形になる。
        // SRP Batcher の互換性検査 (URP0001) がこのノードを対象にする。
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            cbuffer UnityPerMaterial
            {
                float4 _BaseMap_ST;
                half4 _BaseColor;
            }
            """);

        ConstantBufferDeclarationSyntax buffer =
            Assert.IsType<ConstantBufferDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal("UnityPerMaterial", buffer.Name);
        Assert.Equal(["_BaseMap_ST", "_BaseColor"],
            buffer.Fields.SelectMany(f => f.Variables).Select(v => v.Name));
    }

    [Fact]
    public void 関数定義を解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            half4 LitPassFragment(Varyings input) : SV_Target
            {
                return half4(1, 1, 1, 1);
            }
            """);

        FunctionDeclarationSyntax function =
            Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal("LitPassFragment", function.Name);
        Assert.Equal("half4", function.ReturnType.Name);
        Assert.True(function.IsDefinition);
        Assert.Equal("SV_Target", Assert.Single(function.Semantics).Name);

        ParameterSyntax parameter = Assert.Single(function.ParameterList);
        Assert.Equal("Varyings", parameter.Type.Name);
        Assert.Equal("input", parameter.Name);
    }

    [Fact]
    public void プロトタイプ宣言を解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("float3 Normalize(float3 v);");

        FunctionDeclarationSyntax function =
            Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.False(function.IsDefinition);
    }

    [Fact]
    public void 引数の入出力修飾子を解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("void Compute(in float a, out float b, inout float c) { }");

        FunctionDeclarationSyntax function =
            Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal(["in", "out", "inout"],
            function.ParameterList.Select(p => p.ModifierTokens.Single().Text));
    }

    [Fact]
    public void 計算シェーダーの属性を解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            [numthreads(8, 8, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) { }
            """);

        FunctionDeclarationSyntax function =
            Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal("numthreads", Assert.Single(function.Attributes).Name);
    }

    /// <summary>
    /// 属性の引数に括弧が入れ子で現れても解析できることを検証する。
    /// </summary>
    /// <remarks>
    /// <c>[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]</c> の <c>GROUP_SIZE</c> が
    /// <c>(TILE_SIZE / 2)</c> のようなマクロだと、展開後は括弧を含む式になる。
    /// 深さを数えないと、内側の閉じ括弧で属性が終わったと読んでしまう。
    /// HDRP の <c>.compute</c> がこの書き方をしている。
    /// </remarks>
    [Fact]
    public void 属性の引数に入れ子の括弧が来ても解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            [numthreads((16 / 2), (16 / 2), 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) { }
            """);

        FunctionDeclarationSyntax function =
            Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Declarations));

        Assert.Equal("numthreads", Assert.Single(function.Attributes).Name);
    }

    /// <summary>
    /// 型名の前に修飾子が付いた型変換を解析できることを検証する。
    /// </summary>
    /// <remarks>
    /// <c>(const int)</c> や <c>(unsigned int)</c> は C 由来の書き方で、
    /// HDRP の計算ユーティリティが使っている。
    /// 修飾子を読み飛ばせないと、閉じ括弧を探す途中で型名にぶつかって失敗する。
    /// </remarks>
    [Theory]
    [InlineData("(const int) x")]
    [InlineData("(unsigned int) x")]
    [InlineData("(volatile uint) x")]
    [InlineData("(row_major float4x4) x")]
    public void 修飾子付きの型変換を解析できる(string expression)
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid($$"""
            int f(int x)
            {
                return {{expression}};
            }
            """);

        Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Declarations));
    }

    /// <summary>
    /// 修飾子と同じ綴りの変数を、型変換と読み違えないことを検証する。
    /// </summary>
    /// <remarks>
    /// 修飾子とみなすのは、その次も識別子である場合だけである。
    /// <c>(precise) - b</c> のような減算を型変換にしてはならない。
    /// </remarks>
    [Fact]
    public void 修飾子だけの括弧は型変換にしない()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            int f(int a, int b)
            {
                return (a) - b;
            }
            """);

        Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Declarations));
    }

    /// <summary>
    /// 型変換の修飾子を捨てずに木へ載せていることを検証する。
    /// </summary>
    /// <remarks>捨てると、その範囲を指す位置が作れなくなる。</remarks>
    [Fact]
    public void 型変換の修飾子は木に残る()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            int f(int x)
            {
                return (const unsigned int) x;
            }
            """);

        CastExpressionSyntax cast = Assert.Single(
            tree.Root.DescendantNodesAndSelf().OfType<CastExpressionSyntax>());

        Assert.Equal("int", cast.Type.Name);
        Assert.Equal(["const", "unsigned"], cast.Type.ModifierTokens.Select(t => t.Text).ToArray());
    }

    /// <summary>
    /// <c>interface</c> / <c>class</c> を、解釈できない宣言として報告することを検証する。
    /// </summary>
    /// <remarks>
    /// <b>何も伝えずに取り込んでいた。</b>
    /// この経路が無かったとき、<c>interface IBase { ... };</c> は
    /// 「<c>interface</c> 型の変数 <c>IBase</c>」として解析され、
    /// 波括弧の中身は <c>sampler_state { ... }</c> を読み飛ばす経路が捨てていた。
    /// 診断は 1 件も出ず、中のメソッドもフィールドも無かったことになる。
    /// </remarks>
    [Theory]
    [InlineData("interface IBase { float4 Shade(float4 x); };", "interface")]
    [InlineData("class Impl : IBase { float4 v; float4 Shade(float4 x) { return x; } };", "class")]
    [InlineData("class Impl { float4 v; } impl;", "class")]
    [InlineData("enum Mode { First, Second };", "enum")]
    [InlineData("enum class Mode : uint { First, Second };", "enum")]
    [InlineData("template<typename T> T Twice(T x) { return x + x; }", "template")]
    [InlineData("template<typename T> T Value;", "template")]
    public void 解釈できない型宣言を報告する(string source, string keyword)
    {
        HlslSyntaxTree tree = HlslHarness.Parse(source);

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("HL0003", diagnostic.Id);
        Assert.Contains(keyword, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 解釈できない型宣言を、変数宣言として取り込まないことを検証する。
    /// </summary>
    /// <remarks>
    /// 取り込むと、<c>interface</c> 型の uniform が一覧に載る。
    /// 中身を読んでいない以上、名前だけを登録しても根拠にならない。
    /// </remarks>
    [Fact]
    public void 解釈できない型宣言は宣言として取り込まない()
    {
        HlslSyntaxTree tree = HlslHarness.Parse("""
            interface ILight { half3 Shade(half3 n); };
            float4 _BaseColor;
            """);

        Assert.IsType<IncompleteDeclarationSyntax>(tree.Root.Declarations[0]);

        VariableDeclarationSyntax variable =
            Assert.IsType<VariableDeclarationSyntax>(tree.Root.Declarations[1]);
        Assert.Equal("_BaseColor", Assert.Single(variable.Variables).Name);
    }

    /// <summary>
    /// 解釈できない型宣言の後ろから、通常どおり解析を続けることを検証する。
    /// </summary>
    /// <remarks>
    /// 読み飛ばす範囲を誤ると、後続の宣言まで巻き込んで消える。
    /// 消えたことは診断に現れないため、件数ではなく中身で確かめる。
    /// </remarks>
    [Theory]
    [InlineData("class Impl { float4 v; float4 Get() { return v; } };")]
    [InlineData("enum Mode { First, Second };")]

    // template が導く関数は波括弧で終わる。セミコロンまで読み飛ばすと、後ろの宣言を丸ごと捨てる。
    [InlineData("template<typename T> T Twice(T x) { return x + x; }")]
    [InlineData("template<typename T> T Value;")]
    public void 解釈できない型宣言の後も解析を続ける(string unsupported)
    {
        HlslSyntaxTree tree = HlslHarness.Parse(unsupported + "\nfloat4 f(float4 x) { return x; }\n");

        Assert.Single(tree.Diagnostics);

        FunctionDeclarationSyntax function =
            Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Declarations[1]);
        Assert.Equal("f", function.Name);
    }

    /// <summary>
    /// C のビットフィールドを、数値の位置で 1 件だけ報告することを検証する。
    /// </summary>
    /// <remarks>
    /// HLSL にビットフィールドは無く、Unity も
    /// 「syntax error: unexpected integer constant」として拒む。誤りであること自体は正しい。
    /// ただしコロンは構造体のメンバーでは正しい記法 (セマンティクス) なので、
    /// コロンを「余計なもの」として報告すると、本当におかしい数値を指さないまま 1 行に 2 件出る。
    /// </remarks>
    [Fact]
    public void ビットフィールドを数値の位置で報告する()
    {
        HlslSyntaxTree tree = HlslHarness.Parse("""
            struct Packed
            {
                uint a : 8;
            };
            """);

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);

        Assert.Equal("HL0003", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("8", diagnostic.Location.Source.ToString(diagnostic.Location.Span));
        Assert.Contains("ビットフィールド", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("Unity", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// operator による演算子の多重定義を、Unity で使えない構文として報告することを検証する。
    /// </summary>
    /// <remarks>
    /// Unity は "syntax error: unexpected token 'operator'" で拒む。
    /// 構造体の中でも外でも報告し、後ろの宣言は読み続ける。
    /// </remarks>
    [Theory]
    [InlineData("struct S { float4 v; S operator+(S o) { S r; r.v = v + o.v; return r; } };")]
    [InlineData("struct S { float4 v; };\nS operator*(S a, S b) { return a; }")]
    public void 演算子の多重定義をUnityで使えない構文として報告する(string source)
    {
        HlslSyntaxTree tree = HlslHarness.Parse(source + "\nfloat4 f(float4 x) { return x; }\n");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("HL0003", diagnostic.Id);
        Assert.Contains("operator", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains(tree.Root.Declarations, d => d is FunctionDeclarationSyntax { Name: "f" });
    }

    /// <summary>
    /// <c>#line</c> を、Unity で使えない構文として報告することを検証する。
    /// </summary>
    /// <remarks>Unity は "syntax error: unexpected token 'line'" で拒む。</remarks>
    [Fact]
    public void LineはUnityで使えない構文として報告する()
    {
        HlslSyntaxTree tree = HlslHarness.Parse("#line 100\nfloat4 f(float4 x) { return x; }\n");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("HL0003", diagnostic.Id);
        Assert.Contains("#line", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>[[vk::binding(0, 0)]]</c> のような二重角括弧の注釈を読み飛ばすことを検証する。
    /// </summary>
    /// <remarks>
    /// 読み飛ばさないと、後ろの宣言が丸ごと <c>HL0001</c> になり、
    /// その uniform を使っている場所が「宣言されていない」ことになる。
    /// 注釈が指示するのはレジスタの割り当てで、この解析ツールはそれを検査しない。
    /// </remarks>
    [Fact]
    public void 二重角括弧の注釈を読み飛ばす()
    {
        HlslSyntaxTree tree = HlslHarness.Parse("[[vk::binding(0, 0)]] Texture2D _Tex;");

        Assert.Empty(tree.Diagnostics);

        VariableDeclarationSyntax variable =
            Assert.IsType<VariableDeclarationSyntax>(Assert.Single(tree.Root.Declarations));
        Assert.Equal("_Tex", Assert.Single(variable.Variables).Name);
    }
}

public sealed class HlslStatementParsingTests
{
    private static BlockStatementSyntax ParseBody(string body)
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid($"void F() {{ {body} }}");
        return Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Declarations)).Body!;
    }

    [Fact]
    public void 局所変数の宣言と式文を区別できる()
    {
        // float4 x; と foo(x); はどちらも識別子で始まるため、先読みで区別する必要がある。
        BlockStatementSyntax body = ParseBody("float4 x; foo(x);");

        Assert.IsType<LocalDeclarationStatementSyntax>(body.Statements[0]);
        Assert.IsType<ExpressionStatementSyntax>(body.Statements[1]);
    }

    [Fact]
    public void 利用者定義型の局所変数を宣言として認識する()
    {
        BlockStatementSyntax body = ParseBody("Varyings output;");
        Assert.IsType<LocalDeclarationStatementSyntax>(Assert.Single(body.Statements));
    }

    [Fact]
    public void 制御構文を解析できる()
    {
        BlockStatementSyntax body = ParseBody("""
            if (a > 0) { b = 1; } else { b = 2; }
            for (int i = 0; i < 4; i++) { sum += i; }
            while (x) { y(); }
            do { z(); } while (w);
            switch (m) { case 0: break; default: break; }
            return;
            """);

        Assert.IsType<IfStatementSyntax>(body.Statements[0]);
        Assert.IsType<ForStatementSyntax>(body.Statements[1]);
        Assert.IsType<WhileStatementSyntax>(body.Statements[2]);
        Assert.IsType<DoWhileStatementSyntax>(body.Statements[3]);
        Assert.IsType<SwitchStatementSyntax>(body.Statements[4]);
        Assert.IsType<ReturnStatementSyntax>(body.Statements[5]);
    }

    [Fact]
    public void ループ属性を解析できる()
    {
        BlockStatementSyntax body = ParseBody("[unroll] for (int i = 0; i < 4; i++) { }");

        ForStatementSyntax loop = Assert.IsType<ForStatementSyntax>(Assert.Single(body.Statements));
        Assert.Equal("unroll", Assert.Single(loop.Attributes).Name);
    }

    [Fact]
    public void discardを解析できる()
    {
        BlockStatementSyntax body = ParseBody("discard;");
        Assert.Equal("discard", Assert.IsType<JumpStatementSyntax>(Assert.Single(body.Statements)).Keyword.Text);
    }
}

public sealed class HlslExpressionParsingTests
{
    private static HlslExpressionSyntax ParseExpression(string expression)
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid($"void F() {{ x = {expression}; }}");
        BlockStatementSyntax body = Assert.IsType<FunctionDeclarationSyntax>(
            Assert.Single(tree.Root.Declarations)).Body!;

        ExpressionStatementSyntax statement =
            Assert.IsType<ExpressionStatementSyntax>(Assert.Single(body.Statements));

        return Assert.IsType<AssignmentExpressionSyntax>(statement.Expression).Right;
    }

    [Fact]
    public void 演算子の優先順位を正しく解釈する()
    {
        // a + b * c は a + (b * c) でなければならない。
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a + b * c"));

        Assert.Equal("+", root.OperatorToken.Text);
        Assert.Equal("*", Assert.IsType<BinaryExpressionSyntax>(root.Right).OperatorToken.Text);
    }

    [Fact]
    public void 論理演算子は比較演算子より弱く結合する()
    {
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a < b && c > d"));
        Assert.Equal("&&", root.OperatorToken.Text);
    }

    [Fact]
    public void 関数呼び出しとスウィズルを解析できる()
    {
        MemberAccessExpressionSyntax access =
            Assert.IsType<MemberAccessExpressionSyntax>(ParseExpression("SAMPLE_TEXTURE2D(t, s, uv).rgb"));

        Assert.Equal("rgb", access.Name);

        InvocationExpressionSyntax invocation = Assert.IsType<InvocationExpressionSyntax>(access.Target);
        Assert.Equal("SAMPLE_TEXTURE2D", invocation.TargetName);
        Assert.Equal(3, invocation.ArgumentExpressions.Count());
    }

    [Fact]
    public void 組み込み型の型変換を解析できる()
    {
        CastExpressionSyntax cast = Assert.IsType<CastExpressionSyntax>(ParseExpression("(float4)value"));
        Assert.Equal("float4", cast.Type.Name);
    }

    [Fact]
    public void 括弧つきの式と乗算を型変換と誤解釈しない()
    {
        // (a) * b は型変換ではなく乗算である。
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("(a) * b"));
        Assert.Equal("*", root.OperatorToken.Text);
    }

    [Fact]
    public void 括弧つきの式と減算を型変換と誤解釈しない()
    {
        // (a) - b は減算のほうが (MyType)-b という型変換より圧倒的に多い。
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("(a) - b"));
        Assert.Equal("-", root.OperatorToken.Text);
    }

    [Fact]
    public void 三項演算子を解析できる()
    {
        ConditionalExpressionSyntax conditional =
            Assert.IsType<ConditionalExpressionSyntax>(ParseExpression("a > 0 ? b : c"));

        Assert.IsType<BinaryExpressionSyntax>(conditional.Condition);
    }

    [Fact]
    public void 添字アクセスを解析できる()
    {
        Assert.IsType<ElementAccessExpressionSyntax>(ParseExpression("data[i + 1]"));
    }

    [Fact]
    public void 初期化子リストを解析できる()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("static const float4 a[2] = { float4(0,0,0,0), float4(1,1,1,1) };");

        VariableDeclaratorSyntax declarator = Assert
            .IsType<VariableDeclarationSyntax>(Assert.Single(tree.Root.Declarations))
            .Variables.Single();

        InitializerListExpressionSyntax list =
            Assert.IsType<InitializerListExpressionSyntax>(declarator.Initializer);

        Assert.Equal(2, list.Elements.Count(e => e.Node is not null));
    }
}

public sealed class HlslIntegrationTests
{
    [Fact]
    public void マクロを展開してから構文解析する()
    {
        // これが M2 の中心的な要件である。
        // マクロを展開しなければ TEXTURE2D(_BaseMap) は構文として成立しない。
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            #define TEXTURE2D(name) Texture2D name
            #define SAMPLER(name) SamplerState name
            #define CBUFFER_START(name) cbuffer name {
            #define CBUFFER_END };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
            CBUFFER_END
            """);

        Assert.Equal(3, tree.Root.Declarations.Length);

        ConstantBufferDeclarationSyntax buffer = tree.Root.Declarations
            .OfType<ConstantBufferDeclarationSyntax>().Single();

        Assert.Equal("UnityPerMaterial", buffer.Name);
        Assert.Equal(["_BaseMap_ST", "_BaseColor"],
            buffer.Fields.SelectMany(f => f.Variables).Select(v => v.Name));
    }

    [Fact]
    public void 条件分岐で無効化された領域は構文木に現れない()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            #if 0
            this is not valid hlsl at all @@@
            #endif
            float4 a;
            """);

        Assert.Equal("a", Assert.IsType<VariableDeclarationSyntax>(
            Assert.Single(tree.Root.Declarations)).Variables.Single().Name);
    }

    [Fact]
    public void pragmaを構文木から分離して保持する()
    {
        HlslSyntaxTree tree = HlslHarness.ParseValid("""
            #pragma vertex vert
            #pragma fragment frag
            void vert() { }
            void frag() { }
            """);

        Assert.Equal(["vertex", "fragment"], tree.Pragmas.Select(p => p.Name));
        Assert.Equal(2, tree.Root.Declarations.Length);
    }

    [Fact]
    public void 診断はマクロの呼び出し位置を指す()
    {
        // マクロ定義の中を指しても利用者は直しようがない。
        HlslSyntaxTree tree = HlslHarness.Parse("""
            #define BROKEN float4 @@@
            BROKEN
            """);

        Assert.NotEmpty(tree.Diagnostics);
        // 2 行目 (0 始まりで 1) の使用箇所を指していること。
        Assert.All(tree.Diagnostics, d => Assert.Equal(1, d.Location.LineSpan.Start.Line));
    }
}

public sealed class HlslRobustnessTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@@@")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("float4")]
    [InlineData("float4 x")]
    [InlineData("void F(")]
    [InlineData("void F() {")]
    [InlineData("struct S {")]
    [InlineData("cbuffer C {")]
    [InlineData("#if")]
    [InlineData("#define")]
    [InlineData("#include")]
    [InlineData("a b c d e f g")]
    [InlineData("((((((((((")]
    [InlineData("))))))))))")]
    [InlineData("/* 閉じられていない")]
    [InlineData("\"閉じられていない")]
    [InlineData("float4 x = ;")]
    [InlineData("return return return;")]
    public void 壊れた入力でも例外を投げない(string content)
    {
        HlslSyntaxTree tree = HlslHarness.Parse(content);
        Assert.NotNull(tree.Root);
    }

    [Fact]
    public void 構文エラーの報告数には上限がある()
    {
        // マクロが期待どおり展開されないと 1 ファイルから数千件のエラーが出る。
        // そのすべてを報告しても読み手の役に立たず、他のファイルの指摘が埋もれる。
        string content = string.Join("\n", Enumerable.Repeat("@@@", 500));
        HlslSyntaxTree tree = HlslHarness.Parse(content);

        Assert.True(
            tree.Diagnostics.Length <= Hlsl.Parsing.HlslParser.MaxReportedErrors,
            $"診断が {tree.Diagnostics.Length} 件あり上限を超えています。");
    }

    [Fact]
    public void 深く入れ子になった式でスタックを溢れさせない()
    {
        // リンタは壊れた入力・病的な入力を与えられる前提で書く必要がある。
        string content = $"void F() {{ x = {new string('(', 200)}1{new string(')', 200)}; }}";
        HlslSyntaxTree tree = HlslHarness.Parse(content);
        Assert.NotNull(tree.Root);
    }

    [Fact]
    public void 二項演算をつなげすぎた式は誤りとして報告する()
    {
        // a + b + c ... は左へ伸びる木になり、歩く処理は項の数だけ再帰する。
        // マクロを重ねれば 1 行から何十万項でも作れる。
        // 上限が無いとスタックを使い切り、捕まえる手立てが無いままプロセスが終了する。
        int operands = Hlsl.Parsing.HlslParser.MaxBinaryOperands + 100;
        string content = $"void F() {{ x = {string.Join(" + ", Enumerable.Repeat("1", operands))}; }}";

        HlslSyntaxTree tree = HlslHarness.Parse(content);

        Assert.Contains(
            tree.Diagnostics,
            d => d.GetMessage().Contains("つなげることはできません", StringComparison.Ordinal));
    }

    [Fact]
    public void 人が書く長さの式は報告しない()
    {
        // 上限は、人が書く式には届かない高さに置く。
        string content = $"void F() {{ x = {string.Join(" + ", Enumerable.Repeat("1", 256))}; }}";

        HlslSyntaxTree tree = HlslHarness.Parse(content);

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));
    }
}
