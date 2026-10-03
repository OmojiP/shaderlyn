using System.Collections.Immutable;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 指令の行の補完。
/// </summary>
/// <remarks>
/// <b>指令の行で書けるのは、指令の名前・<c>#pragma</c> の名前・条件のシンボルとマクロだけである。</b>
/// ここで組み込み関数や型を出すと、書けない名前を選ばせることになる。
/// </remarks>
internal static partial class CompletionBuilder
{
    /// <summary>LSP の CompletionItemKind (定数)。シンボルとマクロに使う。</summary>
    private const int KindConstant = 21;

    /// <summary>指令の名前と、その説明。</summary>
    private static readonly (string Name, string Description)[] Directives =
    [
        ("define", "マクロを定義する。"),
        ("undef", "マクロの定義を取り消す。"),
        ("include", "ファイルを取り込む。"),
        ("if", "条件が成り立つときだけ、続くコードを使う。"),
        ("ifdef", "マクロかシンボルが定義されているときだけ、続くコードを使う。"),
        ("ifndef", "マクロかシンボルが定義されていないときだけ、続くコードを使う。"),
        ("elif", "前の条件が成り立たず、この条件が成り立つときに使う。"),
        ("else", "前の条件がどれも成り立たないときに使う。"),
        ("endif", "#if / #ifdef / #ifndef を閉じる。"),
        ("pragma", "コンパイラと Unity への指示 (エントリポイント、シンボルの宣言など)。"),
        ("error", "この行に来たらコンパイルを失敗させる。"),
        ("line", "以降の行番号とファイル名を付け替える。"),
    ];

    /// <summary>指令の名前を候補にする。</summary>
    /// <returns>候補。</returns>
    private static ImmutableArray<CompletionItem> BuildDirectives()
        => [.. Directives.Select(d => new CompletionItem(d.Name, KindKeyword, "指令", d.Description))];

    /// <summary><c>#pragma</c> の名前を候補にする。</summary>
    /// <returns>候補。</returns>
    /// <remarks>
    /// シンボルを宣言する <c>#pragma</c> は、<c>_local</c> と段階の接尾辞を付けた形も並べる。
    /// 説明はホバーと同じもの (<see cref="HoverBuilder.PragmaDescriptions"/>) を使う。
    /// </remarks>
    private static ImmutableArray<CompletionItem> BuildPragmas()
    {
        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);

        foreach ((string name, string description) in HoverBuilder.PragmaDescriptions)
        {
            items.TryAdd(name, new CompletionItem(name, KindKeyword, "#pragma", description));
        }

        foreach ((string prefix, _) in HoverBuilder.KeywordPragmaDescriptions)
        {
            foreach (string local in (string[])["", "_local"])
            {
                foreach (string stage in HoverBuilder.PragmaStageSuffixes.Keys.Prepend(""))
                {
                    string name = prefix + local + stage;
                    items.TryAdd(name, new CompletionItem(name, KindKeyword, "#pragma", HoverBuilder.DescribeKeywordPragma(name)));
                }
            }
        }

        return [.. items.Values.OrderBy(i => i.Label, StringComparer.Ordinal)];
    }

    /// <summary><c>#pragma vertex</c> などに続ける関数の名前を候補にする。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>このファイルに書いた関数。</returns>
    private static ImmutableArray<CompletionItem> BuildEntryPoints(ShaderCompilation compilation)
    {
        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                if (declaration is FunctionDeclarationSyntax function && compilation.IsWrittenHere(function))
                {
                    items.TryAdd(function.Name, new CompletionItem(function.Name, KindFunction, DescribeSignature(function)));
                }
            }
        }

        return [.. items.Values.OrderBy(i => i.Label, StringComparer.Ordinal)];
    }

    /// <summary>条件に書ける名前を候補にする。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="includeDefined"><c>defined</c> も出すなら <see langword="true"/> (<c>#if</c> / <c>#elif</c>)。</param>
    /// <returns>
    /// このファイルが <c>#pragma</c> で宣言したシンボル、条件で参照している名前、
    /// 定義されているマクロの順に重複を除いたもの。
    /// </returns>
    private static ImmutableArray<CompletionItem> BuildConditionNames(ShaderCompilation compilation, bool includeDefined)
    {
        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);

        if (includeDefined)
        {
            items["defined"] = new CompletionItem("defined", KindKeyword, "演算子", "名前が定義されていれば 1、されていなければ 0 になる。");
        }

        // 宣言したばかりで、まだどの条件でも使っていないシンボルも書ける。
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (string symbol in ShaderSymbols.CollectDeclared(program.Tree.PreprocessResult.Pragmas))
            {
                items.TryAdd(symbol, new CompletionItem(symbol, KindConstant, "シェーダーのシンボル"));
            }
        }

        foreach (ConditionSymbol symbol in ConditionSymbols.Collect(compilation))
        {
            items.TryAdd(symbol.Name, new CompletionItem(
                symbol.Name,
                KindConstant,
                symbol.DeclaredByPragma ? "シェーダーのシンボル" : "条件で参照している名前"));
        }

        AddMacros(compilation, items);

        return [.. items.Values.OrderBy(i => i.Label, StringComparer.Ordinal)];
    }

    /// <summary>定義されているマクロの名前を候補にする。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>候補。</returns>
    private static ImmutableArray<CompletionItem> BuildMacroNames(ShaderCompilation compilation)
    {
        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);
        AddMacros(compilation, items);
        return [.. items.Values.OrderBy(i => i.Label, StringComparer.Ordinal)];
    }

    /// <summary>展開の終わりに定義されているマクロを足す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="items">足す先。先に入っている名前は残す。</param>
    /// <remarks>
    /// 取り込んだヘッダのマクロも含める。<c>#ifdef UNITY_REVERSED_Z</c> のように、ヘッダのマクロで分けるのは普通の書き方である。
    /// </remarks>
    private static void AddMacros(ShaderCompilation compilation, Dictionary<string, CompletionItem> items)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (string name in program.Tree.PreprocessResult.Macros.Keys)
            {
                items.TryAdd(name, new CompletionItem(name, KindConstant, "マクロ"));
            }
        }
    }
}
