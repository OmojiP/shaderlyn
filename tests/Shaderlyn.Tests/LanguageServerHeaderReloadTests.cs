using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// 取り込んだヘッダを書き換えて保存したあと、言語サーバーが新しい中身で解析し直すことの検証。
/// </summary>
/// <remarks>
/// 言語サーバーは取り込みの読み込み結果を覚えている。覚えたままだと、ヘッダを書き換えても古い中身で解析し、
/// ヘッダへの指摘が書き換える前の行に出る (行がずれて見える)。
/// </remarks>
public sealed class LanguageServerHeaderReloadTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "shaderlyn-lsp-reload-" + Guid.NewGuid().ToString("N"));

    public LanguageServerHeaderReloadTests() => Directory.CreateDirectory(_workspace);

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

    [Fact]
    public async Task ヘッダを書き換えて保存したら新しい中身で指摘する()
    {
        string header = Path.Combine(_workspace, "Lib.hlsl");
        string shader = Path.Combine(_workspace, "Test.shader");
        string headerUri = LanguageServer.DocumentUri.FromFilePath(header);
        const string Source = """
            Shader "Test"
            {
                SubShader { Pass { HLSLPROGRAM
                #pragma vertex vert
                #pragma fragment frag
                #include "Lib.hlsl"
                float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                half4 frag() : SV_Target { return Lib(); }
                ENDHLSL } }
            }
            """;

        File.WriteAllText(header, "float Lib()\n{\n    return missingName;\n}\n");
        File.WriteAllText(shader, Source);

        await using LanguageServerHarness harness = new(LanguageServer.DocumentUri.FromFilePath(shader));
        await harness.InitializeAsync(null, _workspace);
        await harness.OpenAsync(Source);
        await harness.SaveAsync();

        JsonElement before = await harness.ReceiveDiagnosticsForAsync(headerUri, "HL0310");
        Assert.Equal(2, LineOf(before, "HL0310"));

        // ヘッダの先頭に 2 行足して保存した。指摘は 2 行下へ移る。
        File.WriteAllText(header, "// a\n// b\nfloat Lib()\n{\n    return missingName;\n}\n");
        await harness.SaveAsync();

        JsonElement after = await harness.ReceiveDiagnosticsForAsync(headerUri, "HL0310");
        Assert.Equal(4, LineOf(after, "HL0310"));
    }

    private static int LineOf(JsonElement diagnostics, string ruleId)
        => diagnostics.EnumerateArray()
            .First(d => d.GetProperty("code").GetString() == ruleId)
            .GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();
}
