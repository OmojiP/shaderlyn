using System.Collections.Immutable;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// <c>#pragma</c> の説明。
/// </summary>
/// <remarks>
/// <para>
/// <b><c>#pragma</c> は 1 語で挙動が大きく変わるのに、名前から中身が読めない。</b>
/// <c>shader_feature</c> と <c>multi_compile</c> は並べて書かれるが、
/// 前者はビルドで使われていないバリアントが取り除かれ、実行時に有効にしても何も起きない。
/// Unity はこの取り違えをエラーにしないため、その場で読めるようにする。
/// </para>
/// <para>
/// <b>知らない名前を「間違い」と言わない。</b>
/// <c>#pragma</c> は Unity のバージョンやプラットフォームごとに増える。
/// 知らない名前は、知らないと言うにとどめる。
/// </para>
/// <para>
/// 指令の行は構文木に残らないため、展開前のトークン列から探す。
/// </para>
/// </remarks>
internal static partial class HoverBuilder
{
    /// <summary>名前がそのまま決まっている <c>#pragma</c> と、その説明。</summary>
    internal static readonly Dictionary<string, string> PragmaDescriptions = new(StringComparer.Ordinal)
    {
        ["vertex"] = "頂点シェーダーのエントリポイントを指定する。",
        ["fragment"] = "フラグメント (ピクセル) シェーダーのエントリポイントを指定する。",
        ["geometry"] = "ジオメトリシェーダーのエントリポイントを指定する。`#pragma target 4.0` 以上が必要になる。",
        ["hull"] = "ハルシェーダーのエントリポイントを指定する。テッセレーションに使い、`#pragma target 4.6` 以上が必要になる。",
        ["domain"] = "ドメインシェーダーのエントリポイントを指定する。テッセレーションに使い、`#pragma target 4.6` 以上が必要になる。",
        ["kernel"] =
            "コンピュートシェーダーのカーネルを宣言する。C# からは `FindKernel` で名前を引いて `Dispatch` する。"
            + "名前の後ろに書いたマクロは、このカーネルのコンパイルでだけ定義される。",
        ["surface"] = "サーフェスシェーダーの関数と照明モデルを指定する。ビルトインレンダーパイプライン専用。",
        ["target"] = "シェーダーモデルの下限を指定する。満たさない GPU では、この SubShader は使われない。",
        ["require"] = "必要な GPU の機能を個別に指定する (`geometry`、`compute` など)。満たさない GPU では、この SubShader は使われない。",
        ["only_renderers"] = "指定したグラフィックス API に対してだけコンパイルする。",
        ["exclude_renderers"] = "指定したグラフィックス API に対してはコンパイルしない。",
        ["skip_variants"] = "指定したシンボルを含むバリアントをコンパイルしない。",
        ["instancing_options"] = "GPU インスタンシングの動作を調整する (`assumeuniformscaling`、`procedural:関数名` など)。",
        ["hardware_tier_variants"] = "グラフィックスの階層 (Tier) ごとにバリアントを作る。",
        ["editor_sync_compilation"] = "エディタでこのシェーダーを非同期にコンパイルせず、使う前に同期でコンパイルする。",
        ["enable_d3d11_debug_symbols"] = "D3D11 向けにデバッグ情報を付けてコンパイルする。最適化も弱まるため、調査が済んだら外す。",
        ["use_dxc"] = "DXC コンパイラでコンパイルする。",
        ["never_use_dxc"] = "DXC を使わず、従来の FXC コンパイラでコンパイルする。",
        ["multi_compile_fog"] =
            "霧のモード (`FOG_LINEAR` / `FOG_EXP` / `FOG_EXP2`) ごとのバリアントを作る。"
            + "ビルドでは、プロジェクトで使っていない霧のモードのバリアントが取り除かれる。",
        ["multi_compile_instancing"] =
            "GPU インスタンシング用のバリアント (`INSTANCING_ON` など) を作る。"
            + "マテリアルで Enable GPU Instancing を有効にしたときに使われる。",
        ["multi_compile_fwdbase"] = "ビルトインの ForwardBase パスに必要なバリアント (ライトマップ、影、球面調和など) を作る。",
        ["multi_compile_fwdadd"] = "ビルトインの ForwardAdd パスに必要なバリアント (ライトの種類ごと) を作る。追加ライトの影は含まない。",
        ["multi_compile_fwdadd_fullshadows"] = "`multi_compile_fwdadd` に加え、追加ライトの影に必要なバリアントも作る。",
        ["multi_compile_shadowcaster"] = "ShadowCaster パスに必要なバリアント (点光源の影など) を作る。",
        ["multi_compile_prepassfinal"] = "ビルトインの Deferred パスなどに必要なバリアント (ライトマップ、GI など) を作る。",
        ["multi_compile_particles"] = "ビルトインのパーティクル用のバリアント (ソフトパーティクルなど) を作る。",
    };

