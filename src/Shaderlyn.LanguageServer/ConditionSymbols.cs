using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 利用者が選べる、条件のシンボル。
/// </summary>
/// <param name="Name">シンボルの名前。値を持つシンボルでは、値ごとの原子 (<c>QUALITY==2</c>)。</param>
/// <param name="Declared">
/// 構成の軸として宣言されたシンボルかどうか。Unity なら <c>#pragma multi_compile</c> など、
/// ほかの環境なら設定ファイルの <c>symbols:</c> で宣言したもの。
/// </param>
/// <param name="Groups">
/// 属している、同時には有効にならないシンボルの組 (Unity なら同じ <c>#pragma</c> の行)。組に入っていなければ空。
/// 1 つのシンボルが複数の組に入ることがある (<c>at-most-one: [A, B]</c> と <c>at-most-one: [B, C]</c> の <c>B</c>)。
/// </param>
/// <param name="Variable">値を持つシンボルの名前 (<c>QUALITY</c>)。値を持たなければ <see langword="null"/>。</param>
/// <param name="Value">値を持つシンボルの値。値を持たなければ <see langword="null"/>。</param>
/// <remarks>
/// <b>同じ組のシンボルは同時に有効にならない。</b>
/// 選ばせる画面は、組ごとに「組では 1 つだけ」「外せない組がある」を守る。
/// 守らないと、実在しない構成を選べてしまう。値を持つシンボルは、値を 1 つ選ぶ組になる。
/// </remarks>
internal readonly record struct ConditionSymbol(
    string Name,
    bool Declared,
    ImmutableArray<ConditionGroup> Groups = default,
    string? Variable = null,
    int? Value = null)
{
    /// <summary>最初の組の番号。組に入っていなければ -1。</summary>
    /// <remarks>組を 1 つしか知らない古い拡張のために返す。</remarks>
    public int Group => Groups.IsDefaultOrEmpty ? -1 : Groups[0].Id;

    /// <summary>最初の組のどれか 1 つが必ず有効かどうか。</summary>
    public bool RequiresOne => !Groups.IsDefaultOrEmpty && Groups[0].RequiresOne;
}

/// <summary>
/// 同時には有効にならないシンボルの組。
/// </summary>
/// <param name="Id">組の番号。</param>
/// <param name="RequiresOne">
/// 組のどれか 1 つが必ず有効かどうか。<c>_</c> の無い <c>multi_compile</c>、設定ファイルの <c>one-of</c>、値を持つシンボルが該当する。
/// </param>
internal readonly record struct ConditionGroup(int Id, bool RequiresOne);

/// <summary>
/// 条件で参照されているシンボルのうち、利用者が選んで切り替えられるものを集める。
/// </summary>
/// <remarks>
/// <para>
/// 候補は 2 種類ある。
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>どこでも定義されていないシンボル。</b>
///     断片の <c>#ifdef _FOO</c> の <c>_FOO</c> は取り込む側の <c>.shader</c> が定義するので、
///     単体で解析するとその分岐の中は一度も検査されない。選ぶと <c>#define 名前 1</c> として解析し直す。
///   </description></item>
///   <item><description>
///     <b>構成の軸として宣言されたシンボル。</b>
///     解析はすべてのバリアントを合わせて見るので、シンボルで分かれる分岐はどれも効いているように見える。
///     選ぶと、そのシンボルを有効にした構成で「効いていない範囲」を表示する。
///   </description></item>
/// </list>
/// <para>
/// 宣言は環境が読んだ構成の空間 (<see cref="AnalyzedProgram.Variants"/>) から引く。Unity の <c>#pragma</c> も、
/// 設定ファイルの <c>symbols:</c> も同じ形になる。
/// </para>
/// <para>
/// <b>定義を足していないセマンティックモデルから集めること。</b>
/// 利用者が選んだシンボルを定義済みにしたモデルでは、そのシンボルが「定義されている」側に入り、
/// 候補から消えてしまう。
/// </para>
/// </remarks>
internal static class ConditionSymbols
{
    /// <summary>候補を集める。</summary>
    /// <param name="compilation">定義を足していないセマンティックモデル。</param>
    /// <returns>名前順に並べたシンボル。</returns>
    public static ImmutableArray<ConditionSymbol> Collect(ShaderCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        ImmutableArray<AnalyzedProgram> programs = [.. compilation.Programs, .. compilation.SymbolVariants];
        HashSet<string> declared = new(StringComparer.Ordinal);
        Dictionary<string, ImmutableArray<int>> valueSymbols = new(StringComparer.Ordinal);

        // 同時には有効にならない組を集める。画面はこの単位で「1 つだけ」「外せない」を守る。
        // 同じ組 (同じ #pragma の行) はブロックごとに何度も現れるので、並びと RequiresOne が同じなら 1 つに寄せる。
        // 重なる別の組 ([A, B] と [B, C]) は寄せない。寄せると、どちらの組にも入っていない A と C まで同時に選べなくなる。
        List<VariantGroup> distinctGroups = [];
        Dictionary<string, List<ConditionGroup>> groups = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in programs)
        {
            declared.UnionWith(program.Variants.Symbols);

            foreach ((string name, ImmutableArray<int> values) in program.Variants.ValueSymbols)
            {
                valueSymbols.TryAdd(name, values);
            }

            foreach (VariantGroup group in program.Variants.Groups)
            {
                if (distinctGroups.Contains(group))
                {
                    continue;
                }

                ConditionGroup entry = new(distinctGroups.Count, group.RequiresOne);
                distinctGroups.Add(group);

                foreach (string symbol in group.Symbols)
                {
                    if (!groups.TryGetValue(symbol, out List<ConditionGroup>? memberships))
                    {
                        memberships = [];
                        groups[symbol] = memberships;
                    }

                    memberships.Add(entry);
                }
            }
        }

