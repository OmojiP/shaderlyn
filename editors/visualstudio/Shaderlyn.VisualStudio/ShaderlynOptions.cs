using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Shaderlyn.VisualStudio;

/// <summary>
/// 設定のページ (<see cref="ShaderlynOptionsPage"/>) の値を、言語サーバーのクライアントに渡す。
/// </summary>
internal static class ShaderlynOptions
{
    /// <summary>設定が変更されたとき (OK か適用を押したとき) に起きる。</summary>
    public static event EventHandler? Changed;

    /// <summary>設定の言語サーバーの実行ファイルを読む。未設定か、読めない場合は <see langword="null"/>。</summary>
    /// <returns>実行ファイルのパス。</returns>
    public static async Task<string?> GetServerPathAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsShell)) is not IVsShell shell)
        {
            return null;
        }

        Guid guid = new(ShaderlynPackage.PackageGuid);

        if (ErrorHandler.Failed(shell.LoadPackage(ref guid, out IVsPackage loaded)) || loaded is not ShaderlynPackage package)
        {
            return null;
        }

        string path = ((ShaderlynOptionsPage)package.GetDialogPage(typeof(ShaderlynOptionsPage))).ServerPath;
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
    }

    /// <summary>設定が変更されたことを知らせる。</summary>
    public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);
}
