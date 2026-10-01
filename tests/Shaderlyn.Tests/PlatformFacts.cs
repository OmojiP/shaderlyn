namespace Shaderlyn.Tests;

/// <summary>
/// Windows でだけ実行するテスト。
/// </summary>
/// <remarks>
/// <para>
/// <b>ドライブ文字にまつわる検証は、Windows でしか意味を持たない。</b>
/// <c>c:\Users\...</c> は Unix では相対パスとして扱われ、
/// <see cref="Path.GetFullPath(string)"/> が現在のフォルダーを前に付ける。
/// 検証したい挙動とは無関係な理由で落ちる。
/// </para>
/// <para>
/// <b>何も伝えずに通すのではなく、飛ばしたことを出す。</b>
/// 中で早期 return すると「通った」と表示され、
/// その OS で何も確かめていないことが見えなくなる。
/// </para>
/// </remarks>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    /// <summary>属性を生成する。</summary>
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows のドライブ文字を前提とするため、この OS では確かめられません。";
        }
    }
}

/// <summary>
/// Windows でだけ実行する、引数つきのテスト。
/// </summary>
/// <remarks><see cref="WindowsOnlyFactAttribute"/> と同じ理由による。</remarks>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    /// <summary>属性を生成する。</summary>
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows のドライブ文字を前提とするため、この OS では確かめられません。";
        }
    }
}

/// <summary>
/// Windows 以外でだけ実行するテスト。
/// </summary>
/// <remarks>
/// <b>Windows 側だけを直すと、Unix 側の経路が誰にも試されなくなる。</b>
/// 言語サーバは Linux と macOS でも動く。
/// ドライブ文字を持たないパスに対する変換を、こちらで確かめる。
/// </remarks>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    /// <summary>属性を生成する。</summary>
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "ドライブ文字を持たないパスを前提とするため、Windows では確かめられません。";
        }
    }
}
