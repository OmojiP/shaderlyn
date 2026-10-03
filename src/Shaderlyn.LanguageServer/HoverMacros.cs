using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// マクロの説明。
/// </summary>
internal static partial class HoverBuilder
{
    /// <summary>
    /// マクロについて答える。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>説明。マクロの上に無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>構文木にはマクロの呼び出しが残っていない。</b>
    /// 展開されて消えているため、構文木を辿っても
    /// <c>CBUFFER_START</c> や <c>TEXTURE2D</c> の上では何も見つからない。
    /// Unity のシェーダーはこれらを多用するので、
    /// 答えられないままにすると「ほとんどの語で何も出ない」ことになる。
    /// </para>
    /// <para>
    /// 展開前のトークン列を見て、名前がマクロとして定義されていれば
    /// その定義を出す。マクロが何に化けるかは、
    /// 指摘の理由を追うときに最も知りたいことの 1 つである。
    /// </para>
    /// </remarks>
    private static HoverResult? BuildForMacro(ShaderCompilation compilation, int offset)
    {
        HlslSyntaxToken? token = CursorTarget.FindIdentifier(compilation, offset);

        if (token is null)
        {
            return null;
        }

        // #define の行の名前の上なら、マクロ表ではなく、その行に書かれた定義を答える。
        // マクロ表には既定の構成で最後に効いた定義しか残らないので、
        // #else の側の #define の上で #ifdef の側の定義を答えることになる。
        if (DescribeWrittenDefinition(compilation, token) is { } written)
        {
            return written;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            // 定数バッファの名前はマクロの引数として書かれる。
            // 展開後の宣言は呼び出し位置へ移されるため、構文木からは辿れない。
            //
            // ConstantBufferSymbol は構造体なので、FirstOrDefault と is { } を
            // 組み合わせてはならない。見つからなくても既定値が返り、
            // パターンが必ず一致して、中身の無い値を使ってしまう。
            foreach (ConstantBufferSymbol candidate in program.ConstantBuffers)
            {
                if (string.Equals(candidate.Name, token.Text, StringComparison.Ordinal))
                {
                    return DescribeConstantBufferSymbol(compilation, candidate, token.Span);
                }
            }

            if (!program.Tree.PreprocessResult.Macros.TryGetValue(token.Text, out MacroDefinition? macro))
            {
                continue;
            }

            if (DescribeExpansionsByCondition(compilation, program, token) is { } byCondition)
            {
                return new HoverResult(
                    $"```hlsl\n{token.Text}\n```\n\n"
                    + (macro.IsFunctionLike ? "関数形式マクロ" : "マクロ")
                    + "。**この位置の展開結果は構成によって変わります。**\n\n"
                    + byCondition,
                    token.Span);
            }

            // 条件ごとに書き分けた定義なら、既定の構成の 1 つだけを出さずに全部を並べる。
            if (DescribeDefinitionsByCondition(program, token.Text) is { } definitions)
            {
                return new HoverResult(
                    $"```hlsl\n{token.Text}\n```\n\n"
                    + (macro.IsFunctionLike ? "関数形式マクロ" : "マクロ")
                    + "。**定義は構成によって変わります。**\n\n"
                    + definitions,
                    token.Span);
            }

            return new HoverResult(
                $"```hlsl\n{FormatDefinition(macro)}\n```\n\n"
                + (macro.IsFunctionLike ? "関数形式マクロ" : "マクロ")
                + "。展開後のコードに名前は残らない。",
                token.Span);
        }

        return null;
    }

    /// <summary>マクロの定義を 1 行で表す。</summary>
    /// <param name="macro">対象の定義。</param>
    /// <returns><c>#define</c> の形の文字列。</returns>
    private static string FormatDefinition(MacroDefinition macro)
    {
        string parameters = macro.IsFunctionLike ? $"({string.Join(", ", macro.Parameters)})" : string.Empty;
        string body = string.Join(" ", macro.Body.Select(t => t.Text));
        string shown = body.Length <= 200 ? body : body[..200] + "…";

        return $"#define {macro.Name}{parameters} {shown}";
    }

