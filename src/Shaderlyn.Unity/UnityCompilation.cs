using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Unity;

/// <summary>
/// Unity のシェーダーからセマンティックモデルを作る。
/// </summary>
/// <remarks>
/// <c>.shader</c> と HLSL 単体ファイルで違うのは、コンパイル単位の切り出し方だけである。
/// 以降は同じ道 (<see cref="ShaderCompilation.Create"/>) を通るので、片方だけ検査が抜けることがない。
/// </remarks>
public static class UnityCompilation
{
    /// <summary>
    /// 解析済みの ShaderLab 構文木からセマンティックモデルを作る。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="shaderLabTree">解析済みの ShaderLab 構文木。</param>
    /// <param name="options">実行時設定。</param>
    /// <param name="unity">Unity の環境の設定。</param>
    /// <returns>構築されたセマンティックモデル。</returns>
    /// <remarks>
    /// <b>このメソッドは入力がどれだけ壊れていても例外を投げない。</b>
    /// </remarks>
    public static ShaderCompilation Create(
        SourceText text,
        ShaderLabSyntaxTree shaderLabTree,
        SemanticsOptions? options = null,
        UnityOptions? unity = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(shaderLabTree);

        unity ??= UnityOptions.Default;

        UnityShaderModel model = new(text, shaderLabTree, unity.Profile, isStandaloneHlsl: false);

        return Attach(model, ShaderCompilation.Create(
            text,
            [.. ProgramBlockExtractor.Extract(shaderLabTree).Select(block => block.ToUnit())],
            ProgramBlockExtractor.ExtractAllCode(shaderLabTree),
            new UnityHlslEnvironment(unity),
            new ShaderSourceInfo(shaderLabTree.Root, shaderLabTree.Diagnostics, model),
            options));
    }

    /// <summary>
    /// HLSL 単体ファイル (<c>.compute</c> など) を解析してセマンティックモデルを作る。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="options">実行時設定。</param>
    /// <param name="unity">Unity の環境の設定。</param>
    /// <returns>構築されたセマンティックモデル。</returns>
    /// <remarks>
    /// <para>
    /// <b>ファイル全体を 1 つの <c>HLSLPROGRAM</c> として扱う。</b>
    /// 展開・構文解析・セマンティックモデル・HLSL のルール群は、
    /// 埋め込み HLSL とまったく同じ道を通る。
    /// </para>
    /// <para>
    /// <b>ShaderLab の構文木は、全部を空白でマスクしたテキストから作る。</b>
    /// ShaderLab の木を見るルールが、対象の無い空の木を歩くだけで済むようにするためである。
    /// 空のテキストではなく<b>同じ長さ</b>のテキストにしているのは、
    /// 長さが食い違うと位置を扱う処理が「たまたま通る」状態になるためである。
    /// </para>
    /// <para>
    /// <b>このメソッドは入力がどれだけ壊れていても例外を投げない。</b>
    /// </para>
    /// </remarks>
    public static ShaderCompilation CreateForHlsl(
        SourceText text,
        SemanticsOptions? options = null,
        UnityOptions? unity = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        unity ??= UnityOptions.Default;

        // カーネルは展開が始まる前に分かっていなければならない。
        // #pragma kernel が並べたマクロが、そのカーネルの展開そのものを変えるためである。
        ImmutableArray<HlslSyntaxToken> tokens = options?.TokenCache is { } cache
            ? cache.GetOrLex(text, out _)
            : new HlslLexer(text).Lex(out _);

        ShaderLabSyntaxTree blankTree = ShaderLabSyntaxTree.Parse(CreateBlankText(text));
        UnityShaderModel model = new(text, blankTree, unity.Profile, isStandaloneHlsl: true);

        return Attach(model, ShaderCompilation.Create(
            text,
            [.. ProgramBlockExtractor.ForStandaloneHlsl(text, ComputeKernels.Collect(tokens)).Select(block => block.ToUnit())],
            text,
            new UnityHlslEnvironment(unity),

            // 全部を空白でマスクしたテキストから作った ShaderLab の木はダミーである。
            // そこから出る構文診断はダミーについての話であって、解析しているファイルについての事実ではない。
            new ShaderSourceInfo(blankTree.Root, [], model),
            options));
    }

    /// <summary>モデルをセマンティックモデルに結び付けて返す。</summary>
    /// <param name="model">Unity の情報。</param>
    /// <param name="compilation">セマンティックモデル。</param>
    /// <returns><paramref name="compilation"/>。</returns>
    private static ShaderCompilation Attach(UnityShaderModel model, ShaderCompilation compilation)
    {
        model.Attach(compilation);
        return compilation;
    }

    /// <summary>
    /// 元のテキストと同じ長さ・同じ改行位置を持つ、中身の無いテキストを作る。
    /// </summary>
    /// <param name="text">元のテキスト。</param>
    /// <returns>全部を空白で埋めたテキスト。</returns>
    private static SourceText CreateBlankText(SourceText text)
    {
        char[] blank = new char[text.Length];

        for (int i = 0; i < blank.Length; i++)
        {
            char c = text[i];
            blank[i] = c is '\r' or '\n' ? c : ' ';
        }

        return SourceText.From(new string(blank), text.FilePath, text.Encoding);
    }
}
