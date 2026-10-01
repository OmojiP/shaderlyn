using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// 1 行の <c>#pragma</c> が宣言したシンボルの並び。
/// </summary>
/// <param name="Symbols">並んでいるシンボル。<c>_</c> は含まない。</param>
/// <param name="RequiresOne">
/// どれか 1 つが必ず有効かどうか。<c>_</c> の無い <c>multi_compile</c> の行が該当する。
/// </param>
/// <remarks>
/// <b>同じ行のシンボルは、同時には有効にならない。</b>
/// <c>#pragma multi_compile _ _X _Y</c> の <c>_X</c> と <c>_Y</c> を両方有効にした構成は無い。
/// <paramref name="RequiresOne"/> が立っていれば、さらに「どれも無効」も無い。
/// </remarks>
public readonly record struct SymbolGroup(ImmutableArray<string> Symbols, bool RequiresOne);

/// <summary>
/// <c>#pragma</c> によるシェーダーのシンボルの宣言を読み取る。
/// </summary>
/// <remarks>
/// <para>
/// <b>「どれが宣言か」の判断は 1 か所に置く。</b>
/// この判断は 2 か所で必要になる。
/// 宣言と使用が噛み合っているかを検査するルール (HL0330 / HL0331) と、
/// シンボルを 1 つずつ有効にして展開する解析である。
/// </para>
/// <para>
/// 書き写すと、Unity が宣言の書き方を増やしたときに片方だけが追従する。
/// そうなると「ルールは宣言されていないと言うのに、解析は展開する」
/// という食い違いが起き、どちらが正しいのか読み手には分からない。
/// </para>
/// </remarks>
public static class ShaderSymbols
{
    /// <summary>
    /// シンボルを宣言する <c>#pragma</c> の名前の先頭。
    /// </summary>
    /// <remarks>
    /// <c>_local</c> はマテリアル単位、<c>_fragment</c> などは段階を限るバリアントで、
    /// いずれもシンボルを宣言する点は同じである。
    /// </remarks>
    private static readonly string[] DeclaringPragmaPrefixes =
        ["shader_feature", "multi_compile", "dynamic_branch"];

    /// <summary>
    /// 「シンボル無しの構成」を表す名前かどうかを判定する。
    /// </summary>
    /// <param name="name">判定する名前。</param>
    /// <returns>シンボルではなく、プレースホルダーであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>下線だけの名前はすべてプレースホルダーである。</b>
    /// Unity 同梱のシェーダーは <c>_</c> を 1,323 回、<c>__</c> を 28 回使っている。
    /// <c>_</c> だけを除いていたため、<c>__</c> が
    /// 「宣言されているのに条件のどこにも現れないシンボル」として報告されていた。
    /// </para>
    /// <para>
    /// 下線だけの名前をシンボルとして使うことはない。
    /// 数を決め打ちせず、下線しか含まないことを条件にする。
    /// </para>
    /// </remarks>
    public static bool IsNoSymbol(string name)
        => !string.IsNullOrEmpty(name) && name.All(c => c == '_');

    /// <summary>
    /// その <c>#pragma</c> がシンボルを宣言するものかを判定する。
    /// </summary>
    /// <param name="pragma">判定する <c>#pragma</c>。</param>
    /// <returns>シンボルを宣言するものであれば <see langword="true"/>。</returns>
    public static bool IsDeclaringPragma(PragmaDirective pragma)
        => IsDeclaringPragmaName(pragma.Name);

    /// <summary>
    /// その名前の <c>#pragma</c> がシンボルを宣言するものかを判定する。
    /// </summary>
    /// <param name="name"><c>#pragma</c> の直後に書かれた名前。</param>
    /// <returns>シンボルを宣言するものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 展開前のトークンしか持たない呼び出し元 (エディタのホバーなど) のためにある。
    /// </remarks>
    public static bool IsDeclaringPragmaName(string name)
        => DeclaringPragmaPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// 宣言されているシンボルのトークンを列挙する。
    /// </summary>
    /// <param name="pragmas">現れた <c>#pragma</c>。</param>
    /// <returns>シンボルの名前を表すトークン。</returns>
    /// <remarks>
    /// 位置が要る呼び出し元のためにトークンのまま返す。
    /// 名前だけでよければ <see cref="CollectDeclared"/> を使う。
    /// </remarks>
    public static IEnumerable<HlslSyntaxToken> EnumerateDeclared(ImmutableArray<PragmaDirective> pragmas)
    {
        foreach (PragmaDirective pragma in pragmas)
        {
            // 組み込みの宣言の引数はシンボルではなく、除くものの指定である (nolightmap など)。
            if (!IsDeclaringPragma(pragma) || BuiltInSets.ContainsKey(pragma.Name))
            {
                continue;
            }

            foreach (HlslSyntaxToken argument in pragma.Arguments)
            {
                if (argument.Kind == HlslSyntaxKind.IdentifierToken && !IsNoSymbol(argument.Text))
                {
                    yield return argument;
                }
            }
        }
    }

