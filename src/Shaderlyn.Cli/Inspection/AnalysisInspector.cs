using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Cli.Inspection;

/// <summary>
/// 解析の中身を 1 つの HTML にまとめて書き出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>これは診断を出すための機能ではない。</b>
/// ルールが何を見て判断したのかを人が確かめるためのものである。
/// 誤検出の報告を受けたとき、原因が構文解析なのかマクロ展開なのか型判定なのかを
/// 切り分ける手段が無いと、再現環境を作るところから始めることになる。
/// </para>
/// <para>
/// <b>出力は 1 ファイルで完結させる。</b>
/// 外部の CSS や JavaScript を参照すると、
/// 調査結果をそのまま添付して共有することができなくなる。
/// </para>
/// </remarks>
internal static partial class AnalysisInspector
{
    /// <summary>
    /// 構文木 1 つあたりに書き出すノード数の上限。
    /// </summary>
    /// <remarks>
    /// 展開後の HLSL は URP のヘッダ群を含めて数十万ノードに達する。
    /// 全部を書き出すと数百 MB の HTML になり、開くこともできない。
    /// 上限に達したことは画面に表示するので、何も伝えずに切り詰めたことにはならない。
    /// </remarks>
    private const int MaxNodes = 20000;

    /// <summary>
    /// 解析結果を表示する HTML を組み立てる。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="diagnostics">そのファイルに対して報告された診断。</param>
    /// <returns>単独で開ける HTML。</returns>
    public static string BuildHtml(ShaderCompilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        return HtmlTemplate
            .Replace("/*DATA*/", BuildJson(compilation, diagnostics), StringComparison.Ordinal)
            .Replace("/*SCRIPT*/", Script + "\n" + TabsScript, StringComparison.Ordinal);
    }

