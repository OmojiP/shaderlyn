using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Profiles;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Semantics;

/// <summary>
/// 1 つの <c>.shader</c> について、ShaderLab と埋め込み HLSL を突き合わせた解析結果。
/// </summary>
/// <remarks>
/// <para>
/// <b>この型が M3 の中心である。</b>
/// ShaderLab の <c>Properties</c> と HLSL の uniform は別の言語で別々に書かれており、
/// 対応が取れているかは人間がレビューで見るしかなかった。
/// 両者を 1 つのモデルに載せることで、その対応をルールとして書けるようにする。
/// </para>
/// <para>
/// <b>構築は失敗しない。</b>
/// 構文エラーがあっても、include が解決できなくても、
/// 分かった範囲のモデルを作って返す。何が分からなかったかは
/// <see cref="HasCompleteDependencies"/> と <see cref="UnresolvedIncludes"/> で判別できる。
/// </para>
/// </remarks>
public sealed class ShaderCompilation
{
    private readonly Dictionary<string, List<UniformSymbol>> _uniformsByName;
    private readonly Dictionary<string, int> _identifierOccurrences;
    private readonly Dictionary<string, int> _declarationCounts;
    private readonly HashSet<string> _inactiveIdentifiers;
    private readonly HashSet<string> _shaderLabPropertyReferences;
    private readonly FrozenSet<string> _functionLikeMacroNames;
    private Conditional.ConditionMap? _condition;
    private FrozenSet<string>? _declaredNames;

    /// <summary>関数の中の変数と仮引数としてしか宣言されていない名前。</summary>
    private FrozenSet<string>? _localOnlyNames;

    /// <summary>取り込んだヘッダの宣言に現れた名前。</summary>
    private FrozenSet<string>? _headerDeclaredNames;

    /// <summary>コードブロックごとの、取り込んだヘッダの宣言に現れた名前。</summary>
    private FrozenDictionary<(int Start, string? Kernel), FrozenSet<string>>? _headerDeclaredNamesByBlock;
    private FrozenDictionary<string, ImmutableArray<FunctionDeclarationSyntax>>? _functionDeclarations;

    /// <summary>関数の宣言の位置から、それが現れたコードブロックへの対応。</summary>
    private Dictionary<(SourceText Source, int Start), HashSet<(int Start, string? Kernel)>>? _functionBlocks;
    private Dictionary<AnalyzedProgram, ExpressionTypeBinder>? _binders;

    /// <summary>木ごとの、位置から関数の宣言への対応。</summary>
    private Dictionary<AnalyzedProgram, Dictionary<(SourceText Source, int Start), FunctionDeclarationSyntax>>? _ownFunctions;

    internal ShaderCompilation(
        SourceText text,
        ShaderLabSyntaxTree shaderLabTree,
        IRenderPipelineProfile profile,
        ImmutableArray<PropertySymbol> properties,
        ImmutableArray<AnalyzedProgram> programs,
        Dictionary<string, List<UniformSymbol>> uniformsByName,
        Dictionary<string, int> identifierOccurrences,
        Dictionary<string, int> declarationCounts,
        HashSet<string> inactiveIdentifiers,
        HashSet<string> unanalyzedIdentifiers,
        HashSet<string> shaderLabPropertyReferences,
        ImmutableArray<string> allIncludePaths,
        ImmutableArray<string> unresolvedIncludes,
        SourceText codeText,
        ImmutableArray<HlslSyntaxToken> codeTokens,
        ImmutableArray<AnalyzedProgram> symbolVariants,
        ImmutableArray<string> unexploredSymbols,
        ImmutableArray<SymbolCombination> unexploredSymbolCombinations)
    {
        SymbolVariants = symbolVariants;
        UnexploredSymbols = unexploredSymbols;
        UnexploredSymbolCombinations = unexploredSymbolCombinations;
        Text = text;
        ShaderLabTree = shaderLabTree;
        Profile = profile;
        Properties = properties;
        Programs = programs;
        _uniformsByName = uniformsByName;
        _identifierOccurrences = identifierOccurrences;
        _declarationCounts = declarationCounts;
        _inactiveIdentifiers = inactiveIdentifiers;
        UnanalyzedIdentifiers = unanalyzedIdentifiers.ToFrozenSet(StringComparer.Ordinal);
        _shaderLabPropertyReferences = shaderLabPropertyReferences;
        AllIncludePaths = allIncludePaths;
        UnresolvedIncludes = unresolvedIncludes;
        CodeText = codeText;
        CodeTokens = codeTokens;
        _functionLikeMacroNames = BuildFunctionLikeMacroNames([.. programs, .. symbolVariants]);
    }

    /// <summary>
    /// シェーダーが自分で書いた HLSL のコードだけを残したテキスト。
    /// </summary>
    /// <remarks>
    /// 元の <c>.shader</c> と同じ長さ・同じ行構成を持つ。
    /// マクロ展開も include 解決もされていない、書かれたとおりのコードである。
    /// </remarks>
    public SourceText CodeText { get; }

    /// <summary>
    /// シェーダーが自分で書いた HLSL のコードを字句解析したトークン列。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>マクロ展開前のトークンである。</b>
    /// 「この識別子は使わない」という形のルールはこちらを見なければならない。
    /// <c>UNITY_MATRIX_MVP</c> のようなマクロは、展開後には名前が残らないためである。
    /// </para>
    /// <para>
    /// include されたファイルの中身は含まない。
    /// 使ってよい API の判断はプロジェクトが書いたコードに対して行うものであり、
    /// Unity のヘッダの中身を指摘しても直しようがない。
    /// </para>
    /// </remarks>
    public ImmutableArray<HlslSyntaxToken> CodeTokens { get; }

    /// <summary>
    /// 利用者が書いて取り込んだヘッダ (<see cref="IsUserFile"/>) のコードを字句解析したトークン列。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CodeTokens"/> と同じくマクロ展開前のトークンで、ヘッダのファイルを指す。
    /// 「この識別子は使わない」という形のルールは、共通の <c>.hlsl</c> に書かれたものも見なければならない。
    /// <see cref="CodeTokens"/> だけを見ると、共通のヘッダに書いた非推奨の API は報告されない。
    /// </para>
    /// <para>
    /// 初めて引いたときに、取り込んだファイルを読み直して作る。引かないルールには費用が掛からない。
    /// </para>
    /// </remarks>
    public ImmutableArray<HlslSyntaxToken> UserIncludeCodeTokens => _userIncludeCodeTokens ??= LexUserIncludes();

    private ImmutableArray<HlslSyntaxToken>? _userIncludeCodeTokens;

