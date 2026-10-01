namespace Shaderlyn.Semantics;

/// <summary>
/// 解析対象のファイルが、どの言語として書かれているか。
/// </summary>
internal enum ShaderSourceKind
{
    /// <summary>ShaderLab。埋め込まれた HLSL をブロックごとに解析する。</summary>
    ShaderLab,

    /// <summary>HLSL 単体。ファイル全体を 1 つのブロックとして解析する。</summary>
    Hlsl,
}

/// <summary>
/// 拡張子から、どの言語として解析するかを決める。
/// </summary>
/// <remarks>
/// <para>
/// <b>入口が 3 つあるので、判断はここに一本化する。</b>
/// CLI・<c>--inspect</c>・エディタ常駐がそれぞれ別に判定すると、
/// 「CLI では見るのにエディタでは見ない」という食い違いが生まれる。
/// </para>
/// <para>
/// 判断の根拠は拡張子だけである。中身を見て推測はしない。
/// <c>.shader</c> の中に ShaderLab が無ければそれは誤りであり、
/// 何も伝えずに別の言語として読み直すと、誤りが指摘されなくなる。
/// </para>
/// </remarks>
internal static class ShaderSourceKinds
{
    /// <summary>
    /// パスから解析する言語を決める。
    /// </summary>
    /// <param name="filePath">対象のファイルパス。</param>
    /// <returns>解析する言語。判別できない場合は <see cref="ShaderSourceKind.ShaderLab"/>。</returns>
    /// <remarks>
    /// 知らない拡張子を ShaderLab として扱うのは、
    /// このツールが元々 <c>.shader</c> のためのものであり、
    /// 拡張子を持たない入力 (エディタの未保存バッファなど) も
    /// これまでどおり ShaderLab として読まれるべきだからである。
    /// </remarks>
    public static ShaderSourceKind FromPath(string? filePath)
    {
        string extension = filePath is null ? string.Empty : Path.GetExtension(filePath);

        return extension.ToLowerInvariant() switch
        {
            ".compute" or ".hlsl" or ".cginc" or ".hlslinc" => ShaderSourceKind.Hlsl,
            _ => ShaderSourceKind.ShaderLab,
        };
    }

    /// <summary>
    /// ほかのシェーダーから取り込まれる前提の断片かどうかを判定する。
    /// </summary>
    /// <param name="filePath">対象のファイルパス。</param>
    /// <returns><c>.hlsl</c> / <c>.cginc</c> / <c>.hlslinc</c> なら <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>断片だけでは判断できないことがある。</b>
    /// シェーダーステージの <c>#pragma</c> やシンボルの宣言は、
    /// 取り込む側の <c>.shader</c> に書くのが普通である。
    /// 断片を単体で開いたときにそれが「無い」と報告すると、正しいファイルに指摘が並ぶ。
    /// </para>
    /// <para>
    /// <c>.compute</c> は含めない。コンピュートシェーダーは 1 ファイルで完結し、
    /// <c>#pragma kernel</c> もシンボルの宣言も同じファイルに書く。
    /// </para>
    /// </remarks>
    public static bool IsIncludeFragment(string? filePath)
    {
        string extension = filePath is null ? string.Empty : Path.GetExtension(filePath);

        return extension.ToLowerInvariant() is ".hlsl" or ".cginc" or ".hlslinc";
    }
}