    /// <summary>
    /// 宣言されているシンボルの名前を集める。
    /// </summary>
    /// <param name="pragmas">現れた <c>#pragma</c>。</param>
    /// <returns>宣言されたシンボルの名前。</returns>
    /// <remarks>
    /// <para>取り込んだヘッダの宣言も含める。そこで宣言されていれば使ってよい。</para>
    /// <para>
    /// <c>#pragma multi_compile_fog</c> のような組み込みの宣言が作るシンボル (<c>FOG_LINEAR</c> など) も含める。
    /// 書かれた場所が無いので、位置を返す <see cref="EnumerateDeclared"/> には現れない。
    /// </para>
    /// </remarks>
    public static HashSet<string> CollectDeclared(ImmutableArray<PragmaDirective> pragmas)
        => [.. pragmas.SelectMany(LinesOf).SelectMany(l => l.Symbols)];

    /// <summary>
    /// 組み込みの宣言が作るシンボルを、その宣言の名前のトークンと組にして列挙する。
    /// </summary>
    /// <param name="pragmas">現れた <c>#pragma</c>。</param>
    /// <returns>宣言の名前 (<c>multi_compile_fog</c>) のトークンと、それが作るシンボルの名前。</returns>
    /// <remarks>
    /// シンボルの名前は書かれていないので、指摘はその宣言の行に出すしかない。
    /// </remarks>
    public static IEnumerable<(HlslSyntaxToken Declaration, string Symbol)> EnumerateBuiltInDeclared(
        ImmutableArray<PragmaDirective> pragmas)
        => pragmas
            .Where(p => BuiltInSets.ContainsKey(p.Name))
            .SelectMany(p => LinesOf(p).SelectMany(l => l.Symbols).Select(s => (p.NameToken, s)));

    /// <summary>
    /// コードの中で名前として現れた識別子を集める。
    /// </summary>
    /// <param name="tokens">対象のトークン列。</param>
    /// <returns>現れた識別子の名前。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>#ifdef</c> だけを見ていては、使われているかを判定できない。</b>
    /// <c>#pragma dynamic_branch</c> で宣言したシンボルはバリアントを作らず、
    /// <c>if (_HDR_OVERLAY)</c> のように実行時の分岐として書かれる。
    /// 条件だけを数えると「宣言したのに使っていない」と誤って言うことになる。
    /// </para>
    /// <para>
    /// プリプロセッサの行は数えない。
    /// <c>#pragma</c> の行にはシンボルの名前そのものが並んでおり、
    /// それを使用と数えると、どのシンボルも使われていることになる。
    /// </para>
    /// </remarks>
    public static HashSet<string> CollectRuntimeReferences(ImmutableArray<HlslSyntaxToken> tokens)
    {
        HashSet<string> referenced = new(StringComparer.Ordinal);

        for (int i = 0; i < tokens.Length; i++)
        {
            // プリプロセッサの行は飛ばす。
            // #pragma の行にはシンボルの名前そのものが並んでおり、
            // それを使用と数えると、どのシンボルも「使われている」ことになる。
            if (tokens[i].Kind == HlslSyntaxKind.HashToken && tokens[i].IsAtLineStart)
            {
                while (i + 1 < tokens.Length && !tokens[i + 1].IsAtLineStart) { i++; }
                continue;
            }

            if (tokens[i].Kind == HlslSyntaxKind.IdentifierToken)
            {
                referenced.Add(tokens[i].Text);
            }
        }

        return referenced;
    }

    /// <summary>
    /// 展開する前のトークン列から、宣言されているシンボルを集める。
    /// </summary>
    /// <param name="tokens">字句解析しただけのトークン列。</param>
    /// <returns>宣言されたシンボルの名前。</returns>
    /// <remarks>
    /// <para>
    /// <b>展開の前に知る必要がある場面がある。</b>
    /// <c>#ifdef</c> の両方の分岐を残すかどうかは展開しながら決めるため、
    /// 「どれがシンボルか」は展開が始まる前に分かっていなければならない。
    /// </para>
    /// <para>
    /// このファイル自身に書かれた <c>#pragma</c> だけを見る。
    /// 取り込んだヘッダの宣言は、この時点では読めていない。
    /// </para>
    /// </remarks>
    public static HashSet<string> CollectDeclaredFromTokens(ImmutableArray<HlslSyntaxToken> tokens)
        => [.. EnumerateDeclaringLines(tokens).SelectMany(line => line.Symbols)];