    /// <summary>利用者が書いて取り込んだヘッダを読み直し、字句解析する。</summary>
    /// <returns>ヘッダごとのトークンを、パスの順に並べたもの。</returns>
    private ImmutableArray<HlslSyntaxToken> LexUserIncludes()
    {
        if (IsUserInclude is null)
        {
            return [];
        }

        ImmutableArray<HlslSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        foreach (string path in AllIncludePaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (string.Equals(path, Text.FilePath, StringComparison.Ordinal) || !IsUserFile(path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    tokens.AddRange(new HlslLexer(SourceText.From(File.ReadAllText(path), path)).Lex(out _));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めなかったヘッダは、取り込みの解決でも読めていない。検査しようがない。
            }
        }

        return tokens.ToImmutable();
    }

    /// <summary>解析対象の <c>.shader</c> のソーステキスト。</summary>
    public SourceText Text { get; }

    /// <summary>ShaderLab としての構文木。</summary>
    /// <remarks>
    /// HLSL 単体ファイルでは、全部を空白でマスクしたテキストから作った空の木である。
    /// <see cref="IsStandaloneHlsl"/> を参照。
    /// </remarks>
    public ShaderLabSyntaxTree ShaderLabTree { get; }

    /// <summary>
    /// HLSL 単体ファイル (<c>.compute</c> など) を解析したものかどうか。
    /// </summary>
    /// <remarks>
    /// <b>ShaderLab の構造を持たないので、それを根拠にする検査は対象が無い。</b>
    /// 「対象が無いので報告しない」は「検査できなかった」とは違うので、
    /// 見送ったことを報告する必要も無い。
    /// </remarks>
    public bool IsStandaloneHlsl { get; internal init; }

    /// <summary>適用するレンダーパイプラインプロファイル。</summary>
    public IRenderPipelineProfile Profile { get; }

    /// <summary><c>Properties</c> に宣言されたプロパティ。</summary>
    public ImmutableArray<PropertySymbol> Properties { get; }

    /// <summary>埋め込みコードブロックごとの解析結果。</summary>
    /// <remarks>
    /// 既定の構成 (どのシンボルも有効でない状態) のものだけを含む。
    /// ブロックごとに 1 件を前提にしているルールが多く、
    /// ここへシンボル構成を混ぜると同じ箇所を何度も報告することになる。
    /// </remarks>
    public ImmutableArray<AnalyzedProgram> Programs { get; }

    /// <summary>
    /// シンボルを 1 つ有効にした構成での解析結果。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>シンボルは C# からも切り替えられる。</b>
    /// 有効になっていない分岐のコードも「いつか通る」コードであり、
    /// そこに書かれた宣言も構文の誤りも、見なければ見つからない。
    /// </para>
    /// <para>
    /// ここに含まれる宣言は <see cref="Programs"/> のものと同じ索引に入っている。
    /// 「宣言が見つからない」を根拠にするルールは、
    /// シンボルで守られた宣言も見えている状態で判断する。
    /// </para>
    /// </remarks>
    public ImmutableArray<AnalyzedProgram> SymbolVariants { get; }

    /// <summary>
    /// 上限に達したために展開しなかったシンボル。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 何も伝えずに一部だけ調べると、調べていない箇所を「問題なし」と受け取られる。
    /// この事実は報告しなければならない。
    /// </para>
    /// <para>
    /// 1 つだけ有効にした構成を作らなかったシンボルである。
    /// 組として同時に有効にする構成を作らなかったものは <see cref="UnexploredSymbolCombinations"/> に入る。
    /// </para>
    /// </remarks>
    public ImmutableArray<string> UnexploredSymbols { get; }

    /// <summary>
    /// 上限に達したために展開しなかった、同時に有効にするシンボルの組。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>#ifdef _A</c> の中の <c>#ifdef _B</c> のように、組でしか通らない分岐のための構成である。
    /// 組の中のシンボルは 1 つずつなら調べていることがあるので、<see cref="UnexploredSymbols"/> とは分けている。
    /// 「<c>_A</c> を調べていない」と伝えると、読んでいる <c>_A</c> の分岐まで疑わせることになる。
    /// </para>
    /// <para>
    /// 組の中はシンボルの名前順に並ぶ。組どうしの並びも決まっている。
    /// 位置は、その組でしか通らない分岐を始めた指令を指す。
    /// </para>
    /// </remarks>
    public ImmutableArray<SymbolCombination> UnexploredSymbolCombinations { get; }

    /// <summary>
    /// どの構成でも解析されなかった非活性領域に現れた名前。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SHADER_API_*</c> のような環境の条件は 1 通りにしか解かず、
    /// 上限を超えたシンボルは展開しない。その裏側に書かれた宣言や参照は、どの木にも載らない。
    /// 「見つからない」を根拠にする検査は、ここにある名前について判断を見送る。
    /// </para>
    /// <para>
    /// シンボルの <c>#ifdef</c> の中は、そのシンボルのバリアントが解析しているので含まない。
    /// 取り込んだヘッダの非活性領域は区別せず、すべて含む。
    /// </para>
    /// </remarks>
    public FrozenSet<string> UnanalyzedIdentifiers { get; }

    /// <summary>
    /// ノードがどのシンボルの組み合わせのもとで存在するかを引ける索引。
    /// </summary>
    /// <returns>出現条件の索引。</returns>
    /// <remarks>
    /// <para>
    /// <b>呼ばれたときに組み立てる。</b>
    /// 既定の構成とバリアントを突き合わせる費用は、条件を見るルールだけが負えばよい。
    /// 一度組み立てたら使い回す。セマンティックモデルは作られたあと変わらない。
    /// </para>
    /// <para>
    /// 条件を根拠に指摘を出すなら、
    /// <see cref="Conditional.ConditionMap.IsComplete"/> も見ること。
    /// 突き合わせられなかった箇所については、条件が分かっていない。
    /// </para>
    /// </remarks>
    public Conditional.ConditionMap GetConditionMap()
        => _condition ??= Conditional.ConditionMapBuilder.Build(Programs, SymbolVariants);

    /// <summary>
    /// <c>#include</c> に書かれていたパスと、解決できたファイルのパス。
    /// </summary>
    /// <remarks>
    /// 解決の成否にかかわらず両方を含む。
    /// どのパイプライン向けのシェーダーかを判定する根拠に使う。
    /// </remarks>
    public ImmutableArray<string> AllIncludePaths { get; }

    /// <summary>解決できなかった include のパス。</summary>
    public ImmutableArray<string> UnresolvedIncludes { get; }

    /// <summary>シェーダー名。<c>Shader</c> 宣言が無い場合は空文字列。</summary>
    public string ShaderName => ShaderLabTree.Root.Shader?.Name ?? string.Empty;

    /// <summary>HLSL として解析できたコードブロックが 1 つでもあるかどうか。</summary>
    public bool HasHlslPrograms => !Programs.IsEmpty;

    /// <summary>埋め込みコードの解析で出た診断。</summary>
    /// <remarks>
    /// include 先のヘッダで出たものも、Pass ごとに重複したものも、すべてそのまま含む。
    /// <b>診断として出力する用途には <see cref="ReportableHlslDiagnostics"/> を使うこと。</b>
    /// </remarks>
    public ImmutableArray<Diagnostic> HlslDiagnostics =>
        [.. Programs.SelectMany(p => p.Diagnostics)];

    /// <summary>
    /// 利用者へ報告してよい、埋め込みコードの解析で出た診断。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>構文エラーやプリプロセッサエラーは、それ自体が報告されなければならない。</b>
    /// 報告されないと、指摘は「検査を見送った」という <c>SL0002</c> のメッセージの中にしか現れず、
    /// 位置はファイルの先頭を指したまま、実際の箇所へは辿れない。
    /// </para>
    /// <para>
    /// そのまま出すのではなく、<see cref="HlslDiagnostics"/> に次の 2 つの選別を加えている。
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>利用者のファイル (<see cref="IsUserFile"/>) にある指摘だけを残す。</b>
    ///     Unity や外部パッケージのヘッダの中の問題は、利用者に直しようがない。
    ///     利用者が書いた共通の <c>.hlsl</c> の構文エラーは、取り込む側の文脈で見つけて報告する
    ///   </description></item>
    ///   <item><description>
    ///     <b>同じ位置の同じ指摘を 1 件にまとめる。</b>
    ///     共通コード片は Pass の数だけ解析し直されるため、
    ///     まとめないと 1 か所の誤りが Pass の数だけ並ぶ。
    ///     シンボル構成でも同じ箇所を何度も見ることになるため、同じ扱いにする
    ///   </description></item>
    /// </list>
    /// <para>
    /// 位置は元のファイルのソーステキストへ付け替えてある。
    /// マスクした複製の上では、ブロックの外の行が空白になっており
    /// 指摘箇所の周辺を表示できず、そこに書かれた抑制コメントも読み取れない。
    /// </para>
    /// </remarks>
    public ImmutableArray<Diagnostic> ReportableHlslDiagnostics
        => _reportableHlslDiagnostics ??= BuildReportableHlslDiagnostics([.. Programs, .. SymbolVariants]);

    private ImmutableArray<Diagnostic>? _reportableHlslDiagnostics;

    /// <summary>
    /// uniform の一覧が信頼できるかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「宣言が見つからない」ことを根拠に診断を出すルールは、必ずこれを確認しなければならない。</b>
    /// include が解決できていない、あるいは HLSL の解析が途中で失敗している場合、
    /// uniform の一覧には抜けがある。その状態で「対応する uniform が無い」と報告すれば、
    /// 正しく書かれたシェーダーを誤りとして指摘することになる。
    /// </para>
    /// <para>
    /// 判断できないときに報告しないのは消極的な妥協ではない。
    /// 誤検出を出すルールは無効化され、そのルールが本当に見つけるはずだった不具合も
    /// あわせて見逃されるようになる。
    /// </para>
    /// <para>
    /// 検査を行わなかったこと自体は <c>SL0002</c> として報告されるので、
    /// 利用者が「検査された」と誤解することはない。
    /// </para>
    /// </remarks>
    public bool HasCompleteDependencies =>
        UnresolvedIncludes.IsEmpty && Programs.All(p => p.Diagnostics.IsEmpty);

    /// <summary>
    /// 名前から uniform を引く。
    /// </summary>
    /// <param name="name">uniform の名前。</param>
    /// <param name="uniform">見つかった uniform。</param>
    /// <returns>見つかった場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 複数のブロックが同じ名前を宣言している場合は、最初に見つかったものを返す。
    /// Pass ごとに型が違う uniform を宣言するのは正当なコードではないため、
    /// 型を見る用途ならどれを返しても結果は変わらない。
    /// <b>宣言の置き場所を見る用途では <see cref="GetUniforms"/> を使うこと。</b>
    /// 定数バッファの内外は Pass ごとに違いうる。
    /// </remarks>
    public bool TryGetUniform(string name, [NotNullWhen(true)] out UniformSymbol? uniform)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_uniformsByName.TryGetValue(name, out List<UniformSymbol>? found) && found.Count > 0)
        {
            uniform = found[0];
            return true;
        }

