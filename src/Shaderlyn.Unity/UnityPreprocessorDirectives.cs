using System.Collections.Immutable;
using Shaderlyn.Hlsl.Preprocessing;

namespace Shaderlyn.Unity;

/// <summary>
/// Unity のシェーダーのコンパイルが受け付ける指令の違い。
/// </summary>
/// <remarks>
/// プリプロセッサは環境の決まりを持たない。Unity に固有の扱いは、ここから設定として与える
/// (<see cref="PreprocessorOptions.AdditionalIncludeDirectives"/> / <see cref="PreprocessorOptions.UnsupportedDirectives"/>)。
/// </remarks>
public static class UnityPreprocessorDirectives
{
    /// <summary>
    /// <c>#include</c> と同じく取り込む指令。
    /// </summary>
    /// <remarks>
    /// <c>#include_with_pragmas</c> は、取り込んだ先の <c>#pragma</c> も呼び出し側へ伝える。
    /// 対応していないと、URP のシェーダーの多くが依存関係を取り込めなくなる。
    /// </remarks>
    public static ImmutableHashSet<string> IncludeDirectives { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "include_with_pragmas");

    /// <summary>
    /// 使えない指令。
    /// </summary>
    /// <remarks>
    /// <c>#line</c> は Unity のシェーダーのコンパイルでエラーになる ("syntax error: unexpected token 'line'")。
    /// </remarks>
    public static ImmutableHashSet<string> UnsupportedDirectives { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "line");

    /// <summary>設定に Unity の指令の扱いを重ねる。</summary>
    /// <param name="options">元の設定。</param>
    /// <returns>重ねた設定。</returns>
    public static PreprocessorOptions Apply(PreprocessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            AdditionalIncludeDirectives = options.AdditionalIncludeDirectives.Union(IncludeDirectives),
            UnsupportedDirectives = options.UnsupportedDirectives.Union(UnsupportedDirectives),
        };
    }
}
