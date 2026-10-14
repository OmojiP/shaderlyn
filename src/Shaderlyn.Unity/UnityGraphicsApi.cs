using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Shaderlyn.Unity;

/// <summary>
/// Unity がシェーダーをコンパイルするグラフィックス API。
/// </summary>
/// <remarks>値は Unity の <c>GraphicsDeviceType</c> と同じ (<c>ProjectSettings.asset</c> に書かれる番号)。</remarks>
public enum UnityGraphicsApi
{
    /// <summary>Direct3D 11。</summary>
    Direct3D11 = 2,

    /// <summary>OpenGL ES 3。</summary>
    OpenGLES3 = 11,

    /// <summary>Metal。</summary>
    Metal = 16,

    /// <summary>OpenGL Core。</summary>
    OpenGLCore = 17,

    /// <summary>Direct3D 12。</summary>
    Direct3D12 = 18,

    /// <summary>Vulkan。</summary>
    Vulkan = 21,
}

/// <summary>
/// グラフィックス API の名前と番号の対応。
/// </summary>
public static class UnityGraphicsApis
{
    /// <summary>設定が無いときに対象とする API。Windows の既定 (DX11 と DX12)。</summary>
    public static ImmutableArray<UnityGraphicsApi> Default { get; } = [UnityGraphicsApi.Direct3D11, UnityGraphicsApi.Direct3D12];

    /// <summary>設定に書く名前。</summary>
    private static readonly ImmutableArray<(string Name, UnityGraphicsApi Api)> Names =
    [
        ("dx11", UnityGraphicsApi.Direct3D11),
        ("dx12", UnityGraphicsApi.Direct3D12),
        ("vulkan", UnityGraphicsApi.Vulkan),
        ("metal", UnityGraphicsApi.Metal),
        ("gles3", UnityGraphicsApi.OpenGLES3),
        ("glcore", UnityGraphicsApi.OpenGLCore),
    ];

    /// <summary>受け付ける名前の一覧 (説明に使う)。</summary>
    public static string AcceptedNames => string.Join(" / ", Names.Select(n => n.Name));

    /// <summary>設定に書く名前 (<c>dx11</c> など)。</summary>
    /// <param name="api">API。</param>
    /// <returns>名前。</returns>
    public static string GetName(UnityGraphicsApi api)
        => Names.FirstOrDefault(n => n.Api == api).Name ?? api.ToString();

    /// <summary>設定に書いた名前を読む。大文字と小文字は区別しない。<c>d3d11</c> / <c>d3d12</c> も受け付ける。</summary>
    /// <param name="name">名前。</param>
    /// <param name="api">API。</param>
    /// <returns>読めたら <see langword="true"/>。</returns>
    public static bool TryParse(string? name, out UnityGraphicsApi api)
    {
        string key = name?.Trim().ToLowerInvariant() switch
        {
            "d3d11" or "direct3d11" => "dx11",
            "d3d12" or "direct3d12" => "dx12",
            { } other => other,
            null => string.Empty,
        };

        foreach ((string candidate, UnityGraphicsApi value) in Names)
        {
            if (candidate == key)
            {
                api = value;
                return true;
            }
        }

        api = default;
        return false;
    }

    /// <summary>Unity の <c>GraphicsDeviceType</c> の番号から API を引く。</summary>
    /// <param name="deviceType">番号。</param>
    /// <param name="api">API。</param>
    /// <returns>知っている番号なら <see langword="true"/>。</returns>
    public static bool TryFromDeviceType(int deviceType, [NotNullWhen(true)] out UnityGraphicsApi? api)
    {
        api = Enum.IsDefined((UnityGraphicsApi)deviceType) ? (UnityGraphicsApi)deviceType : null;
        return api is not null;
    }
}