    /// <summary>
    /// 宣言から分かる構成の制約を集める。
    /// </summary>
    /// <param name="pragmas">現れた <c>#pragma</c>。</param>
    /// <returns>集めた制約。</returns>
    /// <remarks>
    /// <para>
    /// <b>同じ行に並べたシンボルは、どれか 1 つしか有効にならない。</b>
    /// <c>#pragma multi_compile _ _X _Y</c> の <c>_X</c> と <c>_Y</c> を両方有効にした構成は無い。
    /// </para>
    /// <para>
    /// <b><c>_</c> の無い <c>multi_compile</c> の行は、どれか 1 つが必ず有効になる</b> (<see cref="IsRequiredSet"/>)。
    /// </para>
    /// </remarks>
    public static SymbolConstraints CollectConstraints(ImmutableArray<PragmaDirective> pragmas)
        => ToConstraints(pragmas.SelectMany(LinesOf));

    /// <summary>1 つの <c>#pragma</c> が宣言する行を返す。</summary>
    /// <param name="pragma">対象の <c>#pragma</c>。</param>
    /// <returns>宣言の行。組み込みの宣言は複数の行になる。宣言でなければ空。</returns>
    private static IEnumerable<DeclaringLine> LinesOf(PragmaDirective pragma)
        => IsDeclaringPragma(pragma)
            ? LinesOf(pragma.Name, pragma.Arguments.Where(a => a.Kind == HlslSyntaxKind.IdentifierToken).Select(a => a.Text))
            : [];

    /// <summary>宣言の名前と引数から、宣言の行を作る。</summary>
    /// <param name="pragmaName"><c>#pragma</c> の直後に書かれた名前。</param>
    /// <param name="arguments">引数のうち、識別子のもの。</param>
    /// <returns>宣言の行。</returns>
    private static IEnumerable<DeclaringLine> LinesOf(string pragmaName, IEnumerable<string> arguments)
    {
        if (!BuiltInSets.TryGetValue(pragmaName, out ImmutableArray<DeclaringLine> builtIn))
        {
            List<string> symbols = [];
            bool hasPlaceholder = false;

            foreach (string argument in arguments)
            {
                if (IsNoSymbol(argument))
                {
                    hasPlaceholder = true;
                }
                else
                {
                    symbols.Add(argument);
                }
            }

            return [new DeclaringLine(pragmaName, symbols, hasPlaceholder)];
        }

        // nolightmap のような指定は、そのシンボルを作らない。
        HashSet<string> removed = [.. arguments.SelectMany(a => BuiltInOptions.TryGetValue(a, out string[]? names) ? names : [])];

        return removed.Count == 0
            ? builtIn
            : builtIn
                .Select(line => line with { Symbols = [.. line.Symbols.Where(s => !removed.Contains(s))] })
                .Where(line => line.Symbols.Count > 0);
    }

    /// <summary>
    /// 組み込みの宣言 (<c>#pragma multi_compile_fog</c> など) が作るシンボル。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>書かれていないシンボルも宣言されている。</b>
    /// <c>#pragma multi_compile_fog</c> は <c>FOG_LINEAR</c> / <c>FOG_EXP</c> / <c>FOG_EXP2</c> の構成を作るが、
    /// 名前はどこにも書かれていない。表が無いと、ヘッダの <c>#if defined(FOG_LINEAR)</c> の中は
    /// どの構成でも読まれない。
    /// </para>
    /// <para>
    /// 中身は Unity のマニュアル (「Shader keywords」の組み込みの宣言の一覧) による。
    /// <b>同時に有効にならないことがはっきりしているものだけを 1 つの行にする。</b>
    /// 霧のモード、ライトの種類、影を落とすときの種類がそうである。
    /// 残りは 1 つずつの行にする。実在しない組み合わせを読むことはあっても、実在する構成を読み落とすことは無い。
    /// </para>
    /// <para>
    /// <c>_</c> の無い行は、どれか 1 つが必ず有効になる。<c>multi_compile_fwdbase</c> の <c>DIRECTIONAL</c> は
    /// 常に定義されている。<c>multi_compile_shadowcaster</c> は <c>SHADOWS_DEPTH</c> か <c>SHADOWS_CUBE</c> のどちらかになる。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, ImmutableArray<DeclaringLine>> BuiltInSets = CreateBuiltInSets();