    /// <summary>
    /// シンボルを宣言する <c>#pragma</c> の名前の先頭と、その説明。
    /// </summary>
    /// <remarks>
    /// <c>_local</c> や <c>_fragment</c> が後ろに付いた形は、ここに付け足して説明する。
    /// 組み合わせをすべて並べても、説明の中身は変わらない。
    /// </remarks>
    internal static readonly (string Prefix, string Description)[] KeywordPragmaDescriptions =
    [
        ("shader_feature",
            "マテリアルが使っているシンボルの組み合わせだけを、バリアントとしてビルドに含める。"
            + "**どのマテリアルも使っていないバリアントはビルドで取り除かれるため、実行時にスクリプトから有効にしても効かない。**"),
        ("multi_compile",
            "列挙したシンボルのすべての組み合わせを、バリアントとしてビルドに含める。"
            + "実行時に切り替えられるが、バリアントの数は宣言ごとに掛け算で増える。"),
        ("dynamic_branch",
            "バリアントを作らず、シンボルを実行時の `if` として分岐させる。"
            + "バリアントは増えないが、分岐のコストが毎回かかる。"),
    ];

    /// <summary>段階を限る接尾辞と、その段階の名前。</summary>
    internal static readonly Dictionary<string, string> PragmaStageSuffixes = new(StringComparer.Ordinal)
    {
        ["_vertex"] = "頂点シェーダー",
        ["_fragment"] = "フラグメントシェーダー",
        ["_hull"] = "ハルシェーダー",
        ["_domain"] = "ドメインシェーダー",
        ["_geometry"] = "ジオメトリシェーダー",
        ["_raytracing"] = "レイトレーシングシェーダー",
    };

    /// <summary>エントリポイントを指定する <c>#pragma</c> と、その段階の名前。</summary>
    internal static readonly Dictionary<string, string> EntryPointPragmas = new(StringComparer.Ordinal)
    {
        ["vertex"] = "頂点シェーダー",
        ["fragment"] = "フラグメントシェーダー",
        ["geometry"] = "ジオメトリシェーダー",
        ["hull"] = "ハルシェーダー",
        ["domain"] = "ドメインシェーダー",
        ["surface"] = "サーフェスシェーダー",
    };

    /// <summary>
    /// <c>#pragma</c> について答える。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。<c>#pragma</c> の上に無い場合は <see langword="null"/>。</returns>
    private static HoverResult? BuildForPragma(ShaderCompilation compilation, int offset)
    {
        ImmutableArray<HlslSyntaxToken> tokens = compilation.CodeTokens;

        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Kind != HlslSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || tokens[i + 1].Kind != HlslSyntaxKind.IdentifierToken
                || tokens[i + 1].Text != "pragma"
                || tokens[i + 1].IsAtLineStart
                || tokens[i + 2].Kind != HlslSyntaxKind.IdentifierToken
                || tokens[i + 2].IsAtLineStart)
            {
                continue;
            }

            // 行の終わりまでが引数である。
            int end = i + 3;
            while (end < tokens.Length
                   && !tokens[end].IsAtLineStart
                   && tokens[end].Kind != HlslSyntaxKind.EndOfFileToken)
            {
                end++;
            }

            if (offset < tokens[i].Span.Start || offset > tokens[end - 1].Span.End)
            {
                i = end - 1;
                continue;
            }

            HlslSyntaxToken name = tokens[i + 2];
            ImmutableArray<HlslSyntaxToken> arguments = tokens[(i + 3)..end];

