using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;

namespace Shaderlyn.Hlsl.Parsing;

/// <summary>
/// コードのコンパイル先 1 つ。どのグラフィックス API 向けに、どのコンパイラと HLSL のバージョンでコンパイルされるか。
/// </summary>
/// <param name="Platform">グラフィックス API の名前 (<c>dx11</c> / <c>dx12</c> / <c>vulkan</c> など)。</param>
/// <param name="Dialect">コンパイラと HLSL のバージョン。</param>
/// <remarks>
/// <para>
/// <b>1 つのコードは、API ごとに別のコンパイラや HLSL のバージョンでコンパイルされることがある。</b>
/// Unity は既定で FXC を使い、<c>#pragma use_dxc</c> や <c>#pragma require</c> で DXC に切り替える。
/// 切り替わるのは API ごとで、DX11 は FXC のまま、DX12 は DXC ということが起きる。
/// </para>
/// <para>
/// 構成のキーワードでコンパイラの設定が変わることもある。Unity の <c>UNITY_DEVICE_SUPPORTS_NATIVE_16BIT</c> は、
/// キーワードが有効な構成だけを DXC でコンパイルする。<see cref="WhenAnySymbol"/> / <see cref="UnlessAnySymbol"/> がその条件である。
/// </para>
/// </remarks>
public sealed record HlslCompileTarget(string Platform, HlslDialect Dialect)
{
    /// <summary>
    /// このコンパイル先になるのは、このシンボルの<b>どれかが有効な</b>構成だけ。空なら条件は無い。
    /// </summary>
    public ImmutableArray<string> WhenAnySymbol { get; init; } = [];

    /// <summary>
    /// このコンパイル先になるのは、このシンボルが<b>どれも有効でない</b>構成だけ。空なら条件は無い。
    /// </summary>
    public ImmutableArray<string> UnlessAnySymbol { get; init; } = [];

    /// <summary>
    /// その API で使えるか。
    /// </summary>
    /// <remarks>
    /// Unity は DXC が要るシェーダーを DX11 では使えないものとして扱う (コンパイルしない、または作っても使わない)。
    /// 使えないコンパイル先のエラーは、利用者には見えない。
    /// </remarks>
    public bool IsUsable { get; init; } = true;

    /// <summary>このコンパイル先になった理由 (<c>#pragma use_dxc</c> など)。画面に出す。</summary>
    public string? Reason { get; init; }

    /// <summary>このコンパイル先になる構成の条件 (<see cref="WhenAnySymbol"/> と <see cref="UnlessAnySymbol"/> を式にしたもの)。</summary>
    public SymbolCondition Condition
    {
        get
        {
            SymbolCondition condition = WhenAnySymbol.IsDefaultOrEmpty
                ? SymbolCondition.Always
                : WhenAnySymbol.Aggregate(SymbolCondition.Never, (c, s) => c.Or(SymbolCondition.Symbol(s)));

            return UnlessAnySymbol.IsDefaultOrEmpty
                ? condition
                : UnlessAnySymbol.Aggregate(condition, (c, s) => c.And(SymbolCondition.Symbol(s, isDefined: false)));
        }
    }

    /// <summary>
    /// 診断の文に入れる名前 (<c>dx11 (FXC)</c>、条件があれば <c>UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が無効な構成の dx12 (FXC)</c>)。
    /// </summary>
    public string Subject
    {
        get
        {
            string name = Platform.Length == 0 ? Dialect.DisplayName : $"{Platform} ({Dialect.DisplayName})";

            if (!WhenAnySymbol.IsDefaultOrEmpty)
            {
                return $"{string.Join(" / ", WhenAnySymbol)} が有効な構成の {name}";
            }

            return UnlessAnySymbol.IsDefaultOrEmpty ? name : $"{string.Join(" / ", UnlessAnySymbol)} が無効な構成の {name}";
        }
    }

    /// <summary>有効にしたシンボルの構成が、このコンパイル先になるかを判定する。</summary>
    /// <param name="enabledSymbols">構成で有効なシンボル。</param>
    /// <returns>なるなら <see langword="true"/>。</returns>
    public bool AppliesTo(IReadOnlyCollection<string> enabledSymbols)
    {
        ArgumentNullException.ThrowIfNull(enabledSymbols);

        return (WhenAnySymbol.IsDefaultOrEmpty || WhenAnySymbol.Any(enabledSymbols.Contains))
               && (UnlessAnySymbol.IsDefaultOrEmpty || !UnlessAnySymbol.Any(enabledSymbols.Contains));
    }

    /// <summary>画面に出す 1 行 (<c>dx12: Unity の DXC (HLSL 2018) [UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が有効な構成]</c>)。</summary>
    /// <returns>文字列。</returns>
    public string Describe()
    {
        List<string> notes = [];

        if (!WhenAnySymbol.IsDefaultOrEmpty)
        {
            notes.Add(string.Join(" / ", WhenAnySymbol) + " が有効な構成");
        }

        if (!UnlessAnySymbol.IsDefaultOrEmpty)
        {
            notes.Add(string.Join(" / ", UnlessAnySymbol) + " が無効な構成");
        }

        if (!IsUsable)
        {
            notes.Add("この API では使えない");
        }

        if (Reason is not null)
        {
            notes.Add(Reason);
        }

        return notes.Count == 0
            ? $"{Platform}: {Dialect.DisplayName}"
            : $"{Platform}: {Dialect.DisplayName} ({string.Join("、", notes)})";
    }
}