    /// <summary>
    /// 画面が読み取るデータを JSON として組み立てる。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="diagnostics">報告された診断。</param>
    /// <returns>JSON 文字列。</returns>
    private static string BuildJson(ShaderCompilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("filePath", compilation.Text.FilePath);
            writer.WriteString("source", compilation.Text.Content);
            writer.WriteString("shaderName", compilation.ShaderName);
            writer.WriteString("profile", compilation.Profile.DisplayName);
            writer.WriteBoolean("hasCompleteDependencies", compilation.HasCompleteDependencies);

            WriteStringArray(writer, "unresolvedIncludes", compilation.UnresolvedIncludes);
            WriteStringArray(writer, "resolvedIncludes", compilation.AllIncludePaths);

            writer.WritePropertyName("shaderLab");
            WriteNode(writer, compilation.ShaderLabTree.Root, compilation.Text, new NodeBudget());

            WritePrograms(writer, compilation);
            WriteUnexploredCombinations(writer, compilation);
            WriteProperties(writer, compilation);
            WriteTokens(writer, compilation);
            WriteExpressions(writer, compilation);
            WriteMacros(writer, compilation);
            WriteDiagnostics(writer, diagnostics);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>文字列の並びを書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="name">プロパティ名。</param>
    /// <param name="values">書き出す値。</param>
    private static void WriteStringArray(Utf8JsonWriter writer, string name, ImmutableArray<string> values)
    {
        writer.WriteStartArray(name);

        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    /// <summary>書き出したノード数を数える。</summary>
    /// <remarks>木の走査中に上限へ達したことを伝えるため、参照型で持ち回す。</remarks>
    private sealed class NodeBudget
    {
        /// <summary>書き出したノードの数。</summary>
        public int Written { get; set; }

        /// <summary>上限に達したかどうか。</summary>
        public bool Exhausted => Written >= MaxNodes;
    }

    /// <summary>
    /// 構文木のノードを再帰的に書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="node">対象のノード。</param>
    /// <param name="text">位置を解釈するソーステキスト。</param>
    /// <param name="budget">ノード数の予算。</param>
    /// <param name="conditions">出現条件の索引。ShaderLab の木では <see langword="null"/>。</param>
    /// <param name="parent">親ノードの出現条件。同じなら書かない。</param>
    /// <remarks>
    /// 種別はクラス名から <c>Syntax</c> を落としたものを使う。
    /// 画面に出すのは人が読むための名前であり、実装の型名がそのまま最も分かりやすい。
    /// </remarks>
    private static void WriteNode(
        Utf8JsonWriter writer,
        SyntaxNode node,
        SourceText text,
        NodeBudget budget,
        ConditionMap? conditions = null,
        SymbolCondition parent = default)
    {
        budget.Written++;

        writer.WriteStartObject();
        writer.WriteString("kind", DescribeKind(node));
        writer.WriteNumber("start", node.Span.Start);
        writer.WriteNumber("length", node.Span.Length);
        writer.WriteString("line", text.GetLinePositionSpan(node.Span).Start.ToString());
        writer.WriteString("text", DescribeText(node, text));
        writer.WriteBoolean("expanded", IsFromMacroExpansion(node));

        // このファイルが書いたマクロの本体から来たノードは、本体の位置を指す。
        // そこが報告の位置でもある (HlslSyntaxToken.MacroDefinitionSpan)。
        // 利用者のヘッダで定義したマクロの本体は、このファイルの行では表せないので出さない。
        if (node is HlslSyntaxNode own
            && own.FirstToken?.MacroDefinitionSpan is { } definition
            && string.Equals(own.FirstToken.MacroDefinitionSource?.FilePath, text.FilePath, StringComparison.Ordinal))
        {
            writer.WriteString("macroBody", text.GetLinePositionSpan(definition).Start.ToString());
        }

        HlslSyntaxNode? hlsl = node as HlslSyntaxNode;
        SymbolCondition condition = parent;

        // #ifdef で守られたノードには、どの条件のもとで存在するかを添える。
        // 親と同じ条件なら書かない。部分木のすべてに同じラベルが並ぶと、
        // どこで条件が変わったのかが読み取れなくなる。
        if (conditions is not null && hlsl is not null)
        {
            condition = conditions.GetCondition(hlsl);

            if (condition != parent)
            {
                writer.WriteString("condition", condition.ToString());
            }
        }

        writer.WriteStartArray("children");

        foreach (SyntaxNode child in MergeChildren(node, hlsl, conditions))
        {
            if (budget.Exhausted)
            {
                break;
            }

            WriteNode(writer, child, text, budget, conditions, condition);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// 既定の構成の子と、バリアントにしか無い子を、1 本の木として並べる。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <param name="hlsl">対象が HLSL のノードならそれ。違えば <see langword="null"/>。</param>
    /// <param name="conditions">出現条件の索引。</param>
    /// <returns>並べた子。</returns>
    /// <remarks>
    /// <para>
    /// <b>これが無いと 1 本の木にならない。</b>
    /// <c>#ifdef</c> がマクロの定義を切り替えている場合、使う側の 1 行は
    /// 既定の構成では空文になり、バリアントでは呼び出し式になる。
    /// 呼び出し式はバリアントの木にしか無いので、
    /// 既定の木を歩くだけでは「<c>;</c> があるだけ」に見えてしまう。
    /// </para>
    /// <para>
    /// 書かれた位置の順に並べる。読み手は書かれた順に読む。
    /// 足すものが無ければ、並べ替えも入れ物も作らない。
    /// 木のほとんどの場所がそれである。
    /// </para>
    /// </remarks>
    private static IEnumerable<SyntaxNode> MergeChildren(
        SyntaxNode node,
        HlslSyntaxNode? hlsl,
        ConditionMap? conditions)
    {
        if (conditions is null || hlsl is null || !conditions.HasInsertedChildren)
        {
            return node.ChildNodes();
        }

        IReadOnlyList<ConditionalNode> inserted = conditions.GetInsertedChildren(hlsl);

        if (inserted.Count == 0)
        {
            return node.ChildNodes();
        }

        return node.ChildNodes()
            .Concat(inserted.Select(i => (SyntaxNode)i.Node))
            .OrderBy(n => n.Span.Start);
    }

    /// <summary>
    /// ノードの中身を表す文字列を返す。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <returns>画面に出す文字列。</returns>
    /// <remarks>
    /// <para>
    /// <b>ソースを範囲で切り出すだけでは足りない。</b>
    /// マクロ展開で生まれたトークンは、すべてマクロを呼び出した位置へ移されている。
    /// そのため <c>TEXTURE2D(_BaseMap);</c> の展開結果は、
    /// 型も宣言子もどちらも <c>TEXTURE2D</c> と表示され、
    /// 実際に何へ展開されたのかが分からなくなる。
    /// </para>
    /// <para>
    /// HLSL のノードはトークンを並べ直して表す。
    /// ShaderLab は展開を伴わないので、ソースをそのまま切り出せばよい。
    /// </para>
    /// </remarks>
    private static string DescribeText(SyntaxNode node, SourceText text)
    {
        if (node is not HlslSyntaxNode hlsl)
        {
            return Shorten(text.ToString(node.Span));
        }

        StringBuilder builder = new();
        HlslSyntaxKind previous = HlslSyntaxKind.BadToken;

        foreach (HlslSyntaxToken token in hlsl.DescendantTokens())
        {
            if (builder.Length > MaxTextLength)
            {
                break;
            }

            if (builder.Length > 0 && NeedsSpace(previous, token.Kind))
            {
                builder.Append(' ');
            }

            builder.Append(token.Text);
            previous = token.Kind;
        }

        return Shorten(builder.ToString());
    }

    /// <summary>
    /// 2 つのトークンの間に空白が要るかを判定する。
    /// </summary>
    /// <param name="previous">前のトークンの種別。</param>
    /// <param name="current">後のトークンの種別。</param>
    /// <returns>空白が要る場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 元の空白は復元できない。マクロ展開後のトークンは
    /// 定義側の空白も呼び出し側の空白も失っているためである。
    /// <c>uv . x</c> のように読みにくくならない程度の規則で並べ直す。
    /// </remarks>
    private static bool NeedsSpace(HlslSyntaxKind previous, HlslSyntaxKind current)
    {
        if (previous is HlslSyntaxKind.DotToken
            or HlslSyntaxKind.OpenParenToken or HlslSyntaxKind.OpenBracketToken)
        {
            return false;
        }

        return current is not (HlslSyntaxKind.DotToken or HlslSyntaxKind.CommaToken
            or HlslSyntaxKind.SemicolonToken
            or HlslSyntaxKind.CloseParenToken or HlslSyntaxKind.CloseBracketToken
            or HlslSyntaxKind.OpenParenToken or HlslSyntaxKind.OpenBracketToken);
    }

    /// <summary>表示に使う文字列の上限。</summary>
    private const int MaxTextLength = 120;

    /// <summary>長い文字列を切り詰める。</summary>
    /// <param name="text">対象の文字列。</param>
    /// <returns>切り詰めた文字列。</returns>
    private static string Shorten(string text)
        => text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "…";

    /// <summary>
    /// ノードがマクロ展開で生まれたものかどうかを判定する。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>展開で生まれたものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 展開で生まれたノードは位置がすべて呼び出し位置に重なる。
    /// 印を出しておかないと、同じ行が何度も並ぶ理由が読み手に伝わらない。
    /// </remarks>
    private static bool IsFromMacroExpansion(SyntaxNode node)
        => node is HlslSyntaxNode hlsl
           && hlsl.DescendantTokens().FirstOrDefault() is { IsFromMacroExpansion: true };

    /// <summary>ノードの種別を表す名前を返す。</summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>画面に出す名前。</returns>
    private static string DescribeKind(SyntaxNode node)
    {
        string name = node.GetType().Name;
        return name.EndsWith("Syntax", StringComparison.Ordinal) ? name[..^"Syntax".Length] : name;
    }

    /// <summary>
    /// 埋め込みコードブロックごとの解析結果を書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <remarks>
    /// <b>このファイルに書かれた宣言だけを木として出す。</b>
    /// 展開後の構文木には include したヘッダの中身が丸ごと含まれており、
    /// そのまま出すと利用者が書いた数十行がヘッダ数十万ノードの中に埋もれる。
    /// </remarks>
    private static void WritePrograms(Utf8JsonWriter writer, ShaderCompilation compilation)
    {
        writer.WriteStartArray("programs");

        // 条件は木の中に載っている。#ifdef の両方の分岐は 1 本の木に並んでおり、
        // ノードごとに「どの条件のもとで存在するか」が引ける。
        ConditionMap condition = compilation.GetConditionMap();

        // バリアントは、突き合わせられなかった箇所があるときだけ別に出す。
        // すべて突き合わせられたなら、バリアントにしかないノードは
        // 既定の木の中へ条件付きで並べてあるので、同じものを 2 度見せることになる。
        // 突き合わせられなかった箇所については条件が分かっていないため、
        // そこはバリアントの木そのものを見せるほかない。
        IEnumerable<AnalyzedProgram> programs = condition.IsComplete
            ? compilation.Programs
            : compilation.Programs.Concat(compilation.SymbolVariants);

        foreach (AnalyzedProgram program in programs)
        {
            NodeBudget budget = new();

            writer.WriteStartObject();
            writer.WriteString("label", DescribeProgram(compilation, program));
            writer.WriteNumber("start", program.CodeSpan.Start);
            writer.WriteNumber("length", program.CodeSpan.Length);

            // 条件で中身が変わるマクロを使う文を、定義ごとに複製した回数。
            // 同じ位置に複数の形が並ぶのはこれが働いたためであり、
            // 見えていないと「なぜ 2 回出るのか」が読み取れない。
            PreprocessResult result = program.Tree.PreprocessResult;

            if (result.HoistedUnits > 0 || result.HoistGiveUps > 0)
            {
                writer.WriteNumber("hoistedUnits", result.HoistedUnits);
                writer.WriteNumber("hoistRetries", result.HoistRetries);
                writer.WriteNumber("hoistGiveUps", result.HoistGiveUps);
            }

            writer.WriteStartArray("declarations");

            // 根の直下にも、バリアントにしか無い宣言が入ることがある。
            foreach (SyntaxNode child in MergeChildren(program.Tree.Root, program.Tree.Root, condition))
            {
                if (budget.Exhausted)
                {
                    break;
                }

                if (child is HlslDeclarationSyntax declaration
                    && IsWrittenInSource(compilation, declaration))
                {
                    WriteNode(writer, declaration, compilation.Text, budget, condition);
                }
            }

            writer.WriteEndArray();

            writer.WriteBoolean("truncated", budget.Exhausted);
            WriteUniforms(writer, program);

            // シンボルの扱いは既定の構成のブロックについて書く。バリアントはその結果の 1 つである。
            if (program.EnabledSymbols.IsDefaultOrEmpty)
            {
                WriteSymbolStates(writer, compilation, program);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>宣言がこのファイルに書かれたものかどうかを判定する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="declaration">判定する宣言。</param>
    /// <returns>このファイルに書かれたものであれば <see langword="true"/>。</returns>
    private static bool IsWrittenInSource(ShaderCompilation compilation, HlslDeclarationSyntax declaration)
        => declaration.GetLocation() is { } location
           && string.Equals(location.FilePath, compilation.Text.FilePath, StringComparison.Ordinal);

    /// <summary>コードブロックを説明する名前を返す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象のブロック。</param>
    /// <returns>画面に出す名前。</returns>
    private static string DescribeProgram(ShaderCompilation compilation, AnalyzedProgram program)
    {
        string keyword = program.Block?.Delimiter?.StartKeyword ?? "PROGRAM";

        string pass = program.KernelName is { Length: > 0 } kernelName
            ? $"kernel {kernelName}"
            : program.PassName is { Length: > 0 } passName
            ? $"{keyword} (Pass \"{passName}\")"
            : keyword;

        // バリアントは「1 本の木にまとめられなかったシンボル」の分だけ現れる。
        // まとめられた分は既定の木の中に条件付きで載っているので、ここには出てこない。
        // 区別が付かないと、同じ Pass の木が並んでいる理由が読み手に分からない。
        if (!program.EnabledSymbols.IsDefaultOrEmpty)
        {
            // 2 つ以上になるのは、条件に書かれた論理積を 1 つの構成として作った場合である。
            return $"{pass} — {string.Join(" かつ ", program.EnabledSymbols)} を有効にして展開し直したもの";
        }

        int index = compilation.Programs.IndexOf(program) + 1;

        return $"{index}. {pass}";
    }

    /// <summary>ブロックから見える uniform を書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="program">対象のブロック。</param>
    /// <remarks>
    /// include したヘッダの分も含めるとヘッダ由来のものが大半を占めるため、
    /// このファイルに書かれた宣言だけを出す。
    /// </remarks>
    private static void WriteUniforms(Utf8JsonWriter writer, AnalyzedProgram program)
    {
        writer.WriteStartArray("uniforms");

        foreach (UniformSymbol uniform in program.Uniforms)
        {
            if (uniform.GetLocation() is not { } location
                || !string.Equals(location.FilePath, program.Text.FilePath, StringComparison.Ordinal))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("name", uniform.Name);
            writer.WriteString("type", uniform.TypeName);
            writer.WriteString("buffer", uniform.ContainingBufferName ?? string.Empty);
            writer.WriteBoolean("isArray", uniform.IsArray);
            writer.WriteNumber("start", location.Span.Start);
            writer.WriteNumber("length", location.Span.Length);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary><c>Properties</c> の内容を書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    private static void WriteProperties(Utf8JsonWriter writer, ShaderCompilation compilation)
    {
        writer.WriteStartArray("properties");

        foreach (PropertySymbol property in compilation.Properties)
        {
            Location location = property.NameLocation;

            writer.WriteStartObject();
            writer.WriteString("name", property.Name);
            writer.WriteString("kind", property.Kind.ToString());
            writer.WriteString("line", location.LineSpan.Start.ToString());
            writer.WriteNumber("start", location.Span.Start);
            writer.WriteNumber("length", location.Span.Length);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// マクロ展開前のトークン列を書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <remarks>
    /// <b>展開前のトークンであることに意味がある。</b>
    /// マクロは構文解析の前に展開されて構文木から消えるため、
    /// 「このマクロは使わない」という形のルールはこの列を見るしかない
    /// (docs/custom-rules/cookbook.md の <c>BannedMacroAnalyzer</c>)。
    /// マクロ名が残っているかどうかがそのまま結果を左右する。
    /// </remarks>
    private static void WriteTokens(Utf8JsonWriter writer, ShaderCompilation compilation)
    {
        writer.WriteStartArray("tokens");

        foreach (HlslSyntaxToken token in compilation.CodeTokens)
        {
            if (token.Kind == HlslSyntaxKind.EndOfFileToken)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("kind", token.Kind.ToString());
            writer.WriteString("text", token.Text);
            writer.WriteNumber("start", token.Span.Start);
            writer.WriteNumber("length", token.Span.Length);
            writer.WriteString("line", compilation.Text.GetLinePositionSpan(token.Span).Start.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// 式ごとに、型がどう判定されたかを書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <remarks>
    /// <para>
    /// <b>ビューアーの中心はここである。</b>
    /// 型を根拠にするルールが何も報告しないとき、
    /// 原因のほとんどは「その式の型を判定できなかった」ことである。
    /// 構文木とトークンだけを見せても、そこには辿り着けない。
    /// </para>
    /// <para>
    /// 判定できなかった式も省かずに出す。
    /// 一覧に無いことと「型が分からなかった」ことは、読み手には区別が付かない。
    /// </para>
    /// </remarks>
    private static void WriteExpressions(Utf8JsonWriter writer, ShaderCompilation compilation)
    {
        writer.WriteStartArray("expressions");

        HashSet<TextSpan> seen = [];
        int written = 0;

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ExpressionTypeBinder binder = new(compilation, program);

            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (written >= MaxNodes)
                {
                    break;
                }

                if (node is not HlslExpressionSyntax expression
                    || expression.GetLocation() is not { } location
                    || !string.Equals(location.FilePath, compilation.Text.FilePath, StringComparison.Ordinal)
                    || !seen.Add(location.Span))
                {
                    continue;
                }

                written++;

                writer.WriteStartObject();
                writer.WriteString("kind", DescribeKind(expression));
                writer.WriteString("text", DescribeText(expression, compilation.Text));
                writer.WriteBoolean("expanded", IsFromMacroExpansion(expression));
                writer.WriteString("type", binder.GetEvaluatorFor(expression).Evaluate(expression) ?? string.Empty);
                writer.WriteString("line", location.LineSpan.Start.ToString());
                writer.WriteNumber("start", location.Span.Start);
                writer.WriteNumber("length", location.Span.Length);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// 展開の時点で定義されていたマクロを書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <remarks>
    /// 名前がマクロとして定義されているかどうかで、ルールの判断が変わる。
    /// たとえば HL0310 は、関数形式マクロの名前を宣言されていないとは言わない。
    /// 指摘が出た・出なかった理由を確かめるには、この一覧が要る。
    /// </remarks>
    private static void WriteMacros(Utf8JsonWriter writer, ShaderCompilation compilation)
    {
        writer.WriteStartArray("macros");

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach ((string name, MacroDefinition macro) in program.Tree.PreprocessResult.Macros)
            {
                if (!seen.Add(name))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteBoolean("isFunctionLike", macro.IsFunctionLike);
                writer.WriteString("parameters", string.Join(", ", macro.Parameters));
                writer.WriteString("body", string.Join(" ", macro.Body.Select(t => t.Text)));
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>診断を書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="diagnostics">報告された診断。</param>
    private static void WriteDiagnostics(Utf8JsonWriter writer, ImmutableArray<Diagnostic> diagnostics)
    {
        writer.WriteStartArray("diagnostics");

        foreach (Diagnostic diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("id", diagnostic.Id);
            writer.WriteString("severity", diagnostic.Severity.ToString().ToLowerInvariant());
            writer.WriteString("message", diagnostic.GetMessage());
            writer.WriteString("line", diagnostic.Location.LineSpan.Start.ToString());
            writer.WriteNumber("start", diagnostic.Location.Span.Start);
            writer.WriteNumber("length", diagnostic.Location.Span.Length);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
