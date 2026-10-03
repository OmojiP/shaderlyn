using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Tests;

/// <summary>
/// <c>#if</c> の書き方ごとに、両方の分岐を 1 本の木に並べるか、構成ごとに展開し直す (バリアント) かを検証する。
/// </summary>
/// <remarks>
/// <para>
/// <b>並べられる形でバリアントを作ると、解析が遅くなるうえに上限 (<c>SL0003</c>) に早く届く。</b>
/// 並べられない形で並べると、別の構成の中身で型を求めて誤検出になる。
/// どちらに倒れたかは指摘の数には現れにくいので、形ごとに作った構成を直接確かめる。
/// </para>
/// <para>
/// 期待値の「バリアント」は、有効にしたシンボルを <c>+</c> でつなぎ、構成を <c> / </c> で区切ったものである。
/// </para>
/// </remarks>
public sealed class VariantShapeTests
{
    /// <summary>コードを解析する。</summary>
    /// <param name="code">解析するコード。</param>
    /// <param name="headers">取り込むヘッダ。パスから中身への対応。</param>
    /// <returns>解析結果。</returns>
    private static ShaderCompilation Compile(string code, params (string Path, string Content)[] headers)
    {
        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(headers.ToDictionary(h => h.Path, h => h.Content)),
        };

