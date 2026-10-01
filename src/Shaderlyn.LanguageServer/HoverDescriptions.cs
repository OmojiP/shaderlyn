using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 要素ごとの説明文。
/// </summary>
internal static partial class HoverBuilder
{
    /// <summary>
    /// 型名を説明する。
    /// </summary>
    /// <param name="name">型名。</param>
    /// <param name="span">対応する範囲。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// <b>型名の上で「型を判定できません」と答えてはならない。</b>
    /// <c>half4</c> は型であって、型が不明な式ではない。
    /// 分類が分からない型 (構造体など) でも、型であることは分かる。
    /// </remarks>
    private static HoverResult DescribeType(string name, TextSpan span)
    {
        string body = HlslTypeClassifier.TryDescribeNumeric(name, out HlslNumericShape shape)
            ? shape.Kind switch
            {
                HlslNumericKind.Scalar => $"{shape.BaseName} のスカラー",
                HlslNumericKind.Vector => $"{shape.BaseName} の {shape.Columns} 成分ベクトル",
                HlslNumericKind.Matrix => $"{shape.BaseName} の {shape.Rows} 行 {shape.Columns} 列の行列",
                _ => "数値型",
            }
            : HlslTypeClassifier.Classify(name) switch
            {
                HlslTypeClass.Unknown => "型 (構造体、またはこのツールが知らない型)",
                HlslTypeClass.Sampler => "サンプラ",
                HlslTypeClass.Buffer => "バッファ",
                { } classified => $"テクスチャ ({classified})",
            };

        return new HoverResult($"```hlsl\n{name}\n```\n\n{body}", span);
    }

    /// <summary>
    /// 変数の宣言を説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="declaration">対象の宣言。</param>
    /// <param name="declarator">カーソルの下にある宣言子。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// uniform であるかどうかと、どの定数バッファに入っているかを出す。
    /// SRP Batcher が効くかどうかはこの 2 つで決まる。
    /// </remarks>
    private static HoverResult DescribeVariable(
        ShaderCompilation compilation,
        VariableDeclarationSyntax declaration,
        VariableDeclaratorSyntax declarator)
    {
        string type = declaration.Type.Name + (declarator.IsArray ? "[]" : string.Empty);
        string body = $"```hlsl\n{type} {declarator.Name}\n```";

        if (compilation.TryGetUniform(declarator.Name, out UniformSymbol? uniform))
        {
            body += uniform.ContainingBufferName is { } buffer
                ? $"\n\nuniform (`{buffer}` の中)"
                : "\n\nuniform (定数バッファの外)";
        }

        if (declaration.Type.Name is { Length: > 0 })
        {
            body += $"\n\n型: {DescribeTypeBody(declaration.Type.Name)}";
        }

        return new HoverResult(body, declarator.NameToken.Span);
    }

    /// <summary>仮引数を説明する。</summary>
    /// <param name="parameter">対象の仮引数。</param>
    /// <returns>説明。</returns>
    /// <remarks>セマンティクスは何が入ってくるかを決めるため、あわせて出す。</remarks>
    private static HoverResult DescribeParameter(ParameterSyntax parameter)
    {
        string semantics = parameter.Semantics.IsEmpty
            ? string.Empty
            : " : " + string.Join(" ", parameter.Semantics.Select(s => s.Name));

        return new HoverResult(
            $"```hlsl\n{parameter.Type.Name} {parameter.Name}{semantics}\n```\n\n仮引数",
            parameter.NameToken.Span);
    }

    /// <summary>関数の宣言を説明する。</summary>
    /// <param name="function">対象の関数。</param>
    /// <returns>説明。</returns>
    private static HoverResult DescribeFunction(FunctionDeclarationSyntax function)
        => new(
            $"```hlsl\n{FormatSignature(function)}\n```\n\n関数",
            function.NameToken.Span);

    /// <summary>関数の形を 1 行で表す。</summary>
    /// <param name="function">対象の関数。</param>
    /// <returns>組み立てた文字列。</returns>
    private static string FormatSignature(FunctionDeclarationSyntax function)
    {
        string parameters = string.Join(
            ", ", function.ParameterList.Select(p => $"{p.Type.Name} {p.Name}"));

        return $"{function.ReturnType.Name} {function.Name}({parameters})";
    }

    /// <summary>構造体の宣言を説明する。</summary>
    /// <param name="structure">対象の構造体。</param>
    /// <param name="nameToken">構造体の名前のトークン。</param>
    /// <returns>説明。</returns>
    /// <remarks>フィールドの一覧を出す。型を辿るときに最初に見たいものである。</remarks>
    private static HoverResult DescribeStruct(StructDeclarationSyntax structure, HlslSyntaxToken nameToken)
    {
        IEnumerable<string> fields = structure.Members
            .OfType<VariableDeclarationSyntax>()
            .SelectMany(m => m.Variables.Select(v => $"    {m.Type.Name} {v.Name};"));

        return new HoverResult(
            $"```hlsl\nstruct {structure.Name}\n{{\n{string.Join("\n", fields)}\n}}\n```",
            nameToken.Span);
    }