            if (Touches(tokens[i + 1], offset) || Touches(name, offset))
            {
                return DescribePragma(name, arguments);
            }

            for (int j = 0; j < arguments.Length; j++)
            {
                if (arguments[j].Kind == HlslSyntaxKind.IdentifierToken && Touches(arguments[j], offset))
                {
                    return DescribePragmaArgument(name.Text, j, arguments[j]);
                }
            }

            return null;
        }

        return null;
    }

    /// <summary><c>#pragma</c> の名前を説明する。</summary>
    /// <param name="name">名前のトークン。</param>
    /// <param name="arguments">名前に続く引数のトークン。</param>
    /// <returns>説明。</returns>
    private static HoverResult DescribePragma(HlslSyntaxToken name, ImmutableArray<HlslSyntaxToken> arguments)
    {
        string line = Shorten(string.Join(" ", ["#pragma", name.Text, .. arguments.Select(a => a.Text)]));

        string body = PragmaDescriptions.TryGetValue(name.Text, out string? described)
            ? described
            : DescribeKeywordPragma(name.Text)
              ?? "このツールが説明を持たない `#pragma`。"
              + "Unity のバージョンやプラットフォーム固有のものである可能性がある。";

        return new HoverResult($"```hlsl\n{line}\n```\n\n{body}", name.Span);
    }

    /// <summary>
    /// シンボルを宣言する <c>#pragma</c> を、接尾辞まで含めて説明する。
    /// </summary>
    /// <param name="name"><c>#pragma</c> の名前。</param>
    /// <returns>説明。シンボルを宣言する形でない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>multi_compile_fog</c> のように、接尾辞として読めない形は説明しない。
    /// 名前が決まっているものは <see cref="PragmaDescriptions"/> で先に引いている。
    /// </remarks>
    internal static string? DescribeKeywordPragma(string name)
    {
        foreach ((string prefix, string description) in KeywordPragmaDescriptions)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = name[prefix.Length..];
            bool isLocal = rest.StartsWith("_local", StringComparison.Ordinal);

            if (isLocal)
            {
                rest = rest["_local".Length..];
            }

            string? stage = null;

            if (rest.Length > 0 && !PragmaStageSuffixes.TryGetValue(rest, out stage))
            {
                return null;
            }

            string local = isLocal
                ? "\n\n`_local`: このシンボルはこのシェーダーの中だけのもの。"
                  + "グローバルキーワードの上限を消費しないが、`Shader.EnableKeyword` では切り替わらない。"
                : string.Empty;

            string stageNote = stage is null
                ? string.Empty
                : $"\n\n`{name[^rest.Length..]}`: バリアントは{stage}にだけ作られ、他の段階では共有される。";

            return description + local + stageNote;
        }

        return null;
    }

    /// <summary><c>#pragma</c> の引数を説明する。</summary>
    /// <param name="pragma"><c>#pragma</c> の名前。</param>
    /// <param name="index">何番目の引数か (0 始まり)。</param>
    /// <param name="argument">引数のトークン。</param>
    /// <returns>説明。説明することが無い場合は <see langword="null"/>。</returns>
    private static HoverResult? DescribePragmaArgument(string pragma, int index, HlslSyntaxToken argument)
    {
        string? body = pragma switch
        {
            _ when ShaderSymbols.IsDeclaringPragmaName(pragma) => ShaderSymbols.IsNoSymbol(argument.Text)
                ? "シンボルを 1 つも有効にしない構成を表すプレースホルダー。"
                : $"`#pragma {pragma}` で宣言されたシェーダーのシンボル。",

            "kernel" => index == 0
                ? "コンピュートシェーダーのカーネルとして宣言された関数。"
                : "このカーネルのコンパイルでだけ定義されるマクロ。",

            "surface" => index switch
            {
                0 => "サーフェスシェーダーの関数。",
                1 => $"照明モデル。`Lighting{argument.Text}` という名前の関数が使われる。",
                _ => null,
            },

            _ when index == 0 && EntryPointPragmas.TryGetValue(pragma, out string? stage)
                => $"{stage}のエントリポイントとして指定された関数。",

            _ => null,
        };

        return body is null
            ? null
            : new HoverResult($"```hlsl\n{argument.Text}\n```\n\n{body}", argument.Span);
    }
}
