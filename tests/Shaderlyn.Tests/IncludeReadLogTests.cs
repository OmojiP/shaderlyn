using Shaderlyn.Core.Text;
using Shaderlyn.LanguageServer;

namespace Shaderlyn.Tests;

/// <summary>
/// 言語サーバーが保存のときに取り込みのキャッシュを捨てるかの判断 (<see cref="IncludeReadLog"/>) の検証。
/// </summary>
/// <remarks>
/// 捨てると次の解析がすべてのヘッダを読み直すので、読み込んだファイルが変わっていなければ捨てない。
/// 変わっていれば捨てる (捨て損なうと、古い中身で解析し続ける)。
/// </remarks>
public sealed class IncludeReadLogTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "shaderlyn-include-reads-" + Guid.NewGuid().ToString("N"));

    private readonly string _header;
    private readonly string _shader;

    public IncludeReadLogTests()
    {
        Directory.CreateDirectory(_workspace);
        _header = Path.Combine(_workspace, "Lib.hlsl");
        _shader = Path.Combine(_workspace, "Test.shader");
        File.WriteAllText(_header, "float Lib() { return 1; }\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    private static string Shader(string include) => $$"""
        Shader "Test"
        {
            SubShader { Pass { HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "{{include}}"
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return Lib(); }
            ENDHLSL } }
        }
        """;

    private AnalysisSession Analyze(string include)
    {
        AnalysisSession session = new(_workspace);
        session.Analyze(SourceText.From(Shader(include), _shader), AnalysisDepth.Full);
        return session;
    }

    [Fact]
    public void 取り込み先が変わっていなければ捨てない()
    {
        AnalysisSession session = Analyze("Lib.hlsl");

        Assert.Null(session.Reads.ReasonToForget(_shader));
    }

    [Fact]
    public void 取り込んだヘッダを保存したら捨てる()
    {
        AnalysisSession session = Analyze("Lib.hlsl");

        Assert.NotNull(session.Reads.ReasonToForget(_header));
    }

    [Fact]
    public void 取り込み先を書き換えたら捨てる()
    {
        AnalysisSession session = Analyze("Lib.hlsl");

        File.WriteAllText(_header, "float Lib() { return 2.0; }\n");

        Assert.NotNull(session.Reads.ReasonToForget(_shader));
    }

    [Fact]
    public void 取り込み先の大きさが同じでも更新日時が変われば捨てる()
    {
        AnalysisSession session = Analyze("Lib.hlsl");

        File.WriteAllText(_header, "float Lib() { return 2; }\n");
        File.SetLastWriteTimeUtc(_header, DateTime.UtcNow.AddMinutes(1));

        Assert.NotNull(session.Reads.ReasonToForget(_shader));
    }

    [Fact]
    public void 取り込み先を消したら捨てる()
    {
        AnalysisSession session = Analyze("Lib.hlsl");

        File.Delete(_header);

        Assert.NotNull(session.Reads.ReasonToForget(_shader));
    }

    [Fact]
    public void 解決できなかった取り込みがあれば捨てる()
    {
        // 保存の前に、エディタの外でそのファイルを作ったかもしれない。見つからなかったことも覚えているので、捨てないと読まない。
        AnalysisSession session = Analyze("Missing.hlsl");

        Assert.NotNull(session.Reads.ReasonToForget(_shader));
    }

    [Fact]
    public void カーソルの下を答えるモデルが読み込んだ取り込み先も覚える()
    {
        AnalysisSession session = new(_workspace);
        session.CreateModel(SourceText.From(Shader("Lib.hlsl"), _shader));

        Assert.NotNull(session.Reads.ReasonToForget(_header));
    }
}
