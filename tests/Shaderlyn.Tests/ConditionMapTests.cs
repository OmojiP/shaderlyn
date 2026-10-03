using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// ルールから出現条件をどう見るかの検証。
/// </summary>
/// <remarks>
/// <b>ルールが実際に書く形で確かめる。</b>
/// 索引を単体で突いても、ルールから素直に使えるかは分からない。
/// ここでは「木を歩いて、条件を見て、報告するかを決める」という
/// ルールと同じ順序で書く。
/// </remarks>
public sealed class ConditionMapTests
{
    private const string UrpCorePath =
        "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl";

    /// <summary>ブロックを解析する。</summary>
    /// <param name="body">HLSLPROGRAM の中身。</param>
    /// <param name="pragmas">足す <c>#pragma</c> 行。</param>
    /// <returns>解析結果。</returns>
    private static ShaderCompilation Compile(string body, string pragmas)
    {
        string source = $$"""
            Shader "Test/Condition"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
            {{pragmas}}
            {{body}}
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = string.Empty }),

            // ここで見るのは「2 つの木を突き合わせる」機構である。
            // 1 つの木にまとめられるとその機構を通らないので、明示的に切る。
            BothBranchSymbols = [],
        };

        return ShaderCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), options);
    }

    /// <summary>1 本の木にまとめた状態でブロックを解析する。</summary>
    /// <param name="body">HLSLPROGRAM の中身。</param>
    /// <param name="pragmas">足す <c>#pragma</c> 行。</param>
    /// <param name="hoist">条件によって中身が変わるマクロを巻き上げるかどうか。</param>
    /// <returns>解析結果。</returns>
    /// <remarks>
    /// 既定の経路である。#ifdef の両方の分岐が 1 本の木に載り、
    /// 条件は展開が残した範囲から引ける。
    /// </remarks>
    private static ShaderCompilation CompileFolded(string body, string pragmas, bool hoist = true)
    {
        string source = $$"""
            Shader "Test/Condition"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
            {{pragmas}}
            {{body}}
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = string.Empty }),
            HoistConditionalMacros = hoist,
        };

        return ShaderCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), options);
    }

    /// <summary>
    /// ルールと同じ形で、宣言を歩いて条件を集める。
    /// </summary>
    /// <param name="compilation">対象のセマンティックモデル。</param>
    /// <returns>変数名とその出現条件。</returns>
    private static Dictionary<string, SymbolCondition> WalkDeclarations(ShaderCompilation compilation)
    {
        ConditionMap condition = compilation.GetConditionMap();
        Dictionary<string, SymbolCondition> found = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is not VariableDeclarationSyntax declaration)
                {
                    continue;
                }

                foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                {
                    found[variable.Name] = condition.GetCondition(declaration);
                }
            }
        }

        return found;
    }

    [Fact]
    public void バリアントが無いときは費用ゼロで常にを返す()
    {
        // シンボルを宣言していないシェーダーでは突き合わせるものが無い。
        // そこで何かを組み立てると、条件を見ないルールにも費用がかかる。
        ShaderCompilation compilation = Compile(
            "            float4 _Value;",
            "                        #pragma vertex vert");

        ConditionMap condition = compilation.GetConditionMap();

        Assert.Same(ConditionMap.Empty, condition);
        Assert.True(condition.IsComplete);
        Assert.Empty(condition.EnumerateConditional());
    }

    [Fact]
    public void 索引は一度だけ組み立てて使い回す()
    {
        ShaderCompilation compilation = Compile(
            """
                        float4 _Always;
            #ifdef _NORMALMAP
                        float4 _Guarded;
            #endif
            """,
            "                        #pragma shader_feature_local _NORMALMAP");

        Assert.Same(compilation.GetConditionMap(), compilation.GetConditionMap());
    }

    [Fact]
    public void 条件の外の宣言は常に存在する()
    {
        ShaderCompilation compilation = Compile(
            """
                        float4 _Always;
            #ifdef _NORMALMAP
                        float4 _Guarded;
            #endif
            """,
            "                        #pragma shader_feature_local _NORMALMAP");

        Dictionary<string, SymbolCondition> found = WalkDeclarations(compilation);

        Assert.True(found["_Always"].IsAlways);
    }

    [Fact]
    public void 既定の構成にしかない宣言には否定の条件が付く()
    {
        // ルールが歩くのは既定の構成の木である。
        // そこにあってシンボルを有効にすると消えるものは、
        // 「シンボルが無効なときだけ存在する」。
        ShaderCompilation compilation = Compile(
            """
            #ifdef _NORMALMAP
                        float4 _WhenEnabled;
            #else
                        float4 _WhenDisabled;
            #endif
            """,
            "                        #pragma shader_feature_local _NORMALMAP");

        Dictionary<string, SymbolCondition> found = WalkDeclarations(compilation);

        Assert.Equal(
            SymbolCondition.Symbol("_NORMALMAP", isDefined: false),
            found["_WhenDisabled"]);
    }

    [Fact]
    public void 複数のシンボルの条件が重なる()
    {
        // バリアントごとの突き合わせは、それぞれ独立した制約を与える。
        // どちらのシンボルでも消えるなら、両方が無効なときだけ存在する。
        ShaderCompilation compilation = Compile(
            """
            #ifndef _A
            #ifndef _B
                        float4 _OnlyWhenNeither;
            #endif
            #endif
            """,
            """
                        #pragma shader_feature_local _A
                        #pragma shader_feature_local _B
            """);

        Dictionary<string, SymbolCondition> found = WalkDeclarations(compilation);

        SymbolCondition expected = SymbolCondition.Symbol("_A", isDefined: false)
            .And(SymbolCondition.Symbol("_B", isDefined: false));

        Assert.Equal(expected, found["_OnlyWhenNeither"]);
    }

    [Fact]
    public void 条件付きの部分木の中は親から条件が求まる()
    {
        // 条件は部分木の根にだけ記録してある。
        // 中身の条件はそこから辿って求める。
        // ここが効かないと、シンボルの中のコードが無条件と見なされる。
        ShaderCompilation compilation = Compile(
            """
            #ifdef _NORMALMAP
                        half4 OnlyHere(half4 a)
                        {
                            half4 inner = a * 2;
                            return inner;
                        }
            #endif
            """,
            "                        #pragma shader_feature_local _NORMALMAP");

        ConditionMap condition = compilation.GetConditionMap();
        SymbolCondition expected = SymbolCondition.Symbol("_NORMALMAP");

        // バリアントにしかない関数なので、既定の構成の木からは辿れない。
        // バリアントの木から探す。
        AnalyzedProgram variant = Assert.Single(compilation.SymbolVariants);

        FunctionDeclarationSyntax function = Assert.Single(
            variant.Tree.Root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>(),
            f => f.Name == "OnlyHere");

        Assert.Equal(expected, condition.GetCondition(function));

        // 中の宣言にも、親を辿って同じ条件が付く。
        VariableDeclarationSyntax inner = Assert.Single(
            function.DescendantNodesAndSelf().OfType<VariableDeclarationSyntax>(),
            d => d.Variables.Any(v => v.Name == "inner"));

        Assert.Equal(expected, condition.GetCondition(inner));
        Assert.False(condition.IsAlwaysPresent(inner));
    }

    [Fact]
    public void 突き合わせられなかった箇所では条件を不明として返す()
    {
        // 条件を追えなかった箇所を無条件と見なすと、
        // 「どの構成でも成り立つ」を根拠にした指摘が、
        // 実際には成り立たない構成について出ることになる。
        // これは条件を導入する前より悪い。
        ShaderCompilation compilation = Compile(
            """
                        half4 frag() : SV_Target
                        {
                            half4 color = 0;
            #ifdef _NORMALMAP
                            if (color.x < 1)
            #else
                            if (color.y < 1)
            #endif
                            {
                                color = 1;
                            }
                            return color;
                        }
            """,
            "                        #pragma shader_feature_local _NORMALMAP");

        ConditionMap condition = compilation.GetConditionMap();

        if (condition.IsComplete)
        {
            // 併合できたなら、この検証の前提が崩れているだけで害は無い。
            return;
        }

        Assert.NotEmpty(condition.UnmergedLocations);

        Location unmerged = condition.UnmergedLocations[0];

        // その範囲に重なるノードを探す。
        HlslSyntaxNode? inRegion = null;

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is HlslSyntaxNode hlsl
                    && ReferenceEquals(hlsl.Source, unmerged.Source)
                    && hlsl.Span.Start < unmerged.Span.End
                    && unmerged.Span.Start < hlsl.Span.End)
                {
                    inRegion = hlsl;
                    break;
                }
            }

            if (inRegion is not null) { break; }
        }

        Assert.NotNull(inRegion);

        // 不明であること。無条件と言ってはならない。
        Assert.True(condition.GetCondition(inRegion).IsUnknown);

        // 保守的なルールは、確認を忘れていても自動的に報告しない。
        Assert.False(condition.IsAlwaysPresent(inRegion));
    }

    [Fact]
    public void 保守的なルールは条件付きのものを見送れる()
    {
        // IsAlwaysPresent を使えば、どの構成でも成り立つときだけ指摘するルールを書ける。
        // 組み込みルールは使っていない。
        ShaderCompilation compilation = Compile(
            """
                        float4 _Always;
            #ifdef _NORMALMAP
            #else
                        float4 _OnlyWhenDisabled;
            #endif
            """,
            "                        #pragma shader_feature_local _NORMALMAP");

        ConditionMap condition = compilation.GetConditionMap();

        List<string> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is not VariableDeclarationSyntax declaration
                    || !condition.IsAlwaysPresent(declaration))
                {
                    continue;
                }

                reported.AddRange(declaration.Variables.Select(v => v.Name));
            }
        }

        Assert.Contains("_Always", reported);
        Assert.DoesNotContain("_OnlyWhenDisabled", reported);
    }

    [Fact]
    public void 条件を添えて報告できる()
    {
        // 指摘のメッセージには、どの条件のもとでの話かを添えられること。
        ShaderCompilation compilation = Compile(
            """
            #ifdef _NORMALMAP
            #else
                        float4 _OnlyWhenDisabled;
            #endif
            """,
            "                        #pragma shader_feature_local _NORMALMAP");

        Dictionary<string, SymbolCondition> found = WalkDeclarations(compilation);

        Assert.Equal("!_NORMALMAP", found["_OnlyWhenDisabled"].ToString());
    }
    [Fact]
    public void まとめた木でも宣言に条件が付いている()
    {
        // 既定の経路。バリアントは作られず、1 本の木に両方の分岐が載る。
        ShaderCompilation compilation = CompileFolded(
            """
            #ifdef _NORMALMAP
                float4 _BumpMap;
            #endif
            float4 _BaseMap;
            """,
            "#pragma shader_feature_local _NORMALMAP");

        Assert.Empty(compilation.SymbolVariants);

        Dictionary<string, SymbolCondition> found = WalkDeclarations(compilation);

        Assert.Equal("_NORMALMAP", found["_BumpMap"].ToString());
        Assert.True(found["_BaseMap"].IsAlways);
    }

    [Fact]
    public void まとめた木では両方の分岐が載っている()
    {
        // #else の側も同じ木にある。どちらも「いつか通る」コードである。
        ShaderCompilation compilation = CompileFolded(
            """
            #ifdef _NORMALMAP
                float4 _WhenOn;
            #else
                float4 _WhenOff;
            #endif
            """,
            "#pragma shader_feature_local _NORMALMAP");

        Dictionary<string, SymbolCondition> found = WalkDeclarations(compilation);

        Assert.Equal("_NORMALMAP", found["_WhenOn"].ToString());
        Assert.Equal("!_NORMALMAP", found["_WhenOff"].ToString());
    }

    [Fact]
    public void 入れ子の条件も1本の木に載り条件が掛け合わされる()
    {
        // 入れ子でもバリアント展開に戻さない。4 通りの構成が 1 本の木に載る。
        ShaderCompilation compilation = CompileFolded(
            """
            #ifdef _NORMALMAP
                float4 _Outer;
            #ifdef _DETAIL
                float4 _Both;
            #else
                float4 _OnlyNormal;
            #endif
            #else
                float4 _Neither;
            #endif
            """,
            """
            #pragma shader_feature_local _NORMALMAP
                        #pragma shader_feature_local _DETAIL
            """);

        Assert.Empty(compilation.SymbolVariants);

        Dictionary<string, SymbolCondition> found = WalkDeclarations(compilation);

        SymbolCondition normal = SymbolCondition.Symbol("_NORMALMAP");
        SymbolCondition detail = SymbolCondition.Symbol("_DETAIL");

        Assert.Equal(normal, found["_Outer"]);
        Assert.Equal(normal.And(detail), found["_Both"]);
        Assert.Equal(normal.And(detail.Negate()), found["_OnlyNormal"]);
        Assert.Equal(normal.Negate(), found["_Neither"]);
    }

    [Fact]
    public void 関数の中の条件は関数全体には及ばない()
    {
        // 関数の途中に #ifdef があると、関数の宣言そのものが範囲と重なる。
        // 重なりを条件と見なすと、条件付きなのは中の 1 行だけなのに
        // 関数全体が「そのシンボルのときだけ存在する」ことになってしまう。
        ShaderCompilation compilation = CompileFolded(
            """
            float4 _BaseMap;

            float4 Shade()
            {
                float4 color = _BaseMap;
            #ifdef _NORMALMAP
                float4 _Inner = color;
                color = _Inner;
            #endif
                return color;
            }
            """,
            "#pragma shader_feature_local _NORMALMAP");

        ConditionMap condition = compilation.GetConditionMap();

        FunctionDeclarationSyntax function = compilation.Programs
            .SelectMany(p => p.Tree.Root.DescendantNodesAndSelf())
            .OfType<FunctionDeclarationSyntax>()
            .First(f => f.Name == "Shade");

        Assert.True(condition.GetCondition(function).IsAlways, "関数全体が条件付きになっている");
        Assert.Equal("_NORMALMAP", WalkDeclarations(compilation)["_Inner"].ToString());
    }
    [Fact]
    public void マクロを切り替える条件でも1本の木として並べられる()
    {
        // #ifdef がマクロの定義を切り替えている領域はまとめられない。
        // 使う側の 1 行は、既定の構成では空文に、バリアントでは呼び出し式になる。
        // どちらも「いつか通る」コードであり、1 本の木として並べられなければならない。
        //
        // 巻き上げると、この形は 1 本の木の中で片が付き、突き合わせを通らない。
        // ここで見たいのは突き合わせなので、巻き上げは切る。
        ShaderCompilation compilation = CompileFolded(
            """
            #ifdef _NORMALMAP
                #define TRACE(x) Log(x)
            #else
                #define TRACE(x)
            #endif

            void Use(float4 v)
            {
                TRACE(v);
            }
            """,
            "#pragma shader_feature_local _NORMALMAP",
            hoist: false);

        // マクロを切り替えているのでまとめられず、バリアントが残る。
        Assert.NotEmpty(compilation.SymbolVariants);

        ConditionMap condition = compilation.GetConditionMap();

        // それでも突き合わせは済んでいる。
        Assert.True(condition.IsComplete);

        Dictionary<string, SymbolCondition> statements = new(StringComparer.Ordinal);

        foreach (ConditionalNode conditional in condition.EnumerateConditional())
        {
            statements[conditional.Node.GetType().Name] = conditional.Condition;
        }

        // 呼び出し式はバリアントにしかなく、空文は既定にしかない。
        Assert.Equal("_NORMALMAP", statements["ExpressionStatementSyntax"].ToString());
        Assert.Equal("!_NORMALMAP", statements["EmptyStatementSyntax"].ToString());
    }

    [Fact]
    public void バリアントにしかないノードは既定の木の親へ入れられる()
    {
        // 条件が分かるだけでは 1 本の木にならない。
        // 既定の木を歩くだけでは、バリアントにしかないノードに辿り着けない。
        // 上と同じ理由で、巻き上げは切る。
        ShaderCompilation compilation = CompileFolded(
            """
            #ifdef _NORMALMAP
                #define TRACE(x) Log(x)
            #else
                #define TRACE(x)
            #endif

            void Use(float4 v)
            {
                TRACE(v);
            }
            """,
            "#pragma shader_feature_local _NORMALMAP",
            hoist: false);

        ConditionMap condition = compilation.GetConditionMap();

        Assert.True(condition.HasInsertedChildren);

        // 空文の親をたどれば、そこが挿入先である。
        EmptyStatementSyntax empty = compilation.Programs
            .SelectMany(p => p.Tree.Root.DescendantNodesAndSelf())
            .OfType<EmptyStatementSyntax>()
            .Single();

        HlslSyntaxNode block = (HlslSyntaxNode)empty.Parent!;

        ConditionalNode inserted = Assert.Single(condition.GetInsertedChildren(block));

        Assert.IsType<ExpressionStatementSyntax>(inserted.Node);
        Assert.Equal("_NORMALMAP", inserted.Condition.ToString());
    }

    /// <summary>同じ場所が構成によって別のコードになるシェーダー。</summary>
    /// <remarks>
    /// <c>VALUE</c> は位置も種類も同じまま、トークンだけが入れ替わる。
    /// 位置と種類で対応を取るだけでは「同じノード」と見なしてしまう。
    /// </remarks>
    private const string DifferentFormBody = """
        #ifdef _NORMALMAP
            #define VALUE float2(1, 1)
        #else
            #define VALUE float4(1, 1, 1, 1)
        #endif

        void Use()
        {
            float4 kept = 0;
            float4 varying = VALUE;
        }
        """;

    [Fact]
    public void 巻き上げた複製の条件は掛け合わせない()
    {
        // 定義ごとに複製した文は、どれも同じ位置から作られる。位置では見分けられない。
        // 掛け合わせると「_NORMALMAP かつ !_NORMALMAP」になり、
        // そこにある宣言がどの構成にも無いことになってしまう。
        ShaderCompilation compilation = CompileFolded(
            """
            #ifndef _NORMALMAP
                #define COUNT 1
            #else
                #define COUNT 2
            #endif

            float4 _Values[COUNT];
            """,
            "#pragma shader_feature_local _NORMALMAP");

        ConditionMap condition = compilation.GetConditionMap();

        VariableDeclarationSyntax[] declarations =
        [
            .. compilation.Programs
                .SelectMany(p => p.Tree.Root.DescendantNodesAndSelf())
                .OfType<VariableDeclarationSyntax>()
                .Where(d => d.Variables.Any(v => v.Name == "_Values")),
        ];

        Assert.NotEmpty(declarations);

        Assert.All(
            declarations,
            declaration => Assert.False(
                condition.GetCondition(declaration).IsNever,
                "複製した宣言の条件が決して成り立たないものになっている"));
    }

    /// <summary>
    /// 定義ごとに複製した文のそれぞれに、その定義の条件が付くことを検証する。
    /// </summary>
    /// <param name="pragma">シンボルの宣言。</param>
    /// <param name="whenA"><c>float3</c> の複製に付く条件。</param>
    /// <param name="otherwise"><c>float4</c> の複製に付く条件。</param>
    /// <remarks>
    /// <para>
    /// <b>和を取ると、どの複製も「常に」になる。</b>
    /// <c>_A || !_A</c> はどの構成でも成り立つので、<c>float3 d</c> がどの構成にもあることになり、
    /// <c>!_A</c> の構成で <c>d</c> の型を <c>float3</c> と取り違える。
    /// </para>
    /// <para>
    /// 既定の構成で <c>_A</c> が有効な宣言 (<c>multi_compile _A _B</c>) でも同じでなければならない。
    /// 以前は <c>_A</c> を有効にしない構成を別に展開して突き合わせており、
    /// その 1 文が先頭の複製と対応して、<c>float4 d</c> に <c>!_B</c> が付いていた。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("#pragma multi_compile _ _A", "_A", "!_A")]
    [InlineData("#pragma multi_compile _A _B", "_A", "!_A")]
    [InlineData("#pragma shader_feature_local _A", "_A", "!_A")]
    public void 巻き上げた複製にはそれぞれの定義の条件が付く(string pragma, string whenA, string otherwise)
    {
        ShaderCompilation compilation = CompileFolded(
            """
            #ifdef _A
            #define CTYPE float3
            #else
            #define CTYPE float4
            #endif

            float4 Use()
            {
                CTYPE d = 1.0;
                return d.x;
            }
            """,
            pragma);

        ConditionMap condition = compilation.GetConditionMap();

        VariableDeclarationSyntax[] copies =
        [
            .. compilation.Programs
                .SelectMany(p => p.Tree.Root.DescendantNodesAndSelf())
                .OfType<VariableDeclarationSyntax>()
                .Where(d => d.Variables.Any(v => v.Name == "d")),
        ];

        Assert.Equal(2, copies.Length);
        Assert.Equal(whenA, condition.GetCondition(copies.Single(c => c.Type.Name == "float3")).ToString());
        Assert.Equal(otherwise, condition.GetCondition(copies.Single(c => c.Type.Name == "float4")).ToString());

        // 複製の中の要素も、その複製の条件のもとにある。
        VariableDeclaratorSyntax declarator = copies.Single(c => c.Type.Name == "float4").Variables.Single();
        Assert.Equal(otherwise, condition.GetCondition(declarator).ToString());

        // 複製の外は無条件のまま。
        ReturnStatementSyntax returned = compilation.Programs
            .SelectMany(p => p.Tree.Root.DescendantNodesAndSelf())
            .OfType<ReturnStatementSyntax>()
            .Single();

        Assert.True(condition.GetCondition(returned).IsAlways, condition.GetCondition(returned).ToString());

        // 巻き上げで使う箇所をすべて補えたので、構成ごとに展開し直していない。
        Assert.Empty(compilation.SymbolVariants);
    }

    [Fact]
    public void 巻き上げを諦めた箇所があればシンボルは構成ごとに展開し直す()
    {
        // 間に指令がある文は複製しない。その文の CTYPE は既定の構成の値でしか展開されない。
        // ほかの使い方を補えていても、そのシンボルは構成ごとの展開がまだ要る。
        ShaderCompilation compilation = CompileFolded(
            """
            #ifdef _A
            #define CTYPE float3
            #else
            #define CTYPE float4
            #endif

            float4 Use()
            {
                CTYPE d = 1.0;
                CTYPE e = CTYPE(
            #ifdef _B
                    1, 2, 3, 4
            #else
                    0, 0, 0, 0
            #endif
                );
                return d.x + e.x;
            }
            """,
            "#pragma shader_feature_local _A\n#pragma shader_feature_local _B");

        Assert.Contains(compilation.SymbolVariants, v => v.EnabledSymbols.Contains("_A"));
    }

    [Fact]
    public void 同じ場所が別の形になる文は両方を選択肢として残す()
    {
        // 諦めて Unknown にすると、その文では条件を根拠にするルールが何も言えなくなる。
        // 既定の構成の側とバリアントの側の両方を、それぞれの条件のもとに残す
        // (SuperC の static choice にあたる)。
        ShaderCompilation compilation = CompileFolded(
            DifferentFormBody,
            "#pragma shader_feature_local _NORMALMAP",
            hoist: false);

        ConditionMap condition = compilation.GetConditionMap();

        // 追えなかった箇所として諦めていないこと。
        Assert.True(condition.IsComplete);

        VariableDeclarationSyntax baseline = compilation.Programs
            .SelectMany(p => p.Tree.Root.DescendantNodesAndSelf())
            .OfType<VariableDeclarationSyntax>()
            .Single(d => d.Variables.Any(v => v.Name == "varying"));

        // 既定の構成の側は「シンボルが無効なとき」。
        Assert.Equal("!_NORMALMAP", condition.GetCondition(baseline).ToString());

        // バリアントの側は、同じ親へ入れるものとして「シンボルが有効なとき」。
        ConditionalNode inserted = Assert.Single(
            condition.GetInsertedChildren((HlslSyntaxNode)baseline.Parent!),
            c => c.Node is VariableDeclarationSyntax);

        Assert.Equal("_NORMALMAP", inserted.Condition.ToString());
    }

    [Fact]
    public void 違いのある文だけを分けまわりの文は無条件のままにする()
    {
        // ブロックや関数まで丸ごと分けると、どの構成にもあるコードまで条件付きになる。
        // そうなると、そのコードの誤りが「片方の構成だけの話」に見えてしまう。
        ShaderCompilation compilation = CompileFolded(
            DifferentFormBody,
            "#pragma shader_feature_local _NORMALMAP",
            hoist: false);

        ConditionMap condition = compilation.GetConditionMap();

        VariableDeclarationSyntax kept = compilation.Programs
            .SelectMany(p => p.Tree.Root.DescendantNodesAndSelf())
            .OfType<VariableDeclarationSyntax>()
            .Single(d => d.Variables.Any(v => v.Name == "kept"));

        Assert.True(condition.GetCondition(kept).IsAlways, condition.GetCondition(kept).ToString());
    }
}
