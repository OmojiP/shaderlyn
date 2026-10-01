using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// セマンティクスとレジスタ指定の説明。
/// </summary>
/// <remarks>
/// <para>
/// <b>セマンティクスは名前が意味そのものである。</b>
/// <c>SV_Target</c> と <c>SV_Position</c> は綴りが似ているだけで役割がまったく違い、
/// 取り違えても Unity は多くの場合エラーを出さない。
/// 何が入ってくるのか、どこへ出るのかを、その場で読めるようにする。
/// </para>
/// <para>
/// <b>知らない名前を「間違い」と言わない。</b>
/// セマンティクスはプラットフォームごとに増え、
/// <c>TEXCOORD7</c> のように番号が付くものも多い。
/// 知らない名前は、知らないと言うにとどめる。
/// </para>
/// </remarks>
internal static partial class HoverBuilder
{
    /// <summary>
    /// 番号を除いた名前で引ける説明。
    /// </summary>
    /// <remarks>
    /// <c>TEXCOORD0</c> と <c>TEXCOORD7</c> を別々に並べても、説明の中身は変わらない。
    /// 末尾の数字を落とした名前で引く。
    /// </remarks>
    private static readonly Dictionary<string, string> SemanticDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SV_POSITION"] =
                "頂点シェーダーの出力位置 (クリップ空間)。ラスタライザがこれを使って画面上の位置を決める。"
                + "フラグメントシェーダーの入力として受け取ると、ピクセルの画面座標が入る。",
            ["POSITION"] = "頂点のオブジェクト空間での位置。メッシュの頂点座標がそのまま入る。",
            ["NORMAL"] = "頂点の法線 (オブジェクト空間)。",
            ["TANGENT"] = "頂点の接線 (オブジェクト空間)。w に従法線の向きが入る。",
            ["TEXCOORD"] =
                "UV 座標。番号は何番目の UV セットかを表す。"
                + "頂点とフラグメントの間で任意の値を渡す入れ物としても使う。",
            ["COLOR"] = "頂点カラー。フラグメントの出力に使うと描画先へ書き込む色になる。",
            ["SV_TARGET"] =
                "フラグメントシェーダーの出力先。番号は何番目のレンダーターゲットかを表す。"
                + "**これを付け忘れると、色を返しても画面には何も出ない。**",
            ["SV_DEPTH"] = "フラグメントが書き込む深度値。書き換えると早期深度テストが効かなくなる。",
            ["SV_VERTEXID"] = "頂点の通し番号。頂点バッファを使わない描画で位置を計算するのに使う。",
            ["SV_INSTANCEID"] = "インスタンスの通し番号。GPU インスタンシングで個体を見分けるのに使う。",
            ["SV_PRIMITIVEID"] = "プリミティブ (三角形など) の通し番号。",
            ["SV_ISFRONTFACE"] = "表面から見ているかどうか。両面描画で裏面の法線を反転するのに使う。",
            ["SV_DISPATCHTHREADID"] = "コンピュートシェーダーのスレッドの通し番号 (ディスパッチ全体で一意)。",
            ["SV_GROUPTHREADID"] = "コンピュートシェーダーのスレッドの、グループ内での番号。",
            ["SV_GROUPID"] = "コンピュートシェーダーのスレッドグループの番号。",
            ["SV_GROUPINDEX"] = "コンピュートシェーダーのスレッドの、グループ内での通し番号 (1 次元)。",
            ["BLENDWEIGHT"] = "スキニングのボーンの重み。",
            ["BLENDINDICES"] = "スキニングのボーンの番号。",
            ["PSIZE"] = "点の大きさ。",
        };

    /// <summary>レジスタ指定などの名前と、その説明。</summary>
    private static readonly Dictionary<string, string> BindingDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["register"] =
                "リソースを置くレジスタの指定。`t` はテクスチャ、`s` はサンプラ、"
                + "`b` は定数バッファ、`u` は読み書きできるリソースを表す。",
            ["packoffset"] = "定数バッファの中での配置の指定。",
        };

    /// <summary>
    /// セマクティクスまたはレジスタ指定を説明する。
    /// </summary>
    /// <param name="semantic">対象の修飾。</param>
    /// <returns>説明。</returns>
    private static HoverResult DescribeSemantic(SemanticSyntax semantic)
    {
        string name = semantic.Name;
        string signature = semantic.HasArguments
            ? $"{name}({string.Join(string.Empty, semantic.ArgumentTokens.Select(t => t.Text))})"
            : name;

        string body = semantic.HasArguments && BindingDescriptions.TryGetValue(name, out string? binding)
            ? binding
            : DescribeSemanticName(name);

        return new HoverResult($"```hlsl\n: {signature}\n```\n\n{body}", semantic.NameToken.Span);
    }

    /// <summary>
    /// セマンティクスの名前を説明する。
    /// </summary>
    /// <param name="name">セマンティクスの名前。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// 末尾の番号を落として引く。番号は何番目かを表すだけで、役割は変わらない。
    /// </remarks>
    private static string DescribeSemanticName(string name)
    {
        string stem = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        // 全部が数字だった場合に空文字で引かない。
        if (stem.Length > 0 && SemanticDescriptions.TryGetValue(stem, out string? described))
        {
            return described;
        }

        return SemanticDescriptions.TryGetValue(name, out string? exact)
            ? exact
            : "セマンティクス。このツールは意味を知らないが、"
              + "プラットフォーム固有のものか、新しく増えたものである可能性がある。";
    }
}
