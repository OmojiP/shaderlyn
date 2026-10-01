using Shaderlyn.Cli;

ConfigureConsoleEncoding();
return (int)await Shaderlyn.Cli.Program.RunAsync(args, BuiltInAnalyzers.All, Console.Out, Console.Error).ConfigureAwait(false);

// Windows では標準出力の既定エンコーディングがシステムのコードページ (日本語環境なら CP932) に
// なるため、診断メッセージの日本語がそのままでは文字化けする。
// BOM は付けない。--output で SARIF や JSON をファイルへ書き出した際に
// BOM が混入すると、後段のツールが解釈に失敗することがあるためである。
// 出力がリダイレクトされていると設定に失敗しうるが、解析の成否とは無関係なので既定のまま続ける。
static void ConfigureConsoleEncoding()
{
    try
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
    catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
    {
    }
}