    /// <summary>定数バッファの宣言を説明する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="buffer">対象の定数バッファ。</param>
    /// <param name="nameToken">定数バッファの名前のトークン。</param>
    /// <returns>説明。</returns>
    private static HoverResult DescribeConstantBuffer(
        ShaderCompilation compilation,
        ConstantBufferDeclarationSyntax buffer,
        HlslSyntaxToken nameToken)
    {
        int members = buffer.Members.OfType<VariableDeclarationSyntax>().Sum(m => m.Variables.Count());

        string note = compilation.Profile.MaterialConstantBufferName is { } material
                      && string.Equals(buffer.Name, material, StringComparison.Ordinal)
            ? $"\n\n{compilation.Profile.DisplayName} がマテリアルの値に使う定数バッファ。"
              + "ここに入っていない uniform があると SRP Batcher が無効になる。"
            : string.Empty;

        return new HoverResult(
            $"```hlsl\nCBUFFER_START({buffer.Name})\n```\n\n定数バッファ ({members} 件の uniform){note}",
            nameToken.Span);
    }

    /// <summary>
    /// 呼び出しの対象を説明する。
    /// </summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <param name="identifier">呼び出しの対象となっている識別子。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// <b>コンストラクタは型である。</b>
    /// <c>half4(1, 0, 0, 0)</c> の <c>half4</c> を
    /// 「型が不明な式」として扱ってはならない。
    /// </remarks>
    private static HoverResult DescribeCallee(AnalyzedProgram program, IdentifierExpressionSyntax identifier)
    {
        string name = identifier.Name;

        if (HlslTypeClassifier.Classify(name) != HlslTypeClass.Unknown)
        {
            HoverResult type = DescribeType(name, identifier.Span);
            return type with { Markdown = type.Markdown + "\n\nこの位置ではコンストラクタとして使われている。" };
        }

        // 実際の宣言が見つかるなら、そこから仮引数を取り出す。
        // 「引数 1 個」では、何を渡せばよいのかが分からない。
        List<string> declared = FindDeclaredSignatures(program, name);

        if (declared.Count > 0)
        {
            string note = declared.Count > 1
                ? "\n\n同じ名前の宣言が複数ある。実引数の個数で絞れない場合、戻り値の型は主張しない。"
                : string.Empty;

            return new HoverResult(
                $"```hlsl\n{string.Join("\n", declared)}\n```\n\n宣言されている関数{note}", identifier.Span);
        }

        if (program.FunctionSignatures.TryGetValue(name, out var signatures) && !signatures.IsEmpty)
        {
            // 宣言そのものを取り出せなかった場合の控え。
            // マクロで組み立てられた関数などがここに来る。
            string forms = string.Join(
                "\n", signatures.Select(s => $"{s.ReturnType} {name}(引数 {DescribeArity(s)})"));

            string note = signatures.Length > 1
                ? "\n\n同じ名前の宣言が複数ある。実引数の個数で絞れない場合、戻り値の型は主張しない。"
                : string.Empty;

            return new HoverResult($"```hlsl\n{forms}\n```\n\n宣言されている関数{note}", identifier.Span);
        }

        return HlslIntrinsics.GetResultShape(name) != IntrinsicResultRule.Unknown
               || HlslIntrinsics.GetTextureMethodReturnType(name) != TextureMethodReturnType.Unknown
            ? new HoverResult($"```hlsl\n{name}\n```\n\n組み込み関数", identifier.Span)
            : new HoverResult(
                $"```hlsl\n{name}\n```\n\n宣言が見つからない。"
                + "include が解決できていないか、マクロとして定義されている可能性がある。",
                identifier.Span);
    }

    /// <summary>
    /// 呼び出された関数の宣言を探し、仮引数まで含めた形を組み立てる。
    /// </summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <param name="name">関数の名前。</param>
    /// <returns>見つかった形。無い場合は空。</returns>
    /// <remarks>
    /// <para>
    /// <b>引数の個数だけでは、何を渡せばよいか分からない。</b>
    /// <c>TransformObjectToHClip(引数 1 個)</c> と言われても、
    /// 渡すのが <c>float3</c> なのか <c>float4</c> なのかが分からない。
    /// 構文木には取り込んだヘッダの宣言も入っているので、そこから取り出す。
    /// </para>
    /// <para>
    /// 同じ形が複数回現れることがある。
    /// プロトタイプ宣言と定義の両方が書かれている場合や、
    /// ヘッダを複数の経路で取り込んでいる場合である。同じ文字列は 1 度だけ出す。
    /// </para>
    /// </remarks>
    private static List<string> FindDeclaredSignatures(AnalyzedProgram program, string name)
    {
        List<string> forms = [];

        foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
        {
            if (node is not FunctionDeclarationSyntax function || function.Name != name)
            {
                continue;
            }

            string form = FormatSignature(function);

            if (!forms.Contains(form, StringComparer.Ordinal))
            {
                forms.Add(form);
            }
        }

        return forms;
    }