        return ShaderCompilation.CreateForHlsl(SourceText.From(code, "Shape.hlsl"), options);
    }

    /// <summary>作った構成を、期待値と同じ書き方にする。</summary>
    /// <param name="compilation">解析結果。</param>
    /// <returns>構成を並べた文字列。</returns>
    /// <remarks>まとめて有効にした構成は <c>{_A,_B}</c> と書く。論理積の組 (<c>_A+_B</c>) とは意味が違う。</remarks>
    private static string DescribeVariants(ShaderCompilation compilation)
        => string.Join(" / ", compilation.SymbolVariants.Select(v => v.IsPacked
            ? "{" + string.Join(",", v.EnabledSymbols) + "}"
            : string.Join("+", v.EnabledSymbols)));

    /// <summary>どれかの木にある、その名前の関数の仮引数の数を集める。</summary>
    /// <param name="compilation">解析結果。</param>
    /// <param name="name">関数の名前。</param>
    /// <returns>仮引数の数。</returns>
    private static HashSet<int> ParameterCounts(ShaderCompilation compilation, string name)
        => [
            .. compilation.Programs.Concat(compilation.SymbolVariants)
                .SelectMany(p => p.Tree.Root.Declarations.OfType<FunctionDeclarationSyntax>())
                .Where(f => f.Name == name)
                .Select(f => f.ParameterList.Count()),
        ];

    /// <summary>名前のトークンを含む宣言の、既定の木での出現条件を求める。</summary>
    /// <param name="compilation">解析結果。</param>
    /// <param name="name">宣言の名前。</param>
    /// <returns>出現条件。</returns>
    private static SymbolCondition ConditionOfDeclaration(ShaderCompilation compilation, string name)
    {
        VariableDeclarationSyntax declaration = compilation.Programs[0].Tree.Root
            .DescendantNodesAndSelf()
            .OfType<VariableDeclarationSyntax>()
            .First(d => d.Variables.Any(v => v.Name == name));

        return compilation.GetConditionMap().GetCondition(declaration);
    }

    /// <summary>並べられる形と、並べられない形。</summary>
    /// <returns>コードと、作るはずの構成。</returns>
    public static TheoryData<string, string, string> Shapes() => new()
    {
        {
            "01 文の単位で閉じている",
            "#pragma multi_compile _ _A\nfloat F() {\n#ifdef _A\n    return 1;\n#else\n    return 2;\n#endif\n}",
            ""
        },
        {
            "02 文の途中で分かれる",
            "#pragma multi_compile _ _A\nfloat F() {\n    float m\n#ifdef _A\n        = 1\n#else\n        = 2\n#endif\n        ;\n    return m;\n}",
            "_A"
        },
        {
            "03 if の頭だけを分ける",
            "#pragma multi_compile _ _A\nfloat F(float y, float b, float d) {\n#ifdef _A\n    if (y < b)\n#else\n    y = d - y;\n    if (y < d)\n#endif\n    { return 1; }\n    return 0;\n}",
            "_A"
        },
        {
            // 使う文を定義ごとに複製すれば (条件の巻き上げ)、どの構成の展開も 1 本の木に載る。
            "04 構成で中身が変わるマクロを文で使う",
            "#pragma multi_compile _ _A\n#ifdef _A\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\nfloat F() { CTYPE color = 1; return color.x; }",
            ""
        },
        {
            "05 構成で中身が変わるマクロを式で使う",
            "#pragma multi_compile _ _A\n#ifdef _A\n#define SCALE(x) (x * 2)\n#else\n#define SCALE(x) (x)\n#endif\nfloat F(float v) { return SCALE(v) + SCALE(v * 3); }",
            ""
        },
        {
            "06 構成で中身が変わるマクロを関数の頭で使う",
            "#pragma multi_compile _ _A\n#ifdef _A\n#define EXTRA_PARAM , float extra\n#else\n#define EXTRA_PARAM\n#endif\nfloat F(float v EXTRA_PARAM) { return v; }",
            ""
        },
        {
            // 別のマクロの本体の中での展開は複製できない。既定の構成の値でしか展開されない。
            "15 構成で中身が変わるマクロを別のマクロの中で使う",
            "#pragma multi_compile _ _A\n#ifdef _A\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\n#define COLOR_TYPE CTYPE\nfloat F() { COLOR_TYPE color = 1; return color.x; }",
            "_A"
        },
        {
            // 定義のほかにコードもある分岐は、並べなかった側のコードがどの木にも載らない。
            "16 マクロを定義する分岐にコードもある",
            "#pragma multi_compile _ _A\n#ifdef _A\n#define CTYPE float3\nfloat G() { return 1; }\n#else\n#define CTYPE float4\n#endif\nfloat F() { CTYPE color = 1; return color.x; }",
            "_A"
        },
        {
            // 16 の書き換え。定義とコードを別の #ifdef に分ける。
            "17 マクロの定義とコードを別の分岐に書く",
            "#pragma multi_compile _ _A\n#ifdef _A\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\n#ifdef _A\nfloat G() { return 1; }\n#endif\nfloat F() { CTYPE color = 1; return color.x; }",
            ""
        },
        {
            // 02 の書き換え。
            "18 文ごと分ける",
            "#pragma multi_compile _ _A\nfloat F() {\n#ifdef _A\n    float m = 1;\n#else\n    float m = 2;\n#endif\n    return m;\n}",
            ""
        },
        {
            "19 仮引数を条件で足す",
            "#pragma multi_compile _ _A\nfloat Helper(float a\n#ifdef _A\n    , float b\n#endif\n    ) { return a; }",
            "_A"
        },
        {
            // 19 の書き換え。
            "20 関数ごと分ける",
            "#pragma multi_compile _ _A\n#ifdef _A\nfloat Helper(float a, float b) { return a; }\n#else\nfloat Helper(float a) { return a; }\n#endif",
            ""
        },
        {
            // _B の分岐の中の V は、_A の値でしか展開されない。
            "21 構成で変わるマクロを別のシンボルの分岐の中で使う",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define V 1\n#else\n#define V 2\n#endif\nfloat F() {\n#ifdef _B\n    return V;\n#else\n    return 0;\n#endif\n}",
            "_B"
        },
        {
            // 21 の書き換え。マクロを使う文を分岐の外へ出す。
            "22 構成で変わるマクロを分岐の外で使う",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define V 1\n#else\n#define V 2\n#endif\nfloat F() {\n    float v = V;\n#ifdef _B\n    return v;\n#else\n    return 0;\n#endif\n}",
            ""
        },
        {
            // 定義を条件付きで覚える領域は #define / #else / #endif だけのものに限る。
            "23 #elif で定義を書き分ける",
            "#pragma multi_compile _ _A _B\n#if defined(_A)\n#define CTYPE float3\n#elif defined(_B)\n#define CTYPE float2\n#else\n#define CTYPE float4\n#endif\nfloat F() { CTYPE d = 1; return d.x; }",
            "_A / _B"
        },
        {
            "24 #undef してから定義し直す",
            "#pragma multi_compile _ _A\n#define CTYPE float4\n#ifdef _A\n#undef CTYPE\n#define CTYPE float3\n#endif\nfloat F() { CTYPE d = 1; return d.x; }",
            "_A"
        },
        {
            // 外側が文の途中で分かれていると、内側を並べても外側の分岐が片方しか木に載らない。
            "25 並べられない #if の中の #if",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\nfloat F() {\n    float m\n#ifdef _A\n        = 1;\n#ifdef _B\n    m = 3;\n#endif\n    float k\n#else\n        = 2;\n    float k\n#endif\n        = 0;\n    return m + k;\n}",
            "_A / _B / _A+_B"
        },
        {
            // 25 の書き換え。外側を文ごとに分ける。
            "26 文ごとに分けた #if の中の #if",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\nfloat F() {\n#ifdef _A\n    float m = 1;\n#ifdef _B\n    m = 3;\n#endif\n#else\n    float m = 2;\n#endif\n    return m;\n}",
            ""
        },
        {
            "27 並べられない #if に続く #elif",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\nfloat F() {\n    float m\n#ifdef _A\n        = 1\n#elif defined(_B)\n        = 2\n#else\n        = 3\n#endif\n        ;\n    return m;\n}",
            "_A / _B"
        },
        {
            "28 文ごとに分けた #elif",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\nfloat F() {\n#ifdef _A\n    float m = 1;\n#elif defined(_B)\n    float m = 2;\n#else\n    float m = 3;\n#endif\n    return m;\n}",
            ""
        },
        {
            // 要素は , で区切るので、分岐の中身が文の単位で閉じない。
            "29 初期化の要素を条件で足す",
            "#pragma multi_compile _ _A\nfloat4 F() {\n    float4 c = { 1, 2, 3\n#ifdef _A\n        , 4\n#endif\n    };\n    return c;\n}",
            "_A"
        },
        {
            // 29 の書き換え。
            "30 初期化の文ごと分ける",
            "#pragma multi_compile _ _A\nfloat4 F() {\n#ifdef _A\n    float4 c = { 1, 2, 3, 4 };\n#else\n    float4 c = { 1, 2, 3, 0 };\n#endif\n    return c;\n}",
            ""
        },
        {
            // 1 つの文で複製できるのは 1 つのマクロだけである。VTYPE はどちらか 1 つの構成の値でしか展開されない。
            "31 条件で中身が変わるマクロを 1 つの文で 2 つ使う",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\n#ifdef _B\n#define VTYPE float2\n#else\n#define VTYPE float\n#endif\nfloat F() { CTYPE f = VTYPE(1); return f.x; }",
            "_B"
        },
        {
            // 31 の書き換え。文を分ければ、続けて書いた文もそれぞれ複製する。
            "32 条件で中身が変わるマクロを文ごとに分けて使う",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\n#ifdef _B\n#define VTYPE float2\n#else\n#define VTYPE float\n#endif\nfloat F() { CTYPE d = 1; VTYPE e = 1; return d.x + e.x; }",
            ""
        },
        {
            // 複製すると、文の中の #if を 2 度処理することになる。
            "34 条件で中身が変わるマクロを使う文の中に #if がある",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\nfloat F() {\n    CTYPE d = CTYPE(\n#ifdef _B\n        1, 2, 3, 4\n#else\n        0, 0, 0, 0\n#endif\n    );\n    return d.x;\n}",
            "_A / _B"
        },
        {
            // 34 の書き換え。#if で分ける部分を、マクロを使わない文にする。
            "35 #if で分ける部分をマクロを使わない文にする",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define CTYPE float3\n#else\n#define CTYPE float4\n#endif\nfloat F() {\n#ifdef _B\n    float4 init = float4(1, 2, 3, 4);\n#else\n    float4 init = 0;\n#endif\n    CTYPE d = (CTYPE)init;\n    return d.x;\n}",
            ""
        },
        {
            // 1 以外と比べる条件は、シンボルの条件として読めない。値として解いた構成を別に作る。
            "33 シンボルを値で比べる",
            "#pragma multi_compile _ _A\nfloat F() {\n#if _A == 1\n    return 1;\n#else\n    return 2;\n#endif\n}",
            "_A"
        },
        {
            "07 キーワードと構成によらない比較",
            "#pragma multi_compile _ _A\n#define TARGET_LEVEL 50\nfloat F() {\n#if defined(_A) && (TARGET_LEVEL >= 45)\n    return 1;\n#else\n    return 2;\n#endif\n}",
            ""
        },
        {
            // キーワードの値は 1 なので、_A | _B は _A || _B と同じである。
            "09 ビット演算で書いた論理和",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\nfloat F() {\n#if (_A | _B)\n    return 1;\n#else\n    return 2;\n#endif\n}",
            ""
        },
        {
            "10 論理積で文の途中が分かれる",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\nfloat F() {\n    float m\n#if defined(_A) && defined(_B)\n        = 1\n#else\n        = 2\n#endif\n        ;\n    return m;\n}",
            "_A / _B / _A+_B"
        },
        {
            "11 _ の無い行の #else",
            "#pragma multi_compile MODE_A MODE_B\nfloat F() {\n    float m\n#ifdef MODE_A\n        = 1\n#else\n        = 2\n#endif\n        ;\n    return m;\n}",
            "MODE_B"
        },
        {
            // USE_A_IN_CODE を使うのは、それを定義した _A の分岐の中だけである。
            "12 分岐の中で定義したマクロを同じ分岐の中のコードで使う",
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define USE_A_IN_CODE 1\nfloat G() { return USE_A_IN_CODE; }\n#ifdef _B\nfloat H() { return 2; }\n#endif\n#endif",
            ""
        },
        {
            "14 構成によらない #if の中のキーワード",
            "#pragma multi_compile _ _A\n#if !defined(SHADER_API_GLES2)\nfloat F() {\n#ifdef _A\n    return 1;\n#else\n    return 2;\n#endif\n}\n#endif",
            ""
        },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void 書き方ごとに並べるかバリアントを作るかが決まる(string shape, string code, string expected)
    {
        ShaderCompilation compilation = Compile(code);

        Assert.True(expected == DescribeVariants(compilation), $"{shape}: {DescribeVariants(compilation)}");
    }

    /// <summary>
    /// 取り込みの切り替えを、取り込んだヘッダのマクロの使い方ごとに検証する。
    /// </summary>
    /// <param name="code">解析するコード。</param>
    /// <param name="expected">作るはずの構成。</param>
    /// <remarks>
    /// <para>
    /// ヘッダが定義したマクロをコードで使うと、その展開はどちらか 1 つの構成のものになる。
    /// マクロをこのファイルの #ifdef で定義し直せば、使う文を定義ごとに複製できる。
    /// </para>
    /// <para>
    /// 3 つ目は、取り込みを切り替える領域の終わりを求めずに中身を調べていたとき、
    /// 後ろの #define VALUE_TYPE まで領域の中身として読んで並べられなかった形である。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(
        "#pragma multi_compile _ _A\n#ifdef _A\n#include \"a.hlsl\"\n#else\n#include \"b.hlsl\"\n#endif\nfloat F() { return Shade(); }",
        "")]
    [InlineData(
        "#pragma multi_compile _ _A\n#ifdef _A\n#include \"ma.hlsl\"\n#else\n#include \"mb.hlsl\"\n#endif\nfloat F() { VALUE_TYPE v = 1; return v.x; }",
        "_A")]
    [InlineData(
        "#pragma multi_compile _ _A\n#ifdef _A\n#include \"a.hlsl\"\n#else\n#include \"b.hlsl\"\n#endif\n#ifdef _A\n#define VALUE_TYPE float3\n#else\n#define VALUE_TYPE float4\n#endif\nfloat F() { VALUE_TYPE v = Shade(); return v.x; }",
        "")]
    public void 取り込みの切り替えはヘッダのマクロをコードで使わなければ並べる(string code, string expected)
    {
        ShaderCompilation compilation = Compile(
            code,
            ("a.hlsl", "float Shade() { return 1; }"),
            ("b.hlsl", "float Shade() { return 2; }"),
            ("ma.hlsl", "#define VALUE_TYPE float3"),
            ("mb.hlsl", "#define VALUE_TYPE float4"));

        Assert.Equal(expected, DescribeVariants(compilation));
    }

    [Fact]
    public void 互いに関係しないシンボルは1回の展開にまとめて上限を1つ分だけ使う()
    {
        ShaderCompilation compilation = ShaderCompilation.CreateForHlsl(
            SourceText.From(TwoIndependentSplits, "Shape.hlsl"),
            new SemanticsOptions { MaxSymbolVariants = 1 });

        Assert.Equal("{_A,_B}", DescribeVariants(compilation));
        Assert.Empty(compilation.UnexploredSymbols);
    }

    [Fact]
    public void ビット演算の論理和はどちらかのキーワードの条件として読む()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#if (_A | _B)\nfloat4 _Either;\n#endif\nfloat F() { return 0; }");

        Assert.Equal(
            SymbolCondition.Symbol("_A").Or(SymbolCondition.Symbol("_B")),
            ConditionOfDeclaration(compilation, "_Either"));
    }

    [Fact]
    public void 分岐の中で定義したマクロを分岐の外でも使うならバリアントを作る()
    {
        // _A が無い構成では USE_A は定義されていない。並べたままでは、その構成の展開を取り違える。
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#ifdef _A\n#define USE_A 1\nfloat G() { return USE_A; }\n#endif\nfloat F() { return USE_A; }");

        Assert.Equal("_A", DescribeVariants(compilation));
    }

    /// <summary>
    /// 未定義の名前を 0 として解くことを検証する。
    /// </summary>
    /// <remarks>
    /// C の規則では、マクロ展開の後に <c>#if</c> に残った識別子は 0 である。fxc と DXC も同じ。
    /// キーワード以外の項が解けてキーワードが残らなければ、どの構成でも同じ分岐を通るので展開し直さない。
    /// </remarks>
    [Fact]
    public void 未定義の名前は0として解きキーワードが残らなければ展開しない()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\nfloat F() {\n#if defined(_A) && (UNDEFINED_LEVEL >= 45)\n    return 1;\n#else\n    return 2;\n#endif\n}");

        Assert.Equal("", DescribeVariants(compilation));
    }

    /// <summary>
    /// マクロの本体を通してキーワードを見る条件を読めなかったら、そのキーワードも並べられなかったと数えることを検証する。
    /// </summary>
    /// <remarks>
    /// HDRP の TemporalAntiAliasing.shader は <c>#define VELOCITY_REJECTION (defined(ENABLE_MV_REJECTION) &amp;&amp; 0)</c> を
    /// 品質のキーワードごとに書き分け、<c>#if VELOCITY_REJECTION</c> で見る。行に <c>ENABLE_MV_REJECTION</c> は現れない。
    /// </remarks>
    [Fact]
    public void マクロの本体を通して見るキーワードも並べられなかったと数える()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile LOW HIGH\n#pragma multi_compile _ _REJECT\n#ifdef LOW\n#define REJECTION (defined(_REJECT) && 0)\n#else\n#define REJECTION defined(_REJECT)\n#endif\nfloat F() {\n#if REJECTION\n    return 1;\n#else\n    return 0;\n#endif\n}");

        Assert.Contains(
            compilation.Programs[0].Tree.PreprocessResult.BothBranchDeclines,
            d => d.Symbol == "_REJECT" && d.Reason == BothBranchDeclineReason.UnreadableCondition);
        Assert.Contains("_REJECT", DescribeVariants(compilation), StringComparison.Ordinal);
    }

    [Fact]
    public void 別のカーネルの指定で読み飛ばす分岐のキーワードは展開しない()
    {
        // KFinal のカーネルでは MAX_Z が定義されず、_PLANAR の分岐はどの構成でも読まれない (HDRP の GenerateMaxZ.compute の形)。
        ShaderCompilation compilation = Compile(
            "#pragma kernel KMax MAX_Z=1\n#pragma kernel KFinal\n#pragma multi_compile _ _PLANAR\nRWStructuredBuffer<float> R;\n#if MAX_Z\nfloat Depth() {\n#ifdef _PLANAR\n    return 1;\n#else\n    return 2;\n#endif\n}\n[numthreads(1,1,1)] void KMax(uint id : SV_DispatchThreadID) { R[id] = Depth(); }\n#endif\n[numthreads(1,1,1)] void KFinal(uint id : SV_DispatchThreadID) { R[id] = 0; }");

        Assert.Equal("", DescribeVariants(compilation));
    }

    [Fact]
    public void 取り込むヘッダを切り替えてマクロを使うならバリアントを作る()
    {
        // 08: ヘッダごとに同じ名前のマクロを違う中身で定義していて、それをコードで使う。
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#ifdef _A\n#include \"inc_a.hlsl\"\n#else\n#include \"inc_b.hlsl\"\n#endif\nfloat F() { return VALUE_FROM_HEADER; }",
            ("inc_a.hlsl", "#define VALUE_FROM_HEADER 1\n"),
            ("inc_b.hlsl", "#define VALUE_FROM_HEADER 2.0\n"));

        Assert.Equal("_A", DescribeVariants(compilation));
    }

    [Fact]
    public void インクルードガードの中のキーワードの分岐は並べる()
    {
        // 13: ヘッダはほぼすべてインクルードガードで囲まれている。
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#include \"guard.hlsl\"\nfloat F() { return G(); }",
            ("guard.hlsl", "#ifndef GUARD_H\n#define GUARD_H\nfloat G() {\n#ifdef _A\n    return 1;\n#else\n    return 2;\n#endif\n}\n#endif\n"));

        Assert.Equal("", DescribeVariants(compilation));
    }

    /// <summary>
    /// 解析するファイルが宣言したキーワードで、ヘッダの機能だけを切り替える形を検証する。
    /// </summary>
    /// <remarks>
    /// <c>.shader</c> で <c>#pragma multi_compile _ _FOG_ON</c> を宣言し、共通の <c>.hlsl</c> の中身だけを切り替える運用がある。
    /// 解析するファイルには <c>_FOG_ON</c> の条件が 1 つも無い。
    /// ヘッダの分岐を並べられないなら、キーワードを有効にした構成を作らないと、その側はどの木にも載らない。
    /// </remarks>
    [Fact]
    public void ヘッダの条件でしか使わないキーワードの両側を読む()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _FOG_ON\n#include \"Fog.hlsl\"\nhalf4 Frag(half4 c) { return ApplyFog(c); }",
            ("Fog.hlsl", "#ifndef FOG_INCLUDED\n#define FOG_INCLUDED\n#ifdef _FOG_ON\nhalf4 ApplyFog(half4 c\n#else\nhalf4 ApplyFog(half4 c, float d\n#endif\n) { return c; }\n#endif\n"));

        Assert.Equal([1, 2], ParameterCounts(compilation, "ApplyFog").Order());
    }

    /// <summary>
    /// ヘッダの分岐で定義したマクロを、条件の中でだけ使う形を検証する。
    /// </summary>
    /// <remarks>
    /// <c>#ifdef _A</c> の中の <c>#define USE_A</c> は、<c>_A</c> のときだけ定義される。
    /// その条件を覚えていれば、後の <c>#if defined(USE_A)</c> を「<c>_A</c> のとき」と読めるので、両方の分岐を並べられる。
    /// </remarks>
    [Fact]
    public void ヘッダの分岐で定義して条件でだけ使うマクロは並べる()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#include \"Features.hlsl\"\nfloat F() { return 0; }",
            ("Features.hlsl", "#ifndef FEATURES_INCLUDED\n#define FEATURES_INCLUDED\n#ifdef _A\n#define USE_A 1\n#endif\n#if defined(USE_A)\nfloat4 _OnlyWithA;\n#endif\n#endif\n"));

        Assert.Equal("", DescribeVariants(compilation));
        Assert.Equal(SymbolCondition.Symbol("_A"), ConditionOfDeclaration(compilation, "_OnlyWithA"));
    }

    /// <summary>
    /// ヘッダの分岐で定義したマクロで、別のマクロの中身を切り替える形を検証する。
    /// </summary>
    /// <remarks>
    /// URP のヘッダは <c>DOTS_INSTANCING_ON</c> から <c>UNITY_DOTS_INSTANCING_ENABLED</c> を導き、
    /// それを見て <c>SAMPLE_TEXTURE2D_LIGHTMAP</c> の引数の個数を切り替える。
    /// 導く側を並べても、切り替える側を既定の構成で正しく選べなければならない。
    /// </remarks>
    [Fact]
    public void ヘッダの分岐で導いたマクロで中身を切り替えても既定の構成を崩さない()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ DOTS_ON\n#include \"Lightmap.hlsl\"\nfloat F() { return SAMPLE_LM(1, 2, 3); }",
            ("Lightmap.hlsl", "#ifndef LM_INCLUDED\n#define LM_INCLUDED\n#ifdef DOTS_ON\n#define DOTS_ENABLED\n#endif\n#if defined(DOTS_ENABLED)\n#define SAMPLE_LM(a, b, c, d) Sample4(a, b, c, d)\n#else\n#define SAMPLE_LM(a, b, c) Sample3(a, b, c)\n#endif\n#endif\n"));

        Assert.Empty(compilation.Programs[0].Diagnostics);
    }

    /// <summary>
    /// 古いキーワードが新しいキーワードの名前を定義する形を検証する。
    /// </summary>
    /// <remarks>
    /// HDRP の Lit は、廃止した <c>_ENABLESPECULAROCCLUSION</c> が有効なら
    /// <c>_SPECULAR_OCCLUSION_FROM_BENT_NORMAL_MAP</c> を定義して、新しいコードを通す。
    /// 新しいキーワードの分岐は「新しいキーワードか古いキーワードのとき」に存在する。
    /// </remarks>
    [Fact]
    public void キーワードの名前を別のキーワードで定義する形は条件を足し合わせて並べる()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _OLD_FEATURE\n#pragma multi_compile _ _NEW_FEATURE\n#ifdef _OLD_FEATURE\n#define _NEW_FEATURE\n#endif\n#ifdef _NEW_FEATURE\nfloat4 _WithFeature;\n#endif\nfloat F() { return 0; }");

        Assert.Equal("", DescribeVariants(compilation));
        Assert.Equal(
            SymbolCondition.Symbol("_NEW_FEATURE").Or(SymbolCondition.Symbol("_OLD_FEATURE")),
            ConditionOfDeclaration(compilation, "_WithFeature"));
    }

    /// <summary>
    /// ヘッダの並べた分岐の中の <c>#undef</c> を、条件として扱えないものとして並べ直すことを検証する。
    /// </summary>
    /// <remarks>
    /// 「<c>_A</c> のときは定義されていない」は、定義がある条件を足し合わせても書けない。
    /// 並べたままにすると <c>X</c> がどの構成でも消え、<c>_WithX</c> がどの構成にも無いことになる。
    /// </remarks>
    [Fact]
    public void ヘッダの並べた分岐でマクロを消すなら並べずに展開し直す()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#include \"Undef.hlsl\"\n#ifdef _A\nfloat4 _RootA;\n#endif\nfloat F() { return 0; }",
            ("Undef.hlsl", "#ifndef UNDEF_INCLUDED\n#define UNDEF_INCLUDED\n#define X 1\n#ifdef _A\n#undef X\n#endif\n#if defined(X)\nfloat4 _WithX;\n#endif\n#endif\n"));

        Assert.Equal(SymbolCondition.Symbol("_A", false), ConditionOfDeclaration(compilation, "_WithX"));
    }

    /// <summary>
    /// 並べられなかったキーワードで定義したマクロを、ヘッダの条件で見る形を検証する。
    /// </summary>
    /// <remarks>
    /// HDRP の Lit.shader は <c>#ifdef _HEIGHTMAP</c> の中で <c>_CONSERVATIVE_DEPTH_OFFSET</c> を定義し、
    /// core の D3D11.hlsl がそれを見て <c>SV_POSITION_QUALIFIERS</c> を <c>#undef</c> して定義し直す。
    /// <c>#undef</c> があるので <c>_HEIGHTMAP</c> は並べられない。その木でヘッダの分岐だけを並べると、
    /// 既定の構成に <c>_HEIGHTMAP</c> のときの定義が漏れる。
    /// </remarks>
    [Fact]
    public void 並べなかったキーワードで定義したマクロの分岐はその木で並べない()
    {
        ShaderCompilation compilation = Compile(
            "#pragma shader_feature_local _HEIGHTMAP\n#pragma shader_feature_local _OTHER\n#ifdef _HEIGHTMAP\n#define _CONSERVATIVE\n#endif\n#include \"Api.hlsl\"\nstruct V { QUALIFIERS float4 p : SV_Position; };\nfloat F() { return 0; }",
            ("Api.hlsl", "#ifndef API_INCLUDED\n#define API_INCLUDED\n#define QUALIFIERS\n#ifdef _CONSERVATIVE\n#undef QUALIFIERS\n#define QUALIFIERS linear\n#endif\n#endif\n"));

        Assert.Empty(compilation.Programs[0].Tree.PreprocessResult.Macros["QUALIFIERS"].Body);
        Assert.Equal("_HEIGHTMAP", DescribeVariants(compilation));
        Assert.Equal(
            "linear",
            string.Join(" ", compilation.SymbolVariants[0].Tree.PreprocessResult.Macros["QUALIFIERS"].Body.Select(t => t.Text)));
    }

    /// <summary>
    /// 並べてよい領域を、取り込んだヘッダの中でも位置で決めることを検証する。
    /// </summary>
    /// <remarks>
    /// バリアントは既定の構成が並べた領域だけを並べる。ヘッダの判断もマクロの状態で変わるので、
    /// ヘッダの領域も記録と制限の対象にする。
    /// </remarks>
    [Fact]
    public void 並べてよい領域はヘッダの中でも位置で決める()
    {
        PreprocessorOptions options = new()
        {
            BothBranchSymbols = ["_A"],
            ConfigurationSymbols = ["_A"],
            IncludeResolver = new InMemoryIncludeResolver(new Dictionary<string, string>
            {
                ["H.hlsl"] = "float4 _Common;\n#ifdef _A\nfloat4 _WithA;\n#endif\n",
            }),
        };

        SourceText root = SourceText.From("#include \"H.hlsl\"\nfloat4 _Root;\n", "Root.hlsl");

        PreprocessResult free = new HlslPreprocessor(options).Preprocess(root);
        PreprocessResult same = new HlslPreprocessor(
            options with { MergeOnlyRegions = new MergedRegionSet(free.MergedRegions) }).Preprocess(root);
        PreprocessResult none = new HlslPreprocessor(
            options with { MergeOnlyRegions = new MergedRegionSet([]) }).Preprocess(root);

        Assert.Contains(free.MergedRegions, r => r.FilePath.EndsWith("H.hlsl", StringComparison.Ordinal));
        Assert.Contains(same.Tokens, t => t.Text == "_WithA");
        Assert.DoesNotContain(none.Tokens, t => t.Text == "_WithA");
        Assert.Contains(
            none.BothBranchDeclines,
            d => d.Symbol == "_A" && d.Reason == BothBranchDeclineReason.OutsideDefaultMergedRegions);
    }

    /// <summary>
    /// バリアントが、キーワードで導いたマクロの条件を既定の構成と同じに読むことを検証する。
    /// </summary>
    /// <remarks>
    /// HDRP の Lit.shader は <c>#if !defined(_DISABLE_SSR_TRANSPARENT)</c> の中で <c>WRITE_NORMAL_BUFFER</c> を定義し、
    /// <c>#ifdef WRITE_NORMAL_BUFFER</c> で取り込むヘッダを切り替える。
    /// バリアントがマクロの条件を覚えずに展開すると、その分岐を並べず、関係の無いキーワードのバリアントでも
    /// 既定の木との違いが出て、そのキーワードの否定が条件に付く。
    /// </remarks>
    [Fact]
    public void バリアントもキーワードで導いたマクロの条件を覚えて並べる()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#if !defined(_B)\n#define WNB\n#endif\n#ifdef WNB\nfloat4 _WithWnb;\n#else\nfloat4 _WithoutWnb;\n#endif\nfloat F() {\n    float m\n#ifdef _A\n        = 1\n#else\n        = 2\n#endif\n        ;\n    return m;\n}");

        Assert.Equal("_A", DescribeVariants(compilation));
        Assert.Equal(SymbolCondition.Symbol("_B"), ConditionOfDeclaration(compilation, "_WithoutWnb"));
        Assert.Equal(SymbolCondition.Symbol("_B", false), ConditionOfDeclaration(compilation, "_WithWnb"));
    }

    /// <summary>関数 2 つが、それぞれ別のキーワードで文の途中から分かれるコード。</summary>
    private const string TwoIndependentSplits =
        "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n"
        + "float F() {\n    float m\n#ifdef _A\n        = 1\n#else\n        = 2\n#endif\n        ;\n    return m;\n}\n"
        + "float G() {\n    float n\n#ifdef _B\n        = 3\n#else\n        = 4\n#endif\n        ;\n    return n;\n}";

    /// <summary>木の中から、その名前の変数の宣言を探す。</summary>
    /// <param name="program">対象の木。</param>
    /// <param name="name">変数の名前。</param>
    /// <returns>見つかった宣言。</returns>
    private static VariableDeclarationSyntax FindDeclaration(AnalyzedProgram program, string name)
        => program.Tree.Root.DescendantNodesAndSelf()
            .OfType<VariableDeclarationSyntax>()
            .First(d => d.Variables.Any(v => v.Name == name));

    /// <summary>
    /// 互いに関係しないキーワードを 1 つの構成にまとめ、違いごとにそのキーワードの条件を付けることを検証する。
    /// </summary>
    /// <remarks>
    /// まとめた木の <c>float m = 1</c> は <c>_A</c> だけで現れる。論理積 (<c>_A かつ _B</c>) を付けると、
    /// <c>_A</c> だけを有効にした構成にこの宣言が無いことになる。
    /// </remarks>
    [Fact]
    public void 互いに関係しないキーワードは1つの構成にまとめる()
    {
        ShaderCompilation compilation = Compile(TwoIndependentSplits);

        Assert.Equal("{_A,_B}", DescribeVariants(compilation));

        AnalyzedProgram packed = compilation.SymbolVariants[0];
        Semantics.Conditional.ConditionMap map = compilation.GetConditionMap();

        Assert.Equal(SymbolCondition.Symbol("_A", false), ConditionOfDeclaration(compilation, "m"));
        Assert.Equal(SymbolCondition.Symbol("_B", false), ConditionOfDeclaration(compilation, "n"));
        Assert.Equal(SymbolCondition.Symbol("_A"), map.GetCondition(FindDeclaration(packed, "m")));
        Assert.Equal(SymbolCondition.Symbol("_B"), map.GetCondition(FindDeclaration(packed, "n")));
    }

    /// <summary>
    /// 一方のキーワードの違いが、もう一方の違いで使う名前を宣言するならまとめないことを検証する。
    /// </summary>
    /// <remarks>
    /// <c>_A</c> だけを有効にした構成では <c>_Extra</c> が宣言されておらず、コンパイルできない。
    /// まとめた木では <c>_B</c> が <c>_Extra</c> を宣言するので、その誤りが隠れる。
    /// </remarks>
    [Fact]
    public void 一方が宣言する名前をもう一方が使うならまとめない()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n"
            + "float4\n#ifdef _B\n    _Extra\n#else\n    _Other\n#endif\n    ;\n"
            + "float F() {\n    float m\n#ifdef _A\n        = _Extra.x\n#else\n        = 2\n#endif\n        ;\n    return m;\n}");

        Assert.Equal("_A / _B", DescribeVariants(compilation));
    }

    [Fact]
    public void 同じ関数の中で変わるキーワードはまとめない()
    {
        // _B が同じ関数に return を足すと、_A だけのときの誤り (戻り値が無いなど) が隠れうる。
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n"
            + "float F() {\n    float m\n#ifdef _A\n        = 1\n#else\n        = 2\n#endif\n        ;\n"
            + "    float n\n#ifdef _B\n        = 3\n#else\n        = 4\n#endif\n        ;\n    return m + n;\n}");

        Assert.Equal("_A / _B", DescribeVariants(compilation));
    }

    [Fact]
    public void まとめないように設定すれば1つずつ展開する()
    {
        ShaderCompilation compilation = ShaderCompilation.CreateForHlsl(
            SourceText.From(TwoIndependentSplits, "Shape.hlsl"),
            new SemanticsOptions { PackIndependentSymbols = false });

        Assert.Equal("_A / _B", DescribeVariants(compilation));
    }

    [Fact]
    public void 分岐の中でマクロを定義するキーワードはまとめない()
    {
        // _A を有効にすると VALUE の中身が変わり、使う側の文は _A の連なりの外にある。
        // 別のマクロの本体を通して使うので、使う文を定義ごとに複製 (条件の巻き上げ) することもできない。
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define VALUE 1\n#else\n#define VALUE 2.0\n#endif\n"
            + "#define TWICE (VALUE * 2)\nfloat F() { return TWICE; }\n"
            + "float G() {\n    float n\n#ifdef _B\n        = 3\n#else\n        = 4\n#endif\n        ;\n    return n;\n}");

        Assert.Equal("_A / _B", DescribeVariants(compilation));
    }

    [Fact]
    public void キーワードで導いたマクロを条件でだけ使うキーワードはまとめる()
    {
        // USE_A は条件でしか使わない。#ifdef USE_A の連なりは _A を見ていることになり、違いのキーワードを決められる。
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n#ifdef _A\n#define USE_A\n#endif\n"
            + "float F() {\n    float m\n#ifdef USE_A\n        = 1\n#else\n        = 2\n#endif\n        ;\n    return m;\n}\n"
            + "float G() {\n    float n\n#ifdef _B\n        = 3\n#else\n        = 4\n#endif\n        ;\n    return n;\n}");

        Assert.Equal("{_A,_B}", DescribeVariants(compilation));

        // 既定の構成は USE_A が無い側 (= 2) を読む。並べた #define が効いたまま読むと = 1 の側になる。
        Assert.Equal(SymbolCondition.Symbol("_A", false), ConditionOfDeclaration(compilation, "m"));
        Assert.Equal(
            SymbolCondition.Symbol("_A"),
            compilation.GetConditionMap().GetCondition(FindDeclaration(compilation.SymbolVariants[0], "m")));
    }

    /// <summary>
    /// 並べなかった <c>#elif</c> に <c>defined</c> 無しで書いたキーワードも、条件に現れたものとして数えることを検証する。
    /// </summary>
    /// <remarks>
    /// core の FunctionTestsWave.compute は <c>#if EMULATE_WAVE_SIZE_8 ... #elif EMULATE_WAVE_SIZE_16</c> の形で見る。
    /// 並べなかった <c>#elif</c> の名前は記録されず、「条件のどこにも現れない」(HL0331) と報告され、
    /// そのキーワードを有効にした構成も作られなかった。
    /// </remarks>
    [Fact]
    public void 並べなかったelifに書いたキーワードも条件に現れたものとして展開する()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A _B\nfloat F() {\n    float m\n#if _A\n        = 1\n#elif _B\n        = 2\n#else\n        = 3\n#endif\n        ;\n    return m;\n}");

        Assert.Contains(compilation.Programs[0].Tree.PreprocessResult.ConditionalIdentifiers, t => t.Text == "_B");
        Assert.Equal("_A / _B", DescribeVariants(compilation));
    }

    /// <summary>
    /// 並べ直しのために外したキーワードで、条件がヘッダにしか無いものも、有効にした構成を作ることを検証する。
    /// </summary>
    /// <remarks>
    /// 並べる対象から外したキーワードの条件は、並べられなかったとも記録されない。
    /// 解析しているファイルに条件が無いと、候補のどこにも入らず、何も伝えずに調べないままになっていた。
    /// </remarks>
    [Fact]
    public void 並べ直しで外したキーワードは条件がヘッダにしか無くても展開する()
    {
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#include \"Def.hlsl\"\n#include \"Use.hlsl\"\nfloat F() { return V; }",
            ("Def.hlsl", "#ifndef DEF_INCLUDED\n#define DEF_INCLUDED\n#ifdef _A\n#define V 1\n#endif\n#endif\n"),
            ("Use.hlsl", "#ifndef USE_INCLUDED\n#define USE_INCLUDED\n#ifdef _A\nfloat4 _HeaderA;\n#endif\n#endif\n"));

        Assert.Equal("_A", DescribeVariants(compilation));
    }

    /// <summary>
    /// バリアントにしか無いノードに、そのバリアントが並べた分岐の条件も付くことを検証する。
    /// </summary>
    /// <remarks>
    /// HDRP の LutBuilder3D.compute は、<c>TONEMAPPING_ACES_APPROX</c> の木でだけ現れる呼び出しが
    /// 並べた <c>#ifdef HDR_COLORSPACE_CONVERSION</c> の中にある。並べた範囲を既定の木からしか集めていなかったため、
    /// 呼び出しに <c>HDR_COLORSPACE_CONVERSION</c> が付かず、その定義が無い構成があると誤って報告していた (HL0311)。
    /// </remarks>
    [Fact]
    public void バリアントにしか無いノードにもその木で並べた分岐の条件が付く()
    {
        // A_ON をコードで使うので _A は並べずに展開し直す。_A の木にしか #ifdef _H の中身が無い。
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _H\n#ifdef _A\n#define A_ON 1\n#endif\nfloat G() { return A_ON; }\n"
            + "float F() {\n    float m = 0;\n#ifdef _H\n#ifdef _A\n    float k = 5;\n#endif\n#endif\n    return m;\n}");

        AnalyzedProgram onlyA = compilation.SymbolVariants.Single(v => v.EnabledSymbols.SequenceEqual(["_A"]));

        Assert.Equal(
            SymbolCondition.Symbol("_A").And(SymbolCondition.Symbol("_H")),
            compilation.GetConditionMap().GetCondition(FindDeclaration(onlyA, "k")));
    }

    [Fact]
    public void 違いのキーワードを決められなければ1つずつ展開し直す()
    {
        // 1 つの文が _A と _B の両方で変わる。まとめた木の違いは、どちらのものとも言えない。
        ShaderCompilation compilation = Compile(
            "#pragma multi_compile _ _A\n#pragma multi_compile _ _B\n"
            + "float F() {\n    float m = 0\n#ifdef _A\n        + 1\n#endif\n#ifdef _B\n        + 2\n#endif\n        ;\n    return m;\n}");

        Assert.Equal("_A / _B", DescribeVariants(compilation));
        Assert.Empty(compilation.UnexploredSymbols);
    }

    [Fact]
    public void 使い回したヘッダでも定義がある条件を覚えている()
    {
        // ヘッダの展開結果は、別のファイルの解析でも使い回す。そのときも条件付きの定義を当て直す。
        const string code = "#pragma multi_compile _ _A\n#include \"Features.hlsl\"\n#if defined(USE_A)\nfloat4 _OnlyWithA;\n#endif\nfloat F() { return 0; }";

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(new Dictionary<string, string>
            {
                ["Features.hlsl"] = "#ifndef FEATURES_INCLUDED\n#define FEATURES_INCLUDED\n#ifdef _A\n#define USE_A 1\n#endif\n#endif\n",
            }),
            IncludeCache = new HlslIncludeCache(),
        };

        ShaderCompilation first = ShaderCompilation.CreateForHlsl(SourceText.From(code, "First.hlsl"), options);
        ShaderCompilation second = ShaderCompilation.CreateForHlsl(SourceText.From(code, "Second.hlsl"), options);

        Assert.Equal(SymbolCondition.Symbol("_A"), ConditionOfDeclaration(first, "_OnlyWithA"));
        Assert.Equal(SymbolCondition.Symbol("_A"), ConditionOfDeclaration(second, "_OnlyWithA"));
    }
}