    /// <summary>
    /// カーソルが <c>#define</c> の行の名前の上なら、その行に書かれた定義を説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="token">カーソルの下にある名前のトークン (展開前)。</param>
    /// <returns>説明。<c>#define</c> の名前の上でなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// その定義に通る条件も添える。読み飛ばした分岐の定義でも、別の構成では効いている。
    /// 同じ名前を別の条件で定義していれば、それも並べる。
    /// </remarks>
    private static HoverResult? DescribeWrittenDefinition(ShaderCompilation compilation, HlslSyntaxToken token)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach ((MacroDefinition definition, SymbolCondition condition) in program.Tree.PreprocessResult.WrittenDefinitions)
            {
                if (definition.NameToken.Span != token.Span
                    || !string.Equals(definition.NameToken.Source.FilePath, token.Source.FilePath, StringComparison.Ordinal))
                {
                    continue;
                }

                string kind = definition.IsFunctionLike ? "関数形式マクロ" : "マクロ";
                string where = condition.IsAlways
                    ? string.Empty
                    : condition.IsUnknown
                        ? "この定義に通る `#ifdef` の条件は追えていません。"
                        : $"`#if {condition}` のときの定義。";

                string others = DescribeDefinitionsByCondition(program, definition.Name) is { } listed
                    ? $"\n\n---\n\n同じ名前の定義:\n\n{listed}"
                    : string.Empty;

                return new HoverResult(
                    $"```hlsl\n{FormatDefinition(definition)}\n```\n\n{kind}。{where}展開後のコードに名前は残らない。{others}",
                    token.Span);
            }
        }

        return null;
    }

    /// <summary>
    /// 条件ごとに書き分けたマクロの定義を並べる。
    /// </summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <param name="name">マクロの名前。</param>
    /// <returns>定義を並べた文字列。書き分けていなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 中身が 2 通り以上あり、どれかに条件が付いているときだけ並べる。
    /// 同じ中身を何度定義していても、読み手に伝わることは増えない。
    /// </remarks>
    private static string? DescribeDefinitionsByCondition(AnalyzedProgram program, string name)
    {
        (MacroDefinition Definition, SymbolCondition Condition)[] definitions =
        [
            .. program.Tree.PreprocessResult.WrittenDefinitions
                .Where(d => string.Equals(d.Definition.Name, name, StringComparison.Ordinal)),
        ];

        if (definitions.Select(d => FormatDefinition(d.Definition)).Distinct(StringComparer.Ordinal).Count() < 2
            || definitions.All(d => d.Condition.IsAlways))
        {
            return null;
        }

        return string.Join(
            "\n",
            definitions.Select(d =>
                $"- {(d.Condition.IsAlways ? "条件なし" : d.Condition.IsUnknown ? "条件を追えていない構成" : $"`{d.Condition}` のとき")}: "
                + $"`{FormatDefinition(d.Definition)}`"));
    }

    /// <summary>
    /// マクロの呼び出し位置で、構成ごとに何へ展開されたかを説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象のコードブロック。</param>
    /// <param name="token">カーソルの下にあるマクロ名のトークン (展開前)。</param>
    /// <returns>構成ごとの展開結果を並べた文字列。構成によって変わらなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>マクロ表の定義を出すだけでは、既定の構成の値しか分からない。</b>
    /// <c>#ifdef _A</c> で <c>float3</c>、<c>#else</c> で <c>float4</c> と定義したマクロは、
    /// マクロ表には既定の構成の 1 つしか残らない。
    /// 使う文は定義ごとに複製されて 1 本の木に並んでいるので (条件の巻き上げ)、
    /// その位置に展開されたトークンを複製ごとに集め、複製の条件と組にして出す。
    /// </para>
    /// <para>
    /// 展開されたトークンは呼び出し位置へ移されている。
    /// 呼び出し位置にあり、途切れずに並んだ展開のトークンを 1 回分の展開とみなす。
    /// </para>
    /// </remarks>
    private static string? DescribeExpansionsByCondition(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        HlslSyntaxToken token)
    {
        ImmutableArray<HlslSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
        List<List<HlslSyntaxToken>> runs = [];
        List<HlslSyntaxToken>? current = null;

        foreach (HlslSyntaxToken expanded in tokens)
        {
            bool atCall = expanded.IsFromMacroExpansion
                          && expanded.Span.Start <= token.Span.Start
                          && token.Span.End <= expanded.Span.End
                          && string.Equals(expanded.Source.FilePath, token.Source.FilePath, StringComparison.Ordinal);

            if (!atCall)
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = [];
                runs.Add(current);
            }

            current.Add(expanded);
        }

        if (runs.Count < 2)
        {
            return null;
        }

        // 展開の先頭から始まるノードのうち、最も外側のもの。複製の条件はそこから引く。
        Dictionary<HlslSyntaxToken, HlslSyntaxNode?> starts = [];

        foreach (List<HlslSyntaxToken> run in runs)
        {
            starts[run[0]] = null;
        }

        foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
        {
            if (node is HlslSyntaxNode hlsl
                && hlsl.FirstToken is { } first
                && starts.TryGetValue(first, out HlslSyntaxNode? found)
                && found is null)
            {
                starts[first] = hlsl;
            }
        }

        ConditionMap conditions = compilation.GetConditionMap();
        List<(SymbolCondition Condition, string Text)> byCondition = [];

        foreach (List<HlslSyntaxToken> run in runs)
        {
            SymbolCondition condition = starts[run[0]] is { } node
                ? conditions.GetCondition(node)
                : SymbolCondition.Always;

            string text = string.Join(" ", run.Select(t => t.Text));
            int index = byCondition.FindIndex(e => string.Equals(e.Text, text, StringComparison.Ordinal));

            if (index < 0)
            {
                byCondition.Add((condition, text));
            }
            else
            {
                byCondition[index] = (conditions.Simplify(byCondition[index].Condition.Or(condition)), text);
            }
        }

        // 同じ条件の下にしか無いなら、構成によって変わったのではない。
        // 1 つのマクロが宣言を 2 つ作る形 (TEXTURE2D など) でも、展開は途切れて並ぶ。
        if (byCondition.Count < 2 || byCondition.Any(e => e.Condition.IsAlways))
        {
            return null;
        }

        return string.Join(
            "\n",
            byCondition.Select(e => $"- `{(e.Condition.IsUnknown ? "条件を追えていない構成" : e.Condition.ToString())}` のとき **`{Shorten(e.Text)}`**"));
    }

    /// <summary>
    /// 定数バッファを説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="buffer">対象の定数バッファ。</param>
    /// <param name="span">対応する範囲。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// マテリアル用の定数バッファかどうかを出す。
    /// SRP Batcher が効くかどうかはここに入っているかで決まる。
    /// </remarks>
    private static HoverResult DescribeConstantBufferSymbol(
        ShaderCompilation compilation,
        ConstantBufferSymbol buffer,
        TextSpan span)
    {
        string members = string.Join(
            "\n", buffer.Members.Select(m => $"    {m.TypeName} {m.Name};"));

        string note = compilation.Profile.MaterialConstantBufferName is { } material
                      && string.Equals(buffer.Name, material, StringComparison.Ordinal)
            ? $"\n\n{compilation.Profile.DisplayName} がマテリアルの値に使う定数バッファ。"
              + "ここに入っていない uniform があると SRP Batcher が無効になる。"
            : string.Empty;

        return new HoverResult(
            $"```hlsl\nCBUFFER_START({buffer.Name})\n{members}\nCBUFFER_END\n```\n\n"
            + $"定数バッファ ({buffer.Members.Length} 件の uniform){note}",
            span);
    }
}