        SortedSet<string> symbols = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in programs)
        {
            PreprocessResult result = program.Tree.PreprocessResult;

            foreach (ShaderSyntaxToken token in result.ConditionalIdentifiers)
            {
                // このファイルに書かれた条件だけを見る。
                // 取り込んだヘッダの条件を並べても、利用者のファイルのコードは変わらない。
                if (token.IsFromMacroExpansion || !compilation.IsWrittenHere(token))
                {
                    continue;
                }

                // 値を持つシンボルは、値ごとの原子を並べる。画面は値を 1 つ選ばせる。
                if (valueSymbols.TryGetValue(token.Text, out ImmutableArray<int> values))
                {
                    symbols.UnionWith(values.Select(value => VariantValues.Atom(token.Text, value)));
                    continue;
                }

                // シンボルのバリアントでは、そのシンボルがマクロとして定義されている。
                // シンボルは定義の有無にかかわらず、切り替えられるものとして候補に残す。
                if (declared.Contains(token.Text) || !result.Macros.ContainsKey(token.Text))
                {
                    symbols.Add(token.Text);
                }
            }
        }

        return [.. symbols.Select(name => Create(name, declared, groups))];
    }

    /// <summary>
    /// 選んだシンボルから、構成の軸として宣言されたシンボルを除く。
    /// </summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="symbols">利用者が選んだシンボル。</param>
    /// <param name="environment">宣言を読む環境。<see langword="null"/> なら何も除かない。</param>
    /// <returns>指摘を出す解析で定義済みにするシンボル。</returns>
    /// <remarks>
    /// <para>
    /// <b>シンボルは指摘を出す解析では定義しない。</b>
    /// シンボルの分岐はバリアントとして展開済みで、定義を足しても検査される範囲は増えない。
    /// 足すと、バリアントごとに別のシンボルも有効になり、
    /// <c>multi_compile _ A B</c> の A と B が同時に定義された、ありえない構成を検査することになる。
    /// </para>
    /// <para>
    /// 宣言は展開前のトークンから拾う (環境の <see cref="IShaderEnvironment.ReadDeclaredVariants"/>)。
    /// ここでセマンティックモデルを組むと、保存のたびに解析が 1 回増える。
    /// </para>
    /// </remarks>
    public static ImmutableArray<string> ExcludeDeclaredSymbols(
        SourceText text,
        ImmutableArray<string> symbols,
        IShaderEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (symbols.IsDefaultOrEmpty)
        {
            return [];
        }

        if (environment is null)
        {
            return symbols;
        }

        VariantSpace declared = environment.ReadDeclaredVariants(
            HlslEnvironment.WholeFile(text), new ShaderLexer(text).Lex(out _));

        return [.. symbols.Where(symbol => !declared.IsDeclared(symbol))];
    }

    /// <summary>候補を 1 つ作る。</summary>
    /// <param name="name">名前。</param>
    /// <param name="declared">宣言されたシンボル。</param>
    /// <param name="groups">シンボルごとの、属している組。</param>
    /// <returns>候補。</returns>
    private static ConditionSymbol Create(
        string name,
        HashSet<string> declared,
        Dictionary<string, List<ConditionGroup>> groups)
    {
        ImmutableArray<ConditionGroup> memberships = groups.TryGetValue(name, out List<ConditionGroup>? found) ? [.. found] : [];

        return VariantValues.TryParseAtom(name, out string? variable, out int value)
            ? new ConditionSymbol(name, declared.Contains(name), memberships, variable, value)
            : new ConditionSymbol(name, declared.Contains(name), memberships);
    }
}
