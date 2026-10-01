using System.Collections.Immutable;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;
using Shaderlyn.LanguageServer;
using Shaderlyn.LanguageServer.Protocol;

// 標準入出力はプロトコルの通り道である。
// ここへ何かを書くとメッセージの枠が壊れ、以降のやり取りがすべて読めなくなる。
using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// 実行するルールの一覧。自作ルールを足したサーバを作る場合は、ここへ並べる。
ImmutableArray<DiagnosticAnalyzer> analyzers = BuiltInAnalyzers.All;

ShaderLanguageServer server = new(new LspConnection(input, output), analyzers);

try
{
    return await server.RunAsync(cts.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    return 0;
}
