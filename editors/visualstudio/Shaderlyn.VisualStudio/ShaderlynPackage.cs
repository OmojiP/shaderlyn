using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Shaderlyn.VisualStudio;

/// <summary>
/// Shaderlyn の設定のページ (ツール &gt; オプション &gt; Shaderlyn) を登録するパッケージ。
/// </summary>
/// <remarks>
/// 言語サーバーのクライアント (<see cref="ShaderlynLanguageClientBase"/>) は MEF で読み込まれ、このパッケージに依らない。
/// 設定を読むときに、このパッケージを読み込む (<see cref="ShaderlynOptions"/>)。
/// </remarks>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideBindingPath] // 拡張機能のフォルダーからこのアセンブリを探させる。ないとパッケージを作れない。
[Guid(PackageGuid)]
[ProvideOptionPage(typeof(ShaderlynOptionsPage), "Shaderlyn", "全般", 0, 0, supportsAutomation: true)]
public sealed class ShaderlynPackage : AsyncPackage
{
    /// <summary>パッケージの GUID。</summary>
    public const string PackageGuid = "7d6f3c1e-4b8a-4f2e-9c5d-2a1b8e6f0c47";
}

/// <summary>
/// Shaderlyn の設定のページ。
/// </summary>
/// <remarks>
/// 設定は Visual Studio のユーザーごとの設定に保存され、リポジトリのファイルには書かない。
/// </remarks>
[Guid("c3a9e2b4-6d1f-4e7a-8b5c-9f0d2e4a6b18")]
public sealed class ShaderlynOptionsPage : DialogPage
{
    /// <summary>
    /// 起動する言語サーバーの実行ファイル。空の場合は、拡張機能に同梱したものを使う。
    /// </summary>
    [Category("言語サーバー")]
    [DisplayName("実行ファイル")]
    [Description("起動する言語サーバー (shaderlyn-lsp.exe) のパス。空の場合は、拡張機能に同梱したものを使います。変更すると言語サーバーを起動し直します。")]
    public string ServerPath { get; set; } = string.Empty;

    /// <inheritdoc/>
    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);

        if (e.ApplyBehavior == ApplyKind.Apply)
        {
            ShaderlynOptions.NotifyChanged();
        }
    }
}
