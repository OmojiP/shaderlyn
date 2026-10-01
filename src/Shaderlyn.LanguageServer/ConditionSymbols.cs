using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 利用者が選べる、条件のシンボル。
/// </summary>
/// <param name="Name">シンボルの名前。</param>
/// <param name="DeclaredByPragma">
/// <c>#pragma multi_compile</c> などでこのファイルが宣言したシンボルかどうか。
/// </param>
/// <param name="Group">
/// 同じ <c>#pragma</c> の行で宣言されたシンボルをまとめる番号。宣言されていない場合は -1。
/// </param>
/// <param name="RequiresOne">
/// その行のどれか 1 つが必ず有効かどうか。<c>_</c> の無い <c>multi_compile</c> が該当する。
/// </param>
/// <remarks>
/// <b>同じ行のシンボルは同時に有効にならない。</b>
/// 選ばせる画面は、この 2 つを見て「同じ行では 1 つだけ」「外せない行がある」を守る。
/// 守らないと、実在しない構成を選べてしまう。
/// </remarks>
internal readonly record struct ConditionSymbol(
    string Name,
    bool DeclaredByPragma,
    int Group = -1,
    bool RequiresOne = false);

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
///     <b>このファイルが宣言したシンボル。</b>
///     解析はすべてのバリアントを合わせて見るので、シンボルで分かれる分岐はどれも効いているように見える。
///     選ぶと、そのシンボルを有効にした構成で「効いていない範囲」を表示する。
///   </description></item>
/// </list>
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

        // 同じ #pragma の行で宣言されたものをまとめる。
        // 画面はこの単位で「1 つだけ」「外せない」を守る。
        Dictionary<string, (int Group, bool RequiresOne)> groups = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in programs)
        {
            declared.UnionWith(ShaderSymbols.CollectDeclared(program.Tree.PreprocessResult.Pragmas));

            foreach (SymbolGroup group in ShaderSymbols.CollectGroups(program.Tree.PreprocessResult.Pragmas))
            {
                // 同じ行はブロックごとに何度も現れる。先頭の名前で 1 つに寄せる。
                int index = groups.TryGetValue(group.Symbols[0], out (int Group, bool RequiresOne) known)
                    ? known.Group
                    : groups.Count == 0 ? 0 : groups.Values.Max(v => v.Group) + 1;

                foreach (string symbol in group.Symbols)
                {
                    groups[symbol] = (index, group.RequiresOne);
                }
            }
        }

        SortedSet<string> symbols = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in programs)
        {
            PreprocessResult result = program.Tree.PreprocessResult;

            foreach (HlslSyntaxToken token in result.ConditionalIdentifiers)
            {
                // このファイルに書かれた条件だけを見る。
                // 取り込んだヘッダの条件を並べても、利用者のファイルのコードは変わらない。
                if (token.IsFromMacroExpansion || !compilation.IsWrittenHere(token))
                {
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

        return
        [
            .. symbols.Select(name => groups.TryGetValue(name, out (int Group, bool RequiresOne) group)
                ? new ConditionSymbol(name, declared.Contains(name), group.Group, group.RequiresOne)
                : new ConditionSymbol(name, declared.Contains(name))),
        ];
    }

    /// <summary>
    /// 選んだシンボルから、このファイルが宣言したシンボルを除く。
    /// </summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="symbols">利用者が選んだシンボル。</param>
    /// <returns>指摘を出す解析で定義済みにするシンボル。</returns>
    /// <remarks>
    /// <para>
    /// <b>シンボルは指摘を出す解析では定義しない。</b>
    /// シンボルの分岐はバリアントとして展開済みで、定義を足しても検査される範囲は増えない。
    /// 足すと、バリアントごとに別のシンボルも有効になり、
    /// <c>multi_compile _ A B</c> の A と B が同時に定義された、ありえない構成を検査することになる。
    /// </para>
    /// <para>
    /// 宣言は展開前のトークンから拾う。ここでセマンティックモデルを組むと、保存のたびに解析が 1 回増える。
    /// </para>
    /// </remarks>
    public static ImmutableArray<string> ExcludeDeclaredSymbols(SourceText text, ImmutableArray<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (symbols.IsDefaultOrEmpty)
        {
            return [];
        }

        HashSet<string> declared = ShaderSymbols.CollectDeclaredFromTokens(new HlslLexer(text).Lex(out _));

        return [.. symbols.Where(symbol => !declared.Contains(symbol))];
    }
}
