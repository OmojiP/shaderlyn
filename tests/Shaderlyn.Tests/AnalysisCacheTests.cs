using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// 実行のあいだで結果を持ち越すキャッシュの検証。
/// </summary>
/// <remarks>
/// <b>ここで守りたいのは速さではなく、結果が変わらないことである。</b>
/// キャッシュの壊れ方は「直したのに指摘が消えない」「壊したのに指摘が出ない」であり、
/// どちらも利用者からは原因が見えない。
/// そのため、シェーダー・ヘッダ・設定のそれぞれを変えたときに
/// キャッシュ無しの結果と一致することを確かめる。
/// </remarks>
public sealed class AnalysisCacheTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-cache-test-{Guid.NewGuid():N}");

    public AnalysisCacheTests()
    {
        Directory.CreateDirectory(_workDirectory);
        WriteHeader("float4");
        WriteShader("A.shader");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // テストの後始末が失敗してもテスト結果には影響させない。
        }
    }

    private string CachePath => Path.Combine(_workDirectory, "cache.json");

    /// <summary>
    /// 共通ヘッダを書き出す。
    /// </summary>
    /// <param name="parameterType">関数の仮引数の型。</param>
    /// <remarks>
    /// <c>float4</c> にすると、<c>float3</c> を渡している呼び出しが
    /// HL0340 (成分が増える向きの変換) になる。
    /// <c>float3</c> に戻せば指摘は消える。
    /// ヘッダを直したことが結果に届くかを、この 1 文字で確かめる。
    /// </remarks>
    private void WriteHeader(string parameterType)
        => File.WriteAllText(
            Path.Combine(_workDirectory, "Common.hlsl"),
            $$"""
            #ifndef COMMON_INCLUDED
            #define COMMON_INCLUDED

            float3 Tint({{parameterType}} color)
            {
                return color.rgb * 0.5;
            }

            #endif
            """);

    private void WriteShader(string name)
        => File.WriteAllText(
            Path.Combine(_workDirectory, name),
            """
            Shader "Test/Cache"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #include "Common.hlsl"

                        float4 vert(float4 position : POSITION) : SV_POSITION
                        {
                            return position;
                        }

                        float4 frag() : SV_Target
                        {
                            return float4(Tint(float3(1, 1, 1)), 1);
                        }
                        ENDHLSL
                    }
                }
            }
            """);

    private async Task<(string Output, string Error)> RunAsync(params string[] extra)
    {
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync(
            [_workDirectory, .. extra], BuiltInAnalyzers.All, output, error);

        return (output.ToString(), error.ToString());
    }

    private Task<(string Output, string Error)> RunWithCacheAsync(params string[] extra)
        => RunAsync(["--cache", CachePath, .. extra]);

    [Fact]
    public async Task キャッシュを使っても指摘は変わらない()
    {
        (string plain, _) = await RunAsync();
        (string cold, _) = await RunWithCacheAsync();
        (string warm, _) = await RunWithCacheAsync();

        Assert.Contains("HL0340", plain, StringComparison.Ordinal);
        Assert.Equal(plain, cold);
        Assert.Equal(plain, warm);
    }

    [Fact]
    public async Task 二回目は前回の結果を使い回す()
    {
        await RunWithCacheAsync();
        (_, string error) = await RunWithCacheAsync("--timings");

        Assert.Contains("前回の結果の使い回し 1 件 / 解析 0 件", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// ヘッダを直した結果が、キャッシュを通しても届くことを検証する。
    /// </summary>
    /// <remarks>
    /// <b>これがこの仕組みで最も壊れやすい箇所である。</b>
    /// シェーダー自身は 1 文字も変わっていないため、
    /// 取り込むファイル一式を鍵に含めていなければ、古い指摘を出し続ける。
    /// </remarks>
    [Fact]
    public async Task ヘッダを直すと指摘が消える()
    {
        (string before, _) = await RunWithCacheAsync();
        Assert.Contains("HL0340", before, StringComparison.Ordinal);

        WriteHeader("float3");

        (string after, string error) = await RunWithCacheAsync("--timings");
        (string withoutCache, _) = await RunAsync();

        Assert.DoesNotContain("HL0340", after, StringComparison.Ordinal);
        Assert.Equal(withoutCache, after);
        Assert.Contains("前回の結果の使い回し 0 件 / 解析 1 件", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// ヘッダを壊した結果も、キャッシュを通して届くことを検証する。
    /// </summary>
    /// <remarks>
    /// 消える向きだけを確かめても足りない。
    /// 「壊したのに指摘が出ない」ほうが、静的解析としては重い誤りである。
    /// </remarks>
    [Fact]
    public async Task ヘッダを壊すと指摘が出る()
    {
        WriteHeader("float3");
        (string before, _) = await RunWithCacheAsync();
        Assert.DoesNotContain("HL0340", before, StringComparison.Ordinal);

        WriteHeader("float4");

        (string after, _) = await RunWithCacheAsync();
        Assert.Contains("HL0340", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task シェーダーを直すと解析し直す()
    {
        await RunWithCacheAsync();
        WriteShader("A.shader");
        File.AppendAllText(Path.Combine(_workDirectory, "A.shader"), "\n// touched\n");

        (_, string error) = await RunWithCacheAsync("--timings");

        Assert.Contains("前回の結果の使い回し 0 件 / 解析 1 件", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 設定を変えたら使い回さないことを検証する。
    /// </summary>
    /// <remarks>
    /// 定義済みマクロは展開の結果を変えるため、鍵に含めなければならない。
    /// 含め忘れると「<c>--define</c> を変えても結果が変わらない」ことになる。
    /// </remarks>
    [Fact]
    public async Task 設定が変われば使い回さない()
    {
        await RunWithCacheAsync();
        (_, string error) = await RunWithCacheAsync("--timings", "--define", "SOMETHING_ELSE");

        Assert.Contains("前回の結果の使い回し 0 件 / 解析 1 件", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 壊れたキャッシュファイルは捨てて続行することを検証する。
    /// </summary>
    /// <remarks>
    /// キャッシュは速さのための仕組みでしかない。
    /// 読めないことを理由に解析を止めるのは、目的に対して過剰である。
    /// ただし何も伝えずに捨てると「なぜか毎回遅い」に気づけないので、
    /// 読めなかったことは標準エラーへ伝える。
    /// </remarks>
    [Fact]
    public async Task 壊れたキャッシュファイルは捨てて解析する()
    {
        File.WriteAllText(CachePath, "{ this is not json");

        (string output, string error) = await RunWithCacheAsync();

        Assert.Contains("HL0340", output, StringComparison.Ordinal);
        Assert.Contains("キャッシュを読み込めませんでした", error, StringComparison.Ordinal);

        // 壊れた内容は書き直され、次回からは使い回せる。
        (_, string second) = await RunWithCacheAsync("--timings");
        Assert.Contains("前回の結果の使い回し 1 件", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// 今回解析しなかったファイルの記録が残ることを検証する。
    /// </summary>
    /// <remarks>
    /// 1 ファイルだけを指定して実行するたびに他の記録が消えるようでは、
    /// pre-commit で使ったあとの CI が毎回全件解析になる。
    /// </remarks>
    [Fact]
    public async Task 今回解析しなかったファイルの記録も残る()
    {
        WriteShader("B.shader");
        await RunWithCacheAsync();

        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync(
            [Path.Combine(_workDirectory, "A.shader"), "--cache", CachePath],
            BuiltInAnalyzers.All,
            output,
            error);

        (_, string third) = await RunWithCacheAsync("--timings");
        Assert.Contains("前回の結果の使い回し 2 件 / 解析 0 件", third, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 消えたファイルの記録は残さない()
    {
        WriteShader("B.shader");
        await RunWithCacheAsync();

        File.Delete(Path.Combine(_workDirectory, "B.shader"));

        (_, string second) = await RunWithCacheAsync("--timings");
        Assert.Contains("記録済み 2 件", second, StringComparison.Ordinal);

        (_, string third) = await RunWithCacheAsync("--timings");
        Assert.Contains("記録済み 1 件", third, StringComparison.Ordinal);
    }

    [Fact]
    public void キャッシュのパスは省略できない()
    {
        Assert.False(CommandLineOptions.TryParse(["x.shader", "--cache"], out _, out string? error));
        Assert.Contains("--cache", error, StringComparison.Ordinal);
    }
}
