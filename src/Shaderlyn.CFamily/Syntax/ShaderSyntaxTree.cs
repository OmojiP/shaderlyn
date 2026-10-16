using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.CFamily.Syntax;

/// <summary>
/// 解析済みのコード。構文木、<c>#pragma</c>、診断と、読んだ言語の設定を保持する。
/// </summary>
/// <remarks>
/// 言語ごとの入口 (HLSL なら <c>HlslSyntaxTree.Parse</c>) が、プリプロセスと構文解析を順に行って作る。
/// 順序を誤ると (プリプロセスを飛ばして構文解析するなど)
/// マクロが展開されないまま解析され、実用的なシェーダーが軒並み壊れる。
/// </remarks>
public sealed class ShaderSyntaxTree
{
    /// <summary>解析済みのコードを作る。</summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="root">構文木の根。</param>
    /// <param name="preprocessResult">プリプロセスの結果。</param>
    /// <param name="diagnostics">プリプロセスと構文解析で検出した診断。</param>
    /// <param name="language">読んだ言語の設定。</param>
    /// <param name="unanalyzedIdentifiers">構文木にせずに読み飛ばした宣言に現れた識別子。</param>
    internal ShaderSyntaxTree(
        SourceText text,
        CompilationUnitSyntax root,
        PreprocessResult preprocessResult,
        ImmutableArray<Diagnostic> diagnostics,
        ShaderLanguageOptions language,
        ImmutableArray<ShaderSyntaxToken> unanalyzedIdentifiers)
    {
        Text = text;
        Root = root;
        PreprocessResult = preprocessResult;
        Diagnostics = diagnostics;
        Language = language;
        UnanalyzedIdentifiers = unanalyzedIdentifiers;
    }

    /// <summary>解析対象のソーステキスト。</summary>
    public SourceText Text { get; }

    /// <summary>構文木の根。</summary>
    public CompilationUnitSyntax Root { get; }

    /// <summary>プリプロセスの結果。<c>#pragma</c> や include の情報を含む。</summary>
    public PreprocessResult PreprocessResult { get; }

    /// <summary>プリプロセスと構文解析で検出した診断。</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>現れた <c>#pragma</c> 指令。</summary>
    public ImmutableArray<PragmaDirective> Pragmas => PreprocessResult.Pragmas;

    /// <summary>
    /// この木を読んだ言語の設定 (<see cref="PreprocessorOptions.Language"/>)。
    /// </summary>
    /// <remarks>
    /// 言語に固有の値 (HLSL の方言やコンパイル先) は、ここから読む (HLSL なら <c>tree.Hlsl()</c>)。
    /// </remarks>
    public ShaderLanguageOptions Language { get; }

    /// <summary>
    /// 構文木にせずに読み飛ばした宣言に現れた識別子 (<see cref="Parsing.ShaderParser.UnanalyzedIdentifiers"/>)。
    /// </summary>
    /// <remarks>
    /// 受け付ける構文だが、まだ構文木にしない宣言 (HLSL 2021 の <c>template</c> の明示的な特殊化など) の名前である。
    /// 解析していない場所に現れた名前として扱うこと。
    /// </remarks>
    public ImmutableArray<ShaderSyntaxToken> UnanalyzedIdentifiers { get; }
}
