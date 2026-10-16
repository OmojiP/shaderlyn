using System.Collections.Immutable;
using System.Diagnostics;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;

namespace Shaderlyn.Hlsl;

/// <summary>
/// HLSL のコードを解析して、構文木 (<see cref="ShaderSyntaxTree"/>) を作る入口。
/// </summary>
/// <remarks>
/// プリプロセスと構文解析の手順をここに閉じ込めている。
/// 木の HLSL に固有の値 (方言・言語のバージョン・コンパイル先) は <c>tree.Hlsl()</c> で読む。
/// </remarks>
public static class HlslSyntaxTree
{
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
    public static ShaderSyntaxTree Parse(
        SourceText text,
        PreprocessorOptions? options = null,
        ISyntaxTimingRecorder? timings = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 言語が無ければ HLSL の既定で読む。巻き上げの判定にも要る。
        PreprocessorOptions effective = options ?? new PreprocessorOptions();

        if (effective.Language is not HlslLanguageOptions)
        {
            effective = effective with { Language = effective.Hlsl() };
        }

        HlslLanguageOptions language = effective.Hlsl();
        long start = timings is null ? 0 : Stopwatch.GetTimestamp();
        PreprocessResult preprocessed = new ShaderPreprocessor(effective).Preprocess(text);
        start = Record(timings, SyntaxStep.Preprocess, start);

        HlslParser parser = new(preprocessed.Tokens, text, options?.IsUserInclude)
        {
            Features = language.Features,
            DialectSubject = language.DescribeUnavailable(),
        };
        CompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        start = Record(timings, SyntaxStep.Parse, start);

        SyntaxNode.WireParents(root);
        Record(timings, SyntaxStep.WireParents, start);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(preprocessed.Diagnostics);
        diagnostics.AddRange(parseDiagnostics);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable(), language, parser.UnanalyzedIdentifiers);
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