    /// <summary>組み込みの宣言に付けて、シンボルを作らせない指定。</summary>
    private static readonly Dictionary<string, string[]> BuiltInOptions = new(StringComparer.Ordinal)
    {
        ["nolightmap"] = ["LIGHTMAP_ON"],
        ["nodirlightmap"] = ["DIRLIGHTMAP_COMBINED"],
        ["nodynlightmap"] = ["DYNAMICLIGHTMAP_ON"],
        ["novertexlight"] = ["VERTEXLIGHT_ON"],
        ["noshadowmask"] = ["SHADOWS_SHADOWMASK"],
        ["noshadow"] = ["SHADOWS_SCREEN", "SHADOWS_DEPTH", "SHADOWS_CUBE", "SHADOWS_SOFT"],
    };

    /// <summary><see cref="BuiltInSets"/> を作る。</summary>
    /// <returns>作った表。</returns>
    private static Dictionary<string, ImmutableArray<DeclaringLine>> CreateBuiltInSets()
    {
        static DeclaringLine OneOf(params string[] symbols) => new("multi_compile", symbols, HasPlaceholder: false);
        static DeclaringLine NoneOrOneOf(params string[] symbols) => new("multi_compile", symbols, HasPlaceholder: true);
        static IEnumerable<DeclaringLine> Each(params string[] symbols) => symbols.Select(s => NoneOrOneOf(s));

        DeclaringLine[] lights = [OneOf("DIRECTIONAL", "DIRECTIONAL_COOKIE", "POINT", "POINT_COOKIE", "SPOT")];
        DeclaringLine[] fullShadows =
        [
            .. lights,
            NoneOrOneOf("SHADOWS_DEPTH", "SHADOWS_SCREEN", "SHADOWS_CUBE"),
            .. Each("SHADOWS_SOFT", "SHADOWS_SHADOWMASK", "LIGHTMAP_SHADOW_MIXING"),
        ];

        return new Dictionary<string, ImmutableArray<DeclaringLine>>(StringComparer.Ordinal)
        {
            ["multi_compile_fog"] = [NoneOrOneOf("FOG_LINEAR", "FOG_EXP", "FOG_EXP2")],
            ["multi_compile_instancing"] = [NoneOrOneOf("INSTANCING_ON")],
            ["multi_compile_particles"] = [NoneOrOneOf("SOFTPARTICLES_ON")],
            ["multi_compile_shadowcaster"] = [OneOf("SHADOWS_DEPTH", "SHADOWS_CUBE")],
            ["multi_compile_shadowcollector"] = [.. Each("SHADOWS_SPLIT_SPHERES", "SHADOWS_SINGLE_CASCADE")],
            ["multi_compile_fwdbase"] =
            [
                OneOf("DIRECTIONAL"),
                .. Each(
                    "LIGHTMAP_ON", "DIRLIGHTMAP_COMBINED", "DYNAMICLIGHTMAP_ON", "SHADOWS_SCREEN",
                    "SHADOWS_SHADOWMASK", "LIGHTMAP_SHADOW_MIXING", "LIGHTPROBE_SH", "VERTEXLIGHT_ON"),
            ],
            ["multi_compile_fwdadd"] = [.. lights],
            ["multi_compile_fwdadd_fullshadows"] = [.. fullShadows],
            ["multi_compile_lightpass"] = [.. fullShadows],
            ["multi_compile_prepassfinal"] =
            [
                .. Each(
                    "DIRECTIONAL", "LIGHTMAP_ON", "DIRLIGHTMAP_COMBINED", "DYNAMICLIGHTMAP_ON",
                    "SHADOWS_SHADOWMASK", "LIGHTPROBE_SH", "UNITY_HDR_ON"),
            ],
        };
    }

    /// <summary>
    /// 宣言の行を、そのままの並びで集める。
    /// </summary>
    /// <param name="pragmas">展開後の <c>#pragma</c>。</param>
    /// <returns>行ごとのシンボルと、その行がどれか 1 つを必ず有効にするか。</returns>
    /// <remarks>
    /// <b>利用者に構成を選ばせる画面のためにある。</b>
    /// <see cref="CollectConstraints"/> が返す制約は「同時に有効にならない組」を答えられるが、
    /// 「この行のどれか 1 つ」という単位は取り出せない。
    /// 画面はその単位で、選べるもの・外せないものを決める。
    /// </remarks>
    public static ImmutableArray<SymbolGroup> CollectGroups(ImmutableArray<PragmaDirective> pragmas)
        => [.. pragmas
            .SelectMany(LinesOf)
            .Where(line => line.Symbols.Count > 0)
            .Select(line => new SymbolGroup(
                [.. line.Symbols],
                IsRequiredSet(line.PragmaName, line.HasPlaceholder)))];

