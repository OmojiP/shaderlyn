using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 名前をたどる操作への応答。
/// </summary>
/// <remarks>
/// 補完・呼び出しの案内・参照の一覧・名前の変更は、
/// どれも「カーソルの下の名前は何か」を起点にする。
/// 起点の取り出しを 1 か所に置き、応答の組み立てだけをそれぞれで書く。
/// </remarks>
public sealed partial class ShaderLanguageServer
{
    /// <summary>その位置で書ける名前を返す。</summary>
    /// <param name="message">受け取った要求。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>応答を書き終えるまでの待ち。</returns>
    private async Task OnCompletionAsync(JsonElement message, CancellationToken cancellationToken)
    {
        ImmutableArray<CompletionItem> items = [];

        try
        {
            if (TryGetPosition(message, out ShaderCompilation? model, out int offset, out _))
            {
                items = CompletionBuilder.Build(model, offset);
            }
        }
        catch (Exception e)
        {
            // 候補が出ないだけで、常駐は続ける。
            await LogFailureAsync("補完の候補の組み立て", e).ConfigureAwait(false);
        }

        await _connection.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                WriteId(writer, message);
                writer.WriteStartObject("result");

                // 打ちかけの語での絞り込みはエディタに任せる。
                writer.WriteBoolean("isIncomplete", false);
                writer.WriteStartArray("items");

                foreach (CompletionItem item in items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("label", item.Label);
                    writer.WriteNumber("kind", item.Kind);

                    if (item.Detail is { } detail)
                    {
                        writer.WriteString("detail", detail);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>呼び出しの途中で、その関数の形を返す。</summary>
    /// <param name="message">受け取った要求。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>応答を書き終えるまでの待ち。</returns>
    private async Task OnSignatureHelpAsync(JsonElement message, CancellationToken cancellationToken)
    {
        SignatureHelp? help = null;

        try
        {
            if (TryGetPosition(message, out ShaderCompilation? model, out int offset, out _))
            {
                help = SignatureHelpBuilder.Build(model, offset);
            }
        }
        catch (Exception e)
        {
            // 案内が出ないだけで、常駐は続ける。
            await LogFailureAsync("引数の案内の組み立て", e).ConfigureAwait(false);
        }

        if (help is not { } found)
        {
            await RespondNullAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _connection.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                WriteId(writer, message);
                writer.WriteStartObject("result");
                writer.WriteStartArray("signatures");

                foreach (SignatureInfo signature in found.Signatures)
                {
                    writer.WriteStartObject();
                    writer.WriteString("label", signature.Label);
                    writer.WriteStartArray("parameters");

                    foreach (string parameter in signature.Parameters)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("label", parameter);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteNumber("activeSignature", 0);
                writer.WriteNumber("activeParameter", found.ActiveParameter);
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>カーソルの下の名前が現れる場所をすべて返す。</summary>
    /// <param name="message">受け取った要求。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>応答を書き終えるまでの待ち。</returns>
    private async Task OnReferencesAsync(JsonElement message, CancellationToken cancellationToken)
    {
        ImmutableArray<TextSpan> spans = [];
        OpenDocument? document = null;

        try
        {
            if (TryGetPosition(message, out ShaderCompilation? model, out int offset, out document))
            {
                spans = SymbolOccurrences.Find(model, offset);
            }
        }
        catch (Exception e)
        {
            // 一覧が出ないだけで、常駐は続ける。
            await LogFailureAsync("参照の検索", e).ConfigureAwait(false);
        }

        await _connection.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                WriteId(writer, message);
                writer.WriteStartArray("result");

                foreach (TextSpan span in spans)
                {
                    writer.WriteStartObject();
                    writer.WriteString("uri", document!.Uri);
                    WriteRange(writer, document, span);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// カーソルの下の名前を書き換える編集を返す。
    /// </summary>
    /// <param name="message">受け取った要求。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>応答を書き終えるまでの待ち。</returns>
    /// <remarks>
    /// <b>書き換えるのはこのファイルの中だけである。</b>
    /// 取り込んだヘッダの名前まで書き換えると、
    /// このファイルからは見えない場所を壊す。
    /// </remarks>
    private async Task OnRenameAsync(JsonElement message, CancellationToken cancellationToken)
    {
        ImmutableArray<TextSpan> spans = [];
        OpenDocument? document = null;
        string? newName = null;

        try
        {
            if (TryGetPosition(message, out ShaderCompilation? model, out int offset, out document)
                && message.GetProperty("params").TryGetProperty("newName", out JsonElement name))
            {
                newName = name.GetString();
                spans = SymbolOccurrences.Find(model, offset);
            }
        }
        catch (Exception e)
        {
            // 書き換えないだけで、常駐は続ける。
            await LogFailureAsync("名前の変更", e).ConfigureAwait(false);
        }

        if (newName is null || spans.IsEmpty || document is null)
        {
            await RespondNullAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _connection.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                WriteId(writer, message);
                writer.WriteStartObject("result");
                writer.WriteStartObject("changes");
                writer.WriteStartArray(document.Uri);

                foreach (TextSpan span in spans)
                {
                    writer.WriteStartObject();
                    WriteRange(writer, document, span);
                    writer.WriteString("newText", newName);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>範囲を書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="document">対象のスクリプト。</param>
    /// <param name="span">対象の範囲。</param>
    private static void WriteRange(Utf8JsonWriter writer, OpenDocument document, TextSpan span)
    {
        LinePositionSpan lines = document.Text.GetLinePositionSpan(span);

        writer.WriteStartObject("range");
        WritePosition(writer, "start", lines.Start);
        WritePosition(writer, "end", lines.End);
        writer.WriteEndObject();
    }

    /// <summary>
    /// 要求からカーソルの位置とセマンティックモデルを取り出す。
    /// </summary>
    /// <param name="message">受け取った要求。</param>
    /// <param name="model">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <param name="document">対象のスクリプト。</param>
    /// <returns>取り出せた場合は <see langword="true"/>。</returns>
    private bool TryGetPosition(
        JsonElement message,
        [NotNullWhen(true)] out ShaderCompilation? model,
        out int offset,
        out OpenDocument? document)
    {
        model = null;
        offset = 0;
        document = null;

        if (!TryReadTextDocument(message, out JsonElement textDocument)
            || textDocument.GetProperty("uri").GetString() is not { } uri
            || !_documents.TryGetValue(uri, out OpenDocument? found)
            || !message.GetProperty("params").TryGetProperty("position", out JsonElement position))
        {
            return false;
        }

        document = found;
        model = GetModel(uri, found);
        offset = found.Text.GetOffset(new LinePosition(
            position.GetProperty("line").GetInt32(),
            position.GetProperty("character").GetInt32()));

        return true;
    }
}
