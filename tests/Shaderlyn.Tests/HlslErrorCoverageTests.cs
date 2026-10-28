using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL コンパイラのエラーコードとの対応を、違反するコードで確かめる。
/// </summary>
/// <remarks>
/// <para>
/// 表に書いただけでは、いつの間にか報告しなくなっても誰も気づかない。
/// 1 件につき 1 つ、実際に違反するコードを置いて確かめる。
/// </para>
/// <para>
/// コードの一覧は
/// https://learn.microsoft.com/ja-jp/windows/win32/direct3dhlsl/hlsl-errors-and-warnings
/// にある。
/// </para>
/// </remarks>
public sealed class HlslErrorCoverageTests
{
    private const string UrpCorePath =
        "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl";

    /// <summary>
    /// 対応表に「報告する」と書いたコードと、それを起こすコード。
    /// </summary>
    /// <remarks>
    /// 埋め込み HLSL の中身だけを書く。頂点・フラグメントの雛形は共通で足す。
    /// </remarks>
    public static TheoryData<string, string, string> Covered => new()
    {
        // 字句 (1001-1008)
        { "1001", "CF0001", "/* 閉じていないコメント" },
        { "1002", "CF0511", "static const uint h = 0xFFFFFFFFFFFFFFFFF;" },
        { "1003", "CF0511", "static const int o = 077777777777777777777;" },
        { "1004", "CF0511", "static const int d = 999999999999999999999999;" },

        // プリプロセッサ (1500-1521)
        { "1500", "CF0002", "#if )(\n#endif" },
        { "1502", "CF0002", "#if 1" },
        { "1503", "CF0002", "#if 1/0\n#endif" },
        { "1504", "CF0002", "#bogus_directive" },
        { "1508", "CF0002", "#elif 1\n#endif" },
        { "1509", "CF0002", "#else\n#endif" },
        { "1510", "CF0002", "#endif" },
        { "1511", "CF0002", "#define M(a, a) a" },
        { "1513", "CF0002", "#if 1\n#else\n#elif 0\n#endif" },
        { "1514", "CF0002", "#if 1\n#else\n#else\n#endif" },
        { "1516", "CF0002", "#define M(a, b) (a + b)\nstatic const int x = M(1);" },
        { "1518", "CF0002", "#if \"text\"\n#endif" },

        { "1501", "CF0002", "#if 1\n#endif _STRAY" },
        { "1519", "CF0004", "#define M 1\n#define M 2" },

        // 解析・意味 (3000-3999)
        { "3000", "CF0001", "half4 broken() : SV_Target { return 1 +* ; }" },
        { "3004", "CF0310", "static const float u = undeclaredName;" },
        { "3013", "CF0402", "half4 f(float3 v) { return v.x; }\nstatic const float u = f(float3(0,0,0), 1).x;" },
        { "3014", "CF0404", "static const float u = float3(1, 2).x;" },
        { "3017", "CF0410", "half4 g(float2 uv) { float3 a = uv; return a.x; }" },
        { "3018", "CF0312", "half4 g(float2 uv) { return uv.z; }" },
        { "3025", "CF0403", "void s(out float3 b) { b = 0; }\nhalf4 g() { s(float3(0,0,0)); return 0; }" },
        { "3030", "CF0510", "half4 g() { float arr[3] = {1,2,3}; return arr[5]; }" },
        { "3079", "CF0411", "void v() { return 1; }" },
        { "3080", "CF0501", "float3 e() { }" },
        { "3115", "HL0211", "Texture2D _A : register(t0);\nTexture2D _B : register(t0);\nstatic const float4 u = _A.Load(int3(0,0,0)) + _B.Load(int3(0,0,0));" },
        { "3504", "CF0510", "half4 g() { float arr[2] = {1,2}; return arr[9]; }" },
        { "3510", "CF0311", "half4 decl(float2 a);\nstatic const float u = decl(float2(0,0)).x;" },
        { "3517", "CF0310", "static const float u = notDefinedAnywhere;" },
    };