    /// <summary>
    /// 展開する前のトークン列から、宣言から分かる構成の制約を集める。
    /// </summary>
    /// <param name="tokens">字句解析しただけのトークン列。</param>
    /// <returns>集めた制約。</returns>
    /// <remarks>
    /// 展開の前に要る。決して成り立たない分岐は並べず、
    /// どれか 1 つが必ず有効になる行は、既定の構成でも先頭のシンボルを定義する。
    /// </remarks>
    public static SymbolConstraints CollectConstraintsFromTokens(ImmutableArray<HlslSyntaxToken> tokens)
        => ToConstraints(EnumerateDeclaringLines(tokens));

    /// <summary>
    /// その宣言の行が、どれか 1 つを必ず有効にするものかを判定する。
    /// </summary>
    /// <param name="pragmaName"><c>#pragma</c> の直後に書かれた名前。</param>
    /// <param name="hasPlaceholder">行に <c>_</c> (シンボル無しの構成) があるかどうか。</param>
    /// <returns>必ず有効にするものなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>multi_compile</c> は、書いた数だけの構成を作る。</b>
    /// 「どれも無い」構成は <c>_</c> を書いたときだけ作られる。
    /// <c>#pragma multi_compile MODE_A MODE_B</c> は <c>MODE_A</c> と <c>MODE_B</c> の 2 通りしか無い。
    /// </para>
    /// <para>
    /// <b><c>shader_feature</c> は対象にしない。</b>
    /// 「どのキーワードも定義しない構成も必ずコンパイルする」と説明されている
    /// (Unity 2019.4 の「Shader variants and keywords」)。
    /// 新しいバージョンの説明ははっきりしないので、「無し」も実在するものとして扱う側へ倒す。
    /// <c>dynamic_branch</c> は構成を作らないので対象外である。
    /// </para>
    /// </remarks>
    public static bool IsRequiredSet(string pragmaName, bool hasPlaceholder)
        => !hasPlaceholder && pragmaName.StartsWith("multi_compile", StringComparison.Ordinal);

    /// <summary>宣言の行から制約を作る。</summary>
    /// <param name="lines">宣言の行。</param>
    /// <returns>作った制約。</returns>
    private static SymbolConstraints ToConstraints(IEnumerable<DeclaringLine> lines)
    {
        List<DeclaringLine> all = [.. lines];

        return SymbolConstraints.FromSets(
            all.Select(line => line.Symbols),
            all.Where(line => IsRequiredSet(line.PragmaName, line.HasPlaceholder)).Select(line => line.Symbols));
    }

    /// <summary>シンボルを宣言する <c>#pragma</c> の行 1 つ。</summary>
    /// <param name="PragmaName"><c>#pragma</c> の直後に書かれた名前。</param>
    /// <param name="Symbols">並んでいるシンボル。<c>_</c> は除く。</param>
    /// <param name="HasPlaceholder"><c>_</c> (シンボル無しの構成) があるかどうか。</param>
    private readonly record struct DeclaringLine(string PragmaName, IReadOnlyList<string> Symbols, bool HasPlaceholder);

    /// <summary>
    /// 展開する前のトークン列から、シンボルを宣言する <c>#pragma</c> の行を読む。
    /// </summary>
    /// <param name="tokens">字句解析しただけのトークン列。</param>
    /// <returns>宣言の行。</returns>
    private static IEnumerable<DeclaringLine> EnumerateDeclaringLines(ImmutableArray<HlslSyntaxToken> tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind != HlslSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || i + 2 >= tokens.Length
                || tokens[i + 1].Kind != HlslSyntaxKind.IdentifierToken
                || tokens[i + 1].Text != "pragma"
                || tokens[i + 2].Kind != HlslSyntaxKind.IdentifierToken
                || !IsDeclaringPragmaName(tokens[i + 2].Text))
            {
                continue;
            }

            // 行の終わりまでが引数である。
            List<string> arguments = [];

            for (int j = i + 3; j < tokens.Length && !tokens[j].IsAtLineStart; j++)
            {
                if (tokens[j].Kind == HlslSyntaxKind.IdentifierToken)
                {
                    arguments.Add(tokens[j].Text);
                }
            }

            foreach (DeclaringLine line in LinesOf(tokens[i + 2].Text, arguments))
            {
                yield return line;
            }
        }
    }
}
