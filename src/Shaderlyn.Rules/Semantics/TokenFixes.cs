using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Rules;

/// <summary>
/// トークンの位置から直し (<see cref="DiagnosticFix"/>) を作り、指摘に添える。
/// </summary>
/// <remarks>
/// <para>
/// <b>書き手が書いたトークンにだけ直しを作る。</b>
/// マクロの展開から来たトークンは、位置がマクロの使用箇所か定義を指すので、そこを書き換えると別のコードが壊れる。
/// 足りないトークン (<see cref="ShaderSyntaxToken.IsMissing"/>) は、位置が決まらない。
/// </para>
/// <para>
/// <b>直しは指摘と同じファイルの中だけを書き換える。</b>
/// 指摘がヘッダの中にあり、直す位置が別のファイルにある場合は、直しを添えない。
/// </para>
/// </remarks>
internal static class TokenFixes
{
    /// <summary>書き換えてよいトークンか。</summary>
    /// <param name="token">調べるトークン。</param>
    /// <returns>書き手が書いた、位置の決まるトークンなら <see langword="true"/>。</returns>
    public static bool CanEdit(ShaderSyntaxToken? token) => token is { IsMissing: false, IsFromMacroExpansion: false };

    /// <summary>トークンの前に文字列を差し込む直しを作る。</summary>
    /// <param name="token">差し込む位置のトークン。</param>
    /// <param name="text">差し込む文字列。</param>
    /// <param name="title">操作の名前。</param>
    /// <returns>作った直し。トークンを書き換えられなければ <see langword="null"/>。</returns>
    public static TokenFix? InsertBefore(ShaderSyntaxToken? token, string text, string title)
        => CanEdit(token) ? new TokenFix(token!.Source, DiagnosticFix.Insert(title, token.Span.Start, text)) : null;

    /// <summary>トークンの後ろに文字列を差し込む直しを作る。</summary>
    /// <param name="token">差し込む位置のトークン。</param>
    /// <param name="text">差し込む文字列。</param>
    /// <param name="title">操作の名前。</param>
    /// <returns>作った直し。トークンを書き換えられなければ <see langword="null"/>。</returns>
    public static TokenFix? InsertAfter(ShaderSyntaxToken? token, string text, string title)
        => CanEdit(token) ? new TokenFix(token!.Source, DiagnosticFix.Insert(title, token.Span.End, text)) : null;

    /// <summary>トークンを文字列で置き換える直しを作る。</summary>
    /// <param name="token">置き換えるトークン。</param>
    /// <param name="text">置き換える文字列。</param>
    /// <param name="title">操作の名前。</param>
    /// <returns>作った直し。トークンを書き換えられなければ <see langword="null"/>。</returns>
    public static TokenFix? Replace(ShaderSyntaxToken? token, string text, string title)
        => CanEdit(token) ? new TokenFix(token!.Source, DiagnosticFix.Replace(title, token.Span, text)) : null;

    /// <summary>直しを指摘に添える。直す位置が指摘と別のファイルなら添えない。</summary>
    /// <param name="diagnostic">指摘。</param>
    /// <param name="fix">直し。<see langword="null"/> なら何もしない。</param>
    /// <returns>直しを添えた指摘。</returns>
    public static Diagnostic WithFix(this Diagnostic diagnostic, TokenFix? fix)
        => fix is { } f && ReferenceEquals(f.Source, diagnostic.Location.Source) ? diagnostic.WithFix(f.Fix) : diagnostic;
}

/// <summary>トークンの位置から作った直しと、その位置のファイル。</summary>
/// <param name="Source">直す位置のファイル。</param>
/// <param name="Fix">直し。</param>
internal sealed record TokenFix(SourceText Source, DiagnosticFix Fix);