    /// <summary>
    /// 1 つの HLSL のコードが、場所によってどの規則に分かれるか。
    /// </summary>
    /// <remarks>
    /// HLSL のコードは「何が起きたか」で 1 つにまとまっているが、
    /// この解析ツールの規則は「どこで起きたか」で分けてある。
    /// 直し方が場所によって違うので、そのほうが読んで直せる。
    /// </remarks>
    public static TheoryData<string, string, string> Split => new()
    {
        // 3017 ERR_UNSUPPORTED_CAST
        { "3017 return", "CF0411", "float3 f(float2 uv) { return uv; }" },
        {
            "3017 引数", "CF0401",
            "half4 t(float3 v) { return v.x; }\nhalf4 f(float2 uv) { return t(uv); }"
        },
        { "3017 初期化", "CF0410", "half4 f(float2 uv) { float3 a = uv; return a.x; }" },
        { "3017 代入", "CF0410", "half4 f(float2 uv) { float3 a = 0; a = uv; return a.x; }" },

        // 3018 ERR_SUBSCRIPT
        {
            "3018 どの構成にも無い", "CF0312",
            "struct V { float2 uv : TEXCOORD0; };\nhalf4 f(V i) { return i.notAMember; }"
        },
        { "3018 スウィズル", "CF0312", "half4 f(float2 uv) { return uv.z; }" },
        {
            "3018 その構成にだけ無い", "CF0313",
            "#pragma multi_compile _ SHADER_DEBUG\n"
            + "struct V { float2 uv : TEXCOORD0;\n#ifdef SHADER_DEBUG\nfloat4 d : COLOR;\n#endif\n};\n"
            + "half4 f(V i)\n{\n#ifdef SHADER_DEBUG\nreturn i.d;\n#else\nreturn i.d;\n#endif\n}"
        },
    };

    [Theory]
    [MemberData(nameof(Split))]
    public void 同じコードでも場所ごとに違う規則で報告する(string 場所, string ruleId, string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(body);

        Assert.True(
            diagnostics.Any(d => d.Id == ruleId),
            $"{場所} は {ruleId} で報告するはずが、報告したのは "
            + $"[{string.Join(", ", diagnostics.Select(d => d.Id).Distinct())}] だった");
    }

    [Theory]
    [MemberData(nameof(Covered))]
    public void 対応表に書いたコードを実際に報告する(string code, string ruleId, string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(body);

        Assert.True(
            diagnostics.Any(d => d.Id == ruleId),
            $"{code} を {ruleId} で報告するはずが、報告したのは "
            + $"[{string.Join(", ", diagnostics.Select(d => d.Id).Distinct())}] だった");
    }

    [Fact]
    public void 同じ中身のマクロの再定義は報告しない()
    {
        // 書き手の意図どおりなので何も言わない。
        ImmutableArray<Diagnostic> diagnostics = Analyze("#define SAME 3\n#define SAME 3");

        Assert.DoesNotContain(diagnostics, d => d.Id == "CF0004");
    }

    [Fact]
    public void 取り込んだヘッダの中のマクロの再定義は報告しない()
    {
        // Unity のヘッダは、取り込む順序で中身を差し替える書き方を多用している。
        // 絞らずに報告すると、Unity 同梱の 109 件のうち 105 件が誤検出になった。
        ImmutableArray<Diagnostic> diagnostics = Analyze("#define M 1");

        Assert.DoesNotContain(
            diagnostics,
            d => d.Id == "CF0004"
                 && d.GetMessage().Contains("定義し直しています", StringComparison.Ordinal));
    }

    [Fact]
    public void 正しいリテラルは切り詰めとして報告しない()
    {
        // 32 ビットに収まる値と浮動小数は対象外である。
        // 16 進の 0xFFFFFFFF は F で終わるが、浮動小数の接尾辞ではない。
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            "static const uint  a = 0xFFFFFFFF;\n"
            + "static const uint  b = 4294967295;\n"
            + "static const int   c = 2147483647;\n"
            + "static const int   d = 0777;\n"
            + "static const float e = 1234567890123.0;\n"
            + "static const float f = 1.5e30;\n"
            + "static const half  g = 0.5h;\n"
            + "static const uint  h = 100u;");

        Assert.DoesNotContain(diagnostics, d => d.Id == "CF0511");
    }

    /// <summary>埋め込みコードを解析して診断を得る。</summary>
    /// <param name="body">HLSLPROGRAM の中に置く内容。</param>
    /// <returns>検出した診断。</returns>
    private static ImmutableArray<Diagnostic> Analyze(string body)
    {
        string source = "Shader \"Test/Coverage\"\n{\n    SubShader\n    {\n        Pass\n        {\n"
            + "            HLSLPROGRAM\n"
            + "            #pragma vertex vert\n"
            + "            #pragma fragment frag\n"
            + "            #include \"" + UrpCorePath + "\"\n"
            + body + "\n"
            + "            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }\n"
            + "            half4 frag() : SV_Target { return 0; }\n"
            + "            ENDHLSL\n        }\n    }\n}\n";

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Coverage.shader"));

        SemanticsOptions options = new()
        {
            IncludeResolver = new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { [UrpCorePath] = string.Empty }),
        };

        ShaderCompilation compilation = UnityCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), options);

        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }
}
