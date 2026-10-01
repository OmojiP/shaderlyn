using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 定義の位置 1 つ分。
/// </summary>
/// <param name="FilePath">定義があるファイル。</param>
/// <param name="Range">定義の範囲。</param>
internal readonly record struct DefinitionTarget(string FilePath, LinePositionSpan Range);

/// <summary>
/// カーソルの下の名前の定義を探す。
/// </summary>
/// <remarks>
/// <para>
/// <b>取り込んだヘッダの中も対象にする。</b>
/// Unity のシェーダーはマクロと関数のほとんどを URP のヘッダから受け取っており、
/// このファイルの中しか探さないのでは、ほとんどの名前で飛べないことになる。
/// </para>
/// <para>
/// <b>プロパティと uniform の間も繋ぐ。</b>
/// この 2 つは別の言語で別々に書かれており、
/// 対応が取れているかを目で追うのが最も面倒な部分である。
/// </para>
/// </remarks>
internal static partial class DefinitionBuilder
{
    /// <summary>
    /// 指定した位置にある名前の定義を探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>見つかった定義。無い場合は空。</returns>
    public static IReadOnlyList<DefinitionTarget> Build(ShaderCompilation compilation, int offset)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        // #include は名前ではなくパスを指しているので、識別子を探す前に見る。
        if (FindInclude(compilation, offset) is { } include)
        {
            return OnlyOpenableFiles(compilation, [include]);
        }

        // 構造体のフィールドは左側の型で決まるため、名前だけの探索より先に見る。
        if (FindShaderLabProperty(compilation, offset) is { } fromProperty)
        {
            return OnlyOpenableFiles(compilation, [fromProperty]);
        }

        if (CursorTarget.FindIdentifier(compilation, offset) is not { } name)
        {
            return [];
        }

        List<DefinitionTarget> targets = [];

        Add(targets, FindStructField(compilation, offset));
        Add(targets, FindLocal(compilation, name));
        Add(targets, FindUniform(compilation, name));
        AddRange(targets, FindFunctionsAndStructs(compilation, name.Text));
        Add(targets, FindMacro(compilation, name.Text));
        Add(targets, FindProperty(compilation, name.Text));

        return OnlyOpenableFiles(compilation, targets);
    }

    /// <summary>
    /// エディタが開ける先を指す定義だけを残す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="targets">絞り込む定義。</param>
    /// <returns>開ける先を指すもの。</returns>
    /// <remarks>
    /// <para>
    /// <b>解析の中には、ファイルに対応しない出所がある。</b>
    /// 定義済みマクロは <c>&lt;定義済みマクロ&gt;</c>、
    /// トークン連結や構文エラーからの復元は <c>&lt;トークン連結&gt;</c> や <c>&lt;合成&gt;</c> を
    /// 出所として持つ。これらはファイル名ではない。
    /// </para>
    /// <para>
    /// そのまま返すと、エディタは作業フォルダーからの相対パスとして解釈し、
    /// 「ファイルが見つからなかったため、エディターを開くことができませんでした」
    /// と書かれたタブを開く。
    /// <b>飛べない先を返すくらいなら、何も返さないほうがよい。</b>
    /// 何も返さなければ、エディタは「定義が見つかりません」と正しく言う。
    /// </para>
    /// <para>
    /// <b>解析中のファイル自身は、実在を確かめない。</b>
    /// エディタが開いている以上、そこへは必ず飛べる。
    /// まだ保存していない新規ファイルはディスクに無いが、
    /// そこで自分の関数へ飛べなくなるのは明らかにおかしい。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<DefinitionTarget> OnlyOpenableFiles(
        ShaderCompilation compilation,
        List<DefinitionTarget> targets)
    {
        targets.RemoveAll(t =>
            !string.Equals(t.FilePath, compilation.Text.FilePath, StringComparison.Ordinal)
            && !IsRealFile(t.FilePath));

        return targets;
    }

    /// <summary>パスが実在するファイルを指しているかを判定する。</summary>
    /// <param name="filePath">判定するパス。</param>
    /// <returns>実在するファイルであれば <see langword="true"/>。</returns>
    private static bool IsRealFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || filePath.StartsWith('<'))
        {
            return false;
        }

        try
        {
            return File.Exists(filePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 見に行けないものは、飛べるとは言えない。
            return false;
        }
    }

    /// <summary>見つかった定義を、重複を避けて加える。</summary>
    /// <param name="targets">加える先。</param>
    /// <param name="target">加える定義。</param>
    private static void Add(List<DefinitionTarget> targets, DefinitionTarget? target)
    {
        if (target is { } found && !targets.Contains(found))
        {
            targets.Add(found);
        }
    }

    /// <summary>見つかった定義をまとめて、重複を避けて加える。</summary>
    /// <param name="targets">加える先。</param>
    /// <param name="found">加える定義。</param>
    private static void AddRange(List<DefinitionTarget> targets, IEnumerable<DefinitionTarget> found)
    {
        foreach (DefinitionTarget target in found)
        {
            Add(targets, target);
        }
    }

    /// <summary>位置を定義として表す。</summary>
    /// <param name="location">対象の位置。</param>
    /// <returns>定義。</returns>
    private static DefinitionTarget ToTarget(Core.Diagnostics.Location location)
        => new(location.FilePath, location.LineSpan);

    /// <summary>位置を定義として表す。</summary>
    /// <param name="token">対象のトークン。</param>
    /// <returns>定義。</returns>
    private static DefinitionTarget ToTarget(HlslSyntaxToken token)
        => new(token.Source.FilePath, token.Source.GetLinePositionSpan(token.Span));
}
