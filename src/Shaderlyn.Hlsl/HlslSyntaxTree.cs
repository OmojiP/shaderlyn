using System.Collections.Immutable;
using System.Diagnostics;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl;

/// <summary>
/// 解析済みの HLSL コード。構文木、<c>#pragma</c>、診断を保持する。
/// </summary>
/// <remarks>
/// プリプロセスと構文解析の手順をこの型に閉じ込めている。
/// 順序を誤ると (プリプロセスを飛ばして構文解析するなど)
/// マクロが展開されないまま解析され、実用的なシェーダーが軒並み壊れる。
/// </remarks>
public sealed class HlslSyntaxTree
{
    private HlslSyntaxTree(
        SourceText text,
        HlslCompilationUnitSyntax root,
        PreprocessResult preprocessResult,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        PreprocessResult = preprocessResult;
        Diagnostics = diagnostics;
    }

    /// <summary>解析対象のソーステキスト。</summary>
    public SourceText Text { get; }

    /// <summary>構文木の根。</summary>
    public HlslCompilationUnitSyntax Root { get; }

    /// <summary>プリプロセスの結果。<c>#pragma</c> や include の情報を含む。</summary>
    public PreprocessResult PreprocessResult { get; }

    /// <summary>プリプロセスと構文解析で検出した診断。</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>現れた <c>#pragma</c> 指令。</summary>
    public ImmutableArray<PragmaDirective> Pragmas => PreprocessResult.Pragmas;

    /// <summary>
    /// HLSL コードを解析して構文木を作る。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <param name="timings">
    /// 段ごとの時間を受け取る先。<see langword="null"/> の場合は測らない。
    /// </param>
    /// <returns>解析結果。</returns>
    /// <remarks>
    /// <b>このメソッドは入力がどれだけ壊れていても例外を投げない。</b>
    /// </remarks>
    public static HlslSyntaxTree Parse(
        SourceText text,
        PreprocessorOptions? options = null,
        ISyntaxTimingRecorder? timings = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        long start = timings is null ? 0 : Stopwatch.GetTimestamp();
        PreprocessResult preprocessed = new HlslPreprocessor(options).Preprocess(text);
        start = Record(timings, SyntaxStep.Preprocess, start);

        HlslParser parser = new(preprocessed.Tokens, text, options?.IsUserInclude);
        HlslCompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        start = Record(timings, SyntaxStep.Parse, start);

        SyntaxNode.WireParents(root);
        Record(timings, SyntaxStep.WireParents, start);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(preprocessed.Diagnostics);
        diagnostics.AddRange(parseDiagnostics);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new HlslSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable());
    }

    /// <summary>1 つの段の時間を記録し、次の段の開始時刻を返す。</summary>
    /// <param name="timings">受け取る先。<see langword="null"/> の場合は何もしない。</param>
    /// <param name="phase">記録する段。</param>
    /// <param name="start">その段の開始時刻。</param>
    /// <returns>次の段の開始時刻。測っていない場合は 0。</returns>
    /// <remarks>
    /// 測っていないときの費用は null かどうかの判定 1 回だけである。
    /// </remarks>
    private static long Record(ISyntaxTimingRecorder? timings, SyntaxStep phase, long start)
    {
        if (timings is null)
        {
            return 0;
        }

        long now = Stopwatch.GetTimestamp();
        timings.RecordSyntaxStep(phase, now - start);
        return now;
    }
}