        uniform = null;
        return false;
    }

    /// <summary>
    /// 名前に対応するすべての uniform 宣言を返す。
    /// </summary>
    /// <param name="name">uniform の名前。</param>
    /// <returns>見つかった宣言。無ければ空。</returns>
    /// <remarks>
    /// 同じ名前が複数のブロックで宣言されるのは普通である
    /// (共通コード片が各 Pass へ差し込まれるため)。
    /// <b>SRP Batcher の検査では、1 つでも定数バッファの外にある宣言があれば
    /// そのシェーダーはバッチの対象から外れる</b>ため、すべてを見る必要がある。
    /// </remarks>
    public IReadOnlyList<UniformSymbol> GetUniforms(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _uniformsByName.TryGetValue(name, out List<UniformSymbol>? found) ? found : [];
    }

    /// <summary>
    /// 名前が、展開後のコードには現れないが別の場所には現れているかを判定する。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>現れている場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>「見つからない」ことを根拠にする診断は、必ずこれを確認しなければならない。</b>
    /// 展開は単一構成でしか行わないため、
    /// 解析したコードに現れないことは「使われていない」ことを意味しない。
    /// </para>
    /// <para>
    /// 次の 2 か所を対象にしている。
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     条件分岐の非活性領域のうち、どの構成でも解析されなかったもの (<see cref="UnanalyzedIdentifiers"/>)。
    ///     シンボルの <c>#ifdef</c> の中は、そのシンボルのバリアントが解析しているので含めない。
    ///     そこにある宣言や参照は、そのバリアントの索引に入っている
    ///   </description></item>
    ///   <item><description>
    ///     マクロ定義の本体。呼び出されなかったマクロの中身は展開後のコードに現れない。
    ///     URP の <c>LitInput.hlsl</c> は
    ///     <c>#define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_MetallicGlossMap, ...)</c>
    ///     のように、テクスチャの参照をマクロ定義の中だけに書く
    ///   </description></item>
    /// </list>
    /// </remarks>
    public bool AppearsOutsideAnalyzedCode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _inactiveIdentifiers.Contains(name);
    }

    /// <summary>
    /// 名前が、そのコードブロックの展開後のコードには現れないが、そのブロックの別の場所には現れているかを判定する。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <param name="program">対象のコードブロック。そのバリアントも含めて見る。</param>
    /// <returns>現れている場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>Pass やカーネルごとに判定する検査は、こちらを使う。</b>
    /// <see cref="AppearsOutsideAnalyzedCode(string)"/> はシェーダー全体で集めるので、
    /// 別の Pass の読み飛ばした分岐に名前があるだけで、この Pass の検査まで見送ってしまう。
    /// Pass は別々にコンパイルされるので、別の Pass の構成でその名前が現れても、この Pass の話ではない。
    /// </para>
    /// <para>
    /// URP の TerrainLit.shader は、ある Pass で <c>_HeightTransition</c> を <c>UnityPerMaterial</c> の外に置いているのに、
    /// 別の Pass の読み飛ばした分岐に名前があるために <c>URP0001</c> を見送っていた。
    /// </para>
    /// </remarks>
    public bool AppearsOutsideAnalyzedCode(string name, AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(program);

        return InactiveIdentifiersByBlock.TryGetValue(program.BlockKey, out FrozenSet<string>? names)
            ? names.Contains(name)
            : _inactiveIdentifiers.Contains(name);
    }

    /// <summary>コードブロックごとの、解析したコードに現れない場所に現れた名前。</summary>
    internal FrozenDictionary<(int Start, string? Kernel), FrozenSet<string>> InactiveIdentifiersByBlock { get; init; } =
        FrozenDictionary<(int Start, string? Kernel), FrozenSet<string>>.Empty;

    /// <summary>
    /// 名前が関数形式マクロとして定義されているかどうかを判定する。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>関数形式マクロであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>呼び出しを構文木で探せるかどうかの判断に使う。</b>
    /// マクロは構文解析の前に展開されて消えるため、構文木には呼び出しの形が残っていない。
    /// マクロを対象にするルールは、展開前のトークン列を見なければならない。
    /// </para>
    /// <para>
    /// 展開の完了時点で定義されていたものを見る。
    /// 途中で <c>#undef</c> されたマクロは含まれないが、
    /// その場合はマクロとして展開されていない呼び出しが構文木に残るため、
    /// 構文木側で見つかる。どちらの経路からも漏れることはない。
    /// </para>
    /// </remarks>
    public bool IsFunctionLikeMacro(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _functionLikeMacroNames.Contains(name);
    }

    /// <summary>
    /// 名前が ShaderLab 側から参照されているかどうかを判定する。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>参照されている場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <c>Cull [_Cull]</c> のように、プロパティをレンダーステートの値として使う記法がある。
    /// この形で使われるプロパティは HLSL 側に uniform を持たないのが正常であり、
    /// 「対応する宣言が無い」という指摘の対象から外さなければならない。
    /// </remarks>
    public bool IsReferencedFromShaderLab(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _shaderLabPropertyReferences.Contains(name);
    }

    /// <summary>
    /// 名前が HLSL のコード中に現れた回数を返す。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>出現回数。マクロ展開後のトークン列を数えたもの。</returns>
    /// <remarks>
    /// <para>
    /// 「宣言されているが 1 度も読まれていない」を判定するために使う。
    /// 宣言そのものが 1 回分を占めるため、回数が 1 であれば
    /// 宣言以外のどこにも現れていないことになる。
    /// </para>
    /// <para>
    /// ブロックが複数ある場合はブロックごとに数えて合算する。
    /// 同じ共通コード片が複数の Pass へ差し込まれれば、その分だけ回数が増える。
    /// したがって<b>回数そのものに意味があるのは「1 かどうか」だけ</b>である。
    /// </para>
    /// </remarks>
    public int CountIdentifierOccurrences(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _identifierOccurrences.GetValueOrDefault(name);
    }

    /// <summary>
    /// 名前が uniform として宣言されているのに、宣言以外のどこにも現れないかを判定する。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>宣言だけで一度も使われていない場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 出現回数と宣言の個数を比べる。同じ共通コード片が複数の Pass へ差し込まれると
    /// 宣言も出現も Pass の数だけ増えるため、単純に「出現が 1 回か」では判定できない。
    /// </para>
    /// <para>
    /// 非活性領域に現れる名前は対象外とする。
    /// 別の構成では使われている可能性があり、断定できない。
    /// </para>
    /// </remarks>
    public bool IsDeclaredButNeverUsed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        int declarations = _declarationCounts.GetValueOrDefault(name);
        return declarations > 0
               && _identifierOccurrences.GetValueOrDefault(name) <= declarations
               && !_inactiveIdentifiers.Contains(name);
    }

    /// <summary>
    /// プロファイルが指定する、マテリアル用の定数バッファを列挙する。
    /// </summary>
    /// <returns>該当する定数バッファ。プロファイルがこの概念を持たない場合は空。</returns>
    public ImmutableArray<ConstantBufferSymbol> GetMaterialConstantBuffers()
    {
        if (Profile.MaterialConstantBufferName is not { } bufferName)
        {
            return [];
        }

        return
        [
            .. Programs
                .SelectMany(p => p.ConstantBuffers)
                .Where(b => string.Equals(b.Name, bufferName, StringComparison.Ordinal))
        ];
    }

    /// <summary>
    /// 取り込んだファイルが利用者のものかを、パスから判定する。<see langword="null"/> なら取り込んだファイルは利用者のものではない。
    /// </summary>
    /// <remarks><see cref="SemanticsOptions.IsUserInclude"/> を受け取る。</remarks>
    internal Func<string, bool>? IsUserInclude { get; init; }

    /// <summary>パスごとの <see cref="IsUserFile"/> の結果。ルールから並列に引かれる。</summary>
    private readonly ConcurrentDictionary<string, bool> _userFiles = new(StringComparer.Ordinal);

    /// <summary>
    /// そのファイルが、利用者が書いて直せるファイルかどうかを判定する。
    /// </summary>
    /// <param name="filePath">判定するファイルのパス。</param>
    /// <returns>解析しているファイルか、利用者が書いたヘッダなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>指摘は利用者が直せる場所にだけ出す。</b>
    /// 解析しているファイルのほかに、利用者が書いて取り込んだ共通の <c>.hlsl</c> もそれに当たる。
    /// Unity や外部パッケージのヘッダは当たらない。どれがそうかは <see cref="SemanticsOptions.IsUserInclude"/> が決める。
    /// </para>
    /// <para>
    /// 指摘を出すかどうかの判定には、これを使う <see cref="IsReportable(HlslSyntaxNode)"/> を使うこと。
    /// </para>
    /// </remarks>
    public bool IsUserFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (string.Equals(filePath, Text.FilePath, StringComparison.Ordinal))
        {
            return true;
        }

        return IsUserInclude is { } isUserInclude
               && _userFiles.GetOrAdd(filePath, static (path, predicate) => predicate(path), isUserInclude);
    }

    /// <summary>
    /// トークンが、利用者がこのファイルに書いたものかどうかを判定する。
    /// </summary>
    /// <param name="token">判定するトークン。</param>
    /// <returns>このファイルに書かれたものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>エディタの操作 (ホバー・定義へ移動・補完) が使う判定である。</b>
    /// カーソルは開いているスクリプトの中にしか無いので、そのスクリプトに書かれたかどうかで絞る。
    /// マクロ展開で生まれたトークンは位置が呼び出し側に重なっていて、
    /// カーソルの下にある語とは一致しない。
    /// </para>
    /// <para>
    /// <b>指摘を出すかどうかには <see cref="IsReportable(HlslSyntaxToken)"/> を使うこと。</b>
    /// こちらは利用者が書いて取り込んだヘッダを含まない。
    /// </para>
    /// </remarks>
    public bool IsWrittenHere(HlslSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return IsWrittenIn(token, path => string.Equals(path, Text.FilePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// ノードが、利用者がこのファイルに書いたものかどうかを判定する。
    /// </summary>
    /// <param name="node">判定するノード。</param>
    /// <returns>このファイルに書かれたものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 判定のしかたは <see cref="IsReportable(HlslSyntaxNode)"/> と同じで、ファイルをこのファイルに限る点だけが違う。
    /// エディタの操作が使う。<b>指摘を出すかどうかには <see cref="IsReportable(HlslSyntaxNode)"/> を使うこと。</b>
    /// </para>
    /// </remarks>
    public bool IsWrittenHere(HlslSyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return IsWrittenIn(node, path => string.Equals(path, Text.FilePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// ノードが、このファイルか利用者のヘッダが書いたマクロの本体から来たかどうかを判定する。
    /// </summary>
    /// <param name="node">判定するノード。</param>
    /// <returns>利用者のマクロが作ったものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// Unity や外部パッケージのヘッダのマクロには印が付かない (<see cref="HlslSyntaxToken.MacroDefinitionSpan"/>)。
    /// </remarks>
    public bool IsFromOwnMacro(HlslSyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.FirstToken?.MacroDefinitionSpan is not null;
    }

    /// <summary>
    /// ノードを報告してよいかを判定する。
    /// </summary>
    /// <param name="node">判定するノード。</param>
    /// <returns>報告してよければ <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>利用者が直せるコードだけを報告する。</b>
    /// 利用者のファイル (<see cref="IsUserFile"/>: このファイルと、利用者が書いて取り込んだヘッダ) に書かれたコードと、
    /// それらのファイルが書いたマクロの本体から来たコードである。
    /// Unity や外部パッケージのヘッダのコードと、それらのマクロが作ったコードは対象外である。
    /// </para>
    /// <para>
    /// 判定はノードの先頭のトークンで代表させる。ノードは複数のファイルにまたがりうる。
    /// <b>先頭がヘッダのマクロの展開なら、展開でない最初のトークンで代表させる。</b>
    /// <c>HLSLSupport.cginc</c> は D3D11 で <c>#define fixed4 half4</c> と定義する。
    /// <c>fixed4 frag(v2f i) : SV_Target { ... }</c> の先頭の <c>fixed4</c> は展開の結果だが、
    /// 関数名の <c>frag</c> は利用者が書いたトークンである。
    /// </para>
    /// <para>
    /// マクロから来たコードの位置は、本体の位置になる
    /// (<see cref="HlslSyntaxNode.GetLocation"/>)。
    /// そこが直す場所であり、同じマクロを何度使っても報告は 1 か所にまとまる。
    /// </para>
    /// </remarks>
    public bool IsReportable(HlslSyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return IsWrittenIn(node, IsUserFile) || IsFromOwnMacro(node);
    }

    /// <summary>
    /// トークンを報告してよいかを判定する。
    /// </summary>
    /// <param name="token">判定するトークン。</param>
    /// <returns>報告してよければ <see langword="true"/>。</returns>
    /// <remarks>
    /// ノードの一部 (メンバー名のトークンなど) だけを報告するルールが使う。
    /// 判定の範囲は <see cref="IsReportable(HlslSyntaxNode)"/> と同じである。
    /// </remarks>
    public bool IsReportable(HlslSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return IsWrittenIn(token, IsUserFile) || token.MacroDefinitionSpan is not null;
    }

    /// <summary>トークンが、条件を満たすファイルにそのまま書かれたものかを判定する。</summary>
    /// <param name="token">判定するトークン。</param>
    /// <param name="isTargetFile">対象のファイルかをパスから判定する。</param>
    /// <returns>書かれたものであれば <see langword="true"/>。</returns>
    private static bool IsWrittenIn(HlslSyntaxToken token, Func<string, bool> isTargetFile)
        // マクロの実引数として書かれたトークンは、利用者が呼び出し位置に書いたものである
        // (HlslSyntaxToken.MacroArgumentSpan)。
        => (!token.IsFromMacroExpansion || token.MacroArgumentSpan is not null)
           && isTargetFile(token.Source.FilePath);

    /// <summary>ノードが、条件を満たすファイルに書かれたものかを判定する。</summary>
    /// <param name="node">判定するノード。</param>
    /// <param name="isTargetFile">対象のファイルかをパスから判定する。</param>
    /// <returns>書かれたものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>ふだんは覚えてある先頭のトークンを使う。</b>
    /// <c>DescendantTokens()</c> から取ると、判定 1 回につき列挙子が確保される。
    /// この判定はルールが宣言ごとに呼ぶので、
    /// 展開後の木の大きさに比例した数だけ実行される。
    /// 展開でないトークンを探すのは、先頭が展開の結果だったときだけにする。
    /// </para>
    /// <para>
    /// 実引数のトークンでは代表させない。ヘッダのマクロの本体が実引数を包んだ部分
    /// (<c>#define HEADER_ID(x) (x)</c> の括弧) まで、利用者が書いたことになる。
    /// ヘッダの関数形式マクロが作ったコードは、実引数も含めてすべて展開の結果になるので、この判定では拾わない。
    /// </para>
    /// </remarks>
    private static bool IsWrittenIn(HlslSyntaxNode node, Func<string, bool> isTargetFile)
    {
        if (node.FirstToken is not { } first)
        {
            return false;
        }

        // 先頭がマクロの実引数として書かれたトークンなら、そのノードは呼び出し位置に書かれている。
        if (!first.IsFromMacroExpansion || first.MacroArgumentSpan is not null)
        {
            return IsWrittenIn(first, isTargetFile);
        }

        // 展開の呼び出し位置が対象のファイルでなければ、その外のコードである。
        if (!isTargetFile(first.Source.FilePath))
        {
            return false;
        }

        foreach (HlslSyntaxToken token in node.DescendantTokens())
        {
            if (!token.IsFromMacroExpansion)
            {
                return IsWrittenIn(token, isTargetFile);
            }
        }

        return false;
    }

    /// <summary>
    /// 関数の宣言が 1 つでもあるかどうか。
    /// </summary>
    /// <remarks>関数を見ないシェーダーで、木を歩く前に抜けるために使う。</remarks>
    public bool HasFunctionDeclarations => FunctionDeclarations.Count > 0;

    /// <summary>
    /// 指定した名前で宣言されている関数を、その木の宣言で返す。
    /// </summary>
    /// <param name="name">関数の名前。</param>
    /// <param name="program">呼び出しがある木。</param>
    /// <returns>宣言。1 つも無ければ空。</returns>
    /// <remarks>
    /// <see cref="GetFunctionDeclarations(string)"/> と同じ宣言を、この木に同じ位置の宣言があればそれに置き換えて返す。
    /// ヘッダの関数でも、仮引数の型がキーワードで書き分けたマクロ (<c>inout CTYPE color</c>) なら、木ごとに型が違う。
    /// 呼び出しと同じ木の宣言で比べないと、別の構成の仮引数の型で判定することになる。
    /// </remarks>
    public ImmutableArray<FunctionDeclarationSyntax> GetFunctionDeclarations(string name, AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        ImmutableArray<FunctionDeclarationSyntax> all = GetFunctionDeclarations(name);

        if (all.IsEmpty)
        {
            return all;
        }

        _ownFunctions ??= [];

        if (!_ownFunctions.TryGetValue(program, out Dictionary<(SourceText Source, int Start), FunctionDeclarationSyntax>? own))
        {
            own = [];

            foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                if (declaration is FunctionDeclarationSyntax function && function.FirstToken is { } first)
                {
                    own.TryAdd((first.Source, first.Span.Start), function);
                }
            }

            _ownFunctions[program] = own;
        }

        // 別のコードブロック (Pass やカーネル) にしか現れない宣言は、このコードからは呼べない。
        // 別の Pass が取り込んだヘッダの関数を候補にすると、取り込み忘れを見落とす。
        return
        [
            .. all.Where(function => function.FirstToken is { } first
                                     && _functionBlocks is { } blocks
                                     && blocks.TryGetValue((first.Source, first.Span.Start), out HashSet<(int Start, string? Kernel)>? inBlocks)
                                     && inBlocks.Contains(program.BlockKey))
                  .Select(function => function.FirstToken is { } first
                                      && own.TryGetValue((first.Source, first.Span.Start), out FunctionDeclarationSyntax? mine)
                ? mine
                : function),
        ];
    }

    /// <summary>
    /// 指定した名前で宣言されている関数をすべて返す。
    /// </summary>
    /// <param name="name">関数の名前。</param>
    /// <returns>宣言。1 つも無ければ空。</returns>
    /// <remarks>
    /// <para>
    /// <b>取り込んだヘッダの宣言も含む。</b>
    /// 呼ばれる先がヘッダにあることは普通であり、
    /// 候補から外すと「その関数は無い」という誤った答えになる。
    /// </para>
    /// <para>
    /// プロトタイプと定義は別々の要素として並ぶ。
    /// 出現条件では絞っていない。構成ごとに違う宣言も含まれる。
    /// </para>
    /// <para>
    /// 同じ位置の宣言は 1 つにまとめ、最初に見つけた木のものを返す。
    /// 仮引数の型を見るなら <see cref="GetFunctionDeclarations(string, AnalyzedProgram)"/> を使うこと。
    /// </para>
    /// </remarks>
    public ImmutableArray<FunctionDeclarationSyntax> GetFunctionDeclarations(string name)
        => FunctionDeclarations.TryGetValue(name, out ImmutableArray<FunctionDeclarationSyntax> found)
            ? found
            : [];

    /// <summary>
    /// 呼び出しが、どの関数の宣言に解決されるかを求める。
    /// </summary>
    /// <param name="invocation">対象の呼び出し。</param>
    /// <param name="program">その呼び出しが属するコードブロック。</param>
    /// <returns>解決結果。</returns>
    /// <remarks>
    /// <para>
    /// <b>「どの関数が呼ばれたか」をルールから引くための入口である。</b>
    /// 名前だけで判定すると、多重定義された関数では答えが出せない。
    /// 出現条件で候補を絞り、実引数の型で渡せるものを選ぶ、という手順は
    /// 書き写すたびに食い違うので 1 か所に置く。
    /// </para>
    /// <para>
    /// <b>決められなかったことは、決められなかったと返す。</b>
    /// <see cref="OverloadResolution.Status"/> を見ずに
    /// <see cref="OverloadResolution.Declaration"/> の <see langword="null"/> だけを見ると、
    /// 「調べていない」を「呼ばれていない」と取り違える。
    /// </para>
    /// <para>
    /// 索引と型の評価器はファイルごとに 1 度だけ組み立てて使い回す。
    /// 呼び出しごとに組み立てると走査が二乗になる。
    /// </para>
    /// </remarks>
    public OverloadResolution ResolveCall(InvocationExpressionSyntax invocation, AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(program);

        return OverloadResolver.Resolve(this, invocation, GetExpressionTypeBinder(program));
    }

    /// <summary>
    /// コードブロックに対応する、式の型を求める仕組みを返す。
    /// </summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <returns>型の束縛器。</returns>
    /// <remarks>
    /// <para>
    /// <b>ノードごとに <c>new ExpressionTypeBinder(...)</c> してはいけない。</b>
    /// 組み立てには宣言と構造体のフィールドの集め直しが要るため、
    /// 式ごとに作ると走査が二乗になる。
    /// ここで返すものはコードブロックごとに 1 度だけ作られ、使い回される。
    /// </para>
    /// <para>
    /// 1 つのセマンティックモデルは 1 つのスレッドからしか使われない
    /// (<see cref="GetConditionMap"/> と同じ前提)。
    /// </para>
    /// </remarks>
    public ExpressionTypeBinder GetExpressionTypeBinder(AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        _binders ??= [];

        if (!_binders.TryGetValue(program, out ExpressionTypeBinder? binder))
        {
            binder = new ExpressionTypeBinder(this, program);
            _binders[program] = binder;
        }

        return binder;
    }

    /// <summary>
    /// 関数の名前から宣言を引く索引。
    /// </summary>
    /// <remarks>
    /// <b>同じ宣言が Pass ごとに現れる。</b>
    /// 位置で 1 つに寄せる。先頭のトークンで判定するのは、
    /// 範囲を求めると部分木をたどることになり、
    /// ヘッダの関数すべてで行うと木を丸ごと歩くのと変わらない費用になるためである。
    /// </remarks>
    private FrozenDictionary<string, ImmutableArray<FunctionDeclarationSyntax>> FunctionDeclarations
    {
        get
        {
            if (_functionDeclarations is not null)
            {
                return _functionDeclarations;
            }

            Dictionary<string, List<FunctionDeclarationSyntax>> byName = new(StringComparer.Ordinal);
            HashSet<(SourceText Source, int Start)> seen = [];
            Dictionary<(SourceText Source, int Start), HashSet<(int Start, string? Kernel)>> blocks = [];

            foreach (AnalyzedProgram program in Programs.Concat(SymbolVariants))
            {
                foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
                {
                    // 構造体の外で定義したメソッド (Wave::GetIndex) はグローバルな関数ではない。
                    if (declaration is not FunctionDeclarationSyntax function
                        || function.NameToken.IsMissing
                        || function.IsMethodDefinition
                        || function.FirstToken is not { } first)
                    {
                        continue;
                    }

                    // どのコードブロックの木に現れたかを覚える。別の Pass の関数は、そのコードからは呼べない。
                    if (!blocks.TryGetValue((first.Source, first.Span.Start), out HashSet<(int Start, string? Kernel)>? inBlocks))
                    {
                        inBlocks = [];
                        blocks[(first.Source, first.Span.Start)] = inBlocks;
                    }

                    inBlocks.Add(program.BlockKey);

                    if (!seen.Add((first.Source, first.Span.Start)))
                    {
                        continue;
                    }

                    if (!byName.TryGetValue(function.Name, out List<FunctionDeclarationSyntax>? found))
                    {
                        found = [];
                        byName[function.Name] = found;
                    }

                    found.Add(function);
                }
            }

            _functionDeclarations = byName.ToFrozenDictionary(
                p => p.Key,
                p => (ImmutableArray<FunctionDeclarationSyntax>)[.. p.Value],
                StringComparer.Ordinal);
            _functionBlocks = blocks;

            return _functionDeclarations;
        }
    }

    /// <summary>
    /// 展開後の木すべてに現れる、宣言された名前。
    /// </summary>
    /// <remarks>
    /// 変数・仮引数・関数・構造体・定数バッファの名前を含む。
    /// マクロやシンボルは含まない。呼び出し側が足すこと。
    /// </remarks>
    public FrozenSet<string> AllDeclaredNames
    {
        get
        {
            ScanDeclarations();
            return _declaredNames!;
        }
    }

    /// <summary>
    /// 関数の中の変数と仮引数としてしか宣言されていない名前。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AllDeclaredNames"/> のうち、関数の外の変数・関数・構造体・定数バッファ・構造体のフィールドとして
    /// どこにも宣言されていない名前である。
    /// </para>
    /// <para>
    /// こうした名前は、ある位置で「宣言されている」かどうかが範囲で決まる。
    /// 別の関数の局所変数は、その位置からは見えない。
    /// 位置ごとの判断は <see cref="Programs.ExpressionTypeBinder.ResolveName"/> で行う。
    /// </para>
    /// </remarks>
    public FrozenSet<string> LocalOnlyDeclaredNames
    {
        get
        {
            ScanDeclarations();
            return _localOnlyNames!;
        }
    }

    /// <summary>
    /// 取り込んだヘッダの宣言に現れた名前 (<see cref="AllDeclaredNames"/> のうち、ヘッダにあるもの)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 解析しているファイルに書いた宣言は、その Pass やカーネルのコードからしか見えない。
    /// ヘッダの宣言は、取り込んだどのコードブロックからも見えるものとして扱う
    /// (どのブロックが取り込んだかまでは区別しない)。
    /// </para>
    /// </remarks>
    public FrozenSet<string> HeaderDeclaredNames
    {
        get
        {
            ScanDeclarations();
            return _headerDeclaredNames!;
        }
    }

    /// <summary>
    /// 宣言を 1 度だけ走査して覚える。
    /// </summary>
    /// <remarks>
    /// 1 つのセマンティックモデルは 1 つのファイルのものであり、
    /// 1 つのスレッドからしか使われない (<see cref="GetConditionMap"/> と同じ前提)。
    /// </remarks>
    private void ScanDeclarations()
    {
        if (_declaredNames is not null)
        {
            return;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<string> outside = new(StringComparer.Ordinal);
        HashSet<string> header = new(StringComparer.Ordinal);
        HashSet<string> headerOutside = new(StringComparer.Ordinal);

        // 同じヘッダの宣言が、コードブロックの数だけ現れる。
        // 位置で 1 度だけ見る。Pass が 9 つあるシェーダーでは走査が 9 分の 1 になる。
        //
        // 解析しているファイル自身の宣言はまとめない。
        // 既定の構成とバリアントは同じマスクしたテキストから作るので、同じ位置に始まる関数でも、
        // 中身は構成によって違う (#ifdef の中のローカル変数はバリアントの側にしか無い)。
        // ファイルはパスで見分ける (IsWrittenHere と同じ)。
        HashSet<(SourceText Source, int Start)> seenHeaderDeclarations = [];

        // ヘッダの宣言ごとの名前。コードブロックごとの集合へ足すときに使い回す。
        Dictionary<(SourceText Source, int Start), HashSet<string>> headerNamesAt = [];
        Dictionary<(int Start, string? Kernel), HashSet<string>> headerByBlock = [];
        HashSet<((int Start, string? Kernel) Block, SourceText Source, int Start)> addedToBlock = [];

        foreach (AnalyzedProgram program in Programs.Concat(SymbolVariants))
        {
            if (!headerByBlock.TryGetValue(program.BlockKey, out HashSet<string>? blockNames))
            {
                blockNames = new HashSet<string>(StringComparer.Ordinal);
                headerByBlock[program.BlockKey] = blockNames;
            }

            foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                bool fromHeader = declaration.FirstToken is { } first
                                  && !string.Equals(first.Source.FilePath, Text.FilePath, StringComparison.Ordinal);

                if (fromHeader)
                {
                    (SourceText Source, int Start) at = (declaration.FirstToken!.Source, declaration.FirstToken.Span.Start);

                    if (addedToBlock.Add((program.BlockKey, at.Source, at.Start))
                        && headerNamesAt.TryGetValue(at, out HashSet<string>? seenNames))
                    {
                        blockNames.UnionWith(seenNames);
                    }

                    if (!seenHeaderDeclarations.Add(at))
                    {
                        continue;
                    }

                    HashSet<string> declared = new(StringComparer.Ordinal);
                    Absorb(declaration, declared, headerOutside);
                    headerNamesAt[at] = declared;
                    header.UnionWith(declared);
                    blockNames.UnionWith(declared);
                }

                Absorb(declaration, names, outside);
            }
        }

        _declaredNames = names.ToFrozenSet(StringComparer.Ordinal);
        _headerDeclaredNames = header.ToFrozenSet(StringComparer.Ordinal);
        _localOnlyNames = names.Except(outside).ToFrozenSet(StringComparer.Ordinal);
        _headerDeclaredNamesByBlock = headerByBlock.ToFrozenDictionary(
            p => p.Key,
            p => p.Value.ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// そのコードブロックが取り込んだヘッダの宣言に現れた名前を返す。
    /// </summary>
    /// <param name="block">コードブロック (<see cref="AnalyzedProgram.BlockKey"/>)。</param>
    /// <returns>名前。そのブロックの木が 1 つも無ければ空。</returns>
    /// <remarks>
    /// <b>別の Pass が取り込んだヘッダの宣言は、そのコードからは見えない。</b>
    /// シェーダー全体で集めた <see cref="HeaderDeclaredNames"/> を使うと、
    /// ヘッダの取り込みを忘れた Pass で、別の Pass が取り込んだヘッダの名前を使っても見落とす。
    /// </remarks>
    public FrozenSet<string> GetHeaderDeclaredNames((int Start, string? Kernel) block)
    {
        ScanDeclarations();
        return _headerDeclaredNamesByBlock!.TryGetValue(block, out FrozenSet<string>? names)
            ? names
            : FrozenSet<string>.Empty;
    }

    /// <summary>宣言 1 つ分を索引へ取り込む。</summary>
    /// <param name="declaration">対象の宣言。</param>
    /// <param name="names">名前を足す先。</param>
    /// <param name="outside">関数の中の変数と仮引数を除いた名前を足す先。</param>
    private static void Absorb(HlslDeclarationSyntax declaration, HashSet<string> names, HashSet<string> outside)
    {
        foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case VariableDeclarationSyntax variableDeclaration:
                    bool local = IsInsideFunction(variableDeclaration);

                    foreach (VariableDeclaratorSyntax variable in variableDeclaration.Variables)
                    {
                        names.Add(variable.Name);

                        if (!local)
                        {
                            outside.Add(variable.Name);
                        }
                    }

                    break;

                case ParameterSyntax parameter:
                    names.Add(parameter.Name);
                    break;

                case FunctionDeclarationSyntax function:
                    names.Add(function.Name);
                    outside.Add(function.Name);
                    break;

                case StructDeclarationSyntax structure:
                    names.Add(structure.Name);
                    outside.Add(structure.Name);
                    break;

                case ConstantBufferDeclarationSyntax buffer when buffer.NameToken is { } name:
                    names.Add(name.Text);
                    outside.Add(name.Text);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>変数の宣言が関数の中の変数かを判定する。</summary>
    /// <param name="declaration">対象の宣言。</param>
    /// <returns>
    /// 関数の中の変数なら <see langword="true"/>。
    /// 構造体のフィールドは、その構造体が関数の中にあっても変数ではないので <see langword="false"/>。
    /// </returns>
    private static bool IsInsideFunction(VariableDeclarationSyntax declaration)
    {
        foreach (SyntaxNode ancestor in declaration.Ancestors())
        {
            switch (ancestor)
            {
                case FunctionDeclarationSyntax:
                    return true;

                case StructDeclarationSyntax:
                    return false;

                default:
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// ルールが検査する宣言を、それが属する構成とともに列挙する。
    /// </summary>
    /// <returns>
    /// 既定の構成 (<see cref="Programs"/>) と各バリアント (<see cref="SymbolVariants"/>) の木にある、
    /// このファイルに書かれたトップレベルの宣言。この順に返す。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>どの構成の木も、その木の文脈で歩く。</b>
    /// 形は同じでも構成によって意味が変わるコードがある。
    /// 分岐ごとに同じマクロを違う中身で定義していれば、<c>PRECISE_VECTOR v = 0;</c> は
    /// 既定の木でもバリアントの木でも同じ形の宣言だが、型は構成ごとに違う。
    /// 突き合わせはこれを「同じ」と見るので、既定の木だけを歩くとバリアントの側の型では一度も検査されない。
    /// </para>
    /// <para>
    /// <b>突き合わせで既定の木へ足したノード (<see cref="Conditional.ConditionMap.GetInsertedChildren"/>) は返さない。</b>
    /// どれもバリアントの木にあり、そちらを歩けばその構成の文脈で渡る。
    /// 既定の木の子として渡すと、そのノードの属さない構成の文脈で型を求めることになる。
    /// 1 本の木として見せたい場合 (表示など) は <see cref="EnumerateOwnNodes"/> を使う。
    /// </para>
    /// <para>
    /// <b>既定の木に並べた分岐のうち、あるバリアントの構成でしか存在しないものは、そのバリアントの木で歩く。</b>
    /// 既定の木の宣言は既定の構成の中身で展開してある。
    /// <c>#if !defined(ENABLE_ALPHA)</c> で <c>CTYPE</c> を <c>float3</c> と <c>float4</c> に書き分けていれば、
    /// 既定の木の <c>CTYPE color</c> は <c>float3</c> である。
    /// そこに並べた <c>#ifdef ENABLE_ALPHA</c> の中の <c>color.w</c> を既定の木の文脈で見ると、誤りに見える。
    /// <c>ENABLE_ALPHA</c> を有効にしたバリアントの木には同じコードが正しい型で載っている。
    /// </para>
    /// <para>
    /// 同じ位置のコードは構成の数だけ現れる。報告の重複はルールの側で除くこと。
    /// ノードの出現条件は <see cref="GetEffectiveCondition"/> で求める。
    /// </para>
    /// </remarks>
    public IEnumerable<(HlslDeclarationSyntax Declaration, AnalyzedProgram Program)> EnumerateRuleDeclarations()
    {
        Dictionary<(SymbolCondition, AnalyzedProgram), bool> covered = [];

        foreach (AnalyzedProgram program in Programs.Concat(SymbolVariants))
        {
            foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                // このファイルが書いたマクロが作った宣言も歩く。本体は利用者が直せる。
                if (IsReportable(declaration) && !IsCoveredByVariant(declaration, program, covered))
                {
                    yield return (declaration, program);
                }
            }
        }
    }

    /// <summary>
    /// ノードが、その木では有効にしていないシンボルを要し、そのシンボルを有効にしたバリアントがあるかを判定する。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <param name="program">そのノードが属する木。</param>
    /// <param name="cache">条件と木ごとの判定結果。同じ分岐のノードは同じ条件を持つ。</param>
    /// <returns>
    /// 存在する条件が、同じコードブロックの別のバリアントの有効にしたシンボルをすべて含み、
    /// その中にこの木で有効にしていないシンボルがあれば <see langword="true"/>。
    /// 条件が分からないノードは <see langword="false"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// そのバリアントの木は、残りのシンボルを無効にした構成である。
    /// 条件に含まれるほかのシンボルの分岐は、既定の木と同じ領域を並べてあるので、その木にも載っている。
    /// </para>
    /// <para>
    /// <b>バリアントの木にも同じことが言える。</b>
    /// <c>LOW_RESOLUTION</c> のバリアントの木も <c>#ifdef ENABLE_ALPHA</c> の分岐を並べていて、
    /// その中身は <c>ENABLE_ALPHA</c> を無効にした構成の型で展開してある。
    /// 両方を有効にした構成は作っていないので、その組み合わせでは検査しない。
    /// </para>
    /// </remarks>
    private bool IsCoveredByVariant(HlslSyntaxNode node, AnalyzedProgram program, Dictionary<(SymbolCondition, AnalyzedProgram), bool> cache)
    {
        if (SymbolVariants.IsEmpty)
        {
            return false;
        }

        Conditional.ConditionMap map = GetConditionMap();
        SymbolCondition condition = map.GetCondition(node);

        if (condition.IsAlways || condition.IsUnknown)
        {
            return false;
        }

        if (!cache.TryGetValue((condition, program), out bool covered))
        {
            ImmutableArray<string> enabled = program.EnabledSymbols.IsDefault ? [] : program.EnabledSymbols;

            covered = SymbolVariants.Any(variant =>
                variant.CodeSpan.Start == program.CodeSpan.Start
                && string.Equals(variant.KernelName, program.KernelName, StringComparison.Ordinal)
                && (variant.IsPacked
                    ? IsCoveredByPacked(variant, enabled, condition, map)
                    : variant.EnabledSymbols.Any(symbol => !enabled.Contains(symbol))
                      && variant.EnabledSymbols.All(symbol => !map.IsPossible(condition.And(SymbolCondition.Symbol(symbol, false))))));

            cache[(condition, program)] = covered;
        }

        return covered;
    }

    /// <summary>
    /// キーワードをまとめて有効にしたバリアントの木に、そのノードがその構成の文脈で載っているかを判定する。
    /// </summary>
    /// <param name="variant">まとめた構成のバリアント。</param>
    /// <param name="enabled">ノードが属する木で有効にしたシンボル。</param>
    /// <param name="condition">ノードの存在する条件。</param>
    /// <param name="map">条件の索引。</param>
    /// <returns>載っていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// まとめた構成の違いは、どれか 1 つのキーワードのものである。
    /// ノードがそのうちの 1 つ (この木では有効にしていないもの) を要し、ほかのどれとも両立するなら、その木に載っている。
    /// </remarks>
    private static bool IsCoveredByPacked(
        AnalyzedProgram variant,
        ImmutableArray<string> enabled,
        SymbolCondition condition,
        Conditional.ConditionMap map)
        => variant.EnabledSymbols.Any(symbol => !enabled.Contains(symbol)
                                                && !map.IsPossible(condition.And(SymbolCondition.Symbol(symbol, false))))
           && variant.EnabledSymbols.All(symbol => map.IsPossible(condition.And(SymbolCondition.Symbol(symbol))));

    /// <summary>
    /// ルールが検査するノードを、それが属する構成とともに列挙する。
    /// </summary>
    /// <returns><see cref="EnumerateRuleDeclarations"/> の宣言と、その中のノード。</returns>
    /// <remarks>
    /// 宣言の中にあってこのファイル由来でないノード (ヘッダのマクロが作った部分) も含まれる。
    /// 報告の直前に位置を確かめるルールは、その判定を省いてはならない。
    /// </remarks>
    public IEnumerable<(HlslSyntaxNode Node, AnalyzedProgram Program)> EnumerateRuleNodes()
    {
        Dictionary<(SymbolCondition, AnalyzedProgram), bool> covered = [];

        foreach ((HlslDeclarationSyntax declaration, AnalyzedProgram program) in EnumerateRuleDeclarations())
        {
            foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
            {
                // バリアントの構成でしか存在しない分岐は、そのバリアントの木で渡る (EnumerateRuleDeclarations)。
                if (node is HlslSyntaxNode hlsl && !IsCoveredByVariant(hlsl, program, covered))
                {
                    yield return (hlsl, program);
                }
            }
        }
    }

    /// <summary>
    /// ノードが存在する条件を、それが属する構成を踏まえて求める。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <param name="program">そのノードが属するコードブロック。</param>
    /// <returns>存在する条件。追えなかった場合は <see cref="SymbolCondition.Unknown"/>。</returns>
    /// <remarks>
    /// <b>バリアントの木の中で「条件が付いていない」は「どの構成でも」を意味しない。</b>
    /// そのシンボルを有効にした構成の中での話である。
    /// 出現条件に、その木を解析した構成 (<see cref="AnalyzedProgram.AnalyzedCondition"/>) を掛け合わせる。
    /// </remarks>
    public SymbolCondition GetEffectiveCondition(HlslSyntaxNode node, AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(program);

        return GetConditionMap().GetCondition(node).And(program.AnalyzedCondition);
    }

    /// <summary>
    /// その木が実際に表す構成のうち、木の中の出現条件では表されない部分を返す。
    /// </summary>
    /// <param name="program">木。</param>
    /// <returns>
    /// その木を解析した構成 (<see cref="AnalyzedProgram.AnalyzedCondition"/>) に、
    /// 同じコードブロックでバリアントになったシンボルと調べきれなかったシンボルのうち、
    /// その木で有効にしていないものを無効として掛け合わせた条件。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b><see cref="AnalyzedProgram.AnalyzedCondition"/> は、有効にしたシンボルしか表さない。</b>
    /// 既定の木は「常に」だが、実際にはバリアントになったシンボルをどれも無効にした構成である。
    /// <c>_A</c> のバリアントの木も、<c>_B</c> は無効にしている。
    /// そのまま使うと、どの木も解析していない組み合わせ (<c>_A &amp;&amp; _B</c>) についてまで結論を出してしまう。
    /// 複数の木の結果を条件で突き合わせるルールは、ノードの出現条件にこれを掛けて使うこと。
    /// </para>
    /// <para>
    /// 両方の分岐を 1 本の木に並べたシンボルは掛けない。並べた木はその両方の構成を表している。
    /// </para>
    /// </remarks>
    public SymbolCondition GetTreeConfiguration(AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        _treeConfigurations ??= [];

        if (_treeConfigurations.TryGetValue(program, out SymbolCondition cached))
        {
            return cached;
        }

        ImmutableArray<string> enabled = program.EnabledSymbols.IsDefault ? [] : program.EnabledSymbols;
        SymbolCondition condition = program.AnalyzedCondition;

        IEnumerable<string> others = SymbolVariants
            .Where(v => v.BlockKey == program.BlockKey && !v.EnabledSymbols.IsDefault)
            .SelectMany(v => v.EnabledSymbols)
            .Concat(UnexploredSymbols)
            .Where(s => !enabled.Contains(s))
            .Distinct(StringComparer.Ordinal);

        foreach (string symbol in others)
        {
            condition = condition.And(SymbolCondition.Symbol(symbol, isDefined: false));
        }

        _treeConfigurations[program] = condition;
        return condition;
    }

    /// <summary><see cref="GetTreeConfiguration"/> の結果。</summary>
    private Dictionary<AnalyzedProgram, SymbolCondition>? _treeConfigurations;

    /// <summary><see cref="TryEnumerateConfigurations"/> が組み合わせを試すシンボルの上限。</summary>
    /// <remarks>組み合わせは 2 のこの数乗になる。超えたら数えずに見送る。</remarks>
    public const int MaxConfigurationSymbols = 12;

    /// <summary>
    /// ノードの出現条件に現れるシンボルの、実在する組み合わせを列挙する。
    /// </summary>
    /// <param name="nodes">対象のノード。どれも <paramref name="program"/> の木のもの (か、その木へ足したもの)。</param>
    /// <param name="program">ノードが属するコードブロック。その木を解析した構成に合う組み合わせだけを返す。</param>
    /// <param name="configurations">組み合わせごとの、存在するノード。</param>
    /// <returns>
    /// 列挙できれば <see langword="true"/>。条件が分からないノードがある場合と、
    /// シンボルが <see cref="MaxConfigurationSymbols"/> を超える場合は <see langword="false"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>ノードを数えたり合計したりするルールは、これで構成ごとに数える。</b>
    /// 両方の分岐を並べた木には、同時には存在しないノードが並んでいる。木のノードをそのまま数えると、
    /// どの構成でも起きない数を報告する。構成ごとに数えた最大を上限と比べる。
    /// </para>
    /// <para>
    /// 同じ <c>#pragma</c> 行のシンボルを同時に有効にする組み合わせなど、実在しないものは含めない
    /// (<see cref="Conditional.ConditionMap.IsPossible"/>)。条件の付いたノードが無ければ、組み合わせは 1 つである。
    /// </para>
    /// <para>
    /// 列挙できないときに数えずに済ませるのは、分からないことを誤りにしないためである。
    /// </para>
    /// </remarks>
    public bool TryEnumerateConfigurations(
        IEnumerable<HlslSyntaxNode> nodes,
        AnalyzedProgram program,
        out ImmutableArray<Conditional.NodeConfiguration> configurations)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(program);

        configurations = [];

        List<(HlslSyntaxNode Node, SymbolCondition Condition)> conditioned = [];
        SortedSet<string> symbols = new(StringComparer.Ordinal);

        foreach (HlslSyntaxNode node in nodes)
        {
            SymbolCondition condition = GetEffectiveCondition(node, program);

            if (condition.IsUnknown)
            {
                return false;
            }

            conditioned.Add((node, condition));
            symbols.UnionWith(condition.EnumerateSymbols());
        }

        symbols.UnionWith(program.AnalyzedCondition.EnumerateSymbols());

        if (symbols.Count > MaxConfigurationSymbols)
        {
            return false;
        }

        Conditional.ConditionMap map = GetConditionMap();
        string[] ordered = [.. symbols];
        ImmutableArray<Conditional.NodeConfiguration>.Builder builder =
            ImmutableArray.CreateBuilder<Conditional.NodeConfiguration>();

        for (int mask = 0; mask < 1 << ordered.Length; mask++)
        {
            HashSet<string> enabled = new(StringComparer.Ordinal);
            SymbolCondition assignment = SymbolCondition.Always;

            for (int i = 0; i < ordered.Length; i++)
            {
                bool on = (mask & (1 << i)) != 0;

                if (on)
                {
                    enabled.Add(ordered[i]);
                }

                assignment = assignment.And(SymbolCondition.Symbol(ordered[i], on));
            }

            if (!map.IsPossible(assignment) || !program.AnalyzedCondition.IsSatisfiedBy(enabled.Contains))
            {
                continue;
            }

            HashSet<HlslSyntaxNode> present = [];

            foreach ((HlslSyntaxNode node, SymbolCondition condition) in conditioned)
            {
                if (condition.IsSatisfiedBy(enabled.Contains))
                {
                    present.Add(node);
                }
            }

            builder.Add(new Conditional.NodeConfiguration([.. enabled], present));
        }

        configurations = builder.ToImmutable();
        return true;
    }

    /// <summary>
    /// このファイルに書かれたコードのノードだけを、1 本の木として列挙する。
    /// </summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <returns>このファイルに書かれた宣言と、その中のノード。突き合わせで足したノードも含む。</returns>
    /// <remarks>
    /// <para>
    /// <b>ルールが検査のために歩くなら <see cref="EnumerateRuleNodes"/> を使う。</b>
    /// こちらはバリアントにしか無いノードを既定の木の子として返すので、
    /// そのノードの属さない構成の文脈で渡すことになる。1 本の木として見せる用途に向く。
    /// </para>
    /// <para>
    /// 展開後の木の大半は取り込んだヘッダのコードであり、
    /// そこを歩いても報告する場所は 1 つも見つからない。
    /// Unity 同梱の 109 件では展開後のトークンが 1,428 万個あり、
    /// **ルールの実行時間が全体の 7 割**を占めていた。
    /// </para>
    /// <para>
    /// <b>ノードごとに <see cref="IsWrittenHere(HlslSyntaxNode)"/> を呼ぶより速い。</b>
    /// あの判定は最初のトークンを取り出すために列挙子を作る。
    /// 数百万ノードで呼ぶと、判定そのものが費用になる。
    /// 宣言の単位で 1 度だけ判定すれば、中のノードは見るまでもない。
    /// </para>
    /// <para>
    /// 宣言の中にあってこのファイル由来でないノード (ヘッダのマクロが作った部分) も
    /// 含まれる。**報告の直前に位置を確かめるルールは、その判定を省いてはならない。**
    /// </para>
    /// </remarks>
    public IEnumerable<SyntaxNode> EnumerateOwnNodes(AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        ConditionMap conditions = GetConditionMap();

        // バリアントの木にしか無い宣言 (マクロの定義を #ifdef で切り替えている場合など) は、
        // 木のルートに足すものとして記録されている。
        foreach (SyntaxNode node in EnumerateInserted(conditions, program.Tree.Root))
        {
            yield return node;
        }

        foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
        {
            // このファイルが書いたマクロが作った宣言も歩く。本体は利用者が直せる。
            if (!IsReportable(declaration))
            {
                continue;
            }

            foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
            {
                yield return node;

                // バリアントの木にしか無いノードも、1 本の木に足したものとして歩く。
                // 歩かないと、その中のコードだけ検査されない。
                if (node is not HlslSyntaxNode hlsl)
                {
                    continue;
                }

                foreach (SyntaxNode inserted in EnumerateInserted(conditions, hlsl))
                {
                    yield return inserted;
                }
            }
        }
    }

    /// <summary>
    /// そのノードの子として足すノードを、中身ごと列挙する。
    /// </summary>
    /// <param name="conditions">ノードの出現条件。</param>
    /// <param name="node">足し先のノード。</param>
    /// <returns>足すノードとその中のノード。</returns>
    /// <remarks>
    /// このファイルが書いたものだけを返す。
    /// 取り込んだヘッダの中身は、バリアントの側にしか無くても利用者に直しようがない。
    /// </remarks>
    private IEnumerable<SyntaxNode> EnumerateInserted(ConditionMap conditions, HlslSyntaxNode node)
    {
        foreach (ConditionalNode inserted in conditions.GetInsertedChildren(node))
        {
            if (!IsReportable(inserted.Node))
            {
                continue;
            }

            foreach (SyntaxNode child in inserted.Node.DescendantNodesAndSelf())
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// <c>.shader</c> を解析してセマンティックモデルを作る。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="shaderLabTree">解析済みの ShaderLab 構文木。</param>
    /// <param name="options">実行時設定。</param>
    /// <returns>構築されたセマンティックモデル。</returns>
    /// <remarks>
    /// <b>このメソッドは入力がどれだけ壊れていても例外を投げない。</b>
    /// </remarks>
    public static ShaderCompilation Create(
        SourceText text,
        ShaderLabSyntaxTree shaderLabTree,
        SemanticsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(shaderLabTree);

        return ShaderCompilationBuilder.Build(
            text,
            shaderLabTree,
            ProgramBlockExtractor.Extract(shaderLabTree),
            ProgramBlockExtractor.ExtractAllCode(shaderLabTree),
            options);
    }

    /// <summary>
    /// HLSL 単体ファイル (<c>.compute</c> など) を解析してセマンティックモデルを作る。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="options">実行時設定。</param>
    /// <returns>構築されたセマンティックモデル。</returns>
    /// <remarks>
    /// <para>
    /// <b>ファイル全体を 1 つの <c>HLSLPROGRAM</c> として扱う。</b>
    /// 展開・構文解析・セマンティックモデル・HLSL のルール群は、
    /// 埋め込み HLSL とまったく同じ道を通る。
    /// </para>
    /// <para>
    /// <b>ShaderLab の構文木は、全部を空白でマスクしたテキストから作る。</b>
    /// セマンティックモデルは ShaderLab の木を持つ前提で組み立てられているので、
    /// 単体ファイル用に別の型を用意すると、その道だけ検査が抜ける穴ができる。
    /// 空のテキストではなく<b>同じ長さ</b>のテキストにしているのは、
    /// 長さが食い違うと位置を扱う処理が「たまたま通る」状態になるためである。
    /// </para>
    /// <para>
    /// <b>このメソッドは入力がどれだけ壊れていても例外を投げない。</b>
    /// </para>
    /// </remarks>
    public static ShaderCompilation CreateForHlsl(SourceText text, SemanticsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        options ??= SemanticsOptions.Default;

        SourceText blank = CreateBlankText(text);

        // カーネルは展開が始まる前に分かっていなければならない。
        // #pragma kernel が並べたマクロが、そのカーネルの展開そのものを変えるためである。
        ImmutableArray<HlslSyntaxToken> tokens = options.TokenCache is { } cache
            ? cache.GetOrLex(text, out _)
            : new HlslLexer(text).Lex(out _);

        return ShaderCompilationBuilder.Build(
            text,
            ShaderLabSyntaxTree.Parse(blank),
            ProgramBlockExtractor.ForStandaloneHlsl(text, ComputeKernels.Collect(tokens)),
            text,
            options,
            isStandaloneHlsl: true);
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


    /// <summary>
    /// このモデルを添えた解析単位を作る。
    /// </summary>
    /// <returns>アナライザに渡せる解析単位。</returns>
    /// <remarks>
    /// <b>セマンティックモデルから解析単位を作る経路はここに一本化する。</b>
    /// ShaderLab の構文診断と埋め込み HLSL の診断は、どちらもアナライザに属さない
    /// 解析基盤の報告である。呼び出し側がその都度組み立てると、
    /// 片方を渡し忘れた経路で「解析はしているのに報告されない指摘」が生まれる。
    /// </remarks>
    public AnalysisTarget CreateAnalysisTarget()
        => new AnalysisTarget(
                Text,
                ShaderLabTree.Root,
                [
                    // HLSL 単体ファイルの ShaderLab の木は、
                    // 全部を空白でマスクしたテキストから作ったダミーである。
                    // そこから出る構文診断はダミーについての話であって、
                    // 解析しているファイルについての事実ではない。
                    .. IsStandaloneHlsl ? [] : ShaderLabTree.Diagnostics,
                    .. ReportableHlslDiagnostics,
                ])
            .WithModel(this);

    /// <summary>
    /// 展開時に定義されていた関数形式マクロの名前を集める。
    /// </summary>
    /// <param name="programs">解析済みのコードブロック。</param>
    /// <returns>関数形式マクロの名前。</returns>
    /// <remarks>ブロックごとに定義が異なりうるため、すべてのブロック分を合わせる。</remarks>
    private static FrozenSet<string> BuildFunctionLikeMacroNames(ImmutableArray<AnalyzedProgram> programs)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in programs)
        {
            foreach ((string name, MacroDefinition macro) in program.Tree.PreprocessResult.Macros)
            {
                if (macro.IsFunctionLike)
                {
                    names.Add(name);
                }
            }
        }

        return names.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// 埋め込みコードの診断から、報告してよいものを選び出す。
    /// </summary>
    /// <param name="programs">解析済みのコードブロック。</param>
    /// <returns>元のファイルを指すよう付け替えた、重複の無い診断。</returns>
    /// <remarks>選別の理由は <see cref="ReportableHlslDiagnostics"/> を参照。</remarks>
    private ImmutableArray<Diagnostic> BuildReportableHlslDiagnostics(ImmutableArray<AnalyzedProgram> programs)
    {
        ImmutableArray<Diagnostic>.Builder reportable = ImmutableArray.CreateBuilder<Diagnostic>();
        HashSet<(string Id, string FilePath, TextSpan Span, string Message)> seen = [];

        foreach (AnalyzedProgram program in programs)
        {
            foreach (Diagnostic diagnostic in program.Diagnostics)
            {
                Location location = diagnostic.Location;
                bool own = string.Equals(location.FilePath, Text.FilePath, StringComparison.Ordinal);

                if (!own && !IsUserFile(location.FilePath))
                {
                    continue;
                }

                // マスクした複製は元のファイルと同じパス・同じ長さを持つ。
                // 長さの確認は、その前提が崩れたときに範囲外の位置を作らないための歯止めである。
                if (own && location.Span.End > Text.Length)
                {
                    continue;
                }

                if (!seen.Add((diagnostic.Id, location.FilePath, location.Span, diagnostic.GetMessage())))
                {
                    continue;
                }

                // 利用者のヘッダの指摘は、取り込んだヘッダのテキストをそのまま指している。
                reportable.Add(own ? diagnostic.WithLocation(Location.Create(Text, location.Span)) : diagnostic);
            }
        }

        return reportable.ToImmutable();
    }

}