    /// <summary>関数が受け取れる引数の個数を表す。</summary>
    /// <param name="signature">対象の形。</param>
    /// <returns>組み立てた文字列。</returns>
    private static string DescribeArity(FunctionSignature signature)
        => signature.MinimumArguments == signature.MaximumArguments
            ? $"{signature.MinimumArguments} 個"
            : $"{signature.MinimumArguments}〜{signature.MaximumArguments} 個";

    /// <summary>
    /// 値を表す式を説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="binder">型の評価器を用意する束縛器。</param>
    /// <param name="expression">対象の式。</param>
    /// <returns>説明。</returns>
    /// <remarks>
    /// <para>
    /// <b>ここだけが「型を判定できません」と答えてよい場所である。</b>
    /// 値を表す式について型が分からないことは、
    /// 型を根拠にするルールが何も報告しないことと直結している。
    /// </para>
    /// <para>
    /// ただし、そう答える前に構成ごとの型を試す。
    /// 同じ名前を条件ごとに違う型で宣言するのは普通の書き方であり、
    /// 1 本の木では型が決まらなくても、構成を決めれば決まる。
    /// 診断はすでにそうしている (<see cref="ExpressionTypeBinder.EnumerateAssumptions(HlslSyntaxNode, HlslExpressionSyntax, ConditionMap)"/>)。
    /// ホバーだけが「判定できません」と答えると、
    /// 指摘が出ない理由を調べる利用者を誤った方向へ導く。
    /// </para>
    /// </remarks>
    private static HoverResult DescribeExpression(
        ShaderCompilation compilation,
        ExpressionTypeBinder binder,
        HlslExpressionSyntax expression)
    {
        string text = Shorten(compilation.Text.ToString(expression.Span));

        string body = binder.GetEvaluatorFor(expression).Evaluate(expression) is { } type
            ? $"型: **`{type}`**"
            : DescribeTypeByConfiguration(compilation, binder, expression)
              ?? "**型を判定できません。**\n\n"
                 + "型を根拠にするルールは、この式について何も報告しません。";

        return new HoverResult($"```hlsl\n{text}\n```\n\n{body}", expression.Span);
    }

    /// <summary>
    /// 構成ごとに型を求めて説明する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="binder">型の評価器を用意する束縛器。</param>
    /// <param name="expression">対象の式。</param>
    /// <returns>説明。構成を決めても型が分からない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>どの構成でも型が分かるときだけ答える。</b>
    /// 一部の構成しか分からない状態で型を並べると、
    /// 並んでいない構成が「存在しない」のか「分からなかった」のか区別が付かない。
    /// </para>
    /// <para>
    /// どの構成でも同じ型になるなら、構成を並べない。
    /// <c>float</c> と <c>float3</c> のどちらで宣言されていても <c>a.x</c> は <c>float</c> であり、
    /// その事実に構成の一覧は要らない。
    /// </para>
    /// </remarks>
    private static string? DescribeTypeByConfiguration(
        ShaderCompilation compilation,
        ExpressionTypeBinder binder,
        HlslExpressionSyntax expression)
    {
        ConditionMap conditions = compilation.GetConditionMap();
        List<(SymbolCondition Condition, string Type)> found = [];

        foreach (SymbolCondition assumption in binder.EnumerateAssumptions(expression, expression, conditions))
        {
            if (binder.GetEvaluatorFor(expression, assumption, conditions).Evaluate(expression) is not { } type)
            {
                return null;
            }

            found.Add((assumption, type));
        }

        if (found.Count == 0)
        {
            return null;
        }

        string[] types = [.. found.Select(f => f.Type).Distinct(StringComparer.Ordinal)];

        if (types.Length == 1)
        {
            return $"型: **`{types[0]}`**\n\n"
                   + "宣言は構成によって変わりますが、型はどの構成でも同じです。";
        }

        return "**型は構成によって変わります。**\n\n"
               + string.Join(
                   "\n",
                   found.Select(f => $"- {(f.Condition.IsAlways ? "既定" : $"`{f.Condition}`")} のとき **`{f.Type}`**"));
    }

    /// <summary>型の分類を短く表す。</summary>
    /// <param name="name">型名。</param>
    /// <returns>組み立てた文字列。</returns>
    private static string DescribeTypeBody(string name)
    {
        HoverResult described = DescribeType(name, default);
        int separator = described.Markdown.LastIndexOf("\n\n", StringComparison.Ordinal);

        return separator < 0 ? name : described.Markdown[(separator + 2)..];
    }

    /// <summary>表示できる長さに整えて返す。</summary>
    /// <param name="text">対象の文字列。</param>
    /// <returns>表示する文字列。</returns>
    private static string Shorten(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 80 ? flat : flat[..80] + "…";
    }
}
